using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Editing;

/// <summary>
/// The plain manifest seed puts the compile's ORIGINAL picture on the timeline. Censor boxes are windowed out of it
/// (the manifest carries them), but a region effect (blur or pixelate of one tracked object) is not in the manifest,
/// so the plain seed cannot withhold it. When the seed step applied one, falling back to the plain seed would hand the
/// user the uncensored picture, so that fallback is refused.
/// </summary>
public static class PlainSeedCensorGuard
{
    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Error code returned with the 409 when the plain seed would reveal a censored stretch.</summary>
    public const string Code = "SEED_WOULD_REVEAL_CENSORED";

    /// <summary>The plain-language message that goes with <see cref="Code"/>.</summary>
    public const string Message =
        "This film hides part of the picture, and the editor cannot carry that over from here. " +
        "A timeline built from it would show the original, uncensored footage, so nothing was created. " +
        "Open the finished film instead, or start the timeline from the step before the censoring.";

    /// <summary>
    /// True when a plain seed of <paramref name="seed"/> could show picture that the run (or the editor render's own
    /// timeline) hides. Two kinds of seed step are judged, each from what the RUN used (its snapshot definition's step
    /// when the run has one, the live step only for a legacy run):
    /// a compile with region effects (or unreadable settings), and an editor render (<c>TimelineRender</c>) whose rendered
    /// document carried a mask or region effect, because the render's manifest drops both. An editor render whose document
    /// cannot be read is refused too (fail closed); a render of a timeline with no mask or region effect still seeds.
    /// </summary>
    public static async Task<bool> PlainSeedWouldRevealAsync(InferenceApiDbContext db, Guid projectId, WorkflowStepResult seed, CancellationToken ct)
    {
        foreach (WorkflowStep step in await RunStepsAsync(db, seed, ct))
        {
            if (await StepWouldRevealAsync(db, projectId, step, ct))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> StepWouldRevealAsync(InferenceApiDbContext db, Guid projectId, WorkflowStep step, CancellationToken ct)
    {
        if (step.StepType == StepType.TimelineRender)
        {
            return await RenderedDocumentHidesPictureAsync(db, projectId, step.TimelineRenderConfigJson, ct);
        }

        if (step is not { StepType: StepType.VideoCompile, VideoCompileConfigJson: { Length: > 0 } json })
        {
            return false;
        }

        try
        {
            VideoCompileStepConfig? config = JsonSerializer.Deserialize<VideoCompileStepConfig>(json, ConfigJson);
            return config?.RegionEffects is { Count: > 0 };
        }
        catch (JsonException)
        {
            // Cannot prove the step applied no region effect: fail closed.
            return true;
        }
    }

    /// <summary>
    /// The step as the run had it: the snapshot definition's step of the same order, else the live step (legacy run).
    /// When the two differ in type the order match cannot be trusted, so both are judged.
    /// </summary>
    private static async Task<List<WorkflowStep>> RunStepsAsync(InferenceApiDbContext db, WorkflowStepResult seed, CancellationToken ct)
    {
        WorkflowStep? live = await db.WorkflowSteps.AsNoTracking().FirstOrDefaultAsync(s => s.Id == seed.WorkflowStepId, ct);
        if (live is null)
        {
            return [];
        }

        Guid? snapshotId = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == seed.WorkflowExecutionId)
            .Select(e => e.SnapshotDefinitionId)
            .FirstOrDefaultAsync(ct);
        if (snapshotId is { } id && live.WorkflowDefinitionId != id)
        {
            WorkflowStep? frozen = await db.WorkflowSteps.AsNoTracking()
                .FirstOrDefaultAsync(s => s.WorkflowDefinitionId == id && s.StepOrder == live.StepOrder, ct);
            if (frozen is not null)
            {
                return frozen.StepType == live.StepType ? [frozen] : [frozen, live];
            }
        }

        return [live];
    }

    /// <summary>
    /// The render's manifest drops masks and effects, so the plain seed of an editor render would show the original.
    /// Refuse when the rendered timeline (its pinned revision; an unpinned render is judged on the current document AND
    /// every stored revision, because which one it drew is unknown) has any censor-shaped clip anywhere, including inside
    /// nested sequences, or when a document cannot be read.
    /// </summary>
    private static async Task<bool> RenderedDocumentHidesPictureAsync(InferenceApiDbContext db, Guid projectId, string? configJson, CancellationToken ct)
    {
        try
        {
            TimelineRenderStepConfig? config = string.IsNullOrWhiteSpace(configJson)
                ? null
                : JsonSerializer.Deserialize<TimelineRenderStepConfig>(configJson, ConfigJson);
            if (config is null)
            {
                return true;
            }

            EditTimelineEntity? timeline = await db.EditTimelines.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == config.EditTimelineId && t.ProjectId == projectId, ct);
            if (timeline is null)
            {
                return true;
            }

            if (config.Revision is null)
            {
                List<string> stored = await db.EditTimelineSnapshots.AsNoTracking()
                    .Where(s => s.TimelineId == timeline.Id)
                    .Select(s => s.DocumentJson)
                    .ToListAsync(ct);
                return stored.Append(timeline.DocumentJson).Any(DocumentHidesPicture);
            }

            int revision = config.Revision.Value;
            string? documentJson = revision == timeline.Revision
                ? timeline.DocumentJson
                : await db.EditTimelineSnapshots.AsNoTracking()
                    .Where(s => s.TimelineId == timeline.Id && s.Revision == revision)
                    .Select(s => s.DocumentJson)
                    .FirstOrDefaultAsync(ct);
            return documentJson is null || DocumentHidesPicture(documentJson);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>True for a censor-shaped document, or one that cannot be read (fail closed).</summary>
    private static bool DocumentHidesPicture(string documentJson)
    {
        try
        {
            EditTimeline? document = JsonSerializer.Deserialize<EditTimeline>(documentJson, EditTimelineJson.Options);
            if (document is null || document.Tracks is null)
            {
                return true;
            }

            // AllTracks covers the top-level tracks and every sequence's tracks (compound and nested clips are flattened by the render).
            return TimelineAssetReferences.AllTracks(document)
                .SelectMany(t => t.Items ?? [])
                .Any(IsCensorShaped);
        }
        catch (Exception ex) when (ex is JsonException or NullReferenceException)
        {
            return true;
        }
    }

    private static bool IsCensorShaped(Item item) => item switch
    {
        MediaClip clip => clip.Mask is not null || clip.RegionEffects is { Count: > 0 } || HasObscuringEffect(clip.Effects),
        AdjustmentClip adjustment => HasObscuringEffect(adjustment.Effects),
        _ => false
    };

    private static bool HasObscuringEffect(IReadOnlyList<ClipEffect>? effects) =>
        effects?.Any(e => e is not null && (e.Type == "blur" || e.Type == "pixelate")) == true;
}
