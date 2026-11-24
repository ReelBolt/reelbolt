using System.Text.RegularExpressions;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Turns an ffmpeg stderr capture into a short, safe diagnostic for a step failure: the last few non-empty
/// lines, with anything path-shaped or key-shaped replaced. The raw capture is never surfaced - it can carry
/// scratch paths, object keys or signed URLs - but the last lines are what usually name the real fault
/// ("Cannot find color 'none'", "Invalid argument").
/// </summary>
public static class FfmpegStderrSummary
{
    private const int MaxLineChars = 200;

    // A run of 20+ token characters is treated as a possible secret/identifier and redacted; ordinary words,
    // option names and error phrases are shorter than that.
    private static readonly Regex LongToken = new(@"[A-Za-z0-9+_=\-]{20,}", RegexOptions.Compiled);

    /// <summary>The last <paramref name="maxLines"/> non-empty stderr lines, sanitised and joined with " | ".</summary>
    public static string Tail(string? stderr, int maxLines = 3)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return string.Empty;

        IEnumerable<string> lines = stderr
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .TakeLast(Math.Max(1, maxLines))
            .Select(SanitizeLine);

        return string.Join(" | ", lines);
    }

    private static string SanitizeLine(string line)
    {
        string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Contains('/') || tokens[i].Contains('\\') || tokens[i].Contains("://", StringComparison.Ordinal))
                tokens[i] = "<path>";
            else
                tokens[i] = LongToken.Replace(tokens[i], "<redacted>");
        }

        string cleaned = string.Join(' ', tokens);
        return cleaned.Length <= MaxLineChars ? cleaned : cleaned[..MaxLineChars];
    }
}
