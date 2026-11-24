using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// The ONE place the hosted video-enhancement (AI upscale, AI frame generation) estimate and credit hold are sized, the
/// twin of <see cref="AudioGenerateCreditPricing"/>. Two units, because the vendors price two ways:
/// <list type="bullet">
///   <item><description><b>Upscale</b> (<see cref="UsageKind.VideoEnhanceSeconds"/>): billed OUTPUT seconds, rounded UP
///   to whole seconds, at the price of the output TIER (<see cref="TierFor"/>: <c>720p</c>, <c>1080p</c>, <c>uhd</c>,
///   and the doubled <c>-hfr</c> twin above 30 fps).</description></item>
///   <item><description><b>Interpolation</b> (<see cref="UsageKind.VideoEnhanceComputeSeconds"/>): GPU COMPUTE seconds.
///   The vendor returns no compute figure, so the hold is <c>ceil(outputFrames x ComputeSecondsPerOutputFrame x
///   (outputPixels / 1920x1080))</c> with the setting <c>VideoEnhance:Hosted:ComputeSecondsPerOutputFrame</c>
///   (default 0.05, UNMEASURED: must be measured with a real key) and settlement is the hold (the existing "floored at
///   the hold when metering failed" rule).</description></item>
/// </list>
/// Both are priced through the SAME <see cref="IModelPriceService"/> lookup (capability, model, unit, variant) that
/// settlement uses.
/// <para>
/// <b>Fail closed.</b> A model with no catalogue entry, a tier with no price, no price row, a request over the length
/// cap, or any failure answers <c>null</c>: "unofferable", never "free". No default price exists.
/// </para>
/// </summary>
public static class VideoEnhanceCreditPricing
{
    /// <summary>Default of <c>VideoEnhance:Hosted:MaxSourceSeconds</c>: the longest source one hosted request may carry.</summary>
    public const int DefaultMaxSourceSeconds = 120;

    /// <summary>The configuration key that overrides <see cref="DefaultMaxSourceSeconds"/>.</summary>
    public const string MaxSourceSecondsKey = "VideoEnhance:Hosted:MaxSourceSeconds";

    /// <summary>Default of <c>VideoEnhance:Hosted:PollTimeoutMinutes</c>: the poll budget of one poll call.</summary>
    public const int DefaultPollTimeoutMinutes = 30;

    /// <summary>The configuration key that overrides <see cref="DefaultPollTimeoutMinutes"/>.</summary>
    public const string PollTimeoutMinutesKey = "VideoEnhance:Hosted:PollTimeoutMinutes";

    /// <summary>
    /// Default of <c>VideoEnhance:Hosted:ComputeSecondsPerOutputFrame</c> at 1920x1080. UNMEASURED: a conservative guess
    /// until a real key measures it (docs/fal-ai-models.md section 13, must-measure table).
    /// </summary>
    public const decimal DefaultComputeSecondsPerOutputFrame = 0.05m;

    /// <summary>The configuration key that overrides <see cref="DefaultComputeSecondsPerOutputFrame"/>.</summary>
    public const string ComputeSecondsPerOutputFrameKey = "VideoEnhance:Hosted:ComputeSecondsPerOutputFrame";

    /// <summary>An output above this frame rate is billed at the doubled "high frame rate" price (the vendor says "60fps").</summary>
    public const double HighFrameRateAboveFps = 30;

    private const long ReferencePixels = 1920L * 1080L;

