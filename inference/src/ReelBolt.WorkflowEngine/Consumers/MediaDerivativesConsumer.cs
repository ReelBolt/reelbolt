using MassTransit;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Consumers;

/// <summary>
/// Consumes <see cref="MediaDerivativesRequested"/>: confirms the request is still true, hands it to
/// <see cref="MediaDerivativeDispatcher"/> and returns.
/// </summary>
/// <remarks>
/// <para>
/// <b>It dispatches — it does not produce.</b> Same discipline, and same reason, as
/// <c>WorkflowExecutionRequestedConsumer</c>: the RabbitMQ delivery is acknowledged in milliseconds
/// while the work runs on a process-lifetime task. Awaiting a derivative encode here would hold the
/// delivery open for minutes and put every derivative request back under the broker's
/// <c>consumer_timeout</c>. See <c>CLAUDE.md</c>, "Workflow Execution Liveness".
/// </para>
/// <para>
/// <b>The message is a hint, never a source of truth</b> (the event's own contract). It carries a
/// snapshot of the source's storage key and ETag, so the first thing this consumer does is check that
/// the snapshot is still current. A stale request — the file was relinked, replaced or deleted between
/// publish and consume — is dropped with no row written, because a row claiming an ETag the object no
/// longer has is precisely the cache-identity collision this wave is reviewed for.
/// </para>
/// <para>
/// <b>Nothing here throws.</b> Every failure is logged, reported to the progress sink and acked. A
/// derivative is convenience work: the publisher re-asks on the next upload, render registration or
/// editor open, so a lost delivery costs nothing but a delay, whereas a thrown exception would buy a
/// retry storm (and, on a permanently malformed message, a poison queue) for work nobody is waiting
/// on. MassTransit's retry policy is deliberately <b>not</b> attached to this endpoint for the same
/// reason — with nothing ever thrown, it would be dead code.
/// </para>
/// </remarks>
public class MediaDerivativesConsumer : IConsumer<MediaDerivativesRequested>
{
    private readonly IDbContextFactory<WorkflowEngineDbContext> _dbFactory;
    private readonly MediaDerivativeDispatcher _dispatcher;
    private readonly IMediaDerivativeProgressSink _progress;
    private readonly ILogger<MediaDerivativesConsumer> _logger;

