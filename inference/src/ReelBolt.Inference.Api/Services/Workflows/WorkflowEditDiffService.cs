using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Workflows;

public class WorkflowEditDiffService : IWorkflowEditDiffService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly InferenceApiDbContext _db;
    private readonly StepConfigSchemaExporter _schemaExporter;
    private readonly MinimalJsonSchemaValidator _validator;
    private readonly IConfirmationTokenService _confirmationTokens;
    private readonly StepProviderOwnershipValidator _providerOwnership;

    /// <summary>
    /// Convenience constructor for call sites that are not built by the DI container. The token it
    /// delegates to has no key of its own, which is harmless here: ApplyWorkflow's token is the
    /// keyless digest it has always been (see <see cref="ConfirmationTokenService.ApplyWorkflowOperation"/>),
    /// so it is byte-identical either way.
    /// </summary>
    public WorkflowEditDiffService(InferenceApiDbContext db)
        : this(db, ConfirmationTokenService.CreateEphemeral())
    {
    }

    public WorkflowEditDiffService(InferenceApiDbContext db, IConfirmationTokenService confirmationTokens)
        : this(db, confirmationTokens, new StepProviderOwnershipValidator(db))
    {
    }

    public WorkflowEditDiffService(
        InferenceApiDbContext db,
        IConfirmationTokenService confirmationTokens,
        StepProviderOwnershipValidator providerOwnership)
    {
        _db = db;
        _confirmationTokens = confirmationTokens;
        _schemaExporter = new StepConfigSchemaExporter();
        _validator = new MinimalJsonSchemaValidator();
        _providerOwnership = providerOwnership;
    }

    public WorkflowEditDiff ComputeDiff(
        Guid? workflowId,
        IReadOnlyList<WorkflowStep> current,
        IReadOnlyList<CreateWorkflowStepRequest> proposed)
    {
        // Every caller of ComputeDiff is an assistant proposal surface, so this is the one place a
        // model-authored graph is normalized: a review loop the linter would only warn about (WL003)
        // is retargeted or removed before the diff and the token are computed. Doing it here keeps the
        // token honest: Apply recomputes the same pure normalization on the same input, so a Propose
        // and an Apply of the model's original steps agree, and the save writes ProposedSteps.
        WorkflowNormalizationResult normalization = WorkflowStepNormalizer.NormalizeWithChanges(proposed);
        proposed = normalization.Steps;

        var additions = new List<WorkflowStepDiffEntry>();
        var removals = new List<WorkflowStepDiffEntry>();
        var modifications = new List<WorkflowStepDiffEntry>();

        // Build lookup of current steps by StepOrder for efficient matching
        var currentByOrder = new Dictionary<int, WorkflowStep>();
        foreach (var step in current)
        {
            currentByOrder[step.StepOrder] = step;
        }

        // Process proposed steps to find additions and modifications
        foreach (var proposedStep in proposed)
        {
            if (currentByOrder.TryGetValue(proposedStep.StepOrder, out var existingStep))
            {
                // This is a potential modification - check if it actually differs
                if (StepsAreDifferent(existingStep, proposedStep))
                {
                    modifications.Add(new WorkflowStepDiffEntry(
                        proposedStep.StepOrder,
                        proposedStep.AgentDefinitionId,
                        proposedStep.StepType ?? "Agent",
                        proposedStep.Label));
                }
            }
            else
            {
                // This step doesn't exist in current - it's an addition
                additions.Add(new WorkflowStepDiffEntry(
                    proposedStep.StepOrder,
                    proposedStep.AgentDefinitionId,
                    proposedStep.StepType ?? "Agent",
                    proposedStep.Label));
            }
        }

        // Find removed steps - those in current but not in proposed
        foreach (var currentStep in current)
        {
            if (!proposed.Any(p => p.StepOrder == currentStep.StepOrder))
            {
                removals.Add(new WorkflowStepDiffEntry(
                    currentStep.StepOrder,
                    currentStep.AgentDefinitionId,
                    currentStep.StepType.ToString(),
                    currentStep.Label));
            }
        }

        var token = _confirmationTokens.Compute(
            ConfirmationTokenService.ApplyWorkflowOperation,
            BuildConfirmationPayload(workflowId, current, proposed),
            // The ApplyWorkflow token binds no user: it is the keyless digest this service has always
            // produced, and the product decision pins it byte-for-byte so a proposal made before a deploy
            // still applies after it. Guid.Empty states that absence rather than inventing an owner.
            Guid.Empty);

        return new WorkflowEditDiff(additions, removals, modifications, token, proposed, workflowId)
        {
            NormalizationChanges = normalization.Changes
        };
    }

    private bool StepsAreDifferent(WorkflowStep current, CreateWorkflowStepRequest proposed)
    {
        // Compare key properties to determine if steps are different
        if (current.AgentDefinitionId != proposed.AgentDefinitionId)
            return true;

        if (current.Label != proposed.Label)
            return true;

        var currentStepType = current.StepType.ToString();
        var proposedStepType = proposed.StepType ?? "Agent";
        if (currentStepType != proposedStepType)
            return true;

        // Compare edge conditions
        if (current.EdgeConditionJson != proposed.EdgeConditionJson)
            return true;

        // Compare step config blobs semantically: a proposal that only retunes a step's config is a
        // real change, but a whitespace/key-order difference (jsonb normalizes on store) is not.
        if (ConfigJsonDiffers(current.ExtractConfigJson, proposed.ExtractConfigJson)
            || ConfigJsonDiffers(current.VideoAnalyzeConfigJson, proposed.VideoAnalyzeConfigJson)
            || ConfigJsonDiffers(current.VideoCompileConfigJson, proposed.VideoCompileConfigJson)
            || ConfigJsonDiffers(current.EditRoomConfigJson, proposed.EditRoomConfigJson)
            || ConfigJsonDiffers(current.GraphicsRoomConfigJson, proposed.GraphicsRoomConfigJson)
            || ConfigJsonDiffers(current.ColorGradeRoomConfigJson, proposed.ColorGradeRoomConfigJson)
            || ConfigJsonDiffers(current.ConditionalConfigJson, proposed.ConditionalConfigJson)
            || ConfigJsonDiffers(current.ReviewLoopConfigJson, proposed.ReviewLoopConfigJson)
            || ConfigJsonDiffers(current.VideoGenerateConfigJson, proposed.VideoGenerateConfigJson)
            || ConfigJsonDiffers(current.VoiceoverConfigJson, proposed.VoiceoverConfigJson)
            || ConfigJsonDiffers(current.ThumbnailGenerateConfigJson, proposed.ThumbnailGenerateConfigJson)
            || ConfigJsonDiffers(current.DailiesRoomConfigJson, proposed.DailiesRoomConfigJson)
            || ConfigJsonDiffers(current.TimelineRenderConfigJson, proposed.TimelineRenderConfigJson)
            || ConfigJsonDiffers(current.DesignSystemConfigJson, proposed.DesignSystemConfigJson)
            || ConfigJsonDiffers(current.ContinueCompositionJson, proposed.ContinueCompositionJson)
            || ConfigJsonDiffers(current.ExtractFrameConfigJson, proposed.ExtractFrameConfigJson)
            || ConfigJsonDiffers(current.AudioGenerateConfigJson, proposed.AudioGenerateConfigJson)
            || ConfigJsonDiffers(current.PinInsertConfigJson, proposed.PinInsertConfigJson)
            || ConfigJsonDiffers(current.AudioCleanupConfigJson, proposed.AudioCleanupConfigJson)
            || ConfigJsonDiffers(current.VideoEnhanceConfigJson, proposed.VideoEnhanceConfigJson)
            || ConfigJsonDiffers(current.ImageGenerateConfigJson, proposed.ImageGenerateConfigJson)
            || current.RegisterOutputAsProjectFile != proposed.RegisterOutputAsProjectFile)
            return true;

        // Everything else a proposal can set. These are compared against the step BuildStep would
        // store (so an omitted field means its default, exactly as it will once applied), because
        // "this proposal changes nothing" is also what lets an apply whose token no longer verifies be
        // answered "already applied": a field this method ignored would be silently reported as in.
        WorkflowStep candidate = BuildStep(current.WorkflowDefinitionId, proposed);
        return current.ConditionExpression != candidate.ConditionExpression
            || current.LoopSourceExpression != candidate.LoopSourceExpression
            || current.LoopTargetStepOrder != candidate.LoopTargetStepOrder
            || current.MaxIterations != candidate.MaxIterations
            || current.MinScore != candidate.MinScore
            || current.AgentInputContextMode != candidate.AgentInputContextMode
            || current.TrueBranchStepOrder != candidate.TrueBranchStepOrder
            || current.FalseBranchStepOrder != candidate.FalseBranchStepOrder
            || current.CacheMode != candidate.CacheMode
            || ConfigJsonDiffers(current.InputMappingJson, candidate.InputMappingJson)
            || ConfigJsonDiffers(current.SelectedPriorStepOrdersJson, candidate.SelectedPriorStepOrdersJson)
            || ConfigJsonDiffers(current.ParallelAgentIdsJson, candidate.ParallelAgentIdsJson);
    }

    private static bool ConfigJsonDiffers(string? current, string? proposed)
    {
        bool currentEmpty = string.IsNullOrWhiteSpace(current);
        bool proposedEmpty = string.IsNullOrWhiteSpace(proposed);
        if (currentEmpty || proposedEmpty)
            return currentEmpty != proposedEmpty;

        try
        {
            return !JsonNode.DeepEquals(JsonNode.Parse(current!), JsonNode.Parse(proposed!));
        }
        catch (JsonException)
        {
            return !string.Equals(current, proposed, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The canonical payload <see cref="IConfirmationTokenService"/> hashes for the ApplyWorkflow
    /// operation. Byte-for-byte the object the token used to be computed from, so the resulting
    /// token is unchanged.
    /// </summary>
    private static string BuildConfirmationPayload(
        Guid? workflowId,
        IReadOnlyList<WorkflowStep> current,
        IReadOnlyList<CreateWorkflowStepRequest> proposed)
    {
        // Compute hash of current steps
        var currentStepsHash = ComputeStepsHash(current);

        // Create a canonical JSON object with workflowId, currentStepsHash, and proposedSteps
        var tokenObj = new JsonObject
        {
            ["workflowId"] = workflowId?.ToString() ?? "",
            ["currentStepsHash"] = currentStepsHash,
            ["proposedSteps"] = JsonNode.Parse(JsonSerializer.Serialize(proposed, JsonOptions))
        };

        return tokenObj.ToJsonString(JsonOptions);
    }

    private static string ComputeStepsHash(IReadOnlyList<WorkflowStep> steps)
    {
        if (steps.Count == 0)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(""));
            return Convert.ToHexString(hash).ToLower();
        }

        var stepsArray = new JsonArray();
        foreach (var step in steps)
        {
            var stepObj = new JsonObject
            {
                ["stepOrder"] = step.StepOrder,
                ["agentDefinitionId"] = step.AgentDefinitionId.ToString(),
                ["stepType"] = step.StepType.ToString(),
                ["label"] = step.Label
            };
            stepsArray.Add(stepObj);
        }

        var json = stepsArray.ToJsonString(JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var hashBytes = SHA256.HashData(bytes);

        return Convert.ToHexString(hashBytes).ToLower();
    }

    public WorkflowValidationResult ValidateProposedSteps(Guid projectId, IReadOnlyList<CreateWorkflowStepRequest> proposed)
    {
        var errors = new List<string>();

        // The project row is the authority on which organization a step may draw its agents and its
        // inference providers from (A8/A10). Read ONCE: it cannot change while this walks the steps.
        Project? project = _db.Projects.FirstOrDefault(p => p.Id == projectId);

        foreach (var step in proposed)
        {
            AgentDefinition? agent = _db.AgentDefinitions.FirstOrDefault(a => a.Id == step.AgentDefinitionId);
            if (agent == null)
            {
                errors.Add($"Step {step.StepOrder}: agent {step.AgentDefinitionId} does not exist.");
                continue;
            }
            // A8: the agent must be a built-in or belong to the project's organization. This
            // mirrors the save path in WorkflowsController/WorkflowAssistantController — the
            // caller has already authorized the project, so the project row is the authority on
            // which organization the step may draw its agents from.
            bool usableInProject = agent.IsBuiltIn || (project != null && agent.OrganizationId == project.OrganizationId);
            if (!usableInProject)
            {
                errors.Add($"Step {step.StepOrder}: agent {step.AgentDefinitionId} is not built-in and does not belong to this project's organization.");
                continue;
            }

            // The same check every save path runs (StepConfigSaveValidator), so ValidateWorkflow never
            // passes a step ApplyWorkflow would reject — including a missing VideoGenerate config.
            errors.AddRange(StepConfigSaveValidator.ValidateStep(step));

            if (step.StepType == null || !Enum.TryParse<StepType>(step.StepType, out var stepType))
                continue;

            var schemaResult = _schemaExporter.GetSchema(stepType);
            if (!schemaResult.HasConfig || schemaResult.Schema == null)
                continue;

            string? configJson = stepType switch
            {
                StepType.Extract => step.ExtractConfigJson,
                StepType.VideoAnalyze => step.VideoAnalyzeConfigJson,
                StepType.VideoCompile => step.VideoCompileConfigJson,
                StepType.EditRoom => step.EditRoomConfigJson,
                StepType.GraphicsRoom => step.GraphicsRoomConfigJson,
                StepType.ColorGradeRoom => step.ColorGradeRoomConfigJson,
                StepType.Conditional => step.ConditionalConfigJson,
                StepType.ReviewLoop => step.ReviewLoopConfigJson,
                StepType.VideoGenerate => step.VideoGenerateConfigJson,
                StepType.Voiceover => step.VoiceoverConfigJson,
                StepType.ThumbnailGenerate => step.ThumbnailGenerateConfigJson,
                StepType.DailiesRoom => step.DailiesRoomConfigJson,
                StepType.TimelineRender => step.TimelineRenderConfigJson,
                StepType.DesignSystem => step.DesignSystemConfigJson,
                StepType.ExtractFrame => step.ExtractFrameConfigJson,
                StepType.AudioGenerate => step.AudioGenerateConfigJson,
                StepType.PinInsert => step.PinInsertConfigJson,
                StepType.AudioCleanup => step.AudioCleanupConfigJson,
                StepType.VideoEnhance => step.VideoEnhanceConfigJson,
                StepType.ImageGenerate => step.ImageGenerateConfigJson,
                _ => null
            };

            if (string.IsNullOrWhiteSpace(configJson))
                continue; // not a schema error; StepConfigSaveValidator.ValidateStep already reported it above

            ValidationResult validation = _validator.Validate(schemaResult.Schema, configJson);
            if (!validation.Valid)
                foreach (var err in validation.Errors)
                    errors.Add($"Step {step.StepOrder} ({stepType}): {err}");
        }

        // A10: the same provider-ownership check every save path runs
        // (StepProviderOwnershipValidator), so ValidateWorkflow never passes a step ApplyWorkflow
        // would reject — including a step naming another organization's inference provider.
        if (project is not null)
            errors.AddRange(_providerOwnership.Validate(project.OrganizationId, project.Id, proposed));

        // T-106: the cross-step rules for insert content references (earlier step, playable output).
        errors.AddRange(InsertContentRefSaveValidator.Validate(proposed));

        return new WorkflowValidationResult(errors.Count == 0, errors);
    }

    public WorkflowStep BuildStep(Guid workflowId, CreateWorkflowStepRequest req) => ToStep(workflowId, req);

    /// <summary>
    /// The row a request is written as (the one mapping <see cref="BuildStep"/> uses): enums parsed, defaults
    /// applied. Static so callers that compare what WOULD be stored (the footage-sharing consent guard) share it.
    /// </summary>
    public static WorkflowStep ToStep(Guid workflowId, CreateWorkflowStepRequest req)
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflowId,
            AgentDefinitionId = req.AgentDefinitionId,
            StepOrder = req.StepOrder,
            EdgeConditionJson = req.EdgeConditionJson,
            Label = req.Label,
            ConditionExpression = req.ConditionExpression,
            LoopSourceExpression = req.LoopSourceExpression,
            LoopTargetStepOrder = req.LoopTargetStepOrder,
            MinScore = req.MinScore,
            InputMappingJson = req.InputMappingJson,
            SelectedPriorStepOrdersJson = req.SelectedPriorStepOrdersJson,
            TrueBranchStepOrder = req.TrueBranchStepOrder,
            FalseBranchStepOrder = req.FalseBranchStepOrder,
            ParallelAgentIdsJson = req.ParallelAgentIdsJson,
            ExtractConfigJson = req.ExtractConfigJson,
            VideoAnalyzeConfigJson = req.VideoAnalyzeConfigJson,
            VideoCompileConfigJson = req.VideoCompileConfigJson,
            EditRoomConfigJson = req.EditRoomConfigJson,
            GraphicsRoomConfigJson = req.GraphicsRoomConfigJson,
            ColorGradeRoomConfigJson = req.ColorGradeRoomConfigJson,
            ConditionalConfigJson = req.ConditionalConfigJson,
            ReviewLoopConfigJson = req.ReviewLoopConfigJson,
            VideoGenerateConfigJson = req.VideoGenerateConfigJson,
            VoiceoverConfigJson = req.VoiceoverConfigJson,
            ThumbnailGenerateConfigJson = req.ThumbnailGenerateConfigJson,
            DailiesRoomConfigJson = req.DailiesRoomConfigJson,
            TimelineRenderConfigJson = req.TimelineRenderConfigJson,
            DesignSystemConfigJson = req.DesignSystemConfigJson,
            ContinueCompositionJson = req.ContinueCompositionJson,
            RegisterOutputAsProjectFile = req.RegisterOutputAsProjectFile,
            ExtractFrameConfigJson = req.ExtractFrameConfigJson,
            AudioGenerateConfigJson = req.AudioGenerateConfigJson,
            PinInsertConfigJson = req.PinInsertConfigJson,
            AudioCleanupConfigJson = req.AudioCleanupConfigJson,
            VideoEnhanceConfigJson = req.VideoEnhanceConfigJson,
            ImageGenerateConfigJson = req.ImageGenerateConfigJson
        };

        if (req.StepType != null && Enum.TryParse<StepType>(req.StepType, out var stepType))
            step.StepType = stepType;

        if (req.AgentInputContextMode != null
            && Enum.TryParse<AgentInputContextMode>(req.AgentInputContextMode, out var mode))
            step.AgentInputContextMode = mode;

        if (req.MaxIterations.HasValue)
            step.MaxIterations = req.MaxIterations.Value;

        if (req.CacheMode != null && Enum.TryParse<StepCacheMode>(req.CacheMode, out var cacheMode))
            step.CacheMode = cacheMode;

        return step;
    }
}

