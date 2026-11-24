using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.Shared.Workflows.Review;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>One step of a run's estimate: what it is expected to cost, and what will be held for it.</summary>
/// <param name="LowMillis">Display: this step's p25 for ONE execution (0 for a fixed-price step).</param>
/// <param name="HighMillis">Display: this step's p75 for ONE execution (0 for a fixed-price step).</param>
/// <param name="HoldMillis">
/// What the engine reserves before the step runs (<see cref="RunCreditHolds"/>): the history p90, a
/// VideoGenerate step's declared price, or, for a cold step, the larger of its previous default
/// (B0's low) and its share of the low edge of the cold steps' usual range. Never moved by display history.
/// </param>
/// <param name="Basis">
/// The DISPLAY basis, <see cref="CreditEstimateBasis"/> by name: <c>History</c> (this org's runs of this
/// exact agent), <c>AgentTypeHistory</c> (this org's runs of this kind of agent), <c>Default</c> (the
/// cold-start usage profile, priced through the model capability prices), <c>OwnProvider</c> (the chat part runs on
/// the org's own provider key and is not charged), <c>NotCharged</c> or <c>VideoGeneration</c> (a
/// VideoGenerate step: fixed-price, shown as "up to").
/// </param>
/// <param name="TypicalMillis">Display: this step's median for ONE execution.</param>
/// <param name="ExpectedRuns">How many times the step is expected to run (review loops, repeating steps); 1 otherwise.</param>
/// <param name="FixedMaxMillis">
/// A VideoGenerate step's fixed price for one execution (its declared price, else the 60 s max-clips
/// ceiling); 0 for every other step.
/// </param>
public sealed record WorkflowStepCostEstimate(
    int StepOrder,
    string StepType,
    Guid? AgentDefinitionId,
    string? Label,
    long LowMillis,
    long HighMillis,
    long HoldMillis,
    string Basis,
    long TypicalMillis,
    double ExpectedRuns,
    long FixedMaxMillis);

/// <summary>Where a run's displayed range came from (<see cref="WorkflowCostEstimate.RangeBasis"/>).</summary>
public static class CreditRangeBasis
{
    /// <summary>The p25–p75 of this workflow's own past Passed runs.</summary>
    public const string PastRuns = "PastRuns";

    /// <summary>Combined per step, at least one step priced from this organization's own history.</summary>
    public const string StepHistory = "StepHistory";

    /// <summary>Combined per step from the cold-start profiles only.</summary>
    public const string FirstRun = "FirstRun";
}

/// <summary>Why a displayed range is as wide as it is (<see cref="WorkflowCostEstimate.SpreadReasons"/>).</summary>
public static class CreditSpreadReasons
{
    public const string ReviewLoopRepeats = "review_loop_repeats";
    public const string FootageLength = "footage_length";
    public const string ForEachItemsUnknown = "foreach_items_unknown";
    public const string OwnProviderFree = "own_provider_free";
    public const string VideoGenerationMaxClips = "video_generation_max_clips";
    public const string VideoGenerationInReviewLoop = "video_generation_in_review_loop";

    /// <summary>
    /// A review loop may re-make earlier steps (docs/design/review-loop-adjustments.md): their worst case is part of the
    /// "up to" ceiling (<see cref="WorkflowCostEstimate.FixedMaxMillis"/>).
    /// </summary>
    public const string ReviewStepReruns = "review_step_reruns";
}

/// <summary>
/// What a whole run is expected to cost, in integer millicredits (D8).
/// </summary>
/// <param name="BillingEnabled">False on a self-host install, where nothing is metered or gated.</param>
/// <param name="HasHistory">
/// Whether the organization has any recorded step usage at all (the hold's sample set). False is the
/// cold-start path, where every charged step's HOLD is the larger of its previous default and its share
/// of the low edge of the run's usual range (<see cref="RunCreditHolds"/>).
/// </param>
/// <param name="LowMillis">
/// The bottom of the displayed usual range of the VARIABLE part of the run (fixed-price steps excluded):
/// p25 of past runs, or the per-step combination's typical − 0.674σ. With Conditional branches the
/// combination is taken over the cheapest path: a step a branch can skip counts as 0.
/// </param>
/// <param name="HighMillis">The top of that range (p75, or typical + 0.674σ).</param>
/// <param name="RequiredMillis">
/// Sum of the per-step HOLD: the credits the run must be able to afford to start. This is the figure
/// the preflight compares the balance against, and it is exactly what the engine will reserve before
/// each step, so the two can never disagree about whether a run may begin. With history a step holds
/// its p90; on a cold start each cold step holds the larger of its previous default and its share of
/// the low edge of the cold steps' usual range, so the cold steps together hold at least that low edge
/// (the displayed LowMillis when the whole run is cold and has no display history) and at least what
/// they held before the cold-start hold re-plan. Display history (past runs, agent-type buckets, loop history) never moves it.
/// </param>
/// <param name="TypicalMillis">The displayed typical cost of the variable part ("≈ X credits").</param>
/// <param name="FixedMaxMillis">
/// The fixed-price ceiling ("plus up to F"): declared prices, × MaxIterations inside a loop, plus the bounded worst case of
/// the steps a review loop with adjustments on may re-make before its loop target (<see cref="ReviewAdjustmentCeiling"/>;
/// reason <see cref="CreditSpreadReasons.ReviewStepReruns"/>).
/// </param>
/// <param name="RangeBasis"><see cref="CreditRangeBasis"/>: PastRuns, StepHistory or FirstRun.</param>
/// <param name="PastRunCount">How many past Passed runs of this workflow (with a non-zero total) were found.</param>
/// <param name="SpreadReasons"><see cref="CreditSpreadReasons"/> codes, largest contribution first.</param>
public sealed record WorkflowCostEstimate(
    bool BillingEnabled,
    bool HasHistory,
    long LowMillis,
    long HighMillis,
    long RequiredMillis,
    IReadOnlyList<WorkflowStepCostEstimate> Steps,
    long TypicalMillis,
    long FixedMaxMillis,
    string RangeBasis,
    int PastRunCount,
    IReadOnlyList<string> SpreadReasons)
{
    /// <summary>An estimate with no display detail beyond low/high (a first-run basis and no reasons).</summary>
    public WorkflowCostEstimate(
        bool billingEnabled,
        bool hasHistory,
        long lowMillis,
        long highMillis,
        long requiredMillis,
        IReadOnlyList<WorkflowStepCostEstimate> steps)
        : this(billingEnabled, hasHistory, lowMillis, highMillis, requiredMillis, steps, 0, 0, CreditRangeBasis.FirstRun, 0, [])
    {
    }

    /// <summary>Nothing to show and nothing to enforce (billing off, or no workflow steps).</summary>
    public static WorkflowCostEstimate Disabled { get; } = new(false, false, 0, 0, 0, []);

    /// <summary>
    /// True when at least one cold-start quantity of this estimate was priced from the compiled-in seed constants
    /// (<see cref="StepUsageProfiles.SeedFallbackMillis"/>) because no provider, price service or price row priced it.
    /// The figure is still a usable display estimate, but a caller that must act on a real price (the profile-build
    /// cap) treats it as unpriced.
    /// </summary>
    public bool PricedFromSeedFallback { get; init; }
}

/// <summary>What the execute preflight decided.</summary>
public enum WorkflowPreflightOutcome
{
    /// <summary>Run it. Also the outcome when billing is off, when the caller has no organization, and when the estimate could not be computed.</summary>
    Allowed,

    /// <summary>The organization's available balance is below the run's required credits: answer 402.</summary>
    InsufficientCredits
}

