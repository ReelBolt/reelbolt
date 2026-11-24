using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems.Diff;

/// <summary>One named item that changed: its key plus the before/after shape.</summary>
public sealed record ProductMapDiffEntry(string Name, JsonNode? Before, JsonNode? After);

/// <summary>Added/removed names (or paths) plus the changed items of one product-map collection.</summary>
public sealed record ProductMapCollectionDiff(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<ProductMapDiffEntry> Changed);

/// <summary>
/// The product half of a drift diff (SPEC 13.3): routes, components and dependencies gained, lost
/// or changed. Pure and total.
/// </summary>
public sealed record ProductMapDiff(
    ProductMapCollectionDiff Routes,
    ProductMapCollectionDiff Components,
    ProductMapCollectionDiff Dependencies)
{
    private static readonly ProductMapCollectionDiff NoChange = new([], [], []);

    /// <summary>Directories gained, lost or changed (by path).</summary>
    public ProductMapCollectionDiff Directories { get; init; } = NoChange;

    /// <summary>Entry points gained, lost or changed (by path).</summary>
    public ProductMapCollectionDiff EntryPoints { get; init; } = NoChange;

    /// <summary>Architectural patterns gained or lost.</summary>
    public ProductMapCollectionDiff Patterns { get; init; } = NoChange;

    /// <summary>Scalar fields (<c>name</c>, <c>architecture</c>, <c>routingStrategy</c>) that changed.</summary>
    public IReadOnlyList<ProductMapDiffEntry> Fields { get; init; } = [];

    /// <summary>The SPEC 13.3 <c>product</c> object; the extra sections appear only when non-empty.</summary>
    public JsonObject ToJson()
    {
        var root = new JsonObject
        {
            ["routes"] = Collection(Routes),
            ["components"] = Collection(Components),
            ["dependencies"] = Collection(Dependencies),
        };

        AddIfAny(root, "directories", Directories);
        AddIfAny(root, "entryPoints", EntryPoints);
        AddIfAny(root, "patterns", Patterns);

        if (Fields.Count > 0)
        {
            var fields = new JsonArray();
            foreach (ProductMapDiffEntry entry in Fields)
            {
                fields.Add(new JsonObject
                {
                    ["name"] = entry.Name,
                    ["before"] = entry.Before?.DeepClone(),
                    ["after"] = entry.After?.DeepClone(),
                });
            }

            root["fields"] = fields;
        }

        return root;
    }

    private static void AddIfAny(JsonObject root, string name, ProductMapCollectionDiff collection)
    {
        if (collection.Added.Count + collection.Removed.Count + collection.Changed.Count > 0)
            root[name] = Collection(collection);
    }

    private static JsonObject Collection(ProductMapCollectionDiff collection)
    {
        var changed = new JsonArray();
        foreach (ProductMapDiffEntry entry in collection.Changed)
        {
            changed.Add(new JsonObject
            {
                ["name"] = entry.Name,
                ["before"] = entry.Before?.DeepClone(),
                ["after"] = entry.After?.DeepClone(),
            });
        }

        return new JsonObject
        {
            ["added"] = Strings(collection.Added),
            ["removed"] = Strings(collection.Removed),
            ["changed"] = changed,
        };
    }

    private static JsonArray Strings(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (string value in values)
            array.Add(value);
        return array;
    }
}

/// <summary>
/// Compares two product maps by route path, component name and dependency name (runtime then dev).
/// A missing side is treated as an empty document, so a first extraction reads as every item added.
/// </summary>
public static class ProductMapDiffer
{
    public static ProductMapDiff Diff(ProductMapDocument? before, ProductMapDocument? after, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return new ProductMapDiff(
            DiffRoutes(before?.Routes, after?.Routes),
            DiffComponents(before?.Components, after?.Components),
            DiffDependencies(before?.Dependencies, after?.Dependencies))
        {
            Directories = DiffKeyed(
                before?.Directories, after?.Directories, d => d.Path, d => d.Purpose),
            EntryPoints = DiffKeyed(
                before?.EntryPoints, after?.EntryPoints, e => e.Path, e => e.Role),
            Patterns = DiffKeyed(before?.Patterns, after?.Patterns, p => p, p => string.Empty),
            Fields = DiffScalars(before, after),
        };
    }

