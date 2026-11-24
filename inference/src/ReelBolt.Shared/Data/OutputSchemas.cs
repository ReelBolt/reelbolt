namespace ReelBolt.Shared.Data.OutputSchemas;

/// <summary>
/// Structured output for RemotionComponentTranslatorAgent.
/// The agent builds the Remotion project structure directly inside the sandbox
/// using sandbox tools; this schema summarises what was constructed.
/// </summary>
public class RemotionProjectBuildOutput
{
    /// <summary>Relative sandbox paths of every new TSX/TS file written (e.g. "src/LoginScreen.tsx").</summary>
    public List<string> CreatedFiles { get; set; } = new();

    /// <summary>Relative sandbox paths of existing files that were modified (e.g. "src/index.ts", "src/root.tsx").</summary>
    public List<string> ModifiedFiles { get; set; } = new();

    /// <summary>npm packages installed in addition to the template defaults (e.g. ["@mantine/core"]).</summary>
    public List<string> InstalledPackages { get; set; } = new();

    /// <summary>Remotion composition IDs registered in root.tsx (must match registerRoot / Composition id props).</summary>
    public List<string> RegisteredCompositions { get; set; } = new();

    /// <summary>Whether TypeScript type-checking passed with no errors after writing all files.</summary>
    public bool TypeCheckPassed { get; set; }

    /// <summary>Short human-readable description of what was built and any caveats.</summary>
    public string Summary { get; set; } = string.Empty;
}

/// <summary>
/// Structured output for AnimationStrategyAgent
/// </summary>
public class AnimationStrategyOutput
{
    public List<Scene> Scenes { get; set; } = new();
    public int TotalDurationInFrames { get; set; }
    public int Fps { get; set; } = 30;
    public PacingRecommendations Pacing { get; set; } = new();
}

public class Scene
{
    public string Id { get; set; } = string.Empty;
    public string ComponentName { get; set; } = string.Empty;
    public int StartFrame { get; set; }
    public int DurationInFrames { get; set; }
    public string TransitionType { get; set; } = "fade";
    public int TransitionDurationInFrames { get; set; } = 15;
    public List<AnimationElement> Animations { get; set; } = new();
}

public class AnimationElement
{
    public string ElementId { get; set; } = string.Empty;
    public string AnimationType { get; set; } = string.Empty;
    public int StartFrame { get; set; }
    public int DurationInFrames { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();
}

public class PacingRecommendations
{
    public string OverallTone { get; set; } = string.Empty;
    public int AverageSceneDuration { get; set; }
    public List<string> PacingNotes { get; set; } = new();
}

// ============================================================================
// ANALYSIS AGENT OUTPUT SCHEMAS
// ============================================================================

/// <summary>
/// Structured output for CodeStructureAnalyzerAgent
/// </summary>
public class CodeStructureOutput
{
    public string ProjectType { get; set; } = string.Empty;
    public string Framework { get; set; } = string.Empty;
    public List<DirectoryInfo> Directories { get; set; } = new();
    public List<string> EntryPoints { get; set; } = new();
    public string OverallArchitecture { get; set; } = string.Empty;
}

public class DirectoryInfo
{
    public string Path { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public int FileCount { get; set; }
}

/// <summary>
/// Structured output for DependencyAnalyzerAgent
/// </summary>
public class DependencyAnalysisOutput
{
    public List<Dependency> Dependencies { get; set; } = new();
    public List<string> DevDependencies { get; set; } = new();
    public string PackageManager { get; set; } = string.Empty;
    public List<DependencyRecommendation> Recommendations { get; set; } = new();
}

public class Dependency
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public bool IsCore { get; set; }
}

public class DependencyRecommendation
{
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Structured output for ComponentInventoryAnalyzerAgent
/// </summary>
public class ComponentInventoryOutput
{
    public List<ComponentInfo> Components { get; set; } = new();
    public int TotalComponents { get; set; }
    public List<string> CommonPatterns { get; set; } = new();
}

public class ComponentInfo
{
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public List<PropInfo> Props { get; set; } = new();
    public string Responsibility { get; set; } = string.Empty;
    public List<string> Dependencies { get; set; } = new();
}

public class PropInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool Required { get; set; }
    public string? DefaultValue { get; set; }
}

/// <summary>
/// Structured output for RouteAndApiAnalyzerAgent
/// </summary>
public class RouteAndApiOutput
{
    public List<RouteInfo> ClientRoutes { get; set; } = new();
    public List<ApiEndpoint> ApiEndpoints { get; set; } = new();
    public string RoutingStrategy { get; set; } = string.Empty;
}

public class RouteInfo
{
    public string Path { get; set; } = string.Empty;
    public string ComponentName { get; set; } = string.Empty;
    public List<string> Parameters { get; set; } = new();
    public bool RequiresAuth { get; set; }
}

public class ApiEndpoint
{
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public List<string> Parameters { get; set; } = new();
}

/// <summary>
/// Structured output for StyleAndThemeExtractorAgent
/// </summary>
public class StyleAndThemeOutput
{
    public ColorPalette Colors { get; set; } = new();
    public Typography Typography { get; set; } = new();
    public Spacing Spacing { get; set; } = new();
    public List<string> ComponentStyles { get; set; } = new();
    public string StylingApproach { get; set; } = string.Empty;
}

public class ColorPalette
{
    public string Primary { get; set; } = string.Empty;
    public string Secondary { get; set; } = string.Empty;
    public string Background { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public Dictionary<string, string> Additional { get; set; } = new();
}

public class Typography
{
    public string PrimaryFont { get; set; } = string.Empty;
    public string SecondaryFont { get; set; } = string.Empty;
    public Dictionary<string, string> FontSizes { get; set; } = new();
}

public class Spacing
{
    public string Unit { get; set; } = string.Empty;
    public Dictionary<string, string> Scale { get; set; } = new();
}

// ============================================================================
// PRODUCTION AGENT OUTPUT SCHEMAS
// ============================================================================

/// <summary>
/// Structured output for DirectorAgent
/// </summary>
public class DirectorOutput
{
    public List<Shot> Shots { get; set; } = new();
    public VisualTheme VisualTheme { get; set; } = new();
    public AudioGuidance Audio { get; set; } = new();
    public int TotalDurationInSeconds { get; set; }
}

public class Shot
{
    public string ShotId { get; set; } = string.Empty;
    public string SceneId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int StartTime { get; set; }
    public int Duration { get; set; }
    public CameraDirection Camera { get; set; } = new();
    public List<string> VisualElements { get; set; } = new();
}

public class CameraDirection
{
    public string Angle { get; set; } = string.Empty;
    public string Movement { get; set; } = string.Empty;
    public string Focus { get; set; } = string.Empty;
}

public class VisualTheme
{
    public string Mood { get; set; } = string.Empty;
    public string ColorGrading { get; set; } = string.Empty;
    public List<string> VisualMotifs { get; set; } = new();
}

public class AudioGuidance
{
    public string MusicStyle { get; set; } = string.Empty;
    public string SoundEffects { get; set; } = string.Empty;
    public string Voiceover { get; set; } = string.Empty;
}

/// <summary>
/// Structured output for ScriptwriterAgent
/// </summary>
public class ScriptwriterOutput
{
    public string Title { get; set; } = string.Empty;
    public int DurationInSeconds { get; set; }
    public List<ScriptScene> Scenes { get; set; } = new();
    public string Narrative { get; set; } = string.Empty;
}

public class ScriptScene
{
    public string SceneId { get; set; } = string.Empty;
    public int StartTime { get; set; }
    public int Duration { get; set; }
    public string Voiceover { get; set; } = string.Empty;
    public List<string> OnScreenText { get; set; } = new();
    public string VisualDescription { get; set; } = string.Empty;
}

/// <summary>
/// Structured output for AuthorAgent (RenderManifest)
/// </summary>
public class RenderManifestOutput
{
    public string ProjectName { get; set; } = string.Empty;
    public VideoConfiguration Video { get; set; } = new();
    public List<Composition> Compositions { get; set; } = new();
    public List<AssetReference> Assets { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public class VideoConfiguration
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int Fps { get; set; } = 30;
    public int DurationInFrames { get; set; }
}

public class Composition
{
    public string Id { get; set; } = string.Empty;
    public string ComponentName { get; set; } = string.Empty;
    public int DurationInFrames { get; set; }
    public Dictionary<string, object> Props { get; set; } = new();
    public CompositionScript Script { get; set; } = new();
}

public class CompositionScript
{
    public string Voiceover { get; set; } = string.Empty;
    public List<string> Captions { get; set; } = new();
}

public class AssetReference
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public Dictionary<string, object> Properties { get; set; } = new();
}

// ============================================================================
// QUALITY AGENT OUTPUT SCHEMAS
// ============================================================================

/// <summary>
/// Structured output for ReviewAgent
/// </summary>
public class ReviewOutput
{
    public int OverallScore { get; set; }
    public List<ReviewCriterion> Criteria { get; set; } = new();
    public List<string> Strengths { get; set; } = new();
    public List<string> ImprovementAreas { get; set; } = new();
    public bool PassesReview { get; set; }
    public string Summary { get; set; } = string.Empty;

