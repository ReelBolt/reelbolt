using System.Text;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Inference.Api.Services.Tenancy;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using System.ClientModel;
using System.Text.Json;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Indexing;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.IO;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Inference.Api.Consumers;

public sealed class ProjectFileIndexingConsumer : IConsumer<ProjectFileIndexingRequested>
{
    private const int EmbeddingMaxAttempts = 5;
    private const double EmbeddingRetryBaseDelaySeconds = 1;
    private readonly InferenceApiDbContext _db;
    private readonly IFileStorageService _fileStorage;
    private readonly IFileChunker _fileChunker;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly IVectorIndexService _vectorIndexService;
    private readonly ILogger<ProjectFileIndexingConsumer> _logger;
    private readonly AssistantActivityMonitor? _activity;
    private readonly ITenantContext? _tenant;
    private readonly FileIndexingOptions _options;

    /// <summary>
    /// Before each chunk's embedding, wait at most this long while an assistant turn is in flight:
    /// the assistant's documentation lookup embeds its question on the same (often CPU-only)
    /// embedding server, and a bulk upload's chunks used to queue in front of it.
    /// </summary>
    internal static readonly TimeSpan InteractiveYield = TimeSpan.FromSeconds(10);

    private readonly IProjectFilePdfText? _pdfText;