    private static ProductMapCollectionDiff DiffKeyed<T>(
        IReadOnlyList<T>? before, IReadOnlyList<T>? after, Func<T, string> key, Func<T, string> value)
    {
        Dictionary<string, string> baseItems = Keyed(before, key, value);
        Dictionary<string, string> candidateItems = Keyed(after, key, value);

        var added = new List<string>();
        var removed = new List<string>();
        var changed = new List<ProductMapDiffEntry>();

        foreach (KeyValuePair<string, string> entry in candidateItems)
        {
            if (!baseItems.TryGetValue(entry.Key, out string? previous))
                added.Add(entry.Key);
            else if (!string.Equals(previous, entry.Value, StringComparison.Ordinal))
                changed.Add(new ProductMapDiffEntry(entry.Key, JsonValue.Create(previous), JsonValue.Create(entry.Value)));
        }

        foreach (string name in baseItems.Keys)
        {
            if (!candidateItems.ContainsKey(name))
                removed.Add(name);
        }

        return new ProductMapCollectionDiff(added, removed, changed);
    }

    private static Dictionary<string, string> Keyed<T>(
        IReadOnlyList<T>? items, Func<T, string> key, Func<T, string> value)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (T item in items ?? [])
        {
            if (item is not null)
                map[key(item)] = value(item) ?? string.Empty;
        }