/// <summary>The preflight's answer: the estimate it was based on, and the balance it compared.</summary>
/// <param name="Outcome">What the execute path does.</param>
/// <param name="Estimate">The estimate the verdict was based on.</param>
/// <param name="BalanceMillis">The balance it was compared with.</param>
/// <param name="Evaluated">
/// False when billing is on but the check could not be made: the estimate threw (then
/// <see cref="Estimate"/> is <see cref="WorkflowCostEstimate.Disabled"/>) or the balance could not be read
/// (then <see cref="Estimate"/> is the computed one and <see cref="BalanceMillis"/> is meaningless). The
/// outcome is still <see cref="WorkflowPreflightOutcome.Allowed"/> (the check fails open), but a surface that
/// SHOWS the cost must not read the missing figures as "free". True for billing off and for no organization.
/// </param>
public sealed record WorkflowPreflightResult(
    WorkflowPreflightOutcome Outcome,
    WorkflowCostEstimate Estimate,
    long BalanceMillis,
    bool Evaluated = true)
{
    /// <summary>True when the caller must refuse the execute with 402.</summary>
    public bool Refused => Outcome == WorkflowPreflightOutcome.InsufficientCredits;
}

/// <summary>B7's pre-run cost estimate and execute preflight.</summary>
public interface IWorkflowCostEstimator
{
    /// <summary>
    /// Whether <c>Billing:Enabled</c> is on. False (self-host) means nothing is metered, estimated or
    /// gated, and a caller should skip the preflight entirely rather than showing a zero estimate.
    /// </summary>
    bool BillingEnabled { get; }

    /// <summary>The run's credit range and per-step breakdown. Never throws.</summary>
    Task<WorkflowCostEstimate> EstimateAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default);

    /// <summary>The estimate plus the balance comparison the execute path acts on. Never throws.</summary>
    Task<WorkflowPreflightResult> PreflightAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default);

    /// <summary>
    /// <see cref="EstimateAsync(Guid?, IReadOnlyList{WorkflowStep}, CancellationToken)"/> for a run that
    /// chose <paramref name="outputResolution"/> (short side; null = Auto, identical to the overload
    /// without it). The default ignores the choice, for estimators that do not price pixels.
    /// </summary>
    Task<WorkflowCostEstimate> EstimateAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> steps, int? outputResolution, CancellationToken ct = default)
        => EstimateAsync(organizationId, steps, ct);

    /// <summary>
    /// <see cref="PreflightAsync(Guid?, IReadOnlyList{WorkflowStep}, CancellationToken)"/> for a run that
    /// chose <paramref name="outputResolution"/>. The default ignores the choice.
    /// </summary>
    Task<WorkflowPreflightResult> PreflightAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> steps, int? outputResolution, CancellationToken ct = default)
        => PreflightAsync(organizationId, steps, ct);

    /// <summary>
    /// The estimate of a run that executes only some of its steps (a composition replay reuses the rest).
    /// The hold is planned over every step of <paramref name="runSteps"/>, exactly as the engine plans the
    /// execution, and <c>RequiredMillis</c> (and each step's <c>HoldMillis</c>) is that plan's holds of the
    /// steps in <paramref name="chargedStepOrders"/> alone. The displayed range covers the charged steps
    /// alone. Never throws.
    /// </summary>
    Task<WorkflowCostEstimate> EstimateChargedAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default);

    /// <summary><see cref="EstimateChargedAsync"/> plus the balance comparison the request path acts on. Never throws.</summary>
    Task<WorkflowPreflightResult> PreflightChargedAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default);

    /// <summary>
    /// <see cref="EstimateChargedAsync(Guid?, IReadOnlyList{WorkflowStep}, IReadOnlyCollection{int}, CancellationToken)"/>
    /// for a run that chose <paramref name="outputResolution"/> (a replay renders at its source run's).
    /// The default ignores the choice.
    /// </summary>
    Task<WorkflowCostEstimate> EstimateChargedAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders,
        int? outputResolution, CancellationToken ct = default)
        => EstimateChargedAsync(organizationId, runSteps, chargedStepOrders, ct);

    /// <summary>
    /// <see cref="PreflightChargedAsync(Guid?, IReadOnlyList{WorkflowStep}, IReadOnlyCollection{int}, CancellationToken)"/>
    /// for a run that chose <paramref name="outputResolution"/>. The default ignores the choice.
    /// </summary>
    Task<WorkflowPreflightResult> PreflightChargedAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders,
        int? outputResolution, CancellationToken ct = default)
        => PreflightChargedAsync(organizationId, runSteps, chargedStepOrders, ct);

    /// <summary>
    /// <see cref="PreflightChargedAsync(Guid?, IReadOnlyList{WorkflowStep}, IReadOnlyCollection{int}, int?, CancellationToken)"/>
    /// for a run whose target step re-makes only a share of its items (a step change request): each entry of
    /// <paramref name="stepScales"/> (step order to a fraction in (0, 1]) multiplies that step's hold and display
    /// (<see cref="StepChangeScope"/>). The default ignores the scales.
    /// </summary>
    Task<WorkflowPreflightResult> PreflightChargedAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders,
        int? outputResolution, IReadOnlyDictionary<int, double>? stepScales, CancellationToken ct = default)
        => PreflightChargedAsync(organizationId, runSteps, chargedStepOrders, outputResolution, ct);
}

