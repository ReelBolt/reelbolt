#nullable enable
using System;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// The audio catalogue and its prices (research/audio-vendors.md A1-A3, B): one default per kind and
/// audio job, a price for every endpoint, the price catalogue and the client catalogue unable to drift,
/// and the fail-closed estimate the budget gate prices a take with.
/// </summary>
public class AudioGenerationCatalogPricingTests
{
    private static AudioModelCatalog Catalog => AudioModelCatalog.Instance;

    [Theory]
    [InlineData(InferenceProviderKind.FalAi, AudioGenerationKind.Music, "fal-ai/elevenlabs/music")]
    [InlineData(InferenceProviderKind.FalAi, AudioGenerationKind.SoundEffect, "fal-ai/elevenlabs/sound-effects/v2")]
    [InlineData(InferenceProviderKind.ElevenLabs, AudioGenerationKind.Music, "music_v2_5")]
    [InlineData(InferenceProviderKind.ElevenLabs, AudioGenerationKind.SoundEffect, "eleven_text_to_sound_v2")]
    public void Each_kind_has_one_default_per_audio_job(InferenceProviderKind kind, AudioGenerationKind job, string expected)
    {
        Catalog.DefaultFor(kind, job).Id.Should().Be(expected);
        Catalog.All.Count(e => e.ProviderKind == kind && e.Kind == job && e.IsDefault).Should().Be(1);
    }

    [Fact]
    public void MMAudio_is_a_fal_sound_effect_alternative_and_never_a_default()
    {
        AudioEndpoint mmaudio = Catalog.Find(InferenceProviderKind.FalAi, "fal-ai/mmaudio-v2/text-to-audio")!;
        mmaudio.Kind.Should().Be(AudioGenerationKind.SoundEffect);
        mmaudio.IsDefault.Should().BeFalse();
    }

    [Fact]
    public void Every_catalogued_endpoint_has_the_same_price_in_the_price_catalogue()
    {
        foreach (AudioEndpoint endpoint in Catalog.All)
        {
            ModelPrice price = ModelPriceCatalog.Priced.Single(p =>
                p.ProviderKind == endpoint.ProviderKind && p.Model == endpoint.Id
                && p.Capability == AudioModelCatalog.CapabilityOf(endpoint.Kind));
            price.UsdPerUnit.Should().ContainSingle();
            price.UsdPerUnit[endpoint.Unit].Should().Be(endpoint.UsdPerUnit, endpoint.Id);
            ProviderCapabilityUsage.ByCapability[price.Capability].Should().Contain(endpoint.Unit);
        }

        ModelPriceCatalog.All
            .Where(p => p.Capability is InferenceProviderCapability.MusicGeneration or InferenceProviderCapability.SoundEffectGeneration)
            .Should().HaveCount(Catalog.All.Count, "no audio price row may exist for an endpoint the client cannot call");
    }

