using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>How an input is consumed.</summary>
public enum AppInputUseKind
{
    /// <summary>A <c>{{input.id}}</c> placeholder inside an allowlisted string setting.</summary>
    Placeholder,

    /// <summary>A key of a step's <c>appInputs</c> map (the whole value, or <c>@model</c>).</summary>
    Value,

    /// <summary>A Conditional step's expression reads <c>input.id</c>.</summary>
    Condition,

    /// <summary>The manifest's request template (read by the agent steps).</summary>
    RequestTemplate,

    /// <summary>A <c>model</c> input naming an agent: every step of that agent.</summary>
    AgentModel
}

/// <summary>
/// One place an input is used. <see cref="StepOrder"/> is null for the request template. For a placeholder,
/// <see cref="PlaceholdersInTarget"/> and <see cref="LiteralLength"/> describe the whole string it sits in (for the
/// effective cap, docs/design/app-inputs.md 4.3). For an <c>appInputs</c> entry, <see cref="Binding"/> (appended) is the
/// entry itself: a mapped one lists what each of the input's values writes (docs/design/app-inputs.md 27).
/// </summary>
public sealed record AppInputUse(
    string InputId,
    AppInputUseKind Kind,
    int? StepOrder,
    string Where,
    AppBindableField? Field = null,
    int PlaceholdersInTarget = 0,
    int LiteralLength = 0,
    AppValueBinding? Binding = null);

/// <summary>A binding that cannot be honoured, as a plain sentence; <see cref="StepOrder"/> null for the manifest.</summary>
public sealed record AppBindingProblem(int? StepOrder, string Message);

/// <summary>Every use of every input (the consumer map), and the bindings that are malformed or not allowed.</summary>
public sealed record AppBindingMap(IReadOnlyList<AppInputUse> Uses, IReadOnlyList<AppBindingProblem> Problems)
{
    /// <summary>The uses of one input.</summary>
    public IReadOnlyList<AppInputUse> UsesOf(string inputId) => [.. Uses.Where(u => u.InputId == inputId)];

    /// <summary>The step orders that consume an input (the request template counts for every agent-reading step).</summary>
    public IReadOnlySet<int> ConsumingSteps(string inputId, IReadOnlyList<AppBindingStep> steps)
    {
        HashSet<int> orders = [];
        foreach (AppInputUse use in UsesOf(inputId))
        {
            if (use.StepOrder is { } order)
                orders.Add(order);
            else if (use.Kind == AppInputUseKind.RequestTemplate)
                orders.UnionWith(steps.Where(s => AppBindingAnalysis.ReadsRequest(s.StepType)).Select(s => s.StepOrder));
        }

        return orders;
    }
}

/// <summary>
/// Reads where inputs are bound (docs/design/app-inputs.md 5), from the steps alone: placeholders in config strings,
/// the per-step <c>appInputs</c> map, <c>input.*</c> in Conditional expressions, the manifest's request template and
/// agent <c>model</c> inputs. Pure and shared by the save validator, the manifest validator, the binder and (later) the
/// builder's "used in" list and the change-request seeds.
/// </summary>
public static partial class AppBindingAnalysis
{
    [GeneratedRegex(@"\[\d+\]")]
    private static partial Regex ArrayIndex();

    /// <summary>The steps whose agents read the run's request text (and so the composed request template).</summary>
    public static bool ReadsRequest(StepType type) => type is StepType.Agent or StepType.ReviewLoop or StepType.Parallel
        or StepType.EditRoom or StepType.GraphicsRoom or StepType.ColorGradeRoom or StepType.DailiesRoom;

