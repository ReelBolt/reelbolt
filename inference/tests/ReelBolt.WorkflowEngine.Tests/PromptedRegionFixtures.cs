#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The synthetic "dashboard" fixture for prompted region effects (prompted-region-effects T-002,
/// plan.md §7 "Synthetic with exact ground truth"): a gauge that moves, scales and rotates over a
/// panning dashboard surface, a hand bar that sweeps across it (partial occlusion) and a stretch
/// where the reading hand closes over it (fully hidden, so the ground truth is empty).
///
/// <para>
/// The clip, the mask video and the sidecar all come from ONE formula, so the mask is exact rather
/// than traced: <see cref="Generate"/> paints each frame's pixels and decides in the same pass
/// whether that pixel is the object (mask 255), the occluder, or the background. Nothing is
/// re-derived from the encoded clip.
/// </para>
/// <para>
/// Real ffmpeg draws and encodes: x264 for the clip (<c>yuv420p</c>, 24 fps), FFV1 <c>gray8</c> in
/// Matroska for the mask, both written from raw frames streamed over stdin. Everything is generated
/// into a caller-supplied directory (a test temp dir), never into the repo.
/// </para>
/// </summary>
public static class PromptedRegionFixtures
{
    /// <summary>The name the sidecar gives the tracked object; the effect target in every test.</summary>
    public const string ObjectName = "gauge";

    /// <summary>First frame the reading hand covers the gauge completely (ground truth empty).</summary>
    public const int OcclusionStartFrame = 96;

    /// <summary>How long the gauge stays completely hidden — 12 frames = 0.5 s at 24 fps.</summary>
    public const int OcclusionFrames = 12;

    /// <summary>960x540, 6 s, 24 fps: the main fixture, and the one the bar is measured on.</summary>
    public static PromptedRegionFixtureOptions Dashboard { get; } =
        new("dashboard", Width: 960, Height: 540, FrameCount: 144, Fps: 24, Seed: 20261018);

    /// <summary>
    /// The same scene at 1280x720, for the 16:9 -> 9:16 reframe check: the object is small against a
    /// wide frame, so a crop that follows it has somewhere to go wrong.
    /// </summary>
    public static PromptedRegionFixtureOptions DashboardWide { get; } =
        new("dashboard-wide", Width: 1280, Height: 720, FrameCount: 144, Fps: 24, Seed: 20261018);

    public static PromptedRegionFixture GenerateDashboard(string directory) => Generate(directory, Dashboard);

    public static PromptedRegionFixture GenerateDashboardWide(string directory) => Generate(directory, DashboardWide);

