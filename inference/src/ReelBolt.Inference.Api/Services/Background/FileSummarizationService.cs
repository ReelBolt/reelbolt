using System.Text;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Inference.Api.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Agents;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IO;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Metering;
using ReelBolt.Inference.Api.Services.Indexing;
using MassTransit;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace ReelBolt.Inference.Api.Services.Background;

/// <summary>
/// Background service that processes file summarization tasks.
/// </summary>
/// <remarks>
/// <para>
/// The DATABASE row is the source of truth for "this file still needs a summary"
/// (<see cref="SummaryStatus.Pending"/>); the in-memory queue is only a fast path. Request paths
/// persist Pending and offer the file with the non-blocking
/// <see cref="IBackgroundTaskQueue{T}.TryQueue"/> — they never wait — and a sweeper (shortly after
/// startup, then every <see cref="FileSummarizationOptions.SweepIntervalSeconds"/>) re-offers
/// whatever is still Pending, plus rows a previous process left in <see cref="SummaryStatus.Processing"/>.
/// The queue's own tracking of queued and in-flight ids is what keeps a sweep from duplicating work.
/// </para>
/// <para>
/// Concurrency is bounded by <see cref="FileSummarizationOptions.MaxConcurrency"/> workers, and each
/// worker yields to an in-flight assistant turn (<see cref="AssistantActivityMonitor"/>) before every
/// LLM call, because both share the chat provider.
/// </para>
/// </remarks>
public class FileSummarizationService : BackgroundService
{
    private readonly IBackgroundTaskQueue<FileSummarizationTask> _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FileSummarizationService> _logger;
    private readonly FileSummarizationOptions _options;
    private readonly AssistantActivityMonitor? _activity;

    internal const string ImageDescriptionPrompt =
        "Describe this image for someone who cannot see it, in at most 120 words: what it shows, any readable text " +
        "(quoted exactly), the style, and how it could be used in a promotional video. Plain prose, no lists.";

    private const int MaxImageDescriptionChars = 2000;

    private static readonly JsonSerializerOptions SummaryJsonOptions = new(JsonSerializerDefaults.Web);

    public FileSummarizationService(
        IBackgroundTaskQueue<FileSummarizationTask> queue,
        IServiceScopeFactory scopeFactory,
        ILogger<FileSummarizationService> logger,
        IOptions<FileSummarizationOptions>? options = null,
        AssistantActivityMonitor? activity = null)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options?.Value ?? new FileSummarizationOptions();
        _activity = activity;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int workers = Math.Clamp(_options.MaxConcurrency, 1, 8);
        _logger.LogInformation("File summarization service started with {Workers} worker(s)", workers);

        var loops = new List<Task> { SweepLoopAsync(stoppingToken) };
        for (int i = 0; i < workers; i++)
            loops.Add(WorkerLoopAsync(stoppingToken));

