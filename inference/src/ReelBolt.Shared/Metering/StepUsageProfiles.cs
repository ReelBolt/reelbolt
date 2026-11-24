using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Shared.Metering;

/// <summary>Which provider a <see cref="ProfileQuantity"/> is priced against.</summary>
public enum ProfilePriceTarget
{
    /// <summary>The chat provider resolved for the profile's <see cref="StepUsageProfile.PricedAs"/> agent type.</summary>
    Chat,

    /// <summary>Platform compute (the system compute capability, one resolution tier as the price variant).</summary>
    Compute,

    /// <summary>The resolved text-to-speech provider.</summary>
    SpeechSynthesis,

    /// <summary>The resolved transcription (ASR) provider.</summary>
    Transcription,

    /// <summary>The resolved video-generation provider.</summary>
    VideoGeneration,

    /// <summary>The default vision provider (<c>ResolveVisionAsync(null)</c>): the ReviewLoop visual check's call (S-77). Appended.</summary>
    Vision,

    /// <summary>The default WebSearch provider (<c>ResolveWebSearchAsync(null)</c>): an agent step's worst-case searches. Appended.</summary>
    WebSearch
}

/// <summary>One price-independent quantity of a step's usage, and the provider it is priced against.</summary>
/// <param name="ProviderId">
/// The explicit provider a <see cref="ProfilePriceTarget.Vision"/> quantity is priced on (a ReviewLoop visual check's
/// <c>visionProviderId</c>), or a <see cref="ProfilePriceTarget.SpeechSynthesis"/> quantity of a Voiceover step whose speech model
/// an app chose (docs/design/app-inputs.md 28); null prices the default. Ignored by every other target.
/// </param>
public readonly record struct ProfileQuantity(UsageKind Kind, ProfilePriceTarget Target, decimal Quantity, Guid? ProviderId = null);

/// <summary>
/// One execution of one step at its median. <paramref name="LowFactor"/>/<paramref name="HighFactor"/>
/// follow <see cref="StepUsageProfiles"/>' factor rule (measured when n &gt;= 3, generic below), after its floors.
/// </summary>
/// <param name="PricedAs">
/// The agent type whose chat provider prices the <see cref="ProfilePriceTarget.Chat"/> quantities;
/// null for "the default chat" (or a profile with no chat part).
/// </param>
/// <param name="Measured">True when the figures come from at least 5 measured samples of this very step shape.</param>
/// <param name="Source">Where the numbers come from, for a reviewer (never shown to a user).</param>
public sealed record StepUsageProfile(
    IReadOnlyList<ProfileQuantity> Quantities,
    double LowFactor,
    double HighFactor,
    AgentType? PricedAs,
    bool Measured,
    string Source)
{
    /// <summary>True when any quantity is priced against a chat provider.</summary>
    public bool HasChatPart => Quantities.Any(q => q.Target == ProfilePriceTarget.Chat);
}

/// <summary>
/// The cold-start usage profiles behind the pre-run DISPLAY range, one per step type and agent type,
/// pinned from measurements (staging, 2026-10-12, and
/// the local dev DB where staging has no sample).
///
/// <para>
/// <b>The factor rule.</b> <c>LowFactor</c>/<c>HighFactor</c> are the measured p25/p50 and p75/p50
/// of the profile's source bucket when it has n &gt;= 3 samples, and the generic
/// <see cref="GenericLowFactor"/>/<see cref="GenericHighFactor"/> when it has fewer (or none: an
/// analogue). Either way the floors apply: never tighter than <see cref="MinLowFactor"/>–<see cref="MinHighFactor"/>.
/// <c>Measured</c> is a separate, stricter flag (n &gt;= 5).
/// </para>
///
/// <para>
/// <b>VideoGenerate has no profile.</b> On the display path it is always fixed-price: its declared
/// price, else the <see cref="VideoGenerateMaxClips"/> ceiling. It never enters the variable range.
/// </para>
///
/// <para>
/// <b>Quantities, not prices.</b> Each profile is the median usage of one execution (tokens, compute
/// seconds, TTS bytes, generated seconds). The estimator prices them at estimate time through the same
/// price lookup the meter uses (IModelPriceService), so a model choice, a markup or a multiplier changes the shown
/// figure without touching this table.
/// </para>
///
/// <para>
/// <b>These drive the cold-start hold.</b> A step with no history holds the larger of its
/// <see cref="StepCreditDefaults"/> default and its share of the low edge of these profiles' range
/// (<see cref="ColdStartHoldPlan"/>), so a profile's <c>LowFactor</c> moves <c>RequiredMillis</c> and the 402 preflight.
/// </para>
/// </summary>
public static class StepUsageProfiles
{
    /// <summary>The p25/p50 factor of a bucket with n &lt; 3 samples (or none).</summary>
    public const double GenericLowFactor = 0.6;

