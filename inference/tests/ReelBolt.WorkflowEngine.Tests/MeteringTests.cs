#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class MeteringTests
{
    private static readonly Guid OrgId = Guid.NewGuid();

    private static ResolvedInferenceProvider Platform(string model = "gpt-test") => new(
        Guid.NewGuid(), "platform", InferenceProviderKind.OpenAICompatible, "http://x", model, "", 30);

    private static ResolvedInferenceProvider Byo(string model = "my-model") => new(
        Guid.NewGuid(), "byo", InferenceProviderKind.Anthropic, "http://x", model, "", 30, OrganizationId: OrgId, IsPlatformManaged: false);

    private static (UsageRecorder Recorder, CapturingSink Sink) NewRecorder(UsageRecorderOptions? options = null) =>
        NewRecorder(new StaticPriceService(), options);

    private static (UsageRecorder Recorder, CapturingSink Sink) NewRecorder(IModelPriceService prices, UsageRecorderOptions? options = null)
    {
        CapturingSink sink = new();
        UsageRecorder recorder = new(prices, sink, NullLogger<UsageRecorder>.Instance, options);
        return (recorder, sink);
    }

    private static async Task<IReadOnlyList<UsageEvent>> Flush(UsageRecorder recorder, CapturingSink sink)
    {
        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);
        return sink.Events;
    }

    private static IDisposable Scope(UsageSource source = UsageSource.Workflow, Guid? stepResult = null) =>
        UsageScope.Begin(OrgId, Guid.NewGuid(), Guid.NewGuid(), stepResult ?? Guid.NewGuid(), Guid.NewGuid(), source);

    [Fact]
    public async Task A_chat_call_records_input_and_output_rows_with_kind_model_billing_mode_and_price()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();
        ResolvedInferenceProvider provider = Platform("gpt-test");
        IChatClient client = new MeteringChatClient(new UsageReportingChatClient(1000, 200), provider, recorder);

        using (Scope())
            await client.GetResponseAsync("hi");

        IReadOnlyList<UsageEvent> rows = await Flush(recorder, sink);
        rows.Should().HaveCount(2);
        UsageEvent input = rows.Single(r => r.Kind == UsageKind.LlmInputTokens);
        input.Quantity.Should().Be(1000);
        input.ModelName.Should().Be("gpt-test");
        input.ProviderId.Should().Be(provider.ProviderId);
        input.ProviderKind.Should().Be(InferenceProviderKind.OpenAICompatible);
        input.BillingMode.Should().Be(UsageBillingMode.Platform);
        input.CreditsMillis.Should().Be(2000);
        input.CostUsd.Should().Be(2m);
        input.PriceId.Should().Be(StaticPriceService.PriceIdFor(UsageKind.LlmInputTokens));
        input.RateCardId.Should().BeNull("rate cards are history; nothing writes them any more");
        input.OrganizationId.Should().Be(OrgId);
        input.Source.Should().Be(UsageSource.Workflow);
        rows.Single(r => r.Kind == UsageKind.LlmOutputTokens).CreditsMillis.Should().Be(1600);
    }

    [Fact]
    public async Task Cached_input_tokens_are_split_out_of_input_at_their_own_price()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();
        IChatClient client = new MeteringChatClient(new UsageReportingChatClient(1000, 0, cachedTokens: 400), Platform(), recorder);

        using (Scope())
            await client.GetResponseAsync("hi");

        IReadOnlyList<UsageEvent> rows = await Flush(recorder, sink);
        rows.Single(r => r.Kind == UsageKind.LlmInputTokens).Quantity.Should().Be(600);
        rows.Single(r => r.Kind == UsageKind.LlmCachedInputTokens).Quantity.Should().Be(400);
        rows.Should().NotContain(r => r.Kind == UsageKind.LlmOutputTokens, "zero output records nothing");
    }

    [Fact]
    public async Task A_byo_provider_is_recorded_for_visibility_at_zero_credits_and_zero_cost()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();
        IChatClient client = new MeteringChatClient(new UsageReportingChatClient(500, 100), Byo(), recorder);

        using (Scope())
            await client.GetResponseAsync("hi");

        IReadOnlyList<UsageEvent> rows = await Flush(recorder, sink);
        rows.Should().HaveCount(2).And.OnlyContain(r => r.BillingMode == UsageBillingMode.Byo);
        rows.Should().OnlyContain(r => r.CreditsMillis == 0 && r.CostUsd == 0m);
        rows.Sum(r => r.Quantity).Should().Be(600, "usage dashboards stay complete");
    }

    [Fact]
    public async Task Streaming_usage_from_the_final_update_is_recorded_once()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();
        UsageReportingChatClient inner = new(300, 50);
        IChatClient client = new MeteringChatClient(inner, Platform(), recorder);

        using (Scope())
        {
            await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync("hi")) { }
        }

        IReadOnlyList<UsageEvent> rows = await Flush(recorder, sink);
        rows.Should().HaveCount(2);
        rows.Single(r => r.Kind == UsageKind.LlmInputTokens).Quantity.Should().Be(300);
        rows.Single(r => r.Kind == UsageKind.LlmOutputTokens).Quantity.Should().Be(50);
    }

    [Fact]
    public async Task A_call_outside_any_scope_is_attributed_to_the_platform_org_as_unattributed()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();
        IChatClient client = new MeteringChatClient(new UsageReportingChatClient(10, 10), Platform(), recorder);

        await client.GetResponseAsync("hi");

        IReadOnlyList<UsageEvent> rows = await Flush(recorder, sink);
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.OrganizationId == WellKnownOrganizations.PlatformId && r.Source == UsageSource.Unattributed);
        rows.Select(r => r.IdempotencyKey).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task UsageRecorder_NoScope_ThrowsWhenFailOnUnattributed()
    {
        CapturingSink sink = new();
        UsageRecorder recorder = new(
            new StaticPriceService(), sink, NullLogger<UsageRecorder>.Instance,
            metering: Microsoft.Extensions.Options.Options.Create(new MeteringOptions { FailOnUnattributed = true }));
        UsageRecordRequest request = new(
            UsageKind.LlmInputTokens, 10, Guid.NewGuid(), InferenceProviderKind.OpenAICompatible, "gpt-test", UsageBillingMode.Platform);

        Func<Task> direct = async () => await recorder.RecordAsync(request);
        await direct.Should().ThrowAsync<UnattributedUsageException>();

        // Loud through the metering wrapper too: the chat client does not swallow it into a log line.
        IChatClient client = new MeteringChatClient(new UsageReportingChatClient(10, 10), Platform(), recorder);
        Func<Task> metered = () => client.GetResponseAsync("hi");
        await metered.Should().ThrowAsync<UnattributedUsageException>();

        (await Flush(recorder, sink)).Should().BeEmpty();
    }

    [Fact]
    public async Task UsageRecorder_NoScope_WritesUnattributedByDefault()
    {
        CapturingSink sink = new();
        UsageRecorder recorder = new(
            new StaticPriceService(), sink, NullLogger<UsageRecorder>.Instance,
            metering: Microsoft.Extensions.Options.Options.Create(new MeteringOptions()));
        UsageRecordRequest request = new(
            UsageKind.LlmInputTokens, 10, Guid.NewGuid(), InferenceProviderKind.OpenAICompatible, "gpt-test", UsageBillingMode.Platform);

        long credits = await recorder.RecordAsync(request);

        credits.Should().Be(20);
        UsageEvent row = (await Flush(recorder, sink)).Should().ContainSingle().Subject;
        row.Source.Should().Be(UsageSource.Unattributed);
        row.OrganizationId.Should().Be(WellKnownOrganizations.PlatformId);
        row.IdempotencyKey.Should().StartWith("unattributed:").And.EndWith(":in");
    }

    [Fact]
    public void Metering_options_bind_FailOnUnattributed_from_the_Metering_section_and_default_off()
    {
        Microsoft.Extensions.Configuration.IConfiguration on = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Metering:FailOnUnattributed"] = "true" })
            .Build();
        ServiceCollection services = new();
        services.AddReelBoltMeteringOptions(on);
        services.BuildServiceProvider().GetRequiredService<Microsoft.Extensions.Options.IOptions<MeteringOptions>>()
            .Value.FailOnUnattributed.Should().BeTrue();

        ServiceCollection empty = new();
        empty.AddReelBoltMeteringOptions(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        empty.BuildServiceProvider().GetRequiredService<Microsoft.Extensions.Options.IOptions<MeteringOptions>>()
            .Value.FailOnUnattributed.Should().BeFalse();
        new MeteringOptions().FailOnUnattributed.Should().BeFalse();
        MeteringOptions.SectionName.Should().Be("Metering");
    }

    [Fact]
    public async Task Assistant_scope_rows_carry_source_assistant_and_the_caller()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();
        IChatClient client = new MeteringChatClient(new UsageReportingChatClient(10, 10), Platform(), recorder);
        Guid user = Guid.NewGuid();

        using (UsageScope.Begin(OrgId, null, null, null, user, UsageSource.Assistant))
            await client.GetResponseAsync("hi");

        IReadOnlyList<UsageEvent> rows = await Flush(recorder, sink);
        rows.Should().OnlyContain(r => r.Source == UsageSource.Assistant && r.UserId == user && r.OrganizationId == OrgId);
        rows.Should().OnlyContain(r => r.StepResultId == null && r.ExecutionId == null);
    }

    [Fact]
    public async Task Idempotency_key_is_step_result_call_sequence_and_kind_and_totals_accumulate_on_the_scope()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();
        IChatClient client = new MeteringChatClient(new UsageReportingChatClient(1000, 200), Platform(), recorder);
        Guid stepResult = Guid.NewGuid();
        long running;
        long tokens;

        using (Scope(stepResult: stepResult))
        {
            await client.GetResponseAsync("one");
            await client.GetResponseAsync("two");
            running = UsageScope.RunningCreditsMillis;
            tokens = UsageScope.Current!.TotalTokens;
        }

        IReadOnlyList<UsageEvent> rows = await Flush(recorder, sink);
        rows.Select(r => r.IdempotencyKey).Should().BeEquivalentTo(
        [
            $"{stepResult:N}:1:in", $"{stepResult:N}:1:out", $"{stepResult:N}:2:in", $"{stepResult:N}:2:out"
        ]);
        running.Should().Be(2 * (2000 + 1600));
        tokens.Should().Be(2 * 1200);
        UsageScope.RunningCreditsMillis.Should().Be(0, "outside the scope there is nothing running");
    }

    [Fact]
    public async Task A_platform_unit_with_no_price_is_still_recorded_unpriced_and_never_throws_into_the_call()
    {
        CapturingSink sink = new();
        UsageRecorder recorder = new(
            new EmptyPriceService(), sink, NullLogger<UsageRecorder>.Instance);
        IChatClient client = new MeteringChatClient(new UsageReportingChatClient(10, 10), Platform("gpt-unpriced"), recorder);
        using UnpricedUsageListener meter = new();
        UsageScopeState scope;

        using (Scope())
        {
            scope = UsageScope.Current!;
            await client.GetResponseAsync("hi");
        }

        IReadOnlyList<UsageEvent> rows = await Flush(recorder, sink);
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.CreditsMillis == 0 && r.PriceId == null && r.Unpriced,
            "the row keeps quantity and model and is flagged, never silently free");
        rows.Should().OnlyContain(r => r.ModelName == "gpt-unpriced" && r.Quantity == 10m);
        meter.CountFor(scope).Should().Be(2, "each unpriced platform unit is counted on reelbolt.billing.unpriced_usage");
        meter.TagsFor(scope).Should().OnlyContain(t => t.Source == "Workflow" && t.Capability == "Chat");
        scope.SawUnpricedPlatformUsage.Should().BeTrue("the settle of this unit of work must charge at least its hold");
    }

    [Fact]
    public async Task A_byo_unit_with_no_price_stays_free_and_unflagged()
    {
        CapturingSink sink = new();
        UsageRecorder recorder = new(new EmptyPriceService(), sink, NullLogger<UsageRecorder>.Instance);
        IChatClient client = new MeteringChatClient(new UsageReportingChatClient(10, 10), Byo(), recorder);
        using UnpricedUsageListener meter = new();
        UsageScopeState scope;

        using (Scope())
        {
            scope = UsageScope.Current!;
            await client.GetResponseAsync("hi");
        }

        IReadOnlyList<UsageEvent> rows = await Flush(recorder, sink);
        rows.Should().HaveCount(2).And.OnlyContain(r => r.CreditsMillis == 0 && !r.Unpriced);
        meter.CountFor(scope).Should().Be(0);
        scope.SawUnpricedPlatformUsage.Should().BeFalse();
    }

    [Fact]
    public async Task A_platform_unit_whose_pricing_throws_is_flagged_unpriced_too()
    {
        CapturingSink sink = new();
        UsageRecorder recorder = new(new ThrowingPriceService(), sink, NullLogger<UsageRecorder>.Instance);
        UsageScopeState scope;

        using (Scope())
        {
            scope = UsageScope.Current!;
            await recorder.RecordAsync(new UsageRecordRequest(
                UsageKind.LlmInputTokens, 5, null, InferenceProviderKind.OpenAICompatible, "m", UsageBillingMode.Platform));
        }

        (await Flush(recorder, sink)).Should().ContainSingle().Which.Unpriced.Should().BeTrue();
        scope.SawUnpricedPlatformUsage.Should().BeTrue();
    }

    private sealed class ThrowingPriceService : IModelPriceService
    {
        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("price snapshot unavailable");

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(
            decimal costUsd, UsageKind unit, UsageBillingMode billingMode, DateTime? at = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("price snapshot unavailable");

        public void Invalidate()
        {
        }
    }

    /// <summary>
    /// Captures <c>reelbolt.billing.unpriced_usage</c> measurements keyed by the usage scope they were recorded under (the
    /// callback runs synchronously on the recording flow), so tests running in parallel never see each other's.
    /// </summary>
    private sealed class UnpricedUsageListener : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener = new();
        private readonly System.Collections.Concurrent.ConcurrentBag<(Guid Scope, string? Source, string? Capability)> _seen = [];

        public UnpricedUsageListener()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == "reelbolt.billing.unpriced_usage") listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                string? source = null, capability = null;
                foreach (KeyValuePair<string, object?> tag in tags)
                {
                    if (tag.Key == "source") source = tag.Value?.ToString();
                    if (tag.Key == "capability") capability = tag.Value?.ToString();
                }

                for (long i = 0; i < value; i++) _seen.Add((UsageScope.Current?.ScopeId ?? Guid.Empty, source, capability));
            });
            _listener.Start();
        }

        public int CountFor(UsageScopeState scope) => _seen.Count(s => s.Scope == scope.ScopeId);

        public IReadOnlyList<(string? Source, string? Capability)> TagsFor(UsageScopeState scope) =>
            [.. _seen.Where(s => s.Scope == scope.ScopeId).Select(s => (s.Source, s.Capability))];

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task Rows_are_batched_by_size_and_every_row_is_flushed_on_shutdown()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder(new UsageRecorderOptions { MaxBatchSize = 100, FlushInterval = TimeSpan.FromMinutes(5) });
        UsageScopeState state;
        using (Scope())
        {
            state = UsageScope.Current!;
            for (int i = 0; i < 250; i++)
            {
                await recorder.RecordAsync(new UsageRecordRequest(
                    UsageKind.LlmInputTokens, 1, null, InferenceProviderKind.OpenAICompatible, "m", UsageBillingMode.Platform));
            }
        }

        IReadOnlyList<UsageEvent> rows = await Flush(recorder, sink);

        rows.Should().HaveCount(250);
        sink.BatchSizes.Should().Equal(100, 100, 50);
        state.RunningCreditsMillis.Should().Be(500);
    }

    [Fact]
    public async Task A_failing_write_is_retried_and_the_rows_are_not_lost()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder(new UsageRecorderOptions { RetryDelay = TimeSpan.FromMilliseconds(5) });
        sink.FailuresRemaining = 2;

        using (Scope())
        {
            await recorder.RecordAsync(new UsageRecordRequest(
                UsageKind.LlmOutputTokens, 5, null, InferenceProviderKind.OpenAICompatible, "m", UsageBillingMode.Platform));
        }

        (await Flush(recorder, sink)).Should().ContainSingle();
    }

    [Fact]
    public async Task Parallel_calls_under_one_scope_each_get_their_own_sequence_number()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();
        IChatClient client = new MeteringChatClient(new UsageReportingChatClient(1, 1), Platform(), recorder);

        using (Scope())
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.GetResponseAsync("x")));

        IReadOnlyList<UsageEvent> rows = await Flush(recorder, sink);
        rows.Should().HaveCount(40);
        rows.Select(r => r.IdempotencyKey).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Decision_calls_are_metered_through_the_decision_client_wrapper()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();
        ResolvedDecisionProvider provider = new(
            Guid.NewGuid(), "decide", InferenceProviderKind.TypeSafe, "http://x", "sys1", "", 5);
        IDecisionClient inner = new FixedDecisionClient(new DecisionResult("sys1", new Dictionary<string, DecisionAnswer>(), 70, 9));
        IDecisionClient client = new MeteringDecisionClient(inner, provider, recorder);

        using (Scope())
            await client.DecideAsync(new DecisionState("{}"), [], CancellationToken.None);

        IReadOnlyList<UsageEvent> rows = await Flush(recorder, sink);
        rows.Select(r => (r.Kind, r.Quantity)).Should().BeEquivalentTo(
            [(UsageKind.LlmInputTokens, 70m), (UsageKind.LlmOutputTokens, 9m)]);
        rows.Should().OnlyContain(r => r.ProviderKind == InferenceProviderKind.TypeSafe && r.BillingMode == UsageBillingMode.Platform);
    }

    [Fact]
    public async Task A_chat_call_is_priced_under_its_capability_and_account()
    {
        StaticPriceService prices = new();
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder(prices);
        Guid capability = Guid.NewGuid(), account = Guid.NewGuid();
        ResolvedInferenceProvider provider = new(capability, "Claude", InferenceProviderKind.Anthropic, "http://x",
            "claude-sonnet-5-5", "", 30, AccountId: account);
        IChatClient client = new MeteringChatClient(new UsageReportingChatClient(10, 10), provider, recorder);

        using (Scope())
            await client.GetResponseAsync("hi");

        await Flush(recorder, sink);
        prices.Requests.Should().HaveCount(2).And.OnlyContain(r =>
            r.ModelCapabilityId == capability && r.AccountId == account && r.Capability == InferenceProviderCapability.Chat
            && r.Kind == InferenceProviderKind.Anthropic && r.ModelCode == "claude-sonnet-5-5" && r.Variant == null);
    }

    [Fact]
    public async Task A_vision_call_is_priced_under_the_vision_capability()
    {
        StaticPriceService prices = new();
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder(prices);
        ResolvedInferenceProvider provider = new(Guid.NewGuid(), "Claude Vision", InferenceProviderKind.Anthropic, "http://x",
            "claude-sonnet-5-5", "", 30, Capability: InferenceProviderCapability.Vision);
        IChatClient client = new MeteringChatClient(new UsageReportingChatClient(10, 10), provider, recorder);

        using (Scope())
            await client.GetResponseAsync("hi");

        IReadOnlyList<UsageEvent> rows = await Flush(recorder, sink);
        prices.Requests.Should().OnlyContain(r => r.Capability == InferenceProviderCapability.Vision);
        rows.Should().OnlyContain(r => r.PriceId != null && r.Variant == null);
    }

    [Fact]
    public async Task A_decision_call_is_priced_under_the_decision_capability()
    {
        StaticPriceService prices = new();
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder(prices);
        Guid account = Guid.NewGuid();
        ResolvedDecisionProvider provider = new(
            Guid.NewGuid(), "decide", InferenceProviderKind.DeepSeek, "http://x", "deepseek-flash", "", 5, AccountId: account);
        IDecisionClient client = new MeteringDecisionClient(
            new FixedDecisionClient(new DecisionResult("deepseek-flash", new Dictionary<string, DecisionAnswer>(), 70, 9)), provider, recorder);

        using (Scope())
            await client.DecideAsync(new DecisionState("{}"), [], CancellationToken.None);

        await Flush(recorder, sink);
        prices.Requests.Should().HaveCount(2).And.OnlyContain(r =>
            r.Capability == InferenceProviderCapability.Decision && r.AccountId == account && r.ModelCapabilityId == provider.ProviderId);
    }

    [Fact]
    public async Task An_oracle_quote_is_charged_at_the_markup_with_no_price_row()
    {
        (UsageRecorder recorder, CapturingSink sink) = NewRecorder();

        using (Scope())
        {
            await recorder.RecordAsync(new UsageRecordRequest(
                UsageKind.VideoGenSeconds, 5, Guid.NewGuid(), InferenceProviderKind.Higgsfield, "dop-turbo", UsageBillingMode.Platform,
                Capability: InferenceProviderCapability.VideoGeneration, Variant: "720p", QuotedCostUsd: 0.6m));
        }

        UsageEvent row = (await Flush(recorder, sink)).Single();
        row.CreditsMillis.Should().Be(120_000, "$0.60 x 1.2 markup / $0.006 per credit = 120 credits");
        row.CostUsd.Should().Be(0.6m);
        row.PriceId.Should().BeNull();
        row.Variant.Should().Be("720p");
    }

    [Fact]
    public void The_factories_wrap_only_when_a_recorder_is_supplied()
    {
        (UsageRecorder recorder, _) = NewRecorder();
        ResolvedInferenceProvider provider = Platform();

        new ChatClientFactory().Get(provider).Should().NotBeOfType<MeteringChatClient>();

        ChatClientFactory metered = new(recorder);
        IChatClient first = metered.Get(provider);
        first.Should().BeOfType<MeteringChatClient>();
        // Disposing a wrapper must not dispose the shared, cached inner client other wrappers use.
        first.Dispose();
        metered.Get(provider).Should().BeOfType<MeteringChatClient>();
    }

    [Fact]
    public async Task The_ef_sink_inserts_through_a_migrations_excluded_mapping_and_a_replay_is_a_no_op()
    {
        ServiceCollection services = new();
        string name = Guid.NewGuid().ToString();
        services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(name));
        await using ServiceProvider provider = services.BuildServiceProvider();
        EfUsageEventSink<WorkflowEngineDbContext> sink = new(provider.GetRequiredService<IServiceScopeFactory>());

        UsageEvent Row(string key) => new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = OrgId,
            Kind = UsageKind.LlmInputTokens,
            Quantity = 1,
            ModelName = "m",
            IdempotencyKey = key,
            Source = UsageSource.Workflow
        };

        await sink.WriteAsync([Row("a"), Row("b")], CancellationToken.None);
        await sink.WriteAsync([Row("a"), Row("c")], CancellationToken.None);

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        (await db.UsageEvents.Select(e => e.IdempotencyKey).ToListAsync()).Should().BeEquivalentTo(["a", "b", "c"]);
    }

    private sealed class FixedDecisionClient(DecisionResult result) : IDecisionClient
    {
        public Task<DecisionResult> DecideAsync(DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken ct) =>
            Task.FromResult(result);
    }
}
