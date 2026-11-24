using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Services.PlatformDocs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Tests.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Rerank;

/// <summary>
/// The retrieval tail end to end with fake Qdrant candidates and a fake <see cref="IRerankClient"/>: stage A, then the
/// model rerank (stage B), then the final top K, for the injection builder and the tool path alike.
/// </summary>
public sealed class PlatformDocsRerankPipelineTests
{
    private static readonly PlatformDocsHybridOptions Hybrid = new();

    private static PlatformDocsHit Hit(int i, string? content = null) =>
        new("Doc" + i, "S", "", PlatformDocsAudience.User, content ?? "content " + i, 0.9f - i * 0.01f)
        {
            ChunkId = "c" + i.ToString("D2"),
            DocPath = "d" + i,
            FusedScore = 0.9f - i * 0.01f,
            DenseScore = 0.9f - i * 0.01f
        };

    private static List<PlatformDocsHit> Candidates(int n, string? content = null) =>
        [.. Enumerable.Range(0, n).Select(i => Hit(i, content))];

    private sealed class FakeClient(Func<IReadOnlyList<string>, IReadOnlyList<RerankScore>?> answer) : IRerankClient
    {
        public int Calls { get; private set; }
        public string? Query { get; private set; }
        public IReadOnlyList<string> Texts { get; private set; } = [];

        public Task<IReadOnlyList<RerankScore>?> RerankAsync(string query, IReadOnlyList<string> texts, CancellationToken ct)
        {
            Calls++;
            Query = query;
            Texts = texts;
            return Task.FromResult(answer(texts));
        }
    }

    private sealed class Factory(IRerankClient client) : IRerankClientFactory
    {
        public IRerankClient Create(ResolvedRerankProvider provider, int timeoutSeconds) => client;
    }

    private static IReadOnlyList<RerankScore>? Reverse(IReadOnlyList<string> texts) =>
        [.. Enumerable.Range(0, texts.Count).Reverse().Select(i => new RerankScore(i, i))]; // best first: the last candidate wins

    private static ResolvedRerankProvider Provider(Guid? org = null, string endpoint = "http://reranker:8080") => new(
        Guid.NewGuid(), "reranker", InferenceProviderKind.OpenAICompatible, endpoint, "bge", "", 3,
        OrganizationId: org, IsPlatformManaged: org is null, AccountId: Guid.NewGuid());

    private static PlatformDocsRerankStage Stage(
        IRerankClientFactory factory, ResolvedRerankProvider? provider, PlatformDocsRerankOptions? options = null)
    {
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveRerankAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(provider);
        UsageRecorder recorder = new(new NonWorkflowChargingHost.FixedPrices(4_000), new CapturingSink(), NullLogger<UsageRecorder>.Instance);
        return new PlatformDocsRerankStage(
            resolver.Object, factory, recorder,
            Options.Create(new PlatformDocsOptions { Rerank = options ?? new PlatformDocsRerankOptions() }),
            NullLogger<PlatformDocsRerankStage>.Instance);
    }

    private static IDisposable Turn() =>
        UsageScope.Begin(Guid.NewGuid(), Guid.NewGuid(), null, null, Guid.NewGuid(), UsageSource.Assistant);

    private static Task<IReadOnlyList<PlatformDocsHit>> Rank(
        IReadOnlyList<PlatformDocsHit> candidates, IPlatformDocsRerankStage? stage, int want = 4, string query = "question") =>
        QdrantPlatformDocsIndex.RankAsync(
            candidates, query, new PlatformDocsSearchOptions(), Hybrid, want, stage, NullLogger.Instance, CancellationToken.None);

    private static string[] Ids(IEnumerable<PlatformDocsHit> hits) => [.. hits.Select(h => h.ChunkId)];

    [Fact]
    public async Task Stage_B_order_wins_and_the_final_top_K_is_taken_from_it()
    {
        FakeClient client = new(Reverse);
        using IDisposable turn = Turn();

        IReadOnlyList<PlatformDocsHit> result = await Rank(Candidates(6), Stage(new Factory(client), Provider()), want: 3);

        Ids(result).Should().Equal("c05", "c04", "c03");
        client.Calls.Should().Be(1);
    }

    [Fact]
    public async Task One_rerank_call_per_turn_across_the_injection_and_the_tool_path()
    {
        FakeClient client = new(Reverse);
        PlatformDocsRerankStage stage = Stage(new Factory(client), Provider());
        List<PlatformDocsHit> candidates = Candidates(6);
        using (IDisposable turn = Turn())
        {
            IReadOnlyList<PlatformDocsHit> injection = await Rank(candidates, stage);
            IReadOnlyList<PlatformDocsHit> tool = await Rank(candidates, stage);

            Ids(injection).First().Should().Be("c05");
            Ids(tool).Should().Equal(Ids(await Rank(candidates, null)), "the second search of the turn keeps stage A's order");
            client.Calls.Should().Be(1);
        }

        using IDisposable nextTurn = Turn();
        await Rank(candidates, stage);
        client.Calls.Should().Be(2, "the bound is per turn, not per process");
    }

