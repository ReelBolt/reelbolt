using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>
/// One step of the preflight verdict.
/// </summary>
/// <param name="Allowed">
/// True when the run may proceed. False is the one definite "the balance cannot cover this run" —
/// the only state that refuses.
/// </param>
/// <param name="WorkflowNotFound">
/// True when the workflow id does not resolve in the project. A REST caller renders this as its own
/// 404; a tool caller answers a "not found" error. It is a refusal only in the sense that nothing
/// runs.
/// </param>
/// <param name="Required">
/// The whole-credit figure the run needs (the sum of the per-step holds — the same figure the
/// engine's per-step gate reserves). Meaningful only when <see cref="Allowed"/> is false.
/// </param>
/// <param name="Balance">
/// The workspace's available credits at the moment the estimate was priced. Meaningful only when
/// <see cref="Allowed"/> is false.
/// </param>
/// <param name="Low">The bottom of the estimate's displayed usual range (variable part), in whole credits.</param>
/// <param name="High">The top of the estimate's displayed usual range (variable part), in whole credits.</param>
/// <param name="HasHistory">
/// Whether the organization has any recorded step usage behind the estimate (false = cold start,
/// every step priced from the per-step-type defaults).
/// </param>
/// <param name="Typical">The estimate's typical cost of the variable part, in whole credits.</param>
/// <param name="FixedMax">The fixed-price ceiling on top of the range ("plus up to"), in whole credits.</param>
public sealed record WorkflowExecutionPreflightResult(
    bool Allowed,
    bool WorkflowNotFound,
    double Required,
    double Balance,
    double Low,
    double High,
    bool HasHistory,
    double Typical = 0,
    double FixedMax = 0,
    long RequiredMillis = 0)
{
    /// <summary>The shared "nothing blocks this run" verdict (billing off, no organization, affordable, or not evaluable).</summary>
    public static WorkflowExecutionPreflightResult AllowedResult { get; } = new(true, false, 0, 0, 0, 0, false);

    /// <summary>The workflow id does not resolve in the project.</summary>
    public static WorkflowExecutionPreflightResult NotFoundResult { get; } = new(false, true, 0, 0, 0, 0, false);
}

/// <summary>
/// B7's execute preflight, shared by every execute path. The single source for "may this run start,
/// and if not, by how many credits" — the REST controllers (which render the 402) and the
/// assistant's <c>ExecuteWorkflow</c> tool (which renders the equivalent structured error) must
/// never price the run two different ways.
/// </summary>
/// <remarks>
/// <para>
/// This is the body of what <c>WorkflowsController.RefuseIfUnaffordable</c> did and what
/// <c>WorkflowAssistantController.Execute</c> inlined: load the workflow's steps in deterministic
/// order, run the estimator's preflight, decide. Nothing here costs the caller anything, and —
/// because it is <see cref="IWorkflowCostEstimator.PreflightAsync"/> that is advisory — every
/// state that is not a definite refusal answers <see cref="WorkflowExecutionPreflightResult.Allowed"/>:
/// billing off (self-host), no organization, an estimator that could not be evaluated. A second
/// refusal path that tripped on a transient error would block work the engine's per-step gate
/// would have allowed.
/// </para>
/// <para>
/// Millicredits are converted here, once, to the whole-credit unit the billing endpoints report —
/// the same conversion the REST 402 uses (<c>WorkflowsController.ToCredits</c>).
/// </para>
/// </remarks>
public interface IWorkflowExecutionPreflight
{
    /// <summary>
    /// Prices the run and compares it with the organization's balance. Never throws (the estimator
    /// swallows its own failures into <see cref="WorkflowPreflightOutcome.Allowed"/>), and never
    /// queues anything.
    /// </summary>
    Task<WorkflowExecutionPreflightResult> CheckAsync(
        Guid? organizationId,
        Guid projectId,
        Guid workflowId,
        CancellationToken ct = default);

