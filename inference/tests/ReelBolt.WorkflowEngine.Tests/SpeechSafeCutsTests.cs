using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Tests.Quality;
using Xunit;
using Sp = (double Start, double End, int SourceIndex, System.Collections.Generic.IReadOnlyList<string> OriginIds);

namespace ReelBolt.WorkflowEngine.Tests;

public class SpeechSafeCutsTests
{
    private static readonly IReadOnlyDictionary<int, double> Dur10 = new Dictionary<int, double> { [0] = 10 };

    private static Sp S(double a, double b, string id = "x", int src = 0) => (a, b, src, [id]);

    private static List<SpeechToken> Tokens(params (double, double)[] t) => t.Select(x => new SpeechToken(x.Item1, x.Item2, 0)).ToList();

    private static (List<Sp> Spans, SpeechSafeCutsReport Report) Run(
        Sp[] spans, List<SpeechToken> tokens, double min = 0.25, IReadOnlyList<SilenceInterval>? silences = null, ISet<int>? skip = null) =>
        SpeechSafeCuts.Apply(spans, tokens, silences, Dur10, skip, min);

    // Words every 100 ms with 20 ms between them: no pause anywhere near, so only the word rule acts.
    private static IEnumerable<(double, double)> Dense(double from, int count) =>
        Enumerable.Range(0, count).Select(i => (from + i * 0.1, from + i * 0.1 + 0.08));

    [Fact]
    public void A_start_inside_a_word_moves_to_the_word_start_minus_a_short_lead()
    {
        // previous word ends 5.98; the word starts 6.02: the lead is capped by the gap, never into the previous word
        var tight = Tokens(Dense(5.0, 10).Append((6.02, 6.6)).Concat(Dense(6.65, 15)).ToArray());
        var (spans, report) = Run([S(6.2, 9.9)], tight);
        spans[0].Start.Should().BeApproximately(5.98, 1e-6);
        report.CutsInsideWords.Should().Be(0);
        report.CutsInsideWordsBefore.Should().Be(1);

        // 200 ms of room: the lead is 120 ms
        var roomy = Tokens(Dense(5.0, 10).Append((6.18, 6.7)).Concat(Dense(6.75, 15)).ToArray());
        var (second, _) = Run([S(6.3, 8)], roomy);
        second[0].Start.Should().BeApproximately(6.06, 1e-6);
    }

    [Fact]
    public void A_start_much_closer_to_the_word_end_moves_to_the_gap_after_it()
    {
        var tokens = Tokens(Dense(5.0, 8).Append((5.98, 6.58)).Concat(Dense(6.63, 15)).ToArray());
        var (spans, _) = Run([S(6.55, 8)], tokens);
        spans[0].Start.Should().BeGreaterThanOrEqualTo(6.58);
        SpeechSafeCuts.CountInsideWords([(spans[0].Start, spans[0].End, 0)], tokens).Should().Be(0);
    }

    [Fact]
    public void An_end_inside_a_word_moves_to_the_word_end_plus_a_short_tail_never_into_the_next_word()
    {
        var roomy = Tokens(Dense(5.0, 8).Append((5.98, 6.5)).Concat(Dense(6.75, 15)).ToArray());
        var (spacious, _) = Run([S(1, 6.2)], roomy);
        spacious[0].End.Should().BeApproximately(6.65, 1e-6, "tail is 150 ms");

        var tight = Tokens(Dense(5.0, 8).Append((5.98, 6.5)).Concat(Dense(6.55, 15)).ToArray());
        var (capped, _) = Run([S(1, 6.2)], tight);
        capped[0].End.Should().BeApproximately(6.55, 1e-6, "capped by the next word's start");
    }

