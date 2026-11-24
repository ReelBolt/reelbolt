using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The deterministic half of a <c>StepType.VideoAnalyze</c> step — everything that can run on a
/// machine that has the media bytes and ffmpeg, and nothing that needs an inference provider
/// (plan D10a, "Split VideoAnalyze into extraction and inference (in place)").
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> Before D10a, <c>VideoAnalyzeStepExecutor</c> interleaved two very
/// different kinds of work in one pass: ffmpeg/ffprobe decoding and the pure-C# analysis derived
/// from it (probe, silence, shots, WAV, frame grids, sharpness, chroma tracking, placements, look
/// grouping, keyframes), and calls that leave the host (ASR, vision captioning, object location,
/// the decision pre-pass, input screening). A later compute target (D11) runs only the first half
/// on a runner, so the first half has to be one callable unit with one result value. That result
/// value is this record.
/// </para>
/// <para>
/// <b>The boundary is real; where each side runs is the compute decision's to make.</b> Extraction
/// and inference still run back to back inside the same step attempt, but extraction now addresses
/// every file it names through <see cref="IMediaWorkspace"/> by a session-relative virtual path and
/// reaches ffmpeg only through it, so the workspace a step is handed decides the machine: a
/// <c>Local</c> one runs it here, a runner-backed one (D11) runs it on the machine that holds the
/// footage. That is what this record is for — it, plus the small media inference reads back through
/// the session's own <c>read</c> op, is everything that crosses the boundary.
/// </para>
/// <para>
/// <b>What extraction deliberately does not contain.</b> ASR, vision captioning, subject location,
/// the derush pre-pass and input screening are all inference-side and are absent here by
/// construction. The one provider call that does stay inside extraction is the still-image caption
/// (<c>CaptionStillAsync</c>): for an image source its text becomes the authored scene manifest
/// that shot detection itself consumes, so it cannot be moved behind the boundary without
/// restructuring shot detection. It runs through the media workspace like every other stage here,
/// so it is no longer a reason to keep the step on this host — the ONE such reason left is object
/// tracking, whose <c>IObjectTrackingService</c> samples the whole source off this disk and is
/// therefore <c>RunnerEligible: false</c> in the executor.
/// </para>
/// </remarks>
public sealed record VideoAnalysisExtract(
    /// <summary>Per-source extraction facts, in analysis (= source-index) order.</summary>
    IReadOnlyList<VideoAnalysisExtractSource> Sources,
    /// <summary>The merged, globally-id'd deterministic analysis every source contributed to.</summary>
    VideoAnalysisExtractDeterministic Deterministic,
    /// <summary>Project-level <c>audio/*</c> candidates — populated only when the config asks for them.</summary>
    IReadOnlyList<VideoAnalysisMusicCandidate> MusicCandidates,
    /// <summary>Sound-effect candidates — populated only when the config asks for them.</summary>
    IReadOnlyList<VideoAnalysisSfxCandidate> SfxCandidates,
    /// <summary>Beat facts for the music the edit may be cut to, or null when beat analysis is off/degraded.</summary>
    VideoAnalysisMusicTrack? Music,
    /// <summary>
    /// One entry per shot selected for captioning, in selection order — the keyframes inference
    /// sends to the vision provider. Empty when Vision is off or no shot was selected.
    /// </summary>
    IReadOnlyList<VideoAnalysisExtractKeyframe> Keyframes,
    /// <summary>
    /// Keyframe extractions that failed, formatted <c>"ExceptionType: message"</c> — the same text
    /// the captioning loop used to build from the exception it caught. One entry means one shot
    /// inference will never caption, which is exactly what its failure counters need to know.
    /// </summary>
    IReadOnlyList<string> KeyframeFailures,
    IReadOnlyList<string>? BeatGridFailures = null);

/// <summary>
/// Extraction's return value: either a <see cref="VideoAnalysisExtract"/> or the failure code and
/// message the step must report. Mirrors the per-source outcome shape it replaced, so a source that
/// fails (input too large, duration exceeded, trim refused, still image unusable) still fails the
/// step with exactly the same code and message as before the split.
/// </summary>
public sealed record VideoAnalysisExtractOutcome(VideoAnalysisExtract? Extract, string? ErrorCode, string? ErrorMessage)
{
    public static VideoAnalysisExtractOutcome Ok(VideoAnalysisExtract extract) => new(extract, null, null);