    /// <summary>
    /// <see cref="CheckAsync(Guid?, Guid, Guid, CancellationToken)"/> for a run that chose
    /// <paramref name="outputResolution"/> (short side; null = Auto, identical to the overload without
    /// it): the render steps are priced at that resolution (see <see cref="IWorkflowCostEstimator"/>).
    /// </summary>
    Task<WorkflowExecutionPreflightResult> CheckAsync(
        Guid? organizationId,
        Guid projectId,
        Guid workflowId,
        int? outputResolution,
        CancellationToken ct = default);

    /// <summary>
    /// Prices the run exactly as <see cref="CheckAsync(Guid?, Guid, Guid, int?, CancellationToken)"/> does —
    /// the same workflow load (steps in <c>StepOrder</c>), the same
    /// <see cref="IWorkflowCostEstimator.PreflightAsync(Guid?, IReadOnlyList{WorkflowStep}, int?, CancellationToken)"/>
    /// call — and returns the estimator's whole answer, for a surface that SHOWS the cost (an approval
    /// card) rather than deciding on it. Null when no estimator is wired, billing is off, or the workflow
    /// is not in the project.
    /// </summary>
    Task<WorkflowPreflightResult?> PriceAsync(
        Guid? organizationId,
        Guid projectId,
        Guid workflowId,
        int? outputResolution,
        CancellationToken ct = default);

    /// <summary>
    /// <see cref="PriceAsync(Guid?, Guid, Guid, int?, CancellationToken)"/> over an explicit step list: an app run's
    /// steps as they will run (bound with the run's app inputs, docs/design/app-inputs.md 8.5) with only the steps its
    /// input-decided branches reach charged (<paramref name="chargedStepOrders"/>; null = every step). The caller enters
    /// the run's chosen agent models (<c>AppAgentModelAmbient</c>) around the call. Null when no estimator is wired or
    /// billing is off.
    /// </summary>
    Task<WorkflowPreflightResult?> PriceStepsAsync(
        Guid? organizationId,
        IReadOnlyList<WorkflowStep> steps,
        IReadOnlyCollection<int>? chargedStepOrders,
        int? outputResolution,
        CancellationToken ct = default);

    /// <summary>The verdict of <see cref="PriceStepsAsync"/>, converted exactly as the workflow overloads convert theirs.</summary>
    Task<WorkflowExecutionPreflightResult> CheckStepsAsync(
        Guid? organizationId,
        IReadOnlyList<WorkflowStep> steps,
        IReadOnlyCollection<int>? chargedStepOrders,
        int? outputResolution,
        CancellationToken ct = default);
}