    /// <summary>Setting changes for this run only (docs/design/review-loop-adjustments.md); empty unless offered.</summary>
    public List<ReviewSettingAdjustment> Adjustments { get; set; } = new();

    /// <summary>Frames to look at more closely before the final answer; empty unless targeted looks are offered.</summary>
    public List<ReviewLookRequestItem> Looks { get; set; } = new();
}

/// <summary>
/// One setting change a reviewer asks for (docs/design/review-loop-adjustments.md). Deliberately plain fields:
/// the step, an optional item (for a step that takes a change request), a setting NAME from the offered list, the new
/// value as text and a reason. The engine checks the name
/// against its closed allowlist and the value against the range; nothing here can name a provider, a cost or a prompt
/// (pinned by ReviewOutputSchemaInvariantTests).
/// </summary>
public class ReviewSettingAdjustment
{
    public int StepOrder { get; set; }

    /// <summary>For a step that takes a change request: the line, shot, take or scene id as listed (or <c>*</c>); empty otherwise.</summary>
    public string Item { get; set; } = string.Empty;

    public string Setting { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>A targeted look: times of the finished film, an optional region (0..1; the whole frame when 0,0,1,1) and why.</summary>
public class ReviewLookRequestItem
{
    public List<double> AtSec { get; set; } = new();
    public ReviewLookRegionItem Region { get; set; } = new();
    public string Why { get; set; } = string.Empty;
}

/// <summary>A region of the frame in 0..1 fractions; the default is the whole frame.</summary>
public class ReviewLookRegionItem
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 1;
    public double H { get; set; } = 1;
}

public class ReviewCriterion
{
    public string Name { get; set; } = string.Empty;
    public int Score { get; set; }
    public string Feedback { get; set; } = string.Empty;
}

/// <summary>
/// Structured output for AgentType.VideoReviewAgent — the video-editing pipelines' ReviewLoop
/// review, mirroring ReviewOutput's role for the main promo pipeline but judging the compiled
/// EDIT rather than code/lint quality. Deliberately a separate, smaller schema rather than
/// reusing ReviewOutput: the criteria this agent actually has evidence for (a deterministic
/// sentence-boundary check and overlay frame-coverage numbers VideoCompileStepExecutor already
/// computed) don't map onto ReviewOutput's narrativeClarity/visualAccuracy/timing/completeness
/// criteria, which are meaningless for a video edit with no Remotion code to inspect.
/// </summary>
/// <remarks>
/// The top-level property is deliberately named <see cref="Score"/> (serializing to "score"),
/// not "overallScore" — ReviewLoopStepExecutor.ParseReviewScore checks "score" first, and the
/// main pipeline's ReviewOutput.OverallScore actually serializes as "overallScore", a pre-existing
/// mismatch that meant the main pipeline's review score silently always parsed as 0 (found while
/// wiring this agent's own score through the same method — fixed there by also accepting
/// "overallScore" as a fallback, but this type is named correctly from the start regardless).
/// </remarks>
public class VideoReviewOutput
{
    public int Score { get; set; }
    public bool PassesReview { get; set; }
    public List<string> Issues { get; set; } = new();
    public List<string> Strengths { get; set; } = new();
    public string Summary { get; set; } = string.Empty;

    /// <summary>Setting changes for this run only (docs/design/review-loop-adjustments.md); empty unless offered.</summary>
    public List<ReviewSettingAdjustment> Adjustments { get; set; } = new();

    /// <summary>Frames to look at more closely before the final answer; empty unless targeted looks are offered.</summary>
    public List<ReviewLookRequestItem> Looks { get; set; } = new();
}

// ============================================================================
// FILE PROCESSING AGENT OUTPUT SCHEMAS
// ============================================================================

/// <summary>
/// Structured output for FileSummarizerAgent.
/// </summary>
public class FileSummaryOutput
{
    public string FileType { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<string> KeyPoints { get; set; } = new();
    public string VideoRelevance { get; set; } = string.Empty;
    public List<string> NotablePatterns { get; set; } = new();
}

// ============================================================================
// VIDEO EDITING AGENT OUTPUT SCHEMAS
// ============================================================================

/// <summary>
/// A single contiguous run of offered ids to keep, inclusive on both ends.
/// </summary>
/// <remarks>
/// THE RUSHCUT INVARIANT: this class, and <see cref="VideoEditDecisionOutput"/> as a whole,
/// must never gain a numeric or time-bearing property (no double/int-as-seconds-or-frames, no
/// TimeSpan, no DateTime). The model is never trusted with a timestamp — its only contribution
/// to the cut list is a set of opaque string ids drawn from the set it was actually shown
/// (VideoAnalysisArtifact.OfferedIds). Resolving ids to frame-accurate times is
/// VideoCompileStepExecutor's job, done entirely from the full analysis artifact. A future
/// "helpful" addition of e.g. StartSec here would defeat the entire point of this contract —
/// see the reflection test asserting this type is structurally incapable of expressing a time.
/// </remarks>
public class VideoEditKeepSpan
{
    /// <summary>First offered id to keep, inclusive. e.g. "t7" or "s2".</summary>
    public string FromId { get; set; } = string.Empty;

