using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.PlatformDocs;

/// <summary>Outcome of one search over the documentation index.</summary>
public enum PlatformDocsSearchStatus
{
    Ok,
    /// <summary>Indexing is disabled, no documentation shipped in this image, or nothing indexed yet.</summary>
    NotIndexed,
    /// <summary>No usable Embedding provider, or the vector store could not be reached.</summary>
    Unavailable
}

public sealed record PlatformDocsHit(
    string Title,
    string Section,
    string Url,
    PlatformDocsAudience Audience,
    string Content,
    float Score)
{
    /// <summary>
    /// True for every hit that is not customer-facing user-guide text. Such a section is background the
    /// assistant reads to understand the product and must never quote, paraphrase or link: it is judged by
    /// audience alone, never by whether a stored <see cref="Url"/> happens to be present, so a stale index
    /// can never turn an internal section into a linkable one.
    /// </summary>
    public bool IsInternal => Audience != PlatformDocsAudience.User;

    /// <summary>Stable chunk id (doc path + section ordinal); empty for hits from a collection written before chunker 3.</summary>
    public string ChunkId { get; init; } = string.Empty;

    public string DocPath { get; init; } = string.Empty;

    /// <summary>Retrieval metadata; null for hits from a collection written before chunker 3.</summary>
    public PlatformDocsChunkMeta? Meta { get; init; }

    /// <summary>Dense cosine of the query to this chunk when the dense side ranked it, else null (hybrid only).</summary>
    public float? DenseScore { get; init; }

    /// <summary>The RRF score normalised to 0..1 (1 = first in both prefetches); equals <see cref="Score"/> on the dense-only path.</summary>
    public float FusedScore { get; init; }

    /// <summary>The link a customer may follow, or <c>null</c> for an internal section.</summary>
    public string? PublicUrl => IsInternal || string.IsNullOrWhiteSpace(Url) ? null : Url;
}

public sealed record PlatformDocsSearchResult(
    PlatformDocsSearchStatus Status,
    IReadOnlyList<PlatformDocsHit> Hits,
    string? Message = null)
{
    /// <summary>
    /// True when the hits are already ordered by stage A and past the relevance floor, so the caller must not
    /// re-rank or re-filter them (the hybrid path); false for the dense-only path.
    /// </summary>
    public bool Ranked { get; init; }
}

/// <summary>Semantic search over the shipped platform documentation.</summary>
public interface IPlatformDocsSearch
{
    Task<PlatformDocsSearchResult> SearchAsync(string query, int limit, CancellationToken ct);

    /// <summary>
    /// Search with explicit filters and ranking hints. The default ignores them, which keeps every dense-only
    /// implementation (and test fake) valid.
    /// </summary>
    Task<PlatformDocsSearchResult> SearchAsync(
        string query, int limit, PlatformDocsSearchOptions options, CancellationToken ct) =>
        SearchAsync(query, limit, ct);
}

/// <summary>
/// The Qdrant-backed documentation index. One GLOBAL collection per (embedding model, corpus
/// version), named <c>platformdocs_{model-slug}_{model-hash}_{corpus-hash}</c>, so:
/// <list type="bullet">
/// <item>switching the Embedding provider's model never mixes vector spaces (same rule as
/// <see cref="EmbeddingSpace"/> for project files);</item>
/// <item>a changed document produces a new corpus hash and therefore a new collection, which is
/// built in full and only then made current — superseded <c>platformdocs_*</c> collections are
/// dropped afterwards;</item>
/// <item>"is it indexed?" is a name lookup plus a point count, with no side table.</item>
/// </list>
/// The <c>platformdocs_</c> prefix can never collide with a project collection, whose names start
/// with a 32-hex-digit project id.
/// </summary>
public sealed class QdrantPlatformDocsIndex : IPlatformDocsSearch
{
    internal const string CollectionPrefix = "platformdocs_";
    // Small on purpose: on a CPU embedding server one big batch blocks every user query queued
    // behind it, and the first index of a fresh corpus is hundreds of chunks.
    private const int EmbedBatchSize = 8;

