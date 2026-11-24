#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// The hosted-isolation catalogue, its price row (cited, never invented), the capability rules and the fail-closed
/// credit pricing (rounding up to the minute and the 30-minute cap).
/// </summary>
public class AudioIsolationPricingTests
{
    private const string Model = "fal-ai/elevenlabs/audio-isolation";

    [Fact]
    public void The_catalogue_has_one_default_and_it_is_the_fal_hosted_isolator()
    {
        AudioIsolationModelCatalog.Instance.All.Count(e => e.IsDefault).Should().Be(1);
        AudioIsolationModelCatalog.Instance.Default.Id.Should().Be(Model);
        AudioIsolationModelCatalog.Instance.Default.HasBackgroundStem.Should().BeFalse("the schema returns one audio file");
        AudioIsolationModelCatalog.Instance.Find(InferenceProviderKind.ElevenLabs, Model).Should().BeNull("the ElevenLabs kind stays dormant");
    }

    [Fact]
    public void Every_catalogued_endpoint_has_the_same_cited_price_in_the_price_catalogue_and_nothing_else_is_priced()
    {
        foreach (AudioIsolationEndpoint endpoint in AudioIsolationModelCatalog.Instance.All)
        {
            ModelPrice price = ModelPriceCatalog.Priced.Single(p =>
                p.ProviderKind == endpoint.ProviderKind && p.Model == endpoint.Id && p.Capability == InferenceProviderCapability.AudioIsolation);
            price.UsdPerUnit.Should().ContainSingle();
            price.UsdPerUnit[UsageKind.AudioCleanupMinutes].Should().Be(endpoint.UsdPerMinute);
            price.Basis.Should().Contain("read 2026-11-02");
        }

        ModelPriceCatalog.All.Count(p => p.Capability == InferenceProviderCapability.AudioIsolation)
            .Should().Be(AudioIsolationModelCatalog.Instance.All.Count, "no price row may exist for an endpoint the client cannot call");
    }

    [Fact]
    public void The_price_book_prefills_the_card_and_an_unlisted_model_stays_unpriced()
    {
        PriceBook.SuggestFor(InferenceProviderKind.FalAi, InferenceProviderCapability.AudioIsolation, Model)
            .Should().ContainSingle(s => s.Unit == UsageKind.AudioCleanupMinutes && s.CostUsdPerUnit == 0.10m);
        PriceBook.SuggestFor(InferenceProviderKind.FalAi, InferenceProviderCapability.AudioIsolation, "fal-ai/demucs")
            .Should().BeEmpty("no cited price, so no row: unofferable, never a default");
    }

