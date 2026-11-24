using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ReelBolt.Shared.DesignSystems;

/// <summary>
/// Reads and writes <see cref="ProductMapDocument"/> as canonical JSON. Parsing never throws on
/// malformed input (it answers null), missing collections are normalised to empty ones, and the
/// writer omits the optional members the schema allows to be absent.
/// </summary>
public static class ProductMapJson
{
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The document encoded in <paramref name="json"/>, or null when the text is not a JSON object,
    /// the schema, framework or dependencies section is absent, or the text is not valid JSON.
    /// </summary>
    public static ProductMapDocument? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            JsonNode? node = JsonNode.Parse(json);
            if (node is not JsonObject)
                return null;

            ProductMapDocument? doc = node.Deserialize<ProductMapDocument>(ParseOptions);
            if (doc is null || doc.Schema is null || doc.Framework is null || doc.Dependencies is null)
                return null;

            return Normalize(doc);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The canonical JSON tree for <paramref name="doc"/>, with camelCase keys.</summary>
    public static JsonObject ToNode(ProductMapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        ProductMapFramework framework = doc.Framework
            ?? new ProductMapFramework("", "", "", [], "", "");
        ProductMapDependencies dependencies = doc.Dependencies
            ?? new ProductMapDependencies([], [], []);

        var root = new JsonObject { ["schema"] = doc.Schema };
        SetOptional(root, "name", doc.Name);

        root["framework"] = new JsonObject
        {
            ["name"] = framework.Name,
            ["version"] = framework.Version,
            ["language"] = framework.Language,
            ["styling"] = Strings(framework.Styling),
            ["packageManager"] = framework.PackageManager,
            ["projectType"] = framework.ProjectType,
        };

        SetOptional(root, "architecture", doc.Architecture);
        SetOptional(root, "routingStrategy", doc.RoutingStrategy);

        var directories = new JsonArray();
        foreach (ProductMapDirectory directory in doc.Directories ?? [])
        {
            if (directory is null)
                continue;
            directories.Add(new JsonObject
            {
                ["path"] = directory.Path,
                ["purpose"] = directory.Purpose,
            });
        }
        root["directories"] = directories;

        var entryPoints = new JsonArray();
        foreach (ProductMapEntryPoint entryPoint in doc.EntryPoints ?? [])
        {
            if (entryPoint is null)
                continue;
            entryPoints.Add(new JsonObject
            {
                ["path"] = entryPoint.Path,
                ["role"] = entryPoint.Role,
            });
        }
        root["entryPoints"] = entryPoints;

        var routes = new JsonArray();
        foreach (ProductMapRoute route in doc.Routes ?? [])
        {
            if (route is null)
                continue;
            var routeNode = new JsonObject
            {
                ["path"] = route.Path,
                ["file"] = route.File,
                ["kind"] = route.Kind,
                ["requiresAuth"] = route.RequiresAuth,
            };
            SetOptional(routeNode, "method", route.Method);
            SetOptional(routeNode, "title", route.Title);
            SetOptional(routeNode, "source", route.Source);
            routes.Add(routeNode);
        }
        root["routes"] = routes;

        var components = new JsonArray();
        foreach (ProductMapComponent component in doc.Components ?? [])
        {
            if (component is null)
                continue;
            var componentNode = new JsonObject
            {
                ["name"] = component.Name,
                ["path"] = component.Path,
                ["responsibility"] = component.Responsibility,
                ["keyProps"] = Strings(component.KeyProps),
                ["core"] = component.Core,
            };
            SetOptional(componentNode, "source", component.Source);
            components.Add(componentNode);
        }
        root["components"] = components;

        var runtime = new JsonArray();
        foreach (ProductMapDependency dependency in dependencies.Runtime ?? [])
        {
            if (dependency is null)
                continue;
            runtime.Add(new JsonObject
            {
                ["name"] = dependency.Name,
                ["version"] = dependency.Version,
            });
        }

        var dev = new JsonArray();
        foreach (ProductMapDependency dependency in dependencies.Dev ?? [])
        {
            if (dependency is null)
                continue;
            dev.Add(new JsonObject
            {
                ["name"] = dependency.Name,
                ["version"] = dependency.Version,
            });
        }

        root["dependencies"] = new JsonObject
        {
            ["runtime"] = runtime,
            ["dev"] = dev,
            ["ui"] = Strings(dependencies.Ui),
        };

        root["patterns"] = Strings(doc.Patterns ?? []);

        var provenance = new JsonObject();
        foreach (KeyValuePair<string, ProductMapProvenance> entry in
                 doc.Provenance ?? new Dictionary<string, ProductMapProvenance>())
        {
            if (entry.Value is null)
                continue;
            provenance[entry.Key] = new JsonObject
            {
                ["method"] = entry.Value.Method,
                ["sourceIds"] = Strings(entry.Value.SourceIds ?? []),
            };
        }
        root["provenance"] = provenance;

        if (doc.PreviewCards is { Count: > 0 })
        {
            var previewCards = new JsonArray();
            foreach (ProductMapPreviewCard card in doc.PreviewCards)
            {
                if (card is null)
                    continue;

                var cardNode = new JsonObject
                {
                    ["name"] = card.Name,
                    ["assets"] = Strings(card.Assets ?? []),
                    ["runtimeRendered"] = card.RuntimeRendered,
                };
                SetOptional(cardNode, "group", card.Group);
                SetOptional(cardNode, "subtitle", card.Subtitle);
                SetOptional(cardNode, "viewport", card.Viewport);
                SetOptional(cardNode, "path", card.Path);
                SetOptional(cardNode, "text", card.Text);
                previewCards.Add(cardNode);
            }

            if (previewCards.Count > 0)
                root["previewCards"] = previewCards;
        }

        return root;
    }

