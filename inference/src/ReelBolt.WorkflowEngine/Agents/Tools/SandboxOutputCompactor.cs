using ReelBolt.Shared.Text;
using System.Text;
using System.Text.RegularExpressions;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// What <see cref="SandboxOutputCompactor.Compact(string?)"/> hands a tool result.
/// </summary>
/// <param name="Text">The bounded text the model reads.</param>
/// <param name="TotalLines">How many lines the command really wrote, before anything was dropped.</param>
/// <param name="Truncated">
/// True when real content (not just repeated progress) was left out or clipped. A collapsed
/// <c>Rendered n/N</c> run does NOT set it: the summary line carries the final value, so nothing a
/// caller could act on is missing.
/// </param>
/// <param name="CollapsedLines">How many repeated/progress lines were folded away.</param>
/// <param name="Progress">The final Remotion progress values the output reported, or null when there were none.</param>
public sealed record CompactedOutput(
    string Text,
    int TotalLines,
    bool Truncated,
    int CollapsedLines,
    SandboxRenderProgress? Progress);

/// <summary>The last progress values a Remotion command printed. Any member the output never reported is null.</summary>
public sealed record SandboxRenderProgress(int? RenderedFrames, int? EncodedFrames, int? TotalFrames, int? BundlingPercent);

/// <summary>
/// Bounds a sandbox command's stdout/stderr before it becomes a tool result.
///
/// <para>
/// <b>Why this exists.</b> A successful <c>remotion render</c> of a ~3,500-frame film prints one
/// <c>Rendered n/N, time remaining …</c> line and one <c>Encoded n/N</c> line per frame, and the
/// tools used to return all of it. Measured on a production run (execution 1aa8d6f8, Author step),
/// a single render's tool result was <b>124–140 KB</b> — roughly 35–45 K tokens — and an agent's
/// tool results are replayed on every later turn of its loop: that run reached 10.4 M input tokens
/// across 42 tool calls and 15 renders. Everything the model needed from those 140 KB fits in a
/// few lines (the final frame count, the first lines, the last lines), so this keeps exactly those.
/// </para>
///
/// <para>
/// <b>What is kept.</b> Remotion's named progress families (<c>Rendered</c>, <c>Encoded</c>,
/// <c>Bundling</c>) become one summary line each carrying the FINAL value; any other run of lines
/// that differ only in their digits collapses the same way; of what remains, the first
/// <see cref="HeadLines"/> and last <see cref="TailLines"/> lines survive, with a marker saying how
/// many were omitted. The failure path uses the same compaction, so an error is neither lost nor
/// drowned. Pure and I/O-free so it is cheap to test exhaustively, like <see cref="SandboxTextEditor"/>.
/// </para>
/// </summary>
public static class SandboxOutputCompactor
{
    /// <summary>Lines kept from the start of the (collapsed) output — the command echo, the "Getting composition" preamble.</summary>
    public const int HeadLines = 20;

    /// <summary>Lines kept from the end — where a failure's stack trace and a success's final report live.</summary>
    public const int TailLines = 40;

    /// <summary>
    /// The most characters of stdout one tool result may carry. A few KB, against the 124–140 KB
    /// (35–45 K tokens) a single raw render result measured at before this bound existed.
    /// </summary>
    public const int MaxOutputChars = 6_000;

    /// <summary>The most characters of stderr/error text one tool result may carry; smaller because stdout already carries the tail.</summary>
    public const int MaxErrorChars = 2_000;

    /// <summary>A single pathological line (a minified blob, a base64 dump) is clipped to this.</summary>
    public const int MaxLineChars = 300;

    /// <summary>A run of this many consecutive lines that differ only in digits is folded into three lines.</summary>
    private const int MinRepeatRun = 4;

    /// <summary>A named progress family is folded only when it printed at least this many lines; two lines are cheaper verbatim.</summary>
    private const int MinProgressLines = 3;

