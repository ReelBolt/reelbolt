using System;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Agents.Tools;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="SandboxOutputCompactor"/> is the fix for a measured cost: one successful
/// <c>remotion render</c> returned 124–140 KB of progress lines (35–45 K tokens), replayed on every
/// later turn of the agent's loop. These tests pin what survives that bound — the final progress
/// values, the head, the tail, and an honest count of what was left out.
/// </summary>
public class SandboxOutputCompactorTests
{
    /// <summary>
    /// The shape of a real render's output: a short preamble, one "Rendered n/N, time remaining …"
    /// and one "Encoded n/N" line per frame, interleaved, and a short report at the end.
    /// </summary>
    private static string RenderOutput(int frames)
    {
        StringBuilder builder = new();
        builder.AppendLine("Bundled code 4210ms");
        builder.AppendLine("Getting composition");
        builder.AppendLine("Composition          FinalVideo");
        builder.AppendLine("Codec                h264");
        builder.AppendLine("Output               out/promo.mp4");
        for (int frame = 1; frame <= frames; frame++)
        {
            builder.AppendLine($"Rendered {frame}/{frames}, time remaining {(frames - frame) / 12}s");
            builder.AppendLine($"Encoded {frame}/{frames}");
        }

        builder.AppendLine("+ out/promo.mp4 120.4 MB");
        return builder.ToString();
    }

    [Fact]
    public void A_3555_frame_render_collapses_from_over_100_KB_to_a_few_KB()
    {
        string raw = RenderOutput(3555);
        raw.Length.Should().BeGreaterThan(100_000, "the fixture must reproduce the measured 124-140 KB problem");

        CompactedOutput compacted = SandboxOutputCompactor.Compact(raw);

        compacted.Text.Length.Should().BeLessThan(SandboxOutputCompactor.MaxOutputChars + 1);
        compacted.Text.Length.Should().BeLessThan(2_000, "only the preamble, two summary lines and the report remain");
    }

    [Fact]
    public void Progress_families_become_one_summary_line_each_carrying_the_final_value()
    {
        CompactedOutput compacted = SandboxOutputCompactor.Compact(RenderOutput(3555));

        compacted.Text.Should().Contain("[progress] Rendered 3555/3555 frames (3555 progress lines collapsed)");
        compacted.Text.Should().Contain("[progress] Encoded 3555/3555 frames (3555 progress lines collapsed)");
        compacted.Text.Split('\n').Count(l => l.StartsWith("Rendered ", StringComparison.Ordinal))
            .Should().Be(0, "no raw Rendered n/N line survives");
        compacted.Text.Should().Contain("+ out/promo.mp4 120.4 MB", "the final report line is the tail and survives");
        compacted.Text.Should().Contain("Getting composition", "the preamble is the head and survives");
    }

    [Fact]
    public void Structured_progress_reports_the_final_frame_counts()
    {
        CompactedOutput compacted = SandboxOutputCompactor.Compact(RenderOutput(722));

        compacted.Progress.Should().NotBeNull();
        compacted.Progress!.RenderedFrames.Should().Be(722);
        compacted.Progress.EncodedFrames.Should().Be(722);
        compacted.Progress.TotalFrames.Should().Be(722);
    }

    [Fact]
    public void Total_line_count_is_the_real_one_and_collapsed_progress_is_not_reported_as_truncation()
    {
        CompactedOutput compacted = SandboxOutputCompactor.Compact(RenderOutput(100));

        compacted.TotalLines.Should().Be(5 + 200 + 1);
        compacted.CollapsedLines.Should().Be(200 - 2, "200 progress lines folded into 2 summary lines");
        compacted.Truncated.Should().BeFalse(
            "a summary line carries the final value, so nothing a caller could act on is missing");
    }

    [Fact]
    public void Bundling_percentages_collapse_to_the_last_one()
    {
        string raw = string.Join('\n', Enumerable.Range(1, 100).Select(p => $"Bundling {p}%")) + "\nBundled code 3000ms";

        CompactedOutput compacted = SandboxOutputCompactor.Compact(raw);

        compacted.Text.Should().Contain("[progress] Bundling 100% (100 progress lines collapsed)");
        compacted.Progress!.BundlingPercent.Should().Be(100);
        compacted.Text.Should().Contain("Bundled code 3000ms");
    }

    [Fact]
    public void Carriage_return_progress_and_ansi_colour_are_handled()
    {
        // A CLI redrawing one line with \r, wrapped in colour codes, is still thousands of lines.
        string raw = string.Concat(Enumerable.Range(1, 50).Select(f => $"\u001b[32mRendered {f}/50\u001b[0m\r")) + "done\n";

        CompactedOutput compacted = SandboxOutputCompactor.Compact(raw);

        compacted.Text.Should().Contain("[progress] Rendered 50/50 frames");
        compacted.Text.Should().NotContain("\u001b");
        compacted.Progress!.RenderedFrames.Should().Be(50);
    }

    [Fact]
    public void A_family_that_printed_only_twice_is_left_verbatim()
    {
        CompactedOutput compacted = SandboxOutputCompactor.Compact("Rendered 1/2\nRendered 2/2\nfinished");

        compacted.Text.Should().Be("Rendered 1/2\nRendered 2/2\nfinished");
        compacted.CollapsedLines.Should().Be(0);
        compacted.Progress!.RenderedFrames.Should().Be(2);
    }

