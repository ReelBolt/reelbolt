using System.Text.Json.Serialization;

namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// Status of a project through its lifecycle.
/// </summary>
public enum ProjectStatus
{
    Draft,
    Processing,
    Complete,
    Failed
}

/// <summary>
/// Status of a file's AI-generated summary.
/// </summary>
public enum SummaryStatus
{
    Pending,
    Processing,
    Done,
    Failed
}

/// <summary>
/// Status of semantic/vector indexing for a project file.
/// </summary>
public enum FileIndexingStatus
{
    NotIndexed,
    Pending,
    Processing,
    Indexed,
    Failed
}

/// <summary>
/// Status of content moderation for an ingested project file.
/// </summary>
/// <remarks>
/// <see cref="Unmoderated"/> is the only state the worker claims (it is idempotent); <see cref="Error"/>
/// means the content could not be classified and the file stays usable (fail open); <see cref="Quarantined"/>
/// means a high-confidence block moved the object out of the served prefix and every read path refuses it.
/// </remarks>
public enum FileModerationStatus
{
    Unmoderated,
    Scanning,
    Clean,
    Quarantined,
    Error
}

/// <summary>
/// Status of a workflow execution.
/// </summary>
public enum ExecutionStatus
{
    Queued,
    Running,
    Passed,
    Failed,
    Cancelled
}

/// <summary>
/// Type of agent available in the system.
/// </summary>
public enum AgentType
{
    CodeStructureAnalyzer,
    DependencyAnalyzer,
    ComponentInventoryAnalyzer,
    RouteAndApiAnalyzer,
    StyleAndThemeExtractor,
    RemotionComponentTranslator,
    AnimationStrategyAgent,
    DirectorAgent,
    ScriptwriterAgent,
    AuthorAgent,
    ReviewAgent,
    FileSummarizerAgent,
    Custom,
    /// <summary>
    /// Deterministic, non-LLM data extraction and projection built-in agent used by
    /// StepType.Extract steps. Never sent to a model.
    /// </summary>
    ExtractTransform,
    /// <summary>
    /// LLM agent that decides which shots/silence gaps/transcript spans to KEEP from a bounded,
    /// id-anchored view of a video analysis (StepType.VideoAnalyze output). Never emits a
    /// timestamp — see ReelBolt.Shared.Data.OutputSchemas.VideoEditDecisionOutput.
    /// </summary>
    VideoStoryEditor,
    /// <summary>
    /// Deterministic, non-LLM video derushing, cutting, and generation. Runs ffmpeg or calls
    /// an external video-generation provider, never a model itself.
    /// </summary>
    VideoTransform,
    /// <summary>
    /// LLM agent that plans zero or more motion-graphics overlays (lower-thirds, titles,
    /// callouts) anchored ONLY to opaque placement ids offered by a StepType.VideoAnalyze step
    /// (Phase 3). Never emits a coordinate or a timestamp — see
    /// ReelBolt.Shared.Data.OutputSchemas.MotionGraphicsPlanOutput.
    /// </summary>
    MotionGraphicsPlanner,
    /// <summary>
    /// LLM agent used by a StepType.ReviewLoop step in the video-editing templates
    /// (video-derush-edit / video-derush-edit-graphics) to score the compiled edit and, when the
    /// score is below MinScore, loop execution back to an earlier VideoStoryEditor/
    /// MotionGraphicsPlanner step with concrete feedback. Reviews deterministic facts already
    /// computed by VideoCompileStepExecutor (transcript sentence-boundary check, overlay frame
    /// coverage) rather than judging code/lint quality like AgentType.ReviewAgent — see
    /// ReelBolt.Shared.Data.OutputSchemas.VideoReviewOutput and docs/video-editing.md.
    /// </summary>
    VideoReviewAgent,
    /// <summary>
    /// LLM agent that picks a single background-music track (an offered "m{n}" id, drawn from a
    /// StepType.VideoAnalyze step's OfferMusicTracks-derived candidate list) plus enum-word
    /// choices for intensity/ducking/fit for the video-editing pipeline's optional background
    /// music (see docs/video-editing.md "Background music"). Never emits a dB value, level, or
    /// timestamp - see ReelBolt.Shared.Data.OutputSchemas.MusicPlanOutput. The deterministic
    /// alternative (VideoCompileStepConfig.MusicTrackProjectFileId, set directly by the workflow
    /// author) does not require this agent at all.
    /// </summary>
    MusicSupervisor,
    /// <summary>
    /// LLM agent used TWICE by a StepType.EditRoom step: once per-turn as the moderator
    /// participant in the room's live Microsoft.Agents.AI.Workflows group chat (free-form prose,
    /// emits the literal sentinel "ROOM_DECIDED" once satisfied), and once more, OUTSIDE the group
    /// chat, for a single ordinary structured-output synthesis call that converts the room's
    /// discussion into ONE schema-validated VideoEditDecisionOutput — the exact same schema
    /// AgentType.VideoStoryEditor emits, and reused verbatim: same rushcut invariant (never a
    /// timestamp, only offered ids). See docs/video-editing.md "The edit room".
    /// </summary>
    VideoEditDirector,
    /// <summary>
    /// LLM agent used TWICE by a StepType.GraphicsRoom step — the motion-graphics analogue of
    /// AgentType.VideoEditDirector: once per-turn as the lead-artist moderator participant in the
    /// room's live group chat (free-form prose, emits the literal sentinel "ROOM_DECIDED" once
    /// satisfied), and once more, OUTSIDE the group chat, for a single ordinary structured-output
    /// synthesis call that converts the room's discussion into ONE schema-validated
    /// MotionGraphicsPlanOutput — the exact same schema AgentType.MotionGraphicsPlanner emits,
    /// reused verbatim: same extended rushcut invariant (never a timestamp OR a pixel coordinate,
    /// only offered placement ids). Unlike VideoEditDirector, this agent's synthesis call carries
    /// the same sandbox+Remotion+render tool set as MotionGraphicsPlanner (minus WriteProjectFile)
    /// so a synthesized overlay can be backed by a real rendered transparent asset — see
    /// ToolGroupCatalog's rationale. Room-participant turns are tool-restricted to read-only by
    /// GraphicsRoomStepExecutor regardless. See docs/video-editing.md "The graphics room".
    /// </summary>
    MotionGraphicsDirector,
    /// <summary>
    /// LLM agent that picks ONE whole-program colour-grade treatment for a compiled edit as
    /// enum WORDS only (a named look plus strength/shadow/highlight words — see
    /// ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput). Never emits an RGB value, a
    /// curve/gamma/gain number, a percentage, or a timestamp — every property is a plain string
    /// (guarded by ColorGradePlanOutputInvariantTests), and VideoCompileStepExecutor alone
    /// resolves the words to concrete ffmpeg eq/colorbalance/colorlevels/hue parameters from
    /// first-party tables (ColorGradeFilterBuilder). Deliberates over the same bounded
    /// VideoAnalyze view (measured per-shot colour temperature/tone/saturation words) the story
    /// editor sees. See docs/video-editing.md "Color grading".
    /// </summary>
    Colorist,
    /// <summary>
    /// LLM agent used TWICE by a StepType.ColorGradeRoom step — the colour-grading analogue of
    /// AgentType.VideoEditDirector: once per-turn as the supervising-colorist moderator
    /// participant in the room's live group chat (free-form prose, emits the literal sentinel
    /// "ROOM_DECIDED" once satisfied), and once more, OUTSIDE the group chat, for a single
    /// ordinary structured-output synthesis call that converts the room's discussion into ONE
    /// schema-validated ColorGradePlanOutput — the exact same schema AgentType.Colorist emits,
    /// reused verbatim: same words-only invariant (never a numeric colour value). Same minimal
    /// read-only tool scope as VideoEditDirector — unlike MotionGraphicsDirector, nothing about
    /// a grade ever needs rendering. See docs/video-editing.md "The color grade room".
    /// </summary>
    ColorGradeDirector,
    /// <summary>
    /// LLM agent that plans zero or more discrete sound-effect cues (whooshes, clicks, dings,
    /// stingers) for a compiled edit — each cue an offered "x{n}" SFX-clip id (drawn from a
    /// StepType.VideoAnalyze step's OfferSfxClips-derived candidate list) anchored to an offered
    /// cut-anchor id ("s{n}"/"g{n}"/"t{n}"), plus enum-word Timing/Volume choices — see
    /// ReelBolt.Shared.Data.OutputSchemas.SfxPlanOutput and docs/video-editing.md
    /// "Sound effects". Never emits a timestamp, a dB value, or a duration (guarded by
    /// SfxPlanOutputInvariantTests); VideoCompileStepExecutor alone resolves each anchor id to an
    /// output-timeline moment and each word to a concrete gain/offset. Unlike background music,
    /// there is deliberately NO deterministic no-agent config path — cue placement is inherently
    /// editorial (see docs/video-editing.md "Sound effects" for the rejected alternative).
    /// </summary>
    SoundDesigner,
    /// <summary>
    /// Conversational workflow assistant agent providing free-form chat with read-only workflow
    /// management capabilities (list agents, validate, propose, apply, execute workflows).
    /// Never emits structured output — see Inference.Api.Agents.AssistantAgent.
    /// </summary>
    Assistant,
    /// <summary>
    /// LLM agent that plans zero or more GENERATED shots (b-roll cutaways, a cold open, an end
    /// card, screen content) from a bounded, id-anchored StepType.VideoAnalyze view — each shot
    /// an offered cut-anchor id ("s{n}"/"g{n}"/"t{n}") for its placement, optional offered "s{n}"
    /// keyframes, enum-word purpose/camera/duration choices, and free-prose prompt text — see
    /// ReelBolt.Shared.Data.OutputSchemas.GeneratedShotPlanOutput and docs/video-generation.md
    /// "Planning the shots". Never emits a timestamp, a frame count, a duration in seconds, a
    /// resolution, or a URL (guarded by GeneratedShotPlanOutputInvariantTests); the deterministic
    /// StepType.VideoGenerate step alone resolves the enum words to concrete provider parameters
    /// and is the only component that can spend money. Same minimal read-only tool scope as
    /// VideoStoryEditor/MusicSupervisor/SoundDesigner — ProjectRead + FailWorkflow only, with
    /// explicitly NO sandbox, NO render, and NO tool that can spend.
    /// </summary>
    ShotDirector,
    /// <summary>
    /// LLM agent that writes the words a voice speaks over the compiled edit (see
    /// docs/voiceover.md) — each line an offered cut-anchor id ("s{n}"/"g{n}"/"t{n}") plus the
    /// line's own prose and a prose delivery direction, never a number — see
    /// ReelBolt.Shared.Data.OutputSchemas.NarrationPlanOutput. Replaces the hand-written
    /// "Inline" narration script phase 1 shipped. The rushcut invariant, extended: every
    /// property is a plain string AND no property is even NAMED for a time (guarded by
    /// NarrationPlanOutputInvariantTests), so it is structurally incapable of emitting a
    /// timestamp, an offset, a duration, a frame number, a coordinate, or a percentage. A
    /// separate deterministic step measures how long each synthesized line actually runs against
    /// the shot it is anchored to and feeds any overflow back through the review loop; the model
    /// never counts. Same minimal read-only tool scope as
    /// VideoStoryEditor/MusicSupervisor/SoundDesigner/ShotDirector — ProjectRead + FailWorkflow
    /// only, with explicitly NO sandbox, NO render, and NO tool that can spend.
    /// </summary>
    NarrationWriter,
    /// <summary>
    /// LLM agent that plans PICKUPS for a talking-head edit (see docs/voiceover.md "Pickups"):
    /// each a corrected sentence anchored to ONE offered transcript-segment id ("t{n}") that the
    /// presenter flubbed, re-recorded by the deterministic Voiceover step in the presenter's own
    /// consented, cloned voice and spliced by the compile step over exactly that segment's window
    /// — see ReelBolt.Shared.Data.OutputSchemas.PickupPlanOutput. A SEPARATE schema from
    /// VideoEditDecisionOutput on purpose (a repair is temporal; the rushcut invariant stays
    /// untouched), with its own invariant test (PickupPlanOutputInvariantTests): every property a
    /// plain string, none named for a time. Same minimal read-only tool scope as NarrationWriter.
    /// </summary>
    PickupPlanner,
    /// <summary>
    /// LLM agent that translates a NarrationPlanOutput into another language for a localized dub
    /// (see docs/voiceover.md "Dubs"): same lines, same anchor ids, translated prose — emitting
    /// the SAME NarrationPlanOutput schema it reads, so a NarrationPlan-sourced Voiceover step
    /// consumes the translation unchanged. Same minimal read-only tool scope as NarrationWriter.
    /// </summary>
    NarrationTranslator,
    /// <summary>
    /// LLM agent that revises a finished Remotion composition in place (see
    /// docs/design/composition-revisions.md): given a restored workspace, a plain-language change
    /// request and optional targets (scene names, frame ranges, a timeline region), it makes
    /// TARGETED edits (EditSandboxFile/ApplySandboxFileEdits, a new file only for a new component),
    /// verifies the touched scenes with GetCompositionInfo/RenderPreviewStills and renders once.
    /// Run only by StepType.Revise through ICompositionRevisionRunner — deliberately not the Author
    /// agent with another prompt: Author's contract is "assemble the whole film from upstream
    /// analysis" (RenderManifestOutput, Author-specific output validation, whole-file writes),
    /// which is exactly the rewrite a revision must not do. Output: CompositionRevisionOutput.
    /// </summary>
    CompositionReviser,
    /// <summary>
    /// LLM agent that designs ONE still graphic for the picture (thumbnail) editor — a title
    /// treatment, a badge, a price tag, an arrow or callout, a stylised number, a shape composition —
    /// as React (a Remotion <c>Still</c>) in the execution's sandbox, on a TRANSPARENT frame, checking
    /// it with RenderPreviewStills. It never renders the deliverable itself: the runner renders the
    /// final transparent PNG deterministically, verifies and crops it, and the Inference API adds it
    /// as a layer. Run only by StepType.DesignGraphic through IGraphicDesignRunner (see
    /// docs/editor.md "Designed graphics"). A separate agent rather than the Author or the
    /// CompositionReviser: both contracts are about a FILM (whole-timeline assembly, or a targeted
    /// revision rendered with RenderVideoAndUploadToStorage), while this one is a single transparent
    /// still whose render the platform owns. Output: GraphicDesignOutput.
    /// </summary>
    GraphicDesigner,

