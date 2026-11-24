using System.ComponentModel;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// Controls whether/how a <c>VideoAnalyze</c> step attempts ASR transcription.
/// <c>Off</c> is deterministic and has no external dependency; <c>Optional</c> (the default)
/// degrades cleanly to <c>Off</c> when no transcription-capable provider resolves or the call
/// fails after bounded retry; <c>Required</c> fails the step with a precise diagnostic instead
/// of silently shipping an edit decision with no transcript context.
/// </summary>
public enum VideoTranscriptionMode
{
    Off,
    Optional,
    Required
}

/// <summary>
/// How much per-shot visual/audio detail (Phase 1 descriptors) the bounded prompt view includes
/// per offered shot. <c>VideoAnalyzeStepExecutor.BuildBoundedView</c> degrades through these
/// levels (Full → Compact → None) BEFORE ever dropping an offered item to fit
/// <see cref="VideoAnalyzeStepConfig.MaxOutputChars"/> — richer per-shot data must never silently
/// reduce how many shots/silences/segments the story-editor agent gets to see.
/// </summary>
public enum VideoVisualDetail
{
    /// <summary>No <c>v</c>/<c>a</c> key on any shot — identical to today's (pre-Phase-1) view shape.</summary>
    None,

    /// <summary>A small, LLM-budget-friendly summary per shot (motion class, camera move, cut-in/out, dup/best, bright, colors, rms/speech).</summary>
    Compact,

    /// <summary>Everything <c>Compact</c> has, plus the full region list, full still-window list, and motion std-dev/peak.</summary>
    Full
}

/// <summary>
/// Controls whether/how a <c>VideoAnalyze</c> step attempts Phase 2 vision-LLM shot captioning
/// (see docs/video-editing.md "Vision captioning"). Structurally mirrors
/// <see cref="VideoTranscriptionMode"/> (<c>Off</c>/<c>Optional</c>/<c>Required</c> with the same
/// degrade-vs-fail semantics), with one deliberate deviation: the default here is <c>Off</c>, not
/// <c>Optional</c>.
/// </summary>
/// <remarks>
/// <c>Transcription</c> defaults to <c>Optional</c> because it costs at most one ASR network call
/// per step. Captioning is structurally different: it can cost up to
/// <see cref="VideoAnalyzeStepConfig.MaxCaptionedShots"/> separate vision chat-completion calls
/// (each carrying an image) per <c>VideoAnalyze</c> step. If this defaulted to <c>Optional</c>,
/// then the moment any admin configured a <c>Vision</c>-capability default provider — for some
/// unrelated workflow that actually wants captioning — every OTHER existing or future
/// <c>VideoAnalyze</c> step in the system would silently start making real, billed vision calls
/// with no config change of its own. Defaulting to <c>Off</c> keeps every step's cost/latency
/// unchanged unless its author explicitly opts in by setting <see cref="VideoAnalyzeStepConfig.Vision"/>.
/// </remarks>
public enum VideoVisionMode
{
    Off,
    Optional,
    Required
}

/// <summary>
/// Which shots <c>KeyframeSelector.SelectShotsToCaption</c> picks for Phase 2 vision captioning,
/// up to <see cref="VideoAnalyzeStepConfig.MaxCaptionedShots"/>. See
/// docs/video-editing.md "Vision captioning" for the exact selection algorithm of each strategy.
/// </summary>
public enum VideoCaptionSelection
{
    /// <summary>
    /// Default. Captions each near-duplicate group's best-take shot first (so N takes of one
    /// setup cost one call, not N), then fills any remaining budget with the longest
    /// not-yet-selected shots.
    /// </summary>
    PerDuplicateGroup,

    /// <summary>Simply the N longest eligible shots.</summary>
    LongestShots,

    /// <summary>Shots at roughly even index/time intervals across the whole shot list.</summary>
    EvenlySpaced
}

/// <summary>
/// Controls whether a <c>VideoAnalyze</c> step runs an opt-in derush PRE-PASS: one batched <c>Noul</c>
/// decision question (<c>keep &lt;id&gt;</c>) per offered <c>s{n}</c>/<c>t{n}</c> id, stamping the
/// model's probability of keeping that item onto its own view row as an additive <c>pKeep</c> key,
/// and recording the per-id values in <c>meta.derushPrePass</c>. The pre-pass is advisory: it never
/// adds, removes or reorders an offered item.
/// </summary>
/// <remarks>
/// <para>
/// <b>One definition of confidence.</b> <see cref="VideoAnalyzeStepConfig.DerushAcceptAt"/> and
/// <see cref="VideoAnalyzeStepConfig.DerushMinMargin"/> mirror <c>DecisionGateConfig</c>
/// (<c>0.85</c> / <c>0.25</c>) rather than inventing a second thresholds record — the same
/// escalation rule (<c>p &lt; AcceptAt</c>, or a two-sided margin below <c>MinMargin</c>) is applied
/// here, and the two numbers are meant to stay identical to the gate's.
/// </para>
/// <para>
/// <b><c>Fast</c> is declared but NOT implemented.</b> In wave 1 it behaves <i>exactly</i> like
/// <see cref="Prior"/> — the pre-pass runs, <c>pKeep</c> is stamped, the view is otherwise
/// unchanged. It is deliberately <b>not</b> a skip: a future wave implements the "skip the editor
/// when the pre-pass is confident" behaviour that the name promises. Do not assume it does
/// something different today; the distinction is asserted by a test so it cannot be inherited
/// wrongly.
/// </para>
/// </remarks>
public enum VideoDerushPrePassMode
{
    /// <summary>
    /// Default. No decision call at all: the emitted <c>{view, meta}</c> envelope is
    /// byte-identical to the pre-pre-pass output.
    /// </summary>
    Off,

    /// <summary>
    /// Asks the batched per-item <c>keep</c> question before the view is emitted and stamps the
    /// answers onto the view as <c>pKeep</c>. Fails open on every path (no provider, a throw, a
    /// timeout, a malformed or partial answer) — each of those leaves the envelope byte-identical
    /// to <see cref="Off"/>.
    /// </summary>
    Prior,

    /// <summary>
    /// Declared for wave 2, not implemented in wave 1: behaves exactly like <see cref="Prior"/>
    /// (no editor skip, no early exit, no behaviour difference of any kind).
    /// </summary>
    Fast
}

/// <summary>
/// How a source's hard cuts are found (see <see cref="VideoAnalyzeStepConfig.SceneDetection"/>).
/// <b>APPEND ONLY</b> — persisted by name.
/// </summary>
public enum SceneDetectionMode
{
    /// <summary>Default. ffmpeg's scene score against <c>SceneThreshold</c>: today's behaviour, byte-identical.</summary>
    Standard,

    /// <summary>
    /// The scene score plus a luma-jump detector for dark footage, where the scene score is far below
    /// any threshold even across a hard cut (two dark shots differ by only a few levels). The jump is
    /// measured against the frames' own dynamic range, must happen within one frame (a fade is never a
    /// cut) and respects <c>MinShotMs</c>. Costs one extra decode pass.
    /// </summary>
    Sensitive,

    /// <summary>
    /// The Standard boundaries UNION soft transitions the scene score misses (dissolves, whips, zoom or
    /// warp changes): a 0.2 s lagged difference of tiny frame thumbnails, local maxima above 0.14, merged
    /// within 0.6 s. A soft boundary within 0.3 s of a hard one is dropped (the hard one wins) and
    /// <c>MinShotMs</c> is respected. Shots found this way carry <c>Method = "soft"</c>. Costs one extra
    /// decode pass.
    /// </summary>
    Soft
}

/// <summary>
/// How a long shot is split into sub-shots the editor can keep by id (docs/video-editing.md
/// "Keeping part of a long shot"). Appended values only: stored configs name these as strings.
/// </summary>
public enum VideoSubShotMode
{
    /// <summary>
    /// Default. No split, unless the older <see cref="VideoAnalyzeStepConfig.MaxShotSec"/> is set,
    /// which keeps working exactly as <see cref="Even"/>. Byte-identical for every existing workflow.
    /// </summary>
    Off,

