using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Pure tests for the dialogue gate (docs/voiceover.md "Pickups and dubs") and the two
/// intelligibility facts (docs/voiceover.md "Review facts").
/// </summary>
public class DialogueGateAndIntelligibilityTests
{
    [Fact]
    public void OverMusic_with_no_pickups_builds_no_gate_at_all()
    {
        var config = new VideoCompileStepConfig(1, new ExtractInputRef(ExtractInputSource.Previous), 1);

        VideoCompileStepExecutor.BuildDialogueGate(config, [(1.0, 2.0)], []).Should().BeNull(
            "the default mode must leave the audio chain byte-identical even when narration windows exist");
    }

    [Fact]
    public void ReplaceDialogue_mutes_the_narration_windows_and_DuckDialogue_attenuates_them()
    {
        var replace = new VideoCompileStepConfig(1, new ExtractInputRef(ExtractInputSource.Previous), 1, VoiceoverMode: VoiceoverMode.ReplaceDialogue);
        var duck = new VideoCompileStepConfig(1, new ExtractInputRef(ExtractInputSource.Previous), 1, VoiceoverMode: VoiceoverMode.DuckDialogue, VoiceoverDuckDb: -20);

        DialogueGate? replaceGate = VideoCompileStepExecutor.BuildDialogueGate(replace, [(1.0, 2.0)], []);
        DialogueGate? duckGate = VideoCompileStepExecutor.BuildDialogueGate(duck, [(1.0, 2.0)], []);

        replaceGate.Should().NotBeNull();
        replaceGate!.MuteWindows.Should().Equal((1.0, 2.0));
        replaceGate.DuckWindows.Should().BeEmpty();

        duckGate.Should().NotBeNull();
        duckGate!.DuckWindows.Should().Equal((1.0, 2.0));
        duckGate.DuckGainLinear.Should().BeApproximately(0.1, 1e-9, "-20 dB");
    }

    [Fact]
    public void Pickup_windows_are_always_muted_even_in_OverMusic()
    {
        var config = new VideoCompileStepConfig(1, new ExtractInputRef(ExtractInputSource.Previous), 1);

        DialogueGate? gate = VideoCompileStepExecutor.BuildDialogueGate(config, [], [(4.0, 5.5)]);

        gate.Should().NotBeNull("a pickup replaces the flubbed take by definition");
        gate!.MuteWindows.Should().Equal((4.0, 5.5));
    }

    [Fact]
    public void The_volume_expression_is_a_product_of_ramped_window_factors_over_merged_windows()
    {
        var gate = new DialogueGate(
            DuckWindows: [(1.0, 2.0), (1.5, 3.0)],
            MuteWindows: [(10.0, 11.0)],
            DuckGainLinear: 0.25,
            RampSec: 0.1,
            Mode: "DuckDialogue");

        string filter = gate.BuildVolumeFilter();

        filter.Should().StartWith("volume=eval=frame:volume='");
        // The two overlapping duck windows merge into one, so a shared frame is attenuated once.
        filter.Should().Contain("(1-(1-0.25)*min(1,max(0,(t-1)/0.1))*min(1,max(0,(3-t)/0.1)))");
        filter.Should().Contain("(1-(1-0)*min(1,max(0,(t-10)/0.1))*min(1,max(0,(11-t)/0.1)))");
        filter.Should().NotContain("(t-1.5)");
    }

    [Fact]
    public void A_zero_ramp_falls_back_to_hard_edged_half_open_windows()
    {
        var gate = new DialogueGate([], [(2.0, 4.0)], 0.5, RampSec: 0, Mode: "ReplaceDialogue");

        gate.BuildExpression().Should().Be("(1-(1-0)*gte(t,2)*lt(t,4))");
    }

    [Fact]
    public void Word_error_rate_is_levenshtein_over_normalized_tokens()
    {
        NarrationIntelligibilityFacts.WordErrorRate(["ship", "it", "today"], ["ship", "it", "today"]).Should().Be(0);
        NarrationIntelligibilityFacts.WordErrorRate(["ship", "it", "today"], ["ship", "today"]).Should().BeApproximately(1 / 3.0, 1e-9);
        NarrationIntelligibilityFacts.WordErrorRate(["a"], []).Should().Be(1);
        NarrationIntelligibilityFacts.Normalize("Deploys go — from \"forty\" minutes, to four!")
            .Should().Equal("deploys", "go", "from", "forty", "minutes", "to", "four");
    }

