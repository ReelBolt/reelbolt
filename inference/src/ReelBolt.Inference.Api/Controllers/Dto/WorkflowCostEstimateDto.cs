namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// B7's answer to <c>GET .../workflows/{id}/estimate</c>: what a run is expected to cost, in whole
/// credits (millicredits / 1000, the same unit <c>GET /api/v1/billing/summary</c> reports).
/// </summary>
/// <param name="BillingEnabled">False on a self-host install: nothing is metered, estimated or gated.</param>
/// <param name="HasHistory">
/// Whether the organization has recorded step usage to estimate from. False means every figure came
/// from B0's per-step-type defaults, which the UI should say rather than presenting as measured.
/// </param>
/// <param name="LowCredits">
/// The bottom of the usual range of the variable part of the run (fixed-price steps excluded): the
/// p25 of past runs, or the per-step combination's typical − 0.674σ.
/// </param>
/// <param name="HighCredits">The top of that usual range (p75, or typical + 0.674σ).</param>
/// <param name="RequiredCredits">
/// The credits the run must be able to afford to start: the sum of what the engine will hold before
/// each step. Exactly what <c>POST execute</c> compares the balance against, so a run the UI shows as
/// affordable is not refused by the engine's own gate on its first step. A step with this
/// organization's own history holds its p90; a step without it (a cold start) holds the larger of its
/// old per-step default and its share of the cold steps' low edge, so on a first run this is at
/// least <paramref name="LowCredits"/> plus any fixed-price holds, and never less than before the cold-start hold re-plan.
/// </param>
/// <param name="BalanceCredits">The organization's available credits (balance minus open holds); null when billing is off.</param>
/// <param name="Affordable">Whether the balance covers <paramref name="RequiredCredits"/>.</param>
/// <param name="TypicalCredits">The typical cost of the variable part ("≈ X credits").</param>
/// <param name="FixedMaxCredits">The fixed-price ceiling on top of the range ("plus up to F"), 0 when there is none.</param>
/// <param name="RangeBasis"><c>PastRuns</c>, <c>StepHistory</c> or <c>FirstRun</c>: where the range came from.</param>
/// <param name="PastRunCount">Past successful runs of this workflow found in the window.</param>
/// <param name="SpreadReasons">
/// Why the range is as wide as it is, largest contribution first: <c>review_loop_repeats</c>,
/// <c>footage_length</c>, <c>foreach_items_unknown</c>, <c>own_provider_free</c>,
/// <c>video_generation_max_clips</c>, <c>video_generation_in_review_loop</c>.
/// </param>
/// <param name="VideoSpendLimit">
/// The caller's OWN daily video limit, what their runs spent today and the room left (docs/design/video-spend-limit.md); set
/// only when the workflow generates video, null otherwise and from an older server.
/// </param>
public record WorkflowCostEstimateResponse(
    bool BillingEnabled,
    bool HasHistory,
    double LowCredits,
    double HighCredits,
    double RequiredCredits,
    double? BalanceCredits,
    bool Affordable,
    IReadOnlyList<WorkflowStepCostEstimateResponse> Steps,
    double TypicalCredits,
    double FixedMaxCredits,
    string RangeBasis,
    int PastRunCount,
    IReadOnlyList<string> SpreadReasons,
    ReelBolt.Inference.Api.Services.Billing.UserVideoLimitView? VideoSpendLimit = null);

/// <summary>
/// One step of a run's estimate.
/// </summary>
/// <param name="LowCredits">This step's p25 for one execution (0 for a fixed-price step).</param>
/// <param name="HighCredits">This step's p75 for one execution (0 for a fixed-price step).</param>
/// <param name="RequiredCredits">What the engine holds before the step runs: its history p90, or the larger of its old default and its share of the cold-start low edge.</param>
/// <param name="Basis">
/// Where the shown numbers came from: <c>History</c> (this org's runs of this exact agent),
/// <c>AgentTypeHistory</c> (this org's runs of this kind of agent), <c>Default</c> (typical figures
/// for this kind of step, the first-run path), <c>OwnProvider</c> (runs on the org's own provider
/// key, not charged), <c>NotCharged</c> or <c>VideoGeneration</c> (a fixed price, shown as "up to").
/// </param>
/// <param name="TypicalCredits">This step's typical cost for one execution.</param>
/// <param name="ExpectedRuns">How many times the step is expected to run (above 1 inside a review loop).</param>
/// <param name="FixedMaxCredits">A fixed-price step's price for one execution; 0 otherwise.</param>
public record WorkflowStepCostEstimateResponse(
    int StepOrder,
    string StepType,
    Guid? AgentDefinitionId,
    string? Label,
    double LowCredits,
    double HighCredits,
    double RequiredCredits,
    string Basis,
    double TypicalCredits,
    double ExpectedRuns,
    double FixedMaxCredits);

/// <summary>
/// The body of <c>POST .../workflows/{id}/estimate</c> (docs/design/app-inputs.md 11.4): the run brief (its app inputs pick
/// the branch and the bound values), and optionally the resolution and the chosen clips, which otherwise come from the brief.
/// </summary>
/// <param name="Brief">The run brief as the run page would send it to execute.</param>
/// <param name="OutputResolution"><c>Auto</c>, <c>720p</c>, <c>1080p</c>, <c>2160p</c>; null = the brief's.</param>
/// <param name="VideoFileIds">The clips the run page chose; null = the brief's video files.</param>
public sealed record EstimateWorkflowRequest(
    ReelBolt.Shared.Briefs.RunBrief? Brief = null,
    string? OutputResolution = null,
    Guid[]? VideoFileIds = null);
