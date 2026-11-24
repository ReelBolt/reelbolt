#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// The cold-start seed fallback is priced like a real quote: the seed's USD × the markup (pricing_settings.markup,
/// default 1.2) ÷ the credit value. Without the markup, an estimate or hold built on the fallback sat ~17% below what
/// the settle of the same usage charges.
/// </summary>
public sealed class SeedFallbackMarkupTests
{
    // The seed's LLM input figure: $3 per million blended, split 1:4 input:output => $1.20 per million input tokens.
    private const decimal MillionInputTokens = 1_000_000m;

    [Fact]
    public void The_seed_fallback_applies_the_default_markup()
    {
        // $1.20 × 1000 ÷ 0.006 = 200 000 millicredits at cost; × 1.2 markup = 240 000.
        StepUsageProfiles.SeedFallbackMillis(UsageKind.LlmInputTokens, MillionInputTokens).Should().Be(240_000);
        StepUsageProfiles.SeedFallbackMillis(UsageKind.LlmInputTokens, MillionInputTokens, markup: 1.5m).Should().Be(300_000);
    }

    [Fact]
    public void The_seed_pricer_charges_what_the_meter_would_at_the_default_markup()
    {
        ProfileQuote quote = SeedProfilePricer.Instance.Price(
            new ProfileQuantity(UsageKind.LlmInputTokens, ProfilePriceTarget.Chat, MillionInputTokens), AgentType.Custom, null);

        quote.CreditsMillis.Should().Be(240_000);
    }

    [Fact]
    public async Task A_live_price_table_falls_back_at_the_current_markup()
    {
        // No resolver: the chat quantity cannot be priced from a row, so the table answers with the seed fallback, at
        // the markup the price service is configured with now (1.5 here), not the compiled-in default.
        ModelPriceService prices = new(new MarkupSource(1.5m));
        ProfileQuantity quantity = new(UsageKind.LlmInputTokens, ProfilePriceTarget.Chat, MillionInputTokens);

        IProfileQuantityPricer pricer = await ProfilePriceTable.BuildAsync(
            [ProfilePriceKey.Of(quantity, AgentType.Custom, null)], providers: null, prices, NullLogger.Instance, CancellationToken.None);

        pricer.Price(quantity, AgentType.Custom, null).CreditsMillis.Should().Be(300_000);
    }

    [Fact]
    public void The_cold_start_video_generate_row_applies_the_default_markup()
    {
        // 12s (p50) and 60s (p90) at the seed's $0.08/s: $0.96 and $4.80 at cost => 160 000 and 800 000 millicredits;
        // x 1.2 markup = 192 000 and 960 000.
        StepCreditDefaults.MillisFor(StepType.VideoGenerate).Should().Be((192_000L, 960_000L));
    }

    [Theory]
    [InlineData(StepType.Agent, 5_860L, 446_485L)]          // 9 766 / 744 142 tokens at 0.5 millis, x 1.2
    [InlineData(StepType.VideoCompile, 24_000L, 36_000L)]  // 60 s / 90 s of 1080p encode at the seed card, x 1.2
    [InlineData(StepType.Voiceover, 2_400L, 6_000L)]
    [InlineData(StepType.ThumbnailGenerate, 1_200L, 2_400L)]
    [InlineData(StepType.VideoAnalyze, 14_700L, 26_100L)]  // (8 250 + 4 000, 15 750 + 6 000) x 1.2
    public void The_cold_start_seed_rows_are_priced_at_the_default_markup_not_at_cost(StepType stepType, long low, long high)
    {
        StepCreditDefaults.MillisFor(stepType).Should().Be((low, high));
    }

    [Fact]
    public void Free_step_types_stay_free()
    {
        StepCreditDefaults.MillisFor(StepType.Conditional).Should().Be((0L, 0L));
        StepCreditDefaults.MillisFor(StepType.Extract).Should().Be((0L, 0L));
    }

    /// <summary>
    /// T-032 (low c): the cold-start default hold (StepCreditDefaults' row, here a VideoGenerate step whose price could
    /// not be declared) follows the live pricing_settings markup the run's price table was built at, not the constant 1.2.
    /// </summary>
    [Fact]
    public async Task The_cold_start_default_hold_follows_the_live_markup()
    {
        ModelPriceService prices = new(new MarkupSource(1.5m));
        IProfileQuantityPricer pricer = await ProfilePriceTable.BuildAsync(
            [], providers: null, prices, NullLogger.Instance, CancellationToken.None);
        WorkflowStep step = new() { Id = Guid.NewGuid(), StepOrder = 0, StepType = StepType.VideoGenerate };

        RunHoldPlan plan = RunCreditHolds.Plan([step], new StepCreditEstimator([]), null, new System.Collections.Generic.Dictionary<Guid, AgentType>(), pricer);

        // $0.96 at cost = 160 000 millicredits; x 1.5 (live) = 240 000, not x 1.2 = 192 000.
        plan.HoldsMillis[0].Should().Be(240_000);
        pricer.Markup.Should().Be(1.5m);
        StepCreditDefaults.MillisFor(StepType.VideoGenerate, 1.5m).Should().Be((240_000L, 1_200_000L));
    }

    private sealed class MarkupSource(decimal markup) : IModelPriceSource
    {
        public Task<ModelPriceSnapshot> LoadAsync(CancellationToken ct) => Task.FromResult(new ModelPriceSnapshot([], [], [], markup));
    }
}