    [Fact]
    public async Task At_most_20_candidates_of_600_characters_and_a_500_character_query_reach_the_provider()
    {
        FakeClient client = new(Reverse);
        using IDisposable turn = Turn();

        await Rank(Candidates(30, new string('x', 4000)), Stage(new Factory(client), Provider()), query: new string('q', 3000));

        client.Texts.Should().HaveCount(20);
        client.Texts.Should().OnlyContain(t => t.Length <= 600);
        client.Query!.Length.Should().Be(500);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("malformed")]
    [InlineData("short")]
    [InlineData("throws")]
    public async Task Any_failure_keeps_stage_A_order(string failure)
    {
        FakeClient client = new(texts => failure switch
        {
            "short" => [new RerankScore(0, 1)],
            "throws" => throw new InvalidOperationException("boom"),
            _ => null // the client's fail-soft answer for a timeout or a malformed body
        });
        List<PlatformDocsHit> candidates = Candidates(6);
        using IDisposable turn = Turn();

        IReadOnlyList<PlatformDocsHit> result = await Rank(candidates, Stage(new Factory(client), Provider()));

        Ids(result).Should().Equal(Ids(await Rank(candidates, null)));
    }

    [Fact]
    public async Task Disabled_or_without_a_provider_the_output_is_the_no_rerank_path_exactly()
    {
        List<PlatformDocsHit> candidates = Candidates(8);
        IReadOnlyList<PlatformDocsHit> baseline = await Rank(candidates, null);
        FakeClient client = new(Reverse);
        using IDisposable turn = Turn();

        IReadOnlyList<PlatformDocsHit> disabled = await Rank(
            candidates, Stage(new Factory(client), Provider(), new PlatformDocsRerankOptions { Enabled = false }));
        using IDisposable turn2 = Turn();
        IReadOnlyList<PlatformDocsHit> noProvider = await Rank(candidates, Stage(new Factory(client), null));

        disabled.Should().Equal(baseline);
        noProvider.Should().Equal(baseline);
        client.Calls.Should().Be(0);
    }

    [Fact]
    public async Task An_organisation_row_pointing_at_a_private_address_is_refused_and_stage_A_stands()
    {
        // The real factory: a bring-your-own row connects to public addresses only, so nothing leaves the host.
        PlatformDocsRerankStage stage = Stage(
            new RerankClientFactory(), Provider(Guid.NewGuid(), "http://10.255.255.1:8080"),
            new PlatformDocsRerankOptions { TimeoutSeconds = 2 });
        List<PlatformDocsHit> candidates = Candidates(6);
        using IDisposable turn = Turn();

        IReadOnlyList<PlatformDocsHit> result = await Rank(candidates, stage);

        Ids(result).Should().Equal(Ids(await Rank(candidates, null)));
    }

    [Fact]
    public async Task The_injection_builder_injects_the_stage_B_order()
    {
        PlatformDocsRerankStage stage = Stage(new Factory(new FakeClient(Reverse)), Provider());
        PlatformDocsOptions options = new() { InjectTopK = 2 };
        PipelineSearch search = new(Candidates(6), stage);
        PlatformDocsContextBuilder builder = new(search, Options.Create(options), NullLogger<PlatformDocsContextBuilder>.Instance);
        using IDisposable turn = Turn();

        string? block = await builder.BuildAsync("how do I build a workflow", CancellationToken.None);

        block.Should().NotBeNull();
        block!.IndexOf("Doc5", StringComparison.Ordinal).Should().BeInRange(0, block.IndexOf("Doc4", StringComparison.Ordinal));
        block.Should().NotContain("Doc0");
    }

    private sealed class PipelineSearch(IReadOnlyList<PlatformDocsHit> candidates, IPlatformDocsRerankStage stage) : IPlatformDocsSearch
    {
        public Task<PlatformDocsSearchResult> SearchAsync(string query, int limit, CancellationToken ct) =>
            SearchAsync(query, limit, new PlatformDocsSearchOptions(), ct);

        public async Task<PlatformDocsSearchResult> SearchAsync(string query, int limit, PlatformDocsSearchOptions options, CancellationToken ct) =>
            new(PlatformDocsSearchStatus.Ok, [.. await QdrantPlatformDocsIndex.RankAsync(
                candidates, query, options, Hybrid, Math.Clamp(limit, 1, 20), stage, NullLogger.Instance, ct)])
            { Ranked = true };
    }
}
