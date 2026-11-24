using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// The credit hold a <see cref="StepType.VideoAnalyze"/> step's TEXT-PROMPTED SEGMENTATION work adds on
/// top of its token-shaped estimate (initiative "prompted region effects", plan §6).
///
/// <para>
/// <b>Why an add-on and not a replacement.</b> The existing row
/// (<c>StepCreditDefaults</c>'s <c>VideoAnalyze</c> entry) is a token/ASR estimate: it assumes the
/// vision half of the step and the media-analyse share, and it knows nothing about a hosted vendor that
/// bills per frame of video input. A segmentation target buys a call whose price grows with the clip's
/// LENGTH and with the number of targets, so leaving it out would let a run pass the preflight on the
/// token estimate and then bill far more than was reserved. It is expressed as an addition, so the hold
/// can only ever grow: no configuration makes this function subtract from the estimate.
/// </para>
///
/// <para>
/// <b>The arithmetic, and why every term is what it is.</b> The vendor (fal, SAM 3.1 video) charges
/// <b>$0.01 per 16 frames of video input</b> (plan §6, read from the vendor page on 2026-10-18). The
/// step caps a source at <c>MaxSegmentationSeconds</c> (default 60, clamped 1..600 by
/// <c>SegmentationLimits</c>), so the WORST case a run can buy is
/// <c>ceil(MaxSegmentationSeconds × 30 / 16)</c> frame-units per target — 30 fps is an upper bound for
/// the frame rate a customer clip may carry, chosen deliberately above the 24-25 fps of the fixtures so
/// the hold is conservative rather than exact. Each target is one call, so the product over the
/// configured segmentation targets is the bound. The vendor's USD figure becomes credits through
/// <see cref="CreditCurrency.CreditsFromUsd"/> — the ONE conversion in the system — and is rounded UP to
/// whole millicredits, because a hold that rounds down is a hold that can be exceeded by a billing
/// event that does not round.
/// </para>
///
/// <para>
/// <b>Where the price lives.</b> The house rule is that a vendor price has one home,
/// <see cref="ModelPriceCatalog"/>, keyed by usage kind, and the <c>VideoSegmentation</c> row
/// (<see cref="ModelPriceCatalog.FalVideoSegmentationUsdPerUnit"/>, the vendor's own block price, with
/// its sentence and source date in the row's basis) is that home. Every figure below is READ from it:
/// this type owns the ARITHMETIC — the 16-frame block, the 30 fps worst-case ceiling and the
/// round-up to whole millicredits — and owns no number a rate card also states. The catalog's per-frame
/// price and <see cref="VideoSegmentationBilling"/>'s rounding are the same row, so a hold and an
/// invoice cannot drift apart.
/// </para>
/// </summary>
public static class SegmentationCreditPricing
{
    /// <summary>
    /// The options the analyze step's own persistence uses: Web defaults plus the string-enum converter,
    /// because a step config stores <c>"method": "Segmentation"</c> by NAME.
    /// </summary>
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The endpoint the price below belongs to: fal's SAM 3.1 text-prompted VIDEO segmenter.</summary>
    public const string DefaultModel = ModelPriceCatalog.FalVideoSegmentationModel;

    /// <summary>Frames of video input one billable unit covers, as the vendor states it.</summary>
    public const int VendorFrameUnit = ModelPriceCatalog.FalVideoSegmentationFramesPerUnit;

    /// <summary>
    /// The vendor's published price of one <see cref="VendorFrameUnit"/> of video input, in USD —
    /// <i>"$0.01 per 16 frames of video input"</i>, fal's <c>fal-ai/sam-3-1/video-rle</c>, read
    /// 2026-10-18 (plan §6). Read from <see cref="ModelPriceCatalog"/> rather than restated here: a
    /// second copy is a second source of truth, and the one that drifts is always the one nobody billed
    /// from.
    /// </summary>
    public static decimal UsdPerFrameUnit => ModelPriceCatalog.FalVideoSegmentationUsdPerUnit;

    /// <summary>
    /// The frame rate the worst-case frame count assumes. 30 is an upper bound for customer footage and
    /// is above every fixture in this initiative (24-30 fps), so the resulting hold is a ceiling on the
    /// vendor's bill for a clip the step already refused to accept longer than its cap.
    /// </summary>
    public const int AssumedMaxFps = 30;

    /// <summary>The seconds per source a step with no explicit cap gets, mirroring <c>VideoAnalyzeStepConfig.MaxSegmentationSeconds</c>'s default.</summary>
    public const int DefaultMaxSegmentationSeconds = 60;

    /// <summary>Credits one billable frame-unit costs, rounded UP to whole millicredits.</summary>
    public static long FrameUnitMillis =>
        (long)decimal.Ceiling(CreditCurrency.CreditsFromUsd(UsdPerFrameUnit) * 1000m);

