#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>speedRamps through the whole compile: off = the same graph, drop reasons, a real 3 s render with duration and A/V sync checks.</summary>
public partial class VideoCompileNarrationAndFormatTests
{
    [Fact]
    public async Task Speed_ramps_off_leaves_the_graph_unchanged_and_on_reports_applied_and_dropped()
    {
        VideoAnalysisArtifact artifact = WithEvents(SingleSourceArtifact(10.0), ("e0", 4.0), ("e1", 12.0));

        var off = new CompileFixture(artifact) { Config = c => c with { PrePaddingMs = 0, PostPaddingMs = 0, SpeedRamps = null } };
        (await off.RunAsync(BuildDecisionJson("s0"))).Status.Should().Be(StepStatus.Completed);
        var plain = new CompileFixture(SingleSourceArtifact(10.0)) { Config = c => c with { PrePaddingMs = 0, PostPaddingMs = 0 } };
        (await plain.RunAsync(BuildDecisionJson("s0"))).Status.Should().Be(StepStatus.Completed);
        off.FilterGraph.Should().Be(plain.FilterGraph);
        off.Output!.ContainsKey("speedRamps").Should().BeFalse();

        var on = new CompileFixture(artifact)
        {
            Config = c => c with
            {
                PrePaddingMs = 0,
                PostPaddingMs = 0,
                SpeedRamps = [new SpeedRampRequest("e0"), new SpeedRampRequest("e1"), new SpeedRampRequest("e7"), new SpeedRampRequest("e0", SpeedRampKind.SpeedUp)]
            }
        };
        StepExecutionResult result = await on.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonObject node = on.Output!["speedRamps"]!.AsObject();
        node["rampsApplied"]!.AsArray().Should().HaveCount(1);
        node["dropped"]!.AsArray().Select(d => d!["reason"]!.GetValue<string>()).Should().Equal("anchor_cut_away", "unknown_anchor_id", "overlaps_other_ramp");
        node["inputDurationSec"]!.GetValue<double>().Should().BeApproximately(10, 0.05);
        node["outputDurationSec"]!.GetValue<double>().Should().BeApproximately(11, 0.05);
        on.Output["outputDurationSec"]!.GetValue<double>().Should().BeApproximately(11, 0.05);
        on.Edl!.ContainsKey("speedRamps").Should().BeTrue();
    }

    [Fact]
    public async Task Speed_ramps_on_a_stream_copy_are_a_config_error()
    {
        var fixture = new CompileFixture(WithEvents(SingleSourceArtifact(10.0), ("e0", 4.0)))
        {
            Config = c => c with { Mode = VideoCompileMode.StreamCopy, AllowKeyframeSnapping = true, SpeedRamps = [new SpeedRampRequest("e0")] }
        };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("SPEED_RAMPS_REQUIRE_REENCODE");
    }

    [Fact]
    public void Speed_ramps_config_checks_count_id_and_words()
    {
        var baseConfig = new VideoCompileStepConfig(1, new ExtractInputRef(ExtractInputSource.Previous), 1);
        SpeedRampRequest.Check(baseConfig).Should().BeNull();
        SpeedRampRequest.Check(baseConfig with { SpeedRamps = Enumerable.Range(0, 9).Select(i => new SpeedRampRequest("e" + i)).ToList() })!.ErrorCode.Should().Be("SPEED_RAMPS_TOO_MANY");
        SpeedRampRequest.Check(baseConfig with { SpeedRamps = [new SpeedRampRequest("t1")] })!.ErrorCode.Should().Be("SPEED_RAMPS_EVENT_ID_INVALID");
        SpeedRampRequest.Check(baseConfig with { SpeedRamps = [new SpeedRampRequest("e1", (SpeedRampKind)99)] })!.ErrorCode.Should().Be("SPEED_RAMPS_WORD_UNKNOWN");
        SpeedRampRequest.Check(baseConfig with { SpeedRamps = [new SpeedRampRequest("e1")] }).Should().BeNull();
    }

    [Fact]
    public void Subtitle_timestamps_are_retimed_with_the_program()
    {
        var t = RampTimeline.Build(10, [new RampWindow(4, 5, 0.5)]);
        string srt = "1\r\n00:00:06,000 --> 00:00:09,500\r\nhello\r\n\r\n";

        string mapped = SpeedRampApplier.RemapSubtitles(srt, t);

        mapped.Should().Be("1\r\n00:00:07,000 --> 00:00:10,500\r\nhello\r\n\r\n");
    }

