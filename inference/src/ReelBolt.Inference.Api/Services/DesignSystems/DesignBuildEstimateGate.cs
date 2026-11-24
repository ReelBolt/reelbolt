using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.DesignSystems;

/// <summary>What the cap and accept-the-estimate gate decided for one run of the profile-build template.</summary>
public enum DesignBuildGateOutcome
{
    /// <summary>The run may proceed (billing off, or the estimate is within the cap and accepted).</summary>
    Ok,

    /// <summary>Billing is on but the estimator could not price the build: fail closed (503).</summary>
    Unavailable,

    /// <summary>The estimate's high figure is above the cap (409).</summary>
    OverCap,

    /// <summary>The caller did not accept at least the estimate's high figure (409).</summary>
    NotAccepted,
}

/// <summary>
/// The gate's verdict. <see cref="Applies"/> is false for every workflow that is not the
/// product-profile-from-code template; such a verdict is never a refusal. Low and High are credits; CapCredits is the computed cap rounded UP to 2 decimals.
/// </summary>
public sealed record DesignBuildGateResult(
    bool Applies, DesignBuildGateOutcome Outcome, double Low, double High, double CapCredits)
{
    /// <summary>"This workflow is not the profile-build template": the gate has nothing to say.</summary>
    public static DesignBuildGateResult NotApplicable { get; } = new(false, DesignBuildGateOutcome.Ok, 0, 0, 0);

    /// <summary>True when the run must not start.</summary>
    public bool Refused => Applies && Outcome != DesignBuildGateOutcome.Ok;

    /// <summary>The figure to tell a caller to pass as <c>acceptEstimateCredits</c>: the high estimate rounded UP to 2 decimals.</summary>
    public double AcceptFigure => Math.Ceiling(High * 100 - 1e-9) / 100;

    /// <summary>The HTTP status a REST route answers with (200 when not refused).</summary>
    public int StatusCode => Outcome switch
    {
        DesignBuildGateOutcome.Unavailable => 503,
        DesignBuildGateOutcome.OverCap or DesignBuildGateOutcome.NotAccepted => 409,
        _ => 200,
    };

    /// <summary>The stable error code (empty when not refused).</summary>
    public string Code => Outcome switch
    {
        DesignBuildGateOutcome.Unavailable => "ESTIMATE_UNAVAILABLE",
        DesignBuildGateOutcome.OverCap => "ESTIMATE_OVER_CAP",
        DesignBuildGateOutcome.NotAccepted => "ESTIMATE_NOT_ACCEPTED",
        _ => "",
    };

    /// <summary>The plain-language refusal, carrying the numbers.</summary>
    public string Message => Outcome switch
    {
        DesignBuildGateOutcome.Unavailable =>
            "The cost estimate is unavailable right now, so the build cannot be priced. Try again shortly.",
        DesignBuildGateOutcome.OverCap =>
            $"The AI fallback is estimated at up to {High:0.##} credits, above the {CapCredits:0.##}-credit cap.",
        DesignBuildGateOutcome.NotAccepted =>
            $"Accept an estimate of at least {AcceptFigure:0.##} credits to start this build.",
        _ => "",
    };

    /// <summary>The REST answer for a refusal: 409 as a <see cref="ConflictObjectResult"/>, 503 as an <see cref="ObjectResult"/>.</summary>
    public ObjectResult ToActionResult() => StatusCode == 409
        ? new ConflictObjectResult(ToBody())
        : new ObjectResult(ToBody()) { StatusCode = StatusCode };

    /// <summary>The JSON body of the refusal: <c>{code,message,estimate{low,high},capCredits}</c> (code and message only for 503).</summary>
    public object ToBody() => Outcome == DesignBuildGateOutcome.Unavailable
        ? new { code = Code, message = Message }
        : new { code = Code, message = Message, estimate = new { low = Low, high = High }, capCredits = CapCredits };
}

/// <summary>
/// the ONE cap and accept-the-estimate check for every route that starts the
/// product-profile-from-code template (<c>POST /design-system/builds</c>, generic Execute and Retry, the
/// workflow-assistant execute, the assistant's <c>ExecuteWorkflow</c> in chat and over MCP). A workflow
/// that is not that template never applies, whatever its steps look like.
/// </summary>
public interface IDesignBuildEstimateGate
{
    /// <summary>Whether <paramref name="workflow"/> is the profile-build template (by its step shape).</summary>
    bool IsBuildTemplate(WorkflowDefinition workflow);

