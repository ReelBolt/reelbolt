using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>One tracked region to obscure for one stretch of the output: the padded corners per frame (canvas pixels, frame index relative to <see cref="OutputStartSec"/>).</summary>
public sealed record ResolvedCensorRegion(
    string RegionId,
    string? Label,
    double OutputStartSec,
    double OutputEndSec,
    IReadOnlyList<InsertQuadFrame> Keyframes,
    int FpsNum,
    int FpsDen,
    /// <summary>Margin added on every side, as a fraction of the region's LONGER side — see <c>VideoCompileStepConfig.CensorPadding</c>.</summary>
    double Padding = 0);

/// <summary>
/// Builds the ffmpeg chain that obscures tracked regions — licence plates, signs — following each
/// region frame by frame:
///
/// <code>
/// [in] split                                    -> base, copy
/// copy obscured ONCE (blur / pixelate / fill)   -> obscured
/// one combined mask: a black canvas with, per region and per one-second chunk of its track, a
///   white box moved every frame by an overlay x/y expression in output time
/// obscured + mask alphamerge, overlaid on base  -> out
/// </code>
///
/// <para>
/// <b>Why moving boxes, not the insert path's perspective warp.</b> The first version warped a
/// full-frame white plate onto each region with <c>perspective</c>, exactly as a screen insert is
/// pinned. That breaks on small targets: ffmpeg's <c>perspective</c> precomputes fixed-point
/// coordinate maps, and squeezing a full plate ~20x onto a licence-plate-sized quad overflows them
/// — on a real car-meetup clip the "mask" covered everything above-left of each plate. Screen
/// inserts never hit it because screens are large. A censor mask needs no perspective at all: an
/// axis-aligned box over the padded quad covers it, and erring a little large is the right bias.
/// </para>
/// <para>
/// <b>Cost.</b> Obscuring is the expensive part and happens once whatever the region count; each
/// region adds only small overlays on a quarter-resolution mask canvas (scaled up at the end).
/// Twelve plates on a 16 s 1080p clip: 64 s with per-region obscuring, 23 s with one.
/// </para>
/// </summary>
public static class CensorFilterBuilder
{
    /// <summary>The combined mask is drawn at 1/this of the picture's size.</summary>
    internal const int MaskDownscale = 4;

    /// <summary>A box is re-sized every this many seconds of a track, to the chunk's largest extent.</summary>
    internal const double ChunkSec = 1.0;

    /// <summary>
    /// The obscuring filter for one style, sized from the canvas so the effect is equally strong at
    /// any resolution: a Gaussian blur of 1.5% of the canvas width, 2.5%-of-width mosaic blocks,
    /// or a solid black fill.
    /// </summary>
    internal static string EffectFilter(CensorStyle style, int frameWidth, int frameHeight) => style switch
    {
        CensorStyle.Pixelate => PixelateFilter(frameWidth, frameHeight),
        CensorStyle.Fill => "drawbox=x=0:y=0:w=iw:h=ih:color=black:t=fill",
        _ => $"gblur=sigma={FfmpegArgvFormat.Number(Math.Max(4, Math.Round(frameWidth * 0.015, 1)))}:steps=3"
    };

    private static string PixelateFilter(int w, int h)
    {
        int block = Math.Max(4, (int)Math.Round(w * 0.025));
        int sw = Math.Max(1, w / block), sh = Math.Max(1, h / block);
        return $"scale={FfmpegArgvFormat.Number(sw)}:{FfmpegArgvFormat.Number(sh)}:flags=area," +
               $"scale={FfmpegArgvFormat.Number(w)}:{FfmpegArgvFormat.Number(h)}:flags=neighbor";
    }

    /// <summary>One moving box of the mask: a fixed size, a window, and the top-left position over output time.</summary>
    internal sealed record MaskBox(int Width, int Height, double StartSec, double EndSec, IReadOnlyList<(double TimeSec, double X, double Y)> TopLeft);

