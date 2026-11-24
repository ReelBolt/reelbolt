using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Services.Marketplace.Snapshot;

/// <summary>Slot kinds a copier has to fill (<c>provider</c>, <c>file</c>, <c>voice</c>).</summary>
public static class PortableSlotKinds
{
    public const string Provider = "provider";
    public const string File = "file";
    public const string Voice = "voice";
}

/// <summary>One id that was removed from the snapshot and must be chosen again by whoever copies the recipe.</summary>
public sealed record PortableSlot(string Kind, int StepOrder, string Field, string Label);

/// <summary>Human words for the publish dialog's "what will be removed" list. Never carries an id.</summary>
public sealed record PortableRemoval(int StepOrder, string Field, string What);

/// <summary>A free-text value that can reach a model or a viewer.</summary>
public sealed record PortableTextField(int? StepOrder, string Field, string Text);

/// <summary>One step of the portable recipe: the step's own settings with every author-owned id removed.</summary>
public sealed record PortableStep(
    int Order,
    StepType StepType,
    AgentType? BuiltInAgentType,
    string? Label,
    JsonNode? Config,
    string? ConditionExpression,
    string? LoopSourceExpression,
    int? LoopTargetStepOrder,
    int MaxIterations,
    int? MinScore,
    JsonNode? InputMapping,
    string? TrueBranchStepOrder,
    string? FalseBranchStepOrder,
    IReadOnlyList<AgentType>? ParallelAgentTypes,
    AgentInputContextMode? AgentInputContextMode = null,
    IReadOnlyList<int>? SelectedPriorStepOrders = null,
    StepCacheMode? CacheMode = null,
    bool RegisterOutputAsProjectFile = false);

/// <summary>
/// The versioned, canonical snapshot a published recipe stores (<c>schemaVersion = 1</c>). It is built only by
/// <see cref="PortableRecipeBuilder"/>, which strips every author-owned id; a snapshot that still carries one never
/// leaves the builder (see <see cref="SnapshotLeakGuard"/>).
/// </summary>
public sealed record PortableRecipe(
    int SchemaVersion,
    IReadOnlyList<PortableStep> Steps,
    AppManifest? Manifest,
    IReadOnlyList<PortableSlot> Slots,
    IReadOnlyList<PortableRemoval> Removed)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>The stable serialised form (sorted keys, no indentation, no nulls): equal recipes give equal bytes.</summary>
    public string ToCanonicalJson() => PortableRecipeJson.Serialize(this);

    /// <summary>
    /// Every free-text value (each <c>Text</c> field of every step config, step labels, expressions, mapping strings and the
    /// name-like manifest labels) for the publish gate's content check and the detail DTO's <c>prompts[]</c>.
    /// </summary>
    public IReadOnlyList<PortableTextField> TextFields()
    {
        List<PortableTextField> texts = [];
        foreach (PortableStep step in Steps)
        {
            AddIfPresent(texts, step.Order, "label", step.Label);
            AddIfPresent(texts, step.Order, "conditionExpression", step.ConditionExpression);
            AddIfPresent(texts, step.Order, "loopSourceExpression", step.LoopSourceExpression);
            if (step.InputMapping is not null)
                PortableJsonText.CollectStrings(step.InputMapping, "inputMapping", (field, text) => texts.Add(new(step.Order, field, text)));

            if (step.Config is not null && MarketplaceStepPublishability.ConfigTypeOf(step.StepType) is { } configType)
            {
                WalkResult walked = new();
                ConfigWalker.Walk(step.Config, configType, step.Order, walked);
                texts.AddRange(walked.Texts);
            }
        }

        if (Manifest is { } manifest)
        {
            AddIfPresent(texts, null, "manifest.title", manifest.Title);
            AddIfPresent(texts, null, "manifest.tagline", manifest.Tagline);
            AddIfPresent(texts, null, "manifest.requestTemplate", manifest.RequestTemplate);
            AddIfPresent(texts, null, "manifest.category", manifest.Category);
            AddIfPresent(texts, null, "manifest.suggestedStylePreset", manifest.SuggestedStylePreset);
            for (int i = 0; i < (manifest.Inputs?.Count ?? 0); i++)
            {
                AppManifestInput input = manifest.Inputs![i];
                AddIfPresent(texts, null, $"manifest.inputs[{i}].label", input.Label);
                AddIfPresent(texts, null, $"manifest.inputs[{i}].help", input.Help);
                AddIfPresent(texts, null, $"manifest.inputs[{i}].example", input.Example);
                AddIfPresent(texts, null, $"manifest.inputs[{i}].onLabel", input.OnLabel);
                AddIfPresent(texts, null, $"manifest.inputs[{i}].offLabel", input.OffLabel);
                // Every other string-valued member is checked too: none of them may carry a secret or a private id.
                AddIfPresent(texts, null, $"manifest.inputs[{i}].kind", input.Kind);
                AddIfPresent(texts, null, $"manifest.inputs[{i}].field", input.Field);
                AddIfPresent(texts, null, $"manifest.inputs[{i}].role", input.Role);
                AddIfPresent(texts, null, $"manifest.inputs[{i}].id", input.Id);
                AddIfPresent(texts, null, $"manifest.inputs[{i}].type", input.Type);
                AddIfPresent(texts, null, $"manifest.inputs[{i}].unit", input.Unit);
                AddIfPresent(texts, null, $"manifest.inputs[{i}].capability", input.Capability);
                AddIfPresent(texts, null, $"manifest.inputs[{i}].job", input.Job);
                AddIfPresent(texts, null, $"manifest.inputs[{i}].labelKey", input.LabelKey);
                AddIfPresent(texts, null, $"manifest.inputs[{i}].helpKey", input.HelpKey);
                AddIfPresent(texts, null, $"manifest.inputs[{i}].exampleKey", input.ExampleKey);
                if (input.ShowWhen is { } showWhen)
                {
                    AddIfPresent(texts, null, $"manifest.inputs[{i}].showWhen.input", showWhen.Input);
                    if (showWhen.EqualsValue.ValueKind == JsonValueKind.String)
                        AddIfPresent(texts, null, $"manifest.inputs[{i}].showWhen.equals", showWhen.EqualsValue.GetString());
                }
                for (int o = 0; o < (input.Options?.Count ?? 0); o++)
                {
                    AddIfPresent(texts, null, $"manifest.inputs[{i}].options[{o}].label", input.Options![o].Label);
                    AddIfPresent(texts, null, $"manifest.inputs[{i}].options[{o}].hint", input.Options[o].Hint);
                    AddIfPresent(texts, null, $"manifest.inputs[{i}].options[{o}].labelKey", input.Options[o].LabelKey);
                    if (input.Options[o].Value.ValueKind == JsonValueKind.String)
                        AddIfPresent(texts, null, $"manifest.inputs[{i}].options[{o}].value", input.Options[o].Value.GetString());
                }
                if (input.Default is { ValueKind: JsonValueKind.String } d)
                    AddIfPresent(texts, null, $"manifest.inputs[{i}].default", d.GetString());
                for (int c = 0; c < (input.Choices?.Count ?? 0); c++)
                {
                    if (input.Choices![c].ValueKind == JsonValueKind.String)
                        AddIfPresent(texts, null, $"manifest.inputs[{i}].choices[{c}]", input.Choices[c].GetString());
                }
            }
        }

        return texts;
    }

    private static void AddIfPresent(List<PortableTextField> texts, int? stepOrder, string field, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            texts.Add(new PortableTextField(stepOrder, field, text));
    }
}