    /// <summary>
    /// The collection last confirmed complete, or null while none is. Search answers "not indexed"
    /// immediately while this is null, instead of embedding the question and waiting behind the
    /// indexing run on the same embedding server (which made every turn burn its full timeout).
    /// </summary>
    private volatile string? _readyCollection;

    private readonly IPlatformDocsQdrant _store;
    private readonly PlatformDocsCorpusProvider _corpus;
    private readonly IInferenceProviderResolver _resolver;
    // The platform documentation lives in ONE embedding space (the platform's Embedding provider),
    // independent of whichever organization's request happens to trigger indexing or a search.
    private readonly IInferenceTenantAccessor? _tenantAccessor;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly IOptions<PlatformDocsOptions> _options;
    private readonly ILogger<QdrantPlatformDocsIndex> _logger;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly AssistantActivityMonitor? _activity;
    private readonly IPlatformDocsRerankStage? _stageB;
    private readonly QueryEmbeddingCache _queryCache;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _hybridLayout = new(StringComparer.Ordinal);

    /// <summary>Names of the two vectors of a hybrid collection.</summary>
    internal const string DenseVector = "dense";
    internal const string SparseVector = "bm25";

    /// <summary>Payload fields indexed (keyword) for filtering.</summary>
    internal static readonly string[] IndexedFields =
        ["kind", "stepTypes", "agentTypes", "capabilities", "templateKeys", "costClass", "audience"];

    /// <summary>
    /// Between embedding batches, the indexer waits at most this long while an assistant turn is in
    /// flight: that turn's documentation lookup embeds its question on the same embedding server,
    /// and a full re-embed after a deploy used to sit in front of it for minutes.
    /// </summary>
    internal static readonly TimeSpan InteractiveYield = TimeSpan.FromSeconds(15);

    public QdrantPlatformDocsIndex(
        IOptions<VectorSearchOptions> vectorOptions,
        IOptions<PlatformDocsOptions> options,
        PlatformDocsCorpusProvider corpus,
        IInferenceProviderResolver resolver,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        ILogger<QdrantPlatformDocsIndex> logger,
        AssistantActivityMonitor? activity = null,
        IInferenceTenantAccessor? tenantAccessor = null,
        IPlatformDocsRerankStage? stageB = null)
        : this(
            options, corpus, resolver, embeddings, logger,
            new QdrantPlatformDocsStore(new Lazy<QdrantClient>(() => QdrantClientFactory.Create(vectorOptions.Value))),
            activity, tenantAccessor, stageB)
    {
    }

    /// <summary>The same index over any <see cref="IPlatformDocsQdrant"/> (tests fake the server here).</summary>
    internal QdrantPlatformDocsIndex(
        IOptions<PlatformDocsOptions> options,
        PlatformDocsCorpusProvider corpus,
        IInferenceProviderResolver resolver,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        ILogger<QdrantPlatformDocsIndex> logger,
        IPlatformDocsQdrant store,
        AssistantActivityMonitor? activity = null,
        IInferenceTenantAccessor? tenantAccessor = null,
        IPlatformDocsRerankStage? stageB = null)
    {
        _store = store;
        _stageB = stageB;
        _queryCache = new QueryEmbeddingCache(options.Value.Hybrid.QueryEmbeddingCacheSize);
        _tenantAccessor = tenantAccessor;
        _activity = activity;
        _options = options;
        _corpus = corpus;
        _resolver = resolver;
        _embeddings = embeddings;
        _logger = logger;
    }

