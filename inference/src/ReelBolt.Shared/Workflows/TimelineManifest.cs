namespace ReelBolt.Shared.Workflows;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
/// A single point on the background-music ducking envelope in Unified Program Mode / Expanded Studio Tracks.
/// </summary>
public sealed record TimelineDuckingPoint(
    double TimeSec,
    double GainDb);

/// <summary>
/// The B8b output policy ACTUALLY applied to a rendered program: the plan's height cap and the
/// watermark decision, recorded as facts rather than re-derived by a reader.
///
/// <para>
/// <see cref="HeightCapApplied"/> and <see cref="WatermarkApplied"/> are false for an unentitled
/// org (nothing was constrained) AND for the one entitled path that carries no filtergraph at all
/// (<c>Mode = StreamCopy</c> — see <see cref="VideoCompileMode"/>): the policy is expressed as
/// ffmpeg filters, so a stream copy applies none of it. <see cref="WatermarkSkippedReason"/> names
/// that case, and the "no configured asset" case, explicitly — null means "nothing was skipped"
/// (applied, or not entitled in the first place).
/// </para>
/// </summary>
public sealed record TimelineOutputPolicy(
    /// <summary>Plan key the policy was read from (<c>trial</c>, <c>pro</c>, <c>selfhost</c>…).</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Tier = null,

    /// <summary>The plan's tallest permitted output height in pixels; null = unlimited.</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? MaxHeight = null,

    /// <summary>Whether the height cap was actually part of the encode that produced this file.</summary>
    bool HeightCapApplied = false,

    /// <summary>The height of the file this manifest describes, after the policy; null when no re-encode ran (nothing to state).</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? OutputHeight = null,

    /// <summary>Whether the plan entitles this org to a watermarked output at all.</summary>
    bool WatermarkEntitled = false,

    /// <summary>Whether the ReelBolt mark was actually composited into the encode.</summary>
    bool WatermarkApplied = false,

    /// <summary>Why an entitled watermark was not applied (<c>watermark_asset_not_configured</c>, <c>watermark_asset_missing</c>, <c>stream_copy_has_no_filter_stage</c>); null when it was applied or never entitled.</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? WatermarkSkippedReason = null);

/// <summary>
/// Output video metadata for the compiled timeline manifest.
/// </summary>
public sealed record TimelineOutputVideo(
    int Width,
    int Height,
    double Fps,
    double DurationSec,
    string StorageKey,
    /// <summary>
    /// The trial output policy applied to this render (B8b), or null when no entitlement set was
    /// resolvable (no organization — the single-tenant/self-host sentinel). Null keeps the
    /// serialized manifest byte-identical to the pre-B8b payload.
    ///
    /// <para>
    /// <see cref="Width"/>/<see cref="Height"/> stay the AUTHORING canvas the manifest's geometry is
    /// expressed in (every track's normalized coordinates are relative to it); this node carries the
    /// cap that the encoder then applied on top, so a reader that needs the real pixel height of the
    /// file reads <see cref="TimelineOutputPolicy.OutputHeight"/>.
    /// </para>
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    TimelineOutputPolicy? OutputPolicy = null);

/// <summary>
/// One physical source clip referenced by items on this timeline.
/// </summary>
/// <remarks>
/// <see cref="ProjectFileId"/> / <see cref="StepResultId"/> are the source's resolvable identity
/// (appended, omitted when null so every manifest written before them stays byte-identical). They
/// matter when <see cref="StorageKey"/> is not the original object — a trimmed window or a still's
/// rendered clip is an analysis scratch key no row owns — so a reader (the EditTimeline v2 seeder)
/// can still map the source to the asset it came from. At most one is set.
/// <see cref="TrimInSec"/> is the start of the author-trimmed window in the ORIGINAL asset: every
/// item's <c>SourceStartSec</c> is relative to that window, so a reader mapping the source to the
/// original asset adds it back. Null (omitted) for an untrimmed source.
/// </remarks>
public sealed record TimelineSource(
    int Index,
    string StorageKey,
    double DurationSec,
    int Width,
    int Height,
    double Fps,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? ProjectFileId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? StepResultId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? TrimInSec = null);

/// <summary>
/// A single item on a timeline track. Supports Cuts, Screen Inserts, Overlays,
/// Dialogue segments, SFX cues, and Music beds under Unified Program Mode (Expanded Studio Tracks).
/// </summary>
public sealed record TimelineItem(
    // Common / Identifiers
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Id = null,

    double TimelineStartSec = 0,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? TimelineEndSec = null,

    // Cuts
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ShotId = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? SourceIndex = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? SourceStartSec = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? SourceEndSec = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Label = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? AgentRationale = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? LookGroup = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Color = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ColorGrade = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TransitionOut = null,

    // Screen Inserts
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RegionId = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? AssetStorageKey = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Surface = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? Confidence = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SolvedColorKey = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<VideoInsertQuadKeyframe>? QuadKeyframes = null,

    // Overlays & Dialogue
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PlacementId = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Text = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Subtext = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Style = null,

    // Dialogue
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? RmsDbfs = null,

    // SFX
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SfxId = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ClipName = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? DurationSec = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? GainDb = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Timing = null,

    // Music
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TrackId = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TrackName = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? BedDbfs = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? DuckedDbfs = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<TimelineDuckingPoint>? DuckingEnvelope = null,

    // AI provenance (real, never guessed — see TimelineInspector.tsx defect notes in issue #123 §1)
    /// <summary>
    /// Name of the AI agent that produced this item. Null together with ProviderName and StepResultId
    /// for deterministic/non-agent-authored items — correct, not a bug.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? AgentName = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ProviderName = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? StepResultId = null,

    // Screen inserts — what the compile step actually APPLIED, for drawing over the program video.
    // QuadKeyframes above stay in the analyzed SOURCE's time and frame (the EditTimeline v2 seeder
    // reads them that way); these are in OUTPUT timeline seconds and 0..1 of the OUTPUT canvas,
    // after the cut and the source-to-canvas fit (before overscan: the plate as tracked, not the
    // expanded warp target), so a monitor can draw them directly.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<VideoInsertQuadKeyframe>? OutputQuadKeyframes = null,

    /// <summary>Whether the insert was clipped to the plate's keyed silhouette (rounded corners, notch, occluders) rather than the plain quad.</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Matted = null,

    /// <summary>How the tracker tuned itself for this footage — see <see cref="VideoInsertTrackTuning"/>.</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    VideoInsertTrackTuning? Tuning = null);

/// <summary>
/// A timeline track containing items of a specific type (video, inserts, graphics, dialogue, sfx, music)
/// in Unified Program Mode (Expanded Studio Tracks).
/// </summary>
public sealed record TimelineTrack(
    string Id,
    string Type,
    string Name,
    int Index,
    IReadOnlyList<TimelineItem> Items);

/// <summary>
/// Complete strongly-typed timeline manifest for a compiled video, exposing all studio tracks
/// (cuts, inserts, graphics, dialogue, sfx, music) in Unified Program Mode / Expanded Studio Tracks.
/// </summary>
public sealed record TimelineManifest(
    int Version,
    Guid ProjectId,
    Guid ExecutionId,
    TimelineOutputVideo OutputVideo,
    IReadOnlyList<TimelineSource> Sources,
    IReadOnlyList<TimelineTrack> Tracks);