    /// <summary>
    /// A region's mask boxes, in mask-canvas pixels: its keyframes cut into <see cref="ChunkSec"/>
    /// chunks, each box as large as the region's biggest extent in that chunk and centred on the
    /// region's bounding-box centre at every keyframe.
    /// </summary>
    internal static List<MaskBox> BoxesFor(ResolvedCensorRegion r, double sx, double sy)
    {
        double frameSec = r.FpsDen / (double)Math.Max(1, r.FpsNum);
        var frames = r.Keyframes
            .Select(k =>
            {
                double x0 = Math.Min(Math.Min(k.X0, k.X1), Math.Min(k.X2, k.X3)) * sx;
                double x1 = Math.Max(Math.Max(k.X0, k.X1), Math.Max(k.X2, k.X3)) * sx;
                double y0 = Math.Min(Math.Min(k.Y0, k.Y1), Math.Min(k.Y2, k.Y3)) * sy;
                double y1 = Math.Max(Math.Max(k.Y0, k.Y1), Math.Max(k.Y2, k.Y3)) * sy;
                return (T: r.OutputStartSec + k.FrameIndex * frameSec, X0: x0, Y0: y0, X1: x1, Y1: y1);
            })
            .OrderBy(f => f.T)
            .ToList();
        if (frames.Count == 0)
            return [];

        var boxes = new List<MaskBox>();
        for (double chunkStart = r.OutputStartSec; chunkStart < r.OutputEndSec - 1e-6; chunkStart += ChunkSec)
        {
            double chunkEnd = Math.Min(r.OutputEndSec, chunkStart + ChunkSec);

            // The keyframes inside the chunk plus the nearest one either side, so the box is sized
            // for the whole chunk and its path is defined right up to both edges.
            int first = Math.Max(0, frames.FindLastIndex(f => f.T <= chunkStart));
            int last = frames.FindIndex(f => f.T >= chunkEnd);
            if (last < 0)
                last = frames.Count - 1;
            List<(double T, double X0, double Y0, double X1, double Y1)> span = frames.GetRange(first, last - first + 1);

            // Margin from the LONGER side, on every side: a licence plate is ~4x wider than tall,
            // and a margin proportional to its own height adds only a few pixels where a seed box
            // a little off vertically needs the most cover (measured on a real clip: a plate box
            // 16 px too high left the bottom row of characters readable).
            double margin = r.Padding * span.Max(f => Math.Max(f.X1 - f.X0, f.Y1 - f.Y0));
            // +2 mask pixels each way: one for rounding the box's position to whole mask pixels and
            // one for the bilinear upscale's feathering, so the edge is fully opaque. (The mask is
            // also kept 4:4:4 — on a subsampled format overlay rounds x/y down to even pixels, which
            // left ~6 output px of a plate's right/bottom edge partly uncovered at small padding.)
            int bw = Math.Max(2, (int)Math.Ceiling(span.Max(f => f.X1 - f.X0) + 2 * margin) + 4);
            int bh = Math.Max(2, (int)Math.Ceiling(span.Max(f => f.Y1 - f.Y0) + 2 * margin) + 4);
            boxes.Add(new MaskBox(bw, bh, chunkStart, chunkEnd,
                span.Select(f => (f.T, (f.X0 + f.X1) / 2 - bw / 2.0, (f.Y0 + f.Y1) / 2 - bh / 2.0)).ToList()));
        }

        return boxes;
    }

    /// <param name="totalDurationSec">Length of the stream being censored — the black mask canvas must cover all of it.</param>
    public static string BuildFilterChain(
        string inputLabel,
        IReadOnlyList<ResolvedCensorRegion> regions,
        int frameWidth,
        int frameHeight,
        CensorStyle style,
        string finalLabel,
        double totalDurationSec)
    {
        if (regions.Count == 0)
            throw new ArgumentException("BuildFilterChain requires at least one region.", nameof(regions));

        int maskW = Math.Max(16, frameWidth / MaskDownscale), maskH = Math.Max(16, frameHeight / MaskDownscale);
        double sx = maskW / (double)frameWidth, sy = maskH / (double)frameHeight;
        ResolvedCensorRegion firstRegion = regions[0];
        string fps = $"{FfmpegArgvFormat.Number(firstRegion.FpsNum)}/{FfmpegArgvFormat.Number(firstRegion.FpsDen)}";

        var segments = new List<string>
        {
            $"{inputLabel}split=2[cnb][cnc]",
            $"[cnc]{EffectFilter(style, frameWidth, frameHeight)},format=yuv444p[cno]",
            $"color=c=black:size={FfmpegArgvFormat.Number(maskW)}x{FfmpegArgvFormat.Number(maskH)}:rate={fps}:" +
            $"duration={FfmpegArgvFormat.Number(Math.Max(0.1, totalDurationSec) + 1.0)},format=yuv444p[cnm0]"
        };

        int n = 0;
        foreach (ResolvedCensorRegion region in regions)
        {
            foreach (MaskBox box in BoxesFor(region, sx, sy))
            {
                string x = ScreenInsertFilterBuilder.BuildBalancedPiecewiseExpr(box.TopLeft.Select(p => (p.TimeSec, p.X)).ToList(), "t");
                string y = ScreenInsertFilterBuilder.BuildBalancedPiecewiseExpr(box.TopLeft.Select(p => (p.TimeSec, p.Y)).ToList(), "t");
                segments.Add($"color=c=white:size={FfmpegArgvFormat.Number(box.Width)}x{FfmpegArgvFormat.Number(box.Height)}:rate={fps}:duration=0.1,format=yuv444p[cnw{n}]");
                segments.Add(
                    $"[cnm{n}][cnw{n}]overlay=x='{x}':y='{y}':eval=frame:eof_action=repeat:" +
                    $"enable='between(t,{FfmpegArgvFormat.Number(box.StartSec)},{FfmpegArgvFormat.Number(box.EndSec)})'[cnm{n + 1}]");
                n++;
            }
        }

        segments.Add(
            $"[cnm{n}]scale={FfmpegArgvFormat.Number(frameWidth)}:{FfmpegArgvFormat.Number(frameHeight)}:flags=bilinear,format=gray[cnmask]");
        segments.Add("[cno][cnmask]alphamerge[cna]");
        segments.Add($"[cnb][cna]overlay=x=0:y=0:shortest=1{finalLabel}");
        return string.Join(";", segments);
    }
}
