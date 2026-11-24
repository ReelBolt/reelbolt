using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.WorkflowEngine.Services.VideoGeneration;

/// <summary>
/// One take (clip) a video generation step wants to reserve budget for.
/// <para>
/// <see cref="Quote"/> is the vendor's OWN quote for this take when the price came from an oracle
/// (a provider that publishes no price table, priced per configuration by
/// <see cref="IVideoGenerationPriceOracle"/>), and <c>null</c> when it came from the provider's
/// published per-second table. A quoted take must be charged exactly
/// <see cref="VideoGenerationPriceQuote.TotalUsd"/>; the gate enforces that rather than trusting the
/// caller, because "the ledger records the vendor's exact figure" is an invariant of the component
/// that writes money. It is positional-optional so every existing construction
/// site keeps compiling and a table-priced take is byte-identical to what it always was.
/// </para>
/// </summary>
/// <param name="AvatarId">
/// The avatar THIS take shows when it differs from the rest of its request (a planned shot's own subjects,
/// docs/design/avatar-objects.md "Avatars and objects in planned shots"); null = the request's
/// <see cref="VideoGenerationReservationRequest.AvatarId"/>. Not part of any cap.
/// </param>
/// <param name="SubjectIds">The same for <see cref="VideoGenerationReservationRequest.SubjectIds"/>.</param>
public record VideoGenerationTakeRequest(
    string ClipId,
    string RequestHash,
    decimal CostEstimate,
    VideoGenerationPriceQuote? Quote = null,
    Guid? AvatarId = null,
    IReadOnlyList<Guid>? SubjectIds = null);

/// <summary>
/// What a reservation's figures are denominated in (issue #93 phase 5). <see cref="Credits"/> is
/// every priced vendor: 1 credit is <c>$0.006</c> of vendor cost, so a figure a vendor publishes in
/// dollars is converted by <c>Metering.CreditCurrency</c> before it reaches a cap or the ledger.
/// <see cref="GpuSeconds"/> is a self-hosted ComfyUI provider, whose "cost" is GPU time. The two are
/// never summed together: every cap, every daily total and every ledger query is per unit.
/// Appended-only; persisted on <c>external_generation_jobs.cost_unit</c> by lower-case name.
/// </summary>
public enum VideoGenerationCostUnit
{
    Credits,
    GpuSeconds
}

/// <summary>
/// Which caps a reservation is subject to. <see cref="Video"/>: the step cap and the project,
/// organization and global daily caps. <see cref="Audio"/> (AudioGenerate): the step cap only; every take's clip id
/// must be an audio clip id (<c>VideoBudgetAccounting.IsAudioClipId</c>) so its row is invisible to the video sums.
/// A video request carrying an audio clip id is refused for the same reason. <see cref="Image"/> (ImageGenerate): the
/// same, with image clip ids (<c>VideoBudgetAccounting.IsImageClipId</c>). Append-only.
/// </summary>
public enum VideoGenerationBudgetScope
{
    Video,
    Audio,

    /// <summary>
    /// A hosted VideoEnhance job: the step cap only, like <see cref="Audio"/>; every clip id must be an enhance clip id
    /// (<c>VideoBudgetAccounting.IsEnhanceClipId</c>) so its row is invisible to the video daily sums. Only the step's own credit
    /// hold limits it.
    /// </summary>
    Enhance,
    Image
}

