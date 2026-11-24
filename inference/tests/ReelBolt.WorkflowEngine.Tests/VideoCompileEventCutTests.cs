#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>CutOnEvents: a Keep span may begin or end on an offered event; off = unchanged; a real 3 s render cuts on the event frame.</summary>
public partial class VideoCompileNarrationAndFormatTests
{
    private static string EventSpanJson(string fromId, string toId) => JsonSerializer.Serialize(new
    {
        keep = new[] { new { fromId, toId, reason = "keep" } },
        editRationale = "test",
        suggestedTitle = "Test"
    });

    private static CompileFixture EventCutFixture(VideoAnalysisArtifact artifact, bool cutOnEvents = true) =>
        new(artifact) { Config = c => c with { PrePaddingMs = 0, PostPaddingMs = 0, MinSegmentMs = 0, CutOnEvents = cutOnEvents } };

    [Fact]
    public async Task Event_cut_off_changes_nothing_and_an_event_in_a_keep_is_still_unknown()
    {
        VideoAnalysisArtifact artifact = WithEvents(SingleSourceArtifact(10.0), ("e0", 4.0));

        var off = EventCutFixture(artifact, cutOnEvents: false);
        StepExecutionResult refused = await off.RunAsync(EventSpanJson("e0", "s0"));
        refused.Status.Should().Be(StepStatus.Failed);
        ErrorCode(refused).Should().Be("UNKNOWN_ID");

        var withEvents = EventCutFixture(artifact, cutOnEvents: false);
        (await withEvents.RunAsync(BuildDecisionJson("s0"))).Status.Should().Be(StepStatus.Completed);
        var plain = EventCutFixture(SingleSourceArtifact(10.0), cutOnEvents: false);
        (await plain.RunAsync(BuildDecisionJson("s0"))).Status.Should().Be(StepStatus.Completed);
        withEvents.FilterGraph.Should().Be(plain.FilterGraph);
        withEvents.Output!.ContainsKey("eventCuts").Should().BeFalse();
        JsonSerializer.Serialize(new VideoCompileStepConfig(1, new ExtractInputRef(ExtractInputSource.Previous), 1)).Should().NotContain("CutOnEvents");
    }