    public ProjectFileIndexingConsumer(
        InferenceApiDbContext db,
        IFileStorageService fileStorage,
        IFileChunker fileChunker,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IVectorIndexService vectorIndexService,
        ILogger<ProjectFileIndexingConsumer> logger,
        AssistantActivityMonitor? activity = null,
        ITenantContext? tenant = null,
        IOptions<FileIndexingOptions>? options = null,
        IProjectFilePdfText? pdfText = null)
    {
        _pdfText = pdfText;
        _tenant = tenant;
        _activity = activity;
        _options = options?.Value ?? new FileIndexingOptions();
        _db = db;
        _fileStorage = fileStorage;
        _fileChunker = fileChunker;
        _embeddingGenerator = embeddingGenerator;
        _vectorIndexService = vectorIndexService;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ProjectFileIndexingRequested> context)
    {
        ProjectFileIndexingRequested message = context.Message;

        try
        {
            await _vectorIndexService.DeleteFileChunksAsync(message.ProjectId, message.FileId, context.CancellationToken);
        }
        catch (IndexNotReadyException ex)
        {
            _logger.LogDebug(ex, "Vector index not ready while deleting old vectors for file {FileId}", message.FileId);
        }

        if (string.Equals(message.Operation, "Delete", StringComparison.OrdinalIgnoreCase))
            return;

        if (!string.Equals(message.Operation, "Upsert", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Unsupported indexing operation {Operation} for file {FileId}", message.Operation, message.FileId);
            return;
        }

        // A queue message has no caller: look the project's organization up across tenants, then
        // index as that organization (its embedding provider resolves per org).
        Guid? organizationId;
        using (_tenant.System())
        {
            organizationId = await _db.Projects
                .Where(p => p.Id == message.ProjectId)
                .Select(p => (Guid?)p.OrganizationId)
                .FirstOrDefaultAsync(context.CancellationToken);
        }

        if (organizationId is null)
        {
            _logger.LogInformation("Project {ProjectId} no longer exists; skipping vector upsert", message.ProjectId);
            return;
        }

        using IDisposable orgScope = _tenant.Organization(organizationId.Value);

        // Embedding the file's chunks is metered to the project's organization (source=Indexing), not to the
        // uploader: a queue message has no caller, and the project is what the org pays for (B3c).
        using IDisposable usageScope = UsageScope.Begin(
            organizationId.Value, message.ProjectId, null, null, null, UsageSource.Indexing);

        ProjectFile? file = await _db.ProjectFiles
            .FirstOrDefaultAsync(f => f.Id == message.FileId
                                      && f.ProjectId == message.ProjectId
                                      && f.CsamStatus != CsamScanStatus.Quarantined,
                context.CancellationToken);

        if (file is null)
        {
            _logger.LogInformation("Project file {FileId} no longer exists; skipping vector upsert", message.FileId);
            return;
        }

        // Either quarantine blocks the index: never read those bytes, and settle the row so a later
        // upsert cannot retry it. The "Delete" operation above still runs through the unconditional
        // chunk delete first.
        if (file.CsamStatus == CsamScanStatus.Quarantined
            || file.ModerationStatus == FileModerationStatus.Quarantined)
        {
            file.IndexingStatus = FileIndexingStatus.NotIndexed;
            file.IndexingError = "Quarantined";
            file.SummaryStatus = SummaryStatus.Done;
            await _db.SaveChangesAsync(context.CancellationToken);
            _logger.LogInformation("File {FileId} is quarantined; skipping vector upsert", message.FileId);
            return;
        }

        // A file whose bytes are not served is never read for the index, whatever was queued before.
        if (await OutputGate.IsFileHeldAsync(_db, file, context.CancellationToken))
        {
            file.IndexingStatus = FileIndexingStatus.NotIndexed;
            file.IndexingError = "Unavailable";
            file.SummaryStatus = SummaryStatus.Done;
            await _db.SaveChangesAsync(context.CancellationToken);
            _logger.LogInformation("File {FileId} is not served; skipping vector upsert", message.FileId);
            return;
        }

        // The text cap only applies to text. A file whose stored MIME says text is refused before
        // downloading; anything else is classified from its head first, so a large image/video/PDF
        // is never failed as too_large. A throw would make MassTransit retry a file that can never
        // be indexed, so mark it Failed and return.
        bool declaredText = file.MimeType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true;
        if (declaredText && file.SizeBytes > _options.MaxTextBytes)
        {
            await MarkTooLargeAsync(file, message, context.CancellationToken);
            return;
        }

        file.IndexingStatus = FileIndexingStatus.Processing;
        file.IndexingError = null;
        await _db.SaveChangesAsync(context.CancellationToken);

        try
        {
            string content;
            string chunkFileName;
            await using (Stream stream = await _fileStorage.DownloadAsync(message.ProjectId, file.StorageKey, context.CancellationToken))
            {
                byte[] head = await ProjectFileContentClassifier.ReadHeadAsync(stream, context.CancellationToken);
                ProjectFileContentKind kind = ProjectFileContentClassifier.Classify(file.MimeType, file.OriginalFileName, head);

                if (_pdfText is not null && ProjectFilePdf.IsPdf(file.MimeType, file.OriginalFileName))
                {
                    // A PDF is indexed as the text the import-worker extracts, under the same size cap as any text.
                    ProjectPdfText pdf = await _pdfText.ReadAsync(file, PdfReadPurpose.Background, context.CancellationToken);
                    if (!pdf.Ok)
                    {
                        _logger.LogInformation("Not indexing PDF {FileId}: {Code}", file.Id, pdf.Code);
                        if (pdf.Transient)
                        {
                            file.IndexingStatus = FileIndexingStatus.Failed;
                            file.IndexingError = pdf.Code;
                            await _db.SaveChangesAsync(context.CancellationToken);
                        }
                        else
                        {
                            await MarkNotIndexedAsync(file, context.CancellationToken);
                        }

                        return;
                    }

                    // A summary left Failed (worker down, or the PDF switch was off) with nothing stored is queued again now that
                    // the text reads; the summarization sweep picks Pending rows up.
                    if (file.SummaryStatus == SummaryStatus.Failed && file.AgentSummary is null && file.Category == "userFiles")
                    {
                        file.SummaryStatus = SummaryStatus.Pending;
                        await _db.SaveChangesAsync(context.CancellationToken);
                    }

                    byte[] pdfBytes = Encoding.UTF8.GetBytes(pdf.Text);
                    content = pdfBytes.Length > _options.MaxTextBytes
                        ? Encoding.UTF8.GetString(pdfBytes, 0, _options.MaxTextBytes).TrimEnd('\uFFFD')
                        : pdf.Text;
                    chunkFileName = Path.GetFileNameWithoutExtension(file.OriginalFileName) + ".pdf.txt";
                }
                else if (kind == ProjectFileContentKind.Other)
                {
                    _logger.LogInformation("Not indexing file {FileId} ({FileName}): not human-readable text", file.Id, file.OriginalFileName);
                    await MarkNotIndexedAsync(file, context.CancellationToken);
                    return;
                }
                else if (kind == ProjectFileContentKind.Image)
                {
                    // An image is embedded through its Vision description, which the summarizer
                    // produces and then republishes this message for; the pixels are never read here.
                    if (file.SummaryStatus is SummaryStatus.Pending or SummaryStatus.Processing && file.Category == "userFiles")
                    {
                        file.IndexingStatus = FileIndexingStatus.Pending;
                        await _db.SaveChangesAsync(context.CancellationToken);
                        return;
                    }

                    string? description = file.SummaryStatus == SummaryStatus.Done ? DescriptionText(file.AgentSummary) : null;
                    if (string.IsNullOrWhiteSpace(description))
                    {
                        await MarkNotIndexedAsync(file, context.CancellationToken);
                        return;
                    }

                    content = description;
                    chunkFileName = Path.GetFileNameWithoutExtension(file.OriginalFileName) + ".description.txt";
                }
                else
                {
                    if (file.SizeBytes > _options.MaxTextBytes)
                    {
                        await MarkTooLargeAsync(file, message, context.CancellationToken);
                        return;
                    }

                    (string text, bool truncated) = await ProjectFileContentClassifier.ReadTextAsync(head, stream, _options.MaxTextBytes, context.CancellationToken);

                    // The stored size lied (or the file grew): treat it exactly like an oversize
                    // file, never chunk a truncated body.
                    if (truncated)
                    {
                        file.IndexingStatus = FileIndexingStatus.Failed;
                        file.IndexingError = "too_large";
                        await _db.SaveChangesAsync(context.CancellationToken);
                        _logger.LogInformation(
                            "File {FileId} in project {ProjectId} exceeded the {MaxTextBytes} byte indexing cap while reading",
                            message.FileId,
                            message.ProjectId,
                            _options.MaxTextBytes);
                        return;
                    }

                    content = text;
                    chunkFileName = file.OriginalFileName;
                }
            }

            IReadOnlyList<FileChunk> chunks = _fileChunker.Chunk(content, chunkFileName);
            if (chunks.Count == 0)
            {
                file.IndexingStatus = FileIndexingStatus.Indexed;
                file.IndexingError = null;
                file.IndexedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(context.CancellationToken);
                return;
            }

            List<VectorizedFileChunk> vectorizedChunks = new(chunks.Count);
            string? embeddingModel = null;
            foreach (FileChunk chunk in chunks)
            {
                if (_activity is not null)
                    await _activity.YieldToInteractiveAsync(InteractiveYield, context.CancellationToken);

                Embedding<float> embedding = await GenerateEmbeddingWithRetryAsync(
                    chunk,
                    message.FileId,
                    context.CancellationToken);

                embeddingModel ??= embedding.ModelId;
                vectorizedChunks.Add(new VectorizedFileChunk(
                    chunk.ChunkIndex,
                    chunk.TotalChunks,
                    chunk.Language,
                    chunk.Content,
                    embedding.Vector));
            }

            string filePath = file.OriginalPath ?? file.OriginalFileName;
            await _vectorIndexService.UpsertFileChunksAsync(
                message.ProjectId,
                embeddingModel,
                message.FileId,
                filePath,
                file.OriginalFileName,
                vectorizedChunks,
                context.CancellationToken);

            file.IndexingStatus = FileIndexingStatus.Indexed;
            file.IndexingError = null;
            file.IndexedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(context.CancellationToken);
        }
        catch (Exception ex)
        {
            file.IndexingStatus = FileIndexingStatus.Failed;
            file.IndexingError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            await _db.SaveChangesAsync(context.CancellationToken);
            throw;
        }
    }

    private async Task MarkTooLargeAsync(ProjectFile file, ProjectFileIndexingRequested message, CancellationToken ct)
    {
        file.IndexingStatus = FileIndexingStatus.Failed;
        file.IndexingError = "too_large";
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "File {FileId} in project {ProjectId} is {SizeBytes} bytes, above the {MaxTextBytes} byte indexing cap",
            message.FileId,
            message.ProjectId,
            file.SizeBytes,
            _options.MaxTextBytes);
    }

