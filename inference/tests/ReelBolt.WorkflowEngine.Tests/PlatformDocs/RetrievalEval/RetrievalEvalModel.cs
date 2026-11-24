using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelBolt.WorkflowEngine.Tests.PlatformDocs.RetrievalEval;

/// <summary>One query of docs/design/workflow-apps/retrieval-eval.json.</summary>
public sealed record EvalQuery
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("locale")] public string Locale { get; init; } = "en";
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("query")] public string Query { get; init; } = "";
    [JsonPropertyName("gold")] public List<string> Gold { get; init; } = [];
    [JsonPropertyName("pendingGold")] public List<string> PendingGold { get; init; } = [];

    public bool IsEnglish => Locale == "en";

    /// <summary>
    /// The sections that count as a correct answer against the corpus as it is now: <c>gold</c> plus every
    /// <c>pendingGold</c> entry whose document has landed. Empty means the query is pending-kb and is left out of the metrics.
    /// </summary>
    public IReadOnlySet<string> ActiveGold(IReadOnlySet<string> corpusSections, IReadOnlySet<string> corpusDocs)
    {
        HashSet<string> active = new(Gold, StringComparer.Ordinal);
        foreach (string pending in PendingGold)
        {
            string doc = pending[..Math.Max(0, pending.IndexOf('#'))];
            if (corpusDocs.Contains(doc) && corpusSections.Contains(pending)) active.Add(pending);
        }

        return active;
    }
}

public sealed record EvalSet
{
    [JsonPropertyName("queries")] public List<EvalQuery> Queries { get; init; } = [];

    public static EvalSet Load(string path) =>
        JsonSerializer.Deserialize<EvalSet>(File.ReadAllText(path)) ?? throw new InvalidDataException(path);
}

/// <summary>The per-query outcome of one configuration.</summary>
public sealed record QueryOutcome(
    string Id,
    string Locale,
    string Type,
    IReadOnlyList<string> Ranked,
    IReadOnlyList<string> Injected,
    IReadOnlyList<string> Gold,
    double RankingMs,
    double InjectionMs);

/// <summary>Metrics of one slice of one configuration.</summary>
public sealed record SliceMetrics(
    int Queries,
    double RecallAt5,
    double MrrAt10,
    double NdcgAt10,
    double InjectionHitRate,
    double InjectionAnyRate,
    double LatencyP50Ms,
    double LatencyP95Ms);
