namespace ReelBolt.Inference.Api.Services.PlatformDocs;

/// <summary>
/// Configuration for the platform documentation index the assistant searches (section
/// <c>PlatformDocs</c>). See docs/platform-docs-search.md.
/// </summary>
public sealed class PlatformDocsOptions
{
    public const string SectionName = "PlatformDocs";

    /// <summary>Master switch for indexing, the search tool and per-question injection.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Root of the shipped Markdown tree — the repository's <c>/docs</c> folder, copied into the image
    /// through the <c>platform-docs</c> named build context.
    /// </summary>
    public string Path { get; set; } = "/app/platform-docs";

    /// <summary>
    /// How often the background indexer re-checks that the index matches the current corpus and
    /// embedding model (and retries after a failure, e.g. no Embedding provider configured yet).
    /// </summary>
    public int RecheckIntervalSeconds { get; set; } = 600;

    /// <summary>Upper bound on one chunk's text; a longer section is split at paragraph breaks.</summary>
    public int MaxChunkChars { get; set; } = 2400;

    /// <summary>How many chunks are injected into the assistant's prompt for each user turn (0 disables injection).</summary>
    public int InjectTopK { get; set; } = 4;

    /// <summary>Character budget for the injected documentation block.</summary>
    public int InjectMaxChars { get; set; } = 7000;

    /// <summary>Hard wall-clock bound on the per-turn retrieval; on expiry the turn simply runs without it.</summary>
    public int InjectTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Hard wall-clock bound on one <c>SearchPlatformDocs</c> tool call (same semantics as
    /// <see cref="InjectTimeoutSeconds"/>, a little more generous because the model asked for it
    /// explicitly). On expiry the tool answers "documentation unavailable, answer from what you know"
    /// instead of holding the turn while the embedding server is busy re-indexing.
    /// </summary>
    public int SearchTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Minimum cosine similarity for a chunk to be injected. The search TOOL ignores it (the model
    /// can judge relevance of what it asked for); injection uses it so an off-topic turn ("thanks!")
    /// does not drag unrelated pages into the prompt.
    /// </summary>
    public float InjectMinScore { get; set; } = 0.35f;

    /// <summary>Hybrid (dense + sparse, RRF) retrieval settings (<c>PlatformDocs:Hybrid</c>).</summary>
    public PlatformDocsHybridOptions Hybrid { get; set; } = new();

    /// <summary>Stage B of the retrieval: the paid model rerank (section <c>PlatformDocs:Rerank</c>).</summary>
    public PlatformDocsRerankOptions Rerank { get; set; } = new();
}

/// <summary>
/// Hybrid retrieval over the documentation index (docs/design/workflow-apps.md 7.4). With
/// <see cref="Enabled"/> false the index is built and searched exactly as before (one cosine vector, one search call).
/// </summary>
public sealed class PlatformDocsHybridOptions
{
    /// <summary>Dense + sparse prefetch fused by RRF, then the deterministic stage-A rerank. False = dense-only, as before.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Candidates fetched by each of the dense and the sparse prefetch.</summary>
    public int PrefetchLimit { get; set; } = 30;

    /// <summary>Candidates kept after RRF fusion and handed to the rerank stages.</summary>
    public int FuseLimit { get; set; } = 20;

    /// <summary>The RRF constant k.</summary>
    public int RrfK { get; set; } = 60;

    /// <summary>Stage A keeps at most this many chunks of one document.</summary>
    public int MaxChunksPerDoc { get; set; } = 2;

    /// <summary>Capacity of the query-embedding LRU (keyed on query text and embedding model).</summary>
    public int QueryEmbeddingCacheSize { get; set; } = 256;
}

/// <summary>
/// Configuration of the model rerank stage (section <c>PlatformDocs:Rerank</c>). Disabled, or with no
/// <c>Rerank</c> provider, the search keeps the deterministic stage-A order byte for byte.
/// </summary>
public sealed class PlatformDocsRerankOptions
{
    /// <summary>Master switch of the stage. On by default: with no Rerank provider configured it does nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many of the top stage-A candidates are scored (capped at 20 by <c>RerankLimits.MaxCandidates</c>).</summary>
    public int Candidates { get; set; } = 20;

    /// <summary>Wall-clock bound of the rerank call; on expiry stage A's order is used.</summary>
    public int TimeoutSeconds { get; set; } = 3;
}