    public static VideoAnalysisExtractOutcome Fail(string code, string message) => new(null, code, message);
}

/// <summary>
/// Where one source's analysed media lives, named both ways: the session-relative
/// <see cref="VirtualPath"/> every <see cref="IMediaWorkspace"/> op takes, and the
/// <see cref="LocalPath"/> the caller materialized that virtual path at. A resume has to state both
/// because it re-stages the bytes itself (see <see cref="VideoAnalysisExtractArtifact.ToExtract"/>).
/// </summary>
/// <param name="VirtualPath">The session-relative name inside the media workspace.</param>
/// <param name="LocalPath">
/// <see cref="IMediaWorkspace.MaterializeAsync"/>'s answer for <see cref="VirtualPath"/> — an
/// absolute path for a local workspace, the virtual name itself for a remote one.
/// </param>
public readonly record struct VideoAnalysisExtractMedia(string VirtualPath, string LocalPath, string? PassSourceVersion = null);

/// <summary>
/// What inference still needs to know about ONE extracted source. Everything here is either a
/// scalar fact or a reference into the extract's merged analysis; the bytes themselves live in the
/// media workspace, named by a virtual path.
/// </summary>
/// <param name="StorageKey">
/// The storage key of the media actually analyzed — the trimmed window for an author-trimmed
/// source, the rendered clip for a still image, otherwise the resolved source key.
/// </param>
/// <param name="VideoVirtualPath">
/// The session-RELATIVE name of that media inside <see cref="IMediaWorkspace"/> — the name every
/// workspace operation takes. This is the one that travels: a remote session's bytes stay on the
/// runner, and only a name crosses back.
/// </param>
/// <param name="LocalVideoPath">
/// The same media as <see cref="VideoVirtualPath"/> resolved through
/// <see cref="IMediaWorkspace.MaterializeAsync"/> — a real absolute path for a local workspace, and
/// the virtual name unchanged for a remote one (which is the interface's own rule). It is the
/// string a v1 local-only consumer still needs: object tracking, which reads frames straight off
/// this host and therefore forces a Local workspace (see the class remarks). A ported consumer must
/// use <see cref="VideoVirtualPath"/> and the workspace instead.
/// </param>
/// <param name="AudioVirtualPath">
/// The session-relative name of the extracted full-track WAV, or null when this source has no
/// audio stream or transcription/audio-level analysis did not need one.
/// </param>
/// <param name="AudioStorageKey">
/// Where that WAV was published inside the project scope, or null when nothing was published.
/// D11's remote workspace stages this key instead of reading <see cref="AudioVirtualPath"/>.
/// </param>
/// <param name="MeasuredLevels">
/// The 250 ms RMS windows <c>WavRmsSampler</c> measured for this source, present only when
/// <c>AnalyzeAudioLevels</c> ran — the speech gate needs the same numbers extraction used.
/// </param>
/// <param name="AudioBytes">
/// The extracted WAV's length in bytes, measured through the media workspace when it was written —
/// what ASR chunk planning sizes its chunks from. Zero when no WAV was extracted.
/// </param>
/// <param name="Provenance">
/// This source's OWN provenance, kept per source because the artifact's aggregate is an OR/sum
/// across them, not a projection of the last one.
/// </param>
public sealed record VideoAnalysisExtractSource(
    int SourceIndex,
    string StorageKey,
    /// <summary>
    /// The object store key this source's <see cref="VideoSourceRef"/> resolved to BEFORE any
    /// still-image render or author trim replaced <see cref="StorageKey"/> with the media actually
    /// analysed. Carried because it is the one source fact that is re-resolvable from the context:
    /// D10b's persisted extract (see <see cref="VideoAnalysisExtractArtifact"/>) stores it so a
    /// later attempt can refuse an extraction whose upstream source has since changed, instead of
    /// replaying an analysis of bytes that are no longer the step's input.
    /// </summary>
    string ResolvedStorageKey,
    string VideoVirtualPath,
    string LocalVideoPath,
    MediaProbeResult Probe,
    VideoAnalysisMedia Media,
    IReadOnlyList<(double StartSec, double EndSec)> SilenceSpans,
    IReadOnlyList<WavRmsSampler.RmsWindow>? MeasuredLevels,
    bool HasAudioStream,
    string? AudioVirtualPath,
    string? AudioStorageKey,
    long AudioBytes,
    /// <summary>
    /// Why no WAV exists for a source that ffprobe says HAS an audio stream, or null — the reason
    /// the inference phase reproduces the old in-line extraction failure (Required fails,
    /// Optional degrades to an empty transcript).
    /// </summary>
    string? AudioFailureMessage,
    /// <summary>
    /// This source's items in their LOCAL id space (every kind restarting at 0), exactly as
    /// `ExtractSourceAsync` assigned them. These are the inputs the merge re-offsets into
    /// <see cref="VideoAnalysisExtractDeterministic"/>'s global ids, and the reason a `s0` in here
    /// and a `s0` there can legitimately mean different shots.
    /// </summary>
    IReadOnlyList<VideoAnalysisShot> Shots,
    IReadOnlyList<VideoAnalysisSilenceSpan> Silences,
    IReadOnlyList<VideoAnalysisPlacement> Placements,
    IReadOnlyList<VideoAnalysisDuplicateGroup> DuplicateGroups,
    IReadOnlyList<VideoInsertRegionTrack> InsertRegions,
    VideoAnalysisProvenance Provenance,
    bool IsComposition,
    /// <summary>
    /// Where this source's scenes came from and what did not line up — the authored-scene design's
    /// provenance, carried across the extraction/inference boundary so the inference phase reports it
    /// in the envelope instead of re-deriving it. Null when the source has no authored scenes at all
    /// (any user upload, any edited output), which is most sources.
    /// </summary>
    VideoAnalysisSceneSummary? Scenes,
    bool IsStill,
    double? TrimInSec,
    double? TrimOutSec,
    /// <summary>
    /// #93 phase 4 (dailies): the generated clip id (<c>v{n}</c>/<c>v{n}t{k}</c>) this source stands
    /// for when it came from a <see cref="VideoSourceKind.GeneratedClips"/> expansion, else null.
    /// Appended and defaulted so every persisted extract written before it still deserializes.
    /// </summary>
    string? ClipId = null,
    /// <summary>
    /// Every shot a <c>VideoSubShotMode.Content</c> split cut into parts, in source order, with how
    /// its cuts were chosen; null when the mode is not Content. Appended and defaulted so every
    /// persisted extract written before it still deserializes.
    /// </summary>
    IReadOnlyList<VideoAnalysisSubShotSplit>? SubShots = null,
    // log-footage-color T-013: the camera profile the analysis copy was converted with ("explicit"/"auto"), or null;
    // and whether a requested profile was ignored because the source is HDR. Not persisted with the extract artifact.
    string? InputProfile = null,
    string? InputProfileSource = null,
    bool InputProfileIgnoredHdr = false,
    // A resolved camera profile that was NOT applied, and the plain reason (warning code camera_profile_not_applied).
    string? InputProfileNotAppliedReason = null,
    /// <summary>
    /// Set when this source's pixel passes read the small analysis copy (<see cref="AnalysisProxy"/>) rather than
    /// the source: decided by policy during extraction, and null again when the copy could not be made. The event
    /// pass reads the same pixels by this decision, and makes the copy itself when it is not in the session.
    /// Appended and defaulted so every persisted extract written before it still deserializes (null = the source).
    /// </summary>
    VideoAnalysisCopyPlan? AnalysisCopy = null,
    /// <summary>
    /// The version of the stored object whose bytes are staged in this session, read before and after the staging
    /// and set only when both reads agree. It is the pass cache's identity of this source for the event pass.
    /// Null when the cache is off, the store gave no version, the object changed meanwhile, or the media is a
    /// still's clip or a trimmed window. Never persisted: a resume stages the bytes again and reads it again.
    /// </summary>
    string? PassSourceVersion = null);

