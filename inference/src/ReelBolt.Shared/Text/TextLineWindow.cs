using System.Text;

namespace ReelBolt.Shared.Text;

/// <summary>
/// One numbered slice of a text file. <see cref="Content"/> carries a <c>{line,6}| </c> gutter on
/// every line so a model can quote a line number straight into an edit or a follow-up read.
/// </summary>
/// <param name="StartLine">The 1-based first line of the slice, after clamping.</param>
/// <param name="EndLine">The 1-based last line returned (a slice that starts past the end reports <see cref="TotalLines"/>).</param>
/// <param name="TotalLines">Lines in the whole text. An empty text is zero lines, not one.</param>
/// <param name="Truncated">True when the file has more lines after <see cref="EndLine"/>.</param>
public sealed record TextLineSlice(string Content, int StartLine, int EndLine, int TotalLines, bool Truncated);

/// <summary>
/// Pure ranged-read engine shared by <c>ReadSandboxFileLines</c> and <c>ReadProjectFileLines</c>, so
/// a sandbox file and a project file are sliced, numbered and clamped by exactly the same rules.
/// No I/O of its own, like <see cref="SandboxTextEditor"/>.
/// </summary>
public static class TextLineWindow
{
    /// <summary>The most lines one call may return; a request for more is clamped, never refused.</summary>
    public const int MaxLines = 2000;

    public static TextLineSlice Read(string? text, int startLine, int lineCount)
    {
        text ??= string.Empty;
        startLine = Math.Max(1, startLine);
        lineCount = Math.Clamp(lineCount, 1, MaxLines);

        string[] lines = SplitLines(text);
        int totalLines = lines.Length;

        int startIndex = Math.Min(startLine - 1, totalLines);
        int endIndexExclusive = Math.Min(startIndex + lineCount, totalLines);

        StringBuilder builder = new();
        for (int i = startIndex; i < endIndexExclusive; i++)
            builder.Append($"{i + 1,6}| ").Append(lines[i]).Append('\n');

        return new TextLineSlice(
            builder.ToString(), startLine, endIndexExclusive, totalLines, endIndexExclusive < totalLines);
    }

    /// <summary>
    /// Splits on LF or CRLF. A file with no content splits to a single empty-string "line"; that is
    /// zero lines here, which is also how <see cref="SandboxFileOutline"/> counts an empty file.
    /// </summary>
    public static string[] SplitLines(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
}
