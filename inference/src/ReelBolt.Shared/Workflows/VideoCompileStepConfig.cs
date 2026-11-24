using System.ComponentModel;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// How <c>VideoCompile</c> assembles the kept spans into the output file. <c>Reencode</c> (the
/// default) is frame-accurate via a select/aselect filtergraph. <c>StreamCopy</c> cuts a single continuous
/// span as a lossless copy, which can only land on keyframes, so it requires an explicit opt-in
/// (<see cref="VideoCompileStepConfig.AllowKeyframeSnapping"/>); a cut of more than one span is re-encoded
/// frame-accurately instead, because copied pieces cannot be joined cleanly.
/// </summary>
public enum VideoCompileMode
{
    Reencode,
    StreamCopy
}

/// <summary>
/// How the background-music bed (see docs/video-editing.md "Background music") behaves relative
/// to dialogue. <c>SpeechEnvelope</c> (the default) lifts the music during non-speech windows via
/// a deterministic keyframed <c>volume</c> envelope computed from the analysis artifact's own
/// silence gaps/transcript segments — never a runtime audio-level compressor
/// (<c>sidechaincompress</c>), which this codebase deliberately does not use here since its
/// behavior depends on the actual waveform and cannot be asserted to an exact filter string or
/// explained in the step's own output. <c>Off</c> is a constant ducked bed throughout — the
/// simplest, always-available fallback.
/// </summary>
public enum MusicDuckingMode
{
    Off,
    SpeechEnvelope
}

/// <summary>
/// How a background-music track's own duration relates to the compiled edit's duration. See
/// docs/video-editing.md "Background music".
/// </summary>
public enum MusicFit
{
    /// <summary>The track loops (ffmpeg <c>-stream_loop -1</c>) to fill the whole edit, then is trimmed to the edit's exact frame-quantized length.</summary>
    LoopToFit,

    /// <summary>The track plays once and is trimmed to its own length if shorter than the edit — no loop, no fill.</summary>
    PlayOnce
}

/// <summary>
/// Workflow-level gate for the deterministic per-seam transition system (see
/// docs/video-editing.md "Cut transitions"). <c>Off</c> (default) is byte-identical to the
/// pre-transition compile path — every seam is a hard butt-cut, exactly as before this addition.
/// <c>AudioOnly</c> forces every seam to a short audio-only ramp (no video crossfade at all).
/// <c>Auto</c>/<c>Expressive</c> select a per-seam <see cref="SeamTreatment"/> from a deterministic
/// rule table over measured shot facts (never a model) — <c>Expressive</c> picks visually bolder
/// treatments than <c>Auto</c> for the same measured facts (see <c>SeamTransitionPlanner</c>).
/// </summary>
public enum VideoTransitionPolicy
{
    Off,
    AudioOnly,
    Auto,
    Expressive,

    /// <summary>
    /// The editor agent chooses each seam's transition and speed by word on its kept spans
    /// (<c>VideoEditKeepSpan.Transition</c>/<c>TransitionSpeed</c>); a seam it leaves empty takes
    /// the <see cref="Auto"/> rules. Appended last: stored configs persist enum names.
    /// </summary>
    Editor
}

/// <summary>
/// The concrete treatment a single cut-seam receives, chosen deterministically by
/// <c>SeamTransitionPlanner</c> from measured facts — or, under <see cref="VideoTransitionPolicy.Editor"/>,
/// resolved from the editor agent's preset and speed WORDS (never a model-supplied number). <c>HardCut</c> is the pre-existing behavior (no fade of any kind).
/// <c>AudioOnly</c> keeps the video a hard cut but ramps audio through the seam. The remaining
/// values all apply a video crossfade/fade (requiring the ffmpeg <c>xfade</c>/<c>acrossfade</c>
/// filters — see <c>SeamTransitionPlanner</c>'s <c>xfadeAvailable</c> demotion path).
/// </summary>
public enum SeamTreatment { HardCut, AudioOnly, DipCut, SoftCut, Dissolve, DipToBlack, WhipBlur }

/// <summary>
/// How a tracked screen insert's plate is treated geometrically (see docs/video-editing.md
/// "Non-planar surfaces and multi-plate compositing"). <c>Auto</c> (default) measures the plate's
/// own silhouette and picks: a flat plate takes the original single-<c>perspective</c> corner pin
/// with no change whatsoever, a plate whose measured curvature clears
/// <see cref="VideoCompileStepConfig.MinInsertCurvature"/> takes a piecewise-projective mesh warp.
/// <c>Planar</c> pins the old behaviour on regardless of what was measured; <c>Mesh</c> forces the
/// mesh path even on a plate that read flat (where it is mathematically the same warp, just more
/// expensive — useful only for isolating the two paths during debugging).
/// </summary>
public enum InsertSurfaceMode
{
    Auto,
    Planar,
    Mesh
}

/// <summary>
/// Cheap, in-process structural checks run against the resolved edit before encoding starts.
/// No model call. <see cref="MinRetainedRatio"/> exists specifically to refuse an edit that
/// discards nearly everything the source video contained.
/// </summary>
public sealed record VideoCompileExpectation(
    double? MinOutputSeconds = null,
    double? MaxOutputSeconds = null,
    double? MinRetainedRatio = 0.15,
    double? MaxRetainedRatio = null);

/// <summary>
/// JSON configuration for a <c>StepType.VideoCompile</c> workflow step. Deserialized from
/// <c>WorkflowStep.VideoCompileConfigJson</c>. A closed, versioned, strongly-typed record — the
/// same house style as <see cref="ExtractStepConfig"/>.
/// </summary>
/// <remarks>
/// Reusing <see cref="ExtractInputRef"/> for <see cref="Decision"/> is deliberate: the frontend
/// already has a TS mirror and an input-ref picker for it, so it is reused verbatim rather than
/// invented a second time. Only <c>From = Previous</c> or <c>From = Step</c> are valid decision
/// sources here (validated at execution time, not by the type system) — an Extract step's own
/// <c>Accumulated</c>/<c>ProjectFiles</c> sources make no sense as an edit decision.
/// </remarks>
[StepCard(
    "Cuts and assembles the final film from an editing decision and the footage.",
    ["An editing decision from an earlier step", "A VideoAnalyze step before it", "Pictures or videos to show"],
    "The finished video file",
    ReelBolt.Shared.Workflows.Apps.AppCostClass.NoCredits,
    ReadableBy = ["Any later step", "The project's files"],
    PairsWith = ["ThumbnailGenerate: picks a cover frame of the finished film", "ExtractFrame: grabs a still of the finished film"])]
