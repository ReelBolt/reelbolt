using System;
using System.Collections.Generic;
using System.Linq;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Services.Video.Events;

namespace ReelBolt.WorkflowEngine.Tests.Video.Events;

/// <summary>
/// Builds 160x90 gray frame sequences with exactly known event frames, and runs them through the real analyzer and
/// classifier (camera moves and impacts): pure in-memory tests of the detectors, no ffmpeg.
/// </summary>
internal static class SyntheticFrames
{
    public const int W = FrameSignalSampler.Width;
    public const int H = FrameSignalSampler.Height;

    public static byte[] Render(Func<int, int, double> pixel)
    {
        byte[] frame = new byte[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                frame[y * W + x] = (byte)Math.Clamp((int)Math.Round(pixel(x, y)), 0, 255);
        return frame;
    }

    public static List<VideoEvent> Detect(IEnumerable<byte[]> frames)
    {
        FrameEventAnalyzer analyzer = new();
        int index = 0;
        foreach (byte[] frame in frames)
            analyzer.OnFrame(index++, frame);
        return VisualEventClassifier.Classify(analyzer.Complete());
    }

    public static IEnumerable<byte[]> Sequence(int count, Func<int, byte[]> frame)
    {
        for (int t = 0; t < count; t++)
            yield return frame(t);
    }

    /// <summary>Smooth background texture, slightly different per <paramref name="seed"/>.</summary>
    public static double Background(double x, double y, int seed = 0) =>
        110 + 40 * Math.Sin(0.17 * x + seed) + 30 * Math.Sin(0.12 * y + 0.04 * x + seed * 0.7) + 20 * Math.Sin(0.31 * x + 0.2 * y);

    /// <summary>A nearly flat background with a faint gradient, like a slide or a UI: the usual home of appearing elements.</summary>
    public static double Flat(double x, double y) => 70 + 0.05 * x + 0.03 * y;

    /// <summary>A blocky "slide": two panels on a flat background.</summary>
    public static double SlideA(double x, double y) =>
        Over(Over(Flat(x, y), 215, Box(x, y, 10, 10, 60, 30)), 25, Box(x, y, 100, 50, 45, 25));

    /// <summary>A different slide (different background, different panels), for cuts and dissolves.</summary>
    public static double SlideB(double x, double y) =>
        Over(Over(190 - 0.2 * x, 40, Box(x, y, 70, 8, 70, 40)), 235, Box(x, y, 12, 55, 50, 20));

    private static double Over(double under, double over, double opacity) => under + opacity * (over - under);

    /// <summary>1 inside, 0 outside, a one pixel anti-aliased edge: lets a box sit at a sub-pixel position.</summary>
    public static double Box(double x, double y, double x0, double y0, double w, double h)
    {
        double cx = Math.Clamp(x - x0 + 0.5, 0, 1) * Math.Clamp(x0 + w - x + 0.5, 0, 1);
        double cy = Math.Clamp(y - y0 + 0.5, 0, 1) * Math.Clamp(y0 + h - y + 0.5, 0, 1);
        return cx * cy;
    }

    public static double Ramp(int t, int from, int to) => Math.Clamp((t - from) / (double)(to - from), 0, 1);

    /// <summary>Asserts an event of <paramref name="type"/> lies within +/-<paramref name="tolerance"/> frames of <paramref name="frame"/>.</summary>
    public static bool Has(IEnumerable<VideoEvent> events, VideoEventType type, int frame, int tolerance = 2) =>
        events.Any(e => e.Type == type && Math.Abs(e.Frame - frame) <= tolerance);

    public static string Describe(IEnumerable<VideoEvent> events) =>
        string.Join(", ", events.Select(e => $"{e.Type}@{e.Frame}"));
}
