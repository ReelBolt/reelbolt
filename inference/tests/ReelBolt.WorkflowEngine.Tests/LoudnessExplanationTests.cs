using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The one-sentence reason attached to <c>outputSummary.loudness</c> (docs/video-editing.md "Program loudness").</summary>
public class LoudnessExplanationTests
{
    private static JsonObject Applied(double measured, double gain, double target = -14, double tp = -1.5) => new()
    {
        ["target"] = "Social",
        ["applied"] = true,
        ["targetLufs"] = target,
        ["targetTruePeak"] = tp,
        ["measuredLufs"] = measured,
        ["gainDb"] = gain,
        // QA-P1-19 item 4: the sentence is conditional on the input peak, so the "limited" cases must
        // declare one above the ceiling. A test about the already-under case overrides this.
        ["measuredTruePeak"] = tp + 1.9
    };

    [Fact]
    public void A_raised_program_names_the_measurement_the_gain_the_target_and_the_peak_ceiling()
    {
        LoudnessExplanation.Build(Applied(-19.8, 5.8))
            .Should().Be("Measured -19.8 LUFS, raised by +5.8 dB to the Social target of -14 LUFS; true peak limited to -1.5 dBTP.");
    }

    [Fact]
    public void A_lowered_program_says_lowered()
    {
        LoudnessExplanation.Build(Applied(-9.2, -4.8))
            .Should().Be("Measured -9.2 LUFS, lowered by 4.8 dB to the Social target of -14 LUFS; true peak limited to -1.5 dBTP.");
    }

    [Fact]
    public void A_program_already_on_target_says_it_was_left_alone()
    {
        LoudnessExplanation.Build(Applied(-14.0, 0.0))
            .Should().Be("Measured -14.0 LUFS, already at the Social target of -14 LUFS; true peak limited to -1.5 dBTP.");
    }

    [Theory]
    [InlineData("no_audio_in_program", "the finished program has no audio")]
    [InlineData("audio_stream_copied", "the audio stream is copied, not re-encoded")]
    [InlineData("not_measurable", "the program's loudness could not be measured")]
    [InlineData("measure_failed", "the loudness measurement failed")]
    [InlineData("apply_failed", "applying the gain failed, so the mix is left as encoded")]
    [InlineData("normalize_failed", "setting the loudness failed, so the mix is left as encoded")]
    public void A_skipped_pass_says_why(string reason, string text)
    {
        var node = new JsonObject { ["target"] = "Web", ["applied"] = false, ["targetLufs"] = -16.0, ["reason"] = reason };

        LoudnessExplanation.Build(node).Should().Be($"The Web target of -16 LUFS was not applied: {text}.");
    }

    [Fact]
    public void Annotate_adds_the_explanation_and_keeps_every_existing_key()
    {
        JsonObject node = Applied(-19.8, 5.8);

        LoudnessExplanation.Annotate(node);

        node["explanation"]!.GetValue<string>().Should().StartWith("Measured -19.8 LUFS");
        node["gainDb"]!.GetValue<double>().Should().Be(5.8);
    }

    [Fact]
    public void An_off_target_node_gets_no_explanation()
    {
        var node = new JsonObject { ["target"] = "Off", ["applied"] = false };

        LoudnessExplanation.Annotate(node);

        node.ContainsKey("explanation").Should().BeFalse();
    }

    /// <summary>QA-P1-19 item 4: the sentence claimed the true peak was limited whatever the measurement
    /// said. A mix already under the ceiling was never limited, so the claim is false there.</summary>
    [Fact]
    public void A_program_already_under_the_ceiling_does_not_claim_it_was_limited()
    {
        JsonObject node = Applied(-19.8, 5.8);
        node["measuredTruePeak"] = -3.2;

        LoudnessExplanation.Annotate(node);

        string text = node["explanation"]!.GetValue<string>();
        text.Should().Contain("true peak already within -1.5 dBTP");
        text.Should().NotContain("limited");
    }

    [Fact]
    public void A_program_over_the_ceiling_names_the_limit()
    {
        JsonObject node = Applied(-19.8, 5.8);
        node["measuredTruePeak"] = 0.4;

        LoudnessExplanation.Annotate(node);

        node["explanation"]!.GetValue<string>().Should().Contain("true peak limited to -1.5 dBTP");
    }
}