public sealed class WorkflowExecutionPreflight(
    InferenceApiDbContext db,
    IWorkflowCostEstimator? costEstimator) : IWorkflowExecutionPreflight
{
    private readonly InferenceApiDbContext _db = db;
    private readonly IWorkflowCostEstimator? _costEstimator = costEstimator;

    public Task<WorkflowExecutionPreflightResult> CheckAsync(
        Guid? organizationId,
        Guid projectId,
        Guid workflowId,
        CancellationToken ct = default)
        => CheckAsyncInternal(organizationId, projectId, workflowId, null, ct);

    public Task<WorkflowExecutionPreflightResult> CheckAsync(
        Guid? organizationId,
        Guid projectId,
        Guid workflowId,
        int? outputResolution,
        CancellationToken ct = default)
        => CheckAsyncInternal(organizationId, projectId, workflowId, outputResolution, ct);

    public async Task<WorkflowPreflightResult?> PriceAsync(
        Guid? organizationId,
        Guid projectId,
        Guid workflowId,
        int? outputResolution,
        CancellationToken ct = default)
    {
        if (_costEstimator is null || !_costEstimator.BillingEnabled)
        {
            return null;
        }

        WorkflowDefinition? workflow = await _db.WorkflowDefinitions.AsNoTracking()
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.ProjectId == projectId && w.Kind != WorkflowDefinitionKind.ExecutionSnapshot, ct);
        if (workflow is null)
        {
            return null;
        }

        return await _costEstimator.PreflightAsync(
            organizationId, [.. workflow.Steps.OrderBy(s => s.StepOrder)], outputResolution, ct);
    }

    public async Task<WorkflowPreflightResult?> PriceStepsAsync(
        Guid? organizationId,
        IReadOnlyList<WorkflowStep> steps,
        IReadOnlyCollection<int>? chargedStepOrders,
        int? outputResolution,
        CancellationToken ct = default)
    {
        if (_costEstimator is null || !_costEstimator.BillingEnabled)
        {
            return null;
        }

        WorkflowStep[] ordered = [.. steps.OrderBy(s => s.StepOrder)];
        return chargedStepOrders is null
            ? await _costEstimator.PreflightAsync(organizationId, ordered, outputResolution, ct)
            : await _costEstimator.PreflightChargedAsync(organizationId, ordered, chargedStepOrders, outputResolution, ct);
    }

    public async Task<WorkflowExecutionPreflightResult> CheckStepsAsync(
        Guid? organizationId,
        IReadOnlyList<WorkflowStep> steps,
        IReadOnlyCollection<int>? chargedStepOrders,
        int? outputResolution,
        CancellationToken ct = default)
    {
        WorkflowPreflightResult? preflight = await PriceStepsAsync(organizationId, steps, chargedStepOrders, outputResolution, ct);
        return preflight is null ? WorkflowExecutionPreflightResult.AllowedResult : Convert(preflight);
    }

    /// <summary>A refused estimate as the whole-credit verdict the request paths report; an allowed one as allowed.</summary>
    private static WorkflowExecutionPreflightResult Convert(WorkflowPreflightResult preflight)
    {
        if (!preflight.Refused)
        {
            return WorkflowExecutionPreflightResult.AllowedResult;
        }

        WorkflowCostEstimate cost = preflight.Estimate;
        return new WorkflowExecutionPreflightResult(
            false,
            false,
            WorkflowsController.ToCredits(cost.RequiredMillis),
            WorkflowsController.ToCredits(preflight.BalanceMillis),
            WorkflowsController.ToCredits(cost.LowMillis),
            WorkflowsController.ToCredits(cost.HighMillis),
            cost.HasHistory,
            WorkflowsController.ToCredits(cost.TypicalMillis),
            WorkflowsController.ToCredits(cost.FixedMaxMillis));
    }

    private async Task<WorkflowExecutionPreflightResult> CheckAsyncInternal(
        Guid? organizationId,
        Guid projectId,
        Guid workflowId,
        int? outputResolution,
        CancellationToken ct)
    {
        if (_costEstimator is null || !_costEstimator.BillingEnabled)
        {
            // Nothing is wired, or this is a self-host install: nothing is metered, estimated or
            // gated, so the preflight is a no-op, exactly as on the REST execute path.
            return WorkflowExecutionPreflightResult.AllowedResult;
        }

        // One call: the preflight carries the estimate it was based on, so the breakdown and the
        // balance are guaranteed to describe the same read. PriceAsync is the same call an approval
        // card prices with, so the card and this verdict can never price the run two ways.
        WorkflowPreflightResult? preflight = await PriceAsync(organizationId, projectId, workflowId, outputResolution, ct);
        if (preflight is null)
        {
            return WorkflowExecutionPreflightResult.NotFoundResult;
        }

        if (!preflight.Refused)
        {
            return WorkflowExecutionPreflightResult.AllowedResult with { RequiredMillis = preflight.Estimate.RequiredMillis };
        }

        WorkflowCostEstimate cost = preflight.Estimate;
        return new WorkflowExecutionPreflightResult(
            false,
            false,
            WorkflowsController.ToCredits(cost.RequiredMillis),
            WorkflowsController.ToCredits(preflight.BalanceMillis),
            WorkflowsController.ToCredits(cost.LowMillis),
            WorkflowsController.ToCredits(cost.HighMillis),
            cost.HasHistory,
            WorkflowsController.ToCredits(cost.TypicalMillis),
            WorkflowsController.ToCredits(cost.FixedMaxMillis));
    }
}
