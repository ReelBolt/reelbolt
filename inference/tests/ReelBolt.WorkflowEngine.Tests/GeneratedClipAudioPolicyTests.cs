using System.Collections.Generic;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <c>VideoCompileStepConfig.GeneratedClipAudio</c> (docs/video-editing.md "Generated clips (b-roll)"):
/// how a generated plate's own sound is treated, and the per-clip report of it.
/// </summary>
public class GeneratedClipAudioPolicyTests
{
    [Theory]
    [InlineData(GeneratedAudioMode.Auto, true, "dialogue")]
    [InlineData(GeneratedAudioMode.Keep, true, "ambience")]
    [InlineData(GeneratedAudioMode.Mute, true, "muted")]
    [InlineData(GeneratedAudioMode.Auto, false, "muted")]
    [InlineData(GeneratedAudioMode.Keep, false, "muted")]
    public void Treatment_names_how_the_clips_sound_is_used(GeneratedAudioMode mode, bool hasAudio, string expected)
    {
        GeneratedClipAudioPolicy.Treatment(mode, hasAudio).Should().Be(expected);
    }

    private static (Dictionary<int, bool> Audio, System.Func<int, bool> IsGenerated) Sources() =>
        (new Dictionary<int, bool> { [0] = true, [1] = true, [2] = false }, i => i is 1 or 2);

    [Fact]
    public void Auto_leaves_the_sources_untouched_and_reports_a_generated_plate_as_dialogue()
    {
        var (audio, isGenerated) = Sources();

        GeneratedPlateAudio result = GeneratedClipAudioPolicy.ApplyToSources(
            GeneratedAudioMode.Auto, [0, 1, 2], isGenerated, audio);

        audio.Should().BeEquivalentTo(new Dictionary<int, bool> { [0] = true, [1] = true, [2] = false });
        result.AmbienceSources.Should().BeEmpty();
        result.Node![0]!["sourceIndex"]!.GetValue<int>().Should().Be(1);
        result.Node[0]!["audio"]!.GetValue<string>().Should().Be("dialogue");
        result.Node.Count.Should().Be(1, "a plate with no audio stream and a non-generated source are not reported");
    }

    [Fact]
    public void Keep_marks_the_plate_ambience_and_keeps_its_audio()
    {
        var (audio, isGenerated) = Sources();

        GeneratedPlateAudio result = GeneratedClipAudioPolicy.ApplyToSources(
            GeneratedAudioMode.Keep, [0, 1], isGenerated, audio);

        audio[1].Should().BeTrue("ambience still plays");
        result.AmbienceSources.Should().BeEquivalentTo(new[] { 1 });
        result.Node![0]!["audio"]!.GetValue<string>().Should().Be("ambience");
    }

    [Fact]
    public void Mute_drops_only_the_generated_plates_audio()
    {
        var (audio, isGenerated) = Sources();

        GeneratedPlateAudio result = GeneratedClipAudioPolicy.ApplyToSources(
            GeneratedAudioMode.Mute, [0, 1], isGenerated, audio);

        audio[0].Should().BeTrue("the real footage keeps its sound");
        audio[1].Should().BeFalse();
        result.Node![0]!["audio"]!.GetValue<string>().Should().Be("muted");
    }

    [Fact]
    public void No_generated_source_means_no_node_and_no_change()
    {
        var audio = new Dictionary<int, bool> { [0] = true };

        GeneratedPlateAudio result = GeneratedClipAudioPolicy.ApplyToSources(
            GeneratedAudioMode.Mute, [0], _ => false, audio);

        result.Node.Should().BeNull();
        audio[0].Should().BeTrue();
    }

