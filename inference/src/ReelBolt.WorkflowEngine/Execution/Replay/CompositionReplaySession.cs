using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Execution.Replay;

/// <summary>The replay could not be carried out as planned; carries a <see cref="Services.Billing.FailureClassifier"/> code.</summary>
public sealed class CompositionReplayException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>What the executor does with one step of a replay.</summary>
public enum ReplayActionKind
{
    /// <summary>Run the step through its executor (the ordinary path: cache, credits, retries).</summary>
    Execute,

    /// <summary>Copy the source run's result into this run and move on; nothing runs and nothing is charged.</summary>
    Reuse,

    /// <summary>Record the step as skipped and move on.</summary>
    Skip
}

/// <param name="Kind">What to do.</param>
/// <param name="Source">The source-run result to copy (for <see cref="ReplayActionKind.Reuse"/>).</param>
/// <param name="Kept">True when the reused step is an editorial decision kept under "Keep the same cut".</param>
/// <param name="Reason">Why, for the log, the step's progress line and the copied row's note.</param>
public sealed record ReplayAction(ReplayActionKind Kind, WorkflowStepResult? Source, bool Kept, string Reason)
{
    public static readonly ReplayAction Execute = new(ReplayActionKind.Execute, null, false, "runs");
}

/// <summary>
/// The engine side of a composition revision's replay (docs/design/composition-revisions.md "Replaying
/// what depends on the film"). Loaded once per execution of a <see cref="WorkflowDefinitionKind.CompositionReplay"/>
/// workflow, it answers, step by step, whether the executor runs the step, copies its result from the
/// source run, or skips it — and evaluates "Keep the same cut" at the moment a kept decision is reached,
/// because only then has the re-rendered film been analysed.
///
/// <para>
/// It never runs anything itself and never touches credits: a step it lets through goes down the
/// ordinary executor path (plan re-check, step cache, credit hold/settle/refund, retries, progress), and a
/// step it reuses or skips costs nothing and writes one row.
/// </para>
/// </summary>
public sealed class CompositionReplaySession
{
    public const string PlanMissing = "REPLAY_PLAN_MISSING";
    public const string SourceMissing = "REPLAY_SOURCE_MISSING";

    private enum StepState
    {
        Reused,
        Kept,
        Skipped,
        Revised,
        Ran
    }

    private readonly Dictionary<Guid, WorkflowStepResult> _sourceRows;
    private readonly Dictionary<int, StepState> _state = [];
    private readonly Dictionary<int, string> _ranOutputs = [];
    private readonly Dictionary<int, WorkflowStep> _steps;
    private readonly HashSet<int> _invalidated = [];

    public CompositionReplaySession(CompositionReplayPlan plan, IReadOnlyList<WorkflowStep> steps, IEnumerable<WorkflowStepResult> sourceRows)
    {
        Plan = plan;
        _steps = steps.GroupBy(s => s.StepOrder).ToDictionary(g => g.Key, g => g.First());
        _sourceRows = sourceRows.ToDictionary(r => r.Id);
    }

    public CompositionReplayPlan Plan { get; }

    /// <summary>
    /// The replay of <paramref name="execution"/>, or null when the execution is not one (any workflow
    /// that is not a <see cref="WorkflowDefinitionKind.CompositionReplay"/> snapshot). Throws
    /// <see cref="CompositionReplayException"/> for a replay workflow whose plan is missing — running its
    /// steps blind would re-run (and charge) the whole original pipeline.
    /// </summary>
    public static async Task<CompositionReplaySession?> LoadAsync(
        WorkflowEngineDbContext db, WorkflowExecution execution, IReadOnlyList<WorkflowStep> steps, CancellationToken ct)
    {
        if (execution.WorkflowDefinition?.Kind != WorkflowDefinitionKind.CompositionReplay)
            return null;

        // The plan lives on the composition revision that created this run or, for a step change request
        // (docs/design/change-requests.md), on the request row. A row of another project never counts.
        CompositionRevision? revision = await db.CompositionRevisions.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ExecutionId == execution.Id, ct);
        CompositionReplayPlan? plan = revision is not null && revision.ProjectId == execution.ProjectId
            ? CompositionReplayPlan.TryParse(revision.ReplayPlanJson)
            : null;
        if (revision is null)
        {
            // One row per target; every row of the run carries the same plan, so the lowest target's row is read.
            StepChangeRequest? request = await db.StepChangeRequests.AsNoTracking()
                .Where(r => r.ExecutionId == execution.Id)
                .OrderBy(r => r.TargetStepOrder)
                .FirstOrDefaultAsync(ct);
            if (request is not null && request.ProjectId == execution.ProjectId)
                plan = CompositionReplayPlan.TryParse(request.ReplayPlanJson);
        }