    /// <summary>Paints one fixture (clip + mask + sidecar) into <paramref name="directory"/>.</summary>
    public static PromptedRegionFixture Generate(string directory, PromptedRegionFixtureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(directory);

        string clipName = options.Name + ".mp4";
        string maskName = options.Name + "-mask.mkv";
        string sidecarName = options.Name + ".json";
        string clipPath = Path.Combine(directory, clipName);
        string maskPath = Path.Combine(directory, maskName);
        string sidecarPath = Path.Combine(directory, sidecarName);

        int width = options.Width, height = options.Height;
        string rate = options.Fps.ToString(CultureInfo.InvariantCulture);
        string size = string.Create(CultureInfo.InvariantCulture, $"{width}x{height}");
        var rgb = new byte[width * height * 3];
        var mask = new byte[width * height];
        var truths = new List<PromptedRegionFrameTruth>(options.FrameCount);
        var emptyFrames = new List<int>();

        // The camera pan is quantised to whole pixels so the panning dashboard surface is painted ONCE
        // and copied per frame. The per-pixel version cost tens of seconds at 1280x720; the scene is
        // the same either way, because a pan is a translation and never a resample.
        int panTotalX = (int)Math.Round(0.18 * width), panTotalY = (int)Math.Round(0.05 * height);
        int backgroundWidth = width + panTotalX + 1;
        byte[] background = PaintBackground(backgroundWidth, height + panTotalY + 1);

        using var clipPipe = new RawVideoPipe(clipPath,
        [
            "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", size, "-r", rate, "-i", "-",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart"
        ]);
        using var maskPipe = new RawVideoPipe(maskPath,
        [
            "-f", "rawvideo", "-pix_fmt", "gray", "-s", size, "-r", rate, "-i", "-",
            "-c:v", "ffv1", "-level", "3", "-g", "1", "-pix_fmt", "gray"
        ]);

        for (int frame = 0; frame < options.FrameCount; frame++)
        {
            double u = options.FrameCount == 1 ? 0 : frame / (double)(options.FrameCount - 1);
            Gauge gauge = GaugeAt(u, width, height);
            int panX = (int)Math.Round(0.18 * width * u), panY = (int)Math.Round(0.05 * height * u);
            bool closing = frame >= OcclusionStartFrame && frame < OcclusionStartFrame + OcclusionFrames;

            for (int y = 0; y < height; y++)
                Array.Copy(background, (((y + panY) * backgroundWidth) + panX) * 3, rgb, y * width * 3, width * 3);
            Array.Clear(mask, 0, mask.Length);

            // The gauge: one sprite pass over the disc, which is the mask's whole extent.
            int discX0 = Math.Max(0, (int)Math.Floor(gauge.CenterX - gauge.Radius));
            int discX1 = Math.Min(width - 1, (int)Math.Ceiling(gauge.CenterX + gauge.Radius));
            int discY0 = Math.Max(0, (int)Math.Floor(gauge.CenterY - gauge.Radius));
            int discY1 = Math.Min(height - 1, (int)Math.Ceiling(gauge.CenterY + gauge.Radius));
            double radiusSquared = gauge.Radius * gauge.Radius;
            for (int y = discY0; y <= discY1; y++)
            {
                for (int x = discX0; x <= discX1; x++)
                {
                    double dx = x - gauge.CenterX, dy = y - gauge.CenterY;
                    if ((dx * dx) + (dy * dy) > radiusSquared)
                        continue;
                    (byte R, byte G, byte B) colour = GaugeColour(x, y, dx, dy, gauge);
                    int i = ((y * width) + x) * 3;
                    rgb[i] = colour.R;
                    rgb[i + 1] = colour.G;
                    rgb[i + 2] = colour.B;
                    mask[(y * width) + x] = 255;
                }
            }

            // Occluder 1: the hand bar sweeping across the gauge (partial occlusion)...
            if (u >= 0.08 && u <= 0.46)
            {
                double barLeft = (-0.12 + (1.24 * ((u - 0.08) / 0.38))) * width;
                PaintRectangle(rgb, mask, width,
                    Math.Max(0, (int)Math.Floor(barLeft)),
                    Math.Max(0, (int)Math.Floor(0.12 * height)),
                    Math.Min(width - 1, (int)Math.Ceiling(barLeft + (0.085 * width))),
                    Math.Min(height - 1, (int)Math.Ceiling(0.98 * height)),
                    HandBarColour);
            }

            // ...and occluder 2: the reading hand closing over it, which hides the gauge completely.
            if (closing)
                PaintDisc(rgb, mask, width, height, gauge.CenterX, gauge.CenterY, gauge.Radius * 1.65, ClosingHandColour);

            // The truth is whatever the mask ended up holding — one pass, so it cannot drift from the
            // pixels above.
            int area = 0;
            long sumX = 0, sumY = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (mask[row + x] == 0)
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

            // The occlusion contract is the fixture's own guarantee: a generator that stops hiding the
            // gauge (and every harness that leans on it) must fail here, not produce a quiet no-op.
            if ((area == 0) != closing)
                throw new InvalidOperationException(
                    $"frame {frame}: the gauge must be fully hidden exactly on frames {OcclusionStartFrame}.." +
                    $"{OcclusionStartFrame + OcclusionFrames - 1}, but its mask has {area} pixels (closing={closing}).");

            if (area == 0)
            {
                emptyFrames.Add(frame);
                truths.Add(new PromptedRegionFrameTruth(frame, 0, 0, 0, 0, 0, 0, 0, 0));
            }
            else
            {
                truths.Add(new PromptedRegionFrameTruth(
                    frame, area, area / (double)(width * height), sumX / (double)area, sumY / (double)area,
                    minX, minY, maxX - minX + 1, maxY - minY + 1));
            }

            clipPipe.Write(rgb);
            maskPipe.Write(mask);
        }

        var sidecar = new PromptedRegionSidecar
        {
            Generator = PromptedRegionSidecar.GeneratorName,
            Version = 1,
            Clip = clipName,
            Mask = maskName,
            ObjectName = ObjectName,
            Width = width,
            Height = height,
            Fps = options.Fps,
            FrameCount = options.FrameCount,
            DurationSec = options.FrameCount / (double)options.Fps,
            Seed = options.Seed,
            BoxConvention = "box = [x, y, w, h] in source pixels, corner-anchored (top-left) — OUR convention; " +
                "fal reports centre-anchored normalised boxes, see the real replay pack's PROVENANCE.md",
            OccludedFrames = emptyFrames,
            Frames = truths
        };
        File.WriteAllText(sidecarPath, sidecar.ToJson());

        return new PromptedRegionFixture(directory, clipPath, maskPath, sidecarPath, sidecar);
    }

