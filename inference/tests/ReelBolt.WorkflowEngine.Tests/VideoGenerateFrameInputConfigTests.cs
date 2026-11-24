using System;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>T-105: VideoGenerate FirstFrame / LastFrame (Inline mode) config shape and rules.</summary>
public sealed class VideoGenerateFrameInputConfigTests
{
    private static VideoGenerateStepConfig Parse(string extra) =>
        JsonSerializer.Deserialize<VideoGenerateStepConfig>(
            $$"""{"maxSpendCredits":10,"inlinePrompt":"a shot"{{extra}}}""", VideoGenerateStepConfig.JsonOptions)!;

    [Fact]
    public void A_config_without_frames_is_byte_compatible_and_valid()
    {
        VideoGenerateStepConfig config = Parse("");

        config.FirstFrame.Should().BeNull();
        config.LastFrame.Should().BeNull();
        config.Validate().Should().BeEmpty();
    }

    [Fact]
    public void FirstFrame_and_LastFrame_round_trip_as_extract_input_refs()
    {
        VideoGenerateStepConfig config = Parse(""","firstFrame":{"from":"Step","stepOrder":2},"lastFrame":{"from":"Previous"}""");

        config.FirstFrame.Should().Be(new ExtractInputRef(ExtractInputSource.Step, 2));
        config.LastFrame.Should().Be(new ExtractInputRef(ExtractInputSource.Previous));
        config.Validate().Should().BeEmpty();
    }

    [Fact]
    public void A_project_file_id_can_stand_in_for_a_step_reference()
    {
        Guid id = Guid.NewGuid();
        Parse($$""","firstFrameProjectFileId":"{{id}}" """).Validate().Should().BeEmpty();
    }

    [Fact]
    public void Frames_are_refused_unless_the_plan_source_is_Inline()
    {
        VideoGenerateStepConfig config = Parse(""","planSource":"StepRef","plan":{"from":"Previous"},"firstFrame":{"from":"Previous"}""");

        config.Validate().Should().ContainSingle(e => e.Contains("Inline"));
    }

    [Fact]
    public void A_frame_ref_must_be_Previous_or_Step_with_an_order()
    {
        Parse(""","firstFrame":{"from":"Accumulated"}""").Validate().Should().ContainSingle(e => e.Contains("FirstFrame"));
        Parse(""","firstFrame":{"from":"Step"}""").Validate().Should().ContainSingle(e => e.Contains("FirstFrame") && e.Contains("StepOrder"));
    }

    [Fact]
    public void A_frame_may_name_a_step_or_a_file_never_both_and_a_file_id_may_not_be_empty()
    {
        Guid id = Guid.NewGuid();
        Parse($$""","firstFrame":{"from":"Previous"},"firstFrameProjectFileId":"{{id}}" """).Validate()
            .Should().ContainSingle(e => e.Contains("FirstFrame") && e.Contains("both"));
        Parse($$""","firstFrameProjectFileId":"{{Guid.Empty}}" """).Validate()
            .Should().ContainSingle(e => e.Contains("FirstFrameProjectFileId"));
    }

    [Fact]
    public void A_last_frame_alone_is_refused_because_images_are_first_then_last()
    {
        Parse(""","lastFrame":{"from":"Previous"}""").Validate().Should().ContainSingle(e => e.Contains("LastFrame") && e.Contains("FirstFrame"));
    }
}