        await Task.WhenAll(loops);
    }

    private async Task WorkerLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            FileSummarizationTask task;
            try
            {
                task = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                if (_activity is not null &&
                    await _activity.YieldToInteractiveAsync(TimeSpan.FromSeconds(Math.Max(0, _options.InteractiveYieldSeconds)), stoppingToken))
                {
                    _logger.LogDebug("Summarization of file {FileId} waited for an assistant turn to finish", task.FileId);
                }

                await ProcessAsync(task, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing file summarization for file {FileId}", task.FileId);
            }
            finally
            {
                _queue.Complete(task);
            }
        }
    }

    private async Task SweepLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _options.InitialSweepDelaySeconds)), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int queued = await SweepOnceAsync(stoppingToken);
                if (queued > 0)
                    _logger.LogInformation("Summarization sweep queued {Count} pending file(s)", queued);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Summarization sweep failed; will retry after the interval");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _options.SweepIntervalSeconds)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One sweep, exposed for tests: offers every still-Pending user file (oldest first, as many as
    /// the queue has room for) and resets rows stuck in Processing by an earlier process. Never
    /// offers a file that is already queued or being summarized. Returns how many were queued.
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        int room = _queue.FreeCapacity;
        if (room <= 0)
            return 0;

        using IServiceScope scope = _scopeFactory.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

        // The sweep is platform housekeeping across every organization's files.
        using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();

        // Same eligibility the upload path applies: user files only (engine-written agent/output
        // files are not summarized), and never video/audio — R22, a binary must not reach the text
        // summarizer through the sweeper either.
        IQueryable<ProjectFile> eligible = db.ProjectFiles
            .Where(f => f.Category == "userFiles")
            .Where(f => f.CsamStatus != CsamScanStatus.Quarantined)
            // No file reaches a model (text summarizer, Vision describer, embedder) before its CSAM verdict
            // (docs/csam.md). A synced or assistant-written row starts Pending; once the scan settles it,
            // the next sweep picks it up.
            .Where(f => f.CsamStatus != CsamScanStatus.Pending && f.CsamStatus != CsamScanStatus.Scanning)
            .Where(f => !(f.MimeType.StartsWith("video/") || f.MimeType.StartsWith("audio/")));

        DateTime stuckCutoff = DateTime.UtcNow.AddMinutes(-Math.Max(1, _options.StuckProcessingMinutes));
        List<ProjectFile> stuck = await eligible
            .Where(f => f.SummaryStatus == SummaryStatus.Processing && f.UploadedAt < stuckCutoff)
            .OrderBy(f => f.UploadedAt)
            .Take(room)
            .ToListAsync(ct);

        bool reset = false;
        foreach (ProjectFile file in stuck)
        {
            if (_queue.IsTracked(new FileSummarizationTask(file.Id)))
                continue;
            file.SummaryStatus = SummaryStatus.Pending;
            reset = true;
        }

        if (reset)
            await db.SaveChangesAsync(ct);

        // Over-fetch by everything already queued or in flight: those are typically the OLDEST
        // pending rows, and skipping them must still leave `room` new ones to offer.
        List<Guid> pending = await eligible
            .Where(f => f.SummaryStatus == SummaryStatus.Pending)
            .OrderBy(f => f.UploadedAt)
            .Select(f => f.Id)
            .Take(room + _queue.TrackedCount)
            .ToListAsync(ct);

        int queued = 0;
        foreach (Guid id in pending)
        {
            var task = new FileSummarizationTask(id);
            if (_queue.IsTracked(task))
                continue;
            if (!_queue.TryQueue(task))
            {
                if (_queue.FreeCapacity <= 0)
                    break;
                continue;
            }
            queued++;
        }

        return queued;
    }

    internal async Task ProcessAsync(FileSummarizationTask task, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        IAgentRegistry agentRegistry = scope.ServiceProvider.GetRequiredService<IAgentRegistry>();
        IFileStorageService fileStorage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();

        // The queue carries only a file id: find the file (and its organization) across tenants, then
        // do the actual work as that organization.
        ITenantContext? tenant = scope.ServiceProvider.GetService<ITenantContext>();
        ProjectFile? file;
        Guid organizationId = Guid.Empty;
        using (tenant.System())
        {
            file = await db.ProjectFiles.FirstOrDefaultAsync(
                f => f.Id == task.FileId && f.CsamStatus != CsamScanStatus.Quarantined, ct);
            if (file != null)
            {
                organizationId = await db.Projects
                    .Where(p => p.Id == file.ProjectId)
                    .Select(p => p.OrganizationId)
                    .FirstOrDefaultAsync(ct);
            }
        }

        if (file == null)
        {
            _logger.LogWarning("File {FileId} not found for summarization", task.FileId);
            return;
        }

        using IDisposable orgScope = tenant.Organization(organizationId);

        // Either quarantine blocks summarization: a description would outlive the block and could be
        // indexed as a readable stand-in for bytes no read path will serve. The CSAM scan and the
        // general moderation scan are independent controls; whichever fires first wins.
        if (file.CsamStatus == CsamScanStatus.Quarantined
            || file.ModerationStatus == FileModerationStatus.Quarantined)
        {
            file.SummaryStatus = SummaryStatus.Done;
            file.IndexingStatus = FileIndexingStatus.NotIndexed;
            file.IndexingError = "Quarantined";
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("File {FileId} is quarantined; skipping summarization", task.FileId);
            return;
        }

        // A file whose bytes are not served never reaches a model, whatever was queued before.
        if (await OutputGate.IsFileHeldAsync(db, file, ct))
        {
            file.SummaryStatus = SummaryStatus.Done;
            file.IndexingStatus = FileIndexingStatus.NotIndexed;
            file.IndexingError = "Unavailable";
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("File {FileId} is not served; skipping summarization", task.FileId);
            return;
        }

        file.SummaryStatus = SummaryStatus.Processing;
        await db.SaveChangesAsync(ct);

        ProjectFileContentKind? kind = null;
        try
        {
            using Stream stream = await fileStorage.DownloadAsync(file.ProjectId, file.StorageKey, ct);
            byte[] head = await ProjectFileContentClassifier.ReadHeadAsync(stream, ct);
            kind = ProjectFileContentClassifier.Classify(file.MimeType, file.OriginalFileName, head);

            // A PDF is summarized from the text the import-worker extracts, capped like any other file's text.
            string? pdfContent = null;
            if (ProjectFilePdf.IsPdf(file.MimeType, file.OriginalFileName)
                && scope.ServiceProvider.GetService<IProjectFilePdfText>() is { } pdfReader)
            {
                ProjectPdfText pdf = await pdfReader.ReadAsync(file, PdfReadPurpose.Background, ct);
                if (!pdf.Ok)
                {
                    if (pdf.Transient || pdf.Failure == ProjectPdfTextFailure.Disabled)
                    {
                        // The worker (or the operator's off-switch), not the document: no summary is stored and the file is left
                        // retryable instead of settled as skipped. A later successful read of this PDF puts it back to Pending.
                        file.SummaryStatus = SummaryStatus.Failed;
                        await db.SaveChangesAsync(ct);
                        _logger.LogWarning("PDF text for {FileId} unavailable ({Code}); summary marked Failed", file.Id, pdf.Code);
                        return;
                    }

                    // The plain reason IS the summary, so an engine agent (which cannot open a PDF) that is sent to this
                    // file's agentSummary reads why there is no text and what to do, in the stored summary shape.
                    string pdfReason = pdf.Message ?? "its PDF text could not be read";
                    await SkipAsync(
                        db,
                        file,
                        pdfReason,
                        ct,
                        JsonSerializer.Serialize(new FileSummaryOutput { FileType = "pdf", Summary = pdfReason }, SummaryJsonOptions));
                    return;
                }

                byte[] pdfBytes = Encoding.UTF8.GetBytes(pdf.Text);
                bool cut = pdf.Truncated || pdfBytes.Length > _options.MaxTextBytes;
                string pdfText = pdfBytes.Length > _options.MaxTextBytes
                    ? Encoding.UTF8.GetString(pdfBytes, 0, _options.MaxTextBytes).TrimEnd('\uFFFD')
                    : pdf.Text;
                pdfContent = cut ? pdfText + "\n[truncated]" : pdfText;
                kind = ProjectFileContentKind.Text;
            }

            if (kind == ProjectFileContentKind.Other)
            {
                await SkipAsync(db, file, "it is not human-readable text or a supported image", ct);
                return;
            }

            if (kind == ProjectFileContentKind.Image)
            {
                await DescribeImageAsync(scope.ServiceProvider, db, file, organizationId, head, stream, ct);
                await RequestIndexingAsync(scope.ServiceProvider, file, ct);
                return;
            }

            // A summary of the first MaxTextBytes of a big file is still useful, so summarize the
            // head and say so rather than skipping the file.
            string content;
            if (pdfContent is not null)
            {
                content = pdfContent;
            }
            else
            {
                (string text, bool truncated) = await ProjectFileContentClassifier.ReadTextAsync(head, stream, _options.MaxTextBytes, ct);
                content = truncated ? text + "\n[truncated]" : text;
            }

            IReelBoltAgent? summarizer = agentRegistry.GetByType(AgentType.FileSummarizerAgent);
            if (summarizer == null)
            {
                _logger.LogWarning("FileSummarizerAgent not registered");
                file.SummaryStatus = SummaryStatus.Failed;
                await db.SaveChangesAsync(ct);
                return;
            }

            // Look up the built-in FileSummarizerAgent's own definition id so a per-agent
            // provider override (set via PUT /api/v1/agents/{id}/inference-provider) actually
            // applies here — RunAsync previously always received null (found by Copilot review).
            Guid? agentDefinitionId = await db.AgentDefinitions
                .Where(a => a.AgentType == AgentType.FileSummarizerAgent)
                .Select(a => (Guid?)a.Id)
                .FirstOrDefaultAsync(ct);

            string displayName = file.OriginalPath ?? file.OriginalFileName;
            string prompt = FileSummaryPromptBuilder.Build(displayName, content);
            AgentRunResult result;
            // Metering scope: the summary's model calls belong to the file's organization and project. No user:
            // nobody asked for this call, the upload only queued it.
            Guid? meteredOrganizationId = organizationId == Guid.Empty ? null : organizationId;
            using (UsageScope.Begin(meteredOrganizationId, file.ProjectId, null, null, null, UsageSource.FileSummary))
            {
                result = await summarizer.RunAsync(prompt, agentDefinitionId, ct);
            }

            file.AgentSummary = result.Output;
            file.SummaryStatus = SummaryStatus.Done;
            await db.SaveChangesAsync(ct);

            _logger.LogInformation("Summarized file {FileId}: {FileName} ({TokensUsed} tokens)",
                task.FileId, file.OriginalFileName, result.TokensUsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down mid-summary: put the row back so the next process's sweep picks it up
            // straight away instead of waiting out the stuck-Processing timeout.
            file.SummaryStatus = SummaryStatus.Pending;
            try { await db.SaveChangesAsync(CancellationToken.None); } catch (Exception) { /* best effort */ }
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to summarize file {FileId}", task.FileId);
            file.SummaryStatus = SummaryStatus.Failed;
            await db.SaveChangesAsync(ct);

            // The indexing consumer may have parked this image at Pending waiting for us; it maps a
            // Failed summary to NotIndexed, but only if it is asked again.
            if (kind == ProjectFileContentKind.Image)
                await RequestIndexingAsync(scope.ServiceProvider, file, ct);
        }
    }

    /// <summary>
    /// Republishes an image for indexing once its summary is terminal (described, skipped or
    /// failed). The consumer parks an image at <see cref="FileIndexingStatus.Pending"/> while its
    /// summary is outstanding, and only it writes the indexing status, so every outcome must hand
    /// the file back to it — a description is embedded, anything else is settled as NotIndexed.
    /// Best effort: a failed publish is logged, never thrown (bar cancellation).
    /// </summary>
    private async Task RequestIndexingAsync(IServiceProvider services, ProjectFile file, CancellationToken ct)
    {
        IPublishEndpoint? publish = services.GetService<IPublishEndpoint>();
        if (publish is null)
        {
            _logger.LogWarning("No publish endpoint; image {FileId} keeps its indexing status until the next reindex", file.Id);
            return;
        }

        try
        {
            await publish.Publish(new ProjectFileIndexingRequested
            {
                ProjectId = file.ProjectId,
                FileId = file.Id,
                Operation = "Upsert",
                RequestedAt = DateTime.UtcNow
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not request indexing of image {FileId} after summarizing it", file.Id);
        }
    }

    private async Task SkipAsync(InferenceApiDbContext db, ProjectFile file, string reason, CancellationToken ct, string? agentSummary = null)
    {
        file.AgentSummary = agentSummary;
        file.SummaryStatus = SummaryStatus.Done;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Not summarizing file {FileId} ({FileName}): {Reason}", file.Id, file.OriginalFileName, reason);
    }

    /// <summary>
    /// An image's summary is its Vision description, produced once here; the indexing consumer then
    /// embeds that description (the caller republishes the file for indexing on every outcome).
    /// </summary>
    private async Task DescribeImageAsync(
        IServiceProvider services, InferenceApiDbContext db, ProjectFile file, Guid organizationId,
        byte[] head, Stream rest, CancellationToken ct)
    {
        if (file.SizeBytes > ProjectFileContentClassifier.MaxVisionImageBytes)
        {
            await SkipAsync(db, file, "the image is larger than the vision size cap", ct);
            return;
        }

        IInferenceProviderResolver? resolver = services.GetService<IInferenceProviderResolver>();
        IChatClientFactory? chatClients = services.GetService<IChatClientFactory>();
        ResolvedInferenceProvider? vision = resolver is null ? null : await resolver.ResolveVisionAsync(null, ct);
        if (vision is null || chatClients is null)
        {
            await SkipAsync(db, file, "no vision provider is configured", ct);
            return;
        }

        byte[]? bytes = await ReadCappedAsync(head, rest, ProjectFileContentClassifier.MaxVisionImageBytes, ct);
        if (bytes is null)
        {
            await SkipAsync(db, file, "the image is larger than the vision size cap", ct);
            return;
        }

        if (_activity is not null)
            await _activity.YieldToInteractiveAsync(TimeSpan.FromSeconds(Math.Max(0, _options.InteractiveYieldSeconds)), ct);

        ChatMessage message = new(
            ChatRole.User,
            new List<AIContent>
            {
                new TextContent(ImageDescriptionPrompt),
                new DataContent(bytes, ProjectFileContentClassifier.VisionMimeType(head)!)
            });

        ChatResponse response;
        // An image's description IS its file summary: metered like the text summarizer's call (FileSummary, the
        // file's org and project, no user). The indexing consumer's later embedding stays under Indexing.
        Guid? meteredOrganizationId = organizationId == Guid.Empty ? null : organizationId;
        using (UsageScope.Begin(meteredOrganizationId, file.ProjectId, null, null, null, UsageSource.FileSummary))
        {
            response = await chatClients.Get(vision).GetResponseAsync(
                new[] { message }, new ChatOptions { MaxOutputTokens = 400 }, ct);
        }

        string description = (response.Text ?? string.Empty).Trim();
        if (description.Length == 0)
        {
            await SkipAsync(db, file, "the vision provider returned no description", ct);
            return;
        }

        if (description.Length > MaxImageDescriptionChars)
            description = description[..MaxImageDescriptionChars];

        // Same stored shape as the text summarizer's structured output (FileSummaryOutput, web casing).
        file.AgentSummary = JsonSerializer.Serialize(
            new FileSummaryOutput { FileType = "image", Summary = description }, SummaryJsonOptions);
        file.SummaryStatus = SummaryStatus.Done;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Described image {FileId}: {FileName}", file.Id, file.OriginalFileName);
    }

    /// <summary>The head plus the rest of the stream, or null as soon as the total passes <paramref name="cap"/>.</summary>
    private static async Task<byte[]?> ReadCappedAsync(byte[] head, Stream rest, long cap, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await buffer.WriteAsync(head, ct);
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await rest.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > cap)
                return null;
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        return buffer.Length > cap ? null : buffer.ToArray();
    }
}
