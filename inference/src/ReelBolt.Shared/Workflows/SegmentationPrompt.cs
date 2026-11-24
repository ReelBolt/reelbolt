using System.Text;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// The segmentation prompt as the model receives it, for callers that cannot reference the engine project (the API).
/// A behaviour-preserving copy of <c>SegmentationTrackingService.SanitizePrompt</c>: control characters and whitespace
/// runs collapse to single spaces and the result is capped, so the content key it feeds is stable. A parity test in
/// the engine test project pins the two together.
/// </summary>
public static class SegmentationPrompt
{
    /// <summary>The engine's cap on a prompt (<c>SegmentationTrackingService.MaxPromptChars</c>).</summary>
    public const int MaxChars = 160;

    public static string Sanitize(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        StringBuilder cleaned = new(label.Length);
        bool space = false;
        foreach (char c in label.Trim())
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && cleaned.Length > 0)
            {
                cleaned.Append(' ');
            }

            space = false;
            cleaned.Append(c);
            if (cleaned.Length >= MaxChars) break;
        }

        return cleaned.ToString();
    }
}
