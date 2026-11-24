#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Metering;

/// <summary>
/// Prompt-length price tiers (docs/design/model-catalog.md, prompt-length tiers): a call whose prompt is longer than N
/// thousand tokens bills at the capability's <c>gt{N}k</c> row of the unit, not the short-context (variant-less) row.
/// </summary>
public class PromptTierPricingTests
{
    private static readonly DateTime Past = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Account = Guid.NewGuid();

    private static readonly UsageKind[] LlmUnits =
        [UsageKind.LlmInputTokens, UsageKind.LlmOutputTokens, UsageKind.LlmCachedInputTokens];

    private static PricedCapability Cap(string code = "claude-haiku-4-5") =>
        new(Guid.NewGuid(), Account, null, InferenceProviderKind.Anthropic, InferenceProviderCapability.Chat, code);

    private static ModelCapabilityPrice Row(PricedCapability cap, UsageKind unit, decimal costUsd, string? variant = null) => new()
    {
        Id = Guid.NewGuid(),
        ModelCapabilityId = cap.Id,
        Unit = unit,
        Variant = variant,
        CostUsdPerUnit = costUsd,
        EffectiveFrom = Past
    };

    private static ModelPriceSnapshot Snapshot(PricedCapability cap, IEnumerable<ModelCapabilityPrice> rows) =>
        new([cap], rows.ToList(), [], 1.2m, 0.006m);

    private static PriceRequest Request(PricedCapability cap, UsageKind unit, long? promptTokens, string? variant = null) =>
        new(cap.Id, null, cap.Kind, cap.Capability, cap.ModelCode, unit, variant, UsageBillingMode.Platform, 1000m, Now, promptTokens);

    private static Guid? PricedRow(ModelPriceSnapshot snapshot, PriceRequest request) =>
        ModelPriceService.Price(snapshot, request, Now).PriceId;

    [Theory]
    [InlineData(UsageKind.LlmInputTokens)]
    [InlineData(UsageKind.LlmOutputTokens)]
    [InlineData(UsageKind.LlmCachedInputTokens)]
    public void A_prompt_under_the_tier_prices_at_the_short_row_and_one_over_it_at_the_tier_row(UsageKind unit)
    {
        PricedCapability cap = Cap();
        ModelCapabilityPrice shortRow = Row(cap, unit, 0.000001m);
        ModelCapabilityPrice longRow = Row(cap, unit, 0.000002m, "gt200k");
        ModelPriceSnapshot snapshot = Snapshot(cap, [shortRow, longRow]);

        PricedRow(snapshot, Request(cap, unit, 150_000)).Should().Be(shortRow.Id);
        PricedRow(snapshot, Request(cap, unit, 250_000)).Should().Be(longRow.Id);
        PricedRow(snapshot, Request(cap, unit, 200_000)).Should().Be(shortRow.Id, "the tier is strictly longer than 200k");
        PricedRow(snapshot, Request(cap, unit, 200_001)).Should().Be(longRow.Id);
    }

    [Fact]
    public void Two_tiers_pick_the_largest_one_the_prompt_exceeds()
    {
        PricedCapability cap = Cap();
        ModelCapabilityPrice shortRow = Row(cap, UsageKind.LlmInputTokens, 0.000001m);
        ModelCapabilityPrice gt100 = Row(cap, UsageKind.LlmInputTokens, 0.000002m, "gt100k");
        ModelCapabilityPrice gt200 = Row(cap, UsageKind.LlmInputTokens, 0.000003m, "GT200K");
        ModelPriceSnapshot snapshot = Snapshot(cap, [shortRow, gt200, gt100]);

        PricedRow(snapshot, Request(cap, UsageKind.LlmInputTokens, 50_000)).Should().Be(shortRow.Id);
        PricedRow(snapshot, Request(cap, UsageKind.LlmInputTokens, 150_000)).Should().Be(gt100.Id);
        PricedRow(snapshot, Request(cap, UsageKind.LlmInputTokens, 250_000)).Should().Be(gt200.Id, "the match is case-insensitive");
    }

    [Fact]
    public void An_explicit_variant_is_never_overridden_by_the_prompt_length()
    {
        PricedCapability cap = Cap();
        ModelCapabilityPrice shortRow = Row(cap, UsageKind.LlmInputTokens, 0.000001m);
        ModelCapabilityPrice batch = Row(cap, UsageKind.LlmInputTokens, 0.0000005m, "batch");
        ModelCapabilityPrice longRow = Row(cap, UsageKind.LlmInputTokens, 0.000002m, "gt200k");
        ModelPriceSnapshot snapshot = Snapshot(cap, [shortRow, batch, longRow]);

        PricedRow(snapshot, Request(cap, UsageKind.LlmInputTokens, 250_000, variant: "batch")).Should().Be(batch.Id);
    }

    [Fact]
    public void Without_tier_rows_or_a_prompt_length_the_lookup_is_unchanged()
    {
        PricedCapability cap = Cap();
        ModelCapabilityPrice shortRow = Row(cap, UsageKind.LlmInputTokens, 0.000001m);
        ModelCapabilityPrice other = Row(cap, UsageKind.LlmInputTokens, 0.000009m, "priority");
        ModelCapabilityPrice longRow = Row(cap, UsageKind.LlmInputTokens, 0.000002m, "gt200k");

        ModelPriceSnapshot noTiers = Snapshot(cap, [shortRow, other]);
        PricedRow(noTiers, Request(cap, UsageKind.LlmInputTokens, 250_000)).Should().Be(shortRow.Id);

        ModelPriceSnapshot tiers = Snapshot(cap, [shortRow, longRow]);
        PricedRow(tiers, Request(cap, UsageKind.LlmInputTokens, null)).Should().Be(shortRow.Id);
        PricedRow(tiers, Request(cap, UsageKind.LlmInputTokens, 0)).Should().Be(shortRow.Id);
    }