    /// <summary>
    /// The fixed grid: every shot longer than <c>MaxShotSec</c> (or, when that is 0,
    /// <c>MaxSubShotSec</c>) becomes <c>ceil(length / max)</c> near-even parts, each split nudged to
    /// the stillest nearby moment.
    /// </summary>
    Even,

    /// <summary>
    /// Cut points from the content: pauses in the movement, changes of framing or composition, and
    /// pauses in the sound, measured by code (one extra low-res grid decode per source), with parts
    /// between <c>MinSubShotSec</c> and <c>MaxSubShotSec</c> and at most <c>MaxSubShotsPerShot</c>
    /// per shot. A shot whose signal is flat or missing is split evenly, and the envelope's
    /// <c>meta.subShots</c> says so.
    /// </summary>
    Content
}

/// <summary>
/// Cheap, in-process structural checks run against the produced analysis before the step is
/// considered successful. No model call. Mirrors <see cref="ExtractExpectation"/>'s role for
/// Extract steps.
/// </summary>
public sealed record VideoAnalyzeExpectation(
    int? MinShots = null,
    int? MinTranscriptSegments = null,
    double? MaxSilenceRatio = null,
    int? MinShotsWithVisuals = null);

/// <summary>
/// JSON configuration for a <c>StepType.VideoAnalyze</c> workflow step. Deserialized from
/// <c>WorkflowStep.VideoAnalyzeConfigJson</c>. A closed, versioned, strongly-typed record —
/// the same house style as <see cref="ExtractStepConfig"/>: deterministic, non-LLM, and never
/// throws (every failure mode is representable in the resulting <c>{view, meta}</c> envelope
/// rather than as an exception).
///
/// <para>
/// Phase 1 ("scene/visual analysis") appends the <c>Analyze*</c>/<c>Visual*</c>/<c>Duplicate*</c>
/// fields below the original field set (all with defaults) so existing persisted/template configs
/// keep deserializing unchanged; <see cref="Expect"/> stays the LAST positional parameter.
/// </para>
/// </summary>
[StepCard(
    "Reads video footage: splits it into shots and measures the picture; speech transcription and vision use a provider only when one is configured and turned on. Optionally finds the moments inside each shot (things appearing, moving, text, camera moves, beats) for sound and graphics to anchor to.",
    ["Video footage in the project, or a video from an earlier step", "Optional: a transcription or vision provider, used when configured"],
    "A footage analysis the cutting steps read",
    ReelBolt.Shared.Workflows.Apps.AppCostClass.NoCredits,
    ReadableBy = ["EditRoom", "GraphicsRoom", "ColorGradeRoom", "DailiesRoom", "VideoCompile", "Editor agents"],
    PairsWith = ["EditRoom: reads the analysis to decide the cut", "VideoCompile: resolves the cut against the analysis"])]