    /// <summary>The collection that holds <paramref name="corpusHash"/> embedded with <paramref name="modelId"/>.</summary>
    public static string CollectionName(string? modelId, string corpusHash)
    {
        string model = string.IsNullOrWhiteSpace(modelId) ? "unknown" : modelId.Trim();
        string modelHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model)))[..8].ToLowerInvariant();

        StringBuilder slug = new();
        foreach (char c in model.ToLowerInvariant())
        {
            slug.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
            if (slug.Length >= 40) break;
        }

        return $"{CollectionPrefix}{slug.ToString().Trim('-')}_{modelHash}_{corpusHash}";
    }

    /// <summary>
    /// The collection the current settings build: <see cref="CollectionName"/>, plus a <c>d</c> suffix for the
    /// dense-only layout, so flipping <c>PlatformDocs:Hybrid:Enabled</c> is an ordinary new-collection build (the
    /// serving collection keeps answering and its vectors are reused) rather than a delete of what is serving.
    /// </summary>
    internal string CurrentCollectionName(string? modelId) =>
        CollectionName(modelId, _corpus.Corpus.CorpusHash) + (_options.Value.Hybrid.Enabled ? "" : DenseOnlySuffix);

    internal const string DenseOnlySuffix = "d";

    /// <summary>
    /// Makes sure the current corpus is indexed under the current Embedding model. Idempotent and
    /// cheap when nothing changed (one provider lookup, one exists call, one count). Returns false
    /// when it could not (no provider yet, Qdrant down) — the caller retries later.
    /// </summary>
    public async Task<bool> EnsureIndexedAsync(CancellationToken ct)
    {
        PlatformDocsCorpus corpus = _corpus.Corpus;
        if (!_options.Value.Enabled || corpus.Chunks.Count == 0)
            return true;

        using IDisposable platformScope = _tenantAccessor?.BeginPlatformScope() ?? NoPlatformScope.Instance;

        // The docs corpus belongs to no customer: its embedding cost is the platform's own, so it is charged to
        // the platform organization with source=DocsIndex rather than to whoever happened to trigger the run (B3c).
        using IDisposable usageScope = UsageScope.Begin(
            WellKnownOrganizations.PlatformId, null, null, null, null, UsageSource.DocsIndex);

        ResolvedEmbeddingProvider? provider;
        try
        {
            provider = await _resolver.ResolveEmbeddingAsync(null, ct);
        }
        catch (ModelUnpricedException unpriced)
        {
            // Never thrown out of the background loop (that would stop the host): not indexed, retried later.
            _logger.LogWarning("Platform docs not indexed: {Reason}", unpriced.Message);
            return false;
        }

        if (provider is null)
        {
            _logger.LogInformation("Platform docs not indexed: no Embedding provider is configured yet.");
            return false;
        }

        string collection = CurrentCollectionName(provider.ModelName);

        await _indexLock.WaitAsync(ct);
        try
        {
            IPlatformDocsQdrant client = _store;
            if (await client.CollectionExistsAsync(collection, ct))
            {
                ulong count = await client.CountAsync(collection, ct);
                if (count == (ulong)corpus.Chunks.Count)
                {
                    _readyCollection = collection;
                    await DropSupersededAsync(client, collection, ct);
                    return true;
                }

                // A previous attempt died part-way: start this collection over.
                _hybridLayout.TryRemove(collection, out _);
                await client.DeleteCollectionAsync(collection, ct);
            }

            // The newest earlier collection of the SAME model: searches keep answering from it
            // while this corpus builds (slightly stale beats blind — after every deploy that
            // touched a doc, the assistant used to answer "documentation is still indexing" for as
            // long as a full CPU re-embed took), and its vectors are reused for every chunk whose
            // text did not change, so a docs edit embeds only what was edited.
            string? previous = await PreviousCollectionAsync(client, collection, ct);
            if (previous is not null && _readyCollection is null)
                _readyCollection = previous;
            Dictionary<string, float[]> reusable = previous is null
                ? new Dictionary<string, float[]>(StringComparer.Ordinal)
                : await ReusableVectorsAsync(client, previous, ct);

            List<PlatformDocsChunk> toEmbed = corpus.Chunks.Where(c => !reusable.ContainsKey(EmbedHash(c))).ToList();
            _logger.LogInformation(
                "Indexing {ChunkCount} platform-docs chunks into {Collection} ({Reused} reused from {Previous}, {Embedding} to embed)",
                corpus.Chunks.Count, collection, corpus.Chunks.Count - toEmbed.Count, previous ?? "none", toEmbed.Count);

            var vectorByHash = new Dictionary<string, float[]>(reusable, StringComparer.Ordinal);
            for (int start = 0; start < toEmbed.Count; start += EmbedBatchSize)
            {
                if (_activity is not null)
                    await _activity.YieldToInteractiveAsync(InteractiveYield, ct);

                List<PlatformDocsChunk> batch = toEmbed.Skip(start).Take(EmbedBatchSize).ToList();
                GeneratedEmbeddings<Embedding<float>> vectors = await _embeddings.GenerateAsync(
                    batch.Select(c => c.EmbeddingText), cancellationToken: ct);
                for (int i = 0; i < batch.Count; i++)
                    vectorByHash[EmbedHash(batch[i])] = vectors[i].Vector.ToArray();
            }

            int dimensions = vectorByHash.Values.First().Length;
            bool hybrid = _options.Value.Hybrid.Enabled;
            if (hybrid)
            {
                await client.CreateHybridCollectionAsync(collection, (ulong)dimensions, ct);
                foreach (string field in IndexedFields)
                    await client.CreateKeywordIndexAsync(collection, field, ct);
            }
            else
            {
                await client.CreateDenseCollectionAsync(collection, (ulong)dimensions, ct);
            }

            _hybridLayout[collection] = hybrid;

            for (int start = 0; start < corpus.Chunks.Count; start += 64)
            {
                var points = new List<PointStruct>();
                foreach (PlatformDocsChunk chunk in corpus.Chunks.Skip(start).Take(64))
                {
                    string hash = EmbedHash(chunk);
                    var point = new PointStruct { Id = PointId(chunk.Id) };
                    if (hybrid)
                    {
                        // The sparse side also sees the declared goal phrases: the user's own words, which BM25 matches best.
                        string sparseText = chunk.Meta.Goals.Count == 0
                            ? chunk.EmbeddingText
                            : chunk.EmbeddingText + "\n" + string.Join('\n', chunk.Meta.Goals);
                        point.Vectors = new Dictionary<string, Vector>
                        {
                            [DenseVector] = vectorByHash[hash],
                            [SparseVector] = PlatformDocsMetadata.SparseTermFrequencies(sparseText)
                        };
                    }
                    else
                    {
                        point.Vectors = vectorByHash[hash];
                    }

                    point.Payload["chunkId"] = chunk.Id;
                    point.Payload["docPath"] = chunk.DocPath;
                    point.Payload["audience"] = chunk.Audience.ToString();
                    point.Payload["title"] = chunk.Title;
                    point.Payload["section"] = chunk.Section;
                    point.Payload["url"] = chunk.Url;
                    point.Payload["content"] = chunk.Text;
                    point.Payload["embedHash"] = hash;
                    AddMetaPayload(point, chunk.Meta);
                    points.Add(point);
                }

                await client.UpsertAsync(collection, points, ct);
            }

            _readyCollection = collection;
            await DropSupersededAsync(client, collection, ct);
            _logger.LogInformation("Platform docs indexed into {Collection}", collection);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Platform docs indexing failed; will retry");
            return false;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public Task<PlatformDocsSearchResult> SearchAsync(string query, int limit, CancellationToken ct) =>
        SearchAsync(query, limit, new PlatformDocsSearchOptions(), ct);

    public async Task<PlatformDocsSearchResult> SearchAsync(
        string query, int limit, PlatformDocsSearchOptions options, CancellationToken ct)
    {
        if (!_options.Value.Enabled || _corpus.Corpus.Chunks.Count == 0)
            return new(PlatformDocsSearchStatus.NotIndexed, [], "Documentation search is not available in this deployment.");

        if (string.IsNullOrWhiteSpace(query))
            return new(PlatformDocsSearchStatus.Ok, []);

        if (_readyCollection is null)
            return new(PlatformDocsSearchStatus.NotIndexed, [], "The documentation is still being indexed; try again in a few minutes.");

        // The calling organization, read BEFORE the platform scope hides it: it scopes the query cache so a hit
        // never reveals that another organization asked the same text.
        Guid? callerOrg = _tenantAccessor?.OrganizationId;

        // Query vectors must come from the platform's embedding model (the one the corpus was indexed
        // with), never the calling organization's own provider.
        using IDisposable platformScope = _tenantAccessor?.BeginPlatformScope() ?? NoPlatformScope.Instance;

        // The same question is embedded more than once per turn (injection, then a tool call): keep the
        // vector. Keyed on the text and the model of the ready collection (its name encodes the model).
        string readyModel = _readyCollection[..(_readyCollection.LastIndexOf('_') + 1)];
        string cacheKey = QueryEmbeddingCache.Key(query, readyModel, callerOrg);
        Embedding<float> embedding;
        try
        {
            if (_queryCache.TryGet(cacheKey, out Embedding<float>? cached) && cached is not null)
            {
                embedding = cached;
            }
            else
            {
                embedding = await _embeddings.GenerateAsync(query, cancellationToken: ct);
                _queryCache.Set(cacheKey, embedding);
            }
        }
        catch (EmbeddingProviderUnavailableException ex)
        {
            return new(PlatformDocsSearchStatus.Unavailable, [], ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Embedding a platform-docs query failed");
            return new(PlatformDocsSearchStatus.Unavailable, [], "The embedding provider could not embed the question.");
        }

        // Whatever collection is ready — the current corpus, or the previous one while the current
        // builds — as long as it was embedded with this same model.
        string? collection = _readyCollection;
        if (collection is null || !collection.StartsWith(ModelPrefix(embedding.ModelId), StringComparison.Ordinal))
            collection = CurrentCollectionName(embedding.ModelId);
        try
        {
            IPlatformDocsQdrant client = _store;
            if (!await client.CollectionExistsAsync(collection, ct))
                return new(PlatformDocsSearchStatus.NotIndexed, [], "The documentation is still being indexed; try again in a few minutes.");

            PlatformDocsHybridOptions hybrid = _options.Value.Hybrid;
            bool hybridLayout = await IsHybridLayoutAsync(client, collection, ct);
            Filter? must = PlatformDocsQueryBuilder.MustFilter(options);

            // A collection from before chunker 3 (unnamed vector, still serving while the new one builds) carries no
            // metadata: a filtered call would return zero hits, which the model reads as "not documented".
            if (must is not null && !hybridLayout && collection != CurrentCollectionName(embedding.ModelId))
                return new(PlatformDocsSearchStatus.NotIndexed, [],
                    "The documentation is being re-indexed and filtered search is not available yet; search without filters or try again in a few minutes.");

            if (!hybrid.Enabled || !hybridLayout)
            {
                // Dense-only: today's single search call (a collection written before the hybrid switch
                // is unnamed; one written with it is addressed by its dense vector name).
                IReadOnlyList<ScoredPoint> dense = await client.SearchDenseAsync(
                    collection, embedding.Vector, must,
                    (ulong)Math.Clamp(limit, 1, 20),
                    hybridLayout ? DenseVector : null, ct);

                return new(PlatformDocsSearchStatus.Ok, dense.Select(p => MapPayload(p.Payload, p.Score)).ToList());
            }

            float[] vector = embedding.Vector.ToArray();
            QueryPoints fused = PlatformDocsQueryBuilder.Hybrid(collection, vector, query, must, hybrid);
            QueryPoints denseOnly = PlatformDocsQueryBuilder.DenseCosine(collection, vector, must, hybrid);
            IReadOnlyList<BatchResult> batch = await client.QueryBatchAsync(collection, [fused, denseOnly], ct);

            var cosine = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (ScoredPoint p in batch[1].Result)
                cosine[PointKey(p.Id)] = p.Score;

            float maxRrf = 2f / (hybrid.RrfK + 1);
            List<PlatformDocsHit> candidates = batch[0].Result.Select(p =>
            {
                float? dense = cosine.TryGetValue(PointKey(p.Id), out float c) ? c : null;
                float fusedScore = Math.Min(1f, p.Score / maxRrf);
                return MapPayload(p.Payload, dense ?? fusedScore) with { DenseScore = dense, FusedScore = fusedScore };
            }).ToList();

            int want = Math.Clamp(limit, 1, 20);
            List<PlatformDocsHit> result = (await RankAsync(candidates, query, options, hybrid, want, _stageB, _logger, ct)).ToList();
            await AddNeighboursAsync(client, collection, result, want, ct);

            return new(PlatformDocsSearchStatus.Ok, result) { Ranked = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Platform docs search failed");
            return new(PlatformDocsSearchStatus.Unavailable, [], "The documentation index could not be reached.");
        }
    }

    /// <summary>
    /// The ranking tail of a hybrid search: stage A over the fused candidates, then stage B (a model rerank) when one is
    /// registered, then the final top <paramref name="want"/>. Shared by the injection and the tool path (both go through
    /// <see cref="SearchAsync(string, int, PlatformDocsSearchOptions, CancellationToken)"/>). Any stage-B failure keeps stage A's order.
    /// </summary>
    internal static async Task<IReadOnlyList<PlatformDocsHit>> RankAsync(
        IReadOnlyList<PlatformDocsHit> candidates, string query, PlatformDocsSearchOptions options,
        PlatformDocsHybridOptions hybrid, int want, IPlatformDocsRerankStage? stageB, ILogger logger, CancellationToken ct)
    {
        IReadOnlyList<PlatformDocsHit> ordered = PlatformDocsStageA.Apply(
            candidates, options, hybrid.MaxChunksPerDoc, Math.Max(want, hybrid.FuseLimit));
        ordered = await ApplyStageBAsync(stageB, logger, query, ordered, ct);
        return ordered.Take(want).ToList();
    }

    /// <summary>Stage B (a model rerank), when one is registered; any failure keeps stage A's order.</summary>
    private static async Task<IReadOnlyList<PlatformDocsHit>> ApplyStageBAsync(
        IPlatformDocsRerankStage? stageB, ILogger logger, string query, IReadOnlyList<PlatformDocsHit> stageA, CancellationToken ct)
    {
        if (stageB is null || stageA.Count < 2)
            return stageA;

        try
        {
            return await stageB.RerankAsync(query, stageA, ct) ?? stageA;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Platform docs stage-B rerank failed; keeping the deterministic order");
            return stageA;
        }
    }

    /// <summary>
    /// When the top hit is one piece of a section that was split for length, puts the adjacent pieces of the SAME
    /// section right behind it (so the consumer, which keeps the head of the list, gets the section whole) and trims
    /// the tail back to <paramref name="budget"/>.
    /// </summary>
    private async Task AddNeighboursAsync(
        IPlatformDocsQdrant client, string collection, List<PlatformDocsHit> hits, int budget, CancellationToken ct)
    {
        if (hits.Count == 0 || hits[0].Meta is not { } meta)
            return;

        var wanted = new List<string>();
        foreach (string? id in new[] { meta.PrevId, meta.NextId })
        {
            if (!string.IsNullOrEmpty(id))
                wanted.Add(id);
        }

        if (wanted.Count == 0)
            return;

        IReadOnlyList<RetrievedPoint> points = await client.RetrieveAsync(
            collection, wanted.Select(id => (PointId)PointId(id)).ToList(), ct);
        var neighbours = points
            .Select(p => MapPayload(p.Payload, hits[0].Score) with { FusedScore = hits[0].FusedScore })
            .Where(n => n.ChunkId.Length > 0 && n.DocPath == hits[0].DocPath && n.Section == hits[0].Section)
            .OrderBy(n => n.ChunkId, StringComparer.Ordinal)
            .ToList();
        if (neighbours.Count == 0)
            return;

        hits.RemoveAll(h => neighbours.Any(n => n.ChunkId == h.ChunkId));
        hits.InsertRange(1, neighbours);
        if (hits.Count > budget)
            hits.RemoveRange(budget, hits.Count - budget);
    }

    /// <summary>True when <paramref name="collection"/> has the named dense vector (the hybrid layout). Cached per name.</summary>
    internal async Task<bool> IsHybridLayoutAsync(IPlatformDocsQdrant client, string collection, CancellationToken ct)
    {
        if (_hybridLayout.TryGetValue(collection, out bool known))
            return known;

        bool hybrid = await client.HasNamedDenseAsync(collection, ct);
        _hybridLayout[collection] = hybrid;
        return hybrid;
    }

    private static string PointKey(PointId id) => id.HasUuid ? id.Uuid : id.Num.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void AddMetaPayload(PointStruct point, PlatformDocsChunkMeta meta)
    {
        point.Payload["kind"] = meta.Kind;
        point.Payload["stepTypes"] = List(meta.StepTypes);
        point.Payload["agentTypes"] = List(meta.AgentTypes);
        point.Payload["capabilities"] = List(meta.Capabilities);
        point.Payload["templateKeys"] = List(meta.TemplateKeys);
        point.Payload["goals"] = List(meta.Goals);
        if (meta.CostClass is not null)
            point.Payload["costClass"] = meta.CostClass;
        point.Payload["locale"] = meta.Locale;
        point.Payload["headingPath"] = meta.HeadingPath;
        if (meta.PrevId is not null)
            point.Payload["prevId"] = meta.PrevId;
        if (meta.NextId is not null)
            point.Payload["nextId"] = meta.NextId;
        point.Payload["docVersion"] = meta.DocVersion;
    }

    private static Value List(IEnumerable<string> values)
    {
        var list = new ListValue();
        foreach (string v in values)
            list.Values.Add(new Value { StringValue = v });
        return new Value { ListValue = list };
    }

    /// <summary>The collection-name prefix shared by every corpus version embedded with <paramref name="modelId"/>.</summary>
    internal static string ModelPrefix(string? modelId)
    {
        string full = CollectionName(modelId, "x");
        return full[..^1];
    }

    /// <summary>A hash of exactly what gets embedded for a chunk — equal hashes, equal vectors.</summary>
    internal static string EmbedHash(PlatformDocsChunk chunk) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chunk.EmbeddingText)))[..32];

    /// <summary>The most complete earlier collection embedded with the same model as <paramref name="current"/>, if any.</summary>
    private async Task<string?> PreviousCollectionAsync(IPlatformDocsQdrant client, string current, CancellationToken ct)
    {
        string prefix = current[..(current.LastIndexOf('_') + 1)];
        string? best = null;
        ulong bestCount = 0;
        foreach (string name in await client.ListCollectionsAsync(ct))
        {
            if (name == current || !name.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            ulong count = await client.CountAsync(name, ct);
            if (count > bestCount)
            {
                best = name;
                bestCount = count;
            }
        }

        return best;
    }

    /// <summary>Vectors of an earlier collection keyed by <see cref="EmbedHash"/>; empty for collections written before the hash was stored.</summary>
    private async Task<Dictionary<string, float[]>> ReusableVectorsAsync(IPlatformDocsQdrant client, string collection, CancellationToken ct)
    {
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        try
        {
            PointId? offset = null;
            do
            {
                ScrollResponse page = await client.ScrollHashesAsync(collection, offset, ct);
                foreach (RetrievedPoint point in page.Result)
                {
                    if (point.Payload.TryGetValue("embedHash", out Value? hash) && hash.HasStringValue)
                    {
                        // Hybrid collections keep the dense vector under a name, older ones as the single vector.
                        VectorOutput? vector = point.Vectors?.Vectors?.Vectors.TryGetValue(DenseVector, out VectorOutput? named) == true
                            ? named
                            : point.Vectors?.Vector;
                        if (vector is null)
                            continue;
                        float[] data = vector.Dense is { } dense && dense.Data.Count > 0 ? dense.Data.ToArray() : vector.Data.ToArray();
                        if (data.Length > 0)
                            vectors[hash.StringValue] = data;
                    }
                }

                offset = page.NextPageOffset;
            } while (offset is not null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Reuse is an optimisation: any failure just means embedding everything.
            _logger.LogWarning(ex, "Could not read reusable platform-docs vectors from {Collection}; embedding everything", collection);
            vectors.Clear();
        }

        return vectors;
    }

    private async Task DropSupersededAsync(IPlatformDocsQdrant client, string current, CancellationToken ct)
    {
        foreach (string name in await client.ListCollectionsAsync(ct))
        {
            if (name.StartsWith(CollectionPrefix, StringComparison.Ordinal) && name != current)
            {
                await client.DeleteCollectionAsync(name, ct);
                _logger.LogInformation("Dropped superseded platform-docs collection {Collection}", name);
            }
        }
    }

    /// <summary>Deterministic point id from the chunk id, so a re-index of the same corpus is stable.</summary>
    internal static Guid PointId(string chunkId) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes(chunkId)));

    internal static PlatformDocsHit MapPayload(IDictionary<string, Value> payload, float score)
    {
        string Get(string key) =>
            payload.TryGetValue(key, out Value? v) && v.HasStringValue ? v.StringValue : string.Empty;

        IReadOnlyList<string> GetList(string key) =>
            payload.TryGetValue(key, out Value? v) && v.KindCase == Value.KindOneofCase.ListValue
                ? v.ListValue.Values.Where(x => x.HasStringValue).Select(x => x.StringValue).ToList()
                : [];

        string? Opt(string key) => Get(key) is { Length: > 0 } x ? x : null;

        // Chunks written before chunker 3 carry no metadata at all.
        PlatformDocsChunkMeta? meta = payload.ContainsKey("kind")
            ? new PlatformDocsChunkMeta
            {
                Kind = Get("kind"),
                StepTypes = GetList("stepTypes"),
                AgentTypes = GetList("agentTypes"),
                Capabilities = GetList("capabilities"),
                TemplateKeys = GetList("templateKeys"),
                Goals = GetList("goals"),
                CostClass = Opt("costClass"),
                Locale = Get("locale"),
                HeadingPath = Get("headingPath"),
                PrevId = Opt("prevId"),
                NextId = Opt("nextId"),
                DocVersion = Get("docVersion")
            }
            : null;

        return new PlatformDocsHit(
            Get("title"),
            Get("section"),
            Get("url"),
            Enum.TryParse(Get("audience"), out PlatformDocsAudience a) ? a : PlatformDocsAudience.Developer,
            Get("content"),
            score)
        {
            DocPath = Get("docPath"),
            ChunkId = Get("chunkId"),
            Meta = meta,
            FusedScore = score
        };
    }
}