    [Fact]
    public async Task The_recorder_forwards_the_prompt_length_into_the_price_request()
    {
        CapturingPrices prices = new();
        UsageRecorder recorder = new(prices, new DiscardSink(), NullLogger<UsageRecorder>.Instance);

        await recorder.RecordAsync(new UsageRecordRequest(
            UsageKind.LlmInputTokens, 10, Guid.NewGuid(), InferenceProviderKind.Anthropic, "m", UsageBillingMode.Platform,
            PromptTokens: 123_456));

        prices.Requests.Should().ContainSingle().Which.PromptTokens.Should().Be(123_456);
    }

    [Fact]
    public async Task A_long_chat_call_records_every_unit_at_its_tier_row_with_the_tier_variant()
    {
        PricedCapability cap = Cap();
        Dictionary<UsageKind, ModelCapabilityPrice> shortRows = LlmUnits.ToDictionary(u => u, u => Row(cap, u, 0.000001m));
        Dictionary<UsageKind, ModelCapabilityPrice> longRows = LlmUnits.ToDictionary(u => u, u => Row(cap, u, 0.000002m, "gt200k"));
        ModelPriceService prices = new(new FixedSource(Snapshot(cap, shortRows.Values.Concat(longRows.Values))));
        CapturingSink sink = new();
        UsageRecorder recorder = new(prices, sink, NullLogger<UsageRecorder>.Instance);
        ResolvedInferenceProvider provider = new(
            cap.Id, "anthropic", InferenceProviderKind.Anthropic, "http://x", cap.ModelCode, "", 30, AccountId: Account);

        // 250k prompt of which 60k were cache reads: the uncached remainder (190k) alone is under the tier, the call is not.
        IChatClient client = new MeteringChatClient(new UsageReportingChatClient(250_000, 1_000, cachedTokens: 60_000), provider, recorder);
        using (UsageScope.Begin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), UsageSource.Workflow))
            await client.GetResponseAsync("hi");

        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);

        sink.Events.Should().HaveCount(3);
        foreach (UsageKind unit in LlmUnits)
        {
            UsageEvent row = sink.Events.Single(e => e.Kind == unit);
            row.PriceId.Should().Be(longRows[unit].Id, $"{unit} of a 250k-token prompt bills at the gt200k row");
            row.Variant.Should().Be("gt200k");
        }
    }

    [Fact]
    public async Task A_short_chat_call_keeps_the_short_row_and_no_variant()
    {
        PricedCapability cap = Cap();
        ModelCapabilityPrice shortRow = Row(cap, UsageKind.LlmInputTokens, 0.000001m);
        ModelCapabilityPrice longRow = Row(cap, UsageKind.LlmInputTokens, 0.000002m, "gt200k");
        ModelPriceService prices = new(new FixedSource(Snapshot(cap, [shortRow, longRow])));
        CapturingSink sink = new();
        UsageRecorder recorder = new(prices, sink, NullLogger<UsageRecorder>.Instance);
        ResolvedInferenceProvider provider = new(
            cap.Id, "anthropic", InferenceProviderKind.Anthropic, "http://x", cap.ModelCode, "", 30, AccountId: Account);

        IChatClient client = new MeteringChatClient(new UsageReportingChatClient(150_000, 0), provider, recorder);
        using (UsageScope.Begin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), UsageSource.Workflow))
            await client.GetResponseAsync("hi");

        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);

        UsageEvent input = sink.Events.Single();
        input.PriceId.Should().Be(shortRow.Id);
        input.Variant.Should().BeNull();
    }

    [Fact]
    public async Task A_decision_call_passes_its_prompt_length_on_both_units()
    {
        CapturingPrices prices = new();
        UsageRecorder recorder = new(prices, new DiscardSink(), NullLogger<UsageRecorder>.Instance);
        ResolvedDecisionProvider provider = new(
            Guid.NewGuid(), "decide", InferenceProviderKind.OpenAICompatible, "http://x", "gpt-test", "", 30);
        MeteringDecisionClient client = new(new FixedDecisionClient(210_000, 3), provider, recorder);

        await client.DecideAsync(new DecisionState("x"), [], CancellationToken.None);

        prices.Requests.Should().HaveCount(2).And.OnlyContain(r => r.PromptTokens == 210_000);
    }

    private sealed class FixedSource(ModelPriceSnapshot snapshot) : IModelPriceSource
    {
        public Task<ModelPriceSnapshot> LoadAsync(CancellationToken ct) => Task.FromResult(snapshot);
    }

    private sealed class CapturingPrices : IModelPriceService
    {
        public List<PriceRequest> Requests { get; } = [];

        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default)
        {
            lock (Requests) Requests.Add(request);
            return ValueTask.FromResult(new PriceQuote(1, 0m, null));
        }

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(
            decimal costUsd, UsageKind unit, UsageBillingMode billingMode, DateTime? at = null, CancellationToken ct = default) =>
            ValueTask.FromResult(new PriceQuote(1, 0m, null));

        public void Invalidate()
        {
        }
    }

    private sealed class FixedDecisionClient(int inputTokens, int outputTokens) : IDecisionClient
    {
        public Task<DecisionResult> DecideAsync(DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken ct) =>
            Task.FromResult(new DecisionResult("gpt-test", new Dictionary<string, DecisionAnswer>(), inputTokens, outputTokens));
    }
}