public sealed record VideoAnalyzeStepConfig(
    int Version,
    VideoSourceRef Source,
    // -- silence detection --
    bool DetectSilence = true,
    double SilenceThresholdDb = -34.0,
    int MinSilenceMs = 350,
    // -- shot/scene detection --
    bool DetectShots = true,
    double SceneThreshold = 0.30,
    // -- transcription --
    // Bindable so an app's captions switch can also ask for the transcript it burns (docs/design/app-inputs.md 27).
    [property: AppBindable(AppInputType.Choice, Label = "Transcribe the speech")]
    VideoTranscriptionMode Transcription = VideoTranscriptionMode.Optional,
    Guid? TranscriptionProviderId = null,
    string? Language = null,
    bool WordTimestamps = true,
    /// <summary>
    /// Upper bound on the WAV bytes sent to the ASR backend in a single request. Deliberately well
    /// below the ~25 MB OpenAI/whisper-server body cap (and below the previous 20 MB value): the
    /// bundled <c>whisper</c> service runs <c>faster-whisper-small</c> on CPU at ~5x realtime, so a
    /// 20 MB chunk (~10 min of 16 kHz mono s16) took ~120 s and hit the provider's HttpClient
    /// timeout. ~5 MB (~2.6 min) transcribes in ~30 s, leaving headroom for CPU contention and the
    /// one-time model reload after idle offload.
    /// </summary>
    int MaxAsrChunkBytes = 5_000_000,
    // -- guardrails, checked BEFORE any decode --
    // 0 = not set: the platform-wide VideoSourceLimits decide. A value lowers them, never raises them.
    int MaxDurationSeconds = 0,
    long MaxInputBytes = 0,
    // -- prompt-view budget (mirrors ExtractStepConfig.MaxOutputChars: drop whole trailing
    //    items and re-serialize, never truncate mid-JSON) --
    int MaxOutputChars = 24_000,
    int MaxViewSegments = 400,
    int MaxSegmentTextChars = 160,
    // -- Phase 1: visual scene analysis (one low-res raw-frame grid ffmpeg pass + pure C#) --
    bool AnalyzeVisuals = true,
    double VisualSampleFps = 2.0,
    int VisualGridWidth = 32,
    int VisualGridHeight = 18,
    /// <summary>
    /// Clamps the effective sample fps downward for very long videos:
    /// <c>effectiveFps = min(VisualSampleFps, MaxVisualSampleFrames / durationSec)</c>.
    /// </summary>
    int MaxVisualSampleFrames = 4000,
    double StillMotionThreshold = 0.02,
    int MinStillWindowMs = 400,
    int MaxStillWindowsPerShot = 3,
    /// <summary>
    /// Detects letterbox/pillarbox matte bars from the Phase 1 grid and reports the active
    /// (non-matte) crop as <c>VideoAnalysisShotVisual.ActiveCrop</c>. Free — reuses the luma frames
    /// the grid pass already materializes, no extra ffmpeg invocation. Defaults on since Phase 4
    /// implemented the algorithm (see docs/video-editing.md).
    /// </summary>
    bool DetectLetterbox = true,
    /// <summary>
    /// Measures native-resolution focus (<c>VideoAnalysisShotVisual.Sharpness</c>) via a dedicated
    /// per-shot ffmpeg invocation, capped by <see cref="MaxSharpnessShots"/>. Unlike the other
    /// Phase 4 dimensions this is NOT free — one extra ffmpeg process per measured shot — so it
    /// stays opt-in. See docs/video-editing.md.
    /// </summary>
    bool DetectSharpness = false,
    // -- Phase 1: audio loudness (reuses the WAV already extracted for transcription, or
    //    extracts it if transcription is off) --
    bool AnalyzeAudioLevels = true,
    // -- Phase 1: near-duplicate / best-take grouping --
    bool DetectNearDuplicates = true,
    double DuplicateSimilarityThreshold = 0.90,
    int DuplicateWindowShots = 20,
    // -- Phase 1: bounded-view detail level, and the degrade-before-drop budget it governs --
    VideoVisualDetail VisualDetail = VideoVisualDetail.Compact,
    int MaxViewDuplicateGroups = 20,
    // -- Phase 2: vision-LLM shot captioning (default OFF — see VideoVisionMode's doc comment
    //    for why this deviates from Transcription's Optional-by-default) --
    VideoVisionMode Vision = VideoVisionMode.Off,
    Guid? VisionProviderId = null,
    VideoCaptionSelection CaptionSelection = VideoCaptionSelection.PerDuplicateGroup,
    /// <summary>
    /// A CEILING, not a target — the step-level vision hoist (see docs/video-editing.md) made this
    /// a genuine step-wide cap across every source clip, not a per-source one. 50 is a generous
    /// out-of-the-box default; a workflow author wanting a cheap/fast run should set it lower, and
    /// every shot under <see cref="MinCaptionShotSeconds"/> is excluded before this cap applies
    /// anyway.
    /// </summary>
    int MaxCaptionedShots = 50,
    double MinCaptionShotSeconds = 1.0,
    int KeyframeMaxWidth = 512,
    int VisionTimeoutSeconds = 120,
    int MaxCaptionChars = 320,
    /// <summary>
    /// When <c>false</c> (default), keyframe JPEGs extracted for captioning are scratch-only and
    /// deleted with the rest of the step's scratch space when it completes — never uploaded
    /// anywhere. Uploading them for later inspection is explicitly out of scope for Phase 2.
    /// </summary>
    bool PersistKeyframes = false,
    // -- Phase 3: deterministic overlay-placement candidates (see docs/video-editing.md
    //    "Motion graphics (Phase 3)") — off by default, purely additive, never touches ffmpeg
    //    argv or a timestamp the model can see beyond the server-resolved window below --
    /// <summary>
    /// When <c>true</c>, derives overlay-placement candidates (<c>view.placements</c>) from
    /// each shot's Phase 1 region data, for a downstream motion-graphics planning agent.
    /// </summary>
    [property: AppBindable(AppInputType.Toggle, Label = "Room for titles and callouts")]
    bool EmitOverlayPlacements = false,
    int MaxPlacementsPerShot = 2,
    int MaxPlacements = 40,
    /// <summary>
    /// When a region's chosen time window is long enough to hold more than one distinct overlay
    /// moment, split it into up to this many non-overlapping, evenly-spaced sub-windows instead of
    /// offering the same window to every overlay that wants this region. Without this, every
    /// placement candidate for a single long continuous shot collapsed onto one identical
    /// StartSec/EndSec (the shot's longest still window reused verbatim for every region), so two
    /// agent-chosen overlays anchored to different placement ids could still land on the same
    /// moment and visually overlap — discovered live when a title and a callout overlay, on a
    /// single-shot 45s talking-head source, both rendered on top of each other near the same
    /// timestamp. Default 3.
    /// </summary>
    int MaxTimeSlicesPerRegion = 3,
    VideoAnalyzeExpectation? Expect = null,
    /// <summary>
    /// Multi-source addition. When non-empty, this is the AUTHORITATIVE list of source clips to
    /// analyze into ONE merged artifact (e.g. several takes/angles/B-roll of the same scene) —
    /// <see cref="Source"/> above is then ignored. Null/empty (the default) preserves today's
    /// exact single-source behavior unchanged: <see cref="VideoAnalyzeStepExecutor"/> falls back
    /// to treating <c>[Source]</c> as a one-element source list, so every existing
    /// persisted/template config (which only ever set <see cref="Source"/>) keeps deserializing
    /// and behaving identically. A one-element <see cref="Sources"/> list behaves identically to
    /// the equivalent single-<see cref="Source"/> config too — this field is a strict superset,
    /// never a divergent code path for the N=1 case. See docs/video-editing.md "Multiple source
    /// clips".
    /// </summary>
    IReadOnlyList<VideoSourceRef>? Sources = null,
    // -- Background music (see docs/video-editing.md "Background music"). Off by default, purely
    //    additive — a false/default OfferMusicTracks produces a byte-identical view/artifact to
    //    before this addition. Project-level (not per-source): every audio/* project file is a
    //    music candidate regardless of which source clip(s) this step analyzed. --
    /// <summary>
    /// When <c>true</c>, enumerates every <c>audio/*</c> project file as an <c>m{n}</c> music-track
    /// candidate (<c>view.musicTracks</c>) for a downstream <c>AgentType.MusicSupervisor</c> step.
    /// No ffprobe of the candidates — the fit/duration policy is resolved server-side at compile
    /// time regardless of the track's exact length, so probing every candidate here would only
    /// cost N downloads for a list the agent picks one item from.
    /// </summary>
    [property: AppBindable(AppInputType.Toggle, Label = "Offer the project music")]
    bool OfferMusicTracks = false,
    int MaxMusicTracks = 20,
    // -- Phase 4: semantic visual dimensions (see docs/video-editing.md "Semantic visual
    //    dimensions (Phase 4)"). D1-D4/D6 are FREE — derived from the grid data Phase 1 already
    //    sampled — so they default ON. --
    /// <summary>Gates D1-D3 (colour temperature, tone curve, saturation character). Free — three adds and one array increment inside a pixel loop that already runs. Mirrors <see cref="AnalyzeVisuals"/>.</summary>
    bool AnalyzeColorGrading = true,
    /// <summary>Gates D4 look grouping (<c>k{n}</c>). Free — O(shots²) over six floats, cheaper than the 576-float duplicate grouping already running. Mirrors <see cref="DetectNearDuplicates"/>.</summary>
    bool DetectLookGroups = true,
    /// <summary>Minimum look similarity (0..1) for two shots to share a look group. Lower than <see cref="DuplicateSimilarityThreshold"/> because look distance is a weighted six-vector, not a 576-float grid comparison.</summary>
    double LookSimilarityThreshold = 0.88,
    /// <summary>Caps <c>view.lookGroups</c>. Mirrors <see cref="MaxViewDuplicateGroups"/>.</summary>
    int MaxViewLookGroups = 12,
    /// <summary>
    /// Frames combined into ONE contact-sheet keyframe per captioned shot (clamped 1..3). Default 1 —
    /// a single mid-shot still, byte-identical to the pre-Phase-4 vision path. Raise it when caption
    /// quality on long shots matters more than the extra decode seeks it costs.
    /// </summary>
    int KeyframesPerShot = 1,
    /// <summary>
    /// Step-wide ceiling on sharpness measurements when <see cref="DetectSharpness"/> is on — genuinely
    /// step-wide, not per source. Unlike D1-D4/D6 this costs one extra ffmpeg invocation per measured
    /// shot, which is why <see cref="DetectSharpness"/> stays off by default.
    /// </summary>
    int MaxSharpnessShots = 24,
    // -- Tracked screen inserts (see docs/video-editing.md "Tracked screen inserts (Phase 5)").
    //    Off by default, purely additive: a false/default DetectInsertRegions produces a
    //    byte-identical view/artifact to before this addition. --
    /// <summary>
    /// When <c>true</c>, runs one extra medium-resolution grid ffmpeg pass per source clip and
    /// tracks a uniform-color chroma plate (e.g. a green screen inside a phone held in frame) as a
    /// deforming quadrilateral across frames (<c>ChromaQuadTracker</c> — deterministic C#, no LLM,
    /// no new dependency). Detected tracks are offered to the motion-graphics agent as opaque
    /// <c>r{n}</c> ids (<c>view.insertRegions</c>); the full per-frame corner data stays
    /// compile-time-only in the artifact.
    /// </summary>
    bool DetectInsertRegions = false,
    /// <summary>
    /// Which chroma plate color to track: "green" (default), "blue", or "magenta". Matched
    /// entirely in C# channel-ratio space (<c>ChromaQuadTracker</c>) — this value NEVER reaches an
    /// ffmpeg argv or filter string; an unrecognized value falls back to "green" with a warning.
    /// </summary>
    string InsertRegionColor = "green",
    /// <summary>
    /// Sample rate for the dedicated insert-tracking grid pass. <c>0</c> (the default) means AUTO:
    /// the source's own frame rate, or the smallest exact divisor of it that is at most 30 fps and
    /// fits <see cref="MaxInsertSampleFrames"/> and the tracker's memory budget. Exact divisors
    /// matter: a rate that does not divide the source's makes every sample the NEAREST source
    /// frame, a timing error that shows up as corner jitter (the old fixed 10 fps on a 24 fps
    /// clip did exactly that). A positive value is an explicit override, clamped 0.5..30.
    /// </summary>
    double InsertSampleFps = 0,
    /// <summary>
    /// Grid resolution of the insert-tracking pass. <c>0</c> (the default) means AUTO: the grid is
    /// derived from the source's own probed aspect ratio (roughly 320 px on the long axis, capped
    /// at 640x360 and floored at 64x36), so grid-space and source-space distances agree.
    ///
    /// <para>
    /// This used to be a fixed 320x180 regardless of the source, which on a portrait source
    /// squashed the grid vertically by up to 10.7x. That cost vertical corner precision (±10.7
    /// source px against ±3.4 horizontal — past <c>InsertOverscan</c>'s 0.02 default, leaving a
    /// visible green fringe, measured at 0.773% residual green versus 0.090% on an aspect-matched
    /// grid) AND corrupted every physical distance measured in grid space, most visibly the
    /// <c>aspect</c> value offered to the motion-graphics agent: a 1080x1920 phone screen whose
    /// true aspect is 0.47 was reported as 1.46, telling the agent a portrait screen was landscape.
    /// </para>
    ///
    /// <para>
    /// Setting BOTH to a positive pair is an explicit override and is honoured verbatim (clamped
    /// 64..640 / 36..360). The reported aspect stays correct either way — the tracker is given the
    /// source's real dimensions separately.
    /// </para>
    /// </summary>
    int InsertGridWidth = 0,
    int InsertGridHeight = 0,
    int MaxInsertSampleFrames = 3000,
    /// <summary>Minimum fraction of the frame's area a chroma component must cover to count as a plate at all.</summary>
    double MinInsertRegionAreaRatio = 0.004,
    /// <summary>Tracks shorter than this are dropped — a sub-second plate is not a useful compositing target.</summary>
    double MinInsertRegionSeconds = 1.0,
    /// <summary>Cap on offered insert-region tracks across the whole artifact (longest kept).</summary>
    int MaxInsertRegions = 8,
    /// <summary>
    /// How many chroma plates may be tracked SIMULTANEOUSLY within one frame — the multi-plate
    /// ("multipoint") capability, see docs/video-editing.md "Non-planar surfaces and multi-plate
    /// compositing". <c>1</c> (the default) is the original behaviour exactly: only the largest
    /// chroma component in each frame is a plate, so two devices held up together resolve to one
    /// flickering track rather than two. Raise it (clamped 1..16) and every component clearing the
    /// area/fill gates becomes its own candidate, associated across frames into independent
    /// concurrent <c>r{n}</c> tracks — which the compile step already composites independently,
    /// one rendered asset per region. <see cref="MaxInsertRegions"/> still caps the artifact total.
    /// </summary>
    int MaxInsertPlatesPerFrame = 1,
    /// <summary>
    /// Measure each tracked plate's silhouette (<c>ChromaPlateEdgeProfiler</c>): refined corners
    /// plus edge curvature. Curvature lets a curved or bent plate be composited with a mesh warp
    /// rather than one flat quad; the refined corners improve the FLAT path too, because the
    /// tracker's extreme-point fit lands on a rounded corner's tangent point rather than at the
    /// corner itself — measured on the project's real phone clip, that alone halves the plate
    /// still showing through the composite, 0.55% to 0.22%, at the cost of a little more content
    /// painted onto the bezel. Default <c>true</c>: it costs only some extra boundary probing on
    /// a mask the tracker already built. Set <c>false</c> to skip the measurement entirely, which
    /// reproduces the pre-existing composite exactly.
    /// </summary>
    bool MeasureInsertCurvature = true,
    /// <summary>
    /// Grid-search each tracked plate's own <c>colorkey</c> parameters
    /// (<c>PlateChromaKeySolver</c>) so the compile step can clip the composited insert to the
    /// plate's ACTUAL per-frame silhouette — rounded bezel corners, a camera notch, a hand
    /// crossing the screen — instead of painting a filled quadrilateral over them. Default
    /// <c>true</c>: the search runs over a handful of already-sampled grid frames and is scored
    /// against the tracker's own segmentation, so it costs no extra decoding and needs no
    /// clip-specific configuration. A plate whose best candidate still disagrees with the tracker
    /// stores no key at all, and the compile step then composites exactly as it did before
    /// matting existed. See docs/video-editing.md "Silhouette matting".
    /// </summary>
    bool SolveInsertChromaKey = true,
    // -- Sound effects (see docs/video-editing.md "Sound effects"). Off by default, purely
    //    additive — a false/default OfferSfxClips produces a byte-identical view/artifact to
    //    before this addition. Project-level (not per-source), exactly like OfferMusicTracks:
    //    every audio/* project file is an SFX candidate regardless of which source clip(s) this
    //    step analyzed. Appended AFTER MaxInsertRegions so every existing positional-construction
    //    call site and JSON payload keeps compiling/deserializing unchanged. --
    /// <summary>
    /// When <c>true</c>, enumerates every <c>audio/*</c> project file as an <c>x{n}</c> SFX-clip
    /// candidate (<c>view.sfxClips</c>) for a downstream <c>AgentType.SoundDesigner</c> step. No
    /// ffprobe of the candidates (the same rationale as <see cref="OfferMusicTracks"/>: the cue's
    /// play window is resolved server-side at compile time regardless of the clip's exact length,
    /// so probing every candidate here would only cost N downloads for a list the agent picks a
    /// few items from). The candidate pool deliberately overlaps <see cref="OfferMusicTracks"/>'
    /// — both enumerate <c>audio/*</c> files, under independent id namespaces — since nothing
    /// structural distinguishes an uploaded stinger from an uploaded bed; the sound-designer
    /// agent's prompt tells it to choose short one-shot clips by file name.
    /// </summary>
    [property: AppBindable(AppInputType.Toggle, Label = "Offer the project sound effects")]
    bool OfferSfxClips = false,
    int MaxSfxClips = 40,
    // -- Derush pre-pass (see docs/decision-models.md and plan §1 D2/D3). Off by default, purely
    //    additive: an Off run produces a byte-identical {view, meta} envelope to before this
    //    addition. Appended AFTER MaxSfxClips so every existing positional-construction call site
    //    and JSON payload keeps compiling/deserializing unchanged --
    /// <summary>
    /// When not <see cref="VideoDerushPrePassMode.Off"/>, asks one batched <c>Noul</c> question
    /// (<c>keep &lt;id&gt;</c>) per offered <c>s{n}</c>/<c>t{n}</c> id — the state is that item's own
    /// view row plus its immediate neighbours — and stamps the answer onto the row as an additive
    /// <c>pKeep</c> probability. Never adds or removes an offered id; fails open (byte-identical to
    /// <see cref="VideoDerushPrePassMode.Off"/>) on every failure path, including a partial answer.
    /// See <see cref="VideoDerushPrePassMode"/> for the exact <c>Fast</c> contract.
    /// </summary>
    [property: Description(
        "Off (default) makes no decision call. Prior stamps a per-item pKeep from one batched " +
        "decision call. Fast is declared but NOT IMPLEMENTED: it currently behaves exactly like " +
        "Prior, it does not skip the editor, and it is reserved for a later phase.")]
    VideoDerushPrePassMode DerushPrePass = VideoDerushPrePassMode.Off,
    /// <summary>
    /// Confidence at or above which a <c>pKeep</c> counts as accepted. Mirrors
    /// <c>DecisionGateConfig.AcceptAt</c> (0.85) — deliberately a copy of that number rather than a
    /// reference, because <c>DecisionGateConfig</c> lives in the engine assembly and this record
    /// does not. There is one definition of confidence; if the gate's default ever moves, this
    /// one moves with it.
    /// </summary>
    double DerushAcceptAt = 0.85,
    /// <summary>
    /// Minimum two-sided margin (<c>|2·pKeep − 1|</c>, the <c>Noul</c> analogue of the gate's
    /// top-two probability gap) below which a <c>pKeep</c> counts as escalated. Mirrors
    /// <c>DecisionGateConfig.MinMargin</c> (0.25) for the same reason
    /// <see cref="DerushAcceptAt"/> mirrors <c>AcceptAt</c>.
    /// </summary>
    double DerushMinMargin = 0.25,
    // -- Input screening (plan §3 M1; see docs/video-editing.md "Motion graphics (Phase 3)" and
    //    docs/decision-models.md "Guardrails and structured rejection"). Off by default, purely
    //    additive: a false/default ScreenInstructionLikeSpans produces a byte-identical
    //    {view, meta} envelope to before this addition. Appended AFTER DerushMinMargin so every
    //    existing positional-construction call site and JSON payload keeps compiling and
    //    deserializing unchanged --
    /// <summary>
    /// When <c>true</c>, the media-derived text spans in the bounded view — the Phase 2 vision
    /// captions attached to shot rows (site <c>c:</c>) and the ASR transcript span rows (site
    /// <c>t:</c>), i.e. the text that came out of the source video rather than out of a
    /// user-selected project file — are put through <c>IInputSpanScreener</c> and the
    /// instruction-like ones are <b>quoted and flagged</b> in an additive <c>view.screening</c>
    /// object. Screening <b>never drops</b> a span: on-screen text and transcribed speech are
    /// routinely legitimate content, so no id is added to, removed from or reordered within
    /// <c>view.shots</c>/<c>view.segments</c>.
    ///
    /// <para>
    /// <b>Fail open, exactly like every other optional stage in this executor.</b> No screener
    /// wired, no <c>Decision</c> provider configured, a throw, a timeout, a malformed or partial
    /// result, or simply nothing flagged all leave the emitted envelope <b>byte-identical</b> to
    /// <c>ScreenInstructionLikeSpans = false</c>: the annotation is added only when the screening
    /// actually flagged at least one span. A guardrail that turns a provider outage into a workflow
    /// failure is a worse bug than the injection it guards.
    /// </para>
    /// </summary>
    bool ScreenInstructionLikeSpans = false,
    // -- Markerless object tracking (see docs/video-editing.md "Tracking objects"). Null/empty
    //    (default) runs nothing and leaves the envelope byte-identical. Appended last so every
    //    existing positional-construction call site and stored JSON payload is untouched. --
    /// <summary>
    /// Objects to find and follow through each source — a licence plate to censor, a sign to
    /// replace, a screen with no green plate. Each target is either FOUND (a <c>Label</c> alone:
    /// the vision provider looks for it on frames sampled every <see cref="ObjectDetectEverySec"/>)
    /// or PLACED (a <c>Region</c> at <c>AtSec</c>, e.g. drawn in the editor). Either way the
    /// feature tracker then follows it forward and backward from every seed, self-tuning its
    /// motion model and window per target. Results are added to the artifact's insert regions with
    /// <c>Method = "feature"</c> and the target's <c>Label</c>, so a compile step can censor them
    /// (<c>VideoCompileStepConfig.CensorLabels</c>) or composite into them like any insert region.
    /// </summary>
    IReadOnlyList<ObjectTrackTarget>? TrackObjects = null,
    /// <summary>Frame rate the object tracker samples at. Clamped 2..30.</summary>
    double ObjectTrackSampleFps = 15,
    /// <summary>Longest side, in pixels, of the frames the tracker works on. Clamped 320..1920 — higher finds smaller objects, at a quadratic cost.</summary>
    int ObjectTrackMaxSide = 960,
    /// <summary>How often a FOUND target is looked for again, so objects that enter later, or that a track lost, are picked up. Clamped 0.25..30 s.</summary>
    double ObjectDetectEverySec = 1.0,
    /// <summary>Cap on vision lookups per source (each is one or two vision calls). Clamped 1..60.</summary>
    int MaxObjectDetectFrames = 12,
    /// <summary>Cap on object tracks emitted per source. Generous by default: for censoring, a plate left untracked because the cap was reached is a privacy failure.</summary>
    int MaxObjectTracks = 32,
    /// <summary>The <c>Vision</c> provider used to find labelled targets; null resolves the default Vision provider.</summary>
    Guid? ObjectVisionProviderId = null,
    // -- Transient-shot absorption. Appended last so every existing positional-construction call
    //    site and JSON payload keeps compiling and deserializing unchanged --
    /// <summary>
    /// Shortest span, in milliseconds, kept as a shot of its own. A detected span shorter than
    /// this (a one-frame bloom flash, a white pop, a dissolve's middle frame) is folded into the
    /// shot after it, the shot it cuts into (see <c>ShotSpanConsolidator</c>). Default <c>200</c>:
    /// no editorial decision is ever made about a sixth of a second of picture, and a sliver left
    /// in the view was grouped as a near-duplicate "take" of the real scene behind it, which is how
    /// an editor came to drop whole scenes of a designed composition. <c>0</c> disables it.
    /// </summary>
    int MinShotMs = 200,
    /// <summary>
    /// Whether the source is a finished, DESIGNED composition — a render authored by an earlier
    /// step — rather than raw footage. When true the view carries <c>media.composition: true</c>,
    /// which tells the editors every shot is an intended scene: there is no dead air, false start
    /// or alternate take to remove, so they keep the whole piece unless the brief asks for a
    /// shorter cut. <c>null</c> (the default) decides automatically: true exactly when every
    /// source is the render of an earlier <c>Agent</c> step (the only kind of step that renders a
    /// composition); a project-file source is raw footage unless this is set.
    /// </summary>
    bool? SourceIsComposition = null,
    // -- Subject location (see docs/video-editing.md "Reframing to the subject"). Appended after
    //    SourceIsComposition: positional record, every stored payload keeps deserializing. --
    /// <summary>
    /// Locate each shot's main subject with the <c>Vision</c> provider (two calls per shot: the
    /// shot's keyframe, then a zoomed crop around what it found, which boxes it far more tightly)
    /// and store it as <c>VideoAnalysisShot.Subject</c>. A compile with
    /// <c>OutputFit = Subject</c> crops toward it when it reframes 16:9 footage to 9:16 (or any
    /// other aspect change); shots with a tracked screen use the tracking instead and need no
    /// call. Default <c>false</c>: nothing is sent and the view is byte-identical.
    /// </summary>
    bool LocateSubjects = false,
    /// <summary>
    /// What to look for, in plain words ("the phone", "the presenter's face"). Null (default)
    /// asks for the main subject a viewer would look at.
    /// </summary>
    string? SubjectLabel = null,
    /// <summary>Most shots located per step (two vision calls each); later shots keep no subject and are cropped centred. Default 40.</summary>
    int MaxSubjectShots = 40,
    // -- Sub-shots (see docs/video-editing.md "Keeping part of a long shot"). Appended after
    //    MaxSubjectShots: positional record, every stored payload keeps deserializing. --
    /// <summary>
    /// Split every detected shot longer than this many seconds into sub-shots of at most about this
    /// length, so the editor can keep part of a long shot by keeping only some of its sub-shot ids
    /// (it never names a time). Each split lands on the stillest moment near the even split point
    /// when visual analysis can measure motion, else on a pause in the sound, else exactly evenly.
    /// Sub-shots carry <c>part</c> ("2/3") in the view. <c>0</c> (default) splits nothing and leaves
    /// the view byte-identical; a positive value below 1 s is treated as 1 s.
    /// </summary>
    double MaxShotSec = 0,
    // -- Beat facts (see docs/video-editing.md "Cutting to the beat"). Appended after MaxShotSec:
    //    positional record, every stored payload keeps deserializing. Off (default) is byte-identical. --
    /// <summary>
    /// Measure the beat of the music the edit may be cut to — tempo, beat and bar length, the drop and
    /// the outro, all by code — and show them to the editor: on every offered music track
    /// (<see cref="OfferMusicTracks"/>, up to <see cref="MaxBeatAnalyzedTracks"/>) and on the track named
    /// by <see cref="MusicBeatTrackProjectFileId"/> (<c>view.music</c>). Each analysed track is
    /// downloaded and decoded once. Default <c>false</c>: nothing is decoded and the view is
    /// byte-identical.
    /// </summary>
    bool AnalyzeMusicBeats = false,
    /// <summary>The music track the edit is cut to — usually the same file as the compile step's <c>MusicTrackProjectFileId</c>. Null: only offered tracks are analysed.</summary>
    Guid? MusicBeatTrackProjectFileId = null,
    /// <summary>Most offered music tracks analysed for their beat (each costs one download and decode). Clamped 0..20. Default 6.</summary>
    int MaxBeatAnalyzedTracks = 6,
    // -- Content sub-shots (see docs/video-editing.md "Keeping part of a long shot"). Appended after
    //    MaxBeatAnalyzedTracks: positional record, every stored payload keeps deserializing. Off
    //    (default) with MaxShotSec 0 is byte-identical. --
    /// <summary>How long shots are split; see <see cref="VideoSubShotMode"/>. Default <c>Off</c>.</summary>
    VideoSubShotMode SubShotMode = VideoSubShotMode.Off,
    /// <summary>Shortest sub-shot a <c>Content</c> split leaves; a shot shorter than twice this is never split. Clamped 0.5..10 s. Default 1.2.</summary>
    double MinSubShotSec = 1.2,
    /// <summary>Longest sub-shot a <c>Content</c> split leaves (and the <c>Even</c> length when <c>MaxShotSec</c> is 0). Clamped to 2 x MinSubShotSec..60 s. Default 4.</summary>
    double MaxSubShotSec = 4.0,
    /// <summary>Most parts one shot is split into by <c>Content</c>; a shot too long for it gets longer parts. Clamped 2..64. Default 8.</summary>
    int MaxSubShotsPerShot = 8,
    // -- Scene detection sensitivity (docs/video-editing.md "Finding cuts in finished films"). Appended
    //    after MaxSubShotsPerShot: positional record, every stored payload keeps deserializing. Null
    //    (default) is Standard: byte-identical. --
    /// <summary>
    /// How hard cuts are found; see <see cref="SceneDetectionMode"/>. Null (default) is
    /// <see cref="SceneDetectionMode.Standard"/>: the scene score alone, byte-identical.
    /// </summary>
    [property: AppBindable(AppInputType.Choice, Label = "Scene detection")]
    SceneDetectionMode? SceneDetection = null,

    // -- Text-prompted video segmentation (initiative "prompted region effects", plan §4/§6).
    //    Explicitly opt-in TWICE: a target must say Method = Segmentation AND one of these knobs is
    //    only ever read on that path, so a step with no segmentation target produces a byte-identical
    //    envelope, artifact and step-cache key to before this addition. Appended after
    //    MaxBeatAnalyzedTracks — positional record, so every existing constructor call site and every
    //    stored payload keeps compiling/deserializing unchanged. --
    /// <summary>
    /// The <c>VideoSegmentation</c> provider that turns a <see cref="ObjectTrackTarget"/> with
    /// <see cref="ObjectTrackMethod.Segmentation"/> into per-frame masks. <c>null</c> (default) resolves
    /// the deployment's default <c>VideoSegmentation</c> row. A null resolution (or an id the
    /// organization cannot see, which the resolver treats as missing) fails the step closed with
    /// <c>SEGMENTATION_NO_PROVIDER</c> — never a silent fall back to the feature tracker, which would
    /// publish an effect in the wrong place.
    /// </summary>
    [property: Description("VideoSegmentation provider id for method=segmentation targets. Null resolves the default VideoSegmentation provider; no provider fails the step with SEGMENTATION_NO_PROVIDER.")]
    Guid? SegmentationProviderId = null,
    /// <summary>
    /// Explicit consent for the source clip's frames to leave this host: the segmentation model runs
    /// on a hosted vendor, so the source must be reachable by a short-TTL presigned GET
    /// (&lt; 15 min). <c>false</c> (the default) refuses the whole step with <c>EGRESS_REFUSED</c>,
    /// naming the exact clip — the same fail-closed discipline as
    /// <c>GeneratedShotEgressPolicy</c>. Only consulted when a target says
    /// <see cref="ObjectTrackMethod.Segmentation"/>; nothing else in this record uploads media.
    /// </summary>
    [property: Description("Consent for source frames to be uploaded for hosted segmentation (presigned GET, TTL < 15 min). false refuses the step with EGRESS_REFUSED and names the clip.")]
    bool AllowSourceMediaEgress = false,
    /// <summary>
    /// Longest stretch of one source clip that may be segmented, in seconds — a cost guard, not a
    /// quality knob: the model bills per 16 frames of input. Clamped 1..600
    /// (<see cref="SegmentationLimits.MinSeconds"/>..<see cref="SegmentationLimits.MaxSeconds"/>); a
    /// longer source fails the step with <c>SEGMENTATION_SOURCE_TOO_LONG</c>, whose message names both
    /// the clip's duration and this cap. Never silently truncates the clip.
    /// </summary>
    [property: Description("Longest stretch of one source that may be segmented, in seconds. Clamped 1..600; a longer source fails with SEGMENTATION_SOURCE_TOO_LONG (message names the duration and this cap).")]
    int MaxSegmentationSeconds = 60,
    /// <summary>
    /// The model's own detection confidence floor, sent verbatim as <c>detection_threshold</c>.
    /// Clamped 0.01..1 (<see cref="SegmentationLimits.MinDetectionThreshold"/>..1) — a value outside
    /// that range is clamped, not refused, because it is a quality dial rather than a correctness one.
    /// </summary>
    [property: Description("Model detection confidence floor, sent verbatim. Clamped 0.01..1.")]
    double SegmentationDetectionThreshold = 0.5,
    /// <summary>
    /// Most objects the model may return per frame, sent verbatim as <c>max_num_objects</c>. Clamped
    /// 1..16 (the vendor allows up to 128; the house cap is deliberately far lower because one entry
    /// per object is one mask per frame the compile step must download and conform). The prompt names
    /// the object; the extra slots only let the model separate several instances of it.
    /// </summary>
    [property: Description("Most objects the model may return per frame. Clamped 1..16.")]
    int MaxSegmentationObjects = 4,
    /// <summary>
    /// Wall-clock budget for one segmentation call, in seconds. Clamped 60..3600
    /// (<see cref="SegmentationLimits.MinProviderTimeoutSeconds"/>..<see cref="SegmentationLimits.MaxProviderTimeoutSeconds"/>);
    /// the provider's own HTTP client timeout is this value, never the default. A timeout fails the
    /// step with <c>SEGMENTATION_PROVIDER_ERROR</c>, refunded per the failure-class table.
    /// </summary>
    [property: Description("Wall-clock timeout for one segmentation call, in seconds. Clamped 60..3600.")]
    int SegmentationTimeoutSeconds = 900,
    // -- Pictures and videos over the film (initiative "picture-overlay", design §5.3). Appended
    //    last after SegmentationTimeoutSeconds: positional record, every stored payload keeps
    //    deserializing. Default off, and off produces a byte-identical artifact and view. --
    /// <summary>
    /// When <c>true</c>, the step snapshots the pictures/videos the run may show (<c>m{n}</c> media
    /// offers) and the moments to show them (<c>o{n}</c> overlay windows) into the artifact, and
    /// emits them as <c>view.overlayMedia</c>/<c>view.overlayWindows</c> for an <c>OverlayPlanner</c> step.
    /// Default <c>false</c>: nothing is listed, probed or emitted, and the artifact/view are
    /// byte-identical to before this field existed.
    /// </summary>
    bool EmitOverlayOffers = false,
    /// <summary>Cap on the offered overlay windows (<c>o{n}</c>) across every shot. Clamped 0..200; a non-positive value offers none.</summary>
    int MaxOverlayWindows = 24,
    /// <summary>Cap on the offered media (<c>m{n}</c>) across the whole project. Clamped 0..100; a non-positive value offers none.</summary>
    int MaxOverlayMedia = 20,
    /// <summary>
    /// Per-source camera log profile (log-footage-color): analysis source order (0-based) to a camera profile id or the
    /// word <c>auto</c> (apply only when the detector is High confidence). The analysis copy is converted to Rec.709 first,
    /// so scene, visual and colour facts are measured on corrected pixels. HDR sources ignore it. Appended last;
    /// byte-identical when null.
    /// </summary>
    IReadOnlyDictionary<int, string>? SourceInputProfiles = null,
    // -- Video events analysis (docs/video-editing.md "Events in the picture and the sound"). Appended last:
    //    positional record, every stored payload keeps deserializing. Off (default) is byte-identical. --
    /// <summary>
    /// Find the moments that happen inside each shot by code (things appearing, moving, hitting, text coming
    /// and going, camera moves, transitions, beats, speech edges) and offer them as <c>e{n}</c> anchors:
    /// <c>view.events</c> shows an id, a type, a strength word and a region word, never a time. Free, local,
    /// deterministic; no model is called. A source that cannot be read degrades with the warning
    /// <c>events_unavailable</c>. Default <c>false</c>: nothing is decoded and the artifact and view are byte-identical.
    /// </summary>
    bool AnalyzeEvents = false,
    /// <summary>Most events kept per source by the detector (the strongest win). Null = the engine default; clamped 10..2000.</summary>
    int? MaxEvents = null,
    // -- Progressive context (docs/video-editing.md "Outline and windows"). Appended last; off is byte-identical. --
    /// <summary>
    /// With <see cref="AnalyzeEvents"/> on: also build the outline of the events (3 to 12 sections <c>c{n}</c>, each with a
    /// one-line descriptor, counts by type and its three strongest events) into <c>view.outline</c>, with each section's
    /// events in <c>view.sectionEvents</c> inside a fixed token budget, so a planner reads the whole video without reading
    /// all of it. Free, local, deterministic; ids and words only. Default <c>false</c>: the view is byte-identical.
    /// </summary>
    bool Outline = false,
    /// <summary>
    /// With <c>OfferSfxClips</c>, offer the sound designer only events a sound belongs to (impacts, flashes, appearing,
    /// leaving, changes, transitions, moves, text in, clear camera moves), at most one per 2 s of a source on average and
    /// never closer than 0.8 s; beats, caption words and weak events stay in the artifact but are not offered.
    /// Default <c>true</c>; <c>false</c> restores the full offer.
    /// </summary>
    bool SfxWorthyEventsOnly = true,
    // -- Built-in sound library (docs/sfx-library.md). Appended last; false is byte-identical. --
    /// <summary>
    /// With <c>OfferSfxClips</c>, also offer the built-in sound-effects library: its enabled items come first in the
    /// <c>x{n}</c> list (at most 60, each with a category and tag words such as the event types it suits), the project's own
    /// audio files after (still capped by <c>MaxSfxClips</c>). Free of charge. Default <c>false</c>: nothing changes.
    /// </summary>
    bool IncludeSfxLibrary = false,
    // -- What the picture shows at an event (docs/video-editing.md "What an event shows"). Appended last; false is
    //    byte-identical and calls nothing. --
    /// <summary>
    /// With <see cref="AnalyzeEvents"/>: ask the <c>Vision</c> provider what the picture shows at the events the code
    /// could measure but not name (something appears, leaves, changes, moves, hits, text comes in), and at a hard cut
    /// the code read as a plain cut between two filmed shots, one small strip of three frames per event, and add the
    /// answer as <c>seen</c> (a few plain words, letters only) next to the event. Any other cut, a flash, a camera move
    /// and a caption word get no call.
    /// At most <see cref="MaxDescribedEvents"/> calls a step, the events the code could not place and those plain cuts
    /// first, inside the step's vision time (making the strips is not counted). The answer also says what kind of picture it is (graphic, text or footage). Never fails the step: with no Vision provider, or when a call fails, the event
    /// keeps its measured words and the step says so in a warning. Frames of the source leave this host for the
    /// Vision provider, exactly as with <see cref="Vision"/> captions. Default <c>false</c>.
    /// </summary>
    bool DescribeEvents = false,
    /// <summary>
    /// Most events described per step (one vision call each), 0..40. Null (the default) lets the length of the video
    /// decide: one look per six seconds, at least 12 and at most 30 (<c>VisionLookDefaults.EventLooks</c>). 0 switches
    /// the looks off: no call is made and no strip is kept for a later look, as with <see cref="DescribeEvents"/> off.
    /// </summary>
    [property: AppBindable(AppInputType.Number, Label = "Most looks at the picture", Min = 0, Max = VideoEvents.VisionLookDefaults.MaxEventLooks)]
    int? MaxDescribedEvents = null,
    /// <summary>
    /// With <c>OfferSfxClips</c> and <see cref="SfxWorthyEventsOnly"/>: also offer the sound designer the moments that are
    /// ordinary filmed footage (a person walking, a plain cut between two camera shots). Default <c>false</c>: those stay
    /// in the analysis but get no sound; a camera flash and an impact are always offered.
    /// </summary>
    [property: AppBindable(AppInputType.Toggle, Label = "Sounds on filmed footage too")]
    bool SfxOnFootage = false)
{
    /// <summary>The config's own camera-profile errors (empty when unset or valid).</summary>
    public List<string> ValidateSourceInputProfiles() =>
        SourceInputProfileRules.Validate(SourceInputProfiles, nameof(SourceInputProfiles));

    /// <summary>
    /// True when this step may reach a model or vendor, so its run counts against the PAID hourly bucket
    /// (<c>RunRateClassifier</c>). Fail closed: anything not provably free is true, including an enum value
    /// outside its declared members. One line per provider call in <c>VideoAnalyzeStepExecutor</c>:
    /// <list type="bullet">
    /// <item><see cref="Transcription"/> not Off: <c>ResolveTranscriptionAsync</c> / <c>ITranscriptionClient</c>.</item>
    /// <item><see cref="Vision"/> not Off: <c>ResolveVisionAsync</c> / <c>IShotCaptioner</c> (shot captions).</item>
    /// <item><see cref="LocateSubjects"/>: <c>ResolveVisionAsync</c> / <c>IObjectLocator</c>, even with Vision Off.</item>
    /// <item><see cref="DescribeEvents"/> together with <see cref="AnalyzeEvents"/> (the executor describes only inside
    /// the events block) and a <see cref="MaxDescribedEvents"/> that is not 0: <c>ResolveVisionAsync</c> /
    /// <c>IEventDescriber</c>, even with Vision Off.</item>
    /// <item><see cref="TrackObjects"/> with a target that has no drawn <c>Region</c>: the vision provider seeds it
    /// (<see cref="ObjectVisionProviderId"/>). A drawn region is tracked locally.</item>
    /// <item><see cref="DerushPrePass"/> not Off: <c>ResolveDecisionAsync</c> / <c>IDecisionClient</c>.</item>
    /// <item><see cref="ScreenInstructionLikeSpans"/>: <c>IInputSpanScreener</c> (a guardrail model).</item>
    /// </list>
    /// <b>Adding a field that calls a model or vendor? Add it here in the same change</b>, or a run that spends
    /// credits is counted as free (RunRateClassifierTests pins one case per field).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CallsAnyProvider =>
        Transcription != VideoTranscriptionMode.Off
        || Vision != VideoVisionMode.Off
        || LocateSubjects
        || (AnalyzeEvents && DescribeEvents && MaxDescribedEvents is not <= 0)
        || (TrackObjects is { } targets && targets.Any(t => t is null || t.Region is null))
        || DerushPrePass != VideoDerushPrePassMode.Off
        || ScreenInstructionLikeSpans
        // Prompted region effects: a segmentation target is a hosted, per-frame-billed vendor call.
        || (TrackObjects is { } segmentedTargets && segmentedTargets.Any(
            t => t is not null && t.Method == ObjectTrackMethod.Segmentation));
}

