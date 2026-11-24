using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReelBolt.Shared.Editing;

/// <summary>
/// Editor v2, P9: what a project preset may hold. Every kind is checked against the SAME closed
/// catalogues and ranges the timeline validator and the render config use, so applying a preset can
/// never put a value into a document (or a render) that the document (or the render) would refuse.
/// </summary>
public static class EditorPresetValidator
{
    public const string Transition = "transition";
    public const string Effect = "effect";
    public const string Mastering = "mastering";

    public static readonly IReadOnlyList<string> Kinds = [Transition, Effect, Mastering];

    /// <summary>Presets per project and kind.</summary>
    public const int MaxPresetsPerKind = 100;

    public const int MaxNameLength = 120;

    /// <summary>Every problem with a preset's kind, name and payload; empty when it is valid.</summary>
    public static IReadOnlyList<string> Validate(string? kind, string? name, JsonElement payload)
    {
        List<string> errors = [];
        if (kind is null || !Kinds.Contains(kind)) errors.Add($"kind must be one of {string.Join(", ", Kinds)}");
        if (string.IsNullOrWhiteSpace(name)) errors.Add("name must not be empty");
        else if (name.Trim().Length > MaxNameLength) errors.Add($"name must be at most {MaxNameLength} characters");
        if (payload.ValueKind != JsonValueKind.Object)
        {
            errors.Add("payload must be an object");
            return errors;
        }

        switch (kind)
        {
            case Transition:
                {
                    string? type = String(payload, "transitionType");
                    if (type is null || !EditTimelineValidator.TransitionTypes.Contains(type)) errors.Add("payload.transitionType must be a transition from the catalogue");
                    string? easing = String(payload, "easing");
                    if (easing is not null && !EditTimelineValidator.TransitionEasings.Contains(easing)) errors.Add("payload.easing must be an easing from the catalogue");
                    foreach (string key in new[] { "framesBefore", "framesAfter" })
                    {
                        if (!payload.TryGetProperty(key, out JsonElement v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int frames) || frames < 0 || frames > 600)
                            errors.Add($"payload.{key} must be an integer in [0, 600]");
                    }

                    Only(payload, errors, "transitionType", "easing", "framesBefore", "framesAfter");
                    break;
                }

            case Effect:
                {
                    if (!payload.TryGetProperty("effects", out JsonElement effects) || effects.ValueKind != JsonValueKind.Array || effects.GetArrayLength() == 0 || effects.GetArrayLength() > EditTimelineValidator.MaxEffects)
                    {
                        errors.Add($"payload.effects must be an array of 1..{EditTimelineValidator.MaxEffects} effects");
                        break;
                    }

                    int i = 0;
                    foreach (JsonElement effect in effects.EnumerateArray())
                    {
                        string? type = effect.ValueKind == JsonValueKind.Object ? String(effect, "type") : null;
                        if (type is null || !EditTimelineValidator.EffectTypes.Contains(type)) errors.Add($"payload.effects[{i}].type must be an effect from the catalogue");
                        if (effect.ValueKind == JsonValueKind.Object && effect.TryGetProperty("amount", out JsonElement amount) && (amount.ValueKind != JsonValueKind.Number || amount.GetDouble() < 0 || amount.GetDouble() > (type == "blur" ? EditTimelineValidator.MaxBlurAmount : 1)))
                            errors.Add($"payload.effects[{i}].amount must be in [0, {(type == "blur" ? EditTimelineValidator.MaxBlurAmount : 1)}]");
                        i++;
                    }

                    Only(payload, errors, "effects");
                    break;
                }

            case Mastering:
                Range(payload, "targetLufs", -36, -5, required: true, errors);
                Range(payload, "truePeakDbtp", -9, 0, required: true, errors);
                Range(payload, "loudnessRange", 1, 30, required: false, errors);
                Only(payload, errors, "targetLufs", "truePeakDbtp", "loudnessRange");
                break;
        }

        return errors;
    }