/// <summary>
/// Every take of one video generation step, reserved together or not at all.
/// <see cref="StepResultId"/> is the <see cref="WorkflowStepResult"/> of the attempt making the
/// request; it is written on every newly inserted row and must not be <see cref="Guid.Empty"/>.
/// </summary>
/// <param name="Unit">
/// The unit every <see cref="VideoGenerationTakeRequest.CostEstimate"/> in <paramref name="Takes"/>
/// and <paramref name="StepCap"/> are denominated in — credits for every priced vendor, GPU-seconds
/// for a self-hosted ComfyUI provider. Defaults to <see cref="VideoGenerationCostUnit.Credits"/>.
/// In <see cref="VideoGenerationCostUnit.GpuSeconds"/>
/// the step cap is <paramref name="StepCap"/> read as GPU-SECONDS (the executor passes
/// <c>MaxGpuMinutes × 60</c>), the daily caps are <c>VideoGeneration:DailyGpuMinutes</c>/
/// <c>GlobalDailyGpuMinutes</c>, and only ledger rows of the same unit count toward them.
/// </param>
/// <param name="AvatarId">
/// The project avatar every take of this request shows (avatars phase 2), written on each newly inserted row's
/// <c>avatar_id</c> so the avatar's usage report can list it; null for a take without an avatar. Not part of any cap.
/// </param>
/// <param name="SubjectIds">
/// Every project avatar each take of this request shows, people and objects (a step that names several subjects),
/// written on each newly inserted row's <c>subject_ids_json</c>; null for every other take. Not part of any cap.
/// </param>
public record VideoGenerationReservationRequest(
    Guid ProjectId,
    Guid ExecutionId,
    Guid StepResultId,
    Guid ProviderId,
    decimal StepCap,
    IReadOnlyList<VideoGenerationTakeRequest> Takes,
    VideoGenerationCostUnit Unit = VideoGenerationCostUnit.Credits,
    VideoGenerationBudgetScope Scope = VideoGenerationBudgetScope.Video,
    Guid? AvatarId = null,
    IReadOnlyList<Guid>? SubjectIds = null);

/// <summary>
/// A take's ledger row after reservation. <see cref="Existing"/> is true when the row already
/// existed for the same (ExecutionId, RequestHash) in Reserved or Submitted state (crash-resume);
/// such a row was neither re-inserted nor re-charged.
/// </summary>
public record ReservedTake(
    Guid JobId,
    string ClipId,
    string RequestHash,
    decimal CostEstimate,
    ExternalGenerationJobStatus Status,
    bool Existing);

/// <summary>Which cap refused a reservation.</summary>
public enum BudgetCapKind
{
    /// <summary>The step's own limit (<c>MaxSpendCredits</c> / <c>MaxGpuMinutes</c>); it has no reset.</summary>
    StepLimit,
    ProjectDaily,
    /// <summary>The user's OWN daily video limit (<c>user_video_spend_limits</c>); the sentence names it and where to change it.</summary>
    UserDaily,
    GlobalDaily
}

/// <summary>
/// The facts behind a <c>BUDGET_EXCEEDED</c> refusal, in the unit of the request (credits, or
/// GPU-seconds when <paramref name="Unit"/> is <see cref="VideoGenerationCostUnit.GpuSeconds"/>):
/// which cap refused, its value, what is spent against it today, what the request needs, and the
/// instant a daily cap resets (the next 00:00 UTC; <c>null</c> for <see cref="BudgetCapKind.StepLimit"/>).
/// </summary>
public record BudgetRefusal(
    BudgetCapKind Cap,
    VideoGenerationCostUnit Unit,
    decimal CapValue,
    decimal SpentToday,
    decimal Estimated,
    DateTime? ResetsAtUtc);

/// <summary>
/// Outcome of <see cref="IVideoGenerationBudgetGate.ReserveAsync"/>. When denied,
/// <see cref="DenyReason"/> starts <c>CONFIG_INVALID:</c>, <c>BUDGET_EXCEEDED:</c> or <c>PLAN_NOT_ENTITLED:</c>, nothing
/// was written, and <see cref="Takes"/> is empty. <see cref="Estimated"/> covers new takes only.
/// <see cref="Refusal"/> carries the structured facts of a <c>BUDGET_EXCEEDED</c> denial (null otherwise).
/// </summary>
public record VideoGenerationReservation(
    bool Allowed,
    decimal Estimated,
    decimal ProjectSpentToday,
    decimal GlobalSpentToday,
    string? DenyReason,
    IReadOnlyList<ReservedTake> Takes,
    BudgetRefusal? Refusal = null);

