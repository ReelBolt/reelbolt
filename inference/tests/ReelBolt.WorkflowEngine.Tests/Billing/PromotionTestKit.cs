#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Admin;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Promotions;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Marketing;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>A clock the promotion tests move by hand.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Any caller the promotion controllers need: an org member, a platform admin, or both.</summary>
internal sealed class PromoUser(Guid userId, Guid? org, OrganizationRole? role, bool admin, string email = "u@example.com") : ICurrentUser
{
    public Guid UserId => userId;
    public string Email => email;
    public bool IsAuthenticated => true;
    public Guid? OrganizationId => org;
    public OrganizationRole? OrgRole => role;
    public bool IsPlatformAdmin => admin;
}

/// <summary>
/// saas-promotions T-003: shared Sqlite fixture for the promotion tests, seeded like
/// <see cref="BillingControllerTests"/> (creator and team sellable through the fake provider, pro not)
/// plus an owner, a platform admin and the Platform org.
/// </summary>
public abstract class PromotionTestBase : IDisposable
{
    internal static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string? _file;
    private readonly SqliteConnection _connection;
    protected readonly Guid Org = Guid.NewGuid();
    protected readonly Guid OwnerId = Guid.NewGuid();
    protected readonly Guid AdminId = Guid.NewGuid();
    internal readonly BillingControllerTests.FakeProvider Provider = new();
    internal readonly ManualTime Clock = new(DateTimeOffset.UtcNow);
    internal readonly PromotionCodeAttemptLimiter Limiter;
    internal readonly PromotionOptions PromoOptions = new();
    /// <summary>The marketing tests' counter store: the production C5 sliding window over in-memory rows.</summary>
    internal readonly InMemoryMailRateCounterStore Counters = new();

    /// <param name="onDisk">A temp-file database, so separate connections can race each other.</param>
    protected PromotionTestBase(bool onDisk = false)
    {
        _file = onDisk ? Path.Combine(Path.GetTempPath(), $"promo-{Guid.NewGuid():N}.db") : null;
        _connection = new SqliteConnection(_file is null ? "Data Source=:memory:" : $"Data Source={_file};Pooling=False");
        _connection.Open();
        Limiter = new PromotionCodeAttemptLimiter(Clock);
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
        db.ApplicationUsers.AddRange(
            new ApplicationUser { Id = OwnerId, Email = "owner@acme.test", DisplayName = "Owner", EmailVerifiedAt = DateTime.UtcNow },
            new ApplicationUser { Id = AdminId, Email = "admin@platform.test", DisplayName = "Admin", IsAdmin = true });
        db.Organizations.AddRange(
            new Organization { Id = Org, Name = "Acme Studio", Slug = "acme", Kind = OrganizationKind.Team },
            new Organization { Id = WellKnownOrganizations.PlatformId, Name = "Platform", Slug = WellKnownOrganizations.PlatformSlug, Kind = OrganizationKind.Platform });
        db.OrganizationMemberships.AddRange(
            new OrganizationMembership { OrganizationId = Org, UserId = OwnerId, Role = OrganizationRole.Owner },
            new OrganizationMembership { OrganizationId = WellKnownOrganizations.PlatformId, UserId = AdminId, Role = OrganizationRole.Owner });
        db.Plans.AddRange(
            new Plan { Id = Guid.NewGuid(), Key = "trial", Name = "Trial", Kind = PlanKind.Trial, SortOrder = 0 },
            new Plan { Id = Guid.NewGuid(), Key = "creator", Name = "Creator", Kind = PlanKind.Subscription, PriceMonthlyUsd = 29, MonthlyCreditsMillis = 1_000_000, TopUpAllowed = true, SortOrder = 1, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_creator"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "team", Name = "Team", Kind = PlanKind.PerSeat, MinSeats = 3, PriceMonthlyUsd = 49, MonthlyCreditsMillis = 1_500_000, TopUpAllowed = true, SortOrder = 2, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_team"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "pro", Name = "Pro", Kind = PlanKind.Subscription, PriceMonthlyUsd = 79, MonthlyCreditsMillis = 3_500_000, TopUpAllowed = true, SortOrder = 3 });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (_file is not null)
        {
            SqliteConnection.ClearAllPools();
            File.Delete(_file);
        }
    }

