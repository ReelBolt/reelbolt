using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Services.PlatformDocs;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.PlatformDocs.RetrievalEval;

/// <summary>Runs only when REELBOLT_RETRIEVAL_EVAL=1 (see docs/platform-docs-search.md, "Retrieval eval").</summary>
public sealed class RetrievalEvalFactAttribute : FactAttribute
{
    public RetrievalEvalFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("REELBOLT_RETRIEVAL_EVAL") != "1")
            Skip = "Opt-in: set REELBOLT_RETRIEVAL_EVAL=1 with the local Qdrant, embeddings and reranker services up.";
    }
}

/// <summary>
/// The retrieval eval harness (docs/design/workflow-apps.md 7.5): builds the documentation index from the working tree
/// against a local Qdrant and a local OpenAI-compatible embeddings endpoint, then scores three configurations over the gold
/// set: baseline (dense only, the old behaviour), hybrid stage A, and hybrid stage A plus the model rerank (stage B) against a
/// local TEI reranker. Writes eval-results.json and eval-report.md. Never calls a paid provider.
/// </summary>
public sealed class RetrievalEvalRun(ITestOutputHelper output)
{
    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "design", "workflow-apps", "retrieval-eval.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find docs/design/workflow-apps/retrieval-eval.json above the test output.");
    }

    [RetrievalEvalFact]
    public async Task Scores_baseline_hybrid_and_rerank_over_the_gold_set()
    {
        string root = Env("REELBOLT_RETRIEVAL_EVAL_REPO", FindRepoRoot());
        string docs = Path.Combine(root, "docs");
        string outDir = Env("REELBOLT_RETRIEVAL_EVAL_OUT", Path.Combine(Path.GetTempPath(), "retrieval-eval"));
        Directory.CreateDirectory(outDir);
        string qdrantUrl = Local("REELBOLT_EVAL_QDRANT_URL", Env("REELBOLT_EVAL_QDRANT_URL", "http://localhost:16333"));
        int grpc = int.Parse(Env("REELBOLT_EVAL_QDRANT_GRPC_PORT", "16334"));
        string embUrl = Local("REELBOLT_EVAL_EMBEDDINGS_URL", Env("REELBOLT_EVAL_EMBEDDINGS_URL", "http://localhost:19003"));
        string embModel = Env("REELBOLT_EVAL_EMBEDDINGS_MODEL", "Qwen/Qwen3-Embedding-0.6B");
        string rerankUrl = Local("REELBOLT_EVAL_RERANKER_URL", Env("REELBOLT_EVAL_RERANKER_URL", "http://localhost:19004"));
        string rerankModel = Env("REELBOLT_EVAL_RERANKER_MODEL", "BAAI/bge-reranker-v2-m3");
        int candidates = int.Parse(Env("REELBOLT_EVAL_RERANK_CANDIDATES", "20"));

        PlatformDocsOptions template = new();
        PlatformDocsCorpus full = PlatformDocsCorpus.Load(docs, template.MaxChunkChars);
        EvalSet all = EvalSet.Load(Path.Combine(docs, "design", "workflow-apps", "retrieval-eval.json"));
        int maxChunks = int.Parse(Env("REELBOLT_EVAL_MAX_CHUNKS", "800"));
        int budgetMinutes = int.Parse(Env("REELBOLT_EVAL_BUDGET_MINUTES", "45"));
        string cacheDir = Env("REELBOLT_EVAL_EMBED_CACHE", Path.Combine(Path.GetTempPath(), "retrieval-eval-embed-cache"));
        using CancellationTokenSource budget = new(TimeSpan.FromMinutes(budgetMinutes));
        PlatformDocsCorpus corpus = Subset(full, all, maxChunks, int.Parse(Env("REELBOLT_EVAL_SEED", "13")));
        output.WriteLine($"corpus subset: {corpus.Chunks.Count} of {full.Chunks.Count} chunks; budget {budgetMinutes} min");
        HashSet<string> corpusDocs = [.. corpus.Chunks.Select(c => c.DocPath)];
        HashSet<string> corpusSections = [.. corpus.Chunks.Select(Key)];
        EvalSet set = all;
        List<(EvalQuery Q, IReadOnlySet<string> Gold)> active = [];
        int pending = 0;
        foreach (EvalQuery q in set.Queries)
        {
            IReadOnlySet<string> gold = q.ActiveGold(corpusSections, corpusDocs);
            if (gold.Count == 0) pending++;
            else active.Add((q, gold));
        }

        output.WriteLine($"corpus chunks: {corpus.Chunks.Count}; scored queries: {active.Count}; pending-kb: {pending}");

        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveEmbeddingAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedEmbeddingProvider(null, "eval-embeddings", default, embUrl, embModel, "", 600));
        resolver.Setup(r => r.ResolveRerankAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedRerankProvider(null, "eval-reranker", default, rerankUrl, rerankModel, "", 30));
        var usage = new Mock<IUsageRecorder>();
        usage.Setup(u => u.RecordAsync(It.IsAny<UsageRecordRequest>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<long>(0));

        using var embeddings = new HttpEmbeddingGenerator(embUrl, embModel, cacheDir, m => output.WriteLine(m));
        var vector = Options.Create(new VectorSearchOptions { QdrantUrl = qdrantUrl, QdrantGrpcPort = grpc });
        PlatformDocsCorpusProvider provider = new(corpus);

        QdrantPlatformDocsIndex Make(bool hybrid, bool stageB, out PlatformDocsOptions options)
        {
            options = new PlatformDocsOptions
            {
                Hybrid = new PlatformDocsHybridOptions { Enabled = hybrid },
                Rerank = new PlatformDocsRerankOptions { Enabled = stageB, Candidates = candidates, TimeoutSeconds = 30 }
            };
            IOptions<PlatformDocsOptions> wrapped = Options.Create(options);
            PlatformDocsRerankStage? stage = stageB
                ? new PlatformDocsRerankStage(resolver.Object, new RerankClientFactory(), usage.Object, wrapped, NullLogger<PlatformDocsRerankStage>.Instance)
                : null;
            return new QdrantPlatformDocsIndex(vector, wrapped, provider, resolver.Object, embeddings,
                NullLogger<QdrantPlatformDocsIndex>.Instance, stageB: stage);
        }

        List<ConfigResult> results = [];
        (string Name, string Description, bool Hybrid, bool StageB)[] plan =
        [
            (RetrievalEvalGates.Baseline, "PlatformDocs:Hybrid:Enabled=false (dense cosine only, the old behaviour)", false, false),
            (RetrievalEvalGates.HybridA, "Hybrid dense + sparse RRF, deterministic stage A, no model rerank", true, false),
            (RetrievalEvalGates.HybridAb, $"Hybrid stage A, then stage B: {rerankModel} over the top {candidates}", true, true)
        ];
        List<string> notMeasured = [];
        foreach ((string name, string description, bool hybrid, bool stageB) in plan)
        {
            try
            {
                QdrantPlatformDocsIndex index = Make(hybrid, stageB, out PlatformDocsOptions options);
                Stopwatch build = Stopwatch.StartNew();
                Assert.True(await index.EnsureIndexedAsync(budget.Token), $"{name}: the index could not be built");
                output.WriteLine($"{name}: index ready in {build.Elapsed.TotalSeconds:0}s");

                foreach ((EvalQuery q, _) in active.Take(3))
                    await Search(index, q.Query, 8, SearchOptions(q.Query, options.InjectMinScore), budget.Token);

                List<QueryOutcome> outcomes = [];
                foreach ((EvalQuery q, IReadOnlySet<string> gold) in active)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    PlatformDocsSearchResult ranking = await Search(index, q.Query, 10, SearchOptions(q.Query, null), budget.Token);
                    double rankingMs = sw.Elapsed.TotalMilliseconds;

                    sw.Restart();
                    PlatformDocsSearchResult injection = await Search(
                        index, q.Query, options.InjectTopK * 2, SearchOptions(q.Query, options.InjectMinScore), budget.Token);
                    double injectionMs = sw.Elapsed.TotalMilliseconds;

                    IEnumerable<PlatformDocsHit> injected = injection.Ranked
                        ? injection.Hits
                        : PlatformDocsContextBuilder.Rank(injection.Hits, options.InjectMinScore);
                    outcomes.Add(new QueryOutcome(
                        q.Id, q.Locale, q.Type,
                        RetrievalEvalMetrics.Collapse(ranking.Hits.Select(Key)),
                        RetrievalEvalMetrics.Collapse(injected.Take(options.InjectTopK).Select(Key)),
                        [.. gold], rankingMs, injectionMs));
                }

                results.Add(new ConfigResult(name, description, RetrievalEvalMetrics.BySlice(outcomes), outcomes));
                output.WriteLine($"{name}: recall@5 {results[^1].Slices["all"].RecallAt5:0.000}, nDCG@10 {results[^1].Slices["all"].NdcgAt10:0.000}");
            }
            catch (OperationCanceledException)
            {
                notMeasured.Add(name);
                output.WriteLine($"{name}: NOT MEASURED, the {budgetMinutes} minute budget ran out");
            }
        }

        IReadOnlyList<Gate> gates = RetrievalEvalGates.Evaluate(results);
        string date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        string machine = $"{Environment.ProcessorCount} logical cores, {Environment.OSVersion}";
        Dictionary<string, string> setup = new()
        {
            ["corpus"] = $"{corpus.Chunks.Count} chunks from {corpusDocs.Count} documents (chunker 3, working tree)",
            ["embeddings"] = $"{embModel} via {embUrl}",
            ["reranker"] = $"{rerankModel} via {rerankUrl}, candidates {candidates}",
            ["qdrant"] = qdrantUrl,
            ["corpus subset"] = $"{corpus.Chunks.Count} of {full.Chunks.Count} chunks (every document that is gold for a query, plus seeded random other documents up to {maxChunks})",
            ["not measured"] = notMeasured.Count == 0 ? "none" : string.Join(", ", notMeasured) + $" (budget {budgetMinutes} min exhausted)"
        };
        await File.WriteAllTextAsync(Path.Combine(outDir, "eval-results.json"),
            RetrievalEvalReport.ToJson(machine, date, setup, active.Count, pending, results, gates));
        await File.WriteAllTextAsync(Path.Combine(outDir, "eval-report.md"),
            RetrievalEvalReport.ToMarkdown(machine, date, setup, active.Count, pending, results, gates));
        output.WriteLine($"written to {outDir}");
    }

    /// <summary>
    /// Every document that is gold for some query (and every pendingGold document that exists), then seeded random other
    /// documents, whole, until the cap. Whole documents keep chunk adjacency intact.
    /// </summary>
    private static PlatformDocsCorpus Subset(PlatformDocsCorpus full, EvalSet set, int maxChunks, int seed)
    {
        HashSet<string> goldDocs = [.. set.Queries.SelectMany(q => q.Gold.Concat(q.PendingGold)).Select(g => g[..g.IndexOf('#')])];
        List<IGrouping<string, PlatformDocsChunk>> groups = [.. full.Chunks.GroupBy(c => c.DocPath)];
        List<PlatformDocsChunk> keep = [.. groups.Where(g => goldDocs.Contains(g.Key)).SelectMany(g => g)];
        Random rng = new(seed);
        foreach (IGrouping<string, PlatformDocsChunk> g in groups.Where(g => !goldDocs.Contains(g.Key)).OrderBy(_ => rng.Next()))
            if (keep.Count + g.Count() <= maxChunks) keep.AddRange(g);
        HashSet<PlatformDocsChunk> kept = [.. keep];
        return new PlatformDocsCorpus([.. full.Chunks.Where(kept.Contains)]);
    }

    /// <summary>A search that did not answer Ok is a service failure, not a miss: fail the run instead of scoring it as zero.</summary>
    private static async Task<PlatformDocsSearchResult> Search(
        QdrantPlatformDocsIndex index, string query, int limit, PlatformDocsSearchOptions options, CancellationToken ct)
    {
        PlatformDocsSearchResult result = await index.SearchAsync(query, limit, options, ct);
        if (result.Status != PlatformDocsSearchStatus.Ok)
            throw new InvalidOperationException($"Search for \"{query}\" answered {result.Status} ({result.Message}); the services failed, the run is invalid.");
        return result;
    }

    /// <summary>The eval sends the whole corpus and every query to these URLs; refuse anything but loopback unless explicitly allowed.</summary>
    private static string Local(string name, string value)
    {
        Uri uri = new(value);
        if (!uri.IsLoopback && Environment.GetEnvironmentVariable("REELBOLT_EVAL_ALLOW_REMOTE") != "1")
            throw new InvalidOperationException($"{name} must be a loopback URL (got {uri.Host}); set REELBOLT_EVAL_ALLOW_REMOTE=1 to send the corpus to a remote service.");
        return value;
    }

    private static string Key(PlatformDocsChunk c) => $"{c.DocPath}#{c.Section}";

    private static string Key(PlatformDocsHit h) => $"{h.DocPath}#{h.Section}";

    private static PlatformDocsSearchOptions SearchOptions(string query, float? floor) =>
        new() { Hints = PlatformDocsHints.FromText(query), MinScore = floor };
}
