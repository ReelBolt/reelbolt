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
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.PlatformDocs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Tests.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Rerank;

/// <summary>
/// The platform-docs rerank stage with a fake <see cref="IRerankClient"/>: reorders as scripted, falls back (null)
/// on every failure, meters one row per call, charges inside the assistant turn's allowance. No real call.
/// </summary>
public sealed class PlatformDocsRerankStageTests
{
    private static readonly Guid OrgId = Guid.NewGuid();

    private static readonly IReadOnlyList<PlatformDocsHit> Docs =
        [Hit("a", "alpha"), Hit("b", "bravo"), Hit("c", "charlie")];

    private static PlatformDocsHit Hit(string id, string content) =>
        new("T" + id, "", "", PlatformDocsAudience.User, content, 0.5f) { ChunkId = id };

    private static ResolvedRerankProvider Provider(Guid? org = null) => new(
        Guid.NewGuid(), "reranker", InferenceProviderKind.OpenAICompatible, "http://reranker:8080", "bge", "", 3,
        OrganizationId: org, IsPlatformManaged: org is null, AccountId: Guid.NewGuid());

    private sealed class FakeClient(Func<string, IReadOnlyList<string>, IReadOnlyList<RerankScore>?> answer) : IRerankClient
    {
        public int Calls { get; private set; }
        public bool ReachedVendor { get; set; }
        public string? Query { get; private set; }
        public IReadOnlyList<string> Texts { get; private set; } = [];

        public Task<IReadOnlyList<RerankScore>?> RerankAsync(string query, IReadOnlyList<string> texts, CancellationToken ct)
        {
            Calls++;
            Query = query;
            Texts = texts;
            return Task.FromResult(answer(query, texts));
        }
    }

    private sealed class FakeFactory(IRerankClient client) : IRerankClientFactory
    {
        public int Created { get; private set; }

        public IRerankClient Create(ResolvedRerankProvider provider, int timeoutSeconds)
        {
            Created++;
            return client;
        }
    }

    private sealed class Harness
    {
        public required PlatformDocsRerankStage Stage { get; init; }
        public required FakeClient Client { get; init; }
        public required UsageRecorder Recorder { get; init; }
        public required CapturingSink Sink { get; init; }
        public required Mock<IInferenceProviderResolver> Resolver { get; init; }

        public async Task<IReadOnlyList<UsageEvent>> FlushAsync()
        {
            await Recorder.StartAsync(CancellationToken.None);
            await Recorder.StopAsync(CancellationToken.None);
            return Sink.Events;
        }
    }