    /// <summary>
    /// LLM agent for the dailies room (issue #93 phase 4): judges which of several GENERATED
    /// takes of the same planned shot (<c>v{n}t{k}</c> ids offered by a multi-source VideoAnalyze
    /// over a VideoGenerate step's clips) reads best, emitting a <c>TakeSelectionOutput</c> —
    /// offered take ids plus prose only, never a timestamp or a score number (guarded by
    /// <c>TakeSelectionOutputInvariantTests</c>). Used as every seat of a StepType.DailiesRoom
    /// step and as its solo fallback. Same minimal read-only tool scope as VideoStoryEditor.
    /// See docs/video-generation.md "Dailies: choosing between takes".
    /// </summary>
    TakeSelector,

    /// <summary>
    /// LLM agent used TWICE by a StepType.DailiesRoom step — the dailies analogue of
    /// AgentType.VideoEditDirector: once per-turn as the moderator participant in the room's live
    /// group chat (free-form prose, emits the literal sentinel "ROOM_DECIDED" once satisfied), and
    /// once more, OUTSIDE the group chat, for a single structured-output synthesis call that
    /// converts the room's discussion into ONE schema-validated TakeSelectionOutput — the exact
    /// schema AgentType.TakeSelector emits. Same minimal read-only tool scope.
    /// </summary>
    DailiesDirector,

    /// <summary>
    /// LLM agent that plans zero or more pictures or videos shown over the film (design
    /// <c>docs/design/picture-overlay.md</c> §7) — each choice an offered media id ("m{n}") from
    /// <c>view.overlayMedia</c>, an offered overlay-window id ("o{n}") from <c>view.overlayWindows</c>,
    /// and enum WORDS for form, anchor, size, border and sound; plus the one documented numeric
    /// exemption, a bounded 0..100 opacity. Never emits a timestamp or a coordinate — see
    /// ReelBolt.Shared.Data.OutputSchemas.MediaOverlayPlanOutput. Same minimal read-only tool
    /// scope as VideoStoryEditor: ProjectRead + FailWorkflow only, with explicitly NO sandbox,
    /// NO render and NO write tool — it authors nothing. Appended (never inserted) because the
    /// enum is persisted by name.
    /// </summary>
    OverlayPlanner,

    /// <summary>
    /// Plans camera moves, speed ramps, flashes and graphics on detected events (design
    /// <c>docs/design/video-events-analysis.md</c> section 5): one <c>MotionPlanOutput</c> of ids and words, applied by
    /// <c>VideoCompile</c> (<c>EnableMotion</c> + <c>MotionPlan</c>). ProjectRead + WorkflowControl only; it authors nothing.
    /// Appended (never inserted) because the enum is persisted by name.
    /// </summary>
    MotionDirector,

    /// <summary>
    /// A late planner (design <c>docs/design/dynamic-edit.md</c>) that sees the generated clip ids the story editor cannot: it
    /// emits only the <c>dynamics</c> list (<c>DynamicsPlanOutput</c>, words and offered ids), applied by <c>VideoCompile</c>
    /// (<c>DynamicsPlan</c>) on top of the story editor's own list. ProjectRead + WorkflowControl only; it authors nothing.
    /// Appended (never inserted) because the enum is persisted by name.
    /// </summary>
    DynamicsPlanner
}