    /// <summary>The p75/p50 factor of a bucket with n &lt; 3 samples (or none).</summary>
    public const double GenericHighFactor = 1.8;

    /// <summary>Floor on the low factor: a small sample never claims a tighter bottom than this.</summary>
    public const double MinLowFactor = 0.75;

    /// <summary>Floor on the high factor: a small sample never claims a tighter top than this.</summary>
    public const double MinHighFactor = 1.33;

    /// <summary>The local dev DB's code-reading agents report total tokens only; staging's mix splits them 18 / 4 / 78.</summary>
    private const decimal LocalInputShare = 0.18m;
    private const decimal LocalOutputShare = 0.04m;
    private const decimal LocalCachedShare = 0.78m;

    private static readonly StepUsageProfile Empty = new([], 1d, 1d, null, true, "no provider call");

    // ------------------------------------------------------------------ staging, n >= 3 (measured factors; Measured = n >= 5)

    private static readonly StepUsageProfile AnimationStrategy =
        Agent(AgentType.AnimationStrategyAgent, 25_979, 17_740, 86_528, .79, 1.02, measured: true, "staging p50, n=7");

    // Cost-honesty T-001: the old staging p50 (303,593 in / 14,081 out / 632,576 cached / 98.7 s) was 5-8x the
    // input, output and render of a real production 15 s reel pass; on an Opus Author it quoted ~325 credits a pass
    // against 44-70 actually charged. Per-pass production medians (3 runs, 4 passes; one run was a 2-pass review
    // loop): 38,001 in / 2,877 out / 138,115 cached / 18 s render. Factors: the min/median/max of those runs' per-pass
    // charge (44.0 / 45.7 / 69.7 credits), not true p25/p50/p75. n=3, 15 s reels, all AuthorAgent models: longer films are quoted low.
    private static readonly StepUsageProfile Author = Agent(AgentType.AuthorAgent, 38_001, 2_877, 138_115, .96, 1.52,
        measured: false, "production per-pass median, n=3 runs", (UsageKind.RenderOutputSeconds, 18m))
        // Floored() clamps the low factor to <= 0.75, which would put the cold-start hold (the low edge) at ~36 credits,
        // under every real pass (44-75). The measured band is tight (44.0/45.7), so keep the real p25/p50 for the hold.
        with
    { LowFactor = .96 };

    private static readonly StepUsageProfile Director =
        Agent(AgentType.DirectorAgent, 21_352, 9_365, 67_712, .82, 2.68, measured: true, "staging p50, n=7");

    private static readonly StepUsageProfile RemotionTranslator =
        Agent(AgentType.RemotionComponentTranslator, 215_529, 61_430, 1_724_736, .70, 1.40, measured: true, "staging p50, n=8");

    private static readonly StepUsageProfile GraphicDesigner =
        Agent(AgentType.GraphicDesigner, 140_548, 25_934, 672_512, .43, 1.63, measured: false, "staging p50, n=4");

    // staging p25/p50/p75 3.4/6.8/6.8: .50/1.00, the top floored to 1.33.
    private static readonly StepUsageProfile VideoStoryEditor =
        Agent(AgentType.VideoStoryEditor, 130_270, 937, 51_456, .50, 1.00, measured: false, "staging p50, n=3");

    private static readonly StepUsageProfile VideoReview =
        Agent(AgentType.VideoReviewAgent, 16_078, 3_148, 5_888, .57, 1.64, measured: true, "staging p50, n=8");

    private static readonly StepUsageProfile CompositionReviser = Agent(AgentType.CompositionReviser, 123_452, 17_853, 563_968, .75, 1.15,
        measured: true, "staging p50, n=5", (UsageKind.RenderOutputSeconds, 118.5m));

    // ------------------------------------------------------------------ staging, n <= 2 (generic factors)

    private static readonly StepUsageProfile MotionGraphicsPlanner = Agent(AgentType.MotionGraphicsPlanner, 8_151, 849, 9_280,
        GenericLowFactor, GenericHighFactor, measured: false, "staging p50, n=2 (thin)");

    private static readonly StepUsageProfile Scriptwriter = Agent(AgentType.ScriptwriterAgent, 4_246, 4_732, 13_568,
        GenericLowFactor, GenericHighFactor, measured: false, "staging p50, n=2 (thin)");

    private static readonly StepUsageProfile Review = Agent(AgentType.ReviewAgent, 21_384, 11_050, 88_512,
        GenericLowFactor, GenericHighFactor, measured: false, "staging p50, n=2 (thin)");

    // ------------------------------------------------------------------ local dev DB totals, split by the staging mix (same rule)

    private static readonly StepUsageProfile CodeStructure =
        LocalTotal(AgentType.CodeStructureAnalyzer, 276_540, .89, 1.10, measured: false, "local p50 total tokens, n=4");

