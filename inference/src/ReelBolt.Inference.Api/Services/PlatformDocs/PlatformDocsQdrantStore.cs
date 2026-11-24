using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace ReelBolt.Inference.Api.Services.PlatformDocs;

/// <summary>
/// The slice of Qdrant the documentation index uses. A seam so tests can drive the real build and search paths
/// of <see cref="QdrantPlatformDocsIndex"/> and assert the exact requests, with no server.
/// </summary>
internal interface IPlatformDocsQdrant
{
    Task<bool> CollectionExistsAsync(string name, CancellationToken ct);
    Task<ulong> CountAsync(string name, CancellationToken ct);
    Task DeleteCollectionAsync(string name, CancellationToken ct);
    Task<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken ct);

    /// <summary>The single unnamed cosine vector collection (the dense-only layout).</summary>
    Task CreateDenseCollectionAsync(string name, ulong dimensions, CancellationToken ct);

    /// <summary>Named <c>dense</c> cosine vector plus <c>bm25</c> sparse (<c>Modifier.Idf</c>).</summary>
    Task CreateHybridCollectionAsync(string name, ulong dimensions, CancellationToken ct);

    Task CreateKeywordIndexAsync(string name, string field, CancellationToken ct);
    Task UpsertAsync(string name, IReadOnlyList<PointStruct> points, CancellationToken ct);

    Task<IReadOnlyList<ScoredPoint>> SearchDenseAsync(
        string name, ReadOnlyMemory<float> vector, Filter? filter, ulong limit, string? vectorName, CancellationToken ct);

    Task<IReadOnlyList<BatchResult>> QueryBatchAsync(string name, IReadOnlyList<QueryPoints> queries, CancellationToken ct);
    Task<IReadOnlyList<RetrievedPoint>> RetrieveAsync(string name, IReadOnlyList<PointId> ids, CancellationToken ct);

    /// <summary>True when the collection has the named <c>dense</c> vector (the hybrid layout).</summary>
    Task<bool> HasNamedDenseAsync(string name, CancellationToken ct);

    /// <summary>One page of (embedHash, vectors) for reuse.</summary>
    Task<ScrollResponse> ScrollHashesAsync(string name, PointId? offset, CancellationToken ct);
}

internal sealed class QdrantPlatformDocsStore(Lazy<QdrantClient> lazy) : IPlatformDocsQdrant
{
    private QdrantClient C => lazy.Value;

    public Task<bool> CollectionExistsAsync(string name, CancellationToken ct) => C.CollectionExistsAsync(name, ct);

    public Task<ulong> CountAsync(string name, CancellationToken ct) => C.CountAsync(name, exact: true, cancellationToken: ct);

    public Task DeleteCollectionAsync(string name, CancellationToken ct) => C.DeleteCollectionAsync(name, cancellationToken: ct);

    public async Task<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken ct) => (await C.ListCollectionsAsync(ct)).ToList();

    public Task CreateDenseCollectionAsync(string name, ulong dimensions, CancellationToken ct) =>
        C.CreateCollectionAsync(name, new VectorParams { Size = dimensions, Distance = Distance.Cosine }, cancellationToken: ct);

    public Task CreateHybridCollectionAsync(string name, ulong dimensions, CancellationToken ct) =>
        C.CreateCollectionAsync(
            name,
            new VectorParamsMap
            {
                Map = { [QdrantPlatformDocsIndex.DenseVector] = new VectorParams { Size = dimensions, Distance = Distance.Cosine } }
            },
            sparseVectorsConfig: new SparseVectorConfig
            {
                Map = { [QdrantPlatformDocsIndex.SparseVector] = new SparseVectorParams { Modifier = Modifier.Idf } }
            },
            cancellationToken: ct);

    public Task CreateKeywordIndexAsync(string name, string field, CancellationToken ct) =>
        C.CreatePayloadIndexAsync(name, field, PayloadSchemaType.Keyword, wait: true, cancellationToken: ct);

    public Task UpsertAsync(string name, IReadOnlyList<PointStruct> points, CancellationToken ct) =>
        C.UpsertAsync(name, points, cancellationToken: ct);

    public Task<IReadOnlyList<ScoredPoint>> SearchDenseAsync(
        string name, ReadOnlyMemory<float> vector, Filter? filter, ulong limit, string? vectorName, CancellationToken ct) =>
        C.SearchAsync(name, vector, filter: filter, limit: limit, vectorName: vectorName, cancellationToken: ct);

    public Task<IReadOnlyList<BatchResult>> QueryBatchAsync(string name, IReadOnlyList<QueryPoints> queries, CancellationToken ct) =>
        C.QueryBatchAsync(name, queries, cancellationToken: ct);

    public Task<IReadOnlyList<RetrievedPoint>> RetrieveAsync(string name, IReadOnlyList<PointId> ids, CancellationToken ct) =>
        C.RetrieveAsync(name, ids, new WithPayloadSelector { Enable = true }, new WithVectorsSelector { Enable = false }, cancellationToken: ct);

    public async Task<bool> HasNamedDenseAsync(string name, CancellationToken ct)
    {
        CollectionInfo info = await C.GetCollectionInfoAsync(name, ct);
        return info.Config?.Params?.VectorsConfig?.ParamsMap?.Map.ContainsKey(QdrantPlatformDocsIndex.DenseVector) == true;
    }

    public Task<ScrollResponse> ScrollHashesAsync(string name, PointId? offset, CancellationToken ct) =>
        C.ScrollAsync(
            name, limit: 256, offset: offset,
            payloadSelector: new WithPayloadSelector { Include = new PayloadIncludeSelector { Fields = { "embedHash" } } },
            vectorsSelector: true, cancellationToken: ct);
}