    /// <summary>The gauge's geometry at clip progress <paramref name="u"/> in [0, 1].</summary>
    private static Gauge GaugeAt(double u, int width, int height) => new(
        CenterX: width * (0.30 + (0.24 * u) + (0.030 * Math.Sin(2 * Math.PI * u))),
        CenterY: height * (0.50 + (0.09 * Math.Sin((2 * Math.PI * u) + 0.4))),
        Radius: height * (0.125 + (0.030 * Math.Sin(2 * Math.PI * 1.5 * u))),
        Angle: (1.9 * Math.Sin(2 * Math.PI * u)) - 0.4);

    /// <summary>
    /// The gauge's own pixels: rim, ticks and needle on a textured face. The 2x2 grain is load-bearing —
    /// a flat face blurs to itself, so a blur's changed-pixel set would be the object's edges instead of
    /// the object, and the isolation IoU would measure the texture rather than the mask.
    /// </summary>
    private static (byte R, byte G, byte B) GaugeColour(int x, int y, double dx, double dy, Gauge gauge)
    {
        (byte R, byte G, byte B) baseColour = GaugeBaseColour(dx, dy, gauge);
        int grain = ((((x >> 1) + (y >> 1)) & 1) == 0 ? 24 : -24) + ((((x >> 3) + (y >> 3)) & 1) == 0 ? 7 : -7);
        return (
            Channel(baseColour.R + grain),
            Channel(baseColour.G + grain),
            Channel(baseColour.B + grain));
    }

    private static (byte R, byte G, byte B) GaugeBaseColour(double dx, double dy, Gauge gauge)
    {
        double distance = Math.Sqrt((dx * dx) + (dy * dy));
        if (distance > gauge.Radius * 0.86)
            return (200, 205, 210);

        // The needle: a tapered bar from the centre along the gauge's angle.
        double nx = Math.Cos(gauge.Angle), ny = Math.Sin(gauge.Angle);
        double along = (dx * nx) + (dy * ny), across = Math.Abs((-dx * ny) + (dy * nx));
        double reach = gauge.Radius * 0.80;
        if (along >= 0 && along <= reach && across <= (0.05 * gauge.Radius) + (0.02 * (reach - along)))
            return (235, 60, 48);

        // Twelve tick marks on the outer face band, rotating with the gauge.
        double bearing = Math.Atan2(dy, dx);
        if (distance > gauge.Radius * 0.70 && Math.Abs(Math.IEEERemainder(bearing - gauge.Angle, Math.PI / 6)) < 0.045)
            return (120, 200, 230);

        return (22, 26, 32);
    }

