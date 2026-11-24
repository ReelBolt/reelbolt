using Qdrant.Client.Grpc;

namespace ReelBolt.Inference.Api.Services.PlatformDocs;

/// <summary>
/// Builds the Qdrant Query API requests of the hybrid pipeline (docs/design/workflow-apps.md 7.4). Pure, so the
/// request shape is asserted in tests without a server.
/// </summary>
internal static class PlatformDocsQueryBuilder
{
    /// <summary>Payload fields returned with the fused candidates (never <c>embedHash</c>).</summary>
    internal static readonly string[] PayloadFields =
    [
        "chunkId", "docPath", "audience", "title", "section", "url", "content", "kind", "stepTypes", "agentTypes",
        "capabilities", "templateKeys", "goals", "costClass", "locale", "headingPath", "prevId", "nextId", "docVersion"
    ];

    /// <summary>
    /// The <c>must</c> filter of the explicit tool arguments, or null when there are none. Hints are never
    /// part of it: they re-order in stage A and so can never hide a result.
    /// </summary>
    internal static Filter? MustFilter(PlatformDocsSearchOptions options)
    {
        if (!options.HasMustFilter)
            return null;

        var filter = new Filter();
        if (options.StepType is { } step)
            filter.Must.Add(Conditions.MatchKeyword("stepTypes", step));
        if (options.Kind is { } kind)
            filter.Must.Add(Conditions.MatchKeyword("kind", kind));
        if (options.Capability is { } capability)
            filter.Must.Add(Conditions.MatchKeyword("capabilities", capability));
        if (options.CostClass is { } cost)
            filter.Must.Add(Conditions.MatchKeyword("costClass", cost));
        return filter;
    }

    /// <summary>Two prefetches (dense and sparse, same filter) fused by RRF, selecting only the payload fields used.</summary>
    internal static QueryPoints Hybrid(
        string collection, float[] dense, string queryText, Filter? must, PlatformDocsHybridOptions o)
    {
        (float[] values, uint[] indices) = PlatformDocsMetadata.SparseQueryTerms(queryText);
        var request = new QueryPoints
        {
            CollectionName = collection,
            Query = new Query { Rrf = new Rrf { K = (uint)o.RrfK } },
            Limit = (ulong)o.FuseLimit,
            WithPayload = new WithPayloadSelector
            {
                Include = new PayloadIncludeSelector { Fields = { PayloadFields } }
            }
        };

        request.Prefetch.Add(Prefetch(PlatformDocsHybridVector.DenseQuery(dense), QdrantPlatformDocsIndex.DenseVector, must, o.PrefetchLimit));
        request.Prefetch.Add(Prefetch(PlatformDocsHybridVector.SparseQuery(values, indices), QdrantPlatformDocsIndex.SparseVector, must, o.PrefetchLimit));
        return request;
    }

    /// <summary>The plain dense search (no payload): its cosines are what the injection floor is judged on.</summary>
    internal static QueryPoints DenseCosine(string collection, float[] dense, Filter? must, PlatformDocsHybridOptions o)
    {
        var request = new QueryPoints
        {
            CollectionName = collection,
            Query = PlatformDocsHybridVector.DenseQuery(dense),
            Using = QdrantPlatformDocsIndex.DenseVector,
            Limit = (ulong)o.PrefetchLimit,
            WithPayload = new WithPayloadSelector { Enable = false }
        };
        if (must is not null)
            request.Filter = must;
        return request;
    }

    private static PrefetchQuery Prefetch(Query query, string vectorName, Filter? must, int limit)
    {
        var prefetch = new PrefetchQuery { Query = query, Using = vectorName, Limit = (ulong)limit };
        if (must is not null)
            prefetch.Filter = must;
        return prefetch;
    }
}

internal static class PlatformDocsHybridVector
{
    internal static Query DenseQuery(float[] dense) =>
        new() { Nearest = new VectorInput { Dense = new DenseVector { Data = { dense } } } };

    internal static Query SparseQuery(float[] values, uint[] indices) =>
        new() { Nearest = new VectorInput { Sparse = new SparseVector { Values = { values }, Indices = { indices } } } };
}
