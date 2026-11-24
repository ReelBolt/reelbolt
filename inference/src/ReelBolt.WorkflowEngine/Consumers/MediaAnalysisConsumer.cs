using System.Security.Cryptography;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Consumers;

/// <summary>
/// R09.1: analyses one media file on demand. Downloads it once, hashes it, reuses the organization's cached result for
/// the same bytes where there is one (nothing run, nothing billed), runs the rest through <see cref="MediaAnalysisService"/>,
/// and answers with <see cref="MediaAnalysisFinished"/>. Never throws for a bad file: every failure is an outcome with a
/// plain reason, so the requester always gets an answer.
/// </summary>
public class MediaAnalysisConsumer : IConsumer<MediaAnalysisRequested>
{
    private readonly MediaAnalysisInFlight? _inFlight;
    private readonly IDbContextFactory<WorkflowEngineDbContext> _dbFactory;
    private readonly IProjectFileWorkspace _workspace;
    private readonly IScratchDiskGuard? _scratchGuard;
    private readonly MediaAnalysisService _analysis;
    private readonly IMediaDerivativeProgressSink _progress;
    private readonly VideoEditingOptions _options;
    private readonly ILogger<MediaAnalysisConsumer> _logger;

    public MediaAnalysisConsumer(
        IDbContextFactory<WorkflowEngineDbContext> dbFactory,
        IProjectFileWorkspace workspace,
        MediaAnalysisService analysis,
        IMediaDerivativeProgressSink progress,
        IOptions<VideoEditingOptions> options,
        ILogger<MediaAnalysisConsumer> logger,
        IScratchDiskGuard? scratchGuard = null,
        MediaAnalysisInFlight? inFlight = null)
    {
        _scratchGuard = scratchGuard;
        _inFlight = inFlight;
        _dbFactory = dbFactory;
        _workspace = workspace;
        _analysis = analysis;
        _progress = progress;
        _options = options.Value;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<MediaAnalysisRequested> context)
    {
        MediaAnalysisRequested message = context.Message;
        _inFlight?.Enter();
        try
        {
            await ConsumeCoreAsync(context, message).ConfigureAwait(false);
        }
        finally
        {
            _inFlight?.Exit();
        }
    }

