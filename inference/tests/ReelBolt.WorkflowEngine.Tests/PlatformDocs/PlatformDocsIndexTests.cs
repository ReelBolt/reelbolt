using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Qdrant.Client.Grpc;
using ReelBolt.Inference.Api.Services.PlatformDocs;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.PlatformDocs;

/// <summary>Drives the real build and search paths of <see cref="QdrantPlatformDocsIndex"/> over a faked server.</summary>
public class PlatformDocsIndexTests
{
    private const string Model = "test-model";

    private static readonly PlatformDocsChunk[] Chunks =
    [
        new("a.md#0", "a.md", PlatformDocsAudience.Developer, "A", "Steps", "", "first half")
        {
            Meta = new PlatformDocsChunkMeta { Kind = "recipe", NextId = "a.md#1" }
        },
        new("a.md#1", "a.md", PlatformDocsAudience.Developer, "A", "Steps", "", "second half")
        {
            Meta = new PlatformDocsChunkMeta { Kind = "recipe", PrevId = "a.md#0" }
        },
        new("b.md#0", "b.md", PlatformDocsAudience.Developer, "B", "Other", "", "other doc")
        {
            Meta = new PlatformDocsChunkMeta { Kind = "concept" }
        }
    ];

    private static readonly string HybridName = QdrantPlatformDocsIndex.CollectionName(Model, new PlatformDocsCorpus(Chunks).CorpusHash);

    private static Value PayloadValue(string s) => new() { StringValue = s };

    private static Dictionary<string, Value> Payload(PlatformDocsChunk c) => new()
    {
        ["chunkId"] = PayloadValue(c.Id),
        ["docPath"] = PayloadValue(c.DocPath),
        ["title"] = PayloadValue(c.Title),
        ["section"] = PayloadValue(c.Section),
        ["audience"] = PayloadValue(c.Audience.ToString()),
        ["content"] = PayloadValue(c.Text),
        ["kind"] = PayloadValue(c.Meta.Kind),
        ["prevId"] = PayloadValue(c.Meta.PrevId ?? ""),
        ["nextId"] = PayloadValue(c.Meta.NextId ?? "")
    };

    private static ScoredPoint Scored(PlatformDocsChunk c, float score)
    {
        var p = new ScoredPoint { Id = new PointId { Uuid = QdrantPlatformDocsIndex.PointId(c.Id).ToString() }, Score = score };
        foreach (var kv in Payload(c).Where(kv => kv.Value.StringValue.Length > 0))
            p.Payload[kv.Key] = kv.Value;
        return p;
    }

    private sealed class FakeStore : IPlatformDocsQdrant
    {
        public Dictionary<string, (ulong Count, bool Hybrid)> Collections { get; } = new();
        public List<string> Calls { get; } = [];
        public List<PointStruct> Upserted { get; } = [];
        public List<IReadOnlyList<QueryPoints>> Batches { get; } = [];
        public List<(Filter? Filter, ulong Limit, string? VectorName)> DenseSearches { get; } = [];
        public bool FailCreate { get; set; }

        public Task<bool> CollectionExistsAsync(string name, CancellationToken ct) => Task.FromResult(Collections.ContainsKey(name));
        public Task<ulong> CountAsync(string name, CancellationToken ct) => Task.FromResult(Collections[name].Count);

        public Task DeleteCollectionAsync(string name, CancellationToken ct)
        {
            Calls.Add("delete:" + name);
            Collections.Remove(name);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Collections.Keys.ToList());

        public Task CreateDenseCollectionAsync(string name, ulong dimensions, CancellationToken ct) => Create(name, false);

        public Task CreateHybridCollectionAsync(string name, ulong dimensions, CancellationToken ct) => Create(name, true);

        private Task Create(string name, bool hybrid)
        {
            if (FailCreate) throw new InvalidOperationException("create failed");
            Calls.Add((hybrid ? "create-hybrid:" : "create-dense:") + name);
            Collections[name] = (0, hybrid);
            return Task.CompletedTask;
        }

        public Task CreateKeywordIndexAsync(string name, string field, CancellationToken ct)
        {
            Calls.Add("index:" + field);
            return Task.CompletedTask;
        }

