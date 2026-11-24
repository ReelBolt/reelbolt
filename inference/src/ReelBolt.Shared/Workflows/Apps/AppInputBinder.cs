using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>What a <c>model</c> value resolves to for one step: the provider row and its model code (lane L4).</summary>
public sealed record AppModelTarget(Guid ProviderId, string? Model);

/// <summary>
/// Resolves a <c>model</c> input's capability id for one step, or null when it cannot be used (lane L4 supplies it,
/// re-reading <c>ModelCapabilityView</c> at queue and again at the snapshot; a value is never trusted).
/// </summary>
public delegate AppModelTarget? AppModelResolver(string inputId, Guid capabilityId, AppBindingStep step);

/// <summary>One step after binding, and the input values it consumed (the step-cache component of 10.1, lane L3).</summary>
public sealed record AppBoundStep(AppBindingStep Step, bool Changed, IReadOnlyDictionary<string, string> Consumed);

/// <summary>The bound steps, the composed request text and every binding that could not be honoured.</summary>
public sealed record AppInputBindResult(
    IReadOnlyList<AppBoundStep> Steps,
    string? ComposedRequest,
    IReadOnlyList<AppBindingProblem> Problems)
{
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// Binds app-input values into a copy of the steps (docs/design/app-inputs.md 5, 6): pure, deterministic and shared by
/// the API's dry run at queue (lane L2) and the engine's snapshot (lane L3). It edits parsed JSON, never JSON text, so a
/// value can never add a member or break a config: each <c>{{input.id}}</c> in an allowlisted string becomes the value
/// (an empty optional input becomes empty text); each <c>appInputs</c> key gets the value with its target's type, the
/// authored value staying as the fallback when the input has no value; the <c>appInputs</c> member is then removed. The
/// result holds no placeholder and no <c>appInputs</c>, so binding it again changes nothing (idempotent). Values must
/// already be canonical (<see cref="AppInputValueChecker"/>); the binder re-checks each against its target's limits.
/// The one binding no config names is the permission to send media: a <c>true</c> value of
/// <see cref="MediaEgressPermission.InputId"/> turns <c>allowSourceMediaEgress</c> on, on the steps that need it only.
/// A <c>@model</c> binding needs a <paramref name="resolveModel"/> (lane L4); without one it is reported, never guessed.
/// </summary>
public static class AppInputBinder
{
    /// <summary>Binds the values into the steps; the manifest supplies the request template and the input types.</summary>
    public static AppInputBindResult Bind(
        AppManifest manifest,
        IReadOnlyList<AppBindingStep> steps,
        IReadOnlyDictionary<string, JsonElement> values,
        string? notes = null,
        AppModelResolver? resolveModel = null,
        bool briefAvatar = false)
    {
        Dictionary<string, AppManifestInput> inputs = (manifest.Inputs ?? [])
            .Where(i => i.Kind == AppManifestVocabulary.KindInput && i.Id is not null)
            .GroupBy(i => i.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        bool grantsMediaEgress = MediaEgressPermission.IsRequested(values);
        MediaEgressBriefAvatar egressAvatar = briefAvatar ? MediaEgressBriefAvatar.Chosen : MediaEgressBriefAvatar.None;
        List<AppBindingProblem> problems = [];
        List<AppBoundStep> bound = [];
        foreach (AppBindingStep step in steps)
        {
            Dictionary<string, string> consumed = new(StringComparer.Ordinal);
            AppBindingStep result = BindStep(step, inputs, values, consumed, problems, resolveModel);

            // The permission to send media (docs/design/app-inputs.md "Permission to send media as an app field"): a grant
            // turns the switch on for this run on every step that needs it and has it off. A step it does not change
            // consumed nothing, so its cache key is exactly what it was.
            if (grantsMediaEgress && MediaEgressPermission.TryGrant(result, egressAvatar, out AppBindingStep granted))
            {
                result = granted;
                consumed[MediaEgressPermission.InputId] = "true";
            }

            if (step.StepType == StepType.Conditional)
            {
                foreach (string id in StaticBranchResolver.InputIds(step.ConditionExpression))
                    consumed[id] = values.TryGetValue(id, out JsonElement v) ? v.GetRawText() : string.Empty;
            }

            bound.Add(new AppBoundStep(result, !ReferenceEquals(result, step), consumed));
        }

        return new AppInputBindResult(bound, AppRequestComposer.Compose(manifest, values, notes), problems);
    }

    /// <summary>
    /// The canonical text of what one step consumed (sorted <c>id=value</c> lines), or null when it consumed nothing:
    /// the appended step-cache component, so a step that consumed no input hashes exactly as before (10.1).
    /// </summary>
    public static string? CacheComponent(AppBoundStep step)
    {
        if (step.Consumed.Count == 0)
            return null;
        StringBuilder sb = new();
        foreach ((string id, string value) in step.Consumed.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append(id.Length).Append(':').Append(id).Append('=').Append(value.Length).Append(':').Append(value).Append('|');
        return sb.ToString();
    }

    private static AppBindingStep BindStep(
        AppBindingStep step,
        IReadOnlyDictionary<string, AppManifestInput> inputs,
        IReadOnlyDictionary<string, JsonElement> values,
        Dictionary<string, string> consumed,
        List<AppBindingProblem> problems,
        AppModelResolver? resolveModel)
    {
        string? json = step.ConfigJson;
        if (string.IsNullOrWhiteSpace(json)
            || (!json.Contains(AppInputPlaceholders.Opening, StringComparison.Ordinal)
                && !json.Contains(AppInputPlaceholders.AppInputsKey, StringComparison.OrdinalIgnoreCase)))
            return step;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return step;
        }

        if (root is not JsonObject config)
            return step;

        string prefix = $"Step {step.StepOrder} ({step.StepType})";
        bool changed = SubstitutePlaceholders(config, step, values, consumed, problems, prefix);

        if (AppBindingAnalysis.FindAppInputs(config) is (string key, var node))
        {
            config.Remove(key);
            changed = true;
            if (node is JsonObject map)
            {
                foreach ((string setting, JsonNode? target) in map.ToList())
                {
                    if (!AppValueBinding.TryRead(target, out AppValueBinding? binding, out _))
                        continue; // reported at save by AppBindingAnalysis
                    string id = binding!.InputId;
                    AppBindableField? field = AppBindableCatalog.Find(step.StepType, AppBindingAnalysis.PatternOf(setting));
                    if (field is not { Mode: AppBindingMode.Value })
                    {
                        problems.Add(new AppBindingProblem(step.StepOrder, $"{prefix}: the setting '{setting}' cannot be set from the app."));
                        continue;
                    }

                    if (!values.TryGetValue(id, out JsonElement value))
                        continue; // the authored value stays as the fallback
                    consumed[id] = value.GetRawText();
                    if (!inputs.TryGetValue(id, out AppManifestInput? input) || !AppInputTypes.TryParse(input.Type, out AppInputType type))
                    {
                        problems.Add(new AppBindingProblem(step.StepOrder, $"{prefix}: '{setting}' names the input \"{id}\", which this app does not declare."));
                        continue;
                    }

                    string label = AppInputValueChecker.Label(input);
                    if (field.IsModel)
                    {
                        BindModel(config, step, field, id, type, value, problems, resolveModel, prefix);
                    }
                    else if (field.Path == AppBindableCatalog.SpokenTextKey)
                    {
                        BindSpokenText(config, step, label, type, value, problems, prefix);
                    }
                    else if (binding.IsMapped)
                    {
                        // A mapped binding writes what the author listed for this value; a value not listed keeps the authored one.
                        if (!binding.TryMap(value, out JsonElement mapped))
                            continue;
                        if (!AppValueBinding.TryWritten(field, mapped, out JsonNode? written, out string? error)
                            || !AppValueBinding.TrySet(config, setting, written, out error))
                            problems.Add(new AppBindingProblem(step.StepOrder, $"{prefix}: “{label}” {error}"));
                    }
                    else if (ToTarget(field, type, value, out JsonNode? written, out string? error))
                    {
                        if (!AppValueBinding.TrySet(config, field.IsNested ? setting : PropertyName(config, field.Path), written, out string? setError))
                            problems.Add(new AppBindingProblem(step.StepOrder, $"{prefix}: “{label}” {setError}"));
                    }
                    else
                    {
                        problems.Add(new AppBindingProblem(step.StepOrder, $"{prefix}: “{label}” {error}"));
                    }
                }
            }
        }

        return changed ? step with { ConfigJson = config.ToJsonString() } : step;
    }

    private static bool SubstitutePlaceholders(
        JsonNode node, AppBindingStep step, IReadOnlyDictionary<string, JsonElement> values,
        Dictionary<string, string> consumed, List<AppBindingProblem> problems, string prefix, string path = "")
    {
        bool changed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach ((string key, JsonNode? child) in obj.ToList())
                {
                    if (path.Length == 0 && string.Equals(key, AppInputPlaceholders.AppInputsKey, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string childPath = path.Length == 0 ? JsonNamingPolicy.CamelCase.ConvertName(key) : $"{path}.{JsonNamingPolicy.CamelCase.ConvertName(key)}";
                    if (child is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                    {
                        if (Substitute(v.GetValue<string>(), childPath) is { } replaced)
                        {
                            obj[key] = replaced;
                            changed = true;
                        }
                    }
                    else if (child is not null)
                    {
                        changed |= SubstitutePlaceholders(child, step, values, consumed, problems, prefix, childPath);
                    }
                }

                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    string childPath = $"{path}[{i}]";
                    if (array[i] is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                    {
                        if (Substitute(v.GetValue<string>(), childPath) is { } replaced)
                        {
                            array[i] = replaced;
                            changed = true;
                        }
                    }
                    else if (array[i] is { } child)
                    {
                        changed |= SubstitutePlaceholders(child, step, values, consumed, problems, prefix, childPath);
                    }
                }

                break;
        }

        return changed;

        string? Substitute(string text, string at)
        {
            if (!AppInputPlaceholders.ContainsReserved(text))
                return null;
            AppBindableField? field = AppBindableCatalog.Find(step.StepType, AppBindingAnalysis.PatternOf(at));
            if (field is not { Mode: AppBindingMode.Placeholder } || !AppInputPlaceholders.IsWellFormed(text))
            {
                problems.Add(new AppBindingProblem(step.StepOrder, $"{prefix}: '{at}' cannot take an app input."));
                return null;
            }

            string result = AppInputPlaceholders.Substitute(text, id =>
            {
                string value = values.TryGetValue(id, out JsonElement v) && v.ValueKind == JsonValueKind.String ? Neutralize(v.GetString()!) : string.Empty;
                consumed[id] = values.TryGetValue(id, out JsonElement raw) ? raw.GetRawText() : string.Empty;
                return value;
            }).Trim();
            if (field.MaxLength is { } cap && result.Length > cap)
                problems.Add(new AppBindingProblem(step.StepOrder, $"{prefix}: '{at}' would be {result.Length} characters, more than its {cap}."));
            return result;
        }
    }

    private static void BindModel(
        JsonObject config, AppBindingStep step, AppBindableField field, string inputId, AppInputType type, JsonElement value,
        List<AppBindingProblem> problems, AppModelResolver? resolveModel, string prefix)
    {
        if (type != AppInputType.Model || value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out Guid capabilityId))
        {
            problems.Add(new AppBindingProblem(step.StepOrder, $"{prefix}: '{field.Path}' needs a model input."));
            return;
        }

        if (resolveModel?.Invoke(inputId, capabilityId, step) is not { } target)
        {
            problems.Add(new AppBindingProblem(step.StepOrder, $"{prefix}: the chosen model cannot be used here."));
            return;
        }

        if (field.Path == AppBindableCatalog.StillModelKey)
        {
            // The still's model is the capability itself: its model code is read from the row when the still is made.
            config[PropertyName(config, "stillProviderId")] = target.ProviderId.ToString("D");
            return;
        }

        if (field.Path == AppBindableCatalog.VisionModelKey)
        {
            // One picture model for the step: the capability id is all the engine needs.
            config[PropertyName(config, "visionProviderId")] = target.ProviderId.ToString("D");
            return;
        }

        config[PropertyName(config, "providerId")] = target.ProviderId.ToString("D");
        if (target.Model is not null)
            config[PropertyName(config, "model")] = target.Model;
    }

    private static void BindSpokenText(
        JsonObject config, AppBindingStep step, string label, AppInputType type, JsonElement value, List<AppBindingProblem> problems, string prefix)
    {
        if (!AppInputTypes.IsText(type) || value.ValueKind != JsonValueKind.String)
        {
            problems.Add(new AppBindingProblem(step.StepOrder, $"{prefix}: '{AppBindableCatalog.SpokenTextKey}' needs a text input."));
            return;
        }

        string text = value.GetString()!;
        if (text.Length == 0)
            return; // an empty optional input keeps the authored lines
        if (!VoiceoverSpokenText.TrySplit(Neutralize(text), out IReadOnlyList<string> lines, out string? error)
            || !VoiceoverSpokenText.TryWrite(config, lines, out error))
            problems.Add(new AppBindingProblem(step.StepOrder, $"{prefix}: “{label}” {error}"));
    }

    /// <summary>The value written into the setting, with the setting's own type and limits.</summary>
    private static bool ToTarget(AppBindableField field, AppInputType type, JsonElement value, out JsonNode? written, out string? error)
    {
        written = null;
        error = null;
        if (!field.Types.Contains(type))
        {
            error = $"cannot set '{field.Path}' (it takes {string.Join(" or ", field.Types.Select(AppInputTypes.WireName))}).";
            return false;
        }

        if (type == AppInputType.Toggle)
        {
            bool on = value.ValueKind == JsonValueKind.True;
            written = field.ToggleOn is { } onValue ? JsonValue.Create(on ? onValue : field.ToggleOff ?? 0) : JsonValue.Create(on);
            if (field.ToggleOn is not null && field.Integer)
                written = JsonValue.Create((int)(on ? field.ToggleOn.Value : field.ToggleOff ?? 0));
            return true;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                double number = value.GetDouble();
                if (field.Min is { } min && number < min || field.Max is { } max && number > max)
                {
                    error = $"is outside what '{field.Path}' takes ({field.Min?.ToString(CultureInfo.InvariantCulture)} to {field.Max?.ToString(CultureInfo.InvariantCulture)}).";
                    return false;
                }

                if (field.Integer && number != Math.Floor(number))
                {
                    error = $"must be a whole number for '{field.Path}'.";
                    return false;
                }

                written = field.Integer ? JsonValue.Create((int)number) : JsonValue.Create(number);
                return true;
            case JsonValueKind.String:
                string text = value.GetString()!;
                if (field.Choices is { } choices)
                {
                    string? match = choices.FirstOrDefault(c => string.Equals(c, text, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        error = $"is not one of what '{field.Path}' takes ({string.Join(", ", choices)}).";
                        return false;
                    }

                    text = match;
                }

                written = JsonValue.Create(text);
                return true;
            default:
                error = $"has a value '{field.Path}' cannot take.";
                return false;
        }
    }

    /// <summary>
    /// A value never carries the reserved sequence into a config: <c>{{</c> becomes <c>{ {</c>, so a bound config holds no
    /// placeholder and binding stays idempotent whatever a person types.
    /// </summary>
    public static string Neutralize(string value)
    {
        while (value.Contains("{{", StringComparison.Ordinal))
            value = value.Replace("{{", "{ {", StringComparison.Ordinal);
        return value;
    }

    /// <summary>The member name as the config already spells it (configs read case-insensitively), else camelCase.</summary>
    private static string PropertyName(JsonObject config, string path)
    {
        foreach ((string key, _) in config)
        {
            if (string.Equals(key, path, StringComparison.OrdinalIgnoreCase))
                return key;
        }

        return path;
    }
}
