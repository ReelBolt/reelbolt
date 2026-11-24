using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Rerank;

/// <summary>
/// The Rerank capability's wire contract, bounds, fail-soft behaviour, resolution, admin boundary and coverage.
/// Offline: a fake <see cref="HttpMessageHandler"/>, a fake provider store; no real call.
/// </summary>
public sealed class RerankCapabilityTests
{
    private const string Endpoint = "https://rerank.example.test/v1";

    // ---------------------------------------------------------------- the client

    [Fact]
    public async Task Posts_query_texts_and_top_n_to_the_rerank_route_and_orders_best_first()
    {
        Handler handler = new(_ => Json("""[{"index":0,"score":0.1},{"index":2,"score":0.9},{"index":1,"score":0.5}]"""));
        using HttpClient http = new(handler);
        OpenAICompatibleRerankClient client = new(Endpoint + "/", "sk-test", "bge", 3, http);

        IReadOnlyList<RerankScore>? scores = await client.RerankAsync("how do captions work", ["a", "b", "c"], CancellationToken.None);

        scores!.Select(s => s.Index).Should().Equal(2, 1, 0);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Uri.Should().Be(new Uri(Endpoint + "/rerank"));
        handler.Requests[0].Authorization.Should().Be("Bearer sk-test");
        using JsonDocument body = JsonDocument.Parse(handler.Requests[0].Body);
        body.RootElement.GetProperty("query").GetString().Should().Be("how do captions work");
        body.RootElement.GetProperty("texts").EnumerateArray().Select(e => e.GetString()).Should().Equal("a", "b", "c");
        body.RootElement.GetProperty("top_n").GetInt32().Should().Be(3);
    }

    [Theory]
    [InlineData("https://api.cohere.com/v2", "documents", "top_n")]
    [InlineData("https://api.jina.ai/v1", "documents", "top_n")]
    [InlineData("https://api.voyageai.com/v1", "documents", "top_k")]
    [InlineData("http://reranker:8080", "texts", "top_n")]
    [InlineData("https://tenant.example.test/v1", "texts", "top_n")]
    public async Task The_body_dialect_follows_the_endpoint_host(string endpoint, string textsKey, string countKey)
    {
        Handler handler = new(_ => Json("""{"data":[{"index":0,"relevance_score":1}]}"""));
        using HttpClient http = new(handler);

        IReadOnlyList<RerankScore>? scores = await new OpenAICompatibleRerankClient(endpoint, "k", "m", 3, http)
            .RerankAsync("q", ["a"], CancellationToken.None);

        scores.Should().ContainSingle();
        using JsonDocument body = JsonDocument.Parse(handler.Requests[0].Body);
        body.RootElement.TryGetProperty(textsKey, out _).Should().BeTrue();
        body.RootElement.TryGetProperty(textsKey == "texts" ? "documents" : "texts", out _).Should().BeFalse();
        body.RootElement.GetProperty(countKey).GetInt32().Should().Be(1);
        body.RootElement.GetProperty("model").GetString().Should().Be("m");
    }

    [Fact]
    public async Task Accepts_the_results_object_shape_with_relevance_score()
    {
        Handler handler = new(_ => Json("""{"results":[{"index":1,"relevance_score":0.8},{"index":0,"relevance_score":0.2}]}"""));
        using HttpClient http = new(handler);

        IReadOnlyList<RerankScore>? scores = await new OpenAICompatibleRerankClient(Endpoint, "", null, 3, http)
            .RerankAsync("q", ["a", "b"], CancellationToken.None);

        scores!.Select(s => s.Index).Should().Equal(1, 0);
    }

    [Fact]
    public async Task A_blank_key_sends_no_Authorization_header()
    {
        Handler handler = new(_ => Json("""[{"index":0,"score":1}]"""));
        using HttpClient http = new(handler);

        await new OpenAICompatibleRerankClient(Endpoint, "  ".Trim(), null, 3, http).RerankAsync("q", ["a"], CancellationToken.None);

        handler.Requests[0].Authorization.Should().BeNull();
    }