    /// <summary>The panning dashboard surface, painted once at the frames' size plus the pan's travel.</summary>
    private static byte[] PaintBackground(int width, int height)
    {
        var pattern = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte R, byte G, byte B) colour = DashboardColour(x, y, width, height);
                int i = ((y * width) + x) * 3;
                pattern[i] = colour.R;
                pattern[i + 1] = colour.G;
                pattern[i + 2] = colour.B;
            }
        }

        return pattern;
    }

    private static (byte R, byte G, byte B) DashboardColour(int x, int y, int width, int height)
    {
        double gu = x / (double)width, gv = y / (double)height;
        int checker = ((int)Math.Floor(gu * 16) + (int)Math.Floor(gv * 9)) & 1;
        double glow = 0.5 + (0.5 * Math.Sin(2 * Math.PI * (((gu * 0.8) + (gv * 0.6)) % 1.0)));
        return (
            Channel(24 + (20 * checker) + (26 * glow)),
            Channel(30 + (24 * checker) + (30 * glow)),
            Channel(42 + (32 * checker) + (36 * glow)));
    }

    /// <summary>Paints an occluder rectangle over the picture and clears the object's mask inside it.</summary>
    private static void PaintRectangle(
        byte[] rgb, byte[] mask, int width, int x0, int y0, int x1, int y1, (byte R, byte G, byte B) colour)
    {
        for (int y = y0; y <= y1; y++)
        {
            int row = y * width;
            for (int x = x0; x <= x1; x++)
                PaintOccluder(rgb, mask, row + x, colour);
        }
    }

    /// <summary>Paints an occluder disc (the reading hand) over the picture and clears the mask inside it.</summary>
    private static void PaintDisc(
        byte[] rgb, byte[] mask, int width, int height, double centreX, double centreY, double radius,
        (byte R, byte G, byte B) colour)
    {
        int x0 = Math.Max(0, (int)Math.Floor(centreX - radius));
        int x1 = Math.Min(width - 1, (int)Math.Ceiling(centreX + radius));
        int y0 = Math.Max(0, (int)Math.Floor(centreY - radius));
        int y1 = Math.Min(height - 1, (int)Math.Ceiling(centreY + radius));
        double radiusSquared = radius * radius;
        for (int y = y0; y <= y1; y++)
        {
            double dy = y - centreY;
            int row = y * width;
            for (int x = x0; x <= x1; x++)
            {
                double dx = x - centreX;
                if ((dx * dx) + (dy * dy) > radiusSquared)
                    continue;
                PaintOccluder(rgb, mask, row + x, colour);
            }
        }
    }

    private static void PaintOccluder(byte[] rgb, byte[] mask, int pixel, (byte R, byte G, byte B) colour)
    {
        int i = pixel * 3;
        rgb[i] = colour.R;
        rgb[i + 1] = colour.G;
        rgb[i + 2] = colour.B;
        mask[pixel] = 0;
    }

    private static byte Channel(double value) => (byte)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    private static readonly (byte R, byte G, byte B) HandBarColour = (206, 158, 122);
    private static readonly (byte R, byte G, byte B) ClosingHandColour = (168, 122, 92);

    private readonly record struct Gauge(double CenterX, double CenterY, double Radius, double Angle);

    /// <summary>Streams raw frames into one ffmpeg process; a non-zero exit is reported with its stderr.</summary>
    private sealed class RawVideoPipe : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stderr;
        private readonly Stream _stdin;

        public RawVideoPipe(string outputPath, IEnumerable<string> args)
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                RedirectStandardInput = true,
                RedirectStandardError = true
            };
            foreach (string a in new[] { "-v", "error", "-y" }.Concat(args).Append(outputPath))
                psi.ArgumentList.Add(a);
            _process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
            _stderr = _process.StandardError.ReadToEndAsync();
            _stdin = _process.StandardInput.BaseStream;
        }

        public void Write(byte[] frame) => _stdin.Write(frame, 0, frame.Length);

        public void Dispose()
        {
            _stdin.Dispose();
            string error = _stderr.GetAwaiter().GetResult();
            _process.WaitForExit();
            int exit = _process.ExitCode;
            _process.Dispose();
            if (exit != 0)
                throw new InvalidOperationException($"ffmpeg exited {exit}: {error}");
        }
    }
}

/// <summary>Shape and size of one generated fixture.</summary>
public sealed record PromptedRegionFixtureOptions(string Name, int Width, int Height, int FrameCount, int Fps, int Seed);

/// <summary>The generated fixture on disk, with the sidecar already parsed.</summary>
public sealed record PromptedRegionFixture(
    string Directory, string ClipPath, string MaskPath, string SidecarPath, PromptedRegionSidecar Sidecar);

/// <summary>One frame's ground truth, in source pixels.</summary>
public sealed record PromptedRegionFrameTruth(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("areaPx")] int AreaPx,
    [property: JsonPropertyName("areaFraction")] double AreaFraction,
    [property: JsonPropertyName("centroidX")] double CentroidX,
    [property: JsonPropertyName("centroidY")] double CentroidY,
    [property: JsonPropertyName("boxX")] int BoxX,
    [property: JsonPropertyName("boxY")] int BoxY,
    [property: JsonPropertyName("boxW")] int BoxW,
    [property: JsonPropertyName("boxH")] int BoxH);

/// <summary>
/// The per-frame ground truth beside the generated clip: area, centroid and box of the object, the
/// frames where it is completely hidden, and the generator seed. Read back with <see cref="Parse"/>.
/// </summary>
public sealed class PromptedRegionSidecar
{
    public const string GeneratorName = "PromptedRegionFixtures";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public string Generator { get; init; } = GeneratorName;

    public int Version { get; init; } = 1;

    public string Clip { get; init; } = string.Empty;

    public string Mask { get; init; } = string.Empty;