    public MediaDerivativesConsumer(
        IDbContextFactory<WorkflowEngineDbContext> dbFactory,
        MediaDerivativeDispatcher dispatcher,
        IMediaDerivativeProgressSink progress,
        ILogger<MediaDerivativesConsumer> logger)
    {
        _dbFactory = dbFactory;
        _dispatcher = dispatcher;
        _progress = progress;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Consume(ConsumeContext<MediaDerivativesRequested> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        MediaDerivativesRequested message = context.Message;

        _logger.LogInformation(
            "Received media derivatives request: ProjectId={ProjectId}, SourceKind={SourceKind}, SourceStorageKey={SourceStorageKey}, " +
            "Kinds=[{Kinds}], RequestedBy={RequestedBy}",
            message.ProjectId,
            message.SourceRef?.Kind,
            message.SourceStorageKey,
            string.Join(",", message.Kinds ?? Array.Empty<MediaDerivativeKind>()),
            message.RequestedBy);

        try
        {
            if (!await IsStillCurrentAsync(message, context.CancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "Dropping stale media derivatives request for project {ProjectId}: source {SourceKind}/{SourceId} " +
                    "no longer has storage key {SourceStorageKey}. No row was written.",
                    message.ProjectId,
                    message.SourceRef?.Kind,
                    message.SourceRef?.SourceId,
                    message.SourceStorageKey);

                Report(message, MediaDerivativeProgressStage.Skipped,
                    "Stale request: the source's storage key changed between publish and consume. Nothing written.");
                return;
            }

            if (await IsWithheldAsync(message, context.CancellationToken).ConfigureAwait(false))
            {
                // Acked, never retried: the request is dropped once and nothing is produced from this source.
                _logger.LogWarning(
                    "Media derivatives request for project {ProjectId} source {SourceStorageKey} is withheld; nothing produced",
                    message.ProjectId,
                    message.SourceStorageKey);

                Report(message, MediaDerivativeProgressStage.Failed, "The derivatives could not be produced.");
                return;
            }

            await _dispatcher.DispatchAsync(message).ConfigureAwait(false);

            _logger.LogInformation(
                "Media derivatives request for project {ProjectId} source {SourceStorageKey} dispatched to the background runner",
                message.ProjectId,
                message.SourceStorageKey);
        }
        catch (Exception ex)
        {
            // Swallowed on purpose — see the class remark. The row stays absent, so the next request
            // for this source simply tries again.
            _logger.LogError(
                ex,
                "Failed to handle media derivatives request for project {ProjectId} source {SourceStorageKey}",
                message.ProjectId,
                message.SourceStorageKey);

            Report(message, MediaDerivativeProgressStage.Failed, ex.Message);
        }
    }

    /// <summary>
    /// True when the source is a render that may predate the project's output marker, or when the marker or the
    /// source's stamps cannot be read. Uploaded files are not judged here. A failed read withholds and never throws,
    /// so a broken read cannot cause a redelivery loop.
    /// </summary>
    private Task<bool> IsWithheldAsync(MediaDerivativesRequested message, CancellationToken ct) => IsWithheldAsync(_dbFactory, message, ct);

    /// <summary>The same check for the dispatcher, which repeats it after it waits for its source slot (the hold can start while a run queues).</summary>
    internal static async Task<bool> IsWithheldAsync(
        IDbContextFactory<WorkflowEngineDbContext> dbFactory, MediaDerivativesRequested message, CancellationToken ct)
    {
        try
        {
            await using WorkflowEngineDbContext db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            ProjectOutputHoldState state = await ProjectOutputHold.ReadAsync(db, message.ProjectId, ct).ConfigureAwait(false);
            if (!state.IsActive)
                return false;
            if (state.Unreadable)
                return true;

            MediaSourceKind kind = message.SourceRef?.Kind ?? MediaSourceKind.ProjectFile;
            if (kind == MediaSourceKind.StepResult)
            {
                Guid stepResultId = message.SourceRef?.SourceId ?? Guid.Empty;
                var stamps = await db.WorkflowStepResults.AsNoTracking()
                    .Where(r => r.Id == stepResultId && r.WorkflowExecution.ProjectId == message.ProjectId)
                    .Select(r => new { r.ExecutedAt, r.WorkflowExecution.StartedAt, r.WorkflowExecution.FirstStartedAt })
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                return stamps is null || state.HoldsOutput(stamps.ExecutedAt, ProjectOutputHold.RunStart(stamps.FirstStartedAt, stamps.StartedAt));
            }

            if (message.SourceProjectFileId is { } fileId)
            {
                var row = await db.ProjectFiles.AsNoTracking()
                    .Where(f => f.Id == fileId)
                    .Select(f => new { f.OutputProducedAt, f.UploadedAt })
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                // A row that cannot be found is judged by its key alone, with no stamp: held when the key is an engine output.
                if (ProjectOutputHold.IsPlatformMade(message.ProjectId, message.SourceStorageKey, row?.OutputProducedAt))
                    return state.HoldsFile(row is null ? null : ProjectOutputHold.FileStamp(row.OutputProducedAt, row.UploadedAt));
            }

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>
    /// Confirms the message against the database before any work happens: the source it names must
    /// still exist and must still have exactly the storage key the message snapshot claims.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Which table depends on the source kind</b>, which is why the event carries a discriminated
    /// reference at all. A <see cref="MediaSourceKind.ProjectFile"/> source is an uploaded file (or a
    /// render that was registered as one) and has a <c>project_files</c> row. A
    /// <see cref="MediaSourceKind.StepResult"/> source is an <b>unregistered</b> render: there is no
    /// project file to check, only the step result's own <c>output_storage_key</c>. Same staleness
    /// rule, different table.
    /// </para>
    /// <para>
    /// Ownership is checked here too (the row's project must be the message's project). The storage
    /// key alone would already be prefixed with the owning project by convention, but "the key looks
    /// right" is not the same assertion as "this project owns this row".
    /// </para>
    /// </remarks>
    private Task<bool> IsStillCurrentAsync(MediaDerivativesRequested message, CancellationToken ct) =>
        IsStillCurrentAsync(_dbFactory, message, _logger, ct);

    /// <summary>
    /// The same check for the dispatcher, which repeats it before it replays a request it kept in memory (a warm-up
    /// that gave way): the file may have been moved, replaced or deleted, or its scan reset, since the consumer looked.
    /// Unlike <see cref="IsWithheldAsync(IDbContextFactory{WorkflowEngineDbContext}, MediaDerivativesRequested, CancellationToken)"/>
    /// a failed read throws; the caller decides.
    /// </summary>
    internal static async Task<bool> IsStillCurrentAsync(
        IDbContextFactory<WorkflowEngineDbContext> dbFactory, MediaDerivativesRequested message, ILogger logger, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message.SourceStorageKey))
        {
            // Nothing to confirm against, and a row must never be keyed on an empty storage key.
            logger.LogWarning(
                "Media derivatives request for project {ProjectId} carries no source storage key; dropping it",
                message.ProjectId);
            return false;
        }

        await using WorkflowEngineDbContext db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        MediaSourceKind sourceKind = message.SourceRef?.Kind ?? MediaSourceKind.ProjectFile;

        if (sourceKind == MediaSourceKind.ProjectFile)
        {
            if (message.SourceProjectFileId is not { } fileId)
            {
                logger.LogWarning(
                    "Media derivatives request for project {ProjectId} names a ProjectFile source with no " +
                    "SourceProjectFileId; dropping it",
                    message.ProjectId);
                return false;
            }

            ProjectFile? file = await db.ProjectFiles
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == fileId, ct)
                .ConfigureAwait(false);

            // A file whose CSAM verdict is not settled makes no derivative (docs/csam.md): the publisher already
            // waits, this is the belt for a message sent or in flight before the verdict. The scan sweeper asks
            // again once it has one.
            return file is not null
                && file.ProjectId == message.ProjectId
                && file.CsamStatus is not (CsamScanStatus.Pending or CsamScanStatus.Scanning)
                && string.Equals(file.StorageKey, message.SourceStorageKey, StringComparison.Ordinal);
        }

        Guid stepResultId = message.SourceRef?.SourceId ?? Guid.Empty;
        if (stepResultId == Guid.Empty)
            return false;

        // Project ownership lives on the execution, so the join is what turns "this step result
        // exists" into "this step result belongs to this project".
        var resolved = await (
            from result in db.WorkflowStepResults.AsNoTracking()
            join execution in db.WorkflowExecutions.AsNoTracking() on result.WorkflowExecutionId equals execution.Id
            where result.Id == stepResultId
            select new { result.OutputStorageKey, execution.ProjectId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return resolved is not null
            && resolved.ProjectId == message.ProjectId
            && string.Equals(resolved.OutputStorageKey, message.SourceStorageKey, StringComparison.Ordinal);
    }

    private void Report(MediaDerivativesRequested message, MediaDerivativeProgressStage stage, string? detail)
    {
        try
        {
            // No cache key: nothing has been computed yet on these paths, and inventing one would be
            // worse than reporting without it.
            _progress.Report(new MediaDerivativeProgressUpdate(
                message.ProjectId,
                message.SourceProjectFileId,
                message.SourceStorageKey,
                CacheKey: null,
                stage,
                PercentComplete: null,
                detail));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Media derivative progress sink threw while reporting {Stage}", stage);
        }
    }
}