    /// <summary>Last offered id to keep, inclusive. Must be >= FromId in the offered order.</summary>
    public string ToId { get; set; } = string.Empty;

    /// <summary>Why this span stays. Prose only.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// How this span comes IN from the previous one: a preset WORD ("Fade", "SlideLeft",
    /// "FlashWhite", "Cut", ...) from the compile's catalog, or empty to leave the seam to the
    /// measured rules. Applied only under <c>TransitionPolicy = Editor</c>; ignored on the first span.
    /// </summary>
    public string Transition { get; set; } = string.Empty;

    /// <summary>The transition's speed as a WORD: Snap, Quick, Smooth or Slow. Never a number.</summary>
    public string TransitionSpeed { get; set; } = string.Empty;
}

/// <summary>
/// Structured output for VideoStoryEditorAgent. Single representation only (<see cref="Keep"/>)
/// — there is deliberately no "remove" list, since two ways to express the same edit would
/// create a resolution order to get wrong. Everything not covered by a kept span is cut.
/// </summary>
public class VideoEditDecisionOutput
{
    /// <summary>
    /// Ordered, strictly increasing, non-overlapping spans of offered ids to keep.
    /// </summary>
    public List<VideoEditKeepSpan> Keep { get; set; } = new();

    public string EditRationale { get; set; } = string.Empty;

    public string SuggestedTitle { get; set; } = string.Empty;

    /// <summary>
    /// The one opt-out from the interleave rule (docs/video-editing.md "Using every clip offered"):
    /// when the view offered more than one source clip and this decision keeps material from only
    /// one of them (or keeps a composition as one unbroken run with nothing placed between its
    /// scenes), this must be a VERBATIM quote from the user's brief that excludes the other
    /// material. <c>InterleaveGuard</c> checks it in code against the run's user request — a
    /// paraphrase or an invented restriction fails. Empty whenever no opt-out is claimed.
    /// </summary>
    /// <remarks>
    /// A plain string on purpose, like every other member here: the rushcut invariant forbids a
    /// numeric or time-bearing property, and a quote is neither.
    /// </remarks>
    public string SingleSourceJustification { get; set; } = string.Empty;
    /// <summary>
    /// Optional dynamic-edit moves from the closed catalogue (docs/design/dynamic-edit.md). Empty or
    /// null is valid. Words and offered ids only: every number is computed by the compile.
    /// </summary>
    public List<DynamicsEntryOutput> Dynamics { get; set; } = new();
}
/// <summary>
/// One dynamic-edit move. Strings only (rushcut invariant): <see cref="Effect"/>, <see cref="Moment"/>,
/// <see cref="Beats"/>, <see cref="Division"/>, <see cref="Winner"/> and <see cref="Intensity"/> are words
/// parsed case-insensitively by the compile; ids are offered ids. An unknown word or id drops the entry.
/// </summary>
public class DynamicsEntryOutput
{
    /// <summary>A DynamicEffect word: SplitOnBeat, PunchIn, PunchOut, BeatAlternate, StrobeCut, FlashFrame, SpeedRamp, WhipOnBar.</summary>
    public string Effect { get; set; } = string.Empty;
    /// <summary>An offered id the move anchors to.</summary>
    public string AnchorId { get; set; } = string.Empty;
    /// <summary>The second offered id (BeatAlternate, StrobeCut); empty otherwise.</summary>
    public string OtherId { get; set; } = string.Empty;
    /// <summary>OnBeat, OnBar, OnDrop or OnAccent.</summary>
    public string Moment { get; set; } = string.Empty;
    /// <summary>Two, Four, Eight or Sixteen (BeatAlternate); empty otherwise.</summary>
    public string Beats { get; set; } = string.Empty;
    /// <summary>Beat, HalfBeat or Bar (BeatAlternate, StrobeCut); empty otherwise.</summary>
    public string Division { get; set; } = string.Empty;
    /// <summary>A or B (BeatAlternate); empty otherwise.</summary>
    public string Winner { get; set; } = string.Empty;
    /// <summary>Subtle, Normal or Strong; empty for the pace default.</summary>
    public string Intensity { get; set; } = string.Empty;
    /// <summary>Why. Prose only.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Structured output for <c>DynamicsPlannerAgent</c>: the dynamic-edit moves chosen by a late agent that is offered the generated
/// clip ids. The list is the same string-only <see cref="DynamicsEntryOutput"/> the story editor emits, so the rushcut invariant
/// holds by construction (see <c>DynamicsPlanOutputInvariantTests</c>): words and offered ids, never a number or a time. An empty
/// <see cref="Dynamics"/> is a fully valid outcome.
/// </summary>
public class DynamicsPlanOutput
{
    public List<DynamicsEntryOutput> Dynamics { get; set; } = new();
    public string PlanRationale { get; set; } = string.Empty;
}
/// <summary>
/// One planned motion-graphics overlay (lower-third, title, callout, tag). Structured output for
/// <c>MotionGraphicsPlannerAgent</c> (Phase 3 — see docs/video-editing.md "Motion graphics").
/// </summary>
/// <remarks>
/// THE SAME RUSHCUT INVARIANT, extended to graphics: this class, and
/// <see cref="MotionGraphicsPlanOutput"/> as a whole, must never gain a numeric or time-bearing
/// property, and must never carry a pixel coordinate. The model's only contribution is an opaque
/// <see cref="PlacementId"/> drawn from the set it was actually offered
/// (<c>VideoAnalysisArtifact.OfferedPlacementIds</c>) plus enum-word choices
/// (<see cref="Duration"/>/<see cref="Emphasis"/>) that <c>VideoCompileStepExecutor</c> alone
/// resolves to milliseconds/style — never a number the model supplied directly. See the
/// reflection test asserting this type is structurally incapable of expressing a time or a
/// position.
/// </remarks>
public class MotionGraphicsOverlay
{
    /// <summary>
    /// Must be one of the ids in <c>VideoAnalysisArtifact.OfferedPlacementIds</c> — never invented. Empty when the
    /// overlay is anchored to an event through <see cref="EventId"/> instead: exactly one of the two is set.
    /// </summary>
    public string PlacementId { get; set; } = string.Empty;

