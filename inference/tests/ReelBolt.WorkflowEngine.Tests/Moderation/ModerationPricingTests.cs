using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Billing;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// The moderation price is a real $0 row, not an unpriced gap: the committed seed validates it, the
/// seeder writes a <c>ModerationCalls</c> row with a non-null id, and billing yields
/// <c>PriceId != null</c> with zero credits.
/// </summary>
public sealed class ModerationPricingTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);
    private const string ModelCode = "omni-moderation-latest";
    private readonly string _dbName = Guid.NewGuid().ToString();

    [Fact]
    public void The_committed_seed_carries_a_valid_zero_ModerationCalls_table_for_OpenAICompatible()
    {
        ModelCatalogSeed seed = ModelCatalogSeed.LoadDefault();

        seed.Validate().Should().BeEmpty();

        SeedCapabilityEntry entry = seed.Find(
            InferenceProviderKind.OpenAICompatible, "ContentModeration", ModelCode)!;
        entry.Should().NotBeNull();
        entry.PriceSource.Should().Be("Table");
        entry.SelfHostedOnly.Should().BeFalse("OpenAI's moderation API is free for everyone, not only self-hosted");

        SeedPriceRow row = seed.Rows.Single(r => r.Kind == InferenceProviderKind.OpenAICompatible
            && r.ModelCode == ModelCode && r.Capability == "ContentModeration");
        row.Unit.Should().Be(UsageKind.ModerationCalls);
        row.CostUsdPerUnit.Should().Be(0m);
        row.Variant.Should().BeNull();
        row.SourceUrl.Should().StartWith("https://");
        row.Quoted.Should().Contain("$0");
    }

    [Fact]
    public void The_catalog_entry_is_priced_at_zero()
    {
        ModelPrice price = ModelPriceCatalog.All.Single(p =>
            p.ProviderKind == InferenceProviderKind.OpenAICompatible
            && p.Capability == InferenceProviderCapability.ContentModeration
            && p.Model == ModelCode);

        price.IsPriced.Should().BeTrue("a $0 vendor price is a price, not an unpriced gap");
        price.UsdPerUnit[UsageKind.ModerationCalls].Should().Be(0m);
    }

    [Fact]
    public async Task The_seeder_writes_a_real_zero_row_and_billing_yields_zero_credits_with_a_price_id()
    {
        Guid capabilityId = await AddCapabilityAsync();
        ModelCatalogSeed seed = ModelCatalogSeed.LoadDefault();
        DateTime at = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

        await using (InferenceApiDbContext db = CreateContext())
        {
            SeedRunReport report = await ModelCatalogPriceSeeder.RunAsync(
                db, seed, Now, dryRun: false, NullLogger.Instance);
            report.NotInSeed.Should().BeEmpty();
        }

        await using InferenceApiDbContext check = CreateContext();
        ModelCapabilityPrice row = check.ModelCapabilityPrices.AsNoTracking()
            .Single(p => p.ModelCapabilityId == capabilityId && p.Unit == UsageKind.ModerationCalls);
        row.Id.Should().NotBeEmpty("a real row has a real price id");
        row.CostUsdPerUnit.Should().Be(0m);
        row.EffectiveTo.Should().BeNull();

        PricedCapability capability = new(capabilityId, Guid.NewGuid(), null,
            InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.ContentModeration, ModelCode);
        ModelPriceSnapshot snapshot = new([capability], [row], []);
        PriceQuote quote = ModelPriceService.Price(snapshot, new PriceRequest(
            capabilityId, null, InferenceProviderKind.OpenAICompatible,
            InferenceProviderCapability.ContentModeration, ModelCode, UsageKind.ModerationCalls,
            null, UsageBillingMode.Platform, 1m, at), at);

        quote.PriceId.Should().NotBeNull("the $0 row is matched, not skipped as unpriced");
        quote.CostUsd.Should().Be(0m);
        quote.CreditsMillis.Should().Be(0);
    }

    private InferenceApiDbContext CreateContext()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(_dbName)
            .Options;
        return new InferenceApiDbContext(options);
    }

    private async Task<Guid> AddCapabilityAsync()
    {
        await using InferenceApiDbContext db = CreateContext();
        InferenceProvider provider = new()
        {
            Id = Guid.NewGuid(),
            Name = "OpenAI moderation",
            Kind = InferenceProviderKind.OpenAICompatible,
            Endpoint = "https://api.openai.com/v1"
        };
        InferenceModel model = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = provider.Id,
            ModelCode = ModelCode,
            DisplayName = "OpenAI omni-moderation"
        };
        ModelCapability capability = new()
        {
            Id = Guid.NewGuid(),
            ModelId = model.Id,
            Capability = InferenceProviderCapability.ContentModeration,
            PriceSource = ModelPriceSource.Table
        };
        db.InferenceProviders.Add(provider);
        db.InferenceModels.Add(model);
        db.ModelCapabilities.Add(capability);
        await db.SaveChangesAsync();
        return capability.Id;
    }
}