    /// <summary>The output short side after a scale <paramref name="factor"/>, rounded to the nearest pixel.</summary>
    public static int OutputShortSide(int sourceWidth, int sourceHeight, double factor) =>
        (int)Math.Round(Math.Min(sourceWidth, sourceHeight) * factor, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The price tier of an upscaled output: <c>720p</c> (short side up to 720), <c>1080p</c> (721 to 1080) or <c>uhd</c>
    /// (above 1080), with <c>-hfr</c> appended when <paramref name="outputFps"/> is above 30.
    /// </summary>
    public static string TierFor(int outputShortSide, double outputFps)
    {
        string tier = outputShortSide switch { <= 720 => "720p", <= 1080 => "1080p", _ => "uhd" };
        return outputFps > HighFrameRateAboveFps ? tier + "-hfr" : tier;
    }

    /// <summary>The frame rate bytedance is asked for: the source rate rounded and clamped to its 24..120 range.</summary>
    public static int BytedanceTargetFps(double sourceFps) => (int)Math.Clamp(Math.Round(sourceFps), 24, 120);

    /// <summary>The frame rate the endpoint outputs: bytedance forces <see cref="BytedanceTargetFps"/>, Topaz keeps the source's.</summary>
    public static double OutputFps(VideoEnhanceEndpoint endpoint, double sourceFps) =>
        endpoint.Id == VideoEnhanceModelCatalog.BytedanceId ? BytedanceTargetFps(sourceFps) : sourceFps;

    /// <summary>
    /// The tier of an upscale on <paramref name="endpoint"/>, or <c>null</c> when the endpoint has no price for it
    /// (an unpriced tier is unofferable).
    /// </summary>
    public static string? UpscaleTierFor(VideoEnhanceEndpoint endpoint, int outputShortSide, double sourceFps)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        double fps = OutputFps(endpoint, sourceFps);
        // Fail closed above what the vendor's sentences cover: no 8K tier, no price for a rate above the cited 60 fps.
        if (endpoint.MaxPricedShortSide is { } maxSide && outputShortSide > maxSide) return null;
        if (endpoint.MaxPricedFps is { } maxFps && fps > maxFps) return null;
        string tier = TierFor(outputShortSide, fps);
        return endpoint.UsdPerSecondByTier is { } tiers && tiers.ContainsKey(tier) ? tier : null;
    }

    /// <summary>Whole billed seconds of <paramref name="outputSeconds"/> (exact, never pre-rounded): 5 s is 5, 5.001 s is 6.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The duration is not positive.</exception>
    public static decimal BilledSeconds(double outputSeconds)
    {
        if (!(outputSeconds > 0) || double.IsInfinity(outputSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(outputSeconds), outputSeconds, "A billed duration must be positive.");
        }

        return Math.Ceiling((decimal)outputSeconds);
    }

    /// <summary>True when <paramref name="sourceSeconds"/> is within the cap (<paramref name="maxSourceSeconds"/>, inclusive).</summary>
    public static bool WithinCap(double sourceSeconds, int maxSourceSeconds = DefaultMaxSourceSeconds) =>
        sourceSeconds > 0 && !double.IsInfinity(sourceSeconds) && maxSourceSeconds > 0 && (decimal)sourceSeconds <= maxSourceSeconds;

    /// <summary>The output frame count of an interpolation: the source frames times the multiplier (an upper bound).</summary>
    public static long OutputFrames(long sourceFrames, int multiplier) => sourceFrames * multiplier;

    /// <summary>
    /// The compute seconds to HOLD for an interpolation: <c>ceil(outputFrames x perFrame x (outputPixels / 1920x1080))</c>.
    /// An estimate, not a measurement: the vendor reports no compute time.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A size, frame count or rate is not positive.</exception>
    public static decimal ComputeSecondsHold(
        long outputFrames, int outputWidth, int outputHeight, decimal computeSecondsPerOutputFrame = DefaultComputeSecondsPerOutputFrame)
    {
        if (outputFrames <= 0 || outputWidth <= 0 || outputHeight <= 0 || computeSecondsPerOutputFrame <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(outputFrames), "Frames, size and the per-frame rate must be positive.");
        }

