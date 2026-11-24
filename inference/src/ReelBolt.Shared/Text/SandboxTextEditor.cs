using System.Text;

namespace ReelBolt.Shared.Text;

/// <summary>
/// One find-and-replace hunk for <see cref="SandboxTextEditor.Apply"/>. <see cref="OldText"/> must
/// match the file's current content exactly (ordinal, not a regex) — the model is expected to have
/// copied it verbatim from a prior read. <see cref="ReplaceAll"/> opts into replacing every
/// occurrence instead of requiring the snippet to be unique.
/// </summary>
public sealed record SandboxEdit(string OldText, string NewText, bool ReplaceAll = false);

/// <summary>
/// Result of applying one or more <see cref="SandboxEdit"/>s. <see cref="Ok"/> false means NOTHING
/// was changed — see <see cref="SandboxTextEditor.Apply"/>'s all-or-nothing rule — and
/// <see cref="Content"/> is null in that case; the caller must not write it back.
/// </summary>
public sealed record SandboxEditOutcome(bool Ok, string? Content, string? Error, int Replacements, int EditsApplied);

/// <summary>
/// Pure, deterministic find-and-replace engine for editing an existing sandbox file without
/// re-emitting the whole thing. Deliberately has NO I/O of its own — <c>ReactRemotionSandboxTools</c>
/// owns reading the file from and writing it back to the sandbox HTTP API; this class only knows
/// how to transform a string given a list of edits, which is what makes it cheap to unit-test
/// exhaustively without mocking any HTTP surface.
/// </summary>
public static class SandboxTextEditor
{
    /// <summary>
    /// A generous but finite cap on how many hunks a single tool call may submit. There is no
    /// legitimate reason for a model to submit more than this in one call — beyond this point it
    /// is almost certainly re-deriving something closer to a whole-file rewrite anyway, which
    /// <c>WriteSandboxFile</c> already serves — and an unbounded list risks a single tool call
    /// doing an unreasonable amount of sequential string-search work.
    /// </summary>
    private const int MaxEdits = 50;

    /// <summary>
    /// Applies <paramref name="edits"/> to <paramref name="original"/> IN ORDER, each edit against
    /// the result of the previous one (so a later edit may legitimately target text a previous
    /// edit just introduced). All-or-nothing: if any edit fails to apply — ambiguous, not found,
    /// a no-op, or the list itself is invalid — the whole call fails with <c>Ok=false</c> and
    /// <c>Content=null</c>, and none of the edits are considered applied. A partially-applied
    /// multi-edit hunk would leave the sandbox file in a state neither the calling agent (whose
    /// mental model of the file assumed every edit succeeded) nor a human reviewer could reason
    /// about, so a single failure anywhere in the list voids the entire call rather than writing
    /// back a half-edited file.
    /// </summary>
    public static SandboxEditOutcome Apply(string original, IReadOnlyList<SandboxEdit> edits, long maxResultLength = long.MaxValue)
    {
        if (edits is null || edits.Count == 0)
            return new SandboxEditOutcome(false, null, "at least one edit is required", 0, 0);

        if (edits.Count > MaxEdits)
        {
            return new SandboxEditOutcome(
                false, null, $"too many edits: {edits.Count} (max {MaxEdits})", 0, 0);
        }

        string current = original;
        int totalReplacements = 0;

        for (int i = 0; i < edits.Count; i++)
        {
            SandboxEdit edit = edits[i];
            int editNumber = i + 1;

            if (string.IsNullOrEmpty(edit.OldText))
                return new SandboxEditOutcome(false, null, $"edit {editNumber}: oldText must not be empty", 0, 0);

            // Models routinely emit the wrong line-ending flavor for text they retype from memory
            // (e.g. LF when the file is CRLF, or vice versa). Rather than force every caller to
            // get this exactly right, normalize oldText/newText to whichever ending the file
            // itself actually uses BEFORE matching — this is the single biggest practical reason a
            // textually-correct-looking edit would otherwise fail to match and force a fallback to
            // a whole-file WriteSandboxFile rewrite, which is the exact failure mode this tool
            // exists to remove.
            (string oldText, string newText) = NormalizeLineEndings(current, edit.OldText, edit.NewText);

            if (string.Equals(oldText, newText, StringComparison.Ordinal))
            {
                return new SandboxEditOutcome(
                    false, null, $"edit {editNumber}: oldText and newText are identical — nothing to change", 0, 0);
            }

            int occurrences = CountOccurrences(current, oldText);
            if (occurrences == 0)
            {
                return new SandboxEditOutcome(
                    false, null,
                    $"edit {editNumber}: oldText not found in the file. Read the file again and copy the " +
                    "exact text, including indentation and line endings.",
                    0, 0);
            }

            if (occurrences > 1 && !edit.ReplaceAll)
            {
                return new SandboxEditOutcome(
                    false, null,
                    $"edit {editNumber}: oldText matched {occurrences} times — include more surrounding " +
                    "context to make it unique, or set replaceAll=true.",
                    0, 0);
            }

            // Projected length BEFORE allocating anything, so a replace-all with a large newText fails early.
            long replacing = edit.ReplaceAll ? occurrences : 1;
            long projected = current.Length + replacing * ((long)newText.Length - oldText.Length);
            if (projected > maxResultLength)
            {
                return new SandboxEditOutcome(
                    false, null, $"edit {editNumber}: the result would exceed the size limit ({maxResultLength} characters)", 0, 0);
            }

            current = ReplaceOrdinal(current, oldText, newText, edit.ReplaceAll ? int.MaxValue : 1);
            totalReplacements += occurrences;
        }

        return new SandboxEditOutcome(true, current, null, totalReplacements, edits.Count);
    }