    /// <summary>
    /// Gates a run of <paramref name="workflow"/> (its <c>Steps</c> loaded). <paramref name="acceptedCredits"/> is the
    /// figure the caller accepted (null = none); <see cref="DesignBuildEstimateGate.AcceptedByApproval"/> stands for
    /// a surface where approving the card IS the acceptance.
    /// </summary>
    Task<DesignBuildGateResult> CheckAsync(
        Guid? organizationId, WorkflowDefinition workflow, double? acceptedCredits, CancellationToken ct);

    /// <summary>
    /// As above for a workflow named by id in a project: loads the workflow WITH its steps (the shape decides, so this is one read per call), so the shape can be judged (every route pays that one read); an ordinary workflow is never priced. An unknown workflow does not apply.
    /// </summary>
    Task<DesignBuildGateResult> CheckAsync(
        Guid? organizationId, Guid projectId, Guid workflowId, double? acceptedCredits, CancellationToken ct);

    /// <summary>
    /// For a Retry or Resume of run <paramref name="executionId"/> (QA-PR1-20, SQFU-S16): the run executes its OWN
    /// snapshot, so the gate judges THAT definition, not the workflow as it is now (which may have been edited after
    /// the run, or deleted, the run then filed under its snapshot). The snapshot is judged by the build shape
    /// whatever its kind, but only when it is kind <see cref="WorkflowDefinitionKind.ExecutionSnapshot"/> AND the
    /// run's engine-taken snapshot (owned by the run); no other hidden kind is opened. A run without one (a legacy
    /// run) is judged on <paramref name="workflowId"/>, exactly as <see cref="CheckAsync(Guid?, Guid, Guid, double?, CancellationToken)"/>.
    /// </summary>
    Task<DesignBuildGateResult> CheckRunAsync(
        Guid? organizationId, Guid projectId, Guid workflowId, Guid executionId, Guid? snapshotDefinitionId,
        double? acceptedCredits, CancellationToken ct);
}

public sealed class DesignBuildEstimateGate : IDesignBuildEstimateGate
{
    /// <summary>The default of <c>DesignSystem:Build:FallbackCapMargin</c>: the cap is the reference high plus 30%.</summary>
    public const double DefaultCapMargin = 1.3;

    /// <summary>The largest margin honoured; a larger (or overflowing) configured value is clamped to it.</summary>
    public const double MaxCapMargin = 10;

    /// <summary>Pass as the accepted figure where approving the run (a chat card, auto mode) is the acceptance.</summary>
    public const double AcceptedByApproval = double.PositiveInfinity;

    private const double AcceptTolerance = 0.005;

    private static readonly JsonSerializerOptions ConfigOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly InferenceApiDbContext _db;
    private readonly IWorkflowCostEstimator? _costEstimator;
    private readonly IConfiguration? _configuration;

    public DesignBuildEstimateGate(
        InferenceApiDbContext db, IWorkflowCostEstimator? costEstimator, IConfiguration? configuration)
    {
        _db = db;
        _costEstimator = costEstimator;
        _configuration = configuration;
    }

    /// <summary>
    /// The profile build is recognised by its step SHAPE, which a user cannot rename away: a Standard-kind
    /// workflow with a <c>DesignSystem</c> step whose config op is <c>Compose</c> (the step that writes the
    /// ingest) AND at least one <c>Agent</c> (paid) step. The name does not matter; a missing or invalid
    /// config is "not a build". Needs <c>workflow.Steps</c> loaded.
    /// </summary>
    public bool IsBuildTemplate(WorkflowDefinition workflow) =>
        workflow.Kind == WorkflowDefinitionKind.Standard && HasBuildShape(workflow.Steps);

    /// <summary>The build's step shape alone (a Compose DesignSystem step and a paid Agent step), whatever the kind.</summary>
    private static bool HasBuildShape(IEnumerable<WorkflowStep> workflowSteps)
    {
        bool hasAgent = false;
        bool hasCompose = false;
        foreach (WorkflowStep step in workflowSteps)
        {
            if (step.StepType == StepType.Agent)
                hasAgent = true;
            else if (step.StepType == StepType.DesignSystem && IsCompose(step.DesignSystemConfigJson))
                hasCompose = true;
        }

        return hasAgent && hasCompose;
    }

