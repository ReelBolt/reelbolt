using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>
/// One entry of a step's <c>appInputs</c> map (docs/design/app-inputs.md 5.2, 27). The key is the setting (a top-level
/// member, a nested one such as <c>regionEffects[0].effect</c>, or a pseudo-key); the value is either the input id
/// (<c>"captions"</c>: the input's value is written, with the setting's type) or an object naming the input and what each
/// of its values writes: <c>{"input": "captions", "values": {"true": "Transcript"}}</c>. A mapped binding lets a switch or
/// a choice set a setting of another type (a switch sets <c>captionSource</c>, a choice sets a number of milliseconds) and
/// lets one switch set several settings at once (one entry per setting); a value with no entry in <see cref="Values"/>
/// leaves the authored value in place, exactly like an empty optional input. Keys of <see cref="Values"/> are the input's
/// values as text: <c>true</c>/<c>false</c> for a switch, the option value for a choice.
/// </summary>
public sealed record AppValueBinding(string InputId, IReadOnlyDictionary<string, JsonElement>? Values = null)
{
    /// <summary>The member naming the input in the object form.</summary>
    public const string InputMember = "input";

    /// <summary>The member mapping the input's values to the setting's values in the object form.</summary>
    public const string ValuesMember = "values";

    /// <summary>The most values one mapped binding may list (a choice offers at most this many).</summary>
    public const int MaxValues = AppInputTypes.MaxOptions;

    /// <summary>True when the entry maps the input's values instead of writing the value itself.</summary>
    public bool IsMapped => Values is not null;

    /// <summary>Reads one entry; false (with a plain reason) for anything that is neither form.</summary>
    public static bool TryRead(JsonNode? node, out AppValueBinding? binding, out string? error)
    {
        binding = null;
        error = null;
        if (node is JsonValue value && value.TryGetValue(out string? id))
        {
            if (!AppInputPlaceholders.IdPattern().IsMatch(id))
                return Fail("must name an input id", out error);
            binding = new AppValueBinding(id);
            return true;
        }

        if (node is not JsonObject obj)
            return Fail("must name an input id, or be {\"input\": id, \"values\": {...}}", out error);

        string? inputId = null;
        Dictionary<string, JsonElement>? values = null;
        foreach ((string key, JsonNode? member) in obj)
        {
            if (string.Equals(key, InputMember, StringComparison.OrdinalIgnoreCase))
            {
                if (member is not JsonValue v || !v.TryGetValue(out inputId) || !AppInputPlaceholders.IdPattern().IsMatch(inputId))
                    return Fail("must name an input id in \"input\"", out error);
            }
            else if (string.Equals(key, ValuesMember, StringComparison.OrdinalIgnoreCase))
            {
                if (member is not JsonObject map || map.Count is 0 or > MaxValues)
                    return Fail($"must list 1 to {MaxValues} values in \"values\"", out error);
                values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach ((string from, JsonNode? to) in map)
                {
                    if (string.IsNullOrWhiteSpace(from) || from.Length > 64)
                        return Fail("has a value key that is empty or too long", out error);
                    if (to is not JsonValue scalar || scalar.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                        return Fail($"maps \"{from}\" to something that is not a word, a number, true or false", out error);
                    values[from] = JsonSerializer.SerializeToElement(scalar);
                }
            }
            else
            {
                return Fail($"has an unknown member \"{key}\" (only \"input\" and \"values\")", out error);
            }
        }

        if (inputId is null)
            return Fail("must name an input id in \"input\"", out error);
        if (values is null)
            return Fail("must list what each value writes in \"values\" (or be just the input id)", out error);
        binding = new AppValueBinding(inputId, values);
        return true;
    }

    /// <summary>The text key an input value is listed under: <c>true</c>/<c>false</c>, the word, or the number.</summary>
    public static string KeyOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number => value.GetDouble().ToString("R", CultureInfo.InvariantCulture),
        _ => value.GetRawText()
    };