/// <summary>
/// Discriminator for workflow step behavior.
/// </summary>
public enum StepType
{
    [ReelBolt.Shared.Workflows.StepCard("Runs one AI agent with its prompt and tools and records its answer as the step's output.", ["An agent chosen on the step"], "The agent's text or schema-checked JSON answer", ReelBolt.Shared.Workflows.Apps.AppCostClass.UsesCredits, ReadableBy = ["Any later step"], PairsWith = ["Voiceover: the Voiceover source names this step when it writes the lines", "VideoCompile: reads an editor, planner or supervisor agent's decision by step order"])]
    Agent,
    Conditional,
    [ReelBolt.Shared.Workflows.StepCard("Repeats one agent once for every item an earlier step produced.", ["An earlier step that outputs a list"], "A list with one result per item", ReelBolt.Shared.Workflows.Apps.AppCostClass.NoCredits, ReadableBy = ["Any later step"], PairsWith = ["Agent: runs once per item"])]
    ForEach,
    ReviewLoop,
    /// <summary>
    /// Runs multiple agents in parallel and merges their outputs into a JSON array
    /// passed to the next step as: [{"agentName":"...","output":"{..."}}, ...]
    /// </summary>
    [ReelBolt.Shared.Workflows.StepCard("Runs several agents at the same time and merges their answers into one list.", ["Two or more agents chosen on the step"], "A list of each agent's answer", ReelBolt.Shared.Workflows.Apps.AppCostClass.UsesCredits, ReadableBy = ["Any later step"], PairsWith = ["Agent: a later agent can read the merged answers"])]
    Parallel,
    /// <summary>
    /// Deterministic, non-LLM projection step. See ReelBolt.Shared.Workflows.ExtractStepConfig.
    /// </summary>
    Extract,
    /// <summary>
    /// Deterministic, non-LLM video derush/analysis step (ffmpeg + optional ASR).
    /// See ReelBolt.Shared.Workflows.VideoAnalyzeStepConfig.
    /// </summary>
    VideoAnalyze,
    /// <summary>
    /// Deterministic, non-LLM video cut/compile step (ffmpeg). Resolves an editorial
    /// decision's opaque ids to frame-accurate times against a VideoAnalyze artifact.
    /// See ReelBolt.Shared.Workflows.VideoCompileStepConfig.
    /// </summary>
    VideoCompile,
    /// <summary>
    /// Multi-agent "edit room": several AgentType.VideoStoryEditor-role seats plus an
    /// AgentType.VideoEditDirector moderator converse in a live Microsoft.Agents.AI.Workflows
    /// group chat over a bounded VideoAnalyze view, then the director emits ONE schema-validated
    /// VideoEditDecisionOutput in a normal structured call outside the chat loop — exactly the
    /// shape a StepType.Agent + AgentType.VideoStoryEditor step already produces, so
    /// VideoCompileStepExecutor needs no changes to consume it. See
    /// ReelBolt.Shared.Workflows.EditRoomStepConfig and docs/video-editing.md "The edit room".
    /// </summary>
    EditRoom,
    /// <summary>
    /// Multi-agent "graphics room" — the motion-graphics analogue of EditRoom, built on the same
    /// shared room infrastructure (RoomGroupChatManager/RoomStepExecutorBase): several
    /// AgentType.MotionGraphicsPlanner-role seats plus an AgentType.MotionGraphicsDirector
    /// moderator converse in a live group chat over the same offered view.placements candidates
    /// (p{n} ids) a solo planner would see, then the director emits ONE schema-validated
    /// MotionGraphicsPlanOutput in a normal structured call outside the chat loop — exactly the
    /// shape a StepType.Agent + AgentType.MotionGraphicsPlanner step already produces, so
    /// VideoCompileStepExecutor's GraphicsPlan resolution needs no changes to consume it. See
    /// ReelBolt.Shared.Workflows.GraphicsRoomStepConfig and docs/video-editing.md
    /// "The graphics room".
    /// </summary>
    GraphicsRoom,
    /// <summary>
    /// Multi-agent "color grade room" — the colour-grading analogue of EditRoom/GraphicsRoom,
    /// built on the same shared room infrastructure (RoomGroupChatManager/RoomStepExecutorBase):
    /// several AgentType.Colorist-role seats plus an AgentType.ColorGradeDirector moderator
    /// converse in a live group chat over a bounded VideoAnalyze view's measured per-shot colour
    /// facts (s{n} shot ids), then the director emits ONE schema-validated ColorGradePlanOutput
    /// in a normal structured call outside the chat loop — exactly the shape a StepType.Agent +
    /// AgentType.Colorist step already produces, so VideoCompileStepExecutor's ColorGradePlan
    /// resolution needs no changes to consume it. See
    /// ReelBolt.Shared.Workflows.ColorGradeRoomStepConfig and docs/video-editing.md
    /// "The color grade room".
    /// </summary>
    ColorGradeRoom,
    /// <summary>
    /// Deterministic, non-LLM video generation step. Calls an external video-generation provider
    /// via ReelBolt.Shared.Workflows.VideoGenerateStepConfig and reuses AgentType.VideoTransform.
    /// See docs/video-editing.md.
    /// </summary>
    VideoGenerate,
    /// <summary>
    /// Deterministic, non-LLM step synthesizing narration per line via ISpeechSynthesisClient, run by VoiceoverStepExecutor.
    /// </summary>
    Voiceover,
    /// <summary>
    /// Deterministic, non-LLM step choosing one frame of the project's own video as a cover image
    /// and writing it as a media_derivatives row, run by ThumbnailGenerateStepExecutor. Calls no
    /// model. See ReelBolt.Shared.Workflows.ThumbnailGenerateStepConfig and
    /// docs/thumbnails-and-image-editing.md.
    /// </summary>
    ThumbnailGenerate,
    /// <summary>
    /// Revises a finished Remotion film: restores a composition workspace version into this
    /// execution's sandbox, runs AgentType.CompositionReviser with the request's instruction and
    /// targets, requires the render (unless preview-only) and links the captured version to the
    /// request. A thin adapter over ICompositionRevisionRunner, run ONLY by a project's hidden
    /// WorkflowDefinitionKind.CompositionRevision workflow — never authorable, never cached. See
    /// docs/design/composition-revisions.md.
    /// </summary>
    [ReelBolt.Shared.Workflows.StepCard("Revises a finished film from a change request. Run only by the project's hidden revision workflow, never authored.", ["A finished film and a change request"], "The revised film version", ReelBolt.Shared.Workflows.Apps.AppCostClass.UsesCredits, ReadableBy = ["The request that asked for it"], OutputContract = "CompositionRevisionOutput")]
    Revise,
    /// <summary>
    /// Designs one still graphic for a picture (thumbnail) document: prepares the execution's sandbox
    /// (a transparent Remotion Still scaffold for a new graphic, or a restored composition workspace
    /// version for "Change this graphic"), runs AgentType.GraphicDesigner, then renders the still
    /// itself as a transparent PNG, verifies and crops it, uploads it beside the document and records
    /// the graphic's source as a composition workspace version. The Inference API adds the PNG as a
    /// layer when it next reads the request. A thin adapter over IGraphicDesignRunner, run ONLY by a
    /// project's hidden WorkflowDefinitionKind.GraphicDesign workflow — never authorable, never cached,
    /// never retried. See docs/editor.md "Designed graphics".
    /// </summary>
    [ReelBolt.Shared.Workflows.StepCard("Designs one still graphic for a picture. Run only by the project's hidden graphic workflow, never authored.", ["A picture document and a design request"], "A transparent still graphic", ReelBolt.Shared.Workflows.Apps.AppCostClass.UsesCredits, ReadableBy = ["The picture document"], OutputContract = "GraphicDesignOutput")]
    DesignGraphic,

    /// Multi-agent "dailies room" (issue #93 phase 4) — the take-selection analogue of
    /// EditRoom/GraphicsRoom/ColorGradeRoom on the same shared room infrastructure: several
    /// AgentType.TakeSelector-role seats plus an AgentType.DailiesDirector moderator deliberate over
    /// a multi-source VideoAnalyze view whose <c>view.sources[].clipId</c> names the generated takes
    /// (<c>v{n}t{k}</c>) a VideoGenerate step produced, then the director emits ONE schema-validated
    /// TakeSelectionOutput. A VideoCompile step's <c>TakeSelection</c> reference consumes it to place
    /// only the chosen take of each planned shot. See
    /// ReelBolt.Shared.Workflows.DailiesRoomStepConfig and docs/video-generation.md
    /// "Dailies: choosing between takes".
    /// </summary>
    DailiesRoom,
    /// <summary>
    /// Deterministic, non-LLM render of an editor-v2 EditTimeline document (edit_timelines row at a
    /// pinned revision) to a delivery preset: region-planned ffmpeg mezzanines with a cross-execution
    /// region cache, one sample-accurate audio mix pass, optional two-pass loudnorm, final encode.
    /// Bound to the VideoTransform placeholder agent like the other deterministic types. See
    /// ReelBolt.Shared.Workflows.TimelineRenderStepConfig and docs/editor.md "Rendering (P4)".
    /// </summary>
    TimelineRender,
    /// <summary>
    /// Deterministic, non-LLM product design system step: parses the project's design sources into a
    /// static candidate, composes them (with optional Agent-step LLM fallbacks) into the run's design
    /// system artifact, and requires the requested members. Run by DesignSystemStepExecutor. See
    /// ReelBolt.Shared.Workflows.DesignSystemStepConfig.
    /// </summary>
    DesignSystem,
    /// <summary>
    /// Deterministic, non-LLM, free: grabs one frame (at a time, the first or the last) of one video,
    /// publishes it as an image and optionally registers it as a project file. See
    /// ReelBolt.Shared.Workflows.ExtractFrameStepConfig.
    /// </summary>
    ExtractFrame,
    /// <summary>
    /// Deterministic, non-LLM, PAID: generates music tracks or sound effects through a MusicGeneration /
    /// SoundEffectGeneration provider, budget-gated per take on the external_generation_jobs ledger, and
    /// registers each take as an audio project file. See ReelBolt.Shared.Workflows.AudioGenerateStepConfig.
    /// </summary>
    AudioGenerate,
    /// <summary>
    /// Deterministic, non-LLM, free: the "pinning step" without an agent. Pins content (by reference, resolved
    /// server-side) onto the tracked screens a VideoAnalyze step offered and emits a MotionGraphicsPlanOutput with
    /// screen inserts only. See ReelBolt.Shared.Workflows.PinInsertStepConfig.
    /// </summary>
    PinInsert,
    /// <summary>
    /// Deterministic, non-LLM: cleans the audio of one audio file or one video (hum, wind, hiss, echo and a basic voice
    /// pass locally for free; AI voice isolation and music removal hosted and metered per audio minute, with the
    /// source's explicit egress consent) and registers the result as a project file. See
    /// ReelBolt.Shared.Workflows.AudioCleanupStepConfig.
    /// </summary>
    AudioCleanup,
    /// <summary>
    /// Deterministic, non-LLM: enhances one video (upscale, frame interpolation, denoise or motion blur) and writes a
    /// new video of the same length with the source's own audio remuxed back untouched. The local operations are free;
    /// the hosted AI ones are metered and need egress consent. See ReelBolt.Shared.Workflows.VideoEnhanceStepConfig.
    /// </summary>
    VideoEnhance,
    /// <summary>
    /// Deterministic, non-LLM, PAID: generates pictures from a prompt (Generate) or edits one source picture by an
    /// instruction (Edit) through an ImageGeneration / ImageEdit provider, budget-gated per picture on the
    /// external_generation_jobs ledger, and optionally registers each result as an image project file. See
    /// ReelBolt.Shared.Workflows.ImageGenerateStepConfig.
    /// </summary>
    ImageGenerate
}

