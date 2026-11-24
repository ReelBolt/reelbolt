using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using ReelBolt.Shared.History;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.History;

/// <summary>
/// T-004: the first-party Myers line diff (contract section 7). Property-style tests apply the hunks
/// back to the old text and require the new text, next to the caps and the line-ending rule.
/// </summary>
public class LineDiffTests
{
    [Fact]
    public void IdenticalInputHasNoHunks()
    {
        LineDiffResult result = LineDiff.Compute("a\nb\nc", "a\nb\nc");

        result.TooLarge.Should().BeFalse();
        result.Hunks.Should().BeEmpty();
    }

    [Fact]
    public void EmptySidesAreDiffedAndApply()
    {
        LineDiffResult added = LineDiff.Compute(string.Empty, "a\nb");
        added.TooLarge.Should().BeFalse();
        added.Hunks.Should().NotBeEmpty();
        Apply(string.Empty, added).Should().Be("a\nb");

        LineDiffResult removed = LineDiff.Compute("a\nb", string.Empty);
        removed.Hunks.Should().NotBeEmpty();
        Apply("a\nb", removed).Should().Be(string.Empty);
    }

    [Fact]
    public void HunkKeepsThreeContextLinesEachSide()
    {
        string oldText = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"line{i}"));
        string newText = string.Join("\n", Enumerable.Range(0, 10).Select(i => i == 5 ? "changed" : $"line{i}"));

        LineDiffResult result = LineDiff.Compute(oldText, newText);

        result.Hunks.Should().ContainSingle();
        LineDiffHunk hunk = result.Hunks[0];
        hunk.FromStart.Should().Be(3, "three context lines precede line 6");
        hunk.FromCount.Should().Be(7);
        Apply(oldText, result).Should().Be(newText);
    }

    [Fact]
    public void CrlfAndLfAreDifferentLinesButEndingsAreStrippedFromText()
    {
        LineDiffResult result = LineDiff.Compute("a\r\nb", "a\nb");

        result.Hunks.Should().NotBeEmpty("the carriage return is part of the line");
        result.Hunks.SelectMany(h => h.Lines)
            .Should().OnlyContain(line => !line.Text.Contains('\r'));
    }

    [Fact]
    public void AShownLineIsCappedAtTwoThousandCharacters()
    {
        string longLine = new('x', LineDiff.MaxLineLength + 500);

        LineDiffResult result = LineDiff.Compute("a", longLine);

        result.Hunks.SelectMany(h => h.Lines)
            .Where(line => line.Op == "+")
            .Should().OnlyContain(line => line.Text.Length == LineDiff.MaxLineLength);
    }

    [Fact]
    public void PastTheByteCapIsTooLarge()
    {
        string tooBig = new('x', LineDiff.MaxBytesPerSide + 1);

        LineDiff.Compute("a", tooBig).TooLarge.Should().BeTrue();
        LineDiff.Compute(tooBig, "a").TooLarge.Should().BeTrue();
    }

    [Fact]
    public void PastTheLineCapIsTooLarge()
    {
        string manyLines = string.Join('\n', Enumerable.Repeat("x", LineDiff.MaxLinesPerSide + 1));

        LineDiff.Compute("a", manyLines).TooLarge.Should().BeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(101)]
    public void ApplyingTheHunksToTheOldTextYieldsTheNewText(int seed)
    {
        Random random = new(seed);

        for (int iteration = 0; iteration < 20; iteration++)
        {
            string[] oldLines = Enumerable.Range(0, random.Next(0, 60))
                .Select(_ => $"old-{random.Next(0, 12)}")
                .ToArray();

            List<string> newLines = new(oldLines);
            int edits = random.Next(0, 12);
            for (int edit = 0; edit < edits; edit++)
            {
                switch (random.Next(3))
                {
                    case 0 when newLines.Count > 0:
                        newLines.RemoveAt(random.Next(newLines.Count));
                        break;
                    case 1:
                        newLines.Insert(random.Next(newLines.Count + 1), $"new-{random.Next(0, 12)}");
                        break;
                    case 2 when newLines.Count > 0:
                        newLines[random.Next(newLines.Count)] = $"changed-{random.Next(0, 12)}";
                        break;
                }
            }

            string oldText = string.Join('\n', oldLines);
            string newText = string.Join('\n', newLines);

            LineDiffResult result = LineDiff.Compute(oldText, newText);

            result.TooLarge.Should().BeFalse();
            Apply(oldText, result).Should().Be(newText);
        }
    }

    /// <summary>Applies unified hunks the way a consumer does: copy old lines around, skip removals, add additions.</summary>
    private static string Apply(string oldText, LineDiffResult result)
    {
        string[] oldLines = LineDiff.SplitLines(oldText);
        List<string> output = new();
        int cursor = 0;

        foreach (LineDiffHunk hunk in result.Hunks)
        {
            int start = hunk.FromCount == 0 ? hunk.FromStart : hunk.FromStart - 1;
            while (cursor < start)
                output.Add(oldLines[cursor++]);

            foreach (LineDiffLine line in hunk.Lines)
            {
                switch (line.Op)
                {
                    case " ":
                        output.Add(oldLines[cursor]);
                        cursor++;
                        break;
                    case "-":
                        cursor++;
                        break;
                    case "+":
                        output.Add(line.Text);
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected op '{line.Op}'.");
                }
            }
        }

        while (cursor < oldLines.Length)
            output.Add(oldLines[cursor++]);

        return string.Join('\n', output);
    }
}