    private const int MinHeadLines = 5;
    private const int MinTailLines = 10;

    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex LineBreaks = new(@"\r\n|\n|\r", RegexOptions.Compiled);
    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);

    private static readonly Regex RenderedLine = new(
        @"^\s*(?:\(\d+/\d+\)\s*)?Rendered\s+(\d+)\s*/\s*(\d+)", RegexOptions.Compiled);

    private static readonly Regex EncodedLine = new(
        @"^\s*(?:\(\d+/\d+\)\s*)?Encoded\s+(\d+)\s*/\s*(\d+)", RegexOptions.Compiled);

    private static readonly Regex BundlingLine = new(
        @"^\s*(?:Webpack\s+)?Bundling\s+(\d+)\s*%", RegexOptions.Compiled);

    private enum Family { None, Rendered, Encoded, Bundling }

    /// <summary>Removes ANSI colour/cursor escape sequences, which a CLI prints even to a pipe when forced.</summary>
    public static string StripAnsi(string text) => AnsiEscape.Replace(text, string.Empty);

    /// <summary>Compacts stdout. See the class remarks for the rules.</summary>
    public static CompactedOutput Compact(string? text) => Compact(text, MaxOutputChars);

    /// <summary>Compacts text to at most <paramref name="maxChars"/> characters.</summary>
    public static CompactedOutput Compact(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
            return new CompactedOutput(string.Empty, 0, false, 0, null);

        List<string> lines = [.. LineBreaks.Split(AnsiEscape.Replace(text, string.Empty))];
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        int totalLines = lines.Count;
        if (totalLines == 0)
            return new CompactedOutput(string.Empty, 0, false, 0, null);

        (List<string> afterProgress, SandboxRenderProgress? progress, int collapsedProgress) = CollapseProgressFamilies(lines);
        (List<string> afterRuns, int collapsedRuns) = CollapseRepeatedRuns(afterProgress);
        (string compacted, bool truncated) = KeepHeadAndTail(afterRuns, maxChars);

        return new CompactedOutput(compacted, totalLines, truncated, collapsedProgress + collapsedRuns, progress);
    }

    // ──────────────────────────────────────────────
    // 1. Named Remotion progress families
    // ──────────────────────────────────────────────

    private static (List<string> Lines, SandboxRenderProgress? Progress, int Collapsed) CollapseProgressFamilies(List<string> lines)
    {
        Dictionary<Family, int> counts = [];
        int? rendered = null, encoded = null, total = null, bundling = null;

        // Final values are read from EVERY line of a family, collapsed or not, so the structured
        // `progress` the tool reports is right even for a render short enough to print verbatim.
        foreach (string line in lines)
        {
            Family family = Classify(line, out int done, out int of);
            if (family == Family.None)
                continue;

            counts[family] = counts.GetValueOrDefault(family) + 1;
            switch (family)
            {
                case Family.Rendered: rendered = done; total = of; break;
                case Family.Encoded: encoded = done; total ??= of; break;
                case Family.Bundling: bundling = done; break;
            }
        }

        SandboxRenderProgress? progress = rendered is null && encoded is null && bundling is null
            ? null
            : new SandboxRenderProgress(rendered, encoded, total, bundling);

        HashSet<Family> collapsible = counts.Where(c => c.Value >= MinProgressLines).Select(c => c.Key).ToHashSet();
        if (collapsible.Count == 0)
            return (lines, progress, 0);

        List<string> result = new(lines.Count);
        HashSet<Family> summarised = [];
        int folded = 0;

        foreach (string line in lines)
        {
            Family family = Classify(line, out _, out _);
            if (family == Family.None || !collapsible.Contains(family))
            {
                result.Add(line);
                continue;
            }

            folded++;
            if (summarised.Add(family))
                result.Add(SummaryFor(family, counts[family], rendered, encoded, total, bundling));
        }

        // A summary line stands in for its family's lines, so it is not itself a folded line.
        return (result, progress, folded - summarised.Count);
    }

    private static Family Classify(string line, out int done, out int total)
    {
        done = 0;
        total = 0;

        Match match = RenderedLine.Match(line);
        if (match.Success)
        {
            done = ParseInt(match.Groups[1].Value);
            total = ParseInt(match.Groups[2].Value);
            return Family.Rendered;
        }

        match = EncodedLine.Match(line);
        if (match.Success)
        {
            done = ParseInt(match.Groups[1].Value);
            total = ParseInt(match.Groups[2].Value);
            return Family.Encoded;
        }

        match = BundlingLine.Match(line);
        if (match.Success)
        {
            done = ParseInt(match.Groups[1].Value);
            return Family.Bundling;
        }

        return Family.None;
    }

    private static int ParseInt(string digits) =>
        int.TryParse(digits, out int value) ? value : int.MaxValue;

    private static string SummaryFor(Family family, int lineCount, int? rendered, int? encoded, int? total, int? bundling) => family switch
    {
        Family.Rendered => $"[progress] Rendered {rendered}/{total} frames ({lineCount} progress lines collapsed)",
        Family.Encoded => $"[progress] Encoded {encoded}/{total} frames ({lineCount} progress lines collapsed)",
        Family.Bundling => $"[progress] Bundling {bundling}% ({lineCount} progress lines collapsed)",
        _ => string.Empty
    };

    // ──────────────────────────────────────────────
    // 2. Any other run of near-identical lines
    // ──────────────────────────────────────────────

    private static (List<string> Lines, int Collapsed) CollapseRepeatedRuns(List<string> lines)
    {
        List<string> result = new(lines.Count);
        int collapsed = 0;
        int index = 0;

        while (index < lines.Count)
        {
            string mask = Mask(lines[index]);
            int end = index + 1;

            // A blank line is not a "progress" line, and masking it would glue every paragraph gap together.
            while (mask.Length > 0 && end < lines.Count && Mask(lines[end]) == mask)
                end++;

            int run = end - index;
            if (run >= MinRepeatRun)
            {
                result.Add(lines[index]);
                result.Add($"… ({run - 2} similar lines omitted)");
                result.Add(lines[end - 1]);
                collapsed += run - 2;
            }
            else
            {
                for (int i = index; i < end; i++)
                    result.Add(lines[i]);
            }

            index = end;
        }

        return (result, collapsed);
    }

    private static string Mask(string line) => Digits.Replace(line.Trim(), "#");

    // ──────────────────────────────────────────────
    // 3. First N / last M, inside a character budget
    // ──────────────────────────────────────────────

    private static (string Text, bool Truncated) KeepHeadAndTail(List<string> lines, int maxChars)
    {
        bool truncated = false;

        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length > MaxLineChars)
            {
                lines[i] = lines[i][..MaxLineChars] + "…";
                truncated = true;
            }
        }

        int headCount = Math.Min(HeadLines, lines.Count);
        int tailCount = Math.Min(TailLines, lines.Count - headCount);

        // Shed from the MIDDLE of what is kept until the budget holds, keeping the end last: a
        // failure's cause and a render's final report are at the bottom.
        while (Measure(lines, headCount, tailCount) > maxChars && (headCount > MinHeadLines || tailCount > MinTailLines))
        {
            if (headCount > MinHeadLines)
                headCount--;
            else
                tailCount--;
        }

        int omitted = lines.Count - headCount - tailCount;
        StringBuilder builder = new();

        for (int i = 0; i < headCount; i++)
            builder.Append(lines[i]).Append('\n');

        if (omitted > 0)
        {
            builder.Append($"… [{omitted} lines omitted] …").Append('\n');
            truncated = true;
        }

        for (int i = lines.Count - tailCount; i < lines.Count; i++)
            builder.Append(lines[i]).Append('\n');

        string text = builder.ToString().TrimEnd('\n');

        // The floor on head/tail lines can still leave an over-budget result (300-char lines at the
        // floor). The cap is a promise, so it is enforced last, from the front: the tail survives.
        if (text.Length > maxChars)
        {
            text = "…" + text[^(maxChars - 1)..];
            truncated = true;
        }

        return (text, truncated);
    }

    private static int Measure(List<string> lines, int headCount, int tailCount)
    {
        int chars = 0;
        for (int i = 0; i < headCount; i++)
            chars += lines[i].Length + 1;
        for (int i = lines.Count - tailCount; i < lines.Count; i++)
            chars += lines[i].Length + 1;
        return chars;
    }
}
