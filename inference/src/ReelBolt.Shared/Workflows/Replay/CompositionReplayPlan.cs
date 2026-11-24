using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows.Replay;

/// <summary>
/// What a composition revision does about the rest of the original workflow once the film has been
/// re-rendered (docs/design/composition-revisions.md "Replaying what depends on the film"). Persisted by
/// name on <c>composition_revisions.cut_policy</c>; append-only.
/// </summary>
public enum CompositionCutPolicy
{
    /// <summary>
    /// The default. Editorial decisions that depend on the film (the edit room, the story editor, the
    /// music/graphics/sound plans …) are KEPT as the owner approved them whenever the re-rendered film's
    /// analysis still offers the same ids at the same places; only the deterministic steps (analyse,
    /// compile, …) run again. A decision whose ids no longer resolve is re-made.
    /// </summary>
    KeepCut,

    /// <summary>"Re-edit the video too": every dependent step runs again, decisions included, and review loops loop.</summary>
    ReEdit
}

/// <summary>What happens to one step of the original workflow in a revision's replay. Persisted by name; append-only.</summary>
public enum ReplayStepDisposition
{
    /// <summary>Does not depend on the film: its result in the source run is copied into the revision run, never re-run.</summary>
    Reuse,

    /// <summary>The film render this revision replaces: runs as the <c>Revise</c> step.</summary>
    Revise,

    /// <summary>Depends on the film: runs again (through the step cache, which serves it free when its inputs did not change).</summary>
    Run,

    /// <summary>
    /// An editorial decision under <see cref="CompositionCutPolicy.KeepCut"/>: reused when everything it was
    /// decided from is unchanged (the re-rendered film's analysis offers the same ids at the same places),
    /// otherwise run again. Decided by the engine when the step is reached.
    /// </summary>
    KeepIfCompatible,

    /// <summary>Not run and not copied: it did not run in the source run either, or it only reviews the film itself.</summary>
    Skip
}

/// <summary>One step's place in the replay.</summary>
/// <param name="StepOrder">The step's order (identical in the original workflow and the revision's copy of it).</param>
/// <param name="Disposition">What happens to it.</param>
/// <param name="SourceStepResultId">
/// The step's latest finished result in the source run — what <see cref="ReplayStepDisposition.Reuse"/>
/// copies, and what a <see cref="ReplayStepDisposition.KeepIfCompatible"/> decision is compared against.
/// Null when the step has no such result.
/// </param>
/// <param name="DependsOn">The step orders it reads directly (<see cref="WorkflowStepDependencyGraph.DependsOn"/>).</param>
/// <param name="Reason">Why, in a few words (audit trail; not shown verbatim to customers).</param>
public sealed record ReplayStepPlan(
    int StepOrder,
    ReplayStepDisposition Disposition,
    Guid? SourceStepResultId,
    IReadOnlyList<int> DependsOn,
    string Reason);

