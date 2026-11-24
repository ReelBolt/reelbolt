#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers.Admin;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing.Reconcile;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The platform-admin revenue/cost overview (B11-UI). Only two things are contractual: a non-admin is
/// forbidden and reads nothing, and every figure is built from the ledger columns alone (so a cost
/// with no organization name, a run with no template, and a Platform row with no rate card all stay
/// visible instead of silently disappearing from a total).
/// </summary>
public class BillingAdminControllerTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();
    private static readonly Guid CreatorPlanId = Guid.NewGuid();
    private static readonly Guid DefinitionId = Guid.NewGuid();
    private static readonly Guid ExecutionId = Guid.NewGuid();

    private sealed class Admin(bool isAdmin) : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "a@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => isAdmin;
        public Guid? OrganizationId => null;
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => null;
    }

    /// <summary>The reconciler as the controller sees it: records every call, answers what the test set.</summary>
    private sealed class StubReconciler : IBillingReconciler
    {
        public ReconcileResult Result { get; set; } = new(ReconcileOutcome.InSync, [], 0, 0, null);
        public List<(Guid Org, ReconcileTrigger Trigger, bool Force)> Calls { get; } = [];

        /// <summary>Cancelled once the reconcile has applied: the admin's client went away mid-request.</summary>
        public CancellationTokenSource? AbortAfterApply { get; set; }

        /// <summary>After the abort, the reconcile itself ends in <see cref="OperationCanceledException"/>.</summary>
        public bool ThrowOnAbort { get; set; }

        public Task<ReconcileResult> ReconcileAsync(Guid organizationId, ReconcileTrigger trigger, bool force, CancellationToken ct)
        {
            Calls.Add((organizationId, trigger, force));
            if (AbortAfterApply is not null)
            {
                AbortAfterApply.Cancel();
                if (ThrowOnAbort) ct.ThrowIfCancellationRequested();
            }

            return Task.FromResult(Result);
        }

        public Task<ReconcileResult> ReconcileHeldAsync(Guid organizationId, ReconcileTrigger trigger, bool force, CancellationToken ct)
            => throw new NotSupportedException("The admin resync takes the claim itself.");

        public Task RecordChangeRequestedAsync(Guid organizationId, string changeKind, Guid? userId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private static BillingAdminController Controller(
        InferenceApiDbContext db, ICurrentUser user, StubReconciler? reconciler = null, bool billingEnabled = true) =>
        new(db, user, reconciler ?? new StubReconciler(),
            Options.Create(new BillingOptions { Enabled = billingEnabled }),
            Options.Create(new BillingReconcileOptions()));

    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static UsageEvent Event(
        Guid organization, decimal cost, long creditsMillis, UsageKind kind,
        InferenceProviderKind provider = InferenceProviderKind.Anthropic, string model = "claude-x",
        UsageBillingMode mode = UsageBillingMode.Platform, UsageSource source = UsageSource.Workflow,
        Guid? executionId = null, Guid? priceId = null, int daysAgo = 1) => new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization,
            ExecutionId = executionId,
            Source = source,
            Kind = kind,
            Quantity = 1m,
            ProviderKind = provider,
            ModelName = model,
            BillingMode = mode,
            CreditsMillis = creditsMillis,
            CostUsd = cost,
            PriceId = priceId,
            IdempotencyKey = Guid.NewGuid().ToString(),
            OccurredAt = DateTime.UtcNow.AddDays(-daysAgo)
        };

    /// <summary>Org A on the paid Creator plan, org B with no subscription at all.</summary>
    private static async Task<InferenceApiDbContext> SeededAsync()
    {
        InferenceApiDbContext db = NewDb();

        db.Organizations.Add(new Organization { Id = OrgA, Name = "Acme", Slug = "acme" });
        db.Organizations.Add(new Organization { Id = OrgB, Name = "Beta", Slug = "beta" });

        db.Plans.Add(new Plan
        {
            Id = CreatorPlanId,
            Key = "creator",
            Name = "Creator",
            Kind = PlanKind.Subscription,
            PriceMonthlyUsd = 29m,
            MonthlyCreditsMillis = 1_000_000,
            IsActive = true
        });
        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            OrganizationId = OrgA,
            PlanId = CreatorPlanId,
            Provider = "paddle",
            Status = SubscriptionStatus.Active,
            Seats = 1,
            CreatedAt = DateTime.UtcNow.AddDays(-60)
        });

        db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = DefinitionId, Name = "promo" });
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = ExecutionId,
            WorkflowDefinitionId = DefinitionId,
            ProjectId = Guid.NewGuid(),
            OrganizationId = OrgA
        });

        // Org A: 3.00 priced inside the "promo" template, 1.00 on an Assistant call nothing priced.
        db.UsageEvents.Add(Event(OrgA, 3m, 500, UsageKind.LlmInputTokens, executionId: ExecutionId, priceId: Guid.NewGuid()));
        db.UsageEvents.Add(Event(OrgA, 1m, 0, UsageKind.AsrAudioSeconds, mode: UsageBillingMode.Platform,
            source: UsageSource.Assistant, priceId: null));
        // Org B: half a dollar of runner compute, no subscription — must not vanish into a plan row.
        db.UsageEvents.Add(Event(OrgB, 0.5m, 0, UsageKind.RenderOutputSeconds, provider: InferenceProviderKind.MiniMax,
            model: "minimax-video-01", mode: UsageBillingMode.Runner, priceId: Guid.NewGuid()));
        // Outside the window: never counted.
        db.UsageEvents.Add(Event(OrgA, 99m, 0, UsageKind.LlmOutputTokens, priceId: Guid.NewGuid(), daysAgo: 200));

        db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            Id = Guid.NewGuid(),
            OrganizationId = OrgA,
            AccountId = Guid.NewGuid(),
            Kind = CreditLedgerEntryKind.TopUp,
            BalanceDeltaMillis = 2_000_000,
            CreatedAt = DateTime.UtcNow.AddDays(-2)
        });

        await db.SaveChangesAsync();
        return db;
    }

    /// <summary>
    /// The action returns `Ok(...)`, which binds to the `ActionResult` arm of `ActionResult<T>`
    /// (never the `T` arm), so the payload is read off the `OkObjectResult`.
    /// </summary>
    private static BillingAdminOverviewResponse Body(ActionResult<BillingAdminOverviewResponse> result)
    {
        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Which;
        return (BillingAdminOverviewResponse)ok.Value!;
    }

    [Fact]
    public async Task Non_platform_admins_are_forbidden_and_read_nothing()
    {
        using InferenceApiDbContext db = await SeededAsync();
        BillingAdminController controller = Controller(db, new Admin(false));

        ActionResult<BillingAdminOverviewResponse> result =
            await controller.Overview(null, null, CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Overview_aggregates_the_ledger_it_can_actually_read()
    {
        using InferenceApiDbContext db = await SeededAsync();
        BillingAdminController controller = Controller(db, new Admin(true));
        DateTime to = DateTime.UtcNow;
        DateTime from = to.AddDays(-30);

        ActionResult<BillingAdminOverviewResponse> result = await controller.Overview(from, to, CancellationToken.None);

        BillingAdminOverviewResponse body = Body(result);
        body.WindowDays.Should().Be(30);
        body.Cost.TotalUsd.Should().Be(4.5m);
        body.Cost.Events.Should().Be(3);
        body.Cost.UnpricedPlatformEvents.Should().Be(1);
        body.Cost.UnattributedEvents.Should().Be(0);

        body.Cost.ByProviderKind.Should().ContainSingle(b => b.Key == "Anthropic" && b.CostUsd == 4m);
        body.Cost.ByUsageKind.Should().Contain(b => b.Key == "LlmInputTokens" && b.CreditsMillis == 500);
        body.Cost.ByBillingMode.Should().ContainSingle(b => b.Key == "Runner" && b.CostUsd == 0.5m);
        body.Cost.BySource.Should().ContainSingle(b => b.Key == "Assistant" && b.CostUsd == 1m);

        // The template name comes from the execution's definition; a row with no execution is not a
        // template, and both are listed separately.
        body.Cost.ByTemplate.Should().ContainSingle(b => b.Key == "promo" && b.CostUsd == 3m);
        body.Cost.ByTemplate.Should().ContainSingle(b => b.Key == "(not a workflow run)" && b.CostUsd == 1.5m);

        body.Cost.TopOrganizations.Should().HaveCount(2);
        body.Cost.TopOrganizations[0].OrganizationId.Should().Be(OrgA);
        body.Cost.TopOrganizations[0].Name.Should().Be("Acme");
        body.Cost.TopOrganizations[0].CostUsd.Should().Be(4m);
        body.Cost.TopOrganizations[1].OrganizationId.Should().Be(OrgB);
        body.Cost.TopOrganizationsTruncated.Should().BeFalse();

        // Revenue is the committed list price of paying subscriptions, prorated at 30-day months.
        body.Revenue.PayingSubscriptions.Should().Be(1);
        body.Revenue.CommittedMonthlyUsd.Should().Be(29m);
        body.Revenue.WindowCommittedUsd.Should().Be(29m);
        body.Revenue.TopUpCreditsInWindow.Should().Be(2000);
        body.Revenue.TopUpUsdRecorded.Should().BeTrue();
        body.Revenue.InternalTrialOrganizations.Should().Be(0);
    }

    /// <summary>
    /// The flag is a claim about a capability, not a computed value: it is true because the amount a
    /// completed transaction actually took is now stored, in minor units, in <c>billing_payments</c>
    /// (written per completed transaction by the webhook processor). Nothing derives it from the
    /// window's rows — it reports that the money received is recorded at all.
    /// </summary>
    [Fact]
    public async Task The_revenue_overview_reports_that_top_up_usd_is_recorded()
    {
        using InferenceApiDbContext db = await SeededAsync();
        BillingAdminController controller = Controller(db, new Admin(true));

        BillingAdminOverviewResponse body = Body(await controller.Overview(null, null, CancellationToken.None));

        body.Revenue.TopUpUsdRecorded.Should().BeTrue();
    }

    /// <summary>
    /// Unpriced = a Platform row with no price id AND zero credits. An oracle-priced row (Higgsfield) has no price id but
    /// real credits, and storage is free by decision; neither is a pricing gap.
    /// </summary>
    [Fact]
    public async Task Unpriced_counts_zero_credit_platform_rows_with_no_price_but_not_oracle_or_storage_rows()
    {
        using InferenceApiDbContext db = NewDb();
        db.UsageEvents.AddRange(
            Event(OrgA, 0.6m, 0, UsageKind.LlmInputTokens),
            Event(OrgA, 0.6m, 120_000, UsageKind.VideoGenSeconds, provider: InferenceProviderKind.Higgsfield),
            Event(OrgA, 0m, 0, UsageKind.StorageGbDay),
            Event(OrgA, 0m, 0, UsageKind.LlmInputTokens, mode: UsageBillingMode.Byo),
            Event(OrgA, 0.1m, 20, UsageKind.LlmInputTokens, priceId: Guid.NewGuid()));
        await db.SaveChangesAsync();
        BillingAdminController controller = Controller(db, new Admin(true));

        BillingAdminOverviewResponse body = Body(await controller.Overview(null, null, CancellationToken.None));

        body.Cost.UnpricedPlatformEvents.Should().Be(1);
    }

    [Fact]
    public async Task Cost_with_no_subscription_stays_visible_instead_of_being_smoothed_into_a_plan()
    {
        using InferenceApiDbContext db = await SeededAsync();
        BillingAdminController controller = Controller(db, new Admin(true));

        ActionResult<BillingAdminOverviewResponse> result = await controller.Overview(null, null, CancellationToken.None);

        IReadOnlyList<BillingPlanMargin> plans = Body(result).Plans;
        BillingPlanMargin creator = plans.Should().ContainSingle(p => p.PlanKey == "creator").Subject;
        creator.Organizations.Should().Be(1);
        creator.CostUsd.Should().Be(4m);
        creator.MarginUsd.Should().Be(25m);

        BillingPlanMargin orphan = plans.Should().ContainSingle(p => p.PlanKey == "(no plan)").Subject;
        orphan.CostUsd.Should().Be(0.5m);
        orphan.CommittedMonthlyUsd.Should().Be(0m);
        orphan.MarginPercent.Should().BeNull();
    }

    [Fact]
    public async Task An_empty_window_reports_zeroes_rather_than_a_fabricated_margin()
    {
        using InferenceApiDbContext db = NewDb();
        BillingAdminController controller = Controller(db, new Admin(true));

        ActionResult<BillingAdminOverviewResponse> result = await controller.Overview(null, null, CancellationToken.None);

        BillingAdminOverviewResponse body = Body(result);
        body.Cost.TotalUsd.Should().Be(0m);
        body.Cost.Events.Should().Be(0);
        body.Revenue.CommittedMonthlyUsd.Should().Be(0m);
        body.Plans.Should().BeEmpty();
    }

    [Fact]
    public async Task An_invalid_or_unbounded_window_is_rejected()
    {
        using InferenceApiDbContext db = NewDb();
        BillingAdminController controller = Controller(db, new Admin(true));
        DateTime now = DateTime.UtcNow;

        (await controller.Overview(now, now.AddDays(-1), CancellationToken.None)).Result
            .Should().BeOfType<BadRequestObjectResult>();
        (await controller.Overview(now.AddDays(-400), now, CancellationToken.None)).Result
            .Should().BeOfType<BadRequestObjectResult>();
    }

    // ------------------------------------------------------------------ lost-webhook recovery

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The JSON an object result's body serializes to, as the client sees it.</summary>
    private static JsonElement Json(IActionResult result)
    {
        ObjectResult obj = result.Should().BeAssignableTo<ObjectResult>().Which;
        return JsonSerializer.SerializeToElement(obj.Value, obj.Value!.GetType(), Web);
    }

    private static async Task<List<BillingAdminAction>> AuditRows(InferenceApiDbContext db)
        => await db.BillingAdminActions.AsNoTracking().ToListAsync();

    [Fact]
    public async Task Resync_NonAdmin_403()
    {
        using InferenceApiDbContext db = await SeededAsync();
        StubReconciler reconciler = new();

        IActionResult result = await Controller(db, new Admin(false), reconciler)
            .Resync(OrgA, new BillingResyncRequest("why"), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        reconciler.Calls.Should().BeEmpty();
        (await AuditRows(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task Resync_And_Attention_BillingDisabled_404()
    {
        using InferenceApiDbContext db = await SeededAsync();
        StubReconciler reconciler = new();
        BillingAdminController controller = Controller(db, new Admin(true), reconciler, billingEnabled: false);

        (await controller.Resync(OrgA, null, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await controller.Attention(CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        reconciler.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Resync_UnknownOrg_404()
    {
        using InferenceApiDbContext db = await SeededAsync();
        StubReconciler reconciler = new();

        IActionResult result = await Controller(db, new Admin(true), reconciler)
            .Resync(Guid.NewGuid(), null, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        reconciler.Calls.Should().BeEmpty();
        (await AuditRows(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task Resync_ReasonTooLong_400()
    {
        using InferenceApiDbContext db = await SeededAsync();
        StubReconciler reconciler = new();

        IActionResult result = await Controller(db, new Admin(true), reconciler)
            .Resync(OrgA, new BillingResyncRequest(new string('x', 501)), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        reconciler.Calls.Should().BeEmpty();
        (await AuditRows(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task Resync_Heals_WritesAudit()
    {
        using InferenceApiDbContext db = await SeededAsync();
        Admin admin = new(true);
        StubReconciler reconciler = new() { Result = new(ReconcileOutcome.Healed, ["plan", "period"], 1, 2, null) };

        IActionResult result = await Controller(db, admin, reconciler)
            .Resync(OrgA, new BillingResyncRequest(new string('r', 500)), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        JsonElement body = Json(result);
        body.GetProperty("outcome").GetString().Should().Be("Healed");
        body.GetProperty("drift").EnumerateArray().Select(e => e.GetString()).Should().Equal("plan", "period");
        body.GetProperty("subscriptionEventsApplied").GetInt32().Should().Be(1);
        body.GetProperty("transactionsApplied").GetInt32().Should().Be(2);

        reconciler.Calls.Should().Equal((OrgA, ReconcileTrigger.Admin, true));
        BillingAdminAction row = (await AuditRows(db)).Should().ContainSingle().Subject;
        row.OrganizationId.Should().Be(OrgA);
        row.ActorUserId.Should().Be(admin.UserId);
        row.Action.Should().Be("resync");
        row.Reason.Should().Be(new string('r', 500));
        row.Outcome.Should().Be("Healed");
        using JsonDocument detail = JsonDocument.Parse(row.DetailJson);
        detail.RootElement.GetProperty("outcome").GetString().Should().Be("Healed");
        detail.RootElement.GetProperty("subscriptionEventsApplied").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Resync_ProviderDown_502_WritesAudit()
    {
        using InferenceApiDbContext db = await SeededAsync();
        StubReconciler reconciler = new() { Result = new(ReconcileOutcome.ProviderUnavailable, [], 0, 0, "provider error: HTTP 503") };

        IActionResult result = await Controller(db, new Admin(true), reconciler).Resync(OrgA, null, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(502);
        Json(result).GetProperty("error").GetString().Should().Be("billing_provider_error");
        BillingAdminAction row = (await AuditRows(db)).Should().ContainSingle().Subject;
        row.Outcome.Should().Be("ProviderUnavailable");
        row.Reason.Should().BeNull();
        using JsonDocument detail = JsonDocument.Parse(row.DetailJson);
        detail.RootElement.GetProperty("error").GetString().Should().Be("provider error: HTTP 503");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resync_ClientAbortAfterApply_StillWritesAudit(bool reconcilerThrows)
    {
        using InferenceApiDbContext db = await SeededAsync();
        using CancellationTokenSource client = new();
        StubReconciler reconciler = new()
        {
            Result = new(ReconcileOutcome.Healed, ["plan"], 1, 1, null),
            AbortAfterApply = client,
            ThrowOnAbort = reconcilerThrows
        };

        Func<Task> act = () => Controller(db, new Admin(true), reconciler).Resync(OrgA, new BillingResyncRequest("lost webhook"), client.Token);

        if (reconcilerThrows)
        {
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await act.Should().NotThrowAsync();
        }

        BillingAdminAction row = (await AuditRows(db)).Should().ContainSingle("an applied resync always has an audit row").Subject;
        row.Outcome.Should().Be(reconcilerThrows ? "Cancelled" : "Healed");
        row.Reason.Should().Be("lost webhook");
    }

    [Fact]
    public async Task Resync_Busy_409()
    {
        using InferenceApiDbContext db = await SeededAsync();
        StubReconciler reconciler = new() { Result = new(ReconcileOutcome.Busy, [], 0, 0, null) };

        IActionResult result = await Controller(db, new Admin(true), reconciler).Resync(OrgA, null, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(409);
        Json(result).GetProperty("error").GetString().Should().Be("change_in_progress");
        (await AuditRows(db)).Should().ContainSingle().Which.Outcome.Should().Be("Busy");
    }

    [Fact]
    public async Task Attention_NonAdmin_403()
    {
        using InferenceApiDbContext db = await SeededAsync();

        (await Controller(db, new Admin(false)).Attention(CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Attention_ListsStuckAndOverdue_NoProviderCall()
    {
        using InferenceApiDbContext db = NewDb();
        DateTime now = DateTime.UtcNow;
        db.Plans.Add(new Plan { Id = CreatorPlanId, Key = "creator", Name = "Creator", Kind = PlanKind.Subscription, IsActive = true });

        Guid changePending = Org(db, "Change pending");
        Watch(db, changePending, "change_plan", now.AddHours(-1));
        Sub(db, changePending, periodEnd: now.AddDays(20));

        Guid tooRecent = Org(db, "Too recent");
        Watch(db, tooRecent, "seats", now.AddMinutes(-2));
        Sub(db, tooRecent, periodEnd: now.AddDays(20));

        Guid renewal = Org(db, "Renewal");
        Sub(db, renewal, periodEnd: now.AddHours(-3));

        Guid cancel = Org(db, "Cancel");
        Sub(db, cancel, periodEnd: now.AddHours(-5), cancelAt: now.AddHours(-5));

        Guid checkout = Org(db, "Checkout");
        Watch(db, checkout, "checkout", now.AddMinutes(-30));

        Guid failing = Org(db, "Failing");
        Watch(db, failing, "cancel", now.AddMinutes(-20), failures: 2, lastError: "provider error: HTTP 503", lastChecked: now.AddMinutes(-1));
        Sub(db, failing, periodEnd: now.AddDays(20), status: SubscriptionStatus.PastDue);

        Guid healthy = Org(db, "Healthy");
        Watch(db, healthy, "overdue", null, lastChecked: now.AddHours(-1));
        Sub(db, healthy, periodEnd: now.AddDays(20));

        Guid trial = Org(db, "Trial");
        Sub(db, trial, periodEnd: now.AddDays(-2), provider: null);
        await db.SaveChangesAsync();

        StubReconciler reconciler = new();
        ActionResult<BillingReconcileListResponse> result = await Controller(db, new Admin(true), reconciler).Attention(CancellationToken.None);

        reconciler.Calls.Should().BeEmpty("the list is read from the database only");
        BillingReconcileListResponse body = (BillingReconcileListResponse)result.Result.Should().BeOfType<OkObjectResult>().Which.Value!;
        body.StuckAfterSeconds.Should().Be(600);
        body.Items.Select(i => (i.OrganizationName, i.Reason)).Should().Equal(
            ("Cancel", "cancel_overdue"),
            ("Renewal", "renewal_overdue"),
            ("Change pending", "change_pending"),
            ("Checkout", "checkout_unconfirmed"),
            ("Failing", "reconcile_failing"));

        BillingReconcileItem pending = body.Items.Single(i => i.OrganizationId == changePending);
        pending.PlanKey.Should().Be("creator");
        pending.Status.Should().Be("Active");
        pending.WaitingSince.Should().Be(now.AddHours(-1));
        body.Items.Single(i => i.OrganizationId == renewal).WaitingSince.Should().Be(now.AddHours(-3));
        body.Items.Single(i => i.OrganizationId == cancel).WaitingSince.Should().Be(now.AddHours(-5));
        BillingReconcileItem checkoutItem = body.Items.Single(i => i.OrganizationId == checkout);
        checkoutItem.PlanKey.Should().BeNull();
        checkoutItem.Status.Should().BeNull();
        BillingReconcileItem failingItem = body.Items.Single(i => i.OrganizationId == failing);
        failingItem.Status.Should().Be("PastDue");
        failingItem.LastError.Should().Be("provider error: HTTP 503");
        failingItem.LastCheckedAt.Should().Be(now.AddMinutes(-1));

        // The wire names the web client reads.
        JsonElement json = JsonSerializer.SerializeToElement(body, Web);
        json.GetProperty("stuckAfterSeconds").GetInt32().Should().Be(600);
        JsonElement first = json.GetProperty("items")[0];
        foreach (string name in new[] { "organizationId", "organizationName", "planKey", "status", "reason", "waitingSince", "lastCheckedAt", "lastOutcome", "lastError" })
            first.TryGetProperty(name, out _).Should().BeTrue(name);
    }

    [Fact]
    public async Task Attention_AbandonedCheckout_NotListed()
    {
        using InferenceApiDbContext db = NewDb();
        DateTime now = DateTime.UtcNow;

        Guid abandoned = Org(db, "Abandoned");
        Watch(db, abandoned, "checkout", now.AddHours(-2), lastChecked: now.AddMinutes(-5), outcome: "NothingToReconcile");
        await db.SaveChangesAsync();

        ActionResult<BillingReconcileListResponse> result = await Controller(db, new Admin(true)).Attention(CancellationToken.None);

        BillingReconcileListResponse body = (BillingReconcileListResponse)result.Result.Should().BeOfType<OkObjectResult>().Which.Value!;
        body.Items.Should().BeEmpty("Paddle has no live subscription for it: the checkout was abandoned, not lost");
    }

    [Theory]
    [InlineData(null, 0, "checkout_unconfirmed")]
    [InlineData("TooRecent", 0, "checkout_unconfirmed")]
    [InlineData("ProviderUnavailable", 1, "reconcile_failing")]
    [InlineData("Stale", 2, "reconcile_failing")]
    public async Task Attention_UnconfirmedCheckout_StillListed(string? outcome, int failures, string reason)
    {
        using InferenceApiDbContext db = NewDb();
        DateTime now = DateTime.UtcNow;

        Guid checkout = Org(db, "Checkout");
        Watch(db, checkout, "checkout", now.AddHours(-2), failures: failures,
            lastChecked: outcome is null ? null : now.AddMinutes(-5), outcome: outcome);
        await db.SaveChangesAsync();

        ActionResult<BillingReconcileListResponse> result = await Controller(db, new Admin(true)).Attention(CancellationToken.None);

        BillingReconcileListResponse body = (BillingReconcileListResponse)result.Result.Should().BeOfType<OkObjectResult>().Which.Value!;
        body.Items.Select(i => (i.OrganizationId, i.Reason)).Should().Equal((checkout, reason));
    }

    private static Guid Org(InferenceApiDbContext db, string name)
    {
        Guid id = Guid.NewGuid();
        db.Organizations.Add(new Organization { Id = id, Name = name, Slug = name.Replace(' ', '-').ToLowerInvariant() });
        return id;
    }

    private static void Watch(InferenceApiDbContext db, Guid org, string kind, DateTime? requestedAt,
        int failures = 0, string? lastError = null, DateTime? lastChecked = null, string? outcome = null) =>
        db.BillingReconcileWatches.Add(new BillingReconcileWatch
        {
            OrganizationId = org,
            ChangeKind = kind,
            ChangeRequestedAt = requestedAt,
            ConsecutiveFailures = failures,
            LastError = lastError,
            LastCheckedAt = lastChecked,
            LastOutcome = outcome ?? (lastChecked is null ? null : failures > 0 ? "ProviderUnavailable" : "InSync")
        });

    private static void Sub(InferenceApiDbContext db, Guid org, DateTime periodEnd, DateTime? cancelAt = null,
        SubscriptionStatus status = SubscriptionStatus.Active, string? provider = "paddle") =>
        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            OrganizationId = org,
            PlanId = CreatorPlanId,
            Provider = provider,
            ProviderSubscriptionId = provider is null ? null : "sub_" + org.ToString("N"),
            Status = status,
            PeriodStart = periodEnd.AddMonths(-1),
            PeriodEnd = periodEnd,
            CancelAtPeriodEnd = cancelAt is not null,
            CancelEffectiveAt = cancelAt,
            CreatedAt = DateTime.UtcNow.AddDays(-60)
        });
}
