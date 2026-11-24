using System.Text;

namespace ReelBolt.Shared.History;

/// <summary>One line of a unified-diff hunk: a context line (<c>" "</c>), an addition (<c>"+"</c>) or a removal (<c>"-"</c>).</summary>
public sealed record LineDiffLine(string Op, string Text);

/// <summary>
/// One unified-diff hunk. <see cref="FromStart"/>/<see cref="ToStart"/> are 1-based line numbers of the
/// first line the hunk carries on each side; when that side has no line in the hunk (a pure insertion or
/// deletion) the start is the number of lines before the hunk, exactly as a unified header reads.
/// </summary>
public sealed record LineDiffHunk(
    int FromStart,
    int FromCount,
    int ToStart,
    int ToCount,
    IReadOnlyList<LineDiffLine> Lines);

/// <summary>The hunks between two texts, or <see cref="TooLarge"/> when either side is past a contract cap.</summary>
public sealed record LineDiffResult(IReadOnlyList<LineDiffHunk> Hunks, bool TooLarge);

/// <summary>
/// A first-party Myers line diff (contract section 7). Both sides are split on <c>\n</c>; a line's line
/// ending is part of its identity, so <c>"a\r\n"</c> and <c>"a\n"</c> are different lines, but the
/// carriage return is stripped from the text a hunk shows. Identical input yields no hunks.
/// <para>
/// Caps: either side at most 1 MiB and 20,000 lines; a shown line is truncated to 2,000 characters.
/// Past either cap the result is <see cref="LineDiffResult.TooLarge"/> with no hunks. A diff whose edit
/// distance exceeds the internal bound falls back to one whole-middle replacement, which is still a
/// valid, bounded unified diff (the old text plus the hunks reconstructs the new text).
/// </para>
/// </summary>
public static class LineDiff
{
    /// <summary>Context lines kept on each side of a change.</summary>
    public const int ContextLines = 3;

    /// <summary>The most bytes either side may have (contract section 7).</summary>
    public const int MaxBytesPerSide = 1024 * 1024;

    /// <summary>The most lines either side may have (contract section 7).</summary>
    public const int MaxLinesPerSide = 20000;

    /// <summary>The most characters one hunk line may show (contract section 7).</summary>
    public const int MaxLineLength = 2000;

    /// <summary>
    /// The largest edit distance traced. Past it the whole differing middle is replaced; the trace's
    /// memory is bounded by this value, not by the input size.
    /// </summary>
    private const int MaxEditDistance = 1000;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Diffs <paramref name="oldText"/> against <paramref name="newText"/>.</summary>
    public static LineDiffResult Compute(string? oldText, string? newText, CancellationToken ct = default)
    {
        string before = oldText ?? string.Empty;
        string after = newText ?? string.Empty;

        ct.ThrowIfCancellationRequested();

        if (Encoding.UTF8.GetByteCount(before) > MaxBytesPerSide
            || Encoding.UTF8.GetByteCount(after) > MaxBytesPerSide)
        {
            return new LineDiffResult([], TooLarge: true);
        }

        string[] oldLines = SplitLines(before);
        string[] newLines = SplitLines(after);
        if (oldLines.Length > MaxLinesPerSide || newLines.Length > MaxLinesPerSide)
            return new LineDiffResult([], TooLarge: true);

        if (string.Equals(before, after, StringComparison.Ordinal))
            return new LineDiffResult([], TooLarge: false);

        List<DiffOp> ops = BuildOps(oldLines, newLines, ct);
        return new LineDiffResult(BuildHunks(ops), TooLarge: false);
    }