    [Fact]
    public void Ambience_is_not_dialogue_for_ducking_but_real_footage_still_is()
    {
        var audio = new Dictionary<int, bool> { [0] = true, [1] = true };
        var ambience = new HashSet<int> { 1 };

        GeneratedClipAudioPolicy.HasDuckableDialogue(true, [0, 1], audio, ambience, isMultiSource: true).Should().BeTrue();
        GeneratedClipAudioPolicy.HasDuckableDialogue(true, [1], audio, ambience, isMultiSource: true).Should().BeFalse();
        GeneratedClipAudioPolicy.HasDuckableDialogue(true, [1], audio, ambience, isMultiSource: false).Should().BeFalse();
    }

    [Fact]
    public void With_no_ambience_the_existing_answer_is_returned_untouched()
    {
        var audio = new Dictionary<int, bool> { [0] = false };

        GeneratedClipAudioPolicy.HasDuckableDialogue(false, [0], audio, new HashSet<int>(), isMultiSource: false).Should().BeFalse();
        GeneratedClipAudioPolicy.HasDuckableDialogue(true, [0], audio, new HashSet<int>(), isMultiSource: false).Should().BeTrue();
    }

    private static VideoCompileStepExecutor.GeneratedClipsPlan Plan(bool coldOpenAudio) => new(
        [new VideoCompileStepExecutor.ResolvedGeneratedClip("c1", "coldopen", "/x/c1.mp4", 3, coldOpenAudio)],
        [new VideoCompileStepExecutor.ResolvedGeneratedClip("c2", "endcard", "/x/c2.mp4", 2, true)],
        [new VideoCompileStepExecutor.ResolvedGeneratedClip("c3", "cutaway", "/x/c3.mp4", 1, true, "s1", 4, 5)],
        [], 5, new JsonObject { ["enabled"] = true });

    [Fact]
    public void Mute_silences_cold_open_and_end_card_audio_and_reports_each_clip()
    {
        var plan = GeneratedClipAudioPolicy.ApplyToPlacedClips(Plan(coldOpenAudio: true), GeneratedAudioMode.Mute);

        plan.ColdOpens[0].HasAudio.Should().BeFalse();
        plan.EndCards[0].HasAudio.Should().BeFalse();
        JsonArray clips = plan.Node!["clips"]!.AsArray();
        clips.Count.Should().Be(3);
        clips[0]!["clipId"]!.GetValue<string>().Should().Be("c1");
        clips[0]!["audio"]!.GetValue<string>().Should().Be("muted");
        clips[2]!["audio"]!.GetValue<string>().Should().Be("muted");
        clips[2]!["reason"]!.GetValue<string>().Should().Be("picture_only");
    }

    [Fact]
    public void Auto_keeps_the_clips_audio_and_reports_it_as_dialogue()
    {
        var plan = GeneratedClipAudioPolicy.ApplyToPlacedClips(Plan(coldOpenAudio: true), GeneratedAudioMode.Auto);

        plan.ColdOpens[0].HasAudio.Should().BeTrue();
        plan.Node!["clips"]![0]!["audio"]!.GetValue<string>().Should().Be("dialogue");
        plan.Node["clips"]![0]!["hasAudioStream"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Keep_reports_ambience_and_a_clip_without_a_stream_is_muted()
    {
        var plan = GeneratedClipAudioPolicy.ApplyToPlacedClips(Plan(coldOpenAudio: false), GeneratedAudioMode.Keep);

        plan.Node!["clips"]![0]!["audio"]!.GetValue<string>().Should().Be("muted");
        plan.Node["clips"]![0]!["hasAudioStream"]!.GetValue<bool>().Should().BeFalse();
        plan.Node["clips"]![1]!["audio"]!.GetValue<string>().Should().Be("ambience");
    }

    [Fact]
    public void Nothing_placed_or_no_node_returns_the_plan_unchanged()
    {
        GeneratedClipAudioPolicy.ApplyToPlacedClips(VideoCompileStepExecutor.GeneratedClipsPlan.None, GeneratedAudioMode.Mute)
            .Should().BeSameAs(VideoCompileStepExecutor.GeneratedClipsPlan.None);
    }
}