    internal InferenceApiDbContext NewDb()
        => _file is null
            ? new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options)
            : new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite($"Data Source={_file};Pooling=False").Options);

    internal DateTime Now => Clock.GetUtcNow().UtcDateTime;

    /// <summary>A fresh account with a verified email (C8: only verified accounts reserve or redeem).</summary>
    internal Guid VerifiedUser()
    {
        using InferenceApiDbContext db = NewDb();
        Guid id = Guid.NewGuid();
        db.ApplicationUsers.Add(new ApplicationUser { Id = id, Email = $"{id:N}@example.test", DisplayName = "member", EmailVerifiedAt = DateTime.UtcNow });
        db.SaveChanges();
        return id;
    }

    internal PromotionService Service(InferenceApiDbContext db)
        => new(db, [Provider], Limiter, NullLogger<PromotionService>.Instance, Clock, Options.Create(PromoOptions), Counters);

    /// <summary>The plan-prices service for tests that do not exercise it: no provider, so always empty.</summary>
    internal static IPromotionPlanPriceService NoPlanPrices { get; } = new Moq.Mock<IPromotionPlanPriceService>().Object;

    internal static IOptions<BillingOptions> Billing(bool enabled) => Options.Create(new BillingOptions { Enabled = enabled });

    internal static T WithHttp<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    internal PromotionsController Promotions(Guid? userId = null, OrganizationRole? role = OrganizationRole.Owner, bool enabled = true)
    {
        InferenceApiDbContext db = NewDb();
        return WithHttp(new PromotionsController(new PromoUser(userId ?? OwnerId, Org, role, false), Billing(enabled), Service(db), NoPlanPrices));
    }

    internal PromotionsController Anonymous(bool enabled = true)
    {
        InferenceApiDbContext db = NewDb();
        return WithHttp(new PromotionsController(new PromoUser(Guid.Empty, null, null, false), Billing(enabled), Service(db), NoPlanPrices));
    }

    internal PromotionsAdminController Admin(bool isAdmin = true, bool enabled = true)
    {
        InferenceApiDbContext db = NewDb();
        return WithHttp(new PromotionsAdminController(db, new PromoUser(AdminId, WellKnownOrganizations.PlatformId, OrganizationRole.Owner, isAdmin), Billing(enabled), Service(db)));
    }

    internal OrgCreditsAdminController Credits(bool isAdmin = true, bool enabled = true)
    {
        InferenceApiDbContext db = NewDb();
        return WithHttp(new OrgCreditsAdminController(db, new PromoUser(AdminId, WellKnownOrganizations.PlatformId, OrganizationRole.Owner, isAdmin), Billing(enabled), Service(db)));
    }

    internal BillingController BillingApi(OrganizationRole role = OrganizationRole.Owner, bool enabled = true, Guid? userId = null, Stream? body = null, Guid? orgId = null)
    {
        InferenceApiDbContext db = NewDb();
        BillingController c = new(
            db,
            new PromoUser(userId ?? OwnerId, orgId ?? Org, role, false),
            Billing(enabled),
            [Provider],
            new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, Clock),
            new BillingControllerTests.FakeTenant(),
            Options.Create(new PaddleOptions()),
            NullLogger<BillingController>.Instance,
            promotions: Service(db));
        DefaultHttpContext http = new();
        if (body is not null) http.Request.Body = body;
        c.ControllerContext = new ControllerContext { HttpContext = http };
        return c;
    }

    /// <summary>Stores a promotion directly (bypassing admin create) and returns its id.</summary>
    internal Guid Seed(PromotionKind kind, Action<Promotion>? configure = null)
    {
        Promotion p = new()
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Name = kind.ToString(),
            PublicHeadline = $"{kind} headline",
            Status = PromotionStatus.Active,
            CreatedByUserId = AdminId,
        };
        switch (kind)
        {
            case PromotionKind.CreditCode:
                p.Code = "WELCOME50";
                p.CreditsMillis = 50_000;
                p.CreditExpiryDays = 30;
                break;
            case PromotionKind.PlanDiscount:
                p.DiscountType = PromotionDiscountType.Percentage;
                p.DiscountAmount = "20";
                p.DurationKind = PromotionDuration.Once;
                p.TargetPriceIdsJson = """["pri_creator","pri_team"]""";
                p.ProviderDiscountId = "dsc_plan";
                break;
            case PromotionKind.PackDiscount:
                p.DiscountType = PromotionDiscountType.Percentage;
                p.DiscountAmount = "10";
                p.DurationKind = PromotionDuration.Once;
                p.TargetPriceIdsJson = """["pri_topup_500","pri_topup_1000"]""";
                p.ProviderDiscountId = "dsc_pack";
                break;
            case PromotionKind.PackBonus:
                p.BonusPercent = 20;
                p.TargetPriceIdsJson = """["pri_topup_500","pri_topup_1000"]""";
                break;
        }

        configure?.Invoke(p);
        using InferenceApiDbContext db = NewDb();
        db.Promotions.Add(p);
        db.SaveChanges();
        return p.Id;
    }

    /// <summary>Subscribes <see cref="Org"/> to creator through the processor (so top-ups are allowed).</summary>
    internal async Task SubscribeAsync()
    {
        await using InferenceApiDbContext db = NewDb();
        await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, Clock).ProcessAsync(new BillingEvent
        {
            Provider = "fake",
            EventId = Guid.NewGuid().ToString("N"),
            EventType = "subscription.created",
            OccurredAt = Now,
            Kind = BillingEventKind.SubscriptionChanged,
            OrganizationId = Org,
            CustomerId = "ctm_1",
            SubscriptionId = "sub_1",
            Status = SubscriptionStatus.Active,
            PeriodStart = Now,
            PeriodEnd = Now.AddMonths(1),
            Items = [new BillingLineItem("pri_creator", 1)]
        }, "{}");
    }

    /// <summary>The body exactly as the API would write it (web defaults: camelCase).</summary>
    internal static string Body(IActionResult result) => JsonSerializer.Serialize(((ObjectResult)result).Value, Web);

    internal static JsonElement JsonOf(IActionResult result) => JsonDocument.Parse(Body(result)).RootElement;

    internal static int? StatusOf(IActionResult result) => result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => null
    };

    internal static string? ErrorCode(IActionResult result)
        => JsonOf(result).TryGetProperty("error", out JsonElement e) ? e.GetString() : null;

    internal async Task<long> BalanceMillisAsync()
    {
        await using InferenceApiDbContext db = NewDb();
        return (await new CreditLedger(db).GetBalanceAsync(Org)).BalanceMillis;
    }
}
