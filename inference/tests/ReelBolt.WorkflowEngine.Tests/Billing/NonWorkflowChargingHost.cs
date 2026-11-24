using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Billing;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// Wires <see cref="AssistantAllowanceService"/> and <see cref="NonWorkflowChargeService"/> over one database
/// (Sqlite in memory, or a real Postgres), with the shipped plan catalog seeded and the real plan-backed
/// entitlements, so the allowance is read exactly as production reads it.
/// </summary>
internal sealed class NonWorkflowChargingHost : IDisposable
{
    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A price service that answers one fixed quote, or throws <see cref="UnpricedUsageException"/>.</summary>
    internal sealed class FixedPrices(long? creditsMillis) : IModelPriceService
    {
        public List<PriceRequest> Requests { get; } = [];

        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (request.BillingMode != UsageBillingMode.Platform) return ValueTask.FromResult(new PriceQuote(0, 0m, null));
            if (creditsMillis is not { } millis) throw new UnpricedUsageException(request.Unit, request.Kind, request.ModelCode ?? string.Empty);
            return ValueTask.FromResult(new PriceQuote((long)(millis * request.Quantity), 0.01m, Guid.NewGuid()));
        }

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(decimal costUsd, UsageKind unit, UsageBillingMode billingMode, DateTime? at = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Invalidate() { }
    }

    private readonly ServiceProvider _provider;

    public NonWorkflowChargingHost(
        Action<DbContextOptionsBuilder> configure,
        bool billingEnabled = true,
        IModelPriceService? prices = null,
        Clock? clock = null)
    {
        Time = clock ?? new Clock();
        ServiceCollection services = new();
        services.AddDbContext<InferenceApiDbContext>(configure);
        _provider = services.BuildServiceProvider();

        IOptions<BillingOptions> billing = Microsoft.Extensions.Options.Options.Create(new BillingOptions { Enabled = billingEnabled, EntitlementCacheSeconds = 0 });
        Entitlements = new PlanBackedOrganizationEntitlements(
            new EfPlanSource<InferenceApiDbContext>(_provider.GetRequiredService<IServiceScopeFactory>()),
            billing,
            new TenancyOptions(),
            Time);
        Allowance = new AssistantAllowanceService(
            _provider.GetRequiredService<IServiceScopeFactory>(), Entitlements, billing, Time);
        Options = new NonWorkflowChargeOptions { Enabled = billingEnabled };
        Charges = new NonWorkflowChargeService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Allowance,
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<NonWorkflowChargeService>.Instance,
            prices,
            Time);
    }

    public Clock Time { get; }
    public IOrganizationEntitlements Entitlements { get; }
    public AssistantAllowanceService Allowance { get; }
    public NonWorkflowChargeOptions Options { get; }
    public NonWorkflowChargeService Charges { get; }

    public InferenceApiDbContext NewDb() => _provider.CreateScope().ServiceProvider.GetRequiredService<InferenceApiDbContext>();

    /// <summary>The shipped plan catalog, upserted like the seeder does on boot.</summary>
    public async Task SeedPlansAsync()
    {
        await using InferenceApiDbContext db = NewDb();
        if (await db.Plans.AnyAsync()) return;
        db.Plans.AddRange(PlanCatalogSeeder.Load(File.ReadAllText(PlanCatalogSeeder.DefaultPath)));
        await db.SaveChangesAsync();
    }

    public async Task<Guid> AddOrgAsync(string? planKey = null, int seats = 1, DateTime? periodStart = null, DateTime? periodEnd = null,
        SubscriptionStatus status = SubscriptionStatus.Active)
    {
        Guid org = Guid.NewGuid();
        await using InferenceApiDbContext db = NewDb();
        db.Organizations.Add(new Organization { Id = org, Name = "o", Slug = "o-" + org.ToString("N"), Kind = OrganizationKind.Team });
        if (planKey is not null)
        {
            Guid planId = await db.Plans.Where(p => p.Key == planKey).Select(p => p.Id).SingleAsync();
            db.Subscriptions.Add(new Subscription
            {
                Id = Guid.NewGuid(),
                OrganizationId = org,
                PlanId = planId,
                Provider = "paddle",
                ProviderSubscriptionId = "sub_" + org.ToString("N"),
                Status = status,
                Seats = seats,
                PeriodStart = periodStart,
                PeriodEnd = periodEnd
            });
        }

        await db.SaveChangesAsync();
        return org;
    }

    public async Task AddUsageAsync(Guid org, UsageSource source, long creditsMillis, DateTime at)
    {
        await using InferenceApiDbContext db = NewDb();
        db.UsageEvents.Add(new UsageEvent
        {
            Id = Guid.NewGuid(),
            OrganizationId = org,
            Source = source,
            Kind = UsageKind.LlmOutputTokens,
            BillingMode = UsageBillingMode.Platform,
            CreditsMillis = creditsMillis,
            Quantity = 1,
            ProviderKind = InferenceProviderKind.Anthropic,
            ModelName = "m",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            OccurredAt = at
        });
        await db.SaveChangesAsync();
    }

    public async Task GrantAsync(Guid org, long millis)
    {
        await using InferenceApiDbContext db = NewDb();
        CreditLedgerResult r = await new CreditLedger(db, Time).GrantAsync(org, CreditGrantSource.Plan, millis, null, Guid.NewGuid().ToString("N"));
        if (!r.Ok) throw new InvalidOperationException(r.Error);
    }

    public async Task<CreditLedgerResult> BalanceAsync(Guid org)
    {
        await using InferenceApiDbContext db = NewDb();
        return await new CreditLedger(db, Time).GetBalanceAsync(org);
    }

    public async Task<List<CreditLedgerEntry>> EntriesAsync(Guid org)
    {
        await using InferenceApiDbContext db = NewDb();
        return (await db.CreditLedgerEntries.AsNoTracking().Where(e => e.OrganizationId == org).ToListAsync())
            .OrderBy(e => e.CreatedAt).ToList();
    }

    public void Dispose() => _provider.Dispose();
}
