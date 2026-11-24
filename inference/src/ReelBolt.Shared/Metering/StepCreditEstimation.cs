using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// One workflow step's recorded credit cost: the sum of the <c>usage_events</c> rows attributed to a
/// single <c>workflow_step_result</c>, in integer millicredits (decision D8). B3b/B3c write those
/// rows; this type is the shape B7's estimator consumes. <paramref name="OutputResolution"/> is the
/// output resolution its execution chose (short side; null = Auto, also when the execution row is
/// gone), so a render step's history can be read per resolution (QA-15).
/// </summary>
public readonly record struct StepCreditSample(StepType StepType, Guid? AgentDefinitionId, long CreditsMillis, int? OutputResolution = null);

/// <summary>Where a <see cref="StepCreditEstimate"/>'s numbers came from.</summary>
public enum CreditEstimateBasis
{
    /// <summary>This organization's own history for the step type <b>and</b> the exact agent.</summary>
    History,

    /// <summary>This organization's history for the step type, across every agent.</summary>
    StepTypeHistory,

    /// <summary><see cref="StepCreditDefaults"/>: the cold-start path (no usable history).</summary>
    Default,

    /// <summary>The step type is deliberately not charged in credits, so nothing is held.</summary>
    NotCharged,

    /// <summary>
    /// B6c: priced from the VideoGenerate step's OWN settings — how many clips it will order and how
    /// many seconds each, at the resolved model's per-second rate-card price
    /// (<see cref="VideoGenerateCreditPricing"/>). Appended last, because these values are surfaced as
    /// names.
    /// </summary>
    VideoGeneration,

    /// <summary>
    /// Display range only: this organization's history for the step type and the agent TYPE, across
    /// every agent definition of that type (used when the exact agent has too few samples). Never
    /// produced by <see cref="StepCreditEstimator"/>, so the hold is unaffected.
    /// </summary>
    AgentTypeHistory,

    /// <summary>
    /// Display range only: the step's chat runs on the organization's own provider, so its LLM part is
    /// not charged in credits (any compute it does still is). Never produced by
    /// <see cref="StepCreditEstimator"/>.
    /// </summary>
    OwnProvider,

    /// <summary>
    /// T-108: priced from the AudioGenerate step's OWN settings — takes x billed minutes or seconds of the chosen
    /// music/SFX model, through the same price row the settlement uses (<see cref="AudioGenerateCreditPricing"/>).
    /// Appended last, because these values are surfaced as names.
    /// </summary>
    AudioGeneration,

    /// <summary>
    /// Priced from the ImageGenerate step's OWN settings: pictures x the chosen model's price-book row
    /// (<see cref="ImageGenerateCreditPricing"/>). Appended last, because these values are surfaced as names.
    /// </summary>
    ImageGeneration
}

/// <summary>What one step of a run is expected to cost, in integer millicredits.</summary>
/// <param name="LowMillis">The p50 estimate: the bottom of the range shown before Execute.</param>
/// <param name="HighMillis">The p90 estimate: the top of the range shown before Execute.</param>
/// <param name="HoldMillis">
/// What the credit gate actually reserves before the step runs. A step type that is never charged
/// holds nothing, and the gate then takes no reservation at all.
/// </param>
/// <param name="Basis">Which of the four paths produced these numbers.</param>
public readonly record struct StepCreditEstimate(
    long LowMillis, long HighMillis, long HoldMillis, CreditEstimateBasis Basis)
{
    /// <summary>True when the numbers came from recorded usage rather than the B0 defaults.</summary>
    public bool FromHistory => Basis is CreditEstimateBasis.History or CreditEstimateBasis.StepTypeHistory;
}