public sealed record VideoCompileStepConfig(
    int Version,
    ExtractInputRef Decision,
    int AnalysisStepOrder,               // which VideoAnalyze step's full artifact to resolve ids against
    Guid? AnalysisStepResultId = null,   // cross-execution override: resolve a prior run's artifact instead of this execution's
    VideoCompileMode Mode = VideoCompileMode.Reencode,
    int PrePaddingMs = 80,
    int PostPaddingMs = 120,
    int MinSegmentMs = 250,
    int MaxSegments = 200,
    bool AllowKeyframeSnapping = false,  // required=true whenever Mode=StreamCopy
    string OutputFileName = "edited.mp4",
    // Allowlisted at execution time — these are workflow-author-supplied config, not model
    // output, but they still reach ffmpeg argv, so they are validated just as strictly.
    string VideoCodec = "libx264",
    string AudioCodec = "aac",
    int Crf = 20,
    string Preset = "veryfast",
    bool RegisterProjectFile = true,     // registers the compiled video as a re-editable ProjectFile row
                                         // -- Phase 3: optional motion-graphics overlays (see docs/video-editing.md
                                         //    "Motion graphics (Phase 3)"). EnableGraphics=false (default) is byte-identical to the
                                         //    pre-Phase-3 compile path — this is the load-bearing backward-compatibility guarantee. --
    /// <summary>
    /// Which step's resolved <c>MotionGraphicsPlanOutput</c> to apply. <c>null</c> (default) means
    /// no graphics plan is even looked for. Reuses <see cref="ExtractInputRef"/> verbatim, same as
    /// <see cref="Decision"/> — only <c>From = Previous</c> or <c>From = Step</c> are valid.
    /// </summary>
    ExtractInputRef? GraphicsPlan = null,
    [property: AppBindable(AppInputType.Toggle, Label = "Titles and callouts")]
    bool EnableGraphics = false,
    int MaxOverlays = 20,
    int OverlayShortMs = 1500,
    int OverlayMediumMs = 3000,
    int OverlayHoldMs = 6000,
    int OverlayFadeMs = 300,
    /// <summary>Percent of frame height. Clamped 2..12 at execution time.</summary>
    int OverlayFontSizePct = 5,
    /// <summary>
    /// Percent of FRAME height the drawn overlay box (drawbox/drawtext background, and the box a
    /// rendered-asset overlay is stretch-scaled into) actually occupies — never the raw named
    /// safe-zone band's own height, which is a placement SAFE ZONE (LowerThird/UpperThird are the
    /// frame's outer third), not a target size. Clamped 6..40 at execution time, and never allowed
    /// to exceed the band's own height either way, so the box always stays inside the safe zone it
    /// was offered. Default 16 (a compact accent strip, not a takeover) — see
    /// DrawtextFilterBuilder.ComputeAccentBoxPixels and docs/video-editing.md "Motion graphics
    /// (Phase 3)".
    /// </summary>
    int OverlayBoxHeightPct = 16,
    /// <summary>
    /// Percent of the named band's own WIDTH the drawn overlay box occupies, centered — so the box
    /// is never full-bleed edge-to-edge. Clamped 30..100 at execution time. Default 82.
    /// </summary>
    int OverlayBoxWidthPct = 82,
    // Allowlisted at execution time exactly like VideoCodec/AudioCodec/Preset above — these are
    // workflow-author-supplied config, but still reach ffmpeg's drawtext/drawbox filter string.
    string OverlayFontColor = "white",
    string OverlayBoxColor = "black@0.45",
    int MaxOverlayTextChars = 80,
    int MaxOverlaySubtextChars = 60,
    // -- Background music (see docs/video-editing.md "Background music"). EnableMusic=false
    //    (default) is byte-identical to the pre-music compile path — this is the load-bearing
    //    backward-compatibility guarantee of this whole addition, exactly like EnableGraphics. --
    /// <summary>
    /// Which step's resolved <c>MusicPlanOutput</c> to apply. <c>null</c> (default) means no plan
    /// is even looked for — the deterministic <see cref="MusicTrackProjectFileId"/> path (or no
    /// music) is used instead. Reuses <see cref="ExtractInputRef"/> verbatim, same as
    /// <see cref="Decision"/>/<see cref="GraphicsPlan"/> — only <c>From = Previous</c> or
    /// <c>From = Step</c> are valid.
    /// </summary>
    ExtractInputRef? MusicPlan = null,
    /// <summary>
    /// A specific <c>audio/*</c> project file to use as the background-music track — the
    /// deterministic path (no agent required), and also the fallback when <see cref="MusicPlan"/>
    /// is unresolvable/invalid or names an unoffered track id.
    /// </summary>
    Guid? MusicTrackProjectFileId = null,
    /// <summary>Applies the resolved music track during the same encode. <c>false</c> (default) is byte-identical to the pre-music compile path. Requires <c>Mode = Reencode</c> and <c>AudioCodec != "copy"</c>.</summary>
    [property: AppBindable(AppInputType.Toggle, Label = "Background music")]
    bool EnableMusic = false,
    MusicDuckingMode MusicDucking = MusicDuckingMode.SpeechEnvelope,
    MusicFit MusicFitPolicy = MusicFit.LoopToFit,
    int MusicFadeInMs = 1500,
    int MusicFadeOutMs = 2500,
    // Bed level (dBFS) keyed by the model's Intensity word — exactly how OverlayShortMs/
    // MediumMs/HoldMs key off Duration. Clamped to [-40, -6] at execution time.
    int MusicBedQuietDb = -26,
    int MusicBedBalancedDb = -20,
    int MusicBedFeatureDb = -14,
    // Attenuation (dB, <= 0) applied BELOW the bed while dialogue is present, keyed by the
    // model's Ducking word. Clamped to [-30, 0] at execution time.
    int MusicDuckLightDb = -6,
    int MusicDuckNormalDb = -11,
    int MusicDuckHeavyDb = -18,
    /// <summary>Linear gain ramp (ms) INSIDE each lift window — the music is never above the ducked level exactly at a window boundary, so a lift can never bleed into adjacent dialogue.</summary>
    int MusicDuckRampMs = 400,
    /// <summary>Non-speech windows shorter than this (and shorter than twice the ramp) are never lifted at all — a lift too short to fully ramp reads as pumping, not a genuine break.</summary>
    int MinMusicLiftWindowMs = 1200,
    /// <summary>Caps the volume-envelope expression's length; excess windows are dropped, longest first, then re-sorted chronologically.</summary>
    int MaxMusicLiftWindows = 12,
    /// <summary>Lift windows closer together than this are merged into one.</summary>
    int MusicLiftMergeMs = 400,
    VideoCompileExpectation? Expect = null,
    // -- Cut transitions (see docs/video-editing.md "Cut transitions"). Every field below defaults
    // to a byte-identical-to-before-this-addition value: ProgramFadeInMs/ProgramFadeOutMs/
    // ProgramAudioFadeInMs/ProgramAudioFadeOutMs all default to 0 (no program fade at all), and
    // TransitionPolicy defaults to Off (every seam a hard butt-cut, exactly as before). This is
    // the load-bearing backward-compatibility guarantee of this whole addition, exactly like
    // EnableGraphics/EnableMusic above. Appended AFTER Expect (not inserted earlier) so every
    // existing positional-construction call site and JSON payload keeps compiling/deserializing
    // unchanged. --
    /// <summary>Whole-piece video fade-IN duration (ms) at the very start of the compiled output. 0 (default) applies no fade.</summary>
    int ProgramFadeInMs = 0,
    /// <summary>Whole-piece video fade-OUT duration (ms) at the very end of the compiled output. 0 (default) applies no fade.</summary>
    [property: AppBindable(AppInputType.Toggle, AppInputType.Number, Label = "Fade out at the end", Min = 0, Max = 10000, ToggleOn = 1000, ToggleOff = 0)]
    int ProgramFadeOutMs = 0,
    /// <summary>Whole-piece audio fade-IN duration (ms). 0 (default) applies no fade. Requires the resolved audio codec to not be a stream-copy codec — see <c>PROGRAM_AUDIO_FADE_REQUIRES_AUDIO_REENCODE</c>.</summary>
    int ProgramAudioFadeInMs = 0,
    /// <summary>Whole-piece audio fade-OUT duration (ms). 0 (default) applies no fade. Same requirement as <see cref="ProgramAudioFadeInMs"/>.</summary>
    [property: AppBindable(AppInputType.Toggle, AppInputType.Number, Label = "Fade the sound out at the end", Min = 0, Max = 10000, ToggleOn = 1000, ToggleOff = 0)]
    int ProgramAudioFadeOutMs = 0,
    /// <summary>Fade color for the program video fade-in/out (<c>fade=color=</c>). Allowlisted at execution time to <c>"black"</c>/<c>"white"</c> — see <c>AllowedProgramFadeColors</c>.</summary>
    string ProgramFadeColor = "black",
    /// <summary>Workflow-level gate for the per-seam transition system. <c>Off</c> (default) is byte-identical to the pre-transition compile path.</summary>
    [property: AppBindable(AppInputType.Choice, Label = "Transitions")]
    VideoTransitionPolicy TransitionPolicy = VideoTransitionPolicy.Off,
    /// <summary>Half-width (ms) of the trapezoidal audio ramp <see cref="SeamTreatment.AudioOnly"/> applies around a seam.</summary>
    int AudioSeamRampMs = 24,
    /// <summary>Crossfade/fade-pair duration (ms) for <see cref="SeamTreatment.SoftCut"/>.</summary>
    int SoftCutMs = 200,
    /// <summary>Crossfade duration (ms) for <see cref="SeamTreatment.Dissolve"/>.</summary>
    int DissolveMs = 500,
    /// <summary>Crossfade duration (ms) for <see cref="SeamTreatment.DipToBlack"/> (an <c>xfade=fadeblack</c>).</summary>
    int DipToBlackMs = 600,
    /// <summary>Per-side fade duration (ms) for <see cref="SeamTreatment.DipCut"/> — a non-overlapping fade-out/fade-in pair, not a crossfade.</summary>
    int DipCutMs = 220,
    /// <summary>Hard cap (ms) on any single seam's crossfade overlap, regardless of the rule table's chosen duration.</summary>
    int MaxTransitionMs = 1200,
    /// <summary>Hard cap (percent of total seams) on how many seams may carry an OVERLAPPING treatment (OverlapSec &gt; 0) — excess seams downgrade to <see cref="SeamTreatment.AudioOnly"/>.</summary>
    int MaxTransitionRatioPct = 35,
    /// <summary>When the resolved cut list has more seams than this, the whole transition plan degrades to no overlaps at all (every seam <see cref="SeamTreatment.AudioOnly"/>/<see cref="SeamTreatment.HardCut"/>) rather than building an unbounded filtergraph.</summary>
    int MaxTransitionSegments = 80,
    /// <summary>A same-source removed gap at least this long (ms) is eligible to read as a deliberate section break (rule R4) rather than an ordinary trimmed pause.</summary>
    int SectionBreakGapMs = 8000,
    /// <summary>Reserved no-op flag for a future span-motion phase (speed ramps/Ken Burns at cuts) — deliberately unimplemented in this addition; always behaves as if <c>false</c>.</summary>
    bool EnableSpanMotion = false,
    // -- Tracked screen inserts (see docs/video-editing.md "Tracked screen inserts (Phase 5)").
    //    EnableInserts=false (default) is byte-identical to the pre-inserts compile path — the
    //    same load-bearing backward-compatibility guarantee as EnableGraphics/EnableMusic. --
    /// <summary>
    /// Composites the motion-graphics plan's chosen screen inserts (a Remotion-rendered scene
    /// corner-pinned into a tracked chroma-plate region via ffmpeg's per-frame-animated
    /// <c>perspective</c> filter) during the same encode. Inserts are read from the SAME resolved
    /// <c>MotionGraphicsPlanOutput</c> that <see cref="GraphicsPlan"/> references (the planner
    /// emits overlays and inserts in one output), so <see cref="GraphicsPlan"/> must be configured
    /// for inserts to resolve — but <see cref="EnableGraphics"/> itself need not be on. Requires
    /// <c>Mode = Reencode</c>. v1 limitation: skipped (soft, reported) on a multi-source compile or
    /// one with overlapping seam transitions — see docs/video-editing.md.
    /// </summary>
    bool EnableInserts = false,
    /// <summary>Tracks whose <c>VideoInsertRegionTrack.Confidence</c> falls below this are dropped (<c>confidence_below_threshold</c>) rather than composited badly.</summary>
    double MinInsertConfidence = 0.5,
    /// <summary>Cap on applied screen inserts; excess dropped in plan order (<c>max_inserts_exceeded</c>).</summary>
    int MaxInserts = 3,
    /// <summary>
    /// Cap on the per-insert count of corner keyframes baked into the ffmpeg <c>perspective</c>
    /// expressions (the full track is uniformly downsampled to at most this many). Bounds
    /// filtergraph-expression size; the filtergraph is written to a script file when long, and
    /// past 96 keyframes the series is written as a log-depth balanced tree, so this is a
    /// size knob, not a correctness one. Was 96, which thinned a track sampled at the source's
    /// full frame rate back down to ~4 fps on a 24 s insert and reintroduced the interpolation
    /// error dense sampling removes. Clamped 2..5000.
    /// </summary>
    int MaxInsertExprKeyframes = 1500,
    /// <summary>
    /// Fractional outward expansion of the tracked quad (about its centroid) before compositing,
    /// hiding the plate's own edge fringe under the inserted content. Clamped 0..0.1. Default 0.02.
    /// </summary>
    double InsertOverscan = 0.02,
    // -- Non-planar surfaces (see docs/video-editing.md "Non-planar surfaces and multi-plate
    //    compositing"). A plate that measures FLAT keeps the original single-perspective
    //    corner-pin filtergraph under every setting below — only its corner COORDINATES improve,
    //    and only when the analyze step measured the silhouette
    //    (VideoAnalyzeStepConfig.MeasureInsertCurvature, which turns those measurements off
    //    entirely for a byte-identical pre-existing result). Appended AFTER InsertOverscan, so
    //    every existing positional call site and stored JSON payload is untouched. --
    /// <summary>
    /// Whether a tracked plate may be composited with a piecewise-projective MESH warp (a curved
    /// screen, a bent card, a plate on any surface a single 4-point homography approximates badly)
    /// instead of one flat corner pin. <see cref="InsertSurfaceMode.Auto"/> — the default — decides
    /// per plate from the curvature the analyze step measured off its silhouette. A plate that
    /// measures flat keeps the single corner pin either way: a mesh is what a BEND needs, not
    /// what corner rounding needs, which the refined corners already handle on both paths.
    /// </summary>
    InsertSurfaceMode InsertSurface = InsertSurfaceMode.Auto,
    /// <summary>
    /// Under <see cref="InsertSurfaceMode.Auto"/>, the measured silhouette curvature a plate must
    /// exceed to earn a mesh warp, as a dimensionless fraction of the plate's own size
    /// (<c>VideoInsertRegionTrack.MeanCurvature</c>). Default <c>0.01</c> — roughly 1% of the
    /// plate's across-dimension, about 4px of bow on a 400px plate. Below it the plate is treated
    /// as flat, which is both cheaper and exactly the pre-existing behaviour. Raising it makes the
    /// mesh path rarer; setting it to 0 meshes anything with any measured bow at all.
    /// </summary>
    double MinInsertCurvature = 0.01,
    /// <summary>
    /// Target worst-case error, in destination pixels, between a mesh cell's straight edge and the
    /// measured curve it approximates — this is what sets how many cells a given plate gets
    /// (<c>InsertSurfaceMesh.ChooseDensity</c>: a bow of peak <c>s</c> over <c>n</c> cells leaves
    /// <c>s/n^2</c>, so <c>n = ceil(sqrt(s/tolerance))</c>). Default <c>0.6</c>. Smaller means more
    /// cells and a slower encode; larger means a visibly polygonal edge on a strong bend.
    /// </summary>
    double InsertMeshTolerancePx = 0.6,
    /// <summary>
    /// Hard ceiling on <c>rows * cols</c> for one insert's mesh, bounding both filtergraph size and
    /// per-frame cost (each cell is its own <c>perspective</c> + mask + <c>overlay</c>). Default
    /// <c>24</c>; measured at 1080p, an 18-cell mesh encodes at roughly 94 fps against 253 fps for
    /// the single-quad path. Clamped 1..64.
    /// </summary>
    int MaxInsertMeshCells = 24,
    /// <summary>
    /// Clip each composited insert to the plate's own per-frame silhouette, using the
    /// <c>colorkey</c> parameters the analyze step grid-searched for that plate. This is what
    /// preserves a rounded bezel corner, a camera notch, and anything passing in FRONT of the
    /// plate — a hand crossing the screen occludes the insert instead of being painted over.
    /// Default <c>true</c>; a track with no solved key (or one that scored too low to trust) is
    /// composited with the plain quad mask regardless, so this can only improve a composite or
    /// leave it unchanged. Set <c>false</c> to force the pre-matting behaviour. See
    /// docs/video-editing.md "Silhouette matting".
    /// </summary>
    bool EnableInsertMatte = true,
    // -- Color grading (see docs/video-editing.md "Color grading"). EnableColorGrade=false
    //    (default) is byte-identical to the pre-grade compile path — the same load-bearing
    //    backward-compatibility guarantee as EnableGraphics/EnableMusic/EnableInserts. Appended
    //    AFTER InsertOverscan so every existing positional-construction call site and JSON
    //    payload keeps compiling/deserializing unchanged. --
    /// <summary>
    /// Which step's resolved <c>ColorGradePlanOutput</c> to apply. <c>null</c> (default) means no
    /// grade plan is even looked for. Reuses <see cref="ExtractInputRef"/> verbatim, same as
    /// <see cref="Decision"/>/<see cref="GraphicsPlan"/>/<see cref="MusicPlan"/> — only
    /// <c>From = Previous</c> or <c>From = Step</c> are valid. Points at either a solo
    /// <c>AgentType.Colorist</c> Agent step or a <c>StepType.ColorGradeRoom</c> step — both emit
    /// the exact same <c>ColorGradePlanOutput</c> shape.
    /// </summary>
    ExtractInputRef? ColorGradePlan = null,
    /// <summary>
    /// Applies the resolved colour grade (a whole-program ffmpeg
    /// <c>eq</c>/<c>colorbalance</c>/<c>colorlevels</c>/<c>hue</c> chain built entirely from
    /// first-party tables keyed by the plan's enum words — see <c>ColorGradeFilterBuilder</c>)
    /// during the same encode, BEFORE overlays/inserts are painted so graphics stay clean.
    /// <c>false</c> (default) is byte-identical to the pre-grade compile path. Requires
    /// <c>Mode = Reencode</c>. Every plan-level failure (missing/invalid plan, unknown look word,
    /// unavailable filters) degrades to "no grade applied", never to a failed compile — a plan
    /// whose <c>Look</c> is <c>"None"</c> is a valid decision to apply no grade.
    /// </summary>
    [property: AppBindable(AppInputType.Toggle, Label = "Colour look")]
    bool EnableColorGrade = false,
    // -- Sound effects (see docs/video-editing.md "Sound effects"). EnableSfx=false (default) is
    //    byte-identical to the pre-SFX compile path — the same load-bearing
    //    backward-compatibility guarantee as EnableGraphics/EnableMusic/EnableInserts/
    //    EnableColorGrade. Appended AFTER EnableColorGrade so every existing
    //    positional-construction call site and JSON payload keeps compiling/deserializing
    //    unchanged. --
    /// <summary>
    /// Which step's resolved <c>SfxPlanOutput</c> to apply. <c>null</c> (default) means no SFX
    /// plan is even looked for. Reuses <see cref="ExtractInputRef"/> verbatim, same as
    /// <see cref="Decision"/>/<see cref="GraphicsPlan"/>/<see cref="MusicPlan"/>/
    /// <see cref="ColorGradePlan"/> — only <c>From = Previous</c> or <c>From = Step</c> are
    /// valid. Unlike music's <see cref="MusicTrackProjectFileId"/>, there is deliberately NO
    /// deterministic no-agent fallback field: cue placement (WHICH clip at WHICH moment) is
    /// inherently editorial, and a blanket "same stinger at every cut" config path would be a
    /// footgun, not a capability — see docs/video-editing.md "Sound effects".
    /// </summary>
    ExtractInputRef? SfxPlan = null,
    /// <summary>
    /// Mixes the resolved SFX cues into the output audio during the same encode. <c>false</c>
    /// (default) is byte-identical to the pre-SFX compile path. Requires <c>Mode = Reencode</c>
    /// and <c>AudioCodec != "copy"</c> (the same pair of hard config errors music enforces).
    /// Every plan/cue-level failure degrades to "no SFX applied" / "drop this one cue", never a
    /// failed compile.
    /// </summary>
    [property: AppBindable(AppInputType.Toggle, Label = "Sound effects")]
    bool EnableSfx = false,
    /// <summary>Cap on applied SFX cues; excess dropped in plan order (<c>max_cues_exceeded</c>).</summary>
    int MaxSfxCues = 8,
    /// <summary>
    /// Hard cap (seconds) on any single cue's play window — a long file misused as a cue is
    /// trimmed rather than running under the whole edit (a bed belongs to <see cref="EnableMusic"/>,
    /// not here). Clamped to at least 0.25s at execution time.
    /// </summary>
    double MaxSfxCueSeconds = 4.0,
    // Cue gain (dBFS attenuation relative to full scale) keyed by the model's Volume word —
    // exactly how MusicBedQuietDb/BalancedDb/FeatureDb key off Intensity. Clamped [-40, 0] at
    // execution time. Defaults sit audibly over dialogue near -20 dB RMS (older -18/-12/-6 left Subtle inaudible);
    // set the old values explicitly to reproduce them.
    int SfxSubtleDb = -12,
    int SfxNormalDb = -8,
    int SfxStrongDb = -4,
    /// <summary>How far (ms) a <c>Timing: "Lead"</c> cue fires BEFORE its anchor's output-timeline start moment.</summary>
    int SfxLeadMs = 150,
    /// <summary>How far (ms) a <c>Timing: "Lag"</c> cue fires AFTER its anchor's output-timeline start moment.</summary>
    int SfxLagMs = 150,
    /// <summary>Declick fade-out (ms) at the end of each cue's play window, so a MaxSfxCueSeconds-trimmed clip never ends on a hard edge. Capped at half the cue's own play window.</summary>
    int SfxFadeOutMs = 120,
    // -- Generated clips (see docs/video-generation.md and plan §5 Wave A). EnableGeneratedClips=false
    //    (the default) is byte-identical to the pre-wave compile path — no extra ffmpeg input, no
    //    extra filter, no new EDL/outputSummary key. The same load-bearing
    //    backward-compatibility guarantee as EnableGraphics/EnableMusic/EnableInserts/
    //    EnableColorGrade/EnableSfx. Appended AFTER SfxFadeOutMs (not inserted earlier) so every
    //    existing positional-construction call site and JSON payload keeps
    //    compiling/deserializing unchanged. --
    /// <summary>
    /// Which step's generated-clip MANIFEST to place — the <c>VideoGenerate</c> step, whose
    /// per-clip manifest is written to that step result's <c>ArtifactStorageKey</c>. <c>null</c>
    /// (default) means no manifest is even looked for. Reuses <see cref="ExtractInputRef"/> verbatim,
    /// same as <see cref="Decision"/>/<see cref="GraphicsPlan"/>/<see cref="MusicPlan"/>/
    /// <see cref="ColorGradePlan"/>/<see cref="SfxPlan"/> — only <c>From = Previous</c> or
    /// <c>From = Step</c> are valid.
    /// </summary>
    ExtractInputRef? GeneratedClips = null,
    /// <summary>
    /// Places the resolved manifest's clips in the compiled edit. <c>false</c> (default) is
    /// byte-identical to the pre-generated-clips compile path. Requires <c>Mode = Reencode</c>
    /// (<c>GENERATED_CLIPS_REQUIRE_REENCODE</c>) — a picture replacement and a prepended/appended
    /// concat segment both need a filtergraph, which stream-copy has none of. Every placement
    /// failure degrades PER CLIP (the cut is never taken hostage), and there is deliberately NO
    /// per-purpose placement config: placement derives from each planned shot's own <c>purpose</c>,
    /// exactly as graphics/inserts derive from their plan's own items. This wave implements
    /// <c>Cutaway</c> (picture-only replacement over the anchor's output window — dialogue audio
    /// kept, J/L-cut), <c>ColdOpen</c> and <c>EndCard</c> (prepended/appended as concat segments,
    /// pairing with <see cref="ProgramFadeInMs"/>/<see cref="ProgramFadeOutMs"/>).
    /// <c>SeamBridge</c>/<c>Extend</c>/<c>ScreenContent</c> are waves B/C and degrade explicitly
    /// rather than being silently misplaced as something else.
    /// </summary>
    [property: AppBindable(AppInputType.Toggle, Label = "AI extra shots")]
    bool EnableGeneratedClips = false,
    /// <summary>Cap on APPLIED generated clips, in manifest order; every further placeable clip degrades with <c>max_generated_clips_exceeded</c>.</summary>
    int MaxGeneratedClips = 8,
    // -- Voiceover (see docs/voiceover.md). EnableVoiceover=false (default) is byte-identical
    //    to the pre-voiceover compile path — the same load-bearing backward-compatibility
    //    guarantee as EnableGraphics/EnableMusic/EnableColorGrade/EnableSfx. Appended AFTER
    //    EnableSfx so every existing positional-construction call site and JSON payload keeps
    //    compiling/deserializing unchanged. --
    /// <summary>
    /// Applies a voiceover audio track to the output during the same encode. <c>false</c>
    /// (default) is byte-identical to the pre-voiceover compile path. Requires <c>Mode = Reencode</c>.
    /// See docs/voiceover.md for the full integration.
    /// </summary>
    [property: AppBindable(AppInputType.Toggle, Label = "Voice-over")]
    bool EnableVoiceover = false,
    /// <summary>
    /// Which step's resolved voiceover output to apply. <c>null</c> (default) means no
    /// voiceover is even looked for. See docs/voiceover.md.
    /// </summary>
    int? VoiceoverStepOrder = null,
    // -- Deterministic insert content (see docs/video-editing.md "Inserting an existing video").
    //    Both default to the pre-existing behaviour: no project-file content, and Auto fit, which
    //    stretches exactly as before whenever the content's aspect already matches the plate. --
    /// <summary>
    /// A <c>video/*</c> or <c>image/*</c> project file to composite into the tracked plates — the
    /// deterministic path, needing no motion-graphics agent and no <see cref="GraphicsPlan"/>.
    /// Applied (with <see cref="EnableInserts"/>) to every offered region that clears
    /// <see cref="MinInsertConfidence"/>, most confident first, up to <see cref="MaxInserts"/>,
    /// skipping any region a resolved plan already filled. This is what "put my clip on that
    /// phone" is: the clip is CONTENT, never a source of the edit — listing it as a
    /// <c>VideoAnalyze</c> source offers it to the editor as a shot to keep, which is how a
    /// tracking test once came back with the whole promo appended after the phone take.
    /// </summary>
    Guid? InsertContentProjectFileId = null,
    /// <summary>How insert content is fitted to the plate's own shape — see <see cref="InsertContentFit"/>.</summary>
    InsertContentFit InsertFit = InsertContentFit.Auto,
    // -- Censoring tracked objects (see docs/video-editing.md "Censoring tracked objects"). Null
    //    (default) is byte-identical to before. Requires Mode = Reencode. --
    /// <summary>
    /// Labels of feature-tracked objects (<c>VideoAnalyzeStepConfig.TrackObjects</c>) to obscure,
    /// matched case-insensitively against each track's <c>Label</c>; <c>"*"</c> obscures every
    /// feature-tracked object. Every track with a matching label is obscured in EVERY part of the
    /// output its source time survives into — not just the first, as an insert is — and
    /// regardless of its tracking confidence: for privacy, over-obscuring a doubtful track is the
    /// safe failure. The EDL lists each label that matched nothing, so a missed object is visible.
    /// </summary>
    IReadOnlyList<string>? CensorLabels = null,
    /// <summary>How a censored region is obscured.</summary>
    CensorStyle CensorStyle = CensorStyle.Blur,
    /// <summary>
    /// Margin added on EVERY side of each tracked object before obscuring, as a fraction of the
    /// object's longer side (0..1). Covers tracking and detection error. Measured from the longer
    /// side because thin objects — a licence plate is ~4x wider than tall — are where a vision
    /// model's box is most often a few pixels off vertically.
    /// </summary>
    double CensorPadding = 0.25,
    /// <summary>Seconds the first/last tracked position is held before/after a track, covering the frames where the object was entering or the tracker had just lost it. Clamped 0..3.</summary>
    double CensorHoldSec = 0.4,
    /// <summary>
    /// Ignore <see cref="Decision"/> and keep every analyzed source whole, in source order — the
    /// no-agent path for compiles that only add something to the footage (a tracking test, a
    /// censor pass, a screen insert) rather than cut it. <see cref="Decision"/> is still required
    /// by the record's shape but is never resolved.
    /// </summary>
    bool KeepWholeSources = false,
    // -- Burned captions (see docs/voiceover.md "Captions"). EnableCaptions=false (default) is
    //    byte-identical to the pre-captions compile path — the same load-bearing guarantee as
    //    EnableVoiceover/EnableGraphics/EnableMusic/EnableColorGrade/EnableSfx. Requires
    //    Mode = Reencode and EnableVoiceover (the cues are cut from the Voiceover step's own
    //    sanitized, actually-synthesized text and its measured word timings — never from the
    //    narration plan's raw text). Appended AFTER KeepWholeSources so every positional call
    //    site and stored payload keeps deserializing unchanged. --
    /// <summary>
    /// Burns captions for the applied voiceover lines into the picture during the same encode,
    /// through drawtext's <c>textfile=</c> + <c>expansion=none</c> path. <c>false</c> (default)
    /// is byte-identical to the pre-captions compile path. Requires <c>Mode = Reencode</c>.
    /// </summary>
    [property: AppBindable(AppInputType.Toggle, Label = "Captions")]
    bool EnableCaptions = false,
    /// <summary>Which caption treatment to burn — see <see cref="CaptionStyle"/>.</summary>
    [property: AppBindable(AppInputType.Choice, Label = "Caption look")]
    CaptionStyle CaptionStyle = CaptionStyle.Subtitle,
    /// <summary>
    /// Caption font size as a percentage of the frame height (clamped 2..12; Punch doubles it
    /// within the same clamp). <c>0</c> and <c>4</c> — the value every config written before the
    /// automatic size existed carries, since it was the default — both mean AUTOMATIC: a size
    /// chosen per style and canvas orientation that reads on a phone (see docs/voiceover.md
    /// "Captions"). Any other value is used as written.
    /// </summary>
    int CaptionFontSizePct = 4,
    /// <summary>drawtext font colour for caption text — allowlisted exactly like <see cref="OverlayFontColor"/>.</summary>
    string CaptionFontColor = "white",
    /// <summary>drawbox colour behind each caption cue, or <c>"none"</c> for no box — allowlisted exactly like <see cref="OverlayBoxColor"/>.</summary>
    string CaptionBoxColor = "black@0.6",
    /// <summary>Font colour of the words already spoken in a <see cref="Workflows.CaptionStyle.Karaoke"/> cue.</summary>
    string CaptionHighlightColor = "yellow",
    /// <summary>Longest caption cue, in characters, before a line is split at a word boundary (clamped 12..120). Ignored by <see cref="Workflows.CaptionStyle.Punch"/>, which shows one word at a time.</summary>
    int CaptionMaxCharsPerCue = 42,
    /// <summary>Cap on burned caption drawtext filters; further cues degrade with <c>max_caption_cues_exceeded</c>.</summary>
    int MaxCaptionCues = 400,
    // -- Dialogue under narration (see docs/voiceover.md "Pickups and dubs"). OverMusic (default)
    //    is byte-identical to the phase-1 mix. Requires Mode = Reencode. --
    /// <summary>
    /// What happens to the SOURCE dialogue under each applied voiceover line's window — see
    /// <see cref="Workflows.VoiceoverMode"/>. <c>OverMusic</c> (default) leaves it untouched.
    /// </summary>
    VoiceoverMode VoiceoverMode = VoiceoverMode.OverMusic,
    /// <summary>How far the source dialogue is attenuated under a line in <see cref="Workflows.VoiceoverMode.DuckDialogue"/>, in dB (clamped -40..0).</summary>
    int VoiceoverDuckDb = -12,
    /// <summary>Ramp into/out of a duck or mute window, in milliseconds (clamped 0..1000).</summary>
    int VoiceoverDuckRampMs = 120,
    // -- Pickups (see docs/voiceover.md "Pickups"). EnablePickups=false (default) is byte-identical.
    //    Requires Mode = Reencode. --
    /// <summary>
    /// Splices the re-recorded lines a pickup-sourced Voiceover step produced over the transcript
    /// segments they repair: the source dialogue is muted for exactly that segment's window and
    /// the pickup plays there, picture untouched. <c>false</c> (default) is byte-identical.
    /// </summary>
    [property: AppBindable(AppInputType.Toggle, Label = "Fix flubbed lines")]
    bool EnablePickups = false,
    /// <summary>The Voiceover step (source kind <c>PickupPlan</c>) whose lines are the pickups. <c>null</c> means none is looked for.</summary>
    int? PickupVoiceoverStepOrder = null,
    // -- Appended after the voiceover phase 3-4 members on merge with master (positional record:
    //    every existing call site and stored payload keeps deserializing unchanged). --
    /// <summary>
    /// Carry the plate's reflections onto a matted screen insert: glare and streaks the source had
    /// on the glass are screen-blended over the inserted content, inside the matte only. A plate
    /// with no reflections is unchanged by it (its reflection layer is black). Default
    /// <c>true</c>; has no effect on an insert without a silhouette matte. See
    /// docs/video-editing.md "Reflections".
    /// </summary>
    bool InsertReflections = true,
    /// <summary>
    /// Longest stretch of a generated <c>SeamBridge</c> clip spliced between two beats, in
    /// milliseconds (from the clip's first frame). A bridge is punctuation between scenes, not a
    /// scene: an uncapped 10 s generated clip between every pair of beats would double a promo's
    /// length with footage that carries no message. Default <c>3000</c>, floor 500. See
    /// docs/video-editing.md "Generated clips (b-roll)".
    /// </summary>
    int MaxSeamBridgeMs = 3000,
    /// <summary>
    /// What fills the canvas beside a clip whose aspect ratio differs from the program's (a
    /// portrait phone shot in a 16:9 film). <see cref="VideoCanvasFill.Black"/> (the default) pads
    /// with black bars, exactly as before; <see cref="VideoCanvasFill.Blur"/> fills them with a
    /// blurred, canvas-filling copy of the same frames. The clip itself sits in the identical
    /// place either way, so tracked-insert geometry is unaffected.
    /// </summary>
    VideoCanvasFill CanvasFill = VideoCanvasFill.Black,
    // -- Output format (see docs/video-editing.md "Output format"). Appended after CanvasFill:
    //    positional record, every existing call site and stored payload keeps deserializing. --
    /// <summary>
    /// The program's aspect ratio / target platform. <see cref="VideoOutputFormat.Source"/>
    /// (default) sizes the canvas from the analysed sources — the LARGEST source resolution when
    /// several clips are compiled, never merely the first clip's. The other members fix the
    /// canvas to a platform size (1080x1920, 1080x1080, 1920x1080, 1080x1350) and fit every clip into it per
    /// <see cref="OutputFit"/>.
    /// </summary>
    VideoOutputFormat OutputFormat = VideoOutputFormat.Source,
    /// <summary>
    /// How a clip whose aspect differs from a fixed <see cref="OutputFormat"/> is fitted:
    /// <see cref="VideoOutputFit.Crop"/> (default) fills the frame with a centred crop (16:9 → 9:16
    /// keeps the middle of the shot); <see cref="VideoOutputFit.Subject"/> fills it with a crop
    /// placed on each clip's subject; <see cref="VideoOutputFit.Pad"/> letterboxes it, filled per
    /// <see cref="CanvasFill"/>. Ignored when <see cref="OutputFormat"/> is <c>Source</c>.
    /// </summary>
    VideoOutputFit OutputFit = VideoOutputFit.Crop,
    /// <summary>
    /// The length the customer asked for, in seconds; <c>0</c> (default) means no target. The
    /// compile does not invent footage to meet it — it reports the requested vs. actual length
    /// and warns when the edit misses it by more than 20%.
    /// </summary>
    int TargetDurationSec = 0,
    // -- Caption typography (see docs/voiceover.md "Captions"). Appended after TargetDurationSec:
    //    positional record, every existing call site and stored payload keeps deserializing. Both
    //    are closed enums — a caption font is picked from the fonts the image actually ships,
    //    never named by a free path or family string. --
    /// <summary>
    /// The burned-caption typeface. <see cref="Workflows.CaptionFont.Auto"/> (default) uses the
    /// caption style's own font (Subtitle: Inter, Karaoke: Montserrat, Punch: Anton). Every other
    /// member is a font bundled in the workflow-engine image.
    /// </summary>
    CaptionFont CaptionFont = CaptionFont.Auto,
    /// <summary>
    /// Where burned captions sit. <see cref="Workflows.CaptionPosition.Auto"/> (default) puts
    /// Subtitle and Karaoke in the lower third and Punch in the centre; every position keeps
    /// title-safe margins, wider ones on a portrait canvas so captions clear the platform's own
    /// buttons and header.
    /// </summary>
    CaptionPosition CaptionPosition = CaptionPosition.Auto,
    /// <summary>
    /// Drop the clips' own sound from the program: only music, narration and sound effects are
    /// heard. For a music-driven reel cut from generated or stock footage whose audio is noise —
    /// without it that noise plays under the music and music ducking mistakes it for speech.
    /// Default <c>false</c> (byte-identical). The output's <c>audio.reason</c> reads
    /// <c>source_audio_muted</c> when it removed real audio.
    /// </summary>
    [property: AppBindable(AppInputType.Toggle, Label = "Mute the clips' own sound")]
    bool MuteSourceAudio = false,
    // -- Story order (see docs/video-editing.md "Playing spans in the editor's order"). Appended
    //    after MuteSourceAudio: positional record, every stored payload keeps deserializing.
    //    SourceOrder (default) is byte-identical. --
    /// <summary>
    /// The order the kept spans play in. <see cref="VideoKeepOrder.SourceOrder"/> (default) plays
    /// spans of one clip in that clip's own order whatever order the editor listed them in (spans
    /// of different clips keep the listed order, as before). <see cref="VideoKeepOrder.AsListed"/>
    /// plays every span in exactly the order the editor listed it — "open on the strongest shot",
    /// even when it was filmed last — with any part of a span already used by an earlier span of
    /// the same clip removed, so no moment plays twice. Reordered spans of one clip are cut with the
    /// segmented encode (the single-clip <c>select</c> path can only play a file forwards).
    /// </summary>
    VideoKeepOrder KeepOrder = VideoKeepOrder.SourceOrder,
    // -- Several insert contents and insert-content dips (see docs/video-editing.md "Inserting an
    //    existing video"). Appended after KeepOrder: positional record, every stored payload keeps
    //    deserializing. --
    /// <summary>
    /// Different project files for different tracked screens: each entry names the content for the
    /// plates of one source clip (<see cref="VideoInsertContentAssignment.SourceIndex"/>), one
    /// tracked region (<see cref="VideoInsertContentAssignment.RegionId"/>), or — with neither —
    /// every plate. The most specific matching entry wins (region over source over neither, then
    /// list order); a plate no entry matches falls back to <see cref="InsertContentProjectFileId"/>,
    /// and with no fallback either it gets no deterministic content. Null/empty (default) is
    /// byte-identical to the single-content behaviour.
    /// </summary>
    IReadOnlyList<VideoInsertContentAssignment>? InsertContents = null,
    /// <summary>
    /// What happens to the near-black stretches of a project-file VIDEO used as insert content — the
    /// dips a screen recording makes between screens, which on a tracked phone read as the phone
    /// switching off. <see cref="InsertContentDipMode.Freeze"/> (default) holds the last good frame
    /// across each dip, keeping the content's timing; <see cref="InsertContentDipMode.Cut"/> removes
    /// them; <see cref="InsertContentDipMode.Keep"/> leaves the content untouched (no detection).
    /// Content an agent rendered for the plate, and still images, are never touched.
    /// </summary>
    InsertContentDipMode InsertContentDips = InsertContentDipMode.Freeze,
    // -- Cutting to the beat (see docs/video-editing.md "Cutting to the beat"). Appended after
    //    InsertContentDips: positional record. Off (default) is byte-identical. --
    /// <summary>
    /// Snap every cut of the program to the beat (<see cref="VideoBeatSync.Beat"/>) or the bar
    /// (<see cref="VideoBeatSync.Bar"/>) of the background music actually used, by trimming the TAIL
    /// of each kept span (never extending it past its own footage; a span that cannot keep at least
    /// one beat is kept whole), and cut on the drop when the intro is at least two bars long. Needs
    /// <see cref="EnableMusic"/>; the grid comes from the analyze step's measurement of that track
    /// when it made one, else the track is decoded and measured here. Each cut is reported in the
    /// <c>beatSync</c> node. <see cref="VideoBeatSync.Off"/> (default) is byte-identical.
    /// </summary>
    [property: AppBindable(AppInputType.Choice, Label = "Cut on the beat", Choices = ["Off", "Beat", "Bar"])]
    VideoBeatSync BeatSync = VideoBeatSync.Off,
    /// <summary>
    /// Bring the finished program to a platform loudness: <see cref="ProgramLoudness.Social"/>
    /// (-14 LUFS, the level Instagram/TikTok/YouTube play back at), <see cref="ProgramLoudness.Web"/>
    /// (-16) or <see cref="ProgramLoudness.Broadcast"/> (-23, EBU R128). Measured on the encoded
    /// file and applied as one gain with a true-peak ceiling, so the balance between narration,
    /// music and effects is unchanged; the video stream is copied. Reported in the <c>loudness</c>
    /// node, and any failure keeps the un-normalized render. <see cref="ProgramLoudness.Off"/>
    /// (default) is byte-identical.
    /// </summary>
    ProgramLoudness LoudnessTarget = ProgramLoudness.Off,
    /// <summary>
    /// Issue #93 phase 4 (dailies): which step's <c>TakeSelectionOutput</c> — a <c>StepType.DailiesRoom</c>
    /// step, or a solo <c>TakeSelector</c> agent step — picks ONE generated take per planned shot when
    /// the generation step made several (<c>Takes</c> &gt; 1). Resolved like <see cref="Decision"/>, by
    /// offered take id only; a take the selection did not choose for a shot it decided on degrades
    /// with <c>take_not_selected</c>, and a shot the selection left undecided keeps its FIRST
    /// delivered take. Null (default) is byte-identical: every delivered take flows exactly as before.
    /// Appended LAST, after <see cref="LoudnessTarget"/>, so every positional construction site keeps
    /// compiling. Seam bridges are spliced before this reference is read and are not filtered by it.
    /// </summary>
    ExtractInputRef? TakeSelection = null,
    // -- Sound from an inserted asset (see docs/video-editing.md "Sound from an inserted asset").
    //    Appended after TakeSelection: positional record, so every existing positional-construction
    //    call site and every stored payload keeps compiling/deserializing. MixInsertAudio=false (the
    //    default) is byte-identical — the insert stays picture only. --
    /// <summary>
    /// Mix each applied insert's OWN sound track into the program, starting where its picture starts
    /// on the output timeline and trimmed to the insert's on-screen window, played once and never
    /// looped — a rendered chat animation keeps its keyboard clicks. The window is the one the
    /// picture already uses, so a portion the edit cut away is not heard and a dropped insert
    /// contributes no sound. Requires <see cref="EnableInserts"/> (<c>INSERT_AUDIO_REQUIRES_INSERTS</c>)
    /// and <see cref="AudioCodec"/> other than "copy" (<c>INSERT_AUDIO_REQUIRES_AUDIO_REENCODE</c>).
    /// Content with no sound track is the warning <c>insert_audio_absent</c>, never a failure — a
    /// still image always counts there. <c>false</c> (default) is byte-identical.
    /// </summary>
    bool MixInsertAudio = false,
    /// <summary>
    /// The gain applied to every insert's sound, in dB, from -24 to 12. Checked only when
    /// <see cref="MixInsertAudio"/> is on (<c>INSERT_AUDIO_GAIN_OUT_OF_RANGE</c> outside that range).
    /// </summary>
    int InsertAudioGainDb = 0,
    // -- Brand-aware overlays and captions (see docs/design-system.md and SPEC 14.2). Both default
    //    false: a config that does not opt in is byte-identical to the pre-P3-07 compile path in
    //    both its output and its step-cache key. Appended LAST, after InsertAudioGainDb (not
    //    inserted earlier), so every existing positional-construction call site and stored payload
    //    keeps compiling/deserializing unchanged. --
    /// <summary>
    /// Derives <see cref="OverlayFontColor"/> and <see cref="OverlayBoxColor"/> from the run's
    /// pinned design palette when a candidate token clears the WCAG contrast floor (7.0 against
    /// black). <c>false</c> (default) keeps the configured values exactly as before.
    /// </summary>
    bool DesignSystemColors = false,
    /// <summary>
    /// Derives <see cref="CaptionFont"/> from the run's pinned brand font families when it is
    /// <see cref="Workflows.CaptionFont.Auto"/>. <c>false</c> (default) keeps the configured font
    /// exactly as before.
    /// </summary>
    bool DesignSystemFonts = false,
    // -- Keyframed reframing, per-source fits and the subject safe area (see docs/video-editing.md
    //    "Keyframed reframing"). Appended LAST, after DesignSystemFonts: positional record, every
    //    existing call site and stored payload keeps compiling/deserializing. All three null (the
    //    default) is byte-identical for Crop and Pad fits. --
    /// <summary>
    /// Keyframed zoom/crop/pan over stretches of source clips (<see cref="VideoReframe"/>), each
    /// eased between up to <see cref="VideoReframe.MaxKeyframes"/> keyframes and applied before the
    /// clip is fitted to the canvas; tracked inserts, plate guards, censor masks and overlays move
    /// with it. Requires <see cref="Mode"/> = <c>Reencode</c> (<c>REFRAME_REQUIRES_REENCODE</c>).
    /// Reported per reframe in the <c>reframes</c> node. Null (default) is byte-identical.
    /// </summary>
    IReadOnlyList<VideoReframe>? Reframes = null,
    /// <summary>
    /// Per-source override of <see cref="OutputFit"/>, keyed by source index (a title card padded
    /// while the footage around it is cropped). Like <see cref="OutputFit"/>, ignored when
    /// <see cref="OutputFormat"/> is <c>Source</c>. Null (default) fits every clip per <see cref="OutputFit"/>.
    /// </summary>
    IReadOnlyDictionary<int, VideoOutputFit>? SourceFits = null,
    /// <summary>
    /// <see cref="VideoOutputFit.Subject"/> only: the frame-edge margin (fraction of the canvas,
    /// 0..0.25) on-screen text is kept inside. A kept span over a shot that carries text — any
    /// shot of a designed composition, or a shot the analysis read on-screen text in — is never
    /// cropped: it is padded into the canvas inset by this margin. Null means 0.05; 0 switches the
    /// guard off (the pre-guard Subject crop).
    /// </summary>
    double? SubjectSafeArea = null,
    /// <summary>
    /// T-106: insert content by reference — the fallback for a plate no <see cref="InsertContents"/> entry matches
    /// (ahead of <see cref="InsertContentProjectFileId"/>, which it supersedes when both are set). Names the content
    /// as a project file, an earlier step of THIS run, or a step result of an earlier run of this project (see
    /// <see cref="InsertContentRef"/>). Appended last; null (default) is byte-identical.
    /// </summary>
    InsertContentRef? InsertContentRef = null,
    // -- Music gain and generated-clip audio (T-109, see docs/video-editing.md "Background music" and
    //    "Generated clips (b-roll)"). Appended LAST: positional record, every call site and stored payload
    //    keeps compiling/deserializing. Both null (the default) are byte-identical. --
    /// <summary>
    /// The music bed's ABSOLUTE gain in dB, from -40 to +6 (<c>CONFIG_INVALID</c> outside that
    /// range). When set the bed is mixed at exactly this gain: the -6 dB bed clamp, the sole-soundtrack
    /// level (and its limiter) and the narration-gap lift do not apply, so <c>0</c> passes the track
    /// through at unity. Ducking still follows <see cref="MusicDucking"/>, and only when there is
    /// dialogue or narration to duck under. Null (default) is the intensity-driven bed, byte-identical.
    /// </summary>
    [property: AppBindable(AppInputType.Number, AppInputType.Choice, Label = "Music volume", Min = -40, Max = 6)]
    double? MusicGainDb = null,
    /// <summary>
    /// How a generated plate's own sound (a cold open or end card, or a source clip generated by the
    /// project's VideoGenerate step) is treated; see <see cref="GeneratedAudioMode"/>. Reported per
    /// clip in the output. Null (default) is <see cref="GeneratedAudioMode.Auto"/>: today's behaviour.
    /// </summary>
    GeneratedAudioMode? GeneratedClipAudio = null,
    // -- Whole-shot target length (docs/video-editing.md "Picking whole shots for a target length").
    //    Appended LAST: positional record, every stored payload keeps deserializing. Null (default) is
    //    TrimEnds: byte-identical. --
    /// <summary>
    /// How <see cref="TargetDurationSec"/> is met; see <see cref="TargetDurationMode"/>. Null (default) is
    /// <see cref="TargetDurationMode.TrimEnds"/>: today's behaviour.
    /// </summary>
    TargetDurationMode? TargetDurationMode = null,

    // -- Prompted region effects (initiative "prompted region effects", plan §4/§5). Null/empty
    //    (the default) is byte-identical: no mask is downloaded, no filter chain is built and the
    //    step-cache key is unchanged. Appended LAST, after DesignSystemFonts (not inserted earlier),
    //    so every existing positional-construction call site and stored payload keeps
    //    compiling/deserializing unchanged. --
    /// <summary>
    /// Apply an effect to just the part of the picture a text-prompted object occupies — the second
    /// half of the "apply effect E to object P" opt-in (the first is an analyze target whose
    /// <see cref="ObjectTrackMethod"/> is <c>Segmentation</c>). Each entry names its target by the
    /// analyze step's <c>g{n}</c> id or by the prompt label it was tracked under (case-insensitive).
    /// A target no segmentation track matches FAILS the compile with <c>EFFECT_TARGET_UNVERIFIED</c>;
    /// it never degrades to "no effect". That is decision D-06: a target whose prompt IS one of the
    /// analyze step's <c>Provenance.SegmentationPrompts</c> but has no track means the model was asked
    /// for that object and the masks did not arrive, and a plain config typo must not be able to look
    /// like a model miss — publishing the picture un-effected is the exact outcome this initiative
    /// exists to prevent. Two tracks sharing the prompt is <c>EFFECT_TARGET_AMBIGUOUS</c>; a target
    /// whose mask is missing or hash-unverified is <c>SEGMENTATION_MASK_UNVERIFIED</c> — never a
    /// silently unobscured picture. Capped at
    /// <see cref="RegionEffectRules.MaxEffects"/> entries
    /// (<c>EFFECT_LIMIT_EXCEEDED</c>); requires <c>Mode = Reencode</c>
    /// (<c>EFFECT_REQUIRES_REENCODE</c>). Validated by <see cref="RegionEffectRules.Validate"/>.
    /// </summary>
    [property: Description("Effects applied through a segmentation mask, each naming a g{n} track id or prompt label. Capped at 8 entries; requires Mode=Reencode.")]
    IReadOnlyList<VideoRegionEffect>? RegionEffects = null,
    // -- Subtitles from the clip's own speech (docs/voiceover.md "Subtitles from the transcript"). Appended LAST:
    //    positional record, every call site and stored payload keeps compiling/deserializing. Both defaults
    //    (Voiceover, null) are byte-identical to the pre-subtitles compile path. --
    /// <summary>
    /// Where burned captions take their text and timing from; see <see cref="Workflows.CaptionSource"/>.
    /// <see cref="Workflows.CaptionSource.Voiceover"/> (default) is today's behaviour and requires
    /// <see cref="EnableVoiceover"/>; <see cref="Workflows.CaptionSource.Transcript"/> cuts the cues from the
    /// <see cref="AnalysisStepOrder"/> step's transcript and needs no voiceover.
    /// </summary>
    [property: Description("Where burned captions take their text from: Voiceover (default, needs the narration) or Transcript (the words spoken in the footage, from the analysis step's transcript).")]
    [property: AppBindable(AppInputType.Choice, Label = "Caption text from")]
    CaptionSource CaptionSource = CaptionSource.Voiceover,
    /// <summary>
    /// <see cref="Workflows.CaptionSource.Transcript"/> only: the step whose <c>NarrationPlanOutput</c> carries the
    /// transcript segments translated into the brief's language (lines keyed by the segment id as <c>anchorId</c>).
    /// The cues keep the source timing and swap in the translated text; a segment the plan does not cover keeps its
    /// original words. Null (default) burns the transcript in its own language.
    /// </summary>
    [property: Description("Transcript captions only: the step whose narration plan holds the transcript translated into another language, keyed by segment id.")]
    int? CaptionTranslationStepOrder = null,
    /// <summary>
    /// Encode-once-after-review (docs/video-editing.md "Encoding once after the review"). Null (default) is today's
    /// compile: every pass encodes. <see cref="CompileEncodeStage.Plan"/> runs the whole plan (every fact the review
    /// reads) and stops before any encode; <see cref="CompileEncodeStage.Final"/> is the one compile that encodes, placed
    /// after the review. Both refuse a config whose result the review would read from the encoded file
    /// (<see cref="EncodeOnceBlockers"/>). <b>APPEND ONLY.</b>
    /// </summary>
    CompileEncodeStage? EncodeStage = null,
    // -- Pictures and videos over the film (initiative "picture-overlay"). Appended LAST after
    //    EncodeStage: positional record, every existing call site and stored payload keeps
    //    compiling/deserializing. All defaults (null/empty) are byte-identical. --
    /// <summary>
    /// Fixed pictures/videos a human or the assistant configured. Each entry that cannot resolve
    /// FAILS the compile with a named code (<c>MEDIA_OVERLAY_*</c>), never a silent skip.
    /// Null/empty (default) is byte-identical.
    /// </summary>
    IReadOnlyList<VideoMediaOverlay>? MediaOverlays = null,
    /// <summary>
    /// The step whose <c>MediaOverlayPlanOutput</c> to apply (an <c>OverlayPlanner</c> agent step).
    /// Only <c>From = Previous</c> or <c>From = Step</c> are valid, and the step must be earlier —
    /// the same rule as <see cref="GraphicsPlan"/>. Null (default) is byte-identical.
    /// </summary>
    ExtractInputRef? MediaOverlayPlan = null,
    /// <summary>Cap on resolved overlays from both sources together. Null = 8; the executor clamps 1..16.</summary>
    int? MaxMediaOverlays = null,
    /// <summary>
    /// Fraction of the canvas kept clear around a picture-in-picture inset (0..0.15). Null = 0.04.
    /// A card ignores it. Out of range is a config error.
    /// </summary>
    double? MediaOverlaySafeMargin = null,
    /// <summary>
    /// The user's binding choice for a card's sound (from the run brief's <c>overlayCardAudio</c>).
    /// Null (default) lets the planner choose per card; <see cref="MediaOverlayCardAudio.Keep"/> or
    /// <see cref="MediaOverlayCardAudio.Mute"/> overrides every card. Appended last; byte-identical
    /// when null.
    /// </summary>
    MediaOverlayCardAudio? MediaOverlayCardAudioDefault = null,
    /// <summary>
    /// Per-source camera log profile (log-footage-color): source index to a <see cref="ReelBolt.Shared.Editing.Color.CameraProfiles.Ids"/>
    /// value, or the word <c>auto</c> (apply only when the camera detector is High confidence, else none). The source is
    /// converted to Rec.709 at the start of its picture chain; an HDR (HLG/PQ) source ignores the profile and records a
    /// <c>camera_profile_ignored_hdr</c> warning. Appended last; byte-identical when null.
    /// </summary>
    IReadOnlyDictionary<int, string>? SourceInputProfiles = null,
    // -- Follow a tracked subject (initiative "follow-subject"). Appended LAST: null/empty (default) is
    //    byte-identical. Each entry names a track id of the analysis; the compile expands it into Reframes. --
    [property: Description("Follow tracked subjects: each entry names a track id (r{n} feature track, g{n} segmentation track) plus Tightness, Smoothness, OnLost words; the compile turns it into reframes. Capped at 8 entries; requires Mode=Reencode; a source may not also have hand-written Reframes.")]
    IReadOnlyList<FollowSubjectRequest>? FollowSubject = null,
    // -- Overlay legibility floor (docs/design/graphics-see-and-size.md). Appended LAST. --
    [property: Description("Smallest cap height overlay text may have, in percent of the output height (default 2.5, clamped 1..12). Raises drawtext overlays to it, is the floor a rendered graphic is measured against before it is accepted, and a graphic measured below it is replaced by its plain text.")]
    double MinOverlayCapHeightPct = OverlayTextSizing.DefaultMinCapHeightPct,
    // Optional video rate control. Null (default) is the plain CRF encode, byte-identical to before;
    // omitted from the stored JSON when null so existing step-cache keys do not move.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    TimelineRenderRateControl? RateControl = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    int? VideoBitrateKbps = null,
    // -- Moves on events (initiative "video-events-analysis"). Appended LAST; null/empty is byte-identical. --
    [property: Description("Camera moves on events: each entry names an event id (e{n}) plus Move, Intensity, Hold and Easing words; the compile resolves the event and expands it into reframes. Capped at 16 entries; requires Mode=Reencode.")]
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<EventMoveRequest>? EventMoves = null,
    // -- Flash on events (initiative "video-events-analysis"). Appended LAST; null/empty is byte-identical. --
    [property: Description("Flashes on events: each entry names an event id (e{n}) plus Colour, Strength and Length words; the compile resolves the event and draws a short tint on the program. Capped at 16 entries; requires Mode=Reencode.")]
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<FlashCueRequest>? FlashCues = null,
    // -- Speed ramps on events (initiative "video-events-analysis"). Appended LAST; null/empty is byte-identical. --
    [property: Description("Speed ramps on events: each entry names an event id (e{n}) plus Ramp (SlowMo, SpeedUp, FreezeHit, RampIn, RampOut), Strength and Span words; the compile retimes the finished program there (audio follows, captions and effects ride along). Capped at 8 entries; requires Mode=Reencode. Supersedes the reserved EnableSpanMotion.")]
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<SpeedRampRequest>? SpeedRamps = null,
    // -- Cut on an event (initiative "video-events-analysis"). Appended LAST; false (omitted from the JSON) is byte-identical. --
    [property: Description("Lets a Keep span begin or end on an offered event id (e{n}): the cut lands on the event's frame, or on the nearest silence gap within 0.3 s when speech covers the event. Requires Mode=Reencode.")]
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    [property: AppBindable(AppInputType.Toggle, Label = "Cut on the action")]
    bool CutOnEvents = false,
    // -- Motion on events (initiative "video-events-analysis"). Appended LAST; off / null (omitted from the JSON) is byte-identical. --
    [property: Description("Applies a MotionDirector plan: its cues become event moves, speed ramps, flashes and graphics on events (merged with any hand-written lists). Needs MotionPlan and Mode=Reencode. Default false.")]
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    [property: AppBindable(AppInputType.Toggle, Label = "Zooms and camera moves")]
    bool EnableMotion = false,
    [property: Description("Which step's resolved MotionPlanOutput to apply (a MotionDirector agent step). Only read when EnableMotion is true.")]
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ExtractInputRef? MotionPlan = null,
    [property: Description("Most motion cues applied from the plan (default 8, clamped 1..16); later cues are dropped with reason max_motion_cues. Only read when EnableMotion is true.")]
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    int? MaxMotionCues = null,
    // -- Speech-safe cuts (initiative "footage-edit-quality"). Appended LAST; true (default) moves a cut that lands inside a
    //    spoken word onto the word's edge, preferring a pause. --
    //    false keeps every cut exactly where the editor put it. --
    [property: Description("Moves a cut that would land inside a spoken word onto the word's edge, preferring a pause, so no cut interrupts speech. Default true; false keeps every cut exactly where the editor put it.")]
    bool SnapCutsToSpeech = true,
    // -- Dynamic edit (docs/design/dynamic-edit.md). Appended LAST: null (default) is byte-identical. --
    [property: Description("Dynamic edit: keeps offered shot boundaries as cut points and plans beat-aligned punch-ins, splits, alternations, flashes, speed ramps and whips from the music grid. Null = off. Requires Mode=Reencode.")]
    VideoDynamicsConfig? Dynamics = null,
    // -- Dynamics plan (docs/design/dynamic-edit.md "A late planner"). Appended LAST: null (default, omitted from the JSON) is byte-identical. --
    [property: Description("Which step's resolved DynamicsPlanOutput to apply (a DynamicsPlanner agent step, which sees the generated clip ids the story editor cannot). Its dynamics entries are merged with the story editor's own dynamics list; on an identical anchor and effect the plan's entry wins. Only read when Dynamics is enabled. Null = none.")]
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ExtractInputRef? DynamicsPlan = null)
{
    /// <summary><see cref="MinOverlayCapHeightPct"/> as the compile applies it: clamped to 1..12 (NaN reads as the default).</summary>
    public double ClampedMinOverlayCapHeightPct => OverlayTextSizing.ClampMinCap(MinOverlayCapHeightPct);

    /// <summary>
    /// The <c>MusicGainDb</c> range in dB. Mirrored by the engine's <c>MusicGainPolicy.MinDb</c>/<c>MaxDb</c>
    /// (the two agree by construction; <see cref="Validate"/> is the single check both the save path and
    /// the compile run).
    /// </summary>
    public const double MinMusicGainDb = -40;

    /// <summary>The upper bound of <see cref="MusicGainDb"/>; see <see cref="MinMusicGainDb"/>.</summary>
    public const double MaxMusicGainDb = 6;

    /// <summary>
    /// The config's own errors, one message each: pure config checks (no provider, no media, no
    /// artifact), so the save path can answer <c>400</c> with the other config errors and the compile
    /// re-checks the same list before any argv is built. Empty when the config is usable.
    /// <c>QA-P1-19</c> item 1 moved <c>MusicGainDb</c>'s range here from the executor's run-time check.
    /// </summary>
    public List<string> Validate()
    {
        List<string> errors = [];

        if (RateControl is { } rateControl)
        {
            if (!Enum.IsDefined(rateControl))
                errors.Add($"RateControl '{rateControl}' is not a known rate control.");
            else if (rateControl == TimelineRenderRateControl.Quality)
            {
                if (VideoBitrateKbps is not null)
                    errors.Add("VideoBitrateKbps can only be set when RateControl is Capped or Average.");
            }
            else if (VideoBitrateKbps is not { } kbps)
                errors.Add($"VideoBitrateKbps is required when RateControl is {rateControl}.");
            else if (kbps < TimelineRenderStepConfig.MinVideoBitrateKbps || kbps > TimelineRenderStepConfig.MaxVideoBitrateKbps)
                errors.Add($"VideoBitrateKbps must be between {TimelineRenderStepConfig.MinVideoBitrateKbps} and {TimelineRenderStepConfig.MaxVideoBitrateKbps}; got {kbps}.");
        }
        else if (VideoBitrateKbps is not null)
            errors.Add("VideoBitrateKbps needs a RateControl of Capped or Average.");

        if (MusicGainDb is { } gain && !(double.IsFinite(gain) && gain >= MinMusicGainDb && gain <= MaxMusicGainDb))
            errors.Add($"MusicGainDb must be between {MinMusicGainDb} and {MaxMusicGainDb} dB; got {gain}.");

        if (CaptionSource == CaptionSource.Transcript && !EnableCaptions)
            errors.Add("CaptionSource=Transcript only applies when EnableCaptions is true.");

        if (CaptionTranslationStepOrder is not null && CaptionSource != CaptionSource.Transcript)
            errors.Add("CaptionTranslationStepOrder only applies when CaptionSource is Transcript.");

        if (CaptionTranslationStepOrder is < 1)
            errors.Add("CaptionTranslationStepOrder must name a step (1 or higher).");

        if (EncodeStage is not null)
        {
            foreach (string blocker in EncodeOnceBlockers())
                errors.Add($"EncodeStage={EncodeStage} cannot be combined with {blocker}: the review reads its result from the encoded file. Turn EncodeStage off or remove it.");
        }

        // Pictures and videos over the film (docs/design/picture-overlay.md).
        if (MediaOverlays is { Count: > 0 } && Mode == VideoCompileMode.StreamCopy)
            errors.Add("MEDIA_OVERLAY_REQUIRES_REENCODE: MediaOverlays requires Mode=Reencode — the rgba overlay stage has no stream-copy equivalent.");

        if (MediaOverlayPlan is not null && Mode == VideoCompileMode.StreamCopy)
            errors.Add("MEDIA_OVERLAY_REQUIRES_REENCODE: MediaOverlayPlan requires Mode=Reencode — the rgba overlay stage has no stream-copy equivalent.");

        if (MediaOverlayPlan is { } overlayPlan &&
            overlayPlan.From is not (ExtractInputSource.Previous or ExtractInputSource.Step))
            errors.Add($"MEDIA_OVERLAY_PLAN_INVALID: MediaOverlayPlan.From must be Previous or Step; got '{overlayPlan.From}'.");

        double safeMargin = MediaOverlaySafeMargin ?? 0.04;
        if (!double.IsFinite(safeMargin) || safeMargin is < 0 or > 0.15)
            errors.Add($"MediaOverlaySafeMargin must be between 0 and 0.15; got {safeMargin}.");

        if (MediaOverlays is { } mediaOverlays)
        {
            int cap = Math.Clamp(MaxMediaOverlays ?? 8, 1, 16);
            if (mediaOverlays.Count > cap)
                errors.Add($"MediaOverlays has {mediaOverlays.Count} entries; MaxMediaOverlays is {cap}.");

            for (int i = 0; i < mediaOverlays.Count; i++)
            {
                VideoMediaOverlay? overlay = mediaOverlays[i];
                if (overlay is null)
                {
                    errors.Add($"MediaOverlays[{i}] is null.");
                    continue;
                }

                if (overlay.Content is null)
                {
                    errors.Add($"MediaOverlays[{i}].Content is required.");
                    continue;
                }

                if (overlay.Content.Validate() is { } refError)
                    errors.Add($"MEDIA_OVERLAY_REF_INVALID: MediaOverlays[{i}].Content {refError}");

                if (overlay.OpacityPercent is < 0 or > 100)
                    errors.Add($"MEDIA_OVERLAY_OPACITY_INVALID: MediaOverlays[{i}].OpacityPercent must be between 0 and 100; got {overlay.OpacityPercent}.");

                if (!IsValidOverlayMoment(overlay.StartSec) || !IsValidOverlayMoment(overlay.EndSec))
                {
                    errors.Add($"MEDIA_OVERLAY_SPAN_INVALID: MediaOverlays[{i}].StartSec/EndSec must be finite, between 0 and {VideoSourceRef.MaxWindowSec} seconds.");
                }
                else if (overlay.StartSec is { } start && overlay.EndSec is { } end && end <= start)
                {
                    errors.Add($"MEDIA_OVERLAY_SPAN_INVALID: MediaOverlays[{i}].EndSec ({end}) must be after StartSec ({start}).");
                }
            }
        }

        errors.AddRange(SourceInputProfileRules.Validate(SourceInputProfiles, nameof(SourceInputProfiles)));

        if (Dynamics is { Enabled: true } && Mode != VideoCompileMode.Reencode)
            errors.Add($"{VideoDynamicsConfig.RequiresReencodeCode}: Dynamics requires Mode=Reencode - retimes, flashes and reframes have no stream-copy equivalent.");

        static bool IsValidOverlayMoment(double? value) =>
            value is not { } moment || (double.IsFinite(moment) && moment >= 0 && moment <= VideoSourceRef.MaxWindowSec);

        return errors;
    }

    /// <summary>
    /// What makes encode-once unsafe for this config: a feature whose outcome (a placed graphic, a mixed sound, an
    /// insert, a generated clip) only exists in the encoded file, so the review would be judging a plan that cannot
    /// show it. Empty when every fact the review reads is computed before the encode.
    /// </summary>
    public IReadOnlyList<string> EncodeOnceBlockers()
    {
        List<string> blockers = [];
        if (EnableGraphics || GraphicsPlan is not null) blockers.Add("graphics");
        if (EnableInserts) blockers.Add("inserts");
        if (EnableMusic || MusicPlan is not null || MusicTrackProjectFileId is not null) blockers.Add("music");
        if (BeatSync != VideoBeatSync.Off) blockers.Add("beat sync");
        if (EnableVoiceover || EnablePickups) blockers.Add("voiceover");
        if (EnableGeneratedClips || GeneratedClips is not null) blockers.Add("generated clips");
        if (EnableSfx || SfxPlan is not null) blockers.Add("sound effects");
        if (EnableColorGrade || ColorGradePlan is not null) blockers.Add("colour grading");
        if (RegionEffects is { Count: > 0 }) blockers.Add("region effects");
        if (CensorLabels is { Count: > 0 }) blockers.Add("censoring");
        // Captions are burned by the encode and read by no review fact, so the one Final-stage encode may carry them (an app's
        // captions switch on an encode-once recipe, docs/design/app-inputs.md 27); the Plan stage still refuses them.
        if (EnableCaptions && EncodeStage != CompileEncodeStage.Final) blockers.Add("captions");
        // A planner-backed overlay set is resolved and reported by the Plan stage itself
        // (docs/design/picture-overlay.md §8.5), so the review can judge it without the encode. A fixed
        // human-configured entry still only exists in the encoded file.
        if (MediaOverlays is { Count: > 0 })
            blockers.Add("pictures and videos over the film");
        return blockers;
    }
}

