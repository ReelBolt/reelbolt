#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>An ImageGenerate step's credit hold is priced from its own settings, through the price book (offline fakes).</summary>
public sealed class ImageGenerateCreditPricingTests
{
    private static ResolvedImageProvider Row(InferenceProviderCapability capability, string model = "", Guid? organization = null) =>
        new(Guid.NewGuid(), "row", InferenceProviderKind.FalAi, capability, "", model, "test", 60,
            OrganizationId: organization, IsPlatformManaged: organization is null, AccountId: Guid.NewGuid());

    private static (Mock<IInferenceProviderResolver> Resolver, Mock<IModelPriceService> Prices, List<PriceRequest> Asked) Wire(
        ResolvedImageProvider? generate, ResolvedImageProvider? edit, Func<PriceRequest, PriceQuote>? quote = null)
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveImageGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(generate);
        resolver.Setup(r => r.ResolveImageEditAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(edit);
        List<PriceRequest> asked = [];
        var prices = new Mock<IModelPriceService>();
        prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PriceRequest, CancellationToken>((r, _) => asked.Add(r))
            .Returns((PriceRequest r, CancellationToken _) =>
                new ValueTask<PriceQuote>(quote?.Invoke(r) ?? new PriceQuote(2_000, 0.04m, Guid.NewGuid())));
        return (resolver, prices, asked);
    }

    [Fact]
    public async Task Generate_holds_variations_times_one_picture_at_the_generation_price_row()
    {
        ResolvedImageProvider row = Row(InferenceProviderCapability.ImageGeneration);
        var (resolver, prices, asked) = Wire(row, null);

        long? hold = await ImageGenerateCreditPricing.TryHoldMillisAsync(
            """{"mode":"Generate","prompt":"x","variations":3}""", resolver.Object, prices.Object);

        PriceRequest request = asked.Should().ContainSingle().Which;
        request.Unit.Should().Be(UsageKind.ImageGenImages);
        request.Capability.Should().Be(InferenceProviderCapability.ImageGeneration);
        request.ModelCode.Should().StartWith("ideogram/v4.5", "an empty row model is the catalogue default for the job (plus its price tier)");
        request.Quantity.Should().Be(1m);
        request.ModelCapabilityId.Should().Be(row.ProviderId);
        request.BillingMode.Should().Be(UsageBillingMode.Platform);
        hold.Should().Be(6_000);
    }

    [Fact]
    public async Task Edit_holds_one_picture_at_the_edit_price_row()
    {
        var (resolver, prices, asked) = Wire(null, Row(InferenceProviderCapability.ImageEdit));
        string json = $$$"""{"mode":"Edit","instruction":"x","allowSourceMediaEgress":true,"source":{"kind":"ProjectFile","projectFileId":"{{{Guid.NewGuid()}}}"}}""";

        long? hold = await ImageGenerateCreditPricing.TryHoldMillisAsync(json, resolver.Object, prices.Object);

        asked.Should().ContainSingle().Which.Unit.Should().Be(UsageKind.ImageEditImages);
        hold.Should().Be(2_000);
    }

    [Fact]
    public async Task An_organizations_own_row_holds_nothing_but_the_ledger_cost_is_still_the_platform_rate()
    {
        ResolvedImageProvider row = Row(InferenceProviderCapability.ImageGeneration, organization: Guid.NewGuid());
        var (resolver, prices, _) = Wire(row, null);

        (await ImageGenerateCreditPricing.TryHoldMillisAsync(
            """{"mode":"Generate","prompt":"x"}""", resolver.Object, prices.Object)).Should().Be(0);

        ImageUnitPrice price = await ImageGenerateCreditPricing.PriceOneAsync(
            prices.Object, new ImageGenerateStepConfig(ImageGenerateMode.Generate, "x"), row, "ideogram/v4.5", CancellationToken.None);
        price.CreditsMillis.Should().Be(0);
        price.CostUsd.Should().Be(0.04m);
    }