    private static readonly StepUsageProfile ComponentInventory =
        LocalTotal(AgentType.ComponentInventoryAnalyzer, 177_236, .96, 1.32, measured: false, "local p50 total tokens, n=4");

    private static readonly StepUsageProfile StyleAndTheme =
        LocalTotal(AgentType.StyleAndThemeExtractor, 611_929, .87, 1.11, measured: false, "local p50 total tokens, n=4");

    private static readonly StepUsageProfile Dependency = LocalTotal(AgentType.DependencyAnalyzer, 246_829,
        GenericLowFactor, GenericHighFactor, measured: false, "local total tokens, n=1 (thin)");

    private static readonly StepUsageProfile RouteAndApi = LocalTotal(AgentType.RouteAndApiAnalyzer, 495_931,
        GenericLowFactor, GenericHighFactor, measured: false, "local total tokens, n=1 (thin)");

    private static readonly StepUsageProfile NarrationWriter =
        LocalTotal(AgentType.NarrationWriter, 9_008, .62, 1.17, measured: true, "local p50 total tokens, n=141");

    // ------------------------------------------------------------------ rooms (seat-priced)

    private const long RoomInput = 22_732, RoomOutput = 7_360, RoomCached = 71_296;

    private static readonly StepUsageProfile EditRoom = Room(AgentType.VideoStoryEditor, 1m, .86, 2.04, measured: true, "staging p50, n=9");

    // ------------------------------------------------------------------ media and compute steps

    private static readonly StepUsageProfile VideoAnalyze = Floored(new(
        [
            new(UsageKind.AnalyzeMediaSeconds, ProfilePriceTarget.Compute, 138.7m),
            .. Tokens(721, 4_141, 3_386)
        ], .12, 1.06, null, true, "staging p50, n=12; tokens priced as the default chat"));

    /// <summary>A VideoAnalyze that calls no model or vendor (see <see cref="StepCreditDefaults.IsComputeOnly"/>): the processing time alone.</summary>
    private static readonly StepUsageProfile VideoAnalyzeComputeOnly = Floored(new(
        [new(UsageKind.AnalyzeMediaSeconds, ProfilePriceTarget.Compute, 138.7m)],
        .12, 1.06, null, true, "staging p50, n=12; compute part only (the step calls no model)"));

    private static readonly StepUsageProfile VideoCompile = Floored(new(
        [new(UsageKind.EncodeOutputSeconds, ProfilePriceTarget.Compute, 96.7m)], .31, 1.24, null, true, "staging p50, n=9"));

    private static readonly StepUsageProfile TimelineRender = Floored(new(
        [new(UsageKind.EncodeOutputSeconds, ProfilePriceTarget.Compute, 60m)],
        GenericLowFactor, GenericHighFactor, null, false, "analogue: a 60 s delivery encode (unmeasured)"));

    private static readonly StepUsageProfile ThumbnailGenerate = Floored(new(
        [new(UsageKind.EncodeOutputSeconds, ProfilePriceTarget.Compute, 3m)],
        GenericLowFactor, GenericHighFactor, null, false, "analogue: 3 s of frame decode (unmeasured)"));

    private static readonly StepUsageProfile Voiceover = Floored(new(
        [
            new(UsageKind.TtsInputBytes, ProfilePriceTarget.SpeechSynthesis, 1_200m),
            new(UsageKind.AsrAudioSeconds, ProfilePriceTarget.Transcription, 60m)
        ], GenericLowFactor, GenericHighFactor, null, false, "analogue: a ~60 s narration plus its alignment pass (unmeasured)"));

    /// <summary>
    /// A Voiceover step under the default <c>NarrationFitPolicy.Enforce</c> may shorten an overflowing line
    /// (ONE NarrationWriter chat call, metered inside the step's own hold) and re-synthesize the changed
    /// lines, so its hold carries +30% TTS bytes and one small chat call on top of <see cref="Voiceover"/>.
    /// A <c>Report</c> step holds exactly <see cref="Voiceover"/>. Holds only grow: nothing is removed.
    /// </summary>
    private static readonly StepUsageProfile VoiceoverEnforce = Floored(new(
        [
            new(UsageKind.TtsInputBytes, ProfilePriceTarget.SpeechSynthesis, 1_200m * 1.3m),
            new(UsageKind.AsrAudioSeconds, ProfilePriceTarget.Transcription, 60m),
            .. Tokens(1_500, 400, 0)
        ], GenericLowFactor, GenericHighFactor, AgentType.NarrationWriter, false,
        "analogue: a ~60 s narration plus its alignment pass, +30% TTS and one line-shortening chat call (Enforce)"));