    /// <summary>
    /// Optional: an <c>e{n}</c> id from <c>VideoAnalysisArtifact.OfferedEventIds</c> (the view's <c>events</c> list)
    /// instead of <see cref="PlacementId"/>. The overlay starts on that event's moment and sits where
    /// <see cref="Kind"/> says (a Callout or Tag uses the event's region, in thirds). A string id only — never a time or a
    /// position. Empty (the default) keeps the placement path exactly as before. See docs/video-editing.md "Graphics on an event".
    /// </summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>
    /// One of: LowerThird | Title | Callout | Tag.
    /// </summary>
    /// <remarks>
    /// On an overlay anchored to a placement (<see cref="PlacementId"/>) this is descriptive only, as it always was:
    /// position and size come from the placement's <c>Rect</c> and from <see cref="Emphasis"/>. On an overlay anchored to
    /// an event (<see cref="EventId"/>) it is the layout word — LowerThird (the lower band), Title (the centre band),
    /// Callout (the band of the event's row), Tag (the third of that band under the event). An empty or unknown word lays
    /// out as a Callout. The numbers behind each word live in <c>EventOverlayLayout</c>, never in a plan.
    /// </remarks>
    public string Kind { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string Subtext { get; set; } = string.Empty;

    /// <summary>
    /// One of: Short | Medium | Hold — never a number. Mapped to actual milliseconds
    /// (<c>VideoCompileStepConfig.OverlayShortMs</c>/<c>OverlayMediumMs</c>/<c>OverlayHoldMs</c>)
    /// entirely server-side.
    /// </summary>
    public string Duration { get; set; } = string.Empty;

    /// <summary>One of: Subtle | Normal | Strong.</summary>
    public string Emphasis { get; set; } = string.Empty;

    /// <summary>Why this overlay was chosen. Prose only.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Optional: the S3 storage key of a rendered, transparent-background motion-graphics asset
    /// this agent produced ITSELF by actually calling <c>RenderVideoAndUploadToStorage</c> — a
    /// real tool call that performs a real render and a real S3 upload, unlike <see cref="PlacementId"/>
    /// which is merely echoed back from a set the model was shown. Still not trusted blindly:
    /// <c>VideoCompileStepExecutor</c> validates this key matches the exact
    /// <c>projects/{projectId}/outputFiles/{executionId}/...</c> prefix
    /// <c>RenderVideoAndUploadToStorage</c> itself constructs for the CURRENT execution before
    /// downloading or compositing anything at this key — the same prefix-validation discipline
    /// <c>StepResultArtifactsController</c> already applies to <c>ArtifactStorageKey</c>.
    /// </summary>
    /// <remarks>
    /// When non-empty, this overlay is composited via ffmpeg's <c>overlay</c> filter (the asset is
    /// added as an extra input, scaled to the resolved placement box, and alpha-blended) INSTEAD
    /// of the plain-text drawbox/drawtext path — <see cref="Text"/>/<see cref="Subtext"/> are
    /// drawn for an overlay that carries a rendered asset only when the engine measured that asset's
    /// type below the legibility floor (docs/design/graphics-see-and-size.md), as its fallback.
    /// Empty/absent (the default) leaves
    /// this overlay a plain text overlay exactly as before this field existed — additive, not a
    /// replacement. An overlay is one or the other, never both: author two separate
    /// <see cref="MotionGraphicsOverlay"/> entries (at different placements) to combine a
    /// rendered graphic with separate caption text.
    /// </remarks>
    public string RenderedAssetStorageKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional WORD, one of: "" (legacy) | "Output". "Output" says the rendered asset is a
    /// transparent composition exactly the size of the output canvas the view's
    /// <c>graphicsCanvas</c> states, with the type placed inside the placement's band by the agent's
    /// own composition; the compile then composites it full frame at its designed size instead of
    /// fitting it into the band. Empty keeps the legacy band fit. Appended; never a number — see
    /// docs/design/graphics-see-and-size.md.
    /// </summary>
    public string AssetCanvas { get; set; } = string.Empty;

    /// <summary>
    /// Optional WORD, one of: Small | Medium | Large | XLarge — the cap height of the overlay's main
    /// line. <c>OverlayTextSizing.CapHeightPct</c> alone maps it to a percentage of the output
    /// height (never below the compile's <c>MinOverlayCapHeightPct</c>). Sizes a plain-text overlay
    /// and states the size a rendered asset was designed at. Empty keeps the configured size.
    /// </summary>
    public string TextSize { get; set; } = string.Empty;

    /// <summary>
    /// Optional WORD, one of: Top | Center | Bottom — where a plain-text overlay sits inside its
    /// placement's band. Empty keeps the region's own edge (LowerThird bottom, UpperThird top).
    /// </summary>
    public string Anchor { get; set; } = string.Empty;
}

/// <summary>
/// One planned tracked screen insert: a Remotion-rendered scene composited INTO a tracked
/// chroma-plate region of the source footage (e.g. an app UI inserted into a phone's green-screen
/// area, warping with the phone as the hand moves) — see docs/video-editing.md "Tracked screen
/// inserts (Phase 5)".
/// </summary>
/// <remarks>
/// THE SAME RUSHCUT INVARIANT, extended to tracked compositing: this class must never gain a
/// numeric, time-bearing, or coordinate-bearing property. Motion tracking is inherently per-frame
/// numeric data — every one of those numbers is computed by deterministic C#
/// (<c>ChromaQuadTracker</c>) and consumed by deterministic C#
/// (<c>VideoCompileStepExecutor</c>/<c>ScreenInsertFilterBuilder</c>); the model's only
/// contribution is an opaque <see cref="RegionId"/> drawn from the set it was actually offered
/// (<c>VideoAnalysisArtifact.OfferedInsertRegionIds</c>) plus a rendered asset it produced itself
/// via a real <c>RenderVideoAndUploadToStorage</c> call (the exact
/// <see cref="MotionGraphicsOverlay.RenderedAssetStorageKey"/> precedent, validated the same way).
/// See <c>MotionGraphicsPlanOutputInvariantTests</c>.
/// </remarks>
public class ScreenInsert
{
    /// <summary>Must be one of the ids in <c>VideoAnalysisArtifact.OfferedInsertRegionIds</c> (e.g. "r0") — never invented.</summary>
    public string RegionId { get; set; } = string.Empty;

    /// <summary>
    /// REQUIRED (unlike an overlay, an insert has no plain-text fallback): the storage key of the
    /// rendered scene to composite into the tracked region, produced by a real
    /// <c>RenderVideoAndUploadToStorage</c> call in THIS run. Validated against this execution's
    /// own <c>projects/{projectId}/outputFiles/{executionId}/...</c> prefix before anything is
    /// downloaded or composited — same discipline as
    /// <see cref="MotionGraphicsOverlay.RenderedAssetStorageKey"/>.
    /// </summary>
    public string RenderedAssetStorageKey { get; set; } = string.Empty;

    /// <summary>Why this insert was chosen. Prose only.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Structured output for <c>MotionGraphicsPlannerAgent</c>. Zero or more overlays, each anchored
/// only to a placement id offered by a <c>StepType.VideoAnalyze</c> step
/// (<c>view.placements</c>) — never a timestamp or pixel coordinate anywhere in this type — plus
/// zero or more tracked screen inserts anchored only to offered insert-region ids
/// (<c>view.insertRegions</c>).
/// </summary>
public class MotionGraphicsPlanOutput
{
    public List<MotionGraphicsOverlay> Overlays { get; set; } = new();

    /// <summary>
    /// Tracked screen inserts (docs/video-editing.md "Tracked screen inserts (Phase 5)"). Empty by
    /// default and for every plan produced before this field existed — additive, never required.
    /// Only applied when <c>VideoCompileStepConfig.EnableInserts</c> is on.
    /// </summary>
    public List<ScreenInsert> Inserts { get; set; } = new();

