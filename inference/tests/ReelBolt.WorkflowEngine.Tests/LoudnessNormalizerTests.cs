using System.Collections.Generic;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The two-pass program loudness (docs/video-editing.md "Program loudness"). The parser is fed the
/// exact block ffmpeg 8's loudnorm printed for the narrated launch reel (-22.78 LUFS), which the
/// applying pass took to -14.2 LUFS with every video frame copied.
/// </summary>
public class LoudnessNormalizerTests
{
    private const string RealStderr = """
        [Parsed_loudnorm_0 @ 0x5f2c] 
        {
        	"input_i" : "-22.78",
        	"input_tp" : "-3.76",
        	"input_lra" : "8.10",
        	"input_thresh" : "-33.61",
        	"output_i" : "-15.02",
        	"output_tp" : "-1.00",
        	"output_lra" : "5.00",
        	"output_thresh" : "-25.69",
        	"normalization_type" : "dynamic",
        	"target_offset" : "1.02"
        }
        """;

    [Fact]
    public void Parses_what_the_measuring_pass_prints()
    {
        LoudnessMeasurement? m = LoudnessNormalizer.ParseMeasurement(RealStderr);

        m.Should().Be(new LoudnessMeasurement(-22.78, -3.76, 8.10, -33.61, 1.02));
    }

    [Fact]
    public void Silence_measures_as_nothing_to_normalize()
    {
        LoudnessNormalizer.ParseMeasurement(RealStderr.Replace("\"-22.78\"", "\"-inf\"")).Should().BeNull();
        LoudnessNormalizer.ParseMeasurement("no json here").Should().BeNull();
        LoudnessNormalizer.ParseMeasurement("").Should().BeNull();
    }

    [Fact]
    public void The_applying_pass_copies_the_video_and_feeds_back_every_measured_value()
    {
        var target = LoudnessNormalizer.Target(ProgramLoudness.Social)!.Value;
        var m = new LoudnessMeasurement(-22.78, -3.76, 8.10, -33.61, 1.02);

        IReadOnlyList<string> args = LoudnessNormalizer.ApplyArgs("in.mp4", "out.mp4", target, m);

        args.Should().ContainInConsecutiveOrder("-c:v", "copy");
        args.Should().Contain(
            "loudnorm=I=-14:TP=-1.5:LRA=11:measured_I=-22.78:measured_TP=-3.76:measured_LRA=8.1:measured_thresh=-33.61:offset=1.02:linear=true:print_format=summary" +
            ",alimiter=limit=0.794:level=false:latency=true");
        args[^1].Should().Be("out.mp4");
    }

    [Fact]
    public void Off_has_no_target_and_each_platform_its_own()
    {
        LoudnessNormalizer.Target(ProgramLoudness.Off).Should().BeNull();
        LoudnessNormalizer.Target(ProgramLoudness.Social)!.Value.Lufs.Should().Be(-14);
        LoudnessNormalizer.Target(ProgramLoudness.Web)!.Value.Lufs.Should().Be(-16);
        LoudnessNormalizer.Target(ProgramLoudness.Broadcast)!.Value.Lufs.Should().Be(-23);
    }

    [Fact]
    public void The_final_measurement_replaces_the_verify_pass_figures_after_a_peak_redo()
    {
        var node = new JsonObject { ["outputTruePeak"] = 0.4, ["outputLufs"] = -13.2 };

        LoudnessNormalizer.RecordFinalMeasurement(node, new LoudnessMeasurement(-14.04, -1.46, 7.0, -24.0, 0.0));

        node["outputLufs"]!.GetValue<double>().Should().Be(-14.0);
        node["outputTruePeak"]!.GetValue<double>().Should().Be(-1.5);
        LoudnessNormalizer.SkippedAudioCopyWarning.Should().Be("loudness_skipped_audio_copy");
    }
}