    [Fact]
    public async Task Event_cut_on_resolves_an_offered_event_and_pins_the_graph()
    {
        VideoAnalysisArtifact artifact = WithEvents(SingleSourceArtifact(10.0), ("e0", 4.0), ("e1", 6.5));

        var from = EventCutFixture(artifact);
        StepExecutionResult result = await from.RunAsync(EventSpanJson("e0", "s0"));
        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        from.FilterGraph.Should().Contain("select='gte(t,4)*lt(t,10)',setpts=N/FRAME_RATE/TB[vout]");
        from.Output!["eventCuts"]!.AsArray().Single()!["placedOn"]!.GetValue<string>().Should().Be("event");

        var both = EventCutFixture(artifact);
        (await both.RunAsync(EventSpanJson("e0", "e1"))).Status.Should().Be(StepStatus.Completed);
        both.FilterGraph.Should().Contain("select='gte(t,4)*lt(t,6.5)',setpts=N/FRAME_RATE/TB[vout]");
        both.Output!["eventCuts"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public async Task Event_cut_on_an_unoffered_event_fails_unknown_id()
    {
        VideoAnalysisArtifact artifact = WithEvents(SingleSourceArtifact(10.0), ("e0", 4.0));
        var fixture = EventCutFixture(artifact);

        StepExecutionResult result = await fixture.RunAsync(EventSpanJson("e5", "s0"));

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("UNKNOWN_ID");
        ErrorCode(await EventCutFixture(artifact).RunAsync(EventSpanJson("s0", "e9"))).Should().Be("UNKNOWN_ID");
    }

    [Fact]
    public async Task Event_cut_backwards_is_an_invalid_span_and_stream_copy_is_a_config_error()
    {
        VideoAnalysisArtifact artifact = WithEvents(SingleSourceArtifact(10.0), ("e0", 4.0), ("e1", 2.0));

        ErrorCode(await EventCutFixture(artifact).RunAsync(EventSpanJson("e0", "e1"))).Should().Be("INVALID_SPAN");

        var copy = new CompileFixture(artifact)
        {
            Config = c => c with { Mode = VideoCompileMode.StreamCopy, AllowKeyframeSnapping = true, CutOnEvents = true }
        };
        ErrorCode(await copy.RunAsync(EventSpanJson("e0", "s0"))).Should().Be("EVENT_CUT_REQUIRE_REENCODE");
    }

    [Fact]
    public void Event_boundary_snaps_to_a_frame_and_prefers_a_silence_gap_only_where_speech_covers_the_event()
    {
        VideoAnalysisArtifact baseArtifact = WithEvents(SingleSourceArtifact(10.0), ("e0", 4.013), ("e1", 8.0));
        VideoAnalysisArtifact spoken = baseArtifact with
        {
            Segments = [new VideoAnalysisSegment("t0", "s0", 3.0, 5.0, "hello there")],
            SilenceSpans = [new VideoAnalysisSilenceSpan("g0", 4.1, 4.3, "s0"), new VideoAnalysisSilenceSpan("g1", 6.0, 6.5, "s0")]
        };

        EventCutBoundaries.TryBoundary(baseArtifact, "e0", _ => 30, out var plain, out _).Should().BeTrue();
        plain.Sec.Should().BeApproximately(4.0, 1e-9, "4.013 s snaps to frame 120");
        plain.Via.Should().Be("event");

        EventCutBoundaries.TryBoundary(spoken, "e0", _ => 30, out var snapped, out _).Should().BeTrue();
        snapped.Sec.Should().BeApproximately(4.2, 1e-9, "speech covers e0, so the cut moves to the middle of the gap 0.087 s away");
        snapped.Via.Should().Be("silence_gap");

        EventCutBoundaries.TryBoundary(spoken, "e1", _ => 30, out var free, out _).Should().BeTrue();
        free.Via.Should().Be("event", "no speech covers e1");

        VideoAnalysisArtifact far = spoken with { SilenceSpans = [new VideoAnalysisSilenceSpan("g0", 4.6, 4.8, "s0")] };
        EventCutBoundaries.TryBoundary(far, "e0", _ => 30, out var tooFar, out _);
        tooFar.Via.Should().Be("event", "a gap further than 0.3 s is not preferred");
    }

    [Fact]
    public async Task Event_cut_in_the_compile_lands_on_the_gap_next_to_a_spoken_event()
    {
        VideoAnalysisArtifact artifact = WithEvents(SingleSourceArtifact(10.0), ("e0", 4.0)) with
        {
            Segments = [new VideoAnalysisSegment("t0", "s0", 3.0, 5.0, "hello there")],
            SilenceSpans = [new VideoAnalysisSilenceSpan("g0", 4.1, 4.3, "s0")],
            OfferedIds = ["s0", "g0", "t0"]
        };
        var fixture = EventCutFixture(artifact);

        StepExecutionResult result = await fixture.RunAsync(EventSpanJson("e0", "s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.FilterGraph.Should().Contain("select='gte(t,4.2)*lt(t,10)',setpts=N/FRAME_RATE/TB[vout]");
        fixture.Output!["eventCuts"]!.AsArray().Single()!["placedOn"]!.GetValue<string>().Should().Be("silence_gap");
    }

    [RealFfmpegFact]
    public async Task Real_ffmpeg_event_cut_starts_the_output_on_the_event_frame()
    {
        // A 3 s 30 fps clip that is dark until 1.5 s (frame 45) and bright from there.
        const int width = 128, height = 72, fps = 30;
        string work = Path.Combine(Path.GetTempPath(), "reelbolt-eventcut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string clip = Path.Combine(work, "clip.mp4");
            RunArgv(work, "ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi",
                "-i", $"color=black:s={width}x{height}:r={fps}:d=3,format=yuv420p,geq=lum='if(gte(T,1.5),200,20)':cb=128:cr=128",
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
            fixture.Config = c => c with { PrePaddingMs = 0, PostPaddingMs = 0, MinSegmentMs = 0, Crf = 0, CutOnEvents = true };

            StepExecutionResult result = await fixture.RunAsync(EventSpanJson("e0", "s0"));

            result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
            string raw = Path.Combine(work, "frames.yuv");
            RunArgv(work, "ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", fixture.OutputCopy!, "-pix_fmt", "yuv420p", "-f", "rawvideo", raw);
            byte[] bytes = File.ReadAllBytes(raw);
            int frameBytes = width * height * 3 / 2;
            int frames = bytes.Length / frameBytes;
            frames.Should().BeInRange(fps * 3 / 2 - 1, fps * 3 / 2 + 1, "the cut keeps the 1.5 s from the event to the end");
            bytes[0].Should().BeGreaterThan(150, "the first output frame is the event frame, already bright");
            bytes[(frames - 1) * frameBytes].Should().BeGreaterThan(150);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }
}