    /// <summary>
    /// docs/design/app-inputs.md 28: a Voiceover step whose bound <c>providerId</c> is a model the run's app chose
    /// (<see cref="AppAgentModelAmbient.IsChosenStepModel"/>) prices its speech at that model, the one it will run on. Every
    /// other Voiceover step (no choice, or an author-set provider) prices the default speech model exactly as before.
    /// </summary>
    private static StepUsageProfile WithChosenSpeechModel(StepUsageProfile profile, string? voiceoverConfigJson)
    {
        if (AppAgentModelAmbient.StepModels is null || string.IsNullOrWhiteSpace(voiceoverConfigJson))
            return profile;
        Guid? providerId;
        try
        {
            providerId = VoiceoverStepConfig.Parse(voiceoverConfigJson)?.ProviderId;
        }
        catch (System.Text.Json.JsonException)
        {
            return profile;
        }

        if (!AppAgentModelAmbient.IsChosenStepModel(providerId))
            return profile;
        return profile with
        {
            Quantities = [.. profile.Quantities.Select(q => q.Target == ProfilePriceTarget.SpeechSynthesis ? q with { ProviderId = providerId } : q)]
        };
    }

    private static bool EnforcesNarrationFit(string? voiceoverConfigJson)
    {
        if (string.IsNullOrWhiteSpace(voiceoverConfigJson))
            return true;
        try
        {
            return (VoiceoverStepConfig.Parse(voiceoverConfigJson)?.NarrationFitPolicy ?? NarrationFitPolicy.Enforce) == NarrationFitPolicy.Enforce;
        }
        catch (System.Text.Json.JsonException)
        {
            return true;
        }
    }

    /// <summary>
    /// The "up to" ceiling of a <see cref="StepType.VideoGenerate"/> step with no declared price: B0's
    /// max-clips shape, 4 clips x 15 s = 60 generated seconds, priced against the resolved
    /// video-generation provider (seed constants when it cannot be resolved or priced).
    /// </summary>
    public static ProfileQuantity VideoGenerateMaxClips { get; } =
        new(UsageKind.VideoGenSeconds, ProfilePriceTarget.VideoGeneration, 60m);

    private static readonly StepUsageProfile FixedPrice = new([], 1d, 1d, null, false,
        "fixed-price on the display path: declared price, else VideoGenerateMaxClips");

    /// <summary>
    /// The cold-start profile of one execution of a step. Never null; <see cref="StepType.Conditional"/>
    /// and <see cref="StepType.Extract"/> (no provider call) and <see cref="StepType.VideoGenerate"/>
    /// (fixed-price, see <see cref="VideoGenerateMaxClips"/>) have no quantities at all.
    /// </summary>
    public static StepUsageProfile For(StepType stepType, AgentType? agentType) => stepType switch
    {
        StepType.Conditional or StepType.Extract or StepType.ExtractFrame or StepType.PinInsert or StepType.VideoEnhance => Empty,
        StepType.VideoAnalyze => VideoAnalyze,
        StepType.VideoCompile => VideoCompile,
        StepType.TimelineRender => TimelineRender,
        StepType.ThumbnailGenerate => ThumbnailGenerate,
        StepType.Voiceover => Voiceover,
        StepType.VideoGenerate or StepType.AudioGenerate or StepType.AudioCleanup or StepType.ImageGenerate => FixedPrice,
        StepType.EditRoom => EditRoom,

        // GraphicsRoom seats four against EditRoom's three; ColorGrade and Dailies are EditRoom-shaped.
        // None of the three has a measurement (analogues).
        StepType.GraphicsRoom => Room(AgentType.MotionGraphicsDirector, 4m / 3m, GenericLowFactor, GenericHighFactor,
            measured: false, "analogue: EditRoom x 4/3 (four seats against three)"),
        StepType.ColorGradeRoom => Room(AgentType.Colorist, 1m, GenericLowFactor, GenericHighFactor,
            measured: false, "analogue: EditRoom x 1"),
        StepType.DailiesRoom => Room(AgentType.TakeSelector, 1m, GenericLowFactor, GenericHighFactor,
            measured: false, "analogue: EditRoom x 1"),

        StepType.DesignGraphic => GraphicDesigner,
        StepType.Revise => CompositionReviser,

        // Agent, ReviewLoop, ForEach (one item), Parallel (one branch) and any step type appended
        // later: the agent's own profile.
        _ => ForAgent(agentType)
    };

    /// <summary>Visual check (docs/video-editing.md "Visual check in review loops"): the checklist prompt, in tokens.</summary>
    public const long VisualCheckPromptTokens = 600;

    /// <summary>
    /// Visual check: one frame plus its label, in input tokens, held at the largest per-image charge of the priced vision
    /// vendors (the same 4,000 the assistant's look claims a picture at, <c>AssistantTurnGuard.MinTokensPerImage</c>),
    /// so the hold covers the worst case whichever vision model is the default (S-77).
    /// </summary>
    public const long VisualCheckTokensPerFrame = 4_000;