/// <summary>
/// Status of individual workflow step execution.
/// </summary>
public enum StepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped
}

/// <summary>
/// Controls which workflow context an Agent step receives as input.
/// </summary>
public enum AgentInputContextMode
{
    /// <summary>Uses the current accumulated workflow output.</summary>
    FullWorkflow,

    /// <summary>Uses only the most recent completed step output.</summary>
    PreviousStepOnly,

    /// <summary>Uses only a selected subset of prior step outputs.</summary>
    SelectedPriorSteps,

    /// <summary>Uses InputMappingJson to build a custom mapped subset.</summary>
    CustomMappedSubset
}

/// <summary>
/// Controls how much prior workflow context an agent receives as input.
/// </summary>
public enum ContextMode
{
    /// <summary>Only the immediately preceding step's output is passed (default).</summary>
    LastStep,
    /// <summary>All previous steps' outputs are concatenated and passed.</summary>
    AllSteps,
    /// <summary>The last N steps' outputs are concatenated (N = ContextWindowSize).</summary>
    LastN
}

/// <summary>
/// The kind of backend an <see cref="InferenceProvider"/> talks to.
/// </summary>
/// <remarks>
/// Persisted as a string (<c>.HasConversion&lt;string&gt;()</c> in both DbContexts), so adding a
/// member here needs no EF migration — the same reason <see cref="InferenceProviderCapability.Vision"/>
/// needed none.
/// </remarks>
public enum InferenceProviderKind
{
    AzureOpenAI,
    OpenAICompatible,

    /// <summary>
    /// Anthropic's first-party Messages API (<c>api.anthropic.com</c>), via the official
    /// <c>Anthropic</c> NuGet SDK. Claude speaks a different wire protocol from OpenAI's Chat
    /// Completions — this is deliberately NOT modelled as an <see cref="OpenAICompatible"/> row
    /// pointed at a translating gateway, so no proxy sits between the engine and the model.
    /// <para>
    /// Chat and <see cref="InferenceProviderCapability.Vision"/> only: Anthropic exposes no
    /// speech-to-text endpoint, so a <see cref="InferenceProviderCapability.Transcription"/> row of
    /// this kind is rejected at the API boundary rather than failing later inside a workflow.
    /// </para>
    /// </summary>
    Anthropic,

    /// <summary>
    /// Google's first-party Gemini models via the official OpenAI compatibility endpoint
    /// (<c>https://generativelanguage.googleapis.com/v1beta/openai/</c>).
    /// <para>
    /// Chat and <see cref="InferenceProviderCapability.Vision"/> only: Google Gemini exposes no
    /// OpenAI-compatible speech-to-text endpoint, so a <see cref="InferenceProviderCapability.Transcription"/>
    /// row of this kind is rejected at the API boundary rather than failing later inside a workflow.
    /// </para>
    /// </summary>
    Gemini,

    /// <summary>
    /// DeepSeek models via the official OpenAI compatibility endpoint
    /// (<c>https://api.deepseek.com/</c>).
    /// <para>
    /// Chat and <see cref="InferenceProviderCapability.Vision"/> only: DeepSeek exposes no
    /// speech-to-text endpoint, so a <see cref="InferenceProviderCapability.Transcription"/>
    /// row of this kind is rejected at the API boundary rather than failing later inside a workflow.
    /// </para>
    /// </summary>
    DeepSeek,

    /// <summary>
    /// A provider serving <see cref="InferenceProviderCapability.Decision"/> capability only.
    /// TypeSafe models are not interchangeable with Chat, Transcription, or Vision providers;
    /// they are specialized for decision model inference (see docs/decision-models.md).
    /// </summary>
    TypeSafe,

    /// <summary>
    /// MiniMax — <c>api.minimax.io</c>, authentication via <c>Authorization: Bearer</c> — serving
    /// <see cref="InferenceProviderCapability.VideoGeneration"/> (<c>POST /v1/video_generation</c>),
    /// <see cref="InferenceProviderCapability.ImageGeneration"/> and
    /// <see cref="InferenceProviderCapability.ImageEdit"/>.
    /// <para>
    /// One kind, three capabilities, and deliberately not three kinds: MiniMax is one vendor with
    /// one API key and one host, and the image endpoint (<c>POST /v1/image_generation</c>, model
    /// <c>image-01</c>) is served by the same credential as the video one. Each capability still
    /// gets its own provider row and its own independent "at most one default" constraint, so an
    /// operator can point generation at a MiniMax <c>image-01</c> row while editing resolves to
    /// somewhere else entirely.
    /// </para>
    /// <para>
    /// MiniMax serves nothing else — it is not a chat, transcription, vision, decision, embedding
    /// or speech-synthesis backend — and that is enforced by
    /// <c>InferenceProvidersController.IsUnsupportedCombination</c>, which is written as one
    /// negative test against the three capabilities this kind DOES serve rather than as a positive
    /// rejection per other capability, because that method's fallthrough is ALLOW.
    /// </para>
    /// </summary>
    MiniMax,

    /// <summary>
    /// Any self-hosted endpoint speaking the System One wire contract (<c>POST /v1/systemone</c>,
    /// key as <c>Authorization: Bearer</c>) — Open-Jev, the <c>vllm-jev</c> plugin and Kev all
    /// implement it. Deliberately ONE kind rather than one per backend: the provider row's
    /// endpoint decides which server answers, so no second client is needed — the request shape
    /// is already the one <c>TypeSafeDecisionClient</c> issues.
    /// <para>
    /// <see cref="InferenceProviderCapability.Decision"/> only: a System One server does not
    /// answer chat completions, transcribe audio, caption images, or generate video, so those
    /// four capabilities are rejected at the API boundary.
    /// </para>
    /// <para>
    /// There is no vendor default endpoint — a self-hosted deployment is only reachable at the
    /// address an operator supplied, so a blank endpoint throws rather than silently posting a
    /// prompt to a first-party host. The stored key is sent verbatim and a blank key becomes the
    /// shared "no key required" placeholder; nothing on this path reads an ambient environment
    /// variable.
    /// </para>
    /// </summary>
    OpenJev,

    /// <summary>
    /// Fish Audio — <c>api.fish.audio</c>, or a self-hosted fish-speech server — serving
    /// <see cref="InferenceProviderCapability.SpeechSynthesis"/> (<c>POST /v1/tts</c>) and
    /// <see cref="InferenceProviderCapability.Transcription"/> (<c>POST /v1/asr</c>), and nothing else.
    /// <para>
    /// Both halves matter to billing: TTS meters <c>TtsInputBytes</c> and ASR meters
    /// <c>AsrAudioSeconds</c>, and the price book pre-fills a price for each from
    /// <c>Metering.ProviderCapabilityUsage</c>. A Fish Audio row used as the default transcription
    /// provider — which the staging deployment does — would otherwise price every transcription at
    /// zero; see docs/metering-and-billing.md "Rate-card coverage".
    /// </para>
    /// </summary>
    FishAudio,

