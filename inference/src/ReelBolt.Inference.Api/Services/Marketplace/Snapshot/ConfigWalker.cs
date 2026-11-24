using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Services.Marketplace.Snapshot;

/// <summary>A reason a recipe cannot be published (or cannot be snapshotted safely).</summary>
public sealed record PortableRefusal(string Code, int? StepOrder, string Message);

internal sealed class WalkResult
{
    public List<PortableSlot> Slots { get; } = [];
    public List<PortableRemoval> Removed { get; } = [];
    public List<PortableRefusal> Refusals { get; } = [];
    public List<PortableTextField> Texts { get; } = [];
}

/// <summary>
/// Cleans one step config JSON against its record type and <see cref="MarketplaceFieldCatalog"/>: every property is kept, sanitised,
/// turned into an open slot or dropped, and anything the type does not declare or the catalog does not classify is dropped
/// (fail closed). Walking a config that was already cleaned changes nothing.
/// </summary>
internal static partial class ConfigWalker
{
    private const int MaxDepth = 10;

    [GeneratedRegex(@"^[A-Za-z0-9 _.:@/#%+\-$*\[\](),={}!]{0,200}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex KeepString();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,40}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SafeKey();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-?([0-9a-fA-F]{4}-?){3}[0-9a-fA-F]{12}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GuidShape();

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Humps();

    public const string RunBoundMessage = "A step reads a result from one of your earlier runs, which cannot be shared.";

    public static JsonNode? Walk(JsonNode? config, Type configType, int stepOrder, WalkResult result)
    {
        if (config is JsonObject obj)
            return WalkObject(obj, configType, string.Empty, stepOrder, result, 0);

        if (config is not null)
            result.Removed.Add(new PortableRemoval(stepOrder, "config", "A step setting block that was not a set of settings"));
        return null;
    }

    private static JsonObject WalkObject(JsonObject source, Type type, string path, int stepOrder, WalkResult res, int depth)
    {
        JsonObject output = [];
        if (depth > MaxDepth)
        {
            res.Removed.Add(new PortableRemoval(stepOrder, path, "A setting nested too deeply"));
            return output;
        }

        foreach (KeyValuePair<string, JsonNode?> entry in source)
        {
            if (depth == 0 && path.Length == 0 && string.Equals(entry.Key, AppInputPlaceholders.AppInputsKey, StringComparison.OrdinalIgnoreCase))
            {
                // The app-input bindings of the step: which of its settings an input fills. They name input ids of the recipe's own
                // manifest, never an organisation's data, so they travel with the recipe (see KeepAppInputs).
                if (KeepAppInputs(entry.Value, stepOrder, res) is { Count: > 0 } bindings)
                    output[AppInputPlaceholders.AppInputsKey] = bindings;
                continue;
            }

            ConfigProperty? property = ConfigReflection.Find(type, entry.Key);
            if (property is null)
            {
                res.Removed.Add(new PortableRemoval(stepOrder, path.Length == 0 ? "(config)" : path, "A setting this version does not recognise"));
                continue;
            }

            JsonNode? value = entry.Value;
            string field = path.Length == 0 ? property.JsonName : $"{path}.{property.JsonName}";
            if (value is null)
                continue;

            FieldRule? rule = MarketplaceFieldCatalog.RuleFor(property);
            if (rule is null)
            {
                if (!MarketplaceFieldCatalog.Descends(property))
                {
                    res.Removed.Add(new PortableRemoval(stepOrder, field, $"The {Human(property)} (not recognised, not shared)"));
                    continue;
                }

                JsonNode? cleaned = Descend(value, property, field, stepOrder, res, depth);
                if (cleaned is not null)
                    output[property.JsonName] = cleaned;
                else
                    res.Removed.Add(new PortableRemoval(stepOrder, field, $"The {Human(property)} (not in the expected shape)"));
                continue;
            }

            switch (rule.Class)
            {
                case FieldClass.Keep:
                    JsonNode? kept = property.Info.Name == "SourceInputProfiles" && property.IsDictionary
                        ? KeepSourceInputProfiles(value, field, stepOrder, res)
                        : Keep(value, property, field, stepOrder, res);
                    if (kept is not null)
                        output[property.JsonName] = kept;
                    else
                        res.Removed.Add(new PortableRemoval(stepOrder, field, $"The {Human(property)} (unusual value, not shared)"));
                    break;
                case FieldClass.Text:
                    JsonNode? text = Text(value, field, stepOrder, res);
                    if (text is not null)
                        output[property.JsonName] = text;
                    break;
                case FieldClass.Slot:
                    if (IsPresent(value))
                    {
                        res.Slots.Add(new PortableSlot(rule.SlotKind!, stepOrder, field, Capitalise(Human(property))));
                        res.Removed.Add(new PortableRemoval(stepOrder, field, $"The {Human(property)} you chose (whoever copies this picks their own)"));
                    }

                    break;
                default:
                    if (IsPresent(value))
                    {
                        if (rule.RefuseCode is { } code)
                            res.Refusals.Add(new PortableRefusal(code, stepOrder, RunBoundMessage));
                        res.Removed.Add(new PortableRemoval(stepOrder, field, $"The {Human(property)} (not shared)"));
                    }

                    break;
            }
        }

        RefuseRunBoundKinds(source, type, stepOrder, res);
        return output;
    }

    [GeneratedRegex(@"^(@[A-Za-z][A-Za-z0-9]{0,31}|[A-Za-z][A-Za-z0-9]{0,63}(\[[0-9]{1,3}\])?(\.[A-Za-z][A-Za-z0-9]{0,63}(\[[0-9]{1,3}\])?){0,4})$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BindingKey();

    /// <summary>
    /// The step's <c>appInputs</c> map, rebuilt from what it is allowed to say: a setting path as the key, and an input id or
    /// <c>{input, values}</c> as the value, where the values written are plain words, numbers or switches (words are cleaned and
    /// listed for the content check like every other text). Anything else is dropped and reported.
    /// </summary>
    private static JsonObject? KeepAppInputs(JsonNode? node, int stepOrder, WalkResult res)
    {
        if (node is not JsonObject map)
        {
            res.Removed.Add(new PortableRemoval(stepOrder, "appInputs", "The app-input bindings (not in the expected shape)"));
            return null;
        }

        JsonObject kept = [];
        foreach ((string key, JsonNode? entry) in map)
        {
            string field = $"appInputs.{key}";
            if (!BindingKey().IsMatch(key) || !AppValueBinding.TryRead(entry, out AppValueBinding? binding, out _) || binding is null)
            {
                res.Removed.Add(new PortableRemoval(stepOrder, field, "An app-input binding (unusual shape, not shared)"));
                continue;
            }

            if (binding.Values is null)
            {
                kept[key] = binding.InputId;
                continue;
            }

            JsonObject values = [];
            foreach ((string from, JsonElement to) in binding.Values)
            {
                if (!KeepString().IsMatch(from))
                    continue;
                if (to.ValueKind == JsonValueKind.String)
                {
                    string clean = MarketplaceText.Sanitize(to.GetString());
                    res.Texts.Add(new PortableTextField(stepOrder, $"{field}.{from}", clean));
                    values[from] = clean;
                }
                else
                {
                    values[from] = JsonNode.Parse(to.GetRawText());
                }
            }

            if (values.Count > 0)
                kept[key] = new JsonObject { [AppValueBinding.InputMember] = binding.InputId, [AppValueBinding.ValuesMember] = values };
        }

        return kept;
    }

    private static void RefuseRunBoundKinds(JsonObject source, Type type, int stepOrder, WalkResult res)
    {
        string? discriminator = type.Name switch
        {
            nameof(ReelBolt.Shared.Workflows.VideoSourceRef) => "kind",
            nameof(ReelBolt.Shared.Workflows.InsertContentRef) => "from",
            _ => null
        };
        if (discriminator is null)
            return;

        foreach (KeyValuePair<string, JsonNode?> kv in source)
        {
            if (!string.Equals(kv.Key, discriminator, StringComparison.OrdinalIgnoreCase) || kv.Value is not JsonValue v)
                continue;

            // The discriminator may arrive as a name or, from a hand-written config, as the enum's number.
            string? name = null;
            if (v.TryGetValue(out string? text))
                name = text?.Trim();
            else if (v.TryGetValue(out long number) && ConfigReflection.Find(type, kv.Key) is { } prop && prop.ValueType.IsEnum)
                name = Enum.ToObject(prop.ValueType, number).ToString();

            if (string.Equals(name, "StepResult", StringComparison.OrdinalIgnoreCase))
                res.Refusals.Add(new PortableRefusal("RUN_BOUND_SOURCE", stepOrder, RunBoundMessage));
        }
    }

    private static JsonNode? Descend(JsonNode value, ConfigProperty property, string field, int stepOrder, WalkResult res, int depth)
    {
        if (property.IsDictionary)
        {
            if (value is not JsonObject map)
                return null;
            JsonObject cleanedMap = [];
            foreach (KeyValuePair<string, JsonNode?> kv in map)
            {
                bool keyOk = property.KeyType == typeof(string) ? SafeKey().IsMatch(kv.Key) && !GuidShape().IsMatch(kv.Key)
                    : int.TryParse(kv.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _);
                if (!keyOk || kv.Value is not JsonObject inner)
                {
                    res.Removed.Add(new PortableRemoval(stepOrder, field, $"An entry of the {Human(property)} (unusual name, not shared)"));
                    continue;
                }

                cleanedMap[kv.Key] = WalkObject(inner, property.ValueType, $"{field}.{kv.Key}", stepOrder, res, depth + 1);
            }

            return cleanedMap;
        }

        if (property.IsCollection)
        {
            if (value is not JsonArray array)
                return null;
            JsonArray cleanedList = [];
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonObject element)
                    cleanedList.Add(WalkObject(element, property.ValueType, $"{field}[{i}]", stepOrder, res, depth + 1));
                else
                    res.Removed.Add(new PortableRemoval(stepOrder, $"{field}[{i}]", $"An entry of the {Human(property)} (not in the expected shape)"));
            }

            return cleanedList;
        }

        return value is JsonObject single ? WalkObject(single, property.ValueType, field, stepOrder, res, depth + 1) : null;
    }

    /// <summary>The largest source index a profile map may name; a compile reads a handful of sources.</summary>
    internal const int MaxSourceIndex = 999;

    /// <summary>
    /// A camera-profile map is a closed vocabulary: keys are small non-negative source indexes and values are <c>auto</c> or a name
    /// <see cref="ReelBolt.Shared.Editing.Color.CameraProfiles"/> knows. Any other entry is dropped and recorded (fail closed).
    /// </summary>
    private static JsonNode? KeepSourceInputProfiles(JsonNode value, string field, int stepOrder, WalkResult res)
    {
        if (value is not JsonObject map)
            return null;
        JsonObject kept = [];
        foreach (KeyValuePair<string, JsonNode?> kv in map)
        {
            bool keyOk = int.TryParse(kv.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index <= MaxSourceIndex
                && kept.Count < ReelBolt.Shared.Workflows.SourceInputProfileRules.MaxEntries;
            bool valueOk = kv.Value is JsonValue v && v.GetValueKind() == JsonValueKind.String
                && v.GetValue<string>() is { } name && (name == "auto" || ReelBolt.Shared.Editing.Color.CameraProfiles.IsKnown(name));
            if (keyOk && valueOk)
                kept[kv.Key] = kv.Value!.DeepClone();
            else
                res.Removed.Add(new PortableRemoval(stepOrder, field, "A camera profile entry that is not a known profile for a source (not shared)"));
        }

        return kept;
    }

    /// <summary>The most entries a kept map or list may carry into a public recipe.</summary>
    internal const int MaxKeptEntries = 200;

    private static JsonNode? Keep(JsonNode value, ConfigProperty property, string field, int stepOrder, WalkResult res)
    {
        // Kept strings (file names, paths, model names) are short closed-shape values, but they still reach a public page, so they
        // are listed for the content check like every other text.
        JsonNode? Scalar(JsonNode? node)
        {
            JsonNode? result = KeepScalar(node, property.ValueType);
            if (result is JsonValue jv && property.ValueType == typeof(string) && jv.TryGetValue(out string? s))
                res.Texts.Add(new PortableTextField(stepOrder, field, s));
            return result;
        }

        if (property.IsDictionary)
        {
            if (value is not JsonObject map)
                return null;
            JsonObject kept = [];
            foreach (KeyValuePair<string, JsonNode?> kv in map)
            {
                bool keyOk = property.KeyType == typeof(string) ? SafeKey().IsMatch(kv.Key) && !GuidShape().IsMatch(kv.Key)
                    : int.TryParse(kv.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _);
                if (kept.Count >= MaxKeptEntries)
                {
                    res.Removed.Add(new PortableRemoval(stepOrder, field, $"Entries of the {Human(property)} beyond {MaxKeptEntries} (not shared)"));
                    break;
                }

                if (keyOk && Scalar(kv.Value) is { } scalar)
                    kept[kv.Key] = scalar;
            }

            return kept;
        }

        if (property.IsCollection)
        {
            if (value is not JsonArray array)
                return null;
            JsonArray keptList = [];
            foreach (JsonNode? item in array)
            {
                if (keptList.Count >= MaxKeptEntries)
                {
                    res.Removed.Add(new PortableRemoval(stepOrder, field, $"Entries of the {Human(property)} beyond {MaxKeptEntries} (not shared)"));
                    break;
                }

                if (Scalar(item) is { } scalar)
                    keptList.Add(scalar);
            }

            return keptList;
        }

        return Scalar(value);
    }

    private static JsonNode? KeepScalar(JsonNode? node, Type type)
    {
        if (node is not JsonValue)
            return null;
        JsonValueKind kind = node.GetValueKind();
        if (type == typeof(bool))
            return kind is JsonValueKind.True or JsonValueKind.False ? node.DeepClone() : null;
        if (type.IsEnum)
            return CanonicalEnum(node, kind, type);

        if (type == typeof(string))
            return kind == JsonValueKind.String && node.GetValue<string>() is { } s && KeepString().IsMatch(s) && !GuidShape().IsMatch(s)
                ? node.DeepClone() : null;

        // Every remaining primitive is a number.
        return kind == JsonValueKind.Number && node.GetValue<JsonElement>().TryGetDouble(out double d) && double.IsFinite(d)
            ? node.DeepClone() : null;
    }

    /// <summary>A defined enum member written as its canonical NAME; anything else (undefined or fractional number, bool, odd string) is dropped.</summary>
    private static JsonNode? CanonicalEnum(JsonNode node, JsonValueKind kind, Type type)
    {
        object? member = null;
        if (kind == JsonValueKind.Number)
        {
            if (node.GetValue<JsonElement>().TryGetInt64(out long n))
            {
                try
                {
                    object candidate = Enum.ToObject(type, n);
                    if (Convert.ToInt64(candidate, CultureInfo.InvariantCulture) == n)
                        member = candidate;
                }
                catch (Exception ex) when (ex is ArgumentException or OverflowException)
                {
                    member = null;
                }
            }
        }
        else if (kind == JsonValueKind.String && node.GetValue<string>() is { } name && Enum.TryParse(type, name, true, out object? parsed))
        {
            member = parsed;
        }

        return member is not null && Enum.GetName(type, member) is { } canonical ? JsonValue.Create(canonical) : null;
    }

    private static JsonNode? Text(JsonNode value, string field, int stepOrder, WalkResult res)
    {
        if (value is JsonArray array)
        {
            JsonArray list = [];
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue item && item.GetValueKind() == JsonValueKind.String && TextValue(item.GetValue<string>(), $"{field}[{i}]", stepOrder, res) is { } clean)
                    list.Add(clean);
            }

            return list;
        }

        return value is JsonValue single && single.GetValueKind() == JsonValueKind.String
            ? TextValue(single.GetValue<string>(), field, stepOrder, res)
            : null;
    }

    private static JsonNode? TextValue(string raw, string field, int stepOrder, WalkResult res)
    {
        string clean = MarketplaceText.Sanitize(raw);
        if (clean.Length > MarketplaceText.MaxFieldChars)
        {
            res.Refusals.Add(new PortableRefusal(
                "TOO_LARGE", stepOrder, $"A text setting is longer than {MarketplaceText.MaxFieldChars} characters."));
            clean = clean[..MarketplaceText.MaxFieldChars];
        }

        if (clean.Length > 0)
            res.Texts.Add(new PortableTextField(stepOrder, field, clean));
        return JsonValue.Create(clean);
    }

    private static bool IsPresent(JsonNode value) => value.GetValueKind() switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.False => false,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetValue<string>())
            && !(Guid.TryParse(value.GetValue<string>(), out Guid g) && g == Guid.Empty),
        _ => true
    };

    internal static string Human(ConfigProperty property)
    {
        string words = Humps().Replace(property.Info.Name, " ").ToLowerInvariant();
        return words.EndsWith(" id", StringComparison.Ordinal) ? words[..^3] : words;
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
