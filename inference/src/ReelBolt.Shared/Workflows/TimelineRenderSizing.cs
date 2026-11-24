namespace ReelBolt.Shared.Workflows;

/// <summary>
/// The output dimensions a <see cref="TimelineRenderStepConfig"/> encodes at — the one size table the
/// engine's preset resolver (<c>TimelineRenderPresets</c>), the engine's plan clamp and the API's
/// plan gate all read, so the three cannot drift apart.
/// </summary>
/// <remarks>
/// Fixed presets carry their own size (Hd 1920x1080, Uhd 3840x2160, Vertical 1080x1920, Square
/// 1080x1080, Portrait 1080x1350); <see cref="TimelineRenderPreset.Master"/> and
/// <see cref="TimelineRenderPreset.AudioOnly"/> take the timeline's own settings, evened and clamped;
/// an explicit <see cref="TimelineRenderEncodeSettings"/> width AND height win over all of them.
/// </remarks>
public static class TimelineRenderSizing
{
    /// <summary>R10.4: the short side of a Preview render (landscape 960x540, portrait 540x960, square 540x540).</summary>
    public const int PreviewShortSide = 540;

    /// <summary>
    /// The width and height the encode targets. For <see cref="TimelineRenderPreset.AudioOnly"/> this
    /// is the (unused) size the resolver carries; callers asking "how big is the picture" use
    /// <see cref="ShortSide"/>, which is null there.
    /// </summary>
    public static (int W, int H) Dimensions(TimelineRenderStepConfig config, int timelineWidth, int timelineHeight)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.Encode is { Width: { } w, Height: { } h })
            return (w, h);

        return config.Preset switch
        {
            TimelineRenderPreset.Hd1080p16x9 => (1920, 1080),
            TimelineRenderPreset.Uhd4k16x9 => (3840, 2160),
            TimelineRenderPreset.Vertical9x16 => (1080, 1920),
            TimelineRenderPreset.Square1x1 => (1080, 1080),
            TimelineRenderPreset.Portrait4x5 => (1080, 1350),
            TimelineRenderPreset.Preview => CapToShortSide(Even(timelineWidth), Even(timelineHeight), PreviewShortSide),
            TimelineRenderPreset.Master or TimelineRenderPreset.AudioOnly => (Even(timelineWidth), Even(timelineHeight)),
            _ => throw new ArgumentOutOfRangeException(nameof(config), $"Unknown preset '{config.Preset}'.")
        };
    }

    /// <summary>The short side of the rendered picture; null for <see cref="TimelineRenderPreset.AudioOnly"/> (no picture).</summary>
    public static int? ShortSide(TimelineRenderStepConfig config, int timelineWidth, int timelineHeight)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Preset == TimelineRenderPreset.AudioOnly) return null;

        (int w, int h) = Dimensions(config, timelineWidth, timelineHeight);
        return Math.Min(w, h);
    }

    /// <summary>
    /// The short side the caller EXPLICITLY asked for — the <see cref="TimelineRenderPreset.Uhd4k16x9"/>
    /// preset or an encode width/height — or null when the size is a shape preset or the timeline's
    /// own (which the engine clamps to the plan instead of refusing) or there is no picture.
    /// Never reads the timeline's settings: an explicit size does not depend on them.
    /// </summary>
    public static int? ExplicitShortSide(TimelineRenderStepConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Preset == TimelineRenderPreset.AudioOnly) return null;

        if (config.Encode is { Width: { } w, Height: { } h })
            return Math.Min(w, h);

        return config.Preset == TimelineRenderPreset.Uhd4k16x9 ? 2160 : null;
    }

    /// <summary>
    /// <paramref name="width"/>x<paramref name="height"/> scaled down so its short side is at most
    /// <paramref name="cap"/>: aspect-preserving, both sides even (nearest even for the long side, the
    /// largest even at or under the cap for the short side), never upscaled.
    /// </summary>
    public static (int W, int H) CapToShortSide(int width, int height, int cap)
    {
        int shortSide = Math.Min(width, height);
        if (shortSide <= cap || shortSide <= 0) return (width, height);

        int cappedShort = Math.Max(2, cap - cap % 2);
        int longSide = Math.Max(width, height);
        int cappedLong = Math.Max(2, NearestEven(longSide * (double)cappedShort / shortSide));

        return width >= height ? (cappedLong, cappedShort) : (cappedShort, cappedLong);
    }

    private static int Even(int value)
    {
        int clamped = Math.Clamp(value, TimelineRenderStepConfig.MinDimension, TimelineRenderStepConfig.MaxWidth);
        return clamped % 2 == 0 ? clamped : clamped - 1;
    }

    private static int NearestEven(double value) => (int)Math.Round(value / 2.0, MidpointRounding.AwayFromZero) * 2;
}
