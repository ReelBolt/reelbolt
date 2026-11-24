#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.EdgeSignal;

/// <summary>
/// One way of turning the clip into the tracker's RGB grid. <see cref="Filter"/> is the ffmpeg <c>-vf</c> chain with
/// <c>{fps}</c>, <c>{w}</c>, <c>{h}</c> placeholders. The production chain is the <c>base</c> variant
/// (<c>FfmpegArgvBuilder.BuildGridSampleArgs</c>: <c>fps,scale=area,format=rgb24</c>).
/// </summary>
internal sealed record DecodeVariant(string Id, string Label, string Filter)
{
    public string FilterFor(double fps, int w, int h) =>
        Filter.Replace("{fps}", fps.ToString("0.######", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{w}", w.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{h}", h.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
}

internal sealed record DecodeResult(byte[] Grid, double CpuSeconds, double WallSeconds);

internal static class EdgeDecode
{
    public const string BaseId = "base";

    public static readonly IReadOnlyList<DecodeVariant> All =
    [
        new(BaseId, "production: fps, scale area, rgb24", "fps={fps},scale={w}:{h}:flags=area,format=rgb24"),
        new("e1a", "E1a: area + accurate_rnd + full_chroma_int + full_chroma_inp",
            "fps={fps},scale={w}:{h}:flags=area+accurate_rnd+full_chroma_int+full_chroma_inp,format=rgb24"),
        new("e1a-int", "ablation: area + full_chroma_int only",
            "fps={fps},scale={w}:{h}:flags=area+full_chroma_int,format=rgb24"),
        new("e1a-intinp", "ablation: area + full_chroma_int + full_chroma_inp (no accurate_rnd)",
            "fps={fps},scale={w}:{h}:flags=area+full_chroma_int+full_chroma_inp,format=rgb24"),
        new("e1b-bilinear-fast", "ablation: e1b-bilinear without accurate_rnd",
            "fps={fps},scale=iw:ih:flags=bilinear+full_chroma_inp,format=yuv444p,scale={w}:{h}:flags=area+full_chroma_int,format=rgb24"),
        new("e1b-bicubic", "E1b: chroma -> 4:4:4 at source size (bicubic), then area to grid",
            "fps={fps},scale=iw:ih:flags=bicubic+accurate_rnd+full_chroma_inp,format=yuv444p,scale={w}:{h}:flags=area+accurate_rnd+full_chroma_int,format=rgb24"),
        new("e1b-bilinear", "E1b': chroma -> 4:4:4 at source size (bilinear), then area to grid",
            "fps={fps},scale=iw:ih:flags=bilinear+accurate_rnd+full_chroma_inp,format=yuv444p,scale={w}:{h}:flags=area+accurate_rnd+full_chroma_int,format=rgb24"),
        new("e1b-zscale", "E1b'': zscale bicubic to RGB at source size, then area to grid",
            "fps={fps},zscale=filter=bicubic:matrixin=470bg:matrix=gbr,format=gbrp,scale={w}:{h}:flags=area,format=rgb24"),
        new("e1c-gbrp", "E1c: convert to planar RGB (gbrp) at source size, then area to grid",
            "fps={fps},scale=iw:ih:flags=bicubic+accurate_rnd+full_chroma_inp,format=gbrp,scale={w}:{h}:flags=area,format=rgb24"),
        new("e1d-center", "E1d (siting probe): as e1b-bicubic but ASSUMING centre chroma siting (wrong for these clips, which are 'left')",
            "fps={fps},scale=iw:ih:flags=bicubic+accurate_rnd+full_chroma_inp:in_h_chr_pos=128:in_v_chr_pos=128,format=yuv444p,scale={w}:{h}:flags=area+accurate_rnd+full_chroma_int,format=rgb24"),
        new("e1d-left", "E1d (siting probe): as e1b-bicubic but explicitly 'left' siting (correct for these clips)",
            "fps={fps},scale=iw:ih:flags=bicubic+accurate_rnd+full_chroma_inp:in_h_chr_pos=0:in_v_chr_pos=128,format=yuv444p,scale={w}:{h}:flags=area+accurate_rnd+full_chroma_int,format=rgb24")
    ];

    public static DecodeVariant Get(string id) => All.First(v => v.Id == id);

    private static readonly Regex Bench = new(@"utime=([0-9.]+)s stime=([0-9.]+)s rtime=([0-9.]+)s", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Decodes the whole clip to an RGB24 grid with <paramref name="variant"/>; CPU seconds are ffmpeg's own (-benchmark).</summary>
    public static DecodeResult Decode(string clip, DecodeVariant variant, double fps, int gw, int gh)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "edge-signal-grid-" + Guid.NewGuid().ToString("N") + ".rgb");
        try
        {
            string[] args =
            [
                "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info", "-benchmark", "-y", "-threads", "0", "-i", clip,
                "-an", "-sn", "-vf", variant.FilterFor(fps, gw, gh), "-f", "rawvideo", "-pix_fmt", "rgb24", tmp
            ];
            (string err, double cpu, double wall) = Run(args);
            byte[] data = File.ReadAllBytes(tmp);
            if (data.Length == 0)
                throw new InvalidOperationException($"decode '{variant.Id}' produced no frames: {err}");
            return new DecodeResult(data, cpu, wall);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>The decoder's own luma plane at the grid size (area-scaled, 8-bit gray): no matrix guess, no RGB clipping.</summary>
    public static DecodeResult DecodeGray(string clip, double fps, int gw, int gh)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "edge-signal-gray-" + Guid.NewGuid().ToString("N") + ".gray");
        try
        {
            string[] args =
            [
                "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info", "-benchmark", "-y", "-threads", "0", "-i", clip,
                "-an", "-sn", "-vf", string.Create(CultureInfo.InvariantCulture, $"fps={fps:0.######},scale={gw}:{gh}:flags=area,format=gray"),
                "-f", "rawvideo", "-pix_fmt", "gray", tmp
            ];
            (_, double cpu, double wall) = Run(args);
            return new DecodeResult(File.ReadAllBytes(tmp), cpu, wall);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    internal static (string Stderr, double Cpu, double Wall) Run(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        var sw = Stopwatch.StartNew();
        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        p.WaitForExit();
        sw.Stop();
        string text = err.Result;
        if (p.ExitCode != 0)
            throw new InvalidOperationException(text);
        return (text, ParseCpu(text), sw.Elapsed.TotalSeconds);
    }

    internal static double ParseCpu(string stderr)
    {
        Match m = Bench.Matches(stderr).LastOrDefault() ?? Match.Empty;
        return m.Success
            ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)
            : double.NaN;
    }
}

/// <summary>
/// A forward-only stream of 8-bit gray frames at <c>HiW x HiH</c> read from an ffmpeg pipe, one frame in memory at a time.
/// <c>Native</c> skips the scale (luma plane at source resolution).
/// </summary>
internal sealed class HiResLumaStream : IDisposable
{
    private readonly Process _proc;
    private readonly Stream _out;
    private readonly byte[] _frame;
    private readonly Task<string> _err;
    private int _next;

    public HiResLumaStream(string clip, double fps, int hiW, int hiH, bool native)
    {
        HiW = hiW;
        HiH = hiH;
        _frame = new byte[hiW * hiH];
        string vf = native
            ? string.Create(CultureInfo.InvariantCulture, $"fps={fps:0.######},format=gray")
            : string.Create(CultureInfo.InvariantCulture, $"fps={fps:0.######},scale={hiW}:{hiH}:flags=area,format=gray");
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info", "-benchmark", "-threads", "0", "-i", clip, "-an", "-sn", "-vf", vf, "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1" })
            psi.ArgumentList.Add(a);
        _proc = Process.Start(psi)!;
        _out = _proc.StandardOutput.BaseStream;
        _err = _proc.StandardError.ReadToEndAsync();
    }

    public int HiW { get; }

    public int HiH { get; }

    /// <summary>Peak bytes held for pixels: one frame.</summary>
    public long FrameBytes => _frame.Length;

    /// <summary>Reads forward to <paramref name="index"/> and returns that frame's buffer (reused; valid until the next call), or null at end of stream.</summary>
    public byte[]? Frame(int index)
    {
        if (index < _next - 1)
            throw new InvalidOperationException("HiResLumaStream is forward-only");
        while (_next <= index)
        {
            int got = 0;
            while (got < _frame.Length)
            {
                int n = _out.Read(_frame, got, _frame.Length - got);
                if (n <= 0)
                    return null;
                got += n;
            }

            _next++;
        }

        return _frame;
    }

    /// <summary>ffmpeg's own CPU seconds, available after the stream has been drained or closed.</summary>
    public double CpuSeconds
    {
        get
        {
            try
            {
                // Drain so ffmpeg finishes and prints its -benchmark line; the CPU figure is then the whole stream's.
                _out.CopyTo(Stream.Null);
                _proc.WaitForExit();
                return EdgeDecode.ParseCpu(_err.Result);
            }
            catch (Exception)
            {
                return double.NaN;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_proc.HasExited)
                _proc.Kill(true);
        }
        catch (InvalidOperationException)
        {
            // already gone
        }

        _proc.Dispose();
    }
}
