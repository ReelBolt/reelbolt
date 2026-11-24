using MassTransit;
using ReelBolt.Inference.Api.Services.Archives;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Notifications;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;

namespace ReelBolt.Inference.Api.Services.Uploads;

/// <summary>
/// What happens after a new file's <c>project_files</c> row is committed, whichever way its bytes
/// arrived (form upload, direct upload completion, the assistant's upload tools, URL import): ONE code
/// path, so no entry point can forget a step. (Lives next to the URL importer, its newest caller.)
/// </summary>
public interface IProjectFileAddedEffects
{
    /// <summary>
    /// A row left <see cref="SummaryStatus.Pending"/> (text, images: anything not media and not synced)
    /// is queued for indexing and summarizing; a video/audio row requests its media derivative set
    /// (R22's other half) best-effort; then the addition is reported to the activity feed best-effort.
    /// Only cancellation and a failed indexing publish propagate, exactly as the upload routes always did.
    /// </summary>
    Task RunAsync(ProjectFile file, Guid userId, CancellationToken ct);
}

public sealed class ProjectFileAddedEffects : IProjectFileAddedEffects
{
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly IBackgroundTaskQueue<FileSummarizationTask> _summarizationQueue;
    private readonly IFileStorageService _fileStorage;
    private readonly IUserActivityNotifier? _activityNotifier;
    private readonly ILogger<ProjectFileAddedEffects>? _logger;
    private readonly ICsmaHashMatcher? _csamMatcher;
    private readonly IContentModerationEnqueuer? _moderation;
    private readonly IArchiveUnpackScheduler? _unpacks;
    private readonly IUploadAnalysisSetting? _uploadAnalysis;

    public ProjectFileAddedEffects(
        IPublishEndpoint publishEndpoint,
        IBackgroundTaskQueue<FileSummarizationTask> summarizationQueue,
        IFileStorageService fileStorage,
        IUserActivityNotifier? activityNotifier = null,
        ILogger<ProjectFileAddedEffects>? logger = null,
        ICsmaHashMatcher? csamMatcher = null,
        IContentModerationEnqueuer? moderation = null,
        IArchiveUnpackScheduler? unpacks = null,
        IUploadAnalysisSetting? uploadAnalysis = null)
    {
        _publishEndpoint = publishEndpoint;
        _summarizationQueue = summarizationQueue;
        _fileStorage = fileStorage;
        _activityNotifier = activityNotifier;
        _logger = logger;
        _csamMatcher = csamMatcher;
        _moderation = moderation;
        _unpacks = unpacks;
        _uploadAnalysis = uploadAnalysis;
    }

    public async Task RunAsync(ProjectFile file, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);

        // CSAM scan FIRST: a match quarantines the bytes and this method must stop before the
        // summarizer, the indexer, the media-derivative request, the general moderation scan or any
        // activity is triggered — none of those may ever see suspected illegal material.
        if (file.CsamStatus == CsamScanStatus.Quarantined) return;

        if (_csamMatcher is not null)
        {
            CsamScanOutcome outcome = await _csamMatcher.ScanAsync(file, userId, ct);
            if (outcome.Status == CsamScanStatus.Quarantined) return;
        }

        // Ingest moderation: offered only when the CSAM verdict is settled and not a quarantine.
        // A Pending row waits for the CSAM sweeper; the moderation sweep offers it again after.
        if (CsamScanGate.AllowsModeration(file))
            _moderation?.TryQueue(file.Id);

        // A zip is unpacked into the project in the background (never fails the upload). Only the paths that reach
        // this method unpack; GitHub, cloud-drive and computer-folder sync never call it, so a synced zip stays a file.
        if (_unpacks is not null)
            await _unpacks.ScheduleForUploadAsync(file, userId, ct);

        if (file.SummaryStatus == SummaryStatus.Pending)
        {
            await _publishEndpoint.Publish(new ProjectFileIndexingRequested
            {
                ProjectId = file.ProjectId,
                FileId = file.Id,
                Operation = "Upsert",
                RequestedAt = DateTime.UtcNow
            }, ct);

            // Never awaited, never blocking: the row is already persisted as Pending, so when the queue
            // is full the file simply waits for FileSummarizationService's sweeper.
            _summarizationQueue.TryQueue(new FileSummarizationTask(file.Id));
        }
        else if (UploadDerivativeRequest.IsMediaMimeType(file.MimeType)
                 && (_csamMatcher is null || CsamScanGate.AllowsDerivatives(file)))
        {
            // The editor needs a media file's derivative set (proxy, waveform, filmstrip, peaks,
            // keyframe index) — and its stored probe — before it can scrub. The row is ALREADY
            // COMMITTED, so a broker outage must not turn a stored file into an error the client
            // retries (storing the bytes twice): the event is a hint, and
            // POST /media/derivatives/request is the recovery path. UploadDerivativeRequest owns the
            // message and that best-effort contract, so this path and the direct-upload completion
            // and the cloud sync cannot drift into publishing different things.
            await UploadDerivativeRequest.PublishAsync(
                _publishEndpoint, _fileStorage, _uploadAnalysis, file, file.MimeType!, _logger, ct);
        }

        // Best-effort (docs/notifications.md): a notification failure never breaks the addition.
        try
        {
            if (_activityNotifier is not null)
                await _activityNotifier.FileCreatedAsync(file.ProjectId, userId, file.OriginalFileName, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }
}