/// <summary>Which normalization <see cref="WorkflowStepNormalizer"/> applied to a review loop.</summary>
public enum WorkflowNormalizationChangeKind
{
    /// <summary>The loop's target moved to an earlier step that can improve on a repeat.</summary>
    Retargeted,

    /// <summary>The loop was dropped because nothing could improve on a repeat.</summary>
    Removed
}

/// <summary>
/// One review-loop normalization, in the orders of the proposal the model authored, so a proposal
/// surface can render it without recomputing <see cref="WorkflowStepNormalizer.Normalize"/>.
/// </summary>
/// <param name="Kind">Whether the loop was retargeted or removed.</param>
/// <param name="StepOrder">The loop's order in the authored proposal.</param>
/// <param name="Label">The loop's label, for the human-readable report.</param>
/// <param name="StepType">The loop's step type as authored (a ReviewLoop).</param>
/// <param name="FromTarget">The loop's target in the authored proposal.</param>
/// <param name="ToTarget">The retargeted target; <c>null</c> when the loop was removed.</param>
public record WorkflowNormalizationChange(
    WorkflowNormalizationChangeKind Kind,
    int StepOrder,
    string? Label,
    string? StepType,
    int? FromTarget,
    int? ToTarget);

/// <summary>The normalized steps and the changes that produced them.</summary>
public record WorkflowNormalizationResult(
    IReadOnlyList<CreateWorkflowStepRequest> Steps,
    IReadOnlyList<WorkflowNormalizationChange> Changes);