/// <summary>
/// Where a <see cref="VideoCompileStepConfig"/> sits in an encode-once workflow (see
/// <see cref="VideoCompileStepConfig.EncodeStage"/>). <b>APPEND ONLY</b> — persisted by name.
/// </summary>
public enum CompileEncodeStage
{
    /// <summary>Plan only: resolves the cut and every review fact, writes the edit decision list, encodes nothing and is not charged.</summary>
    Plan,

    /// <summary>The one encode: placed after the review, it plans the same cut again from the same inputs and renders it.</summary>
    Final
}

/// <summary>Reads <see cref="VideoCompileStepConfig.EncodeStage"/> off a stored step without anything but the step.</summary>
public static class CompileEncodeStages
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>The stage the step's stored config names; null for any other step, a missing, unreadable or stage-less config.</summary>
    public static CompileEncodeStage? Of(ReelBolt.Shared.Data.Models.WorkflowStep step)
    {
        if (step.StepType != ReelBolt.Shared.Data.Models.StepType.VideoCompile || string.IsNullOrWhiteSpace(step.VideoCompileConfigJson))
            return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<VideoCompileStepConfig>(step.VideoCompileConfigJson, Options)?.EncodeStage;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>True for a compile that plans and never encodes: it takes no hold, records no usage and is never cached.</summary>
    public static bool IsPlanStage(ReelBolt.Shared.Data.Models.WorkflowStep step) => Of(step) == CompileEncodeStage.Plan;
}