    public string PlanRationale { get; set; } = string.Empty;
}

/// <summary>
/// One planned picture or video shown over the film (design
/// <c>docs/design/picture-overlay.md</c> §5.2). The model contributes only an opaque media id
/// drawn from the set it was offered (<c>view.overlayMedia</c> / <c>VideoAnalysisArtifact.OfferedMediaIds</c>),
/// an opaque overlay-window id drawn from <c>view.overlayWindows</c> /
/// <c>VideoAnalysisArtifact.OfferedOverlayWindowIds</c>, and enum WORDS for form, anchor, size,
/// border and sound that <c>VideoCompileStepExecutor</c> alone resolves to concrete geometry,
/// timing, level and filter strings — never a timestamp and never a coordinate.
/// </summary>
/// <remarks>
/// THE RUSHCUT INVARIANT, extended to pictures: every property is a plain string except
/// <see cref="OpacityPercent"/>, the one documented numeric exemption — a bounded render constant
/// with no temporal or spatial meaning, which the compile clamps regardless. Guarded by
/// <c>MediaOverlayPlanOutputInvariantTests</c>.
/// </remarks>
public class MediaOverlayChoice
{
    /// <summary>Must be one of the ids in <c>view.overlayMedia</c> (e.g. "m0") — never invented.</summary>
    public string MediaId { get; set; } = string.Empty;

    /// <summary>Must be one of the ids in <c>view.overlayWindows</c> (e.g. "o0") — never invented.</summary>
    public string WindowId { get; set; } = string.Empty;

    /// <summary>One of: Card | PictureInPicture.</summary>
    public string Form { get; set; } = string.Empty;

    /// <summary>
    /// One of the nine edge anchors, case-insensitive: TopLeft | Top | TopRight | Left | Center |
    /// Right | BottomLeft | Bottom | BottomRight.
    /// </summary>
    public string Anchor { get; set; } = string.Empty;

    /// <summary>One of: Small | Medium | Large.</summary>
    public string Size { get; set; } = string.Empty;

    /// <summary>
    /// 0..100. THE SINGLE DOCUMENTED NUMERIC EXEMPTION from the rushcut invariant: a bounded
    /// render constant with no temporal or spatial meaning. The compile clamps it regardless of
    /// what the model sends.
    /// </summary>
    public int OpacityPercent { get; set; } = 100;

    /// <summary>One of: None | Light | Dark.</summary>
    public string Border { get; set; } = string.Empty;

    /// <summary>Videos only. One of: Muted | Mixed.</summary>
    public string Audio { get; set; } = string.Empty;

    /// <summary>Videos only. One of: Quiet | Balanced | Prominent.</summary>
    public string AudioLevel { get; set; } = string.Empty;

    /// <summary>Cards only. One of: Keep | Mute.</summary>
    public string CardAudio { get; set; } = string.Empty;

    /// <summary>Why this picture was chosen. Plain words only — never a timestamp or coordinate.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Structured output for <c>OverlayPlannerAgent</c>: zero or more pictures or videos shown over
/// the film, each anchored only to a media id and an overlay-window id the agent was actually
/// offered. Empty is a valid, first-class answer.
/// </summary>
public class MediaOverlayPlanOutput
{
    public List<MediaOverlayChoice> Overlays { get; set; } = new();

    /// <summary>Overall explanation of the picture plan. Plain prose only.</summary>
    public string PlanRationale { get; set; } = string.Empty;
}

// ============================================================================
// BACKGROUND MUSIC AGENT OUTPUT SCHEMA (see docs/video-editing.md "Background music")
// ============================================================================

/// <summary>
/// Structured output for <c>MusicSupervisorAgent</c>. Flat, single-track, no list: one background
/// bed per edit in v1 (no cue sheet, no per-section music). Every property is a plain string, so
/// the same rushcut invariant <see cref="VideoEditDecisionOutput"/>/<see cref="MotionGraphicsPlanOutput"/>
/// established holds here by construction — there is no numeric/time-bearing CLR type to even ban.
/// </summary>
/// <remarks>
/// The model's only contribution is an opaque <see cref="TrackId"/> drawn from the set it was
/// actually offered (<c>VideoAnalysisArtifact.OfferedMusicIds</c>) plus enum-word choices
/// (<see cref="Intensity"/>/<see cref="Ducking"/>/<see cref="Fit"/>) that
/// <c>VideoCompileStepExecutor</c> alone resolves to dB levels/ffmpeg behavior — never a dB value,
/// a volume, a level, a percentage, or a timestamp/duration in seconds anywhere in this type. See
/// <c>MusicPlanOutputInvariantTests</c>.
/// </remarks>
public class MusicPlanOutput
{
    /// <summary>Must be one of the ids in <c>VideoAnalysisArtifact.OfferedMusicIds</c> — never invented. Empty when no track suits the edit.</summary>
    public string TrackId { get; set; } = string.Empty;

    /// <summary>One of: Quiet | Balanced | Feature. Never a dB number — mapped to a bed level entirely server-side.</summary>
    public string Intensity { get; set; } = string.Empty;

    /// <summary>One of: Off | Light | Normal | Heavy. Never a dB number — mapped to an attenuation entirely server-side.</summary>
    public string Ducking { get; set; } = string.Empty;

    /// <summary>One of: LoopToFit | PlayOnce.</summary>
    public string Fit { get; set; } = string.Empty;

    /// <summary>Why this track/settings were chosen. Prose only.</summary>
    public string Reason { get; set; } = string.Empty;

    public string PlanRationale { get; set; } = string.Empty;
}

// ============================================================================
// SOUND EFFECTS AGENT OUTPUT SCHEMA (see docs/video-editing.md "Sound effects")
// ============================================================================

/// <summary>
/// One planned sound-effect cue: an offered SFX clip played at the moment an offered cut-anchor
/// item begins in the compiled output. Structured output element for <c>SoundDesignerAgent</c> —
/// see docs/video-editing.md "Sound effects".
/// </summary>
/// <remarks>
/// THE SAME RUSHCUT INVARIANT, extended to sound effects: this class, and
/// <see cref="SfxPlanOutput"/> as a whole, must never gain a numeric or time-bearing property —
/// every property is a plain string (guarded by <c>SfxPlanOutputInvariantTests</c>), so the model
/// is physically incapable of emitting a timestamp, an offset in milliseconds, a dB value, or a
/// duration. Its only contributions are an opaque <see cref="SfxId"/> drawn from the set it was
/// actually offered (<c>VideoAnalysisArtifact.OfferedSfxIds</c>), an opaque <see cref="AnchorId"/>
/// drawn from the CUT-ANCHOR set it was offered (<c>VideoAnalysisArtifact.OfferedIds</c> —
/// deliberately the same ids a Keep span may name, since "this sound fires when this shot/gap/
/// segment begins" is the whole anchoring model), and enum-word <see cref="Timing"/>/
/// <see cref="Volume"/> choices that <c>VideoCompileStepExecutor</c> alone resolves to a concrete
/// output-timeline offset and gain.
/// </remarks>
public class SfxCue
{
    /// <summary>Must be one of the ids in <c>VideoAnalysisArtifact.OfferedSfxIds</c> (e.g. "x0") — never invented, never a music-track/placement id.</summary>
    public string SfxId { get; set; } = string.Empty;