    private static string? String(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Range(JsonElement obj, string key, double min, double max, bool required, List<string> errors)
    {
        if (!obj.TryGetProperty(key, out JsonElement v))
        {
            if (required) errors.Add($"payload.{key} is required");
            return;
        }

        if (v.ValueKind != JsonValueKind.Number || v.GetDouble() < min || v.GetDouble() > max || !double.IsFinite(v.GetDouble()))
            errors.Add($"payload.{key} must be a number in [{min}, {max}]");
    }

    private static void Only(JsonElement obj, List<string> errors, params string[] keys)
    {
        foreach (JsonProperty property in obj.EnumerateObject())
        {
            if (!keys.Contains(property.Name)) errors.Add($"payload.{property.Name} is not a field of this preset kind");
        }
    }
}

/// <summary>
/// Editor v2, P9: the shape and size of a user's editor preferences. The client owns what an action
/// id or a layout means; the server bounds what it stores — a keymap is at most 200 entries of an
/// action id (<c>[a-zA-Z0-9.-]</c>, ≤ 64) to a key chord (≤ 32 printable characters, or empty to
/// unbind), and at most 50 layouts of at most 8 KB each.
/// </summary>
public static partial class EditorPreferencesValidator
{
    public const int MaxKeymapEntries = 200;
    public const int MaxLayouts = 50;
    public const int MaxLayoutBytes = 8 * 1024;

    [GeneratedRegex("^[A-Za-z0-9.-]{1,64}$")]
    private static partial Regex ActionId();

    public static IReadOnlyList<string> Validate(JsonElement keymap, JsonElement layouts)
    {
        List<string> errors = [];
        if (keymap.ValueKind != JsonValueKind.Object) errors.Add("keymap must be an object");
        else
        {
            int count = 0;
            foreach (JsonProperty entry in keymap.EnumerateObject())
            {
                count++;
                if (!ActionId().IsMatch(entry.Name)) errors.Add($"keymap action '{Trunc(entry.Name)}' is not an action id");
                if (entry.Value.ValueKind != JsonValueKind.String || entry.Value.GetString()!.Length > 32 || entry.Value.GetString()!.Any(char.IsControl))
                    errors.Add($"keymap['{Trunc(entry.Name)}'] must be a key chord of at most 32 printable characters");
            }

            if (count > MaxKeymapEntries) errors.Add($"keymap may hold at most {MaxKeymapEntries} entries");
        }

        if (layouts.ValueKind != JsonValueKind.Array) errors.Add("layouts must be an array");
        else
        {
            if (layouts.GetArrayLength() > MaxLayouts) errors.Add($"at most {MaxLayouts} layouts");
            int i = 0;
            foreach (JsonElement layout in layouts.EnumerateArray())
            {
                if (layout.ValueKind != JsonValueKind.Object || !layout.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Length > 80)
                    errors.Add($"layouts[{i}] must be an object with a name of 1..80 characters");
                if (layout.GetRawText().Length > MaxLayoutBytes) errors.Add($"layouts[{i}] is larger than {MaxLayoutBytes} bytes");
                i++;
            }
        }

        return errors;
    }

    private static string Trunc(string s) => s.Length > 40 ? s[..40] + "…" : s;
}

/// <summary>Editor v2, P9: review-comment bounds.</summary>
public static class EditTimelineCommentRules
{
    public const int MaxBodyLength = 4000;

    /// <summary>Comments per timeline (a review is a conversation, not a log).</summary>
    public const int MaxCommentsPerTimeline = 2000;

    public static IReadOnlyList<string> Validate(string? body, long frame, long? durationFrames)
    {
        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(body)) errors.Add("body must not be empty");
        else if (body.Length > MaxBodyLength) errors.Add($"body must be at most {MaxBodyLength} characters");
        if (frame < 0) errors.Add("frame must be >= 0");
        if (durationFrames is { } d && (d < 1 || d > 10_000_000)) errors.Add("durationFrames must be in [1, 10000000]");
        return errors;
    }
}