    /// <summary>Visual check: the findings answer's ceiling, in output tokens (<c>VisualFrameReviewer</c>'s MaxOutputTokens).</summary>
    public const long VisualCheckOutputTokens = 3_000;

    /// <summary>Visual check: the most vision calls one check makes (the first, and one retry after a malformed answer).</summary>
    public const int VisualCheckCalls = 2;

    /// <summary>
    /// The cold-start profile of one execution of <paramref name="step"/>: <see cref="For(StepType, AgentType?)"/>,
    /// plus, for a <see cref="StepType.ReviewLoop"/> whose <c>visualCheck</c> is enabled, that check's worst case per
    /// iteration (S-77): <see cref="VisualCheckCalls"/> vision calls, each with every image (frames + 1) at
    /// <see cref="VisualCheckTokensPerFrame"/> and the full <see cref="VisualCheckOutputTokens"/>, priced on the
    /// <see cref="ProfilePriceTarget.Vision"/> target (the default vision model), not on the reviewer's chat. An absent,
    /// disabled or unreadable config leaves the profile unchanged.
    /// </summary>
    public static StepUsageProfile For(WorkflowStep step, AgentType? agentType)
    {
        // A local AudioCleanup target calls nothing (no quantities at all); a hosted one is the fixed-price media row below.
        if (step.StepType == StepType.AudioCleanup)
            return StepCreditDefaults.IsComputeOnly(step) ? Empty : For(StepType.AudioCleanup, agentType);

        // A local VideoEnhance is compute-only too, but it falls through to For(StepType.VideoEnhance) (Empty), never to
        // the VideoAnalyze compute row.
        if (step.StepType == StepType.VideoAnalyze && StepCreditDefaults.IsComputeOnly(step))
            return VideoAnalyzeComputeOnly;

        StepUsageProfile profile = For(step.StepType, agentType);
        if (step.StepType == StepType.Voiceover)
            return WithChosenSpeechModel(EnforcesNarrationFit(step.VoiceoverConfigJson) ? VoiceoverEnforce : profile, step.VoiceoverConfigJson);

        // Looks at events (describeEvents) are not in this profile: they have their own (EventLookProfile) with their own
        // range, and the hold takes their worst case as an add-on (RunCreditHolds). What is left is the analysis itself:
        // the compute row when the looks are the only thing the step asks a provider.
        if (step.StepType == StepType.VideoAnalyze && EventLooks(step.VideoAnalyzeConfigJson) is { } looks)
            return looks.OnlyLooks ? VideoAnalyzeComputeOnly : profile;

        if (step.StepType != StepType.ReviewLoop || VisualCheck(step.ReviewLoopConfigJson) is not { } check)
            return profile;

        List<ProfileQuantity> quantities = [.. profile.Quantities];
        List<string> notes = [];
        if (check.Enabled)
        {
            int images = check.Images;
            decimal input = VisualCheckCalls * (VisualCheckPromptTokens + (decimal)images * VisualCheckTokensPerFrame);
            quantities.Add(new ProfileQuantity(UsageKind.LlmInputTokens, ProfilePriceTarget.Vision, input, check.ProviderId));
            quantities.Add(new ProfileQuantity(UsageKind.LlmOutputTokens, ProfilePriceTarget.Vision, VisualCheckCalls * VisualCheckOutputTokens, check.ProviderId));
            notes.Add($"the visual check's worst case on the vision model ({VisualCheckCalls} calls, {images} images each)");
        }

        if (check.ExtraRounds > 0)
        {
            // Each targeted-look round is one vision call (and one retry after a malformed answer) over at most
            // MaxFramesPerRound frames, then one more reviewer call over the reviewer's own input: so the reviewer's
            // own chat quantities are held once more per round.
            int rounds = check.ExtraRounds;
            decimal lookInput = rounds * VisualCheckCalls * (VisualCheckPromptTokens + (decimal)check.FramesPerRound * VisualCheckTokensPerFrame);
            quantities.Add(new ProfileQuantity(UsageKind.LlmInputTokens, ProfilePriceTarget.Vision, lookInput, check.ProviderId));
            quantities.Add(new ProfileQuantity(UsageKind.LlmOutputTokens, ProfilePriceTarget.Vision, rounds * VisualCheckCalls * VisualCheckOutputTokens, check.ProviderId));
            foreach (ProfileQuantity reviewer in profile.Quantities.Where(q => q.Target == ProfilePriceTarget.Chat))
                quantities.Add(reviewer with { Quantity = reviewer.Quantity * rounds });
            notes.Add($"{rounds} targeted-look round(s) of up to {check.FramesPerRound} frames, each a vision call and one more reviewer call");
        }

        if (notes.Count == 0)
            return profile;

        return profile with
        {
            Quantities = quantities,
            Source = profile.Source + "; + " + string.Join("; + ", notes)
        };
    }