/// <summary>The analysis copy a source's pixel passes read: the camera profile it is converted with, or null for none.</summary>
public sealed record VideoAnalysisCopyPlan(string? InputProfileId = null);

/// <summary>
/// One shot a content split cut into parts: its span before the split, how many parts it became,
/// the method (<c>"content"</c> or <c>"even_fallback"</c>) and one reason word per cut, in order
/// (<c>"motion_pause"</c>, <c>"visual_change"</c>, <c>"audio_dip"</c>, <c>"even"</c>).
/// </summary>
public sealed record VideoAnalysisSubShotSplit(double StartSec, double EndSec, int Parts, string Method, IReadOnlyList<string> Cuts);

/// <summary>
/// One source's authored scenes, as extraction resolved them: the split's provenance, how many
/// scenes it holds, how many of them carry a description, and every finding the reconciliation
/// produced.
/// </summary>
/// <remarks>
/// Produced exactly once, in the extraction phase, from the manifest this source carries. It is a
/// SUMMARY and not the manifest itself: the manifest is a real object in the media workspace's own
/// object store (the sidecar beside the video), and copying it here would create a second copy that
/// a resume could disagree with.
/// </remarks>
/// <param name="Origin">
/// One of <see cref="CompositionSceneOrigin"/> — <c>author</c> (a declaration, so the split is a
/// claim), <c>structure</c> (read from the composition's own sequences), <c>reconciled</c> (code for
/// the split, declaration for the meaning), <c>still</c>, <c>generated</c>, or <c>none</c> for a
/// source that is a designed composition whose scenes are NOT known.
/// </param>
/// <param name="Findings">
/// Every disagreement and staleness finding, in plain sentences. Empty is a normal, good answer.
/// </param>
public sealed record VideoAnalysisSceneSummary(
    string Origin,
    int SceneCount,
    int DescribedSceneCount,
    IReadOnlyList<string> Findings);