    private static Harness Build(
        ResolvedRerankProvider? provider,
        Func<string, IReadOnlyList<string>, IReadOnlyList<RerankScore>?>? answer = null,
        PlatformDocsRerankOptions? options = null,
        Exception? resolveThrows = null,
        long? priceMillis = 4_000)
    {
        FakeClient client = new(answer ?? ((_, texts) => [.. Enumerable.Range(0, texts.Count).Reverse().Select(i => new RerankScore(i, i))]));
        Mock<IInferenceProviderResolver> resolver = new();
        if (resolveThrows is not null)
            resolver.Setup(r => r.ResolveRerankAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ThrowsAsync(resolveThrows);
        else
            resolver.Setup(r => r.ResolveRerankAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(provider);

        CapturingSink sink = new();
        UsageRecorder recorder = new(new NonWorkflowChargingHost.FixedPrices(priceMillis), sink, NullLogger<UsageRecorder>.Instance);
        PlatformDocsRerankStage stage = new(
            resolver.Object, new FakeFactory(client), recorder,
            Options.Create(new PlatformDocsOptions { Rerank = options ?? new PlatformDocsRerankOptions() }),
            NullLogger<PlatformDocsRerankStage>.Instance);
        return new Harness { Stage = stage, Client = client, Recorder = recorder, Sink = sink, Resolver = resolver };
    }

    private static IDisposable Scope() =>
        UsageScope.Begin(OrgId, Guid.NewGuid(), null, null, Guid.NewGuid(), UsageSource.Assistant);

    // ---------------------------------------------------------------- ordering and fail-soft

    [Fact]
    public async Task Reorders_as_the_client_scripts()
    {
        Harness h = Build(Provider(), (_, _) => [new(2, 0.9), new(0, 0.5), new(1, 0.1)]);
        using IDisposable scope = Scope();

        IReadOnlyList<PlatformDocsHit>? result = await h.Stage.RerankAsync("question", Docs, default);

        result!.Select(c => c.ChunkId).Should().Equal("c", "a", "b");
    }

    [Fact]
    public async Task Candidates_past_the_rerank_window_keep_their_stage_A_order_after_the_reranked_head()
    {
        Harness h = Build(Provider(), options: new PlatformDocsRerankOptions { Candidates = 2 });
        using IDisposable scope = Scope();

        IReadOnlyList<PlatformDocsHit>? result = await h.Stage.RerankAsync("question", Docs, default);

        h.Client.Texts.Should().HaveCount(2);
        result!.Select(c => c.ChunkId).Should().Equal("b", "a", "c");
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("timeout")]
    [InlineData("fewer")]
    [InlineData("duplicate")]
    public async Task A_failure_falls_back_to_stage_A_and_writes_no_usage_row(string failure)
    {
        Func<string, IReadOnlyList<string>, IReadOnlyList<RerankScore>?> answer = failure switch
        {
            "fewer" => (_, _) => [new(0, 1)],
            "duplicate" => (_, _) => [new(0, 1), new(0, 1), new(1, 1)],
            _ => (_, _) => null // the client's fail-soft answer for malformed and timed-out calls
        };
        Harness h = Build(Provider(), answer);
        using IDisposable scope = Scope();

        (await h.Stage.RerankAsync("question", Docs, default)).Should().BeNull();
        (await h.FlushAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_stage_timeout_cancellation_is_null_but_a_caller_cancellation_is_rethrown()
    {
        Harness slow = Build(Provider(), (_, _) => throw new OperationCanceledException());
        using IDisposable scope = Scope();
        (await slow.Stage.RerankAsync("q", Docs, default)).Should().BeNull();

        using IDisposable nextTurn = Scope(); // the per-turn guard would answer null for a second call in one scope
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        Func<Task> act = () => slow.Stage.RerankAsync("q", Docs, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Disabled_no_provider_and_unpriced_are_all_the_no_rerank_path()
    {
        Harness disabled = Build(Provider(), options: new PlatformDocsRerankOptions { Enabled = false });
        Harness none = Build(null);
        Harness unpriced = Build(null, resolveThrows: new ModelUnpricedException(InferenceProviderCapability.Rerank, "bge"));
        using IDisposable scope = Scope();

        (await disabled.Stage.RerankAsync("q", Docs, default)).Should().BeNull();
        disabled.Resolver.Verify(r => r.ResolveRerankAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        disabled.Client.Calls.Should().Be(0);
        (await none.Stage.RerankAsync("q", Docs, default)).Should().BeNull();
        none.Client.Calls.Should().Be(0);
        (await unpriced.Stage.RerankAsync("q", Docs, default)).Should().BeNull("an unpriced platform rerank model is 'rerank unavailable'");
        unpriced.Client.Calls.Should().Be(0);
        (await disabled.FlushAsync()).Should().BeEmpty();
        (await none.FlushAsync()).Should().BeEmpty();
        (await unpriced.FlushAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task One_candidate_or_an_empty_query_needs_no_call()
    {
        Harness h = Build(Provider());
        using IDisposable scope = Scope();

        (await h.Stage.RerankAsync("q", [Docs[0]], default)).Should().BeNull();
        (await h.Stage.RerankAsync("   ", Docs, default)).Should().BeNull();
        h.Client.Calls.Should().Be(0);
    }

    // ---------------------------------------------------------------- bounds and privacy

    [Fact]
    public async Task At_most_20_candidates_of_600_characters_and_a_truncated_query_reach_the_client()
    {
        Harness h = Build(Provider(), options: new PlatformDocsRerankOptions { Candidates = 500 });
        List<PlatformDocsHit> many = [.. Enumerable.Range(0, 35).Select(i => Hit("k" + i, new string('x', 4000)))];
        using IDisposable scope = Scope();

        IReadOnlyList<PlatformDocsHit>? result = await h.Stage.RerankAsync(new string('q', 3000), many, default);

        h.Client.Calls.Should().Be(1, "one rerank call per invocation");
        h.Client.Texts.Should().HaveCount(RerankLimits.MaxCandidates);
        h.Client.Texts.Should().OnlyContain(t => t.Length == RerankLimits.MaxCandidateChars);
        h.Client.Query!.Length.Should().Be(RerankLimits.MaxQueryChars);
        result.Should().HaveCount(35, "nothing is dropped");
    }

    // ---------------------------------------------------------------- metering and charging

    [Fact]
    public async Task One_usage_row_per_call_with_the_rerank_unit_assistant_source_and_capability()
    {
        Harness h = Build(Provider());
        using (Scope())
        {
            await h.Stage.RerankAsync("question", Docs, default);
        }

        IReadOnlyList<UsageEvent> events = await h.FlushAsync();

        UsageEvent row = events.Should().ContainSingle().Subject;
        row.Kind.Should().Be(UsageKind.RerankSearches);
        row.Quantity.Should().Be(1m);
        row.Source.Should().Be(UsageSource.Assistant);
        row.OrganizationId.Should().Be(OrgId);
        row.BillingMode.Should().Be(UsageBillingMode.Platform);
        row.CreditsMillis.Should().Be(4_000);
    }

    [Fact]
    public async Task The_charge_lands_in_the_scope_that_the_turn_hold_settles()
    {
        Harness h = Build(Provider());
        using IDisposable scope = Scope();

        await h.Stage.RerankAsync("question", Docs, default);

        UsageScope.Current!.RunningCreditsMillis.Should().Be(4_000);
    }

    [Fact]
    public async Task An_org_owned_rerank_row_never_receives_documentation()
    {
        Harness h = Build(Provider(org: OrgId));
        using (Scope())
        {
            (await h.Stage.RerankAsync("question", Docs, default)).Should().BeNull();
        }

        h.Client.Calls.Should().Be(0, "internal docs never go to a tenant-controlled reranker");
        (await h.FlushAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_malformed_answer_that_reached_the_vendor_is_metered_but_a_timeout_is_not()
    {
        Harness reached = Build(Provider(), (_, _) => null);
        reached.Client.ReachedVendor = true;
        Harness timedOut = Build(Provider(), (_, _) => null);
        using (Scope())
        {
            (await reached.Stage.RerankAsync("q", Docs, default)).Should().BeNull();
        }

        using (Scope())
        {
            (await timedOut.Stage.RerankAsync("q", Docs, default)).Should().BeNull();
        }

        (await reached.FlushAsync()).Should().ContainSingle();
        (await timedOut.FlushAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Inside_the_turn_allowance_the_rerank_cost_adds_no_ledger_line()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        using NonWorkflowChargingHost host = new(o => o.UseSqlite(connection), prices: new NonWorkflowChargingHost.FixedPrices(4_000));
        await using (InferenceApiDbContext db = host.NewDb())
        {
            await db.Database.EnsureCreatedAsync();
        }

        await host.SeedPlansAsync();
        Guid org = await host.AddOrgAsync(); // trial: 20 credits a month included
        Harness h = Build(Provider());

        TurnHold turn = await host.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        long metered;
        using (UsageScope.Begin(org, null, null, null, Guid.NewGuid(), UsageSource.Assistant))
        {
            await h.Stage.RerankAsync("question", Docs, default);
            metered = UsageScope.Current!.RunningCreditsMillis;
        }

        TurnSettlement settled = await host.Charges.SettleTurnAsync(turn, metered);

        metered.Should().Be(4_000);
        (settled.Ok, settled.AllowanceMillis, settled.ChargedMillis).Should().Be((true, 4_000L, 0L));
        (await host.EntriesAsync(org)).Should().BeEmpty("the call is inside the turn's existing allowance: no new line");
    }

    [Fact]
    public async Task With_billing_off_the_usage_row_is_written_and_nothing_is_charged()
    {
        using NonWorkflowChargingHost off = new(o => o.UseSqlite("Data Source=:memory:"), billingEnabled: false);
        Harness h = Build(Provider());

        TurnHold turn = await off.Charges.BeginTurnAsync(OrgId, UsageSource.Assistant, Guid.NewGuid());
        long metered;
        using (Scope())
        {
            await h.Stage.RerankAsync("question", Docs, default);
            metered = UsageScope.Current!.RunningCreditsMillis;
        }

        TurnSettlement settled = await off.Charges.SettleTurnAsync(turn, metered);

        (await h.FlushAsync()).Should().ContainSingle();
        (turn.Code, settled.ChargedMillis).Should().Be((NonWorkflowChargeCodes.BillingDisabled, 0L));
    }
}