    /// <summary>
    /// Black Forest Labs — <c>api.bfl.ai</c>, the FLUX vendor — serving
    /// <see cref="InferenceProviderCapability.ImageGeneration"/>,
    /// <see cref="InferenceProviderCapability.ImageEdit"/> and the two mask jobs
    /// (<see cref="InferenceProviderCapability.ImageErase"/>, <see cref="InferenceProviderCapability.ImageFill"/>,
    /// through the <c>flux-pro-1.0-fill</c> model) and nothing else.
    /// <para>
    /// A kind of its own rather than an <see cref="OpenAICompatible"/> row, because the wire
    /// contract is genuinely different and cannot be translated by a base-URL swap: authentication
    /// is an <c>x-key</c> header rather than <c>Authorization: Bearer</c>, the API is
    /// <b>asynchronous</b> (submit returns <c>{id, polling_url}</c> and the result is polled), and
    /// the model is selected by the URL PATH (<c>/v1/flux-2-pro</c>) rather than by a body field.
    /// The same shape <see cref="MiniMax"/> video generation already implements.
    /// </para>
    /// <para>
    /// Two consequences of BFL's own documentation that the client has to honour, both read from
    /// <c>docs.bfl.ml/quick_start/generating_images</c> on 2026-10-09:
    /// <c>result.sample</c> URLs expire after <b>10 minutes</b> and are <b>not CORS-enabled</b>
    /// ("we recommend downloading the image and re-serving it from your own infrastructure"), so a
    /// generated image must be fetched and re-hosted through the same guarded download path
    /// generated video already uses; and the submission endpoint is rate limited to 24 active tasks
    /// (6 for <c>flux-kontext-max</c>), answering <c>429</c>, with <c>402</c> for exhausted credits.
    /// </para>
    /// <para>
    /// <b>Data handling:</b> BFL's FLUX API Service Terms grant BFL a perpetual, irrevocable,
    /// sublicensable licence over Inputs AND Outputs and state that it "may use Inputs and Outputs
    /// to train and improve its artificial intelligence models". For a video platform an Input is a
    /// frame of the customer's footage. That is disclosed to the customer rather than hidden —
    /// <c>site/lib/provider-data-handling.ts</c> carries the row, and it drives the Terms and
    /// Privacy sub-processor sections. It is not a technical control and this comment does not
    /// claim to be one.
    /// </para>
    /// </summary>
    BlackForestLabs,

    /// <summary>
    /// fal.ai — <c>queue.fal.run</c> — serving <see cref="InferenceProviderCapability.Segmentation"/>,
    /// <see cref="InferenceProviderCapability.Depth"/>, <see cref="InferenceProviderCapability.ImageGeneration"/>,
    /// <see cref="InferenceProviderCapability.ImageEdit"/> and the six per-tool picture capabilities
    /// (<see cref="InferenceProviderCapability.ImageErase"/> ... <see cref="InferenceProviderCapability.ImageUpscale"/>)
    /// and nothing else.
    /// <para>
    /// <b>One kind for many models, chosen by the row or by the call.</b> Like <see cref="OpenJev"/> and
    /// <see cref="OpenAICompatible"/>, this is ONE kind rather than one per model: fal exposes a
    /// single queue contract over every hosted endpoint. For segmentation and depth the row's
    /// <c>ModelName</c> IS the endpoint id (<c>fal-ai/sam-3-1/image</c>, <c>fal-ai/birefnet/v2</c>,
    /// <c>fal-ai/imageutils/depth</c>, ...). For images each editor tool resolves its own capability
    /// (<c>ImageErase</c> ... <c>ImageUpscale</c>, <c>ImageEdit</c>, <c>ImageGeneration</c>) and the row's
    /// <c>ModelName</c> is the endpoint for that job, from <c>FalAiImageClient.Catalog</c>; a pre-split
    /// <c>ImageEdit</c> row serving another tool by fallback uses that tool's catalogue default. Adding a model is a catalogue entry with a read schema and a priced
    /// <c>ModelPriceCatalog</c> entry, never a guess.
    /// </para>
    /// <para>
    /// <b>The contract, read from fal's own documentation on 2026-10-10</b> and from each endpoint's
    /// machine-readable OpenAPI schema at
    /// <c>fal.ai/api/openapi/queue/openapi.json?endpoint_id={id}</c>: authentication is the
    /// <c>Authorization: Key &lt;FAL_KEY&gt;</c> header; <c>POST https://queue.fal.run/{model-id}</c>
    /// accepts the endpoint's own input schema and answers a <c>QueueStatus</c> carrying
    /// <c>request_id</c>, <c>status_url</c> and <c>response_url</c>; the job moves
    /// <c>IN_QUEUE</c> to <c>IN_PROGRESS</c> to <c>COMPLETED</c>; and the result is a <c>GET</c> of
    /// <c>response_url</c> in that endpoint's output schema. fal's own pricing note says a model with
    /// no fixed per-output price "fall[s] back to per-second billing based on the GPU machine type",
    /// which is why this kind's rate-card rows are derived from fal's published GPU hourly rate
    /// rather than from a per-model figure fal does not publish — see the pre-catalog rate-card seed (ported once as price rows).
    /// </para>
    /// <para>
    /// <b>Why Segmentation and Depth are served here rather than by an operator-hosted model.</b>
    /// The owner's binding answer was that no GPU VM is funded, so an open-weight segmenter can only
    /// run on a vendor's machines or on the user's own runner; the runner path needs a capability
    /// vocabulary and an egress policy for pulling model weights that do not exist yet, and fal is a
    /// hosted vendor that needs neither. Every endpoint this kind is configured with was read from
    /// its own schema, not inferred from a sibling.
    /// </para>
    /// </summary>
    FalAi,

    /// <summary>
    /// Higgsfield video generation aggregator. Base URL <c>https://api.higgsfield.ai</c>,
    /// authentication via <c>Authorization: Key &lt;KEY_ID&gt;:&lt;KEY_SECRET&gt;</c> — a two-part
    /// credential, NOT a Bearer token. Serves
    /// <see cref="InferenceProviderCapability.VideoGeneration"/> capability only — video generation
    /// is not interchangeable with chat, transcription, vision, or decision capabilities.
    /// <para>
    /// A multi-model AGGREGATOR: submissions are addressed by the vendor's per-workflow
    /// "Endpoint ID" (e.g. <c>kling-video/v3.0/std/text-to-video</c>,
    /// https://docs.higgsfield.ai/docs/models/kling-3/standard-text-to-video.md), which
    /// <c>VideoGenerationRequest.Model</c> carries for this kind.
    /// </para>
    /// <para>
    /// No model is offered by the client's <c>Describe</c> today: Higgsfield publishes no per-second
    /// or per-model price table, and the only authoritative price is the account- and
    /// parameter-specific authenticated <c>POST /estimate/{endpoint_id}</c> response
    /// (https://docs.higgsfield.ai/docs/concepts/billing-and-retention). See
    /// <c>HiggsfieldVideoGenerationClient.Describe</c>.
    /// </para>
    /// </summary>
    Higgsfield,

    /// <summary>
    /// A self-hosted ComfyUI server (issue #93 phase 5) speaking ComfyUI's own HTTP API —
    /// <c>POST /prompt</c>, <c>GET /history/{prompt_id}</c>, <c>GET /view</c>,
    /// <c>POST /upload/image</c>, <c>GET /system_stats</c>. Serves
    /// <see cref="InferenceProviderCapability.VideoGeneration"/> only, and ONLY through the
    /// first-party vetted workflow templates compiled into <c>ComfyUiWorkflowTemplates</c>: no
    /// user, model or step config can submit a free-form graph, because ComfyUI custom nodes
    /// execute arbitrary code (the same input-hygiene stance as the sandbox allowlist).
    /// <para>
    /// Cost model is <c>$0</c> per second but GPU time is finite, so a step targeting this kind is
    /// budgeted in GPU-seconds (<c>VideoGenerateStepConfig.MaxGpuMinutes</c>) through the SAME
    /// atomic budget gate and ledger as a priced vendor. Self-hosted, so — like OpenJev, FishAudio
    /// and OpenAICompatible — a private/loopback endpoint is legitimate and a blank endpoint
    /// throws rather than defaulting to any vendor host. The stored key, when any, is sent
    /// verbatim as <c>Authorization: Bearer</c> for a reverse proxy; a blank key sends none.
    /// </para>
    /// </summary>
    ComfyUI,

    /// <summary>
    /// ElevenLabs, called directly (Product decision: the alternative route to fal's hosted ElevenLabs
    /// endpoints). Base URL <c>https://api.elevenlabs.io</c>, authentication via the
    /// <c>xi-api-key</c> header. Serves <see cref="InferenceProviderCapability.MusicGeneration"/>
    /// (<c>POST /v1/music</c>) and <see cref="InferenceProviderCapability.SoundEffectGeneration"/>
    /// (<c>POST /v1/sound-generation</c>) and nothing else. The stored key <c>env:</c> reads
    /// <c>ELEVENLABS_API_KEY</c>; a blank key THROWS and never falls back to the ambient environment.
    /// See <c>docs/elevenlabs-provider.md</c>.
    /// </summary>
    ElevenLabs,

    /// <summary>
    /// A model running on a user's own machine, reached through that user's paired desktop runner.
    /// ReelBolt only relays: the request travels over the runner's existing outbound connection to the
    /// user's local OpenAI-compatible server. Organization-owned only (never a platform row), and it serves
    /// exactly the Chat, Vision and Embedding capabilities. <see cref="InferenceProvider.Endpoint"/> holds the paired
    /// runner device id (a GUID that must belong to the same organization and not be revoked), the row
    /// carries no API key, and the model name is the free-text name of the local model. Bring-your-own, so
    /// nothing is metered against the credit ledger. When the runner is offline the call fails with a typed
    /// error; it never falls back to a cloud model.
    /// </summary>
    LocalRunner
}