/// <summary>Where burned captions take their text from (see <see cref="VideoCompileStepConfig.CaptionSource"/>). <b>APPEND ONLY</b> — persisted by name.</summary>
public enum CaptionSource
{
    /// <summary>Default: the words the Voiceover step synthesized, timed to the narration. Requires <c>EnableVoiceover</c>.</summary>
    Voiceover,
    /// <summary>The words spoken in the footage, from the analysis step's transcript, timed to the cut. Needs no voiceover.</summary>
    Transcript
}

/// <summary>
/// How a compile reaches <see cref="VideoCompileStepConfig.TargetDurationSec"/>. <b>APPEND ONLY</b> — persisted by name.
/// </summary>
public enum TargetDurationMode
{
    /// <summary>Default: shorten span tails, longest first; never drops a span.</summary>
    TrimEnds,

    /// <summary>
    /// Keep whole spans only: choose the subset whose total is closest to the target without exceeding it
    /// by more than 10%, in the editor's order, and never cut inside a span.
    /// </summary>
    WholeShots
}

/// <summary>
/// What the compile does with a generated plate's own sound (see <see cref="VideoCompileStepConfig.GeneratedClipAudio"/>).
/// <b>APPEND ONLY</b> — persisted by name.
/// </summary>
public enum GeneratedAudioMode
{
    /// <summary>Today's behaviour: the plate's sound plays as programme audio and counts as dialogue for ducking.</summary>
    Auto,
    /// <summary>The plate's sound plays as ambience and is NOT treated as dialogue, so the music is not ducked under it.</summary>
    Keep,
    /// <summary>The plate's own sound is dropped.</summary>
    Mute
}