    [Fact]
    public void The_capability_table_and_admin_boundary_serve_isolation_through_fal_only()
    {
        ProviderCapabilityUsage.ServableByKind[InferenceProviderKind.FalAi].Should().Contain(InferenceProviderCapability.AudioIsolation);
        ProviderCapabilityUsage.ByCapability[InferenceProviderCapability.AudioIsolation].Should().Equal(UsageKind.AudioCleanupMinutes);
        ProviderCapabilityUsage.RequiredCards().Should().Contain((InferenceProviderKind.FalAi, UsageKind.AudioCleanupMinutes));

        InferenceProvidersController.IsUnsupportedCombination(InferenceProviderKind.FalAi, InferenceProviderCapability.AudioIsolation, out string e)
            .Should().BeFalse(e);
        foreach (InferenceProviderKind kind in Enum.GetValues<InferenceProviderKind>().Where(k => k != InferenceProviderKind.FalAi))
        {
            InferenceProvidersController.IsUnsupportedCombination(kind, InferenceProviderCapability.AudioIsolation, out _)
                .Should().BeTrue(kind + " must not serve AudioIsolation");
        }

        InferenceProvidersController.IsUnsupportedCombination(InferenceProviderKind.ElevenLabs, InferenceProviderCapability.AudioIsolation, out string error)
            .Should().BeTrue();
        error.Should().Contain("ElevenLabs").And.Contain("not offered");
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(Model, false)]
    [InlineData("fal-ai/demucs", true)]
    [InlineData("fal-ai/elevenlabs/music", true)]
    public void The_model_boundary_refuses_an_uncatalogued_isolation_model(string model, bool refused)
    {
        InferenceProvidersController.IsUnsupportedModel(InferenceProviderKind.FalAi, InferenceProviderCapability.AudioIsolation, model, out _)
            .Should().Be(refused);
    }

    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(1, 1)]
    [InlineData(60, 1)]
    [InlineData(60.001, 2)]
    [InlineData(95, 2)]
    [InlineData(1800, 30)]
    public void Billed_minutes_round_up_to_a_started_minute(double seconds, int minutes)
    {
        AudioCleanupCreditPricing.BilledMinutes(seconds).Should().Be(minutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.PositiveInfinity)]
    public void A_non_positive_duration_cannot_be_billed(double seconds)
    {
        Action act = () => AudioCleanupCreditPricing.BilledMinutes(seconds);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_estimate_is_price_times_started_minutes_and_the_cap_is_thirty_minutes_inclusive()
    {
        AudioIsolationEndpoint endpoint = AudioIsolationModelCatalog.Instance.Default;

        AudioCleanupCreditPricing.EstimateUsd(endpoint, 95).Should().Be(0.20m);
        AudioCleanupCreditPricing.EstimateUsd(endpoint, 1800).Should().Be(3.00m, "exactly 30 minutes is allowed");

        Action over = () => AudioCleanupCreditPricing.EstimateUsd(endpoint, 1800.5);
        over.Should().Throw<ArgumentOutOfRangeException>();
        Action tight = () => AudioCleanupCreditPricing.EstimateUsd(endpoint, 601, maxSourceMinutes: 10);
        tight.Should().Throw<ArgumentOutOfRangeException>("the cap is configurable");
        AudioCleanupCreditPricing.DefaultMaxSourceMinutes.Should().Be(30);
        AudioCleanupCreditPricing.MaxSourceMinutesKey.Should().Be("Cleanup:Hosted:MaxSourceMinutes");
    }

    [Fact]
    public void A_zero_price_estimate_fails_closed()
    {
        AudioIsolationEndpoint free = AudioIsolationModelCatalog.Instance.Default with { UsdPerMinute = 0m };
        Action act = () => AudioCleanupCreditPricing.EstimateUsd(free, 60);
        act.Should().Throw<InvalidOperationException>();
    }

    private static ResolvedAudioGenerationProvider Row(string model = Model) =>
        new(Guid.NewGuid(), "row", InferenceProviderKind.FalAi, InferenceProviderCapability.AudioIsolation, "", model, "test", 60,
            AccountId: Guid.NewGuid());

    private static (Mock<IInferenceProviderResolver>, Mock<IModelPriceService>, List<PriceRequest>) Wire(ResolvedAudioGenerationProvider? row)
    {
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveAudioIsolationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(row);
        List<PriceRequest> asked = [];
        Mock<IModelPriceService> prices = new();
        prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PriceRequest, CancellationToken>((r, _) => asked.Add(r))
            .ReturnsAsync((PriceRequest r, CancellationToken _) => new PriceQuote((long)(r.Quantity * 1000), 0m, Guid.NewGuid()));
        return (resolver, prices, asked);
    }

    [Fact]
    public async Task The_hold_prices_the_started_minutes_under_the_isolation_capability_and_unit()
    {
        ResolvedAudioGenerationProvider row = Row();
        var (resolver, prices, asked) = Wire(row);

        long? hold = await AudioCleanupCreditPricing.TryHoldMillisAsync(95, null, resolver.Object, prices.Object);

        PriceRequest request = asked.Should().ContainSingle().Which;
        request.Unit.Should().Be(UsageKind.AudioCleanupMinutes);
        request.Capability.Should().Be(InferenceProviderCapability.AudioIsolation);
        request.ModelCode.Should().Be(Model);
        request.Quantity.Should().Be(2m);
        request.ModelCapabilityId.Should().Be(row.ProviderId);
        hold.Should().Be(2_000);
    }

    [Fact]
    public async Task Over_the_cap_no_provider_an_unknown_model_or_an_unpriced_row_all_answer_null()
    {
        var (resolver, prices, asked) = Wire(Row());
        (await AudioCleanupCreditPricing.TryHoldMillisAsync(1801, null, resolver.Object, prices.Object)).Should().BeNull();
        asked.Should().BeEmpty("the cap is checked before any price lookup");

        var (none, p2, _) = Wire(null);
        (await AudioCleanupCreditPricing.TryHoldMillisAsync(60, null, none.Object, p2.Object)).Should().BeNull();

        var (unknown, p3, _) = Wire(Row("fal-ai/demucs"));
        (await AudioCleanupCreditPricing.TryHoldMillisAsync(60, null, unknown.Object, p3.Object)).Should().BeNull();

        var (priced, _, _) = Wire(Row());
        Mock<IModelPriceService> unpriced = new();
        unpriced.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnpricedUsageException(UsageKind.AudioCleanupMinutes, InferenceProviderKind.FalAi, Model));
        (await AudioCleanupCreditPricing.TryHoldMillisAsync(60, null, priced.Object, unpriced.Object)).Should().BeNull("unpriced is unofferable, never free");
    }
}