    /// <summary>
    /// Must be one of the CUT-ANCHOR ids in <c>VideoAnalysisArtifact.OfferedIds</c> (a shot
    /// "s{n}", silence gap "g{n}", or transcript segment "t{n}"), or (T-110) one of the
    /// composition-event ids in <c>VideoAnalysisArtifact.OfferedEventIds</c> ("e{n}", an event
    /// inside a render the analysis saw as a source or as pinned insert content). The cue fires
    /// at the moment this item BEGINS (or the event happens) in the compiled output — an anchor
    /// whose moment was cut away by the edit decision is dropped server-side, never guessed at.
    /// Still an opaque id, never a time.
    /// </summary>
    public string AnchorId { get; set; } = string.Empty;

    /// <summary>
    /// One of: OnCut | Lead | Lag — never a number. OnCut fires exactly at the anchor's start
    /// moment; Lead slightly before it; Lag slightly after it. The actual offsets are
    /// server-side config (<c>VideoCompileStepConfig.SfxLeadMs</c>/<c>SfxLagMs</c>).
    /// </summary>
    public string Timing { get; set; } = string.Empty;

    /// <summary>One of: Subtle | Normal | Strong — never a dB number. Mapped to a gain entirely server-side.</summary>
    public string Volume { get; set; } = string.Empty;

    /// <summary>Why this cue, here. Prose only.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Structured output for <c>SoundDesignerAgent</c>: zero or more discrete sound-effect cues.
/// Unlike <see cref="MusicPlanOutput"/> (one continuous bed for the whole program, at most one
/// track), SFX is naturally a LIST of short moments — but each element carries only opaque
/// offered ids plus enum words, so the rushcut invariant holds by construction (no numeric or
/// time-bearing CLR type anywhere — see <c>SfxPlanOutputInvariantTests</c>). An EMPTY
/// <see cref="Cues"/> list is a fully valid outcome: no effect is better than a gratuitous one.
/// </summary>
public class SfxPlanOutput
{
    public List<SfxCue> Cues { get; set; } = new();

    public string PlanRationale { get; set; } = string.Empty;
}

// ============================================================================
// MOTION DIRECTOR OUTPUT SCHEMA (see docs/video-editing.md "Motion on events")
// ============================================================================

/// <summary>
/// One planned motion cue. Strings only (the rushcut invariant, see <c>MotionPlanOutputInvariantTests</c>): an opaque
/// offered event id plus enum words. <c>VideoCompileStepExecutor</c> alone resolves the id to a frame and the words to the
/// primitives' own enums (<c>EventMoveKind</c>, <c>SpeedRampKind</c>, <c>FlashColour</c>...), whose numbers live in first-party tables.
/// </summary>
public class MotionCue
{
    /// <summary>Must be an id in <c>VideoAnalysisArtifact.OfferedEventIds</c> ("e{n}") - never invented, never a time.</summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>
    /// One of: PunchIn | PushIn | PullOut | PanLeft | PanRight | PanUp | PanDown | Shake (camera moves),
    /// SlowMo | SpeedUp | FreezeHit | RampIn | RampOut (speed), Flash, Graphic.
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>One of: Subtle | Normal | Strong. Move intensity, ramp strength or flash strength; never a number.</summary>
    public string Intensity { get; set; } = string.Empty;

    /// <summary>One of: Short | Medium | Long. Move hold, ramp span or flash length; empty reads as Medium. Never a number.</summary>
    public string Length { get; set; } = string.Empty;

    /// <summary>Flash only: White | Warm | Cool. Empty reads as White.</summary>
    public string Colour { get; set; } = string.Empty;

    /// <summary>Graphic only: LowerThird | Title | Callout | Tag (the layout word of a graphic on an event).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Graphic only: the few words the graphic shows. Prose, sanitised and capped by the compile.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Why this cue. Prose only.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Structured output for <c>MotionDirectorAgent</c>. An EMPTY <see cref="Cues"/> list is a fully valid outcome: no motion is
/// better than gratuitous motion. Applied by <c>VideoCompile</c> (<c>EnableMotion</c> + <c>MotionPlan</c>).
/// </summary>
public class MotionPlanOutput
{
    public List<MotionCue> Cues { get; set; } = new();

    public string PlanRationale { get; set; } = string.Empty;
}

// ============================================================================
// COLOR GRADE AGENT OUTPUT SCHEMA (see docs/video-editing.md "Color grading")
// ============================================================================

/// <summary>
/// Structured output for <c>ColoristAgent</c> (and, verbatim-reused, for
/// <c>ColorGradeDirectorAgent</c>'s <c>StepType.ColorGradeRoom</c> synthesis call). Flat,
/// single-grade, whole-program: one grade treatment applied to the entire compiled edit in v1 —
/// no per-shot or per-look-group grades, no keyframes, no cue points. Every property is a plain
/// string, so the rushcut invariant <see cref="VideoEditDecisionOutput"/>/
/// <see cref="MusicPlanOutput"/> established holds here by construction — there is no numeric or
/// time-bearing CLR type to even ban.
/// </summary>
/// <remarks>
/// The model's only contribution is a handful of enum-word choices (<see cref="Look"/>/
/// <see cref="Strength"/>/<see cref="ShadowTone"/>/<see cref="HighlightTone"/>) that
/// <c>VideoCompileStepExecutor</c> alone resolves to concrete ffmpeg
/// <c>eq</c>/<c>colorbalance</c>/<c>colorlevels</c>/<c>hue</c> parameters from first-party tables
/// (<c>ColorGradeFilterBuilder</c>) — never an RGB value, a curve point, a gamma/gain/contrast
/// number, a percentage, or a timestamp anywhere in this type. See
/// <c>ColorGradePlanOutputInvariantTests</c>.
/// </remarks>
public class ColorGradePlanOutput
{
    /// <summary>
    /// One of: None | Warm | Cool | Filmic | Vibrant | Muted | Mono. "None" declines the whole
    /// grade — the compile applies no colour filter at all, a perfectly valid outcome for footage
    /// that is already well exposed and consistent. Never a numeric colour value — mapped to
    /// concrete filter parameters entirely server-side.
    /// </summary>
    public string Look { get; set; } = string.Empty;

    /// <summary>One of: Subtle | Normal | Strong. Never a number — a server-side multiplier over the look's own first-party parameter table.</summary>
    public string Strength { get; set; } = string.Empty;

    /// <summary>One of: Neutral | Lifted | Deepened. Never a level value — mapped to a colorlevels black-point shift entirely server-side.</summary>
    public string ShadowTone { get; set; } = string.Empty;

    /// <summary>One of: Neutral | Softened | Brightened. Never a level value — mapped to a colorlevels white-point shift entirely server-side.</summary>
    public string HighlightTone { get; set; } = string.Empty;

    /// <summary>Why this grade was chosen for this footage. Prose only.</summary>
    public string Reason { get; set; } = string.Empty;

