using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Review;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Review;

/// <summary>The measured edit-quality facts and the score ceilings they put on a review.</summary>
public class ReviewEditFactsTests
{
    private static string Output(int cutsInside = 0, double dropped = 0, double target = 0, string? sfx = null, string? loudness = null) =>
        $$"""
        {"speechSafeCuts":{"cutsInsideWords":{{cutsInside}}},
         "speechCoverage":{"lastKeptSourceSec":170,"lastSpeechSourceSec":172,"droppedSpeechPct":{{dropped}},"targetLengthSec":{{target}}{{(sfx is null ? "" : ",\"sfx\":" + sfx)}}}
         {{(loudness is null ? "" : ",\"loudness\":" + loudness)}}}
        """;

    [Fact]
    public void Cuts_inside_words_cap_the_score_at_five()
    {
        ReviewLoopStepExecutor.ApplyDeterministicCaps(9, Output(cutsInside: 2)).Should().Be(5);
        ReviewLoopStepExecutor.ApplyDeterministicCaps(9, Output()).Should().Be(9);
    }

    [Fact]
    public void Dropped_speech_caps_at_six_only_without_a_target_length()
    {
        ReviewLoopStepExecutor.ApplyDeterministicCaps(9, Output(dropped: 60)).Should().Be(6);
        ReviewLoopStepExecutor.ApplyDeterministicCaps(9, Output(dropped: 60, target: 30)).Should().Be(9);
        ReviewLoopStepExecutor.ApplyDeterministicCaps(9, Output(dropped: 20)).Should().Be(9);
    }

    [Fact]
    public void Sfx_over_speech_or_all_inaudible_caps_at_six_and_zero_cues_is_fine()
    {
        ReviewLoopStepExecutor.ApplyDeterministicCaps(9, Output(sfx: """{"cues":2,"onEvent":2,"overSpeech":1,"minGainDb":-8}""")).Should().Be(6);
        ReviewLoopStepExecutor.ApplyDeterministicCaps(9, Output(sfx: """{"cues":2,"onEvent":2,"overSpeech":0,"minGainDb":-8}""")).Should().Be(9);
        const string inaudible = """
            {"speechCoverage":{"droppedSpeechPct":0,"sfx":{"cues":1,"onEvent":1,"overSpeech":0,"minGainDb":-40}},
             "sfx":{"cues":[{"gainDb":-40}]},"loudness":{"measuredLufs":-16}}
            """;
        ReviewLoopStepExecutor.ApplyDeterministicCaps(9, inaudible).Should().Be(6);
        ReviewEditFacts.Read(inaudible)!.SfxCues.Should().Be(1);
        ReviewEditFacts.Read(Output())!.SfxCues.Should().Be(0);
        ReviewEditFacts.Cap(ReviewEditFacts.Read(Output())).Should().BeNull();
    }

    [Fact]
    public void A_compile_without_the_nodes_is_not_capped_and_the_facts_are_measured_lines()
    {
        ReviewLoopStepExecutor.ApplyDeterministicCaps(9, """{"length":{}}""").Should().Be(9);
        ReviewLoopStepExecutor.ApplyDeterministicCaps(9, null).Should().Be(9);
        string joined = string.Join("\n", ReviewEditFacts.Lines(Output(cutsInside: 1, dropped: 40, sfx: """{"cues":3,"onEvent":3,"overSpeech":0,"minGainDb":-12}""")));
        joined.Should().Contain("cutsInsideWords 1").And.Contain("droppedSpeechPct 40").And.Contain("cues 3, onEvent 3, overSpeech 0, minGainDb -12");
    }

    [Fact]
    public void A_sfx_offering_analysis_has_its_own_cache_tag_only_when_the_option_is_effective()
    {
        string plain = DeterministicStepRevisions.For(StepType.VideoAnalyze, """{"offerSfxClips":false}""");
        DeterministicStepRevisions.For(StepType.VideoAnalyze, """{"offerSfxClips":true}""").Should().Contain("+sfx-worthy-events-1");
        DeterministicStepRevisions.For(StepType.VideoAnalyze, """{"offerSfxClips":true,"sfxWorthyEventsOnly":false}""").Should().NotContain("sfx-worthy-events");
        plain.Should().NotContain("sfx-worthy-events");
    }
}