    [Theory]
    [InlineData("fal-ai/elevenlabs/music", InferenceProviderCapability.MusicGeneration, UsageKind.MusicGenMinutes, "0.60")]
    [InlineData("fal-ai/elevenlabs/sound-effects/v2", InferenceProviderCapability.SoundEffectGeneration, UsageKind.SoundEffectGenSeconds, "0.002")]
    [InlineData("fal-ai/mmaudio-v2/text-to-audio", InferenceProviderCapability.SoundEffectGeneration, UsageKind.SoundEffectGenSeconds, "0.001")]
    public void The_price_book_prefills_the_fal_audio_cards(string model, InferenceProviderCapability capability, UsageKind unit, string usd)
    {
        PriceBook.SuggestFor(InferenceProviderKind.FalAi, capability, model)
            .Should().ContainSingle(s => s.Unit == unit && s.Variant == null && s.CostUsdPerUnit == decimal.Parse(usd, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(30, 1, "0.60")]
    [InlineData(60, 1, "0.60")]
    [InlineData(61, 2, "1.20")]
    [InlineData(300, 5, "3.00")]
    public void Fal_music_bills_each_started_minute(double seconds, int units, string usd)
    {
        AudioGenerationCapabilities caps = new FalAiAudioGenerationClient(new System.Net.Http.HttpClient(), "k")
            .Describe("fal-ai/elevenlabs/music");

        caps.Unit.Should().Be(UsageKind.MusicGenMinutes);
        caps.BilledUnits(seconds).Should().Be(units);
        caps.EstimateUsd(seconds).Should().Be(decimal.Parse(usd, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// QA-P1-9: the billed quantity is the exact started unit of the duration the probe reported, with no
    /// pre-rounding. Rounding to 3 dp first (AwayFromZero) turned a 60.029 s measurement of a 60 s track into
    /// 60.000 and billed one minute for two, and a value a hair past the boundary back into the unit below it.
    /// The boundary is the boundary: 60.000 s is one minute, and any played time past it starts the second.
    /// </summary>
    [Theory]
    [InlineData(59.9999, 1)]
    [InlineData(60.0, 1)]
    [InlineData(60.0001, 2)]
    [InlineData(60.001, 2)]
    [InlineData(60.029, 2)]
    [InlineData(120.0, 2)]
    [InlineData(120.001, 3)]
    public void Music_bills_the_started_minute_of_the_exact_probed_duration(double probedSeconds, int minutes)
    {
        AudioGenerationCapabilities caps = new FalAiAudioGenerationClient(new System.Net.Http.HttpClient(), "k")
            .Describe("fal-ai/elevenlabs/music");

        caps.BilledUnits(probedSeconds).Should().Be(minutes, "the probe's own precision is the billed precision");
        caps.EstimateUsd(probedSeconds).Should().Be(minutes * caps.UsdPerUnit);
    }

    [Theory]
    [InlineData(2.0, 2)]
    [InlineData(2.0375, 3)]
    [InlineData(3.0, 3)]
    [InlineData(3.0001, 4)]
    public void Sound_effects_bill_the_started_second_of_the_exact_probed_duration(double probedSeconds, int seconds)
    {
        AudioGenerationCapabilities caps = new FalAiAudioGenerationClient(new System.Net.Http.HttpClient(), "k")
            .Describe("fal-ai/elevenlabs/sound-effects/v2");

        caps.BilledUnits(probedSeconds).Should().Be(seconds);
    }

    [Fact]
    public void Sound_effects_bill_each_started_second()
    {
        AudioGenerationCapabilities v2 = new FalAiAudioGenerationClient(new System.Net.Http.HttpClient(), "k")
            .Describe("fal-ai/elevenlabs/sound-effects/v2");
        v2.BilledUnits(1.5).Should().Be(2);
        v2.EstimateUsd(1.5).Should().Be(0.004m);

        AudioGenerationCapabilities mmaudio = new FalAiAudioGenerationClient(new System.Net.Http.HttpClient(), "k")
            .Describe("fal-ai/mmaudio-v2/text-to-audio");
        mmaudio.EstimateUsd(8).Should().Be(0.008m);
    }

    [Fact]
    public void An_unset_duration_is_estimated_at_the_endpoint_maximum()
    {
        AudioGenerationCapabilities v2 = new FalAiAudioGenerationClient(new System.Net.Http.HttpClient(), "k")
            .Describe("fal-ai/elevenlabs/sound-effects/v2");
        v2.MaxDurationSec.Should().Be(22);
        v2.EstimateUsd(null).Should().Be(22 * 0.002m);
    }

    [Fact]
    public void Our_music_cap_is_five_minutes_although_the_schema_allows_ten()
    {
        AudioGenerationCapabilities caps = new FalAiAudioGenerationClient(new System.Net.Http.HttpClient(), "k")
            .Describe("fal-ai/elevenlabs/music");
        caps.MinDurationSec.Should().Be(3);
        caps.MaxDurationSec.Should().Be(300);
        FluentActions.Invoking(() => caps.EstimateUsd(301)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("music_v2_5", 30, "0.15")]
    [InlineData("music_v2", 90, "0.30")]
    [InlineData("eleven_text_to_sound_v2", 1.5, "0.004")]
    public void ElevenLabs_direct_prices_are_marked_must_measure(string model, double seconds, string usd)
    {
        AudioGenerationCapabilities caps = new ElevenLabsAudioGenerationClient(new System.Net.Http.HttpClient(), "k")
            .Describe(model);
        caps.EstimateUsd(seconds).Should().Be(decimal.Parse(usd, System.Globalization.CultureInfo.InvariantCulture));
        caps.PriceMustBeMeasured.Should().BeTrue();
        caps.PriceBasis.Should().Contain("per generation");
    }

    [Theory]
    [InlineData("")]
    [InlineData("fal-ai/stable-audio-25/text-to-audio")]
    [InlineData("music_v2_5")]
    public void Describe_refuses_a_model_it_cannot_price(string model)
    {
        FluentActions.Invoking(() => new FalAiAudioGenerationClient(new System.Net.Http.HttpClient(), "k").Describe(model))
            .Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void A_non_positive_estimate_fails_closed()
    {
        AudioGenerationCapabilities free = new("m", AudioGenerationKind.SoundEffect, UsageKind.SoundEffectGenSeconds, 0m,
            AudioBillingRounding.UpToWholeSecond, 0.5, 22, "none", PriceMustBeMeasured: false);
        FluentActions.Invoking(() => free.EstimateUsd(5)).Should().Throw<InvalidOperationException>();
    }
}