    /// <summary>True when <paramref name="key"/> lists the input value <paramref name="value"/> (numbers by value).</summary>
    public static bool KeyMatches(string key, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => double.TryParse(key, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && n == value.GetDouble(),
        _ => string.Equals(key, KeyOf(value), StringComparison.Ordinal)
    };

    /// <summary>The setting value listed for <paramref name="inputValue"/>, or false when none is (the authored value stays).</summary>
    public bool TryMap(JsonElement inputValue, out JsonElement mapped)
    {
        mapped = default;
        if (Values is null)
            return false;
        foreach ((string key, JsonElement to) in Values)
        {
            if (KeyMatches(key, inputValue))
            {
                mapped = to;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The value written into <paramref name="field"/> for a mapped <paramref name="value"/>, with the setting's own type and
    /// limits (a choice word spelt as the setting spells it), or the plain reason it does not fit.
    /// </summary>
    public static bool TryWritten(AppBindableField field, JsonElement value, out JsonNode? written, out string? error)
    {
        written = null;
        error = null;
        switch (field.ValueKind)
        {
            case AppBindableValueKind.Boolean:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Fail($"'{field.Path}' takes true or false", out error);
                written = JsonValue.Create(value.ValueKind == JsonValueKind.True);
                return true;
            case AppBindableValueKind.Number:
                if (value.ValueKind != JsonValueKind.Number || !double.IsFinite(value.GetDouble()))
                    return Fail($"'{field.Path}' takes a number", out error);
                double number = value.GetDouble();
                if (field.Min is { } min && number < min || field.Max is { } max && number > max)
                    return Fail($"{value.GetRawText()} is outside what '{field.Path}' takes ({field.Min?.ToString(CultureInfo.InvariantCulture) ?? "any"} to {field.Max?.ToString(CultureInfo.InvariantCulture) ?? "any"})", out error);
                if (field.Integer && number != Math.Floor(number))
                    return Fail($"'{field.Path}' takes whole numbers", out error);
                written = field.Integer ? JsonValue.Create((long)number) : JsonValue.Create(number);
                return true;
            default:
                if (value.ValueKind != JsonValueKind.String)
                    return Fail($"'{field.Path}' takes a word", out error);
                string text = value.GetString()!;
                if (field.Choices is { } choices)
                {
                    string? match = choices.FirstOrDefault(c => string.Equals(c, text, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                        return Fail($"\"{text}\" is not one of what '{field.Path}' takes ({string.Join(", ", choices)})", out error);
                    text = match;
                }

                written = JsonValue.Create(text);
                return true;
        }
    }

    /// <summary>
    /// Writes <paramref name="value"/> at a concrete setting path (<c>ratio</c>, <c>regionEffects[0].effect</c>) of a parsed
    /// config. Every object and array item on the way must already be there (a binding replaces a member of an item the
    /// author wrote, it never adds an item); the last member keeps the config's own spelling, else camelCase.
    /// </summary>
    public static bool TrySet(JsonObject config, string path, JsonNode? value, out string? error)
    {
        error = null;
        if (!TryWalk(config, path, out JsonObject? parent, out string? member, out error))
            return false;
        parent![MemberName(parent, member!)] = value;
        return true;
    }

    /// <summary>The value at a concrete setting path, or null when the path is not there.</summary>
    public static JsonNode? Get(JsonObject config, string path) =>
        TryWalk(config, path, out JsonObject? parent, out string? member, out _) ? parent![MemberName(parent, member!)] : null;

    private static bool TryWalk(JsonObject config, string path, out JsonObject? parent, out string? member, out string? error)
    {
        parent = null;
        member = null;
        error = null;
        string[] segments = path.Split('.');
        JsonObject current = config;
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            int open = segment.IndexOf('[', StringComparison.Ordinal);
            string name = open < 0 ? segment : segment[..open];
            if (name.Length == 0)
                return Fail($"'{path}' is not a setting path", out error);
            if (i == segments.Length - 1 && open < 0)
            {
                parent = current;
                member = name;
                return true;
            }

            JsonNode? child = current[MemberName(current, name)];
            if (open >= 0)
            {
                if (!segment.EndsWith(']') || !int.TryParse(segment[(open + 1)..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                    return Fail($"'{path}' is not a setting path (write an item as name[0])", out error);
                if (child is not JsonArray array || index >= array.Count)
                    return Fail($"'{path}': the step has no item {index} in {name}", out error);
                child = array[index];
                if (i == segments.Length - 1)
                    return Fail($"'{path}' names a whole item, not a setting of it", out error);
            }

            if (child is not JsonObject next)
                return Fail($"'{path}': the step has no {name} to set", out error);
            current = next;
        }

        return Fail($"'{path}' is not a setting path", out error);
    }

    /// <summary>The member name as the object already spells it (configs read case-insensitively), else as given.</summary>
    internal static string MemberName(JsonObject obj, string name)
    {
        foreach ((string key, _) in obj)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return key;
        }

        return name;
    }

    /// <summary>True for a VideoGenerate step whose config gives the avatar speech (a talking head, the only step a still model serves).</summary>
    public static bool IsTalkingHead(AppBindingStep step) =>
        step.StepType == StepType.VideoGenerate && AppModelRules.JobFor(step) == AppModelRules.TalkingHead;

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }
}
