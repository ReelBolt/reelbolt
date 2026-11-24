using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Pins <see cref="NarrationFitSolver"/>: the deterministic measurement of how far a narration line
/// overruns the window it was written for.
///
/// <para>
/// These are rule tests, not smoke tests. The solver is the ONLY place on the narration path where a
/// duration is computed at all — no model produces one — so every arithmetic rule it follows
/// (words-per-second, the rounding, the ceiling, the totals, what a missing window means) is pinned
/// here by value rather than left to a caller to rediscover. The solver is proven deterministic, not
/// assumed deterministic: calling it twice on the same input is asserted to return an equal report
/// AND byte-identical JSON.
/// </para>
/// </summary>
public class NarrationFitSolverTests
{
    /// <summary>
    /// The review loop's own serialization settings, mirroring the step's
    /// <c>OutputJsonOptions</c> (web defaults), so "the report serializes" is asserted under the same
    /// conventions a consumer actually reads it with.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static NarrationFitLine Line(
        string id,
        double playDurationSec,
        int wordCount,
        double? windowStartSec = null,
        double? windowEndSec = null,
        string? anchorId = null,
        int? alignedWordCount = null) =>
        new(id, anchorId, windowStartSec, windowEndSec, playDurationSec, wordCount, alignedWordCount);

    // =====================================================================
    // The core overflow verdict
    // =====================================================================

    [Fact]
    public void A_line_longer_than_its_window_overflows_in_both_seconds_and_words_and_does_not_fit()
    {
        // 10 words over 4.0 s = 2.5 words/s. The declared window is 3.0 s, so the line overruns it
        // by exactly 1.0 s — and 1.0 s at 2.5 words/s is 2.5 words, which is CEILED to 3. The
        // ceiling is the whole point: a line that overruns by half a word still overruns by a word,
        // and "0 words over" would read as a fit to the review loop.
        NarrationFitLineResult result = NarrationFitSolver.SolveLine(
            Line("vo0", playDurationSec: 4.0, wordCount: 10, windowStartSec: 0.0, windowEndSec: 3.0));

        result.Id.Should().Be("vo0");
        result.WindowSec.Should().Be(3.0);
        result.PlayDurationSec.Should().Be(4.0);
        result.WordCount.Should().Be(10);
        result.WordsPerSecond.Should().Be(2.5);
        result.OverflowSec.Should().Be(1.0);
        result.OverflowWords.Should().Be(3, "ceil(1.0 s * 2.5 words/s) = ceil(2.5) = 3, never 2");
        result.Fits.Should().BeFalse();
        result.WindowSource.Should().Be(NarrationFitSolver.WindowSourceDeclaredStarts);
    }

    [Fact]
    public void A_line_that_fits_its_window_reports_every_overflow_number_as_zero_and_fits()
    {
        // 6 words over 1.0 s inside a 2.0 s window: 1.0 s of headroom, so nothing is over. The rate
        // is still reported — it is a property of the line, not of the window.
        NarrationFitLineResult result = NarrationFitSolver.SolveLine(
            Line("vo0", playDurationSec: 1.0, wordCount: 6, windowStartSec: 3.0, windowEndSec: 5.0));

        result.WindowSec.Should().Be(2.0);
        result.WordsPerSecond.Should().Be(6.0);
        result.OverflowSec.Should().Be(0.0);
        result.OverflowWords.Should().Be(0);
        result.Fits.Should().BeTrue();
        result.WindowSource.Should().Be(NarrationFitSolver.WindowSourceDeclaredStarts);
    }

    [Fact]
    public void A_line_that_exactly_fills_its_window_fits()
    {
        // The boundary is `overflowsSec <= 0`, so playing exactly as long as the window allows is a
        // FIT — it is not one millisecond into the next line's slot. Pinned because off-by-one at
        // this boundary is the difference between a review loop that fires on every tight script and
        // one that never fires.
        NarrationFitLineResult result = NarrationFitSolver.SolveLine(
            Line("vo0", playDurationSec: 2.0, wordCount: 8, windowStartSec: 1.0, windowEndSec: 3.0));

        result.WindowSec.Should().Be(2.0);
        result.OverflowSec.Should().Be(0.0);
        result.OverflowWords.Should().Be(0);
        result.Fits.Should().BeTrue();
    }

    // =====================================================================
    // The window itself
    // =====================================================================

    [Fact]
    public void A_windowless_line_is_reported_as_unmeasured_and_never_overflows()
    {
        // The last declared line has no successor, so its window genuinely does not exist. It must
        // NOT be treated as overrunning: an unknown window is an absence of evidence, and a solver
        // that reported it as an overflow would make every script's final line a review-loop
        // failure. The enormous play duration here is deliberate — even a line that dwarfs every
        // other one contributes nothing without a window to overrun.
        NarrationFitLineResult result = NarrationFitSolver.SolveLine(
            Line("vo9", playDurationSec: 99.0, wordCount: 40));

        result.WindowSec.Should().BeNull();
        result.WindowSource.Should().Be(NarrationFitSolver.WindowSourceNone);
        result.OverflowSec.Should().Be(0.0);
        result.OverflowWords.Should().Be(0);
        result.Fits.Should().BeTrue();
        result.WordsPerSecond.Should().Be(40.0 / 99.0, "the rate is measured from the audio and needs no window");
    }

    [Theory]
    [InlineData(2.0, 2.0)] // zero-length window
    [InlineData(3.0, 1.0)] // reversed window
    [InlineData(1.0, null)] // only a start
    [InlineData(null, 3.0)] // only an end
    public void A_window_that_is_absent_zero_length_or_reversed_is_no_window_at_all(double? start, double? end)
    {
        NarrationFitLineResult result = NarrationFitSolver.SolveLine(
            Line("vo0", playDurationSec: 5.0, wordCount: 5, windowStartSec: start, windowEndSec: end));

        result.WindowSec.Should().BeNull();
        result.WindowSource.Should().Be(NarrationFitSolver.WindowSourceNone);
        result.OverflowSec.Should().Be(0.0);
        result.OverflowWords.Should().Be(0);
        result.Fits.Should().BeTrue("an unusable window is not a window to overrun");
    }

    // =====================================================================
    // Degenerate durations and word counts — the NaN/Infinity firewall
    // =====================================================================

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_line_with_no_measurable_duration_has_a_zero_rate_and_never_overflows(double playDurationSec)
    {
        // A zero, negative, NaN or infinite duration is not a rate and must never become one: a NaN
        // words-per-second would serialize as an exception (System.Text.Json refuses NaN/Infinity)
        // and an Infinity would poison every total it touched. The rule is a flat 0.
        NarrationFitLineResult result = NarrationFitSolver.SolveLine(
            Line("vo0", playDurationSec: playDurationSec, wordCount: 10,
                windowStartSec: 0.0, windowEndSec: 1.0));

        result.WordsPerSecond.Should().Be(0.0);
        result.OverflowSec.Should().Be(0.0);
        result.OverflowWords.Should().Be(0);
        result.Fits.Should().BeTrue();

        // Proof rather than assertion-by-eye: a non-finite value anywhere in the report would make
        // this call throw instead of return.
        JsonSerializer.Serialize(result, JsonOptions).Should().NotContain("NaN").And.NotContain("Infinity");
    }

    /// <summary>
    /// The complement of the NaN/Infinity firewall above: that test fences the INPUTS, and this one
    /// pins the method's own ARITHMETIC.
    ///
    /// <para>
    /// <c>wordsPerSecond</c> is a division by <c>playDurationSec</c>, so a denormal-but-positive
    /// duration — finite, non-zero, and therefore passed through the input guard untouched —
    /// overflows the quotient to <c>Infinity</c>. That value then leaves through the record, and
    /// <c>System.Text.Json</c> refuses it with an <c>ArgumentException</c> — <b>not</b> a
    /// <c>JsonException</c> — so a step whose only catch names the latter reports the run as
    /// <c>UNEXPECTED_ERROR</c> instead of degrading. This method's whole contract is "deterministic,
    /// never throws", and this is the one input shape where the guard alone did not make it true.
    /// </para>
    /// <para>
    /// Latent in production (reaching it needs <c>playDurationSec &lt; ~1.2e-299</c>, which ffprobe
    /// cannot emit and the executor's own <c>DurationSec &lt;= 0</c> guard rejects) — pinned anyway,
    /// because a pure function's contract is not conditional on its current caller.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(double.Epsilon)]
    [InlineData(1e-320)]
    [InlineData(double.Epsilon * 8)]
    public void A_denormal_play_duration_yields_a_finite_zero_rate_not_an_infinity(double playDurationSec)
    {
        NarrationFitLineResult result = NarrationFitSolver.SolveLine(
            Line("vo0", playDurationSec: playDurationSec, wordCount: 1,
                windowStartSec: 0.0, windowEndSec: 2.0));

        result.WordsPerSecond.Should().Be(0.0);
        double.IsFinite(result.WordsPerSecond).Should().BeTrue();
        double.IsFinite(result.OverflowSec).Should().BeTrue();
        result.OverflowSec.Should().Be(0.0);

        // The proof, and the actual defect: serializing used to throw ArgumentException.
        JsonSerializer.Serialize(result, JsonOptions)
            .Should().NotContain("Infinity").And.NotContain("NaN");
    }

    [Fact]
    public void A_zero_duration_line_inside_a_window_fits_with_a_zero_rate()
    {
        // The zero-duration line's own case, stated separately from the NaN firewall above: this is
        // what a skipped line (empty sanitized text, never synthesized) actually produces, and it
        // must read as a line with nothing to say rather than as a measurement failure.
        NarrationFitLineResult result = NarrationFitSolver.SolveLine(
            Line("vo0", playDurationSec: 0.0, wordCount: 0, windowStartSec: 0.0, windowEndSec: 2.0));

        result.WindowSec.Should().Be(2.0);
        result.WindowSource.Should().Be(NarrationFitSolver.WindowSourceDeclaredStarts);
        result.WordsPerSecond.Should().Be(0.0);
        result.OverflowSec.Should().Be(0.0);
        result.OverflowWords.Should().Be(0);
        result.Fits.Should().BeTrue();
    }

    [Fact]
    public void A_line_with_no_words_has_a_zero_rate_however_long_it_plays()
    {
        NarrationFitLineResult result = NarrationFitSolver.SolveLine(
            Line("vo0", playDurationSec: 4.0, wordCount: 0, windowStartSec: 0.0, windowEndSec: 1.0));

        result.WordsPerSecond.Should().Be(0.0, "no words means no rate, never a division by a duration");
        result.OverflowSec.Should().Be(3.0, "the line still overruns a 1.0 s window by 3.0 s of audio");
        result.OverflowWords.Should().Be(0, "0 words/s cannot overflow by a word");
        result.Fits.Should().BeFalse();
    }

    [Fact]
    public void A_negative_word_count_is_clamped_rather_than_producing_a_negative_overflow()
    {
        NarrationFitLineResult result = NarrationFitSolver.SolveLine(
            Line("vo0", playDurationSec: 1.0, wordCount: -7, windowStartSec: 0.0, windowEndSec: 2.0));

        result.WordCount.Should().Be(0);
        result.WordsPerSecond.Should().Be(0.0);
        result.OverflowWords.Should().Be(0);
    }

    // =====================================================================
    // Rounding
    // =====================================================================

    [Fact]
    public void Overflow_seconds_are_rounded_to_three_decimal_places()
    {
        // 2.23456 - 1.0 = 1.23456 -> 1.235 at three places. Pinned by literal because the rounding is
        // what makes two reports of the same run comparable instead of differing in the 16th digit —
        // and because the word ceiling is computed from the ROUNDED value, so the two reported
        // numbers can never describe different overruns.
        NarrationFitLineResult result = NarrationFitSolver.SolveLine(
            Line("vo0", playDurationSec: 2.23456, wordCount: 10,
                windowStartSec: 1.0, windowEndSec: 2.0));

        result.OverflowSec.Should().Be(1.235);
        // 10 words / 2.23456 s = 4.4751 words/s; 1.235 s at that rate is 5.5267 words, ceiled to 6 —
        // computed from the ROUNDED overflow, so the two published numbers agree with each other.
        result.OverflowWords.Should().Be(6);
        result.WindowSec.Should().Be(1.0, "the window is a subtraction of two declared starts, not a rounded measurement");
    }

    // =====================================================================
    // Single line, empty input, and the totals
    // =====================================================================

    [Fact]
    public void A_single_windowless_line_is_a_complete_report_of_one_unmeasured_line()
    {
        NarrationFitReport report = NarrationFitSolver.Solve(
            [Line("vo0", playDurationSec: 1.0, wordCount: 3)]);

        report.Lines.Should().HaveCount(1);
        report.MeasuredLineCount.Should().Be(0);
        report.UnknownWindowLineCount.Should().Be(1);
        report.OverflowLineCount.Should().Be(0);
        report.OverflowWords.Should().Be(0);
        report.OverflowTotalSec.Should().Be(0.0);
        report.MaxOverflowSec.Should().Be(0.0);
    }

    [Fact]
    public void An_empty_input_is_a_zeroed_report_with_no_nulls()
    {
        // The shape a consumer reads unconditionally: every counter 0 and both second totals 0.0 —
        // never null, never NaN. A review loop must be able to gate on meta.fit.overflowWords
        // without first proving that anything was measured.
        foreach (NarrationFitReport report in new[]
                 {
                     NarrationFitSolver.Solve([]),
                     NarrationFitSolver.Solve(null),
                     NarrationFitSolver.Solve(new List<NarrationFitLine> { null! })
                 })
        {
            report.Lines.Should().BeEmpty();
            report.MeasuredLineCount.Should().Be(0);
            report.OverflowLineCount.Should().Be(0);
            report.OverflowWords.Should().Be(0);
            report.OverflowTotalSec.Should().Be(0.0);
            report.MaxOverflowSec.Should().Be(0.0);
            report.UnknownWindowLineCount.Should().Be(0);
        }
    }

    [Fact]
    public void The_totals_sum_the_per_line_verdicts_and_count_each_line_once()
    {
        // Three declared lines: the first overruns its 3.0 s window, the second fits its 2.0 s
        // window, the third is the declared last line and has no window at all. So exactly two lines
        // were MEASURED, exactly one is unknown, exactly one overflows — and the three lines are
        // partitioned by those two counters rather than double-counted.
        NarrationFitReport report = NarrationFitSolver.Solve(
        [
            Line("vo0", playDurationSec: 4.0, wordCount: 10, windowStartSec: 0.0, windowEndSec: 3.0),
            Line("vo1", playDurationSec: 1.0, wordCount: 6, windowStartSec: 3.0, windowEndSec: 5.0),
            Line("vo2", playDurationSec: 2.0, wordCount: 4)
        ]);

        report.Lines.Should().HaveCount(3);
        report.Lines.Select(l => l.Id).Should().ContainInOrder("vo0", "vo1", "vo2");

        report.MeasuredLineCount.Should().Be(2);
        report.UnknownWindowLineCount.Should().Be(1);
        report.MeasuredLineCount.Should().Be(3 - report.UnknownWindowLineCount,
            "every line is either measured or unknown, never both and never neither");

        report.OverflowLineCount.Should().Be(1);
        report.OverflowWords.Should().Be(3);
        report.OverflowTotalSec.Should().Be(1.0);
        report.MaxOverflowSec.Should().Be(1.0);
    }

    [Fact]
    public void Overflow_total_seconds_is_the_rounded_sum_of_the_rounded_per_line_values()
    {
        // Two overflows that each round at three places: 1.2346 -> 1.235 and 2.3457 -> 2.346. The
        // total is the sum of THOSE (3.581), rounded again — so the report never claims a precision
        // its own per-line numbers do not have, and max is the larger of the two, not their sum.
        NarrationFitReport report = NarrationFitSolver.Solve(
        [
            Line("vo0", playDurationSec: 2.2346, wordCount: 4, windowStartSec: 0.0, windowEndSec: 1.0),
            Line("vo1", playDurationSec: 3.3457, wordCount: 7, windowStartSec: 1.0, windowEndSec: 2.0)
        ]);

        report.Lines[0].OverflowSec.Should().Be(1.235);
        report.Lines[1].OverflowSec.Should().Be(2.346);
        report.OverflowTotalSec.Should().Be(3.581);
        report.MaxOverflowSec.Should().Be(2.346);
        report.OverflowLineCount.Should().Be(2);
    }

    // =====================================================================
    // Alignment evidence is carried, never used
    // =====================================================================

    [Fact]
    public void Aligned_word_count_is_reported_but_changes_no_arithmetic()
    {
        // The solver must be usable on a deployment with no ASR provider at all, so the aligned word
        // count is evidence and nothing else. Same input, one with alignment and one without: every
        // number except AlignedWordCount is identical.
        NarrationFitLine input = Line("vo0", playDurationSec: 4.0, wordCount: 10,
            windowStartSec: 0.0, windowEndSec: 3.0);

        NarrationFitLineResult unaligned = NarrationFitSolver.SolveLine(input);
        NarrationFitLineResult aligned = NarrationFitSolver.SolveLine(input with { AlignedWordCount = 9 });

        unaligned.AlignedWordCount.Should().BeNull();
        aligned.AlignedWordCount.Should().Be(9);

        aligned.WordCount.Should().Be(unaligned.WordCount);
        aligned.WordsPerSecond.Should().Be(unaligned.WordsPerSecond);
        aligned.OverflowSec.Should().Be(unaligned.OverflowSec);
        aligned.OverflowWords.Should().Be(unaligned.OverflowWords);
        aligned.Fits.Should().Be(unaligned.Fits);
    }

    [Fact]
    public void An_anchor_id_is_carried_through_untouched()
    {
        NarrationFitLineResult result = NarrationFitSolver.SolveLine(
            Line("vo0", playDurationSec: 1.0, wordCount: 4, anchorId: "s3"));

        result.AnchorId.Should().Be("s3");
        result.Id.Should().Be("vo0");
    }

    // =====================================================================
    // Word counting
    // =====================================================================

    [Theory]
    [InlineData("one two three", 3)]
    [InlineData("  leading and trailing   spaces  ", 4)]
    [InlineData("one\ttwo\nthree", 3)]
    [InlineData("one   two", 2)] // runs of whitespace count once
    [InlineData("", 0)]
    [InlineData("     ", 0)]
    [InlineData(null, 0)]
    [InlineData("single", 1)]
    public void Count_words_counts_whitespace_separated_tokens(string? text, int expected)
    {
        NarrationFitSolver.CountWords(text).Should().Be(expected);
    }

    [Fact]
    public void Count_words_is_the_same_rule_the_report_uses()
    {
        // The step counts the SANITIZED text with this member and hands the number to Solve, so a
        // second, subtly different split anywhere would silently move every rate in the report. This
        // pins the two together rather than trusting the call site.
        NarrationFitLineResult result = NarrationFitSolver.SolveLine(
            Line("vo0", playDurationSec: 2.0, wordCount: NarrationFitSolver.CountWords("Hello there world"),
                windowStartSec: 0.0, windowEndSec: 4.0));

        result.WordCount.Should().Be(3);
        result.WordsPerSecond.Should().Be(1.5);
    }

    // =====================================================================
    // Determinism
    // =====================================================================

    [Fact]
    public void Solve_is_a_function_of_its_argument()
    {
        // The acceptance criterion, asserted rather than assumed. Note WHY the JSON comparison is
        // here as well as the structural one: NarrationFitReport holds its lines in a list, so two
        // reports from two separate calls are never reference-equal, and the thing the step actually
        // emits — and the step cache actually stores — is the serialized form.
        IReadOnlyList<NarrationFitLine> input =
        [
            Line("vo0", playDurationSec: 4.0, wordCount: 10, windowStartSec: 0.0, windowEndSec: 3.0),
            Line("vo1", playDurationSec: 1.0, wordCount: 6, windowStartSec: 3.0, windowEndSec: 5.0),
            Line("vo2", playDurationSec: 2.5, wordCount: 7)
        ];

        NarrationFitReport first = NarrationFitSolver.Solve(input);
        NarrationFitReport second = NarrationFitSolver.Solve(input);

        first.Should().BeEquivalentTo(second, "the same input must produce an equal report");
        JsonSerializer.Serialize(first, JsonOptions)
            .Should().Be(JsonSerializer.Serialize(second, JsonOptions),
                "and the emitted bytes must be identical, because that is what is cached and compared");

        // Nothing was mutated on the way through, so a third call over the SAME instances still
        // agrees — a solver that sorted or rewrote its input would pass the two calls above and
        // fail here.
        NarrationFitSolver.Solve(input).Should().BeEquivalentTo(first);
    }

    [Fact]
    public void The_serialized_shape_is_camelCase_and_carries_every_key()
    {
        NarrationFitLineResult result = NarrationFitSolver.SolveLine(
            Line("vo0", playDurationSec: 4.0, wordCount: 10, windowStartSec: 0.0, windowEndSec: 3.0));

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(result, JsonOptions));

        document.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(
                "id", "anchorId", "windowSec", "playDurationSec", "wordCount", "alignedWordCount",
                "wordsPerSecond", "overflowSec", "overflowWords", "fits", "windowSource", "wordBudget");

        // `anchorId` and `alignedWordCount` are present AS NULL rather than omitted. That is
        // deliberate: this report's key set is closed, so a consumer reads overflowWords — and the
        // compile-side passthrough that copies this object requires it — without a TryGetProperty
        // dance, and a null already means exactly one thing per member.
        document.RootElement.GetProperty("anchorId").ValueKind.Should().Be(JsonValueKind.Null);
        document.RootElement.GetProperty("alignedWordCount").ValueKind.Should().Be(JsonValueKind.Null);
        document.RootElement.GetProperty("windowSource").GetString()
            .Should().Be(NarrationFitSolver.WindowSourceDeclaredStarts);
    }
}