/// <summary>A platform loudness the finished program is brought to (see <see cref="VideoCompileStepConfig.LoudnessTarget"/>). <b>APPEND ONLY</b> — persisted by name.</summary>
public enum ProgramLoudness
{
    /// <summary>Leave the mix's level as it comes out.</summary>
    Off,
    /// <summary>-14 LUFS integrated, -1.5 dBTP (AAC overshoots a -1 ceiling): social feeds and YouTube.</summary>
    Social,
    /// <summary>-16 LUFS integrated, -1.5 dBTP: websites and podcasts.</summary>
    Web,
    /// <summary>-23 LUFS integrated, -2 dBTP: broadcast (EBU R128).</summary>
    Broadcast
}

/// <summary>How a compile snaps its cuts to the music (see <see cref="VideoCompileStepConfig.BeatSync"/>). <b>APPEND ONLY</b> — persisted by name.</summary>
public enum VideoBeatSync
{
    /// <summary>Cuts fall where the edit puts them.</summary>
    Off,
    /// <summary>Every cut lands on a beat.</summary>
    Beat,
    /// <summary>Every cut lands on a bar line (four beats), falling back to a beat when no bar line fits.</summary>
    Bar
}

/// <summary>
/// One deterministic insert-content assignment (see <see cref="VideoCompileStepConfig.InsertContents"/>).
/// </summary>
/// <param name="ProjectFileId">A <c>video/*</c> or <c>image/*</c> file of this project.</param>
/// <param name="SourceIndex">Only plates tracked in this analysed source clip (0-based, the order of the analyze step's sources); null = any.</param>
/// <param name="RegionId">Only this tracked region (<c>r{n}</c>, as the analyze step offered it); null = any.</param>
/// <param name="Fit">This content's fit; null uses <see cref="VideoCompileStepConfig.InsertFit"/>.</param>
/// <param name="InsertContentRef">T-106: names the content by reference instead (a prior step or step result); when set it takes the place of <paramref name="ProjectFileId"/>, which may then be left empty.</param>
public sealed record VideoInsertContentAssignment(
    Guid ProjectFileId,
    int? SourceIndex = null,
    string? RegionId = null,
    InsertContentFit? Fit = null,
    InsertContentRef? InsertContentRef = null);

