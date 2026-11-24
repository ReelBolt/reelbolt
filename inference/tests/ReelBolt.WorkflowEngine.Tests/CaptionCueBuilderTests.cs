using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Pure tests for <see cref="CaptionCueBuilder"/> and <see cref="CaptionFilterBuilder"/> (see
/// docs/voiceover.md "Captions"): cues are cut from the SANITIZED text and the measured word
/// timings, never from anything the model wrote; styles that need words degrade per line with a
/// reason; and text never reaches the filter string.
/// </summary>
public class CaptionCueBuilderTests
{
    private static CaptionSourceLine Line(string id, string text, double start, double duration, params (string Text, double S, double E)[] words) =>
        new(id, text, start, duration, words.Length == 0 ? null : words.Select(w => new CaptionSourceWord(w.Text, w.S, w.E)).ToList());

    [Fact]
    public void Subtitle_with_words_times_each_chunk_from_its_first_word_to_its_last_word_on_the_output_timeline()
    {
        CaptionSourceLine line = Line("vo0", "deploys go from forty minutes to four", 10.0, 3.0,
            ("deploys", 0.0, 0.4), ("go", 0.4, 0.6), ("from", 0.6, 0.8), ("forty", 0.8, 1.2),
            ("minutes", 1.2, 1.7), ("to", 1.7, 1.9), ("four", 1.9, 2.4));

        CaptionCueBuildResult result = CaptionCueBuilder.Build(CaptionStyle.Subtitle, [line], maxCharsPerCue: 22, maxCues: 400);

        result.Dropped.Should().BeEmpty();
        result.Cues.Should().HaveCount(2);
        result.Cues[0].Text.Should().Be("deploys go from forty");
        result.Cues[0].OutputStartSec.Should().Be(10.0);
        result.Cues[0].OutputEndSec.Should().Be(11.2, "the chunk's last word ends 1.2 s into the line");
        result.Cues[1].Text.Should().Be("minutes to four");
        result.Cues[1].OutputStartSec.Should().Be(11.2);
        // The last word ends at 2.4 of a 3.0 s line — more than half a second before the audio
        // stops, so the cue ends with the word rather than lingering over silence.
        result.Cues[1].OutputEndSec.Should().Be(12.4);
        result.Cues.Select(c => c.CueIndex).Should().Equal(0, 1);
    }

    [Fact]
    public void Bold_shows_at_most_two_words_a_cue_with_or_without_word_timings()
    {
        CaptionSourceLine timed = Line("vo0", "deploys go from forty minutes to four", 10.0, 3.0,
            ("deploys", 0.0, 0.4), ("go", 0.4, 0.6), ("from", 0.6, 0.8), ("forty", 0.8, 1.2),
            ("minutes", 1.2, 1.7), ("to", 1.7, 1.9), ("four", 1.9, 2.4));
        CaptionSourceLine untimed = Line("vo1", "this tool just edited my whole reel", 20.0, 2.8);

        CaptionCueBuildResult result = CaptionCueBuilder.Build(CaptionStyle.Bold, [timed, untimed], maxCharsPerCue: 42, maxCues: 400);

        result.Dropped.Should().BeEmpty("Bold, like Subtitle, shares an unaligned line by length instead of dropping it");
        result.Cues.Should().OnlyContain(c => c.Text.Split(' ').Length <= 2 && c.Text.Length <= 12);
        result.Cues.Should().OnlyContain(c => c.HighlightPrefix == null, "no karaoke highlight");
        result.Cues.Where(c => c.LineId == "vo0").Select(c => c.Text)
            .Should().Equal("deploys go", "from forty", "minutes to", "four");
        result.Cues.First(c => c.LineId == "vo0").OutputEndSec.Should().Be(10.6);
    }

    [Fact]
    public void Bold_is_big_unboxed_poppins_above_the_platform_buttons_on_a_phone()
    {
        CaptionLayout portrait = CaptionLayout.Resolve(1080, 1920, CaptionStyle.Bold, 0, CaptionPosition.Auto);
        portrait.FontSizePx.Should().Be(151, "14% of the 1080 px short side — twice Subtitle's 6.5%");
        portrait.Position.Should().Be(CaptionPosition.Bottom);
        portrait.MarginV.Should().Be(538, "28% of 1920: above the caption, handle and buttons Reels and TikTok draw");
        CaptionFontCatalog.ForStyle(CaptionStyle.Bold).Should().Be(CaptionFont.Poppins);

        string script = AssSubtitleBuilder.Build(
            [new ResolvedCaptionCue("vo0", 0, "this tool just", 0, 1)], CaptionStyle.Bold, portrait,
            CaptionFontCatalog.Get(CaptionFont.Poppins), CaptionColor.OpaqueBlack with { R = 255, G = 255, B = 255 },
            CaptionColor.HalfBlack, CaptionColor.OpaqueBlack);
        string style = script.Split('\n').Single(l => l.StartsWith("Style:", StringComparison.Ordinal));
        style.Split(',')[15].Should().Be("1", "BorderStyle 1 — outline and shadow, never the opaque box");
    }

    [Fact]
    public void Subtitle_without_words_divides_the_line_duration_proportionally_by_characters_and_never_drops_the_line()
    {
        CaptionSourceLine line = Line("vo0", "one two three four", 5.0, 4.0);

        CaptionCueBuildResult result = CaptionCueBuilder.Build(CaptionStyle.Subtitle, [line], maxCharsPerCue: 9, maxCues: 400);

        result.Dropped.Should().BeEmpty("Subtitle works from line timing alone");
        result.Cues.Should().HaveCount(2);
        result.Cues[0].OutputStartSec.Should().Be(5.0);
        result.Cues[1].OutputEndSec.Should().Be(9.0);
        result.Cues[0].OutputEndSec.Should().Be(result.Cues[1].OutputStartSec);
    }

    [Fact]
    public void Karaoke_emits_one_cue_per_word_with_a_growing_highlight_prefix_and_needs_words()
    {
        CaptionSourceLine aligned = Line("vo0", "ship it today", 0.0, 1.5,
            ("ship", 0.0, 0.4), ("it", 0.4, 0.7), ("today", 0.7, 1.4));
        CaptionSourceLine unaligned = Line("vo1", "no words here", 2.0, 1.0);

        CaptionCueBuildResult result = CaptionCueBuilder.Build(CaptionStyle.Karaoke, [aligned, unaligned], 42, 400);

        result.Dropped.Should().Equal(("vo1", CaptionCueBuilder.NoWordAlignmentReason));
        result.Cues.Should().HaveCount(3);
        result.Cues.Select(c => c.Text).Should().OnlyContain(t => t == "ship it today");
        result.Cues.Select(c => c.HighlightPrefix).Should().Equal("ship", "ship it", "ship it today");
        result.Cues.Select(c => c.OutputStartSec).Should().Equal(0.0, 0.4, 0.7);
        // Each cue holds until the next word begins, so the highlight never goes dark between words.
        result.Cues[0].OutputEndSec.Should().Be(0.4);
        result.Cues[1].OutputEndSec.Should().Be(0.7);
    }

    [Fact]
    public void Punch_emits_one_word_per_cue_and_maps_tokens_onto_a_different_recognised_word_count_monotonically()
    {
        // Five text tokens, three recognised words: the recogniser merged "forty minutes" and
        // dropped nothing of consequence. Every token still gets a window, in order.
        CaptionSourceLine line = Line("vo0", "go from forty minutes to", 1.0, 2.0,
            ("go", 0.0, 0.3), ("from", 0.3, 0.6), ("fortyminutes", 0.6, 1.8));

        CaptionCueBuildResult result = CaptionCueBuilder.Build(CaptionStyle.Punch, [line], 42, 400);

        result.Cues.Should().HaveCount(5);
        result.Cues.Select(c => c.Text).Should().Equal("go", "from", "forty", "minutes", "to");
        result.Cues.Select(c => c.OutputStartSec).Should().BeInAscendingOrder();
        result.Cues.Should().OnlyContain(c => c.OutputEndSec > c.OutputStartSec);
        result.Cues.Should().OnlyContain(c => c.OutputStartSec >= 1.0 && c.OutputEndSec <= 3.0);
    }

    [Fact]
    public void The_cue_cap_truncates_with_a_recorded_reason_instead_of_dropping_everything()
    {
        CaptionSourceLine a = Line("vo0", "a b c d", 0.0, 1.0);
        CaptionSourceLine b = Line("vo1", "e f g h", 2.0, 1.0);

        CaptionCueBuildResult result = CaptionCueBuilder.Build(CaptionStyle.Subtitle, [a, b], maxCharsPerCue: 12, maxCues: 1);

        result.Cues.Should().HaveCount(1);
        result.Truncated.Should().BeTrue();
        result.Dropped.Should().Contain(d => d.Reason == CaptionCueBuilder.MaxCuesExceededReason);
    }

    [Fact]
    public void Empty_text_lines_are_dropped_with_a_reason_and_never_become_a_cue()
    {
        CaptionCueBuildResult result = CaptionCueBuilder.Build(CaptionStyle.Subtitle, [Line("vo0", "   ", 0, 1)], 42, 400);

        result.Cues.Should().BeEmpty();
        result.Dropped.Should().Equal(("vo0", CaptionCueBuilder.EmptyTextReason));
    }

    [Fact]
    public void Filter_chain_references_text_only_through_textfile_paths_with_expansion_none_and_half_open_enable_windows()
    {
        var cues = new List<ResolvedCaptionCue>
        {
            new("vo0", 0, "deploys go from", 10.0, 10.8),
            new("vo0", 1, "forty minutes to four", 10.8, 13.0)
        };

        string chain = CaptionFilterBuilder.BuildFilterChain(
            "[vcap]", cues, 1920, 1080, CaptionStyle.Subtitle, fontSizePct: 4, fontColor: "white",
            boxColor: "black@0.6", highlightColor: "yellow", fontFilePath: "/fonts/DejaVuSans.ttf",
            textFilePathForSlot: slot => $"/scratch/cap-{slot}.txt");

        chain.Should().StartWith("[vcap]drawbox=");
        chain.Should().EndWith("[vout]");
        chain.Should().NotContain("deploys").And.NotContain("forty", "cue text must never be interpolated into the filter string");
        chain.Should().Contain("textfile='/scratch/cap-0.txt':expansion=none");
        chain.Should().Contain("textfile='/scratch/cap-2.txt':expansion=none");
        chain.Should().Contain("enable='gte(t,10)*lt(t,10.8)'");
        chain.Should().Contain("enable='gte(t,10.8)*lt(t,13)'");
        chain.Should().Contain("fontsize=43", "4% of 1080 rounds to 43");
        chain.Should().NotContain(":text=", "only textfile= is ever used");
    }

    [Fact]
    public void Karaoke_chain_draws_the_prefix_left_anchored_at_the_same_x_in_the_highlight_colour()
    {
        var cues = new List<ResolvedCaptionCue>
        {
            new("vo0", 0, "ship it today", 0.0, 0.4, "ship"),
            new("vo0", 1, "ship it today", 0.4, 0.7, "ship it")
        };

        string chain = CaptionFilterBuilder.BuildFilterChain(
            "[vcap]", cues, 1920, 1080, CaptionStyle.Karaoke, 4, "white", "none", "yellow", "/f.ttf",
            slot => $"/s/cap-{slot}.txt");

        chain.Should().NotContain("drawbox", "a box colour of none skips the box");
        chain.Should().Contain("textfile='/s/cap-0.txt'").And.Contain("textfile='/s/cap-1.txt'", "slot 1 is cue 0's highlight prefix");
        chain.Should().Contain("fontcolor=yellow");
        // Both the base text and its prefix are drawn at the fixed left x, so the prefix lands exactly
        // over the words it repeats.
        chain.Should().Contain("fontcolor=white:x=176:").And.Contain("fontcolor=yellow:x=176:");
        chain.Should().EndWith("[vout]");
    }

    [Fact]
    public void Punch_doubles_the_font_percentage_within_the_same_clamp()
    {
        CaptionFilterBuilder.ComputeFontSize(1080, 4, CaptionStyle.Punch).Should().Be(86);
        CaptionFilterBuilder.ComputeFontSize(1080, 10, CaptionStyle.Punch).Should().Be(130, "clamped at 12%");
        CaptionFilterBuilder.ComputeFontSize(1080, 4, CaptionStyle.Subtitle).Should().Be(43);
    }
}