/// <summary>
/// The persisted plan of one revision's replay (<c>composition_revisions.replay_plan_json</c>): which steps
/// of the run that produced the base version are reused, which one is revised, and which run again. Built
/// by the Inference API when the request is accepted (so the credit preflight and the panel price exactly
/// what will run) and executed by the engine.
/// </summary>
public sealed record CompositionReplayPlan(
    int Version,
    Guid SourceExecutionId,
    Guid? OriginWorkflowDefinitionId,
    int RevisedStepOrder,
    CompositionCutPolicy CutPolicy,
    IReadOnlyList<ReplayStepPlan> Steps,
    IReadOnlyList<int> FinalVideoStepOrders)
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The plan of <paramref name="stepOrder"/>, or null.</summary>
    public ReplayStepPlan? For(int stepOrder) => Steps.FirstOrDefault(s => s.StepOrder == stepOrder);

    /// <summary>
    /// True when anything besides the revision itself runs again. A plan without downstream work is not
    /// persisted: the revision runs as the plain one-step revision it always was.
    /// </summary>
    [JsonIgnore]
    public bool HasDownstreamWork => Steps.Any(s => s.Disposition is ReplayStepDisposition.Run or ReplayStepDisposition.KeepIfCompatible);

    /// <summary>True when the owner's cut choice matters: an editorial decision depends on the film.</summary>
    public bool HasEditDecisions { get; init; }

    /// <summary>
    /// Every target step order when the plan belongs to a change request with more than one target; null
    /// otherwise (so single-target plans serialize byte-identically). <see cref="RevisedStepOrder"/> is the smallest.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? RevisedStepOrders { get; init; }

    /// <summary>Every target step order (one for a single-target plan).</summary>
    [JsonIgnore]
    public IReadOnlyList<int> TargetOrders => RevisedStepOrders is { Count: > 0 } many ? many : [RevisedStepOrder];

    /// <summary>True when <paramref name="stepOrder"/> is one of the plan's target steps.</summary>
    public bool IsTarget(int stepOrder) => TargetOrders.Contains(stepOrder);

    /// <summary>
    /// The <see cref="StepType"/> name of the target step when the plan belongs to a step change request
    /// (docs/design/change-requests.md); null for a composition revision. Additive: old JSON parses.
    /// </summary>
    public string? TargetStepType { get; init; }

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parses a stored plan; null for null/blank/garbage or a version this build does not know.</summary>
    public static CompositionReplayPlan? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            CompositionReplayPlan? plan = JsonSerializer.Deserialize<CompositionReplayPlan>(json, Json);
            return plan is { Version: CurrentVersion, Steps: not null, FinalVideoStepOrders: not null } ? plan : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Plans a revision's replay from the workflow's steps, the source run's latest results and the step the
/// revision replaces. Pure: the Inference API calls it to price and persist the plan, and the tests call
/// it directly over every template shape.
/// </summary>
public static class CompositionReplayPlanner
{
    /// <param name="steps">The source run's workflow steps (agent definitions loaded).</param>
    /// <param name="sourceResults">Each step order's latest finished (Completed or Skipped) result in the source run.</param>
    /// <param name="revisedStepOrder">The step whose render the revision replaces.</param>
    public static CompositionReplayPlan Plan(
        IReadOnlyList<WorkflowStep> steps,
        IReadOnlyDictionary<int, Guid> sourceResults,
        int revisedStepOrder,
        CompositionCutPolicy cutPolicy,
        Guid sourceExecutionId,
        Guid? originWorkflowDefinitionId,
        string? targetLabel = null,
        string? targetStepType = null)
        => Plan(steps, sourceResults, [revisedStepOrder], cutPolicy, sourceExecutionId, originWorkflowDefinitionId, targetLabel, targetStepType);

    /// <summary>
    /// Plans one replay run that applies SEVERAL target steps at once (a change request with more than one
    /// target). Every target is revised; whatever is downstream of any target runs again; whatever is before
    /// the first target, or not downstream of any target, is reused. A single target behaves exactly like the
    /// single-target overload.
    /// </summary>
    /// <param name="revisedStepOrders">The target step orders; non-empty, each a step of the workflow.</param>
    /// <param name="renderStepOrder">The film render step when one of the targets is it (final-video fallback).</param>
    public static CompositionReplayPlan Plan(
        IReadOnlyList<WorkflowStep> steps,
        IReadOnlyDictionary<int, Guid> sourceResults,
        IReadOnlyCollection<int> revisedStepOrders,
        CompositionCutPolicy cutPolicy,
        Guid sourceExecutionId,
        Guid? originWorkflowDefinitionId,
        string? targetLabel = null,
        string? targetStepType = null,
        int? renderStepOrder = null)
    {
        ArgumentNullException.ThrowIfNull(revisedStepOrders);
        if (revisedStepOrders.Count == 0)
            throw new ArgumentException("At least one target step is required.", nameof(revisedStepOrders));

        WorkflowStepDependencyGraph graph = WorkflowStepDependencyGraph.Build(steps);
        List<int> targets = [.. revisedStepOrders.Distinct().Order()];
        foreach (int t in targets)
        {
            if (graph.Steps.All(s => s.StepOrder != t))
                throw new ArgumentException($"Step {t} is not a step of this workflow.", nameof(revisedStepOrders));
        }

        int first = targets[0];
        bool multi = targets.Count > 1;
        HashSet<int> downstream = [];
        foreach (int t in targets)
            downstream.UnionWith(graph.DownstreamOf(t));
        HashSet<int> replayed = [.. downstream, .. targets];

        // More than one target: the wording never names a single step.
        string? label = multi ? "a step this request changes" : targetLabel;

        List<ReplayStepPlan> plans = [];
        foreach (WorkflowStep step in graph.Steps)
        {
            Guid? source = sourceResults.TryGetValue(step.StepOrder, out Guid id) ? id : null;
            (ReplayStepDisposition disposition, string reason) =
                Decide(graph, step, source, targets, first, multi, downstream, replayed, cutPolicy, label);
            plans.Add(new ReplayStepPlan(step.StepOrder, disposition, source, [.. graph.DependsOn(step.StepOrder).Keys.Order()], reason));
        }

        bool hasEditDecisions = graph.Steps.Any(s => downstream.Contains(s.StepOrder) && WorkflowStepDependencyGraph.IsEditDecisionStep(s));

        return new CompositionReplayPlan(
            CompositionReplayPlan.CurrentVersion,
            sourceExecutionId,
            originWorkflowDefinitionId,
            first,
            cutPolicy,
            plans,
            graph.FinalVideoStepOrders(renderStepOrder ?? (multi ? null : first)))
        {
            HasEditDecisions = hasEditDecisions,
            TargetStepType = targetStepType,
            RevisedStepOrders = multi ? targets : null
        };
    }

