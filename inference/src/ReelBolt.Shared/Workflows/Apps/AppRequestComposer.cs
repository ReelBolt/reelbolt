using System.Globalization;
using System.Text.Json;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>
/// Composes the run's request text from the manifest's request template (docs/design/app-inputs.md 5.4, 5.5 item 4).
/// Each value goes in as a quoted span with inner quotes escaped and fence-like tokens neutralised, so a value can
/// never pose as framing; the person's notes follow under <see cref="NotesHeading"/>, the framing
/// <c>BriefFileResolver</c> already uses. With no template the request is exactly the notes, today's text. Agents
/// get inputs this way only: never in a system prompt, a custom agent prompt or a <c>[RUN BRIEF]</c> sentence.
/// </summary>
public static class AppRequestComposer
{
    /// <summary>The line the notes follow, byte for byte the one <c>BriefFileResolver</c> writes.</summary>
    public const string NotesHeading = "Notes for this run:";

    /// <summary>The request text, or the notes unchanged when the manifest has no request template.</summary>
    public static string? Compose(AppManifest manifest, IReadOnlyDictionary<string, JsonElement> values, string? notes)
    {
        if (string.IsNullOrWhiteSpace(manifest.RequestTemplate))
            return notes;
        Dictionary<string, AppManifestInput> inputs = (manifest.Inputs ?? [])
            .Where(i => i.Kind == AppManifestVocabulary.KindInput && i.Id is not null)
            .GroupBy(i => i.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // A line whose every input is empty is left out (an optional "Call to action" left blank adds nothing), and a
        // template left with no text at all gives today's request: the notes alone (docs/design/app-inputs.md 22).
        IEnumerable<string> lines = manifest.RequestTemplate.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(line => AppInputPlaceholders.Ids(line) is not { Count: > 0 } ids || ids.Any(id => DisplayOf(id).Length > 0));
        string text = AppInputPlaceholders.Substitute(string.Join('\n', lines), id => Quote(DisplayOf(id))).Trim();
        return text.Length == 0 ? notes : Frame(text, notes);

        string DisplayOf(string id) =>
            values.TryGetValue(id, out JsonElement v) && inputs.TryGetValue(id, out AppManifestInput? input) ? Display(input, v) : string.Empty;
    }

    /// <summary>Appends the notes under the heading: <c>text\n\nNotes for this run:\nnotes</c>; the text alone when there are none.</summary>
    public static string Frame(string text, string? notes) =>
        string.IsNullOrWhiteSpace(notes) ? text : $"{text}\n\n{NotesHeading}\n{notes}";

    /// <summary>A value as a quoted span: inner quotes escaped, fence-like tokens neutralised.</summary>
    public static string Quote(string value)
    {
        string safe = AppInputBinder.Neutralize(value)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("“", "\\“", StringComparison.Ordinal)
            .Replace("”", "\\”", StringComparison.Ordinal)
            .Replace("[END", "(END", StringComparison.OrdinalIgnoreCase)
            .Replace("[RUN BRIEF", "(RUN BRIEF", StringComparison.OrdinalIgnoreCase)
            .Replace("---", "—", StringComparison.Ordinal)
            .Replace("```", "'''", StringComparison.Ordinal)
            .Replace("User Request:", "User Request -", StringComparison.OrdinalIgnoreCase)
            .Replace(NotesHeading, "Notes for this run -", StringComparison.OrdinalIgnoreCase);
        return $"\"{safe}\"";
    }

    /// <summary>The words a value reads as: text as is, a choice by its label, a toggle by its on/off label.</summary>
    public static string Display(AppManifestInput input, JsonElement value)
    {
        AppInputTypes.TryParse(input.Type, out AppInputType type);
        switch (type)
        {
            case AppInputType.Choice:
                AppInputOption? option = (input.Options ?? []).FirstOrDefault(o => AppInputValueChecker.SameScalar(o.Value, value));
                return option?.Label ?? Raw(value);
            case AppInputType.Toggle:
                bool on = value.ValueKind == JsonValueKind.True;
                return on ? input.OnLabel ?? "yes" : input.OffLabel ?? "no";
            default:
                return Raw(value);
        }
    }

    private static string Raw(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetDouble().ToString(CultureInfo.InvariantCulture),
        JsonValueKind.True => "yes",
        JsonValueKind.False => "no",
        _ => string.Empty
    };
}
