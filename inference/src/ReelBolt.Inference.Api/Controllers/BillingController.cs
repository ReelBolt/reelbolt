using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Billing;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Paddle;
using ReelBolt.Inference.Api.Services.Billing.Promotions;
using ReelBolt.Inference.Api.Services.Billing.Reconcile;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;


namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Body of <c>POST /api/v1/billing/checkout</c>: exactly one of <c>plan</c> or <c>topUpCredits</c>, and an
/// optional promo code (an invalid one is refused, never silently dropped).
/// </summary>
public sealed record CheckoutRequestDto(string? Plan, int? Seats, int? TopUpCredits, string? PromoCode = null, string? Interval = null);

/// <summary>Body of <c>POST /api/v1/billing/checkout/confirm</c>.</summary>
public sealed record ConfirmCheckoutDto(string? SessionId);

/// <summary>Body of <c>POST /api/v1/billing/checkout/preview</c>: what a checkout would cost, as the provider prices it.</summary>
public sealed record CheckoutPreviewRequestDto(string? Plan, int? Seats, int? TopUpCredits, string? PromoCode, string? Interval = null);

/// <summary>Body of <c>POST /api/v1/billing/seats</c>.</summary>
public sealed record ChangeSeatsRequestDto(int Seats);

/// <summary>Body of <c>POST /api/v1/billing/subscription/change-plan</c>.</summary>
public sealed record ChangePlanRequestDto(string? Plan, int? Seats, string? Interval = null);

/// <summary>Credits (whole credits, i.e. millicredits / 1000 like the summary) and events over a window.</summary>
/// <param name="CostUsd">Our provider cost (COGS); null unless the caller is a platform admin.</param>
public sealed record UsageTotalsDto(double Credits, int Events, double? CostUsd);

/// <summary>One usage kind's consumed quantity, in that kind's own unit (tokens, seconds, bytes, GB-days).</summary>
public sealed record UsageUnitDto(string Kind, double Quantity);

/// <summary>
/// One group of the requested axis. <paramref name="Key"/> is the raw axis value (a UTC
/// <c>yyyy-MM-dd</c>, a project id, a kind or model name, a billing mode); <paramref name="Label"/> is
/// what to show instead of it.
/// </summary>
public sealed record UsageGroupDto(
    string Key,
    string Label,
    double Credits,
    int Events,
    double? CostUsd,
    IReadOnlyList<UsageUnitDto> Units);

/// <summary>The who-pays split, always reported regardless of <c>groupBy</c>.</summary>
public sealed record UsageModeDto(string Mode, double Credits, int Events, double? CostUsd);

/// <summary>The usage endpoint's body. The window is half-open: <c>[from, to)</c>.</summary>
public sealed record BillingUsageResponse(
    DateTime From,
    DateTime To,
    string GroupBy,
    UsageTotalsDto Totals,
    IReadOnlyList<UsageModeDto> ByBillingMode,
    IReadOnlyList<UsageGroupDto> Groups);

