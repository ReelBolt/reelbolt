#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The proposed bar for prompted region effects, in ONE place so T-009 and any later wave read the
/// same numbers instead of restating them. Every threshold below is transcribed from
/// <c>prompted-region-effects/plan.md</c> §7 "Fixtures and the bar"; nothing here is invented.
/// </summary>
public static class PromptedRegionBar
{
    /// <summary>Where each number comes from: plan.md §7, "The bar (measured on the synthetic family)".</summary>
    public const string Source = "prompted-region-effects/plan.md §7";

    public static class Thresholds
    {
        /// <summary>Isolation: mean |Δ| outside the ground-truth mask (1 px dilated), per frame — "≤ 1/255".</summary>
        public const double OutsideMeanMax = 1.0;

        /// <summary>Isolation: p99 |Δ| outside the ground-truth mask, per frame — "p99 ≤ 2/255".</summary>
        public const double OutsideP99Max = 2.0;

        /// <summary>Isolation: mean |Δ| inside the mask for Blur/Pixelate/Outline — "≥ 8/255".</summary>
        public const double InsideMeanMin = 8.0;

        /// <summary>Changed-pixel set versus the 1 px dilated mask — "IoU ≥ 0.99 per frame (p05)".</summary>
        public const double ChangedSetIoUMin = 0.99;

        /// <summary>Reframe check: effect centroid within 2 px of the ground-truth object centroid.</summary>
        public const double CentroidErrorPxMax = 2.0;

        /// <summary>Temporal stability: p95 relative error of the effect-region area per frame — "≤ 1%".</summary>
        public const double AreaErrorP95Max = 0.01;

        /// <summary>Occlusion: on frames whose ground truth is empty the two renders are identical — 0 changed pixels.</summary>
        public const int OccludedFrameChangedPixelsMax = 0;

        /// <summary>A pixel counts as changed at |Δ| ≥ 1/255 on any channel (the plan's unit is 0..255).</summary>
        public const int ChangedPixelDelta = 1;

        /// <summary>The plan excludes "1 px dilated" from the outside measurement; a feathered effect widens it.</summary>
        public const int MaskDilationPx = 1;
    }
}

/// <summary>One measured frame. Every difference is in 0..255 code values, every position in output pixels.</summary>
public sealed record PromptedRegionFrameMetrics(
    int Index,
    int MaskPixels,
    bool Occluded,
    double MaskCentroidX,
    double MaskCentroidY,
    double OutsideMean,
    double OutsideP99,
    double InsideMean,
    int ChangedPixels,
    double ChangedSetIoU,
    double ChangedCentroidX,
    double ChangedCentroidY,
    double CentroidErrorPx,
    double AreaErrorRel);

/// <summary>One bar criterion evaluated against this measurement: the number, its limit, and the verdict.</summary>
public sealed record PromptedRegionBarCheck(string Name, double Value, double Limit, string Comparison, bool Pass);

/// <summary>The aggregate half of the report — the numbers a verdict quotes.</summary>
public sealed record PromptedRegionBarAggregate(
    double OutsideMean,
    double OutsideP99,
    double InsideMean,
    double ChangedSetIoUP05,
    double ChangedSetIoUMin,
    double CentroidErrorMaxPx,
    double AreaErrorP95,
    int OccludedFrames,
    int OccludedChangedPixelsMax,
    int MaskedFrames,
    double MaskCoverageMean);

/// <summary>What <see cref="PromptedRegionBarHarness.Measure"/> produced; <see cref="ToJson"/> is the stable report.</summary>
public sealed record PromptedRegionBarReport(
    string OnPath,
    string OffPath,
    string MaskPath,
    int Width,
    int Height,
    int FpsNum,
    int FpsDen,
    int FrameCount,
    int ExcludeRingPx,
    IReadOnlyList<PromptedRegionFrameMetrics> Frames,
    PromptedRegionBarAggregate Aggregate,
    IReadOnlyList<PromptedRegionBarCheck> Checks)
{
    /// <summary>True only when every bar criterion passed.</summary>
    public bool Pass => Checks.All(c => c.Pass);

    /// <summary>The failed criteria, named — what a failing run has to explain.</summary>
    public IReadOnlyList<string> Failures => Checks.Where(c => !c.Pass).Select(c => c.Name).ToList();

    public string ToJson() => JsonSerializer.Serialize(this, PromptedRegionBarHarness.JsonOptions);
}

