using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>Bound from the <c>Billing</c> section (<c>Billing__*</c> env vars), the same keys the engine's credit gate reads.</summary>
public sealed class NonWorkflowChargeOptions
{
    public const string SectionName = "Billing";

    /// <summary>False (self-host default): every charge path allows and writes nothing.</summary>
    public bool Enabled { get; set; }

    /// <summary>A hold may be up to this share of its estimate short of the available balance (policy: 20 %).</summary>
    public int OverdraftTolerancePercent { get; set; } = 20;

    /// <summary>The overdraft never reaches below minus this many credits (policy: 100).</summary>
    public long OverdraftFloorCredits { get; set; } = 100;

    /// <summary><c>Billing:NonWorkflow:*</c>.</summary>
    public NonWorkflowHoldOptions NonWorkflow { get; set; } = new();
}

/// <summary><c>Billing:NonWorkflow</c>.</summary>
public sealed class NonWorkflowHoldOptions
{
    /// <summary>
    /// Credits a Platform call is estimated at (and so held) when the platform has no price for it or its quote fails:
    /// never 0, so an unpriced call cannot run without a hold; it then settles at least this hold.
    /// </summary>
    public long UnpricedEstimateCredits { get; set; } = 10;

    /// <summary>
    /// The least credits held before a timeline AI proposal; the hold is sized from the request when that is more
    /// (its real cost is settled afterwards).
    /// </summary>
    public long TimelineProposalHoldCredits { get; set; } = 5;

    /// <summary>
    /// The least credits held before the post text for a finished video is written (<c>Billing:NonWorkflow:PostTextHoldCredits</c>);
    /// the hold is sized from the request when that is more, and settled at what the call metered.
    /// </summary>
    public long PostTextHoldCredits { get; set; } = 5;

    /// <summary>
    /// Credits held (allowance first, then credits) before each assistant chat turn and each MCP tool call. A call is
    /// refused when the allowance left plus the available balance cannot cover it.
    /// </summary>
    public long AssistantTurnHoldCredits { get; set; } = 5;

    /// <summary>
    /// The least a turn must be able to hold (allowance left plus available balance) to be admitted; null means
    /// <see cref="AssistantTurnHoldCredits"/>. A turn that cannot hold this much is refused, never run on less.
    /// </summary>
    public long? AssistantMinTurnCredits { get; set; }

    /// <summary>The most credits one assistant turn or MCP tool call may meter before it is cut off.</summary>
    public long AssistantTurnCeilingCredits { get; set; } = 100;

    /// <summary>Open charged turns (assistant and MCP together) an organization may have at once.</summary>
    public int MaxConcurrentTurnsPerOrg { get; set; } = 4;
}

/// <summary>Stable codes on <see cref="ChargeHold"/>, <see cref="TurnHold"/> and <see cref="TurnSettlement"/>.</summary>
public static class NonWorkflowChargeCodes
{
    public const string BillingDisabled = "BILLING_DISABLED";
    public const string NoPlatformCost = "NO_PLATFORM_COST";
    public const string Resumed = "RESUMED";
    public const string InsufficientCredits = CreditLedgerErrors.InsufficientCredits;
    public const string GateUnavailable = "GATE_UNAVAILABLE";

    /// <summary>Billing is on and the call has no organization to charge: refused, never run uncharged.</summary>
    public const string NoOrganization = "NO_ORGANIZATION";

    /// <summary>The turn reached its per-turn ceiling (<c>Billing:NonWorkflow:AssistantTurnCeilingCredits</c>).</summary>
    public const string TurnLimit = "TURN_LIMIT";

    /// <summary>The organization already has <c>Billing:NonWorkflow:MaxConcurrentTurnsPerOrg</c> charged turns open.</summary>
    public const string TurnConcurrencyLimit = "TURN_CONCURRENCY_LIMIT";

    /// <summary>
    /// The organization's plan explicitly includes unlimited assistant use (<c>assistantUnlimited</c>): the turn holds
    /// zero credits and charges nothing, but it still takes a hold row, so its steps are pre-claimed, it stops at
    /// the per-turn ceiling and it counts against the in-flight cap. Unlimited exempts the allowance only.
    /// </summary>
    public const string UnlimitedAllowance = "UNLIMITED_ALLOWANCE";

    /// <summary>
    /// the only codes an assistant/MCP turn may be admitted with and no hold row: billing off, and (for
    /// a hold shaped before T-026) the explicit unlimited entitlement. Every other admitted turn holds (a BYO or Runner
    /// one too), or is charged at settlement anyway.
    /// </summary>
    public static bool IsAllowedWithoutHold(string? code) => code is BillingDisabled or UnlimitedAllowance;
}

/// <summary>
/// A hold taken before an editor operation or a timeline proposal. <see cref="BalanceMillis"/> is what was
/// available (balance minus open holds) when it was decided.
/// </summary>
public sealed record ChargeHold(bool Allowed, Guid? ReservationId, long RequiredMillis, long BalanceMillis, string? Code)
{
    /// <summary>The organization the hold belongs to (needed to settle or release it).</summary>
    public Guid OrganizationId { get; init; }

    /// <summary>What the hold was taken for (logged when a settlement is capped).</summary>
    public UsageSource Source { get; init; }

    /// <summary>The caller's request id (logged when a settlement is capped).</summary>
    public Guid RequestId { get; init; }
}

/// <summary>
/// The hold an assistant chat turn or MCP tool call takes before it runs: <see cref="AllowanceHeldMillis"/> of the
/// monthly allowance and, for the part the allowance cannot cover, <see cref="CreditHeldMillis"/> reserved on the
/// ledger. <see cref="BalanceMillis"/> is the available balance when it was decided. No <see cref="HoldId"/> means
/// nothing was held (billing off, or a refusal); an unlimited plan holds a row of zero.
/// </summary>
public sealed record TurnHold(bool Allowed, Guid? HoldId, long RequiredMillis, long BalanceMillis, string? Code)
{
    public Guid? OrganizationId { get; init; }
    public UsageSource Source { get; init; }
    public Guid RequestId { get; init; }

    /// <summary>The plan's included allowance when the hold was taken (null = unlimited).</summary>
    public long? IncludedMillis { get; init; }

    public DateTime PeriodStart { get; init; }
    public DateTime PeriodEnd { get; init; }
    public long AllowanceHeldMillis { get; init; }
    public long CreditHeldMillis { get; init; }
}

/// <summary>How a turn closed: allowance consumed, credits charged, and metered credits that were not charged.</summary>
public sealed record TurnSettlement(bool Ok, long AllowanceMillis, long ChargedMillis, long UnchargedMillis, string? Code);

/// <summary>
/// Whether a running turn may take its next paid step: <see cref="Continue"/> false with <see cref="Code"/>
/// <see cref="NonWorkflowChargeCodes.TurnLimit"/> (its ceiling) or <see cref="NonWorkflowChargeCodes.InsufficientCredits"/>
/// (allowance and credits down to the overdraft floor are spent). <see cref="LimitMillis"/> is where it stops.
/// </summary>
public sealed record TurnCheck(bool Continue, string? Code, long LimitMillis);

/// <summary>
/// One paid step a running turn is about to take, pre-claimed before it runs: what the turn has
/// <see cref="MeteredMillis">metered</see> so far, what its other in-flight steps have
/// <see cref="ClaimedMillis">claimed</see> and not yet landed, and this step's worst-case cost
/// (<see cref="StepMillis"/>: a model round-trip's estimated input plus its output ceiling, or a tool's quote).
/// <see cref="AdmitUnderConcurrencyCap"/> asks for the turn to be counted against
/// <c>Billing:NonWorkflow:MaxConcurrentTurnsPerOrg</c> from this step on (a BYO-chat turn's first Platform-billed step).
/// </summary>
public sealed record TurnStepClaim(long MeteredMillis, long ClaimedMillis = 0, long StepMillis = 0, bool AdmitUnderConcurrencyCap = false);

/// <summary>Charges non-workflow usage (decision 9; docs/design/model-catalog.md §7.3).</summary>
public interface INonWorkflowChargeService
{
    /// <summary>The options in force (the hold sizes live here).</summary>
    NonWorkflowChargeOptions Options { get; }

    /// <summary>
    /// Millicredits one unit of <paramref name="request"/> costs (its quantity is ignored), or the configured unpriced
    /// default (ten credits) when the platform has no price for it; 0 for BYO and Runner, and when billing is off.
    /// </summary>
    Task<long> EstimateOneUnitAsync(PriceRequest request, CancellationToken ct = default);

    /// <summary>
    /// Millicredits the organization can spend right now (balance minus open holds), or <c>null</c> when billing is
    /// off or the ledger cannot be read. Used to tell the editor whether a priced tool is affordable.
    /// </summary>
    Task<long?> GetAvailableMillisAsync(Guid organizationId, CancellationToken ct = default) => Task.FromResult<long?>(null);

    /// <summary>
    /// Millicredits <paramref name="request"/> costs at its quantity; the configured unpriced default
    /// (<c>Billing:NonWorkflow:UnpricedEstimateCredits</c>) when the platform has no price for it or its quote fails;
    /// 0 only for BYO and Runner, and when billing is off (callers apply their own floor).
    /// </summary>
    Task<long> EstimateAsync(PriceRequest request, CancellationToken ct = default);

    /// <summary>Reserves <paramref name="estimateMillis"/> before a charged call, within the overdraft tolerance.</summary>
    Task<ChargeHold> BeginHoldAsync(Guid organizationId, UsageSource source, long estimateMillis, Guid requestId, CreditLedgerContext context, CancellationToken ct = default);

    /// <summary>
    /// Closes a hold at what the call actually cost, capped at the hold plus its overdraft headroom; releases it
    /// when that is 0.
    /// </summary>
    Task<bool> SettleAsync(ChargeHold hold, long actualMillis, CancellationToken ct = default);

