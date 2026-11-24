using Microsoft.Extensions.AI;

namespace ReelBolt.Inference.Api.Services.PlatformDocs;

/// <summary>
/// Boost hints for ranking: the step types, template keys, agent types and capabilities the conversation is
/// about. They only re-order results (stage A); they never filter anything out.
/// </summary>
public sealed record PlatformDocsHints
{
    public static readonly PlatformDocsHints None = new();

    public IReadOnlyList<string> StepTypes { get; init; } = [];
    public IReadOnlyList<string> AgentTypes { get; init; } = [];
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public IReadOnlyList<string> TemplateKeys { get; init; } = [];

    /// <summary>
    /// The question is about building or changing a workflow, so stage A prefers recipes and step cards. False
    /// (neutral: no kind preference) unless the query says so.
    /// </summary>
    public bool BuildIntent { get; init; }

    private static readonly System.Text.RegularExpressions.Regex BuildWords = new(
        @"\b(build|create|make|design|set ?up|add|change|edit|workflow|template|recipe|pipeline|steps?)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public bool IsEmpty => StepTypes.Count + AgentTypes.Count + Capabilities.Count + TemplateKeys.Count == 0;

    /// <summary>Hints from the exact enum names and template keys a message mentions.</summary>
    public static PlatformDocsHints FromText(string text) => new()
    {
        StepTypes = PlatformDocsMetadata.ScanStepTypes(text),
        AgentTypes = PlatformDocsMetadata.ScanAgentTypes(text),
        Capabilities = PlatformDocsMetadata.ScanCapabilities(text),
        TemplateKeys = PlatformDocsMetadata.ScanTemplateKeys(text),
        BuildIntent = BuildWords.IsMatch(text)
    };

    /// <summary>Union of two hint sets (explicit session hints plus message-derived ones).</summary>
    public PlatformDocsHints Merge(PlatformDocsHints other) => new()
    {
        StepTypes = StepTypes.Union(other.StepTypes).ToList(),
        AgentTypes = AgentTypes.Union(other.AgentTypes).ToList(),
        Capabilities = Capabilities.Union(other.Capabilities).ToList(),
        TemplateKeys = TemplateKeys.Union(other.TemplateKeys).ToList(),
        BuildIntent = BuildIntent || other.BuildIntent
    };
}

/// <summary>
/// Per-search options. The four explicit filters are exact-match <c>must</c> conditions (they come from the
/// <c>SearchPlatformDocs</c> arguments and may hide results by design); <see cref="Hints"/> only boost.
/// </summary>
public sealed record PlatformDocsSearchOptions
{
    public string? StepType { get; init; }
    public string? Kind { get; init; }
    public string? Capability { get; init; }
    public string? CostClass { get; init; }
    public PlatformDocsHints Hints { get; init; } = PlatformDocsHints.None;

    /// <summary>
    /// Injection only: the relevance floor (cosine). The hybrid path applies it inside stage A; the
    /// dense-only path leaves it to <see cref="PlatformDocsContextBuilder"/>. Null for the search tool.
    /// </summary>
    public float? MinScore { get; init; }

    public bool HasMustFilter => StepType is not null || Kind is not null || Capability is not null || CostClass is not null;
}

/// <summary>
/// Stage B of the retrieval rerank (a model rerank, a separate capability): receives the stage-A ordered
/// candidates and returns a re-ordered list, or <c>null</c> to keep stage A's order. It must never throw for
/// a provider problem; the index also treats an exception or a timeout as <c>null</c>. No implementation is
/// registered by default.
/// </summary>
public interface IPlatformDocsRerankStage
{
    Task<IReadOnlyList<PlatformDocsHit>?> RerankAsync(
        string query, IReadOnlyList<PlatformDocsHit> candidates, CancellationToken ct);
}

/// <summary>
/// Stage A: the deterministic, free rerank that always runs on hybrid candidates. Rules, in order: score =
/// fused RRF score plus a kind-priority bonus, an exact step/template/agent/capability match with the hints,
/// and the existing +0.05 for customer-facing audience; then at most N chunks per document; then the
/// relevance floor (injection only). Ties break on chunk id so the order is reproducible.
/// </summary>
public static class PlatformDocsStageA
{
    internal const float AudienceBoost = 0.05f;
    internal const float StepMatchBoost = 0.08f;
    internal const float TemplateMatchBoost = 0.10f;
    internal const float AgentMatchBoost = 0.04f;
    internal const float CapabilityMatchBoost = 0.03f;