/// <summary>
/// Budget gate for video generation steps. Checking the ledger and writing the step's
/// reservations are ONE atomic operation, so concurrent steps cannot all pass a check that
/// only one of them fits under (the TOCTOU the security review found).
/// </summary>
public interface IVideoGenerationBudgetGate
{
    /// <summary>
    /// Atomically, in order:
    /// (1) empty Takes, any CostEstimate &lt;= 0, StepCap &lt;= 0, or an empty StepResultId → CONFIG_INVALID;
    /// (1b) a take carrying a non-null <see cref="VideoGenerationTakeRequest.Quote"/> whose
    ///     <see cref="VideoGenerationTakeRequest.CostEstimate"/> is not that quote's own
    ///     <see cref="VideoGenerationPriceQuote.TotalUsd"/> → CONFIG_INVALID with ZERO rows written.
    ///     A quoted take's price is the vendor's figure; a caller that arrives with a different
    ///     number is refused rather than silently charged its own arithmetic, so no rate-derived or
    ///     drifted amount can ever reach the ledger for an oracle-priced model. A take with a null
    ///     Quote is unaffected, which is why the table-priced (MiniMax) path is byte-identical;
    /// (2) a take whose (ExecutionId, RequestHash) already has a Reserved/Submitted row is returned
    ///     <c>Existing = true</c>, not re-inserted and not re-charged;
    /// (2b) except a Reserved row with no RemoteJobId that an EARLIER attempt of the run left for the same
    ///     step (its step result superseded by a Retry, or failed on a run since resumed; never the
    ///     requesting step result's own row): it is marked <see cref="ExternalGenerationJobStatus.Abandoned"/>
    ///     and its take is reserved again as a new row (a fresh idempotency key). It may have reached the
    ///     vendor, so it KEEPS counting at its reserved amount toward the step cap (every abandoned row of
    ///     the step's takes) and, for its UTC day, the daily caps; only the user is never billed for it. A
    ///     take is abandoned at most <c>VideoGeneration:MaxAbandonsPerTake</c> times per execution (default 1);
    ///     past that it is returned Existing and ends RESERVATION_ORPHANED. Saved only with the new rows, so
    ///     a denial abandons nothing;
    /// (3) the step total over ALL takes &gt; StepCap → BUDGET_EXCEEDED;
    /// (4) the project's spend today + new takes &gt; the optional operator per-project cap (<c>VideoGeneration:DailyBudgetCredits</c>,
    ///     unset by default = no cap) → BUDGET_EXCEEDED;
    /// (4a) the owner's spend today + new takes &gt; the owner's OWN daily limit (<c>user_video_spend_limits</c>, set in the
    ///     user's settings; the owner is the run's <c>InitiatedByUserId</c>; no row, a null limit or no initiating user = no
    ///     limit) → BUDGET_EXCEEDED naming that limit and Settings &gt; Usage; counted in the same transaction;
    /// (4b) the organization's spend today + new takes &gt; its plan's daily budget → BUDGET_EXCEEDED
    ///     (a plan budget of 0 → PLAN_NOT_ENTITLED);
    /// (5) the global spend today + new takes &gt; the operator's safety ceiling (<c>VideoGeneration:GlobalDailyBudgetCredits</c>, far above any normal day) → BUDGET_EXCEEDED;
    /// (4)/(4a)/(4b)/(5) are skipped for <see cref="VideoGenerationBudgetScope.Audio"/>, <see cref="VideoGenerationBudgetScope.Enhance"/> and <see cref="VideoGenerationBudgetScope.Image"/>, whose rows never count toward them;
    /// (6) otherwise one Reserved <see cref="ExternalGenerationJob"/> is inserted per new take, carrying
    ///     the request's StepResultId (an existing row keeps its own), and all takes are returned in
    ///     request order.
    /// </summary>
    Task<VideoGenerationReservation> ReserveAsync(VideoGenerationReservationRequest request, CancellationToken ct);
}