    private async Task ConsumeCoreAsync(ConsumeContext<MediaAnalysisRequested> context, MediaAnalysisRequested message)
    {
        IReadOnlyList<string> kinds = message.Kinds.Where(k => MediaAnalysisKinds.All.Contains(k)).Distinct().ToList();
        string language = NormalizeLanguage(message.Language);
        string? directory = null;
        string? digest = null;
        List<MediaAnalysisKindOutcome> outcomes = [];
        await context.Publish(new MediaAnalysisStarted(message.RequestId, message.ProjectId)).ConfigureAwait(false);
        try
        {
            string root = Path.GetFullPath(Path.Combine(_options.ScratchPath, "media-analysis", Guid.NewGuid().ToString("N")));
            directory = root;
            Directory.CreateDirectory(directory);
            string local = Path.Combine(directory, "source" + SafeExtension(message.SourceStorageKey));
            using ScratchLeases scratch = new();
            await scratch.ReserveAsync(_scratchGuard, _workspace, message.ProjectId, message.SourceStorageKey, context.CancellationToken).ConfigureAwait(false);
            await _workspace.DownloadStorageKeyToFileAsync(message.ProjectId, message.SourceStorageKey, local, context.CancellationToken).ConfigureAwait(false);
            digest = await HashAsync(local, context.CancellationToken).ConfigureAwait(false);

            Dictionary<string, MediaAnalysisResult> cached = await LoadCacheAsync(message.OrganizationId, digest, kinds, language, context.CancellationToken).ConfigureAwait(false);
            List<string> todo = [];
            foreach (string kind in kinds)
            {
                if (cached.TryGetValue(kind, out MediaAnalysisResult? hit))
                    outcomes.Add(new MediaAnalysisKindOutcome(kind, true, hit.ResultJson, hit.SummaryJson, null, hit.MeasuredSeconds, true));
                else
                    todo.Add(kind);
            }

            if (todo.Count > 0)
            {
                // The scope attributes the transcription's metered usage to the organization (source Editor) and
                // totals its credits, which the API settles the up-front hold against. Free kinds meter nothing.
                IReadOnlyList<MediaAnalysisKindOutcome> fresh;
                using (UsageScope.Begin(message.OrganizationId, message.ProjectId, null, null, message.UserId, UsageSource.Editor))
                {
                    UsageScopeState? scope = UsageScope.Current;
                    fresh = await _analysis.AnalyseAsync(
                        local, todo, language.Length == 0 ? null : language, message.HeldSeconds,
                        (kind, percent) => Report(message, MediaDerivativeProgressStage.Analysis, kind, percent),
                        context.CancellationToken).ConfigureAwait(false);
                    long spent = scope?.RunningCreditsMillis ?? 0;
                    bool unpriced = scope?.SawUnpricedPlatformUsage == true;
                    fresh = fresh.Select(o => o.Kind == MediaAnalysisKinds.Transcript && o.Ok ? o with { ActualMillis = spent, UnpricedUsage = unpriced } : o).ToList();
                }

                outcomes.AddRange(fresh);
                foreach (MediaAnalysisKindOutcome failed in fresh.Where(o => !o.Ok))
                    Report(message, MediaDerivativeProgressStage.AnalysisFailed, failed.Reason, null);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Media analysis {RequestId} for project {ProjectId} failed before any kind ran", message.RequestId, message.ProjectId);
            outcomes = kinds.Select(k => new MediaAnalysisKindOutcome(k, false, null, null, MediaAnalysisService.CouldNotRead, null, false)).ToList();
            Report(message, MediaDerivativeProgressStage.AnalysisFailed, MediaAnalysisService.CouldNotRead, null);
        }
        finally
        {
            TryDelete(directory);
        }

        await context.Publish(new MediaAnalysisFinished(
            message.RequestId, message.ProjectId, message.OrganizationId, message.SourceProjectFileId, digest, outcomes)).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, MediaAnalysisResult>> LoadCacheAsync(
        Guid organizationId, string digest, IReadOnlyList<string> kinds, string language, CancellationToken ct)
    {
        await using WorkflowEngineDbContext db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        List<MediaAnalysisResult> rows = await db.MediaAnalysisResults.AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.Digest == digest && kinds.Contains(r.Kind)
                        && (r.Language == language || r.Kind != MediaAnalysisKinds.Transcript))
            .ToListAsync(ct).ConfigureAwait(false);
        // An events result made by another version of the detector, or one marked degraded, is not a hit: the kind is run again and the API replaces the row.
        return rows.Where(r => r.Kind != MediaAnalysisKinds.Events || MediaAnalysisJson.EventsAreCurrent(r.ResultJson))
            .GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.First());
    }

    private void Report(MediaAnalysisRequested message, MediaDerivativeProgressStage stage, string? detail, int? percent) =>
        _progress.Report(new MediaDerivativeProgressUpdate(
            message.ProjectId, message.SourceProjectFileId, message.SourceStorageKey, null, stage, percent, detail));

    /// <summary>The transcript language key: lower-case code, empty for "detect"; other kinds are language-free.</summary>
    public static string NormalizeLanguage(string? language) => string.IsNullOrWhiteSpace(language) ? string.Empty : language.Trim().ToLowerInvariant();

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static string SafeExtension(string storageKey)
    {
        string ext = Path.GetExtension(storageKey);
        return ext.Length is > 0 and <= 10 && ext.All(c => char.IsLetterOrDigit(c) || c == '.') ? ext : string.Empty;
    }

    private void TryDelete(string? directory)
    {
        try
        {
            if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remove analysis scratch directory");
        }
    }
}

/// <summary>Counts analysis requests being worked inside the consumer (drain activeWork).</summary>
public sealed class MediaAnalysisInFlight : ReelBolt.Shared.Drain.IDrainWorkSource
{
    private int _count;

    public string Name => "mediaAnalysis";

    public int ActiveCount => Volatile.Read(ref _count);

    public void Enter() => Interlocked.Increment(ref _count);

    public void Exit() => Interlocked.Decrement(ref _count);
}
