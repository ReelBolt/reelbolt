using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The tracker's gray frame pass, and the colour grid pass beside it, on a tiny clip that ffmpeg renders at test time. The bounds added to the pass (input
/// whitelist, frame cap, size cap) must not move one byte of the frames of an honest file, and must stop a file that
/// is longer than its caller was told. No-ops on a host without ffmpeg/ffprobe.
/// </summary>
public class GraySamplerFfmpegTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gray-fixture-" + Guid.NewGuid().ToString("N"));
    private readonly IOptions<VideoEditingOptions> _options = Options.Create(new VideoEditingOptions());
    private readonly FfmpegVideoToolRunner _runner;

    public GraySamplerFfmpegTests()
    {
        Directory.CreateDirectory(_dir);
        _runner = new FfmpegVideoToolRunner(_options, NullLogger<FfmpegVideoToolRunner>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<string> RenderAsync(string name, double seconds)
    {
        string path = Path.Combine(_dir, name);
        string graph = FormattableString.Invariant($"testsrc2=s=320x180:r=30:d={seconds}");
        VideoToolResult result = await _runner.RunFfmpegAsync(
            ["-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-f", "lavfi", "-i", graph, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "12", path],
            TimeSpan.FromSeconds(60), CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.StdErr);
        return name;
    }

    /// <summary>The pass as it was before the bounds: the same decode and filter, nothing else.</summary>
    private async Task<byte[]> UnboundedAsync(string clip, double fps, int width, int height)
    {
        string output = Path.Combine(_dir, "unbounded-" + Guid.NewGuid().ToString("N") + ".raw");
        VideoToolResult result = await _runner.RunFfmpegAsync(
            ["-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-protocol_whitelist", "file", "-threads", "2",
             "-i", Path.Combine(_dir, clip), "-an", "-sn",
             "-vf", FormattableString.Invariant($"fps={fps},scale={width}:{height}:flags=bicubic,format=gray"),
             "-f", "rawvideo", "-pix_fmt", "gray", output],
            TimeSpan.FromSeconds(60), CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.StdErr);
        return await File.ReadAllBytesAsync(output);
    }

    [FfmpegToolsFact]
    public async Task An_honest_file_gives_the_same_frames_with_the_bounds_as_without()
    {
        string clip = await RenderAsync("honest.mp4", 4);
        LocalMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(_dir, _runner);
        FfmpegFrameGridSampler sampler = new(_runner, _options, NullLogger<FfmpegFrameGridSampler>.Instance);

        // 4 s at 12 fps: 48 frames, well inside a cap of 3,000.
        FrameGridResult gray = await sampler.SampleGrayAsync(workspace, clip, 12.0, 160, 90, 4.0, 3000, CancellationToken.None);
        // 4 s where the caller allows 20 frames: the rate drops to 5 fps, and the pass still writes every frame of it.
        FrameGridResult lowered = await sampler.SampleGrayAsync(workspace, clip, 12.0, 160, 90, 4.0, 20, CancellationToken.None);

        byte[] expected = await UnboundedAsync(clip, 12.0, 160, 90);
        byte[] expectedLowered = await UnboundedAsync(clip, 5.0, 160, 90);
        gray.FrameCount.Should().Be(48);
        gray.PixelData.SequenceEqual(expected).Should().BeTrue("the bounds must not change one byte of the frames");
        lowered.EffectiveFps.Should().Be(5.0);
        lowered.PixelData.SequenceEqual(expectedLowered).Should().BeTrue();
        Directory.GetFiles(_dir, "gray-*.raw").Should().BeEmpty();
    }

    [FfmpegToolsFact]
    public async Task A_file_longer_than_the_caller_was_told_stops_at_the_frame_cap()
    {
        // The caller believes the clip lasts one second and allows 30 frames; the file runs for ten.
        string clip = await RenderAsync("long.mp4", 10);
        LocalMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(_dir, _runner);
        FfmpegFrameGridSampler sampler = new(_runner, _options, NullLogger<FfmpegFrameGridSampler>.Instance);

        FrameGridResult gray = await sampler.SampleGrayAsync(workspace, clip, 30.0, 160, 90, 1.0, 30, CancellationToken.None);

        long cap = FfmpegFrameGridSampler.PlanMaxGrayFrames(30);
        gray.FrameCount.Should().Be((int)cap);
        gray.PixelData.Length.Should().Be((int)cap * 160 * 90);
        byte[] unbounded = await UnboundedAsync(clip, 30.0, 160, 90);
        unbounded.Length.Should().BeGreaterThan(gray.PixelData.Length);
        gray.PixelData.SequenceEqual(unbounded.Take(gray.PixelData.Length)).Should().BeTrue("the frames it keeps are the first frames, unchanged");
    }

    /// <summary>The colour grid pass as it was before its frame cap: the same decode and filter, no cap.</summary>
    private async Task<byte[]> UnboundedColourAsync(string clip, double fps, int width, int height, GridChroma chroma)
    {
        string output = Path.Combine(_dir, "unbounded-" + Guid.NewGuid().ToString("N") + ".rgb");
        VideoToolResult result = await _runner.RunFfmpegAsync(
            FfmpegArgvBuilder.BuildGridSampleArgs(Path.Combine(_dir, clip), output, fps, width, height, _options.Value.FfmpegThreads, chroma),
            TimeSpan.FromSeconds(60), CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.StdErr);
        return await File.ReadAllBytesAsync(output);
    }

    [FfmpegToolsTheory]
    [InlineData(GridChroma.Default)]
    [InlineData(GridChroma.FullChroma)]
    public async Task The_colour_grid_of_an_honest_file_is_the_same_with_the_frame_cap_as_without(GridChroma chroma)
    {
        string clip = await RenderAsync("honest.mp4", 4);
        LocalMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(_dir, _runner);
        FfmpegFrameGridSampler sampler = new(_runner, _options, NullLogger<FfmpegFrameGridSampler>.Instance);

        // 4 s at 12 fps: 48 frames, well inside a cap of 3,000.
        FrameGridResult grid = await sampler.SampleAsync(workspace, clip, 12.0, 160, 90, 4.0, 3000, chroma, CancellationToken.None);
        byte[] expected = await UnboundedColourAsync(clip, 12.0, 160, 90, chroma);
        // 4 s where the caller allows 20 frames: the rate drops to 5 fps, and the pass still writes every frame of it.
        FrameGridResult lowered = await sampler.SampleAsync(workspace, clip, 12.0, 160, 90, 4.0, 20, chroma, CancellationToken.None);
        byte[] expectedLowered = await UnboundedColourAsync(clip, 5.0, 160, 90, chroma);

        grid.FrameCount.Should().Be(48);
        grid.PixelData.SequenceEqual(expected).Should().BeTrue("the cap must not change one byte of the frames");
        lowered.EffectiveFps.Should().Be(5.0);
        lowered.FrameCount.Should().Be(20);
        lowered.PixelData.SequenceEqual(expectedLowered).Should().BeTrue();
    }

    [FfmpegToolsFact]
    public async Task A_colour_grid_of_a_file_longer_than_the_caller_was_told_stops_at_the_frame_cap()
    {
        // The caller believes the clip lasts one second and allows 30 frames; the file runs for ten.
        string clip = await RenderAsync("long.mp4", 10);
        LocalMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(_dir, _runner);
        FfmpegFrameGridSampler sampler = new(_runner, _options, NullLogger<FfmpegFrameGridSampler>.Instance);

        FrameGridResult grid = await sampler.SampleAsync(workspace, clip, 30.0, 160, 90, 1.0, 30, CancellationToken.None);

        long cap = FfmpegFrameGridSampler.PlanMaxGrayFrames(30);
        grid.FrameCount.Should().Be((int)cap);
        grid.PixelData.Length.Should().Be((int)cap * 160 * 90 * 3);
        byte[] unbounded = await UnboundedColourAsync(clip, 30.0, 160, 90, GridChroma.Default);
        unbounded.Length.Should().BeGreaterThan(grid.PixelData.Length, "without the cap the pass writes the whole ten seconds");
        grid.PixelData.SequenceEqual(unbounded.Take(grid.PixelData.Length)).Should().BeTrue("the frames it keeps are the first frames, unchanged");
    }

    [FfmpegToolsFact]
    public async Task A_file_that_is_not_a_media_container_is_refused()
    {
        // An ffmpeg script: readable by a demuxer that is not on the list.
        string name = "script.ffconcat";
        await File.WriteAllTextAsync(Path.Combine(_dir, name), "ffconcat version 1.0\nfile 'honest.mp4'\n");
        await RenderAsync("honest.mp4", 1);
        LocalMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(_dir, _runner);
        FfmpegFrameGridSampler sampler = new(_runner, _options, NullLogger<FfmpegFrameGridSampler>.Instance);

        Func<Task> act = () => sampler.SampleGrayAsync(workspace, name, 12.0, 160, 90, 1.0, 100, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        Directory.GetFiles(_dir, "gray-*.raw").Should().BeEmpty();
    }
}
