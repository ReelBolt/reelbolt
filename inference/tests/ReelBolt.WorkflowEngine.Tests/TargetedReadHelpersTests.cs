using ReelBolt.Shared.Text;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Agents.Tools;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The pure halves of the targeted-read tools and of <c>GetCompositionInfo</c>: the ranged-read
/// window shared by <c>ReadSandboxFileLines</c>/<c>ReadProjectFileLines</c>, the project-file
/// outline, and the parser for <c>remotion compositions</c>' table.
/// </summary>
public class TargetedReadHelpersTests
{
    // ── TextLineWindow ──────────────────────────────────────────────────────

    [Fact]
    public void A_slice_is_numbered_clamped_and_reports_whether_more_follows()
    {
        TextLineSlice slice = TextLineWindow.Read("a\nb\nc\nd\ne\n", startLine: 2, lineCount: 2);

        slice.StartLine.Should().Be(2);
        slice.EndLine.Should().Be(3);
        slice.Truncated.Should().BeTrue();
        slice.Content.Should().Be("     2| b\n     3| c\n");
    }

    [Fact]
    public void The_last_slice_is_not_truncated_and_a_start_past_the_end_is_empty()
    {
        TextLineSlice last = TextLineWindow.Read("a\nb\nc", startLine: 3, lineCount: 10);
        last.Truncated.Should().BeFalse();
        last.EndLine.Should().Be(3);

        TextLineSlice past = TextLineWindow.Read("a\nb\nc", startLine: 99, lineCount: 10);
        past.Content.Should().BeEmpty();
        past.EndLine.Should().Be(3);
        past.TotalLines.Should().Be(3);
        past.Truncated.Should().BeFalse();
    }

    [Fact]
    public void An_empty_file_is_zero_lines_and_crlf_is_normalised()
    {
        TextLineWindow.Read("", 1, 10).TotalLines.Should().Be(0);
        TextLineWindow.Read("a\r\nb\r\n", 1, 10).Content.Should().Be("     1| a\n     2| b\n     3| \n");
    }

    [Fact]
    public void Start_and_count_are_clamped_not_refused()
    {
        TextLineSlice slice = TextLineWindow.Read("a\nb", startLine: -5, lineCount: 0);

        slice.StartLine.Should().Be(1);
        slice.EndLine.Should().Be(1, "a count of 0 is clamped up to 1");
    }

    // ── ProjectFileOutline ──────────────────────────────────────────────────

    [Fact]
    public void Markdown_outline_lists_headings_with_levels_and_lines_and_ignores_code_fences()
    {
        const string markdown = "# Title\n\nintro\n\n## Setup\n```bash\n# not a heading\n```\n### Deep ###\n~~~\n## also not\n~~~\n## Usage\n";

        using JsonDocument outline = JsonDocument.Parse(ProjectFileOutline.Build("docs/readme.md", markdown));
        JsonElement root = outline.RootElement;

        root.GetProperty("kind").GetString().Should().Be("markdown");
        root.GetProperty("totalLines").GetInt32().Should().Be(14);
        JsonElement[] headings = [.. root.GetProperty("headings").EnumerateArray()];
        headings.Select(h => h.GetProperty("text").GetString()).Should().Equal("Title", "Setup", "Deep", "Usage");
        headings.Select(h => h.GetProperty("level").GetInt32()).Should().Equal(1, 2, 3, 2);
        headings.Select(h => h.GetProperty("line").GetInt32()).Should().Equal(1, 5, 9, 13);
        root.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Markdown_outline_is_capped()
    {
        string markdown = string.Join('\n', Enumerable.Range(1, ProjectFileOutline.MaxHeadings + 25).Select(i => $"## Section {i}"));

        using JsonDocument outline = JsonDocument.Parse(ProjectFileOutline.Build("big.md", markdown));

        outline.RootElement.GetProperty("headings").GetArrayLength().Should().Be(ProjectFileOutline.MaxHeadings);
        outline.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Source_files_get_the_code_outline_other_files_just_their_size()
    {
        using JsonDocument code = JsonDocument.Parse(ProjectFileOutline.Build(
            "src/Scene.tsx", "import React from 'react';\nexport const Scene = () => <div/>;\n"));
        code.RootElement.GetProperty("imports").EnumerateArray().Select(e => e.GetString()).Should().Equal("react");
        code.RootElement.GetProperty("symbols")[0].GetProperty("name").GetString().Should().Be("Scene");

        using JsonDocument other = JsonDocument.Parse(ProjectFileOutline.Build("data.csv", "a,b\n1,2\n"));
        other.RootElement.GetProperty("kind").GetString().Should().Be("text");
        other.RootElement.GetProperty("totalLines").GetInt32().Should().Be(3);
        other.RootElement.GetProperty("note").GetString().Should().Contain("ReadProjectFileLines");
    }

    // ── RemotionCompositionTable ────────────────────────────────────────────

    [Fact]
    public void The_CLI_table_is_parsed_into_numbers()
    {
        const string output = """

            The following compositions are available:

            FinalVideo    30      1080x1920      3555 (118.50 sec)
            Poster                1080x1080      Still
            Scene-01_a    24      1920x1080      72 (3.00 sec)

            """;

        IReadOnlyList<CompositionInfo> compositions = RemotionCompositionTable.Parse(output);

        compositions.Should().HaveCount(3);
        compositions[0].Should().BeEquivalentTo(new CompositionInfo("FinalVideo", 30, 1080, 1920, 3555, false));
        compositions[0].DurationSeconds.Should().Be(118.5);
        compositions[1].IsStill.Should().BeTrue();
        compositions[1].Fps.Should().BeNull();
        compositions[1].DurationInFrames.Should().Be(1);
        compositions[2].Id.Should().Be("Scene-01_a");
        compositions[2].Fps.Should().Be(24);
    }

    [Fact]
    public void Colour_codes_and_log_noise_do_not_break_the_parse_and_garbage_yields_nothing()
    {
        const string noisy = "\u001b[90mdebug: starting\u001b[0m\n\u001b[32mMain    30      1280x720      150 (5.00 sec)\u001b[0m\nwarning: something\n";
        RemotionCompositionTable.Parse(noisy).Should().ContainSingle().Which.Id.Should().Be("Main");

        RemotionCompositionTable.Parse("Error: could not find entry point").Should().BeEmpty();
        RemotionCompositionTable.Parse(null).Should().BeEmpty();
    }

    [Fact]
    public void Fractional_frame_rates_are_kept()
    {
        IReadOnlyList<CompositionInfo> compositions = RemotionCompositionTable.Parse("Film    29.97      1920x1080      300 (10.01 sec)");

        compositions.Should().ContainSingle().Which.Fps.Should().Be(29.97);
    }
}