    /// <summary>The canonical JSON text for <paramref name="doc"/>.</summary>
    public static string Serialize(ProductMapDocument doc) => CanonicalJson.Serialize(ToNode(doc));

    private static void SetOptional(JsonObject target, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            target[key] = value;
    }

    private static JsonArray Strings(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (string value in values)
            array.Add(value);
        return array;
    }

    private static ProductMapDocument Normalize(ProductMapDocument doc) => doc with
    {
        Framework = doc.Framework with { Styling = Clean(doc.Framework.Styling) },
        Directories = Clean(doc.Directories),
        EntryPoints = Clean(doc.EntryPoints),
        Routes = Clean(doc.Routes),
        Components = Clean(doc.Components)
            .Select(static component => component with { KeyProps = Clean(component.KeyProps) })
            .ToList(),
        Dependencies = doc.Dependencies with
        {
            Runtime = Clean(doc.Dependencies.Runtime),
            Dev = Clean(doc.Dependencies.Dev),
            Ui = Clean(doc.Dependencies.Ui),
        },
        Patterns = Clean(doc.Patterns),
        Provenance = NormalizeProvenance(doc.Provenance),
        PreviewCards = NormalizePreviewCards(doc.PreviewCards),
    };

    private static IReadOnlyList<ProductMapPreviewCard>? NormalizePreviewCards(
        IReadOnlyList<ProductMapPreviewCard>? source)
    {
        if (source is null)
            return null;

        var cards = new List<ProductMapPreviewCard>(source.Count);
        foreach (ProductMapPreviewCard? card in source)
        {
            if (card is null)
                continue;

            cards.Add(card with { Assets = Clean(card.Assets) });
        }

        return cards.Count == 0 ? null : cards;
    }

    private static IReadOnlyList<T> Clean<T>(IReadOnlyList<T>? values)
        where T : class =>
        values is null ? [] : values.Where(static value => value is not null).ToList();

    private static IReadOnlyDictionary<string, ProductMapProvenance> NormalizeProvenance(
        IReadOnlyDictionary<string, ProductMapProvenance>? source)
    {
        if (source is null)
            return new Dictionary<string, ProductMapProvenance>(StringComparer.Ordinal);

        var normalized = new Dictionary<string, ProductMapProvenance>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, ProductMapProvenance> entry in source)
        {
            if (entry.Value is null)
                continue;
            normalized[entry.Key] = entry.Value with { SourceIds = Clean(entry.Value.SourceIds) };
        }

        return normalized;
    }
}
