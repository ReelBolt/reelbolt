using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReelBolt.Shared.Text;

/// <summary>
/// Pure locator aid for a PROJECT file — the project-file counterpart of
/// <see cref="SandboxFileOutline"/>. Source files (ts/tsx/js/jsx) reuse that class; markdown gets
/// its heading tree with line numbers; anything else gets only its size, with a pointer to
/// <c>ReadProjectFileLines</c>. Heuristic by design (per-line regexes, no parser): a wrong or
/// missed entry costs one extra ranged read, never correctness.
/// </summary>
public static class ProjectFileOutline
{
    /// <summary>Headings listed before the outline reports <c>truncated</c>.</summary>
    public const int MaxHeadings = 200;

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"
    };

    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdx"
    };

    /// <summary>
    /// Every outline pattern is NonBacktracking (matching time is linear in the line, so the cost of a whole file is linear in
    /// its size, whatever the content), has a match timeout as a second bound, and lines longer than this are not matched at all: a hostile file (one huge line
    /// of spaces) must not cost seconds of CPU per outline call. A skipped line is simply absent from the outline.
    /// </summary>
    internal const int MaxMatchedLineChars = 2000;
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex AtxHeading = new(
        @"^ {0,3}(#{1,6})\s+(.+?)\s*#*\s*$", RegexOptions.NonBacktracking, MatchTimeout);

    private static readonly Regex FenceStart = new(
        @"^ {0,3}(`{3,}|~{3,})", RegexOptions.NonBacktracking, MatchTimeout);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Build(string? fileName, string? content)
    {
        content ??= string.Empty;
        string extension = Path.GetExtension(fileName ?? string.Empty);

        if (CodeExtensions.Contains(extension))
            return SandboxFileOutline.BuildOutline(content);

        if (MarkdownExtensions.Contains(extension))
            return BuildMarkdownOutline(content);

        return JsonSerializer.Serialize(new
        {
            kind = "text",
            totalLines = TextLineWindow.SplitLines(content).Length,
            totalChars = content.Length,
            note = "No outline is available for this file type. Use ReadProjectFileLines to read it in ranges."
        }, JsonOptions);
    }

    private static string BuildMarkdownOutline(string content)
    {
        string[] lines = TextLineWindow.SplitLines(content);

        List<MarkdownHeading> headings = [];
        bool truncated = false;
        char? openFence = null;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length > MaxMatchedLineChars)
                continue;

            // A `#` inside a fenced code block is a shell comment or a preprocessor line, not a heading.
            Match fence;
            Match heading;
            try
            {
                fence = FenceStart.Match(line);
                heading = fence.Success ? Match.Empty : AtxHeading.Match(line);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (fence.Success)
            {
                char marker = fence.Groups[1].Value[0];
                if (openFence is null)
                    openFence = marker;
                else if (marker == openFence)
                    openFence = null;
                continue;
            }

            if (openFence is not null)
                continue;

            if (!heading.Success)
                continue;

            if (headings.Count >= MaxHeadings)
            {
                truncated = true;
                break;
            }

            headings.Add(new MarkdownHeading(i + 1, heading.Groups[1].Length, heading.Groups[2].Value));
        }

        return JsonSerializer.Serialize(new
        {
            kind = "markdown",
            totalLines = lines.Length,
            totalChars = content.Length,
            headings,
            truncated
        }, JsonOptions);
    }

    private sealed record MarkdownHeading(int Line, int Level, string Text);
}