/// <summary>
/// The measuring stick for prompted region effects (T-002): given the same render twice — once with
/// the effect applied, once without — plus the mask that says where the effect belongs, it reports
/// the plan §7 numbers per frame and in aggregate, and evaluates the bar.
///
/// <para>
/// It is a difference instrument, so it says nothing about what either render looks like: it only
/// reads where they differ and compares that set with the mask. Both renders must be the same size
/// and frame count, and — for the isolation bar to mean anything — encoded losslessly: through a
/// delivery encode the codec's own ringing around the mask edge, not the effect, is what the
/// outside-mask number would measure. <see cref="PromptedRegionEffectRenders"/> renders the control
/// pairs that way.
/// </para>
/// <para>
/// Needs ffmpeg and ffprobe on PATH. Opt-in for the suite: <c>REELBOLT_PROMPTED_REGION_HARNESS=1</c>
/// (see <see cref="PromptedRegionHarnessFactAttribute"/>), so a normal <c>dotnet test</c> skips it.
/// </para>
/// </summary>
public static class PromptedRegionBarHarness
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Measures an effect-on/effect-off pair against a mask video.</summary>
    /// <param name="onPath">The render with the effect applied.</param>
    /// <param name="offPath">The same render without it.</param>
    /// <param name="maskPath">Grey mask at the renders' own size: 255 = the object, 0 = everything else.</param>
    /// <param name="sidecarPath">Optional fixture sidecar; when given, the mask is checked against its truth first.</param>
    /// <param name="excludeRingPx">
    /// How far outside the object the effect is allowed to reach — 1 px by the plan's wording, plus the
    /// effect's feather radius when it has one.
    /// </param>
    public static PromptedRegionBarReport Measure(
        string onPath, string offPath, string maskPath, string? sidecarPath = null,
        int excludeRingPx = PromptedRegionBar.Thresholds.MaskDilationPx)
    {
        var (width, height, fpsNum, fpsDen, _) = Probe(onPath);
        var (offWidth, offHeight, _, _, _) = Probe(offPath);
        var (maskWidth, maskHeight, _, _, _) = Probe(maskPath);
        if (offWidth != width || offHeight != height)
            throw new InvalidOperationException($"effect-off render is {offWidth}x{offHeight}, effect-on is {width}x{height}");
        if (maskWidth != width || maskHeight != height)
            throw new InvalidOperationException($"mask is {maskWidth}x{maskHeight}, renders are {width}x{height}");

        if (sidecarPath is not null)
            VerifyMaskMatchesSidecar(maskPath, PromptedRegionSidecar.Parse(sidecarPath), width, height);

        int pixels = width * height;
        var onFrame = new byte[pixels * 3];
        var offFrame = new byte[pixels * 3];
        var mask = new byte[pixels];
        var ringA = new byte[pixels];
        var ringB = new byte[pixels];
        var outsideHistogram = new int[256];
        var frames = new List<PromptedRegionFrameMetrics>();

        using var on = RawFrameStream.Open(onPath, "rgb24", pixels * 3);
        using var off = RawFrameStream.Open(offPath, "rgb24", pixels * 3);
        using var maskStream = RawFrameStream.Open(maskPath, "gray", pixels);
        while (true)
        {
            bool hasOn = on.TryReadFrame(onFrame);
            bool hasOff = off.TryReadFrame(offFrame);
            bool hasMask = maskStream.TryReadFrame(mask);
            if (!hasOn && !hasOff && !hasMask)
                break;
            if (!hasOn || !hasOff || !hasMask)
                throw new InvalidOperationException(
                    $"frame counts differ at frame {frames.Count}: on={hasOn}, off={hasOff}, mask={hasMask}");

            Dilate(mask, ringA, ringB, width, height, excludeRingPx);
            frames.Add(MeasureFrame(frames.Count, onFrame, offFrame, mask, ringA, outsideHistogram, width));
        }

        if (frames.Count == 0)
            throw new InvalidOperationException("no frames decoded from the effect-on render");

        PromptedRegionBarAggregate aggregate = Aggregate(frames, pixels);
        return new PromptedRegionBarReport(
            onPath, offPath, maskPath, width, height, fpsNum, fpsDen, frames.Count, excludeRingPx,
            frames, aggregate, Evaluate(aggregate));
    }

    /// <summary>
    /// Checks a mask video against the fixture sidecar's own per-frame truth (area, centroid, box).
    /// The harness measures against whatever mask it is handed; this is what proves that mask IS the
    /// ground truth rather than a second, independent guess.
    /// </summary>
    public static void VerifyMaskMatchesSidecar(string maskPath, PromptedRegionSidecar sidecar, int? width = null, int? height = null)
    {
        ArgumentNullException.ThrowIfNull(sidecar);
        int w = width ?? sidecar.Width, h = height ?? sidecar.Height;
        var frame = new byte[w * h];
        using var stream = RawFrameStream.Open(maskPath, "gray", w * h);
        int index = 0;
        while (stream.TryReadFrame(frame))
        {
            if (index >= sidecar.Frames.Count)
                throw new InvalidDataException($"{maskPath}: more frames than the sidecar's {sidecar.Frames.Count}");
            PromptedRegionFrameTruth truth = sidecar.Frames[index];
            (int area, double centroidX, double centroidY, int x0, int y0, int boxW, int boxH) = MaskStatistics(frame, w, h);
            if (area != truth.AreaPx)
                throw new InvalidDataException($"mask frame {index}: {area} pixels, sidecar says {truth.AreaPx}");
            if (Math.Abs(centroidX - truth.CentroidX) > 0.01 || Math.Abs(centroidY - truth.CentroidY) > 0.01)
                throw new InvalidDataException($"mask frame {index}: centroid is off the sidecar's");
            if (area > 0 && (x0 != truth.BoxX || y0 != truth.BoxY || boxW != truth.BoxW || boxH != truth.BoxH))
                throw new InvalidDataException(
                    $"mask frame {index}: box {x0},{y0},{boxW},{boxH} vs sidecar " +
                    $"{truth.BoxX},{truth.BoxY},{truth.BoxW},{truth.BoxH}");
            index++;
        }

        if (index != sidecar.Frames.Count)
            throw new InvalidDataException($"{maskPath}: {index} frames, sidecar says {sidecar.Frames.Count}");
    }

    /// <summary>
    /// Writes a labelled sheet a human can read: per sampled frame, the untouched render, the effected
    /// render, the mask and the difference (amplified ×8), stacked. Returns the path written.
    /// </summary>
    public static string WriteContactSheet(
        PromptedRegionBarReport report, string onPath, string offPath, string maskPath, string outputPath, int samples = 4)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (samples < 1)
            throw new ArgumentOutOfRangeException(nameof(samples), samples, "at least one sample");
        int width = report.Width, height = report.Height, pixels = width * height;
        int count = Math.Min(samples, report.FrameCount);
        int[] wanted = Enumerable.Range(0, count)
            .Select(i => (int)Math.Round(count == 1 ? 0 : i * (report.FrameCount - 1) / (double)(count - 1)))
            .ToArray();

        var before = new byte[wanted.Length][];
        var after = new byte[wanted.Length][];
        var masks = new byte[wanted.Length][];
        var onFrame = new byte[pixels * 3];
        var offFrame = new byte[pixels * 3];
        var maskFrame = new byte[pixels];
        int index = 0;
        using (RawFrameStream on = RawFrameStream.Open(onPath, "rgb24", pixels * 3))
        using (RawFrameStream off = RawFrameStream.Open(offPath, "rgb24", pixels * 3))
        using (RawFrameStream maskStream = RawFrameStream.Open(maskPath, "gray", pixels))
        {
            while (on.TryReadFrame(onFrame) && off.TryReadFrame(offFrame) && maskStream.TryReadFrame(maskFrame))
            {
                int slot = Array.IndexOf(wanted, index);
                if (slot >= 0)
                {
                    // The sheet reads left to right for a human: untouched, then effected.
                    before[slot] = (byte[])offFrame.Clone();
                    after[slot] = (byte[])onFrame.Clone();
                    masks[slot] = (byte[])maskFrame.Clone();
                }

                index++;
            }
        }

        const int gap = 2;
        int sheetWidth = (4 * width) + (3 * gap);
        int sheetHeight = (count * height) + ((count - 1) * gap);
        var sheet = new byte[sheetWidth * sheetHeight * 3];
        for (int y = 0; y < sheetHeight; y++)
            for (int x = 0; x < sheetWidth; x++)
                SetPixel(sheet, sheetWidth, x, y, 60, 60, 60);

        for (int row = 0; row < count; row++)
        {
            if (before[row] is null || after[row] is null || masks[row] is null)
                throw new InvalidOperationException($"frame {wanted[row]} could not be read for the contact sheet");
            int top = row * (height + gap);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int source = (y * width) + x;
                    BlitRgb(sheet, sheetWidth, x, top + y, before[row], source);
                    BlitRgb(sheet, sheetWidth, width + gap + x, top + y, after[row], source);
                    int maskX = (2 * (width + gap)) + x;
                    byte value = masks[row][source];
                    SetPixel(sheet, sheetWidth, maskX, top + y, value, value, value);
                    int delta = Math.Abs(after[row][source * 3] - before[row][source * 3]);
                    delta = Math.Max(delta, Math.Abs(after[row][(source * 3) + 1] - before[row][(source * 3) + 1]));
                    delta = Math.Max(delta, Math.Abs(after[row][(source * 3) + 2] - before[row][(source * 3) + 2]));
                    byte amplified = (byte)Math.Min(255, delta * 8);
                    SetPixel(sheet, sheetWidth, (3 * (width + gap)) + x, top + y, amplified, amplified, amplified);
                }
            }
        }

        string labels = string.Join(",", new[] { "effect off", "effect on", "mask", "difference x8" }
            .Select((label, column) => string.Create(CultureInfo.InvariantCulture,
                $"drawtext=text='{label}':x={(column * (width + gap)) + 8}:y=8:fontsize=20:fontcolor=white:box=1:boxcolor=black@0.6")));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, RedirectStandardError = true };
        foreach (string a in new[]
                 {
                     "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24",
                     "-s", string.Create(CultureInfo.InvariantCulture, $"{sheetWidth}x{sheetHeight}"),
                     "-r", "1", "-i", "-", "-vf", labels, "-frames:v", "1", outputPath
                 })
            psi.ArgumentList.Add(a);
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.BaseStream.Write(sheet, 0, sheet.Length);
        process.StandardInput.BaseStream.Dispose();
        string error = stderr.GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg could not write the contact sheet ({process.ExitCode}): {error}");
        return outputPath;
    }

    private static PromptedRegionFrameMetrics MeasureFrame(
        int index, byte[] on, byte[] off, byte[] mask, byte[] ring, int[] outsideHistogram, int width)
    {
        Array.Clear(outsideHistogram, 0, outsideHistogram.Length);
        int pixels = mask.Length;
        long maskPixels = 0, insideCount = 0, outsideCount = 0, changed = 0, changedInMask = 0;
        double insideSum = 0, outsideSum = 0;
        long maskSumX = 0, maskSumY = 0, changedSumX = 0, changedSumY = 0;

        for (int i = 0; i < pixels; i++)
        {
            int channel = i * 3;
            int delta = Math.Abs(on[channel] - off[channel]);
            delta = Math.Max(delta, Math.Abs(on[channel + 1] - off[channel + 1]));
            delta = Math.Max(delta, Math.Abs(on[channel + 2] - off[channel + 2]));
            bool inMask = mask[i] != 0;
            if (inMask)
            {
                maskPixels++;
                insideCount++;
                insideSum += delta;
                maskSumX += i % width;
                maskSumY += i / width;
            }
            else if (ring[i] == 0)
            {
                outsideCount++;
                outsideSum += delta;
                outsideHistogram[Math.Min(delta, 255)]++;
            }

            if (delta < PromptedRegionBar.Thresholds.ChangedPixelDelta)
                continue;
            changed++;
            changedSumX += i % width;
            changedSumY += i / width;
            if (inMask)
                changedInMask++;
        }

        double maskCentroidX = maskPixels == 0 ? 0 : maskSumX / (double)maskPixels;
        double maskCentroidY = maskPixels == 0 ? 0 : maskSumY / (double)maskPixels;
        double changedCentroidX = changed == 0 ? 0 : changedSumX / (double)changed;
        double changedCentroidY = changed == 0 ? 0 : changedSumY / (double)changed;
        // The changed-pixel set is compared with the ground truth ITSELF — the dilation exists only to
        // keep the codec's own edge ring out of the outside-mask measurement (plan §7).
        long union = changed + maskPixels - changedInMask;

        return new PromptedRegionFrameMetrics(
            Index: index,
            MaskPixels: (int)maskPixels,
            Occluded: maskPixels == 0,
            MaskCentroidX: maskCentroidX,
            MaskCentroidY: maskCentroidY,
            OutsideMean: outsideCount == 0 ? 0 : outsideSum / outsideCount,
            OutsideP99: PercentileFromHistogram(outsideHistogram, outsideCount, 0.99),
            InsideMean: insideCount == 0 ? 0 : insideSum / insideCount,
            ChangedPixels: (int)changed,
            ChangedSetIoU: union == 0 ? 1.0 : changedInMask / (double)union,
            ChangedCentroidX: changedCentroidX,
            ChangedCentroidY: changedCentroidY,
            CentroidErrorPx: maskPixels == 0 || changed == 0
                ? 0
                : Math.Sqrt(Math.Pow(changedCentroidX - maskCentroidX, 2) + Math.Pow(changedCentroidY - maskCentroidY, 2)),
            AreaErrorRel: maskPixels == 0 ? 0 : Math.Abs(changed - maskPixels) / (double)maskPixels);
    }

    private static PromptedRegionBarAggregate Aggregate(IReadOnlyList<PromptedRegionFrameMetrics> frames, int pixels)
    {
        List<PromptedRegionFrameMetrics> masked = frames.Where(f => !f.Occluded).ToList();
        List<PromptedRegionFrameMetrics> occluded = frames.Where(f => f.Occluded).ToList();
        return new PromptedRegionBarAggregate(
            OutsideMean: frames.Average(f => f.OutsideMean),
            OutsideP99: frames.Max(f => f.OutsideP99),
            InsideMean: masked.Count == 0 ? 0 : masked.Average(f => f.InsideMean),
            ChangedSetIoUP05: Percentile(masked.Select(f => f.ChangedSetIoU).ToList(), 0.05),
            ChangedSetIoUMin: masked.Count == 0 ? 1.0 : masked.Min(f => f.ChangedSetIoU),
            CentroidErrorMaxPx: masked.Count == 0 ? 0 : masked.Max(f => f.CentroidErrorPx),
            AreaErrorP95: Percentile(masked.Select(f => f.AreaErrorRel).ToList(), 0.95),
            OccludedFrames: occluded.Count,
            OccludedChangedPixelsMax: occluded.Count == 0 ? 0 : occluded.Max(f => f.ChangedPixels),
            MaskedFrames: masked.Count,
            MaskCoverageMean: masked.Count == 0 ? 0 : masked.Average(f => f.MaskPixels) / pixels);
    }

    private static IReadOnlyList<PromptedRegionBarCheck> Evaluate(PromptedRegionBarAggregate aggregate) =>
    [
        new("isolation.outsideMean", aggregate.OutsideMean, PromptedRegionBar.Thresholds.OutsideMeanMax, "<=",
            aggregate.OutsideMean <= PromptedRegionBar.Thresholds.OutsideMeanMax),
        new("isolation.outsideP99", aggregate.OutsideP99, PromptedRegionBar.Thresholds.OutsideP99Max, "<=",
            aggregate.OutsideP99 <= PromptedRegionBar.Thresholds.OutsideP99Max),
        new("isolation.insideMean", aggregate.InsideMean, PromptedRegionBar.Thresholds.InsideMeanMin, ">=",
            aggregate.InsideMean >= PromptedRegionBar.Thresholds.InsideMeanMin),
        new("isolation.changedSetIoUP05", aggregate.ChangedSetIoUP05, PromptedRegionBar.Thresholds.ChangedSetIoUMin, ">=",
            aggregate.ChangedSetIoUP05 >= PromptedRegionBar.Thresholds.ChangedSetIoUMin),
        new("isolation.changedSetIoUMin", aggregate.ChangedSetIoUMin, PromptedRegionBar.Thresholds.ChangedSetIoUMin, ">=",
            aggregate.ChangedSetIoUMin >= PromptedRegionBar.Thresholds.ChangedSetIoUMin),
        new("occlusion.changedPixelsMax", aggregate.OccludedChangedPixelsMax,
            PromptedRegionBar.Thresholds.OccludedFrameChangedPixelsMax, "<=",
            aggregate.OccludedChangedPixelsMax <= PromptedRegionBar.Thresholds.OccludedFrameChangedPixelsMax),
        new("temporal.areaErrorP95", aggregate.AreaErrorP95, PromptedRegionBar.Thresholds.AreaErrorP95Max, "<=",
            aggregate.AreaErrorP95 <= PromptedRegionBar.Thresholds.AreaErrorP95Max),
        new("centroid.errorMaxPx", aggregate.CentroidErrorMaxPx, PromptedRegionBar.Thresholds.CentroidErrorPxMax, "<=",
            aggregate.CentroidErrorMaxPx <= PromptedRegionBar.Thresholds.CentroidErrorPxMax)
    ];

    private static (int Area, double CentroidX, double CentroidY, int X0, int Y0, int BoxW, int BoxH) MaskStatistics(
        byte[] mask, int width, int height)
    {
        long area = 0, sumX = 0, sumY = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (mask[(y * width) + x] == 0)
                    continue;
                area++;
                sumX += x;
                sumY += y;
                if (x < minX)
                    minX = x;
                if (y < minY)
                    minY = y;
                if (x > maxX)
                    maxX = x;
                if (y > maxY)
                    maxY = y;
            }
        }

        return area == 0
            ? (0, 0, 0, 0, 0, 0, 0)
            : ((int)area, sumX / (double)area, sumY / (double)area, minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Square-structuring-element dilation, repeated <paramref name="radius"/> times into <c>first</c>.</summary>
    private static void Dilate(byte[] mask, byte[] first, byte[] second, int width, int height, int radius)
    {
        if (radius <= 0)
        {
            Buffer.BlockCopy(mask, 0, first, 0, mask.Length);
            return;
        }

        byte[] source = mask, target = first;
        for (int pass = 0; pass < radius; pass++)
        {
            DilateOnce(source, target, width, height);
            source = target;
            target = ReferenceEquals(target, first) ? second : first;
        }

        if (!ReferenceEquals(source, first))
            Buffer.BlockCopy(source, 0, first, 0, mask.Length);
    }

    private static void DilateOnce(byte[] source, byte[] target, int width, int height)
    {
        Array.Clear(target, 0, width * height);
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (source[row + x] == 0)
                    continue;
                target[row + x] = 255;
                if (x > 0)
                    target[row + x - 1] = 255;
                if (x + 1 < width)
                    target[row + x + 1] = 255;
                if (y > 0)
                    target[row - width + x] = 255;
                if (y + 1 < height)
                    target[row + width + x] = 255;
            }
        }
    }

    private static double Percentile(List<double> values, double fraction)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[(int)Math.Floor(fraction * (sorted.Count - 1))];
    }

    private static double PercentileFromHistogram(int[] histogram, long count, double fraction)
    {
        if (count == 0)
            return 0;
        long wanted = (long)Math.Ceiling(fraction * count);
        long running = 0;
        for (int value = 0; value < histogram.Length; value++)
        {
            running += histogram[value];
            if (running >= wanted)
                return value;
        }

        return histogram.Length - 1;
    }

    private static void BlitRgb(byte[] sheet, int sheetWidth, int x, int y, byte[] sourceFrame, int source)
    {
        int destination = ((y * sheetWidth) + x) * 3;
        sheet[destination] = sourceFrame[source * 3];
        sheet[destination + 1] = sourceFrame[(source * 3) + 1];
        sheet[destination + 2] = sourceFrame[(source * 3) + 2];
    }

    private static void SetPixel(byte[] sheet, int sheetWidth, int x, int y, byte r, byte g, byte b)
    {
        int destination = ((y * sheetWidth) + x) * 3;
        sheet[destination] = r;
        sheet[destination + 1] = g;
        sheet[destination + 2] = b;
    }

    internal static (int Width, int Height, int FpsNum, int FpsDen, double Duration) Probe(string path)
    {
        var psi = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[]
                 {
                     "-v", "error", "-select_streams", "v:0", "-show_entries",
                     "stream=width,height,r_frame_rate:format=duration", "-of", "default=nw=1", path
                 })
            psi.ArgumentList.Add(a);
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("ffprobe did not start");
        string text = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe could not read {path}: {error}");
        Dictionary<string, string> values = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0])
            .ToDictionary(group => group.Key, group => group.First()[1].Trim());
        string[] rate = values["r_frame_rate"].Split('/');
        return (
            int.Parse(values["width"], CultureInfo.InvariantCulture),
            int.Parse(values["height"], CultureInfo.InvariantCulture),
            int.Parse(rate[0], CultureInfo.InvariantCulture),
            int.Parse(rate[1], CultureInfo.InvariantCulture),
            // A still image (the contact sheet) has no duration: 0, never a parse failure.
            double.TryParse(values.GetValueOrDefault("duration", "N/A"), NumberStyles.Float, CultureInfo.InvariantCulture, out double duration)
                ? duration
                : 0);
    }

    /// <summary>Frames read from an ffmpeg decode, one at a time, with no frame dropping or duplication.</summary>
    private sealed class RawFrameStream : IDisposable
    {
        private readonly Process _process;
        private readonly Stream _stdout;
        private readonly Task<string> _stderr;
        private readonly int _frameBytes;

        private RawFrameStream(Process process, int frameBytes)
        {
            _process = process;
            _frameBytes = frameBytes;
            _stdout = process.StandardOutput.BaseStream;
            _stderr = process.StandardError.ReadToEndAsync();
        }

        public static RawFrameStream Open(string path, string pixelFormat, int frameBytes)
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string a in new[]
                     {
                         "-v", "error", "-i", path, "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", pixelFormat, "-"
                     })
                psi.ArgumentList.Add(a);
            Process process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
            return new RawFrameStream(process, frameBytes);
        }

        public bool TryReadFrame(byte[] frame)
        {
            if (frame.Length < _frameBytes)
                throw new ArgumentException($"{frame.Length} bytes cannot hold a {_frameBytes}-byte frame", nameof(frame));
            int first = _stdout.ReadByte();
            if (first < 0)
                return false;
            frame[0] = (byte)first;
            _stdout.ReadExactly(frame, 1, _frameBytes - 1);
            return true;
        }

        public void Dispose()
        {
            _stdout.Dispose();
            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Exited between the check and the kill; nothing to stop.
                }
            }

            _process.WaitForExit();
            _ = _stderr.GetAwaiter().GetResult();
            _process.Dispose();
        }
    }
}

