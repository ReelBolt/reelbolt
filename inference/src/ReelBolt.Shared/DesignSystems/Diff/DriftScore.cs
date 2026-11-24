using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems.Diff;

/// <summary>
/// The uncalibrated drift scores of SPEC 13.3. A weight is a property of a token path, never of the
/// value: <c>min(1, W_changed / max(4, W_base))</c>. <c>W_changed</c> sums the weight of every token
/// path that differs between the base and the candidate (added, removed and changed), and
/// <c>W_base</c> sums the weight of every token in the base document. A <c>voice</c> section change
/// adds one half-unit on top.
/// </summary>
public static class DriftScore
{
    /// <summary>The exact token paths that carry the highest weight.</summary>
    private static readonly HashSet<string> CriticalPaths = new(StringComparer.Ordinal)
    {
        "color.brand.primary",
        "color.brand.secondary",
        "color.background",
        "color.text",
        "typography.family.primary",
        "typography.family.secondary",
    };

    /// <summary>The uncalibrated score of a design diff against its base document.</summary>
    public static double Design(DesignDiff diff, JsonObject baseDoc)
    {
        ArgumentNullException.ThrowIfNull(diff);

        double baseWeight = 0;
        if (baseDoc is not null)
        {
            foreach (DesignTokenRef token in DesignDocument.Tokens(baseDoc))
                baseWeight += DesignWeight(token.Path);
        }

        double changedWeight = 0;
        foreach (DesignDiffEntry entry in diff.Added)
            changedWeight += DesignWeight(entry.Path);
        foreach (DesignDiffEntry entry in diff.Removed)
            changedWeight += DesignWeight(entry.Path);
        foreach (DesignDiffEntry entry in diff.Changed)
            changedWeight += DesignWeight(entry.Path);

        if (diff.Sections.TryGetValue("voice", out string? voice)
            && string.Equals(voice, "changed", StringComparison.Ordinal))
        {
            changedWeight += 0.5;
        }

        return Math.Min(1, changedWeight / Math.Max(4, baseWeight));
    }

    /// <summary>The uncalibrated score of a product-map diff against its base document.</summary>
    public static double Product(ProductMapDiff diff, ProductMapDocument baseDoc)
    {
        ArgumentNullException.ThrowIfNull(diff);

        double baseWeight = ProductWeight(baseDoc);
        double changedWeight = CollectionWeight(diff.Routes, 0.5)
            + CollectionWeight(diff.Components, 0.1)
            + CollectionWeight(diff.Dependencies, 0.25)
            + CollectionWeight(diff.EntryPoints, 0.25)
            + (diff.Fields.Count * 0.25);
        // Directories and patterns are listed in the diff but weigh nothing: a new component alone
        // already adds a directory and a pattern, and must stay below the proposal threshold.

        return Math.Min(1, changedWeight / Math.Max(4, baseWeight));
    }

    /// <summary>The weight of one design token path (SPEC 13.3 table).</summary>
    public static double DesignWeight(string path)
    {
        if (path is null)
            return 0.25;

        if (CriticalPaths.Contains(path))
            return 1.0;

        return path.StartsWith("color.", StringComparison.Ordinal)
            || path.StartsWith("typography.", StringComparison.Ordinal)
                ? 0.5
                : 0.25;
    }

    private static double CollectionWeight(ProductMapCollectionDiff collection, double itemWeight) =>
        (collection.Added.Count + collection.Removed.Count + collection.Changed.Count) * itemWeight;

    private static double ProductWeight(ProductMapDocument? doc)
    {
        if (doc is null)
            return 0;

        int routes = doc.Routes?.Count ?? 0;
        int components = doc.Components?.Count ?? 0;
        int dependencies = (doc.Dependencies?.Runtime?.Count ?? 0) + (doc.Dependencies?.Dev?.Count ?? 0);
        return (routes * 0.5) + (components * 0.1) + (dependencies * 0.25);
    }
}