/// <summary>
/// How an <see cref="ObjectTrackTarget"/> is followed through a source. <b>APPEND ONLY</b> — persisted
/// by name (<c>"Feature"</c>/<c>"Segmentation"</c>), so a stored target that names no method
/// deserializes to <see cref="Feature"/> and behaves exactly as it did before this enum existed.
/// </summary>
public enum ObjectTrackMethod
{
    /// <summary>
    /// Default. The free, in-process planar feature tracker (<c>ObjectTrackingService</c> +
    /// <c>ChromaQuadTracker</c>), seeded by the <c>Vision</c> provider or by the target's own
    /// <see cref="ObjectTrackTarget.Region"/>. Rigid and planar-friendly; drifts on deforming,
    /// non-planar or heavily occluded objects. Costs no provider call of its own beyond the seeds.
    /// </summary>
    Feature,

    /// <summary>
    /// Opt-in. A hosted, text-prompted video segmentation model (fal SAM 3.1 video-rle first) returns
    /// one mask per frame per object, which is cached by content and stored as a full-frame grey8 mask
    /// video; the existing feature tracker is NOT used on this path. Requires
    /// <see cref="VideoAnalyzeStepConfig.AllowSourceMediaEgress"/> (the source clip leaves this host)
    /// and a resolvable <c>VideoSegmentation</c> provider. Masks land in the artifact's own
    /// <c>SegmentationTracks</c> (<c>g{n}</c>) collection, never in the insert regions — a mask is not
    /// a quad and must never match <c>VideoCompileStepConfig.CensorLabels</c>.
    /// </summary>
    Segmentation
}