    /// <summary>
    /// The billable frame-units one target on a <paramref name="maxSeconds"/>-second clip can cost:
    /// <c>ceil(seconds × 30 / 16)</c>. Integer arithmetic throughout — no floating point in a money path.
    /// </summary>
    public static int FrameUnitsFor(int maxSeconds) =>
        maxSeconds <= 0
            ? 0
            : (int)(((long)maxSeconds * AssumedMaxFps + VendorFrameUnit - 1) / VendorFrameUnit);

    /// <summary>
    /// The FRAME COUNT one target's hold buys on a <paramref name="maxSeconds"/>-second clip:
    /// <see cref="FrameUnitsFor"/> rounded up to whole vendor blocks, expressed in frames. This is the
    /// number the analyze step compares a source's own probed frame count against before it calls the
    /// vendor, so the priced bound and the billed quantity are the same quantity.
    /// </summary>
    /// <remarks>
    /// 30 fps is a pricing assumption, not a measurement: the vendor bills the clip's REAL frame count,
    /// so a 60 or 120 fps source costs two to four times what a seconds-only cap prices. Comparing the
    /// probed count against this figure is what makes the cap self-consistent at every frame rate —
    /// <c>SegmentationTrackingService</c> refuses a source above it with
    /// <c>SEGMENTATION_FRAME_BUDGET_EXCEEDED</c> before any provider call, instead of settling the
    /// organization negative after one.
    /// </remarks>
    public static int PricedFramesFor(int maxSeconds) =>
        FrameUnitsFor(SegmentationLimits.ClampSeconds(maxSeconds)) * VendorFrameUnit;

    /// <summary>
    /// The millicredits to add to a <see cref="StepType.VideoAnalyze"/> step's hold for
    /// <paramref name="targets"/> segmentation targets on one cap of
    /// <paramref name="maxSegmentationSeconds"/> seconds each. Zero targets — the default, and every step
    /// that predates segmentation — answers <c>0</c>, so the hold is unchanged.
    /// </summary>
    public static long AddOnMillis(int targets, int maxSegmentationSeconds)
    {
        if (targets <= 0)
        {
            return 0;
        }

        // Clamped with the SAME rule the step applies before it runs (1..600), so the seconds this hold
        // prices and the seconds the engine allows cannot disagree: a config carrying 0 is a 1-second
        // pass at run time, and must be a 1-second hold here.
        return (long)targets
            * FrameUnitsFor(SegmentationLimits.ClampSeconds(maxSegmentationSeconds))
            * FrameUnitMillis;
    }

    /// <summary>
    /// The same figure read out of the step's PERSISTED <c>video_analyze_config_json</c>.
    /// <para>
    /// Read as JSON rather than through <c>VideoAnalyzeStepConfig</c> on purpose: this runs inside the
    /// run-hold plan, which is shared by the API's preflight and the engine's reservation, and the only
    /// thing it needs is the two fields that size the purchase. A config that does not parse, has no
    /// targets, or names no segmentation method answers <c>0</c> — the hold simply does not grow, which
    /// is the same answer as "this step buys no segmentation", and it can never refuse a run.
    /// </para>
    /// </summary>
    public static long AddOnMillis(string? configJson)
    {
        (int targets, int seconds) = Shape(configJson);
        return AddOnMillis(targets, seconds);
    }

    /// <summary>
    /// The (segmentation target count, max seconds) pair <paramref name="configJson"/> implies — the
    /// shape <see cref="AddOnMillis(int, int)"/> prices. A target counts when its <c>method</c> is
    /// <c>Segmentation</c> (case-insensitive, the enum's name in the persisted payload) and it has a
    /// non-blank <c>label</c>, which is also the condition the analyze step applies before it calls the
    /// vendor: a target the step would skip must not be held for.
    /// </summary>
    public static (int Targets, int MaxSeconds) Shape(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return (0, DefaultMaxSegmentationSeconds);
        }

        VideoAnalyzeStepConfig? config;
        try
        {
            // The TYPED step config, not a hand-walked JSON tree: a renamed or retyped member must break
            // the build rather than silently price zero and under-hold a paid vendor call. The Web
            // defaults are what the step's own persistence uses (camelCase, case-insensitive).
            config = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(configJson, ConfigJsonOptions);
        }
        catch (JsonException)
        {
            return (0, DefaultMaxSegmentationSeconds);
        }

        if (config is null)
        {
            return (0, DefaultMaxSegmentationSeconds);
        }

        int targets = (config.TrackObjects ?? [])
            .Count(t => t.Method == ObjectTrackMethod.Segmentation && !string.IsNullOrWhiteSpace(t.Label));

        return (targets, config.MaxSegmentationSeconds);
    }
}