    /// <summary>Decodes UTF-8 strictly; returns null for bytes that are not valid UTF-8.</summary>
    public static string? DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Splits on <c>\n</c>. An empty text is no lines; <c>"a\n"</c> is two lines (the empty line after
    /// the terminator), so joining the same split restores the exact input.
    /// </summary>
    public static string[] SplitLines(string text) =>
        text.Length == 0 ? [] : text.Split('\n');

    private static List<DiffOp> BuildOps(string[] oldLines, string[] newLines, CancellationToken ct)
    {
        int oldCount = oldLines.Length;
        int newCount = newLines.Length;

        int prefix = 0;
        while (prefix < oldCount && prefix < newCount
               && string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < oldCount - prefix && suffix < newCount - prefix
               && string.Equals(oldLines[oldCount - 1 - suffix], newLines[newCount - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        List<DiffOp> ops = new(oldCount + newCount);
        for (int i = 0; i < prefix; i++)
            ops.Add(new DiffOp('=', oldLines[i]));

        int oldMiddle = oldCount - prefix - suffix;
        int newMiddle = newCount - prefix - suffix;

        if (oldMiddle > 0 && newMiddle > 0)
        {
            List<DiffOp>? middle = Myers(oldLines, prefix, oldMiddle, newLines, prefix, newMiddle, ct);
            if (middle is null)
                middle = WholeReplacement(oldLines, prefix, oldMiddle, newLines, prefix, newMiddle);

            ops.AddRange(middle);
        }
        else
        {
            for (int i = 0; i < oldMiddle; i++)
                ops.Add(new DiffOp('-', oldLines[prefix + i]));
            for (int i = 0; i < newMiddle; i++)
                ops.Add(new DiffOp('+', newLines[prefix + i]));
        }

        for (int i = oldCount - suffix; i < oldCount; i++)
            ops.Add(new DiffOp('=', oldLines[i]));

        return ops;
    }

    private static List<DiffOp> WholeReplacement(
        string[] oldLines, int oldStart, int oldCount,
        string[] newLines, int newStart, int newCount)
    {
        List<DiffOp> ops = new(oldCount + newCount);
        for (int i = 0; i < oldCount; i++)
            ops.Add(new DiffOp('-', oldLines[oldStart + i]));
        for (int i = 0; i < newCount; i++)
            ops.Add(new DiffOp('+', newLines[newStart + i]));
        return ops;
    }

    /// <summary>
    /// Myers' O(ND) shortest edit script over the two mid-slices, or null when the edit distance is past
    /// <see cref="MaxEditDistance"/>. The trace stores one fixed-size frontier per distance so memory is
    /// bounded by the cap rather than the input.
    /// </summary>
    private static List<DiffOp>? Myers(
        string[] a, int aStart, int aCount,
        string[] b, int bStart, int bCount,
        CancellationToken ct)
    {
        int n = aCount;
        int m = bCount;
        int offset = MaxEditDistance + 1;
        int[] frontier = new int[(2 * MaxEditDistance) + 3];
        List<int[]> trace = new(MaxEditDistance + 1);
        int found = -1;

        for (int d = 0; d <= MaxEditDistance; d++)
        {
            if ((d & 31) == 0)
                ct.ThrowIfCancellationRequested();

            trace.Add((int[])frontier.Clone());

            for (int k = -d; k <= d; k += 2)
            {
                int index = offset + k;
                int x;
                if (k == -d || (k != d && frontier[index - 1] < frontier[index + 1]))
                    x = frontier[index + 1];
                else
                    x = frontier[index - 1] + 1;

                int y = x - k;
                while (x < n && y < m
                       && string.Equals(a[aStart + x], b[bStart + y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }

                frontier[index] = x;
                if (x >= n && y >= m)
                {
                    found = d;
                    break;
                }
            }

            if (found >= 0)
                break;
        }

        if (found < 0)
            return null;

        List<DiffOp> reversed = new(n + m);

        int cx = n;
        int cy = m;
        for (int d = found; d > 0; d--)
        {
            int[] previous = trace[d];
            int k = cx - cy;
            int previousK = k == -d || (k != d && previous[offset + k - 1] < previous[offset + k + 1])
                ? k + 1
                : k - 1;
            int previousX = previous[offset + previousK];
            int previousY = previousX - previousK;

            while (cx > previousX && cy > previousY)
            {
                reversed.Add(new DiffOp('=', a[aStart + cx - 1]));
                cx--;
                cy--;
            }

            if (cx == previousX)
            {
                reversed.Add(new DiffOp('+', b[bStart + cy - 1]));
                cy--;
            }
            else
            {
                reversed.Add(new DiffOp('-', a[aStart + cx - 1]));
                cx--;
            }
        }

        while (cx > 0 && cy > 0)
        {
            reversed.Add(new DiffOp('=', a[aStart + cx - 1]));
            cx--;
            cy--;
        }

        while (cx > 0)
        {
            reversed.Add(new DiffOp('-', a[aStart + cx - 1]));
            cx--;
        }

        while (cy > 0)
        {
            reversed.Add(new DiffOp('+', b[bStart + cy - 1]));
            cy--;
        }

        reversed.Reverse();
        return reversed;
    }

    private static List<LineDiffHunk> BuildHunks(List<DiffOp> ops)
    {
        List<LineDiffHunk> hunks = new();
        if (ops.Count == 0)
            return hunks;

        int[] oldBefore = new int[ops.Count + 1];
        int[] newBefore = new int[ops.Count + 1];
        for (int i = 0; i < ops.Count; i++)
        {
            oldBefore[i + 1] = oldBefore[i] + (ops[i].Kind == '+' ? 0 : 1);
            newBefore[i + 1] = newBefore[i] + (ops[i].Kind == '-' ? 0 : 1);
        }

        List<(int Start, int End)> ranges = new();
        for (int i = 0; i < ops.Count; i++)
        {
            if (ops[i].Kind == '=')
                continue;

            int start = Math.Max(0, i - ContextLines);
            int end = Math.Min(ops.Count - 1, i + ContextLines);

            if (ranges.Count > 0 && start <= ranges[^1].End + 1)
                ranges[^1] = (ranges[^1].Start, Math.Max(ranges[^1].End, end));
            else
                ranges.Add((start, end));
        }

        foreach ((int start, int end) in ranges)
        {
            int oldCount = oldBefore[end + 1] - oldBefore[start];
            int newCount = newBefore[end + 1] - newBefore[start];
            int fromStart = oldCount == 0 ? oldBefore[start] : oldBefore[start] + 1;
            int toStart = newCount == 0 ? newBefore[start] : newBefore[start] + 1;

            List<LineDiffLine> lines = new(end - start + 1);
            for (int i = start; i <= end; i++)
                lines.Add(ToLine(ops[i]));

            hunks.Add(new LineDiffHunk(fromStart, oldCount, toStart, newCount, lines));
        }

        return hunks;
    }

    private static LineDiffLine ToLine(DiffOp op)
    {
        string text = op.Text;
        if (text.EndsWith('\r'))
            text = text[..^1];
        if (text.Length > MaxLineLength)
            text = text[..MaxLineLength];
        return new LineDiffLine(op.Kind == '=' ? " " : op.Kind.ToString(), text);
    }

    private readonly record struct DiffOp(char Kind, string Text);
}
