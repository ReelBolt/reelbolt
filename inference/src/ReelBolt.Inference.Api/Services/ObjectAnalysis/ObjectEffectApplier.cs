using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.EditTimelines;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.ObjectAnalysis;

/// <summary>
/// The finalizer behind "blur the license plate": for requests that carry a timeline intent, writes the effect onto the named
/// clips once the analysis has passed, without a browser. One sweep claims each ready row with a single conditional UPDATE (so
/// replicas never double-apply), then writes every clip in ONE <see cref="IEditTimelineWriter"/> edit.
/// </summary>
/// <remarks>
/// Row lifecycle: <c>queued</c> -> (derived from the run) <c>succeeded</c> | <c>failed</c> -> (claimed) <c>applied</c> | <c>apply_failed</c>.
/// A claim is the row going to <c>applied</c> with no <c>AppliedRevision</c> yet and <c>FinalizedAt</c> as the claim time; a claim older than
/// <see cref="ClaimLease"/> with no revision was abandoned (the replica died) and is claimed again. That is safe because a clip
/// that already carries this analysis's effect is left alone, so a replay never stacks a second copy. For an applied row that
/// skipped clips, <c>FailureMessage</c> holds the plain-words note of what was skipped and why.
/// Rows with no timeline intent are only finalized (queued -> succeeded | failed) like a status read does; nothing is applied.
/// </remarks>
public sealed class ObjectEffectApplier(
    InferenceApiDbContext db,
    IObjectAnalysisService analysis,
    IEditTimelineWriter writer,
    ILogger<ObjectEffectApplier> logger,
    TimeProvider? clock = null,
    ITenantContext? tenant = null)
{
    /// <summary>How long a claim may sit without a result before another sweep takes it over.</summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(5);

    public const int BatchSize = 50;
    public const string ApplyFailedCode = "APPLY_FAILED";
    public const string IntervalKey = "ObjectEffects:ApplySweepSeconds";
    public const int DefaultIntervalSeconds = 15;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Seconds between sweeps; 0 (or less) switches the finalizer off.</summary>
    public static int IntervalSeconds(IConfiguration? configuration) =>
        Math.Max(0, configuration?.GetValue(IntervalKey, DefaultIntervalSeconds) ?? DefaultIntervalSeconds);

    /// <summary>One pass. Returns how many requests were applied to a timeline.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        // The sweep spans every organization; each write below is pinned to the row's own project.
        using IDisposable system = tenant.System();

        DateTime now = Now();
        DateTime stale = now - ClaimLease;
        ExecutionStatus[] terminal = [ExecutionStatus.Passed, ExecutionStatus.Failed, ExecutionStatus.Cancelled];

        List<Guid> ids = await db.ObjectEffectRequests.AsNoTracking()
            .Where(r =>
                (r.Status == ObjectEffectRequest.StatusQueued
                    && db.WorkflowExecutions.Any(e => e.Id == r.ExecutionId && terminal.Contains(e.Status)))
                || (r.TimelineId != null && r.Status == ObjectEffectRequest.StatusSucceeded)
                || (r.TimelineId != null && r.Status == ObjectEffectRequest.StatusApplied
                    && r.AppliedRevision == null && r.FinalizedAt != null && r.FinalizedAt < stale))
            .OrderBy(r => r.CreatedAt)
            .Select(r => r.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        int applied = 0;
        foreach (Guid id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await FinalizeAsync(id, ct)) applied++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Finalizing object-effect request {RequestId} failed; the next sweep will retry", id);
                db.ChangeTracker.Clear();
            }
        }

        return applied;
    }

    /// <summary>
    /// Applies ONE request now (the assistant's reuse path), with exactly the sweep's claim and write, and never sweeps other rows.
    /// The row must belong to <paramref name="projectId"/>. Returns whether it was applied to the timeline.
    /// </summary>
    public async Task<bool> ApplyOneAsync(Guid projectId, Guid requestId, CancellationToken ct)
    {
        using IDisposable system = tenant.System();
        return await FinalizeAsync(requestId, ct, projectId);
    }

    private DateTime Now()
    {
        // Millisecond precision so the claim token round-trips through the database exactly.
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }

    private async Task<bool> FinalizeAsync(Guid id, CancellationToken ct, Guid? onlyProject = null)
    {
        // A copy tracked earlier in this scope would hide a claim another replica made since: always read the row afresh.
        foreach (var tracked in db.ChangeTracker.Entries<ObjectEffectRequest>().Where(e => e.Entity.Id == id).ToList())
        {
            tracked.State = EntityState.Detached;
        }

        ObjectEffectRequest? row = await db.ObjectEffectRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null || (onlyProject is { } pinned && row.ProjectId != pinned)) return false;

        ObjectAnalysisDerived derived = await analysis.DeriveAsync(row, ct);
        if (!derived.Terminal || row.TimelineId is not { } timelineId) return false;

        bool reclaim = row.Status == ObjectEffectRequest.StatusApplied;
        if (row.Status is not (ObjectEffectRequest.StatusSucceeded or ObjectEffectRequest.StatusApplied)) return false;

        DateTime claimedAt = Now();
        IQueryable<ObjectEffectRequest> target = db.ObjectEffectRequests.Where(r => r.Id == id);
        DateTime? previousClaim = row.FinalizedAt;
        int claimed = reclaim
            ? await target.Where(r => r.Status == ObjectEffectRequest.StatusApplied && r.AppliedRevision == null && r.FinalizedAt == previousClaim)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.FinalizedAt, claimedAt), ct)
            : await target.Where(r => r.Status == ObjectEffectRequest.StatusSucceeded)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, ObjectEffectRequest.StatusApplied)
                    .SetProperty(r => r.FinalizedAt, claimedAt), ct);
        if (claimed != 1) return false;

        // Writes from here are guarded by Status == applied AND this claim's token, so a replica whose lease lapsed cannot
        // overwrite the newer claim's outcome.
        IQueryable<ObjectEffectRequest> mine = db.ObjectEffectRequests
            .Where(r => r.Id == id && r.Status == ObjectEffectRequest.StatusApplied && r.AppliedRevision == null && r.FinalizedAt == claimedAt);

        if (derived.Status != ObjectAnalysisStatus.Succeeded || derived.StepResultId is not { } stepResultId || derived.TrackIds is not { Count: > 0 } trackIds)
        {
            await FailAsync(mine, derived.FailureCode ?? ApplyFailedCode, derived.Message ?? ObjectAnalysisMessages.For(null), ct);
            return false;
        }

        // A reclaimed row may already have written its own edit (the replica died before recording the revision), which would read as
        // a change; replaying is safe because an identical effect is left alone, and a vanished clip still sends it to review below.
        if (await TimelineChangedSinceAsync(row, timelineId, ct))
        {
            await ToNeedsReviewAsync(mine, ct);
            return false;
        }

        ApplyOutcome outcome = await ApplyAsync(row, timelineId, stepResultId, trackIds, ct);
        if (outcome.NeedsReview)
        {
            await ToNeedsReviewAsync(mine, ct);
            return false;
        }

        if (outcome.Revision is { } revision)
        {
            await mine.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.AppliedRevision, (long?)revision)
                .SetProperty(r => r.FailureMessage, outcome.Note), ct);
            return true;
        }

        await FailAsync(mine, ApplyFailedCode, outcome.Note ?? "The effect could not be added to the timeline.", ct);
        return false;
    }

    private static async Task FailAsync(IQueryable<ObjectEffectRequest> mine, string code, string message, CancellationToken ct) =>
        await mine.ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, ObjectEffectRequest.StatusApplyFailed)
            .SetProperty(r => r.FailureCode, code)
            .SetProperty(r => r.FailureMessage, message), ct);

    /// <summary>Clock-skew margin: an edit within this long after the request was made is the edit that launched it, not a change.</summary>
    private static readonly TimeSpan ChangeSkew = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The change signal: any committed op batch on the timeline created after the request (plus the skew margin), other than
    /// the batches this finalizer itself wrote for other requests. Every editor, assistant and server edit commits an op row, so this
    /// also covers trims, moves, splits and deletes made while the analysis ran.
    /// </summary>
    private async Task<bool> TimelineChangedSinceAsync(ObjectEffectRequest row, Guid timelineId, CancellationToken ct)
    {
        DateTime since = row.CreatedAt + ChangeSkew;
        List<int> ours = (await db.ObjectEffectRequests.AsNoTracking()
            .Where(r => r.TimelineId == timelineId && r.AppliedRevision != null)
            .Select(r => r.AppliedRevision!.Value)
            .ToListAsync(ct)).Select(v => (int)v).ToList();
        return await db.EditTimelineOps.AsNoTracking()
            .AnyAsync(o => o.TimelineId == timelineId && o.CreatedAt > since && !ours.Contains(o.Revision), ct);
    }

    private static async Task ToNeedsReviewAsync(IQueryable<ObjectEffectRequest> mine, CancellationToken ct) =>
        await mine.ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, ObjectEffectRequest.StatusNeedsReview)
            .SetProperty(r => r.FailureMessage, (string?)"The timeline changed while the effect was being prepared; it is waiting for your OK in the editor."), ct);

    private sealed record ApplyOutcome(long? Revision, string? Note, bool NeedsReview = false);

    private async Task<ApplyOutcome> ApplyAsync(
        ObjectEffectRequest row, Guid timelineId, Guid stepResultId, IReadOnlyList<string> trackIds, CancellationToken ct)
    {
        HashSet<Guid> wanted = ParseItemIds(row.TimelineItemIdsJson);
        if (wanted.Count == 0) return new ApplyOutcome(null, "No clips were named for the effect.");
        if (!Guid.TryParse(row.SourceId, out Guid sourceId)) return new ApplyOutcome(null, "The analysed video could not be identified.");

        AssetRef analysed = row.SourceKind == ObjectEffectRequest.SourceFile
            ? new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: sourceId)
            : new AssetRef(AssetRefKind.StepResult, StepResultId: sourceId);

        RegionClipEffect entry = new(
            row.Effect.ToLowerInvariant(),
            row.Strength,
            new ShapeMask(
                Kind: ShapeMask.SegmentationKind,
                Invert: row.Invert,
                AnalysisStepResultId: stepResultId,
                TrackIds: trackIds,
                FeatherPx: row.FeatherPx),
            Enabled: true);

        HashSet<Guid> excluded = [];
        Dictionary<Guid, string> notes = [];
        bool targetChanged = false;
        for (int round = 0; round < 3; round++)
        {
            Dictionary<string, Guid> pathToClip = [];
            HashSet<Guid> seen = [];
            List<Guid> added = [], updated = [], same = [];
            Dictionary<Guid, string> roundNotes = [];

            ServerEditMutation? Mutate(EditTimeline doc)
            {
                pathToClip.Clear();
                seen.Clear();
                added.Clear();
                updated.Clear();
                same.Clear();
                roundNotes.Clear();
                targetChanged = false;
                JsonArray ops = [];

                List<Track> tracks = [];
                for (int t = 0; t < doc.Tracks.Count; t++)
                {
                    Track track = doc.Tracks[t];
                    List<Item> items = [];
                    bool trackChanged = false;
                    for (int i = 0; i < track.Items.Count; i++)
                    {
                        Item item = track.Items[i];
                        if (item is not MediaClip clip || !wanted.Contains(clip.Id))
                        {
                            items.Add(item);
                            continue;
                        }

                        seen.Add(clip.Id);
                        if (excluded.Contains(clip.Id) || notes.ContainsKey(clip.Id))
                        {
                            items.Add(item);
                            continue;
                        }

                        if (clip.AssetRef != analysed)
                        {
                            targetChanged = true;
                            roundNotes[clip.Id] = $"{Label(clip)} now plays a different video than the one that was analysed";
                            items.Add(item);
                            continue;
                        }

                        List<RegionClipEffect> current = clip.RegionEffects?.ToList() ?? [];
                        int existing = current.FindIndex(e => e.Mask is { } m && m.AnalysisStepResultId == stepResultId
                            && string.Equals(e.Effect, entry.Effect, StringComparison.Ordinal)
                            && (m.TrackIds ?? []).SequenceEqual(trackIds));
                        if (existing >= 0)
                        {
                            RegionClipEffect old = current[existing];
                            if (old.Strength == entry.Strength && old.Mask.Invert == entry.Mask.Invert
                                && old.Mask.FeatherPx == entry.Mask.FeatherPx && old.Enabled == entry.Enabled)
                            {
                                // Identical: written by an earlier (possibly interrupted) pass of this same request, or asked twice.
                                same.Add(clip.Id);
                                items.Add(item);
                                continue;
                            }

                            // Same object and effect, different look (strength, "everything except it", edge): change that entry.
                            current[existing] = entry;
                            items.Add(WithRegionEffects(clip, current));
                            trackChanged = true;
                            updated.Add(clip.Id);
                            pathToClip[$"tracks[{t}].items[{i}]"] = clip.Id;
                            ops.Add(SetRegionEffectsOp(clip.Id, current));
                            continue;
                        }

                        if (current.Count >= EditTimelineValidator.MaxRegionEffects)
                        {
                            roundNotes[clip.Id] = $"{Label(clip)} already has {EditTimelineValidator.MaxRegionEffects} effects on an object";
                            items.Add(item);
                            continue;
                        }

                        current.Add(entry);
                        items.Add(WithRegionEffects(clip, current));
                        trackChanged = true;
                        added.Add(clip.Id);
                        pathToClip[$"tracks[{t}].items[{i}]"] = clip.Id;
                        ops.Add(SetRegionEffectsOp(clip.Id, current));
                    }

                    tracks.Add(trackChanged ? track with { Items = items } : track);
                }

                foreach (Guid missing in wanted.Where(w => !seen.Contains(w) && !notes.ContainsKey(w) && !excluded.Contains(w)))
                {
                    roundNotes[missing] = "a clip was no longer on the timeline";
                    targetChanged = true;
                }

                if (targetChanged && round == 0) return null;
                return ops.Count == 0 ? null : new ServerEditMutation(doc with { Tracks = tracks }, ops.ToJsonString());
            }

            ServerEditResult result = await writer.ApplyServerEditAsync(row.ProjectId, timelineId, Mutate, ct);
            if (targetChanged && round == 0 && result.Outcome == ServerEditOutcome.NoChange) return new ApplyOutcome(null, null, NeedsReview: true);
            foreach ((Guid clip, string note) in roundNotes) notes[clip] = note;

            switch (result.Outcome)
            {
                case ServerEditOutcome.Applied:
                case ServerEditOutcome.NoChange when same.Count > 0:
                    return new ApplyOutcome(result.Revision, Summarize(wanted.Count, added.Count, updated.Count, same.Count, notes));
                case ServerEditOutcome.NoChange:
                    return new ApplyOutcome(null, Summarize(wanted.Count, 0, 0, 0, notes) ?? "None of the named clips could take the effect.");
                case ServerEditOutcome.NotFound:
                    return new ApplyOutcome(null, "The timeline is no longer available.");
                case ServerEditOutcome.TooLarge:
                    return new ApplyOutcome(null, "The timeline is too large to take the edit.");
                case ServerEditOutcome.Conflict:
                    return new ApplyOutcome(null, "The timeline kept changing while the effect was being added. Ask again.");
                case ServerEditOutcome.Invalid:
                    {
                        // The validator (the mask binding rule above all) may refuse single clips: leave those out and write the rest.
                        HashSet<Guid> refused = [];
                        bool foreign = false;
                        foreach ((string path, string message) in result.Violations ?? [])
                        {
                            string? owner = pathToClip.Keys.FirstOrDefault(p => path.StartsWith(p + ".", StringComparison.Ordinal));
                            if (owner is null) foreign = true;
                            else
                            {
                                Guid clipId = pathToClip[owner];
                                refused.Add(clipId);
                                notes[clipId] = $"a clip could not take the effect: {message}";
                            }
                        }

                        if (foreign || refused.Count == 0)
                        {
                            return new ApplyOutcome(null, result.Message ?? "The timeline would not accept the effect as it stands.");
                        }

                        excluded.UnionWith(refused);
                        break;
                    }
            }
        }

        return new ApplyOutcome(null, "The effect could not be added to the clips.");
    }

    private static string Label(MediaClip clip) => string.IsNullOrWhiteSpace(clip.Name) ? "a clip" : $"\"{clip.Name}\"";

    private static JsonObject SetRegionEffectsOp(Guid clipId, IReadOnlyList<RegionClipEffect> effects) => new()
    {
        ["type"] = "setProperty",
        ["itemId"] = clipId.ToString("D"),
        ["key"] = "regionEffects",
        ["value"] = JsonSerializer.SerializeToNode(effects, EditTimelineJson.Options)
    };

    /// <summary>Null when every clip simply got the effect; otherwise plain words saying what was added, changed, left alone and skipped.</summary>
    private static string? Summarize(int wanted, int added, int updated, int same, Dictionary<Guid, string> notes)
    {
        if (notes.Count == 0 && updated == 0 && same == 0) return null;
        List<string> parts = [$"Added to {added} of {wanted} clips"];
        if (updated > 0) parts.Add($"changed the existing effect on {updated}");
        if (same > 0) parts.Add($"left {same} as they were (already the same)");
        string text = string.Join(", ", parts) + ".";
        return notes.Count == 0 ? text : $"{text} Skipped: {string.Join("; ", notes.Values)}.";
    }

    private static HashSet<Guid> ParseItemIds(string? json)
    {
        HashSet<Guid> ids = [];
        if (string.IsNullOrWhiteSpace(json)) return ids;
        try
        {
            foreach (string? text in JsonSerializer.Deserialize<List<string?>>(json) ?? [])
            {
                if (Guid.TryParse(text, out Guid id)) ids.Add(id);
            }
        }
        catch (JsonException)
        {
        }

        return ids;
    }

    /// <summary>A copy of <paramref name="clip"/> with its region effects replaced (the contract's properties are read-only, so there is no <c>with</c>).</summary>
    public static MediaClip WithRegionEffects(MediaClip c, IReadOnlyList<RegionClipEffect> effects) =>
        new(c.Id, c.Name, c.RecordIn, c.RecordOut, c.AssetRef, c.SourceIn, c.PlaybackRate, c.Enabled, c.Provenance, c.GainDb, c.Transform,
            c.Crop, c.Reverse, c.Hold, c.FadeInFrames, c.FadeOutFrames, c.Grade, c.Mask, c.Keyer, c.Effects, c.Stabilize, c.Reframe,
            c.RetimeQuality, c.CornerPin, c.ChannelMap, c.LinkId, effects, c.InputProfile, c.SourceRotation);
}