/// <summary>
/// T-106: a reference to insert content that is not (yet) a project file. <c>{"from":"Step","stepOrder":3}</c> is the
/// output of an earlier step of this run, <c>{"from":"StepResult","stepResultId":"…"}</c> the output of a step result
/// of an earlier run of this project, and <c>{"from":"ProjectFile","projectFileId":"…"}</c> a project file, as
/// <see cref="VideoCompileStepConfig.InsertContentProjectFileId"/> always did. Video or image only.
/// </summary>
/// <param name="From"><see cref="ProjectFileKind"/>, <see cref="StepKind"/> or <see cref="StepResultKind"/> (case-insensitive).</param>
/// <param name="ProjectFileId">From=ProjectFile only.</param>
/// <param name="StepOrder">From=Step only: an earlier step of this run.</param>
/// <param name="StepResultId">From=StepResult only: a step result of this project.</param>
public sealed record InsertContentRef(
    string From,
    Guid? ProjectFileId = null,
    int? StepOrder = null,
    Guid? StepResultId = null)
{
    public const string ProjectFileKind = "ProjectFile";
    public const string StepKind = "Step";
    public const string StepResultKind = "StepResult";

    /// <summary>The canonical spelling of <see cref="From"/>, or null when it is not one of the three kinds.</summary>
    public string? Kind =>
        string.Equals(From?.Trim(), ProjectFileKind, StringComparison.OrdinalIgnoreCase) ? ProjectFileKind :
        string.Equals(From?.Trim(), StepKind, StringComparison.OrdinalIgnoreCase) ? StepKind :
        string.Equals(From?.Trim(), StepResultKind, StringComparison.OrdinalIgnoreCase) ? StepResultKind :
        null;

    /// <summary>The reason this reference is malformed (exactly its own kind's field, and no other), or null.</summary>
    public string? Validate()
    {
        int set = (ProjectFileId.HasValue ? 1 : 0) + (StepOrder.HasValue ? 1 : 0) + (StepResultId.HasValue ? 1 : 0);
        switch (Kind)
        {
            case ProjectFileKind:
                return ProjectFileId is { } f && f != Guid.Empty && set == 1
                    ? null : "insertContentRef from=ProjectFile requires projectFileId and nothing else.";
            case StepKind:
                return StepOrder is > 0 && set == 1
                    ? null : "insertContentRef from=Step requires a positive stepOrder and nothing else.";
            case StepResultKind:
                return StepResultId is { } r && r != Guid.Empty && set == 1
                    ? null : "insertContentRef from=StepResult requires stepResultId and nothing else.";
            default:
                return "insertContentRef from must be ProjectFile, Step or StepResult.";
        }
    }
}