/// <summary>
/// B0's measured defaults: the cold-start path of <see cref="StepCreditEstimator"/>, i.e. what a step
/// of a given type costs when the organization has no usable history of its own.
///
/// <para>
/// <b>Where the numbers come from.</b> <c>docs/design/saas-pricing-and-billing.md</c> (B0) measured
/// the dev database per step type and per agent. Its "Mid" blended LLM price of $3 per million
/// tokens against D9's "1 credit = $0.006 of COGS" makes <b>1 token = 0.5 millicredits</b>
/// (3e-6 / 0.006 * 1000), which is the conversion every token-derived row below uses; the compute and
/// voice rows are derived from the B4 seed rate cards (the pre-catalog rate-card seed; constants now in <see cref="PricingDefaults"/>), which are
/// themselves B0 placeholders. Every row is therefore a <b>placeholder shape, not a priced truth</b>:
/// B13 replaces them from measured cost, exactly as it replaces the rate cards and the plan catalog.
/// </para>
///
/// <para>
/// <b>Why a chargeable step type is never silently zero.</b> A zero hold short-circuits the gate
/// (CreditGate.ReserveAsync answers NO_PLATFORM_COST with no reservation), and a step with no
/// reservation is never settled, so a chargeable type left at zero would be free forever rather than
/// "estimated at zero". Only the two types that genuinely make no provider call are 0:
/// <see cref="StepType.Conditional"/> and <see cref="StepType.Extract"/>.
/// </para>
///
/// <para>
/// <b><see cref="StepType.VideoGenerate"/> is a special case (B6c).</b> Its live price is
/// <c>seconds x the rate card's per-second figure</c>, which only its own step config can size, so
/// the real hold comes from <see cref="VideoGenerateCreditPricing"/>; the table below is what a
/// caller gets when that lookup cannot answer (no provider row, no rate card, an unreadable config),
/// and it is the same figure B7's preflight falls back to. It is deliberately a real, non-zero row:
/// before B6c it was <see cref="NoHold"/>, which is exactly why the step's recorded
/// <c>VideoGenSeconds</c> credit rows were never charged.
/// </para>
/// </summary>
public static class StepCreditDefaults
{
    /// <summary>No hold at all: the step type is never charged in credits.</summary>
    public const long NoHold = 0;

    /// <summary>
    /// Millicredits per token at B0's Mid scenario and D9's credit price
    /// ($3 per million tokens, divided by $0.006 per credit, times 1000 millicredits).
    /// </summary>
    public const decimal MillisPerToken = 0.5m;

    /// <summary>
    /// The (low, high) millicredit default for <paramref name="stepType"/>. Low is B0's p50 (the
    /// bottom of the shown range, and the cold-start hold); high is B0's p90 (the top of the range,
    /// and the hold once history exists). Priced like a real quote: the seed's at-cost figure (below)
    /// times <see cref="PricingDefaults.DefaultMarkup"/>, as <see cref="StepUsageProfiles.SeedFallbackMillis"/>
    /// does, so a cold-start hold is not below what the settle of the same usage charges.
    /// </summary>
    public static (long Low, long High) MillisFor(StepType stepType) => MillisFor(stepType, PricingDefaults.DefaultMarkup);

    /// <summary>
    /// <see cref="MillisFor(StepType)"/> at <paramref name="markup"/>: the live <c>pricing_settings.markup</c> where the
    /// caller has it (T-032), so a cold-start hold follows a repriced markup like the settle of the same usage does.
    /// </summary>
    public static (long Low, long High) MillisFor(StepType stepType, decimal markup) => MillisFor(stepType, markup, computeOnly: false);

    /// <summary>
    /// <see cref="MillisFor(StepType, decimal)"/>, but for a <see cref="StepType.VideoAnalyze"/> that
    /// <see cref="IsComputeOnly"/> only its compute (processing time) part: the model tokens a transcription or vision call
    /// would add are not held or estimated for a step that makes no such call. Every other step type ignores the flag.
    /// </summary>
    public static (long Low, long High) MillisFor(StepType stepType, decimal markup, bool computeOnly)
    {
        // A local AudioCleanup target or a local VideoEnhance runs ffmpeg on this host and calls nothing: it is free
        // (nothing to hold or show).
        if (computeOnly && stepType is StepType.AudioCleanup or StepType.VideoEnhance)
            return (0, 0);

        (long low, long high) = computeOnly && stepType == StepType.VideoAnalyze ? VideoAnalyzeComputeAtCost : AtCostMillisFor(stepType);
        decimal m = markup > 0 ? markup : PricingDefaults.DefaultMarkup;
        return (AtMarkup(low, m), AtMarkup(high, m));
    }

    /// <summary>The compute part of the VideoAnalyze row below (its token part is <c>Tokens(16_500, 31_500)</c>).</summary>
    private static readonly (long Low, long High) VideoAnalyzeComputeAtCost = (4_000, 6_000);