    public string ObjectName { get; init; } = PromptedRegionFixtures.ObjectName;

    public int Width { get; init; }

    public int Height { get; init; }

    public int Fps { get; init; }

    public int FrameCount { get; init; }

    public double DurationSec { get; init; }

    public int Seed { get; init; }

    /// <summary>How the frame boxes are anchored — stated in the file, never inferred by a reader.</summary>
    public string BoxConvention { get; init; } = string.Empty;

    public IReadOnlyList<int> OccludedFrames { get; init; } = [];

    public IReadOnlyList<PromptedRegionFrameTruth> Frames { get; init; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static PromptedRegionSidecar Parse(string path)
    {
        PromptedRegionSidecar? sidecar = JsonSerializer.Deserialize<PromptedRegionSidecar>(File.ReadAllText(path), Options);
        if (sidecar is null)
            throw new InvalidDataException($"{path}: not a fixture sidecar");
        if (sidecar.Frames.Count != sidecar.FrameCount)
            throw new InvalidDataException($"{path}: {sidecar.Frames.Count} frame records for a {sidecar.FrameCount}-frame clip");
        return sidecar;
    }
}

/// <summary>One decoded replay: frame count, empty frames, the stack digest and the box agreement.</summary>
public sealed record PromptedRegionMaskDecode(int FrameCount, int EmptyFrames, string DecodedMaskSha256, double MinInsideBoxFraction);

/// <summary>One run in the real replay pack, as recorded in <c>decode-digests.json</c>.</summary>
public sealed record PromptedRegionReplayFixture(
    string Name,
    string ResponsePath,
    string ClipPath,
    int Width,
    int Height,
    int FrameCount,
    int EmptyFrames,
    string DecodedMaskSha256);

/// <summary>
/// Reference decoder for fal's per-object video mask RLE — deliberately NOT the shipping decoder
/// (T-003 owns that). It exists so the real replay pack pins one documented layout: "start length
/// start length …" pairs, 0-based, over a row-major index of a <c>width</c>-wide frame, 255 = object.
/// Every run is validated against the frame before it is used, because the vendor output is data and
/// the layout above is inferred (fal documents neither the encoding nor the grid, plan.md §10 R2).
/// </summary>
public static class PromptedRegionRleReference
{
    /// <summary>Decodes one frame into <paramref name="destination"/> (width*height bytes, 0 or 255).</summary>
    public static void DecodeInto(string? rle, byte[] destination, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int pixels = width * height;
        if (destination.Length < pixels)
            throw new ArgumentException($"{destination.Length} bytes cannot hold a {width}x{height} frame", nameof(destination));
        Array.Clear(destination, 0, pixels);
        if (string.IsNullOrWhiteSpace(rle))
            return;

        string[] parts = rle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length % 2 != 0)
            throw new InvalidDataException($"RLE has {parts.Length} numbers: not 'start length' pairs");

        long previousEnd = 0;
        for (int i = 0; i < parts.Length; i += 2)
        {
            long start = Parse(parts[i]);
            long length = Parse(parts[i + 1]);
            if (length <= 0)
                throw new InvalidDataException($"RLE run at {start} has length {length}");
            if (start < 0)
                throw new InvalidDataException($"RLE run starts at {start}: a row-major index cannot be negative");
            long end = start + length;
            if (end > pixels)
                throw new InvalidDataException($"RLE run {start}+{length} leaves the {width}x{height} frame ({pixels} pixels)");
            if (start < previousEnd)
                throw new InvalidDataException($"RLE run at {start} overlaps the previous run (ends at {previousEnd})");
            destination.AsSpan((int)start, (int)length).Fill(255);
            previousEnd = end;
        }

