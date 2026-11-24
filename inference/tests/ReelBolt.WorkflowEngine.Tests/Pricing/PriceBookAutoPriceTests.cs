#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Pricing;

/// <summary><see cref="PriceBook.AutoPriceAsync"/>: what a newly created platform capability is pre-priced with.</summary>
public sealed class PriceBookAutoPriceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private InferenceApiDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private sealed class CountingPrices : IModelPriceService
    {
        public int Invalidations;
        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default) => default;
        public ValueTask<PriceQuote> PriceQuotedUsdAsync(decimal costUsd, UsageKind unit, UsageBillingMode billingMode,
            DateTime? at = null, CancellationToken ct = default) => default;
        public void Invalidate() => Invalidations++;
    }

    private async Task<ModelCapability> AddAsync(InferenceProviderKind kind, InferenceProviderCapability capability, string code, Guid? org = null)
    {
        if (org is { } id && !await _db.Organizations.AnyAsync(o => o.Id == id))
            _db.Organizations.Add(new Organization { Id = id, Name = "o", Slug = "o" + id.ToString("N")[..6] });
        ModelCapability row = _db.AddFlat(new ModelCapabilityView
        {
            OrganizationId = org,
            Name = $"{kind} {code} {capability}",
            Kind = kind,
            Capability = capability,
            ModelName = code,
            Endpoint = "https://x"
        });
        await _db.SaveChangesAsync();
        return row;
    }

    [Fact]
    public async Task A_new_platform_capability_is_priced_from_the_book_and_the_cache_is_invalidated()
    {
        ModelCapability video = await AddAsync(InferenceProviderKind.MiniMax, InferenceProviderCapability.VideoGeneration, "MiniMax-H3-Max");
        CountingPrices prices = new();

        int added = await new PriceBook(_db, prices).AutoPriceAsync(video.Id);

        added.Should().Be(3);
        (await _db.ModelCapabilityPrices.Where(p => p.ModelCapabilityId == video.Id).Select(p => p.Variant).ToListAsync())
            .Should().BeEquivalentTo([null, "480P", "768P"]);
        prices.Invalidations.Should().Be(1);
    }

    [Fact]
    public async Task A_byo_capability_an_already_priced_one_and_an_unknown_model_are_left_alone()
    {
        ModelCapability byo = await AddAsync(InferenceProviderKind.Anthropic, InferenceProviderCapability.Chat, "claude-sonnet-5-5", Guid.NewGuid());
        ModelCapability unknown = await AddAsync(InferenceProviderKind.AzureOpenAI, InferenceProviderCapability.Chat, "gpt-4o");
        ModelCapability priced = await AddAsync(InferenceProviderKind.Anthropic, InferenceProviderCapability.Chat, "claude-opus-5-5");
        PriceBook book = new(_db);
        (await book.AutoPriceAsync(priced.Id)).Should().Be(3);

        (await book.AutoPriceAsync(byo.Id)).Should().Be(0, "BYO is never priced");
        (await book.AutoPriceAsync(unknown.Id)).Should().Be(0, "the book has no published price for it");
        (await book.AutoPriceAsync(priced.Id)).Should().Be(0, "a capability that has prices keeps them");
        (await book.AutoPriceAsync(Guid.NewGuid())).Should().Be(0);
        (await _db.ModelCapabilityPrices.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task The_system_compute_capability_gets_the_default_compute_prices()
    {
        ModelCapability compute = await AddAsync(ComputeMetering.ProviderKind, InferenceProviderCapability.Compute, WellKnownModelCatalog.ComputeModelCode);

        (await new PriceBook(_db).AutoPriceAsync(compute.Id)).Should().Be(PricingDefaults.ComputePrices().Count);
    }
}