/// <summary>
/// The one normalizer of an assistant-proposed step list, applied in
/// <see cref="WorkflowEditDiffService.ComputeDiff"/> so every proposal surface (the REST propose/apply,
/// the chat tools and the patch tools) agrees.
/// <para>
/// It removes the shape the linter reports as WL003 (a review loop whose re-run span contains nothing
/// that makes new output): for every review loop with a target that exists and is earlier than the loop,
/// if no step in <c>[target, loop)</c> can improve on a repeat (the same rule as
/// <c>StepGraphLinter</c>'s <c>CanImproveOnRepeat</c>: a model step, the dailies room or video
/// generation), the loop is retargeted to the latest earlier step that can improve. When there is no
/// such step the loop is removed: only the steps above it move down by one (<c>originalOrder - 1</c>)
/// and only the references at or after it move with them (the reference shifting is
/// <see cref="RecipePatches.ShiftJson"/>, not a second copy of it). Orders below the removed loop are
/// left untouched; there is no positional renumbering.
/// </para>
/// <para>
/// Only a contiguous 1..N graph is normalized. A legacy/hand-written graph with gaps or duplicate
/// orders is returned untouched (the same instance) rather than renumbered, so orders it never
/// mentions keep their meaning.
/// </para>
/// <para>
/// Pure and idempotent: the same steps always give the same steps, normalizing the output changes
/// nothing, and a graph whose loop already spans an improving step is returned untouched (the same
/// list instance, so its serialized token payload is byte-identical).
/// </para>
/// </summary>
public static class WorkflowStepNormalizer
{
    public static IReadOnlyList<CreateWorkflowStepRequest> Normalize(IReadOnlyList<CreateWorkflowStepRequest> steps) =>
        NormalizeWithChanges(steps).Steps;

