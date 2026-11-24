#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo;

/// <summary>The read-only inputs from the staging project and the geometry of the compile that produced the film.</summary>
internal sealed class PromoInputs
{
    public const int OutW = 1920;
    public const int OutH = 1080;
    public const double Fps = 24.0;

    public string Dir { get; }

    public string FilmPath => Path.Combine(Dir, "final-cold-open-ac30a89f.mp4");

    public string ProxyPath => Path.Combine(Dir, "insert-content-chat-window-proxy.mp4");

    /// <summary>Plate keyframes (<c>surfaceCorners</c>, the corners the compile pins to) in plate source px (1344x768), stamped at their own time.</summary>
    public IReadOnlyList<Quad> Surface { get; }

    /// <summary>The same keyframes' <c>x0..y3</c> (the tracker's own corners) in plate source px.</summary>
    public IReadOnlyList<Quad> Corners { get; }

    public int SrcW { get; }

    public int SrcH { get; }

    public VideoCompileStepExecutor.SourceCanvasFit Fit { get; }

    /// <summary>Output px per plate px (isotropic: the cover fit).</summary>
    public double Scale => Fit.Width / SrcW;

    public PromoInputs()
    {
        Dir = Environment.GetEnvironmentVariable("REELBOLT_JITTER_PROMO") is { Length: > 0 } p ? p : "/tmp/insert-tracking/promo";
        string artifact = Directory.GetFiles(Dir, "artifact-65fe46f0-*.json").FirstOrDefault()
            ?? throw new FileNotFoundException("artifact-65fe46f0-*.json not found in " + Dir);
        (List<Quad> surface, List<Quad> corners, int w, int h) = BenchSupport.LoadArtifact(artifact);
        Surface = surface;
        Corners = corners;
        SrcW = w;
        SrcH = h;
        var source = new VideoAnalysisMedia(15.104, 24, 1, w, h);
        var canvas = new VideoAnalysisMedia(15.125, 24, 1, OutW, OutH);
        Fit = VideoCompileStepExecutor.SourceCanvasFit.For(source, canvas, cover: true);
    }

    public string OutDir
    {
        get
        {
            string d = Environment.GetEnvironmentVariable("REELBOLT_PROMO_MEASURE_DIR") is { Length: > 0 } x ? x : "/tmp/insert-tracking/promo-measure";
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public string WorkDir
    {
        get
        {
            string d = Path.Combine(OutDir, "work");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>A plate-px quad mapped onto the 1920x1080 canvas through the compile's fit (no overscan).</summary>
    public Quad ToOut(Quad src)
    {
        double X(double x) => Fit.OffsetX + x / SrcW * Fit.Width;
        double Y(double y) => Fit.OffsetY + y / SrcH * Fit.Height;
        return new Quad(src.T, X(src.X0), Y(src.Y0), X(src.X1), Y(src.Y1), X(src.X2), Y(src.Y2), X(src.X3), Y(src.Y3));
    }

    /// <summary>The inverse of <see cref="ToOut"/>.</summary>
    public Quad ToSource(Quad outQ)
    {
        double X(double x) => (x - Fit.OffsetX) / Fit.Width * SrcW;
        double Y(double y) => (y - Fit.OffsetY) / Fit.Height * SrcH;
        return new Quad(outQ.T, X(outQ.X0), Y(outQ.Y0), X(outQ.X1), Y(outQ.Y1), X(outQ.X2), Y(outQ.Y2), X(outQ.X3), Y(outQ.Y3));
    }
}

/// <summary>Streaming luma decode of a clip through ffmpeg (luma plane only, no range or colour conversion).</summary>
internal static class PromoVideo
{
    /// <summary>
    /// Yields frame index and a luma field (values 0..255 as float, one per pixel). The buffer is REUSED between frames: consume it before
    /// asking for the next one.
    /// </summary>
    public static IEnumerable<(int Index, float[] Luma)> Luma(string path, int w, int h, int maxFrames = int.MaxValue, string? extraFilter = null)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-v", "error", "-nostdin", "-i", path, "-vf", (extraFilter is null ? string.Empty : extraFilter + ",") + "extractplanes=y", "-frames:v", maxFrames.ToString(CultureInfo.InvariantCulture), "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1" })
            psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        System.Threading.Tasks.Task<string> err = p.StandardError.ReadToEndAsync();
        var raw = new byte[w * h];
        var field = new float[w * h];
        int index = 0;
        while (ReadFull(p.StandardOutput.BaseStream, raw))
        {
            for (int i = 0; i < raw.Length; i++)
                field[i] = raw[i];
            yield return (index++, field);
        }

        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg luma decode failed: " + err.Result);
    }

    private static bool ReadFull(Stream s, byte[] buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf, got, buf.Length - got);
            if (n <= 0)
                return false;
            got += n;
        }

        return true;
    }

    public static void Run(IEnumerable<string> args, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        System.Threading.Tasks.Task<string> err = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg failed: " + err.Result);
    }
}
