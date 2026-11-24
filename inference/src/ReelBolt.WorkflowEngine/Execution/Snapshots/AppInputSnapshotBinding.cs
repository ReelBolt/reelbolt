using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Execution.Snapshots;

/// <summary>
/// The workflow changed between Run and the claim so that a stored app-input value no longer binds (docs/design/app-inputs.md
/// 6.3). The run ends Failed before its first step, as a platform failure (nothing was held or spent).
/// </summary>
public sealed class AppInputsStaleException(IReadOnlyList<AppBindingProblem> problems)
    : InvalidOperationException(UserMessage + " " + string.Join(" ", problems.Select(p => p.Message)))
{
    public const string FailureCode = "APP_INPUTS_STALE";

    public const string UserMessage = "This app changed after you pressed Run. Run it again.";

    public string Code => FailureCode;

    public IReadOnlyList<AppBindingProblem> Problems { get; } = problems;
}

/// <summary>
/// What a run's app inputs mean to the engine while it runs (docs/design/app-inputs.md 9.2, 9.3, 10.1): the canonical
/// values (the <c>input.*</c> parameters of input-decided conditions), each step's step-cache component (the values that
/// step consumed; a step that consumed none has none, so its key is unchanged), and the steps the input-decided branches
/// leave reachable (the holds plan over these only). Null for a run that carries no app input.
/// </summary>
public sealed class AppInputRunState
{
    private readonly IReadOnlyDictionary<int, string> _components;
    private readonly IReadOnlyList<AppBindingStep> _steps;

    public AppInputRunState(
        IReadOnlyDictionary<string, JsonElement> values,
        IReadOnlyDictionary<int, string> components,
        IReadOnlyList<AppBindingStep> steps,
        IReadOnlySet<Guid>? stepModels = null)
    {
        Values = values;
        StepModels = stepModels;
        _components = components;
        _steps = steps;
        ReachableStepOrders = StaticBranchResolver.ReachableStepOrders(steps, values);
    }

    /// <summary>The run's canonical values (empty when only defaults that resolved to nothing were stored).</summary>
    public IReadOnlyDictionary<string, JsonElement> Values { get; }

    /// <summary>
    /// The model capabilities the run's step <c>model</c> inputs chose (<see cref="AppModelRules.StepModelChoices"/>): the holds price
    /// a bound Voiceover's speech at that model (docs/design/app-inputs.md 28). Null when none was chosen.
    /// </summary>
    public IReadOnlySet<Guid>? StepModels { get; }

    /// <summary>The step orders the run can reach with these values.</summary>
    public IReadOnlySet<int> ReachableStepOrders { get; }

    /// <summary>The <c>input.*</c> NCalc parameters, converted exactly as <see cref="StaticBranchResolver"/> converts them.</summary>
    public IReadOnlyDictionary<string, object?> ExpressionParameters => StaticBranchResolver.Parameters(Values);

    /// <summary>The step-cache component of the step at <paramref name="stepOrder"/>, or null when it consumed no input.</summary>
    public string? ComponentFor(int stepOrder) => _components.GetValueOrDefault(stepOrder);

    /// <summary>The steps of <paramref name="runSteps"/> the run can reach (the holds plan over these, 8.5, 9.3).</summary>
    public IReadOnlyList<WorkflowStep> Reachable(IReadOnlyList<WorkflowStep> runSteps)
    {
        if (_steps.Count == 0)
            return runSteps;
        List<WorkflowStep> reachable = [.. runSteps.Where(s => ReachableStepOrders.Contains(s.StepOrder))];
        return reachable.Count == runSteps.Count ? runSteps : reachable;
    }
}

/// <summary>
/// The engine half of app inputs at the snapshot (docs/design/app-inputs.md 6.3): the same pure <see cref="AppInputBinder"/>
/// the API ran as a dry run, applied to the run's cloned step rows with the stored canonical values, so every executor,
/// the replay planner and the change-request code read plain configs. A workflow with no app input is never touched.
/// </summary>
public static class AppInputSnapshotBinding
{
    /// <summary>
    /// Binds <paramref name="clones"/> in place (each config rewritten through <see cref="AppBindingStep.SetConfigJson"/>).
    /// Returns false (and the problems) when a value no longer binds; nothing is changed then. Idempotent: a bound config
    /// has nothing left to bind.
    /// </summary>
    public static bool TryBindInPlace(
        AppManifest? manifest,
        IReadOnlyList<WorkflowStep> clones,
        IReadOnlyDictionary<string, JsonElement>? values,
        out IReadOnlyList<AppBindingProblem> problems,
        AppModelResolver? resolveModel = null,
        bool briefAvatar = false)
    {
        problems = [];
        List<WorkflowStep> ordered = [.. clones.OrderBy(s => s.StepOrder)];
        IReadOnlyList<AppBindingStep> bindingSteps = [.. ordered.Select(AppBindingStep.From)];
        // A granted permission to send media binds onto the snapshot even when the app has no other field.
        if (!AppInputUsage.Uses(manifest, bindingSteps) && !MediaEgressPermission.IsRequested(values))
            return true;

        AppInputBindResult bound = AppInputBinder.Bind(
            manifest ?? new AppManifest(), bindingSteps, values ?? new Dictionary<string, JsonElement>(), resolveModel: resolveModel,
            briefAvatar: briefAvatar);
        if (!bound.IsValid)
        {
            problems = bound.Problems;
            return false;
        }

        for (int i = 0; i < ordered.Count; i++)
        {
            if (bound.Steps[i].Changed)
                AppBindingStep.SetConfigJson(ordered[i], bound.Steps[i].Step.ConfigJson);
        }

        return true;
    }