/// <summary>
/// How many of the objects a segmentation prompt matches one target covers. Append-only, persisted by
/// name (D-23).
/// <para>
/// This is a DIFFERENT axis from the vendor renumbering an object it re-acquires: those ids are always
/// merged into one track, because that is a fix, not a choice (<c>SegmentationTrackingService.ChooseSlots</c>).
/// This enum is the product choice, and it is about DISTINCT instances — two speedometers, two people.
/// </para>
/// </summary>
public enum ObjectMatchScope
{
    /// <summary>
    /// Default. Every instance the prompt matches, across the clip: a blur or censor hides all of them,
    /// not the first one the selector happened to return. Deliberately NOT "every object the model
    /// emitted" — a slot that overlaps an instance and covers under a quarter of its area is a PART of
    /// that object and stays inside its track, so an answer that emits many fragments for one prompt
    /// still yields one instance.
    /// </summary>
    AllInstances,

    /// <summary>
    /// One object only: the anchor the selector picks (the most covered pixels, or the target's own
    /// <see cref="ObjectTrackTarget.Region"/> when it has one) plus its re-acquisitions. For the
    /// deliberate "this particular one" case.
    /// </summary>
    SingleTrack
}

/// <summary>
/// One thing for the object tracker to follow. <see cref="Label"/> names it ("licence plate",
/// "the red car's number plate", "shop sign"), and for
/// <see cref="ObjectTrackMethod.Segmentation"/> it IS the model's prompt. With no
/// <see cref="Region"/> the vision provider (or, for segmentation, the prompt alone) finds every
/// instance itself; with a <see cref="Region"/> (normalized corners in the source frame, TL/TR/BL/BR
/// like every insert quad) the tracker starts exactly there at <see cref="AtSec"/> and no vision call
/// is made — for segmentation the region becomes a box prompt (its centre, a point prompt) on that
/// frame instead.
/// </summary>
public sealed record ObjectTrackTarget(
    [property: AppBindable(AppInputType.ShortText, Label = "What to follow", MaxLength = SegmentationPrompt.MaxChars)]
    string Label,
    double? AtSec = null,
    VideoInsertSurfaceQuad? Region = null,
    /// <summary>
    /// Which tracker follows this target. <see cref="ObjectTrackMethod.Feature"/> (the default) is
    /// byte-identical to the pre-segmentation behaviour, including every stored target that carries no
    /// <c>method</c> key at all. Appended as the FOURTH positional parameter so every existing
    /// construction site keeps compiling.
    /// </summary>
    ObjectTrackMethod Method = ObjectTrackMethod.Feature,
    /// <summary>
    /// For <see cref="ObjectTrackMethod.Segmentation"/> only: whether the target covers every instance
    /// the prompt matches (<see cref="ObjectMatchScope.AllInstances"/>, the default and the C# default
    /// when the key is absent) or one object only (<see cref="ObjectMatchScope.SingleTrack"/>). Ignored
    /// by the free feature tracker. Appended LAST so every existing construction site keeps compiling
    /// and every stored target without the key keeps its meaning.
    /// </summary>
    ObjectMatchScope MatchScope = ObjectMatchScope.AllInstances,
    /// <summary>
    /// For <see cref="ObjectTrackMethod.Segmentation"/> only: the point a person tapped, as fractions of the upright source
    /// picture, at <see cref="AtSec"/>. With it the target names ONE object by where it is, not by words: it is always a
    /// <see cref="ObjectMatchScope.SingleTrack"/>, the object is the one whose mask holds the point on that frame, and none
    /// there means "not found" (never another object). A drawn box, if any, is <see cref="Region"/>. Appended; absent = no tap.
    /// </summary>
    ObjectSeedPoint? SeedPoint = null,
    /// <summary>
    /// For <see cref="ObjectTrackMethod.Segmentation"/> only: do not send <see cref="Label"/> to the model as words. The label
    /// stays required and is only the display name ("Selected object"). Appended; absent = the label is the prompt.
    /// </summary>
    bool NoWords = false);