/// <summary>Loads the corpus once per process (the Markdown is baked into the image).</summary>
public sealed class PlatformDocsCorpusProvider
{
    private readonly Lazy<PlatformDocsCorpus> _corpus;

    public PlatformDocsCorpusProvider(IOptions<PlatformDocsOptions> options, ILogger<PlatformDocsCorpusProvider> logger)
    {
        _corpus = new Lazy<PlatformDocsCorpus>(() =>
        {
            PlatformDocsOptions o = options.Value;
            PlatformDocsCorpus corpus = PlatformDocsCorpus.Load(o.Path, o.MaxChunkChars, includeGeneratedFacts: true);
            if (corpus.Chunks.Count == 0)
                logger.LogWarning("No platform documentation found under {Path}; documentation search is disabled", o.Path);
            else
                logger.LogInformation(
                    "Loaded {ChunkCount} platform-docs chunks from {Path} (corpus {Hash})",
                    corpus.Chunks.Count, o.Path, corpus.CorpusHash);
            return corpus;
        });
    }

    /// <summary>For tests: a provider over a fixed corpus.</summary>
    public PlatformDocsCorpusProvider(PlatformDocsCorpus corpus) =>
        _corpus = new Lazy<PlatformDocsCorpus>(() => corpus);

    public PlatformDocsCorpus Corpus => _corpus.Value;
}

