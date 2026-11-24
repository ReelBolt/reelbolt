#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>T-108: an AudioGenerate step's credit hold is priced from its own settings, through the settlement's price row.</summary>
public sealed class AudioGenerateCreditPricingTests
{
    private static ResolvedAudioGenerationProvider Row(InferenceProviderKind kind, InferenceProviderCapability capability, string model) =>
        new(Guid.NewGuid(), "row", kind, capability, "", model, "test", 60, AccountId: Guid.NewGuid());

    private static (Mock<IInferenceProviderResolver> Resolver, Mock<IModelPriceService> Prices, List<PriceRequest> Asked) Wire(
        ResolvedAudioGenerationProvider? music, ResolvedAudioGenerationProvider? sfx)
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveMusicGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(music);
        resolver.Setup(r => r.ResolveSoundEffectGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(sfx);
        List<PriceRequest> asked = [];
        var prices = new Mock<IModelPriceService>();
        prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PriceRequest, CancellationToken>((r, _) => asked.Add(r))
            .ReturnsAsync((PriceRequest r, CancellationToken _) => new PriceQuote((long)(r.Quantity * 1000), 0m, Guid.NewGuid()));
        return (resolver, prices, asked);
    }

    [Fact]
    public async Task Music_holds_takes_times_the_started_minutes_at_the_music_price_row()
    {
        ResolvedAudioGenerationProvider row = Row(InferenceProviderKind.FalAi, InferenceProviderCapability.MusicGeneration, "fal-ai/elevenlabs/music");
        var (resolver, prices, asked) = Wire(row, null);

        long? hold = await AudioGenerateCreditPricing.TryHoldMillisAsync(
            """{"kind":"Music","prompt":"x","durationSec":90,"takes":2}""", resolver.Object, prices.Object);

        PriceRequest request = asked.Should().ContainSingle().Which;
        request.Unit.Should().Be(UsageKind.MusicGenMinutes);
        request.Capability.Should().Be(InferenceProviderCapability.MusicGeneration);
        request.ModelCode.Should().Be("fal-ai/elevenlabs/music");
        request.Quantity.Should().Be(4m, "90 s is two started minutes, times two takes");
        request.ModelCapabilityId.Should().Be(row.ProviderId);
        request.BillingMode.Should().Be(UsageBillingMode.Platform);
        hold.Should().Be(4_000);
    }

    [Fact]
    public async Task A_sound_effect_holds_takes_times_its_started_seconds()
    {
        var (resolver, prices, asked) = Wire(null,
            Row(InferenceProviderKind.ElevenLabs, InferenceProviderCapability.SoundEffectGeneration, "eleven_text_to_sound_v2"));

        await AudioGenerateCreditPricing.TryHoldMillisAsync(
            """{"kind":"SoundEffect","prompt":"whoosh","durationSec":1.5,"takes":3}""", resolver.Object, prices.Object);

        asked.Should().ContainSingle().Which.Should().Match<PriceRequest>(r =>
            r.Unit == UsageKind.SoundEffectGenSeconds && r.Quantity == 6m && r.ModelCode == "eleven_text_to_sound_v2");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"kind":"Music","prompt":"x","durationSec":30,"model":"fal-ai/elevenlabs/sound-effects/v2"}""")]
    public async Task An_unpriceable_step_answers_null_so_the_caller_falls_back(string? json)
    {
        var (resolver, prices, _) = Wire(
            Row(InferenceProviderKind.FalAi, InferenceProviderCapability.MusicGeneration, "fal-ai/elevenlabs/music"), null);

        (await AudioGenerateCreditPricing.TryHoldMillisAsync(json, resolver.Object, prices.Object)).Should().BeNull();
    }

    [Fact]
    public async Task No_provider_row_answers_null()
    {
        var (resolver, prices, _) = Wire(null, null);

        (await AudioGenerateCreditPricing.TryHoldMillisAsync(
            """{"kind":"Music","prompt":"x","durationSec":30}""", resolver.Object, prices.Object)).Should().BeNull();
    }

    [Fact]
    public async Task A_missing_price_row_answers_null_never_zero()
    {
        var (resolver, prices, _) = Wire(
            Row(InferenceProviderKind.FalAi, InferenceProviderCapability.MusicGeneration, "fal-ai/elevenlabs/music"), null);
        prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no price row"));

        (await AudioGenerateCreditPricing.TryHoldMillisAsync(
            """{"kind":"Music","prompt":"x","durationSec":30}""", resolver.Object, prices.Object)).Should().BeNull();
    }
}