/// <summary>
/// The clamps behind <see cref="VideoAnalyzeStepConfig"/>'s segmentation knobs, in ONE place so the
/// step's own validation, the provider client and the refusal messages cannot drift apart. Pure
/// arithmetic — no state, no I/O. A value is clamped, not refused, unless the doc comment on the
/// owning field says otherwise (only <c>MaxSegmentationSeconds</c> refuses, because truncating a
/// customer's clip silently is worse than failing).
/// </summary>
public static class SegmentationLimits
{
    /// <summary>Smallest usable <c>MaxSegmentationSeconds</c> (one second of input).</summary>
    public const int MinSeconds = 1;

    /// <summary>Largest <c>MaxSegmentationSeconds</c> the step will accept.</summary>
    public const int MaxSeconds = 600;

    /// <summary>Smallest <c>SegmentationDetectionThreshold</c> the vendor accepts (fal: 0.01).</summary>
    public const double MinDetectionThreshold = 0.01;

    /// <summary>Smallest <c>MaxSegmentationObjects</c> — zero objects would return no mask at all.</summary>
    public const int MinObjects = 1;

    /// <summary>The house cap on <c>MaxSegmentationObjects</c> (the vendor allows more; see the field's doc).</summary>
    public const int MaxObjects = 16;

    /// <summary>Smallest accepted <c>SegmentationTimeoutSeconds</c>.</summary>
    public const int MinProviderTimeoutSeconds = 60;