/// <summary>
/// Renders the control pairs the bar is calibrated on, with REAL ffmpeg: an effect applied through the
/// mask ("as it should be") and the same effect applied everywhere ("what failing looks like"). Both
/// go out as FFV1, i.e. losslessly, because <see cref="PromptedRegionBarHarness"/> measures a
/// difference and a delivery encode would contribute codec noise of its own.
/// </summary>
public static class PromptedRegionEffectRenders
{
    /// <summary>
    /// The filter graph for a masked blur — <paramref name="everywhere"/> forces the mask to white.
    ///
    /// <para>
    /// Both inputs are re-timed to the clip's exact frame cadence first: a mask stored in Matroska
    /// carries millisecond-rounded timestamps while the clip's are exact, and `maskedmerge` resolves
    /// that disagreement by repeating a frame roughly every third frame (measured: an FFV1 mask at
    /// 24 fps against an mp4 clip). Repeating a frame shifts the whole picture, which would look like
    /// an effect applied everywhere. `settb`/`setpts` on both inputs removes it (also measured).
    /// </para>
    /// </summary>
    public static string BlurGraph(double sigma, bool everywhere, int fpsNum, int fpsDen)
    {
        string timebase = string.Create(CultureInfo.InvariantCulture, $"{fpsDen}/{fpsNum}");
        string normalize = string.Create(CultureInfo.InvariantCulture, $"settb={timebase},setpts=N");
        string mask = everywhere ? $"{normalize},format=gray,lut=y=255" : $"{normalize},format=gray";
        return FormattableString.Invariant(
            $"[0:v]{normalize},format=gbrp,split[b][t];[t]gblur=sigma={sigma:0.###}[g];[1:v]{mask}[m];[b][g][m]maskedmerge[o]");
    }

