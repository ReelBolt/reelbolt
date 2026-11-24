using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>
/// "Speak exactly this" (docs/design/app-inputs.md 27, owner-approved rule): a text input bound to a Voiceover step's
/// <c>@spokenText</c> becomes that step's <c>Inline</c> lines. The text goes in as one block starting at 0 s: each paragraph
/// (separated by a blank line) is one spoken line, in order, and the step places them back to back from 0 s
/// (<see cref="VoiceoverSource.Sequential"/>), so nobody has to say where a line starts. The words are spoken as written:
/// no writer agent sits in between. Limits: at most <see cref="MaxLines"/> paragraphs, each at most
/// <see cref="MaxLineChars"/> characters (the Voiceover sanitizer's own cap), and <see cref="MaxTotalChars"/> in all; a
/// value over them is refused with <c>APP_INPUT_INVALID</c> naming the field, never cut.
/// </summary>
public static partial class VoiceoverSpokenText
{
    /// <summary>The most paragraphs (spoken lines) one text may have.</summary>
    public const int MaxLines = 40;

    /// <summary>The longest paragraph: the Voiceover sanitizer reads no more of one line (<see cref="VoiceoverTextSanitizer.HardMaxRawChars"/>).</summary>
    public const int MaxLineChars = VoiceoverTextSanitizer.HardMaxRawChars;

    /// <summary>The longest text in all (the long-text cap).</summary>
    public const int MaxTotalChars = AppInputTypes.MaxLongText;

    [GeneratedRegex(@"\n[ \t]*\n\s*")]
    private static partial Regex BlankLine();

    [GeneratedRegex(@"\s*\n\s*")]
    private static partial Regex LineBreak();

    /// <summary>The spoken lines of <paramref name="text"/> (paragraphs, inner line breaks read as spaces), or why it cannot be spoken.</summary>
    public static bool TrySplit(string? text, out IReadOnlyList<string> lines, out string? error)
    {
        lines = [];
        error = null;
        string normalized = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.Length > MaxTotalChars)
        {
            error = $"is too long to speak: at most {MaxTotalChars} characters.";
            return false;
        }

        List<string> paragraphs = [.. BlankLine().Split(normalized)
            .Select(p => LineBreak().Replace(p, " ").Trim())
            .Where(p => p.Length > 0)];
        if (paragraphs.Count == 0)
        {
            error = "has nothing to speak.";
            return false;
        }

        if (paragraphs.Count > MaxLines)
        {
            error = $"has {paragraphs.Count} paragraphs; at most {MaxLines} can be spoken. Join some of them.";
            return false;
        }

        int tooLong = paragraphs.FindIndex(p => p.Length > MaxLineChars);
        if (tooLong >= 0)
        {
            error = $"has a paragraph (number {tooLong + 1}) longer than {MaxLineChars} characters. Split it with a blank line.";
            return false;
        }

        lines = paragraphs;
        return true;
    }

    /// <summary>
    /// Writes the lines into the step's <c>source</c>: <c>lines</c> = one <c>{text, startSec: 0}</c> per paragraph and
    /// <c>sequential: true</c>. The source must be <c>Inline</c> (checked at save, refused here otherwise).
    /// </summary>
    public static bool TryWrite(JsonObject config, IReadOnlyList<string> lines, out string? error)
    {
        error = null;
        if (config[AppValueBinding.MemberName(config, "source")] is not JsonObject source || !IsInline(source))
        {
            error = "speaks lines from another step, so it cannot take the words from the app (its source must be Inline).";
            return false;
        }

        JsonArray array = [];
        foreach (string line in lines)
            array.Add(new JsonObject { ["text"] = line, ["startSec"] = 0 });
        source[AppValueBinding.MemberName(source, "lines")] = array;
        source[AppValueBinding.MemberName(source, "sequential")] = true;
        return true;
    }

    /// <summary>True when a Voiceover config's source is <c>Inline</c> (the only kind whose lines the app can write).</summary>
    public static bool IsInline(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return false;
        try
        {
            return JsonNode.Parse(configJson) is JsonObject config
                && config[AppValueBinding.MemberName(config, "source")] is JsonObject source && IsInline(source);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsInline(JsonObject source) =>
        source[AppValueBinding.MemberName(source, "kind")] is JsonValue kind
        && (kind.TryGetValue(out string? word) ? string.Equals(word, nameof(VoiceoverSourceKind.Inline), StringComparison.OrdinalIgnoreCase)
            : kind.TryGetValue(out int number) && number == (int)VoiceoverSourceKind.Inline);
}