    /// <summary>Largest accepted <c>SegmentationTimeoutSeconds</c> (one hour — the step's own ceiling).</summary>
    public const int MaxProviderTimeoutSeconds = 3600;

    /// <summary>
    /// Largest file the hosted segmentation model is ever handed by URL (100 MB). A bigger source goes
    /// as a bounded re-encode of the same frames at the same size (the engine's "vendor copy"), never the
    /// original upload. fal documents no input limit for <c>sam-3-1/video-rle</c>, but it refused a
    /// 563 MB original with <c>422 file_download_error</c> after 8 s (prod, 2026-11-02) while every
    /// input it accepted was a few MB; 100 MB keeps a 60 s 4K clip at ~11 Mbps, far more than the
    /// model's own ~1K working resolution needs.
    /// </summary>
    public const long VendorSourceMaxBytes = 100_000_000;

    /// <summary>The effective per-source cap in seconds, clamped to 1..600.</summary>
    public static int ClampSeconds(int seconds) => Math.Clamp(seconds, MinSeconds, MaxSeconds);

    /// <summary>The effective detection threshold, clamped to 0.01..1.</summary>
    public static double ClampDetectionThreshold(double threshold) =>
        double.IsNaN(threshold) ? 0.5 : Math.Clamp(threshold, MinDetectionThreshold, 1.0);

    /// <summary>The effective object count, clamped to 1..16.</summary>
    public static int ClampObjects(int objects) => Math.Clamp(objects, MinObjects, MaxObjects);

    /// <summary>The effective call timeout in seconds, clamped to 60..3600.</summary>
    public static int ClampProviderTimeout(int seconds) =>
        Math.Clamp(seconds, MinProviderTimeoutSeconds, MaxProviderTimeoutSeconds);
}