/// <summary>
/// What an <see cref="InferenceProvider"/> row is used for. A single row's <c>ModelName</c>
/// cannot serve more than one role (a Whisper deployment is a different deployment from a chat
/// deployment, and many OpenAI-compatible chat gateways have no <c>/audio/transcriptions</c>
/// endpoint at all), so this discriminator is load-bearing, not cosmetic: it determines which
/// resolution path (chat vs. transcription vs. vision) a provider is eligible for, and which
/// "one default row" uniqueness constraint it participates in — each capability's default is
/// fully independent (a composite unique index on <c>(capability, is_default)</c>).
/// </summary>
/// <remarks>
/// <see cref="Vision"/> (Phase 2 of the video-editing feature — see docs/video-editing.md
/// "Vision captioning") reuses the exact same chat-completions machinery as <see cref="Chat"/>
/// (a vision call is just a chat call with an image content part), so it resolves via
/// <c>IInferenceProviderResolver.ResolveVisionAsync</c> into the same <c>ResolvedInferenceProvider</c>
/// type <see cref="Chat"/> uses — it is kept as a separate capability rather than folded into
/// <see cref="Chat"/> only so a Vision-capable deployment (which may differ from the agent chat
/// deployment) can be configured and defaulted independently, and so a Vision default never
/// silently answers an agent chat-completion resolution or vice versa.
/// </remarks>
public enum InferenceProviderCapability
{
    Chat,
    Transcription,
    Vision,

    /// <summary>
    /// Decision model inference returning a typed choice, score, or judgment instead of
    /// resolving to an <see cref="IChatClient"/>. Not interchangeable with Chat or Vision
    /// capability resolution (which both use <c>ResolveAsync</c> / <c>ResolveVisionAsync</c>
    /// and share <see cref="ResolvedInferenceProvider"/>); it gets its own future
    /// <c>ResolveDecisionAsync</c> method returning its own <c>ResolvedDecisionProvider</c> type.
    /// </summary>
    Decision,

    /// <summary>
    /// Video generation inference via an external provider (e.g., MiniMax). Resolves via
    /// <c>ResolveVideoGenerationAsync</c> into its own typed provider contract, distinct from
    /// chat-completion, transcription, vision, and decision capabilities — video generation is
    /// not a model backbone but a specialized media-synthesis service.
    /// </summary>
    VideoGeneration,

    /// <summary>
    /// A provider capability for text-to-speech, resolved via IInferenceProviderResolver.ResolveSpeechSynthesisAsync and ISpeechSynthesisClient.
    /// </summary>
    SpeechSynthesis,

    /// <summary>
    /// Text embeddings (semantic file search / vector indexing), resolved via
    /// <c>IInferenceProviderResolver.ResolveEmbeddingAsync</c> and <c>IEmbeddingClientFactory</c>.
    /// Appended last: the enum is persisted by NAME, so insertion order is not load-bearing for
    /// stored rows, but the house rule is append-only.
    /// </summary>
    Embedding,

    /// <summary>
    /// Text (+ optional reference image) to image, resolved via
    /// <c>IInferenceProviderResolver.ResolveImageGenerationAsync</c> and <c>IImageClient</c>.
    /// <para>
    /// <b>Its own capability, not a mode of <see cref="Vision"/> or <see cref="VideoGeneration"/>.</b>
    /// A vision row is a chat deployment that reads an image; a video-generation row makes moving
    /// pictures; neither can produce a still. It is also not interchangeable with
    /// <see cref="ImageEdit"/>: this one starts from an empty canvas and a sentence, that one
    /// starts from pixels the customer already has. A vendor usually serves both, which is why one
    /// kind (<see cref="InferenceProviderKind.MiniMax"/>, <see cref="InferenceProviderKind.BlackForestLabs"/>)
    /// appears on both — but they remain two rows and two independent "at most one default"
    /// constraints, so an operator can generate on one vendor and edit on another.
    /// </para>
    /// <para>
    /// Appended last at the time it was added, like every member before it: the enum is persisted
    /// by NAME, so insertion order is not load-bearing for stored rows, but the house rule is
    /// append-only.
    /// </para>
    /// </summary>
    ImageGeneration,

    /// <summary>
    /// Image + (an instruction and/or a mask) to image, resolved via
    /// <c>IInferenceProviderResolver.ResolveImageEditAsync</c> and <c>IImageClient</c>.
    /// <para>
    /// <b>One capability, two operations, deliberately.</b> <c>IImageClient</c> carries both
    /// <c>EditAsync</c> (an instruction in prose) and <c>InpaintAsync</c> (a mask), because the
    /// same vendor row almost always serves both and a second capability would double the admin
    /// surface — a second provider row, a second default, a second picker — for one deployment
    /// decision. A vendor that offers only one of the two is expected to fail the other with a
    /// typed <see cref="NotSupportedException"/>, the same discipline
    /// <c>TranscriptionClientFactory</c> already applies to Anthropic, rather than silently
    /// degrading to a different operation.
    /// </para>
    /// <para>
    /// The distinction is real at the API level and must not be blurred: mask-based inpainting
    /// takes a mask, instruction editing takes a sentence, and a vendor that documents one does not
    /// thereby document the other. OpenAI, for instance, documents its mask as prompt GUIDANCE that
    /// is "not pixel-exact", which is not the same guarantee a Fill-style inpaint makes.
    /// </para>
    /// </summary>
    ImageEdit,

    /// <summary>
    /// Image (+ an optional subject prompt, or point/box prompts) to an alpha matte: "which pixels
    /// are the person". Resolved via <c>IInferenceProviderResolver.ResolveSegmentationAsync</c> and
    /// <c>IImageAnalysisClient</c>.
    /// <para>
    /// <b>This is the capability that makes a layer a subject rather than a frame.</b> Everything
    /// before it could only composite whole images; the gap between a bounding box (which the
    /// engine's <c>IObjectLocator</c> already produces) and an alpha matte is exactly what this
    /// adds, and it is the difference between "centre a crop on the subject" and "cut the subject
    /// out onto its own layer".
    /// </para>
    /// <para>
    /// A segmentation model is never a chat model and never an image generator, so it is its own
    /// capability rather than a mode of <see cref="Vision"/> or <see cref="ImageEdit"/> — the same
    /// rule the four model families in <c>docs/thumbnails-and-image-editing.md</c> are built on.
    /// </para>
    /// </summary>
    Segmentation,

    /// <summary>
    /// Image to a depth map: "how far away is each pixel". Resolved via
    /// <c>IInferenceProviderResolver.ResolveDepthAsync</c> and <c>IImageAnalysisClient</c>.
    /// <para>
    /// The input to the 2.5D path the owner chose: depth plus a layered-depth re-render is the
    /// classical, cheap "3D-aware" technique, and it is deliberately NOT the point-cloud or
    /// video-diffusion approach, which needs a GPU this platform does not fund.
    /// </para>
    /// <para>
    /// The output is a depth MAP (a greyscale image), not a mesh and not a point cloud, because that
    /// is what every hosted depth endpoint documents returning. A caller that needs geometry derives
    /// it; this capability delivers pixels.
    /// </para>
    /// </summary>
    Depth,

    // ---- The picture editor's per-job capabilities --------------------------------------------------
    //
    // Before these existed every editing tool resolved ImageEdit, so one row (and one model) had to
    // serve an instruction edit, an object removal, an outpaint, a relight and an upscale alike. A
    // model good at one of those is rarely the right one for another, and on a non-fal kind the
    // row's model was simply sent to every tool. Each tool now resolves its OWN capability (explicit
    // id -> the single default row of that capability -> none), so an admin points each job at a
    // provider and model built for it. ImageEdit keeps its meaning for the instruction edit
    // ("make the sky purple"), and ImageGeneration for text-to-image; Segmentation still serves both
    // subject selection and background removal.
    //
    // Backward compatibility (decided in EditImageOperationsController, not here): when a job's own
    // capability has no default row, the editor falls back to the ImageEdit default — but ONLY when
    // that row's kind serves the job (ProviderCapabilityUsage.ServableByKind), so a job is never sent to
    // a vendor that cannot do it. On a fal.ai row the fallback picks the catalogue's default endpoint for
    // the job, exactly as every edit did before the split.
    //
    // All six meter UsageKind.ImageEditImages, priced per endpoint (ModelPriceCatalog.FalImages).
    // Appended after Depth: persisted by NAME, append-only.

    /// <summary>
    /// Remove what a painted mask covers and fill the hole from its surroundings (the editor's
    /// <c>erase</c>). Served by fal.ai (<c>fal-ai/object-removal/mask</c> by default), and by the
    /// mask-inpainting arm of OpenAI-compatible and Black Forest Labs rows.
    /// </summary>
    ImageErase,

    /// <summary>
    /// Regenerate a painted area from a prompt (the editor's <c>fill</c>, mask inpainting). fal.ai
    /// (<c>fal-ai/flux-pro/v1/fill</c> by default), OpenAI-compatible and Black Forest Labs.
    /// </summary>
    ImageFill,

    /// <summary>Outpaint a picture to larger bounds (the editor's <c>expand</c>). fal.ai only.</summary>
    ImageExpand,

    /// <summary>Keep the foreground and generate a new background (the editor's <c>replaceBackground</c>). fal.ai only.</summary>
    ImageBackgroundReplace,

    /// <summary>Change a picture's lighting (the editor's <c>relight</c>). fal.ai only.</summary>
    ImageRelight,

    /// <summary>Raise a picture's resolution (the editor's <c>upscale</c>). fal.ai only.</summary>
    ImageUpscale,

    /// <summary>
    /// Platform rendering and encoding (docs/design/model-catalog.md §2.3). Served only by the system
    /// compute provider (<c>WellKnownModelCatalog</c>), so it can carry per-tier prices like any other
    /// model capability. No admin create path, picker or resolver method offers it. Appended: persisted
    /// by NAME, append-only.
    /// </summary>
    Compute,