    /// <summary>Renders <paramref name="graph"/> over (clip, mask) to a lossless FFV1 mkv.</summary>
    public static string RenderLossless(string clipPath, string maskPath, string outputPath, string graph, int frames)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(directory);
        string script = Path.Combine(directory, Path.GetFileNameWithoutExtension(outputPath) + ".graph.txt");
        File.WriteAllText(script, graph);

        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in new[]
                 {
                     "-v", "error", "-y", "-i", clipPath, "-i", maskPath,
                     "-filter_complex_script", script, "-map", "[o]",
                     "-frames:v", frames.ToString(CultureInfo.InvariantCulture),
                     "-c:v", "ffv1", "-level", "3", "-g", "1", "-pix_fmt", "bgr0", outputPath
                 })
            psi.ArgumentList.Add(a);
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg could not render {outputPath} ({process.ExitCode}): {error}");
        return outputPath;
    }
}

/// <summary>
/// Skips unless <c>REELBOLT_PROMPTED_REGION_HARNESS=1</c> and ffmpeg/ffprobe are on PATH — the bar
/// harness runs real renders and is opt-in, exactly like the insert tracking harness.
/// </summary>
public sealed class PromptedRegionHarnessFactAttribute : FactAttribute
{
    public const string EnableVariable = "REELBOLT_PROMPTED_REGION_HARNESS";

    public PromptedRegionHarnessFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
            Skip = $"Bar harness is opt-in: set {EnableVariable}=1 to run it.";
        else if (new FfmpegFactAttribute().Skip is { } why)
            Skip = why;
    }
}