    private static bool IsCompose(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            return JsonSerializer.Deserialize<DesignSystemStepConfig>(json, ConfigOptions)?.Op == DesignSystemOp.Compose;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task<DesignBuildGateResult> CheckAsync(
        Guid? organizationId, WorkflowDefinition workflow, double? acceptedCredits, CancellationToken ct)
    {
        if (!IsBuildTemplate(workflow))
            return DesignBuildGateResult.NotApplicable;

        return await CheckBuildAsync(organizationId, workflow.Steps, acceptedCredits, ct);
    }

    public async Task<DesignBuildGateResult> CheckRunAsync(
        Guid? organizationId, Guid projectId, Guid workflowId, Guid executionId, Guid? snapshotDefinitionId,
        double? acceptedCredits, CancellationToken ct)
    {
        if (snapshotDefinitionId is { } snapshotId)
        {
            WorkflowDefinition? snapshot = await _db.WorkflowDefinitions.AsNoTracking().Include(w => w.Steps)
                .FirstOrDefaultAsync(w => w.Id == snapshotId && w.ProjectId == projectId
                    && w.Kind == WorkflowDefinitionKind.ExecutionSnapshot, ct);
            if (snapshot is not null
                && (await ExecutionResumability.EngineTakenAsync(_db, [(executionId, snapshotId)], ct)).Contains(executionId))
            {
                return HasBuildShape(snapshot.Steps)
                    ? await CheckBuildAsync(organizationId, snapshot.Steps, acceptedCredits, ct)
                    : DesignBuildGateResult.NotApplicable;
            }
        }

        return await CheckAsync(organizationId, projectId, workflowId, acceptedCredits, ct);
    }

    /// <summary>The cap and accept-the-estimate verdict for steps already recognised as the build.</summary>
    private async Task<DesignBuildGateResult> CheckBuildAsync(
        Guid? organizationId, IEnumerable<WorkflowStep> buildSteps, double? acceptedCredits, CancellationToken ct)
    {
        WorkflowStep[] steps = [.. buildSteps.OrderBy(s => s.StepOrder)];
        bool billingOn = _costEstimator?.BillingEnabled ?? _configuration?.GetValue<bool?>("Billing:Enabled") ?? false;
        WorkflowCostEstimate estimate = _costEstimator is null || !_costEstimator.BillingEnabled
            ? WorkflowCostEstimate.Disabled
            : await _costEstimator.EstimateAsync(organizationId, steps, ct);

        double low = WorkflowsController.ToCredits(estimate.LowMillis);
        double high = WorkflowsController.ToCredits(estimate.HighMillis);

        // Fail closed on the build's OWN figure too: an estimate that used the compiled-in seed constants for any
        // quantity is not a real price, so it is never compared against the cap or accepted (same signal as the reference).
        if (billingOn && (!estimate.BillingEnabled || estimate.PricedFromSeedFallback))
            return new(true, DesignBuildGateOutcome.Unavailable, low, high, 0);

        double cap = 0;
        if (estimate.BillingEnabled)
        {
            // The cap follows live model prices: the cold-start HIGH of the reference template, priced by the same
            // estimator at the prices of the models the resolver picks, times the margin. A reference the estimator
            // could not price from real catalog prices (seed constants never count) fails every route closed.
            double? reference = await ReferenceHighCreditsAsync(ct);
            if (reference is null)
                return new(true, DesignBuildGateOutcome.Unavailable, low, high, 0);

            cap = CapFor(reference.Value, _configuration);
        }

        if (estimate.BillingEnabled)
        {
            if (estimate.HighMillis > cap * 1000.0)
                return new(true, DesignBuildGateOutcome.OverCap, low, high, cap);

            // NaN and negative figures are never an acceptance; infinity is (chat approval). A 0.005 tolerance
            // lets the caller echo back the rounded-up figure the refusal printed.
            double accepted = acceptedCredits ?? 0;
            if (double.IsNaN(accepted) || accepted < 0 || !(accepted + AcceptTolerance >= high))
                return new(true, DesignBuildGateOutcome.NotAccepted, low, high, cap);
        }

        return new(true, DesignBuildGateOutcome.Ok, low, high, cap);
    }

    /// <summary>
    /// The cap in credits for a reference high: <c>reference x margin</c> rounded UP to 2 decimals, lowered to
    /// <c>DesignSystem:Build:MaxFallbackCreditsHigh</c> when that optional ceiling is set (an unreadable ceiling
    /// fails closed: it caps at 0). The margin, clamped to <see cref="MaxCapMargin"/>, is <c>DesignSystem:Build:FallbackCapMargin</c>; unset, not a finite
    /// number or not above 0 means <see cref="DefaultCapMargin"/>.
    /// </summary>
    public static double CapFor(double referenceHighCredits, IConfiguration? configuration)
    {
        double margin = ParseNumber(configuration?["DesignSystem:Build:FallbackCapMargin"]) is { } m && m > 0
            ? Math.Min(m, MaxCapMargin)
            : DefaultCapMargin;
        double cap = Math.Ceiling(referenceHighCredits * margin * 100 - 1e-9) / 100;

        string? ceilingText = configuration?["DesignSystem:Build:MaxFallbackCreditsHigh"];
        if (!string.IsNullOrWhiteSpace(ceilingText))
            cap = Math.Min(cap, Math.Max(0, ParseNumber(ceilingText) ?? 0));

        return cap;
    }

    private static double? ParseNumber(string? text) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
        && double.IsFinite(v)
            ? v
            : null;

    /// <summary>
    /// The cold-start HIGH, in credits, of the reference profile-build template (every step, no organization
    /// history) priced by the estimator at the live prices of the models the resolver picks; null when it cannot be
    /// priced from real catalog prices (billing off or the estimator unavailable, a built-in agent missing, no
    /// model, an unpriced model, or anything priced from the seed constants).
    /// Which models the resolver picks is decided by the ambient organization (<c>IInferenceTenantAccessor</c>, the
    /// same caller as <c>organizationId</c> in <see cref="CheckAsync(Guid?, WorkflowDefinition, double?, CancellationToken)"/>),
    /// so the workspace's per-agent model choices apply here exactly as they do to the real run; the organization id is
    /// deliberately NOT passed to the estimator, because that would read the workspace's history and stop the figure
    /// being a cold start. Covered by DesignBuildOrgModelCapTests.
    /// </summary>
    private async Task<double?> ReferenceHighCreditsAsync(CancellationToken ct)
    {
        if (_costEstimator is null || WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey) is not { } template)
            return null;

        IEnumerable<AgentType> types = template.Steps.Select(s => s.AgentType)
            .Concat(template.Steps.Where(s => s.ParallelAgentTypes is { Count: > 0 }).SelectMany(s => s.ParallelAgentTypes!))
            .Distinct();
        List<AgentDefinition> builtIns = await _db.AgentDefinitions.AsNoTracking()
            .Where(a => a.IsBuiltIn && types.Contains(a.AgentType))
            .ToListAsync(ct);
        Dictionary<AgentType, AgentDefinition> agents = builtIns
            .GroupBy(a => a.AgentType)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.CreatedAt).First());
        if (types.Any(t => !agents.ContainsKey(t)))
            return null;

        // Transient steps, never persisted: the same mapping the provisioning service applies to the template.
        List<WorkflowStep> steps = [];
        for (int i = 0; i < template.Steps.Count; i++)
        {
            WorkflowTemplateStepDefinition d = template.Steps[i];
            AgentDefinition agent = agents[d.AgentType];
            steps.Add(new WorkflowStep
            {
                Id = Guid.NewGuid(),
                AgentDefinitionId = agent.Id,
                AgentDefinition = agent,
                StepOrder = i + 1,
                Label = d.Label,
                StepType = d.StepType,
                ConditionExpression = d.ConditionExpression,
                LoopSourceExpression = d.LoopSourceExpression,
                LoopTargetStepOrder = d.LoopTargetStepOrder,
                MaxIterations = d.MaxIterations,
                MinScore = d.MinScore,
                InputMappingJson = d.InputMappingJson,
                AgentInputContextMode = d.AgentInputContextMode,
                SelectedPriorStepOrdersJson = d.SelectedPriorStepOrders is { Count: > 0 }
                    ? JsonSerializer.Serialize(d.SelectedPriorStepOrders)
                    : null,
                TrueBranchStepOrder = d.TrueBranchStepOrder,
                FalseBranchStepOrder = d.FalseBranchStepOrder,
                ParallelAgentIdsJson = d.ParallelAgentTypes is { Count: > 0 }
                    ? JsonSerializer.Serialize(d.ParallelAgentTypes.Distinct().Select(t => agents[t].Id).ToList())
                    : null,
                ConditionalConfigJson = d.ConditionalConfigJson,
                ReviewLoopConfigJson = d.ReviewLoopConfigJson,
                DesignSystemConfigJson = d.DesignSystemConfigJson,
                CacheMode = d.CacheMode,
            });
        }

        WorkflowCostEstimate reference = await _costEstimator.EstimateAsync(null, steps, ct);
        if (!reference.BillingEnabled || reference.PricedFromSeedFallback)
            return null;

        return WorkflowsController.ToCredits(reference.HighMillis);
    }

    public async Task<DesignBuildGateResult> CheckAsync(
        Guid? organizationId, Guid projectId, Guid workflowId, double? acceptedCredits, CancellationToken ct)
    {
        // Steps are loaded because the shape decides; an unknown workflow does not apply.
        WorkflowDefinition? workflow = await _db.WorkflowDefinitions.AsNoTracking().Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.ProjectId == projectId, ct);
        return workflow is null
            ? DesignBuildGateResult.NotApplicable
            : await CheckAsync(organizationId, workflow, acceptedCredits, ct);
    }
}
