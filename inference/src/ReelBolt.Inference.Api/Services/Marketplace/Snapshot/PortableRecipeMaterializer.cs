using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Services.Marketplace.Snapshot;

/// <summary>An unsaved workflow built from a recipe, its steps, and the open slots the copier still has to fill.</summary>
public sealed record MaterializedRecipe(WorkflowDefinition Workflow, IReadOnlyList<WorkflowStep> Steps, IReadOnlyList<PortableSlot> Slots);

/// <summary>The outcome of <see cref="PortableRecipeMaterializer.Materialize"/>: a workflow or the reasons it could not be built.</summary>
public sealed record MaterializeResult(MaterializedRecipe? Value, IReadOnlyList<string> Errors);

/// <summary>
/// The exact inverse of <see cref="PortableRecipeBuilder"/>: a snapshot and a target project become an unsaved workflow with its
/// steps. Built-in agents are resolved by <see cref="AgentType"/> (never an id from the snapshot), open slots stay unset, source-media
/// egress is explicitly off, every text is sanitised again, and the snapshot is refused (fail closed) when its schema version is
/// unknown, when it still carries anything id-shaped, or when it is no longer a coherent workflow.
/// </summary>
public static class PortableRecipeMaterializer
{
    public const string TemplateKeyPrefix = "market:";
    public const int MaxNameChars = 120;

    /// <summary>The template key a copy of <paramref name="recipeId"/> carries.</summary>
    public static string TemplateKeyOf(Guid recipeId) => TemplateKeyPrefix + recipeId.ToString("N");

    /// <summary>Every built-in agent type the recipe needs (a step's own agent and a parallel step's members).</summary>
    public static IReadOnlySet<AgentType> AgentTypesOf(PortableRecipe recipe) =>
        recipe.Steps
            .SelectMany(s => (s.BuiltInAgentType is { } t ? new[] { t } : []).Concat(s.ParallelAgentTypes ?? []))
            .ToHashSet();

    public static MaterializeResult Materialize(
        PortableRecipe recipe,
        Guid recipeId,
        Guid projectId,
        string name,
        IReadOnlyDictionary<AgentType, AgentDefinition> builtInAgents)
    {
        if (recipe.SchemaVersion != PortableRecipe.CurrentSchemaVersion)
            return Fail("This recipe was made with a version of the format this app does not know yet.");
        IReadOnlyList<string> unusable = Recheck(recipe);
        if (unusable.Count > 0)
            return new MaterializeResult(null, unusable);

        Guid workflowId = Guid.NewGuid();
        List<string> errors = [];
        List<WorkflowStep> steps = [];
        foreach (PortableStep step in recipe.Steps.OrderBy(s => s.Order))
        {
            WorkflowStep? built = BuildStep(step, workflowId, builtInAgents, errors);
            if (built is not null)
                steps.Add(built);
        }

        if (errors.Count > 0)
            return new MaterializeResult(null, errors);

        string? manifestJson = null;
        bool needsInput = false;
        if (recipe.Manifest is { } manifest)
        {
            AppManifest clean = AppManifestValidator.Sanitize(manifest);
            IReadOnlyList<AppStepFacts> facts =
                [.. recipe.Steps.OrderBy(s => s.Order).Zip(steps, (p, s) => AppStepFacts.FromStep(s, p.BuiltInAgentType))];
            IReadOnlyList<string> manifestErrors = AppManifestValidator.Validate(clean, facts, [.. steps.Select(AppBindingStep.From)]);
            if (manifestErrors.Count > 0)
                return new MaterializeResult(null, manifestErrors);
            manifestJson = AppManifestJson.Serialize(clean);
            needsInput = clean.Inputs?.Any(i => string.Equals(i.Kind, "notes", StringComparison.Ordinal)) == true;
        }

        DateTime now = DateTime.UtcNow;
        WorkflowDefinition workflow = new()
        {
            Id = workflowId,
            ProjectId = projectId,
            Name = CleanName(name),
            RequiresUserInput = needsInput,
            AppManifestJson = manifestJson,
            CreatedVia = WorkflowCreatedVia.Marketplace,
            TemplateKey = TemplateKeyOf(recipeId),
            CreatedAt = now,
            UpdatedAt = now
        };
        foreach (WorkflowStep step in steps)
            workflow.Steps.Add(step);

        return new MaterializeResult(new MaterializedRecipe(workflow, steps, recipe.Slots), []);
    }