    private async Task MarkNotIndexedAsync(ProjectFile file, CancellationToken ct)
    {
        file.IndexingStatus = FileIndexingStatus.NotIndexed;
        file.IndexingError = null;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The text of an image's stored description: the <c>summary</c> of a FileSummaryOutput-shaped JSON
    /// object (what the summarizer stores), or the stored value itself when it is plain text.
    /// </summary>
    internal static string? DescriptionText(string? agentSummary)
    {
        if (string.IsNullOrWhiteSpace(agentSummary))
            return null;

        string trimmed = agentSummary.Trim();
        if (!trimmed.StartsWith('{'))
            return trimmed;

        try
        {
            using JsonDocument document = JsonDocument.Parse(trimmed);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "summary", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString()?.Trim();
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return trimmed;
        }
    }

    private async Task<Embedding<float>> GenerateEmbeddingWithRetryAsync(
        FileChunk chunk,
        Guid fileId,
        CancellationToken ct)
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= EmbeddingMaxAttempts; attempt++)
        {
            try
            {
                return await _embeddingGenerator.GenerateAsync(chunk.Content, cancellationToken: ct);
            }
            catch (Exception ex) when (attempt < EmbeddingMaxAttempts && IsTransientEmbeddingException(ex))
            {
                lastException = ex;

                double delaySeconds = Math.Pow(2, attempt - 1) * EmbeddingRetryBaseDelaySeconds;
                int jitterMs = Random.Shared.Next(0, 250);
                TimeSpan delay = TimeSpan.FromSeconds(delaySeconds) + TimeSpan.FromMilliseconds(jitterMs);

                _logger.LogWarning(
                    ex,
                    "Embedding generation failed for file {FileId}, chunk {ChunkIndex}/{TotalChunks} on attempt {Attempt}/{MaxAttempts}. Retrying in {DelayMs}ms",
                    fileId,
                    chunk.ChunkIndex + 1,
                    chunk.TotalChunks,
                    attempt,
                    EmbeddingMaxAttempts,
                    (int)delay.TotalMilliseconds);

                await Task.Delay(delay, ct);
            }
        }

        _logger.LogError(
            lastException,
            "Embedding generation failed for file {FileId}, chunk {ChunkIndex}/{TotalChunks} after {MaxAttempts} attempts",
            fileId,
            chunk.ChunkIndex + 1,
            chunk.TotalChunks,
            EmbeddingMaxAttempts);

        throw lastException ?? new InvalidOperationException("Embedding generation failed after retries.");
    }

    private static bool IsTransientEmbeddingException(Exception ex)
    {
        Exception baseException = ex.GetBaseException();

        if (baseException is OperationCanceledException)
            return false;

        if (baseException is ClientResultException clientResultException)
        {
            return clientResultException.Status is 408 or 409 or 425 or 429 or >= 500;
        }

        if (baseException is HttpRequestException)
            return true;

        string message = baseException.Message;
        return message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
            || message.Contains("throttl", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("temporar", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unavailable", StringComparison.OrdinalIgnoreCase);
    }
}