        return map;
    }

    private static List<ProductMapDiffEntry> DiffScalars(ProductMapDocument? before, ProductMapDocument? after)
    {
        var fields = new List<ProductMapDiffEntry>();
        Scalar(fields, "name", before?.Name, after?.Name);
        Scalar(fields, "architecture", before?.Architecture, after?.Architecture);
        Scalar(fields, "routingStrategy", before?.RoutingStrategy, after?.RoutingStrategy);
        return fields;
    }

    private static void Scalar(List<ProductMapDiffEntry> fields, string name, string? before, string? after)
    {
        if (!string.Equals(before ?? string.Empty, after ?? string.Empty, StringComparison.Ordinal))
            fields.Add(new ProductMapDiffEntry(name, JsonValue.Create(before), JsonValue.Create(after)));
    }

    private static ProductMapCollectionDiff DiffRoutes(
        IReadOnlyList<ProductMapRoute>? before, IReadOnlyList<ProductMapRoute>? after)
    {
        Dictionary<string, ProductMapRoute> baseRoutes = ByPath(before);
        Dictionary<string, ProductMapRoute> candidateRoutes = ByPath(after);

        var added = new List<string>();
        var removed = new List<string>();
        var changed = new List<ProductMapDiffEntry>();

        foreach (KeyValuePair<string, ProductMapRoute> entry in candidateRoutes)
        {
            if (!baseRoutes.TryGetValue(entry.Key, out ProductMapRoute? previous))
                added.Add(entry.Key);
            else if (!SameRoute(previous, entry.Value))
                changed.Add(new ProductMapDiffEntry(entry.Key, RouteNode(previous), RouteNode(entry.Value)));
        }

        foreach (string path in baseRoutes.Keys)
        {
            if (!candidateRoutes.ContainsKey(path))
                removed.Add(path);
        }

        return new ProductMapCollectionDiff(added, removed, changed);
    }

    private static ProductMapCollectionDiff DiffComponents(
        IReadOnlyList<ProductMapComponent>? before, IReadOnlyList<ProductMapComponent>? after)
    {
        Dictionary<string, ProductMapComponent> baseComponents = ByName(before);
        Dictionary<string, ProductMapComponent> candidateComponents = ByName(after);

        var added = new List<string>();
        var removed = new List<string>();
        var changed = new List<ProductMapDiffEntry>();

        foreach (KeyValuePair<string, ProductMapComponent> entry in candidateComponents)
        {
            if (!baseComponents.TryGetValue(entry.Key, out ProductMapComponent? previous))
                added.Add(entry.Key);
            else if (!SameComponent(previous, entry.Value))
                changed.Add(new ProductMapDiffEntry(entry.Key, ComponentNode(previous), ComponentNode(entry.Value)));
        }

        foreach (string name in baseComponents.Keys)
        {
            if (!candidateComponents.ContainsKey(name))
                removed.Add(name);
        }

        return new ProductMapCollectionDiff(added, removed, changed);
    }

    private static ProductMapCollectionDiff DiffDependencies(
        ProductMapDependencies? before, ProductMapDependencies? after)
    {
        Dictionary<string, string> baseDependencies = ByName(before);
        Dictionary<string, string> candidateDependencies = ByName(after);

        var added = new List<string>();
        var removed = new List<string>();
        var changed = new List<ProductMapDiffEntry>();

        foreach (KeyValuePair<string, string> entry in candidateDependencies)
        {
            if (!baseDependencies.TryGetValue(entry.Key, out string? previous))
                added.Add(entry.Key);
            else if (!string.Equals(previous, entry.Value, StringComparison.Ordinal))
                changed.Add(new ProductMapDiffEntry(entry.Key, JsonValue.Create(previous), JsonValue.Create(entry.Value)));
        }

        foreach (string name in baseDependencies.Keys)
        {
            if (!candidateDependencies.ContainsKey(name))
                removed.Add(name);
        }

        return new ProductMapCollectionDiff(added, removed, changed);
    }

    private static Dictionary<string, ProductMapRoute> ByPath(IReadOnlyList<ProductMapRoute>? routes)
    {
        var map = new Dictionary<string, ProductMapRoute>(StringComparer.Ordinal);
        foreach (ProductMapRoute route in routes ?? [])
        {
            if (route is null)
                continue;
            map[route.Path] = route;
        }

        return map;
    }

    private static Dictionary<string, ProductMapComponent> ByName(IReadOnlyList<ProductMapComponent>? components)
    {
        var map = new Dictionary<string, ProductMapComponent>(StringComparer.Ordinal);
        foreach (ProductMapComponent component in components ?? [])
        {
            if (component is null)
                continue;
            map[component.Name] = component;
        }

        return map;
    }

    private static Dictionary<string, string> ByName(ProductMapDependencies? dependencies)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ProductMapDependency dependency in dependencies?.Runtime ?? [])
        {
            if (dependency is not null)
                map[dependency.Name] = dependency.Version ?? string.Empty;
        }

        foreach (ProductMapDependency dependency in dependencies?.Dev ?? [])
        {
            if (dependency is not null && !map.ContainsKey(dependency.Name))
                map[dependency.Name] = dependency.Version ?? string.Empty;
        }

        return map;
    }

    private static bool SameRoute(ProductMapRoute left, ProductMapRoute right) =>
        string.Equals(left.Path, right.Path, StringComparison.Ordinal)
        && string.Equals(left.File, right.File, StringComparison.Ordinal)
        && string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
        && string.Equals(left.Method, right.Method, StringComparison.Ordinal)
        && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
        && left.RequiresAuth == right.RequiresAuth
        && string.Equals(left.Source, right.Source, StringComparison.Ordinal);

    private static bool SameComponent(ProductMapComponent left, ProductMapComponent right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && string.Equals(left.Path, right.Path, StringComparison.Ordinal)
        && string.Equals(left.Responsibility, right.Responsibility, StringComparison.Ordinal)
        && left.Core == right.Core
        && string.Equals(left.Source, right.Source, StringComparison.Ordinal)
        && left.KeyProps.SequenceEqual(right.KeyProps, StringComparer.Ordinal);

    private static JsonObject RouteNode(ProductMapRoute route) => new()
    {
        ["path"] = route.Path,
        ["file"] = route.File,
        ["kind"] = route.Kind,
        ["method"] = route.Method,
        ["title"] = route.Title,
        ["requiresAuth"] = route.RequiresAuth,
        ["source"] = route.Source,
    };

    private static JsonObject ComponentNode(ProductMapComponent component)
    {
        var keyProps = new JsonArray();
        foreach (string prop in component.KeyProps ?? [])
            keyProps.Add(prop);

        return new JsonObject
        {
            ["name"] = component.Name,
            ["path"] = component.Path,
            ["responsibility"] = component.Responsibility,
            ["core"] = component.Core,
            ["source"] = component.Source,
            ["keyProps"] = keyProps,
        };
    }
}
