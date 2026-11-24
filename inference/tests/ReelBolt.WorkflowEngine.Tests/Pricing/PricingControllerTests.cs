#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Admin;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Pricing;

/// <summary>
/// <c>/api/v1/pricing/*</c> (docs/design/model-catalog.md §6): the global markup, the billing-mode multipliers (moved
/// from <c>/api/v1/rate-multipliers</c> with identical rules — their tests moved here from <c>ModelPriceServiceTests</c>),
/// the coverage check and the price book. Every action is platform-admin only.
/// </summary>
public class PricingControllerTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Admin(bool isAdmin) : ICurrentUser
    {
        public Guid UserId => Guid.NewGuid();
        public string Email => "a@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => isAdmin;
        public Guid? OrganizationId => null;
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => null;
    }

    private sealed class CountingPrices : IModelPriceService
    {
        public int Invalidations;
        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default) => default;
        public ValueTask<PriceQuote> PriceQuotedUsdAsync(decimal costUsd, UsageKind unit, UsageBillingMode billingMode,
            DateTime? at = null, CancellationToken ct = default) => default;
        public void Invalidate() => Invalidations++;
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static PricingController Controller(InferenceApiDbContext db, bool isAdmin = true, CountingPrices? prices = null) =>
        new(db, new Admin(isAdmin), prices ?? new CountingPrices(), new FixedTime());

    // ------------------------------------------------------------------ authority

    [Fact]
    public async Task Every_pricing_action_is_forbidden_to_a_non_platform_admin_and_writes_nothing()
    {
        using InferenceApiDbContext db = NewDb();
        CountingPrices prices = new();
        PricingController c = Controller(db, isAdmin: false, prices);

        (await c.GetSettings(default)).Result.Should().BeOfType<ForbidResult>();
        (await c.UpdateSettings(new UpdatePricingSettingsRequest(2m), default)).Result.Should().BeOfType<ForbidResult>();
        (await c.Coverage(default)).Result.Should().BeOfType<ForbidResult>();
        (await c.ListMultipliers(default)).Result.Should().BeOfType<ForbidResult>();
        (await c.CreateMultiplier(new CreateRateMultiplierRequest("Byo", null, 1m, null), default)).Result.Should().BeOfType<ForbidResult>();
        c.PriceBookFor("Anthropic", "Chat", "claude-opus-5-5").Result.Should().BeOfType<ForbidResult>();

        (await db.PricingSettings.CountAsync()).Should().Be(0);
        (await db.RateMultipliers.CountAsync()).Should().Be(0);
        prices.Invalidations.Should().Be(0);
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public async Task Settings_read_the_defaults_until_a_markup_is_saved()
    {
        using InferenceApiDbContext db = NewDb();

        PricingSettingsResponse settings =
            (PricingSettingsResponse)((OkObjectResult)(await Controller(db).GetSettings(default)).Result!).Value!;

        settings.Markup.Should().Be(PricingDefaults.DefaultMarkup);
        settings.CreditUsdValue.Should().Be(PricingDefaults.CreditUsdValue);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("10.0001")]
    [InlineData(null)]
    public async Task Markup_must_be_above_zero_and_at_most_ten(string? markup)
    {
        using InferenceApiDbContext db = NewDb();
        CountingPrices prices = new();

        ActionResult<PricingSettingsResponse> result = await Controller(db, prices: prices).UpdateSettings(
            new UpdatePricingSettingsRequest(markup is null ? null : decimal.Parse(markup, System.Globalization.CultureInfo.InvariantCulture)),
            default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await db.PricingSettings.CountAsync()).Should().Be(0);
        prices.Invalidations.Should().Be(0);
    }

    [Theory]
    [InlineData("10")]
    [InlineData("1.35")]
    [InlineData("0.5")]
    public async Task A_valid_markup_is_saved_keeps_the_credit_value_and_invalidates_the_price_cache(string markup)
    {
        using InferenceApiDbContext db = NewDb();
        db.PricingSettings.Add(new PricingSettings { Markup = 1.2m, CreditUsdValue = 0.006m, UpdatedAt = Now.AddDays(-5) });
        await db.SaveChangesAsync();
        CountingPrices prices = new();
        decimal value = decimal.Parse(markup, System.Globalization.CultureInfo.InvariantCulture);

        ActionResult<PricingSettingsResponse> result = await Controller(db, prices: prices).UpdateSettings(new UpdatePricingSettingsRequest(value), default);

        PricingSettingsResponse body = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<PricingSettingsResponse>().Subject;
        body.Markup.Should().Be(value);
        body.CreditUsdValue.Should().Be(0.006m);
        body.UpdatedAt.Should().Be(Now);
        (await db.PricingSettings.AsNoTracking().SingleAsync()).Markup.Should().Be(value);
        prices.Invalidations.Should().Be(1);
    }

    [Fact]
    public async Task Saving_a_markup_creates_the_settings_row_when_it_is_missing()
    {
        using InferenceApiDbContext db = NewDb();

        (await Controller(db).UpdateSettings(new UpdatePricingSettingsRequest(1.4m), default)).Result.Should().BeOfType<OkObjectResult>();

        PricingSettings row = await db.PricingSettings.AsNoTracking().SingleAsync();
        row.Id.Should().Be(PricingSettings.SingletonId);
        row.Markup.Should().Be(1.4m);
        row.CreditUsdValue.Should().Be(PricingDefaults.CreditUsdValue);
    }

    // ------------------------------------------------------------------ coverage

    [Fact]
    public async Task Coverage_lists_enabled_platform_capabilities_missing_a_unit_with_their_provider_id()
    {
        using InferenceApiDbContext db = NewDb();
        ModelCapabilityView partly = Row("Partly priced", null, InferenceProviderCapability.Chat);
        ModelCapabilityView full = Row("Fully priced", null, InferenceProviderCapability.Embedding);
        ModelCapabilityView byo = Row("Workspace's own", Guid.NewGuid(), InferenceProviderCapability.Chat);
        db.AddFlat(partly, full, byo);
        db.ModelCapabilityPrices.Add(Price(partly.Id, UsageKind.LlmInputTokens));
        db.ModelCapabilityPrices.Add(Price(full.Id, UsageKind.EmbeddingTokens));
        await db.SaveChangesAsync();

        List<PriceCoverageGapResponse> gaps = (await Controller(db).Coverage(default)).Value
            .Should().BeAssignableTo<List<PriceCoverageGapResponse>>().Subject;

        PriceCoverageGapResponse gap = gaps.Should().ContainSingle().Subject;
        gap.CapabilityId.Should().Be(partly.Id);
        gap.ProviderId.Should().Be(partly.ProviderId);
        gap.ProviderName.Should().Be("Partly priced");
        gap.ModelDisplayName.Should().Be("Partly priced");
        gap.Capability.Should().Be("Chat");
        gap.MissingUnits.Should().BeEquivalentTo(["LlmOutputTokens", "LlmCachedInputTokens"]);
    }

    private static ModelCapabilityView Row(string name, Guid? org, InferenceProviderCapability capability) => new()
    {
        Id = Guid.NewGuid(),
        ProviderId = Guid.NewGuid(),
        OrganizationId = org,
        Name = name,
        Kind = InferenceProviderKind.OpenAICompatible,
        Capability = capability,
        Endpoint = "https://198.51.100.7",
        ModelName = "m"
    };

    private static ModelCapabilityPrice Price(Guid capabilityId, UsageKind unit) => new()
    {
        Id = Guid.NewGuid(),
        ModelCapabilityId = capabilityId,
        Unit = unit,
        CostUsdPerUnit = 0.000001m,
        EffectiveFrom = Now.AddDays(-1)
    };

    // ------------------------------------------------------------------ multipliers (moved from /api/v1/rate-multipliers)

    [Fact]
    public async Task CreatingAMultiplier_ClosesTheOpenOne_AndInvalidatesThePriceCache()
    {
        using InferenceApiDbContext db = NewDb();
        CountingPrices prices = new();
        PricingController c = Controller(db, prices: prices);
        DateTime first = Now.AddDays(-2);
        DateTime second = Now.AddDays(-1);

        await c.CreateMultiplier(new CreateRateMultiplierRequest("Byo", null, 0.1m, first), CancellationToken.None);
        await c.CreateMultiplier(new CreateRateMultiplierRequest("byo", null, 0.2m, second), CancellationToken.None);

        List<RateMultiplier> rows = await db.RateMultipliers.OrderBy(r => r.EffectiveFrom).ToListAsync();
        rows[0].EffectiveTo.Should().Be(second);
        rows[1].EffectiveTo.Should().BeNull();
        prices.Invalidations.Should().Be(2);
    }

    [Fact]
    public async Task Multipliers_travel_by_name_and_a_kind_specific_row_is_its_own_series()
    {
        using InferenceApiDbContext db = NewDb();
        PricingController c = Controller(db);

        ActionResult<RateMultiplierResponse> created = await c.CreateMultiplier(
            new CreateRateMultiplierRequest("Runner", "RenderOutputSeconds", 0.5m, null), default);
        await c.CreateMultiplier(new CreateRateMultiplierRequest("Runner", null, 0m, null), default);

        RateMultiplierResponse body = (RateMultiplierResponse)((CreatedAtActionResult)created.Result!).Value!;
        body.BillingMode.Should().Be("Runner");
        body.UsageKind.Should().Be("RenderOutputSeconds");
        body.EffectiveFrom.Should().Be(Now);
        List<RateMultiplierResponse> list = (await c.ListMultipliers(default)).Value!;
        list.Should().HaveCount(2);
        list.Should().OnlyContain(m => m.EffectiveTo == null);
        list.Select(m => m.UsageKind).Should().Equal(null, "RenderOutputSeconds");
    }

    [Theory]
    [InlineData("Nope", null, "1")]
    [InlineData("3", null, "1")]
    [InlineData(null, null, "1")]
    [InlineData("Byo", "NoSuchKind", "1")]
    [InlineData("Byo", null, "-0.5")]
    public async Task A_multiplier_with_an_unknown_mode_or_kind_or_a_negative_value_is_400(string? mode, string? kind, string multiplier)
    {
        using InferenceApiDbContext db = NewDb();

        ActionResult<RateMultiplierResponse> result = await Controller(db).CreateMultiplier(new CreateRateMultiplierRequest(
            mode, kind, decimal.Parse(multiplier, System.Globalization.CultureInfo.InvariantCulture), null), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await db.RateMultipliers.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Two_multipliers_may_not_start_at_the_same_instant()
    {
        using InferenceApiDbContext db = NewDb();
        PricingController c = Controller(db);
        DateTime at = Now.AddDays(-3);

        await c.CreateMultiplier(new CreateRateMultiplierRequest("Platform", null, 1m, at), default);
        (await c.CreateMultiplier(new CreateRateMultiplierRequest("Platform", null, 2m, at), default))
            .Result.Should().BeOfType<ConflictObjectResult>();
    }

    // ------------------------------------------------------------------ price book

    [Fact]
    public void The_price_book_proposes_the_catalog_prices_of_a_known_model()
    {
        using InferenceApiDbContext db = NewDb();
        ModelPrice entry = ModelPriceCatalog.Priced.First(e => e.ProviderKind == InferenceProviderKind.Anthropic
                                                               && e.Capability == InferenceProviderCapability.Chat);

        ActionResult<List<PriceBookEntryResponse>> result = Controller(db).PriceBookFor("Anthropic", "Chat", entry.Model);

        List<PriceBookEntryResponse> rows = result.Value!;
        rows.Select(r => r.Unit).Should().BeEquivalentTo(entry.UsdPerUnit.Keys.Select(k => k.ToString()));
        rows.Should().OnlyContain(r => r.Notes != null && r.Notes.Contains(PricingDefaults.PlaceholderMarker));
        rows.Single(r => r.Unit == nameof(UsageKind.LlmInputTokens)).CostUsdPerUnit.Should().Be(entry.UsdPerUnit[UsageKind.LlmInputTokens]);
        Controller(db).PriceBookFor("Anthropic", "Chat", "a-model-nobody-prices").Value.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Nope", "Chat")]
    [InlineData("Anthropic", "Nope")]
    [InlineData(null, "Chat")]
    public void The_price_book_refuses_an_unknown_kind_or_capability(string? kind, string? capability)
    {
        using InferenceApiDbContext db = NewDb();

        Controller(db).PriceBookFor(kind, capability, "m").Result.Should().BeOfType<BadRequestObjectResult>();
    }
}
