using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;

namespace ReelBolt.Inference.Api.Services.Editing;

/// <summary>
/// The one implementation of "where the analysis of this media stands" and "queue it": the media-token lookup, the
/// held/quarantined refusals granted by <see cref="OutputGate"/> and the file row, the per-kind reuse rules, the stale
/// queued/running answer, the cached result read and the queue+publish path. The media-analysis controller and the
/// assistant's <c>GetMusicBeats</c> tool both go through it, so a read or a start means the same thing on either surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never a storage key.</b> <see cref="Source"/> carries one because callers need it to publish the request; it is
/// never serialized into a tool result.
/// </para>
/// <para>
/// <b>Quarantined is distinct from missing.</b> The controller answers 404 for both (the same answer the file itself
/// gives); the assistant surface can tell the caller plainly that moderation blocked the file.
/// </para>
/// </remarks>
public static class MediaAnalysisAccess
{
    /// <summary>A queued or running kind older than this is reported failed: the engine never answered.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(60);

    /// <summary>The plain refusal for a file whose bytes are not audio or video.</summary>
    public const string NotAnalysableMessage = "Only video and audio files can be analysed.";

    /// <summary>The plain reason a queued/running row older than <see cref="StaleAfter"/> is reported failed with.</summary>
    public const string TimedOutReason = "The analysis took too long and was stopped. Try again.";

    /// <summary>The plain sentence a failed publish is reported with (never the provider's own words).</summary>
    public const string QueueFailedMessage = "We couldn't start the analysis. Try again in a moment.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Whether the media token names a usable source of this project.</summary>
    public enum SourceStatus
    {
        Found,
        NotFound,
        Quarantined
    }

    /// <summary>The media a token names, for this project only (quarantined media is its own status). Duration comes from the ingest probe.</summary>
    public sealed record Source(string StorageKey, Guid? FileId, bool Analysable, double? DurationSec, string? FileName);

    /// <summary>The lookup: a source, or why there is none.</summary>
    public sealed record SourceLookup(SourceStatus Status, Source? Source);

    /// <summary>True when the media a token names is a step result or file of this project whose bytes are not served.</summary>
    public static async Task<bool> IsHeldAsync(InferenceApiDbContext db, Guid projectId, string mediaRef, CancellationToken ct)
    {
        string[] parts = mediaRef.Split(':', 2);
        return parts.Length == 2 && Guid.TryParse(parts[1], out Guid id)
            && await OutputGate.IsOutputIdHeldAsync(db, projectId, id, ct);
    }

