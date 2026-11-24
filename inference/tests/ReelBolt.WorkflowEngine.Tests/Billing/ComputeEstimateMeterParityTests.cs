#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Pricing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// QA-8: a cold-start profile's compute quantity is quoted at exactly what the meter charges for the same
/// seconds of a 1080p source, whatever the deployment's compute prices say. The meter side is the
/// production call the engine makes (<see cref="ComputeMetering.RecordSecondsAsync"/> through a real
/// <see cref="UsageRecorder"/>); the estimate side is <see cref="ProfilePriceTable.BuildAsync"/>, the one
/// pricer the API's estimate and the engine's hold share.
/// </summary>
public sealed class ComputeEstimateMeterParityTests
{
    private static readonly DateTime Effective = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Every compute quantity a cold profile carries, with the step it belongs to.</summary>
    public static TheoryData<string, StepType, AgentType?> ComputeProfiles() => new()
    {
        { "seed", StepType.VideoAnalyze, null },
        { "seed", StepType.VideoCompile, null },
        { "seed", StepType.TimelineRender, null },
        { "seed", StepType.ThumbnailGenerate, null },
        { "seed", StepType.Agent, AgentType.AuthorAgent },
        { "seed", StepType.Revise, AgentType.CompositionReviser },
        { "pre-D9", StepType.VideoAnalyze, null },
        { "pre-D9", StepType.VideoCompile, null },
        { "pre-D9", StepType.Agent, AgentType.AuthorAgent },
        { "pre-D9", StepType.Revise, AgentType.CompositionReviser }
    };

    [Theory]
    [MemberData(nameof(ComputeProfiles))]
    public async Task The_estimate_quotes_a_profiles_compute_seconds_at_what_the_meter_charges_for_a_1080p_source(
        string cards, StepType stepType, AgentType? agentType)
    {
        IModelPriceService rates = LegacyCardPrices.Of(cards == "seed" ? LegacyRateCardSeed.ComputeCards(Effective) : PreD9ComputeCards());
        ProfileQuantity[] compute = [.. StepUsageProfiles.For(stepType, agentType).Quantities
            .Where(q => q.Target == ProfilePriceTarget.Compute)];
        compute.Should().NotBeEmpty($"{stepType}/{agentType} carries a compute quantity");

        foreach (ProfileQuantity quantity in compute)
        {
            long metered = await MeterAsync(rates, quantity.Kind, quantity.Quantity, 1920, 1080);
            IProfileQuantityPricer pricer = await ProfilePriceTable.BuildAsync(
                [ProfilePriceKey.Of(quantity, AgentType.Custom, null)], null, rates, NullLogger.Instance, CancellationToken.None);

            ProfileQuote quoted = pricer.Price(quantity, AgentType.Custom, null);

            metered.Should().BePositive();
            quoted.Should().Be(new ProfileQuote(metered, OwnProvider: false),
                $"{quantity.Quantity} s of {quantity.Kind} on the {cards} cards must be quoted at the meter's own charge");
        }
    }

    /// <summary>
    /// The diagnosis behind QA-8, pinned: measurements.md's VideoCompile median (16.1 credits for a 96.7 s
    /// encode) is the meter charging a 720p output on the compute cards seeded before c6355fda2, which a
    /// deployment keeps because the seeder adds compute cards only when it has none. The current seed charges
    /// the same encode 0.14-1.23 credits by tier, so the gap is in the reference cards, not in the estimator.
    /// </summary>
    [Fact]
    public async Task The_staging_compile_median_is_a_720p_encode_on_the_pre_D9_compute_cards()
    {
        IModelPriceService preD9 = LegacyCardPrices.Of(PreD9ComputeCards());
        IModelPriceService seed = LegacyCardPrices.Of(LegacyRateCardSeed.ComputeCards(Effective));

        long staging = await MeterAsync(preD9, UsageKind.EncodeOutputSeconds, 96.7m, 1280, 720);
        long seedUhd = await MeterAsync(seed, UsageKind.EncodeOutputSeconds, 96.7m, 3840, 2160);

        Math.Round(staging / 1000m, 1).Should().Be(16.1m);
        seedUhd.Should().BeLessThan(staging / 10, "no tier of the current seed reaches the staging figure");
    }

    private static async Task<long> MeterAsync(IModelPriceService rates, UsageKind kind, decimal seconds, int width, int height)
    {
        UsageRecorder recorder = new(rates, new DiscardSink(), NullLogger<UsageRecorder>.Instance);
        using IDisposable scope = UsageScope.Begin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), UsageSource.Workflow);
        return await ComputeMetering.RecordSecondsAsync(recorder, kind, (double)seconds, width, height, null);
    }

    /// <summary>The compute cards as seeded before c6355fda2 (USD per media second, by tier: render, encode, analyse).</summary>
    private static IReadOnlyList<RateCard> PreD9ComputeCards()
    {
        (string Tier, decimal Render, decimal Encode, decimal Analyze)[] tiers =
        [
            (ComputeMetering.Tier720, 0.0020m, 0.0010m, 0.0005m),
            (ComputeMetering.Tier1080, 0.0040m, 0.0020m, 0.0010m),
            (ComputeMetering.TierUhd, 0.0160m, 0.0080m, 0.0040m)
        ];

        return [.. tiers.SelectMany(t => new[]
        {
            Card(t.Tier, UsageKind.RenderOutputSeconds, t.Render),
            Card(t.Tier, UsageKind.EncodeOutputSeconds, t.Encode),
            Card(t.Tier, UsageKind.AnalyzeMediaSeconds, t.Analyze)
        })];
    }

    private static RateCard Card(string tier, UsageKind kind, decimal usd) => new()
    {
        Id = Guid.NewGuid(),
        ProviderKind = LegacyRateCardSeed.ComputeProviderKind,
        ModelPattern = tier,
        UsageKind = kind,
        CostUsdPerUnit = usd,
        CreditsMillisPerUnit = usd * LegacyRateCardSeed.CreditsMillisPerUsd,
        EffectiveFrom = Effective
    };

    private sealed class DiscardSink : IUsageEventSink
    {
        public Task WriteAsync(IReadOnlyList<UsageEvent> events, CancellationToken ct) => Task.CompletedTask;
    }
}