/// <summary>
/// Extraction's per-source outcome, mirroring the pre-split <c>SourceAnalysisOutcome</c>: either a
/// source's extraction facts or the failure that must fail the step (with the same code, and the
/// same message the caller prefixes with the source index in a multi-source step).
/// </summary>
public sealed record VideoAnalysisExtractSourceOutcome(
    VideoAnalysisExtractSource? Source, string? ErrorCode, string? ErrorMessage)
{
    public static VideoAnalysisExtractSourceOutcome Ok(VideoAnalysisExtractSource source) => new(source, null, null);

    public static VideoAnalysisExtractSourceOutcome Fail(string code, string message) => new(null, code, message);
}

/// <summary>
/// The merged deterministic analysis: every source's local ids re-offset into one global id space,
/// exactly as the artifact persists them. This is the part of the artifact that is fully decided
/// before any provider is called.
/// </summary>
/// <param name="InsertRegions">
/// <b>Chroma-plate tracks only.</b> Markerless object tracks are a vision result and are appended
/// by the inference phase, at this source's own block boundary, continuing the same <c>r{n}</c>
/// numbering — so the final list is byte-identical to the pre-split one.
/// </param>
/// <param name="SharpnessShotsMeasured">
/// The step-wide sharpness budget's measured count, carried across the boundary because the
/// artifact's provenance reports it.
/// </param>
public sealed record VideoAnalysisExtractDeterministic(
    IReadOnlyList<VideoAnalysisShot> Shots,
    IReadOnlyList<VideoAnalysisSilenceSpan> Silences,
    IReadOnlyList<VideoAnalysisPlacement> Placements,
    IReadOnlyList<VideoAnalysisDuplicateGroup> DuplicateGroups,
    IReadOnlyList<VideoInsertRegionTrack> InsertRegions,
    IReadOnlyList<VideoAnalysisSourceInfo> SourceInfos,
    VideoAnalysisProvenance Provenance,
    VideoAnalysisPacing Pacing,
    IReadOnlyList<VideoAnalysisLookGroup> LookGroups,
    bool LookGroupingApplied,
    bool LookUniform,
    int SharpnessShotsMeasured,
    double TotalDurationSec);

/// <summary>
/// One keyframe (or contact sheet) extraction produced for a shot the deterministic
/// <c>KeyframeSelector</c> picked out, addressed inside the media workspace.
/// </summary>
public sealed record VideoAnalysisExtractKeyframe(string ShotId, string VirtualPath, bool IsContactSheet);