    /// <summary>
    /// The same normalization as <see cref="Normalize"/>, also reporting each loop it retargeted or
    /// removed so a proposal surface can render it without recomputing.
    /// </summary>
    public static WorkflowNormalizationResult NormalizeWithChanges(IReadOnlyList<CreateWorkflowStepRequest> steps)
    {
        var changes = new List<WorkflowNormalizationChange>();
        List<CreateWorkflowStepRequest> ordered = [.. steps.OrderBy(step => step.StepOrder)];

        // Only a graph numbered exactly 1..N is safe to renumber. A legacy or hand-written workflow may
        // use non-contiguous or duplicate orders as real identity; normalizing it would move steps the
        // proposal never mentioned (and make a later loop lookup miss), so leave it untouched.
        if (!HasContiguousOrders(ordered))
            return new WorkflowNormalizationResult(steps, changes);

        List<int> loopOrders = [.. ordered
            .Where(step => IsStepType(step, StepType.ReviewLoop))
            .Select(step => step.StepOrder)
            .Distinct()
            .OrderByDescending(order => order)];

        List<CreateWorkflowStepRequest>? normalized = null;
        foreach (int loopOrder in loopOrders)
        {
            List<CreateWorkflowStepRequest> current = normalized ?? ordered;

            // Descending order means an earlier removal only shifts steps above it, but a defensive
            // FirstOrDefault keeps a loop that is already gone from throwing.
            CreateWorkflowStepRequest? loop = current.FirstOrDefault(step => step.StepOrder == loopOrder);
            if (loop is null)
                continue;

            // A missing, self or later target is a linter ERROR the proposal surfaces refuse; it is not
            // this normalizer's shape to invent a fix for.
            if (loop.LoopTargetStepOrder is not { } target
                || target >= loopOrder
                || !current.Any(step => step.StepOrder == target))
            {
                continue;
            }

            if (current.Any(step => step.StepOrder >= target && step.StepOrder < loopOrder && CanImproveOnRepeat(step)))
                continue; // the loop can change the result: leave it byte-identical

            CreateWorkflowStepRequest? improving = current
                .Where(step => step.StepOrder < loopOrder && CanImproveOnRepeat(step))
                .MaxBy(step => step.StepOrder);

            if (improving is not null)
            {
                int index = current.FindIndex(step => step.StepOrder == loopOrder);
                current[index] = loop with { LoopTargetStepOrder = improving.StepOrder };
                normalized = current;
                changes.Add(new WorkflowNormalizationChange(
                    WorkflowNormalizationChangeKind.Retargeted,
                    loopOrder,
                    loop.Label,
                    loop.StepType,
                    loop.LoopTargetStepOrder,
                    improving.StepOrder));
                continue;
            }

            // Nothing earlier can improve on a repeat: the loop would run forever without changing the
            // film, so drop it and move only the steps and references above it.
            normalized = WithoutLoop(current, loopOrder);
            changes.Add(new WorkflowNormalizationChange(
                WorkflowNormalizationChangeKind.Removed,
                loopOrder,
                loop.Label,
                loop.StepType,
                loop.LoopTargetStepOrder,
                null));
        }

        return new WorkflowNormalizationResult(normalized ?? steps, changes);
    }