    /// <summary>The consumer map of a manifest over its steps (manifest may be null: config bindings only).</summary>
    public static AppBindingMap Analyze(AppManifest? manifest, IReadOnlyList<AppBindingStep> steps)
    {
        List<AppInputUse> uses = [];
        List<AppBindingProblem> problems = [];
        foreach (AppBindingStep step in steps)
        {
            AnalyzeConfig(step, uses, problems);
            if (step.StepType == StepType.Conditional)
            {
                foreach (string id in StaticBranchResolver.InputIds(step.ConditionExpression))
                    uses.Add(new AppInputUse(id, AppInputUseKind.Condition, step.StepOrder, "condition"));
            }

            if (AppInputPlaceholders.ContainsReserved(step.ConditionExpression))
                problems.Add(new AppBindingProblem(step.StepOrder, $"Step {step.StepOrder} ({step.StepType}): a condition reads an input as input.name, never as {{{{input.name}}}}."));
        }

        if (manifest?.RequestTemplate is { } template)
        {
            if (!AppInputPlaceholders.IsWellFormed(template))
                problems.Add(new AppBindingProblem(null, "The request template has a {{input. that is not a complete placeholder like {{input.videoPrompt}}."));
            foreach (string id in AppInputPlaceholders.Ids(template).Distinct())
                uses.Add(new AppInputUse(id, AppInputUseKind.RequestTemplate, null, "requestTemplate"));
        }

        foreach (AppManifestInput input in manifest?.Inputs ?? [])
        {
            if (input.Kind != AppManifestVocabulary.KindInput || input.Id is null || input.Agent is not { } agent)
                continue;
            foreach (AppBindingStep step in steps.Where(s => s.AgentDefinitionId == agent && ReadsRequest(s.StepType)))
                uses.Add(new AppInputUse(input.Id, AppInputUseKind.AgentModel, step.StepOrder, "agent"));
        }

        return new AppBindingMap(uses, problems);
    }

    /// <summary>The bindable path pattern of a concrete JSON path (<c>trackObjects[2].label</c> to <c>trackObjects[].label</c>).</summary>
    public static string PatternOf(string path) => ArrayIndex().Replace(path, "[]");

    private static void AnalyzeConfig(AppBindingStep step, List<AppInputUse> uses, List<AppBindingProblem> problems)
    {
        if (string.IsNullOrWhiteSpace(step.ConfigJson)
            || (!step.ConfigJson.Contains(AppInputPlaceholders.Opening, StringComparison.Ordinal)
                && !step.ConfigJson.Contains(AppInputPlaceholders.AppInputsKey, StringComparison.OrdinalIgnoreCase)))
            return;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(step.ConfigJson);
        }
        catch (JsonException)
        {
            return; // the save validator reports invalid JSON on its own
        }

        if (root is not JsonObject config)
            return;

        string prefix = $"Step {step.StepOrder} ({step.StepType})";
        foreach ((string path, string value) in Strings(config, string.Empty, skipAppInputs: true))
        {
            if (!AppInputPlaceholders.ContainsReserved(value))
                continue;
            if (!AppInputPlaceholders.IsWellFormed(value))
            {
                problems.Add(new AppBindingProblem(step.StepOrder, $"{prefix}: '{path}' has a {{{{input. that is not a complete placeholder like {{{{input.videoPrompt}}}}."));
                continue;
            }

            AppBindableField? field = AppBindableCatalog.Find(step.StepType, PatternOf(path));
            if (field is not { Mode: AppBindingMode.Placeholder })
            {
                problems.Add(new AppBindingProblem(step.StepOrder,
                    $"{prefix}: '{path}' cannot take an app input. {PlaceholderFieldsSentence(step.StepType)}"));
                continue;
            }

            IReadOnlyList<string> ids = AppInputPlaceholders.Ids(value);
            int literal = AppInputPlaceholders.Literal(value).Length;
            foreach (string id in ids.Distinct())
                uses.Add(new AppInputUse(id, AppInputUseKind.Placeholder, step.StepOrder, path, field, ids.Count, literal));
        }

        if (FindAppInputs(config) is not (string key, var node))
            return;
        if (node is not JsonObject map)
        {
            problems.Add(new AppBindingProblem(step.StepOrder, $"{prefix}: '{key}' must map a setting name to an input id, for example {{\"durationSeconds\": \"clipLength\"}}."));
            return;
        }

