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

/// <summary>eventMoves through the whole compile: off = the same graph, on = zoompan at the event, drop reasons, a real 3 s render.</summary>
public partial class VideoCompileNarrationAndFormatTests
{
    private static VideoAnalysisArtifact WithEvents(VideoAnalysisArtifact a, params (string Id, double Sec)[] events) => a with
    {
        Events = events.Select(e => new VideoAnalysisRenderEvent(e.Id, "event", "event", (int)Math.Round(e.Sec * 30), e.Sec, 0, null, "Flash", "local", 0.9, null)).ToList(),
        OfferedEventIds = events.Select(e => e.Id).ToList()
    };

    [Fact]
    public async Task Event_moves_off_leaves_the_graph_unchanged_and_on_adds_a_zoompan_and_a_report()
    {
        VideoAnalysisArtifact artifact = WithEvents(SingleSourceArtifact(10.0), ("e0", 4.0), ("e1", 12.0));

        var off = new CompileFixture(artifact) { Config = c => c with { PrePaddingMs = 0, PostPaddingMs = 0, EventMoves = null } };
        (await off.RunAsync(BuildDecisionJson("s0"))).Status.Should().Be(StepStatus.Completed);
        var plain = new CompileFixture(SingleSourceArtifact(10.0)) { Config = c => c with { PrePaddingMs = 0, PostPaddingMs = 0 } };
        (await plain.RunAsync(BuildDecisionJson("s0"))).Status.Should().Be(StepStatus.Completed);
        off.FilterGraph.Should().Be(plain.FilterGraph).And.NotContain("zoompan");
        off.Output!.ContainsKey("eventMoves").Should().BeFalse();

        var on = new CompileFixture(artifact)
        {
            Config = c => c with
            {
                PrePaddingMs = 0,
                PostPaddingMs = 0,
                EventMoves = [new EventMoveRequest("e0", EventMoveKind.PunchIn), new EventMoveRequest("e1"), new EventMoveRequest("e7")]
            }
        };
        StepExecutionResult result = await on.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        on.FilterGraph.Should().Contain("zoompan");
        JsonObject node = on.Output!["eventMoves"]!.AsObject();
        node["movesApplied"]!.AsArray().Should().HaveCount(1);
        node["dropped"]!.AsArray().Select(d => d!["reason"]!.GetValue<string>()).Should().Equal("anchor_cut_away", "unknown_anchor_id");
        on.Edl!.ContainsKey("eventMoves").Should().BeTrue();
    }

    [Fact]
    public async Task Event_moves_on_a_stream_copy_are_a_config_error()
    {
        var fixture = new CompileFixture(WithEvents(SingleSourceArtifact(10.0), ("e0", 4.0)))
        {
            Config = c => c with { Mode = VideoCompileMode.StreamCopy, AllowKeyframeSnapping = true, EventMoves = [new EventMoveRequest("e0")] }
        };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("EVENT_MOVES_REQUIRE_REENCODE");
    }

    [RealFfmpegFact]
    public async Task Real_ffmpeg_event_punch_in_starts_on_the_event_frame()
    {
        // A 256x144 horizontal luma ramp (pixel value = column), event at 1.5 s = frame 45 of a 3 s 30 fps clip.
        const int width = 256, height = 144, fps = 30;
        string work = Path.Combine(Path.GetTempPath(), "reelbolt-eventmove-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string clip = Path.Combine(work, "clip.mp4");
            RunArgv(work, "ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi",
                "-i", $"color=black:s={width}x{height}:r={fps}:d=3,format=yuv420p,geq=lum='X':cb=128:cr=128",
                "-an", "-c:v", "libx264", "-crf", "0", "-pix_fmt", "yuv420p", clip);
            var options = Options.Create(new VideoEditingOptions
            {
                ScratchPath = Path.Combine(work, "scratch"),
                MaxConcurrentJobs = 1,
                AnalyzeTimeoutSeconds = 60,
                CompileTimeoutSeconds = 120
            });
            var runner = new FfmpegVideoToolRunner(options, NullLogger<FfmpegVideoToolRunner>.Instance);
            var fixture = new CompileFixture(WithEvents(SingleSourceArtifact(3.0, width, height), ("e0", 1.5)), sourcesHaveAudio: false)
            {
                RealToolRunner = runner,
                RealProbe = new FfprobeMediaProbe(runner, options, NullLogger<FfprobeMediaProbe>.Instance),
                SourceFile = clip,
                OutputCopy = Path.Combine(work, "out.mp4")
            };
            fixture.Config = c => c with
            {
                PrePaddingMs = 0,
                PostPaddingMs = 0,
                Crf = 0,
                EventMoves = [new EventMoveRequest("e0", EventMoveKind.PunchIn, EventMoveIntensity.Strong, EventMoveHold.Short, ReframeEasing.Linear)]
            };

            StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

            result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
            string raw = Path.Combine(work, "frames.yuv");
            RunArgv(work, "ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", fixture.OutputCopy!, "-pix_fmt", "yuv420p", "-f", "rawvideo", raw);
            byte[] bytes = File.ReadAllBytes(raw);
            int frameBytes = width * height * 3 / 2;
            (bytes.Length / frameBytes).Should().Be(3 * fps, "a move keeps every frame");
            // Visible width of a frame = right-edge column - left-edge column + 1 of the ramp.
            int Visible(int k)
            {
                int row = k * frameBytes + height / 2 * width;
                return bytes[row + width - 1] - bytes[row] + 1;
            }

            int firstZoomed = Enumerable.Range(0, 3 * fps).First(k => Visible(k) < width - 4);
            firstZoomed.Should().BeInRange(45 - 1, 45 + 2, "the zoom starts on the event's frame");
            Visible(60).Should().BeLessThan(width * 3 / 4, "the strong punch-in has reached its peak shortly after the event");
            Visible(fps * 3 - 1).Should().BeGreaterThan(width - 6, "the move has released by the end");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }
}