/// <summary>
/// One configured picture or video composited over the film (initiative "picture-overlay"; see
/// docs/design/picture-overlay.md). <b>APPEND ONLY</b> — new members go last.
/// </summary>
/// <param name="Content">The picture or video by reference. Images: png/jpg/jpeg/webp; videos: mp4/m4v/mov/webm.</param>
/// <param name="Form">A full-frame card, or a picture-in-picture inset.</param>
/// <param name="Anchor">Where an inset sits inside the safe area; ignored by a card.</param>
/// <param name="Size">Inset width as a fraction of the safe width; ignored by a card.</param>
/// <param name="OpacityPercent">0..100. 0 is not drawn and is reported.</param>
/// <param name="Border">An inset's hairline border; ignored by a card.</param>
/// <param name="StartSec">Output-timeline start in seconds; null = the first frame of the film.</param>
/// <param name="EndSec">Output-timeline end in seconds; null = the last frame of the film.</param>
/// <param name="Loop">A video shorter than the span loops, or stops at its own end.</param>
/// <param name="Audio">A video's own sound: muted, or mixed at <paramref name="Volume"/>.</param>
/// <param name="Volume">Mixed audio only: Quiet (-12 dB), Balanced (-6 dB), Prominent (0 dB).</param>
/// <param name="CardAudio">A card's sound: the film keeps playing, or is muted under the card.</param>
public sealed record VideoMediaOverlay(
    InsertContentRef Content,
    MediaOverlayForm Form = MediaOverlayForm.PictureInPicture,
    MediaOverlayAnchor Anchor = MediaOverlayAnchor.BottomRight,
    MediaOverlaySize Size = MediaOverlaySize.Medium,
    int OpacityPercent = 100,
    MediaOverlayBorder Border = MediaOverlayBorder.None,
    double? StartSec = null,
    double? EndSec = null,
    MediaOverlayLoop Loop = MediaOverlayLoop.Loop,
    MediaOverlayAudio Audio = MediaOverlayAudio.Muted,
    MediaOverlayVolume Volume = MediaOverlayVolume.Balanced,
    MediaOverlayCardAudio CardAudio = MediaOverlayCardAudio.Keep);

/// <summary>How a picture or video sits over the film. <b>APPEND ONLY</b> — persisted by name.</summary>
public enum MediaOverlayForm
{
    /// <summary>An inset over the film, anchored inside the safe area.</summary>
    PictureInPicture,
    /// <summary>The picture fills the frame for the span (a testimonial, document or price table).</summary>
    Card
}

/// <summary>Where an inset sits inside the safe area. <b>APPEND ONLY</b> — persisted by name.</summary>
public enum MediaOverlayAnchor
{
    TopLeft, Top, TopRight,
    Left, Center, Right,
    BottomLeft, Bottom, BottomRight
}

/// <summary>An inset's width as a fraction of the safe width. <b>APPEND ONLY</b> — persisted by name.</summary>
public enum MediaOverlaySize
{
    /// <summary>0.25 of the safe width — a logo or badge.</summary>
    Small,
    /// <summary>0.36 of the safe width — a product clip or second angle.</summary>
    Medium,
    /// <summary>0.50 of the safe width — a split-screen look.</summary>
    Large
}

/// <summary>An inset's hairline border. <b>APPEND ONLY</b> — persisted by name.</summary>
public enum MediaOverlayBorder
{
    None,
    /// <summary>A white hairline.</summary>
    Light,
    /// <summary>A black hairline.</summary>
    Dark
}

/// <summary>What a video overlay does when its own content is shorter than its span. <b>APPEND ONLY</b>.</summary>
public enum MediaOverlayLoop
{
    /// <summary>Loop the content to fill the span (the default).</summary>
    Loop,
    /// <summary>Play once; the film shows through after the content ends.</summary>
    PlayOnce
}

/// <summary>A video overlay's own sound. <b>APPEND ONLY</b> — persisted by name.</summary>
public enum MediaOverlayAudio
{
    /// <summary>No overlay audio (the default).</summary>
    Muted,
    /// <summary>Mix the overlay's sound at <see cref="VideoMediaOverlay.Volume"/>.</summary>
    Mixed
}

/// <summary>A mixed overlay's bounded level. <b>APPEND ONLY</b> — persisted by name.</summary>
public enum MediaOverlayVolume
{
    /// <summary>-12 dB: background.</summary>
    Quiet,
    /// <summary>-6 dB: present but under the film's voice (the default).</summary>
    Balanced,
    /// <summary>0 dB: the clip's own sound is the point.</summary>
    Prominent
}

/// <summary>What a full-frame card does with the film's own sound. <b>APPEND ONLY</b> — persisted by name.</summary>
public enum MediaOverlayCardAudio
{
    /// <summary>The film's sound continues under the card (the default).</summary>
    Keep,
    /// <summary>The film's sound ramps to silence for the card span, then back.</summary>
    Mute
}

