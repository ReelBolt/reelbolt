using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReelBolt.Shared.Text;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>One exact search-and-replace of the assistant's targeted file edit (ordinal, never a regex).</summary>
public sealed record ProjectFileEdit(string OldText, string NewText, bool ReplaceAll = false);

/// <summary>One changed region of a line diff, with a little unchanged context either side.</summary>
internal sealed record DiffHunk(
    int Line,
    IReadOnlyList<string> ContextBefore,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> ContextAfter);

/// <summary>
/// The pure core of the assistant's targeted file tools (<c>ProposeEditProjectFile</c> and
/// <c>ProposeInsertProjectFileLines</c> with their Apply twins), shared by the tools and by the approval card so
/// the card can only ever show what the tool will really do. The edit and insert engines themselves live in
/// <c>ReelBolt.Shared.Text</c> (<see cref="SandboxTextEditor"/>) and are the same ones the agents' project-file
/// tools run. No I/O.
/// </summary>
internal static class ProjectFileTargetedEdits
{
    public const string OpEdit = "EditProjectFile";
    public const string OpInsert = "InsertProjectFileLines";

    /// <summary>Most edits in one call (the engine's own cap).</summary>
    public const int MaxEdits = 50;

    /// <summary>Longest <c>oldText</c>, <c>newText</c> or inserted <c>text</c>, in characters.</summary>
    public const int MaxTextChars = 64 * 1024;

    /// <summary>Same cap the agents' whole-file read uses: no unbounded model context.</summary>
    public const int MaxReadChars = 60_000;

    public const int ContextLines = 2;

    /// <summary>Most differing lines (removed + added) the edit script is computed for; beyond it the diff is one hunk.</summary>
    public const int MaxDiffedLines = 20_000;

    public static bool TryEdit(string content, IReadOnlyList<ProjectFileEdit>? edits, out string result, out string error)
    {
        result = string.Empty;
        if (edits is null || edits.Count == 0)
            return Fail("at least one edit is required", out error);
        if (edits.Count > MaxEdits)
            return Fail($"too many edits: {edits.Count} (max {MaxEdits})", out error);

        for (int i = 0; i < edits.Count; i++)
        {
            ProjectFileEdit? edit = edits[i];
            if (edit is null)
                return Fail($"edit {i + 1}: is empty", out error);
            if ((edit.OldText?.Length ?? 0) > MaxTextChars || (edit.NewText?.Length ?? 0) > MaxTextChars)
                return Fail($"edit {i + 1}: oldText and newText are limited to {MaxTextChars} characters each", out error);
        }

        SandboxEditOutcome outcome = SandboxTextEditor.Apply(
            content,
            edits.Select(e => new SandboxEdit(e.OldText ?? string.Empty, e.NewText ?? string.Empty, e.ReplaceAll)).ToList(),
            AssistantFileTools.MaxContentBytes);
        if (!outcome.Ok)
            return Fail(outcome.Error ?? "the edit could not be applied", out error);

        result = outcome.Content!;
        error = string.Empty;
        return true;
    }