    /// <summary>
    /// T-013 (F1): closes a hold at what the call actually cost with NO ceiling, for a size-priced picture job the vendor
    /// has already answered (its price follows the delivered picture, so a cap would bill below cost). Every other
    /// non-workflow charge keeps <see cref="SettleAsync"/>'s cap.
    /// </summary>
    Task<bool> SettleUncappedAsync(ChargeHold hold, long actualMillis, CancellationToken ct = default);

    /// <summary>Drops a hold without charging (the call failed).</summary>
    Task<bool> ReleaseAsync(ChargeHold hold, CancellationToken ct = default);

    /// <summary>
    /// T-026 (finding 5): charges <paramref name="actualMillis"/> for a call that already ran and metered Platform usage
    /// without a hold, keyed on its request id (a retry charges once). No balance check: the usage happened.
    /// </summary>
    Task<bool> ChargeUnheldAsync(Guid organizationId, UsageSource source, long actualMillis, Guid requestId, CreditLedgerContext context, CancellationToken ct = default);

    /// <summary>
    /// Holds <c>Billing:NonWorkflow:AssistantTurnHoldCredits</c> for an assistant turn or MCP tool call, from the
    /// allowance first and then the balance, under the organization's ledger lock. Refused with
    /// <see cref="NonWorkflowChargeCodes.InsufficientCredits"/> when the allowance left plus the available balance
    /// cannot cover <c>AssistantMinTurnCredits</c> (default the hold), and with
    /// <see cref="NonWorkflowChargeCodes.TurnConcurrencyLimit"/> when the organization already has
    /// <c>MaxConcurrentTurnsPerOrg</c> turns open (a non-Platform <paramref name="billingMode"/> is exempt from that
    /// cap). Sweeps the organization's holds left open past <see cref="NonWorkflowChargeService.StaleTurnHoldAge"/>
    /// first. A null organization is refused while billing is on.
    /// </summary>
    Task<TurnHold> BeginTurnAsync(
        Guid? organizationId, UsageSource source, Guid requestId, UsageBillingMode billingMode = UsageBillingMode.Platform, CancellationToken ct = default);

    /// <summary>
    /// Before each paid step of a running turn (a model round-trip, a tool call): whether it may go on having metered
    /// <paramref name="meteredMillis"/> so far.
    /// </summary>
    Task<TurnCheck> CheckTurnAsync(TurnHold turn, long meteredMillis, CancellationToken ct = default);

    /// <summary>
    /// Pre-claims one paid step of a running turn under the organization's ledger lock: refused when
    /// metered + claimed + this step would pass <c>min(turn ceiling, allowance free + available + headroom - what other
    /// open turns metered and claimed beyond their holds)</c>; otherwise the turn's in-flight total (metered + claims) is
    /// recorded on its open hold, where other turns' checks see it.
    /// </summary>
    Task<TurnCheck> ClaimStepAsync(TurnHold turn, TurnStepClaim step, CancellationToken ct = default);

    /// <summary>
    /// A claimed step's usage landed: the turn's recorded in-flight total becomes <paramref name="meteredMillis"/> plus
    /// what is still <paramref name="claimedMillis">claimed</paramref> (the claim is replaced by the metered actual).
    /// </summary>
    Task LandStepAsync(TurnHold turn, long meteredMillis, long claimedMillis, CancellationToken ct = default);

    /// <summary>
    /// Closes a turn at what it metered: the allowance first (consumed, not debited), then the full remainder in
    /// credits, even below the overdraft floor (the in-turn cut-off of <see cref="CheckTurnAsync"/> bounds that to the
    /// last round-trip; the overshoot is counted). A swept turn that settles late is charged the same way. Releases
    /// when the turn metered nothing.
    /// </summary>
    Task<TurnSettlement> SettleTurnAsync(TurnHold turn, long meteredMillis, CancellationToken ct = default);

    /// <summary>Drops a turn's hold without consuming or charging anything.</summary>
    Task<bool> ReleaseTurnAsync(TurnHold turn, CancellationToken ct = default);

    /// <summary>
    /// T-026 (finding 1): a turn whose settlement failed records what it metered on its still-open hold (at least what
    /// was recorded there already), so the stale sweep charges it instead of releasing it. Best effort; never throws.
    /// </summary>
    Task RecordUnsettledTurnAsync(TurnHold turn, long meteredMillis, CancellationToken ct = default);

    /// <summary>
    /// T-110 (L1): an editor-operation, subject or proposal hold whose close failed twice records what it metered on
    /// its still-open hold, so the stale sweep (<see cref="NonWorkflowChargeService.SweepStaleHoldsAsync"/>) charges it
    /// instead of releasing it; a hold the sweep already closed is charged the rest at once. Best effort; never throws.
    /// </summary>
    Task RecordUnsettledHoldAsync(ChargeHold hold, long meteredMillis, CancellationToken ct = default);
}

/// <summary>Metrics of the non-workflow charge path, on the Inference API's billing meter.</summary>
public static class NonWorkflowChargeDiagnostics
{
    private static readonly Meter Meter = new(SpendReportDiagnostics.MeterName);

    /// <summary>
    /// One per settlement that went past what it was admitted with: an editor hold capped at the hold plus its
    /// headroom, or an assistant/MCP turn charged below minus the overdraft headroom (its last round-trip's
    /// overshoot). Tagged with the source.
    /// </summary>
    public static readonly Counter<long> SettleCapped = Meter.CreateCounter<long>(
        "reelbolt.billing.nonworkflow_settle_capped", "settlements",
        "Non-workflow settlements capped at the hold plus the overdraft headroom, or turns charged past it, tagged with source");

    /// <summary>T-110 (L1): one per editor-operation, subject or proposal hold whose close failed twice (left for the sweep).</summary>
    public static readonly Counter<long> HoldCloseFailed = Meter.CreateCounter<long>(
        "reelbolt.billing.nonworkflow_hold_close_failed", "holds",
        "Editor, subject and proposal holds whose settle or release failed twice and were left for the stale sweep, tagged with source");

    /// <summary>T-110 (L1): editor-operation, subject and proposal holds the stale sweep closed.</summary>
    public static readonly Counter<long> HoldsSwept = Meter.CreateCounter<long>(
        "reelbolt.billing.nonworkflow_holds_swept", "holds",
        "Editor, subject and proposal holds left open past the stale age and closed by the sweep");
}

/// <summary>
/// <see cref="INonWorkflowChargeService"/> over <see cref="CreditLedger"/> on a fresh short-lived
/// <see cref="InferenceApiDbContext"/> per operation, with the engine credit gate's overdraft headroom
/// (<c>min(hold × 20 %, 100 credits)</c>). A no-op when <c>Billing:Enabled</c> is false. It fails closed: a
/// ledger that cannot be read refuses with <see cref="NonWorkflowChargeCodes.GateUnavailable"/>.
/// <para>
/// Assistant and MCP calls share the monthly allowance through <see cref="AssistantAllowanceHold"/> rows, the
/// authoritative counter: each turn's hold is decided, and each settlement applied, inside one transaction holding
/// the organization's ledger advisory lock, so concurrent turns are serialized against the same allowance left and
/// the same balance, never against the asynchronously flushed <c>usage_events</c>.
/// </para>
/// </summary>
public sealed class NonWorkflowChargeService : INonWorkflowChargeService
{
    private const long MillisPerCredit = 1000;
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>The hold for an editor operation the platform has no price for.</summary>
    public const long UnpricedEstimateMillis = 10 * MillisPerCredit;

    /// <summary>
    /// How long an assistant turn hold may stay open before the next turn of its organization sweeps it. At least
    /// twice the turn wall clock (<c>Assistant:MaxTurnSeconds</c>, see <see cref="ValidateTurnClock"/>), so the sweep
    /// only ever takes turns whose process died.
    /// </summary>
    public static readonly TimeSpan StaleTurnHoldAge = TimeSpan.FromHours(1);

    /// <summary>
    /// T-110 (L1): the margin past the call's own bound after which an editor-operation, subject or proposal hold still
    /// open is taken for abandoned. The editor operation's vendor wait is bounded at 90 s
    /// (<c>EditImageOperationsController.DefaultOperationTimeout</c>); a subject cut-out and a proposal are bounded by
    /// their provider's HTTP timeout (100 s by default). 15 minutes is ten times the longest of those, so only a dead
    /// process's or a failed close's hold is swept; and a call that does outlive it is still charged by its late close.
    /// </summary>
    public static readonly TimeSpan StaleHoldMargin = TimeSpan.FromMinutes(15);

    /// <summary>How long an editor-operation, subject or proposal hold may stay open before it is swept.</summary>
    public static readonly TimeSpan StaleHoldAge = Controllers.EditImageOperationsController.DefaultOperationTimeout + StaleHoldMargin;

    /// <summary>The most stale holds one sweep closes (the rest go at the next one).</summary>
    private const int MaxHoldsPerSweep = 50;

    private readonly IServiceScopeFactory _scopes;
    private readonly IAssistantAllowanceService _allowance;
    private readonly IModelPriceService? _prices;
    private readonly TimeProvider _time;
    private readonly ILogger<NonWorkflowChargeService> _logger;

    public NonWorkflowChargeService(
        IServiceScopeFactory scopes,
        IAssistantAllowanceService allowance,
        IOptions<NonWorkflowChargeOptions> options,
        ILogger<NonWorkflowChargeService> logger,
        IModelPriceService? prices = null,
        TimeProvider? time = null,
        IConfiguration? configuration = null)
    {
        _scopes = scopes;
        _allowance = allowance;
        Options = options.Value;
        _logger = logger;
        _prices = prices;
        _time = time ?? TimeProvider.System;
        ValidateTurnClock(TimeSpan.FromSeconds(Services.Assistant.AssistantChatLimits.MaxTurnSeconds(configuration)));
    }