    [Fact]
    public void A_boundary_prefers_a_nearby_pause_over_a_mere_word_boundary()
    {
        // words every 100 ms (no pause), a 500 ms pause 3.0-3.5; the end lands between words at 2.7
        var words = Enumerable.Range(0, 10).Select(i => (2.0 + i * 0.1, 2.0 + i * 0.1 + 0.08)).ToArray();
        var tokens = Tokens(words.Concat([(3.5, 3.6)]).ToArray());
        var (spans, report) = Run([S(1, 2.74)], tokens);
        spans[0].End.Should().BeGreaterThan(2.9).And.BeLessThanOrEqualTo(3.5, "the cut sits in the pause");
        report.PausesChosen.Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_pause_further_than_the_max_shift_is_not_chosen()
    {
        var words = Enumerable.Range(0, 30).Select(i => (2.0 + i * 0.1, 2.0 + i * 0.1 + 0.08)).ToArray();
        var tokens = Tokens(words.Concat([(8.0, 8.1)]).ToArray());
        var (spans, _) = Run([S(1, 3.04)], tokens);
        spans[0].End.Should().BeLessThan(3.4, "the only pause is more than 1.2 s away; the cut stays at a word edge");
        SpeechSafeCuts.CountInsideWords([(spans[0].Start, spans[0].End, 0)], tokens).Should().Be(0);
    }

    [Fact]
    public void A_silence_span_counts_as_a_pause()
    {
        var tokens = Tokens(Dense(2.0, 10).Append((3.9, 4.0)).ToArray());
        var (spans, report) = Run([S(1, 2.95)], tokens, silences: [new SilenceInterval(3.0, 3.5, 0)]);
        spans[0].End.Should().BeInRange(2.98, 3.5, "the end was inside a word and the nearest pause is the silence, within 0.6 s");
        report.PausesChosen.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Spans_that_now_touch_are_merged_and_keep_both_origin_ids()
    {
        var tokens = Tokens((2.0, 2.6), (2.62, 3.0));
        var (spans, report) = Run([S(0.5, 2.3, "a"), S(2.4, 5, "b")], tokens);
        spans.Should().ContainSingle();
        spans[0].OriginIds.Should().BeEquivalentTo(["a", "b"]);
        report.SpansMerged.Should().Be(1);
    }

    [Fact]
    public void A_span_stays_inside_its_source_and_never_drops_below_the_minimum()
    {
        var (atEnd, _) = Run([S(8, 9.95)], Tokens((9.9, 10.0)));
        atEnd[0].End.Should().BeLessThanOrEqualTo(10);
        atEnd[0].Start.Should().BeGreaterThanOrEqualTo(0);

        // moving the start past the word would leave 100 ms; the word-safe start before the word keeps it long enough
        var (tiny, _) = Run([S(4.55, 4.9)], Tokens((4.0, 4.6), (4.95, 5.2)), min: 0.25);
        (tiny[0].End - tiny[0].Start).Should().BeGreaterThanOrEqualTo(0.25);
    }

    [Fact]
    public void Compositions_and_sources_without_tokens_are_untouched()
    {
        var tokens = new List<SpeechToken> { new(2.0, 2.6, 0), new(2.0, 2.6, 1) };
        var input = new[] { S(2.2, 5, "a", 0), S(2.2, 5, "b", 1), S(2.2, 5, "c", 2) };
        var (spans, _) = SpeechSafeCuts.Apply(input, tokens, null, new Dictionary<int, double> { [0] = 10, [1] = 10, [2] = 10 }, new HashSet<int> { 1 }, 0.25);
        spans[0].Start.Should().NotBe(2.2);
        spans[1].Should().Be(input[1], "a composition is skipped");
        spans[2].Should().Be(input[2], "no tokens for that source");
    }

    [Fact]
    public void The_pass_is_deterministic()
    {
        var head = new SyntheticTalkingHead();
        var tokens = head.Words.Select(w => new SpeechToken(w.Start, w.End, 0)).ToList();
        Sp[] input = head.KeptSpans.Select((s, i) => S(s.Start, s.End, "k" + i)).ToArray();
        var a = SpeechSafeCuts.Apply(input, tokens, null, new Dictionary<int, double> { [0] = head.DurationSec }, null, 0.25);
        var b = SpeechSafeCuts.Apply(input, tokens, null, new Dictionary<int, double> { [0] = head.DurationSec }, null, 0.25);
        a.Spans.Select(s => (s.Start, s.End)).Should().Equal(b.Spans.Select(s => (s.Start, s.End)));
        a.Report.Should().Be(b.Report);
    }

    [Fact]
    public void The_fixture_matches_its_description()
    {
        var head = new SyntheticTalkingHead();
        head.Words.Count.Should().BeInRange(500, 650);
        head.DurationSec.Should().BeApproximately(170, 1);
        head.Words[^1].End.Should().BeLessThan(head.DurationSec);
        head.Shots.Count.Should().Be(85);
        head.KeptRanges.Count.Should().Be(10);
    }

    [Fact]
    public void Ten_kept_spans_of_the_synthetic_recording_have_no_cut_inside_a_word_afterwards()
    {
        var head = new SyntheticTalkingHead();
        var tokens = head.Words.Select(w => (w.Start, w.End)).ToList();
        int before = FootageEditQuality.CutsInsideWords(head.KeptSpans, tokens);
        before.Should().BeGreaterThan(0, "shots ignore word boundaries");

        Sp[] input = head.KeptSpans.Select((s, i) => S(s.Start, s.End, "k" + i)).ToArray();
        var (spans, report) = SpeechSafeCuts.Apply(
            input, head.Words.Select(w => new SpeechToken(w.Start, w.End, 0)).ToList(), null,
            new Dictionary<int, double> { [0] = head.DurationSec }, null, 0.25);

        FootageEditQuality.CutsInsideWords(spans.Select(s => (s.Start, s.End)), tokens).Should().Be(0);
        report.CutsInsideWordsBefore.Should().Be(before);
        report.CutsInsideWords.Should().Be(0);
        report.AdjustedBoundaries.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task The_compile_cuts_no_word_when_snapping_is_on_and_reports_it()
    {
        var head = new SyntheticTalkingHead();
        var tokens = head.Words.Select(w => (w.Start, w.End)).ToList();

        (JsonObject onOutput, List<(double, double)> onSpans) = await CompileAsync(head, snap: true);
        (JsonObject offOutput, List<(double, double)> offSpans) = await CompileAsync(head, snap: false);

        int before = FootageEditQuality.CutsInsideWords(offSpans, tokens);
        before.Should().BeGreaterThan(0);
        offOutput.ContainsKey("speechSafeCuts").Should().BeFalse("the switch is off");

        JsonObject node = onOutput["speechSafeCuts"]!.AsObject();
        node["cutsInsideWords"]!.GetValue<int>().Should().Be(0);
        node["cutsInsideWordsBefore"]!.GetValue<int>().Should().BeGreaterThan(0);
        node["adjustedBoundaries"]!.GetValue<int>().Should().BeGreaterThan(0);
        FootageEditQuality.CutsInsideWords(onSpans, tokens).Should().Be(0, "the EDL carries the adjusted times");
    }

    private static async Task<(JsonObject Output, List<(double, double)> EdlSpans)> CompileAsync(SyntheticTalkingHead head, bool snap)
    {
        VideoAnalysisArtifact artifact = VideoCompileStepExecutorTests.BuildTalkingHeadArtifact(head);
        string decision = VideoCompileStepExecutorTests.BuildTalkingHeadDecision(head);
        return await VideoCompileStepExecutorTests.CompileTalkingHeadAsync(artifact, decision, snap);
    }

    [Fact]
    public void A_reordered_list_is_never_merged_or_reordered()
    {
        var tokens = Tokens(Dense(4.0, 10).Concat(Dense(0.0, 10)).ToArray());
        var (spans, _) = Run([S(4.85, 9.9, "a"), S(0, 4.85, "b")], tokens);
        spans.Should().HaveCount(2);
        spans[0].OriginIds.Should().Equal("a");
        spans[1].OriginIds.Should().Equal("b");
        spans[1].End.Should().BeLessThanOrEqualTo(spans[0].Start + 1e-6, "the second span may not replay what the first one starts with");
    }

    [Fact]
    public void A_cutaway_between_two_spans_of_one_source_never_plays_a_word_twice()
    {
        var tokens = Tokens(Dense(0.0, 100).ToArray());
        var (spans, report) = Run([S(0, 4.85, "a1"), S(0, 3, "x", src: 1), S(4.85, 9.9, "a2")], tokens);
        var a = spans.Where(s => s.SourceIndex == 0).ToList();
        a.Should().HaveCount(2);
        a[0].End.Should().BeLessThanOrEqualTo(a[1].Start + 1e-6);
        report.CutsInsideWords.Should().Be(0);
    }

    [Fact]
    public void Adjacent_spans_that_continue_forwards_still_merge()
    {
        var tokens = Tokens((2.0, 2.6), (2.62, 3.0));
        var (spans, _) = Run([S(0.5, 2.3, "a"), S(2.4, 5, "b")], tokens);
        spans.Should().ContainSingle();
    }

    [Fact]
    public void A_cut_already_clear_of_speech_is_not_moved_to_a_pause()
    {
        var tokens = Tokens(Dense(2.0, 10).Concat(Dense(5.0, 10)).ToArray());
        var (spans, report) = Run([S(1.0, 3.5, "a")], tokens);
        spans[0].Start.Should().Be(1.0);
        spans[0].End.Should().Be(3.5);
        report.AdjustedBoundaries.Should().Be(0);
    }

    [Fact]
    public void Growth_past_the_target_prefers_the_shortening_word_edges()
    {
        var tokens = Tokens(Dense(0.0, 100).ToArray());
        var spansIn = new[] { S(0.0, 4.85), S(5.03, 9.55) };
        double before = spansIn.Sum(x => x.End - x.Start);
        var (grown, _) = SpeechSafeCuts.Apply(spansIn, tokens, null, Dur10, null, 0.25);
        var (fitted, _) = SpeechSafeCuts.Apply(spansIn, tokens, null, Dur10, null, 0.25, targetSec: before);
        fitted.Sum(x => x.End - x.Start).Should().BeLessThanOrEqualTo(before + 0.35 + 1e-9);
        fitted.Sum(x => x.End - x.Start).Should().BeLessThanOrEqualTo(grown.Sum(x => x.End - x.Start) + 1e-9);
    }
}
