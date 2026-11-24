#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// WebSearch's money: the step hold grows by the worst case of web search for an agent that holds the tool (platform
/// provider only), through the one shared plan the API and the engine both read; and the settle charges the searches
/// the metering decorator recorded inside the step's usage scope.
/// </summary>
public sealed class WebSearchCreditTests : IDisposable
{
    private const long WorstCaseMillis = 7_500;

    private sealed class Factory(DbContextOptions<WorkflowEngineDbContext> options) : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext() => new(options);
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:;Foreign Keys=False");
    private readonly Factory _factory;
    private readonly Guid _org = Guid.NewGuid();
    private readonly Guid _exec = Guid.NewGuid();

    public WebSearchCreditTests()
    {
        _connection.Open();
        using (InferenceApiDbContext owner = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options))
        {
            owner.Database.EnsureCreated();
        }

        _factory = new Factory(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite(_connection).Options);
    }

    public void Dispose() => _connection.Dispose();

    // -----------------------------------------------------------------
    // The hold
    // -----------------------------------------------------------------

    /// <summary>Quotes the WebSearch worst case, and prices everything else at the seed constants.</summary>
    private sealed class SearchQuotePricer(bool ownProvider) : IProfileQuantityPricer
    {
        public List<ProfileQuantity> SearchAsks { get; } = [];

        public ProfileQuote Price(ProfileQuantity quantity, AgentType chatAgentType, Guid? chatAgentDefinitionId)
        {
            if (quantity.Target == ProfilePriceTarget.WebSearch)
            {
                SearchAsks.Add(quantity);
                return new ProfileQuote(WorstCaseMillis, ownProvider);
            }

            return SeedProfilePricer.Instance.Price(quantity, chatAgentType, chatAgentDefinitionId);
        }
    }

    private static long HoldOf(AgentType agent, IProfileQuantityPricer pricer, StepType stepType = StepType.Agent)
    {
        WorkflowStep step = new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = stepType, AgentDefinitionId = Guid.NewGuid() };
        Dictionary<Guid, AgentType> types = new() { [step.AgentDefinitionId] = agent };
        return RunCreditHolds.Plan([step], new StepCreditEstimator([]), null, types, pricer).HoldsMillis[0];
    }

    [Theory]
    [InlineData(AgentType.ScriptwriterAgent)]
    [InlineData(AgentType.DirectorAgent)]
    [InlineData(AgentType.ShotDirector)]
    [InlineData(AgentType.NarrationWriter)]
    public void An_agent_with_WebSearch_holds_its_token_hold_plus_the_worst_case_of_its_searches(AgentType agent)
    {
        long without = HoldOf(agent, SeedProfilePricer.Instance);
        SearchQuotePricer pricer = new(ownProvider: false);

        HoldOf(agent, pricer).Should().Be(without + WorstCaseMillis);
        pricer.SearchAsks.Should().OnlyContain(q =>
            q.Kind == UsageKind.WebSearches && q.Quantity == WebSearchLimits.DefaultMaxCallsPerStep);
    }

    [Fact]
    public void A_provider_the_organization_brought_adds_nothing()
    {
        HoldOf(AgentType.ScriptwriterAgent, new SearchQuotePricer(ownProvider: true))
            .Should().Be(HoldOf(AgentType.ScriptwriterAgent, SeedProfilePricer.Instance));
    }

    [Fact]
    public void With_no_resolvable_search_provider_the_seed_prices_it_at_nothing()
    {
        // The seed pricer is what the plan falls back to when the WebSearch provider or its price cannot be resolved.
        StepUsageProfiles.SeedFallbackMillis(UsageKind.WebSearches, 5, 1.2m).Should().Be(0);
    }

    [Theory]
    [InlineData(AgentType.Custom)]
    [InlineData(AgentType.AuthorAgent)]
    [InlineData(AgentType.ReviewAgent)]
    [InlineData(AgentType.VideoStoryEditor)]
    public void An_agent_without_WebSearch_is_unchanged_and_never_asks_for_the_price(AgentType agent)
    {
        SearchQuotePricer pricer = new(ownProvider: false);

        HoldOf(agent, pricer).Should().Be(HoldOf(agent, SeedProfilePricer.Instance));
        pricer.SearchAsks.Should().BeEmpty();
    }

    [Fact]
    public void Only_an_Agent_step_carries_the_term()
    {
        SearchQuotePricer pricer = new(ownProvider: false);

        HoldOf(AgentType.ScriptwriterAgent, pricer, StepType.VideoCompile).Should().Be(HoldOf(AgentType.ScriptwriterAgent, SeedProfilePricer.Instance, StepType.VideoCompile));
        pricer.SearchAsks.Should().BeEmpty();
    }

    [Fact]
    public async Task The_price_table_prices_a_platform_provider_through_the_WebSearch_capability_and_a_byo_one_as_own()
    {
        foreach (Guid? organization in new Guid?[] { null, Guid.NewGuid() })
        {
            Mock<IInferenceProviderResolver> resolver = new();
            resolver.Setup(r => r.ResolveWebSearchAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(
                new ResolvedWebSearchProvider(Guid.NewGuid(), "ds", InferenceProviderKind.DeepSeek, "https://api.deepseek.com", "search", "k", 20, OrganizationId: organization));
            List<PriceRequest> asked = [];
            Mock<IModelPriceService> prices = new();
            prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PriceRequest, CancellationToken>((r, _) => asked.Add(r))
                .Returns(ValueTask.FromResult(new PriceQuote(WorstCaseMillis, 0.00375m, Guid.NewGuid())));
            prices.Setup(p => p.PriceQuotedUsdAsync(It.IsAny<decimal>(), It.IsAny<UsageKind>(), It.IsAny<UsageBillingMode>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
                .Returns(ValueTask.FromResult(new PriceQuote(1_200, 1m, null)));

            WorkflowStep step = new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = Guid.NewGuid() };
            Dictionary<Guid, AgentType> types = new() { [step.AgentDefinitionId] = AgentType.DirectorAgent };
            RecordingProfilePricer recorder = new();
            RunCreditHolds.Plan([step], new StepCreditEstimator([]), null, types, recorder);
            recorder.Requests.Should().Contain(k => k.Target == ProfilePriceTarget.WebSearch, "the dry run names the quantity to price");

            IProfileQuantityPricer table = await ProfilePriceTable.BuildAsync(recorder.Requests, resolver.Object, prices.Object, NullLogger.Instance, CancellationToken.None);
            long held = RunCreditHolds.Plan([step], new StepCreditEstimator([]), null, types, table).HoldsMillis[0];
            long baseline = RunCreditHolds.Plan([step], new StepCreditEstimator([]), null, types, new SeedProfilePricer(table.Markup)).HoldsMillis[0];

            asked.Should().Contain(r => r.Capability == InferenceProviderCapability.WebSearch && r.Unit == UsageKind.WebSearches
                && r.Quantity == WebSearchLimits.DefaultMaxCallsPerStep);
            if (organization is null)
                held.Should().BeGreaterThanOrEqualTo(baseline + WorstCaseMillis - 1, "a platform provider adds the worst case");
            else
                held.Should().BeLessThan(baseline + WorstCaseMillis, "a provider the organization brought is not charged");
        }
    }

    // -----------------------------------------------------------------
    // The settle
    // -----------------------------------------------------------------

    private sealed class SearchPriceService : IModelPriceService
    {
        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default) =>
            ValueTask.FromResult(request.Unit == UsageKind.WebSearches
                ? new PriceQuote((long)(request.Quantity * 1_500m), request.Quantity * 0.75m, Guid.NewGuid())
                : new PriceQuote(0, 0m, null));

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(
            decimal costUsd, UsageKind unit, UsageBillingMode billingMode, DateTime? at = null, CancellationToken ct = default) =>
            ValueTask.FromResult(new PriceQuote(0, costUsd, null));

        public void Invalidate()
        {
        }
    }

    /// <summary>WebSearches priced; the catalogued model's Chat token rows are NoPrice, so token pricing throws.</summary>
    private sealed class UnpricedTokensPriceService : IModelPriceService
    {
        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default) =>
            request.Unit == UsageKind.WebSearches
                ? ValueTask.FromResult(new PriceQuote((long)(request.Quantity * 1_500m), request.Quantity * 0.75m, Guid.NewGuid()))
                : throw new UnpricedUsageException(request.Unit, InferenceProviderKind.OpenAICompatible, "gpt-4o-mini");

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(
            decimal costUsd, UsageKind unit, UsageBillingMode billingMode, DateTime? at = null, CancellationToken ct = default) =>
            ValueTask.FromResult(new PriceQuote(0, costUsd, null));

        public void Invalidate()
        {
        }
    }

    private sealed class FailingPriceService : IModelPriceService
    {
        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("price lookup failed");

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(
            decimal costUsd, UsageKind unit, UsageBillingMode billingMode, DateTime? at = null, CancellationToken ct = default) =>
            ValueTask.FromResult(new PriceQuote(0, costUsd, null));

        public void Invalidate()
        {
        }
    }

    [Fact]
    public async Task An_informational_token_event_whose_price_lookup_FAILS_is_still_marked_unpriced()
    {
        UsageRecorder recorder = new(new FailingPriceService(), new DiscardSink(), NullLogger<UsageRecorder>.Instance);
        using (UsageScope.Begin(_org, null, _exec, Guid.NewGuid(), null, UsageSource.Workflow))
        {
            await recorder.RecordAsync(new UsageRecordRequest(
                UsageKind.LlmInputTokens, 10, Guid.NewGuid(), InferenceProviderKind.OpenAICompatible, "m", UsageBillingMode.Platform,
                Scope: UsageScope.Current, Capability: InferenceProviderCapability.Chat, InformationalWhenUnpriced: true), CancellationToken.None);
            UsageScope.Current!.SawUnpricedPlatformUsage.Should().BeTrue("only a successful lookup with no price is informational");
        }
    }

    private sealed class TokenSearchClient : IWebSearchClient
    {
        public Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken ct) =>
            Task.FromResult(new WebSearchResponse("openai", [new WebSearchResult("t", "https://example.com", "s")], null, ["q"], 1, null, false, 1_200, 300));
    }

    [Fact]
    public async Task A_one_search_step_with_unpriced_chat_rows_settles_at_the_WebSearches_amount_not_the_hold()
    {
        CreditGate gate = new(_factory, Options.Create(new CreditGateOptions { Enabled = true }), NullLogger<CreditGate>.Instance);
        Guid stepResult = Guid.NewGuid();
        await using (WorkflowEngineDbContext db = _factory.CreateDbContext())
        {
            (await new CreditLedger(db).GrantAsync(_org, CreditGrantSource.Plan, 100_000, null, Guid.NewGuid().ToString("N"))).Ok.Should().BeTrue();
        }

        CreditReservation reservation = await gate.ReserveAsync(_org, _exec, stepResult, 10_000);
        long before = await Balance();

        ResolvedWebSearchProvider provider = new(Guid.NewGuid(), "oa", InferenceProviderKind.OpenAICompatible, "https://api.openai.com/v1", "gpt-4o-mini", "k", 20);
        UsageRecorder recorder = new(new UnpricedTokensPriceService(), new DiscardSink(), NullLogger<UsageRecorder>.Instance);
        IWebSearchClient metered = new MeteringWebSearchClient(new TokenSearchClient(), provider, recorder);

        CreditSettlement settlement;
        using (UsageScope.Begin(_org, null, _exec, stepResult, null, UsageSource.Workflow))
        {
            await metered.SearchAsync(new WebSearchRequest("one", 5), CancellationToken.None);
            UsageScope.Current!.SawUnpricedPlatformUsage.Should().BeFalse("search tokens with no price are informational");
            settlement = await gate.SettleAsync(reservation.ReservationId, UsageScope.RunningCreditsMillis);
        }

        settlement.Ok.Should().BeTrue();
        settlement.ChargedMillis.Should().Be(1_500, "the WebSearches unit only, not the 10,000 hold");
        (before - await Balance()).Should().Be(1_500);
    }

    private sealed class StubSearchClient : IWebSearchClient
    {
        public Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken ct) =>
            Task.FromResult(new WebSearchResponse("DeepSeek", [new WebSearchResult("t", "https://example.com", "s")], null, ["q"], 1, null, false));
    }

    [Fact]
    public async Task A_step_with_two_recorded_WebSearches_events_settles_to_include_them()
    {
        // The engine settles at UsageScope.RunningCreditsMillis (WorkflowExecutorService.SettleStepCreditsAsync): every
        // priced event the metering decorator records inside the step's scope, whatever its kind, lands in it.
        CreditGate gate = new(_factory, Options.Create(new CreditGateOptions { Enabled = true }), NullLogger<CreditGate>.Instance);
        Guid stepResult = Guid.NewGuid();
        await using (WorkflowEngineDbContext db = _factory.CreateDbContext())
        {
            (await new CreditLedger(db).GrantAsync(_org, CreditGrantSource.Plan, 100_000, null, Guid.NewGuid().ToString("N"))).Ok.Should().BeTrue();
        }

        CreditReservation reservation = await gate.ReserveAsync(_org, _exec, stepResult, 10_000);
        long before = await Balance();

        ResolvedWebSearchProvider provider = new(Guid.NewGuid(), "ds", InferenceProviderKind.DeepSeek, "https://api.deepseek.com", "search", "k", 20);
        UsageRecorder recorder = new(new SearchPriceService(), new DiscardSink(), NullLogger<UsageRecorder>.Instance);
        IWebSearchClient metered = new MeteringWebSearchClient(new StubSearchClient(), provider, recorder);

        CreditSettlement settlement;
        using (UsageScope.Begin(_org, null, _exec, stepResult, null, UsageSource.Workflow))
        {
            await metered.SearchAsync(new WebSearchRequest("one", 5), CancellationToken.None);
            await metered.SearchAsync(new WebSearchRequest("two", 5), CancellationToken.None);
            UsageScope.RunningCreditsMillis.Should().Be(3_000, "two WebSearches units at 1,500 each");
            settlement = await gate.SettleAsync(reservation.ReservationId, UsageScope.RunningCreditsMillis);
        }

        settlement.Ok.Should().BeTrue();
        settlement.ChargedMillis.Should().Be(3_000);
        (before - await Balance()).Should().Be(3_000);
    }

    private async Task<long> Balance()
    {
        await using WorkflowEngineDbContext db = _factory.CreateDbContext();
        return (await new CreditLedger(db).GetBalanceAsync(_org)).BalanceMillis;
    }
}
