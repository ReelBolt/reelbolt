#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.RenderPath;

/// <summary>
/// One change at a time against the production flat-path recipe (<c>ScreenInsertFilterBuilder.BuildFilterChain</c>, unmatted,
/// no mesh, no content keyframes). Every knob defaults to the production behaviour, and
/// <see cref="RenderRig.BuildGraph"/> with all defaults reproduces the production segments byte for byte (pinned by
/// <c>RenderPathTests.Rig_default_graph_is_the_production_chain</c>). Anything else is a TEST-ONLY prototype of a
/// possible change and is labelled so in the report.
/// </summary>
internal sealed record RigVariant(
    string Id,
    string Label,
    /// <summary><c>perspective</c> interpolation option: <c>linear</c> (the filter default, production) or <c>cubic</c>.</summary>
    string Interpolation = "linear",
    /// <summary><c>overlay</c> <c>format=</c> option; null = the filter default (yuv420), production.</summary>
    string? OverlayFormat = null,
    /// <summary>Pixel format piped to the meter (<c>rgb24</c> or <c>gray</c>); the graph ends in <c>format=</c> this. Production S6: rgb24 after a yuv420 overlay.</summary>
    string OutPixFmt = "rgb24",
    /// <summary>PROTOTYPE: pin the quad to the content's WHITE region instead of the 1 px bordered frame (removes the constant border inset).</summary>
    bool CompensateBorder = false,
    /// <summary>PROTOTYPE: scale content and mask to the largest quad over the insert (+1 px border) and pin that region, so <c>perspective</c> runs near 1:1 instead of minifying by the canvas/quad ratio.</summary>
    bool Prescale = false,
    /// <summary>PROTOTYPE: render the MASK branch at this integer multiple and area-average it down (content unchanged).</summary>
    int MaskSupersample = 1,
    /// <summary>Output the warped mask branch alone (gray) instead of the composite: what <c>perspective</c> does with no alphamerge/overlay.</summary>
    bool MaskOnly = false,
    /// <summary>PROTOTYPE: content is NOT given a black border and is smeared by <c>perspective</c>'s edge clamp (the mask alone decides the edge).</summary>
    bool ContentEdgeReplicate = false,
    /// <summary>Round-trip the composite through yuv420p (what the final H.264 encode sees) before it is converted for measurement.</summary>
    bool Via420 = false,
    /// <summary>
    /// Build the graph with the SHIPPED <c>ScreenInsertFilterBuilder.BuildFilterChain</c> (F3: prescaled, edge-smeared, cubic) instead of
    /// one of the prototype recipes. Every other knob is ignored except <see cref="OutPixFmt"/>. The default variant stays the pre-F3 recipe.
    /// </summary>
    bool Production = false,
    /// <summary>Applied to the finished <see cref="Production"/> graph; lets a bench try a one-token change to the shipped chain.</summary>
    Func<string, string>? Rewrite = null,
    /// <summary>Applied to the <see cref="ResolvedScreenInsert"/> a <see cref="Production"/> graph is built from (e.g. to give it a <c>Prescale</c> with explicit bounds, or content keyframes).</summary>
    Func<ResolvedScreenInsert, ResolvedScreenInsert>? Adjust = null);

internal sealed record RigInput(
    int W, int H, int FpsNum, int FpsDen, int Frames,
    /// <summary>What <c>perspective</c> is told (canvas px, per insert-branch frame). Before any prototype re-mapping.</summary>
    IReadOnlyList<InsertQuadFrame> Keyframes,
    /// <summary>Per output frame, the commanded quad (canvas px) the detectors seed from and that the error is scored against.</summary>
    IReadOnlyList<Quad> Commanded,
    /// <summary>lavfi colour of the inserted content, e.g. <c>0xFFFFFF</c>.</summary>
    string ContentLavfi,
    /// <summary>Encoded clip used as the base picture, or null for a flat colour.</summary>
    string? BaseClip,
    /// <summary>Flat base colour (lavfi) when <see cref="BaseClip"/> is null.</summary>
    string BaseLavfi = "0x404040",
    /// <summary>Output frame at which the insert starts (its own frame 0); <see cref="Commanded"/> is indexed from it.</summary>
    int StartFrame = 0);