        if (plan is null)
        {
            throw new CompositionReplayException(PlanMissing,
                "This change request has no plan for the rest of the workflow. Ask for the change again from the finished video's page.");
        }

        List<Guid> sourceIds = [.. plan.Steps.Where(s => s.SourceStepResultId is not null).Select(s => s.SourceStepResultId!.Value).Distinct()];
        List<WorkflowStepResult> sourceRows = await db.WorkflowStepResults.AsNoTracking()
            .Where(r => sourceIds.Contains(r.Id) && r.WorkflowExecution.ProjectId == execution.ProjectId)
            .ToListAsync(ct);
        return new CompositionReplaySession(plan, steps, sourceRows);
    }

    /// <summary>What to do with <paramref name="step"/> now. Pure: reads the plan and what already happened in this run.</summary>
    /// <summary>
    /// Marks a step whose planned copy was refused at run time (e.g. a Voiceover whose voice consent lapsed since the
    /// source run), so it is re-made. Every later Reuse/KeepIfCompatible step that depends on it — directly or
    /// through another invalidated step — is then re-made too (<see cref="Decide"/>), because its recorded output
    /// was built from the old result.
    /// </summary>
    public void Invalidate(int stepOrder) => _invalidated.Add(stepOrder);

    public ReplayAction Decide(WorkflowStep step)
    {
        ReplayStepPlan? planned = Plan.For(step.StepOrder);
        if (planned is null)
            return ReplayAction.Execute;

        if (_invalidated.Count > 0 && planned.Disposition is ReplayStepDisposition.Reuse or ReplayStepDisposition.KeepIfCompatible)
        {
            int upstream = planned.DependsOn.FirstOrDefault(_invalidated.Contains, -1);
            if (upstream >= 0)
            {
                _invalidated.Add(step.StepOrder);
                return new ReplayAction(ReplayActionKind.Execute, null, false,
                    $"step {upstream} it depends on is being re-made, so the copy from the earlier run would be stale");
            }
        }

        switch (planned.Disposition)
        {
            case ReplayStepDisposition.Reuse:
                return new ReplayAction(ReplayActionKind.Reuse, SourceRow(planned)
                    ?? throw new CompositionReplayException(SourceMissing,
                        $"Step {step.StepOrder}'s result in the run being changed is no longer available, so it cannot be reused. Ask for the change again."),
                    false, planned.Reason);

            case ReplayStepDisposition.Skip:
                return new ReplayAction(ReplayActionKind.Skip, null, false, planned.Reason);

            case ReplayStepDisposition.KeepIfCompatible:
                {
                    CutCompatibilityResult compatibility = EvaluateKeep(planned);
                    return compatibility.Compatible
                        ? new ReplayAction(ReplayActionKind.Reuse, SourceRow(planned), true, compatibility.Reason)
                        : new ReplayAction(ReplayActionKind.Execute, null, false, compatibility.Reason);
                }

            default:
                return ReplayAction.Execute;
        }
    }

    /// <summary>Records that <paramref name="stepOrder"/> was copied (or kept) in this run.</summary>
    public void RecordReused(int stepOrder, bool kept) => _state[stepOrder] = kept ? StepState.Kept : StepState.Reused;

    /// <summary>Records that <paramref name="stepOrder"/> was skipped in this run.</summary>
    public void RecordSkipped(int stepOrder) => _state[stepOrder] = StepState.Skipped;

    /// <summary>Records that <paramref name="stepOrder"/> ran in this run and produced <paramref name="output"/>.</summary>
    public void RecordExecuted(int stepOrder, string? output)
    {
        _state[stepOrder] = Plan.For(stepOrder)?.Disposition == ReplayStepDisposition.Revise ? StepState.Revised : StepState.Ran;
        _ranOutputs[stepOrder] = output ?? string.Empty;
    }

    /// <summary>
    /// A review loop in a replay loops back only into steps that can respond to its critique AND that the
    /// revision is actually re-making. Under "Keep the same cut" the decision it would loop into was kept on
    /// purpose, so looping would re-run deterministic steps to the same result (or override the owner's
    /// choice); and it never loops into or before the revision itself — that would redo the owner's change
    /// with a critic's notes instead of their words. The review still runs once, so its score is on record.
    /// Returns the index to continue at and, when it differs from <paramref name="proposedNextIndex"/>, why.
    /// </summary>
    public (int NextIndex, string? Note) AdjustLoopBack(IReadOnlyList<WorkflowStep> steps, int currentIndex, int proposedNextIndex)
    {
        if (proposedNextIndex > currentIndex || proposedNextIndex < 0 || proposedNextIndex >= steps.Count)
            return (proposedNextIndex, null);

        // The last target step inside the loop range: a loop-back that lands on or before it would redo a change
        // the owner asked for with a critic's notes instead of their words.
        int lastTargetOrder = -1;
        for (int i = proposedNextIndex; i < currentIndex; i++)
        {
            if (Plan.IsTarget(steps[i].StepOrder))
                lastTargetOrder = steps[i].StepOrder;
        }

        List<int> responsive = [];
        for (int i = proposedNextIndex; i < currentIndex; i++)
        {
            WorkflowStep candidate = steps[i];
            if (candidate.StepOrder > Plan.RevisedStepOrder
                && !Plan.IsTarget(candidate.StepOrder)
                && candidate.StepOrder > lastTargetOrder
                && _state.GetValueOrDefault(candidate.StepOrder) == StepState.Ran
                && WorkflowStepDependencyGraph.IsModelStep(candidate))
            {
                responsive.Add(i);
            }
        }

        if (responsive.Count == 0)
        {
            return (currentIndex + 1, Plan.CutPolicy == CompositionCutPolicy.KeepCut
                ? "The review is recorded; the cut you chose to keep is not re-edited."
                : "The review is recorded; nothing it could loop back to was re-made by this change.");
        }

        if (steps[proposedNextIndex].StepOrder <= Plan.RevisedStepOrder || lastTargetOrder >= 0)
            return (responsive[0], "The review loops back to the re-made edit, never into the change you asked for.");

        return (proposedNextIndex, null);
    }

    /// <summary>
    /// "Keep the same cut": a kept decision still means what the owner approved when everything it was
    /// decided from is unchanged — reused or kept steps, a re-analysed film that offers the same ids at the
    /// same places, and any other re-run input that produced the same output. The film render itself is an
    /// input only through its analysis, so a decision that reads the render with no re-checked analysis
    /// beside it is re-made.
    /// </summary>
    private CutCompatibilityResult EvaluateKeep(ReplayStepPlan planned)
    {
        if (SourceRow(planned) is null)
            return CutCompatibilityResult.No("the original decision is no longer available");

        bool readsTheRender = false;
        bool analysisChecked = false;
        foreach (int dependency in planned.DependsOn)
        {
            if (!_state.TryGetValue(dependency, out StepState state))
                return CutCompatibilityResult.No($"step {dependency} it was decided from did not run in this change");

            switch (state)
            {
                case StepState.Reused:
                case StepState.Kept:
                case StepState.Skipped:
                    continue;

                case StepState.Revised:
                    readsTheRender = true;
                    continue;

                case StepState.Ran:
                    {
                        WorkflowStepResult? original = Plan.For(dependency) is { } depPlan ? SourceRow(depPlan) : null;
                        if (original is null)
                            return CutCompatibilityResult.No($"step {dependency} has no original result to compare with");

                        string ran = _ranOutputs.GetValueOrDefault(dependency) ?? string.Empty;
                        if (_steps.TryGetValue(dependency, out WorkflowStep? depStep) && depStep.StepType == StepType.VideoAnalyze)
                        {
                            CutCompatibilityResult views = CutCompatibility.CompareAnalysisViews(OriginalOutput(original), ran);
                            if (!views.Compatible)
                                return views;
                            analysisChecked = true;
                            continue;
                        }

                        if (!CutCompatibility.SameJson(OriginalOutput(original), ran))
                            return CutCompatibilityResult.No($"step {dependency} it was decided from produced something different this time");
                        continue;
                    }
            }
        }

        if (readsTheRender && !analysisChecked)
            return CutCompatibilityResult.No("it reads the film directly, so the changed film needs a new decision");

        return CutCompatibilityResult.Yes(analysisChecked
            ? "the changed film has the same scenes at the same places, so the approved cut still fits"
            : "nothing it was decided from changed");
    }

    private WorkflowStepResult? SourceRow(ReplayStepPlan planned) =>
        planned.SourceStepResultId is { } id ? _sourceRows.GetValueOrDefault(id) : null;

    /// <summary>The raw output the source run's history carried (the jsonb column is a re-serialized copy).</summary>
    public static string OriginalOutput(WorkflowStepResult row) =>
        !string.IsNullOrEmpty(row.Output) ? row.Output : row.OutputJson ?? string.Empty;
}