    /// <summary>
    /// Only when the query has a build intent (neutral otherwise): recipe > step-card > how-to > concept, everything else no bonus. The window is
    /// small against the 0..1 normalised fused score on purpose: it orders near-ties, it does not bury a
    /// much better lexical/semantic match.
    /// </summary>
    internal static float KindBonus(string kind) => kind switch
    {
        "recipe" => 0.06f,
        "step-card" => 0.045f,
        "how-to" => 0.03f,
        "concept" => 0.015f,
        _ => 0f
    };

    public static IReadOnlyList<PlatformDocsHit> Apply(
        IReadOnlyList<PlatformDocsHit> candidates, PlatformDocsSearchOptions options, int maxPerDoc, int limit)
    {
        IEnumerable<PlatformDocsHit> pool = candidates;
        if (options.MinScore is { } floor)
        {
            // Semantic support: the best dense cosine of the query must clear the floor, otherwise nothing is
            // injected (an off-topic "thanks!" must not pull in lexical-only matches). Within a supported
            // query a candidate whose own cosine is known and below the floor is dropped; one outside the
            // dense list (found by the lexical side only) stays.
            float? best = candidates.Where(c => c.DenseScore is not null).Select(c => c.DenseScore).DefaultIfEmpty(null).Max();
            if (best is null || best < floor)
                return [];
            pool = pool.Where(c => c.DenseScore is null || c.DenseScore >= floor);
        }

        var perDoc = new Dictionary<string, int>(StringComparer.Ordinal);
        var ordered = new List<PlatformDocsHit>();
        foreach (PlatformDocsHit hit in pool
                     .OrderByDescending(h => Adjusted(h, options.Hints))
                     .ThenBy(h => h.ChunkId, StringComparer.Ordinal))
        {
            string doc = hit.DocPath.Length > 0 ? hit.DocPath : hit.Title;
            int taken = perDoc.GetValueOrDefault(doc);
            if (taken >= Math.Max(1, maxPerDoc))
                continue;
            perDoc[doc] = taken + 1;
            ordered.Add(hit);
            if (ordered.Count >= limit)
                break;
        }

        return ordered;
    }

    internal static float Adjusted(PlatformDocsHit hit, PlatformDocsHints hints)
    {
        PlatformDocsChunkMeta meta = hit.Meta ?? PlatformDocsChunkMeta.Empty;
        float score = hit.FusedScore + (hints.BuildIntent ? KindBonus(meta.Kind) : 0f);
        if (meta.StepTypes.Intersect(hints.StepTypes).Any())
            score += StepMatchBoost;
        if (meta.TemplateKeys.Intersect(hints.TemplateKeys).Any())
            score += TemplateMatchBoost;
        if (meta.AgentTypes.Intersect(hints.AgentTypes).Any())
            score += AgentMatchBoost;
        if (meta.Capabilities.Intersect(hints.Capabilities).Any())
            score += CapabilityMatchBoost;
        if (hit.Audience == PlatformDocsAudience.User)
            score += AudienceBoost;
        return score;
    }
}

/// <summary>A small thread-safe LRU of query embeddings keyed on the text and the model.</summary>
internal sealed class QueryEmbeddingCache(int capacity)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, Embedding<float> Value)>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, Embedding<float> Value)> _order = new();

    public static string Key(string text, string model, Guid? organizationId = null) =>
        (organizationId?.ToString("N") ?? "-") + "\0" + model + "\0" + text;

    public bool TryGet(string key, out Embedding<float>? embedding)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                embedding = node.Value.Value;
                return true;
            }
        }

        embedding = null;
        return false;
    }

    public void Set(string key, Embedding<float> embedding)
    {
        if (capacity <= 0)
            return;
        lock (_gate)
        {
            if (_map.Remove(key, out var existing))
                _order.Remove(existing);
            _map[key] = _order.AddFirst((key, embedding));
            while (_map.Count > capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    public int Count
    {
        get { lock (_gate) return _map.Count; }
    }
}
