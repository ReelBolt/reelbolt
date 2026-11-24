using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Replay;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// The models a run of <paramref name="organizationId"/> may choose (docs/design/app-inputs.md 8.3): the same rows as
/// <c>GET /api/v1/inference-providers/selectable</c> (enabled, the platform's fully priced ones and the organization's own
/// while its plan allows bring-your-own), re-read at every check. <see cref="AppModelRules"/> then applies the step's
/// capability, the author's <c>allowed</c> list and the job. Unregistered (a minimally built validator), every model value
/// is refused with <see cref="AppInputValueValidator.ModelUnavailableCode"/> rather than guessed.
/// </summary>
public interface IAppInputModelResolverSource
{
    /// <summary>The selectable models of one run start; read once per check.</summary>
    Task<IReadOnlyList<AppModelCandidate>> CandidatesAsync(Guid? organizationId, Guid projectId, CancellationToken ct);
}

/// <summary>
/// The verdict of the app-input check at a run start (docs/design/app-inputs.md 6.2). <see cref="Applies"/> is false for a
/// workflow that uses no app input (nothing to store, nothing composed, the run start is exactly today's). A refusal
/// carries the code and the plain message; otherwise the canonical values to store, the composed request text, the
/// steps as they will run (bound, detached copies) and the steps the input-decided branches leave reachable.
/// </summary>
public sealed record AppInputGateResult(
    bool Applies,
    string? Code,
    string? Message,
    IReadOnlyDictionary<string, JsonElement>? Inputs,
    string? UserRequest,
    IReadOnlyList<WorkflowStep>? BoundSteps,
    IReadOnlySet<int>? ReachableStepOrders,
    string? InputId = null,
    IReadOnlyDictionary<Guid, Guid>? AgentModels = null,
    IReadOnlySet<Guid>? StepModels = null,
    string? Feature = null,
    MediaEgressSummary? MediaEgress = null,
    string? MediaEgressBy = null)
{
    /// <summary>True when the run must not start.</summary>
    public bool Refused => Code is not null;

    /// <summary>The bound, reachable steps to price, lint and gate (null when the app uses no input).</summary>
    public IReadOnlyList<WorkflowStep>? ReachableSteps =>
        BoundSteps is null ? null : [.. BoundSteps.Where(s => ReachableStepOrders is null || ReachableStepOrders.Contains(s.StepOrder))];

    /// <summary>A workflow that uses no app input: the notes stay the request, nothing else changes.</summary>
    public static AppInputGateResult NotApplicable(string? notes) => new(false, null, null, null, notes, null, null);

    /// <summary>A refusal (400).</summary>
    public static AppInputGateResult Refuse(string code, string message, string? inputId = null, string? feature = null) =>
        new(true, code, message, null, null, null, null, inputId, Feature: feature);

    /// <summary>
    /// The 400 body of a refusal: <c>{ code, message }</c>, plus <c>inputId</c> when the refusal names one input, so the run
    /// page shows the sentence next to that field (docs/design/app-inputs.md 21).
    /// </summary>
    public object ErrorBody() => (InputId, Feature) switch
    {
        ({ } inputId, { } feature) => new { code = Code, message = Message, inputId, feature },
        ({ } inputId, null) => new { code = Code, message = Message, inputId },
        _ => new { code = Code, message = Message }
    };
}

/// <summary>
/// What the run gate knows about the permission to send media for one run start (docs/design/app-inputs.md "Permission
/// to send media as an app field"): whether the run's brief chose an avatar (an inline generate step then sends that
/// person's media), and who is giving the permission. <see cref="GrantedBy"/> is null for every caller that may not give
/// it, so a value of <c>true</c> from an unknown caller is refused (fail closed).
/// </summary>
public sealed record MediaEgressGate(bool AvatarChosen, string? GrantedBy)
{
    /// <summary>The gate of a stored brief (Retry, Resume): the consent belongs to the run, whoever retries it.</summary>
    public static MediaEgressGate FromStored(ReelBolt.Shared.Briefs.StoredRunBrief? stored) =>
        new(stored?.Avatar?.AvatarId is not null, stored?.AllowMediaEgressBy);
}

