using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>Pure tests for the transcript caption source: line cutting, translation anchors, reading-comfort fit and SRT/VTT text.</summary>
public class TranscriptCaptionsTests
{
    private static VideoAnalysisSegment Seg(string id, double start, double end, string text, int source = 0) => new(id, null, start, end, text, source);

    private static IReadOnlyList<TranscriptCaptionLines.Portion> Whole(VideoAnalysisSegment s, double shift = 0) =>
        [new(s.StartSec, s.EndSec, s.StartSec - shift, s.EndSec - shift)];

    private static IReadOnlyList<TranscriptCaptionLines.Portion> Identity(VideoAnalysisSegment s) => Whole(s);

    private static string Clean(string text) => text;

    [Fact]
    public void Lines_keep_the_segment_id_and_the_output_timing()
    {
        VideoAnalysisSegment[] segments = [Seg("t0", 1, 3, "Hello there."), Seg("t1", 4, 6.5, "Second line.")];

        TranscriptCaptionLinesResult result = TranscriptCaptionLines.Build(segments, [], s => Whole(s, 1), translations: null, Clean);

        result.Lines.Select(l => (l.LineId, l.OutputStartSec, l.PlayDurationSec)).Should().Equal(("t0", 0.0, 2.0), ("t1", 3.0, 2.5));
        result.TranslatedCount.Should().Be(0);
        result.UntranslatedCount.Should().Be(0, "no translation was asked for");
    }

    [Fact]
    public void A_segment_the_cut_removed_gets_no_caption()
    {
        VideoAnalysisSegment[] segments = [Seg("t0", 1, 3, "Kept."), Seg("t1", 4, 6, "Cut away.")];

        TranscriptCaptionLinesResult result = TranscriptCaptionLines.Build(segments, [], s => s.Id == "t1" ? [] : Whole(s), null, Clean);

        result.Lines.Should().ContainSingle().Which.LineId.Should().Be("t0");
        result.CutAwayCount.Should().Be(1);
    }

    [Fact]
    public void A_translation_replaces_the_text_and_keeps_anchor_and_timing()
    {
        VideoAnalysisSegment[] segments = [Seg("t0", 1, 3, "Hello there."), Seg("t1", 4, 6, "Second line.")];
        Dictionary<string, string> translations = new() { ["t0"] = "Ciao a tutti.", ["t1"] = "   " };

        TranscriptCaptionLinesResult result = TranscriptCaptionLines.Build(segments, [], Identity, translations, Clean);

        result.Lines.Select(l => (l.LineId, l.Text, l.OutputStartSec, l.PlayDurationSec))
            .Should().Equal(("t0", "Ciao a tutti.", 1.0, 2.0), ("t1", "Second line.", 4.0, 2.0));
        result.TranslatedCount.Should().Be(1);
        result.UntranslatedCount.Should().Be(1, "a blank translation falls back to the original words");
        result.Lines[0].Words!.Last().EndSec.Should().BeApproximately(2.0, 1e-9);
    }

    [Fact]
    public void A_long_translated_segment_is_kept_whole_not_truncated()
    {
        string longText = string.Join(' ', Enumerable.Repeat("parola", 60));
        VideoAnalysisSegment[] segments = [Seg("t0", 0, 30, "x")];

        TranscriptCaptionLinesResult result = TranscriptCaptionLines.Build(segments, [], Identity, new Dictionary<string, string> { ["t0"] = longText }, Clean);

        result.Lines.Single().Text.Should().Be(longText);
        CaptionCueBuildResult cues = CaptionCueBuilder.Build(CaptionStyle.Subtitle, result.Lines, SubtitleCueFit.MaxCharsPerLine, 5000);
        string.Join(" ", cues.Cues.Select(c => c.Text)).Should().Be(longText, "long text becomes more cues, never fewer words");
    }

    [Fact]
    public void Recognised_words_are_used_when_the_segment_survived_whole()
    {
        VideoAnalysisSegment segment = Seg("t0", 10, 12, "one two");
        VideoAnalysisWord[] words = [new("w0", 10, 10.8, "one"), new("w1", 11, 12, "two")];

        CaptionSourceLine whole = TranscriptCaptionLines.Build([segment], words, Identity, null, Clean).Lines.Single();

        whole.Words!.Select(w => (w.Text, Math.Round(w.StartSec, 6), Math.Round(w.EndSec, 6))).Should().Equal(("one", 0.0, 0.8), ("two", 1.0, 2.0));
    }

    [Fact]
    public void A_cut_through_a_segment_captions_each_kept_piece_with_only_its_own_words()
    {
        VideoAnalysisSegment segment = Seg("t0", 10, 14, "alpha beta gamma delta");
        VideoAnalysisWord[] words = [new("w0", 10, 11, "alpha"), new("w1", 11, 12, "beta"), new("w2", 12, 13, "gamma"), new("w3", 13, 14, "delta")];
        // The middle two seconds (11 to 13) are cut out: alpha stays at 0 to 1, delta lands at 1 to 2.
        IReadOnlyList<TranscriptCaptionLines.Portion> portions =
            [new(10, 11, 0, 1), new(13, 14, 1, 2)];

        IReadOnlyList<CaptionSourceLine> lines = TranscriptCaptionLines.Build([segment], words, _ => portions, null, Clean).Lines;

        lines.Select(l => (l.LineId, l.Text, l.OutputStartSec, l.PlayDurationSec)).Should().Equal(("t0", "alpha", 0.0, 1.0), ("t0~2", "delta", 1.0, 1.0));
    }