internal sealed record RigResult(
    List<Quad> Meter, List<Quad> Prod, int Frames, int MeterFailed, int ProdFailed,
    double CpuMs, double WallMs, double PeakRssMb, string Graph);

internal static class RenderRig
{
    /// <summary>Inner-frame inset of the production recipe: the content is scaled to (W-2)x(H-2) and given a 1 px black border.</summary>
    public const int Border = 1;

    /// <summary>The lavfi source of the inserted content: a flat colour (<c>0xRRGGBB</c>) or a full lavfi spec such as <c>testsrc2=s=1344x768:r=24:d=3</c>.</summary>
    public static string ContentInput(RigInput i)
    {
        double fps = i.FpsNum / (double)i.FpsDen;
        return i.ContentLavfi.StartsWith("0x", StringComparison.Ordinal)
            ? string.Create(CultureInfo.InvariantCulture, $"color=c={i.ContentLavfi}:s={i.W}x{i.H}:r={i.FpsNum}/{i.FpsDen}:d={i.Frames / fps + 2}")
            : i.ContentLavfi;
    }

    /// <summary>Renders every frame of the graph and returns the raw frames (no measuring).</summary>
    public static List<byte[]> RenderRaw(RigInput i, RigVariant v, string pixFmt = "rgb24")
    {
        RigVariant raw = v with { OutPixFmt = pixFmt };
        string graph = BuildGraph(i, raw, out string pipe);
        string tmp = Path.Combine(Path.GetTempPath(), "insert-rig-raw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string script = Path.Combine(tmp, "graph.txt");
            File.WriteAllText(script, graph);
            double fps = i.FpsNum / (double)i.FpsDen;
            var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
            var args = new List<string> { "-v", "error", "-nostdin" };
            if (i.BaseClip is null)
                args.AddRange(["-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture, $"color=c={i.BaseLavfi}:s={i.W}x{i.H}:r={i.FpsNum}/{i.FpsDen}:d={i.Frames / fps + 2}")]);
            else
                args.AddRange(["-i", i.BaseClip]);
            args.AddRange(["-f", "lavfi", "-i", ContentInput(i), "-filter_complex_script", script, "-map", "[vout]", "-frames:v", i.Frames.ToString(CultureInfo.InvariantCulture), "-f", "rawvideo", "-pix_fmt", pipe, "pipe:1"]);
            foreach (string a in args)
                psi.ArgumentList.Add(a);
            using Process p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            int bpp = pipe == "gray" ? 1 : 3;
            var frames = new List<byte[]>();
            while (true)
            {
                var f = new byte[i.W * i.H * bpp];
                if (!ReadFull(p.StandardOutput.BaseStream, f))
                    break;
                frames.Add(f);
            }

            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidOperationException("rig raw ffmpeg failed: " + err.Result + "\n" + graph);
            return frames;
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    public static string Fps(RigInput i) => $"{i.FpsNum}/{i.FpsDen}";

    /// <summary>The (cw, ch) of the pre-scaled content and its pad offset, or the production values when <paramref name="prescale"/> is false.</summary>
    public static (int Cw, int Ch, int Ox, int Oy) ContentRegion(RigInput i, bool prescale)
    {
        if (!prescale)
            return (i.W - 2 * Border, i.H - 2 * Border, Border, Border);
        double maxW = 0, maxH = 0;
        foreach (InsertQuadFrame k in i.Keyframes)
        {
            double wq = (Dist(k.X0, k.Y0, k.X1, k.Y1) + Dist(k.X2, k.Y2, k.X3, k.Y3)) / 2;
            double hq = (Dist(k.X0, k.Y0, k.X2, k.Y2) + Dist(k.X1, k.Y1, k.X3, k.Y3)) / 2;
            maxW = Math.Max(maxW, wq);
            maxH = Math.Max(maxH, hq);
        }

        int cw = Math.Clamp((int)Math.Ceiling(maxW) + 1 & ~1, 8, i.W - 2 * Border);
        int ch = Math.Clamp((int)Math.Ceiling(maxH) + 1 & ~1, 8, i.H - 2 * Border);
        int ox = (i.W - (cw + 2 * Border)) / 2 + Border;
        int oy = (i.H - (ch + 2 * Border)) / 2 + Border;
        return (cw, ch, ox, oy);
    }

    /// <summary>
    /// The field the integrating meter reads: LINEAR in the pixel value (a thresholded field such as S6's
    /// <c>luma above 170</c> turns every pixel under 2/3 coverage into nothing, which is a staircase of the detector's own making).
    /// White: luma; anything else: the content's own field (the chroma dominance is linear in a blended RGB).
    /// </summary>
    public static Func<byte, byte, byte, float> LinearField(RenderStage.InsertContent content) =>
        content.Label == "white" ? (r, g, b) => (float)(0.2126 * r + 0.7152 * g + 0.0722 * b) : content.Field;

    private static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    /// <summary>
    /// The keyframes <c>perspective</c> receives for a variant: the commanded quad extrapolated, through its own projective
    /// map (exactly, as <c>ScreenInsertMeshFilterBuilder.ExtrapolateToCanvas</c> does for a mesh cell), out to the frame corners
    /// of whatever region of the frame the quad is meant to land the content on.
    /// </summary>
    public static IReadOnlyList<InsertQuadFrame> Remap(RigInput i, RigVariant v)
    {
        if (!v.CompensateBorder && !v.Prescale)
            return i.Keyframes;
        (int cw, int ch, int ox, int oy) = ContentRegion(i, v.Prescale);
        // Region of the frame that the quad pins: the white area (compensated) or the white area + its border (not).
        double inset = v.CompensateBorder ? 0 : Border;
        double rx0 = ox - inset, rx1 = ox + cw + inset, ry0 = oy - inset, ry1 = oy + ch + inset;
        if (!v.CompensateBorder && !v.Prescale)
            return i.Keyframes;
        var result = new List<InsertQuadFrame>(i.Keyframes.Count);
        foreach (InsertQuadFrame k in i.Keyframes)
        {
            var q = new Quad(0, k.X0, k.Y0, k.X1, k.Y1, k.X2, k.Y2, k.X3, k.Y3);
            UnitSquareMap m = q.ToMap();
            (double X, double Y) P(double fx, double fy) => m.Map((fx - rx0) / (rx1 - rx0), (fy - ry0) / (ry1 - ry0));
            (double X, double Y) tl = P(0, 0), tr = P(i.W, 0), bl = P(0, i.H), br = P(i.W, i.H);
            result.Add(new InsertQuadFrame(k.FrameIndex, tl.X, tl.Y, tr.X, tr.Y, bl.X, bl.Y, br.X, br.Y));
        }

        return result;
    }

    /// <summary>The filtergraph for one variant. Input 0 is the base picture, input 1 the content colour.</summary>
    public static string BuildGraph(RigInput i, RigVariant v, out string pipePixFmt)
    {
        pipePixFmt = v.MaskOnly ? "gray" : v.OutPixFmt;
        if (v.Production)
        {
            double endProd = (i.StartFrame + i.Commanded.Count) * i.FpsDen / (double)i.FpsNum;
            double startProd = i.StartFrame * i.FpsDen / (double)i.FpsNum;
            var insert = new ResolvedScreenInsert("r0", "x", startProd, endProd, i.Keyframes, i.FpsNum, i.FpsDen);
            if (v.Adjust is not null)
                insert = v.Adjust(insert);
            string chain = ScreenInsertFilterBuilder.BuildFilterChain("[b0]", [insert], i.W, i.H, _ => 1, "[vc]");
            string graphProd = $"[0:v]null[b0];{chain};[vc]format={v.OutPixFmt}[vout]";
            return v.Rewrite is null ? graphProd : v.Rewrite(graphProd);
        }

        string fps = Fps(i);
        (int cw, int ch, int ox, int oy) = ContentRegion(i, v.Prescale);
        IReadOnlyList<InsertQuadFrame> keys = Remap(i, v);
        string persp = ScreenInsertFilterBuilder.BuildPerspectiveFilter(keys);
        if (v.Interpolation != "linear")
            persp += ":interpolation=" + v.Interpolation;
        double startSec = i.StartFrame * i.FpsDen / (double)i.FpsNum;
        double endSec = (i.StartFrame + i.Commanded.Count) * i.FpsDen / (double)i.FpsNum;
        string delay = $"setpts=PTS-STARTPTS+{FfmpegArgvFormat.Number(startSec)}/TB";
        double maskDuration = Math.Max(0.1, endSec - startSec) + 1.0;
        string dur = maskDuration.ToString(CultureInfo.InvariantCulture);

        string padContent;
        string contentSize;
        if (v.ContentEdgeReplicate && v.Prescale)
        {
            // Content at its prescaled size, edge pixels smeared outward over the rest of the frame.
            contentSize = $"scale={cw}:{ch}:flags=bilinear";
            padContent = $",pad={i.W}:{i.H}:{ox}:{oy}:black,fillborders=left={ox}:right={i.W - ox - cw}:top={oy}:bottom={i.H - oy - ch}:mode=smear";
        }
        else if (v.ContentEdgeReplicate)
        {
            // Whole frame is content; perspective's own edge clamp smears it past the quad.
            contentSize = $"scale={i.W}:{i.H}:flags=bilinear";
            padContent = string.Empty;
        }
        else
        {
            contentSize = $"scale={cw}:{ch}:flags=bilinear";
            padContent = $",pad={i.W}:{i.H}:{ox}:{oy}:black";
        }

        var segs = new List<string>();
        if (!v.MaskOnly)
            segs.Add($"[1:v]{contentSize},fps={fps},format=yuv444p{padContent},{persp},{delay}[sic0]");

        if (v.MaskSupersample > 1)
        {
            int s = v.MaskSupersample;
            string perspSs = ScreenInsertFilterBuilder.BuildPerspectiveFilter(
                keys.Select(k => new InsertQuadFrame(k.FrameIndex, k.X0 * s, k.Y0 * s, k.X1 * s, k.Y1 * s, k.X2 * s, k.Y2 * s, k.X3 * s, k.Y3 * s)).ToList());
            if (v.Interpolation != "linear")
                perspSs += ":interpolation=" + v.Interpolation;
            segs.Add(
                $"color=c=white:size={cw * s}x{ch * s}:rate={fps}:duration={dur},format=gray,pad={i.W * s}:{i.H * s}:{ox * s}:{oy * s}:black," +
                $"{perspSs},scale={i.W}:{i.H}:flags=area,{delay}[sim0]");
        }
        else
        {
            segs.Add($"color=c=white:size={cw}x{ch}:rate={fps}:duration={dur},format=gray,pad={i.W}:{i.H}:{ox}:{oy}:black,{persp},{delay}[sim0]");
        }

        string graph;
        if (v.MaskOnly)
        {
            graph = $"{string.Join(";", segs)};[sim0]format=gray[vout]";
            return graph;
        }

        segs.Add("[sic0][sim0]alphamerge[sia0]");
        string ovFmt = v.OverlayFormat is null ? string.Empty : $"format={v.OverlayFormat}:";
        segs.Add($"[b0][sia0]overlay=x=0:y=0:{ovFmt}eof_action=pass:enable='between(t,{FfmpegArgvFormat.Number(startSec)},{FfmpegArgvFormat.Number(endSec)})'[vc]");
        graph = $"[0:v]null[b0];{string.Join(";", segs)};[vc]format={(v.Via420 ? "yuv420p," + "format=" : string.Empty)}{v.OutPixFmt}[vout]";
        return graph;
    }

    /// <summary>
    /// Runs the graph and measures the composited quad in every frame with the integrating meter (<see cref="EdgeMeter"/>)
    /// and the production edge fitter (<see cref="ChromaEdgeLineFitter"/>, as S6 used it).
    /// </summary>
    public static RigResult Run(RigInput i, RigVariant v, RenderStage.InsertContent content, bool runProd = true, bool runMeter = true, Func<byte, byte, byte, float>? meterField = null)
    {
        meterField ??= LinearField(content);
        string graph = BuildGraph(i, v, out string pipe);
        string tmp = Path.Combine(Path.GetTempPath(), "insert-rig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string script = Path.Combine(tmp, "graph.txt");
            File.WriteAllText(script, graph);
            double fps = i.FpsNum / (double)i.FpsDen;
            string baseInput = i.BaseClip is null
                ? string.Create(CultureInfo.InvariantCulture, $"color=c={i.BaseLavfi}:s={i.W}x{i.H}:r={i.FpsNum}/{i.FpsDen}:d={i.Frames / fps + 2}")
                : i.BaseClip;
            var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
            var args = new List<string> { "-v", "error", "-nostdin" };
            if (i.BaseClip is null)
            {
                args.AddRange(["-f", "lavfi", "-i", baseInput]);
            }
            else
            {
                args.AddRange(["-i", baseInput]);
            }

            args.AddRange(
            [
                "-f", "lavfi", "-i", ContentInput(i),
                "-filter_complex_script", script, "-map", "[vout]", "-frames:v", i.Frames.ToString(CultureInfo.InvariantCulture),
                "-f", "rawvideo", "-pix_fmt", pipe, "pipe:1"
            ]);
            foreach (string a in args)
                psi.ArgumentList.Add(a);

            var sw = Stopwatch.StartNew();
            using Process p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            int bpp = pipe == "gray" ? 1 : 3;
            var frame = new byte[i.W * i.H * bpp];
            var dom = new float[i.W * i.H];
            var lin = new float[i.W * i.H];
            var meter = new List<Quad>();
            var prod = new List<Quad>();
            int meterFail = 0, prodFail = 0, n = 0, seen = 0;
            Stream stdout = p.StandardOutput.BaseStream;
            double peakKb = 0;
            int g = -1;
            while (g + 1 < i.Frames && ReadFull(stdout, frame))
            {
                g++;
                n = g - i.StartFrame;
                if (n < 0 || n >= i.Commanded.Count)
                    continue;
                for (int q = 0, o = 0; q < dom.Length; q++, o += bpp)
                {
                    byte r = frame[o], gg = bpp == 1 ? r : frame[o + 1], b = bpp == 1 ? r : frame[o + 2];
                    if (runProd)
                        dom[q] = content.Field(r, gg, b);
                    if (runMeter)
                        lin[q] = meterField(r, gg, b);
                }

                Quad seed = i.Commanded[n];
                double t = g / fps;
                if (runMeter)
                {
                    Quad? m = EdgeMeter.Measure(lin, i.W, i.H, seed);
                    if (m is null)
                        meterFail++;
                    else
                        meter.Add(m.Value with { T = t });
                }

                if (runProd)
                {
                    (double X, double Y) S((double X, double Y) c) => (c.X - 0.5, c.Y - 0.5);
                    ChromaEdgeLineFitter.Result? fit = ChromaEdgeLineFitter.Fit(
                        dom, i.W, i.H, content.Level, S(seed.Corner(0)), S(seed.Corner(1)), S(seed.Corner(3)), S(seed.Corner(2)));
                    if (fit is null)
                    {
                        prodFail++;
                    }
                    else
                    {
                        (double X, double Y)[] c4 = fit.Value.Corners;
                        (double X, double Y) C((double X, double Y) c) => (c.X + 0.5, c.Y + 0.5);
                        prod.Add(Quad.From(t, C(c4[0]), C(c4[1]), C(c4[3]), C(c4[2])));
                    }
                }

                if (g == i.Frames - 2)
                    peakKb = Math.Max(peakKb, PeakKb(p.Id));
                seen++;
            }

            peakKb = Math.Max(peakKb, PeakKb(p.Id));
            p.WaitForExit();
            sw.Stop();
            if (p.ExitCode != 0)
                throw new InvalidOperationException("rig ffmpeg failed: " + err.Result + "\n" + graph);
            double cpu = 0;
            try
            {
                cpu = (p.TotalProcessorTime).TotalMilliseconds;
            }
            catch (InvalidOperationException)
            {
            }

            return new RigResult(meter, prod, seen, meterFail, prodFail, cpu, sw.Elapsed.TotalMilliseconds, peakKb / 1024.0, graph);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    /// <summary>
    /// CPU cost of the graph alone: runs it to <c>-f null</c> in yuv420p (the production output format) and returns the
    /// ffmpeg child's CPU milliseconds and peak RSS. Wall time is meaningless on a loaded box; CPU time is not.
    /// </summary>
    public static (double CpuMs, double PeakRssMb) Cost(RigInput i, RigVariant v)
    {
        RigVariant plain = v with { MaskOnly = false, OutPixFmt = "yuv420p", Via420 = false };
        string graph = BuildGraph(i, plain, out _);
        string tmp = Path.Combine(Path.GetTempPath(), "insert-rig-cost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string script = Path.Combine(tmp, "graph.txt");
            File.WriteAllText(script, graph);
            double fps = i.FpsNum / (double)i.FpsDen;
            var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
            var args = new List<string> { "-v", "info", "-nostdin", "-benchmark" };
            if (i.BaseClip is null)
                args.AddRange(["-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture, $"color=c={i.BaseLavfi}:s={i.W}x{i.H}:r={i.FpsNum}/{i.FpsDen}:d={i.Frames / fps + 2}")]);
            else
                args.AddRange(["-i", i.BaseClip]);
            args.AddRange(
            [
                "-f", "lavfi", "-i", ContentInput(i),
                "-filter_complex_script", script, "-map", "[vout]", "-frames:v", i.Frames.ToString(CultureInfo.InvariantCulture), "-f", "null", "-"
            ]);
            foreach (string a in args)
                psi.ArgumentList.Add(a);
            using Process p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();
            p.WaitForExit();
            string log = err.Result;
            if (p.ExitCode != 0)
                throw new InvalidOperationException("cost ffmpeg failed: " + log);
            // ffmpeg -benchmark: "bench: utime=12.3s stime=1.2s rtime=3.4s" and "bench: maxrss=123456KiB".
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(log, @"bench: utime=([\d.]+)s stime=([\d.]+)s");
            System.Text.RegularExpressions.Match r = System.Text.RegularExpressions.Regex.Match(log, @"bench: maxrss=(\d+)");
            if (!m.Success)
                throw new InvalidOperationException("no bench line in ffmpeg output: " + log);
            double cpuMs = (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) * 1000;
            double rssMb = r.Success ? double.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture) / 1024.0 : 0;
            return (cpuMs, rssMb);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    private static double PeakKb(int pid)
    {
        try
        {
            foreach (string line in File.ReadLines($"/proc/{pid}/status"))
            {
                if (line.StartsWith("VmHWM:", StringComparison.Ordinal))
                    return double.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
            }
        }
        catch (IOException)
        {
        }

        return 0;
    }

    private static bool ReadFull(Stream s, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer, read, buffer.Length - read);
            if (n <= 0)
                return false;
            read += n;
        }

        return true;
    }
}