    /// <summary>Input tokens of one look at an event: the three-frame strip (about 1,152 px wide) and the question.</summary>
    public const long EventLookInputTokens = 2_200;

    /// <summary>
    /// Typical output tokens of one look (the answer is a dozen words; a reasoning model may think first). The shown
    /// estimate of an analysis step uses this figure; its hold uses <see cref="EventLookMaxOutputTokens"/>.
    /// </summary>
    public const long EventLookOutputTokens = 300;

    /// <summary>
    /// The most output tokens one look can bill: the ceiling the describer asks the model with
    /// (<c>VisionEventDescriber</c> reads this constant). A worst case uses it: the hold for an analysis step's looks at
    /// events and the hold for an agent's own looks.
    /// </summary>
    public const long EventLookMaxOutputTokens = 600;

    /// <summary>Fewest automatic looks over the typical count, and most over it: the band of a describing analysis.</summary>
    public const double EventLookLowFactor = ReelBolt.Shared.Workflows.VideoEvents.VisionLookDefaults.MinEventLooks / (double)ReelBolt.Shared.Workflows.VideoEvents.VisionLookDefaults.TypicalEventLooks;
    public const double EventLookHighFactor = ReelBolt.Shared.Workflows.VideoEvents.VisionLookDefaults.MaxAutoEventLooks / (double)ReelBolt.Shared.Workflows.VideoEvents.VisionLookDefaults.TypicalEventLooks;

    /// <summary>
    /// The shown estimate of a step's looks at events, beside <see cref="For(WorkflowStep, AgentType?)"/>: null when the
    /// step takes none. An explicit count is shown as exactly that count (both factors 1); the automatic count is shown as
    /// the typical <see cref="ReelBolt.Shared.Workflows.VideoEvents.VisionLookDefaults.TypicalEventLooks"/> with the
    /// automatic range around it (12 to 30), so the high edge is never more than the step can take. Typical output tokens.
    /// </summary>
    public static StepUsageProfile? EventLookProfile(WorkflowStep step)
    {
        if (step.StepType != StepType.VideoAnalyze || EventLooks(step.VideoAnalyzeConfigJson) is not { } looks)
            return null;
        return new StepUsageProfile(
            [
                new ProfileQuantity(UsageKind.LlmInputTokens, ProfilePriceTarget.Vision, looks.Count * EventLookInputTokens, looks.ProviderId),
                new ProfileQuantity(UsageKind.LlmOutputTokens, ProfilePriceTarget.Vision, looks.Count * EventLookOutputTokens, looks.ProviderId)
            ],
            looks.Explicit ? 1.0 : EventLookLowFactor,
            looks.Explicit ? 1.0 : EventLookHighFactor,
            PricedAs: null,
            Measured: false,
            Source: $"{looks.Count} looks at events on the vision model");
    }

    /// <summary>
    /// The most looks at events a VideoAnalyze step can take, for its hold, and the vision model it names (null is the
    /// default one): the author's count (at most the hard cap), else the most an automatic count gives. Null when the step
    /// takes none (describing off, or a count of 0) or its config is unreadable.
    /// </summary>
    public static (int Looks, Guid? ProviderId)? EventLookWorstCase(string? videoAnalyzeConfigJson) =>
        EventLooks(videoAnalyzeConfigJson) is { } looks
            ? (looks.Explicit ? looks.Count : ReelBolt.Shared.Workflows.VideoEvents.VisionLookDefaults.MaxAutoEventLooks, looks.ProviderId)
            : null;

    /// <summary>
    /// Whether a VideoAnalyze step describes events (and so keeps strips an agent can look at later), and the vision model
    /// it names (null is the default one). An absent or unreadable config describes nothing.
    /// </summary>
    public static (bool Describes, Guid? ProviderId) EventLookProvider(string? videoAnalyzeConfigJson) =>
        EventLooks(videoAnalyzeConfigJson) is { } looks ? (true, looks.ProviderId) : (false, null);