    /// <summary>
    /// True for a <see cref="StepType.VideoAnalyze"/> whose config reads and calls no model or vendor
    /// (<see cref="VideoAnalyzeStepConfig.CallsAnyProvider"/> false): it is priced and held for its processing time only.
    /// A missing or unreadable config is not compute-only (priced as the full row), the same deny-by-default as the run-rate classifier.
    /// </summary>
    public static bool IsComputeOnly(WorkflowStep step)
    {
        // An AudioCleanup with a LOCAL target calls no provider and spends nothing; a hosted, missing or unreadable one is held.
        if (step.StepType == StepType.AudioCleanup)
            return IsLocalAudioCleanup(step.AudioCleanupConfigJson);

        // A VideoEnhance with a LOCAL operation calls no provider and spends nothing; a hosted, missing or unreadable one is held.
        if (step.StepType == StepType.VideoEnhance)
            return IsLocalVideoEnhance(step.VideoEnhanceConfigJson);

        if (step.StepType != StepType.VideoAnalyze || string.IsNullOrWhiteSpace(step.VideoAnalyzeConfigJson))
            return false;

        try
        {
            System.Text.Json.JsonSerializerOptions options = new(System.Text.Json.JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            };
            return System.Text.Json.JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(step.VideoAnalyzeConfigJson, options) is { } config
                && !config.CallsAnyProvider;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>True only when the config parses and names a local (free) target; anything else is treated as hosted (deny by default).</summary>
    public static bool IsLocalAudioCleanup(string? audioCleanupConfigJson)
    {
        if (string.IsNullOrWhiteSpace(audioCleanupConfigJson))
            return false;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<AudioCleanupStepConfig>(audioCleanupConfigJson, AudioCleanupStepConfig.JsonOptions) is { } config
                && System.Enum.IsDefined(config.Target)
                && !config.CallsAnyProvider;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>True only when the config parses, validates and names a local (free) operation; anything else is treated as hosted (deny by default).</summary>
    public static bool IsLocalVideoEnhance(string? videoEnhanceConfigJson) =>
        TryParseVideoEnhance(videoEnhanceConfigJson, out VideoEnhanceStepConfig? config)
        && config is not null
        && System.Enum.IsDefined(config.Engine)
        && !config.CallsAnyProvider;

    /// <summary>Parses a VideoEnhance config; false for blank or unreadable JSON. Never throws.</summary>
    public static bool TryParseVideoEnhance(string? json, out VideoEnhanceStepConfig? config)
    {
        config = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            config = System.Text.Json.JsonSerializer.Deserialize<VideoEnhanceStepConfig>(json, VideoEnhanceStepConfig.JsonOptions);
            return config is not null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static long AtMarkup(long atCostMillis, decimal markup) =>
        (long)Math.Round(atCostMillis * markup, MidpointRounding.AwayFromZero);

    /// <summary>The seed's figure for <paramref name="stepType"/> at COST (the provider's price, no markup).</summary>
    private static (long Low, long High) AtCostMillisFor(StepType stepType) => stepType switch
    {
        // p50 9,766 / p90 744,142 tokens (B0 query 2). The spread is what makes the Agent step type
        // dangerous to blanket-estimate: it spans a cheap narration agent and a code-translator loop
        // that resends context on every tool round.
        StepType.Agent => Tokens(9_766, 744_142),

        // Both run agent work: ForEach re-runs its loop body inline, Parallel runs several agents at
        // once. Neither has a B0 measurement of its own, so they inherit the Agent row rather than
        // being left at zero.
        StepType.ForEach => Tokens(9_766, 744_142),
        StepType.Parallel => Tokens(9_766, 744_142),

        // p50 19,804 / p90 48,392 tokens (B0 query 2). Cheaper per step than Agent because every
        // iteration is judged by a small reviewer rather than a code-writing agent.
        StepType.ReviewLoop => Tokens(19_804, 48_392),

        // The step's own rows read 0 tokens (captioning goes through a separate client), so B0
        // estimated the vision half instead: 11 captions at 1.5k tokens at p50, 21 at the observed
        // maximum. The compute allowance on top is the ASR and media-analyse share.
        StepType.VideoAnalyze => (Tokens(16_500, 31_500).Low + 4_000, Tokens(16_500, 31_500).High + 6_000),

        // ffmpeg only: 1080p encode seconds at the seed card's $0.0020/s over a 60s (p50) and 90s
        // (p90) output, rounded to whole credits.
        StepType.VideoCompile => (20_000, 30_000),

        // Fish TTS is billed per UTF-8 byte, plus a word-alignment ASR pass: a ~60s narration at the
        // seed card's $15 per million bytes, rounded out.
        StepType.Voiceover => (2_000, 5_000),

        // B6c: priced per second at the model's rate-card rate (VideoGenerateCreditPricing), which is
        // the same arithmetic the settlement uses. This row is the FALLBACK for a caller that could
        // not resolve the step's own settings; it is the seed rate card's placeholder USD per second
        // (the old seed's VideoGenSeconds row, <see cref="PricingDefaults.VideoGenUsdPerUnit"/>) over the two shapes a step may take: a 2-clip x 6s
        // inline order at p50, and a 4-clip x 15s plan -- the MaxClips ceiling at the model's longest
        // shot -- at p90. The USD external_generation_jobs ledger is untouched by any of this: it caps
        // dollar spend and holds no credits, so nothing is charged twice.
        StepType.VideoGenerate => VideoGenerateRow,

        // T-108: priced per take from the step's own settings (AudioGenerateCreditPricing). This row is the
        // FALLBACK for a step whose provider or price cannot be resolved: one 30 s fal ElevenLabs Music take
        // ($0.60, one started minute) at p50 and the 4 takes x 5 min ceiling ($12.00) at p90.
        StepType.AudioGenerate => AudioGenerateRow,

        // ImageGenerate: priced from the step's own settings (ImageGenerateCreditPricing). This row is the FALLBACK for a
        // step whose provider or price cannot be resolved: one mid-priced picture ($0.04) at p50 and four at the dear end
        // ($0.10 each) at p90, at the markup-free cost like the audio row.
        StepType.ImageGenerate => ImageGenerateRow,

        // The three rooms are group chats of VideoStoryEditor seats plus a director. B0 has no room
        // row of its own, so they inherit the Agent row; the closest measured analogue, the
        // VideoStoryEditor agent (p50 11,157 / p90 605,225 tokens over 110 samples), lands within a
        // few percent of it anyway.
        StepType.EditRoom => Tokens(9_766, 744_142),
        StepType.GraphicsRoom => Tokens(9_766, 744_142),
        StepType.ColorGradeRoom => Tokens(9_766, 744_142),

        // ffmpeg only, and far less of it than a compile: two frame decodes at thumbnail
        // resolution. Deliberately NOT (NoHold, NoHold) — Conditional/Extract are free because they
        // make no provider call AND no measurable compute, while this decodes real video. The figure
        // is the seed card's $0.0020/s encode rate over 2-4s, rounded out, which keeps it
        // comfortably below the compile row it is a fragment of.
        StepType.ThumbnailGenerate => (1_000, 2_000),

        // Rendering an editor timeline is ffmpeg only, like a compile — region mezzanines, a concat, a
        // mix and one delivery encode over the same program length — so it holds the compile row until
        // the organization has TimelineRender history of its own.
        StepType.TimelineRender => (20_000, 30_000),

        // A composition revision is one agent run over a restored film plus one Remotion render —
        // the Author agent's shape, so it holds the Agent row (the organization's own Revise history
        // replaces it once there is some, exactly as for every other step type).
        StepType.Revise => Tokens(9_766, 744_142),

        // Designing a graphic is one GraphicDesigner run (author-shaped tool loop over a much smaller
        // composition) plus one Remotion still, so it holds the Agent row too until the organization
        // has DesignGraphic history of its own. Generous on purpose: an unused hold is released at
        // settle, a missing one is never charged.
        StepType.DesignGraphic => Tokens(9_766, 744_142),

        // Neither makes a provider call: Conditional evaluates an NCalc expression, Extract queries
        // project files.
        StepType.Conditional => (NoHold, NoHold),
        StepType.Extract => (NoHold, NoHold),

        // AudioCleanup (hosted targets only; a local target is compute-only, see IsComputeOnly, and holds nothing): the
        // FALLBACK for a step whose provider or price cannot be resolved, at the default endpoint's vendor price over the
        // reference length at p50 and the 30-minute request cap at p90. The step's own hold is sized from the price book
        // (AudioCleanupCreditPricing) and its settle meters the probed minutes through the same row.
        StepType.AudioCleanup => AudioCleanupRow,

        // VideoEnhance (hosted operations only; a local one is compute-only, see IsComputeOnly, and holds nothing): the
        // FALLBACK for a step whose provider or price cannot be resolved, at the reference 10-second 1080p upscale at p50
        // and the 120-second hosted cap at p90. The step's own hold is sized from the price book (VideoEnhanceStepPricing)
        // and the executor refuses a source that costs more than it.
        StepType.VideoEnhance => VideoEnhanceRow,

        // ExtractFrame is one local ffmpeg frame grab: no provider call, nothing to hold (free, like Extract).
        StepType.ExtractFrame => (NoHold, NoHold),

        // PinInsert (T-206) resolves references and writes a plan: no provider call, no media work.
        StepType.PinInsert => (NoHold, NoHold),

        // The DesignSystem op runs no provider call of its own: the design system is read from the
        // project (and, when the config names them, from Agent fallback steps that are charged on
        // their own rows). Zero-rated, so IsNotCharged returns true for it.
        StepType.DesignSystem => (NoHold, NoHold),

        // A step type appended to the enum without a row here inherits the Agent row rather than a
        // zero: an unnecessary hold is released at settle, while a missing one is never charged at
        // all. This is the direction the whole table is biased in.
        _ => Tokens(9_766, 744_142)
    };

    /// <summary>
    /// True for the step types whose hold is priced from their OWN settings when the caller could price them
    /// (<see cref="VideoGenerateCreditPricing"/>, <see cref="AudioGenerateCreditPricing"/>, <see cref="ImageGenerateCreditPricing"/>): a declared figure wins
    /// over history, and on the display path they are fixed-price ("up to").
    /// </summary>
    public static bool IsDeclaredPriced(StepType stepType) =>
        stepType is StepType.VideoGenerate or StepType.AudioGenerate or StepType.VideoEnhance or StepType.ImageGenerate;

    /// <summary>True when <paramref name="stepType"/> is deliberately never charged in credits.</summary>
    public static bool IsNotCharged(StepType stepType) => MillisFor(stepType) == (NoHold, NoHold);

    /// <summary>
    /// B6c's cold-start VideoGenerate row: the seed rate card's placeholder price per generated
    /// second, over a 12-second (2 clips x 6s) step at p50 and a 60-second (4 clips x 15s) step at
    /// p90. Computed from <see cref="PricingDefaults"/>' own constants rather than written down as a
    /// number, so re-pricing the seed cannot leave this row quoting the old one.
    /// </summary>
    private static readonly (long Low, long High) VideoGenerateRow = VideoSeconds(2 * 6, 4 * 15);

    /// <summary>T-108's cold-start AudioGenerate row, at cost: one fal music minute at p50, four 5-minute takes at p90.</summary>
    private static readonly (long Low, long High) AudioGenerateRow = (
        (long)Math.Round(0.60m * PricingDefaults.CreditsMillisPerUsd, MidpointRounding.AwayFromZero),
        (long)Math.Round(4 * 5 * 0.60m * PricingDefaults.CreditsMillisPerUsd, MidpointRounding.AwayFromZero));

    /// <summary>The cold-start ImageGenerate row, at cost: one $0.04 picture at p50, four $0.10 pictures at p90.</summary>
    private static readonly (long Low, long High) ImageGenerateRow = (
        (long)Math.Round(0.04m * PricingDefaults.CreditsMillisPerUsd, MidpointRounding.AwayFromZero),
        (long)Math.Round(4 * 0.10m * PricingDefaults.CreditsMillisPerUsd, MidpointRounding.AwayFromZero));

    /// <summary>The cold-start AudioCleanup row (hosted targets), at cost: the reference hold length at p50, the request cap at p90.</summary>
    private static readonly (long Low, long High) AudioCleanupRow = (
        (long)Math.Round(AudioCleanupCreditPricing.DefaultMaxSourceMinutes * ReelBolt.Shared.Inference.AudioIsolationModelCatalog.Instance.Default.UsdPerMinute
            * PricingDefaults.CreditsMillisPerUsd, MidpointRounding.AwayFromZero),
        (long)Math.Round(AudioCleanupCreditPricing.DefaultMaxSourceMinutes * ReelBolt.Shared.Inference.AudioIsolationModelCatalog.Instance.Default.UsdPerMinute
            * PricingDefaults.CreditsMillisPerUsd, MidpointRounding.AwayFromZero));

    /// <summary>The cold-start VideoEnhance row (hosted operations), at cost: Topaz 1080p list price per output second.</summary>
    private static readonly (long Low, long High) VideoEnhanceRow = (
        (long)Math.Round(10 * 0.04m * PricingDefaults.CreditsMillisPerUsd, MidpointRounding.AwayFromZero),
        (long)Math.Round(VideoEnhanceCreditPricing.DefaultMaxSourceSeconds * 0.04m * PricingDefaults.CreditsMillisPerUsd, MidpointRounding.AwayFromZero));

    private static (long Low, long High) VideoSeconds(int p50Seconds, int p90Seconds) => (
        (long)Math.Round(p50Seconds * PricingDefaults.VideoGenUsdPerUnit * PricingDefaults.CreditsMillisPerUsd, MidpointRounding.AwayFromZero),
        (long)Math.Round(p90Seconds * PricingDefaults.VideoGenUsdPerUnit * PricingDefaults.CreditsMillisPerUsd, MidpointRounding.AwayFromZero));

    private static (long Low, long High) Tokens(long p50Tokens, long p90Tokens) => (
        (long)Math.Round(p50Tokens * MillisPerToken, MidpointRounding.AwayFromZero),
        (long)Math.Round(p90Tokens * MillisPerToken, MidpointRounding.AwayFromZero));
}

/// <summary>
/// PostgreSQL's <c>percentile_cont</c>, in memory: linear interpolation between the two closest ranks
/// of a sorted sample. Kept in step with the SQL form B0's own queries used, so an estimate this code
/// computes is the number an analyst would read out of the database.
/// </summary>
public static class CreditPercentiles
{
    /// <summary>
    /// The <paramref name="percentile"/> (0..1) of <paramref name="sorted"/>, which must be ascending.
    /// Returns 0 for an empty sample and the single value for a one-element sample.
    /// </summary>
    public static long Continued(IReadOnlyList<long> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        double clamped = Math.Clamp(percentile, 0d, 1d);
        double rank = clamped * (sorted.Count - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return sorted[lower];
        }

        double value = sorted[lower] + ((rank - lower) * (sorted[upper] - sorted[lower]));
        return (long)Math.Round(value, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// B7's estimator over a set of <see cref="StepCreditSample"/>s: what a step of a given type and
/// agent is expected to cost, priced from the usage the ledger already records (decision D10) and
/// falling back to <see cref="StepCreditDefaults"/> when there is not enough of it.
///
/// <para>
/// <b>Pure and synchronous.</b> Loading the samples is a separate concern (each service queries its
/// own DbContext); everything here is arithmetic over an in-memory list, so the maths, including the
/// cold-start path, is unit-testable without a database.
/// </para>
///
/// <para>
/// <b>The hold is the p90 when history exists, and the p50 default when it does not.</b> B0's
/// reservation policy ("reserve at the p90 estimate for that step type") is only meaningful once we
/// know which agent the step type actually is here. With no observations the step-type p90 is the
/// worst case of an unrelated agent (an Agent p90 of 744k tokens against a p50 of 9.8k), and holding
/// it would refuse a first run the organization can plainly afford: a cold-start Trial workspace has
/// 100 credits. A caller's <c>required</c> figure is the sum of these holds, so a run that passes the
/// preflight cannot then be refused by the gate on its first step.
/// </para>
///
/// <para>
/// <b>A run's cold steps are re-planned.</b> The per-step default low here is what a
/// lone step falls back to. Both the Inference API and the engine size a whole run's holds through
/// <see cref="RunCreditHolds"/>, which keeps this class's p90 for history steps and holds, per cold
/// step, the larger of this default low and its share of the low edge of the cold steps' usual range:
/// The re-plan only ever raises a cold step's hold above this default, never lowers it.
/// </para>
/// </summary>
public sealed class StepCreditEstimator
{
    /// <summary>Observations a bucket needs before its percentile is trusted over the defaults.</summary>
    public const int DefaultMinSamples = 3;

    private readonly Dictionary<(StepType Type, Guid? Agent), List<long>> _byAgent = [];
    private readonly Dictionary<StepType, List<long>> _byType = [];

    // QA-15: the same buckets over reference samples only (Auto and 1080p), and per exact resolution.
    private readonly Dictionary<(StepType Type, Guid? Agent), List<long>> _referenceByAgent = [];
    private readonly Dictionary<StepType, List<long>> _referenceByType = [];
    private readonly Dictionary<(StepType Type, Guid? Agent, int? Resolution), List<long>> _nativeByAgent = [];
    private readonly Dictionary<(StepType Type, int? Resolution), List<long>> _nativeByType = [];
    private readonly int _minSamples;

    /// <summary>Builds an estimator over one organization's samples; <paramref name="samples"/> may be empty.</summary>
    public StepCreditEstimator(IEnumerable<StepCreditSample> samples, int minSamples = DefaultMinSamples)
    {
        _minSamples = Math.Max(1, minSamples);
        foreach (StepCreditSample sample in samples)
        {
            Bucket(_byAgent, (sample.StepType, sample.AgentDefinitionId)).Add(sample.CreditsMillis);
            Bucket(_byType, sample.StepType).Add(sample.CreditsMillis);
            if (ResolutionHoldScaling.IsReference(sample.OutputResolution))
            {
                Bucket(_referenceByAgent, (sample.StepType, sample.AgentDefinitionId)).Add(sample.CreditsMillis);
                Bucket(_referenceByType, sample.StepType).Add(sample.CreditsMillis);
            }

            Bucket(_nativeByAgent, (sample.StepType, sample.AgentDefinitionId, sample.OutputResolution)).Add(sample.CreditsMillis);
            Bucket(_nativeByType, (sample.StepType, sample.OutputResolution)).Add(sample.CreditsMillis);
        }
    }

    /// <summary>Whether any sample was loaded at all: the difference between an estimate and a guess.</summary>
    public bool HasHistory => _byAgent.Count > 0;

    /// <summary>The number of samples behind <paramref name="stepType"/>, across every agent.</summary>
    public int SampleCount(StepType stepType) => _byType.TryGetValue(stepType, out List<long>? values) ? values.Count : 0;

    /// <summary>
    /// The estimate for one step. <paramref name="agentDefinitionId"/> refines a step-type bucket down
    /// to the exact agent (B0's third query is the reason: the step-type split hides where the tokens
    /// are), and is ignored when that bucket is too thin to trust.
    /// </summary>
    /// <param name="videoGenerateHoldMillis">
    /// B6c: the millicredits a <see cref="StepType.VideoGenerate"/> step's own settings say it will
    /// buy (<see cref="VideoGenerateCreditPricing"/>), when the caller could price them. Null — the
    /// ordinary case for every other step type, and the fallback for a VideoGenerate step whose
    /// provider or price could not be resolved — leaves the history/defaults path below in charge.
    /// A declared figure WINS over history, because it is not an estimate at all: it is the same
    /// quantity x the same rate card the settlement charges, so it is exact by construction where a
    /// p90 of unrelated takes is only representative.
    /// </param>
    /// <param name="markup">
    /// The live markup the cold-start default is priced at (T-032; the run's price table's); null = the default markup.
    /// </param>
    /// <param name="referenceOnly">
    /// False reads every sample (pooled across output resolutions); true reads only the reference samples, Auto and
    /// 1080p (<see cref="ResolutionHoldScaling.IsReference"/>), for a step that renders the output and whose hold is
    /// then scaled to the run's resolution (QA-15). Same ladder and minimum either way.
    /// </param>
    /// <param name="computeOnly">
    /// The step is a VideoAnalyze that calls no provider (<see cref="StepCreditDefaults.IsComputeOnly"/>): on
    /// <b>every</b> branch its numbers are the compute (processing time) part only, at <paramref name="markup"/> —
    /// a p90 of token-bearing runs of the same step type would hold (and show) model tokens the step will never
    /// spend. The branch is still taken, so <see cref="StepCreditEstimate.Basis"/> keeps naming whether this
    /// organization has usable history for the step type; only the numbers change.
    /// </param>
    public StepCreditEstimate Estimate(
        StepType stepType,
        Guid? agentDefinitionId,
        long? videoGenerateHoldMillis = null,
        decimal? markup = null,
        bool referenceOnly = false,
        bool computeOnly = false)
    {
        Dictionary<(StepType Type, Guid? Agent), List<long>> byAgent = referenceOnly ? _referenceByAgent : _byAgent;
        Dictionary<StepType, List<long>> byType = referenceOnly ? _referenceByType : _byType;

        if (StepCreditDefaults.IsDeclaredPriced(stepType) && videoGenerateHoldMillis is { } declared && declared > 0)
        {
            return new StepCreditEstimate(declared, declared, declared,
                stepType == StepType.AudioGenerate ? CreditEstimateBasis.AudioGeneration
                : stepType == StepType.ImageGenerate ? CreditEstimateBasis.ImageGeneration
                : CreditEstimateBasis.VideoGeneration);
            // (A hosted VideoEnhance step is declared-priced too and reports the VideoGeneration basis: both are "priced from the step's own settings".)
        }

        if (StepCreditDefaults.IsNotCharged(stepType) || (computeOnly && stepType is StepType.AudioCleanup or StepType.VideoEnhance))
        {
            return new StepCreditEstimate(0, 0, 0, CreditEstimateBasis.NotCharged);
        }

        if (agentDefinitionId is { } agentId
            && byAgent.TryGetValue((stepType, agentId), out List<long>? exact)
            && exact.Count >= _minSamples)
        {
            return ComputeOnly(From(exact, CreditEstimateBasis.History));
        }

        if (byType.TryGetValue(stepType, out List<long>? perType) && perType.Count >= _minSamples)
        {
            return ComputeOnly(From(perType, CreditEstimateBasis.StepTypeHistory));
        }

        // Cold start: B0's defaults, and the HOLD is the low end. See this class's summary.
        (long low, long high) = StepCreditDefaults.MillisFor(stepType, markup ?? PricingDefaults.DefaultMarkup, computeOnly);
        return ComputeOnly(new StepCreditEstimate(low, high, low, CreditEstimateBasis.Default));

        // QA-P2-8: the compute-only price replaces every branch's numbers, so a step that calls no model can
        // never hold or show the tokens a transcript or a caption pass would add. Applied here, once, rather
        // than as an early return, so the basis still names the branch and a step with usable history is not
        // pushed back into the cold-start allocation.
        StepCreditEstimate ComputeOnly(StepCreditEstimate estimate)
        {
            if (!computeOnly || stepType != StepType.VideoAnalyze)
                return estimate;

            (long computeLow, long computeHigh) =
                StepCreditDefaults.MillisFor(stepType, markup ?? PricingDefaults.DefaultMarkup, computeOnly: true);
            return estimate with { LowMillis = computeLow, HighMillis = computeHigh, HoldMillis = computeLow };
        }
    }

    /// <summary>
    /// QA-15: the hold (p90) of <paramref name="stepType"/>'s samples from runs that chose exactly
    /// <paramref name="outputResolution"/> — the exact agent's, else the step type's — with the same
    /// minimum as <see cref="Estimate(StepType, Guid?, long?, decimal?, bool)"/>. Null for a reference resolution (Auto,
    /// 1080p: <see cref="ResolutionHoldScaling.IsReference"/>), a step type that is not charged, or too
    /// few samples. Only ever raises a hold (<see cref="ResolutionHoldScaling.HoldAt"/>).
    /// </summary>
    public long? NativeHoldMillis(StepType stepType, Guid? agentDefinitionId, int? outputResolution)
    {
        if (ResolutionHoldScaling.IsReference(outputResolution) || StepCreditDefaults.IsNotCharged(stepType))
        {
            return null;
        }

        if (agentDefinitionId is { } agentId
            && _nativeByAgent.TryGetValue((stepType, agentId, outputResolution), out List<long>? exact)
            && exact.Count >= _minSamples)
        {
            return From(exact, CreditEstimateBasis.History).HoldMillis;
        }

        return _nativeByType.TryGetValue((stepType, outputResolution), out List<long>? perType) && perType.Count >= _minSamples
            ? From(perType, CreditEstimateBasis.StepTypeHistory).HoldMillis
            : null;
    }

    private static List<long> Bucket<TKey>(Dictionary<TKey, List<long>> buckets, TKey key)
        where TKey : notnull
    {
        if (!buckets.TryGetValue(key, out List<long>? values))
        {
            values = [];
            buckets[key] = values;
        }

        return values;
    }

    private static StepCreditEstimate From(IReadOnlyCollection<long> credits, CreditEstimateBasis basis)
    {
        long[] sorted = [.. credits.Order()];
        long p50 = CreditPercentiles.Continued(sorted, 0.5);
        long p90 = CreditPercentiles.Continued(sorted, 0.9);

        // Clamp out a percentile inversion: with a heavily repeated sample (three identical zero-cost
        // rows, say) rounding can leave p50 above p90, and a range whose top is below its bottom is
        // nonsense to show a user.
        return new StepCreditEstimate(Math.Min(p50, p90), Math.Max(p50, p90), Math.Max(p50, p90), basis);
    }
}