/// <summary>
/// Billing for the caller's active organization: summary, hosted checkout, customer portal, seat
/// changes, and the provider webhook receiver. With <c>Billing:Enabled=false</c> (self-host) every
/// endpoint answers 404. Starting a checkout or opening the portal needs the Owner or Billing role.
/// Nothing here grants credits or changes a subscription: only a verified webhook does
/// (see <see cref="BillingEventProcessor"/>).
/// </summary>
[ApiController]
[Route("api/v1/billing")]
public class BillingController(
    InferenceApiDbContext db,
    ICurrentUser currentUser,
    IOptions<BillingOptions> options,
    IEnumerable<IBillingProvider> providers,
    BillingEventProcessor processor,
    ITenantContext tenant,
    IOptions<PaddleOptions> paddleOptions,
    ILogger<BillingController> logger,
    ICreditAccountProvisioner? trialCredits = null,
    IStorageUsageService? storageUsage = null,
    IAssistantAllowanceService? assistantAllowance = null,
    TimeProvider? time = null,
    IBillingChangeLock? changeLock = null,
    IPromotionService? promotions = null,
    IOrganizationEntitlements? entitlements = null,
    IBillingReconciler? reconciler = null,
    IOptions<BillingProviderOptions>? providerSelection = null,
    ReelBolt.Inference.Api.Services.Telemetry.IProductEventEmitter? productEvents = null,
    CheckoutConfirmLimiter? confirmLimiter = null) : ControllerBase
{
    private readonly IReadOnlyList<IBillingProvider> _providers = providers.ToList();
    private readonly ICreditAccountProvisioner? _trialCredits = trialCredits;
    private readonly IBillingChangeLock _changeLock = changeLock ?? InProcessBillingChangeLock.Shared;

    private bool Disabled => !options.Value.Enabled;

    private DateTime UtcNow => (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    /// <summary>The provider taking new checkouts, previews and top-ups (<c>Billing:Provider</c>).</summary>
    private IBillingProvider? ActiveProvider()
        => BillingProviderSelector.Active(_providers, providerSelection?.Value);

    /// <summary>The provider registered under a name (the webhook route).</summary>
    private IBillingProvider? Provider(string name)
        => BillingProviderSelector.ByName(_providers, name);

    /// <summary>The provider that owns an existing subscription: the row's own, else the active one.</summary>
    private async Task<IBillingProvider?> SubscriptionProviderAsync(Guid orgId, CancellationToken ct)
    {
        Subscription? existing = await CurrentSubscriptionAsync(orgId, ct);
        return BillingProviderSelector.ForSubscription(_providers, providerSelection?.Value, existing?.Provider);
    }

    private static bool CanManage(OrganizationRole? role) => role is OrganizationRole.Owner or OrganizationRole.Billing;

    // ------------------------------------------------------------------ summary

    [HttpGet("summary")]
    [Filters.ConditionalGet]
    [Authorize]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        if (Disabled || currentUser.OrganizationId is not { } orgId || currentUser.OrgRole is null) return NotFound();

        // C6: the dashboard's billing page is the first thing a verified Personal-Owner sees that reads
        // a balance, so the one-time trial grant must already have landed by the time this answers.
        // It is lazy and idempotent (no-op when billing is off, a no-op on repeat calls, and never
        // throws), so putting it here is safe for the very request that surfaces the number.
        if (_trialCredits is not null) await _trialCredits.EnsureTrialGrantAsync(currentUser.UserId, orgId, ct);

        Subscription? sub = await CurrentSubscriptionAsync(orgId, ct);
        List<Plan> plans = await db.Plans.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.SortOrder).ToListAsync(ct);
        Plan? current = sub is null
            ? plans.FirstOrDefault(p => p.Kind == PlanKind.Trial)
            : plans.FirstOrDefault(p => p.Id == sub.PlanId) ?? await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == sub.PlanId, ct);
        CreditLedgerResult balance = await new CreditLedger(db).GetBalanceAsync(orgId, ct);
        IBillingProvider? provider = ActiveProvider();
        bool topUpAvailable = provider is not null && current is { TopUpAllowed: true } && sub is { Provider: not null };
        // The caller's billing authority, computed once: it gates both the canManageBilling flag and
        // whether the Paddle customer id (a handle into the merchant account) leaves in the paddle block.
        bool canManageBilling = CanManage(currentUser.OrgRole);

        // The browser half of the embedded checkout: the publishable token, the Paddle environment
        // this server bills through, and — per plan, below — the price id a checkout would actually
        // carry. All three come from the response the dashboard already fetches, deliberately: the web
        // image is built in Docker, so a NEXT_PUBLIC_* token could not be rotated without a rebuild,
        // and serving the environment from here means the browser can never be initialized against a
        // different Paddle account than the one this server creates transactions in.
        object? paddleClient = await PaddleClientAsync(provider, orgId, canManageBilling, ct);

        // Storage used against the plan's limit (null = unlimited), from the same definition the quota
        // enforces (docs/design/local-folder-sync-and-storage.md Part 2).
        StorageUsage? storage = storageUsage is null ? null : await storageUsage.GetUsageAsync(orgId, ct);

        // The month's included assistant credits (decision 9) and how much of them assistant chat and MCP used,
        // for the usage page's allowance meter. includedCredits null = unlimited.
        AssistantAllowance? allowance = assistantAllowance is null ? null : await assistantAllowance.GetAsync(orgId, ct);
        // A downgrade held until the period ends (BillingEventProcessor); null when none is pending.
        Plan? scheduledPlan = sub?.ScheduledPlanId is { } scheduledId
            ? plans.FirstOrDefault(p => p.Id == scheduledId) ?? await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == scheduledId, ct)
            : null;

        // The plan entitlements the dashboard needs to explain a choice before the server refuses it
        // (the server stays the authority): the tallest output short side, null when unlimited. Read
        // through the same entitlement service the plan gates use, so the two cannot disagree.
        OrganizationEntitlementSet? granted = entitlements is null ? null : await entitlements.GetAsync(orgId, ct);

        return Ok(new
        {
            paddle = paddleClient,
            billingProvider = provider?.Name,
            billingProviderDisplayName = provider?.DisplayName,
            canManageBilling = canManageBilling,
            subscription = current is null ? null : new
            {
                planKey = current.Key,
                planName = current.Name,
                status = (sub?.Status ?? SubscriptionStatus.Active).ToString(),
                seats = sub?.Seats ?? 1,
                interval = SubscriptionInterval.ToWire(SubscriptionInterval.Of(sub?.PeriodStart, sub?.PeriodEnd)),
                periodStart = sub?.PeriodStart,
                periodEnd = sub?.PeriodEnd,
                creditsRenewAt = AnnualCreditSchedule.CreditsRenewAt(sub, UtcNow),
                cancelAtPeriodEnd = sub?.CancelAtPeriodEnd ?? false,
                cancelEffectiveAt = sub is { CancelAtPeriodEnd: true } ? sub.CancelEffectiveAt ?? sub.PeriodEnd : null,
                scheduledChange = scheduledPlan is null ? null : new
                {
                    planKey = scheduledPlan.Key,
                    planName = scheduledPlan.Name,
                    seats = sub!.ScheduledSeats ?? 1,
                    effectiveAt = sub.ScheduledChangeAt ?? sub.PeriodEnd
                },
                hasBillingCustomer = provider is not null && await HasCustomerAsync(orgId, provider.Name, ct),
                // lets the change-plan modal say when a change ends the customer's promo.
                promoDiscount = await ActivePromoDiscountAsync(sub, UtcNow, ct)
            },
            credits = new
            {
                balance = balance.BalanceMillis / 1000.0,
                reserved = balance.ReservedMillis / 1000.0,
                available = balance.AvailableMillis / 1000.0,
                grants = await LiveGrantsAsync(orgId, ct)
            },
            firstVideoGrant = await FirstVideoGrantAsync(orgId, ct),
            firstVideoGrantStatus = await FirstVideoGrantStatusAsync(orgId, ct),
            storage = storage is null ? null : new { usedBytes = storage.UsedBytes, limitBytes = storage.LimitBytes },
            assistantAllowance = allowance is null ? null : new
            {
                includedCredits = allowance.IncludedMillis is { } included ? Credits(included) : (double?)null,
                usedCredits = Credits(allowance.UsedMillis),
                periodStart = allowance.PeriodStart,
                periodEnd = allowance.PeriodEnd
            },
            entitlements = granted is null ? null : new
            {
                maxOutputHeight = granted.MaxOutputHeight == OrganizationEntitlementSet.UnlimitedInt ? (int?)null : granted.MaxOutputHeight,
                // The lowest catalogue plan that includes each feature this plan lacks: what the web's upgrade callout names
                // ("Available on Pro"). Display only; a feature the plan already has is absent.
                unlocks = PlanUnlocks.UnlocksFor(granted)
            },
            topUp = new
            {
                available = topUpAvailable,
                packs = topUpAvailable ? provider!.TopUpPacks : []
            },
            // Only the plans this provider can actually sell, each carrying the price id its checkout
            // will use — the same one PaddleBillingProvider sends to /transactions — so the dashboard can
            // show Paddle's own localized price for it. A plan with no configured price id is omitted
            // rather than listed with a blank one: a missing price id must never reach Paddle.
            plans = plans
                .Where(p => p.Kind is PlanKind.Subscription or PlanKind.PerSeat)
                .Select(p => new { Plan = p, PriceId = PriceIdOf(p, provider) })
                .Where(x => x.PriceId is not null)
                .Select(x => new
                {
                    key = x.Plan.Key,
                    name = x.Plan.Name,
                    kind = x.Plan.Kind.ToString(),
                    priceMonthlyUsd = x.Plan.PriceMonthlyUsd,
                    priceAnnualUsd = PlanPricing.AnnualPriceUsd(x.Plan),
                    minSeats = x.Plan.MinSeats,
                    includedCredits = x.Plan.MonthlyCreditsMillis / 1000.0,
                    priceId = x.PriceId,
                    annualPriceId = provider is null ? null : BillingEventProcessor.AnnualPriceIdOf(x.Plan, provider.Name),
                    storageGb = PlanCardEntitlements(x.Plan).StorageGb,
                    entitlements = PlanCardEntitlements(x.Plan).Entitlements,
                    current = current is not null && x.Plan.Id == current.Id
                })
        });
    }

    /// <summary><c>granted</c> once the first-video grant was written, <c>unavailable</c> when a refusal reason is recorded, else <c>none</c>.</summary>
    private async Task<string> FirstVideoGrantStatusAsync(Guid orgId, CancellationToken ct)
    {
        string key = CreditAccountProvisioner.FirstVideoKey(orgId);
        if (await db.CreditLedgerEntries.AsNoTracking().AnyAsync(e => e.OrganizationId == orgId && e.IdempotencyKey == key, ct)) return "granted";
        return await db.TrialClaims.AsNoTracking().AnyAsync(c => c.OrganizationId == orgId && c.FirstVideoDeniedReason != null, ct) ? "unavailable" : "none";
    }

    /// <summary>The org's live first-video grant (Promo grant whose ledger entry carries the first-video key), or null.</summary>
    private async Task<object?> FirstVideoGrantAsync(Guid orgId, CancellationToken ct)
    {
        string key = CreditAccountProvisioner.FirstVideoKey(orgId);
        DateTime now = UtcNow;
        var grant = await (from e in db.CreditLedgerEntries.AsNoTracking()
                           join g in db.CreditGrants.AsNoTracking() on e.GrantId equals g.Id
                           where e.OrganizationId == orgId && e.IdempotencyKey == key && g.Source == CreditGrantSource.Promo
                                 && g.RemainingMillis > 0 && g.ExpiredAt == null && (g.ExpiresAt == null || g.ExpiresAt > now)
                           select new { g.AmountMillis, g.RemainingMillis, g.ExpiresAt }).FirstOrDefaultAsync(ct);
        return grant is null ? null : new
        {
            credits = grant.AmountMillis / 1000.0,
            expiresAt = grant.ExpiresAt,
            remaining = grant.RemainingMillis / 1000.0
        };
    }

    // ------------------------------------------------------------------ checkout

    [HttpPost("checkout")]
    [Authorize]
    public async Task<IActionResult> Checkout([FromBody] CheckoutRequestDto body, CancellationToken ct)
    {
        if (Disabled || currentUser.OrganizationId is not { } orgId || currentUser.OrgRole is null) return NotFound();
        if (!CanManage(currentUser.OrgRole)) return Forbid();
        if (ActiveProvider() is not { } provider) return StatusCode(503, new { error = "billing_not_configured" });

        // The token's emailVerified claim may be stale: the database decides.
        bool emailVerified = await db.ApplicationUsers.AsNoTracking()
            .AnyAsync(u => u.Id == currentUser.UserId && u.EmailVerifiedAt != null && !u.IsServiceAccount, ct);
        if (!emailVerified)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "EMAIL_NOT_VERIFIED",
                error = "email_not_verified",
                message = "Verify your email address before starting a checkout. Check your inbox for the verification link."
            });
        }

        bool isTopUp = body.TopUpCredits is not null;
        if (isTopUp == !string.IsNullOrWhiteSpace(body.Plan))
        {
            return BadRequest(new { error = "choose_plan_or_topup", message = "Send exactly one of plan or topUpCredits." });
        }

        Subscription? sub = await CurrentSubscriptionAsync(orgId, ct);
        string? customerId = await CustomerIdAsync(orgId, provider.Name, ct);

        if (isTopUp)
        {
            Plan? currentPlan = sub is null ? null : await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == sub.PlanId, ct);
            // D9: top-ups are for paying customers only. A free Trial org upgrades first.
            if (sub?.Provider is null || currentPlan is not { TopUpAllowed: true } || sub.Status is not (SubscriptionStatus.Active or SubscriptionStatus.Trialing))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "topup_requires_paid_plan", message = "Top-up credits are available on paid plans." });
            }

            if (provider.TopUpPriceId(body.TopUpCredits!.Value) is null)
            {
                return BadRequest(new { error = "unknown_topup_pack", packs = provider.TopUpPacks });
            }

            CheckoutTarget packTarget = new(provider.TopUpPriceId(body.TopUpCredits!.Value)!, body.TopUpCredits);
            PromotionOutcome<CheckoutPromotions> packPromos = await ResolvePromotionsAsync(orgId, packTarget, body.PromoCode, ct);
            if (packPromos.Error is { } packError) return packError.ToResult();

            return await StartCheckoutAsync(provider, new CheckoutRequest(orgId, currentUser.UserId, currentUser.Email, null, 1, body.TopUpCredits, customerId), packTarget, packPromos.Value!, ct);
        }

        Plan? plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Key == body.Plan && p.IsActive, ct);
        if (plan is null || plan.Kind is not (PlanKind.Subscription or PlanKind.PerSeat) || PriceIdOf(plan, provider) is null)
        {
            return BadRequest(new { error = "plan_not_purchasable" });
        }

        if (IntervalProblem(body.Interval, plan, provider, out BillingInterval interval) is { } intervalProblem) return intervalProblem;

        // A second subscription would bill twice. Plan changes on a live subscription go through change-plan.
        if (sub?.Provider is not null && sub.Status is SubscriptionStatus.Active or SubscriptionStatus.Trialing or SubscriptionStatus.PastDue or SubscriptionStatus.Paused)
        {
            return Conflict(new { error = "already_subscribed", message = "This workspace already has a subscription. Change plan from the Plan & billing page instead of starting a second checkout." });
        }

        int seats = 1;
        if (plan.Kind == PlanKind.PerSeat)
        {
            int min = plan.MinSeats ?? 1;
            seats = body.Seats ?? min;
            if (seats < min) return BadRequest(new { error = "below_min_seats", minSeats = min });
        }

        // The webhook gap (F-04): paid at the provider, but the subscription row only lands with the first
        // webhook. An org that has a customer may have just paid, so ask the provider. A provider failure
        // refuses the checkout: a retry costs a minute, a double subscription costs a refund.
        if (customerId is not null)
        {
            try
            {
                if (await provider.HasLiveSubscriptionAsync(customerId, orgId, ct))
                {
                    // Its subscription.created webhook may be lost rather than late: heal it now if Paddle's
                    // subscription is old enough, then the answer is the one a landed webhook would give.
                    if (await HealUnderClaimAsync(orgId, ct))
                    {
                        return Conflict(new { error = "already_subscribed", message = "This workspace already has a subscription. Change plan from the Plan & billing page instead of starting a second checkout." });
                    }

                    return Conflict(new { error = "subscription_pending", message = "This workspace's new plan is still being confirmed. Refresh this page in a minute." });
                }
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Billing provider {Provider} could not list the customer's subscriptions.", provider.Name);
                return StatusCode(502, new { error = "billing_provider_error" });
            }
        }

        CheckoutTarget planTarget = new(PriceIdOf(plan, provider, interval)!, null);
        PromotionOutcome<CheckoutPromotions> planPromos = await ResolvePromotionsAsync(orgId, planTarget, body.PromoCode, ct);
        if (planPromos.Error is { } planError) return planError.ToResult();

        return await StartCheckoutAsync(provider, new CheckoutRequest(orgId, currentUser.UserId, currentUser.Email, plan, seats, null, customerId, Interval: interval), planTarget, planPromos.Value!, ct);
    }

    private async Task<IActionResult> StartCheckoutAsync(IBillingProvider provider, CheckoutRequest request, CheckoutTarget target, CheckoutPromotions promos, CancellationToken ct)
    {
        // Reserve first (contract C8): the caps are checked and the PromotionCheckout row inserted under
        // the promotions' locks BEFORE the provider is asked for a transaction, so what the provider
        // checkout carries is exactly what was reserved. The webhook grants a bonus only from this record
        // (never from the checkout's client-visible custom data).
        CheckoutReservation reservation = CheckoutReservation.None;
        if (promotions is not null)
        {
            PromotionOutcome<CheckoutReservation> reserved = await promotions.ReserveCheckoutAsync(request.OrganizationId, request.UserId, target, promos, ct);
            if (reserved.Error is { } error) return error.ToResult();
            reservation = reserved.Value!;
        }

        CheckoutPromotions attached = reservation.Promotions;
        CheckoutSession session;
        try
        {
            try
            {
                session = await provider.CreateCheckoutAsync(WithPromotions(request, attached), ct);
            }
            catch (Exception ex)
            {
                if (reservation.Id is { } released)
                {
                    await promotions!.ReleaseAsync(released, CancellationToken.None);
                }

                if (ex is BillingProviderException { Status: 503, Code: "stripe_not_configured" })
                {
                    logger.LogError(ex, "Billing provider {Provider} is not configured for checkout: {Code}.", provider.Name, ((BillingProviderException)ex).Code);
                    return StatusCode(503, new { error = "billing_not_configured" });
                }

                if (ex is not HttpRequestException)
                {
                    throw;
                }

                logger.LogWarning(ex, "Billing provider {Provider} rejected the checkout.", provider.Name);
                return StatusCode(502, new { error = "billing_provider_error" });
            }

            // F-5: the transaction exists at the provider now. A client abort must not leave it unattached
            // (orphaned, never swept), so the attach comes first and does not observe the request token.
            bool live = reservation.Id is not { } reservationId
                || await promotions!.AttachTransactionAsync(reservationId, session.TransactionId, CancellationToken.None);

            // Plan decision 5: remember the provider customer now, so "Manage billing" works and a
            // retried checkout reuses it before the first webhook arrives.
            if (session.CustomerId is { Length: > 0 } customerId && customerId != request.ExistingCustomerId)
            {
                await processor.RememberCustomerAsync(request.OrganizationId, provider.Name, customerId, ct);
            }

            // F-5: a parallel checkout superseded this one before it was attached. Its transaction is
            // already being cancelled; handing out its URL would be a payable discount past the caps.
            if (!live)
            {
                return PromotionErrors.CheckoutSuperseded.ToResult();
            }

            // A subscription checkout is watched so its subscription.created webhook, if lost, is healed.
            // A top-up creates no subscription, so there is nothing for the reconciler to compare.
            if (request.Plan is not null)
            {
                await RecordChangeAsync(request.OrganizationId, BillingReconciler.CheckoutKind);
            }
        }
        finally
        {
            // C10 / F-5: the reservations this checkout superseded must not stay payable, whatever happened
            // to it (success, provider failure, exception, client abort). Best effort and independent of the
            // request token; a failure is retried by the promotion checkout sweeper.
            if (reservation.Superseded is { Count: > 0 } superseded)
            {
                await promotions!.CancelSupersededAsync(superseded, CancellationToken.None);
            }
        }

        // product-analytics: the provider checkout exists, so checkout has started. Best-effort.
        if (productEvents is not null)
        {
            await productEvents.FirstAsync(
                request.UserId, request.OrganizationId,
                ReelBolt.Inference.Api.Services.Telemetry.ProductEventNames.CheckoutStarted, ct);
        }

        return Ok(new
        {
            checkoutUrl = session.Url,
            transactionId = session.TransactionId,
            discount = attached.Discount is { } d ? new { headline = d.PublicHeadline } : null
        });
    }

    /// <summary>
    /// What a checkout would cost with its promotions applied. The money strings are the provider's own
    /// formatted totals, returned verbatim. An invalid code is 400 <c>promo_code_invalid</c>.
    /// </summary>
    [HttpPost("checkout/preview")]
    [Authorize]
    public async Task<IActionResult> Preview([FromBody] CheckoutPreviewRequestDto body, CancellationToken ct)
    {
        if (Disabled || currentUser.OrganizationId is not { } orgId || currentUser.OrgRole is null) return NotFound();
        if (!CanManage(currentUser.OrgRole)) return Forbid();
        if (ActiveProvider() is not { } provider) return StatusCode(503, new { error = "billing_not_configured" });

        bool isTopUp = body.TopUpCredits is not null;
        if (isTopUp == !string.IsNullOrWhiteSpace(body.Plan))
        {
            return BadRequest(new { error = "choose_plan_or_topup", message = "Send exactly one of plan or topUpCredits." });
        }

        string priceId;
        int quantity = 1;
        if (isTopUp)
        {
            if (provider.TopUpPriceId(body.TopUpCredits!.Value) is not { } packPriceId)
            {
                return BadRequest(new { error = "unknown_topup_pack", packs = provider.TopUpPacks });
            }

            priceId = packPriceId;
        }
        else
        {
            Plan? plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Key == body.Plan && p.IsActive, ct);
            if (plan is null || plan.Kind is not (PlanKind.Subscription or PlanKind.PerSeat) || PriceIdOf(plan, provider) is not { } planPriceId)
            {
                return BadRequest(new { error = "plan_not_purchasable" });
            }

            if (IntervalProblem(body.Interval, plan, provider, out BillingInterval previewInterval) is { } intervalProblem) return intervalProblem;
            priceId = previewInterval == BillingInterval.Annual ? PriceIdOf(plan, provider, previewInterval)! : planPriceId;
            if (plan.Kind == PlanKind.PerSeat)
            {
                int min = plan.MinSeats ?? 1;
                quantity = body.Seats ?? min;
                if (quantity < min) return BadRequest(new { error = "below_min_seats", minSeats = min });
            }
        }

        PromotionOutcome<CheckoutPromotions> outcome = await ResolvePromotionsAsync(orgId, new CheckoutTarget(priceId, body.TopUpCredits), body.PromoCode, ct);
        if (outcome.Error is { } error) return error.ToResult();

        CheckoutPromotions promos = outcome.Value!;
        PricePreview price;
        try
        {
            price = await provider.PreviewPriceAsync([new BillingLineItem(priceId, quantity)], promos.Discount?.ProviderDiscountId, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Billing provider {Provider} rejected the price preview.", provider.Name);
            return StatusCode(502, new { error = "billing_provider_error" });
        }

        Dictionary<string, object?> preview = new()
        {
            ["currencyCode"] = price.CurrencyCode,
            ["subtotal"] = price.Subtotal,
            ["discount"] = price.Discount,
            ["total"] = price.Total,
            ["promotion"] = promos.Discount is { } d ? new { headline = d.PublicHeadline, kind = PromotionRules.Camel(d.Kind) } : null,
            ["bonusCredits"] = BonusCredits(promos.Bonus, body.TopUpCredits)
        };
        // Additive raw amounts, only from a provider that returns integer totals (not Paddle).
        if (price.SubtotalMinor is { } subtotalMinor) preview["subtotalMinor"] = subtotalMinor;
        if (price.DiscountMinor is { } discountMinor) preview["discountMinor"] = discountMinor;
        if (price.TotalMinor is { } totalMinor) preview["totalMinor"] = totalMinor;
        if (price.UnitSubtotalMinor is { } unitSubtotalMinor) preview["unitSubtotalMinor"] = unitSubtotalMinor;
        if (price.UnitTotalMinor is { } unitTotalMinor) preview["unitTotalMinor"] = unitTotalMinor;
        if (price.MinorUnitExponent is { } exponent) preview["minorUnitExponent"] = exponent;
        return Ok(preview);
    }

    private static int? BonusCredits(Promotion? bonus, int? packCredits)
        => bonus?.BonusPercent is { } pct && packCredits is { } credits ? credits * pct / 100 : null;

    private Task<PromotionOutcome<CheckoutPromotions>> ResolvePromotionsAsync(Guid orgId, CheckoutTarget target, string? code, CancellationToken ct)
    {
        if (promotions is not null)
        {
            return promotions.ResolveCheckoutPromotionAsync(orgId, currentUser.UserId, target, code, ct);
        }

        // No promotion service wired: there is nothing a code could match, and no automatic promotion.
        return Task.FromResult(PromotionService.NormalizeCode(code) is null
            ? PromotionOutcome<CheckoutPromotions>.Success(CheckoutPromotions.None)
            : (PromotionOutcome<CheckoutPromotions>)PromotionErrors.Invalid);
    }

    private static CheckoutRequest WithPromotions(CheckoutRequest request, CheckoutPromotions promos) => request with
    {
        DiscountId = promos.Discount?.ProviderDiscountId,
        PromotionId = promos.Discount?.Id,
        BonusPromotionId = promos.Bonus?.Id
    };

    // ------------------------------------------------------------------ portal and seats

    [HttpPost("portal")]
    [Authorize]
    public async Task<IActionResult> Portal(CancellationToken ct)
    {
        if (Disabled || currentUser.OrganizationId is not { } orgId || currentUser.OrgRole is null) return NotFound();
        if (!CanManage(currentUser.OrgRole)) return Forbid();
        if (await SubscriptionProviderAsync(orgId, ct) is not { } provider) return StatusCode(503, new { error = "billing_not_configured" });

        string? customerId = await CustomerIdAsync(orgId, provider.Name, ct);
        if (customerId is null)
        {
            return Conflict(new { error = "no_billing_customer", message = "This organization has not purchased anything yet." });
        }

        Subscription? sub = await CurrentSubscriptionAsync(orgId, ct);
        try
        {
            PortalSession session = await provider.CreatePortalSessionAsync(customerId, sub?.ProviderSubscriptionId, ct);
            return Ok(new { url = session.OverviewUrl, cancelUrl = session.CancelUrl, updatePaymentMethodUrl = session.UpdatePaymentMethodUrl });
        }
        catch (BillingProviderException ex) when (ex.Status == 503 && ex.Code == "stripe_not_configured")
        {
            logger.LogError(ex, "Billing provider {Provider} is not configured for the portal: {Code}.", provider.Name, ex.Code);
            return StatusCode(503, new { error = "billing_not_configured" });
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Billing provider {Provider} rejected the portal request.", provider.Name);
            return StatusCode(502, new { error = "billing_provider_error" });
        }
    }

    /// <summary>
    /// Sets the seat count of a per-seat subscription, through the same path as a plan change: an
    /// increase is prorated and effective now (the webhook grants the extra seats' credits pro rata); a
    /// decrease is billed from the next period and held by the webhook until then, so seats paid for are
    /// never refunded as credits. The local row changes only when the provider's webhook arrives.
    /// </summary>
    [HttpPost("seats")]
    [Authorize]
    public async Task<IActionResult> ChangeSeats([FromBody] ChangeSeatsRequestDto body, CancellationToken ct)
    {
        if (Disabled || currentUser.OrganizationId is not { } orgId || currentUser.OrgRole is null) return NotFound();
        if (!CanManage(currentUser.OrgRole)) return Forbid();
        if (await SubscriptionProviderAsync(orgId, ct) is not { } provider) return StatusCode(503, new { error = "billing_not_configured" });

        Subscription? sub = await CurrentSubscriptionAsync(orgId, ct);
        Plan? plan = sub is null ? null : await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == sub.PlanId, ct);
        if (sub?.ProviderSubscriptionId is null || plan is not { Kind: PlanKind.PerSeat } || PriceIdOf(plan, provider) is not { } priceId)
        {
            return Conflict(new { error = "not_a_per_seat_subscription" });
        }

        if (SeatsProblem(provider, plan, body.Seats) is { } invalid) return invalid;
        // An annual subscription's items carry the annual price id; a seat change keeps the interval.
        if (SubscriptionInterval.Of(sub.PeriodStart, sub.PeriodEnd) == BillingInterval.Annual)
        {
            priceId = PriceIdOf(plan, provider, BillingInterval.Annual) ?? priceId;
        }

        return await UnderChangeClaimAsync(orgId, async healed =>
        {
            if (healed)
            {
                // The lost webhook was just applied: judge the request against the healed row.
                sub = await LiveSubscriptionAsync(orgId, provider, ct);
                plan = sub is null ? null : await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == sub.PlanId, ct);
                if (sub is null || plan is not { Kind: PlanKind.PerSeat } || PriceIdOf(plan, provider) is not { } healedPriceId)
                {
                    return Conflict(new { error = "not_a_per_seat_subscription" });
                }

                if (SeatsProblem(provider, plan, body.Seats) is { } healedInvalid) return healedInvalid;
                priceId = SubscriptionInterval.Of(sub.PeriodStart, sub.PeriodEnd) == BillingInterval.Annual
                    ? PriceIdOf(plan, provider, BillingInterval.Annual) ?? healedPriceId
                    : healedPriceId;
            }

            return await RequestChangeAsync(provider, sub, plan, plan, priceId, body.Seats, SeatsKind, ct, SubscriptionInterval.Of(sub.PeriodStart, sub.PeriodEnd), SubscriptionInterval.Of(sub.PeriodStart, sub.PeriodEnd));
        }, ct);
    }

    // ------------------------------------------------------------------ cancel, resume, change plan

    /// <summary>
    /// Schedules the live subscription to cancel at the end of its period (undoable with resume). Only
    /// asks the provider: the row changes when its webhook arrives, hence 202. Already scheduled (on the
    /// row, or on Paddle with the webhook still to land) is an idempotent 200; the Paddle case also records a
    /// <c>cancel</c> watch and tries a lazy reconcile (an overdue webhook heals; the answer stays 200). Like every subscription
    /// change it runs under the org's change claim and is <c>409 change_pending</c> while Paddle's live
    /// items show a plan or seat change whose webhook has not landed.
    /// </summary>
    [HttpPost("subscription/cancel")]
    [Authorize]
    public async Task<IActionResult> CancelSubscription(CancellationToken ct)
    {
        if (Disabled || currentUser.OrganizationId is not { } orgId || currentUser.OrgRole is null) return NotFound();
        if (!CanManage(currentUser.OrgRole)) return Forbid();
        if (await SubscriptionProviderAsync(orgId, ct) is not { } provider) return StatusCode(503, new { error = "billing_not_configured" });

        return await UnderChangeClaimAsync(orgId, async _ =>
        {
            if (await LiveSubscriptionAsync(orgId, provider, ct) is not { } sub) return NoSubscription();
            if (sub.CancelAtPeriodEnd)
            {
                return Ok(new { status = "cancel_scheduled", effectiveAt = sub.CancelEffectiveAt ?? sub.PeriodEnd });
            }

            (ProviderSubscriptionState? live, IActionResult? unreadable) = await ReadLiveAsync(provider, sub, ct);
            if (unreadable is not null) return unreadable;

            // A cancel already sent whose webhook has not landed: same answer as a confirmed one, nothing sent.
            // Its webhook may be lost rather than late: watch it, and heal now if it is overdue (QA-3).
            if (live!.CancelScheduled)
            {
                await RecordChangeAsync(orgId, CancelKind);
                await HealHeldAsync(orgId, ct);
                return Ok(new { status = "cancel_scheduled", effectiveAt = live.PeriodEnd ?? sub.PeriodEnd });
            }

            if (!await IsSettledAsync(live, sub, provider, ct)) return DriftPending();

            if (await CallProviderAsync(provider, "cancel", () => provider.CancelSubscriptionAsync(sub.ProviderSubscriptionId!, ct)) is { } failed) return failed;
            await RecordChangeAsync(orgId, CancelKind);
            return Accepted(new { status = "cancel_requested", effectiveAt = sub.PeriodEnd });
        }, ct);
    }

    /// <summary>
    /// Removes a scheduled cancellation ("Keep my plan"). Nothing scheduled on the row is an idempotent 200;
    /// a resume Paddle already applied (webhook still to land) is 202 with nothing sent, a <c>resume</c> watch and a
    /// lazy reconcile attempt (the answer stays 202); an unconfirmed plan
    /// or seat change is <c>409 change_pending</c>.
    /// </summary>
    [HttpPost("subscription/resume")]
    [Authorize]
    public async Task<IActionResult> ResumeSubscription(CancellationToken ct)
    {
        if (Disabled || currentUser.OrganizationId is not { } orgId || currentUser.OrgRole is null) return NotFound();
        if (!CanManage(currentUser.OrgRole)) return Forbid();
        if (await SubscriptionProviderAsync(orgId, ct) is not { } provider) return StatusCode(503, new { error = "billing_not_configured" });

        return await UnderChangeClaimAsync(orgId, async _ =>
        {
            if (await LiveSubscriptionAsync(orgId, provider, ct) is not { } sub) return NoSubscription();
            if (!sub.CancelAtPeriodEnd)
            {
                return Ok(new { status = "active" });
            }

            (ProviderSubscriptionState? live, IActionResult? unreadable) = await ReadLiveAsync(provider, sub, ct);
            if (unreadable is not null) return unreadable;

            // A resume already sent whose webhook has not landed: Paddle has nothing scheduled, nothing to send.
            // Its webhook may be lost rather than late: heal now if it is overdue (QA-3).
            if (!live!.CancelScheduled)
            {
                await RecordChangeAsync(orgId, ResumeKind);
                await HealHeldAsync(orgId, ct);
                return Accepted(new { status = "resume_requested" });
            }

            if (!await IsSettledAsync(live, sub, provider, ct)) return DriftPending();

            if (await CallProviderAsync(provider, "resume", () => provider.ResumeSubscriptionAsync(sub.ProviderSubscriptionId!, ct)) is { } failed) return failed;
            await RecordChangeAsync(orgId, ResumeKind);
            return Accepted(new { status = "resume_requested" });
        }, ct);
    }

    /// <summary>
    /// Moves the live subscription to another self-serve plan (and/or seat count). Plan decision 1: a
    /// higher (or equal) monthly price × seats is an upgrade, prorated and effective now — the webhook
    /// then grants the credit difference; a lower one is a downgrade billed from the next period, whose
    /// entitlements the webhook holds until the period ends. Choosing the current plan while a downgrade
    /// is held is "keep my plan": the current items go back, billed from the next period (already paid).
    /// </summary>
    [HttpPost("subscription/change-plan")]
    [Authorize]
    public async Task<IActionResult> ChangePlan([FromBody] ChangePlanRequestDto body, CancellationToken ct)
    {
        if (Disabled || currentUser.OrganizationId is not { } orgId || currentUser.OrgRole is null) return NotFound();
        if (!CanManage(currentUser.OrgRole)) return Forbid();
        if (await SubscriptionProviderAsync(orgId, ct) is not { } provider) return StatusCode(503, new { error = "billing_not_configured" });

        Plan? target = string.IsNullOrWhiteSpace(body.Plan)
            ? null
            : await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Key == body.Plan && p.IsActive, ct);
        if (target is null)
        {
            return BadRequest(new { error = "unknown_plan" });
        }

        // A plan without a list price is sold by hand (it would also count as $0 when choosing the direction).
        if (target.Kind is not (PlanKind.Subscription or PlanKind.PerSeat) || target.PriceMonthlyUsd is null || PriceIdOf(target, provider) is not { } priceId)
        {
            return BadRequest(new { error = "plan_not_self_serve", message = "This plan cannot be bought from the dashboard." });
        }

        if (await LiveSubscriptionAsync(orgId, provider, ct) is not { } sub) return NoSubscription();
        (Plan current, int seats, IActionResult? invalid) = await ChangePlanTargetAsync(provider, sub, target, body.Seats, ct);
        if (invalid is not null) return invalid;

        // The interval defaults to the subscription's own; annual to monthly is not supported in-app
        // (cancel at renewal and subscribe monthly), monthly to annual is an immediate, prorated change.
        BillingInterval currentInterval = SubscriptionInterval.Of(sub.PeriodStart, sub.PeriodEnd);
        BillingInterval interval = currentInterval;
        if (!string.IsNullOrWhiteSpace(body.Interval))
        {
            if (IntervalProblem(body.Interval, target, provider, out interval) is { } intervalProblem) return intervalProblem;
        }
        else if (currentInterval == BillingInterval.Annual && IntervalProblem("annual", target, provider, out _) is { } annualProblem)
        {
            return annualProblem;
        }

        if (currentInterval == BillingInterval.Annual && interval == BillingInterval.Monthly)
        {
            return Conflict(new { error = "interval_change_not_supported", message = "An annual subscription cannot be switched to monthly here. Cancel it at renewal and subscribe monthly." });
        }

        priceId = PriceIdOf(target, provider, interval)!;

        return await UnderChangeClaimAsync(orgId, async healed =>
        {
            if (healed)
            {
                // The lost webhook was just applied: judge the request against the healed row.
                if (await LiveSubscriptionAsync(orgId, provider, ct) is not { } healedSub) return NoSubscription();
                sub = healedSub;
                (current, seats, invalid) = await ChangePlanTargetAsync(provider, sub, target, body.Seats, ct);
                if (invalid is not null) return invalid;
            }

            return await RequestChangeAsync(provider, sub, current, target, priceId, seats, ChangePlanKind, ct, currentInterval, interval);
        }, ct);
    }

    /// <summary>The row's current plan and the seat count a change-plan asks for, or the 400 that refuses it.</summary>
    private async Task<(Plan Current, int Seats, IActionResult? Invalid)> ChangePlanTargetAsync(IBillingProvider provider, Subscription sub, Plan target, int? requestedSeats, CancellationToken ct)
    {
        Plan current = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == sub.PlanId, ct);
        int seats = 1;
        if (target.Kind == PlanKind.PerSeat)
        {
            seats = requestedSeats ?? (target.Id == current.Id ? sub.Seats : target.MinSeats ?? 1);
            if (SeatsProblem(provider, target, seats) is { } invalid) return (current, seats, invalid);
        }

        return (current, seats, null);
    }

    /// <summary>
    /// Security R1: runs one subscription change (seats, change-plan, cancel, resume) under the org's
    /// change claim, held from before the live provider read until the provider's PATCH/POST returns
    /// (success, refusal or exception). Never waits: a claim another request holds is
    /// <c>409 change_pending</c>, and that request then sees the first one's items when it retries.
    /// <para>
    /// A change refused only because Paddle's live state disagrees with the row (<see cref="DriftPending"/>)
    /// may be a lost webhook rather than a late one: the reconciler runs under this same claim and, when it
    /// healed the row, the change runs once more (its argument <c>true</c>: reload the row) — never a
    /// second reconcile. Any other reconcile outcome keeps the <c>409 change_pending</c>.
    /// </para>
    /// </summary>
    private async Task<IActionResult> UnderChangeClaimAsync(Guid orgId, Func<bool, Task<IActionResult>> change, CancellationToken ct)
    {
        IAsyncDisposable? claim;
        try
        {
            claim = await _changeLock.TryAcquireAsync(orgId, ct);
        }
        catch (BillingChangeLockCapacityException)
        {
            // S-4/S-23: this replica's lock connections are all in use; nothing about this org's change.
            logger.LogWarning("Billing change of organization {Org} refused: every change lock connection of this replica is in use.", orgId);
            return BillingBusy();
        }

        await using (claim)
        {
            if (claim is null) return ChangePending();

            IActionResult result = await change(false);
            if (result is DriftPendingResult && await HealHeldAsync(orgId, ct)) result = await change(true);

            // The marker never leaves the controller: the client sees the plain 409 change_pending.
            return result is DriftPendingResult ? ChangePending() : result;
        }
    }

    private const string CancelKind = "cancel";
    private const string ResumeKind = "resume";
    private const string ChangePlanKind = "change_plan";
    private const string KeepKind = "keep";
    private const string SeatsKind = "seats";

    private const string ChangePendingMessage = "Your last change is still being confirmed. Refresh this page in a minute, then try again.";

    private ConflictObjectResult ChangePending()
        => Conflict(new { error = "change_pending", message = ChangePendingMessage });

    private const string BillingBusyMessage = "Billing is busy right now. Try again in a few seconds.";

    /// <summary>Seconds a <c>503 billing_busy</c> asks the client to wait (<c>Retry-After</c>).</summary>
    private const int BillingBusyRetryAfterSeconds = 5;

    /// <summary>
    /// S-4/S-23: <c>503 billing_busy</c> with <c>Retry-After: 5</c>, when every billing change lock connection
    /// of this replica is in use. Retryable, and unlike <c>409 change_pending</c> it says nothing about the
    /// organization's last change.
    /// </summary>
    private ObjectResult BillingBusy()
    {
        Response.Headers.RetryAfter = BillingBusyRetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "billing_busy", message = BillingBusyMessage });
    }

    /// <summary>
    /// The same <c>409 change_pending</c> as <see cref="ChangePending"/>, marking that Paddle's live state
    /// disagrees with the row (not lock contention), so <see cref="UnderChangeClaimAsync"/> may heal it.
    /// </summary>
    private sealed class DriftPendingResult(object value) : ConflictObjectResult(value);

    private DriftPendingResult DriftPending() => new(new { error = "change_pending", message = ChangePendingMessage });

    /// <summary>
    /// A lazy reconcile of the org under the change claim its caller already holds. True only when the
    /// reconciler applied the provider's state (a webhook was lost); without a reconciler, or on any other
    /// outcome or failure (already logged by the reconciler), false and nothing more is done.
    /// </summary>
    private async Task<bool> HealHeldAsync(Guid orgId, CancellationToken ct)
    {
        if (reconciler is null) return false;
        try
        {
            ReconcileResult healed = await reconciler.ReconcileHeldAsync(orgId, ReconcileTrigger.Lazy, force: false, ct);
            return healed.Outcome == ReconcileOutcome.Healed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Lazy reconcile of organization {Org} failed; answering change pending.", orgId);
            return false;
        }
    }

    /// <summary>
    /// Checkout's subscription-pending path: takes the org's claim (held, or no lock connection free on this
    /// replica → not healed) and reconciles under it.
    /// </summary>
    private async Task<bool> HealUnderClaimAsync(Guid orgId, CancellationToken ct)
    {
        if (reconciler is null) return false;
        IAsyncDisposable? claim;
        try
        {
            claim = await _changeLock.TryAcquireAsync(orgId, ct);
        }
        catch (BillingChangeLockCapacityException)
        {
            logger.LogWarning("Checkout heal of organization {Org} skipped: every change lock connection of this replica is in use.", orgId);
            return false;
        }

        await using (claim)
        {
            return claim is not null && await HealHeldAsync(orgId, ct);
        }
    }

    /// <summary>
    /// Opens the org's reconcile watch after Paddle accepted a change (a 202) or a checkout session was
    /// created. The change already reached Paddle, so a failure here is logged and never changes the answer.
    /// </summary>
    private async Task RecordChangeAsync(Guid orgId, string kind)
    {
        if (reconciler is null) return;
        try
        {
            // S-15: not the request token. The change already reached Paddle, so a client abort now must not
            // skip its watch, nor (by rethrowing) the promo-discount removal record written after it.
            await reconciler.RecordChangeRequestedAsync(orgId, kind, currentUser.UserId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record the {Kind} change of organization {Org} for reconciliation; the change was accepted.", kind, orgId);
        }
    }

    /// <summary><c>400 below_min_seats</c> / <c>400 too_many_seats</c> for a per-seat target, else null.</summary>
    private BadRequestObjectResult? SeatsProblem(IBillingProvider provider, Plan target, int seats)
    {
        int min = target.MinSeats ?? 1;
        if (seats < min) return BadRequest(new { error = "below_min_seats", minSeats = min });
        int max = provider.MaxSelfServeSeats;
        if (seats > max)
        {
            return BadRequest(new { error = "too_many_seats", maxSeats = max, message = $"Self-serve plans go up to {max} seats. Contact us for a larger team." });
        }

        return null;
    }

    /// <summary>
    /// The one path that asks the provider to replace a subscription's items, shared by change-plan and
    /// seats so a seat decrease cannot be billed differently from a plan downgrade. Answers 202
    /// <c>{direction, effective, effectiveAt, creditGrantMillis, seats, message}</c>. Reads the provider's
    /// live subscription first: <c>409 change_pending</c> while its items disagree with the row (the last
    /// change's webhook has not landed), <c>409 downgrade_scheduled</c> for anything but "keep" while a
    /// downgrade is held, <c>502 billing_provider_error</c> when it cannot be read (nothing is sent).
    /// </summary>
    private async Task<IActionResult> RequestChangeAsync(IBillingProvider provider, Subscription sub, Plan current, Plan target, string priceId, int seats, string kind, CancellationToken ct, BillingInterval currentInterval = BillingInterval.Monthly, BillingInterval targetInterval = BillingInterval.Monthly)
    {
        if (sub.CancelAtPeriodEnd)
        {
            return Conflict(new { error = "cancel_scheduled", message = "Your plan is set to cancel. Keep your plan first, then change it." });
        }

        IReadOnlyList<BillingLineItem> items = [new BillingLineItem(priceId, seats)];
        bool keep = target.Id == current.Id && seats == sub.Seats && currentInterval == targetInterval;
        bool downgradeHeld = sub.ScheduledPlanId is not null;
        if (keep && !downgradeHeld)
        {
            return Conflict(new { error = "no_change", message = "This workspace is already on that plan and seat count." });
        }

        // Re-audit F2: Paddle already bills the held (lower) items, so any other change would be charged
        // from those but credited from the row's plan. "Keep" first, then change.
        if (downgradeHeld && !keep)
        {
            return Conflict(new { error = "downgrade_scheduled", message = "Keep your current plan first, then change it." });
        }

        // Re-audit F1: the row only moves when the webhook lands, Paddle's items move at once. Judge the
        // request against Paddle's live items, and refuse while they disagree with the row (a change is
        // still in flight): judged against the stale row, a decrease could pass as an upgrade and be
        // refunded prorated_immediately while the first webhook still grants the increase.
        (ProviderSubscriptionState? live, IActionResult? unreadable) = await ReadLiveAsync(provider, sub, ct);
        if (unreadable is not null) return unreadable;

        if (live!.CancelScheduled)
        {
            return Conflict(new { error = "cancel_scheduled", message = "Your plan is set to cancel. Keep your plan first, then change it." });
        }

        (Plan? livePlan, int liveSeats) = await LivePlanAsync(live, provider, ct);
        bool settled = IsRowPlan(livePlan, liveSeats, sub);
        if ((!downgradeHeld && !settled) || !IsRowPeriod(live, sub))
        {
            return DriftPending();
        }

        if (keep)
        {
            if (await CallProviderAsync(provider, "plan change", () => provider.ChangePlanAsync(sub.ProviderSubscriptionId!, items, PlanChangeTiming.NextBillingPeriod, PlanChangeDiscount.Unchanged, ct)) is { } keepFailed) return keepFailed;
            await RecordChangeAsync(sub.OrganizationId, kind == SeatsKind ? SeatsKind : KeepKind);
            return Accepted(new
            {
                direction = "keep",
                effective = "now",
                effectiveAt = (DateTime?)null,
                creditGrantMillis = 0L,
                seats,
                promoDiscountRemoved = false,
                message = "You keep your current plan; the scheduled change is removed."
            });
        }

        // Here Paddle's live items are the row's plan and seats (settled above), so the direction is
        // judged against what Paddle actually bills, with the webhook's own rule.
        bool downgrade = BillingEventProcessor.IsDowngrade(livePlan!, liveSeats, target, seats, currentInterval, targetInterval);
        PlanChangeTiming timing = downgrade ? PlanChangeTiming.NextBillingPeriod : PlanChangeTiming.Immediately;
        DateTime now = UtcNow;

        // a seat change on the same plan never touches the discount; a plan change keeps it only
        // while the new price is one the promotion targets, else the same PATCH removes it.
        bool movingToAnnual = targetInterval == BillingInterval.Annual && currentInterval != BillingInterval.Annual;
        (PlanChangeDiscount discount, Promotion? promotion) = target.Id == current.Id && !movingToAnnual
            ? (PlanChangeDiscount.Unchanged, null)
            : await DiscountOnPlanChangeAsync(live, sub, priceId, now, ct, movingToAnnual);
        if (await CallProviderAsync(provider, "plan change", () => provider.ChangePlanAsync(sub.ProviderSubscriptionId!, items, timing, discount, ct)) is { } failed) return failed;
        await RecordChangeAsync(sub.OrganizationId, kind);

        bool promoDiscountRemoved = discount == PlanChangeDiscount.Remove;
        const string annualOfferNote = " Your monthly offer does not carry over to the yearly price, so it was removed.";
        string offerNote = promoDiscountRemoved && movingToAnnual ? annualOfferNote : "";
        if (promoDiscountRemoved)
        {
            await RecordDiscountRemovedAsync(promotion!, sub, live.Items.FirstOrDefault()?.PriceId, priceId, now);
        }

        if (downgrade)
        {
            return Accepted(new
            {
                direction = "downgrade",
                effective = "period_end",
                effectiveAt = sub.PeriodEnd,
                creditGrantMillis = 0L,
                seats,
                promoDiscountRemoved,
                message = sub.PeriodEnd is { } end
                    ? $"The change takes effect at the end of the current billing period ({end:yyyy-MM-dd} UTC). You keep what you have until then.{offerNote}"
                    : $"The change takes effect at the end of the current billing period. You keep what you have until then.{offerNote}"
            });
        }

        long grant = BillingEventProcessor.ProratedUpgradeGrantMillis(
            BillingEventProcessor.EntitlementMillis(current, sub.Seats),
            BillingEventProcessor.EntitlementMillis(target, seats),
            sub.PeriodStart,
            sub.PeriodEnd,
            now);
        return Accepted(new
        {
            direction = "upgrade",
            effective = "now",
            effectiveAt = (DateTime?)now,
            creditGrantMillis = grant,
            seats,
            promoDiscountRemoved,
            message = "The change takes effect now. The prorated difference is charged now and the extra credits for the rest of this period are added once the payment is confirmed." + offerNote
        });
    }

    /// <summary>
    /// what a plan change to <paramref name="newPriceId"/> does with the live subscription's discount.
    /// The checkout's own rule (<see cref="PromotionRules.ReadStrings"/> of the target price ids, ordinal):
    /// a targeted price keeps it, any other price removes it. No discount, one that has already ended
    /// (<c>EndsAt &lt;= now</c>), one no promotion of ours created (we never remove a discount we did not
    /// create), or one whose promotion does not recur (neither Forever nor Months) is left alone.
    /// </summary>
    private async Task<(PlanChangeDiscount Discount, Promotion? Promotion)> DiscountOnPlanChangeAsync(
        ProviderSubscriptionState live, Subscription sub, string newPriceId, DateTime now, CancellationToken ct, bool movingToAnnual = false)
    {
        if (live.Discount is not { } discount || discount.EndsAt is { } endsAt && endsAt <= now)
        {
            return (PlanChangeDiscount.Unchanged, null);
        }

        Promotion? promotion = await db.Promotions.AsNoTracking().FirstOrDefaultAsync(p => p.ProviderDiscountId == discount.Id, ct);
        if (promotion is null)
        {
            logger.LogInformation("Subscription {Sub} carries discount {Discount} that no promotion created; a plan change leaves it alone.", sub.ProviderSubscriptionId, discount.Id);
            return (PlanChangeDiscount.Unchanged, null);
        }

        // QA-13: only a recurring promotion's discount is acted on (the same set the summary warns about);
        // a one-off discount, or one with no duration, is left alone.
        if (promotion.DurationKind is not (PromotionDuration.Forever or PromotionDuration.Months))
        {
            return (PlanChangeDiscount.Unchanged, null);
        }

        // A move to a yearly price: the yearly price shares the monthly price's product, so a monthly-only offer
        // (a months duration, or a plan discount that did not ask for annual prices) would discount the whole
        // annual invoice. Only a promotion that targets the yearly price explicitly (includeAnnual) survives.
        if (movingToAnnual && promotion.Kind == PromotionKind.PlanDiscount
            && (promotion.DurationKind == PromotionDuration.Months
                || !PromotionRules.ReadStrings(promotion.TargetPriceIdsJson).Contains(newPriceId, StringComparer.Ordinal)))
        {
            return (PlanChangeDiscount.Remove, promotion);
        }

        // A plan promotion created with NO explicit plans (TargetPlanKeysJson is empty) targets every plan: a plan or
        // price added after it was created must not strip a subscriber's locked-in discount (founder price).
        if (promotion.Kind == PromotionKind.PlanDiscount && PromotionRules.ReadStrings(promotion.TargetPlanKeysJson).Count == 0)
        {
            return (PlanChangeDiscount.Unchanged, promotion);
        }

        return PromotionRules.ReadStrings(promotion.TargetPriceIdsJson).Contains(newPriceId, StringComparer.Ordinal)
            ? (PlanChangeDiscount.Unchanged, promotion)
            : (PlanChangeDiscount.Remove, promotion);
    }

    /// <summary>
    /// Records, once, on the subscription's <see cref="PromotionRedemptionKind.DiscountApplied"/> row that a
    /// plan change removed its discount. Runs only after the provider accepted the PATCH, so the discount is
    /// already gone there: a failure here is logged, never answered as an error.
    /// </summary>
    private async Task RecordDiscountRemovedAsync(Promotion promotion, Subscription sub, string? fromPriceId, string toPriceId, DateTime now)
    {
        BillingEventProcessor.PromotionDiscountRemovedOnPlanChange.Add(1);
        logger.LogInformation(
            "PromoDiscountRemovedOnPlanChange: promotion {PromotionId} org {OrganizationId} from {FromPriceId} to {ToPriceId}.",
            promotion.Id, sub.OrganizationId, fromPriceId, toPriceId);
        try
        {
            // A tracked update guarded by DiscountRemovedAt == null: a replay never overwrites the first record.
            PromotionRedemption? row = await db.PromotionRedemptions
                .Where(r => r.PromotionId == promotion.Id
                    && r.Kind == PromotionRedemptionKind.DiscountApplied
                    && r.ProviderSubscriptionId == sub.ProviderSubscriptionId
                    && r.OrganizationId == sub.OrganizationId
                    && r.DiscountRemovedAt == null)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync(CancellationToken.None);
            if (row is null)
            {
                logger.LogWarning("No open discount redemption of promotion {PromotionId} for subscription {Sub} to record the removal on.", promotion.Id, sub.ProviderSubscriptionId);
                return;
            }

            row.DiscountRemovedAt = now;
            row.DiscountRemovedForPriceId = toPriceId;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record that a plan change removed promotion {PromotionId}'s discount from subscription {Sub}.", promotion.Id, sub.ProviderSubscriptionId);
        }
    }

    /// <summary>
    /// the recurring promo discount the subscription still carries, as the dashboard needs it to
    /// warn before a plan change ends it: type, amount, currency and target price ids only (never the
    /// promotion's name or id). From the latest <see cref="PromotionRedemptionKind.DiscountApplied"/> row of
    /// the subscription not removed on a plan change, whose promotion recurs (<see cref="PromotionDuration.Forever"/>,
    /// or <see cref="PromotionDuration.Months"/> still within its months); null otherwise.
    /// </summary>
    private async Task<object?> ActivePromoDiscountAsync(Subscription? sub, DateTime now, CancellationToken ct)
    {
        if (sub?.ProviderSubscriptionId is not { } providerSubscriptionId) return null;
        var candidates = await db.PromotionRedemptions.AsNoTracking()
            .Where(r => r.Kind == PromotionRedemptionKind.DiscountApplied
                && r.ProviderSubscriptionId == providerSubscriptionId
                && r.OrganizationId == sub.OrganizationId
                && r.DiscountRemovedAt == null)
            .Join(db.Promotions.AsNoTracking(), r => r.PromotionId, p => p.Id, (r, p) => new { r.CreatedAt, Promotion = p })
            .ToListAsync(ct);
        Promotion? active = candidates
            .Where(c => c.Promotion.DiscountType is not null
                && (c.Promotion.DurationKind == PromotionDuration.Forever
                    || c.Promotion.DurationKind == PromotionDuration.Months
                        && c.Promotion.DurationMonths is { } months
                        && PromotionRules.Utc(c.CreatedAt).AddMonths(months) > now))
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => c.Promotion)
            .FirstOrDefault();
        return active is null ? null : new
        {
            discountType = active.DiscountType!.Value.ToString(),
            amount = active.DiscountAmount ?? "",
            currencyCode = active.DiscountType == PromotionDiscountType.Flat ? active.CurrencyCode : null,
            targetPriceIds = PromotionRules.ReadStrings(active.TargetPriceIdsJson)
        };
    }

    /// <summary>
    /// Paddle's live view of the subscription, read before any change is sent; when it cannot be read the
    /// second value is the <c>502 billing_provider_error</c> to answer, and nothing is sent.
    /// </summary>
    private async Task<(ProviderSubscriptionState? Live, IActionResult? Failed)> ReadLiveAsync(IBillingProvider provider, Subscription sub, CancellationToken ct)
    {
        try
        {
            return (await provider.GetSubscriptionAsync(sub.ProviderSubscriptionId!, ct), null);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Billing provider {Provider} could not read subscription {Sub} before a change.", provider.Name, sub.ProviderSubscriptionId);
            return (null, StatusCode(502, new { error = "billing_provider_error" }));
        }
    }

    /// <summary>Paddle's live items as a catalog plan and seat count (the webhook's own rule).</summary>
    private async Task<(Plan? Plan, int Seats)> LivePlanAsync(ProviderSubscriptionState live, IBillingProvider provider, CancellationToken ct)
    {
        List<Plan> plans = await db.Plans.AsNoTracking().ToListAsync(ct);
        return BillingEventProcessor.ResolveItems(live.Items, plans, provider.Name);
    }

    private static bool IsRowPlan(Plan? livePlan, int liveSeats, Subscription sub)
        => SubscriptionDrift.MatchesRowPlan(livePlan, liveSeats, sub);

    /// <summary>
    /// Security R-3: Paddle's live billing period is the row's. A different start or end means a renewal
    /// or reactivation whose webhook has not landed: the row's plan, seats and refill are last period's,
    /// so a change judged against them (and its upgrade credits) would be wrong. A bound the provider does
    /// not report is not evidence of drift.
    /// </summary>
    private static bool IsRowPeriod(ProviderSubscriptionState live, Subscription sub)
        => SubscriptionDrift.MatchesRowPeriod(live.PeriodStart, live.PeriodEnd, sub);

    /// <summary>
    /// Security F-2: no change is in flight. Paddle's live items are the row's plan and seats (every
    /// webhook has landed) or, with a downgrade held, the held plan and seats Paddle already bills from
    /// the next period. Anything else is a change whose webhook has not landed yet. The rule is
    /// <see cref="SubscriptionDrift"/>'s (shared with the reconciler); status and cancellation never count here.
    /// </summary>
    private async Task<bool> IsSettledAsync(ProviderSubscriptionState live, Subscription sub, IBillingProvider provider, CancellationToken ct)
    {
        List<Plan> plans = await db.Plans.AsNoTracking().ToListAsync(ct);
        return SubscriptionDrift.Compute(sub, live, plans, provider.Name).IsSettled;
    }

    /// <summary>The org's provider-backed subscription that is not canceled, or null (Trial, canceled, none).</summary>
    private async Task<Subscription?> LiveSubscriptionAsync(Guid orgId, IBillingProvider provider, CancellationToken ct)
    {
        Subscription? sub = await CurrentSubscriptionAsync(orgId, ct);
        return sub is { ProviderSubscriptionId: not null } && string.Equals(sub.Provider, provider.Name, StringComparison.OrdinalIgnoreCase)
            ? sub
            : null;
    }

    private ConflictObjectResult NoSubscription()
        => Conflict(new { error = "no_subscription", message = "This workspace has no paid subscription. Choose a plan to subscribe." });

    /// <summary>Our own text for a provider refusal code the dashboard may show; the provider's detail never is.</summary>
    private static readonly Dictionary<string, string> ProviderRefusalMessages = new(StringComparer.Ordinal)
    {
        ["subscription_locked_pending_changes"] = "This subscription already has a change being processed at Paddle. Wait a few minutes, then try again.",
        ["subscription_locked_processing"] = "Paddle is still processing a payment for this subscription. Wait a few minutes, then try again.",
        ["subscription_locked_renewal"] = "This subscription is renewing right now. Wait a few minutes, then try again.",
        ["subscription_update_when_canceled"] = "This subscription is canceled, so it can no longer be changed.",
        ["subscription_is_canceled_action_invalid"] = "This subscription is canceled, so it can no longer be changed.",
        ["subscription_not_active"] = "This subscription is not active. Open Manage in Paddle to update your payment method, then try again.",
        ["subscription_update_when_past_due"] = "This subscription has an unpaid invoice. Open Manage in Paddle to update your payment method, then try again.",
    };

    public const string GenericProviderRefusalMessage = "Paddle refused this change. Try again later or open Manage in Paddle.";

    /// <summary>Stripe's codes (<c>error.code</c>); worded for the customer portal ("Manage billing"). Paddle's strings above are untouched.</summary>
    private static readonly Dictionary<string, string> StripeRefusalMessages = new(StringComparer.Ordinal)
    {
        ["resource_missing"] = "Stripe could not find this subscription. Open Manage billing to check it, then try again.",
        ["invoice_payment_intent_requires_action"] = "Stripe needs you to confirm the payment. Open Manage billing to complete it, then try again.",
        ["subscription_payment_intent_requires_action"] = "Stripe needs you to confirm the payment. Open Manage billing to complete it, then try again.",
        ["card_declined"] = "Stripe could not charge your card. Open Manage billing to update your payment method, then try again.",
        ["parameter_invalid_empty"] = "Stripe could not apply this change as sent. Try again later or open Manage billing.",
        ["lock_timeout"] = "Stripe is still processing another change to this subscription. Wait a few minutes, then try again.",
    };

    public static string ProviderRefusalMessage(string code)
        => ProviderRefusalMessages.TryGetValue(code, out string? message) ? message : GenericProviderRefusalMessage;

    /// <summary>
    /// <see cref="ProviderRefusalMessage(string)"/> worded for a provider's customer-facing name; identical
    /// to it for Paddle.
    /// </summary>
    public static string ProviderRefusalMessage(string code, string providerDisplayName)
        => string.Equals(providerDisplayName, "Paddle", StringComparison.Ordinal)
            ? ProviderRefusalMessage(code)
            : string.Equals(providerDisplayName, "Stripe", StringComparison.Ordinal) && StripeRefusalMessages.TryGetValue(code, out string? stripeMessage)
                ? stripeMessage
                : ProviderRefusalMessage(code).Replace("Paddle", providerDisplayName, StringComparison.Ordinal);

    /// <summary>
    /// Runs a provider call. A typed 4xx refusal (other than an auth failure, which is our
    /// misconfiguration) is a 409 carrying the provider's code; anything else is a 502.
    /// </summary>
    private async Task<IActionResult?> CallProviderAsync(IBillingProvider provider, string action, Func<Task> call)
    {
        try
        {
            await call();
            return null;
        }
        catch (BillingProviderException ex) when (ex.Status is >= 400 and < 500 and not (401 or 403))
        {
            // The provider's own wording stays in the log: it is not ours to show and may name internals.
            logger.LogWarning(ex, "Billing provider {Provider} refused the {Action}: {Code}: {Detail}", provider.Name, action, ex.Code, ex.Detail);
            return Conflict(new { error = "billing_provider_refused", providerCode = ex.Code, message = ProviderRefusalMessage(ex.Code, provider.DisplayName) });
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Billing provider {Provider} rejected the {Action}.", provider.Name, action);
            return StatusCode(502, new { error = "billing_provider_error" });
        }
    }

    // ------------------------------------------------------------------ confirm on return

    /// <summary>
    /// Confirms a finished checkout on the buyer's return so the plan and credits show at once; the webhook later is a
    /// no-op (the processor's own idempotency keys). The provider re-reads the checkout and the events it builds go through
    /// the same <see cref="BillingEventProcessor"/> as a webhook. Another organization's session is a 404 identical to an unknown one.
    /// </summary>
    [HttpPost("checkout/confirm")]
    [Authorize]
    public async Task<IActionResult> ConfirmCheckout([FromBody] ConfirmCheckoutDto? body, CancellationToken ct)
    {
        if (Disabled || currentUser.OrganizationId is not { } orgId || currentUser.OrgRole is null) return NotFound();
        if (!CanManage(currentUser.OrgRole)) return Forbid();

        string? sessionId = body?.SessionId;
        if (string.IsNullOrEmpty(sessionId) || sessionId.Length > 200
            || !sessionId.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
        {
            return BadRequest(new { error = "invalid_session" });
        }

        // A transaction id another provider owns (Paddle txn_) is a real id that cannot be confirmed here: 409.
        // An id nobody owns that is not a Checkout Session id (cs_) is malformed.
        IBillingProvider? provider = _providers.FirstOrDefault(p => p.IsConfigured && p.OwnsTransaction(sessionId));
        bool checkoutSession = sessionId.StartsWith("cs_", StringComparison.Ordinal);
        if (provider is null && !checkoutSession) return BadRequest(new { error = "invalid_session" });
        if (provider is null || !checkoutSession) return Conflict(new { error = "confirm_unsupported" });

        // A settled answer within the last minute is replayed without touching the limiter or the provider.
        CheckoutConfirmLimiter limiter = confirmLimiter ?? CheckoutConfirmLimiter.Shared;
        if (limiter.TryGetResult(orgId, sessionId, UtcNow, out (string Status, string Kind) cached)) return Ok(new { status = cached.Status, kind = cached.Kind });
        if (limiter.IsLimited(orgId, UtcNow, currentUser.UserId)) return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "rate_limited" });

        CheckoutConfirmation confirmation;
        try
        {
            confirmation = await provider.ConfirmCheckoutAsync(sessionId, orgId, ct);
        }
        catch (NotSupportedException)
        {
            return Conflict(new { error = "confirm_unsupported" });
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Billing provider {Provider} could not confirm a checkout ({Type}).", provider.Name, ex.GetType().Name);
            return StatusCode(503, new { error = "provider_unavailable" });
        }

        switch (confirmation.Status)
        {
            case CheckoutConfirmationStatus.NotFound:
                return NotFound(new { error = "not_found" });
            case CheckoutConfirmationStatus.Pending:
                return Ok(new { status = "pending", kind = confirmation.Kind });
            case CheckoutConfirmationStatus.Abandoned:
                limiter.StoreResult(orgId, sessionId, "abandoned", confirmation.Kind, UtcNow);
                return Ok(new { status = "abandoned", kind = confirmation.Kind });
        }

        bool settled = false;

        using (tenant.System())
        {
            for (int i = 0; i < confirmation.Events.Count; i++)
            {
                BillingEvent evt = confirmation.Events[i];
                string raw = confirmation.RawBodies is { } raws && i < raws.Count ? raws[i] : "{}";
                try
                {
                    BillingEventOutcome outcome = await processor.ProcessAsync(evt, raw, ct);
                    if (evt.Kind != BillingEventKind.Ignored)
                    {
                        settled |= outcome == BillingEventOutcome.Applied
                            || (outcome == BillingEventOutcome.Duplicate
                                && await db.BillingWebhookEvents.AsNoTracking().AnyAsync(
                                    e => e.Provider == evt.Provider && e.EventId == evt.EventId && e.Outcome == nameof(BillingEventOutcome.Applied), ct));
                    }

                    BillingWebhookDiagnostics.Outcomes.Add(1,
                        new KeyValuePair<string, object?>("provider", ProviderTag(provider)),
                        new KeyValuePair<string, object?>("outcome", outcome.ToString().ToLowerInvariant()));
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    BillingWebhookDiagnostics.RecordProcessingFailure(ProviderTag(provider), evt.Kind.ToString().ToLowerInvariant());
                    logger.LogError(BillingWebhookDiagnostics.WebhookProcessingFailed, ex,
                        "Confirming {Provider} checkout: processing {EventId} ({EventType}) failed.", provider.Name, evt.EventId, evt.EventType);
                    throw;
                }
            }
        }

        // Only an event that changed (or had already changed) state is a confirmation; all-ignored / unresolved stays neutral.
        if (!settled) return Ok(new { status = "pending", kind = confirmation.Kind });
        limiter.StoreResult(orgId, sessionId, "confirmed", confirmation.Kind, UtcNow);
        return Ok(new { status = "confirmed", kind = confirmation.Kind });
    }

    // ------------------------------------------------------------------ webhook

    /// <summary>
    /// The provider's notification receiver. Anonymous by design: the signature over the exact raw body
    /// is the only credential. A bad signature, or an event id with the reconciler's reserved <c>reconcile:</c>
    /// prefix, is 400 (and stores nothing); a duplicate or stale event is
    /// 200; a processing failure is 500 so the provider retries.
    /// </summary>
    [HttpPost("webhooks/{provider}")]
    [AllowAnonymous]
    [RequestSizeLimit(1_048_576)]
    public async Task<IActionResult> Webhook(string provider, CancellationToken ct)
    {
        if (Disabled || Provider(provider) is not { IsConfigured: true } impl) return NotFound();

        string raw;
        using (StreamReader reader = new(Request.Body, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        {
            raw = await reader.ReadToEndAsync(ct);
        }

        Dictionary<string, string> headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        BillingEvent evt;
        try
        {
            evt = impl.VerifyAndParseWebhook(raw, headers);
        }
        catch (WebhookVerificationException ex)
        {
            BillingWebhookDiagnostics.RecordSignatureFailure(ProviderTag(impl));
            logger.LogWarning(BillingWebhookDiagnostics.WebhookSignatureFailed, "Rejected {Provider} webhook: {Reason}", impl.Name, ex.Message);
            return BadRequest(new { error = "invalid_webhook" });
        }

        // The provider may read its own API to complete a verified event. A failure throws (HttpRequestException
        // included): the 500 makes the provider retry and nothing has been recorded yet.
        evt = await impl.ResolveAsync(evt, ct);

        if (evt.EventId.StartsWith(BillingReconciler.SyntheticEventIdPrefix, StringComparison.Ordinal)
            || evt.EventId.StartsWith(BillingReconciler.ConfirmEventIdPrefix, StringComparison.Ordinal))
        {
            // Signed, but claiming an id only the reconciler mints: processing it would pre-empt (as a Duplicate)
            // the synthetic event a later reconcile feeds. Refused before the processor; not a signature failure.
            logger.LogWarning(BillingWebhookDiagnostics.WebhookRejected,
                "Rejected {Provider} webhook {EventType}: its event id uses a reserved (reconcile/confirm) prefix.", impl.Name, evt.EventType);
            return BadRequest(new { error = "invalid_webhook" });
        }

        using (tenant.System())
        {
            BillingEventOutcome outcome;
            try
            {
                outcome = await processor.ProcessAsync(evt, raw, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Rethrown: the 500 makes the provider retry, and the rolled-back row lets the retry run again.
                BillingWebhookDiagnostics.RecordProcessingFailure(ProviderTag(impl), evt.Kind.ToString().ToLowerInvariant());
                logger.LogError(BillingWebhookDiagnostics.WebhookProcessingFailed, ex,
                    "Processing {Provider} webhook {EventId} ({EventType}) failed; answering 500 so it is retried.", impl.Name, evt.EventId, evt.EventType);
                throw;
            }

            BillingWebhookDiagnostics.Outcomes.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderTag(impl)),
                new KeyValuePair<string, object?>("outcome", outcome.ToString().ToLowerInvariant()));
            if (outcome == BillingEventOutcome.Unresolved)
            {
                logger.LogWarning(BillingWebhookDiagnostics.WebhookUnresolved,
                    "{Provider} webhook {EventId} ({EventType}) was recorded but could not be resolved to an organization or plan.", impl.Name, evt.EventId, evt.EventType);
            }

            return Ok(new { outcome = outcome.ToString() });
        }
    }

    /// <summary>The provider's configured name as a metric tag (low cardinality; never an id).</summary>
    private static string ProviderTag(IBillingProvider provider) => provider.Name.ToLowerInvariant();

    // ------------------------------------------------------------------ usage

    private const int UsageDefaultWindowDays = 30;
    private const int UsageMaxWindowDays = 366;
    private static readonly string[] UsageGroupByAxes = ["day", "project", "kind", "model", "mode", "source"];

    /// <summary>
    /// The active organization's metered usage over <c>[from, to)</c>, broken down by one axis
    /// (<c>groupBy</c>: <c>day</c>, <c>project</c>, <c>kind</c>, <c>model</c>, <c>mode</c> or <c>source</c> — what
    /// spent it: Workflow, Assistant, Mcp, Editor, TimelineAssist, …) plus the
    /// always-present BYO / platform / runner split.
    /// <para>
    /// Every number here is read straight off <c>usage_events</c> — the append-only ledger B3b writes for
    /// every metered call — so it is what was actually charged and actually consumed, priced at write time
    /// by the rate card that applied then. Nothing is re-priced or estimated on read, which is why a
    /// rate-card change never rewrites history.
    /// </para>
    /// <para>
    /// <c>costUsd</c> is our own provider cost (COGS), so it is reported only to platform admins: an org
    /// member sees the credits they were charged and the units consumed, never our margin. It is null for
    /// everyone else.
    /// </para>
    /// <para>
    /// With <c>executionId</c> supplied the window defaults to the maximum instead of the usual 30 days, so
    /// a caller that only knows the run's id does not also have to guess its date range.
    /// </para>
    /// </summary>
    [HttpGet("usage")]
    [Authorize]
    public async Task<IActionResult> Usage(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? groupBy = null,
        [FromQuery] Guid? executionId = null,
        CancellationToken ct = default)
    {
        if (Disabled || currentUser.OrganizationId is not { } orgId || currentUser.OrgRole is null) return NotFound();

        string axis = (groupBy ?? "day").Trim().ToLowerInvariant();
        if (!UsageGroupByAxes.Contains(axis))
        {
            return BadRequest(new { error = "unknown_group_by", supported = UsageGroupByAxes });
        }

        DateTime end = ToUtc(to ?? DateTime.UtcNow);
        DateTime start = from is { } f
            ? ToUtc(f)
            : end.AddDays(executionId is null ? -UsageDefaultWindowDays : -UsageMaxWindowDays);
        if (start >= end) return BadRequest(new { error = "invalid_window", message = "from must be before to." });
        if (end - start > TimeSpan.FromDays(UsageMaxWindowDays))
        {
            return BadRequest(new { error = "window_too_large", maxDays = UsageMaxWindowDays });
        }

        IQueryable<UsageEvent> scoped = db.UsageEvents.AsNoTracking()
            .Where(e => e.OrganizationId == orgId && e.OccurredAt >= start && e.OccurredAt < end);
        if (executionId is { } execution)
        {
            scoped = scoped.Where(e => e.ExecutionId == execution);
        }

        List<UsageRow> rows = await RollUpAsync(scoped, axis, ct);
        bool showCost = currentUser.IsPlatformAdmin;

        Dictionary<string, string> projectLabels = axis == "project"
            ? await ProjectLabelsAsync(rows.Select(r => r.Key).Where(k => k.Length > 0).ToList(), ct)
            : [];

        List<UsageGroupDto> rolled = rows
            .GroupBy(r => r.Key)
            .Select(g => new UsageGroupDto(
                g.Key,
                Label(axis, g.Key, projectLabels),
                Credits(g.Sum(r => r.CreditsMillis)),
                g.Sum(r => r.Events),
                showCost ? Usd(g.Sum(r => r.CostUsd)) : null,
                g.GroupBy(r => r.Kind)
                    .Select(k => new UsageUnitDto(k.Key.ToString(), Units(k.Sum(r => r.Quantity))))
                    .OrderBy(u => u.Kind, StringComparer.Ordinal)
                    .ToList()))
            .ToList();

        // Chronological reads as a time series (the day keys sort as yyyy-MM-dd); every other axis
        // reads best with the biggest spender first.
        List<UsageGroupDto> groups = axis == "day"
            ? rolled.OrderBy(g => g.Key, StringComparer.Ordinal).ToList()
            : rolled.OrderByDescending(g => g.Credits).ToList();

        List<UsageModeDto> byMode = rows
            .GroupBy(r => r.BillingMode)
            .Select(g => new UsageModeDto(
                g.Key.ToString(),
                Credits(g.Sum(r => r.CreditsMillis)),
                g.Sum(r => r.Events),
                showCost ? Usd(g.Sum(r => r.CostUsd)) : null))
            .OrderByDescending(m => m.Credits)
            .ToList();

        return Ok(new BillingUsageResponse(
            start, end, axis,
            new UsageTotalsDto(Credits(rows.Sum(r => r.CreditsMillis)), rows.Sum(r => r.Events), showCost ? Usd(rows.Sum(r => r.CostUsd)) : null),
            byMode,
            groups));
    }

    /// <summary>One aggregated ledger cell: the group axis value plus the kind/mode it splits on.</summary>
    private sealed record UsageRow(string Key, UsageKind Kind, UsageBillingMode BillingMode, long CreditsMillis, decimal CostUsd, decimal Quantity, int Events);

    /// <summary>
    /// Rolls the window up in SQL, grouped by the requested axis <em>and</em> by kind and billing mode, so
    /// one query answers both the axis breakdown, the per-group unit mix and the BYO/platform split. The
    /// fold into those three shapes happens in memory over rows the database has already summed.
    /// </summary>
    private static async Task<List<UsageRow>> RollUpAsync(IQueryable<UsageEvent> scoped, string axis, CancellationToken ct)
    {
        switch (axis)
        {
            case "project":
                {
                    var raw = await scoped.GroupBy(e => new { e.ProjectId, e.Kind, e.BillingMode })
                        .Select(g => new { g.Key.ProjectId, g.Key.Kind, g.Key.BillingMode, Credits = g.Sum(e => e.CreditsMillis), Cost = g.Sum(e => e.CostUsd), Quantity = g.Sum(e => e.Quantity), Events = g.Count() })
                        .ToListAsync(ct);
                    return raw.Select(r => new UsageRow(
                            r.ProjectId?.ToString() ?? string.Empty, r.Kind, r.BillingMode, r.Credits, r.Cost, r.Quantity, r.Events))
                        .ToList();
                }

            case "kind":
                {
                    var raw = await scoped.GroupBy(e => new { e.Kind, e.BillingMode })
                        .Select(g => new { g.Key.Kind, g.Key.BillingMode, Credits = g.Sum(e => e.CreditsMillis), Cost = g.Sum(e => e.CostUsd), Quantity = g.Sum(e => e.Quantity), Events = g.Count() })
                        .ToListAsync(ct);
                    return raw.Select(r => new UsageRow(
                            r.Kind.ToString(), r.Kind, r.BillingMode, r.Credits, r.Cost, r.Quantity, r.Events))
                        .ToList();
                }

            case "model":
                {
                    var raw = await scoped.GroupBy(e => new { e.ModelName, e.Kind, e.BillingMode })
                        .Select(g => new { g.Key.ModelName, g.Key.Kind, g.Key.BillingMode, Credits = g.Sum(e => e.CreditsMillis), Cost = g.Sum(e => e.CostUsd), Quantity = g.Sum(e => e.Quantity), Events = g.Count() })
                        .ToListAsync(ct);
                    // A row with no model is a metered unit that is not model-scoped at all (storage, a render);
                    // it is reported under an explicit key rather than dropped or folded into a model.
                    return raw.Select(r => new UsageRow(
                            string.IsNullOrWhiteSpace(r.ModelName) ? UsageNoModelKey : r.ModelName,
                            r.Kind, r.BillingMode, r.Credits, r.Cost, r.Quantity, r.Events))
                        .ToList();
                }

            case "source":
                {
                    var raw = await scoped.GroupBy(e => new { e.Source, e.Kind, e.BillingMode })
                        .Select(g => new { g.Key.Source, g.Key.Kind, g.Key.BillingMode, Credits = g.Sum(e => e.CreditsMillis), Cost = g.Sum(e => e.CostUsd), Quantity = g.Sum(e => e.Quantity), Events = g.Count() })
                        .ToListAsync(ct);
                    return raw.Select(r => new UsageRow(
                            r.Source.ToString(), r.Kind, r.BillingMode, r.Credits, r.Cost, r.Quantity, r.Events))
                        .ToList();
                }

            case "mode":
                {
                    var raw = await scoped.GroupBy(e => new { e.BillingMode, e.Kind })
                        .Select(g => new { g.Key.BillingMode, g.Key.Kind, Credits = g.Sum(e => e.CreditsMillis), Cost = g.Sum(e => e.CostUsd), Quantity = g.Sum(e => e.Quantity), Events = g.Count() })
                        .ToListAsync(ct);
                    return raw.Select(r => new UsageRow(
                            r.BillingMode.ToString(), r.Kind, r.BillingMode, r.Credits, r.Cost, r.Quantity, r.Events))
                        .ToList();
                }

            default:
                {
                    var raw = await scoped.GroupBy(e => new { Day = e.OccurredAt.Date, e.Kind, e.BillingMode })
                        .Select(g => new { g.Key.Day, g.Key.Kind, g.Key.BillingMode, Credits = g.Sum(e => e.CreditsMillis), Cost = g.Sum(e => e.CostUsd), Quantity = g.Sum(e => e.Quantity), Events = g.Count() })
                        .ToListAsync(ct);
                    return raw.Select(r => new UsageRow(
                            r.Day.ToString("yyyy-MM-dd"), r.Kind, r.BillingMode, r.Credits, r.Cost, r.Quantity, r.Events))
                        .ToList();
                }
        }
    }

    /// <summary>Model-axis key for usage that carries no model at all (storage GB-days, renders).</summary>
    private const string UsageNoModelKey = "(none)";

    private async Task<Dictionary<string, string>> ProjectLabelsAsync(List<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0) return [];

        List<Guid> ids = keys.Select(k => Guid.TryParse(k, out Guid id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToList();
        // Scoped by the tenant filter, exactly like every other project read here.
        Dictionary<Guid, string> names = await db.Projects.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        // A project purged since the event was written keeps its row in the ledger (attribution ids are
        // plain columns, not foreign keys), so the group stays visible under a plain label.
        return names.ToDictionary(n => n.Key.ToString(), n => n.Value);
    }

    private static string Label(string axis, string key, Dictionary<string, string> projectLabels)
        => axis switch
        {
            "project" => key.Length == 0 ? "Not attributed to a project" : projectLabels.GetValueOrDefault(key, "Deleted project"),
            "model" => key == UsageNoModelKey ? "Not model-specific" : key,
            _ => key
        };

    private static double Credits(long creditsMillis) => creditsMillis / 1000.0;

    private static double Usd(decimal costUsd) => (double)decimal.Round(costUsd, 6, MidpointRounding.AwayFromZero);

    /// <summary>Quantities are in the unit of their kind, so they stay as recorded (tokens, seconds, bytes).</summary>
    private static double Units(decimal quantity) => (double)decimal.Round(quantity, 6, MidpointRounding.AwayFromZero);

    private static DateTime ToUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);

    // ------------------------------------------------------------------ helpers

    /// <summary>The org's grants with credits left and not expired, soonest expiry first (never-expiring last).</summary>
    private async Task<IReadOnlyList<object>> LiveGrantsAsync(Guid orgId, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        List<CreditGrant> grants = await db.CreditGrants.AsNoTracking()
            .Where(g => g.OrganizationId == orgId && g.RemainingMillis > 0 && g.ExpiredAt == null && (g.ExpiresAt == null || g.ExpiresAt > now))
            .ToListAsync(ct);
        return grants
            .OrderBy(g => g.ExpiresAt is null)
            .ThenBy(g => g.ExpiresAt)
            .ThenBy(g => g.CreatedAt)
            .Select(g => (object)new
            {
                source = PromotionRules.Camel(g.Source),
                remaining = g.RemainingMillis / 1000.0,
                expiresAt = PromotionRules.Utc(g.ExpiresAt)
            })
            .ToList();
    }

    private async Task<Subscription?> CurrentSubscriptionAsync(Guid orgId, CancellationToken ct)
    {
        // A manual (provider-less) grant lapses at its period end; the summary must not keep showing a
        // plan the gates no longer grant. Provider-backed rows are the provider's to expire.
        DateTime now = UtcNow;
        return await db.Subscriptions.AsNoTracking()
            .Where(s => s.OrganizationId == orgId && s.Status != SubscriptionStatus.Canceled)
            .Where(s => s.Provider != null || s.PeriodEnd == null || s.PeriodEnd > now)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    private Task<bool> HasCustomerAsync(Guid orgId, string provider, CancellationToken ct)
        => db.BillingCustomers.AnyAsync(c => c.OrganizationId == orgId && c.Provider == provider, ct);

    private Task<string?> CustomerIdAsync(Guid orgId, string provider, CancellationToken ct)
        => db.BillingCustomers.AsNoTracking().Where(c => c.OrganizationId == orgId && c.Provider == provider)
            .Select(c => c.ProviderCustomerId).FirstOrDefaultAsync(ct)!;

    /// <summary>
    /// Plan-card entitlement figures; per-seat and unlimited assistant allowances are stated explicitly, never as null.
    /// </summary>
    private static (int? StorageGb, object Entitlements) PlanCardEntitlements(Plan plan)
    {
        OrganizationEntitlementSet e = PlanEntitlements.ToSet(plan);
        static int? Limit(int v) => v == OrganizationEntitlementSet.UnlimitedInt ? null : v;
        return (Limit(e.StorageGb), new
        {
            maxOutputHeight = Limit(e.MaxOutputHeight),
            watermark = e.Watermark,
            byoAllowed = e.ByoAllowed,
            roomsAllowed = e.RoomsAllowed,
            videoGenAllowed = e.VideoGenAllowed,
            segmentationAllowed = e.SegmentationAllowed,
            audioGenAllowed = e.AudioGenAllowed,
            audioCleanupAllowed = e.AudioCleanupAllowed,
            maxConcurrentExecutions = Limit(e.MaxConcurrentExecutions),
            assistantMonthlyCredits = e.AssistantMonthlyCredits,
            assistantMonthlyCreditsPerSeat = e.AssistantMonthlyCreditsPerSeat,
            assistantUnlimited = e.AssistantUnlimited,
            retentionDays = Limit(e.RetentionDays),
            runnerAllowed = e.RunnerAllowed,
            priority = e.Priority
        });
    }

    /// <summary>Plan-card figures for the pricing/billing UI, from the typed parser (null storage = unlimited).</summary>
    internal static (int? StorageGb, object Entitlements) CardEntitlementsOf(Plan plan) => PlanCardEntitlements(plan);

    /// <summary>
    /// The price id a checkout of <paramref name="plan"/> would carry; null when no provider is configured
    /// (billing on without Paddle keys), so nothing is purchasable.
    /// </summary>
    private static string? PriceIdOf(Plan plan, IBillingProvider? provider, BillingInterval interval = BillingInterval.Monthly)
        => provider is null ? null : BillingEventProcessor.PriceIdFor(plan, provider.Name, interval);

    /// <summary>
    /// Parses the requested interval (null = monthly) and refuses annual for a plan that does not sell it
    /// (no <c>priceAnnual</c> in the catalog) or has no annual price id for this provider.
    /// </summary>
    private IActionResult? IntervalProblem(string? requested, Plan plan, IBillingProvider provider, out BillingInterval interval)
    {
        if (!SubscriptionInterval.TryParse(requested, out interval))
        {
            return BadRequest(new { error = "invalid_interval", message = "interval must be monthly or annual." });
        }

        if (interval == BillingInterval.Annual && (PlanPricing.AnnualPriceUsd(plan) is null || PriceIdOf(plan, provider, BillingInterval.Annual) is null))
        {
            return BadRequest(new { error = "annual_unavailable", message = "Annual billing is not available for this plan." });
        }

        return null;
    }

    /// <summary>
    /// The values Paddle.js needs in the browser, or null when this deployment has no embedded
    /// checkout to offer: the provider is not Paddle, or <c>Environment</c> did not resolve (which
    /// the startup validator already refuses while an API key is set).
    /// </summary>
    /// <remarks>
    /// <c>environment</c> is emitted in Paddle.js's own vocabulary — <c>sandbox</c> or
    /// <c>production</c> — rather than the configuration's <c>sandbox</c>/<c>live</c>, so the
    /// translation happens once, here, next to the setting it translates. A browser that had to map
    /// it would be one more chance to point a live deployment at the sandbox account.
    ///
    /// Whether the token is present is the dashboard's answer to "is there an embedded checkout?".
    /// It is a publishable token: Paddle's browser library is meant to run with it, and it grants
    /// nothing on its own. It is served from an authenticated endpoint like everything else in the
    /// summary, and the API key next to it in configuration is never serialized anywhere.
    ///
    /// <c>customerId</c> is the active organization's Paddle customer id (<c>ctm_…</c>), so Paddle.js
    /// can be told which buyer is signed in (Retain's in-app cancellation flow). It is null for an
    /// organization that has never checked out — a brand-new workspace is a normal case, not an
    /// error. It is buyer-identifying but not a secret, and leaves only through this authenticated
    /// summary, exactly like the client token beside it.
    ///
    /// It is withheld from a caller who cannot manage billing (<paramref name="canManage"/>): a
    /// Member or Admin who can read the summary but not act on billing is served null. That is
    /// **least-privilege hygiene, matching <c>canManageBilling</c>, not a confidentiality or tenant
    /// boundary** — the identifier is the organization's own data, and the organization's own
    /// account export (<c>AccountExportService</c>) deliberately includes it, because an Art. 15/20
    /// portability request must not be answered with a hole in it.
    /// </remarks>
    private async Task<object?> PaddleClientAsync(IBillingProvider? provider, Guid orgId, bool canManage, CancellationToken ct)
    {
        if (provider is null || !string.Equals(provider.Name, PaddleBillingProvider.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        PaddleOptions paddle = paddleOptions.Value.Paddle;
        if (paddle.EnvironmentKind is not { } environment)
        {
            return null;
        }

        return new
        {
            environment = environment == PaddleOptions.SandboxEnvironment ? "sandbox" : "production",
            clientToken = string.IsNullOrWhiteSpace(paddle.ClientToken) ? null : paddle.ClientToken.Trim(),
            customerId = canManage ? await CustomerIdAsync(orgId, provider.Name, ct) : null
        };
    }
}
