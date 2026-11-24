using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Lint;

namespace ReelBolt.Inference.Api.Services.Marketplace.Snapshot;

/// <summary>
/// Checks that a stripped snapshot is still a coherent workflow, without needing a project: the save validators on every step
/// config, the app manifest rules, and ONLY the structural linter rules (WL001-005, 014, 018, 019, 021). The readiness rules
/// (WL006-010, 012, 015: providers, voices, files, plans, egress, length) must never run here because a snapshot has open slots.
/// </summary>
/// <remarks>
/// Open slots: provider and voice slots are left empty (an empty provider is the organization's default, an empty voice is valid);
/// each open FILE slot is filled with one fixed placeholder id for the duration of the check so the save validators, which insist
/// on a file id where a step names a project file, judge the rest of the step. The placeholder is never stored or returned.
/// </remarks>
public static partial class PortableRecipeValidator
{
    /// <summary>The structural rules a recipe must satisfy. A readiness rule must never be added here.</summary>
    public static readonly IReadOnlySet<string> StructuralRules =
        new HashSet<string>(["WL001", "WL002", "WL003", "WL004", "WL005", "WL014", "WL018", "WL019", "WL021"], StringComparer.Ordinal);

    private static readonly Guid FilePlaceholder = new("00000000-0000-4000-8000-0000000f11e5");
    private static readonly Guid AgentPlaceholder = new("00000000-0000-4000-8000-0000000a9e07");

    [GeneratedRegex(@"\[(\d+)\]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IndexToken();

    /// <summary>The reasons the snapshot is not a coherent workflow; empty means it is.</summary>
    public static IReadOnlyList<string> Validate(PortableRecipe recipe)
    {
        List<string> errors = [];
        List<WorkflowStep> steps = [.. recipe.Steps.Select(s => ToStep(s, recipe.Slots))];

        try
        {
            errors.AddRange(StepConfigSaveValidator.Validate(steps.Select(AssistantStepShapes.FromStep)));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            errors.Add("A step's settings could not be read.");
        }

        errors.AddRange(StepGraphLinter.Evaluate(steps)
            .Where(f => StructuralRules.Contains(f.RuleId) && f.Severity == LintSeverity.Error)
            .Select(f => f.StepOrder is { } n ? $"Step {n}: {f.Message}" : f.Message));

        if (recipe.Manifest is { } manifest)
        {
            AppManifest clean = AppManifestValidator.Sanitize(manifest);
            IReadOnlyList<AppStepFacts> facts =
                FactsOf(recipe);
            errors.AddRange(AppManifestValidator.Validate(clean, facts, [.. steps.Select(AppBindingStep.From)]));
        }

        return errors;
    }

    /// <summary>
    /// The app facts of the snapshot's steps, in order, read from every per-type config (a picture edit of the run's
    /// files, an avatar, a dynamic compile...), so a published recipe is judged on what its steps really take.
    /// </summary>
    public static IReadOnlyList<AppStepFacts> FactsOf(PortableRecipe recipe) =>
        [.. recipe.Steps.OrderBy(s => s.Order).Select(s => AppStepFacts.FromStep(ToStep(s, recipe.Slots), s.BuiltInAgentType))];

    private static WorkflowStep ToStep(PortableStep step, IReadOnlyList<PortableSlot> slots)
    {
        WorkflowStep result = new()
        {
            AgentDefinitionId = AgentPlaceholder,
            StepOrder = step.Order,
            StepType = step.StepType,
            Label = step.Label,
            ConditionExpression = step.ConditionExpression,
            LoopSourceExpression = step.LoopSourceExpression,
            LoopTargetStepOrder = step.LoopTargetStepOrder,
            MaxIterations = step.MaxIterations,
            MinScore = step.MinScore,
            InputMappingJson = step.InputMapping?.ToJsonString(),
            TrueBranchStepOrder = step.TrueBranchStepOrder,
            FalseBranchStepOrder = step.FalseBranchStepOrder,
            AgentInputContextMode = step.AgentInputContextMode,
            SelectedPriorStepOrdersJson = step.SelectedPriorStepOrders is { Count: > 0 } o ? System.Text.Json.JsonSerializer.Serialize(o) : null,
            CacheMode = step.CacheMode,
            RegisterOutputAsProjectFile = step.RegisterOutputAsProjectFile,
            AgentDefinition = new AgentDefinition { Id = AgentPlaceholder, AgentType = step.BuiltInAgentType ?? default, IsBuiltIn = true }
        };
        StepConfigColumns.Set(result, ConfigJson(step, slots));
        return result;
    }

    private static string? ConfigJson(PortableStep step, IReadOnlyList<PortableSlot> slots)
    {
        if (step.Config is null)
            return null;
        JsonNode config = step.Config.DeepClone();
        foreach (PortableSlot slot in slots.Where(s => s.StepOrder == step.Order && s.Kind == PortableSlotKinds.File))
            SetPath(config, slot.Field, JsonValue.Create(FilePlaceholder.ToString()));
        return config.ToJsonString();
    }

    private static void SetPath(JsonNode root, string path, JsonNode value)
    {
        List<object> tokens = [];
        foreach (string part in path.Split('.'))
        {
            string name = IndexToken().Replace(part, string.Empty);
            if (name.Length > 0)
                tokens.Add(name);
            foreach (Match m in IndexToken().Matches(part))
                tokens.Add(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        }

        JsonNode? current = root;
        for (int i = 0; i < tokens.Count - 1 && current is not null; i++)
        {
            current = tokens[i] switch
            {
                string key when current is JsonObject obj => obj[key],
                int index when current is JsonArray arr && index < arr.Count => arr[index],
                _ => null
            };
        }

        switch (tokens.Count > 0 ? tokens[^1] : null)
        {
            case string key when current is JsonObject target:
                target[key] = value;
                break;
            case int index when current is JsonArray array && index < array.Count:
                array[index] = value;
                break;
        }
    }
}