    /// <summary>True when the sorted orders are exactly 1..N (which also rejects gaps and duplicates).</summary>
    private static bool HasContiguousOrders(IReadOnlyList<CreateWorkflowStepRequest> ordered)
    {
        for (int i = 0; i < ordered.Count; i++)
            if (ordered[i].StepOrder != i + 1)
                return false;
        return true;
    }

    /// <summary>The same rule as the linter's <c>CanImproveOnRepeat</c>, over a request before it is a <c>WorkflowStep</c>.</summary>
    private static bool CanImproveOnRepeat(CreateWorkflowStepRequest step)
    {
        // BuildStep stores the default (Agent) for a missing or unparseable type, so mirror what will be saved.
        StepType type = Enum.TryParse(step.StepType, out StepType parsed) ? parsed : StepType.Agent;
        return type is StepType.Agent or StepType.Parallel or StepType.ForEach
            or StepType.EditRoom or StepType.GraphicsRoom or StepType.ColorGradeRoom
            or StepType.DailiesRoom or StepType.VideoGenerate;
    }

    private static bool IsStepType(CreateWorkflowStepRequest step, StepType type) =>
        Enum.TryParse(step.StepType, out StepType parsed) && parsed == type;

    /// <summary>
    /// Removes one step, moves only the references at or after <paramref name="loopOrder"/> + 1 down by
    /// one, and renumbers only the steps above it (<c>originalOrder - 1</c>). Orders below the removed
    /// loop are left exactly as they were; there is no positional renumbering.
    /// </summary>
    private static List<CreateWorkflowStepRequest> WithoutLoop(List<CreateWorkflowStepRequest> ordered, int loopOrder) =>
    [
        .. ordered
            .Where(step => step.StepOrder != loopOrder)
            .Select(step => ShiftReferences(step, loopOrder + 1, -1))
            .Select(step => step.StepOrder > loopOrder
                ? step with { StepOrder = step.StepOrder - 1 }
                : step),
    ];