/// <summary>
/// Keeps the documentation index current: indexes shortly after startup, then re-checks on an
/// interval so adding (or switching) an Embedding provider at runtime is picked up without a restart.
/// </summary>
public sealed class PlatformDocsIndexingService : BackgroundService
{
    private readonly QdrantPlatformDocsIndex _index;
    private readonly IOptions<PlatformDocsOptions> _options;

    public PlatformDocsIndexingService(QdrantPlatformDocsIndex index, IOptions<PlatformDocsOptions> options)
    {
        _index = index;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
            return;

        // Deliberately NO tenant scope: with neither an organization nor a system scope the tenant
        // filter on inference_providers yields platform-managed rows only, which is exactly what the
        // global documentation index may use. A system scope would also expose BYO rows (A9a makes
        // this an explicit platform scope in the resolver).

        // Let migrations, seeding and the provider cache settle first.
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            bool ok = await _index.EnsureIndexedAsync(stoppingToken);
            int seconds = Math.Max(30, _options.Value.RecheckIntervalSeconds);
            await Task.Delay(TimeSpan.FromSeconds(ok ? seconds : Math.Min(seconds, 120)), stoppingToken);
        }
    }
}

internal sealed class NoPlatformScope : IDisposable
{
    public static readonly NoPlatformScope Instance = new();
    public void Dispose() { }
}