    [Fact]
    public async Task A_hold_that_cannot_be_sized_is_null_never_zero()
    {
        var (resolver, prices, _) = Wire(Row(InferenceProviderCapability.ImageGeneration), null);
        var none = new Mock<IInferenceProviderResolver>();
        none.Setup(r => r.ResolveImageGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync((ResolvedImageProvider?)null);
        var unpriced = new Mock<IModelPriceService>();
        unpriced.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .Throws(new UnpricedUsageException(UsageKind.ImageGenImages, InferenceProviderKind.FalAi, "m"));

        const string json = """{"mode":"Generate","prompt":"x"}""";
        (await ImageGenerateCreditPricing.TryHoldMillisAsync(json, none.Object, prices.Object)).Should().BeNull("no provider");
        (await ImageGenerateCreditPricing.TryHoldMillisAsync(json, resolver.Object, unpriced.Object)).Should().BeNull("no price");
        (await ImageGenerateCreditPricing.TryHoldMillisAsync("not json", resolver.Object, prices.Object)).Should().BeNull();
        (await ImageGenerateCreditPricing.TryHoldMillisAsync("""{"mode":"Generate","prompt":""}""", resolver.Object, prices.Object)).Should().BeNull();
        (await ImageGenerateCreditPricing.TryHoldMillisAsync(null, resolver.Object, prices.Object)).Should().BeNull();
    }

    [Fact]
    public void A_non_fal_row_uses_its_own_model_and_a_fal_row_only_a_model_of_the_jobs_catalogue()
    {
        ImageGenerateCreditPricing.ResolveModel(ImageGenerateMode.Generate,
            Row(InferenceProviderCapability.ImageGeneration) with { Kind = InferenceProviderKind.OpenAICompatible, ModelName = "gpt-image-1" })
            .Should().Be("gpt-image-1");
        // An edit endpoint on a generation row is not that job's endpoint: the default for the job is used.
        ImageGenerateCreditPricing.ResolveModel(ImageGenerateMode.Generate,
            Row(InferenceProviderCapability.ImageGeneration, "google/nano-banana-2.1/edit")).Should().Be("ideogram/v4.5");
        ImageGenerateCreditPricing.ResolveModel(ImageGenerateMode.Generate,
            Row(InferenceProviderCapability.ImageGeneration, "google/nano-banana-2.1")).Should().Be("google/nano-banana-2.1");
    }

    // ------------------------------------------------------------------ tier-priced endpoints (nano-banana)

    /// <summary>The platform snapshot the seeder writes for one fal endpoint: its tier rows, keyed model + variant.</summary>
    private sealed class FalCatalogSource(Guid capability, Guid account, InferenceProviderCapability kind, string model) : IModelPriceSource
    {
        public Task<ModelPriceSnapshot> LoadAsync(CancellationToken ct)
        {
            DateTime effective = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var rows = new List<ModelCapabilityPrice>();
            foreach (ModelPrice price in ModelPriceCatalog.All.Where(m =>
                         m.ProviderKind == InferenceProviderKind.FalAi && m.Capability == kind && m.Model.StartsWith(model + "#", StringComparison.Ordinal)))
            {
                (string _, string? tier) = MeteringImageClient.SplitBillingKey(price.Model);
                foreach ((UsageKind unit, decimal usd) in price.UsdPerUnit)
                    rows.Add(new ModelCapabilityPrice
                    {
                        Id = Guid.NewGuid(),
                        ModelCapabilityId = capability,
                        Unit = unit,
                        Variant = tier,
                        CostUsdPerUnit = usd,
                        EffectiveFrom = effective
                    });
            }

            List<RateMultiplier> multipliers =
            [
                new() { Id = Guid.NewGuid(), BillingMode = UsageBillingMode.Platform, Multiplier = 1m, EffectiveFrom = effective },
            ];
            return Task.FromResult(new ModelPriceSnapshot(
                [new PricedCapability(capability, account, null, InferenceProviderKind.FalAi, kind, model)], rows, multipliers));
        }
    }

    private static ModelPriceService NanoPrices(ResolvedImageProvider row, InferenceProviderCapability kind, string model) =>
        new(new FalCatalogSource(row.ProviderId!.Value, row.AccountId!.Value, kind, model));

    [Fact]
    public async Task A_one_megapixel_nano_banana_edit_is_priced_at_the_1K_tier_not_the_dearest_row()
    {
        // Prod 2026-11-20: an 843x1264 (~1.07 MP) edit billed 27.6 credits (the 4K row) instead of 8.8 (1K, $0.044).
        ResolvedImageProvider row = Row(InferenceProviderCapability.ImageEdit, "google/nano-banana-2.1/edit");
        ModelPriceService prices = NanoPrices(row, InferenceProviderCapability.ImageEdit, "google/nano-banana-2.1/edit");
        var config = System.Text.Json.JsonSerializer.Deserialize<ImageGenerateStepConfig>(
            $$$"""{"mode":"Edit","instruction":"x","width":843,"height":1264,"allowSourceMediaEgress":true,"source":{"kind":"ProjectFile","projectFileId":"{{{Guid.NewGuid()}}}"}}""",
            ImageGenerateStepConfig.JsonOptions)!;

        ImageUnitPrice price = await ImageGenerateCreditPricing.PriceOneAsync(prices, config, row, "google/nano-banana-2.1/edit", CancellationToken.None);

        price.CostUsd.Should().Be(0.044m);
        price.CreditsMillis.Should().Be(8_800);
        price.Variant.Should().Be("1K");
    }

    [Fact]
    public async Task A_one_megapixel_nano_banana_generate_is_priced_at_the_1K_tier()
    {
        ResolvedImageProvider row = Row(InferenceProviderCapability.ImageGeneration, "google/nano-banana-2.1");
        ModelPriceService prices = NanoPrices(row, InferenceProviderCapability.ImageGeneration, "google/nano-banana-2.1");
        var config = new ImageGenerateStepConfig(ImageGenerateMode.Generate, "x");

        ImageUnitPrice price = await ImageGenerateCreditPricing.PriceOneAsync(prices, config, row, "google/nano-banana-2.1", CancellationToken.None);

        price.CostUsd.Should().Be(0.04m);
        price.CreditsMillis.Should().Be(8_000);
        price.Variant.Should().Be("1K");
    }

    [Theory]
    [InlineData("1K", 0.040, 8_000)]
    [InlineData("2K", 0.059, 11_800)]
    [InlineData("4K", 0.134, 26_800)]
    public async Task Nano_generate_is_priced_at_the_chosen_resolution_tier(string tier, double usd, long millis)
    {
        ResolvedImageProvider row = Row(InferenceProviderCapability.ImageGeneration, "google/nano-banana-2.1");
        ModelPriceService prices = NanoPrices(row, InferenceProviderCapability.ImageGeneration, "google/nano-banana-2.1");
        var config = new ImageGenerateStepConfig(ImageGenerateMode.Generate, "x", Resolution: tier.ToLowerInvariant());

        ImageUnitPrice price = await ImageGenerateCreditPricing.PriceOneAsync(prices, config, row, "google/nano-banana-2.1", CancellationToken.None);

        price.CostUsd.Should().Be((decimal)usd);
        price.CreditsMillis.Should().Be(millis);
        price.Variant.Should().Be(tier);
        price.BillingKey.Should().Be("google/nano-banana-2.1#" + tier);
    }

    [Theory]
    [InlineData(1000, 1000, 0.044, 8_800)]
    [InlineData(2000, 2000, 0.063, 12_600)]
    [InlineData(4096, 3000, 0.138, 27_600)]
    public async Task Nano_edit_is_priced_at_the_tier_that_covers_the_output_size(int w, int h, double usd, long millis)
    {
        ResolvedImageProvider row = Row(InferenceProviderCapability.ImageEdit, "google/nano-banana-2.1/edit");
        ModelPriceService prices = NanoPrices(row, InferenceProviderCapability.ImageEdit, "google/nano-banana-2.1/edit");
        var config = System.Text.Json.JsonSerializer.Deserialize<ImageGenerateStepConfig>(
            $$$"""{"mode":"Edit","instruction":"x","outputWidth":{{{w}}},"outputHeight":{{{h}}},"allowSourceMediaEgress":true,"source":{"kind":"ProjectFile","projectFileId":"{{{Guid.NewGuid()}}}"}}""",
            ImageGenerateStepConfig.JsonOptions)!;

        ImageUnitPrice price = await ImageGenerateCreditPricing.PriceOneAsync(prices, config, row, "google/nano-banana-2.1/edit", CancellationToken.None);

        price.CostUsd.Should().Be((decimal)usd);
        price.CreditsMillis.Should().Be(millis);
    }

    [Fact]
    public async Task An_item_list_hold_is_items_times_variations_at_the_1K_tier_price()
    {
        ResolvedImageProvider row = Row(InferenceProviderCapability.ImageGeneration, "google/nano-banana-2.1");
        ModelPriceService prices = NanoPrices(row, InferenceProviderCapability.ImageGeneration, "google/nano-banana-2.1");
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveImageGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(row);

        long? hold = await ImageGenerateCreditPricing.TryHoldMillisAsync(
            """{"mode":"Generate","prompt":"A {item} sofa","itemList":"a,b,c","variations":2}""", resolver.Object, prices);

        hold.Should().Be(6 * 8_000, "3 items x 2 variations, each at the 1K tier ($0.04), not the dearest row");
    }
}