        static long Parse(string value) =>
            long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed)
                ? parsed
                : throw new InvalidDataException($"RLE run '{value}' is not an integer");
    }

    /// <summary>
    /// Decodes every frame of a response and hashes the stack, in one pass so a 158-frame 768x1344
    /// mask never has to be held in memory: the SHA-256 is over the concatenated grey8 frames, which
    /// is what an FFV1 <c>gray8</c> mask video of the same size decodes to.
    /// </summary>
    public static PromptedRegionMaskDecode Decode(PromptedRegionReplayFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fixture.ResponsePath));
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("rle", out JsonElement frames) || frames.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{fixture.ResponsePath}: no 'rle' array");
        if (frames.GetArrayLength() != fixture.FrameCount)
            throw new InvalidDataException(
                $"{fixture.ResponsePath}: {frames.GetArrayLength()} RLE frames, expected {fixture.FrameCount}");
        if (!root.TryGetProperty("boxes", out JsonElement boxes) || boxes.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{fixture.ResponsePath}: no 'boxes' array");
        if (boxes.GetArrayLength() != fixture.FrameCount)
            throw new InvalidDataException(
                $"{fixture.ResponsePath}: {boxes.GetArrayLength()} boxes, expected {fixture.FrameCount}");

        int width = fixture.Width, height = fixture.Height;
        var mask = new byte[width * height];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int emptyFrames = 0;
        double minInsideBox = 1.0;
        int index = 0;

        foreach (JsonElement frame in frames.EnumerateArray())
        {
            PromptedRegionRleReference.DecodeInto(frame.GetString(), mask, width, height);
            hash.AppendData(mask, 0, mask.Length);

            // The vendor sends a null box for a frame it saw no object in — and the mask must be empty
            // there too, or the two halves of the response disagree about the same frame.
            JsonElement box = boxes[index];
            int frameIndex = index;
            index++;
            if (box.ValueKind != JsonValueKind.Array)
            {
                int stray = CountMask(mask);
                if (stray > 0)
                    throw new InvalidDataException(
                        $"{fixture.ResponsePath}: frame {frameIndex} has no box but a {stray}-pixel mask");
                emptyFrames++;
                continue;
            }

            // The vendor's box is [centre-x, centre-y, width, height], normalised — measured, not
            // documented (see the replay pack's PROVENANCE.md). Grown by 2 px it bounds the mask.
            (double centreX, double centreY, double boxWidth, double boxHeight) = ReadBox(box, fixture);
            double x0 = ((centreX - (boxWidth / 2)) * width) - 2, x1 = ((centreX + (boxWidth / 2)) * width) + 2;
            double y0 = ((centreY - (boxHeight / 2)) * height) - 2, y1 = ((centreY + (boxHeight / 2)) * height) + 2;

            int covered = 0, inside = 0;
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (mask[row + x] == 0)
                        continue;
                    covered++;
                    if (x >= x0 && x <= x1 && y >= y0 && y <= y1)
                        inside++;
                }
            }

            if (covered == 0)
                emptyFrames++;
            else
                minInsideBox = Math.Min(minInsideBox, inside / (double)covered);
        }

        return new PromptedRegionMaskDecode(
            fixture.FrameCount, emptyFrames, Convert.ToHexStringLower(hash.GetHashAndReset()), minInsideBox);
    }

    private static int CountMask(byte[] mask)
    {
        int count = 0;
        foreach (byte value in mask)
        {
            if (value != 0)
                count++;
        }

        return count;
    }

    private static (double CentreX, double CentreY, double Width, double Height) ReadBox(
        JsonElement box, PromptedRegionReplayFixture fixture)
    {
        if (box.ValueKind != JsonValueKind.Array || box.GetArrayLength() != 4)
            throw new InvalidDataException($"{fixture.ResponsePath}: box is not [centre-x, centre-y, width, height]");
        double[] values = box.EnumerateArray().Select(v => v.GetDouble()).ToArray();
        return (values[0], values[1], values[2], values[3]);
    }
}

/// <summary>Reads the real <c>fal-ai/sam-3-1/video-rle</c> replay pack out of the test output directory.</summary>
public static class PromptedRegionReplay
{
    public static string PackDirectory => Path.Combine(AppContext.BaseDirectory, "TestData", "prompted-region-effects", "sam31-rle");

    public static IReadOnlyList<PromptedRegionReplayFixture> ReadDigests()
    {
        string path = Path.Combine(PackDirectory, "decode-digests.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        int width = root.GetProperty("width").GetInt32(), height = root.GetProperty("height").GetInt32();
        var fixtures = new List<PromptedRegionReplayFixture>();
        foreach (JsonProperty entry in root.GetProperty("fixtures").EnumerateObject())
        {
            JsonElement value = entry.Value;
            fixtures.Add(new PromptedRegionReplayFixture(
                entry.Name,
                Path.Combine(PackDirectory, value.GetProperty("response").GetString()!),
                Path.Combine(PackDirectory, value.GetProperty("clip").GetString()!),
                width,
                height,
                value.GetProperty("frameCount").GetInt32(),
                value.GetProperty("emptyFrames").GetInt32(),
                value.GetProperty("decodedMaskSha256").GetString()!));
        }

        return fixtures;
    }
}