    private static (ReplayStepDisposition, string) Decide(
        WorkflowStepDependencyGraph graph,
        WorkflowStep step,
        Guid? source,
        IReadOnlyList<int> targets,
        int firstTarget,
        bool multi,
        IReadOnlySet<int> downstream,
        IReadOnlySet<int> replayed,
        CompositionCutPolicy cutPolicy,
        string? targetLabel)
    {
        // Null keeps the composition-revision wording byte-identical; a step change request names its target.
        bool generic = targetLabel is not null;
        string target = targetLabel ?? "the film";
        int order = step.StepOrder;
        if (targets.Contains(order))
            return (ReplayStepDisposition.Revise, multi ? "a step this request changes" : generic ? $"{target}, which this request changes" : "the film render this revision replaces");

        if (order < firstTarget)
        {
            return source is null
                ? (ReplayStepDisposition.Skip, "did not run in the original")
                : (ReplayStepDisposition.Reuse, multi ? "comes before the first step this request changes" : generic ? $"comes before {target}" : "comes before the film");
        }

        // A branch is re-evaluated: replaying its original direction blindly could skip a step the new
        // film needs. Its inputs are unchanged unless it depends on the film.
        if (step.StepType == StepType.Conditional)
            return (ReplayStepDisposition.Run, "a branch is re-evaluated");

        if (!downstream.Contains(order))
        {
            return source is null
                ? (ReplayStepDisposition.Run, generic ? $"does not depend on {target}, but has no earlier result to keep" : "does not depend on the film, but has no earlier result to keep")
                : (ReplayStepDisposition.Reuse, generic ? $"does not depend on {target}" : "does not depend on the film");
        }

        if (step.StepType == StepType.ReviewLoop)
        {
            // A review whose only replayed input is the film itself reviews the composition the owner
            // just asked to change; it can never loop back into a revision, so re-running it would only
            // spend credits. A review of anything re-made downstream runs.
            bool reviewsOnlyTheFilm = graph.DependsOn(order).Keys.Where(replayed.Contains).All(targets.Contains);
            return reviewsOnlyTheFilm
                ? (ReplayStepDisposition.Skip, multi ? "only reviews steps this request changes" : generic ? $"reviews {target} itself, which this request just changed" : "reviews the film itself, which the revision just changed on request")
                : (ReplayStepDisposition.Run, generic ? "reviews what the request re-made" : "reviews what the revision re-made");
        }

        if (cutPolicy == CompositionCutPolicy.KeepCut && source is not null && WorkflowStepDependencyGraph.IsEditDecisionStep(step))
            return (ReplayStepDisposition.KeepIfCompatible, generic ? $"an editorial decision, kept unless {target} changed what it was decided from" : "an editorial decision, kept unless the film's scenes changed");

        return (ReplayStepDisposition.Run, generic ? $"depends on {target}" : "depends on the film");
    }
}