        foreach ((string setting, JsonNode? target) in map)
        {
            AppBindableField? field = AppBindableCatalog.Find(step.StepType, PatternOf(setting));
            if (field is not { Mode: AppBindingMode.Value })
            {
                problems.Add(new AppBindingProblem(step.StepOrder,
                    $"{prefix}: the setting '{setting}' cannot be set from the app. {ValueFieldsSentence(step.StepType)}"));
                continue;
            }

            if (!AppValueBinding.TryRead(target, out AppValueBinding? binding, out string? shapeError))
            {
                problems.Add(new AppBindingProblem(step.StepOrder, $"{prefix}: '{key}.{setting}' {shapeError}."));
                continue;
            }

            if (setting.Contains("[]", StringComparison.Ordinal))
            {
                problems.Add(new AppBindingProblem(step.StepOrder, $"{prefix}: '{setting}' must name the item, for example {field.Path.Replace("[]", "[0]", StringComparison.Ordinal)}."));
                continue;
            }

            if (field.IsNested && AppValueBinding.Get(config, setting) is null)
            {
                problems.Add(new AppBindingProblem(step.StepOrder, $"{prefix}: '{setting}' is not in the step's settings; the app can only change a setting the step already has."));
                continue;
            }

            if (binding!.IsMapped)
            {
                if (field.IsPseudo)
                {
                    problems.Add(new AppBindingProblem(step.StepOrder, $"{prefix}: '{setting}' takes the field's own value; it cannot list values."));
                    continue;
                }

                string? unfit = binding.Values!
                    .Select(p => AppValueBinding.TryWritten(field, p.Value, out _, out string? why) ? null : $"\"{p.Key}\": {why}")
                    .FirstOrDefault(m => m is not null);
                if (unfit is not null)
                {
                    problems.Add(new AppBindingProblem(step.StepOrder, $"{prefix}: '{key}.{setting}' lists a value the setting cannot take ({unfit})."));
                    continue;
                }
            }

            if (field.Path == AppBindableCatalog.SpokenTextKey && !VoiceoverSpokenText.IsInline(step.ConfigJson))
            {
                problems.Add(new AppBindingProblem(step.StepOrder,
                    $"{prefix}: '{setting}' writes the words this step speaks, so the step's source must be Inline (lines written in the step)."));
                continue;
            }

            if (field.Path == AppBindableCatalog.StillModelKey && !AppValueBinding.IsTalkingHead(step))
            {
                problems.Add(new AppBindingProblem(step.StepOrder,
                    $"{prefix}: '{setting}' picks the model of a talking head's still picture, so the step must give the avatar speech (avatarAudio)."));
                continue;
            }

            uses.Add(new AppInputUse(binding.InputId, AppInputUseKind.Value, step.StepOrder, field.IsNested ? CamelCasePath(setting) : field.Path, field, Binding: binding));
        }
    }

    /// <summary>A concrete path with each member camelCased (<c>RegionEffects[0].Effect</c> to <c>regionEffects[0].effect</c>).</summary>
    private static string CamelCasePath(string path) =>
        string.Join('.', path.Split('.').Select(segment =>
        {
            int open = segment.IndexOf('[', StringComparison.Ordinal);
            return open < 0 ? CamelCase(segment) : CamelCase(segment[..open]) + segment[open..];
        }));

    /// <summary>The top-level <c>appInputs</c> member, matched case-insensitively like every config member.</summary>
    public static (string Key, JsonNode? Node)? FindAppInputs(JsonObject config)
    {
        foreach ((string key, JsonNode? node) in config)
        {
            if (string.Equals(key, AppInputPlaceholders.AppInputsKey, StringComparison.OrdinalIgnoreCase))
                return (key, node);
        }

        return null;
    }

    /// <summary>Every string value of a config, with its JSON path (property names as written, array indexes).</summary>
    internal static IEnumerable<(string Path, string Value)> Strings(JsonNode? node, string path, bool skipAppInputs)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach ((string key, JsonNode? child) in obj)
                {
                    if (skipAppInputs && path.Length == 0 && string.Equals(key, AppInputPlaceholders.AppInputsKey, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string childPath = path.Length == 0 ? CamelCase(key) : $"{path}.{CamelCase(key)}";
                    foreach ((string Path, string Value) s in Strings(child, childPath, skipAppInputs))
                        yield return s;
                }

                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    foreach ((string Path, string Value) s in Strings(array[i], $"{path}[{i}]", skipAppInputs))
                        yield return s;
                }

                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                yield return (path, value.GetValue<string>());
                break;
        }
    }

    private static string CamelCase(string key) => JsonNamingPolicy.CamelCase.ConvertName(key);

    private static string PlaceholderFieldsSentence(StepType type)
    {
        string[] paths = [.. AppBindableCatalog.For(type).Where(f => f.Mode == AppBindingMode.Placeholder).Select(f => f.Path)];
        return paths.Length == 0
            ? $"A {type} step has no text setting an app input can fill."
            : $"On a {type} step only these settings can hold {{{{input.name}}}}: {string.Join(", ", paths)}.";
    }

    private static string ValueFieldsSentence(StepType type)
    {
        string[] paths = [.. AppBindableCatalog.For(type).Where(f => f.Mode == AppBindingMode.Value).Select(f => f.Path)];
        return paths.Length == 0
            ? $"A {type} step has no setting an app input can set."
            : $"On a {type} step these can: {string.Join(", ", paths)}.";
    }
}
