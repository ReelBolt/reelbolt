using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="TranscriptSpeechGate"/> (docs/video-editing.md "Transcripts from clips with no
/// speech"): transcript segments recognised from music or silence — the customer's "Thank you for
/// watching." on silent b-roll, which the story editor then cut to — are discarded, while real
/// speech, including a real "thank you", survives.
/// </summary>
public class TranscriptSpeechGateTests
{
    private static readonly (double, double)[] NoSilence = [];

    [Fact]
    public void The_lone_stock_phrase_on_a_b_roll_clip_with_no_confidence_figures_is_dropped()
    {
        TranscriptSpeechGate.Result r = TranscriptSpeechGate.Apply(
            [new TranscriptSegment("Thank you for watching.", 8.0, 10.0)],
            [new TranscriptWord("Thank", 8.0, 8.3), new TranscriptWord("watching.", 9.5, 10.0)],
            NoSilence, null, -34);

        r.Kept.Should().BeEmpty();
        r.KeptWords.Should().BeEmpty("the words belong to the dropped segment");
        r.Dropped.Should().ContainSingle().Which.Reason.Should().Be(TranscriptSpeechGate.StockPhraseReason);
    }

    [Fact]
    public void A_confidently_recognised_thank_you_in_real_dialogue_is_kept()
    {
        TranscriptSegment real = new("Thank you.", 3.0, 3.6, NoSpeechProb: 0.03, AvgLogprob: -0.2);
        TranscriptSpeechGate.Apply([new TranscriptSegment("We shipped it.", 0, 2), real], [], NoSilence, null, -34)
            .Kept.Should().HaveCount(2);

        // ...and without confidence figures, a stock phrase among other dialogue is kept too.
        TranscriptSpeechGate.Apply(
                [new TranscriptSegment("We shipped it.", 0, 2), new TranscriptSegment("Thank you.", 3.0, 3.6)], [], NoSilence, null, -34)
            .Dropped.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0.65, -1.4, TranscriptSpeechGate.NoSpeechReason)]
    [InlineData(0.65, null, TranscriptSpeechGate.NoSpeechReason)]
    [InlineData(0.95, -0.1, TranscriptSpeechGate.NoSpeechReason)]
    [InlineData(0.65, -0.4, null)]
    [InlineData(0.3, -1.5, null)]
    public void The_recognisers_own_no_speech_rule_applies(double noSpeech, double? logprob, string? expected) =>
        TranscriptSpeechGate.Judge(new TranscriptSegment("Some words here", 0, 2, noSpeech, logprob), 3, NoSilence, null, -34)
            .Should().Be(expected);

    [Fact]
    public void Looping_output_is_dropped_as_repetition() =>
        TranscriptSpeechGate.Judge(new TranscriptSegment("go go go go go go go go", 0, 4, CompressionRatio: 3.1), 2, NoSilence, null, -34)
            .Should().Be(TranscriptSpeechGate.RepetitionReason);

    [Fact]
    public void A_segment_over_detected_silence_or_below_the_silence_threshold_has_no_speech_energy()
    {
        TranscriptSegment seg = new("Some words here", 2.0, 4.0);
        TranscriptSpeechGate.Judge(seg, 3, [(1.5, 3.8)], null, -34)
            .Should().Be(TranscriptSpeechGate.NoSpeechEnergyReason, "90% of the window is silence");
        TranscriptSpeechGate.Judge(seg, 3, [(3.0, 3.5)], null, -34).Should().BeNull("only 25% is silence");

        WavRmsSampler.RmsWindow[] quiet = [new(2.0, 3.0, -48, -40), new(3.0, 4.0, -45, -38)];
        TranscriptSpeechGate.Judge(seg, 3, NoSilence, quiet, -34).Should().Be(TranscriptSpeechGate.NoSpeechEnergyReason);

        WavRmsSampler.RmsWindow[] speech = [new(2.0, 3.0, -48, -40), new(3.0, 4.0, -22, -8)];
        TranscriptSpeechGate.Judge(seg, 3, NoSilence, speech, -34).Should().BeNull("one window rises above the silence threshold");
    }

    [Fact]
    public void Words_inside_a_dropped_segment_go_with_it_and_kept_words_are_untouched()
    {
        TranscriptSegment kept = new("Real speech", 0, 2, NoSpeechProb: 0.02);
        TranscriptSegment ghost = new("you", 5, 6, NoSpeechProb: 0.97);
        TranscriptWord[] words = [new("Real", 0, 0.6), new("speech", 0.7, 1.9), new("you", 5.1, 5.8)];

        TranscriptSpeechGate.Result r = TranscriptSpeechGate.Apply([kept, ghost], words, NoSilence, null, -34);

        r.Kept.Should().Equal(kept);
        r.KeptWords.Select(w => w.Text).Should().Equal("Real", "speech");
        r.Dropped.Should().ContainSingle().Which.Reason.Should().Be(TranscriptSpeechGate.NoSpeechReason);
    }

    [Fact]
    public void Nothing_dropped_returns_the_inputs_unchanged()
    {
        TranscriptSegment[] segs = [new("Hello there", 0, 1)];
        TranscriptWord[] words = [new("Hello", 0, 0.5)];
        TranscriptSpeechGate.Result r = TranscriptSpeechGate.Apply(segs, words, NoSilence, null, -34);
        r.Kept.Should().BeSameAs(segs);
        r.KeptWords.Should().BeSameAs(words);
    }

    [Theory]
    [InlineData("  Thank you for watching!  ", "thank you for watching")]
    [InlineData("Subtitles by the Amara.org community", "subtitles by the amaraorg community")]
    [InlineData("Don't forget to subscribe.", "dont forget to subscribe")]
    public void Phrases_are_compared_normalised(string text, string expected) =>
        TranscriptSpeechGate.Normalize(text).Should().Be(expected);
}
