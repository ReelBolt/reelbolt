namespace ReelBolt.Shared.Workflows.Review;

/// <summary>
/// The engine's own record at the very end of a review step's stored text output (what a review's re-run reuses and
/// carries out). It lives only in the text <c>Output</c> the engine reads back on resume; the JSON the run page and the
/// API's readers parse never holds it.
/// </summary>
public static class ReviewEngineRecord
{
    public const string Marker = "\n[engine-record]";

    /// <summary>The output without its engine record (unchanged when it has none).</summary>
    public static string Without(string? output)
    {
        string text = output ?? string.Empty;
        int at = text.IndexOf(Marker, StringComparison.Ordinal);
        return at < 0 ? text : text[..at];
    }
}