    /// <summary>Moves every step reference at or after <paramref name="at"/> by <paramref name="by"/> (the configs are moved by <see cref="RecipePatches.ShiftJson"/>).</summary>
    private static CreateWorkflowStepRequest ShiftReferences(CreateWorkflowStepRequest step, int at, int by)
    {
        string? Json(string? json) => json is null ? null : RecipePatches.ShiftJson(json, at, by);
        string? Text(string? text) =>
            int.TryParse(text, out int order) && order >= at
                ? (order + by).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : text;

        return step with
        {
            LoopTargetStepOrder = step.LoopTargetStepOrder is { } loop && loop >= at ? loop + by : step.LoopTargetStepOrder,
            TrueBranchStepOrder = Text(step.TrueBranchStepOrder),
            FalseBranchStepOrder = Text(step.FalseBranchStepOrder),
            SelectedPriorStepOrdersJson = Json(step.SelectedPriorStepOrdersJson),
            InputMappingJson = Json(step.InputMappingJson),
            ParallelAgentIdsJson = Json(step.ParallelAgentIdsJson),
            ExtractConfigJson = Json(step.ExtractConfigJson),
            VideoAnalyzeConfigJson = Json(step.VideoAnalyzeConfigJson),
            VideoCompileConfigJson = Json(step.VideoCompileConfigJson),
            EditRoomConfigJson = Json(step.EditRoomConfigJson),
            GraphicsRoomConfigJson = Json(step.GraphicsRoomConfigJson),
            ColorGradeRoomConfigJson = Json(step.ColorGradeRoomConfigJson),
            ConditionalConfigJson = Json(step.ConditionalConfigJson),
            ReviewLoopConfigJson = Json(step.ReviewLoopConfigJson),
            VideoGenerateConfigJson = Json(step.VideoGenerateConfigJson),
            VoiceoverConfigJson = Json(step.VoiceoverConfigJson),
            ThumbnailGenerateConfigJson = Json(step.ThumbnailGenerateConfigJson),
            DailiesRoomConfigJson = Json(step.DailiesRoomConfigJson),
            TimelineRenderConfigJson = Json(step.TimelineRenderConfigJson),
            DesignSystemConfigJson = Json(step.DesignSystemConfigJson),
            ContinueCompositionJson = Json(step.ContinueCompositionJson),
            ExtractFrameConfigJson = Json(step.ExtractFrameConfigJson),
            AudioGenerateConfigJson = Json(step.AudioGenerateConfigJson),
            PinInsertConfigJson = Json(step.PinInsertConfigJson),
            AudioCleanupConfigJson = Json(step.AudioCleanupConfigJson),
            VideoEnhanceConfigJson = Json(step.VideoEnhanceConfigJson),
            ImageGenerateConfigJson = Json(step.ImageGenerateConfigJson),
        };
    }
}
