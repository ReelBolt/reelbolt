using System.Globalization;
using System.Text;
using System.Text.Json;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;

namespace ReelBolt.WorkflowEngine.Services.Review;

/// <summary>
/// Reads the vision model's visual-check answer against the fixed shape
/// <c>{"findings":[{"frame":3,"atSec":12.4,"kind":"…","detail":"…","severity":"…"}]}</c>.
///
/// <para>
/// <b>Strict.</b> The root must be an object with a <c>findings</c> array; every item must be an object
/// whose <c>frame</c> is an integer naming a frame that was sent, whose <c>kind</c> and <c>severity</c> are
/// one of the closed tokens (matched case-insensitively, normalised to lower case), and whose <c>detail</c>
/// is a non-blank string. One bad item makes the whole answer malformed (null), which the caller retries
/// once and then reports as "visual check unavailable" — never a step failure. The model's own
/// <c>atSec</c> is ignored: the time recorded is the one the frame was actually taken at.
/// </para>
///
/// <para>
/// <b>Untrusted prose.</b> A frame can carry text written to steer whoever reads its description, so a
/// <c>detail</c> is data: control and format characters dropped, JSON and markup punctuation
/// (<c>" ' ` { } [ ] &lt; &gt; \ :</c>) replaced by spaces — so it can never spell a verdict key or a block
/// header like <c>[VISUAL CHECK]</c> — whitespace collapsed, and capped at <see cref="MaxDetailChars"/>.
/// </para>
/// </summary>
public static class VisualCheckFindingsParser
{
    public const int MaxDetailChars = 240;
    public const int MaxFindings = 24;

    public static readonly IReadOnlyList<string> Kinds =
        ["text_overflow", "text_cropped", "outside_safe_area", "text_too_small", "broken_layout", "low_contrast", "other"];

    public static readonly IReadOnlyList<string> Severities = ["high", "medium", "low"];

    /// <summary>The findings, an empty list for a clean answer, or null when the answer is malformed.</summary>
    public static IReadOnlyList<VisualCheckFinding>? Parse(string? text, IReadOnlyList<VisualCheckFrameTime> sent)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string? json = RobustJsonExtractor.ExtractJsonObject(RobustJsonExtractor.CollapseDuplicateLeadingBrace(text));
        if (json is null)
            return null;

        Dictionary<int, double> times = sent.ToDictionary(f => f.Frame, f => f.AtSec);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("findings", out JsonElement findings)
                || findings.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            List<VisualCheckFinding> parsed = [];
            foreach (JsonElement item in findings.EnumerateArray())
            {
                VisualCheckFinding? finding = ReadFinding(item, times);
                if (finding is null)
                    return null;
                if (parsed.Count < MaxFindings)
                    parsed.Add(finding);
            }

            return parsed;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // InvalidOperationException/ArgumentException: a lone surrogate in the answer. Malformed, never a throw.
            return null;
        }
    }

    private static VisualCheckFinding? ReadFinding(JsonElement item, IReadOnlyDictionary<int, double> times)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        if (!item.TryGetProperty("frame", out JsonElement frameProp)
            || frameProp.ValueKind != JsonValueKind.Number
            || !frameProp.TryGetInt32(out int frame)
            || !times.TryGetValue(frame, out double atSec))
        {
            return null;
        }

        string? kind = ReadToken(item, "kind", Kinds);
        string? severity = ReadToken(item, "severity", Severities);
        if (kind is null || severity is null)
            return null;

        if (!item.TryGetProperty("detail", out JsonElement detailProp) || detailProp.ValueKind != JsonValueKind.String)
            return null;

        string detail = SanitizeDetail(detailProp.GetString());
        return detail.Length == 0 ? null : new VisualCheckFinding(frame, atSec, kind, detail, severity);
    }

    private static string? ReadToken(JsonElement item, string name, IReadOnlyList<string> allowed)
    {
        if (!item.TryGetProperty(name, out JsonElement prop) || prop.ValueKind != JsonValueKind.String)
            return null;

        string value = prop.GetString()?.Trim() ?? string.Empty;
        return allowed.FirstOrDefault(token => string.Equals(token, value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One line of plain text, safe to place in a prompt or a JSON string; see the type remarks.</summary>
    public static string SanitizeDetail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        StringBuilder builder = new(Math.Min(value.Length, MaxDetailChars + 1));
        bool pendingSpace = false;
        foreach (char c in value)
        {
            UnicodeCategory category = char.GetUnicodeCategory(c);
            if (char.IsWhiteSpace(c) || IsStructural(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
            if (builder.Length > MaxDetailChars)
                break;
        }

        string line = builder.ToString().Trim();
        return line.Length <= MaxDetailChars ? line : line[..(MaxDetailChars - 1)].TrimEnd() + "…";
    }

    private static bool IsStructural(char c) =>
        c is '"' or '\'' or '`' or '{' or '}' or '[' or ']' or '<' or '>' or '\\' or ':';
}
