using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Regression (#340), against the REAL ffprobe: a transparent VP9/WebM that went through a plain
/// stream-copy remux (<c>ffmpeg -i a.webm -c copy b.webm</c>, the kind of mux step a Remotion
/// render ends with) reports its alpha only as <c>tags.ALPHA_MODE="1"</c> — uppercase. The parser's
/// exact <c>"alpha_mode"</c> lookup missed it, so every such overlay was dropped as opaque.
/// </summary>
public class WebmAlphaModeTagRealFfmpegTests
{
    [FfmpegToolsFact]
    public async Task A_stream_copy_remuxed_transparent_webm_reports_ALPHA_MODE_and_still_probes_as_alpha()
    {
        string root = WebmAlphaFixtures.NewTempRoot();
        try
        {
            FfmpegVideoToolRunner runner = WebmAlphaFixtures.Runner(root);
            string encoded = Path.Combine(root, "encoded.webm");
            string remuxed = Path.Combine(root, "remuxed.webm");
            await WebmAlphaFixtures.EncodeTransparentWebmAsync(runner, encoded, width: 64, height: 64, durationSec: 0.5);
            await WebmAlphaFixtures.RemuxStreamCopyAsync(runner, encoded, remuxed);

            // The premise, read off the real ffprobe JSON: the remux carries ONLY the uppercase key.
            JsonElement remuxedTags = await RawVideoStreamTagsAsync(runner, remuxed);
            remuxedTags.EnumerateObject().Select(p => p.Name).Should().Contain("ALPHA_MODE")
                .And.NotContain("alpha_mode");

            var probe = new FfprobeMediaProbe(runner, WebmAlphaFixtures.Options(root), NullLogger<FfprobeMediaProbe>.Instance);
            MediaProbeResult encodedProbe = await probe.ProbeAsync(encoded, CancellationToken.None);
            MediaProbeResult remuxedProbe = await probe.ProbeAsync(remuxed, CancellationToken.None);

            encodedProbe.AlphaModeTag.Should().Be("1");
            remuxedProbe.AlphaModeTag.Should().Be("1", "the remux's ALPHA_MODE tag is the same alpha signal in another case");
            AlphaPixelFormats.HasAlpha(encodedProbe.PixFmt, encodedProbe.AlphaModeTag).Should().BeTrue();
            AlphaPixelFormats.HasAlpha(remuxedProbe.PixFmt, remuxedProbe.AlphaModeTag).Should().BeTrue();
        }
        finally
        {
            WebmAlphaFixtures.TryDelete(root);
        }
    }

    [FfmpegToolsFact]
    public async Task An_opaque_vp9_webm_still_probes_as_having_no_alpha()
    {
        string root = WebmAlphaFixtures.NewTempRoot();
        try
        {
            FfmpegVideoToolRunner runner = WebmAlphaFixtures.Runner(root);
            string opaque = Path.Combine(root, "opaque.webm");
            await WebmAlphaFixtures.EncodeOpaqueWebmAsync(runner, opaque, width: 64, height: 64, durationSec: 0.5);

            var probe = new FfprobeMediaProbe(runner, WebmAlphaFixtures.Options(root), NullLogger<FfprobeMediaProbe>.Instance);
            MediaProbeResult result = await probe.ProbeAsync(opaque, CancellationToken.None);

            result.AlphaModeTag.Should().BeNull();
            AlphaPixelFormats.HasAlpha(result.PixFmt, result.AlphaModeTag).Should().BeFalse();
        }
        finally
        {
            WebmAlphaFixtures.TryDelete(root);
        }
    }

    private static async Task<JsonElement> RawVideoStreamTagsAsync(IVideoToolRunner runner, string path)
    {
        VideoToolResult result = await runner.RunFfprobeAsync(
            FfmpegArgvBuilder.BuildProbeArgs(path), TimeSpan.FromMinutes(1), CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.StdErr);

        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        JsonElement video = doc.RootElement.GetProperty("streams").EnumerateArray()
            .First(s => s.GetProperty("codec_type").GetString() == "video");
        return video.GetProperty("tags").Clone();
    }
}

/// <summary>
/// Real VP9/WebM overlay fixtures built with the host's own ffmpeg, in a caller-owned temp dir.
/// </summary>
internal static class WebmAlphaFixtures
{
    public static string NewTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "webm-alpha-mode", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static IOptions<VideoEditingOptions> Options(string root) => Microsoft.Extensions.Options.Options.Create(new VideoEditingOptions
    {
        ScratchPath = Path.Combine(root, "scratch"),
        MaxConcurrentJobs = 1,
        AnalyzeTimeoutSeconds = 120,
        CompileTimeoutSeconds = 300
    });

    public static FfmpegVideoToolRunner Runner(string root) =>
        new(Options(root), NullLogger<FfmpegVideoToolRunner>.Instance);

    /// <summary>
    /// A yuva420p VP9/WebM: fully transparent, with an OPAQUE red rectangle covering its middle half
    /// (in each dimension). The encoder writes <c>tags.alpha_mode="1"</c>.
    /// </summary>
    public static Task EncodeTransparentWebmAsync(
        IVideoToolRunner runner, string output, int width, int height, double durationSec)
    {
        string d = durationSec.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string graph =
            $"color=c=black@0.0:s={width}x{height}:r=30:d={d},format=yuva420p[bg];" +
            $"color=c=red:s={width / 2}x{height / 2}:r=30:d={d},format=yuva420p[fg];" +
            $"[bg][fg]overlay={width / 4}:{height / 4}:format=auto,format=yuva420p[v]";
        return RunAsync(runner,
        [
            "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
            "-filter_complex", graph, "-map", "[v]",
            "-c:v", "libvpx-vp9", "-pix_fmt", "yuva420p", "-deadline", "realtime", "-cpu-used", "8",
            "-b:v", "0", "-crf", "20",
            output
        ]);
    }

    /// <summary>A plain yuv420p VP9/WebM: no alpha plane, no alpha_mode tag.</summary>
    public static Task EncodeOpaqueWebmAsync(
        IVideoToolRunner runner, string output, int width, int height, double durationSec)
    {
        string d = durationSec.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return RunAsync(runner,
        [
            "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
            "-f", "lavfi", "-i", $"color=c=red:s={width}x{height}:r=30:d={d}",
            "-c:v", "libvpx-vp9", "-pix_fmt", "yuv420p", "-deadline", "realtime", "-cpu-used", "8",
            "-b:v", "0", "-crf", "20",
            output
        ]);
    }

    /// <summary>A stream-copy remux; ffmpeg's matroska muxer then writes the tag as <c>ALPHA_MODE</c>.</summary>
    public static Task RemuxStreamCopyAsync(IVideoToolRunner runner, string input, string output) =>
        RunAsync(runner,
        [
            "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
            "-i", input, "-c", "copy",
            output
        ]);

    public static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch directory is not a test failure.
        }
    }

    private static async Task RunAsync(IVideoToolRunner runner, string[] args)
    {
        VideoToolResult result = await runner.RunFfmpegAsync(args, TimeSpan.FromMinutes(2), CancellationToken.None);
        result.Succeeded.Should().BeTrue("the fixture is built with the host's own ffmpeg: " + result.StdErr);
    }
}