    /// <summary>Inserts <paramref name="text"/> after line <paramref name="afterLine"/> (0 = top, -1 = append).</summary>
    public static bool TryInsert(string content, int afterLine, string? text, out string result, out string error)
    {
        result = string.Empty;
        if (string.IsNullOrEmpty(text))
            return Fail("text must not be empty", out error);
        if (text.Length > MaxTextChars)
            return Fail($"text is limited to {MaxTextChars} characters", out error);

        if ((long)content.Length + (long)text.Length + text.Count(c => c == '\n') > AssistantFileTools.MaxContentBytes)
            return Fail($"the result would exceed the {AssistantFileTools.MaxContentBytes} byte limit for a file", out error);

        int totalLines = TextLineWindow.SplitLines(content).Length;
        if (afterLine < -1 || afterLine > totalLines)
        {
            return Fail(
                $"afterLine {afterLine} is outside the file: it has {totalLines} line(s), so use 0..{totalLines} " +
                "(0 = before the first line), or -1 to append at the end.", out error);
        }

        SandboxEditOutcome outcome = SandboxTextEditor.InsertAt(content, afterLine == -1 ? -1 : afterLine + 1, text);
        if (!outcome.Ok)
            return Fail(outcome.Error ?? "the insert could not be applied", out error);

        result = outcome.Content!;
        error = string.Empty;
        return true;
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string EditPayload(Guid projectId, Guid fileId, string baseHash, IReadOnlyList<ProjectFileEdit> edits) =>
        JsonSerializer.Serialize(new
        {
            op = OpEdit,
            projectId,
            fileId,
            baseHash,
            edits = edits.Select(e => new { oldText = e.OldText ?? string.Empty, newText = e.NewText ?? string.Empty, replaceAll = e.ReplaceAll })
        });

    public static string InsertPayload(Guid projectId, Guid fileId, string baseHash, int afterLine, string text) =>
        JsonSerializer.Serialize(new { op = OpInsert, projectId, fileId, baseHash, afterLine, text });

    /// <summary>
    /// A line diff (Myers) of <paramref name="before"/> against <paramref name="after"/>, grouped into hunks. A change so
    /// large the edit script is not worth computing collapses to one hunk covering the differing middle.
    /// </summary>
    public static IReadOnlyList<DiffHunk> Diff(string before, string after)
    {
        string[] a = TextLineWindow.SplitLines(before);
        string[] b = TextLineWindow.SplitLines(after);

        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
            prefix++;
        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
            suffix++;

        string[] ma = a[prefix..(a.Length - suffix)];
        string[] mb = b[prefix..(b.Length - suffix)];
        if (ma.Length == 0 && mb.Length == 0)
            return [];

        // The edit script costs time and memory in proportion to the lines that differ; past this bound the whole differing
        // middle is ONE hunk, which the approval card then refuses as too large to review (no Myers run, no trace memory).
        List<(int AStart, int ALen, int BStart, int BLen)> regions = ma.Length + mb.Length > MaxDiffedLines
            ? [(0, ma.Length, 0, mb.Length)]
            : MyersRegions(ma, mb);
        List<DiffHunk> hunks = [];
        foreach ((int aStart, int aLen, int bStart, int bLen) in regions)
        {
            int line = prefix + aStart; // zero-based index into a
            int ctxStart = Math.Max(0, line - ContextLines);
            int afterIdx = line + aLen;
            hunks.Add(new DiffHunk(
                line + 1,
                a[ctxStart..line],
                ma[aStart..(aStart + aLen)],
                mb[bStart..(bStart + bLen)],
                a[afterIdx..Math.Min(a.Length, afterIdx + ContextLines)]));
        }

        return hunks;
    }

    private static List<(int, int, int, int)> MyersRegions(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        int max = Math.Min(n + m, 1500);
        if (n == 0 || m == 0)
            return [(0, n, 0, m)];

        int offset = max + 1;
        int[] v = new int[2 * max + 3];
        List<int[]> trace = [];
        int foundD = -1;

        for (int d = 0; d <= max && foundD < 0; d++)
        {
            trace.Add((int[])v.Clone());
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                    ? v[offset + k + 1]
                    : v[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    foundD = d;
                    break;
                }
            }
        }

        if (foundD < 0)
            return [(0, n, 0, m)];

        bool[] deleted = new bool[n];
        bool[] inserted = new bool[m];
        int cx = n, cy = m;
        for (int d = foundD; d > 0; d--)
        {
            int[] pv = trace[d];
            int k = cx - cy;
            int prevK = k == -d || (k != d && pv[offset + k - 1] < pv[offset + k + 1]) ? k + 1 : k - 1;
            int prevX = pv[offset + prevK];
            int prevY = prevX - prevK;
            if (prevK == k + 1)
                inserted[prevY] = true;
            else
                deleted[prevX] = true;
            cx = prevX;
            cy = prevY;
        }

        List<(int, int, int, int)> regions = [];
        int i = 0, j = 0;
        while (i < n || j < m)
        {
            if (i < n && j < m && !deleted[i] && !inserted[j])
            {
                i++;
                j++;
                continue;
            }

            int startI = i, startJ = j;
            while ((i < n && deleted[i]) || (j < m && inserted[j]))
            {
                if (i < n && deleted[i])
                    i++;
                if (j < m && inserted[j])
                    j++;
            }

            if (i == startI && j == startJ)
                return [(0, n, 0, m)];
            regions.Add((startI, i - startI, startJ, j - startJ));
        }

        return regions;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    /// <summary>Reads an integer argument exactly as the function binder does (numbers, and numeric strings).</summary>
    public static bool TryReadInt(JsonElement? element, out int value)
    {
        value = 0;
        if (element is not { } e)
            return false;
        try
        {
            value = e.Deserialize<int>(Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string Utf8Decode(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes);
}