        public Task UpsertAsync(string name, IReadOnlyList<PointStruct> points, CancellationToken ct)
        {
            Upserted.AddRange(points);
            Collections[name] = (Collections[name].Count + (ulong)points.Count, Collections[name].Hybrid);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ScoredPoint>> SearchDenseAsync(
            string name, ReadOnlyMemory<float> vector, Filter? filter, ulong limit, string? vectorName, CancellationToken ct)
        {
            DenseSearches.Add((filter, limit, vectorName));
            return Task.FromResult<IReadOnlyList<ScoredPoint>>([Scored(Chunks[2], 0.8f)]);
        }

        public Task<IReadOnlyList<BatchResult>> QueryBatchAsync(string name, IReadOnlyList<QueryPoints> queries, CancellationToken ct)
        {
            Batches.Add(queries);
            var fused = new BatchResult { Result = { Scored(Chunks[0], 0.03f), Scored(Chunks[2], 0.02f) } };
            var dense = new BatchResult { Result = { Scored(Chunks[0], 0.9f), Scored(Chunks[2], 0.5f) } };
            return Task.FromResult<IReadOnlyList<BatchResult>>([fused, dense]);
        }

        public Task<IReadOnlyList<RetrievedPoint>> RetrieveAsync(string name, IReadOnlyList<PointId> ids, CancellationToken ct)
        {
            var point = new RetrievedPoint { Id = new PointId { Uuid = QdrantPlatformDocsIndex.PointId(Chunks[1].Id).ToString() } };
            foreach (var kv in Payload(Chunks[1]).Where(kv => kv.Value.StringValue.Length > 0))
                point.Payload[kv.Key] = kv.Value;
            return Task.FromResult<IReadOnlyList<RetrievedPoint>>([point]);
        }

        public Task<bool> HasNamedDenseAsync(string name, CancellationToken ct) => Task.FromResult(Collections[name].Hybrid);

        public Task<ScrollResponse> ScrollHashesAsync(string name, PointId? offset, CancellationToken ct)
        {
            var response = new ScrollResponse();
            foreach (PlatformDocsChunk c in Chunks)
            {
                var point = new RetrievedPoint { Id = new PointId { Uuid = QdrantPlatformDocsIndex.PointId(c.Id).ToString() } };
                point.Payload["embedHash"] = PayloadValue(QdrantPlatformDocsIndex.EmbedHash(c));
                var dense = new DenseVector();
                dense.Data.Add(1f);
                var vector = new VectorOutput { Dense = dense };
                point.Vectors = Collections[name].Hybrid
                    ? new VectorsOutput { Vectors = new NamedVectorsOutput { Vectors = { ["dense"] = vector } } }
                    : new VectorsOutput { Vector = vector };
                response.Result.Add(point);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class FakeEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int Calls { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var result = new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(new float[] { 1f, 0f, 0f }) { ModelId = Model }));
            return Task.FromResult(result);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static (QdrantPlatformDocsIndex Index, FakeStore Store, FakeEmbeddings Embeddings, PlatformDocsOptions Options) Make(
        bool hybrid, IInferenceTenantAccessor? tenant = null)
    {
        var options = new PlatformDocsOptions { Hybrid = new PlatformDocsHybridOptions { Enabled = hybrid } };
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveEmbeddingAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedEmbeddingProvider(null, "p", default, "http://x", Model, "k", 30));
        var store = new FakeStore();
        var embeddings = new FakeEmbeddings();
        var index = new QdrantPlatformDocsIndex(
            Options.Create(options), new PlatformDocsCorpusProvider(new PlatformDocsCorpus(Chunks)), resolver.Object,
            embeddings, NullLogger<QdrantPlatformDocsIndex>.Instance, store, tenantAccessor: tenant);
        return (index, store, embeddings, options);
    }

    [Fact]
    public async Task Dense_only_builds_the_unnamed_layout_and_searches_with_the_single_call_as_before()
    {
        var (index, store, _, _) = Make(hybrid: false);

        (await index.EnsureIndexedAsync(CancellationToken.None)).Should().BeTrue();
        store.Calls.Should().ContainSingle(c => c == "create-dense:" + HybridName + "d");
        store.Calls.Should().NotContain(c => c.StartsWith("index:") || c.StartsWith("create-hybrid:"));
        store.Upserted.Should().OnlyContain(p => p.Vectors.Vector != null && p.Vectors.Vectors_ == null);

        PlatformDocsSearchResult result = await index.SearchAsync("how do I render", 5, CancellationToken.None);

        result.Status.Should().Be(PlatformDocsSearchStatus.Ok);
        result.Ranked.Should().BeFalse();
        store.Batches.Should().BeEmpty();
        store.DenseSearches.Should().ContainSingle().Which.Should().Be((null, 5ul, null));
        result.Hits.Single().Score.Should().Be(0.8f, "raw cosine, untouched");
    }

    [Fact]
    public async Task Hybrid_builds_named_dense_and_sparse_with_keyword_indexes_and_queries_with_rrf()
    {
        var (index, store, _, _) = Make(hybrid: true);

        (await index.EnsureIndexedAsync(CancellationToken.None)).Should().BeTrue();
        store.Calls.Should().ContainSingle(c => c == "create-hybrid:" + HybridName);
        store.Calls.Count(c => c.StartsWith("index:")).Should().Be(7);
        store.Upserted.Should().OnlyContain(p => p.Vectors.Vectors_.Vectors.ContainsKey("dense") && p.Vectors.Vectors_.Vectors.ContainsKey("bm25"));

        PlatformDocsSearchResult result = await index.SearchAsync("render the video", 5, CancellationToken.None);

        result.Ranked.Should().BeTrue();
        IReadOnlyList<QueryPoints> batch = store.Batches.Should().ContainSingle().Which;
        batch.Should().HaveCount(2);
        batch[0].Query.Rrf.K.Should().Be(60u);
        batch[0].Prefetch.Select(p => p.Using).Should().Equal("dense", "bm25");
        store.DenseSearches.Should().BeEmpty();
    }

    [Fact]
    public async Task Neighbours_of_the_same_split_section_are_placed_right_behind_the_top_hit()
    {
        var (index, _, _, _) = Make(hybrid: true);
        await index.EnsureIndexedAsync(CancellationToken.None);

        PlatformDocsSearchResult result = await index.SearchAsync("render the video", 5, CancellationToken.None);

        result.Hits.Select(h => h.ChunkId).Should().Equal("a.md#0", "a.md#1", "b.md#0");
    }

    [Fact]
    public async Task The_injection_floor_reaches_stage_a_through_the_search_options()
    {
        var (index, _, _, _) = Make(hybrid: true);
        await index.EnsureIndexedAsync(CancellationToken.None);

        PlatformDocsSearchResult result = await index.SearchAsync(
            "x", 5, new PlatformDocsSearchOptions { MinScore = 0.95f }, CancellationToken.None);

        result.Hits.Should().BeEmpty("no dense cosine clears 0.95");
    }

    [Fact]
    public async Task Flipping_the_hybrid_switch_builds_a_new_collection_reuses_vectors_and_never_deletes_the_serving_one()
    {
        var (hybridIndex, store, embeddings, options) = Make(hybrid: true);
        await hybridIndex.EnsureIndexedAsync(CancellationToken.None);
        string hybridName = store.Collections.Keys.Single();
        store.Calls.Clear();
        int embedCalls = embeddings.Calls;

        options.Hybrid.Enabled = false;
        (await hybridIndex.EnsureIndexedAsync(CancellationToken.None)).Should().BeTrue();

        store.Calls.IndexOf("create-dense:" + hybridName + "d").Should().BeLessThan(store.Calls.IndexOf("delete:" + hybridName),
            "the old collection is dropped only after the new one is complete");
        embeddings.Calls.Should().Be(embedCalls, "the named dense vectors of the old collection are reused");
        store.Collections.Keys.Should().Equal(hybridName + "d");

        options.Hybrid.Enabled = true;
        await hybridIndex.EnsureIndexedAsync(CancellationToken.None);
        store.Collections.Keys.Should().Equal(new[] { hybridName });
    }

    [Fact]
    public async Task While_the_new_collection_builds_the_previous_one_serves_and_filtered_calls_are_not_silently_empty()
    {
        var (index, store, _, _) = Make(hybrid: true);
        string old = QdrantPlatformDocsIndex.CollectionName(Model, "oldcorpus1");
        store.Collections[old] = (3, false);
        store.FailCreate = true;

        (await index.EnsureIndexedAsync(CancellationToken.None)).Should().BeFalse();

        PlatformDocsSearchResult plain = await index.SearchAsync("q", 3, CancellationToken.None);
        plain.Status.Should().Be(PlatformDocsSearchStatus.Ok);
        store.DenseSearches.Should().ContainSingle().Which.VectorName.Should().BeNull("the old collection is unnamed");

        PlatformDocsSearchResult filtered = await index.SearchAsync(
            "q", 3, new PlatformDocsSearchOptions { StepType = "VideoCompile" }, CancellationToken.None);
        filtered.Status.Should().Be(PlatformDocsSearchStatus.NotIndexed);
        filtered.Message.Should().Contain("filtered search is not available");
        store.DenseSearches.Should().HaveCount(1, "no dense search was issued for the filtered call");
    }

    [Fact]
    public async Task The_query_embedding_cache_is_scoped_to_the_calling_organization()
    {
        var tenant = new Mock<IInferenceTenantAccessor>();
        Guid? org = Guid.NewGuid();
        tenant.SetupGet(t => t.OrganizationId).Returns(() => org);
        tenant.Setup(t => t.BeginPlatformScope()).Returns(Mock.Of<IDisposable>());
        var (index, _, embeddings, _) = Make(hybrid: true, tenant.Object);
        await index.EnsureIndexedAsync(CancellationToken.None);
        int baseline = embeddings.Calls;

        await index.SearchAsync("same text", 3, CancellationToken.None);
        await index.SearchAsync("same text", 3, CancellationToken.None);
        embeddings.Calls.Should().Be(baseline + 1, "the same organization hits its own cache");

        org = Guid.NewGuid();
        await index.SearchAsync("same text", 3, CancellationToken.None);
        embeddings.Calls.Should().Be(baseline + 2, "another organization never hits it");
    }
}