/// <summary>
/// B7: prices a workflow run before it starts, from the usage the ledger already records
/// (<c>usage_events</c>, written by B3b/B3c) grouped by step type and agent, falling back to B0's
/// measured per-step-type defaults when the organization has no history yet.
///
/// <para>
/// <b>Cold start is explicit, bounded and non-zero.</b> A step with no usable history holds the larger
/// of <see cref="StepCreditDefaults"/>' low and its share of the low edge of the cold steps' usual range
/// (<see cref="ColdStartHoldPlan"/>), so never less than before the cold-start hold re-plan and never zero (a zero would
/// short-circuit the engine's gate into holding nothing at all, so the step would be free); if that
/// plan cannot be computed it holds <see cref="StepCreditDefaults"/>' low. The
/// <c>Basis</c> on every step says which of the two produced its number, and <c>hasHistory</c> says
/// whether the run has any recorded usage behind it, so the UI never presents a default as a
/// measurement.
/// </para>
///
/// <para>
/// <b>The preflight is advisory to the gate, not a replacement for it.</b> It refuses a run whose
/// balance cannot cover the sum of the holds, which is what the caller sees as a 402 before anything
/// is queued. The per-step gate (B6a/B6b) remains the enforcement point and fails closed on its own.
/// Because this is the friendlier of the two checks, a failure to compute the estimate — a database
/// that is briefly unreachable, a mapping that is not deployed — must not block a run: it is logged
/// and answered <see cref="WorkflowPreflightOutcome.Allowed"/> rather than refusing work the gate
/// would have accepted. That is the opposite of the gate's own fail-closed rule, and deliberate.
/// </para>
///
/// <para>
/// <b>What is shown is not what is held, but a cold start holds at least its low edge.</b>
/// <c>RequiredMillis</c> (the hold, the 402) is <see cref="RunCreditHolds"/>: history steps hold their
/// p90, and each cold step the larger of its old default and its share of the low edge of the cold
/// steps' usual range, priced from the same cold-start profiles as the display (the cold-start hold re-plan). Display history never moves it. The displayed "≈ X (usually A–B)" is a separate path: per-agent-type
/// usage profiles (<see cref="StepUsageProfiles"/>) priced through <see cref="IModelPriceService"/> with
/// each agent's resolved provider, refined by this organization's charged history (exact agent, then
/// agent type), combined per run with review loops and repeating steps modelled
/// (<see cref="RunCreditRange"/>), and replaced by the p25–p75 of the workflow's own past runs once
/// there are <see cref="StepCreditEstimateOptions.MinRuns"/> of them. Fixed-price steps leave the
/// range and are shown as "up to".
/// </para>
/// </summary>
public sealed class WorkflowCostEstimator(
    IServiceScopeFactory scopes,
    IOptions<BillingOptions> billing,
    IOptions<StepCreditEstimateOptions> estimateOptions,
    ILogger<WorkflowCostEstimator> logger,
    TimeProvider? time = null) : IWorkflowCostEstimator
{
    private readonly StepCreditEstimateOptions _options = estimateOptions.Value;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Whether <c>Billing:Enabled</c> is on. Off (self-host): nothing is estimated or refused.</summary>
    public bool BillingEnabled => billing.Value.Enabled;

    public Task<WorkflowCostEstimate> EstimateAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default)
        => EstimateAsync(organizationId, steps, outputResolution: null, ct);

    public async Task<WorkflowCostEstimate> EstimateAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> steps, int? outputResolution, CancellationToken ct = default)
        => await EstimateOrUnknownAsync(organizationId, steps, outputResolution, ct).ConfigureAwait(false)
            ?? WorkflowCostEstimate.Disabled;

    /// <summary>
    /// The estimate, or null when it cannot be trusted not to understate the hold (QA-EF-11): the agent
    /// types of the run's steps could not be read, so a step that renders the output may have lost its
    /// resolution scaling. Every public surface turns null into <see cref="WorkflowCostEstimate.Disabled"/>,
    /// and the preflight into an un-evaluated Allowed.
    /// </summary>
    private async Task<WorkflowCostEstimate?> EstimateOrUnknownAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> steps, int? outputResolution, CancellationToken ct)
    {
        if (!BillingEnabled)
        {
            return WorkflowCostEstimate.Disabled;
        }

        // B6c: a VideoGenerate step is priced from its own settings, not from history (or from B0's
        // per-step-type row). Resolved BEFORE the history query so the cold-start path and the
        // history path price those steps identically, and so the engine's own per-step hold and this
        // preflight can never disagree about whether a run may begin.
        IReadOnlyDictionary<int, long> videoHolds = await ResolveVideoGenerateHoldsAsync(steps, ct).ConfigureAwait(false);

        Guid? orgId = organizationId is { } id && id != Guid.Empty ? id : null;
        IReadOnlyList<StepCreditSample> samples = [];
        if (orgId is { } org && steps.Count > 0)
        {
            // The HOLD's history: exactly the query and the fallback this method always had. The
            // display path below reads its own history separately; a failure there leaves these samples
            // alone. The hold plan takes its render-step flags from the display's agent types, so a failed
            // agent-type read (only types known from the steps' own navigation remain) could drop a step's
            // resolution scaling and understate RequiredMillis: that estimate is answered as unknown.
            try
            {
                using IServiceScope scope = scopes.CreateScope();
                InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
                DateTime since = _time.GetUtcNow().UtcDateTime.AddDays(-Math.Max(1, _options.WindowDays));
                samples = await StepCreditHistoryQuery.LoadAsync(
                    db.UsageEvents, db.WorkflowStepResults, db.WorkflowSteps, db.WorkflowExecutions,
                    org, since, _options.MaxSamples, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Estimating the cost of a run for organization {OrganizationId} failed; falling back to the B0 defaults",
                    org);
                samples = [];
            }
        }

        if (steps.Count == 0)
        {
            return EstimateFrom(samples, steps, videoHolds, outputResolution);
        }

        // Decision 3 and QA-15: render/encode work scales with output pixels. The resolution goes into
        // the shared hold plan (RunCreditHolds), which also decides which steps render the output from
        // the display's agent types (platform agents and the org's own, as the engine reads them).
        // Null (Auto) with no explicit-resolution history leaves every figure as it was.
        (DisplayCreditInputs display, bool agentTypesUnknown) = await LoadDisplayInputsAsync(orgId, steps, outputResolution, ct).ConfigureAwait(false);
        if (agentTypesUnknown)
        {
            return null;
        }

        IReadOnlyDictionary<int, IReadOnlyList<SourceFootage>> footage = await LoadSourceFootageAsync(orgId, steps, ct).ConfigureAwait(false);
        (IProfileQuantityPricer pricer, bool seedPriced) = await PrefetchPricesAsync(samples, steps, videoHolds, display, outputResolution, footage, ct).ConfigureAwait(false);
        return EstimateFrom(samples, steps, videoHolds, display, pricer, outputResolution, footage) with { PricedFromSeedFallback = seedPriced };
    }

    /// <summary>
    /// The display path's history: agent types for every agent the run names, this org's charged
    /// per-step samples, past run totals of this workflow at the same output resolution and past
    /// review-loop pass counts. Best
    /// effort: anything it cannot read degrades to the cold-start display, never to a throw.
    /// <c>AgentTypesUnknown</c> is true when the run names agents whose types could not be read (the hold
    /// plan's render-step flags depend on them); a failure after that read leaves it false.
    /// </summary>
    private async Task<(DisplayCreditInputs Display, bool AgentTypesUnknown)> LoadDisplayInputsAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> steps, int? outputResolution, CancellationToken ct)
    {
        Dictionary<Guid, AgentType> agentTypes = ColdStartStepPricing.KnownAgentTypes(steps);
        List<Guid> agentIds = [.. steps.SelectMany(ColdStartStepPricing.BranchAgentIds).Where(a => a != Guid.Empty && !agentTypes.ContainsKey(a)).Distinct()];
        bool agentTypesRead = agentIds.Count == 0;

        DisplayCreditInputs display = DisplayCreditInputs.Empty with { AgentTypes = agentTypes, MinRuns = Math.Max(1, _options.MinRuns) };
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

            if (agentIds.Count > 0)
            {
                // Only platform agents (no organization) and the caller's own: another tenant's agent
                // definition is never read, even by an id the caller's workflow happens to name.
                var rows = await db.AgentDefinitions.AsNoTracking()
                    .Where(a => agentIds.Contains(a.Id) && (a.OrganizationId == null || a.OrganizationId == organizationId))
                    .Select(a => new { a.Id, a.AgentType })
                    .ToListAsync(ct).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    agentTypes[row.Id] = row.AgentType;
                }

                agentTypesRead = true;
            }

            if (organizationId is not { } org)
            {
                return (display, false);
            }

            DateTime since = _time.GetUtcNow().UtcDateTime.AddDays(-Math.Max(1, _options.WindowDays));
            IReadOnlyList<DisplayStepSample> stepSamples = await DisplayCreditHistory.LoadStepSamplesAsync(
                db.UsageEvents, db.WorkflowStepResults, db.WorkflowSteps, db.WorkflowExecutions, db.AgentDefinitions,
                org, since, _options.MaxSamples, ct).ConfigureAwait(false);

            // Run totals only make sense for one saved workflow. Every step edit replaces the steps
            // and bumps WorkflowDefinition.UpdatedAt (the PUT, the assistant's diff apply and its
            // tool), so a run from before the last edit describes a different workflow and is left out.
            IReadOnlyList<long> runTotals = [];
            List<Guid> workflowIds = [.. steps.Select(s => s.WorkflowDefinitionId).Distinct()];
            if (workflowIds is [var workflowId] && workflowId != Guid.Empty)
            {
                DateTime? updatedAt = await db.WorkflowDefinitions.AsNoTracking()
                    .Where(w => w.Id == workflowId)
                    .Select(w => (DateTime?)w.UpdatedAt)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                DateTime runsSince = updatedAt is { } edited && edited > since ? edited : since;
                // Every VideoGenerate and AudioGenerate step is fixed-price on the display path, declared or not.
                List<Guid> fixedStepIds = [.. steps
                    .Where(s => StepCreditDefaults.IsDeclaredPriced(s.StepType))
                    .Select(s => s.Id)];
                // A run executed from its own ExecutionSnapshot references the snapshot's copies of the
                // steps, so its fixed-price steps are also matched by order within that snapshot.
                List<int> fixedStepOrders = [.. steps
                    .Where(s => s.StepType == StepType.VideoGenerate)
                    .Select(s => s.StepOrder)];
                runTotals = await DisplayCreditHistory.LoadRunTotalsAsync(
                    db.UsageEvents, db.WorkflowStepResults, db.WorkflowExecutions, db.WorkflowSteps,
                    org, runsSince, workflowId, fixedStepIds, fixedStepOrders, _options.MaxSamples, outputResolution, ct).ConfigureAwait(false);
            }

            List<WorkflowStep> loopSteps = [.. steps.Where(s => s.StepType == StepType.ReviewLoop && s.Id != Guid.Empty)];
            IReadOnlyDictionary<Guid, IReadOnlyList<int>> loopPasses = await DisplayCreditHistory.LoadLoopPassesAsync(
                db.WorkflowStepResults, db.WorkflowExecutions, db.WorkflowSteps, org, since, loopSteps, ct).ConfigureAwait(false);

            return (display with { StepSamples = stepSamples, RunTotals = runTotals, LoopPasses = loopPasses }, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Reading the display history for organization {OrganizationId} failed; showing the cold-start range",
                organizationId);
            return (display, !agentTypesRead);
        }
    }

    /// <summary>
    /// Prices, up front, every profile quantity the display core will ask for: the chat provider is
    /// resolved once per (agent type, agent), compute once, and speech, transcription and video
    /// generation once each, then each quantity goes through <see cref="IModelPriceService.PriceAsync"/>
    /// — the meter's own lookup, so the model capability prices, the markup and the multipliers all
    /// flow through. A provider that cannot be resolved or a quantity that cannot be priced falls back to
    /// the seed constants (<see cref="StepUsageProfiles.SeedFallbackMillis"/>), logged, never thrown.
    /// </summary>
    private async Task<(IProfileQuantityPricer Pricer, bool SeedPriced)> PrefetchPricesAsync(
        IReadOnlyList<StepCreditSample> samples,
        IReadOnlyList<WorkflowStep> steps,
        IReadOnlyDictionary<int, long> videoHolds,
        DisplayCreditInputs display,
        int? outputResolution,
        IReadOnlyDictionary<int, IReadOnlyList<SourceFootage>>? footage,
        CancellationToken ct)
    {
        // A dry run of the pure core with a recording pricer names exactly the quantities to price:
        // the display's and the cold-start hold plan's, which ask for the same cold profiles.
        var recorder = new RecordingProfilePricer();
        EstimateFrom(samples, steps, videoHolds, display, recorder, outputResolution, footage);

        // No early SeedProfilePricer.Instance return when nothing needs quoting: that pricer carries the compiled-in
        // markup. ProfilePriceTable.BuildAsync answers an empty request list with a seed pricer at the LIVE markup,
        // which the cold-start default holds are priced at (T-032), exactly as the engine's plan does.
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            IProfileQuantityPricer table = await ProfilePriceTable.BuildAsync(
                recorder.Requests,
                scope.ServiceProvider.GetService<IInferenceProviderResolver>(),
                scope.ServiceProvider.GetService<IModelPriceService>(),
                logger,
                ct).ConfigureAwait(false);
            return (table, recorder.Requests.Any(table.IsSeedFallback));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Pricing the estimate's cold-start profiles failed; using the seed constants");
            return (SeedProfilePricer.Instance, recorder.Requests.Count > 0);
        }
    }

    /// <summary>
    /// One VideoGenerate step's hold. A plan-sourced step whose planner may be offered the project's avatars and objects
    /// holds for them only when the step's project holds any avatar (docs/design/avatar-objects.md, Money): the same
    /// fact the engine enters before it reserves (<see cref="ProjectSubjectsPricing"/>), read here through the step's
    /// workflow. A project that cannot be read is priced as if it may hold one: a hold only ever grows.
    /// </summary>
    private static async Task<long?> VideoGenerateHoldAsync(
        IServiceProvider services, WorkflowStep step, IInferenceProviderResolver providers, IVideoGenerationClientFactory clients,
        IModelPriceService prices, CancellationToken ct)
    {
        bool? projectHasAvatars = null;
        if (ProjectSubjectsPricing.Matters(step.VideoGenerateConfigJson))
        {
            try
            {
                var db = services.GetService<ReelBolt.Inference.Api.Data.InferenceApiDbContext>();
                Guid? projectId = step.WorkflowDefinition?.ProjectId;
                if (db is not null && projectId is null && step.WorkflowDefinitionId != Guid.Empty)
                {
                    projectId = await db.WorkflowDefinitions.AsNoTracking().IgnoreQueryFilters()
                        .Where(w => w.Id == step.WorkflowDefinitionId)
                        .Select(w => (Guid?)w.ProjectId)
                        .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                }

                if (db is not null && projectId is { } project && project != Guid.Empty)
                {
                    projectHasAvatars = await db.ProjectAvatars.AsNoTracking().IgnoreQueryFilters()
                        .AnyAsync(a => a.ProjectId == project, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                projectHasAvatars = null;
            }
        }

        using IDisposable projectSubjects = ProjectSubjectsPricing.Enter(projectHasAvatars);
        return await VideoGenerateCreditPricing
            .TryHoldMillisAsync(step.VideoGenerateConfigJson, providers, clients, prices, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// B6c (and T-108 for <see cref="StepType.AudioGenerate"/>, through <see cref="AudioGenerateCreditPricing"/>): the
    /// per-step hold for every <see cref="StepType.VideoGenerate"/> step in the run, keyed by
    /// step order, priced through <see cref="VideoGenerateCreditPricing"/> — the same call the engine
    /// makes before it reserves. Best-effort in this class's own spirit: a step it cannot price is
    /// simply absent from the map and falls back to <see cref="StepCreditDefaults"/>, because a
    /// pricing gap must never refuse a run the gate itself would have allowed.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, long>> ResolveVideoGenerateHoldsAsync(
        IReadOnlyList<WorkflowStep> steps, CancellationToken ct)
    {
        // A hosted VideoEnhance step is held from its own config too (VideoEnhanceStepPricing, the engine's own call); a local one holds nothing.
        List<WorkflowStep> videoSteps = [.. steps.Where(s => StepCreditDefaults.IsDeclaredPriced(s.StepType)
            && !(s.StepType == StepType.VideoEnhance && StepCreditDefaults.IsLocalVideoEnhance(s.VideoEnhanceConfigJson)))];
        if (videoSteps.Count == 0)
        {
            return new Dictionary<int, long>();
        }

        Dictionary<int, long> holds = [];
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            IInferenceProviderResolver providers = scope.ServiceProvider.GetRequiredService<IInferenceProviderResolver>();
            IVideoGenerationClientFactory clients = scope.ServiceProvider.GetRequiredService<IVideoGenerationClientFactory>();
            IModelPriceService prices = scope.ServiceProvider.GetRequiredService<IModelPriceService>();

            foreach (WorkflowStep step in videoSteps)
            {
                // T-108: an AudioGenerate step is priced the same way, from the audio catalogue (no client needed).
                // An ImageGenerate step too: ImageGenerateCreditPricing is the engine's own hold call (preflight == hold).
                long? hold = step.StepType == StepType.VideoEnhance
                    ? await VideoEnhanceStepPricing
                        .TryStepHoldMillisAsync(step.VideoEnhanceConfigJson, providers, prices, ct,
                            VideoEnhanceHostedSettings.From(scope.ServiceProvider.GetService<Microsoft.Extensions.Configuration.IConfiguration>()))
                        .ConfigureAwait(false)
                    : step.StepType == StepType.AudioGenerate
                    ? await AudioGenerateCreditPricing
                        .TryHoldMillisAsync(step.AudioGenerateConfigJson, providers, prices, ct)
                        .ConfigureAwait(false)
                    : step.StepType == StepType.ImageGenerate
                    ? await ImageGenerateCreditPricing
                        .TryHoldMillisAsync(step.ImageGenerateConfigJson, providers, prices, ct)
                        .ConfigureAwait(false)
                    : await VideoGenerateHoldAsync(scope.ServiceProvider, step, providers, clients, prices, ct).ConfigureAwait(false);

                if (hold is { } millis)
                {
                    holds[step.StepOrder] = millis;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Pricing this run's VideoGenerate steps from their own settings failed; they fall back to B0's per-step-type default");
        }

        return holds;
    }

    public Task<WorkflowPreflightResult> PreflightAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default)
        => PreflightAsync(organizationId, steps, outputResolution: null, ct);

    public Task<WorkflowPreflightResult> PreflightAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> steps, int? outputResolution, CancellationToken ct = default)
        => PreflightOverAsync(organizationId, org => EstimateOrUnknownAsync(org, steps, outputResolution, ct), ct);

    public Task<WorkflowCostEstimate> EstimateChargedAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default)
        => EstimateChargedAsync(organizationId, runSteps, chargedStepOrders, outputResolution: null, ct);

    public async Task<WorkflowCostEstimate> EstimateChargedAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders,
        int? outputResolution, CancellationToken ct = default)
        => await EstimateChargedOrUnknownAsync(organizationId, runSteps, chargedStepOrders, outputResolution, ct).ConfigureAwait(false)
            ?? WorkflowCostEstimate.Disabled;

    /// <summary>The charged estimate, or null when either estimate it is built from is unknown (QA-EF-11).</summary>
    private async Task<WorkflowCostEstimate?> EstimateChargedOrUnknownAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders,
        int? outputResolution, CancellationToken ct)
    {
        bool[] isCharged = [.. runSteps.Select(s => chargedStepOrders.Contains(s.StepOrder))];
        List<WorkflowStep> charged = [.. runSteps.Where((_, i) => isCharged[i])];
        WorkflowCostEstimate? shown = await EstimateOrUnknownAsync(organizationId, charged, outputResolution, ct).ConfigureAwait(false);
        if (shown is null || !shown.BillingEnabled || charged.Count == runSteps.Count)
        {
            return shown;
        }

        // QA-7: a cold step's hold may be a share of the run's low edge, so it depends on the other steps in
        // the run. The engine plans the execution's every step, reused ones included, so the hold is
        // planned here over the same steps (at the same resolution) and only the charged steps' shares are kept.
        WorkflowCostEstimate? planned = await EstimateOrUnknownAsync(organizationId, runSteps, outputResolution, ct).ConfigureAwait(false);
        return planned is null ? null : WithChargedHolds(shown, planned, isCharged);
    }

    public Task<WorkflowPreflightResult> PreflightChargedAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default)
        => PreflightChargedAsync(organizationId, runSteps, chargedStepOrders, outputResolution: null, ct);

    public Task<WorkflowPreflightResult> PreflightChargedAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders,
        int? outputResolution, CancellationToken ct = default)
        => PreflightChargedAsync(organizationId, runSteps, chargedStepOrders, outputResolution, stepScales: null, ct);

    public Task<WorkflowPreflightResult> PreflightChargedAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders,
        int? outputResolution, IReadOnlyDictionary<int, double>? stepScales, CancellationToken ct = default)
        => PreflightOverAsync(
            organizationId,
            async org => ScaleSteps(await EstimateChargedOrUnknownAsync(org, runSteps, chargedStepOrders, outputResolution, ct).ConfigureAwait(false), stepScales),
            ct);

    /// <summary>
    /// <paramref name="estimate"/> with each step in <paramref name="scales"/> (step order to a fraction in (0, 1])
    /// priced for that share of its items: the step's hold (never below 1, never above the whole-step hold), its
    /// display range and fixed price, with the run's hold sum and totals moved by the same amounts. Fractions of 1
    /// or more, and unknown step orders, change nothing; null in, null out.
    /// </summary>
    public static WorkflowCostEstimate? ScaleSteps(WorkflowCostEstimate? estimate, IReadOnlyDictionary<int, double>? scales)
    {
        if (estimate is null || scales is not { Count: > 0 } || !estimate.BillingEnabled)
        {
            return estimate;
        }

        long dLow = 0, dHigh = 0, dTypical = 0, dFixed = 0;
        List<WorkflowStepCostEstimate> steps = [];
        foreach (WorkflowStepCostEstimate st in estimate.Steps)
        {
            if (!scales.TryGetValue(st.StepOrder, out double f) || !double.IsFinite(f) || f <= 0d || f >= 1d)
            {
                steps.Add(st);
                continue;
            }

            WorkflowStepCostEstimate scaled = st with
            {
                LowMillis = Part(st.LowMillis, f),
                HighMillis = Part(st.HighMillis, f),
                TypicalMillis = Part(st.TypicalMillis, f),
                FixedMaxMillis = Part(st.FixedMaxMillis, f),
                HoldMillis = StepChangeScope.ApplyToHold(st.HoldMillis, f)
            };
            dLow += st.LowMillis - scaled.LowMillis;
            dHigh += st.HighMillis - scaled.HighMillis;
            dTypical += st.TypicalMillis - scaled.TypicalMillis;
            dFixed += st.FixedMaxMillis - scaled.FixedMaxMillis;
            steps.Add(scaled);
        }

        return estimate with
        {
            Steps = steps,
            RequiredMillis = steps.Sum(x => x.HoldMillis) + (estimate.RequiredMillis - estimate.Steps.Sum(x => x.HoldMillis)),
            LowMillis = Math.Max(0, estimate.LowMillis - dLow),
            HighMillis = Math.Max(0, estimate.HighMillis - dHigh),
            TypicalMillis = Math.Max(0, estimate.TypicalMillis - dTypical),
            FixedMaxMillis = Math.Max(0, estimate.FixedMaxMillis - dFixed)
        };

        static long Part(long millis, double fraction) => millis <= 0 ? millis : Math.Max(1, (long)Math.Ceiling(millis * fraction));
    }

    /// <summary>
    /// <paramref name="shown"/> (the charged steps' display) with its holds replaced by the charged steps'
    /// entries of <paramref name="planned"/> (the whole run's plan, one entry per run step, in run order).
    /// </summary>
    internal static WorkflowCostEstimate WithChargedHolds(WorkflowCostEstimate shown, WorkflowCostEstimate planned, IReadOnlyList<bool> isCharged)
    {
        long[] holds = [.. planned.Steps.Where((_, i) => isCharged[i]).Select(s => s.HoldMillis)];
        if (holds.Length != shown.Steps.Count)
        {
            return shown;
        }

        return shown with
        {
            RequiredMillis = holds.Sum(),
            Steps = [.. shown.Steps.Select((s, i) => s with { HoldMillis = holds[i] })]
        };
    }

    private async Task<WorkflowPreflightResult> PreflightOverAsync(
        Guid? organizationId, Func<Guid, Task<WorkflowCostEstimate?>> estimateFor, CancellationToken ct)
    {
        if (!BillingEnabled || organizationId is not { } orgId || orgId == Guid.Empty)
        {
            return new WorkflowPreflightResult(WorkflowPreflightOutcome.Allowed, WorkflowCostEstimate.Disabled, 0);
        }

        WorkflowCostEstimate? estimate = null;
        try
        {
            estimate = await estimateFor(orgId).ConfigureAwait(false);
            if (estimate is null)
            {
                // QA-EF-11: the agent types could not be read, so the figure could understate the engine's
                // hold. Still Allowed (fail open), but not evaluated, and no figures to show.
                logger.LogWarning(
                    "The execute preflight for organization {OrganizationId} could not read the run's agent types; allowing the run",
                    orgId);
                return new WorkflowPreflightResult(
                    WorkflowPreflightOutcome.Allowed, WorkflowCostEstimate.Disabled, 0, Evaluated: false);
            }

            using IServiceScope scope = scopes.CreateScope();
            // A separate scope from the estimate's: the balance is read under the org's own account
            // row and must not share a change tracker with the usage query.
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            CreditLedgerResult balance = await new CreditLedger(db, _time).GetBalanceAsync(orgId, ct).ConfigureAwait(false);

            WorkflowPreflightOutcome outcome = balance.AvailableMillis < estimate.RequiredMillis
                ? WorkflowPreflightOutcome.InsufficientCredits
                : WorkflowPreflightOutcome.Allowed;

            if (outcome == WorkflowPreflightOutcome.InsufficientCredits)
            {
                logger.LogInformation(
                    "Refusing to start a run for organization {OrganizationId}: {Required} millicredits required, {Available} available",
                    orgId, estimate.RequiredMillis, balance.AvailableMillis);
            }

            return new WorkflowPreflightResult(outcome, estimate, balance.AvailableMillis);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Advisory check: never block a run the gate would have allowed (see this class's summary).
            // Still Allowed, but marked not evaluated so a surface that shows the cost does not read it as
            // free; an estimate computed before the balance read failed is kept.
            logger.LogWarning(ex,
                "The execute preflight for organization {OrganizationId} could not be evaluated; allowing the run",
                orgId);
            return new WorkflowPreflightResult(
                WorkflowPreflightOutcome.Allowed, estimate ?? WorkflowCostEstimate.Disabled, 0, Evaluated: false);
        }
    }

    /// <summary>
    /// The pure part over the hold's samples alone: the hold (<see cref="RunCreditHolds"/>), and a display priced at
    /// the seed constants with those same samples (the charged ones) as its only history. Kept for
    /// callers with no provider or price context.
    /// </summary>
    internal static WorkflowCostEstimate EstimateFrom(
        IReadOnlyList<StepCreditSample> samples,
        IReadOnlyList<WorkflowStep> steps,
        IReadOnlyDictionary<int, long>? videoHolds = null,
        int? outputResolution = null)
        => EstimateFrom(samples, steps, videoHolds,
            DisplayCreditInputs.Empty with
            {
                StepSamples = [.. samples
                    .Where(s => s.CreditsMillis > 0 && s.AgentDefinitionId is not null)
                    .Select(s => new DisplayStepSample(s.StepType, null, s.AgentDefinitionId!.Value, s.CreditsMillis, s.OutputResolution))]
            },
            SeedProfilePricer.Instance,
            outputResolution);

    /// <summary>
    /// The pure part: one HOLD per step from the shared run plan (<see cref="RunCreditHolds"/>: p90 with
    /// history, max(old default, share of the cold steps' low edge) without), summed into <c>RequiredMillis</c>, and,
    /// separately, the displayed range. Separated so the maths is testable without a database or a
    /// provider. <paramref name="videoHolds"/> carries B6c's per-step-order VideoGenerate holds,
    /// already priced from those steps' own settings. Every VideoGenerate step is fixed-price on the
    /// display ("up to"): its declared price, else <see cref="StepUsageProfiles.VideoGenerateMaxClips"/>
    /// through <paramref name="pricer"/>, which also prices the cold-start profiles.
    /// <para>
    /// <paramref name="outputResolution"/> (short side; null = Auto) is the run's chosen output resolution.
    /// The holds are the shared plan's FINAL holds at that resolution (<see cref="RunCreditHolds"/>, which
    /// also flags the steps that render the output: VideoCompile, TimelineRender, Revise and an Agent
    /// granted <c>SandboxRender</c>): a render step's reference hold (its Auto/1080p history, else the cold
    /// plan, priced at the 1080p reference, see <see cref="ProfilePriceTable"/>) times
    /// <see cref="OutputResolution.PixelFactor"/> above 1, raised (never lowered) to the p90 of its own
    /// history at that resolution, never shrunk below the reference — exactly what the engine reserves.
    /// On the display, a render step at a non-reference resolution with enough history of its own at that
    /// resolution shows that history unscaled; otherwise its reference history (else its profile) is scaled:
    /// VideoCompile and TimelineRender move their whole distribution with the factor, a rendering Agent or
    /// Revise step only its top, above 1, because its bottom is token work a resolution does not change.
    /// Other steps pool every resolution's history.
    /// </para>
    /// </summary>
    internal static WorkflowCostEstimate EstimateFrom(
        IReadOnlyList<StepCreditSample> samples,
        IReadOnlyList<WorkflowStep> steps,
        IReadOnlyDictionary<int, long>? videoHolds,
        DisplayCreditInputs display,
        IProfileQuantityPricer pricer,
        int? outputResolution = null,
        IReadOnlyDictionary<int, IReadOnlyList<SourceFootage>>? footage = null)
    {
        // The hold: the shared per-run plan the engine computes too (RunCreditHolds), FINAL at the run's
        // resolution. History steps keep their p90, a VideoGenerate step its declared price (else the
        // default), and each cold step the larger of its old default and its share of the cold low edge; a
        // step that renders the output then grows with the chosen resolution (ResolutionHoldScaling.HoldAt).
        // RequiredMillis is the sum, so the 402 and the engine's reservations cannot disagree.
        var estimator = new StepCreditEstimator(samples, StepCreditEstimator.DefaultMinSamples);
        RunHoldPlan holds = RunCreditHolds.Plan(steps, estimator, videoHolds, display.AgentTypes, pricer, outputResolution, footage);
        double pixelFactor = OutputResolution.PixelFactor(outputResolution);
        DisplayBuckets buckets = new(display.StepSamples);
        List<StepDisplay> shown = [];
        long required = 0;

        for (int i = 0; i < steps.Count; i++)
        {
            WorkflowStep step = steps[i];
            long? videoHold = videoHolds is not null && videoHolds.TryGetValue(step.StepOrder, out long declared)
                ? declared
                : null;

            bool rendersOutput = holds.RendersOutput[i];
            long hold = holds.HoldsMillis[i];
            required += hold;
            StepDisplay plain = DisplayStep(step, videoHold, hold, buckets, display.AgentTypes, pricer, rendersOutput, outputResolution);
            shown.Add(SourceFootageAddOn(SegmentationAddOn(
                plain.AtResolution ? plain : plain with { Cost = ScaleCost(plain.Cost, step, rendersOutput, pixelFactor) }),
                holds.SourceAddOnsMillis is { } sourceAddOns && i < sourceAddOns.Count ? sourceAddOns[i] : 0));
        }

        RunLoops runLoops = RunLoopModel.Of(steps, display.LoopPasses);
        IReadOnlyList<LoopInput> loops = runLoops.Loops;
        List<RunStepInput> inputs = [.. shown.Select(d => new RunStepInput(d.Step.StepOrder, d.Cost, d.FixedMaxMillis))];
        RunRange range = RunCreditRange.Combine(inputs, loops);
        RunVarianceBreakdown variance = RunCreditRange.Decompose(inputs, loops);

        // A review loop that may change steps can re-make steps before its loop target: at most once per loop-back, each
        // at its own hold (or its fixed price). That bounded worst case joins the "up to" ceiling; it is not held up front
        // (the engine holds each re-run when it runs), so RequiredMillis stays the engine's plan.
        IReadOnlyDictionary<int, int> reviewExtraRuns = ReviewAdjustmentCeiling.ExtraRunsByStepOrder(
            steps, s => ColdStartStepPricing.AgentTypeOf(s.AgentDefinitionId, s, display.AgentTypes));
        long reviewRerunMax = shown.Sum(d => reviewExtraRuns.TryGetValue(d.Step.StepOrder, out int extraRuns)
            ? extraRuns * Math.Max(d.HoldMillis, d.FixedMaxMillis)
            : 0L);

        List<WorkflowStepCostEstimate> perStep = [.. shown.Select(d => new WorkflowStepCostEstimate(
            d.Step.StepOrder,
            d.Step.StepType.ToString(),
            d.Step.AgentDefinitionId,
            d.Step.Label,
            d.Cost.P25Millis,
            d.Cost.P75Millis,
            d.HoldMillis,
            d.Basis.ToString(),
            d.Cost.TypicalMillis,
            range.ExpectedRunsByStepOrder.GetValueOrDefault(d.Step.StepOrder, 1d),
            d.FixedMaxMillis))];

        // Run level: the workflow's own past runs once there are enough of them, else the combination.
        long[] totals = [.. display.RunTotals.Where(t => t > 0).Order()];
        long typical = range.TypicalMillis;
        long low = CheapestPathLowMillis(steps, inputs, loops) is { } branchLow ? Math.Min(range.LowMillis, branchLow) : range.LowMillis;
        long high = range.HighMillis;
        string rangeBasis = shown.Any(d => d.Basis is CreditEstimateBasis.History or CreditEstimateBasis.AgentTypeHistory)
            ? CreditRangeBasis.StepHistory
            : CreditRangeBasis.FirstRun;
        if (totals.Length >= Math.Max(1, display.MinRuns))
        {
            typical = CreditPercentiles.Continued(totals, 0.5);
            low = CreditPercentiles.Continued(totals, 0.25);
            high = CreditPercentiles.Continued(totals, 0.75);
            rangeBasis = CreditRangeBasis.PastRuns;
        }

        return new WorkflowCostEstimate(
            true,
            estimator.HasHistory,
            low,
            high,
            required,
            perStep,
            typical,
            range.FixedMaxMillis + reviewRerunMax,
            rangeBasis,
            totals.Length,
            reviewRerunMax > 0
                ? [.. SpreadReasons(shown, loops, runLoops.ReviewLoops, runLoops.UnknownForEach, variance, range), CreditSpreadReasons.ReviewStepReruns]
                : SpreadReasons(shown, loops, runLoops.ReviewLoops, runLoops.UnknownForEach, variance, range));
    }

    /// <summary>
    /// the bottom of the range of a run with Conditional branches is its cheapest path, not
    /// every step at its p25. A step a branch can skip (<see cref="WorkflowBranchReach.OnEveryPath"/>)
    /// counts as free there, so the profile template's low is the free path with no fallback while its
    /// high (and the hold, <c>RequiredMillis</c>) still covers every fallback step. Null for a run every
    /// step of which always runs: its range is unchanged.
    /// </summary>
    private static long? CheapestPathLowMillis(
        IReadOnlyList<WorkflowStep> steps, IReadOnlyList<RunStepInput> inputs, IReadOnlyList<LoopInput> loops)
    {
        bool[] always = WorkflowBranchReach.OnEveryPath(steps);
        if (always.All(a => a) || always.Length != inputs.Count)
        {
            return null;
        }

        List<RunStepInput> cheapest = [.. inputs.Select((input, i) => always[i] ? input : input with { Cost = default, FixedMaxMillis = 0 })];
        return RunCreditRange.Combine(cheapest, loops).LowMillis;
    }

    /// <summary>
    /// One step's display: one execution's distribution, its fixed price if any, and where it came from.
    /// <paramref name="AtResolution"/>: the distribution is already the run's resolution's own history, so
    /// it is not scaled again.
    /// </summary>
    private sealed record StepDisplay(
        WorkflowStep Step, StepCostDistribution Cost, long FixedMaxMillis, long HoldMillis, CreditEstimateBasis Basis, bool OwnProvider,
        bool AtResolution = false);

    private static StepDisplay DisplayStep(
        WorkflowStep step,
        long? videoHold,
        long holdMillis,
        DisplayBuckets buckets,
        IReadOnlyDictionary<Guid, AgentType> agentTypes,
        IProfileQuantityPricer pricer,
        bool rendersOutput,
        int? outputResolution)
    {
        // Encode-once: a Plan-stage compile does the planning and encodes nothing, so it records no usage and costs nothing;
        // the one Final-stage compile after the review carries the encode (and the history, hold and estimate) of the run.
        if (StepCreditDefaults.IsNotCharged(step.StepType) || CompileEncodeStages.IsPlanStage(step)
            || DynamicsPlannerPricing.SkipsAmbient(ColdStartStepPricing.AgentTypeOf(step.AgentDefinitionId, step, agentTypes)))
        {
            return new StepDisplay(step, default, 0, holdMillis, CreditEstimateBasis.NotCharged, false);
        }

        if (step.StepType == StepType.VideoGenerate)
        {
            // Always fixed-price on the display path: it leaves the range and is shown as "up to" —
            // its declared price, else B0's max-clips ceiling priced with the resolved provider.
            long ceiling = videoHold is { } declared && declared > 0
                ? declared
                : Math.Max(0, pricer.Price(StepUsageProfiles.VideoGenerateMaxClips, AgentType.Custom, null).CreditsMillis);
            return new StepDisplay(step, default, ceiling, holdMillis, CreditEstimateBasis.VideoGeneration, false);
        }

        if (step.StepType == StepType.AudioGenerate)
        {
            // T-108: fixed-price like VideoGenerate, shown as "up to": its declared price (takes x billed units at the
            // settlement's price row), else B0's p90 row (four 5-minute takes) at the live markup.
            long ceiling = videoHold is { } declared && declared > 0
                ? declared
                : Math.Max(0, StepCreditDefaults.MillisFor(StepType.AudioGenerate, pricer.Markup).High);
            return new StepDisplay(step, default, ceiling, holdMillis, CreditEstimateBasis.AudioGeneration, false);
        }

        if (step.StepType == StepType.VideoEnhance)
        {
            // A local operation is free; a hosted one is fixed-price like VideoGenerate, shown as "up to": its declared price (the
            // engine's own hold, VideoEnhanceStepPricing), else the cold-start row at the live markup.
            if (StepCreditDefaults.IsLocalVideoEnhance(step.VideoEnhanceConfigJson))
                return new StepDisplay(step, default, 0, holdMillis, CreditEstimateBasis.NotCharged, false);

            long ceiling = videoHold is { } declared && declared > 0
                ? declared
                : Math.Max(0, StepCreditDefaults.MillisFor(StepType.VideoEnhance, pricer.Markup).High);
            return new StepDisplay(step, default, ceiling, holdMillis, CreditEstimateBasis.VideoGeneration, false);
        }

        if (step.StepType == StepType.ImageGenerate)
        {
            // Fixed-price like AudioGenerate, shown as "up to": its declared price (pictures x the model's price-book
            // row), else the cold-start row's p90 (four pictures) at the live markup.
            long ceiling = videoHold is { } declared && declared > 0
                ? declared
                : Math.Max(0, StepCreditDefaults.MillisFor(StepType.ImageGenerate, pricer.Markup).High);
            return new StepDisplay(step, default, ceiling, holdMillis, CreditEstimateBasis.ImageGeneration, false);
        }

        AgentType? agentType = ColdStartStepPricing.AgentTypeOf(step.AgentDefinitionId, step, agentTypes);
        (StepCostDistribution profiled, bool ownProvider) = ColdStartStepPricing.PriceProfile(step, agentType, agentTypes, pricer);

        // An own-provider chat is free whatever the org's platform-billed history says.
        if (ownProvider)
        {
            return new StepDisplay(step, profiled, 0, holdMillis, CreditEstimateBasis.OwnProvider, true);
        }

        // docs/design/app-inputs.md 28: history metered on the agent's ordinary model is shown moved to the model the run's app
        // chose for it, by the same ratio the hold uses (AppModelHoldScaling). Null (shown as measured) without a choice.
        AppModelPricing? chosenModel = AppModelHoldScaling.For(step, agentType, agentTypes, pricer);
        StepCostDistribution Moved(StepCostDistribution history) => chosenModel is { } model ? model.Display(history) : history;

        // QA-15: a render step at a non-reference resolution shows that resolution's own history, unscaled,
        // once there is enough of it; otherwise (and at the reference) its Auto/1080p history, which the
        // caller scales. Every other step pools all resolutions.
        if (rendersOutput && !ResolutionHoldScaling.IsReference(outputResolution))
        {
            if (buckets.NativeExact(step.StepType, step.AgentDefinitionId, outputResolution) is { } nativeExact)
            {
                return new StepDisplay(step, Moved(nativeExact), 0, holdMillis, CreditEstimateBasis.History, false, AtResolution: true);
            }

            if (agentType is { } nativeType && buckets.NativeByAgentType(step.StepType, nativeType, outputResolution) is { } nativeByType)
            {
                return new StepDisplay(step, Moved(nativeByType), 0, holdMillis, CreditEstimateBasis.AgentTypeHistory, false, AtResolution: true);
            }
        }

        if (buckets.Exact(step.StepType, step.AgentDefinitionId, referenceOnly: rendersOutput) is { } exact)
        {
            return new StepDisplay(step, Moved(exact), 0, holdMillis, CreditEstimateBasis.History, false);
        }

        if (agentType is { } type && buckets.ByAgentType(step.StepType, type, referenceOnly: rendersOutput) is { } byType)
        {
            return new StepDisplay(step, Moved(byType), 0, holdMillis, CreditEstimateBasis.AgentTypeHistory, false);
        }

        return new StepDisplay(step, profiled, 0, holdMillis, CreditEstimateBasis.Default, false);
    }

    /// <summary>The reason codes, largest variance contribution first (ties in the order listed here), deduplicated.</summary>
    private static IReadOnlyList<string> SpreadReasons(
        IReadOnlyList<StepDisplay> shown,
        IReadOnlyList<LoopInput> loops,
        IReadOnlySet<int> reviewLoops,
        IReadOnlySet<int> unknownForEach,
        RunVarianceBreakdown variance,
        RunRange range)
    {
        List<(string Code, double Variance, int Rank)> reasons = [];
        if (reviewLoops.Count > 0)
        {
            reasons.Add((CreditSpreadReasons.ReviewLoopRepeats,
                reviewLoops.Sum(o => variance.LoopVarianceByLoopStepOrder.GetValueOrDefault(o)), 0));
        }

        List<StepDisplay> footage = [.. shown.Where(d => d.Basis == CreditEstimateBasis.Default
            && d.Step.StepType is StepType.VideoAnalyze or StepType.VideoCompile)];
        if (footage.Count > 0)
        {
            reasons.Add((CreditSpreadReasons.FootageLength, footage.Sum(d =>
            {
                double runs = range.ExpectedRunsByStepOrder.GetValueOrDefault(d.Step.StepOrder, 1d);
                return RunCreditRange.StepVariance(d.Cost) * runs * runs;
            }), 1));
        }

        if (unknownForEach.Count > 0)
        {
            reasons.Add((CreditSpreadReasons.ForEachItemsUnknown,
                unknownForEach.Sum(o => variance.LoopVarianceByLoopStepOrder.GetValueOrDefault(o)), 2));
        }

        if (shown.Any(d => d.OwnProvider))
        {
            reasons.Add((CreditSpreadReasons.OwnProviderFree, 0, 3));
        }

        if (range.FixedMaxMillis > 0)
        {
            reasons.Add((CreditSpreadReasons.VideoGenerationMaxClips, 0, 4));
        }

        if (shown.Any(d => d.FixedMaxMillis > 0 && RunCreditRange.WindowOf(d.Step.StepOrder, loops) is not null))
        {
            reasons.Add((CreditSpreadReasons.VideoGenerationInReviewLoop, 0, 5));
        }

        return [.. reasons.OrderByDescending(r => r.Variance).ThenBy(r => r.Rank).Select(r => r.Code).Distinct()];
    }

    /// <summary>
    /// The display history, bucketed by exact agent and by agent type (charged samples only): pooled over
    /// every resolution, over the reference resolutions only (Auto, 1080p), and per exact resolution (QA-15).
    /// </summary>
    private sealed class DisplayBuckets
    {
        private readonly Dictionary<(StepType, Guid), List<long>> _exact = [];
        private readonly Dictionary<(StepType, AgentType), List<long>> _byType = [];
        private readonly Dictionary<(StepType, Guid), List<long>> _referenceExact = [];
        private readonly Dictionary<(StepType, AgentType), List<long>> _referenceByType = [];
        private readonly Dictionary<(StepType, Guid, int?), List<long>> _nativeExact = [];
        private readonly Dictionary<(StepType, AgentType, int?), List<long>> _nativeByType = [];

        public DisplayBuckets(IEnumerable<DisplayStepSample> samples)
        {
            foreach (DisplayStepSample sample in samples)
            {
                if (sample.CreditsMillis <= 0)
                {
                    continue;
                }

                bool reference = ResolutionHoldScaling.IsReference(sample.OutputResolution);
                Add(_exact, (sample.StepType, sample.AgentDefinitionId), sample.CreditsMillis);
                Add(_nativeExact, (sample.StepType, sample.AgentDefinitionId, sample.OutputResolution), sample.CreditsMillis);
                if (reference)
                {
                    Add(_referenceExact, (sample.StepType, sample.AgentDefinitionId), sample.CreditsMillis);
                }

                if (sample.AgentType is { } type)
                {
                    Add(_byType, (sample.StepType, type), sample.CreditsMillis);
                    Add(_nativeByType, (sample.StepType, type, sample.OutputResolution), sample.CreditsMillis);
                    if (reference)
                    {
                        Add(_referenceByType, (sample.StepType, type), sample.CreditsMillis);
                    }
                }
            }
        }

        public StepCostDistribution? Exact(StepType stepType, Guid agentId, bool referenceOnly)
            => (referenceOnly ? _referenceExact : _exact).TryGetValue((stepType, agentId), out List<long>? values) ? From(values) : null;

        public StepCostDistribution? ByAgentType(StepType stepType, AgentType agentType, bool referenceOnly)
            => (referenceOnly ? _referenceByType : _byType).TryGetValue((stepType, agentType), out List<long>? values) ? From(values) : null;

        public StepCostDistribution? NativeExact(StepType stepType, Guid agentId, int? outputResolution)
            => _nativeExact.TryGetValue((stepType, agentId, outputResolution), out List<long>? values) ? From(values) : null;

        public StepCostDistribution? NativeByAgentType(StepType stepType, AgentType agentType, int? outputResolution)
            => _nativeByType.TryGetValue((stepType, agentType, outputResolution), out List<long>? values) ? From(values) : null;

        private static StepCostDistribution? From(List<long> values)
        {
            if (values.Count < StepCreditEstimator.DefaultMinSamples)
            {
                return null;
            }

            long[] sorted = [.. values.Order()];
            return new StepCostDistribution(
                CreditPercentiles.Continued(sorted, 0.5),
                CreditPercentiles.Continued(sorted, 0.25),
                CreditPercentiles.Continued(sorted, 0.75));
        }

        private static void Add<TKey>(Dictionary<TKey, List<long>> buckets, TKey key, long value)
            where TKey : notnull
        {
            if (!buckets.TryGetValue(key, out List<long>? values))
            {
                values = [];
                buckets[key] = values;
            }

            values.Add(value);
        }
    }

    /// <summary>
    /// <paramref name="display"/> with the text-prompted segmentation add-on — the SAME
    /// <see cref="SegmentationCreditPricing.AddOnMillis(string?)"/> the hold adds
    /// (<see cref="RunCreditHolds"/> via <c>ColdStartHoldPlan</c>) — added to its low, typical and high
    /// alike, so the shown band and the reserved figure agree about the segmentation portion instead of
    /// the hold quietly being the larger of the two (estimate matches reservation, B7). One shared
    /// arithmetic, never a second copy: a step that is not a <see cref="StepType.VideoAnalyze"/> step, and
    /// a config that names no segmentation target, answer an add-on of 0 and are unchanged.
    /// </summary>
    private static StepDisplay SegmentationAddOn(StepDisplay display)
    {
        long addOn = display.Step.StepType == StepType.VideoAnalyze
            ? SegmentationCreditPricing.AddOnMillis(display.Step.VideoAnalyzeConfigJson)
            : 0;

        return addOn == 0
            ? display
            : display with
            {
                Cost = new StepCostDistribution(
                    display.Cost.TypicalMillis + addOn,
                    display.Cost.P25Millis + addOn,
                    display.Cost.P75Millis + addOn)
            };
    }

    /// <summary>
    /// <paramref name="display"/> with the known-source add-on the hold carries (<see cref="SourceFootageCredits"/>), added to
    /// its low, typical and high like the segmentation add-on, so the shown band and the reserved figure agree.
    /// </summary>
    private static StepDisplay SourceFootageAddOn(StepDisplay display, long addOn) =>
        addOn <= 0
            ? display
            : display with
            {
                Cost = new StepCostDistribution(
                    display.Cost.TypicalMillis + addOn,
                    display.Cost.P25Millis + addOn,
                    display.Cost.P75Millis + addOn)
            };

    /// <summary>The known footage of the run's VideoAnalyze sources; empty on any failure (those steps hold today's figure).</summary>
    private async Task<IReadOnlyDictionary<int, IReadOnlyList<SourceFootage>>> LoadSourceFootageAsync(
        Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            return await SourceFootageCredits.ResolveAsync(db.ProjectFiles, db.MediaDerivatives, db.WorkflowDefinitions, steps, organizationId, ct, SourceFootageBrief.Current).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reading the source footage of a run for its cost estimate failed; those steps are estimated as before");
            return new Dictionary<int, IReadOnlyList<SourceFootage>>();
        }
    }

    /// <summary>One step's displayed distribution under the run's pixel factor (see <see cref="EstimateFrom"/>).</summary>
    private static StepCostDistribution ScaleCost(StepCostDistribution cost, WorkflowStep step, bool rendersOutput, double pixelFactor)
    {
        if (pixelFactor == 1.0)
        {
            return cost;
        }

        return step.StepType switch
        {
            StepType.VideoCompile or StepType.TimelineRender => new StepCostDistribution(
                Times(cost.TypicalMillis, pixelFactor), Times(cost.P25Millis, pixelFactor), Times(cost.P75Millis, pixelFactor)),
            StepType.Agent or StepType.Revise when pixelFactor > 1.0 && rendersOutput
                => cost with { P75Millis = Times(cost.P75Millis, pixelFactor) },
            _ => cost
        };
    }

    private static long Times(long millis, double factor) => (long)Math.Round(millis * factor, MidpointRounding.AwayFromZero);
}
