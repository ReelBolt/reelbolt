namespace ReelBolt.Shared.Workflows;

/// <summary>
/// A rough output file size for a delivery render, shared by the estimate route and its tests. Pure:
/// seconds, picture size and encode settings in, bytes out.
/// </summary>
/// <remarks>
/// Average rate control is close but not exact (the stated video plus audio bitrate over the length, plus a
/// small container allowance; single-pass output lands within about 10-15%, so it is flagged approximate). Quality and Capped depend on the picture, so they use a coarse
/// bits-per-pixel figure that halves every 6 CRF steps, scaled for codec and speed; the answer is
/// labelled approximate and Capped is never larger than its ceiling.
/// </remarks>
public static class RenderSizeEstimator
{
    /// <summary>Container and muxing overhead on top of the elementary streams.</summary>
    public const double ContainerAllowance = 1.02;

    /// <summary>Bits per pixel per frame at CRF 23, H.264, medium speed, typical content.</summary>
    public const double BaseBitsPerPixelAtCrf23 = 0.07;

    /// <summary>The estimated size in bytes and whether it is only approximate (true for every video size, including Average); null when there is nothing to estimate.</summary>
    public static (long Bytes, bool Approximate)? Estimate(
        double seconds, int width, int height, double fps, bool audioOnly, int crf,
        TimelineRenderVideoCodec codec, TimelineRenderEncoderSpeed speed, int audioBitrateKbps,
        TimelineRenderRateControl rateControl, int? videoBitrateKbps)
    {
        if (!(seconds > 0) || double.IsInfinity(seconds)) return null;

        double audioBits = audioBitrateKbps * 1000.0 * seconds;
        if (audioOnly)
            return ((long)Math.Round(audioBits / 8 * ContainerAllowance), false);

        if (width <= 0 || height <= 0 || !(fps > 0)) return null;

        if (rateControl == TimelineRenderRateControl.Average && videoBitrateKbps is { } target)
            return ((long)Math.Round((((target * 1000.0 * seconds) + audioBits) / 8) * ContainerAllowance), true);

        double bpp = BaseBitsPerPixelAtCrf23 * Math.Pow(2, -(crf - 23) / 6.0)
            * (codec == TimelineRenderVideoCodec.H265 ? 0.6 : 1.0)
            * speed switch { TimelineRenderEncoderSpeed.Fast => 1.15, TimelineRenderEncoderSpeed.Slow => 0.9, _ => 1.0 };
        double videoBitsPerSecond = bpp * width * height * fps;
        if (rateControl == TimelineRenderRateControl.Capped && videoBitrateKbps is { } cap)
            videoBitsPerSecond = Math.Min(videoBitsPerSecond, cap * 1000.0);

        return ((long)Math.Round((((videoBitsPerSecond * seconds) + audioBits) / 8) * ContainerAllowance), true);
    }
}