    public string PlanRationale { get; set; } = string.Empty;
}

// ============================================================================
// GENERATED SHOT PLAN AGENT OUTPUT SCHEMA (see docs/video-generation.md "Planning the shots")
// ============================================================================

/// <summary>
/// One planned generated shot: a free-prose generation prompt plus the editorial anchors that say
/// WHERE it belongs in the compiled edit and how it should look. Structured output element for
/// <c>ShotDirectorAgent</c> — see docs/video-generation.md "Planning the shots".
/// </summary>
/// <remarks>
/// THE SAME RUSHCUT INVARIANT, extended to generated video: this class, and
/// <see cref="GeneratedShotPlanOutput"/> as a whole, must never gain a numeric or time-bearing
/// property — every property is a plain string (guarded by
/// <c>GeneratedShotPlanOutputInvariantTests</c>), so the model is physically incapable of emitting
/// a timestamp, a frame index, a duration in seconds, a resolution, or a URL. Its only
/// contributions are opaque offered ids (<see cref="Anchor"/> and the optional
/// <see cref="FirstFrame"/>/<see cref="LastFrame"/> keyframes, all drawn from the CUT-ANCHOR set a
/// <c>VideoAnalyze</c> step actually offered — <c>VideoAnalysisArtifact.OfferedIds</c>), enum-word
/// choices (<see cref="Purpose"/>/<see cref="Camera"/>/<see cref="Duration"/>), and the free-prose
/// <see cref="Prompt"/> — which is the ONE field that leaves the building, and is therefore run
/// through <c>GeneratedPromptSanitizer</c> before it reaches a provider. Every enum word is
/// resolved to concrete provider parameters (model duration, aspect ratio, camera instruction
/// phrasing) by first-party C# in the deterministic <c>VideoGenerate</c> step.
/// </remarks>
public class ShotPlan
{
    /// <summary>
    /// What this shot is FOR in the edit — one of: Cutaway | SeamBridge | Extend | ColdOpen |
    /// EndCard | ScreenContent. This is what the compile step's placement table switches on;
    /// each placement degrades per clip rather than failing the step.
    /// </summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>
    /// Must be one of the CUT-ANCHOR ids in <c>VideoAnalysisArtifact.OfferedIds</c> (a shot
    /// "s{n}", silence gap "g{n}", or transcript segment "t{n}"). The generated clip is placed at
    /// the moment this item BEGINS in the compiled output — an anchor whose start was cut away by
    /// the edit decision is dropped server-side, never guessed at.
    /// </summary>
    public string Anchor { get; set; } = string.Empty;

    /// <summary>
    /// Optional: an offered "s{n}" shot id whose frame is used as the generated clip's first
    /// frame, so the shot starts visually continuous with the edit. Requires
    /// <c>VideoGenerateStepConfig.AllowSourceMediaEgress</c> — a keyframe IS the user's own
    /// footage, and sending it to a third-party provider without explicit consent is refused
    /// server-side with a clear reason. Null when the shot does not need a keyframe.
    /// </summary>
    public string? FirstFrame { get; set; }

    /// <summary>
    /// Optional: an offered "s{n}" shot id whose frame is used as the generated clip's last
    /// frame. Same egress rule as <see cref="FirstFrame"/>. Null when unused.
    /// </summary>
    public string? LastFrame { get; set; }

    /// <summary>
    /// One of the first-party camera-move enum words (see the executor's mapper for the exact
    /// list — deliberately NOT restated here so there is a single source of truth). Never a
    /// pixel coordinate, an angle, or a numeric camera parameter — resolved to provider phrasing
    /// entirely server-side.
    /// </summary>
    public string Camera { get; set; } = string.Empty;

    /// <summary>
    /// One of: Short | Medium | Long. Never seconds — mapped to a concrete, provider-supported
    /// duration entirely server-side (and snapped to the nearest duration the resolved model
    /// actually offers).
    /// </summary>
    public string Duration { get; set; } = string.Empty;

    /// <summary>
    /// The free-prose generation prompt, describing the shot's content. This is the ONLY field
    /// this agent authors that leaves the building — it is sanitized (length cap, control
    /// characters stripped) before it reaches a provider.
    /// </summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>Why this shot, here. Prose only; never sent to a provider.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Optional (docs/design/avatar-objects.md "Avatars and objects in planned shots"): the project's own people and
    /// objects this shot shows, as the opaque offer ids the <c>[PROJECT SUBJECTS]</c> block listed (<c>a1b2c3d4</c>),
    /// separated by spaces or commas; at most three, at most one of them a person. Never a name, a number or an avatar
    /// id: the deterministic <c>VideoGenerate</c> step resolves each offer id against the project's usable subjects and
    /// drops (and reports) one it cannot use. A shot that shows subjects never also takes a planned first or last frame.
    /// Null (the default, and the value of every plan made before subjects existed) shows none.
    /// </summary>
    public string? Subjects { get; set; }
}

/// <summary>
/// Structured output for <c>ShotDirectorAgent</c>: zero or more generated shots planned against a
/// bounded, id-anchored <c>VideoAnalyze</c> view, to be produced by a plan-sourced
/// <c>StepType.VideoGenerate</c> step and placed by <c>StepType.VideoCompile</c>. Each element
/// carries only opaque offered ids plus enum words, so the rushcut invariant holds by construction
/// (no numeric or time-bearing CLR type anywhere — see
/// <c>GeneratedShotPlanOutputInvariantTests</c>, which also pins the exact property set).
/// An EMPTY <see cref="Shots"/> list is a fully valid outcome: an edit needs no generated shot.
/// </summary>
public class GeneratedShotPlanOutput
{
    public List<ShotPlan> Shots { get; set; } = new();

    public string PlanRationale { get; set; } = string.Empty;
}

// ============================================================================
// NARRATION WRITER AGENT OUTPUT SCHEMA (see docs/voiceover.md)
// ============================================================================

/// <summary>
/// One planned narration line: the words a voice speaks, anchored to the moment an offered
/// cut-anchor item begins in the compiled output. Structured output element for
/// <c>NarrationWriterAgent</c> — see docs/voiceover.md.
/// </summary>
/// <remarks>
/// THE SAME RUSHCUT INVARIANT, extended to narration: this class, and
/// <see cref="NarrationPlanOutput"/> as a whole, must never gain a numeric or time-bearing
/// property — every property is a plain string (guarded by
/// <c>NarrationPlanOutputInvariantTests</c>, which additionally bans a property merely NAMED for
/// a time), so the model is physically incapable of emitting a timestamp, an offset in
/// milliseconds, a duration, a frame number, a pixel coordinate, or a percentage. Its only
/// contributions are an opaque <see cref="AnchorId"/> drawn from the CUT-ANCHOR set it was
/// actually offered (<c>VideoAnalysisArtifact.OfferedIds</c> — deliberately the same ids a Keep
/// span or an SFX cue may name) and prose: the spoken <see cref="Text"/>, a prose
/// <see cref="Delivery"/> direction, and the <see cref="Reason"/>. A separate deterministic step
/// measures how long the synthesized line actually runs against the shot it is anchored to and
/// reports any overflow back through the review loop — the model is never asked to count.
/// </remarks>
public class NarrationLine
{
    /// <summary>
    /// Must be one of the CUT-ANCHOR ids in <c>VideoAnalysisArtifact.OfferedIds</c> (a shot
    /// "s{n}", silence gap "g{n}", or transcript segment "t{n}"). The line begins to be spoken at
    /// the moment this item BEGINS in the compiled output — an anchor whose start was cut away by
    /// the edit decision is dropped server-side, never guessed at. Never a placement, music-track,
    /// sound-effect, or insert-region id.
    /// </summary>
    public string AnchorId { get; set; } = string.Empty;