/// <summary>
/// How near-black dips in insert content are handled (see <see cref="VideoCompileStepConfig.InsertContentDips"/>).
/// <b>APPEND ONLY</b> — persisted by name.
/// </summary>
public enum InsertContentDipMode
{
    /// <summary>Hold the last good frame across each dip (the content keeps its length and timing).</summary>
    Freeze,
    /// <summary>Remove each dip (the content gets shorter).</summary>
    Cut,
    /// <summary>Leave the content as it is; nothing is detected.</summary>
    Keep
}

/// <summary>
/// The order a compile plays its kept spans in (see <see cref="VideoCompileStepConfig.KeepOrder"/>).
/// <b>APPEND ONLY</b> — persisted by name in <c>video_compile_config_json</c>.
/// </summary>
public enum VideoKeepOrder
{
    /// <summary>Spans of one clip play in source order; different clips in the listed order.</summary>
    SourceOrder,
    /// <summary>Every span plays in exactly the order the editor listed it.</summary>
    AsListed
}

/// <summary>
/// The program's output format / target platform. <b>APPEND ONLY</b> — persisted by name in
/// <c>video_compile_config_json</c>.
/// </summary>
public enum VideoOutputFormat
{
    /// <summary>Canvas sized from the sources (the largest analysed source resolution).</summary>
    Source,
    /// <summary>9:16 vertical, 1080x1920 — Reels, TikTok, Shorts.</summary>
    Vertical,
    /// <summary>1:1 square, 1080x1080 — feed posts.</summary>
    Square,
    /// <summary>16:9 landscape, 1920x1080 — YouTube, websites.</summary>
    Landscape,
    /// <summary>4:5 portrait, 1080x1350 — portrait feed posts (Instagram, Facebook, LinkedIn).</summary>
    Portrait
}

/// <summary>How a clip is fitted into a fixed output format. <b>APPEND ONLY</b> — persisted by name.</summary>
public enum VideoOutputFit
{
    /// <summary>Fill the frame, cropping the overflow equally from both sides (centre crop).</summary>
    Crop,
    /// <summary>Fit the whole clip, filling the rest per <see cref="VideoCompileStepConfig.CanvasFill"/>.</summary>
    Pad,
    /// <summary>
    /// Fill the frame like <see cref="Crop"/>, but place each clip's crop on its subject instead of
    /// its centre: a tracked screen when the clip has one, else the subject the analyze step
    /// located (<c>LocateSubjects</c>), else the centre.
    /// </summary>
    Subject
}

/// <summary>How the canvas around an aspect-mismatched clip is filled. Appended-only: persisted by name.</summary>
public enum VideoCanvasFill
{
    Black,
    Blur
}

/// <summary>
/// How burned captions are laid out (see docs/voiceover.md "Captions"). <b>APPEND ONLY</b> — the
/// member is persisted by name in <c>video_compile_config_json</c>.
/// </summary>
public enum CaptionStyle
{
    /// <summary>One or two lines of text per cue, split at word boundaries, timed from the first to the last word of the cue (or proportionally when no word timings exist).</summary>
    Subtitle,
    /// <summary>Like Subtitle, but the words already spoken are drawn in the highlight colour as each word begins — needs word timings.</summary>
    Karaoke,
    /// <summary>One word at a time, larger and centred — the short-form social style. Needs word timings.</summary>
    Punch,
    /// <summary>
    /// The Reels/TikTok caption look: big bold words, a few at a time (up to three words), white with
    /// a heavy outline and no box, no karaoke highlight, sitting above the platform's own buttons on
    /// a portrait canvas. Uses word timings when present, else shares the line by length.
    /// </summary>
    Bold
}

/// <summary>
/// The burned-caption typeface (see docs/voiceover.md "Captions"). <b>APPEND ONLY</b> — persisted
/// by name. Every member other than <see cref="Auto"/> names a font file bundled in the
/// workflow-engine image (<c>inference/fonts/captions</c>, SIL Open Font License), or the image's
/// legacy DejaVu Sans; a closed list so a config can never name an arbitrary font path.
/// </summary>
public enum CaptionFont
{
    /// <summary>The caption style's own font: Inter for Subtitle, Montserrat for Karaoke, Anton for Punch.</summary>
    Auto,
    /// <summary>Inter Bold — a clean, very legible text face.</summary>
    Inter,
    /// <summary>Montserrat Bold — a wide geometric face with presence.</summary>
    Montserrat,
    /// <summary>Poppins Bold — a rounded geometric face.</summary>
    Poppins,
    /// <summary>Bebas Neue — tall, condensed, all capitals.</summary>
    BebasNeue,
    /// <summary>Anton — a heavy condensed display face, the classic short-form look.</summary>
    Anton,
    /// <summary>DejaVu Sans Bold — the font captions used before the bundled set existed.</summary>
    DejaVuSans
}

/// <summary>Where burned captions sit in the frame (see docs/voiceover.md "Captions"). <b>APPEND ONLY</b> — persisted by name.</summary>
public enum CaptionPosition
{
    /// <summary>The style's default: lower third for Subtitle and Karaoke, centre for Punch.</summary>
    Auto,
    /// <summary>Lower third, above the title-safe bottom margin.</summary>
    Bottom,
    /// <summary>Vertically centred.</summary>
    Center,
    /// <summary>Upper third, below the title-safe top margin.</summary>
    Top
}

/// <summary>
/// What the compile step does to the source dialogue under an applied voiceover line's window
/// (see docs/voiceover.md "Pickups and dubs"). <b>APPEND ONLY</b> — persisted by name.
/// </summary>
public enum VoiceoverMode
{
    /// <summary>Narration on top of the program; the source dialogue is untouched (phase 1's mix, byte-identical).</summary>
    OverMusic,
    /// <summary>The source dialogue is attenuated by <see cref="VideoCompileStepConfig.VoiceoverDuckDb"/> under each line.</summary>
    DuckDialogue,
    /// <summary>The source dialogue is muted under each line — the basis for dubs.</summary>
    ReplaceDialogue
}

/// <summary>How <see cref="VideoCompileStepConfig.CensorLabels"/> obscures a region.</summary>
public enum CensorStyle
{
    /// <summary>Strong Gaussian blur.</summary>
    Blur,
    /// <summary>Coarse mosaic blocks.</summary>
    Pixelate,
    /// <summary>Solid black — the only style that leaves nothing of the original to recover.</summary>
    Fill
}

/// <summary>
/// How a screen insert's content is shaped to the tracked plate before it is warped onto it. The
/// warp maps the content's full frame onto the plate's four corners, so content of a different
/// aspect is otherwise squashed or stretched to fit.
/// </summary>
public enum InsertContentFit
{
    /// <summary>
    /// <see cref="Stretch"/> for content an agent rendered for the plate (byte-identical to the
    /// pre-fit behaviour) and for a project file whose aspect is within 15% of the plate's;
    /// otherwise <see cref="Contain"/>.
    /// </summary>
    Auto,
    /// <summary>Map the whole content frame onto the plate, distorting its aspect if they differ.</summary>
    Stretch,
    /// <summary>Fit the whole content inside the plate, black bars filling the rest — a landscape video playing on a portrait phone.</summary>
    Contain,
    /// <summary>Fill the plate, cropping the content's overflow equally from both sides.</summary>
    Cover
}

/// <summary>
/// One effect applied to just the part of the picture a segmentation mask covers — the "E" of
/// "apply effect E to object P". <b>APPEND ONLY</b> — persisted by name in
/// <c>video_compile_config_json</c>, so the member order is documentation, not format.
/// </summary>
/// <remarks>
/// The mask comes from an analyze step's <c>SegmentationTracks</c> and is rendered by first-party
/// ffmpeg argv only: the enum picks a constant filter graph, and the numeric knobs are clamped and
/// formatted invariantly. No value of this enum ever becomes a string in an argv, and
/// <see cref="VideoRegionEffect.Target"/> is only ever a lookup key — never interpolated into a
/// filter, a file name or a shell.
/// </remarks>
public enum RegionEffect
{
    /// <summary>Gaussian blur inside the mask. Strength scales sigma (1.5% of the frame's short side at 1.0).</summary>
    Blur,

    /// <summary>Mosaic blocks inside the mask. Strength scales the block size (2% of the short side at 1.0).</summary>
    Pixelate,

    /// <summary>Brighten inside the mask and dim outside it — draws the eye to the object without hiding it.</summary>
    Highlight,

    /// <summary>Dim everything OUTSIDE the mask and leave the object untouched.</summary>
    Spotlight,

    /// <summary>Keep colour inside the mask, desaturate everything outside it.</summary>
    ColorPop,

    /// <summary>Draw a band on the mask's own edge (a dilated mask minus the mask), inside the mask's silhouette.</summary>
    Outline,

    /// <summary>Brighten inside the mask with a punchy flash look (brightness and a mild contrast lift).</summary>
    Flash,

    /// <summary>A soft bloom inside the mask: a blurred, brightened and slightly more saturated copy.</summary>
    Glow
}

/// <summary>
/// One "apply effect E to object P" entry of <see cref="VideoCompileStepConfig.RegionEffects"/>.
/// <b>APPEND ONLY</b> — positional record, so a new knob goes LAST and a stored entry without it keeps
/// deserializing. Every bound stated below is enforced by <see cref="RegionEffectRules.Validate"/>,
/// which returns a specific refusal code per rule.
/// </summary>
/// <param name="Target">
/// Which segmentation track to apply to: a <c>g{n}</c> id, or the prompt label the track was created
/// under (case-insensitive). Non-blank, at most <see cref="RegionEffectRules.MaxTargetChars"/>
/// characters (<c>EFFECT_TARGET_INVALID</c> otherwise). A name that matches no track of the analyze
/// step's artifact degrades the entry with <c>region_effect_target_unmatched</c>; two tracks sharing a
/// label make the entry ambiguous and it is refused as <c>EFFECT_TARGET_AMBIGUOUS</c> — never resolved
/// by guessing.
/// </param>
/// <param name="Effect">Which effect. Must be a DEFINED member of the enum (<c>EFFECT_INVALID</c> otherwise — a stored payload naming a member this build does not have).</param>
/// <param name="Invert">
/// Apply the effect to everything EXCEPT the object (the mask is negated before it is feathered).
/// Exact by construction: the complement of the mask is the same mask negated, so "invert" never
/// approximates a background by growing the foreground.
/// </param>
/// <param name="Strength">How strong the effect is, 0..2 (<c>EFFECT_STRENGTH_OUT_OF_RANGE</c> outside). Each effect maps it onto its own knob; 1.0 is the effect's designed default and 0 is a no-op.</param>
/// <param name="FeatherPx">Softening of the mask's edge in pixels AT OUTPUT RESOLUTION, 0..64 (<c>EFFECT_FEATHER_OUT_OF_RANGE</c> outside). 0 is a crisp edge; the blur is applied to the mask, never to the picture.</param>
/// <param name="Opacity">The mask's alpha, 0..1 (<c>EFFECT_OPACITY_OUT_OF_RANGE</c> outside): 1 (default) applies the effect at full strength inside the mask, 0 applies nothing at all.</param>
public sealed record VideoRegionEffect(
    // The object the effect follows, in plain words; it must match a track's label. A recipe binds the same text input
    // into this and the analyze step's trackObjects[].label, so the two always agree (docs/design/app-inputs.md 22).
    [property: AppBindable(AppInputType.ShortText, Label = "What to hide", MaxLength = RegionEffectRules.MaxTargetChars)]
    string Target,
    // Which effect, as a whole value a choice may set (appInputs key regionEffects[0].effect, docs/design/app-inputs.md 27).
    [property: AppBindable(AppInputType.Choice, Label = "Effect")]
    RegionEffect Effect,
    bool Invert = false,
    double Strength = 1.0,
    double FeatherPx = 0,
    double Opacity = 1.0,
    // Optional time window, as event ids of the analysis (e{n}); null on both = the whole track, byte-identical to before.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? FromEventId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? ToEventId = null);

/// <summary>The one check of a per-source camera profile map, shared by VideoCompile and VideoAnalyze.</summary>
public static class SourceInputProfileRules
{
    /// <summary>The most entries a map may carry (a compile reads a handful of sources).</summary>
    public const int MaxEntries = 64;

    public static List<string> Validate(IReadOnlyDictionary<int, string>? profiles, string fieldName)
    {
        List<string> errors = [];
        if (profiles is null) return errors;

        if (profiles.Count > MaxEntries)
            errors.Add($"{fieldName} has {profiles.Count} entries; the most allowed is {MaxEntries}.");

        foreach ((int sourceIndex, string? profile) in profiles.OrderBy(p => p.Key).Take(MaxEntries))
        {
            if (sourceIndex < 0)
                errors.Add($"{fieldName}: source index {sourceIndex} must be 0 or higher.");

            if (profile is null || !(profile == "auto" || ReelBolt.Shared.Editing.Color.CameraProfiles.IsKnown(profile)))
                errors.Add($"{fieldName}[{sourceIndex}] must be 'auto' or one of: {string.Join(", ", ReelBolt.Shared.Editing.Color.CameraProfiles.Ids)}.");
        }

        return errors;
    }
}