/// <summary>Serialisation of <see cref="PortableRecipe"/>: System.Text.Json, camelCase, nulls omitted, object keys sorted.</summary>
public static class PortableRecipeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static string Serialize(PortableRecipe recipe)
    {
        PortableRecipe ordered = recipe with
        {
            Steps = [.. recipe.Steps.OrderBy(s => s.Order)],
            Slots = [.. recipe.Slots.OrderBy(s => s.StepOrder).ThenBy(s => s.Field, StringComparer.Ordinal).ThenBy(s => s.Kind, StringComparer.Ordinal)],
            Removed = [.. recipe.Removed.OrderBy(s => s.StepOrder).ThenBy(s => s.Field, StringComparer.Ordinal).ThenBy(s => s.What, StringComparer.Ordinal)]
        };
        JsonNode? node = JsonSerializer.SerializeToNode(ordered, Options);
        return Canonicalize(node)?.ToJsonString() ?? "null";
    }

    public static PortableRecipe? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<PortableRecipe>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A deep copy with every object's keys in ordinal order (arrays keep their order).</summary>
    public static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        JsonObject obj => SortedCopy(obj),
        JsonArray arr => new JsonArray([.. arr.Select(Canonicalize)]),
        null => null,
        _ => node.DeepClone()
    };

    private static JsonObject SortedCopy(JsonObject obj)
    {
        JsonObject copy = [];
        foreach (KeyValuePair<string, JsonNode?> kv in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
            copy[kv.Key] = Canonicalize(kv.Value);
        return copy;
    }
}

internal static class PortableJsonText
{
    /// <summary>Calls <paramref name="visit"/> for every string leaf with its path.</summary>
    internal static void CollectStrings(JsonNode node, string path, Action<string, string> visit)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> kv in obj)
                {
                    if (kv.Value is not null)
                        CollectStrings(kv.Value, $"{path}.{kv.Key}", visit);
                }

                break;
            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is { } child)
                        CollectStrings(child, $"{path}[{i}]", visit);
                }

                break;
            case JsonValue value when value.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s):
                visit(path, s);
                break;
        }
    }
}