    [Fact]
    public void A_cut_through_a_translated_segment_shares_the_translation_out_by_the_kept_share()
    {
        VideoAnalysisSegment segment = Seg("t0", 0, 4, "a b c d");
        IReadOnlyList<TranscriptCaptionLines.Portion> portions = [new(0, 2, 0, 2)];

        CaptionSourceLine line = TranscriptCaptionLines.Build([segment], [], _ => portions, new Dictionary<string, string> { ["t0"] = "uno due tre quattro" }, Clean).Lines.Single();

        line.Text.Should().Be("uno due", "only the first half of the segment is in the output");
    }

    [Fact]
    public void Translation_parse_keeps_only_known_anchors_with_text()
    {
        const string output = "```json\n{\"lines\":[{\"anchorId\":\"t0\",\"text\":\"Ciao\"},{\"anchorId\":\"t7\",\"text\":\"x\"},{\"anchorId\":\"t1\",\"text\":\"\"},{\"anchorId\":\"t0\",\"text\":\"dup\"}],\"planRationale\":\"it\"}\n```";

        var (map, error) = CaptionTranslations.Parse(output, new HashSet<string> { "t0", "t1" });

        error.Should().BeNull();
        map.Should().BeEquivalentTo(new Dictionary<string, string> { ["t0"] = "Ciao" });
        CaptionTranslations.Parse("nope", new HashSet<string>()).Error.Should().Be("translation_output_invalid");
        CaptionTranslations.Parse("", new HashSet<string>()).Error.Should().Be("translation_output_empty");
    }

    [Fact]
    public void Long_text_is_wrapped_into_cues_at_word_boundaries_within_the_line_limit()
    {
        CaptionSourceLine line = new("t0", "This is a long sentence that has to be split into several readable subtitle cues", 0, 8, null);

        CaptionCueBuildResult built = CaptionCueBuilder.Build(CaptionStyle.Subtitle, [line], SubtitleCueFit.MaxCharsPerLine, 5000);

        built.Cues.Should().HaveCountGreaterThan(1);
        built.Cues.Should().OnlyContain(c => c.Text.Length <= SubtitleCueFit.MaxCharsPerLine);
        string.Join(" ", built.Cues.Select(c => c.Text)).Should().Be(line.Text);
        built.Cues[0].OutputStartSec.Should().Be(0);
        built.Cues[^1].OutputEndSec.Should().BeApproximately(8, 1e-9);
    }

    [Fact]
    public void Fit_holds_a_fast_cue_for_reading_time_but_never_into_the_next_cue()
    {
        ResolvedCaptionCue fast = new("a", 0, new string('x', 40), 0.0, 0.5);
        ResolvedCaptionCue next = new("b", 1, "next", 1.0, 2.0);

        IReadOnlyList<ResolvedCaptionCue> fitted = SubtitleCueFit.Apply([fast, next]);

        fitted[0].OutputEndSec.Should().BeApproximately(1.0 - SubtitleCueFit.GapSec, 1e-9, "wants 2 s to read 40 characters but the next cue starts at 1 s");
        fitted[1].OutputStartSec.Should().Be(1.0);
        fitted[1].OutputEndSec.Should().BeApproximately(2.0, 1e-9, "already long enough");
    }

    [Fact]
    public void Fit_trims_an_overlap_and_caps_a_very_long_cue()
    {
        ResolvedCaptionCue overlapping = new("a", 0, "first", 0.0, 3.0);
        ResolvedCaptionCue second = new("b", 1, "second", 2.0, 20.0);

        IReadOnlyList<ResolvedCaptionCue> fitted = SubtitleCueFit.Apply([overlapping, second]);

        fitted[0].OutputEndSec.Should().Be(2.0);
        fitted[1].OutputEndSec.Should().Be(2.0 + SubtitleCueFit.MaxCueSec);
    }

    [Fact]
    public void Srt_and_Vtt_are_written_exactly()
    {
        ResolvedCaptionCue[] cues =
        [
            new("a", 0, "Tom & Jerry", 3661.0504, 3662.5),
            new("b", 1, "Line one\nline  two --> <b>x</b>", 3663, 3664.999)
        ];

        SubtitleFiles.Srt(cues).Should().Be(
            "1\r\n01:01:01,050 --> 01:01:02,500\r\nTom & Jerry\r\n\r\n" +
            "2\r\n01:01:03,000 --> 01:01:04,999\r\nLine one line two -> <b>x</b>\r\n\r\n");
        SubtitleFiles.Vtt(cues).Should().Be(
            "WEBVTT\n\n01:01:01.050 --> 01:01:02.500\nTom &amp; Jerry\n\n" +
            "01:01:03.000 --> 01:01:04.999\nLine one line two -&gt; &lt;b&gt;x&lt;/b&gt;\n\n");
    }

    [Fact]
    public void An_empty_cue_list_writes_a_header_only_vtt_and_an_empty_srt()
    {
        SubtitleFiles.Srt([]).Should().BeEmpty();
        SubtitleFiles.Vtt([]).Should().Be("WEBVTT\n\n");
    }
}