    public NonWorkflowChargeOptions Options { get; }

    /// <summary>
    /// Fails fast when the turn wall clock (<c>Assistant:MaxTurnSeconds</c>) is more than half of
    /// <see cref="StaleTurnHoldAge"/>: the sweep would then release turns that are still allowed to run.
    /// </summary>
    public static void ValidateTurnClock(TimeSpan maxTurn)
    {
        if (TurnClockProblem(maxTurn) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
    }

    /// <summary>Why <paramref name="maxTurn"/> is not an allowed turn wall clock, or null when it is.</summary>
    public static string? TurnClockProblem(TimeSpan maxTurn) => maxTurn * 2 > StaleTurnHoldAge
        ? $"{Services.Assistant.AssistantChatLimits.MaxTurnSecondsKey} is {maxTurn.TotalSeconds:0} s; it must be at most half of the "
          + $"stale turn-hold age ({StaleTurnHoldAge.TotalSeconds:0} s) so the stale sweep never releases a running turn."
        : null;

    private static string ReserveKey(UsageSource source, Guid requestId) => $"nwc-reserve:{source}:{requestId:N}";

    private static string SettleKey(Guid reservationId) => $"nwc-settle:{reservationId:N}";

    private static string ReleaseKey(Guid reservationId) => $"nwc-release:{reservationId:N}";

    private static string TurnKey(UsageSource source, Guid requestId) => $"nwa-hold:{source}:{requestId:N}";

    private static string OverageKey(Guid holdId) => $"nwc-overage:{holdId:N}";

    /// <summary>The reservation a swept turn's late settle charges through (never the released reservation's key).</summary>
    private static string LateSettleKey(Guid holdId) => $"nwc-late:{holdId:N}";

    /// <summary>The prefix of an editor-operation, subject or proposal hold's row key; no turn's key has it.</summary>
    internal const string HoldRowPrefix = "nwc-hold:";

    /// <summary>The row of an editor-operation, subject or proposal hold, keyed on its reservation.</summary>
    private static string HoldRowKey(Guid reservationId) => $"{HoldRowPrefix}{reservationId:N}";

    /// <summary>The key the stale sweep closes a hold's reservation with (never the normal settle's or release's).</summary>
    private static string SweepKey(Guid reservationId) => $"nwc-swept:{reservationId:N}";

    private long TurnHoldMillis => Math.Max(1, Options.NonWorkflow.AssistantTurnHoldCredits) * MillisPerCredit;

    /// <summary>The least a turn must be able to hold to be admitted (never more than the hold itself).</summary>
    private long MinTurnMillis
        => Math.Min(TurnHoldMillis, Math.Max(1, Options.NonWorkflow.AssistantMinTurnCredits ?? Options.NonWorkflow.AssistantTurnHoldCredits) * MillisPerCredit);

    private long TurnCeilingMillis => Math.Max(1, Options.NonWorkflow.AssistantTurnCeilingCredits) * MillisPerCredit;

    /// <summary>The credit gate's overdraft headroom on a hold: <c>min(hold × tolerance %, floor credits)</c>.</summary>
    private long Headroom(long holdMillis)
        => Math.Max(0, Math.Min(holdMillis * Options.OverdraftTolerancePercent / 100, Options.OverdraftFloorCredits * MillisPerCredit));

    /// <summary>
    /// what an unpriced (or unquotable) Platform call is estimated at, <c>Billing:NonWorkflow:UnpricedEstimateCredits</c>
    /// (default <see cref="UnpricedEstimateMillis"/>), never below one credit.
    /// </summary>
    private long UnpricedMillis => Math.Max(1, Options.NonWorkflow.UnpricedEstimateCredits) * MillisPerCredit;

    /// <inheritdoc />
    public async Task<long?> GetAvailableMillisAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (!Options.Enabled) return null;
        try
        {
            // A plain single-row read of the credit account: an estimate or an affordability check must never queue behind
            // (or hold up) a charge, so this does NOT take the organization's ledger advisory lock. Only the paths that
            // write (holds, settlements) lock; the hold re-checks the balance under the lock, so a stale read here can
            // only make an estimate slightly optimistic, never overspend.
            using IServiceScope scope = _scopes.CreateScope();
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            return (await new CreditLedger(db, _time).GetBalanceAsync(organizationId, ct).ConfigureAwait(false)).AvailableMillis;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the balance of organization {OrganizationId}", organizationId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<long> EstimateOneUnitAsync(PriceRequest request, CancellationToken ct = default)
    {
        if (!Options.Enabled || request.BillingMode != UsageBillingMode.Platform) return 0;
        if (_prices is null) return UnpricedMillis;

        try
        {
            PriceQuote quote = await _prices.PriceAsync(request with { Quantity = 1m }, ct).ConfigureAwait(false);
            return quote.CreditsMillis > 0 ? quote.CreditsMillis : UnpricedMillis;
        }
        catch (UnpricedUsageException)
        {
            return UnpricedMillis;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Pricing is only the size of the hold; the settlement uses what metering actually recorded.
            _logger.LogWarning(ex, "Estimating {Unit} for {Kind}/{Model} failed; holding the unpriced default", request.Unit, request.Kind, request.ModelCode);
            return UnpricedMillis;
        }
    }

    /// <inheritdoc />
    public async Task<long> EstimateAsync(PriceRequest request, CancellationToken ct = default)
    {
        // 0 only when nothing is charged (billing off, BYO, Runner). A Platform call the platform cannot price, or whose
        // quote throws, holds the unpriced default instead: a 0 estimate would let it run with no hold.
        if (!Options.Enabled || request.BillingMode != UsageBillingMode.Platform) return 0;
        if (_prices is null) return UnpricedMillis;

        try
        {
            PriceQuote quote = await _prices.PriceAsync(request, ct).ConfigureAwait(false);
            return Math.Max(0, quote.CreditsMillis);
        }
        catch (UnpricedUsageException)
        {
            return UnpricedMillis;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Estimating {Unit} for {Kind}/{Model} failed; holding the unpriced default", request.Unit, request.Kind, request.ModelCode);
            return UnpricedMillis;
        }
    }

    /// <inheritdoc />
    public async Task<ChargeHold> BeginHoldAsync(
        Guid organizationId, UsageSource source, long estimateMillis, Guid requestId, CreditLedgerContext context, CancellationToken ct = default)
    {
        if (!Options.Enabled)
        {
            return new ChargeHold(true, null, 0, 0, NonWorkflowChargeCodes.BillingDisabled) { OrganizationId = organizationId, Source = source, RequestId = requestId };
        }

        if (estimateMillis <= 0)
        {
            return new ChargeHold(true, null, 0, 0, NonWorkflowChargeCodes.NoPlatformCost) { OrganizationId = organizationId, Source = source, RequestId = requestId };
        }

        // T-110 (L1): holds of this organization left open past StaleHoldAge are closed first (best effort).
        await TrySweepStaleHoldsAsync(organizationId, ct).ConfigureAwait(false);

        try
        {
            // The reservation and its hold row are written in one transaction under the organization's ledger lock, so
            // the stale sweep sees every open hold, and a late close finds what the sweep did.
            CreditLedgerResult r = await LockedAsync(organizationId, async (db, ledger) =>
            {
                CreditLedgerResult reserve = await ledger.ReserveAsync(
                    organizationId, estimateMillis, ReserveKey(source, requestId), context,
                    minAvailableAfterMillis: -Headroom(estimateMillis), ct: ct).ConfigureAwait(false);
                if (reserve.Ok)
                {
                    string key = HoldRowKey(reserve.Entry!.Id);
                    bool exists = await db.AssistantAllowanceHolds.AnyAsync(h => h.IdempotencyKey == key, ct).ConfigureAwait(false);
                    if (!exists)
                    {
                        db.AssistantAllowanceHolds.Add(NewHoldRow(organizationId, source, reserve.Entry.Id, estimateMillis));
                    }
                }

                return reserve;
            }, ct).ConfigureAwait(false);

            return r.Ok
                ? new ChargeHold(true, r.Entry!.Id, estimateMillis, r.AvailableMillis, r.Replayed ? NonWorkflowChargeCodes.Resumed : null)
                { OrganizationId = organizationId, Source = source, RequestId = requestId }
                : new ChargeHold(false, null, estimateMillis, r.AvailableMillis, r.Error) { OrganizationId = organizationId, Source = source, RequestId = requestId };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed: a charge we cannot evaluate must not hand out a free call.
            _logger.LogError(ex, "Credit hold for {Source} request {RequestId} failed; refusing", source, requestId);
            return new ChargeHold(false, null, estimateMillis, 0, NonWorkflowChargeCodes.GateUnavailable) { OrganizationId = organizationId, Source = source, RequestId = requestId };
        }
    }

    /// <inheritdoc />
    public Task<bool> SettleAsync(ChargeHold hold, long actualMillis, CancellationToken ct = default) =>
        SettleCoreAsync(hold, actualMillis, capped: true, ct);

    /// <inheritdoc />
    public Task<bool> SettleUncappedAsync(ChargeHold hold, long actualMillis, CancellationToken ct = default) =>
        SettleCoreAsync(hold, actualMillis, capped: false, ct);

    private async Task<bool> SettleCoreAsync(ChargeHold hold, long actualMillis, bool capped, CancellationToken ct)
    {
        if (!Options.Enabled || hold.ReservationId is not { } reservationId) return true;
        if (actualMillis <= 0) return await ReleaseAsync(hold, ct).ConfigureAwait(false);

        // The settlement can exceed the hold (the real usage is only estimated up front), but never by more than the
        // overdraft headroom the hold was admitted with: a call cannot run up an unbounded bill on a small hold. The one
        // exception (SettleUncappedAsync) is a size-priced picture job whose vendor already answered.
        long ceiling = capped ? hold.RequiredMillis + Headroom(hold.RequiredMillis) : long.MaxValue;
        long chargedMillis = Math.Min(actualMillis, ceiling);
        if (chargedMillis < actualMillis)
        {
            ReportCapped(hold.OrganizationId, hold.Source, hold.RequestId, actualMillis, chargedMillis);
        }

        try
        {
            return await LockedAsync(hold.OrganizationId, async (db, ledger) =>
            {
                AssistantAllowanceHold? row = await HoldRowAsync(db, hold.OrganizationId, reservationId, ct).ConfigureAwait(false);
                if (row is { Status: AssistantAllowanceHoldStatus.Swept })
                {
                    // T-110 (L1): the stale sweep took this hold for abandoned and closed it (charging what was recorded,
                    // or releasing it); the call still ran, so the rest is charged through the late key, once.
                    await ChargeLateHoldAsync(ledger, hold.OrganizationId, row, chargedMillis, ct).ConfigureAwait(false);
                    return true;
                }

                if (row is { Status: AssistantAllowanceHoldStatus.Settled }) return true;

                CreditLedgerResult r = await ledger.SettleAsync(
                    hold.OrganizationId, reservationId, chargedMillis, SettleKey(reservationId),
                    new CreditLedgerContext(Note: chargedMillis > hold.RequiredMillis ? "overage" : null), ct).ConfigureAwait(false);
                if (!r.Ok)
                {
                    _logger.LogError("Settling hold {ReservationId} refused: {Error}", reservationId, r.Error);
                    return false;
                }

                CloseHoldRow(row, AssistantAllowanceHoldStatus.Settled, chargedMillis);
                return true;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Settling hold {ReservationId} for {Millis} millicredits failed", reservationId, chargedMillis);
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// One reservation keyed like a hold of the same request (<see cref="ReserveKey"/>, no floor) settled in the same
    /// transaction under the organization's ledger lock, so a retry replays the same entries and charges once.
    /// </remarks>
    public async Task<bool> ChargeUnheldAsync(
        Guid organizationId, UsageSource source, long actualMillis, Guid requestId, CreditLedgerContext context, CancellationToken ct = default)
    {
        if (!Options.Enabled || actualMillis <= 0) return true;

        try
        {
            return await LockedAsync(organizationId, async (_, ledger) =>
            {
                CreditLedgerResult reserve = await ledger.ReserveAsync(
                    organizationId, actualMillis, ReserveKey(source, requestId), context, minAvailableAfterMillis: long.MinValue, ct: ct).ConfigureAwait(false);
                if (!reserve.Ok) throw new InvalidOperationException($"Reserving the unheld charge was refused: {reserve.Error}");
                CreditLedgerResult settle = await ledger.SettleAsync(
                    organizationId, reserve.Entry!.Id, actualMillis, SettleKey(reserve.Entry.Id), context, ct).ConfigureAwait(false);
                if (!settle.Ok && settle.Error != CreditLedgerErrors.ReservationClosed)
                {
                    throw new InvalidOperationException($"Settling the unheld charge was refused: {settle.Error}");
                }

                return true;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Charging the unheld {Source} call of organization {OrganizationId} (request {RequestId}) for {Millis} millicredits failed; it was not charged",
                source, organizationId, requestId, actualMillis);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseAsync(ChargeHold hold, CancellationToken ct = default)
    {
        if (!Options.Enabled || hold.ReservationId is not { } reservationId) return true;

        try
        {
            return await LockedAsync(hold.OrganizationId, async (db, ledger) =>
            {
                // A hold the sweep already closed, or one settled, has nothing left to release: the call failed, so
                // nothing more is charged.
                AssistantAllowanceHold? row = await HoldRowAsync(db, hold.OrganizationId, reservationId, ct).ConfigureAwait(false);
                if (row is { Status: not AssistantAllowanceHoldStatus.Open }) return true;

                CreditLedgerResult r = await ledger.ReleaseAsync(
                    hold.OrganizationId, reservationId, ReleaseKey(reservationId), default, ct).ConfigureAwait(false);
                if (!r.Ok)
                {
                    _logger.LogError("Releasing hold {ReservationId} refused: {Error}", reservationId, r.Error);
                    return false;
                }

                CloseHoldRow(row, AssistantAllowanceHoldStatus.Released, 0);
                return true;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Releasing hold {ReservationId} failed", reservationId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task RecordUnsettledHoldAsync(ChargeHold hold, long meteredMillis, CancellationToken ct = default)
    {
        if (!Options.Enabled || meteredMillis < 0 || hold.ReservationId is not { } reservationId) return;

        long chargedMillis = Math.Min(meteredMillis, HoldCeiling(hold.RequiredMillis));
        try
        {
            await LockedAsync(hold.OrganizationId, async (db, ledger) =>
            {
                AssistantAllowanceHold? row = await HoldRowAsync(db, hold.OrganizationId, reservationId, ct).ConfigureAwait(false);
                if (row is null)
                {
                    // A hold taken before hold rows existed: it gets one now, open, with its usage on it.
                    row = NewHoldRow(hold.OrganizationId, hold.Source, reservationId, hold.RequiredMillis);
                    db.AssistantAllowanceHolds.Add(row);
                }

                if (row.Status == AssistantAllowanceHoldStatus.Open)
                {
                    row.UnsettledMeteredMillis = Math.Max(row.UnsettledMeteredMillis ?? 0, meteredMillis);
                }
                else if (row.Status == AssistantAllowanceHoldStatus.Swept)
                {
                    // The sweep closed it meanwhile: the usage is charged now, like a late close.
                    await ChargeLateHoldAsync(ledger, hold.OrganizationId, row, chargedMillis, ct).ConfigureAwait(false);
                }

                return true;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Recording the unsettled {Source} hold {ReservationId} of organization {OrganizationId} (request {RequestId}) at {Millis} millicredits failed",
                hold.Source, reservationId, hold.OrganizationId, hold.RequestId, meteredMillis);
        }
    }

    /// <summary>
    /// T-110 (L1): closes the organization's editor-operation, subject and proposal holds still open
    /// <see cref="StaleHoldAge"/> after they were taken (the process died, or both close attempts failed), with the rule
    /// of the assistant-turn sweep: a hold whose failed close recorded its usage is charged exactly that (capped like its
    /// settlement), one with nothing recorded is released. Each is marked <see cref="AssistantAllowanceHoldStatus.Swept"/>
    /// and closed under its own key, so a late close of the same hold still charges what the sweep did not, through
    /// <see cref="LateSettleKey"/>, once. Runs under the organization's ledger lock; idempotent. Returns how many it closed.
    /// </summary>
    public async Task<int> SweepStaleHoldsAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (!Options.Enabled) return 0;

        return await LockedAsync(organizationId, async (db, ledger) =>
        {
            DateTime now = _time.GetUtcNow().UtcDateTime;
            DateTime cutoff = now - StaleHoldAge;
            List<AssistantAllowanceHold> stale = await db.AssistantAllowanceHolds
                .Where(h => h.OrganizationId == organizationId && h.Status == AssistantAllowanceHoldStatus.Open
                            && h.CreatedAt < cutoff && h.IdempotencyKey.StartsWith(HoldRowPrefix))
                .OrderBy(h => h.CreatedAt)
                .Take(MaxHoldsPerSweep)
                .ToListAsync(ct).ConfigureAwait(false);
            int closed = 0;
            foreach (AssistantAllowanceHold row in stale)
            {
                if (row.CreditReservationId is not { } reservationId) continue;

                long charged = Math.Min(Math.Max(0, row.UnsettledMeteredMillis ?? 0), HoldCeiling(row.CreditHeldMillis));
                CreditLedgerContext context = new(Note: row.Source.ToString());
                CreditLedgerResult r = charged > 0
                    ? await ledger.SettleAsync(organizationId, reservationId, charged, SweepKey(reservationId), context, ct).ConfigureAwait(false)
                    : await ledger.ReleaseAsync(organizationId, reservationId, SweepKey(reservationId), context, ct).ConfigureAwait(false);
                if (!r.Ok && r.Error == CreditLedgerErrors.ReservationClosed)
                {
                    // Closed some other way (before hold rows tracked it): nothing is known to be owed, and charging
                    // it again could charge twice, so the row is only closed.
                    CloseHoldRow(row, AssistantAllowanceHoldStatus.Settled, 0);
                    continue;
                }

                if (!r.Ok) throw new InvalidOperationException($"Closing the stale hold's reservation {reservationId} was refused: {r.Error}");

                row.Status = AssistantAllowanceHoldStatus.Swept;
                row.ChargedMillis = charged;
                row.ClosedAt = now;
                closed++;
                _logger.LogWarning(
                    "Swept the {Source} hold {ReservationId} of organization {OrganizationId}, left open since {CreatedAt}: {Outcome} (a later close is charged only the rest)",
                    row.Source, reservationId, organizationId, row.CreatedAt, charged > 0 ? $"charged its recorded {charged} millicredits" : "released, nothing was recorded");
            }

            if (closed > 0) NonWorkflowChargeDiagnostics.HoldsSwept.Add(closed);
            return closed;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Runs <see cref="SweepStaleHoldsAsync"/>; a failure is logged and never stops the call that triggered it.</summary>
    private async Task TrySweepStaleHoldsAsync(Guid organizationId, CancellationToken ct)
    {
        try
        {
            await SweepStaleHoldsAsync(organizationId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Sweeping the stale holds of organization {OrganizationId} failed; retried at its next charged call", organizationId);
        }
    }

    /// <summary>
    /// Charges a swept hold's late close: <paramref name="chargedMillis"/> less what the sweep already charged, through
    /// one reservation keyed <see cref="LateSettleKey"/> on the row and settled in the same transaction (no floor: the
    /// usage happened). Inside <see cref="LockedAsync{T}"/>; marks the row settled, so a repeat charges nothing.
    /// </summary>
    private async Task ChargeLateHoldAsync(CreditLedger ledger, Guid org, AssistantAllowanceHold row, long chargedMillis, CancellationToken ct)
    {
        long rest = Math.Max(0, chargedMillis - row.ChargedMillis);
        if (rest > 0)
        {
            CreditLedgerContext context = new(Note: row.Source.ToString());
            CreditLedgerResult reserve = await ledger.ReserveAsync(
                org, rest, LateSettleKey(row.Id), context, minAvailableAfterMillis: long.MinValue, ct: ct).ConfigureAwait(false);
            if (!reserve.Ok) throw new InvalidOperationException($"Reserving the late close of hold {row.CreditReservationId} was refused: {reserve.Error}");
            CreditLedgerResult settle = await ledger.SettleAsync(
                org, reserve.Entry!.Id, rest, SettleKey(reserve.Entry.Id), context, ct).ConfigureAwait(false);
            if (!settle.Ok && settle.Error != CreditLedgerErrors.ReservationClosed)
            {
                throw new InvalidOperationException($"Settling the late close of hold {row.CreditReservationId} was refused: {settle.Error}");
            }
        }

        _logger.LogWarning("The swept {Source} hold {ReservationId} of organization {OrganizationId} closed late; charged the remaining {Millis} millicredits",
            row.Source, row.CreditReservationId, org, rest);
        CloseHoldRow(row, AssistantAllowanceHoldStatus.Settled, row.ChargedMillis + rest);
    }

    /// <summary>The most a hold settles at: the hold plus its overdraft headroom (<see cref="SettleAsync"/>'s cap).</summary>
    private long HoldCeiling(long holdMillis) => holdMillis + Headroom(holdMillis);

    private static Task<AssistantAllowanceHold?> HoldRowAsync(InferenceApiDbContext db, Guid org, Guid reservationId, CancellationToken ct)
    {
        string key = HoldRowKey(reservationId);
        return db.AssistantAllowanceHolds.FirstOrDefaultAsync(h => h.IdempotencyKey == key && h.OrganizationId == org, ct);
    }

    private void CloseHoldRow(AssistantAllowanceHold? row, AssistantAllowanceHoldStatus status, long chargedMillis)
    {
        if (row is null) return;
        row.Status = status;
        row.ChargedMillis = chargedMillis;
        row.ClosedAt = _time.GetUtcNow().UtcDateTime;
    }

    /// <summary>
    /// The row that tracks an editor-operation, subject or proposal hold: it holds no allowance (its
    /// <see cref="AssistantAllowanceHold.HeldMillis"/> and <see cref="AssistantAllowanceHold.ConsumedMillis"/> stay 0)
    /// and is never a turn (keyed <see cref="HoldRowPrefix"/>, which every turn query excludes).
    /// </summary>
    private AssistantAllowanceHold NewHoldRow(Guid org, UsageSource source, Guid reservationId, long heldMillis) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = org,
        Source = source,
        IdempotencyKey = HoldRowKey(reservationId),
        CreditReservationId = reservationId,
        CreditHeldMillis = heldMillis,
        Status = AssistantAllowanceHoldStatus.Open,
        CreatedAt = _time.GetUtcNow().UtcDateTime
    };

    /// <inheritdoc />
    public async Task<TurnHold> BeginTurnAsync(
        Guid? organizationId, UsageSource source, Guid requestId, UsageBillingMode billingMode = UsageBillingMode.Platform, CancellationToken ct = default)
    {
        long holdMillis = TurnHoldMillis;
        if (!Options.Enabled)
        {
            return new TurnHold(true, null, 0, 0, NonWorkflowChargeCodes.BillingDisabled) { OrganizationId = organizationId, Source = source, RequestId = requestId };
        }

        if (organizationId is not { } org)
        {
            // No organization means nothing to charge: refuse rather than run the call for free.
            return new TurnHold(false, null, holdMillis, 0, NonWorkflowChargeCodes.NoOrganization) { Source = source, RequestId = requestId };
        }

        // T-110 (L1): the organization's stale editor, subject and proposal holds are swept here too (best effort).
        await TrySweepStaleHoldsAsync(org, ct).ConfigureAwait(false);

        try
        {
            AssistantAllowanceTerms terms = await _allowance.GetTermsAsync(org, ct).ConfigureAwait(false);

            // T-026 (finding 4): the explicit unlimited entitlement exempts the ALLOWANCE only. The turn still takes a
            // hold row (of zero credits: Enterprise is billed outside credits), so it is still pre-claimed step by
            // step, cut off at the per-turn ceiling and counted against the in-flight cap.
            long? includedOrUnlimited = terms.IncludedMillis;

            TurnHold Shaped(TurnHold hold) => hold with
            {
                OrganizationId = org,
                Source = source,
                RequestId = requestId,
                IncludedMillis = includedOrUnlimited,
                PeriodStart = terms.PeriodStart,
                PeriodEnd = terms.PeriodEnd
            };

            string key = TurnKey(source, requestId);
            return await LockedAsync(org, async (db, ledger) =>
            {
                AssistantAllowanceHold? existing = await db.AssistantAllowanceHolds.AsNoTracking()
                    .FirstOrDefaultAsync(h => h.IdempotencyKey == key, ct).ConfigureAwait(false);
                if (existing is not null)
                {
                    if (existing.OrganizationId != org) throw new InvalidOperationException("Turn idempotency key already used by another organization.");
                    return Shaped(new TurnHold(true, existing.Id, existing.HeldMillis + existing.CreditHeldMillis, 0, NonWorkflowChargeCodes.Resumed)
                    {
                        AllowanceHeldMillis = existing.HeldMillis,
                        CreditHeldMillis = existing.CreditHeldMillis
                    });
                }

                // A turn whose process died between hold and settle never closes its hold: sweep it before counting.
                await SweepStaleTurnsAsync(db, ledger, org, terms, ct).ConfigureAwait(false);

                // The in-flight cap: concurrent charged turns each overshoot by at most one round-trip, so their number
                // is bounded. A BYO or Runner turn's model calls are not charged; it is not refused by the cap.
                if (billingMode == UsageBillingMode.Platform)
                {
                    int open = await db.AssistantAllowanceHolds.AsNoTracking()
                        .CountAsync(h => h.OrganizationId == org && h.Status == AssistantAllowanceHoldStatus.Open
                                         && !h.IdempotencyKey.StartsWith(HoldRowPrefix), ct).ConfigureAwait(false);
                    if (open >= Math.Max(1, Options.NonWorkflow.MaxConcurrentTurnsPerOrg))
                    {
                        return Shaped(new TurnHold(false, null, holdMillis, 0, NonWorkflowChargeCodes.TurnConcurrencyLimit));
                    }
                }

                if (includedOrUnlimited is not { } included)
                {
                    AssistantAllowanceHold unlimitedRow = NewTurnRow(org, source, key, 0, null, 0);
                    db.AssistantAllowanceHolds.Add(unlimitedRow);
                    return Shaped(new TurnHold(true, unlimitedRow.Id, 0, 0, NonWorkflowChargeCodes.UnlimitedAllowance));
                }

                // Under the lock: every other turn's hold or consumption is committed, so this is what is left. The
                // hold is the configured size, or whatever allowance left plus available balance there is when that
                // is less but still covers the minimum turn; less than the minimum is a refusal, never a
                // partial hold run in full.
                long inUse = await AllowanceInUseAsync(db, org, terms.PeriodStart, terms.PeriodEnd, Guid.Empty, ct).ConfigureAwait(false);
                long allowanceLeft = Math.Max(0, included - inUse);
                long available = (await ledger.GetBalanceAsync(org, ct).ConfigureAwait(false)).AvailableMillis;
                long held = Math.Min(holdMillis, allowanceLeft + Math.Max(0, available));
                if (held < MinTurnMillis)
                {
                    return Shaped(new TurnHold(false, null, holdMillis, available, NonWorkflowChargeCodes.InsufficientCredits));
                }

                long fromAllowance = Math.Min(held, allowanceLeft);
                long fromCredits = held - fromAllowance;

                Guid? reservationId = null;
                if (fromCredits > 0)
                {
                    // Reserved against the balance with no overdraft: in-flight holds never exceed allowance left + balance.
                    CreditLedgerResult r = await ledger.ReserveAsync(
                        org, fromCredits, ReserveKey(source, requestId), new CreditLedgerContext(Note: source.ToString()),
                        minAvailableAfterMillis: 0, ct: ct).ConfigureAwait(false);
                    if (!r.Ok) return Shaped(new TurnHold(false, null, holdMillis, r.AvailableMillis, r.Error));
                    reservationId = r.Entry!.Id;
                    available = r.AvailableMillis;
                }

                AssistantAllowanceHold row = NewTurnRow(org, source, key, fromAllowance, reservationId, fromCredits);
                db.AssistantAllowanceHolds.Add(row);
                return Shaped(new TurnHold(true, row.Id, held, available, null)
                {
                    AllowanceHeldMillis = fromAllowance,
                    CreditHeldMillis = fromCredits
                });
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed: a turn we cannot evaluate must not run for free.
            _logger.LogError(ex, "Assistant credit hold for {Source} request {RequestId} of organization {OrganizationId} failed; refusing",
                source, requestId, org);
            return new TurnHold(false, null, holdMillis, 0, NonWorkflowChargeCodes.GateUnavailable) { OrganizationId = org, Source = source, RequestId = requestId };
        }
    }

    /// <inheritdoc />
    public async Task<TurnSettlement> SettleTurnAsync(TurnHold turn, long meteredMillis, CancellationToken ct = default)
    {
        if (!Options.Enabled || turn.OrganizationId is not { } org || !turn.Allowed)
        {
            return new TurnSettlement(true, 0, 0, 0, turn.Code);
        }

        // only billing off and the explicit unlimited entitlement are admitted with no hold and charge
        // nothing. Any other admitted turn without a hold is charged at its metered actual anyway, keyed on its
        // request id (a turn hold row is created for it under the lock, so a retry finds it settled).
        Guid? heldId = turn.HoldId;
        if (heldId is null && (NonWorkflowChargeCodes.IsAllowedWithoutHold(turn.Code) || meteredMillis <= 0))
        {
            return new TurnSettlement(true, 0, 0, 0, turn.Code);
        }

        if (heldId is not null && meteredMillis <= 0)
        {
            bool released = await ReleaseTurnAsync(turn, ct).ConfigureAwait(false);
            return new TurnSettlement(released, 0, 0, 0, released ? null : NonWorkflowChargeCodes.GateUnavailable);
        }

        Guid holdId = heldId ?? Guid.Empty;
        try
        {
            long overshoot = 0;
            bool holdless = heldId is null;
            AssistantAllowanceTerms? holdlessTerms = holdless
                ? await _allowance.GetTermsAsync(org, ct).ConfigureAwait(false)
                : null;
            long included = holdless ? holdlessTerms!.IncludedMillis ?? long.MaxValue : turn.IncludedMillis ?? long.MaxValue;
            DateTime periodStart = holdless ? holdlessTerms!.PeriodStart : turn.PeriodStart;
            DateTime periodEnd = holdless ? holdlessTerms!.PeriodEnd : turn.PeriodEnd;
            TurnSettlement settled = await LockedAsync(org, async (db, ledger) =>
            {
                AssistantAllowanceHold? row = holdless
                    ? await HoldlessRowAsync(db, org, turn.Source, turn.RequestId, ct).ConfigureAwait(false)
                    : await db.AssistantAllowanceHolds
                        .FirstOrDefaultAsync(h => h.Id == holdId && h.OrganizationId == org, ct).ConfigureAwait(false);
                if (row is null) return new TurnSettlement(false, 0, 0, 0, CreditLedgerErrors.ReservationNotFound);

                // A turn the stale sweep closed (it outlived StaleTurnHoldAge, so it was taken for dead) still ran: it
                // settles as a hold of 0 through the normal path, so it is still charged, less whatever the sweep
                // already charged for its recorded usage. A turn released any other way (failure, disconnect),
                // however old, is closed, like a settled one: keyed on the status alone.
                bool late = row.Status == AssistantAllowanceHoldStatus.Swept;
                if (row.Status != AssistantAllowanceHoldStatus.Open && !late)
                {
                    return new TurnSettlement(true, row.ConsumedMillis, row.ChargedMillis, row.UnchargedMillis, NonWorkflowChargeCodes.Resumed);
                }

                long priorAllowance = late ? row.ConsumedMillis : 0;
                long priorCharged = late ? row.ChargedMillis : 0;
                long amount = Math.Max(0, meteredMillis - priorAllowance - priorCharged);
                (long fromAllowance, long charged, long over) = await ChargeRowAsync(
                    db, ledger, org, row, amount, included, periodStart, periodEnd, late, holdless, ct).ConfigureAwait(false);
                overshoot = over;

                row.Status = AssistantAllowanceHoldStatus.Settled;
                row.ConsumedMillis = priorAllowance + fromAllowance;
                row.ChargedMillis = priorCharged + charged;
                row.UnchargedMillis = 0;
                row.ClosedAt = _time.GetUtcNow().UtcDateTime;
                return new TurnSettlement(true, fromAllowance, charged, 0, null);
            }, ct).ConfigureAwait(false);

            if (settled.Ok && settled.Code is null && overshoot > 0)
            {
                ReportOvershoot(org, turn.Source, turn.RequestId, meteredMillis, overshoot);
            }

            return settled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Settling the {Source} turn {HoldId} of organization {OrganizationId} (request {RequestId}) at {Millis} millicredits failed",
                turn.Source, holdId, org, turn.RequestId, meteredMillis);
            return new TurnSettlement(false, 0, 0, 0, NonWorkflowChargeCodes.GateUnavailable);
        }
    }

    /// <summary>
    /// Charges <paramref name="amount"/> on a turn's hold <paramref name="row"/>, inside <see cref="LockedAsync{T}"/>: the
    /// allowance first (this hold's own plus whatever nobody else holds or has consumed), then the full remainder in
    /// credits through the row's reservation (settled, or released when nothing is left for credits) or, without one, an
    /// overage reservation settled in the same transaction (the <see cref="LateSettleKey"/> one when <paramref name="late"/>).
    /// Shared by the settlement and the stale sweep, so both charge the same way under the same idempotent keys.
    /// Returns what came from the allowance, what was charged in credits, and how far that went past the balance plus
    /// the headroom. The caller updates the row.
    /// </summary>
    private async Task<(long FromAllowance, long Charged, long Overshoot)> ChargeRowAsync(
        InferenceApiDbContext db, CreditLedger ledger, Guid org, AssistantAllowanceHold row, long amount,
        long included, DateTime periodStart, DateTime periodEnd, bool late, bool holdless, CancellationToken ct)
    {
        long allowanceHeld = late ? 0 : row.HeldMillis;
        long creditHeld = late ? 0 : row.CreditHeldMillis;
        Guid? heldReservation = late ? null : row.CreditReservationId;

        // The allowance first: this turn's own hold, plus whatever nobody else holds or has consumed.
        long others = await AllowanceInUseAsync(db, org, periodStart, periodEnd, row.Id, ct).ConfigureAwait(false);
        long allowanceFree = Math.Max(allowanceHeld, included - others);
        long fromAllowance = Math.Min(Math.Max(0, amount), Math.Max(0, allowanceFree));
        long charged = Math.Max(0, amount) - fromAllowance;

        // Then credits, the full remainder: what the turn metered is charged, never dropped. The bound is the
        // in-turn cut-off (CheckTurnAsync), so this can go below minus the headroom only by the last round-trip; that
        // overshoot is counted.
        long overshoot = 0;
        if (charged > 0)
        {
            CreditLedgerResult balance = await ledger.GetBalanceAsync(org, ct).ConfigureAwait(false);
            // A late (or hold-less) settle has no hold of its own to size the headroom on: it gets a full turn hold's.
            long availableWithoutThis = balance.AvailableMillis + creditHeld;
            long headroom = Headroom(late || holdless ? TurnHoldMillis : allowanceHeld + creditHeld);
            overshoot = Math.Max(0, charged - (availableWithoutThis + headroom));
        }

        CreditLedgerContext context = new(Note: row.Source.ToString());
        if (heldReservation is { } reservationId)
        {
            CreditLedgerResult r = charged > 0
                ? await ledger.SettleAsync(org, reservationId, charged, SettleKey(reservationId), context, ct).ConfigureAwait(false)
                : await ledger.ReleaseAsync(org, reservationId, ReleaseKey(reservationId), context, ct).ConfigureAwait(false);
            if (!r.Ok && r.Error != CreditLedgerErrors.ReservationClosed)
            {
                throw new InvalidOperationException($"Closing the turn's credit reservation {reservationId} was refused: {r.Error}");
            }
        }
        else if (charged > 0)
        {
            // All of the hold came from the allowance (or there is none left, settling late): the overage is reserved
            // (no floor; the cut-off bounds it) and settled in this same transaction.
            CreditLedgerResult reserve = await ledger.ReserveAsync(
                org, charged, late ? LateSettleKey(row.Id) : OverageKey(row.Id), context, minAvailableAfterMillis: long.MinValue, ct: ct).ConfigureAwait(false);
            if (!reserve.Ok) throw new InvalidOperationException($"Reserving the turn's overage was refused: {reserve.Error}");
            CreditLedgerResult settle = await ledger.SettleAsync(
                org, reserve.Entry!.Id, charged, SettleKey(reserve.Entry.Id), context, ct).ConfigureAwait(false);
            if (!settle.Ok && settle.Error != CreditLedgerErrors.ReservationClosed)
            {
                throw new InvalidOperationException($"Settling the turn's overage was refused: {settle.Error}");
            }
        }

        return (fromAllowance, charged, overshoot);
    }

    /// <summary>
    /// The turn hold row a hold-less turn is charged through: the one already keyed on its request id
    /// (a retry), else a new open row holding nothing, added to <paramref name="db"/>'s change tracker.
    /// </summary>
    private async Task<AssistantAllowanceHold?> HoldlessRowAsync(
        InferenceApiDbContext db, Guid org, UsageSource source, Guid requestId, CancellationToken ct)
    {
        string key = TurnKey(source, requestId);
        AssistantAllowanceHold? existing = await db.AssistantAllowanceHolds
            .FirstOrDefaultAsync(h => h.IdempotencyKey == key, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.OrganizationId == org ? existing : null;
        }

        AssistantAllowanceHold row = NewTurnRow(org, source, key, 0, null, 0);
        db.AssistantAllowanceHolds.Add(row);
        _logger.LogWarning(
            "The {Source} turn of organization {OrganizationId} (request {RequestId}) was admitted without a hold outside the allowed codes; charging it at settlement",
            source, org, requestId);
        return row;
    }

    /// <summary>A new open turn hold row (not yet added to a context).</summary>
    private AssistantAllowanceHold NewTurnRow(Guid org, UsageSource source, string key, long allowanceHeld, Guid? reservationId, long creditHeld) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = org,
        Source = source,
        IdempotencyKey = key,
        HeldMillis = allowanceHeld,
        CreditReservationId = reservationId,
        CreditHeldMillis = creditHeld,
        Status = AssistantAllowanceHoldStatus.Open,
        CreatedAt = _time.GetUtcNow().UtcDateTime
    };

    /// <inheritdoc />
    /// <remarks>A <see cref="ClaimStepAsync"/> of a zero-cost step: what the turn has metered so far.</remarks>
    public Task<TurnCheck> CheckTurnAsync(TurnHold turn, long meteredMillis, CancellationToken ct = default)
        => ClaimStepAsync(turn, new TurnStepClaim(meteredMillis), ct);

    /// <inheritdoc />
    /// <remarks>
    /// The step is refused when the turn's exposure (metered + claimed + this step) would pass
    /// <c>min(ceiling, allowance free + available + headroom - what other open turns metered and claimed beyond their
    /// holds)</c>, or when what it metered already reached that limit; read under the organization's ledger lock. While
    /// the exposure stays within the turn's own hold no lookup is needed, so the check before a turn's FIRST round-trip
    /// is sized on that round-trip, not on 0 metered; the turn's first paid step is always checked here, which marks the
    /// hold used. An admitted step records the exposure on the open hold
    /// (<see cref="AssistantAllowanceHold.ConsumedMillis"/>), so concurrent turns count each other's claims; the claim is
    /// replaced by the actual in <see cref="LandStepAsync"/>. A turn admitted without a hold under the allowed codes
    /// (billing off) always continues; an unlimited turn holds a zero row and is checked like any other, its allowance
    /// never running out; a check that cannot be evaluated stops the turn (fail closed).
    /// </remarks>
    public async Task<TurnCheck> ClaimStepAsync(TurnHold turn, TurnStepClaim step, CancellationToken ct = default)
    {
        if (!Options.Enabled || NonWorkflowChargeCodes.IsAllowedWithoutHold(turn.Code) && turn.HoldId is null)
        {
            return new TurnCheck(true, null, long.MaxValue);
        }

        if (turn.HoldId is not { } holdId || turn.OrganizationId is not { } org)
        {
            // a turn that holds nothing outside the allowed codes is not run on (fail closed).
            return new TurnCheck(false, NonWorkflowChargeCodes.GateUnavailable, 0);
        }

        long ceiling = TurnCeilingMillis;
        long metered = Math.Max(0, step.MeteredMillis);
        long exposure = SaturatingAdd(SaturatingAdd(metered, Math.Max(0, step.ClaimedMillis)), Math.Max(0, step.StepMillis));
        if (metered >= ceiling || exposure > ceiling) return new TurnCheck(false, NonWorkflowChargeCodes.TurnLimit, ceiling);

        // T-040: the turn's first paid step (nothing metered, nothing else claimed) always takes the locked path, which
        // records its exposure on the hold: the hold is then marked used, so the stale sweep charges it even when the
        // settlement and the record both fail. One write per turn; later steps within the hold write nothing.
        bool firstPaidStep = step.StepMillis > 0 && metered == 0 && step.ClaimedMillis <= 0;
        if (!step.AdmitUnderConcurrencyCap && !firstPaidStep && exposure < turn.RequiredMillis) return new TurnCheck(true, null, ceiling);

        try
        {
            long included = turn.IncludedMillis ?? long.MaxValue;
            return await LockedAsync(org, async (db, ledger) =>
            {
                AssistantAllowanceHold? row = await db.AssistantAllowanceHolds
                    .FirstOrDefaultAsync(h => h.Id == holdId && h.OrganizationId == org, ct).ConfigureAwait(false);
                if (row is null || row.Status != AssistantAllowanceHoldStatus.Open)
                {
                    // Closed under it (swept, released): nothing is held for it any more, so it does not go on.
                    return new TurnCheck(false, NonWorkflowChargeCodes.InsufficientCredits, 0);
                }

                // F-7: a BYO-chat turn's first Platform-billed step counts it against the in-flight cap, like a turn
                // that began in Platform mode (every open hold of the organization counts, its own aside).
                if (step.AdmitUnderConcurrencyCap)
                {
                    int open = await db.AssistantAllowanceHolds.AsNoTracking()
                        .CountAsync(h => h.OrganizationId == org && h.Status == AssistantAllowanceHoldStatus.Open && h.Id != row.Id
                                         && !h.IdempotencyKey.StartsWith(HoldRowPrefix), ct)
                        .ConfigureAwait(false);
                    if (open >= Math.Max(1, Options.NonWorkflow.MaxConcurrentTurnsPerOrg))
                    {
                        return new TurnCheck(false, NonWorkflowChargeCodes.TurnConcurrencyLimit, 0);
                    }
                }

                if (!step.AdmitUnderConcurrencyCap && exposure < turn.RequiredMillis)
                {
                    // The first paid step, within the turn's own hold: admitted as before, and the hold marked used.
                    row.ConsumedMillis = exposure;
                    return new TurnCheck(true, null, ceiling);
                }

                long others = await AllowanceInUseAsync(db, org, turn.PeriodStart, turn.PeriodEnd, row.Id, ct).ConfigureAwait(false);
                long allowanceFree = Math.Max(row.HeldMillis, included - others);
                long available = (await ledger.GetBalanceAsync(org, ct).ConfigureAwait(false)).AvailableMillis + row.CreditHeldMillis;
                long othersBeyondHolds = await db.AssistantAllowanceHolds.AsNoTracking()
                    .Where(h => h.OrganizationId == org && h.Status == AssistantAllowanceHoldStatus.Open && h.Id != row.Id
                                && h.ConsumedMillis > h.HeldMillis + h.CreditHeldMillis && !h.IdempotencyKey.StartsWith(HoldRowPrefix))
                    .SumAsync(h => (long?)(h.ConsumedMillis - h.HeldMillis - h.CreditHeldMillis), ct).ConfigureAwait(false) ?? 0;
                long funded = SaturatingAdd(Math.Max(0, allowanceFree), available + Headroom(row.HeldMillis + row.CreditHeldMillis)) - othersBeyondHolds;

                // Within the ceiling here, so a stop is always for want of allowance and credits.
                long limit = Math.Min(ceiling, funded);
                if (metered >= limit || exposure > limit) return new TurnCheck(false, NonWorkflowChargeCodes.InsufficientCredits, limit);

                // The turn's in-flight total, metered plus every claim not yet landed: what other turns' checks count.
                row.ConsumedMillis = exposure;
                return new TurnCheck(true, null, limit);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Checking the {Source} turn {HoldId} of organization {OrganizationId} (request {RequestId}) at {Millis} millicredits failed; stopping it",
                turn.Source, holdId, org, turn.RequestId, metered);
            return new TurnCheck(false, NonWorkflowChargeCodes.GateUnavailable, 0);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Only an open hold whose recorded exposure is beyond its own hold matters to other turns, so a landing that stays
    /// within the hold writes nothing. Best effort: a failure is logged, and the next claim or the settlement records
    /// the turn's real total anyway.
    /// </remarks>
    public async Task LandStepAsync(TurnHold turn, long meteredMillis, long claimedMillis, CancellationToken ct = default)
    {
        if (!Options.Enabled || turn.HoldId is not { } holdId || turn.OrganizationId is not { } org) return;

        try
        {
            long exposure = SaturatingAdd(Math.Max(0, meteredMillis), Math.Max(0, claimedMillis));
            await LockedAsync(org, async (db, _) =>
            {
                AssistantAllowanceHold? row = await db.AssistantAllowanceHolds
                    .FirstOrDefaultAsync(h => h.Id == holdId && h.OrganizationId == org, ct).ConfigureAwait(false);
                if (row is { Status: AssistantAllowanceHoldStatus.Open } && row.ConsumedMillis != exposure
                    && Math.Max(row.ConsumedMillis, exposure) > row.HeldMillis + row.CreditHeldMillis)
                {
                    row.ConsumedMillis = exposure;
                }

                return true;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Recording the landed step of the {Source} turn {HoldId} of organization {OrganizationId} failed",
                turn.Source, holdId, org);
        }
    }

    /// <summary>
    /// The most a burst of pre-claimed turns can take an organization's balance below zero: every
    /// step is claimed at its worst case before it runs and refused past what the turn can pay, so the balance goes
    /// negative only by the overdraft headroom (<paramref name="headroomMillis"/>, the largest any open turn was admitted
    /// with) plus, per open turn, how far one step's actual usage exceeded its claim
    /// (<paramref name="estimationErrorPerStepMillis"/>; 0 when the estimate is exact).
    /// </summary>
    public static long WorstCaseOvershootMillis(int openTurns, long headroomMillis, long estimationErrorPerStepMillis)
        => Math.Max(0, headroomMillis) + Math.Max(0, openTurns) * Math.Max(0, estimationErrorPerStepMillis);

    private static long SaturatingAdd(long a, long b) => b > 0 && a > long.MaxValue - b ? long.MaxValue : a + b;

    /// <inheritdoc />
    public async Task<bool> ReleaseTurnAsync(TurnHold turn, CancellationToken ct = default)
    {
        if (!Options.Enabled || turn.HoldId is not { } holdId || turn.OrganizationId is not { } org) return true;

        try
        {
            return await LockedAsync(org, async (db, ledger) =>
            {
                AssistantAllowanceHold? row = await db.AssistantAllowanceHolds
                    .FirstOrDefaultAsync(h => h.Id == holdId && h.OrganizationId == org, ct).ConfigureAwait(false);
                if (row is null) return false;
                if (row.Status != AssistantAllowanceHoldStatus.Open) return true;

                if (row.CreditReservationId is { } reservationId)
                {
                    CreditLedgerResult r = await ledger.ReleaseAsync(
                        org, reservationId, ReleaseKey(reservationId), new CreditLedgerContext(Note: row.Source.ToString()), ct).ConfigureAwait(false);
                    if (!r.Ok && r.Error != CreditLedgerErrors.ReservationClosed)
                    {
                        throw new InvalidOperationException($"Releasing the turn's credit reservation {reservationId} was refused: {r.Error}");
                    }
                }

                row.Status = AssistantAllowanceHoldStatus.Released;
                row.ConsumedMillis = 0;
                row.ClosedAt = _time.GetUtcNow().UtcDateTime;
                return true;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Releasing the {Source} turn {HoldId} of organization {OrganizationId} (request {RequestId}) failed",
                turn.Source, holdId, org, turn.RequestId);
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Writes the metered actual on the open hold's <see cref="AssistantAllowanceHold.UnsettledMeteredMillis"/> (T-040:
    /// known, so <see cref="SweepStaleTurnsAsync"/> charges exactly it, 0 included) and <c>max(recorded, metered)</c> on
    /// its <see cref="AssistantAllowanceHold.ConsumedMillis"/> (the exposure other turns' checks read). A closed hold and
    /// a turn without a hold write nothing.
    /// </remarks>
    public async Task RecordUnsettledTurnAsync(TurnHold turn, long meteredMillis, CancellationToken ct = default)
    {
        if (!Options.Enabled || meteredMillis < 0 || turn.HoldId is not { } holdId || turn.OrganizationId is not { } org) return;

        try
        {
            await LockedAsync(org, async (db, _) =>
            {
                AssistantAllowanceHold? row = await db.AssistantAllowanceHolds
                    .FirstOrDefaultAsync(h => h.Id == holdId && h.OrganizationId == org, ct).ConfigureAwait(false);
                if (row is { Status: AssistantAllowanceHoldStatus.Open })
                {
                    row.UnsettledMeteredMillis = Math.Max(row.UnsettledMeteredMillis ?? 0, meteredMillis);
                    if (row.ConsumedMillis < meteredMillis) row.ConsumedMillis = meteredMillis;
                }

                return true;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Recording the unsettled {Source} turn {HoldId} of organization {OrganizationId} (request {RequestId}) at {Millis} millicredits failed",
                turn.Source, holdId, org, turn.RequestId, meteredMillis);
        }
    }

    /// <summary>
    /// Sweeps the organization's turn holds left <see cref="AssistantAllowanceHoldStatus.Open"/> for longer than
    /// <see cref="StaleTurnHoldAge"/> (the process died between hold and settle, or its settlement failed) and closes
    /// each as <see cref="AssistantAllowanceHoldStatus.Swept"/>, which a late settle still charges (for what the sweep did
    /// not). T-026 (finding 1), T-040: a hold whose failed settlement recorded its metered actual
    /// (<see cref="AssistantAllowanceHold.UnsettledMeteredMillis"/>) is CHARGED exactly that; one marked used
    /// (<see cref="AssistantAllowanceHold.ConsumedMillis"/>: its in-flight exposure, written from its first paid claim on)
    /// is charged that, at least what it held; both through the settlement's own path (<see cref="ChargeRowAsync"/>:
    /// allowance first, then credits, same keys), a hold from a closed allowance period with only its own allowance hold.
    /// One that never metered anything is released (its allowance stops counting, its credit reservation is released).
    /// Runs inside <see cref="LockedAsync{T}"/>, at the next turn.
    /// </summary>
    private async Task SweepStaleTurnsAsync(InferenceApiDbContext db, CreditLedger ledger, Guid org, AssistantAllowanceTerms terms, CancellationToken ct)
    {
        DateTime now = _time.GetUtcNow().UtcDateTime;
        DateTime cutoff = now - StaleTurnHoldAge;
        List<AssistantAllowanceHold> stale = await db.AssistantAllowanceHolds
            .Where(h => h.OrganizationId == org && h.Status == AssistantAllowanceHoldStatus.Open && h.CreatedAt < cutoff
                        && !h.IdempotencyKey.StartsWith(HoldRowPrefix))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (AssistantAllowanceHold row in stale)
        {
            // T-040: a known metered actual (a failed settlement recorded it) is charged exactly; otherwise a hold marked
            // used (exposure recorded, from its first paid claim on) is charged at least its hold; else it is released.
            long amount = row.UnsettledMeteredMillis
                          ?? (row.ConsumedMillis > 0 ? Math.Max(row.ConsumedMillis, row.HeldMillis + row.CreditHeldMillis) : 0);
            if (amount > 0)
            {
                // T-040: a hold from a closed allowance period is charged against that period, never the current one: its
                // own allowance hold (admitted within that period's allowance, and counted there ever since) covers what
                // it can, the rest is credits. That period's full terms are not derivable (the plan may have changed); a
                // zero hold is an unlimited one (no other turn is admitted with nothing held), so it stays unlimited.
                bool closedPeriod = row.CreatedAt < terms.PeriodStart;
                long included = !closedPeriod ? terms.IncludedMillis ?? long.MaxValue
                    : row.HeldMillis == 0 && row.CreditHeldMillis == 0 && row.CreditReservationId is null ? long.MaxValue
                    : 0;
                (long fromAllowance, long charged, long overshoot) = await ChargeRowAsync(
                    db, ledger, org, row, amount, included, terms.PeriodStart, terms.PeriodEnd,
                    late: false, holdless: false, ct).ConfigureAwait(false);
                row.Status = AssistantAllowanceHoldStatus.Swept;
                row.ConsumedMillis = fromAllowance;
                row.ChargedMillis = charged;
                row.UnchargedMillis = 0;
                row.ClosedAt = now;
                _logger.LogWarning(
                    "Swept the {Source} turn hold {HoldId} of organization {OrganizationId}, left open since {CreatedAt}, charging its recorded {Millis} millicredits (a later settle is charged only the rest)",
                    row.Source, row.Id, org, row.CreatedAt, amount);
                if (overshoot > 0) ReportOvershoot(org, row.Source, Guid.Empty, amount, overshoot);

                // Saved at once: the next stale row's allowance check reads this one's consumption from the database.
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                continue;
            }

            if (row.CreditReservationId is { } reservationId)
            {
                CreditLedgerResult r = await ledger.ReleaseAsync(
                    org, reservationId, ReleaseKey(reservationId), new CreditLedgerContext(Note: row.Source.ToString()), ct).ConfigureAwait(false);
                if (!r.Ok && r.Error != CreditLedgerErrors.ReservationClosed)
                {
                    throw new InvalidOperationException($"Releasing the stale turn's credit reservation {reservationId} was refused: {r.Error}");
                }
            }

            row.Status = AssistantAllowanceHoldStatus.Swept;
            row.ConsumedMillis = 0;
            row.ClosedAt = now;
            _logger.LogWarning(
                "Swept the {Source} turn hold {HoldId} of organization {OrganizationId}, left open since {CreatedAt} (if it settles later it is still charged)",
                row.Source, row.Id, org, row.CreatedAt);
        }

        if (stale.Count > 0) await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The allowance the organization has used in its current period by the authoritative counter (open holds plus
    /// settled consumption); null when the plan is unlimited.
    /// </summary>
    public async Task<long?> AllowanceUsedAsync(Guid organizationId, CancellationToken ct = default)
    {
        AssistantAllowanceTerms terms = await _allowance.GetTermsAsync(organizationId, ct).ConfigureAwait(false);
        using IServiceScope scope = _scopes.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        return await AllowanceInUseAsync(db, organizationId, terms.PeriodStart, terms.PeriodEnd, Guid.Empty, ct).ConfigureAwait(false);
    }

    /// <summary>The authoritative allowance counter (<see cref="AssistantAllowanceService.AllowanceInUseAsync"/>).</summary>
    private static Task<long> AllowanceInUseAsync(
        InferenceApiDbContext db, Guid organizationId, DateTime start, DateTime end, Guid except, CancellationToken ct)
        => AssistantAllowanceService.AllowanceInUseAsync(db, organizationId, start, end, except, ct);

    private void ReportOvershoot(Guid organizationId, UsageSource source, Guid requestId, long meteredMillis, long overshootMillis)
    {
        _logger.LogWarning(
            "The {Source} turn of organization {OrganizationId} (request {RequestId}) metered {MeteredMillis} millicredits and was charged in full, {OvershootMillis} past the balance plus the overdraft headroom",
            source, organizationId, requestId, meteredMillis, overshootMillis);
        NonWorkflowChargeDiagnostics.SettleCapped.Add(1, new KeyValuePair<string, object?>("source", source.ToString()));
    }

    private void ReportCapped(Guid organizationId, UsageSource source, Guid requestId, long meteredMillis, long chargedMillis)
    {
        _logger.LogWarning(
            "Non-workflow {Source} charge for organization {OrganizationId} (request {RequestId}) capped: metered {MeteredMillis} millicredits, covered {ChargedMillis}; the rest is not charged",
            source, organizationId, requestId, meteredMillis, chargedMillis);
        NonWorkflowChargeDiagnostics.SettleCapped.Add(1, new KeyValuePair<string, object?>("source", source.ToString()));
    }

    /// <summary>
    /// Runs <paramref name="body"/> in one transaction on a fresh context holding the organization's ledger advisory
    /// lock (<c>pg_advisory_xact_lock(hashtext(org_id))</c>, the key <see cref="CreditLedger"/> uses, so the ledger
    /// calls inside re-enter it), then saves and commits. Any exception rolls everything back.
    /// </summary>
    private async Task<T> LockedAsync<T>(Guid organizationId, Func<InferenceApiDbContext, CreditLedger, Task<T>> body, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using IServiceScope scope = _scopes.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (db.Database.ProviderName == NpgsqlProvider)
        {
            string lockKey = organizationId.ToString();
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey}))", ct).ConfigureAwait(false);
        }

        T result = await body(db, new CreditLedger(db, _time)).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }
}
