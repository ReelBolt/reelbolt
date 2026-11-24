using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// Sizes the credit hold of a hosted <c>VideoEnhance</c> STEP from its own config, before the source is probed (the engine
/// holds before the step runs). The config carries no picture size or frame rate, so the hold is the WORST CASE of a
/// reference source: 1920x1080 at 60 fps (the doubled <c>-hfr</c> upscale tier, the 60 fps frame count) for the config's
/// own length bound (<c>maxSourceSeconds</c>, which the route sets to the probed length, else the hosted cap). When the route
/// measured the source it writes its width, height and frame rate into the config (<c>sourceWidth</c>, <c>sourceHeight</c>,
/// <c>sourceFps</c>) and those are priced instead of the reference, so the hold, the estimate and the route's balance check are
/// the same figure for the real source. The executor
/// refuses a source whose real price exceeds the hold, so a hold is never under what the step can cost, and settlement
/// meters the real figure. Fail closed: anything that cannot be priced answers <c>null</c>, never zero.
/// </summary>
public static class VideoEnhanceStepPricing
{
    /// <summary>The reference frame rate the hold is sized at.</summary>
    public const double ReferenceFps = 60;

    private const int ReferenceHeight = 1080;
    private const int ReferenceWidth = 1920;

    /// <summary>The hold in millicredits: 0 for a local operation, the price-book figure for a hosted one, null when unpriced.</summary>
    public static async ValueTask<long?> TryStepHoldMillisAsync(
        string? configJson,
        IInferenceProviderResolver providers,
        IModelPriceService prices,
        CancellationToken ct = default,
        VideoEnhanceHostedSettings? settings = null)
    {
        settings ??= VideoEnhanceHostedSettings.Default;
        if (!StepCreditDefaults.TryParseVideoEnhance(configJson, out VideoEnhanceStepConfig? config) || config is null)
            return null;

        if (!config.IsHosted)
            return 0L;

        int seconds = config.EffectiveMaxSourceSeconds(settings.HostedMaxSourceSeconds);
        int refWidth = config.HasSourceHints ? config.SourceWidth!.Value : ReferenceWidth;
        int refHeight = config.HasSourceHints ? config.SourceHeight!.Value : ReferenceHeight;
        double refFps = config.HasSourceHints ? config.SourceFps!.Value : ReferenceFps;
        switch (config.Op)
        {
            case VideoEnhanceOp.Upscale:
                {
                    int width = refWidth;
                    int height = refHeight;
                    double factor = config.UpscaleFactor ?? 1;
                    if (config.UpscaleFactor is null && config.TargetHeight is { } target)
                    {
                        // Priced by the OUTPUT short side: a source whose short side is the target, at factor 1.
                        height = target;
                        width = (int)Math.Round(target * 16d / 9d);
                    }

                    return await VideoEnhanceCreditPricing.TryHoldUpscaleMillisAsync(
                        width, height, refFps, seconds, factor, config.ProviderId, providers, prices, seconds, ct).ConfigureAwait(false);
                }

            case VideoEnhanceOp.Interpolate:
                {
                    if (config.FrameMultiplier is not { } multiplier)
                        return null;

                    return await VideoEnhanceCreditPricing.TryHoldInterpolationMillisAsync(
                        refWidth, refHeight, (long)Math.Ceiling(seconds * refFps), multiplier, seconds,
                        config.ProviderId, providers, prices, settings.ComputeSecondsPerOutputFrame, seconds, ct)
                        .ConfigureAwait(false);
                }

            case VideoEnhanceOp.Extend:
                {
                    // The hold is the canvas of the reference (or measured) source: an unofferable canvas is unpriced.
                    if (config.ExtendCanvas(refWidth, refHeight) is not { } canvas)
                        return null;

                    return await VideoEnhanceCreditPricing.TryHoldOutpaintMillisAsync(
                        canvas.Width, canvas.Height, (long)Math.Ceiling(seconds * refFps), seconds,
                        config.ProviderId, providers, prices, seconds, ct, clampFramesToCeiling: true).ConfigureAwait(false);
                }

            default:
                return null;
        }
    }
}