    [Fact]
    public void Other_runs_that_differ_only_in_digits_are_folded_too()
    {
        string raw = "start\n" + string.Join('\n', Enumerable.Range(1, 30).Select(i => $"asset-{i}.png  resolved in {i * 3}ms")) + "\nend";

        CompactedOutput compacted = SandboxOutputCompactor.Compact(raw);

        compacted.Text.Should().Contain("asset-1.png");
        compacted.Text.Should().Contain("asset-30.png");
        compacted.Text.Should().Contain("(28 similar lines omitted)");
        compacted.Text.Should().NotContain("asset-15.png");
    }

    [Fact]
    public void Short_ordinary_output_passes_through_unchanged()
    {
        const string raw = "src/root.tsx(12,3): error TS2304: Cannot find name 'Foo'.\nsrc/root.tsx(20,1): error TS1005: ';' expected.";

        CompactedOutput compacted = SandboxOutputCompactor.Compact(raw);

        compacted.Text.Should().Be(raw);
        compacted.TotalLines.Should().Be(2);
        compacted.Truncated.Should().BeFalse();
        compacted.CollapsedLines.Should().Be(0);
        compacted.Progress.Should().BeNull();
    }

    [Fact]
    public void Long_unrelated_output_keeps_the_first_20_and_last_40_lines_and_counts_the_rest()
    {
        // Every line differs in SHAPE (not just digits), so the repeat-run folding does not apply.
        string raw = string.Join('\n', Enumerable.Range(1, 500).Select(i => $"{new string((char)('a' + i % 26), 3)} item {i} {new string('q', i % 11)} tail{i % 5}x"));

        CompactedOutput compacted = SandboxOutputCompactor.Compact(raw);
        string[] lines = compacted.Text.Split('\n');

        compacted.TotalLines.Should().Be(500);
        compacted.Truncated.Should().BeTrue();
        compacted.Text.Should().Contain("[440 lines omitted]");
        lines[0].Should().Contain(" item 1 ");
        lines[^1].Should().Contain(" item 500 ");
        lines.Length.Should().Be(20 + 1 + 40);
    }

    [Fact]
    public void The_character_cap_is_a_promise_and_the_tail_is_what_survives_it()
    {
        // 300-char lines at the head/tail floor still exceed a tiny cap; the cap wins, from the front.
        // Each line differs in shape (its letter), so the repeat-run folding does not shrink the fixture.
        string raw = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"{new string((char)('a' + i % 26), 250)}-END{i}"));

        CompactedOutput compacted = SandboxOutputCompactor.Compact(raw, 1_000);

        compacted.Text.Length.Should().BeLessThanOrEqualTo(1_000);
        compacted.Truncated.Should().BeTrue();
        compacted.Text.Should().Contain("-END200", "the end of the output is the last thing sacrificed");
    }

    [Fact]
    public void A_pathological_single_line_is_clipped()
    {
        CompactedOutput compacted = SandboxOutputCompactor.Compact(new string('z', 50_000));

        compacted.Text.Length.Should().BeLessThanOrEqualTo(SandboxOutputCompactor.MaxLineChars + 1);
        compacted.Truncated.Should().BeTrue();
        compacted.TotalLines.Should().Be(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n\n")]
    public void Empty_output_is_empty(string? raw)
    {
        CompactedOutput compacted = SandboxOutputCompactor.Compact(raw);

        compacted.Text.Should().BeEmpty();
        compacted.TotalLines.Should().Be(0);
        compacted.Truncated.Should().BeFalse();
        compacted.Progress.Should().BeNull();
    }

    [Fact]
    public void A_failing_render_keeps_its_error_even_after_thousands_of_progress_lines()
    {
        StringBuilder builder = new();
        for (int frame = 1; frame <= 2000; frame++)
            builder.AppendLine($"Rendered {frame}/3555, time remaining 4m");
        builder.AppendLine("Error: Could not play video with src=/workspace/public/clip.mp4");
        builder.AppendLine("    at delayRender timed out after 28000ms");

        CompactedOutput compacted = SandboxOutputCompactor.Compact(builder.ToString());

        compacted.Text.Should().Contain("Could not play video");
        compacted.Text.Should().Contain("delayRender timed out");
        compacted.Text.Should().Contain("[progress] Rendered 2000/3555");
        compacted.Text.Length.Should().BeLessThan(1_000);
    }

    [Fact]
    public void Compaction_is_deterministic()
    {
        string raw = RenderOutput(900);

        SandboxOutputCompactor.Compact(raw).Should().BeEquivalentTo(SandboxOutputCompactor.Compact(raw));
    }

    [Fact]
    public void The_documented_limits_are_the_ones_the_class_applies()
    {
        SandboxOutputCompactor.HeadLines.Should().Be(20);
        SandboxOutputCompactor.TailLines.Should().Be(40);
        SandboxOutputCompactor.MaxOutputChars.Should().BeLessThanOrEqualTo(8_000, "the cap is 'a few KB'");
    }
}