    /// <summary>
    /// The run state of <paramref name="execution"/>: null when it carries no app input value and its steps read none.
    /// The components come from binding <paramref name="authoredSteps"/> (the source workflow's steps, as the snapshot was
    /// taken from) in memory with the stored values; the reach from <paramref name="runSteps"/> (what runs).
    /// </summary>
    public static AppInputRunState? StateFor(
        WorkflowExecution execution,
        string? manifestJson,
        IReadOnlyList<WorkflowStep> authoredSteps,
        IReadOnlyList<WorkflowStep> runSteps)
    {
        StoredRunBrief? stored = RunBriefJson.TryParse(execution.RunBriefJson);
        IReadOnlyDictionary<string, JsonElement>? values = GrantedValues(stored);
        if (values is not { Count: > 0 })
            return null;

        AppManifest? manifest = AppManifestJson.TryParse(manifestJson);
        IReadOnlyList<AppBindingStep> authored = [.. authoredSteps.OrderBy(s => s.StepOrder).Select(AppBindingStep.From)];
        Dictionary<int, string> components = [];
        if (AppInputUsage.Uses(manifest, authored) || MediaEgressPermission.IsRequested(values))
        {
            // The component only names what a step consumed: a @model step's reads the chosen capability id (re-resolved
            // when the snapshot was taken, TakeAsync), so a different choice is a different key.
            AppInputBindResult bound = AppInputBinder.Bind(
                manifest ?? new AppManifest(), authored, values, resolveModel: AnyModel, briefAvatar: stored?.Avatar?.AvatarId is not null);
            foreach (AppBoundStep step in bound.Steps)
            {
                if (AppInputBinder.CacheComponent(step) is { } component)
                    components[step.Step.StepOrder] = component;
            }
        }

        return new AppInputRunState(
            values, components, [.. runSteps.OrderBy(s => s.StepOrder).Select(AppBindingStep.From)], AppModelRules.StepModelChoices(manifest, values));
    }

    /// <summary>
    /// The stored values the engine honours: the run's canonical inputs, with the permission to send media dropped unless the
    /// brief also names who allowed it (<see cref="StoredRunBrief.AllowMediaEgressBy"/>, written only by the run gate). A
    /// value with no known grantor is treated as not given: the steps keep their own switches (fail closed).
    /// </summary>
    public static IReadOnlyDictionary<string, JsonElement>? GrantedValues(StoredRunBrief? stored)
    {
        IReadOnlyDictionary<string, JsonElement>? values = stored?.Inputs;
        if (values is null || !values.ContainsKey(MediaEgressPermission.InputId) || MediaEgressPermission.IsGrantor(stored!.AllowMediaEgressBy))
            return values;
        return values.Where(p => p.Key != MediaEgressPermission.InputId).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    /// <summary>True when the manifest has a <c>model</c> input with a value: the only case the models are read at the snapshot.</summary>
    public static bool NeedsModels(AppManifest? manifest, IReadOnlyDictionary<string, JsonElement>? values) =>
        manifest is not null && values is { Count: > 0 }
        && (manifest.Inputs ?? []).Any(i => i.Kind == AppManifestVocabulary.KindInput && i.Id is not null && values.ContainsKey(i.Id)
            && AppInputTypes.TryParse(i.Type, out AppInputType type) && type == AppInputType.Model);

    /// <summary>
    /// The models the run's organization may use, re-read at the snapshot (docs/design/app-inputs.md 8.3): enabled rows of the
    /// platform and of this organization only (never another's), the system compute provider excluded. Pricing and the plan's
    /// bring-your-own rule were checked at queue; at run time the resolver still fails closed on both.
    /// </summary>
    public static async Task<IReadOnlyList<AppModelCandidate>> LoadModelCandidatesAsync(
        WorkflowEngineDbContext db, Guid organizationId, CancellationToken ct)
    {
        List<ModelCapabilityView> rows = await ModelCapabilityView.EnabledFor(db.ModelCapabilities.AsNoTracking(), organizationId)
            .Where(c => !c.IsSystem && c.Capability != InferenceProviderCapability.Compute)
            .ToListAsync(ct);
        return [.. rows.Select(r => new AppModelCandidate(
            r.Id, r.Capability.ToString(), r.Kind.ToString(), r.ModelName, r.Name, r.OrganizationId is null, r.IsDefault))];
    }

    /// <summary>
    /// The agents' chosen Chat models (agent id to capability id), or null when the app chose none; throws
    /// <see cref="AppInputsStaleException"/> when a chosen model can no longer be used.
    /// </summary>
    public static IReadOnlyDictionary<Guid, Guid>? AgentModelsOrThrow(
        AppManifest? manifest, IReadOnlyDictionary<string, JsonElement>? values, IReadOnlyList<AppModelCandidate>? candidates)
    {
        if (!AppModelRules.HasAgentModelValue(manifest, values))
            return null;
        IReadOnlyDictionary<Guid, Guid> chosen = AppModelRules.AgentModels(manifest!, values!, candidates ?? [], out string? refused);
        if (refused is not null)
            throw new AppInputsStaleException([new AppBindingProblem(null, $"The model chosen for \"{refused}\" can no longer be used.")]);
        return chosen.Count == 0 ? null : chosen;
    }

    /// <summary>A stand-in model target used only to read what a step consumes (never written anywhere).</summary>
    private static AppModelTarget? AnyModel(string inputId, Guid capabilityId, AppBindingStep step) => new(capabilityId, null);
}