    /// <summary>
    /// Generate a music track from a prompt or a composition plan. Served by <c>FalAi</c>
    /// (<c>fal-ai/elevenlabs/music</c>) and <c>ElevenLabs</c> (<c>POST /v1/music</c>) through
    /// <c>IAudioGenerationClient</c>; metered in <see cref="UsageKind.MusicGenMinutes"/>. Appended:
    /// persisted by NAME, append-only.
    /// </summary>
    MusicGeneration,

    /// <summary>
    /// Generate a sound effect from a text description. Served by <c>FalAi</c>
    /// (<c>fal-ai/elevenlabs/sound-effects/v2</c>, <c>fal-ai/mmaudio-v2/text-to-audio</c>) and
    /// <c>ElevenLabs</c> (<c>POST /v1/sound-generation</c>) through <c>IAudioGenerationClient</c>;
    /// metered in <see cref="UsageKind.SoundEffectGenSeconds"/>. Appended: persisted by NAME,
    /// append-only.
    /// </summary>
    SoundEffectGeneration,

    /// TEXT-PROMPTED VIDEO segmentation ("find this object in every frame of this clip"), resolved via
    /// <c>IInferenceProviderResolver.ResolveVideoSegmentationAsync</c> and implemented by
    /// <see cref="Inference.IVideoSegmentationClient"/>. Appended last: the enum is persisted by NAME,
    /// so insertion order is not load-bearing
    /// for stored rows, but the house rule is append-only.
    /// <para>
    /// <b>Its own capability, not a mode of <see cref="Segmentation"/>.</b> Image segmentation answers
    /// "where is the subject in this still"; video segmentation answers "where is THIS PROMPTED OBJECT
    /// in every frame", returns a per-frame mask track, is priced per 16 frames of input rather than
    /// per request, and can only be served by a provider whose queue accepts a video URL. Sharing the
    /// capability would let an image-only segmenter row be selected for a video job and fail at the
    /// vendor.
    /// </para>
    /// <para>
    /// Shaped GENERALLY (per-frame mask or matte, prompt/point/box seeded, several objects) so a later
    /// background-removal or matting endpoint can register under this same capability without a second
    /// enum member — see the prompted-region-effects plan §1.
    /// </para>
    /// </summary>
    VideoSegmentation,

    /// <summary>
    /// Content moderation: text and images classified against a policy (OpenAI's
    /// <c>omni-moderation-latest</c>, free to use), resolved via
    /// <c>IInferenceProviderResolver.ResolveContentModerationAsync</c> and
    /// <c>IContentModerationClient</c>. Appended last: persisted by NAME, append-only.
    /// <para>
    /// Served by <see cref="InferenceProviderKind.OpenAICompatible"/> only. Video and audio are
    /// sampled into frames plus text first (the model accepts text and images, never audio or
    /// video), and an unresolved capability means "moderation is off" — never a fallback to a chat
    /// deployment.
    /// </para>
    /// </summary>
    ContentModeration,

    /// <summary>
    /// Relevance reranking: a query and a short list of candidate texts scored against each other
    /// (<c>POST {base}/rerank</c>, the Hugging Face text-embeddings-inference route that hosted
    /// rerankers also expose), resolved via <c>IInferenceProviderResolver.ResolveRerankAsync</c> and
    /// <c>IRerankClient</c>. Appended last: persisted by NAME, append-only.
    /// <para>
    /// Served by <see cref="InferenceProviderKind.OpenAICompatible"/> only. An unresolved or
    /// unpriced capability means "rerank is off": the platform-docs search keeps its deterministic
    /// order and never fails because the reranker did.
    /// </para>
    /// </summary>
    Rerank,

    /// <summary>
    /// Live web search for agent steps (<c>IWebSearchClient</c>, resolved via
    /// <c>IInferenceProviderResolver.ResolveWebSearchAsync</c>). Served by DeepSeek, Gemini and
    /// OpenAICompatible rows pointing at api.openai.com. Appended last: persisted by NAME, append-only.
    /// </summary>
    WebSearch,

    /// <summary>
    /// Hosted AI voice isolation: strip background noise and music from a recording and return the voice
    /// (and, where the model offers one, a background stem). Served by <c>FalAi</c> only
    /// (<c>fal-ai/elevenlabs/audio-isolation</c>, the one use of ElevenLabs technology that is
    /// allowed; the <c>ElevenLabs</c> kind itself stays dormant) through <c>IAudioIsolationClient</c>;
    /// metered in <see cref="UsageKind.AudioCleanupMinutes"/>. Appended last: persisted by NAME, append-only.
    /// </summary>
    AudioIsolation,

    /// <summary>
    /// Hosted video upscaling: one input clip in, the same clip at a larger short side, resolved via
    /// <c>IInferenceProviderResolver.ResolveVideoUpscaleAsync</c> and <see cref="Inference.IVideoUpscaleClient"/>.
    /// Appended last: persisted by NAME, append-only.
    /// <para>
    /// <b>Its own capability, not a mode of <see cref="ImageUpscale"/>.</b> The unit is an OUTPUT
    /// SECOND of video and the vendor (fal's Topaz and bytedance endpoints) prices by resolution tier
    /// and frame rate, while an image upscale is priced per output image; sharing the capability would
    /// let an image upscaler row be selected for a clip and mis-price it. Audio is not part of this
    /// capability — a hosted upscaler returns video only, and the caller remuxes the untouched source
    /// audio back locally.
    /// </para>
    /// <para>
    /// Served by <see cref="InferenceProviderKind.FalAi"/> only. An unresolved or unpriced capability
    /// means "video upscale is off", never a fallback to a lower-resolution render.
    /// </para>
    /// </summary>
    VideoUpscale,

    /// <summary>
    /// Hosted frame interpolation: one input clip in, the same clip at a higher frame rate, resolved
    /// via <c>IInferenceProviderResolver.ResolveFrameInterpolationAsync</c> and
    /// <see cref="Inference.IFrameInterpolationClient"/>. Appended last: persisted by NAME, append-only.
    /// <para>
    /// <b>Its own capability, not a mode of</b> <see cref="VideoUpscale"/>: an interpolator is priced per
    /// GPU COMPUTE second (RIFE/FILM), an upscaler per OUTPUT second of video, and the two vendors'
    /// fields differ (<c>num_frames</c> against <c>upscale_factor</c>). A row that offers one must not be
    /// silently selectable for the other. Audio is not part of this capability either.
    /// </para>
    /// <para>
    /// Served by <see cref="InferenceProviderKind.FalAi"/> only. An unresolved or unpriced capability
    /// means "frame interpolation is off", never a fallback to the source frame rate.
    /// </para>
    /// </summary>
    FrameInterpolation,

    /// <summary>
    /// Hosted video outpainting: one input clip in, the same clip on a larger canvas with generated margins,
    /// resolved via <c>IInferenceProviderResolver.ResolveVideoOutpaintAsync</c> and
    /// <see cref="Inference.IVideoOutpaintClient"/>. Appended last: persisted by NAME, append-only.
    /// <para>
    /// <b>Its own capability, not a mode of</b> <see cref="VideoUpscale"/>: an upscaler magnifies the picture, an
    /// outpaint invents picture outside it, and the vendors' price sentences and request fields differ
    /// (<c>expand_left/right/top/bottom</c> plus a required prompt against <c>upscale_factor</c>). Served by
    /// <see cref="InferenceProviderKind.FalAi"/> only (Wan VACE default, Luma reframe alternative).
    /// </para>
    /// <para>
    /// An unresolved or unpriced capability means "extend is off", never a crop or a scale in its place. Audio is
    /// not part of this capability: the caller remuxes the untouched source audio locally.
    /// </para>
    /// </summary>
    VideoOutpaint,

    /// <summary>
    /// Hosted talking-head lip-sync: one still image plus a speech clip in, a video of that face speaking the clip
    /// out, resolved via <c>IInferenceProviderResolver.ResolveLipSyncAsync</c> and the same video-generation client
    /// as <see cref="VideoGeneration"/>. Appended last: persisted by NAME, append-only.
    /// <para>
    /// <b>Its own capability, not a mode of</b> <see cref="VideoGeneration"/>: a text/image-to-video model invents
    /// motion, a talking-head model animates a face to given audio, and one default row cannot serve both jobs (the
    /// default video model rejects a talking-head model id). Served by <see cref="InferenceProviderKind.FalAi"/>
    /// only; the row's model must be a talking-head entry of the fal video catalog. Unit:
    /// <c>UsageKind.VideoGenSeconds</c>. An unresolved capability means "no lip-sync", never a fall back to
    /// <see cref="VideoGeneration"/>.
    /// </para>
    /// </summary>
    LipSync

}

/// <summary>
/// Where a model capability's usage price comes from (docs/design/model-catalog.md §2). Persisted by NAME.
/// </summary>
public enum ModelPriceSource
{
    /// <summary>The capability's <c>model_capability_prices</c> rows.</summary>
    Table,

    /// <summary>The USD the vendor quoted for the call (Higgsfield); no price row.</summary>
    Oracle
}

/// <summary>
/// Type of track in an editing timeline.
/// </summary>
/// <remarks>
/// Edit-v2 contract encoding: camelCase string (<c>"video"</c>/<c>"audio"</c>), integers rejected.
/// </remarks>
[JsonConverter(typeof(EditTimelineEnumJsonConverter))]
public enum TrackKind
{
    Video,
    Audio
}