    /// <summary>
    /// The looks a VideoAnalyze step takes at events (<c>describeEvents</c> with <c>analyzeEvents</c>): the author's count,
    /// or the typical automatic one when it is left to the video's length (and which of the two it is); the vision model it
    /// names; and whether the looks are the only thing the step asks a provider. Null when the step takes none or its
    /// config is unreadable.
    /// </summary>
    private static (int Count, bool Explicit, Guid? ProviderId, bool OnlyLooks)? EventLooks(string? videoAnalyzeConfigJson)
    {
        if (string.IsNullOrWhiteSpace(videoAnalyzeConfigJson))
            return null;
        try
        {
            System.Text.Json.JsonSerializerOptions options = new(System.Text.Json.JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            };
            VideoAnalyzeStepConfig? config = System.Text.Json.JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(videoAnalyzeConfigJson, options);
            if (config is not { AnalyzeEvents: true, DescribeEvents: true })
                return null;
            // 0 is the author switching the looks off: the step takes none and keeps no strip for a later look.
            if (config.MaxDescribedEvents is <= 0)
                return null;
            int count = config.MaxDescribedEvents is { } asked
                ? Math.Min(asked, ReelBolt.Shared.Workflows.VideoEvents.VisionLookDefaults.MaxEventLooks)
                : ReelBolt.Shared.Workflows.VideoEvents.VisionLookDefaults.TypicalEventLooks;
            return (count, config.MaxDescribedEvents is not null, config.VisionProviderId, !(config with { DescribeEvents = false }).CallsAnyProvider);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// An enabled frame check (images = clamped frames + the last frame) and/or the targeted-look rounds of a review loop's
    /// <c>visualCheck</c>, and the vision provider it names; null when neither is on or the config is unreadable.
    /// </summary>
    private static (bool Enabled, int Images, int ExtraRounds, int FramesPerRound, Guid? ProviderId)? VisualCheck(string? reviewLoopConfigJson)
    {
        if (string.IsNullOrWhiteSpace(reviewLoopConfigJson))
            return null;
        try
        {
            ReviewLoopStepConfig? config = System.Text.Json.JsonSerializer.Deserialize<ReviewLoopStepConfig>(
                reviewLoopConfigJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            if (config?.VisualCheck is not { } visual || (!visual.Enabled && visual.ClampedExtraRounds == 0))
                return null;
            return (visual.Enabled, visual.ClampedFrames + 1, visual.ClampedExtraRounds, visual.ClampedFramesPerRound, visual.VisionProviderId);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The profile of one solo run of an agent of <paramref name="agentType"/>.</summary>
    public static StepUsageProfile ForAgent(AgentType? agentType) => agentType switch
    {
        AgentType.AnimationStrategyAgent => AnimationStrategy,
        AgentType.AuthorAgent => Author,
        AgentType.DirectorAgent => Director,
        AgentType.RemotionComponentTranslator => RemotionTranslator,
        AgentType.GraphicDesigner => GraphicDesigner,
        AgentType.VideoReviewAgent => VideoReview,
        AgentType.CompositionReviser => CompositionReviser,
        AgentType.MotionGraphicsPlanner => MotionGraphicsPlanner,
        AgentType.ScriptwriterAgent => Scriptwriter,
        AgentType.VideoStoryEditor => VideoStoryEditor,
        AgentType.ReviewAgent => Review,
        AgentType.CodeStructureAnalyzer => CodeStructure,
        AgentType.ComponentInventoryAnalyzer => ComponentInventory,
        AgentType.StyleAndThemeExtractor => StyleAndTheme,
        AgentType.DependencyAnalyzer => Dependency,
        AgentType.RouteAndApiAnalyzer => RouteAndApi,
        AgentType.NarrationWriter => NarrationWriter,

        // Custom and unknown agents: the DirectorAgent's quantities, as an analogue.
        null or AgentType.Custom => Analogue(Director, agentType, "analogue: DirectorAgent quantities"),

        // Every other solo agent type (NarrationTranslator, PickupPlanner, MusicSupervisor,
        // SoundDesigner, ShotDirector, TakeSelector, Colorist, ...) has no measurement of its own and
        // uses the NarrationWriter's quantities.
        { } other => Analogue(NarrationWriter, other, "analogue: NarrationWriter quantities")
    };

    /// <summary>
    /// The seed-constant price of <paramref name="quantity"/>, in millicredits: the fallback used only
    /// when a provider cannot be resolved or no price row prices it. Uses the pre-catalog seed's
    /// USD-per-unit constants (below; <see cref="PricingDefaults"/> for video and compute, compute at the
    /// 1080p tier) x <see cref="PricingDefaults.CreditsMillisPerUsd"/> x <paramref name="markup"/>, so it is never zero
    /// for a chargeable quantity and it is priced like a real quote: the markup is <c>pricing_settings.markup</c> (the live
    /// one where the caller has a price service, <see cref="ProfilePriceTable.BuildAsync"/>; else its default 1.2), so an
    /// estimate or hold built on the fallback is not below what the settle of the same usage charges.
    /// </summary>
    public static long SeedFallbackMillis(UsageKind kind, decimal quantity, decimal markup = PricingDefaults.DefaultMarkup)
    {
        decimal usdPerUnit = kind switch
        {
            UsageKind.LlmInputTokens => FallbackLlmInputUsdPerToken,
            UsageKind.LlmOutputTokens => FallbackLlmInputUsdPerToken * FallbackLlmOutputToInputRatio,
            UsageKind.LlmCachedInputTokens => FallbackLlmInputUsdPerToken * FallbackLlmCachedInputShare,
            UsageKind.EmbeddingTokens => FallbackEmbeddingUsdPerToken,
            UsageKind.AsrAudioSeconds => FallbackAsrUsdPerSecond,
            UsageKind.TtsInputBytes => FallbackTtsUsdPerByte,
            UsageKind.VideoGenSeconds => PricingDefaults.VideoGenUsdPerUnit,
            UsageKind.RenderOutputSeconds => Compute1080UsdPerSecond,
            UsageKind.EncodeOutputSeconds => Compute1080UsdPerSecond * PricingDefaults.ComputeEncodeShare,
            UsageKind.AnalyzeMediaSeconds => Compute1080UsdPerSecond * PricingDefaults.ComputeAnalyzeShare,
            _ => 0m
        };

        decimal millis = quantity * usdPerUnit * PricingDefaults.CreditsMillisPerUsd * markup;
        long rounded = (long)Math.Round(millis, MidpointRounding.AwayFromZero);
        return rounded == 0 && millis > 0m ? 1 : rounded;
    }

    /// <summary>The seed's render price at the 1080p tier: the 720p anchor times the pixel ratio (1080/720)^2.</summary>
    private static readonly decimal Compute1080UsdPerSecond = PricingDefaults.ComputeRenderUsdPerSecond * (1080m * 1080m) / (720m * 720m);

    // The pre-catalog seed's B0 Mid figures (ESTIMATED, not measured), kept only as the fallback above:
    // $3.00 per million tokens blended at a 1:4 input:output split over a 50/50 mix, cached input 10% of input.
    private const decimal FallbackLlmOutputToInputRatio = 4m;
    private const decimal FallbackLlmCachedInputShare = 0.10m;
    private static readonly decimal FallbackLlmInputUsdPerToken =
        3.00m / 1_000_000m * 2m / (1m + FallbackLlmOutputToInputRatio);
    private const decimal FallbackEmbeddingUsdPerToken = 0.01m / 1_000_000m;
    private const decimal FallbackAsrUsdPerSecond = 0.006m / 60m;
    private const decimal FallbackTtsUsdPerByte = 15m / 1_000_000m;

    private static StepUsageProfile Agent(
        AgentType type, long input, long output, long cached, double low, double high, bool measured, string source,
        params (UsageKind Kind, decimal Seconds)[] compute)
        => Floored(new(
            [.. Tokens(input, output, cached), .. compute.Select(c => new ProfileQuantity(c.Kind, ProfilePriceTarget.Compute, c.Seconds))],
            low, high, type, measured, source));

    private static StepUsageProfile LocalTotal(AgentType type, long totalTokens, double low, double high, bool measured, string source)
        => Agent(type,
            (long)Math.Round(totalTokens * LocalInputShare, MidpointRounding.AwayFromZero),
            (long)Math.Round(totalTokens * LocalOutputShare, MidpointRounding.AwayFromZero),
            (long)Math.Round(totalTokens * LocalCachedShare, MidpointRounding.AwayFromZero),
            low, high, measured, source + "; split 18% in / 4% out / 78% cached");

    private static StepUsageProfile Room(AgentType seat, decimal scale, double low, double high, bool measured, string source)
        => Floored(new(
            [.. Tokens(
                (long)Math.Round(RoomInput * scale, MidpointRounding.AwayFromZero),
                (long)Math.Round(RoomOutput * scale, MidpointRounding.AwayFromZero),
                (long)Math.Round(RoomCached * scale, MidpointRounding.AwayFromZero))],
            low, high, seat, measured, source));

    private static StepUsageProfile Analogue(StepUsageProfile basis, AgentType? pricedAs, string source)
        => basis with
        {
            LowFactor = Math.Min(GenericLowFactor, MinLowFactor),
            HighFactor = Math.Max(GenericHighFactor, MinHighFactor),
            PricedAs = pricedAs,
            Measured = false,
            Source = source
        };

    private static ProfileQuantity[] Tokens(long input, long output, long cached) =>
    [
        new(UsageKind.LlmInputTokens, ProfilePriceTarget.Chat, input),
        new(UsageKind.LlmOutputTokens, ProfilePriceTarget.Chat, output),
        new(UsageKind.LlmCachedInputTokens, ProfilePriceTarget.Chat, cached)
    ];

    /// <summary>The floors: a profile never claims a band tighter than 0.75–1.33 of its median.</summary>
    private static StepUsageProfile Floored(StepUsageProfile profile)
        => profile with
        {
            LowFactor = Math.Min(profile.LowFactor, MinLowFactor),
            HighFactor = Math.Max(profile.HighFactor, MinHighFactor)
        };
}