    /// <summary>
    /// The media a token names, for this project only. A quarantined project file (moderation or CSAM) is
    /// <see cref="SourceStatus.Quarantined"/>; an unknown token, a foreign file and a foreign step result are
    /// <see cref="SourceStatus.NotFound"/>.
    /// </summary>
    public static async Task<SourceLookup> FindSourceAsync(
        InferenceApiDbContext db, Guid projectId, string mediaRef, CancellationToken ct)
    {
        string[] parts = mediaRef.Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out Guid id))
            return new SourceLookup(SourceStatus.NotFound, null);

        string? storageKey;
        string? mime;
        Guid? fileId = null;
        string? fileName = null;
        if (parts[0] == "file")
        {
            ProjectFile? file = await db.ProjectFiles.AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == id && f.ProjectId == projectId, ct);
            if (file is null)
                return new SourceLookup(SourceStatus.NotFound, null);
            if (file.ModerationStatus == FileModerationStatus.Quarantined || file.CsamStatus == CsamScanStatus.Quarantined)
                return new SourceLookup(SourceStatus.Quarantined, null);

            storageKey = file.StorageKey;
            mime = file.MimeType;
            fileId = file.Id;
            fileName = file.OriginalFileName;
        }
        else
        {
            string prefix = $"projects/{projectId}/outputFiles/";
            storageKey = await db.WorkflowStepResults.AsNoTracking()
                .Where(r => r.Id == id && r.WorkflowExecution.ProjectId == projectId && r.OutputStorageKey != null && r.OutputStorageKey.StartsWith(prefix))
                .Select(r => r.OutputStorageKey).FirstOrDefaultAsync(ct);
            if (storageKey is null)
                return new SourceLookup(SourceStatus.NotFound, null);
            mime = Path.GetExtension(storageKey).ToLowerInvariant() switch
            {
                ".mp4" or ".mov" or ".webm" or ".mkv" => "video/mp4",
                ".mp3" or ".wav" or ".m4a" or ".aac" => "audio/mpeg",
                _ => null
            };
        }

        Dictionary<string, MediaSourceProbe> probes = await StoredMediaProbes.LoadAsync(db, projectId, [storageKey], ct);
        double? duration = probes.TryGetValue(storageKey, out MediaSourceProbe? probe) && probe.DurationSec > 0 ? probe.DurationSec : null;
        bool analysable = mime is not null && (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));
        return new SourceLookup(SourceStatus.Found, new Source(storageKey, fileId, analysable, duration, fileName));
    }

    /// <summary>The newest 200 request rows for one project/media token.</summary>
    public static async Task<List<MediaAnalysisRequest>> LoadRowsAsync(
        InferenceApiDbContext db, Guid projectId, string mediaRef, CancellationToken ct) =>
        await db.MediaAnalysisRequests.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.MediaRef == mediaRef)
            .OrderByDescending(r => r.UpdatedAt).Take(200).ToListAsync(ct);

    /// <summary>
    /// True when the row's result is stored for its organization and can answer a new request. An <c>events</c> result made
    /// by another version of the detector, or one marked degraded (<see cref="MediaAnalysisJson.EventsAreCurrent"/>, read
    /// from its small summary), does not count: the kind is then queued again and the fresh result replaces the stored one. Reading is another
    /// matter: <see cref="ToStateAsync"/> still serves such a result, marked outdated.
    /// </summary>
    public static async Task<bool> ResultExistsAsync(
        InferenceApiDbContext db, MediaAnalysisRequest row, Guid organizationId, CancellationToken ct)
    {
        if (row.ResultId is not { } id) return false;
        if (row.Kind != MediaAnalysisKinds.Events)
            return await db.MediaAnalysisResults.AsNoTracking().AnyAsync(r => r.Id == id && r.OrganizationId == organizationId, ct);
        string? summary = await db.MediaAnalysisResults.AsNoTracking().Where(r => r.Id == id && r.OrganizationId == organizationId)
            .Select(r => r.SummaryJson).FirstOrDefaultAsync(ct);
        return MediaAnalysisJson.EventsAreCurrent(summary);
    }

    /// <summary>True when a row can answer this request instead of queueing again (same rules on every surface).</summary>
    public static async Task<bool> IsReusableAsync(
        InferenceApiDbContext db, MediaAnalysisRequest row, Guid organizationId, DateTime now, CancellationToken ct) =>
        row.State switch
        {
            MediaAnalysisStates.Queued or MediaAnalysisStates.Running => now - row.UpdatedAt < StaleAfter,
            MediaAnalysisStates.Ready => await ResultExistsAsync(db, row, organizationId, ct),
            _ => false
        };

    /// <summary>
    /// The row's answer: stale in-flight rows read failed, ready rows carry their stored summary, and a ready result that is
    /// gone reads none. An events result of another detector version (every file analysed before results carried one), or one
    /// marked degraded (a part of the detection failed), stays
    /// ready, so the Events lane shows its marks, and is marked <see cref="MediaAnalysisKindState.Outdated"/> so the
    /// editor can offer to find the events again.
    /// </summary>
    public static async Task<MediaAnalysisKindState> ToStateAsync(
        InferenceApiDbContext db, MediaAnalysisRequest row, DateTime now, CancellationToken ct)
    {
        string state = row.State;
        string? reason = row.Reason;
        JsonElement? summary = null;
        if (state is MediaAnalysisStates.Queued or MediaAnalysisStates.Running && now - row.UpdatedAt >= StaleAfter)
        {
            state = MediaAnalysisStates.Failed;
            reason = TimedOutReason;
        }
        else if (state == MediaAnalysisStates.Ready)
        {
            string? summaryJson = row.ResultId is { } id
                ? await db.MediaAnalysisResults.AsNoTracking().Where(r => r.Id == id && r.OrganizationId == row.OrganizationId)
                    .Select(r => r.SummaryJson).FirstOrDefaultAsync(ct)
                : null;
            if (summaryJson is null) return new MediaAnalysisKindState("none", null, row.RequestId, row.UpdatedAt, null);
            summary = JsonSerializer.Deserialize<JsonElement>(summaryJson, Json);
            if (row.Kind == MediaAnalysisKinds.Events && !MediaAnalysisJson.EventsAreCurrent(summaryJson))
                return new MediaAnalysisKindState(state, null, row.RequestId, row.UpdatedAt, summary, Outdated: true);
        }

        return new MediaAnalysisKindState(state, state == MediaAnalysisStates.Failed ? reason : null, row.RequestId, row.UpdatedAt, summary);
    }

    /// <summary>The stored result JSON for a ready row, or null when it is missing.</summary>
    public static async Task<string?> ReadResultJsonAsync(
        InferenceApiDbContext db, MediaAnalysisRequest row, CancellationToken ct) =>
        row.ResultId is { } id
            ? await db.MediaAnalysisResults.AsNoTracking()
                .Where(r => r.Id == id && r.OrganizationId == row.OrganizationId)
                .Select(r => r.ResultJson).FirstOrDefaultAsync(ct)
            : null;

    /// <summary>A brand-new queued row for one kind, in the shape every surface writes.</summary>
    public static MediaAnalysisRequest NewRequest(
        Guid requestId, Guid projectId, Guid organizationId, string mediaRef, string kind, string language,
        Guid? userId, DateTime now) => new()
        {
            Id = Guid.NewGuid(),
            RequestId = requestId,
            ProjectId = projectId,
            OrganizationId = organizationId,
            MediaRef = mediaRef,
            Kind = kind,
            Language = language,
            State = MediaAnalysisStates.Queued,
            RequestedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now
        };

    /// <summary>
    /// Writes the fresh rows and publishes the one request. A failed publish marks them failed in plain words and
    /// returns false, so the caller answers 503 (or a failed status) and nothing pretends to be running.
    /// </summary>
    public static async Task<bool> QueueAsync(
        InferenceApiDbContext db,
        IPublishEndpoint publish,
        Guid projectId,
        Guid organizationId,
        Guid? sourceFileId,
        string sourceStorageKey,
        string? language,
        IReadOnlyList<MediaAnalysisRequest> fresh,
        Guid? userId,
        double? heldSeconds,
        IDictionary<string, MediaAnalysisKindState> states,
        ILogger? logger,
        CancellationToken ct)
    {
        if (fresh.Count == 0) return true;

        Guid requestId = fresh[0].RequestId;
        db.MediaAnalysisRequests.AddRange(fresh);
        await db.SaveChangesAsync(ct);
        try
        {
            await publish.Publish(new MediaAnalysisRequested(
                requestId, projectId, organizationId, sourceFileId, sourceStorageKey,
                fresh.Select(r => r.Kind).ToList(), language, userId, heldSeconds), ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Could not queue media analysis {RequestId}", requestId);
            foreach (MediaAnalysisRequest row in fresh)
            {
                row.State = MediaAnalysisStates.Failed;
                row.Reason = QueueFailedMessage;
                row.UpdatedAt = DateTime.UtcNow;
                row.HoldSettled = true;
                states[row.Kind] = new MediaAnalysisKindState(row.State, row.Reason, requestId, row.UpdatedAt, null);
            }

            await db.SaveChangesAsync(CancellationToken.None);
            return false;
        }
    }
}