/// <summary>
/// The manifest-aware check of <c>brief.inputs</c> at every run start (docs/design/app-inputs.md 6.2), run BEFORE the
/// plan, estimate and credit gates so a bad input never costs anything and never writes a run row:
/// <list type="number">
/// <item>the typed check of the values against the workflow's manifest (<see cref="AppInputValueChecker"/>: unknown ids,
/// wrong types, ranges, choices, text caps, defaults, hidden inputs dropped, a missing required visible input is
/// <c>APP_INPUT_MISSING</c>);</item>
/// <item>the model re-resolution seam (lane L4, <see cref="IAppInputModelResolverSource"/>);</item>
/// <item>a dry-run bind (<see cref="AppInputBinder"/>, the same pure binder the engine runs on the snapshot) onto detached
/// copies of the steps, then each bound config's own <c>Validate()</c> and the save validator, so a value that would make
/// a step invalid is refused now, naming the input.</item>
/// </list>
/// </summary>
public sealed class AppInputValueValidator(
    InferenceApiDbContext db,
    IAppInputModelResolverSource? models = null,
    IOrganizationEntitlements? entitlements = null,
    ILogger<AppInputValueValidator>? logger = null,
    IAppInputSpendPricer? spendPricer = null)
{
    /// <summary>The bound credits cap is below what the step's bound clips cost: refused up front, never after the paid steps before it.</summary>
    public const string SpendCapTooLowCode = "APP_INPUT_SPEND_CAP_TOO_LOW";

    /// <summary>A chosen model that cannot be used for the step (docs/design/app-inputs.md 8.3).</summary>
    public const string ModelUnavailableCode = AppInputValueProblem.ModelUnavailable;

    /// <summary>
    /// A chosen model of a kind the organization's plan does not include (docs/design/app-inputs.md 28): 400 with the input and
    /// the plan <c>feature</c>, the same rule (<see cref="AppModelRules.PlanFeature"/>) the options list marks the cards with.
    /// </summary>
    public const string ModelNotInPlanCode = "APP_INPUT_MODEL_NOT_IN_PLAN";

    /// <summary>
    /// Checks a run start of <paramref name="workflowId"/>. <paramref name="steps"/> are the steps that will run (a retry's
    /// or resume's snapshot); null = the saved workflow's. The manifest is <paramref name="manifestDefinitionId"/>'s (the
    /// run's snapshot) when it has one, else the workflow's.
    /// </summary>
    /// <param name="forEstimate">True for the estimate: a missing required input is not a refusal (the page prices as the person types).</param>
    public async Task<AppInputGateResult> CheckAsync(
        Guid? organizationId,
        Guid projectId,
        Guid workflowId,
        IReadOnlyList<WorkflowStep>? steps,
        Guid? manifestDefinitionId,
        IReadOnlyDictionary<string, JsonElement>? raw,
        string? notes,
        CancellationToken ct,
        bool forEstimate = false,
        MediaEgressGate? mediaEgress = null)
    {
        List<WorkflowStep> toCheck = steps is null
            ? await db.WorkflowSteps.AsNoTracking()
                .Where(s => s.WorkflowDefinitionId == workflowId && s.WorkflowDefinition.ProjectId == projectId)
                .OrderBy(s => s.StepOrder)
                .ToListAsync(ct)
            : [.. steps.OrderBy(s => s.StepOrder)];

        string? manifestJson = null;
        if (manifestDefinitionId is { } snapshotId && snapshotId != workflowId)
        {
            manifestJson = await db.WorkflowDefinitions.AsNoTracking()
                .Where(w => w.Id == snapshotId && w.ProjectId == projectId)
                .Select(w => w.AppManifestJson)
                .FirstOrDefaultAsync(ct);
        }

        manifestJson ??= await db.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.Id == workflowId && w.ProjectId == projectId)
            .Select(w => w.AppManifestJson)
            .FirstOrDefaultAsync(ct);

        AppManifest? manifest = AppManifestJson.TryParse(manifestJson);
        IReadOnlyList<AppModelCandidate>? candidates = models is not null && UsesModelInputs(manifest)
            ? await models.CandidatesAsync(organizationId, projectId, ct)
            : null;
        OrganizationEntitlementSet? plan = entitlements is not null && organizationId is { } org && raw is { Count: > 0 } && UsesModelInputs(manifest)
            ? await entitlements.GetAsync(org, ct)
            : null;
        AppInputGateResult result = Evaluate(manifest, toCheck, raw, notes, forEstimate: forEstimate, candidates: candidates, plan: plan, logger: logger, mediaEgress: mediaEgress);
        return forEstimate || manifest is null || spendPricer is null ? result : await RefuseCapBelowPriceAsync(manifest, toCheck, result, spendPricer, ct);
    }

    /// <summary>
    /// A step whose credits cap is an app input (<c>appInputs.maxSpendCredits</c>) is priced as bound: its hold in credits
    /// (the clips the bound config may buy, the upper bound when the plan's shot count is not known yet). A cap under that
    /// price would end in BUDGET_EXCEEDED after the paid analysis, so the run is refused now, naming the input.
    /// </summary>
    public static async Task<AppInputGateResult> RefuseCapBelowPriceAsync(
        AppManifest manifest, IReadOnlyList<WorkflowStep> authored, AppInputGateResult result, IAppInputSpendPricer pricer, CancellationToken ct)
    {
        if (result.Refused || result.ReachableSteps is not { } bound)
            return result;
        foreach (WorkflowStep step in bound.OrderBy(s => s.StepOrder))
        {
            string? config = step.StepType switch
            {
                StepType.VideoGenerate => step.VideoGenerateConfigJson,
                StepType.AudioGenerate => step.AudioGenerateConfigJson,
                _ => null
            };
            WorkflowStep? source = authored.FirstOrDefault(a => a.Id == step.Id);
            string? authoredConfig = source is null ? null : source.StepType == StepType.VideoGenerate ? source.VideoGenerateConfigJson : source.AudioGenerateConfigJson;
            if (config is null || authoredConfig is null || BoundInputs(step.StepType, authoredConfig) is not { } bindings)
                continue;
            bool capBound = bindings.TryGetValue("maxSpendCredits", out string? capInput);
            string? inputId = capInput ?? bindings.GetValueOrDefault("maxClips") ?? bindings.GetValueOrDefault("takes") ?? bindings.Values.First();
            double? cap = SpendCapOf(config);
            double? price = await pricer.CreditsAsync(step, ct);
            if (cap is not { } limit || price is not { } credits || credits <= limit)
                continue;
            string label = LabelOf(manifest, inputId!);
            return AppInputGateResult.Refuse(SpendCapTooLowCode,
                capBound
                    ? $"“{label}” is {limit:0.##} credits, but the shots chosen cost up to {Math.Ceiling(credits):0} credits. Raise “{label}” or choose fewer clips or takes."
                    : $"This step's spending limit is {limit:0.##} credits, but “{label}” makes it cost up to {Math.Ceiling(credits):0} credits. Choose a smaller value.",
                inputId);
        }

        return result;
    }

    /// <summary>
    /// The settings an app input binds on this step as the setting's catalogue path to input id, read with the binder's own
    /// reader (<see cref="AppBindingAnalysis.FindAppInputs"/>, <see cref="AppValueBinding.TryRead"/>: case-insensitive, plain
    /// and <c>{"input": id}</c> forms), so this check and the binder cannot disagree; null when none.
    /// </summary>
    private static Dictionary<string, string>? BoundInputs(StepType stepType, string configJson)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(configJson) is not System.Text.Json.Nodes.JsonObject config
                || AppBindingAnalysis.FindAppInputs(config) is not (string, System.Text.Json.Nodes.JsonObject map))
                return null;
            Dictionary<string, string> bound = new(StringComparer.Ordinal);
            foreach ((string setting, System.Text.Json.Nodes.JsonNode? target) in map)
            {
                if (!AppValueBinding.TryRead(target, out AppValueBinding? binding, out _)
                    || ReelBolt.Shared.Workflows.AppBindableCatalog.Find(stepType, AppBindingAnalysis.PatternOf(setting)) is not { } field)
                    continue;
                bound[field.Path] = binding!.InputId;
            }

            return bound.Count == 0 ? null : bound;
        }
        catch (JsonException) { return null; }
    }

    private static double? SpendCapOf(string configJson)
    {
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(configJson) is System.Text.Json.Nodes.JsonObject o
                ? o.FirstOrDefault(p => string.Equals(p.Key, "maxSpendCredits", StringComparison.OrdinalIgnoreCase)).Value?.GetValue<double>()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>The pure core of <see cref="CheckAsync"/> (no storage), shared by the estimate and the tests.</summary>
    public static AppInputGateResult Evaluate(
        AppManifest? manifest,
        IReadOnlyList<WorkflowStep> steps,
        IReadOnlyDictionary<string, JsonElement>? raw,
        string? notes,
        AppModelResolver? resolveModel = null,
        bool forEstimate = false,
        IReadOnlyList<AppModelCandidate>? candidates = null,
        OrganizationEntitlementSet? plan = null,
        ILogger? logger = null,
        Func<IEnumerable<CreateWorkflowStepRequest>, IReadOnlyList<string>>? saveValidate = null,
        MediaEgressGate? mediaEgress = null)
    {
        List<WorkflowStep> ordered = [.. steps.OrderBy(s => s.StepOrder)];
        IReadOnlyList<AppBindingStep> bindingSteps = [.. ordered.Select(AppBindingStep.From)];

        // The permission to send media is a field the app derives from its steps (never stored). This run's avatar decides
        // an inline generate step's need; the field itself is offered wherever a run could need it.
        bool avatarChosen = mediaEgress?.AvatarChosen == true;
        MediaEgressBriefAvatar avatarNow = avatarChosen ? MediaEgressBriefAvatar.Chosen : MediaEgressBriefAvatar.None;
        MediaEgressBriefAvatar avatarOffer = avatarChosen ? MediaEgressBriefAvatar.Chosen : MediaEgressBriefAvatar.Offered;
        if (!UsesAppInputs(manifest, bindingSteps))
        {
            // An app whose only field is the permission: without a grant the run start is exactly what it was.
            bool present = MediaEgressPermission.Describe(bindingSteps, avatarOffer).Present;
            string? unknown = raw?.Keys.FirstOrDefault(k => !present || !string.Equals(k, MediaEgressPermission.InputId, StringComparison.Ordinal));
            if (unknown is not null)
                return AppInputGateResult.Refuse(AppInputValueProblem.Invalid, $"This app has no input named \"{Clip(unknown)}\".");
            if (!MediaEgressPermission.IsRequested(raw))
            {
                if (raw is not null && raw.TryGetValue(MediaEgressPermission.InputId, out JsonElement off)
                    && off.ValueKind is not (JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined))
                    return AppInputGateResult.Refuse(AppInputValueProblem.Invalid, $"“{MediaEgressPermission.Label}” must be on or off.", MediaEgressPermission.InputId);
                return !forEstimate && MediaEgressPermission.Describe(bindingSteps, avatarNow).Required
                    ? AppInputGateResult.Refuse(AppInputValueProblem.Missing, MediaEgressPermission.RequiredMessage, MediaEgressPermission.InputId)
                    : AppInputGateResult.NotApplicable(notes);
            }
        }

        AppManifest effective = MediaEgressPermission.WithField(manifest ?? new AppManifest(), bindingSteps, avatarOffer) ?? new AppManifest();
        AppInputCheckResult check = AppInputValueChecker.Check(effective, bindingSteps, raw);
        AppInputValueProblem? problem = check.Problems.FirstOrDefault(p => !forEstimate || p.Code != AppInputValueProblem.Missing);
        if (problem is not null)
            return AppInputGateResult.Refuse(problem.Code, problem.Message, problem.InputId);

        // The permission: required when a step the run reaches cannot run without it and has its own switch off; and a grant
        // counts only from a caller that may give it (fail closed). An estimate is never refused for either.
        IReadOnlySet<int> reach = StaticBranchResolver.ReachableStepOrders(bindingSteps, check.Values);
        bool grantsMediaEgress = MediaEgressPermission.IsRequested(check.Values);
        if (!forEstimate)
        {
            if (grantsMediaEgress && !MediaEgressPermission.IsGrantor(mediaEgress?.GrantedBy))
                return AppInputGateResult.Refuse(AppInputValueProblem.Invalid, MediaEgressPermission.NotAllowedHereMessage, MediaEgressPermission.InputId);
            if (!grantsMediaEgress && MediaEgressPermission.Describe(bindingSteps, avatarNow, reach).Required)
                return AppInputGateResult.Refuse(AppInputValueProblem.Missing, MediaEgressPermission.RequiredMessage, MediaEgressPermission.InputId);
        }

        // Lane L4: the chosen models are re-resolved against the organization's selectable models (8.3); a model the
        // rules refuse is said as such, and without candidates (nothing registered) every model value is refused.
        resolveModel ??= candidates is null ? null : AppModelRules.Resolver(effective, candidates);
        IReadOnlyDictionary<Guid, Guid>? agentModels = null;
        if (AppModelRules.HasAgentModelValue(effective, check.Values))
        {
            agentModels = AppModelRules.AgentModels(effective, check.Values, candidates ?? [], out string? refusedAgentInput);
            if (refusedAgentInput is not null)
            {
                return AppInputGateResult.Refuse(
                    ModelUnavailableCode, $"The model chosen for “{LabelOf(effective, refusedAgentInput)}” cannot be used for this app. Choose another one.", refusedAgentInput);
            }
        }

        HashSet<string> unavailableModels = new(StringComparer.Ordinal);
        AppModelResolver resolver = (inputId, capabilityId, step) =>
        {
            AppModelTarget? target = resolveModel?.Invoke(inputId, capabilityId, step);
            if (target is null)
                unavailableModels.Add(inputId);
            return target;
        };

        AppInputBindResult bound = AppInputBinder.Bind(effective, bindingSteps, check.Values, notes, resolver, briefAvatar: avatarChosen);
        if (unavailableModels.Count > 0)
        {
            string label = LabelOf(effective, unavailableModels.First());
            return AppInputGateResult.Refuse(
                ModelUnavailableCode, $"The model chosen for “{label}” cannot be used for this app. Choose another one.", unavailableModels.First());
        }

        if (bound.Problems.Count > 0)
            return AppInputGateResult.Refuse(AppInputValueProblem.Invalid, bound.Problems[0].Message);

        List<WorkflowStep> boundSteps = [];
        for (int i = 0; i < ordered.Count; i++)
        {
            AppBoundStep boundStep = bound.Steps[i];
            if (!boundStep.Changed)
            {
                boundSteps.Add(ordered[i]);
                continue;
            }

            WorkflowStep copy = WorkflowStepCloner.Clone(ordered[i], ordered[i].WorkflowDefinitionId);
            copy.Id = ordered[i].Id;
            copy.AgentDefinition = ordered[i].AgentDefinition;
            AppBindingStep.SetConfigJson(copy, boundStep.Step.ConfigJson);
            boundSteps.Add(copy);
        }

        if (FirstUnfitStep(effective, ordered, boundSteps, bound, logger, saveValidate) is { } unfit)
            return AppInputGateResult.Refuse(AppInputValueProblem.Invalid, unfit.Message, unfit.InputId);

        IReadOnlySet<int> reachable = StaticBranchResolver.ReachableStepOrders(bindingSteps, check.Values);
        if (plan is not null && FirstModelNotInPlan(effective, bindingSteps, check.Values, reachable, plan) is { } notInPlan)
        {
            return AppInputGateResult.Refuse(
                ModelNotInPlanCode,
                $"{AppModelRules.PlanSentence(notInPlan.Feature)}, so the model chosen for “{LabelOf(effective, notInPlan.InputId)}” cannot be used. Upgrade your plan to use it.",
                notInPlan.InputId, notInPlan.Feature);
        }

        return new AppInputGateResult(
            true,
            null,
            null,
            check.Values.Count == 0 ? null : check.Values,
            bound.ComposedRequest,
            boundSteps,
            reachable,
            AgentModels: agentModels is { Count: > 0 } ? agentModels : null,
            StepModels: AppModelRules.StepModelChoices(effective, check.Values),
            MediaEgress: grantsMediaEgress ? MediaEgressPermission.Describe(bindingSteps, avatarNow, reach) : null,
            MediaEgressBy: grantsMediaEgress ? mediaEgress?.GrantedBy : null);
    }

    /// <summary>
    /// The first step <c>model</c> input with a value whose model kind the plan does not include (docs/design/app-inputs.md 28), on a
    /// step the run can reach: the rule the options list marks every card of the input with (<see cref="AppModelRules.PlanFeature"/>).
    /// </summary>
    private static (string InputId, string Feature)? FirstModelNotInPlan(
        AppManifest manifest, IReadOnlyList<AppBindingStep> steps, IReadOnlyDictionary<string, JsonElement> values,
        IReadOnlySet<int> reachable, OrganizationEntitlementSet plan)
    {
        AppBindingMap map = AppBindingAnalysis.Analyze(manifest, steps);
        foreach (AppManifestInput input in manifest.Inputs ?? [])
        {
            if (input.Kind != AppManifestVocabulary.KindInput || input.Id is null || input.Agent is not null || !values.ContainsKey(input.Id)
                || !AppInputTypes.TryParse(input.Type, out AppInputType type) || type != AppInputType.Model)
                continue;
            foreach (AppInputUse use in map.UsesOf(input.Id).Where(u => u is { Kind: AppInputUseKind.Value, Field.IsModel: true }))
            {
                if (use.StepOrder is not { } order || !reachable.Contains(order) || steps.FirstOrDefault(s => s.StepOrder == order) is not { } step)
                    continue;
                string? path = use.Field?.Path;
                if (AppModelRules.CapabilityFor(step, path) is { } capability
                    && AppModelRules.PlanFeature(capability, AppModelRules.JobFor(step, path), plan, AppModelRules.ClipShortSide(step)) is { } feature)
                    return (input.Id, feature);
            }
        }

        return null;
    }

    /// <summary>True when the manifest has a <c>model</c> input (the only case the selectable models are read).</summary>
    private static bool UsesModelInputs(AppManifest? manifest) =>
        (manifest?.Inputs ?? []).Any(i => i.Kind == AppManifestVocabulary.KindInput
            && AppInputTypes.TryParse(i.Type, out AppInputType type) && type == AppInputType.Model);

    /// <summary>
    /// True when the app uses app inputs at all: an <c>input</c> input or a request template in the manifest, or a binding
    /// in a step (a placeholder, an <c>appInputs</c> map, an <c>input.*</c> condition).
    /// </summary>
    public static bool UsesAppInputs(AppManifest? manifest, IReadOnlyList<AppBindingStep> steps) => AppInputUsage.Uses(manifest, steps);

    /// <summary>
    /// The first save-validator problem the bound values introduce (one present after binding and not before), named by
    /// the inputs that step consumed; null when every bound step is as valid as the authored one.
    /// </summary>
    private static (string Message, string? InputId)? FirstUnfitStep(AppManifest manifest, List<WorkflowStep> authored, List<WorkflowStep> boundSteps, AppInputBindResult bound, ILogger? logger, Func<IEnumerable<CreateWorkflowStepRequest>, IReadOnlyList<string>>? saveValidate)
    {
        if (!bound.Steps.Any(s => s.Changed))
            return null;

        saveValidate ??= steps => StepConfigSaveValidator.Validate(steps);
        HashSet<string> before;
        IReadOnlyList<string> after;
        try
        {
            before = [.. saveValidate(authored.Select(AssistantStepShapes.FromStep))];
            after = saveValidate(boundSteps.Select(AssistantStepShapes.FromStep));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed: a bound config the save validator cannot even read is not a config to run.
            logger?.LogWarning(ex, "The save validator failed on the steps bound with app input values; refusing the run");
            return ("The values chosen do not produce a valid configuration for this app.", bound.Steps.FirstOrDefault(s => s.Changed)?.Consumed.Keys.Order(StringComparer.Ordinal).FirstOrDefault());
        }

        foreach (string error in after)
        {
            if (before.Contains(error))
                continue;
            int index = bound.Steps.ToList().FindIndex(s => s.Changed && error.StartsWith($"Step {s.Step.StepOrder} ", StringComparison.Ordinal));
            List<string> ids = index >= 0 ? [.. bound.Steps[index].Consumed.Keys.Order(StringComparer.Ordinal)] : [];
            string labels = string.Join(", ", ids.Select(id => $"“{LabelOf(manifest, id)}”"));
            return (labels.Length == 0 ? error : $"The value of {labels} does not fit: {error}", ids.FirstOrDefault());
        }

        return null;
    }

    private static string LabelOf(AppManifest manifest, string inputId)
    {
        AppManifestInput? input = (manifest.Inputs ?? []).FirstOrDefault(i => i.Kind == AppManifestVocabulary.KindInput && i.Id == inputId);
        return input is null ? Clip(inputId) : AppInputValueChecker.Label(input);
    }

    private static string Clip(string text) => text.Length <= 40 ? text : text[..40];
}
