using ReelBolt.Shared.DesignSystems;

namespace ReelBolt.Shared.DesignSystems.Merge;

/// <summary>
/// Combines the static product map with the LLM fallback (D-4: "static parse first, LLM fallback
/// second"). The static map wins every section it has; a section it lacks — null, empty or an empty
/// list — is filled from the LLM map. Pure: no IO, no mutation, never throws.
/// </summary>
public static class ProductMapMerger
{
    /// <summary>
    /// Merges <paramref name="staticMap"/> and <paramref name="llmMap"/>. Either may be null; when
    /// both are the result is a fresh, empty document. Scalar fields and per-item
    /// <c>Source</c>/<c>Provenance</c> are preserved from the winning side.
    /// </summary>
    public static ProductMapDocument Merge(
        ProductMapDocument? staticMap, ProductMapDocument? llmMap, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (staticMap is null && llmMap is null)
            return Empty();

        if (staticMap is null)
            return llmMap!;

        if (llmMap is null)
            return staticMap;

        return new ProductMapDocument(
            ProductMapDocument.SchemaId,
            FirstNonEmpty(staticMap.Name, llmMap.Name),
            MergeFramework(staticMap.Framework, llmMap.Framework),
            FirstNonEmpty(staticMap.Architecture, llmMap.Architecture),
            FirstNonEmpty(staticMap.RoutingStrategy, llmMap.RoutingStrategy),
            FirstNonEmptyList(staticMap.Directories, llmMap.Directories),
            FirstNonEmptyList(staticMap.EntryPoints, llmMap.EntryPoints),
            FirstNonEmptyList(staticMap.Routes, llmMap.Routes),
            FirstNonEmptyList(staticMap.Components, llmMap.Components),
            MergeDependencies(staticMap.Dependencies, llmMap.Dependencies),
            FirstNonEmptyList(staticMap.Patterns, llmMap.Patterns),
            MergeProvenance(staticMap, llmMap),
            // Preview cards are captured from markup by the static reader only; the LLM fallback
            // carries none, so an LLM document never supplies cards.
            HasItems(staticMap.PreviewCards) ? staticMap.PreviewCards : null);
    }

    private static ProductMapFramework MergeFramework(ProductMapFramework? staticFramework, ProductMapFramework? llmFramework)
    {
        ProductMapFramework s = staticFramework ?? EmptyFramework();
        ProductMapFramework l = llmFramework ?? EmptyFramework();

        return new ProductMapFramework(
            FirstNonEmpty(s.Name, l.Name) ?? string.Empty,
            FirstNonEmpty(s.Version, l.Version) ?? string.Empty,
            FirstNonEmpty(s.Language, l.Language) ?? string.Empty,
            FirstNonEmptyList(s.Styling, l.Styling),
            FirstNonEmpty(s.PackageManager, l.PackageManager) ?? string.Empty,
            FirstNonEmpty(s.ProjectType, l.ProjectType) ?? string.Empty);
    }

    private static ProductMapDependencies MergeDependencies(ProductMapDependencies? staticDependencies, ProductMapDependencies? llmDependencies)
    {
        ProductMapDependencies s = staticDependencies ?? EmptyDependencies();
        ProductMapDependencies l = llmDependencies ?? EmptyDependencies();

        return new ProductMapDependencies(
            FirstNonEmptyList(s.Runtime, l.Runtime),
            FirstNonEmptyList(s.Dev, l.Dev),
            FirstNonEmptyList(s.Ui, l.Ui));
    }

    private static IReadOnlyDictionary<string, ProductMapProvenance> MergeProvenance(
        ProductMapDocument staticMap,
        ProductMapDocument llmMap)
    {
        var result = new Dictionary<string, ProductMapProvenance>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);

        if (staticMap.Provenance is not null)
        {
            foreach (string key in staticMap.Provenance.Keys)
                keys.Add(key);
        }

        if (llmMap.Provenance is not null)
        {
            foreach (string key in llmMap.Provenance.Keys)
                keys.Add(key);
        }

        foreach (string key in keys)
        {
            ProductMapProvenance? chosen = null;

            if (WonByStatic(key, staticMap))
            {
                if (staticMap.Provenance is not null && staticMap.Provenance.TryGetValue(key, out ProductMapProvenance? staticProv))
                    chosen = staticProv;
                else if (llmMap.Provenance is not null && llmMap.Provenance.TryGetValue(key, out ProductMapProvenance? llmProv))
                    chosen = llmProv;
            }
            else
            {
                if (llmMap.Provenance is not null && llmMap.Provenance.TryGetValue(key, out ProductMapProvenance? llmProv))
                    chosen = llmProv;
                else if (staticMap.Provenance is not null && staticMap.Provenance.TryGetValue(key, out ProductMapProvenance? staticProv))
                    chosen = staticProv;
            }

            if (chosen is not null)
                result[key] = chosen;
        }

        return result;
    }

    private static bool WonByStatic(string key, ProductMapDocument staticMap) => key switch
    {
        "routes" => HasItems(staticMap.Routes),
        "components" => HasItems(staticMap.Components),
        "directories" => HasItems(staticMap.Directories),
        "entryPoints" => HasItems(staticMap.EntryPoints),
        "patterns" => HasItems(staticMap.Patterns),
        "dependencies" => staticMap.Dependencies is not null
            && (HasItems(staticMap.Dependencies.Runtime)
                || HasItems(staticMap.Dependencies.Dev)
                || HasItems(staticMap.Dependencies.Ui)),
        "framework" => staticMap.Framework is not null
            && (!string.IsNullOrEmpty(staticMap.Framework.Name)
                || !string.IsNullOrEmpty(staticMap.Framework.Version)
                || !string.IsNullOrEmpty(staticMap.Framework.Language)
                || HasItems(staticMap.Framework.Styling)
                || !string.IsNullOrEmpty(staticMap.Framework.PackageManager)
                || !string.IsNullOrEmpty(staticMap.Framework.ProjectType)),
        "name" => !string.IsNullOrEmpty(staticMap.Name),
        "architecture" => !string.IsNullOrEmpty(staticMap.Architecture),
        "routingStrategy" => !string.IsNullOrEmpty(staticMap.RoutingStrategy),
        _ => true,
    };

    private static bool HasItems<T>(IReadOnlyList<T>? values) => values is { Count: > 0 };

    private static string? FirstNonEmpty(string? first, string? second) =>
        string.IsNullOrEmpty(first) ? second : first;

    private static IReadOnlyList<T> FirstNonEmptyList<T>(IReadOnlyList<T>? first, IReadOnlyList<T>? second) =>
        HasItems(first) ? first! : second ?? [];

    private static ProductMapFramework EmptyFramework() => new(string.Empty, string.Empty, string.Empty, [], string.Empty, string.Empty);

    private static ProductMapDependencies EmptyDependencies() => new([], [], []);

    private static ProductMapDocument Empty() => new(
        ProductMapDocument.SchemaId,
        null,
        EmptyFramework(),
        null,
        null,
        [],
        [],
        [],
        [],
        EmptyDependencies(),
        [],
        new Dictionary<string, ProductMapProvenance>(StringComparer.Ordinal));
}
