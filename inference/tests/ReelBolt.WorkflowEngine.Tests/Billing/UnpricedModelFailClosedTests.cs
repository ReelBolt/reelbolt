#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// which capabilities the two real stores report as unpriced (the set the resolver drops while billing is on):
/// an enabled PLATFORM, table-priced capability with no price row in force now, or one with no price in force
/// for some unit its capability meters. Organization (BYO) rows and oracle-priced
/// rows are never in it; neither is a disabled row (it is not offered anyway).
/// </summary>
public sealed class UnpricedModelFailClosedTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    private sealed record Seeded(Guid Open, Guid Expired, Guid Future, Guid None, Guid Byo, Guid Oracle, Guid Disabled, Guid Partial);

    private static Seeded Seed(DbContext db)
    {
        ModelCapabilityView Row(string name, Guid? org = null, ModelPriceSource source = ModelPriceSource.Table, bool enabled = true) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            ModelName = name,
            Kind = InferenceProviderKind.AzureOpenAI,
            Endpoint = "https://x.example",
            Capability = InferenceProviderCapability.Chat,
            OrganizationId = org,
            PriceSource = source,
            IsEnabled = enabled
        };

        ModelCapabilityView open = Row("open"), expired = Row("expired"), future = Row("future"), none = Row("none"),
            byo = Row("byo", Guid.NewGuid()), oracle = Row("oracle", source: ModelPriceSource.Oracle), disabled = Row("disabled", enabled: false),
            partial = Row("partial");
        db.AddFlat(open, expired, future, none, byo, oracle, disabled, partial);

        ModelCapabilityPrice Price(Guid cap, DateTime from, DateTime? to, UsageKind unit = UsageKind.LlmInputTokens) => new()
        {
            Id = Guid.NewGuid(),
            ModelCapabilityId = cap,
            Unit = unit,
            CostUsdPerUnit = 0.000001m,
            EffectiveFrom = from,
            EffectiveTo = to
        };

        // T-032 (low b): a chat capability is priced only when EVERY unit it meters has a price in force (the rule of
        // PriceCoverage.Find): "open" prices input, output and cached input; "partial" prices input only.
        db.AddRange(
            Price(open.Id, Now.AddDays(-1), null),
            Price(open.Id, Now.AddDays(-1), null, UsageKind.LlmOutputTokens),
            Price(open.Id, Now.AddDays(-1), null, UsageKind.LlmCachedInputTokens),
            Price(partial.Id, Now.AddDays(-1), null),
            Price(expired.Id, Now.AddDays(-10), Now.AddDays(-1)),
            Price(future.Id, Now.AddDays(1), null));
        db.SaveChanges();
        return new Seeded(open.Id, expired.Id, future.Id, none.Id, byo.Id, oracle.Id, disabled.Id, partial.Id);
    }

    [Fact]
    public async Task The_api_store_reports_platform_table_capabilities_with_no_price_in_force()
    {
        await using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"unpriced-api-{Guid.NewGuid():N}").Options);
        Seeded s = Seed(db);

        IReadOnlySet<Guid> unpriced = await ((IInferenceProviderStore)new InferenceApiProviderStore(db)).LoadUnpricedCapabilityIdsAsync(Now, CancellationToken.None);

        unpriced.Should().BeEquivalentTo([s.Expired, s.Future, s.None, s.Partial]);
    }

    [Fact]
    public async Task The_engine_store_reports_the_same_set()
    {
        await using WorkflowEngineDbContext db = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase($"unpriced-engine-{Guid.NewGuid():N}").Options);
        Seeded s = Seed(db);

        IReadOnlySet<Guid> unpriced = await ((IInferenceProviderStore)new WorkflowEngineProviderStore(db)).LoadUnpricedCapabilityIdsAsync(Now, CancellationToken.None);

        unpriced.Should().BeEquivalentTo([s.Expired, s.Future, s.None, s.Partial]);
    }
}