    [Fact]
    public void Wer_node_is_applicable_false_with_a_reason_when_no_line_has_words_and_measures_when_one_does()
    {
        JsonObject none = NarrationIntelligibilityFacts.BuildWerNode([("vo0", "ship it", null)]);
        none["applicable"]!.GetValue<bool>().Should().BeFalse();
        none["reason"]!.GetValue<string>().Should().Be("no_word_alignment");

        JsonObject empty = NarrationIntelligibilityFacts.BuildWerNode([]);
        empty["applicable"]!.GetValue<bool>().Should().BeFalse();
        empty["reason"]!.GetValue<string>().Should().Be("no_applied_lines");

        JsonObject measured = NarrationIntelligibilityFacts.BuildWerNode(
        [
            ("vo0", "ship it today", [new CaptionSourceWord("ship", 0, 0.3), new CaptionSourceWord("today", 0.5, 0.9)])
        ]);
        measured["applicable"]!.GetValue<bool>().Should().BeTrue();
        measured["maxWer"]!.GetValue<double>().Should().BeApproximately(0.333, 1e-3);
    }

    [Fact]
    public void Headroom_node_is_applicable_false_when_nothing_is_measurable_and_reports_line_minus_dialogue_otherwise()
    {
        JsonObject none = NarrationIntelligibilityFacts.BuildHeadroomNode([("vo0", null, -30.0)]);
        none["applicable"]!.GetValue<bool>().Should().BeFalse();
        none["reason"]!.GetValue<string>().Should().Be("not_measurable");

        JsonObject measured = NarrationIntelligibilityFacts.BuildHeadroomNode([("vo0", -18.0, -30.0), ("vo1", -24.0, -22.0)]);
        measured["applicable"]!.GetValue<bool>().Should().BeTrue();
        measured["minHeadroomDb"]!.GetValue<double>().Should().Be(-2.0);
        measured["meanHeadroomDb"]!.GetValue<double>().Should().Be(5.0);
    }

    [Fact]
    public void Mean_rms_of_a_real_tone_is_measured_and_garbage_bytes_are_not()
    {
        string tonePath = Path.Combine(Path.GetTempPath(), $"tone-{System.Guid.NewGuid():N}.wav");
        string junkPath = Path.Combine(Path.GetTempPath(), $"junk-{System.Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(tonePath, VideoCompileStepExecutorTests.TestToneWav());
            File.WriteAllBytes(junkPath, [0x00, 0x01, 0x02]);

            double? tone = NarrationIntelligibilityFacts.TryMeasureMeanRmsDbfs(tonePath);
            tone.Should().NotBeNull();
            // A sine at 0.25 full scale has RMS 0.25/sqrt(2) = -15.05 dBFS.
            tone!.Value.Should().BeApproximately(-15.05, 0.5);

            NarrationIntelligibilityFacts.TryMeasureMeanRmsDbfs(junkPath).Should().BeNull();
            NarrationIntelligibilityFacts.TryMeasureMeanRmsDbfs(Path.Combine(Path.GetTempPath(), "does-not-exist.wav")).Should().BeNull();
        }
        finally
        {
            File.Delete(tonePath);
            File.Delete(junkPath);
        }
    }

    [Theory]
    [InlineData("ReelBolt alpha is live.", "Real Bolt alpha is live.", 0.0)]   // observed: split + respelt brand
    [InlineData("ReelBolt edits your footage", "Reelbolt edits your footage", 0.0)]
    [InlineData("ReelBolt edits your footage", "Real boat edits your footage", 0.5)]  // a different word still counts: one substitution + one insertion
    [InlineData("cuts it to the beat", "cuts it to a beat", 0.2)]              // short words compare exactly
    public void A_recognisers_spelling_of_a_long_word_is_not_a_mispronunciation(string spoken, string heard, double expectedWer)
    {
        string[] reference = NarrationIntelligibilityFacts.Normalize(spoken);
        string[] hypothesis = NarrationIntelligibilityFacts.AlignSpelling(reference, NarrationIntelligibilityFacts.Normalize(heard));

        NarrationIntelligibilityFacts.WordErrorRate(reference, hypothesis).Should().BeApproximately(expectedWer, 1e-9);
    }
}