    /// <summary>
    /// Re-runs on a STORED snapshot every check the publish gate ran on the live workflow (a legacy or crafted row may never have
    /// passed them): step-count and size caps, defined step and agent types, shareable step types and built-in agents only, loop
    /// bounds, expression grammar of conditions, loop sources and input mappings, the leak guard and the coherence validator.
    /// Empty means the snapshot may be copied.
    /// </summary>
    public static IReadOnlyList<string> Recheck(PortableRecipe recipe)
    {
        if (recipe.Steps.Count == 0)
            return ["The recipe has no steps."];
        if (recipe.Steps.Count > PortableRecipeBuilder.MaxSteps)
            return ["The recipe has more steps than a shared recipe may have."];
        if (System.Text.Encoding.UTF8.GetByteCount(recipe.ToCanonicalJson()) > PortableRecipeBuilder.MaxSnapshotBytes)
            return ["The recipe is larger than a shared recipe may be."];

        List<string> errors = [];
        foreach (PortableStep step in recipe.Steps)
        {
            if (!Enum.IsDefined(step.StepType))
            {
                errors.Add($"Step {step.Order}: unknown step type.");
                continue;
            }

            if (MarketplaceStepPublishability.For(step.StepType) is not StepPublishability.Shareable)
                errors.Add($"Step {step.Order}: this step type cannot be shared.");
            if (step.BuiltInAgentType is not { } agent || !Enum.IsDefined(agent))
                errors.Add($"Step {step.Order}: only built-in agents can be shared.");
            if (step.ParallelAgentTypes?.Any(t => !Enum.IsDefined(t)) == true)
                errors.Add($"Step {step.Order}: unknown agent in a parallel step.");
            if (step.MaxIterations is < 1 or > PortableRecipeBuilder.MaxLoopIterations
                || step.MinScore is < PortableRecipeBuilder.MinPassMark or > PortableRecipeBuilder.MaxPassMark)
                errors.Add($"Step {step.Order}: loop bounds are out of range.");
            if (step.AgentInputContextMode is { } mode && !Enum.IsDefined(mode)
                || step.CacheMode is { } cache && !Enum.IsDefined(cache))
                errors.Add($"Step {step.Order}: unknown step option.");
            foreach (string? expression in new[] { step.ConditionExpression, step.LoopSourceExpression })
            {
                if (!string.IsNullOrWhiteSpace(expression) && !PortableRecipeBuilder.IsValidExpression(expression))
                    errors.Add($"Step {step.Order}: an expression is not a plain condition.");
            }

            if (step.InputMapping is not null)
            {
                PortableJsonText.CollectStrings(step.InputMapping, "inputMapping", (_, text) =>
                {
                    if (!PortableRecipeBuilder.IsValidExpression(text))
                        errors.Add($"Step {step.Order}: an input mapping value is not a plain reference.");
                });
            }
        }

        if (errors.Count > 0)
            return errors;

        // The same backstop the publish gate runs: a snapshot that names any id, key or secret never becomes a workflow.
        if (SnapshotLeakGuard.Scan(recipe.ToCanonicalJson()).Count > 0)
            return ["The recipe could not be checked for private ids."];
        return PortableRecipeValidator.Validate(recipe);
    }

    /// <summary>A workflow name as plain text, capped; empty when nothing usable is left.</summary>
    public static string CleanName(string? name)
    {
        string clean = string.Join(' ', MarketplaceText.Sanitize(name).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return clean.Length <= MaxNameChars ? clean : clean[..MaxNameChars].TrimEnd();
    }

    private static MaterializeResult Fail(string message) => new(null, [message]);

    private static WorkflowStep? BuildStep(
        PortableStep step, Guid workflowId, IReadOnlyDictionary<AgentType, AgentDefinition> agents, List<string> errors)
    {
        if (step.BuiltInAgentType is not { } agentType || !agents.TryGetValue(agentType, out AgentDefinition? agent))
        {
            errors.Add($"Step {step.Order}: the built-in agent it needs is not available.");
            return null;
        }

        string? parallelJson = null;
        if (step.ParallelAgentTypes is { Count: > 0 } members)
        {
            List<Guid> ids = [];
            foreach (AgentType member in members.Distinct())
            {
                if (!agents.TryGetValue(member, out AgentDefinition? memberAgent))
                {
                    errors.Add($"Step {step.Order}: a built-in agent it needs is not available.");
                    return null;
                }

                ids.Add(memberAgent.Id);
            }

            parallelJson = JsonSerializer.Serialize(ids);
        }

        WorkflowStep result = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflowId,
            AgentDefinitionId = agent.Id,
            StepOrder = step.Order,
            StepType = step.StepType,
            Label = step.Label is null ? null : MarketplaceText.Sanitize(step.Label),
            ConditionExpression = step.ConditionExpression,
            LoopSourceExpression = step.LoopSourceExpression,
            LoopTargetStepOrder = step.LoopTargetStepOrder,
            MaxIterations = step.MaxIterations,
            MinScore = step.MinScore,
            InputMappingJson = step.InputMapping?.ToJsonString(),
            TrueBranchStepOrder = step.TrueBranchStepOrder,
            FalseBranchStepOrder = step.FalseBranchStepOrder,
            ParallelAgentIdsJson = parallelJson,
            AgentInputContextMode = step.AgentInputContextMode,
            SelectedPriorStepOrdersJson = step.SelectedPriorStepOrders is { Count: > 0 } orders ? JsonSerializer.Serialize(orders) : null,
            CacheMode = step.CacheMode,
            RegisterOutputAsProjectFile = step.RegisterOutputAsProjectFile
        };

        if (step.Config is not null && MarketplaceStepPublishability.ConfigTypeOf(step.StepType) is { } configType)
        {
            // Walking an already cleaned config changes nothing; it re-sanitises every text and drops anything unclassified.
            WalkResult walk = new();
            JsonNode? cleaned = ConfigWalker.Walk(step.Config.DeepClone(), configType, step.Order, walk);
            if (walk.Refusals.Count > 0 || cleaned is not JsonObject config)
            {
                errors.Add($"Step {step.Order}: its settings could not be used.");
                return null;
            }

            if (step.StepType is StepType.VideoGenerate or StepType.VideoAnalyze)
            {
                foreach (string key in config.Select(kv => kv.Key).Where(k => string.Equals(k, "allowSourceMediaEgress", StringComparison.OrdinalIgnoreCase)).ToList())
                    config.Remove(key);
                config["allowSourceMediaEgress"] = false;
            }

            StepConfigColumns.Set(result, config.ToJsonString());
        }

        return result;
    }
}