/// <summary>
/// How to handle aspect ratio mismatches in video editing.
/// </summary>
/// <remarks>
/// Edit-v2 contract encoding: camelCase string (<c>"fit"</c>), integers rejected.
/// </remarks>
[JsonConverter(typeof(EditTimelineEnumJsonConverter))]
public enum AspectPolicy
{
    Fit,
    Fill,
    Stretch
}

/// <summary>
/// Interpolation method for keyframed animation.
/// </summary>
/// <remarks>
/// Edit-v2 contract encoding: camelCase string (<c>"linear"</c>, <c>"easeInQuad"</c>), integers rejected.
/// </remarks>
[JsonConverter(typeof(EditTimelineEnumJsonConverter))]
public enum InterpKind
{
    Linear,
    EaseInQuad,
    EaseOutQuad,
    EaseInOutQuad,
    EaseInCubic,
    EaseOutCubic,
    EaseInOutCubic,
    Step
}

/// <summary>
/// Origin of an edited timeline item.
/// </summary>
/// <remarks>
/// Edit-v2 contract encoding: camelCase string (<c>"manual"</c>), integers rejected.
/// </remarks>
[JsonConverter(typeof(EditTimelineEnumJsonConverter))]
public enum ProvenanceOrigin
{
    Manual,
    Generated,
    Imported,
    Analyzed
}

/// <summary>
/// Type of asset reference in editing.
/// </summary>
/// <remarks>
/// Edit-v2 contract encoding: camelCase string (<c>"projectFile"</c>/<c>"stepResult"</c>), integers rejected.
/// </remarks>
[JsonConverter(typeof(EditTimelineEnumJsonConverter))]
public enum AssetRefKind
{
    ProjectFile,
    StepResult
}

/// <summary>
/// The billable physical unit of a <c>usage_events</c> row (docs/design/saas-metering.md "Usage kinds").
/// Append-only: persisted by member name, so a rename breaks stored rows, an append does not.
/// </summary>
public enum UsageKind
{
    LlmInputTokens,
    LlmOutputTokens,
    LlmCachedInputTokens,
    EmbeddingTokens,
    AsrAudioSeconds,
    TtsInputBytes,
    VideoGenSeconds,
    RenderOutputSeconds,
    EncodeOutputSeconds,
    AnalyzeMediaSeconds,
    StorageGbDay,

    /// <summary>
    /// One image RETURNED by an <see cref="InferenceProviderCapability.ImageGeneration"/> call —
    /// the unit every image-generation vendor we integrate bills in (MiniMax publishes
    /// <c>$0.0035 per image</c> for <c>image-01</c>; BFL prices FLUX 3 Image per image and FLUX.2
    /// per output megapixel). A request that returns <c>n</c> images meters <c>n</c> of these, not
    /// one, so a batch is never charged as a single unit.
    /// </summary>
    ImageGenImages,

    /// <summary>
    /// One image RETURNED by an <see cref="InferenceProviderCapability.ImageEdit"/> call,
    /// instruction-based or mask-based. Kept separate from <see cref="ImageGenImages"/> because the
    /// two are separately priced by every vendor that publishes both: BFL lists FLUX.2 [pro] at
    /// "from $0.03" text-to-image and "from $0.045" for image editing, so one shared unit would
    /// under-charge every edit by a third.
    /// </summary>
    ImageEditImages,

    /// <summary>
    /// One image RETURNED by an <see cref="InferenceProviderCapability.Segmentation"/> call — a
    /// cut-out or a mask. One call produces one, so this counts calls as well as outputs, and it is
    /// the unit fal's queue bills in (per successful output; a server error or queue time is not
    /// charged).
    /// </summary>
    SegmentationImages,

    /// <summary>
    /// One image RETURNED by an <see cref="InferenceProviderCapability.Depth"/> call — a depth map.
    /// Kept separate from <see cref="SegmentationImages"/> rather than sharing one "analysis" unit
    /// because the two endpoints are priced from different GPU classes and a later per-model
    /// measured price has to be able to land on one without moving the other.
    /// </summary>
    DepthImages,

    /// <summary>
    /// Billed MINUTES of generated music (<see cref="InferenceProviderCapability.MusicGeneration"/>).
    /// fal prices <c>fal-ai/elevenlabs/music</c> at $0.60 "per output audio minute, rounded up to the
    /// next minute", so a 30 s track is one unit; the quantity is the vendor's rounded count, never a
    /// fraction (<c>AudioGenerationCapabilities.BilledUnits</c>).
    /// </summary>
    MusicGenMinutes,

    /// <summary>
    /// Seconds of generated sound effect (<see cref="InferenceProviderCapability.SoundEffectGeneration"/>):
    /// fal lists its ElevenLabs SFX v2 at $0.002 and MMAudio at $0.001 per second.
    /// </summary>
    SoundEffectGenSeconds,

    /// One FRAME of video input sent to a <see cref="InferenceProviderCapability.VideoSegmentation"/>
    /// call — the unit fal's <c>fal-ai/sam-3-1/video-rle</c> prices in ("$0.01 per 16 frames of video
    /// input", read 2026-10-18). Not <see cref="SegmentationImages"/>: an image request is billed per
    /// output, a video request per frame of INPUT, and the two price scales differ by two orders of
    /// magnitude, so one shared unit would mis-price both.
    /// <para>
    /// <b>Frames, not 16-frame blocks.</b> The vendor rounds a request UP to a whole block
    /// (<c>VideoSegmentationBilling.BillableFrames</c>), and the rate card is linear per frame at
    /// <c>$0.01 / 16</c>; the metering decorator records the rounded-up frame count so the arithmetic
    /// lands on the vendor's own unit without a second, block-shaped quantity on the event row.
    /// </para>
    /// </summary>
    VideoSegmentationFrames,

    /// <summary>
    /// One call to a <see cref="InferenceProviderCapability.ContentModeration"/> model. OpenAI's
    /// moderation models are free, so every such call is metered at
    /// <c>CostUsdPerUnit = 0</c> — a real $0 price, not an unpriced row. Appended last: persisted by
    /// NAME, append-only.
    /// </summary>
    ModerationCalls,

    /// <summary>
    /// One call to a <see cref="InferenceProviderCapability.Rerank"/> model: one query plus up to 20
    /// candidate texts is ONE unit. A self-hosted reranker is a real $0 price. Appended last:
    /// persisted by NAME, append-only.
    /// </summary>
    RerankSearches,

    /// <summary>
    /// One web search query/action of a <see cref="InferenceProviderCapability.WebSearch"/> model.
    /// Appended last: persisted by NAME, append-only.
    /// </summary>
    WebSearches,

    /// <summary>
    /// Billed MINUTES of source audio sent to an <see cref="InferenceProviderCapability.AudioIsolation"/> model.
    /// The metering path measures audio SECONDS and rounds UP to whole minutes (a started minute is a unit, the
    /// way fal prices <c>fal-ai/elevenlabs/audio-isolation</c>: "$0.1 per minutes"); the quantity is that
    /// rounded count, never a fraction (<c>AudioCleanupCreditPricing.BilledMinutes</c>). Appended last:
    /// persisted by NAME, append-only.
    /// </summary>
    AudioCleanupMinutes,

    /// <summary>
    /// One OUTPUT SECOND of an enhanced clip (<see cref="InferenceProviderCapability.VideoUpscale"/> and
    /// <see cref="InferenceProviderCapability.FrameInterpolation"/>). fal's Topaz and bytedance upscalers
    /// publish a price per output second of video, by resolution tier, with high frame rate billed at
    /// double; RIFE/FILM publish a price per GPU COMPUTE second instead — that is
    /// <see cref="VideoEnhanceComputeSeconds"/>, a different unit, never this one. Appended last:
    /// persisted by NAME, append-only.
    /// </summary>
    VideoEnhanceSeconds,

    /// <summary>
    /// One COMPUTE SECOND of a frame-interpolation job (<see cref="InferenceProviderCapability.FrameInterpolation"/>):
    /// fal's <c>fal-ai/rife</c> and <c>fal-ai/film</c> are listed at $0.0013 "per compute second". The vendor
    /// returns no compute figure, so the platform's hold is an estimate from the OUTPUT frames
    /// (<c>VideoEnhanceCreditPricing</c>) and settles at that hold. Not <see cref="VideoEnhanceSeconds"/>: an upscaler's unit is a second of the produced
    /// video, an interpolator's is a second of compute, and the two do not convert. Appended last:
    /// persisted by NAME, append-only.
    /// </summary>
    VideoEnhanceComputeSeconds

}

/// <summary>
/// Who should bear the cost of the work a failed step consumed (docs/design/saas-metering.md
/// "Failure classes"). Append-only: persisted by member name.
/// </summary>
public enum FailureClass
{
    None,
    UserInput,
    UserCancelled,
    ByoProviderError,
    PlatformProviderError,
    PlatformInternal,
    InsufficientCredits,
    EntitlementDenied,

    /// <summary>
    /// D12: the organization's own runner misbehaved, or its output failed platform verification
    /// (a missing, oversized, unreadable, wrong-shaped, wrong-hash or blank object). Deliberately NOT
    /// refunded: the compute was the customer's own machine, the bytes it returned are the reason the
    /// step failed, and refunding would let a modified runner mint credit by failing verification.
    /// docs/runner-protocol.md "Failure classes" states the same rule in the protocol's own table.
    /// </summary>
    Runner
}