    /// <summary>
    /// Inserts <paramref name="text"/> so that its first line becomes line
    /// <paramref name="beforeLine"/> (1-based, numbered exactly as <see cref="TextLineWindow"/> numbers
    /// a file for <c>ReadSandboxFileLines</c>), or appends it when <paramref name="beforeLine"/> is
    /// <c>-1</c> or one past the last line. No existing line is changed: the insertion point is
    /// always a line boundary, the inserted text is converted to the file's own line ending, and it
    /// is terminated with one so it never merges into the line it lands in front of. An append to a
    /// file with no trailing newline keeps it that way. Like <see cref="Apply"/>, a refusal returns
    /// <c>Ok=false</c> with <c>Content=null</c>.
    /// </summary>
    public static SandboxEditOutcome InsertAt(string original, int beforeLine, string text)
    {
        original ??= string.Empty;

        if (string.IsNullOrEmpty(text))
            return new SandboxEditOutcome(false, null, "text must not be empty", 0, 0);

        string eol = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string body = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (eol == "\r\n")
            body = body.Replace("\n", "\r\n", StringComparison.Ordinal);

        int totalLines = TextLineWindow.SplitLines(original).Length;
        bool append = beforeLine == -1 || beforeLine == totalLines + 1;

        if (!append && (beforeLine < 1 || beforeLine > totalLines))
        {
            return new SandboxEditOutcome(
                false, null,
                $"beforeLine {beforeLine} is outside the file: it has {totalLines} line(s), so use 1..{totalLines + 1}, " +
                "or -1 to append at the end.",
                0, 0);
        }

        if (append)
        {
            if (original.Length == 0 || original.EndsWith('\n'))
                return new SandboxEditOutcome(true, original + Terminate(body, eol), null, 0, 1);

            // No trailing newline: start the text on a line of its own and leave the file without one.
            string tail = body.EndsWith(eol, StringComparison.Ordinal) ? body[..^eol.Length] : body;
            return new SandboxEditOutcome(true, original + eol + tail, null, 0, 1);
        }

        int offset = LineStartOffset(original, beforeLine);
        return new SandboxEditOutcome(true, original.Insert(offset, Terminate(body, eol)), null, 0, 1);
    }

    private static string Terminate(string body, string eol) =>
        body.EndsWith(eol, StringComparison.Ordinal) ? body : body + eol;

    /// <summary>The character offset where 1-based <paramref name="line"/> starts; line 1 starts at 0.</summary>
    private static int LineStartOffset(string content, int line)
    {
        int offset = 0;
        for (int current = 1; current < line; current++)
            offset = content.IndexOf('\n', offset) + 1;
        return offset;
    }

    /// <summary>
    /// If exactly one of <paramref name="fileContent"/>/<paramref name="oldText"/> uses CRLF and
    /// the other only ever uses LF, strips the <c>\r</c>s from <paramref name="oldText"/> and
    /// <paramref name="newText"/> (or, in the reverse case, adds them back) so matching happens
    /// against whatever the file actually contains. Left untouched when both already agree, or
    /// when the snippet is too short to tell.
    /// </summary>
    private static (string OldText, string NewText) NormalizeLineEndings(string fileContent, string oldText, string newText)
    {
        bool fileHasCrlf = fileContent.Contains("\r\n", StringComparison.Ordinal);
        bool oldHasCrlf = oldText.Contains("\r\n", StringComparison.Ordinal);

        if (fileHasCrlf == oldHasCrlf)
            return (oldText, newText);

        return fileHasCrlf
            ? (LfToCrlf(oldText), LfToCrlf(newText))
            : (CrlfToLf(oldText), CrlfToLf(newText));
    }

    private static string CrlfToLf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string LfToCrlf(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal)
            ? text
            : text.Replace("\n", "\r\n", StringComparison.Ordinal);

    private static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0)
            return 0;

        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>
    /// Ordinal (never regex/culture-aware) find-and-replace, so a snippet containing regex
    /// metacharacters (<c>.*</c>, <c>$1</c>, etc.) is always treated as a literal string.
    /// </summary>
    private static string ReplaceOrdinal(string haystack, string oldText, string newText, int maxReplacements)
    {
        StringBuilder builder = new(haystack.Length);
        int index = 0;
        int replaced = 0;

        while (replaced < maxReplacements)
        {
            int found = haystack.IndexOf(oldText, index, StringComparison.Ordinal);
            if (found < 0)
                break;

            builder.Append(haystack, index, found - index);
            builder.Append(newText);
            index = found + oldText.Length;
            replaced++;
        }

        builder.Append(haystack, index, haystack.Length - index);
        return builder.ToString();
    }
}