    [Fact]
    public async Task At_most_20_candidates_of_600_characters_and_a_truncated_query_leave_the_host()
    {
        Handler handler = new(_ => Json(Scores(RerankLimits.MaxCandidates)));
        using HttpClient http = new(handler);
        string[] texts = [.. Enumerable.Range(0, 30).Select(_ => new string('x', 5000))];

        IReadOnlyList<RerankScore>? scores = await new OpenAICompatibleRerankClient(Endpoint, "", null, 3, http)
            .RerankAsync(new string('q', 4000), texts, CancellationToken.None);

        scores.Should().HaveCount(20);
        using JsonDocument body = JsonDocument.Parse(handler.Requests[0].Body);
        string[] sent = [.. body.RootElement.GetProperty("texts").EnumerateArray().Select(e => e.GetString()!)];
        sent.Should().HaveCount(20);
        sent.Should().OnlyContain(t => t.Length == 600);
        body.RootElement.GetProperty("query").GetString()!.Length.Should().Be(RerankLimits.MaxQueryChars);
        body.RootElement.GetProperty("top_n").GetInt32().Should().Be(20);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"results":"nope"}""")]
    [InlineData("""[{"index":0}]""")]
    [InlineData("""[{"index":0,"score":"high"},{"index":1,"score":1}]""")]
    [InlineData("""[{"index":5,"score":1},{"index":1,"score":1}]""")]
    [InlineData("""[{"index":0,"score":1},{"index":0,"score":2}]""")]
    public async Task A_malformed_answer_is_null_never_an_exception(string body)
    {
        using HttpClient http = new(new Handler(_ => Json(body)));

        IReadOnlyList<RerankScore>? scores = await new OpenAICompatibleRerankClient(Endpoint, "", null, 3, http)
            .RerankAsync("q", ["a", "b"], CancellationToken.None);

        scores.Should().BeNull();
    }

    [Fact]
    public async Task Fewer_scores_than_candidates_is_a_fall_back_never_a_silent_drop()
    {
        using HttpClient http = new(new Handler(_ => Json("""[{"index":1,"score":0.9}]""")));

        IReadOnlyList<RerankScore>? scores = await new OpenAICompatibleRerankClient(Endpoint, "", null, 3, http)
            .RerankAsync("q", ["a", "b", "c"], CancellationToken.None);

        scores.Should().BeNull();
    }

    [Fact]
    public async Task A_non_success_status_and_a_transport_failure_are_null()
    {
        using HttpClient serverError = new(new Handler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using HttpClient unreachable = new(new Handler(_ => throw new HttpRequestException("down")));

        (await new OpenAICompatibleRerankClient(Endpoint, "", null, 3, serverError).RerankAsync("q", ["a"], default)).Should().BeNull();
        (await new OpenAICompatibleRerankClient(Endpoint, "", null, 3, unreachable).RerankAsync("q", ["a"], default)).Should().BeNull();
    }

    [Fact]
    public async Task A_timeout_is_null_but_a_caller_cancellation_is_rethrown()
    {
        using HttpClient slow = new(new SlowHandler());
        OpenAICompatibleRerankClient client = new(Endpoint, "", null, 1, slow);

        (await client.RerankAsync("q", ["a"], CancellationToken.None)).Should().BeNull("the 1 s client timeout expired");

        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));
        Func<Task> act = () => client.RerankAsync("q", ["a"], cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---------------------------------------------------------------- Cloudflare Workers AI dialect

    private const string CfEndpoint = "https://api.cloudflare.com/client/v4/accounts/acct1/ai/v1";
    private const string CfModel = "@cf/baai/bge-reranker-base";
    private const string CfEnvelope = """{"result":{"response":[{"id":0,"score":0.1},{"id":2,"score":0.9},{"id":1,"score":0.5}]},"success":true,"errors":[],"messages":[]}""";

    [Theory]
    [InlineData("https://api.cloudflare.com/client/v4/accounts/a/ai/v1", "WorkersAi")]
    [InlineData("https://API.CLOUDFLARE.COM/client/v4/accounts/a/ai/v1", "WorkersAi")]
    [InlineData("https://api.cloudflare.com.evil.test/client/v4/accounts/a/ai/v1", "Tei")]
    [InlineData("https://evil.test/api.cloudflare.com", "Tei")]
    [InlineData("https://reranker.acct.workers.dev/v1", "Tei")]
    public void Workers_ai_style_is_selected_by_exact_host_only(string endpoint, string expected)
        => RerankStyles.For(endpoint).ToString().Should().Be(expected);

    [Theory]
    [InlineData(CfEndpoint, CfModel, "https://api.cloudflare.com/client/v4/accounts/acct1/ai/run/@cf/baai/bge-reranker-base")]
    [InlineData(CfEndpoint + "/", CfModel, "https://api.cloudflare.com/client/v4/accounts/acct1/ai/run/@cf/baai/bge-reranker-base")]
    [InlineData("https://api.cloudflare.com/client/v4/accounts/acct1/ai/v2", CfModel, null)]
    [InlineData("https://api.cloudflare.com/client/v4/accounts//ai/v1", CfModel, null)]
    [InlineData("https://api.cloudflare.com/client/v4/accounts/a/ai/v1/extra", CfModel, null)]
    [InlineData("https://api.cloudflare.com/client/v4/accounts/a/ai/v1?x=1", CfModel, null)]
    [InlineData(CfEndpoint, "", null)]
    [InlineData(CfEndpoint, "@cf/baai/x?y=1", null)]
    [InlineData(CfEndpoint, "@cf/baai/x#f", null)]
    [InlineData(CfEndpoint, "@cf/baai/../x", null)]
    [InlineData(CfEndpoint, "@cf/../..", null)]
    [InlineData(CfEndpoint, "@cf/baai/..", null)]
    [InlineData(CfEndpoint, "@cf/baai/x\n", null)]
    [InlineData("https://api.cloudflare.com:8443/client/v4/accounts/acct1/ai/v1", CfModel, null)]
    [InlineData("https://api.cloudflare.com/client/v4/accounts/a%2Fb/ai/v1", CfModel, null)]
    [InlineData(CfEndpoint, "@cf/baai/x/y", null)]
    [InlineData(CfEndpoint, "BAAI/bge-reranker-base", null)]
    public void Workers_ai_url_is_derived_and_hostile_input_is_rejected(string endpoint, string model, string? expected)
    {
        Uri? actual = OpenAICompatibleRerankClient.WorkersAiUri(endpoint, model);
        if (expected is null) actual.Should().BeNull();
        else (actual?.AbsoluteUri).Should().Be(new Uri(expected).AbsoluteUri);
    }

    [Fact]
    public async Task Workers_ai_posts_query_contexts_top_k_to_the_run_url_with_bearer_and_no_model_in_body()
    {
        Handler handler = new(_ => Json(CfEnvelope));
        using HttpClient http = new(handler);

        IReadOnlyList<RerankScore>? scores = await new OpenAICompatibleRerankClient(CfEndpoint, "tok", CfModel, 3, http)
            .RerankAsync("q", ["a", "", "c"], CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Uri.AbsolutePath.Should().Be("/client/v4/accounts/acct1/ai/run/@cf/baai/bge-reranker-base");
        handler.Requests[0].Authorization.Should().Be("Bearer tok");
        using JsonDocument body = JsonDocument.Parse(handler.Requests[0].Body);
        body.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("query", "contexts", "top_k");
        body.RootElement.GetProperty("top_k").GetInt32().Should().Be(3);
        body.RootElement.GetProperty("contexts").EnumerateArray().Select(c => c.GetProperty("text").GetString())
            .Should().Equal("a", " ", "c");
        scores!.Select(x => x.Index).Should().Equal(2, 1, 0);
    }

    [Fact]
    public async Task Workers_ai_accepts_the_bare_response_and_flags_the_vendor_as_reached()
    {
        using HttpClient http = new(new Handler(_ => Json("""{"response":[{"id":1,"score":2},{"id":0,"score":1}]}""")));
        OpenAICompatibleRerankClient client = new(CfEndpoint, "", CfModel, 3, http);
        client.ReachedVendor.Should().BeFalse();
        (await client.RerankAsync("q", ["a", "b"], default))!.Select(x => x.Index).Should().Equal(1, 0);
        client.ReachedVendor.Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"result":{"response":[{"id":0,"score":1}]},"success":false}""")]
    [InlineData("""{"result":{"response":[{"id":0,"score":1}]},"success":true}""")]
    [InlineData("""{"result":{"response":[{"id":0,"score":1},{"id":0,"score":2}]},"success":true}""")]
    [InlineData("""{"result":{"response":[{"id":0,"score":1},{"id":5,"score":2}]},"success":true}""")]
    [InlineData("""{"result":{"response":[{"id":0,"score":1},{"id":1,"score":"x"}]},"success":true}""")]
    [InlineData("""{"result":{"response":[{"index":0,"score":1},{"index":1,"score":2}]},"success":true}""")]
    [InlineData("""{"results":[{"index":0,"relevance_score":1},{"index":1,"relevance_score":2}]}""")]
    [InlineData("""[{"id":0,"score":1},{"id":1,"score":2}]""")]
    public async Task Workers_ai_rejects_malformed_or_foreign_answers(string answer)
    {
        using HttpClient http = new(new Handler(_ => Json(answer)));
        (await new OpenAICompatibleRerankClient(CfEndpoint, "", CfModel, 3, http).RerankAsync("q", ["a", "b"], default)).Should().BeNull();
    }

    [Fact]
    public void Workers_ai_rejects_a_nan_score()
        => OpenAICompatibleRerankClient.Parse("""{"response":[{"id":0,"score":NaN}]}""", 1, true).Should().BeNull();

    [Fact]
    public async Task Workers_ai_without_a_model_or_on_a_bad_path_sends_nothing()
    {
        Handler handler = new(_ => Json(CfEnvelope));
        using HttpClient http = new(handler);
        (await new OpenAICompatibleRerankClient(CfEndpoint, "k", null, 3, http).RerankAsync("q", ["a"], default)).Should().BeNull();
        (await new OpenAICompatibleRerankClient("https://api.cloudflare.com/client/v4/ai/v1", "k", CfModel, 3, http).RerankAsync("q", ["a"], default)).Should().BeNull();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Workers_ai_non_2xx_and_timeout_fail_soft_without_reaching_the_vendor()
    {
        using HttpClient bad = new(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        OpenAICompatibleRerankClient c1 = new(CfEndpoint, "k", CfModel, 3, bad);
        (await c1.RerankAsync("q", ["a"], default)).Should().BeNull();
        c1.ReachedVendor.Should().BeFalse();

        using HttpClient slow = new(new SlowHandler());
        (await new OpenAICompatibleRerankClient(CfEndpoint, "k", CfModel, 1, slow).RerankAsync("q", ["a"], default)).Should().BeNull();
    }

    [Fact]
    public void Workers_ai_rerank_catalog_entry_is_priced_per_search()
    {
        ModelPriceCatalog.All.Should().Contain(e => e.ProviderKind == InferenceProviderKind.OpenAICompatible
            && e.Capability == InferenceProviderCapability.Rerank && e.Model == CfModel
            && e.UsdPerUnit[UsageKind.RerankSearches] == 0.00002m);
    }

    // ---------------------------------------------------------------- resolution

    [Fact]
    public async Task Resolution_prefers_the_explicit_Rerank_row_then_the_default_then_none()
    {
        ModelCapabilityView fallback = Row(isDefault: true, "rerank-default");
        ModelCapabilityView explicitRow = Row(isDefault: false, "rerank-explicit");
        ModelCapabilityView chat = Row(isDefault: true, "chat", InferenceProviderCapability.Chat);

        (await Resolver([fallback, explicitRow]).ResolveRerankAsync(explicitRow.Id, default))!.ProviderId.Should().Be(explicitRow.Id);
        (await Resolver([fallback]).ResolveRerankAsync(null, default))!.ProviderId.Should().Be(fallback.Id);
        (await Resolver([chat, fallback]).ResolveRerankAsync(chat.Id, default))!.ProviderId
            .Should().Be(fallback.Id, "a Chat id must never be handed to the rerank client");
        (await Resolver([chat]).ResolveRerankAsync(null, default)).Should().BeNull("no legacy configuration fallback");
    }

    [Fact]
    public async Task An_unpriced_platform_rerank_default_is_unavailable_when_billing_is_on()
    {
        ModelCapabilityView unpriced = Row(isDefault: true, "unpriced");

        Func<Task> act = () => Resolver([unpriced], [unpriced.Id], new Dictionary<string, string?> { ["Billing:Enabled"] = "true" })
            .ResolveRerankAsync(null, default);

        await act.Should().ThrowAsync<ModelUnpricedException>();
    }

    [Fact]
    public async Task The_resolved_rerank_provider_redacts_its_key()
    {
        ResolvedRerankProvider? resolved = await Resolver([Row(true, "r", apiKey: "sk-secret")]).ResolveRerankAsync(null, default);

        resolved!.ApiKey.Should().Be("sk-secret");
        resolved.ToString().Should().NotContain("sk-secret");
        resolved.CacheKey.Should().HaveLength(64).And.NotContain("sk-secret");
    }

    // ---------------------------------------------------------------- admin boundary, usage kind, coverage, enum

    [Fact]
    public void Rerank_is_servable_by_OpenAICompatible_only_and_the_allowlist_agrees()
    {
        foreach (InferenceProviderKind kind in ProviderCapabilityUsage.Kinds)
        {
            bool rejected = InferenceProvidersController.IsUnsupportedCombination(kind, InferenceProviderCapability.Rerank, out string? error);
            if (kind == InferenceProviderKind.Anthropic) error.Should().Contain("Rerank");
            bool servable = ProviderCapabilityUsage.ServableByKind[kind].Contains(InferenceProviderCapability.Rerank);

            rejected.Should().Be(kind != InferenceProviderKind.OpenAICompatible, kind.ToString());
            servable.Should().Be(!rejected, kind.ToString());
        }
    }

    [Fact]
    public void Rerank_meters_one_RerankSearches_unit_per_search()
    {
        ProviderCapabilityUsage.ByCapability[InferenceProviderCapability.Rerank].Should().Equal(UsageKind.RerankSearches);
        ProviderCapabilityUsage.DefaultCapabilityFor(UsageKind.RerankSearches).Should().Be(InferenceProviderCapability.Rerank);
    }

    [Fact]
    public void Coverage_lists_a_Rerank_capability_with_no_price()
    {
        CoverageCapability rerank = new(
            Guid.NewGuid(), "Acct", "Reranker", InferenceProviderKind.OpenAICompatible,
            InferenceProviderCapability.Rerank, ModelPriceSource.Table);

        IReadOnlyList<PriceCoverageGap> gaps = PriceCoverage.Find([rerank], [], new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        gaps.Should().ContainSingle().Which.MissingUnits.Should().Equal(UsageKind.RerankSearches);
    }

    [Fact]
    public void The_capability_and_usage_kind_serialize_by_name()
    {
        InferenceProviderCapability.Rerank.ToString().Should().Be("Rerank");
        UsageKind.RerankSearches.ToString().Should().Be("RerankSearches");
        Enum.Parse<InferenceProviderCapability>("Rerank").Should().Be(InferenceProviderCapability.Rerank);
    }

    [Fact]
    public void A_self_hosted_price_suggestion_is_a_real_zero_and_no_hosted_vendor_price_is_seeded()
    {
        IReadOnlyList<ModelPrice> rerank = [.. ModelPriceCatalog.Priced.Where(p => p.Capability == InferenceProviderCapability.Rerank)];

        // Exactly two priced rerank rows: the self-hosted real zero and the Cloudflare Workers AI per-search price.
        rerank.Select(p => p.Model).Should().BeEquivalentTo("BAAI/bge-reranker-base", CfModel);
        rerank.Single(p => p.Model == "BAAI/bge-reranker-base").UsdPerUnit[UsageKind.RerankSearches].Should().Be(0m);
    }

    // ---------------------------------------------------------------- helpers

    private static string Scores(int n) =>
        "[" + string.Join(",", Enumerable.Range(0, n).Select(i => $"{{\"index\":{i},\"score\":{(n - i) / 100.0:0.00}}}")) + "]";

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static ModelCapabilityView Row(
        bool isDefault, string name, InferenceProviderCapability capability = InferenceProviderCapability.Rerank, string? apiKey = null) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Kind = InferenceProviderKind.OpenAICompatible,
            Endpoint = "http://reranker:8080",
            ModelName = "BAAI/bge-reranker-base",
            ApiKeyEncrypted = apiKey,
            IsDefault = isDefault,
            IsEnabled = true,
            Capability = capability
        };

    private static InferenceProviderResolver Resolver(
        IReadOnlyList<ModelCapabilityView> providers,
        IReadOnlyCollection<Guid>? unpricedIds = null,
        IDictionary<string, string?>? config = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<IInferenceProviderStore>(new FakeStore(providers, unpricedIds));
        ServiceProvider sp = services.BuildServiceProvider();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(config ?? new Dictionary<string, string?>()).Build();
        return new InferenceProviderResolver(
            sp.GetRequiredService<IServiceScopeFactory>(), configuration, new Passthrough(), NullLogger<InferenceProviderResolver>.Instance);
    }

    private sealed class FakeStore(IReadOnlyList<ModelCapabilityView> providers, IReadOnlyCollection<Guid>? unpricedIds) : IInferenceProviderStore
    {
        public Task<IReadOnlyList<ModelCapabilityView>> LoadEnabledAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelCapabilityView>>([.. providers.Where(p => p.OrganizationId is null || p.OrganizationId == organizationId)]);

        public Task<IReadOnlyDictionary<Guid, Guid?>> LoadAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, Guid?>>(new Dictionary<Guid, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadOrgAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<AgentType, Guid?>>(new Dictionary<AgentType, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadBuiltInAgentOverridesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<AgentType, Guid?>>(new Dictionary<AgentType, Guid?>());

        public Task<IReadOnlySet<Guid>> LoadUnpricedCapabilityIdsAsync(DateTime at, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<Guid>>((unpricedIds ?? []).ToHashSet());
    }

    private sealed class Passthrough : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public bool TryUnprotect(string? ciphertext, out string plaintext)
        {
            plaintext = ciphertext ?? string.Empty;
            return ciphertext != null;
        }
    }

    private sealed record Captured(Uri Uri, string Body, string? Authorization);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Captured(request.RequestUri!, body, request.Headers.Authorization?.ToString()));
            return respond(request);
        }
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

/// <summary>The factory's per-provider handler: org rows are public-only and follow no redirect; platform rows work.</summary>
public sealed class RerankClientFactoryEgressTests
{
    private static ResolvedRerankProvider Provider(string endpoint, bool platform) => new(
        Guid.NewGuid(), "r", InferenceProviderKind.OpenAICompatible, endpoint, "bge", "sk-secret", 3,
        OrganizationId: platform ? null : Guid.NewGuid(), IsPlatformManaged: platform);

    private static (System.Net.HttpListener Listener, string Url, List<string> Paths) Serve(Func<System.Net.HttpListenerContext, Task> handle)
    {
        int port;
        using (System.Net.Sockets.TcpListener probe = new(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }

        System.Net.HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        List<string> paths = [];
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    System.Net.HttpListenerContext ctx = await listener.GetContextAsync();
                    lock (paths) paths.Add(ctx.Request.Url!.AbsolutePath);
                    await handle(ctx);
                    ctx.Response.Close();
                }
                catch
                {
                    return;
                }
            }
        });
        return (listener, $"http://127.0.0.1:{port}", paths);
    }

    private static Task Ok(System.Net.HttpListenerContext ctx)
    {
        byte[] body = Encoding.UTF8.GetBytes("""[{"index":0,"score":1}]""");
        ctx.Response.ContentType = "application/json";
        return ctx.Response.OutputStream.WriteAsync(body).AsTask();
    }

    [Fact]
    public async Task An_org_row_pointing_at_a_private_address_is_refused_fail_soft_and_never_connects()
    {
        (System.Net.HttpListener listener, string url, List<string> paths) = Serve(Ok);
        using (listener)
        {
            using IRerankClientDisposable client = new(new RerankClientFactory().Create(Provider(url, platform: false), 3));

            IReadOnlyList<RerankScore>? scores = await client.Inner.RerankAsync("q", ["a"], CancellationToken.None);

            scores.Should().BeNull();
            lock (paths) paths.Should().BeEmpty("the connect step refused the loopback address");
        }
    }

    [Fact]
    public async Task A_platform_row_reaches_its_private_endpoint()
    {
        (System.Net.HttpListener listener, string url, _) = Serve(Ok);
        using (listener)
        {
            using IRerankClientDisposable client = new(new RerankClientFactory().Create(Provider(url, platform: true), 3));

            (await client.Inner.RerankAsync("q", ["a"], CancellationToken.None)).Should().ContainSingle();
        }
    }

    [Fact]
    public async Task A_redirect_is_not_followed()
    {
        (System.Net.HttpListener listener, string url, List<string> paths) = Serve(ctx =>
        {
            if (ctx.Request.Url!.AbsolutePath == "/v1/rerank")
            {
                ctx.Response.StatusCode = 302;
                ctx.Response.RedirectLocation = "/internal/secret";
                return Task.CompletedTask;
            }

            return Ok(ctx);
        });
        using (listener)
        {
            using IRerankClientDisposable client = new(new RerankClientFactory().Create(Provider(url + "/v1", platform: true), 3));

            (await client.Inner.RerankAsync("q", ["a"], CancellationToken.None)).Should().BeNull();
            lock (paths) paths.Should().Equal("/v1/rerank");
        }
    }

    private sealed class IRerankClientDisposable(IRerankClient inner) : IDisposable
    {
        public IRerankClient Inner { get; } = inner;

        public void Dispose() => (Inner as IDisposable)?.Dispose();
    }
}