        decimal scale = (decimal)((long)outputWidth * outputHeight) / ReferencePixels;
        return Math.Ceiling(outputFrames * computeSecondsPerOutputFrame * scale);
    }

    /// <summary>
    /// The vendor-list USD of an upscale of <paramref name="durationSec"/> seconds, or <c>null</c> when it cannot be priced
    /// (unknown tier, over the cap, not an upscaler). Never zero, never a default.
    /// </summary>
    public static decimal? EstimateUpscaleUsd(
        VideoEnhanceEndpoint endpoint, int sourceWidth, int sourceHeight, double sourceFps, double durationSec, double factor,
        int maxSourceSeconds = DefaultMaxSourceSeconds)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!WithinCap(durationSec, maxSourceSeconds)) return null;
        string? tier = UpscaleTierFor(endpoint, OutputShortSide(sourceWidth, sourceHeight, factor), sourceFps);
        if (tier is null) return null;
        decimal estimate = BilledSeconds(durationSec) * endpoint.UsdPerSecondByTier![tier];
        return estimate > 0m ? estimate : null;
    }

    /// <summary>
    /// The vendor-list USD of an interpolation (the held compute seconds times the compute-second price), or <c>null</c>
    /// when it cannot be priced (over the cap, not an interpolator).
    /// </summary>
    public static decimal? EstimateInterpolationUsd(
        VideoEnhanceEndpoint endpoint, int sourceWidth, int sourceHeight, long sourceFrames, int multiplier, double durationSec,
        decimal computeSecondsPerOutputFrame = DefaultComputeSecondsPerOutputFrame, int maxSourceSeconds = DefaultMaxSourceSeconds)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!WithinCap(durationSec, maxSourceSeconds) || endpoint.UsdPerComputeSecond is not { } price) return null;
        decimal estimate = ComputeSecondsHold(OutputFrames(sourceFrames, multiplier), sourceWidth, sourceHeight, computeSecondsPerOutputFrame) * price;
        return estimate > 0m ? estimate : null;
    }

    /// <summary>
    /// The price row of an outpaint output with short side <paramref name="outputShortSide"/>: the Wan VACE resolution
    /// tier (<c>480p</c> up to 480, <c>580p</c> up to 580, <c>720p</c> above), or the flat Luma rate with no variant.
    /// <c>null</c> when the endpoint has no cited price for that output (unofferable).
    /// </summary>
    public static (string? Variant, decimal UsdPerSecond)? OutpaintRateFor(VideoEnhanceEndpoint endpoint, int outputShortSide)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (endpoint.UsdPerSecondFlat is { } flat) return (null, flat);

        string tier = outputShortSide switch { <= 480 => "480p", <= 580 => "580p", _ => "720p" };
        return endpoint.UsdPerSecondByTier is { } tiers && tiers.TryGetValue(tier, out decimal price) ? (tier, price) : null;
    }

    /// <summary>
    /// The seconds an outpaint bills: Wan VACE counts <c>frames / 16</c> rounded UP (its price sentence bills video
    /// seconds at 16 fps, whatever the source rate); Luma bills the clip's own seconds, rounded up.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The frame count or duration is not positive.</exception>
    public static decimal OutpaintBilledSeconds(VideoEnhanceEndpoint endpoint, long outputFrames, double durationSec)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (endpoint.UsdPerSecondByTier is not null)
        {
            if (outputFrames <= 0) throw new ArgumentOutOfRangeException(nameof(outputFrames), outputFrames, "Frames must be positive.");
            // The vendor enforces a minimum frame count, so a shorter source is billed as the minimum: never under-billed.
            long billedFrames = Math.Max(outputFrames, endpoint.MinOutputFrames ?? 0);
            return Math.Ceiling(billedFrames / (decimal)VideoEnhanceModelCatalog.WanVaceBillingFps);
        }

        return BilledSeconds(durationSec);
    }

    /// <summary>
    /// True when <paramref name="sourceFrames"/> is within the endpoint's own frame ceiling (Wan VACE: 241). An endpoint
    /// with no ceiling (Luma) accepts any positive count.
    /// </summary>
    public static bool WithinFrameCap(VideoEnhanceEndpoint endpoint, long sourceFrames)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (sourceFrames <= 0) return false;
        return endpoint.MaxOutputFrames is not { } max || sourceFrames <= max;
    }

    /// <summary>
    /// True when <paramref name="sourceFrames"/> reaches the endpoint's own frame floor (Wan VACE: 81). An endpoint with
    /// no floor accepts any positive count.
    /// </summary>
    public static bool AtLeastMinFrames(VideoEnhanceEndpoint endpoint, long sourceFrames)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return sourceFrames > 0 && (endpoint.MinOutputFrames is not { } min || sourceFrames >= min);
    }

    /// <summary>The shortest source the frame floor allows at <paramref name="sourceFps"/>, in whole seconds rounded up; null with no floor.</summary>
    public static int? MinFrameSeconds(VideoEnhanceEndpoint endpoint, double sourceFps)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.MinOutputFrames is not { } min || !(sourceFps > 0)) return null;
        return (int)Math.Ceiling(min / sourceFps);
    }

    /// <summary>
    /// The longest source the Wan VACE frame ceiling allows at <paramref name="sourceFps"/> (241 / fps), for the
    /// SOURCE_TOO_LONG message. Null when the endpoint has no frame ceiling.
    /// </summary>
    public static double? FrameCapSeconds(VideoEnhanceEndpoint endpoint, double sourceFps)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.MaxOutputFrames is not { } max || !(sourceFps > 0)) return null;
        return max / sourceFps;
    }

    /// <summary>
    /// The vendor-list USD of an outpaint of <paramref name="outputFrames"/> frames, or <c>null</c> when it cannot be
    /// priced (over the duration or frame cap, a tier with no row, not an outpaint endpoint). Never zero, never a default.
    /// </summary>
    public static decimal? EstimateOutpaintUsd(
        VideoEnhanceEndpoint endpoint, int outputWidth, int outputHeight, long outputFrames, double durationSec,
        int maxSourceSeconds = DefaultMaxSourceSeconds)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!WithinCap(durationSec, maxSourceSeconds) || !WithinFrameCap(endpoint, outputFrames)) return null;
        if (OutpaintRateFor(endpoint, Math.Min(outputWidth, outputHeight)) is not { } rate) return null;
        decimal estimate = OutpaintBilledSeconds(endpoint, outputFrames, durationSec) * rate.UsdPerSecond;
        return estimate > 0m ? estimate : null;
    }

    /// <summary>
    /// The millicredits to estimate for (and hold on) an upscale on the provider <paramref name="explicitProviderId"/> (or
    /// the default VideoUpscale row), or <c>null</c> when it cannot be priced: no provider, a model with no catalogue entry,
    /// a tier or price row that does not exist, a source over the cap.
    /// </summary>
    public static async ValueTask<long?> TryHoldUpscaleMillisAsync(
        int sourceWidth,
        int sourceHeight,
        double sourceFps,
        double durationSec,
        double factor,
        Guid? explicitProviderId,
        IInferenceProviderResolver providers,
        IModelPriceService prices,
        int maxSourceSeconds = DefaultMaxSourceSeconds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(prices);

        try
        {
            if (!WithinCap(durationSec, maxSourceSeconds)) return null;

            ResolvedAudioGenerationProvider? provider = await providers.ResolveVideoUpscaleAsync(explicitProviderId, ct).ConfigureAwait(false);
            if (provider is null) return null;

            VideoEnhanceEndpoint? endpoint = VideoEnhanceModelCatalog.Instance.Find(InferenceProviderCapability.VideoUpscale, provider.ModelName);
            if (endpoint is null || provider.Kind != InferenceProviderKind.FalAi) return null;

            string? tier = UpscaleTierFor(endpoint, OutputShortSide(sourceWidth, sourceHeight, factor), sourceFps);
            if (tier is null) return null;

            PriceQuote quote = await prices.PriceAsync(new PriceRequest(
                provider.ProviderId,
                provider.AccountId,
                provider.Kind,
                InferenceProviderCapability.VideoUpscale,
                endpoint.Id,
                endpoint.Unit,
                tier,
                MeteringChatClient.BillingModeOf(provider.OrganizationId),
                BilledSeconds(durationSec)), ct).ConfigureAwait(false);

            return quote.CreditsMillis;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // UnpricedUsageException, a row that will not decrypt, a database that is down: "cannot size this", never "free".
            return null;
        }
    }

    /// <summary>
    /// The millicredits to estimate for (and hold on) a frame interpolation: the compute-second hold at the compute-second
    /// price, or <c>null</c> when it cannot be priced. The settlement is this same hold (no vendor figure exists).
    /// </summary>
    public static async ValueTask<long?> TryHoldInterpolationMillisAsync(
        int sourceWidth,
        int sourceHeight,
        long sourceFrames,
        int multiplier,
        double durationSec,
        Guid? explicitProviderId,
        IInferenceProviderResolver providers,
        IModelPriceService prices,
        decimal computeSecondsPerOutputFrame = DefaultComputeSecondsPerOutputFrame,
        int maxSourceSeconds = DefaultMaxSourceSeconds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(prices);

        try
        {
            if (!WithinCap(durationSec, maxSourceSeconds) || sourceFrames <= 0) return null;

            ResolvedAudioGenerationProvider? provider = await providers.ResolveFrameInterpolationAsync(explicitProviderId, ct).ConfigureAwait(false);
            if (provider is null) return null;

            VideoEnhanceEndpoint? endpoint = VideoEnhanceModelCatalog.Instance.Find(InferenceProviderCapability.FrameInterpolation, provider.ModelName);
            if (endpoint is null || provider.Kind != InferenceProviderKind.FalAi || !endpoint.HostedMultipliers.Contains(multiplier)) return null;

            PriceQuote quote = await prices.PriceAsync(new PriceRequest(
                provider.ProviderId,
                provider.AccountId,
                provider.Kind,
                InferenceProviderCapability.FrameInterpolation,
                endpoint.Id,
                endpoint.Unit,
                null,
                MeteringChatClient.BillingModeOf(provider.OrganizationId),
                ComputeSecondsHold(OutputFrames(sourceFrames, multiplier), sourceWidth, sourceHeight, computeSecondsPerOutputFrame)), ct)
                .ConfigureAwait(false);

            return quote.CreditsMillis;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The millicredits to estimate for (and hold on) an outpaint on the provider <paramref name="explicitProviderId"/>
    /// (or the default VideoOutpaint row), or <c>null</c> when it cannot be priced: no provider, a model with no
    /// catalogue entry, a tier with no price row, a source over the duration or frame cap.
    /// </summary>
    public static async ValueTask<long?> TryHoldOutpaintMillisAsync(
        int outputWidth,
        int outputHeight,
        long outputFrames,
        double durationSec,
        Guid? explicitProviderId,
        IInferenceProviderResolver providers,
        IModelPriceService prices,
        int maxSourceSeconds = DefaultMaxSourceSeconds,
        CancellationToken ct = default,
        bool clampFramesToCeiling = false)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(prices);

        try
        {
            if (!WithinCap(durationSec, maxSourceSeconds)) return null;

            ResolvedAudioGenerationProvider? provider = await providers.ResolveVideoOutpaintAsync(explicitProviderId, ct).ConfigureAwait(false);
            if (provider is null) return null;

            VideoEnhanceEndpoint? endpoint = VideoEnhanceModelCatalog.Instance.Find(InferenceProviderCapability.VideoOutpaint, provider.ModelName);
            if (endpoint is null || provider.Kind != InferenceProviderKind.FalAi) return null;
            // A step hold is sized for the WORST source its length cap allows; the model can never be sent more than its ceiling,
            // so the hold stops there (a longer real source is refused by the executor with the cap in seconds, not unpriced).
            if (clampFramesToCeiling && endpoint.MaxOutputFrames is { } ceiling) outputFrames = Math.Min(outputFrames, ceiling);
            if (!WithinFrameCap(endpoint, outputFrames)) return null;
            if (OutpaintRateFor(endpoint, Math.Min(outputWidth, outputHeight)) is not { } rate) return null;

            PriceQuote quote = await prices.PriceAsync(new PriceRequest(
                provider.ProviderId,
                provider.AccountId,
                provider.Kind,
                InferenceProviderCapability.VideoOutpaint,
                endpoint.Id,
                endpoint.Unit,
                rate.Variant,
                MeteringChatClient.BillingModeOf(provider.OrganizationId),
                OutpaintBilledSeconds(endpoint, outputFrames, durationSec)), ct).ConfigureAwait(false);

            return quote.CreditsMillis;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