    // A 3 s 30 fps clip: black picture and silence until 2.0 s, then white picture and a 440 Hz tone. Picture and sound change on the same instant.
    private static string MakeSyncClip(string work)
    {
        string clip = Path.Combine(work, "clip.mp4");
        RunArgv(work, "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "color=black:s=128x72:r=30:d=3,format=yuv420p,drawbox=x=0:y=0:w=128:h=72:color=white:t=fill:enable='gte(t,2)'",
            "-f", "lavfi", "-i", "aevalsrc='if(gte(t,2),0.5*sin(2*PI*440*t),0)':s=48000:d=3",
            "-c:v", "libx264", "-crf", "0", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-shortest", clip);
        return clip;
    }

    private static (string Work, FfmpegVideoToolRunner Runner, IOptions<VideoEditingOptions> Options) NewWork(string tag)
    {
        string work = Path.Combine(Path.GetTempPath(), "reelbolt-" + tag + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var options = Options.Create(new VideoEditingOptions
        {
            ScratchPath = Path.Combine(work, "scratch"),
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 60,
            CompileTimeoutSeconds = 120
        });
        return (work, new FfmpegVideoToolRunner(options, NullLogger<FfmpegVideoToolRunner>.Instance), options);
    }

    private static async Task<(string Out, StepExecutionResult Result)> RenderRamped(string work, FfmpegVideoToolRunner runner, IOptions<VideoEditingOptions> options, SpeedRampRequest ramp)
    {
        string clip = MakeSyncClip(work);
        var fixture = new CompileFixture(WithEvents(SingleSourceArtifact(3.0, 128, 72), ("e0", 1.0)), sourcesHaveAudio: true)
        {
            RealToolRunner = runner,
            RealProbe = new FfprobeMediaProbe(runner, options, NullLogger<FfprobeMediaProbe>.Instance),
            SourceFile = clip,
            OutputCopy = Path.Combine(work, "out.mp4")
        };
        fixture.Config = c => c with { PrePaddingMs = 0, PostPaddingMs = 0, Crf = 0, SpeedRamps = [ramp] };
        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));
        return (fixture.OutputCopy!, result);
    }

    private static string ProbeOut(params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return stdout;
    }

    private static double ProbeDuration(string work, string path, string stream) =>
        double.Parse(ProbeOut("-v", "error", "-select_streams", stream, "-show_entries", "stream=duration", "-of", "csv=p=0", path).Trim(),
            System.Globalization.CultureInfo.InvariantCulture);

    [RealFfmpegFact]
    public async Task Real_ffmpeg_slow_motion_keeps_duration_and_av_sync()
    {
        (string work, FfmpegVideoToolRunner runner, IOptions<VideoEditingOptions> options) = NewWork("ramp");
        try
        {
            // Event at 1.0 s, slow motion (Normal = half speed) for 1 s: the output is 4 s and the source's 2.0 s change lands at 3.0 s.
            (string output, StepExecutionResult result) = await RenderRamped(work, runner, options, new SpeedRampRequest("e0", SpeedRampKind.SlowMo));

            result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
            ProbeDuration(work, output, "v:0").Should().BeApproximately(4.0, 0.1);
            ProbeDuration(work, output, "a:0").Should().BeApproximately(4.0, 0.1);

            string raw = Path.Combine(work, "frames.yuv");
            RunArgv(work, "ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", output, "-pix_fmt", "yuv420p", "-f", "rawvideo", raw);
            byte[] frames = File.ReadAllBytes(raw);
            int frameBytes = 128 * 72 * 3 / 2;
            (frames.Length / frameBytes).Should().BeInRange(4 * 30 - 2, 4 * 30 + 2, "constant 30 fps over 4 s");
            int firstWhite = Enumerable.Range(0, frames.Length / frameBytes).First(k => frames[k * frameBytes + 64 * 36] > 200);

            string pcm = Path.Combine(work, "audio.s16");
            RunArgv(work, "ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", output, "-ac", "1", "-ar", "48000", "-f", "s16le", pcm);
            byte[] audio = File.ReadAllBytes(pcm);
            int firstLoud = 0;
            for (; firstLoud + 1 < audio.Length; firstLoud += 2)
            {
                if (Math.Abs((short)(audio[firstLoud] | (audio[firstLoud + 1] << 8))) > 2000)
                    break;
            }

            double videoChangeSec = firstWhite / 30.0;
            double audioChangeSec = firstLoud / 2 / 48000.0;
            videoChangeSec.Should().BeApproximately(3.0, 2.5 / 30, "the 2.0 s change of the source lands at 3.0 s");
            audioChangeSec.Should().BeApproximately(videoChangeSec, 0.08, "sound and picture stay together through the ramp");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    [RealFfmpegFact]
    public async Task Real_ffmpeg_freeze_hit_holds_one_frame_and_keeps_audio_in_step()
    {
        (string work, FfmpegVideoToolRunner runner, IOptions<VideoEditingOptions> options) = NewWork("freeze");
        try
        {
            // Event at 1.0 s, a 0.6 s hold (Medium): 3.6 s out; the tone that starts at source 2.0 s starts at 2.6 s.
            (string output, StepExecutionResult result) = await RenderRamped(work, runner, options, new SpeedRampRequest("e0", SpeedRampKind.FreezeHit));

            result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
            ProbeDuration(work, output, "v:0").Should().BeApproximately(3.6, 0.1);
            ProbeDuration(work, output, "a:0").Should().BeApproximately(3.6, 0.1);
            string raw = Path.Combine(work, "frames.yuv");
            RunArgv(work, "ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", output, "-pix_fmt", "yuv420p", "-f", "rawvideo", raw);
            byte[] frames = File.ReadAllBytes(raw);
            int frameBytes = 128 * 72 * 3 / 2;
            int firstWhite = Enumerable.Range(0, frames.Length / frameBytes).First(k => frames[k * frameBytes + 64 * 36] > 200);
            (firstWhite / 30.0).Should().BeApproximately(2.6, 2.5 / 30);
            string pcm = Path.Combine(work, "audio.s16");
            RunArgv(work, "ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", output, "-ac", "1", "-ar", "48000", "-f", "s16le", pcm);
            byte[] audio = File.ReadAllBytes(pcm);
            int firstLoud = 0;
            for (; firstLoud + 1 < audio.Length; firstLoud += 2)
            {
                if (Math.Abs((short)(audio[firstLoud] | (audio[firstLoud + 1] << 8))) > 2000)
                    break;
            }

            (firstLoud / 2 / 48000.0).Should().BeApproximately(2.6, 0.08);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }
}
