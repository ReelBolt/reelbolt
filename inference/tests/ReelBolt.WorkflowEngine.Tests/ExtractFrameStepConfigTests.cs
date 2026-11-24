using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>T-105: the config shape and save-time rules of the ExtractFrame step.</summary>
public sealed class ExtractFrameStepConfigTests
{
    private static ExtractFrameStepConfig Parse(string json) =>
        JsonSerializer.Deserialize<ExtractFrameStepConfig>(json, ExtractFrameStepConfig.JsonOptions)!;

    [Fact]
    public void Minimal_config_defaults_to_png_registered_and_last_or_time_per_at()
    {
        ExtractFrameStepConfig config = Parse("""{"source":{"kind":"StepOutput","stepOrder":3},"at":"Last"}""");

        config.At.Should().Be(FrameAt.Last);
        config.Format.Should().Be("png");
        config.RegisterProjectFile.Should().BeTrue();
        config.AtSec.Should().BeNull();
        config.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Time_requires_a_non_negative_AtSec()
    {
        Parse("""{"source":{"kind":"StepOutput","stepOrder":3},"at":"Time"}""").Validate()
            .Should().ContainSingle(e => e.Contains("AtSec"));
        Parse("""{"source":{"kind":"StepOutput","stepOrder":3},"at":"Time","atSec":-1}""").Validate()
            .Should().ContainSingle(e => e.Contains("AtSec"));
        Parse("""{"source":{"kind":"StepOutput","stepOrder":3},"at":"Time","atSec":2.5}""").Validate().Should().BeEmpty();
    }

    [Fact]
    public void AtSec_is_refused_with_First_or_Last()
    {
        Parse("""{"source":{"kind":"StepOutput","stepOrder":3},"at":"Last","atSec":2}""").Validate()
            .Should().ContainSingle(e => e.Contains("AtSec"));
    }

    [Theory]
    [InlineData("gif")]
    [InlineData("")]
    [InlineData("png; rm -rf")]
    public void Format_is_a_closed_set(string format)
    {
        Parse($$"""{"source":{"kind":"StepOutput","stepOrder":3},"at":"First","format":"{{format}}"}""").Validate()
            .Should().ContainSingle(e => e.Contains("Format"));
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("JPEG")]
    [InlineData("png")]
    public void Format_png_and_jpeg_are_accepted_case_insensitively(string format) =>
        Parse($$"""{"source":{"kind":"StepOutput","stepOrder":3},"at":"First","format":"{{format}}"}""").Validate()
            .Should().BeEmpty();

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(8193)]
    public void MaxLongSide_is_bounded(int side) =>
        Parse($$"""{"source":{"kind":"StepOutput","stepOrder":3},"at":"First","maxLongSide":{{side}}}""").Validate()
            .Should().ContainSingle(e => e.Contains("MaxLongSide"));

    [Theory]
    [InlineData("""{"x":0,"y":0,"w":1,"h":1}""", 0)]
    [InlineData("""{"x":0.25,"y":0.1,"w":0.5,"h":0.5}""", 0)]
    [InlineData("""{"x":-0.1,"y":0,"w":0.5,"h":0.5}""", 1)]
    [InlineData("""{"x":0.6,"y":0,"w":0.5,"h":0.5}""", 1)]
    [InlineData("""{"x":0,"y":0.5,"w":0.5,"h":0.6}""", 1)]
    [InlineData("""{"x":0,"y":0,"w":0,"h":0.5}""", 1)]
    [InlineData("""{"x":0,"y":0,"w":0.001,"h":0.5}""", 1)]
    public void Crop_must_be_a_non_degenerate_rectangle_inside_the_unit_square(string crop, int errors) =>
        Parse($$"""{"source":{"kind":"StepOutput","stepOrder":3},"at":"First","crop":{{crop}}}""").Validate()
            .Should().HaveCount(errors);

    [Fact]
    public void A_malformed_source_is_reported()
    {
        Parse("""{"source":{"kind":"StepResult"},"at":"First"}""").Validate()
            .Should().ContainSingle(e => e.Contains("StepResultId"));
    }

    [Fact]
    public void GeneratedClips_source_is_refused_because_it_names_many_videos()
    {
        Parse("""{"source":{"kind":"GeneratedClips","stepOrder":2},"at":"First"}""").Validate()
            .Should().ContainSingle(e => e.Contains("GeneratedClips"));
    }
}