    /// <summary>
    /// The words that will actually be SPOKEN, as prose — written the way they should be heard,
    /// not the way a document reads. No stage directions, no bracketed notes, no markup, no
    /// speaker label. Never a number the voice would have to interpret: a quantity that must be
    /// read as a word is written as a word.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// PROSE describing HOW the line is read — its tone, its pace relative to its neighbours,
    /// where it leans. A direction to a voice, never a machine-readable setting: never a rate, a
    /// pitch, a volume, a decibel value, a percentage, or an enum word a server-side table would
    /// map to one. Nothing in this value is ever parsed.
    /// </summary>
    public string Delivery { get; set; } = string.Empty;

    /// <summary>Why this line, at this anchor. Prose only; never spoken.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Structured output for <c>NarrationWriterAgent</c>: zero or more narration lines, each anchored
/// to an offered cut-anchor id. Like <see cref="SfxPlanOutput"/> (a list of short moments) rather
/// than <see cref="MusicPlanOutput"/> (one continuous bed), but each element carries only an
/// opaque offered id plus prose, so the rushcut invariant holds by construction — no numeric or
/// time-bearing CLR type anywhere, and no property even NAMED for a time (see
/// <c>NarrationPlanOutputInvariantTests</c>, which also pins the exact property set). An EMPTY
/// <see cref="Lines"/> list is a fully valid outcome: a silent stretch, or a wholly silent edit,
/// is a legitimate editorial choice rather than a failure, and <c>VoiceoverStepExecutor</c> honours
/// that — a NarrationPlan-sourced step carrying zero speakable lines completes with an empty
/// <c>{view, meta}</c> envelope whose <c>meta.emptyReason</c> names the provenance
/// (<c>"narration_plan_empty"</c> for a plan that declared no entries,
/// <c>"narration_plan_lines_discarded"</c> for one whose declared entries were all blank text) and
/// whose <c>meta.declaredLines</c> carries the declared count — never a failed step. (The <c>NO_LINES</c> failure belongs to the <c>Inline</c> and <c>ScriptScenes</c>
/// source kinds, where an empty resolved list is an author misconfiguration rather than a decision,
/// and to a plan that is malformed rather than merely empty.)
/// </summary>
public class NarrationPlanOutput
{
    public List<NarrationLine> Lines { get; set; } = new();

    public string PlanRationale { get; set; } = string.Empty;
}

/// <summary>
/// One pickup (see docs/voiceover.md "Pickups"): the corrected sentence for ONE offered
/// transcript segment the presenter flubbed. Every property is a plain string and none is named
/// for a time — the deterministic Voiceover step synthesizes the words and the compile step resolves
/// the segment's window from the analysis artifact; the model never counts.
/// </summary>
public class PickupLine
{
    /// <summary>Must be an offered transcript-segment id ("t{n}") the story editor kept. Never a shot, gap, placement, music, SFX or insert-region id.</summary>
    public string SegmentId { get; set; } = string.Empty;

    /// <summary>The corrected sentence exactly as it should be HEARD, in prose — the presenter's words with the flub fixed. No markup, no brackets, no speaker label.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>What was wrong with the original take. Prose only; never spoken.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Structured output for <c>PickupPlannerAgent</c>: zero or more pickups. Deliberately a SEPARATE
/// schema from <see cref="VideoEditDecisionOutput"/> — a repair is inherently temporal, and a
/// <c>repair</c> value on the story editor's output would have weakened the rushcut invariant that
/// output carries. This type keeps its own copy of that invariant (<c>PickupPlanOutputInvariantTests</c>).
/// An EMPTY <see cref="Pickups"/> list is the normal outcome for a clean take.
/// </summary>
public class PickupPlanOutput
{
    public List<PickupLine> Pickups { get; set; } = new();

    public string PlanRationale { get; set; } = string.Empty;
}

/// <summary>
/// Structured output for <c>CompositionReviserAgent</c> (AgentType.CompositionReviser): what a
/// revision of a finished composition changed. Descriptive only — the deliverable is the edited
/// workspace and, in render mode, the uploaded render the runner checks for itself
/// (<c>WorkflowExecutionContext.PendingOutputStorageKey</c>); nothing here is trusted as proof that
/// a render happened. See docs/design/composition-revisions.md.
/// </summary>
public class CompositionRevisionOutput
{
    /// <summary>One or two plain sentences a customer can read: what changed in the film.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>The workspace-relative files edited or created (e.g. <c>src/scenes/Intro.tsx</c>).</summary>
    public List<string> ChangedFiles { get; set; } = new();

    /// <summary>The names of the scenes the change touched, as the scene list names them.</summary>
    public List<string> ChangedScenes { get; set; } = new();

    /// <summary>The composition that was (or, in preview-only mode, would be) rendered.</summary>
    public string CompositionId { get; set; } = string.Empty;

    /// <summary>Whether the agent rendered and uploaded the film in this run.</summary>
    public bool Rendered { get; set; }

    /// <summary>Anything the requester should know: a part of the request that could not be done, and why.</summary>
    public string Notes { get; set; } = string.Empty;
}

/// <summary>
/// Structured output for <c>GraphicDesignerAgent</c> (AgentType.GraphicDesigner): what it designed.
/// Descriptive only: the deliverable is the composition in the sandbox, which the runner renders,
/// verifies and crops itself; nothing here is trusted as proof that a graphic exists. See
/// docs/editor.md "Designed graphics".
/// </summary>
public class GraphicDesignOutput
{
    /// <summary>One or two plain sentences a customer can read: what the graphic shows.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>The composition (a Remotion <c>Still</c>) that draws the graphic.</summary>
    public string CompositionId { get; set; } = string.Empty;

    /// <summary>The exact on-screen words the graphic shows, as the request wrote them.</summary>
    public List<string> OnScreenText { get; set; } = new();

    /// <summary>Anything the requester should know: a part of the request that could not be done, and why.</summary>
    public string Notes { get; set; } = string.Empty;
}

/// <summary>
/// One chosen generated take (issue #93 phase 4, dailies). <see cref="TakeId"/> must be one of the
/// <c>v{n}</c>/<c>v{n}t{k}</c> clip ids a GeneratedClips-sourced <c>VideoAnalyze</c> view offered in
/// <c>view.sources[].clipId</c>; the compile step's <c>TakeSelection</c> resolution drops anything
/// else. Prose only otherwise — never a score, a number, a timestamp or a storage key (the rushcut
/// invariant, pinned by <c>TakeSelectionOutputInvariantTests</c>).
/// </summary>
public class TakeSelection
{
    public string TakeId { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Structured output for <c>TakeSelectorAgent</c> / the dailies room's director: at most one take
/// per planned shot, chosen by offered take id. An EMPTY <see cref="Selected"/> list is a fully
/// valid outcome (every shot keeps its first delivered take downstream).
/// </summary>
public class TakeSelectionOutput
{
    public List<TakeSelection> Selected { get; set; } = new();

    public string PlanRationale { get; set; } = string.Empty;
}
