using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReelBolt.Shared.DesignSystems.Importers;

namespace ReelBolt.Shared.DesignSystems;

/// <summary>
/// Validates a <see cref="ProductMapDocument"/> against the closed rule set <c>PM_V01</c>..
/// <c>PM_V12</c>. The validator is pure and never throws on bad input: it accumulates every
/// violation it finds and returns them in rule-code order.
/// </summary>
public static class ProductMapValidator
{
    /// <summary>The largest serialized document allowed, in UTF-8 bytes (PM_V01).</summary>
    public const int MaxSerializedBytes = 262144;

    private static readonly Regex UrlLikePattern = new(
        @"^[a-z][a-z0-9+.-]*://",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    private static readonly Regex DependencyNamePattern = new(
        @"^[@a-z0-9][a-z0-9._/@-]{0,99}$",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> AllowedTopLevelKeys = new(StringComparer.Ordinal)
    {
        "schema", "name", "framework", "architecture", "routingStrategy", "directories",
        "entryPoints", "routes", "components", "dependencies", "patterns", "provenance",
        "previewCards",
    };

    private static readonly HashSet<string> FrameworkNames = new(StringComparer.Ordinal)
    {
        "next", "react", "vite", "remix", "nuxt", "vue", "svelte", "angular", "astro", "express",
        "other",
    };

    private static readonly HashSet<string> Languages = new(StringComparer.Ordinal)
    {
        "typescript", "javascript", "mixed", "other",
    };

    private static readonly HashSet<string> PackageManagers = new(StringComparer.Ordinal)
    {
        "npm", "pnpm", "yarn", "bun", "unknown",
    };

    private static readonly HashSet<string> RouteKinds = new(StringComparer.Ordinal)
    {
        "page", "layout", "api", "other",
    };

    private static readonly HashSet<string> RouteMethods = new(StringComparer.Ordinal)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "ANY",
    };

    private static readonly HashSet<string> Sources = new(StringComparer.Ordinal)
    {
        "static", "llm", "manual",
    };

    private static readonly HashSet<string> ProvenanceMethods = new(StringComparer.Ordinal)
    {
        "static", "llm", "manual", "mixed",
    };

    /// <summary>Every rule breach in <paramref name="doc"/>, in rule-code order; empty when valid.</summary>
    public static DesignValidationResult Validate(ProductMapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var violations = new List<DesignViolation>();

        // PM_V01 - serialized size.
        int bytes = Encoding.UTF8.GetByteCount(ProductMapJson.Serialize(doc));
        if (bytes > MaxSerializedBytes)
        {
            violations.Add(new DesignViolation(
                "PM_V01",
                "",
                $"serialized document is {bytes} UTF-8 bytes; the limit is {MaxSerializedBytes}."));
        }

        // PM_V02 - schema identifier.
        if (!string.Equals(doc.Schema, ProductMapDocument.SchemaId, StringComparison.Ordinal))
        {
            violations.Add(new DesignViolation(
                "PM_V02",
                "schema",
                $"schema must be \"{ProductMapDocument.SchemaId}\" but was \"{doc.Schema}\"."));
        }

        // PM_V03 - unknown top-level key. A typed document cannot carry one, so this checks the
        // writer itself: any key ToNode emits outside the schema is a violation.
        JsonObject node = ProductMapJson.ToNode(doc);
        foreach (KeyValuePair<string, JsonNode?> property in node)
        {
            if (!AllowedTopLevelKeys.Contains(property.Key))
                violations.Add(new DesignViolation("PM_V03", "", $"unknown top-level key \"{property.Key}\"."));
        }

        ProductMapFramework framework = doc.Framework
            ?? new ProductMapFramework("", "", "", [], "", "");
        ProductMapDependencies dependencies = doc.Dependencies
            ?? new ProductMapDependencies([], [], []);

        IReadOnlyList<ProductMapDirectory> directories = doc.Directories ?? [];
        IReadOnlyList<ProductMapEntryPoint> entryPoints = doc.EntryPoints ?? [];
        IReadOnlyList<ProductMapRoute> routes = doc.Routes ?? [];
        IReadOnlyList<ProductMapComponent> components = doc.Components ?? [];
        IReadOnlyList<string> patterns = doc.Patterns ?? [];
        IReadOnlyList<string> styling = framework.Styling ?? [];
        IReadOnlyList<ProductMapDependency> runtime = dependencies.Runtime ?? [];
        IReadOnlyList<ProductMapDependency> dev = dependencies.Dev ?? [];
        IReadOnlyList<string> ui = dependencies.Ui ?? [];
        IReadOnlyList<ProductMapPreviewCard> previewCards = doc.PreviewCards ?? [];

        List<StringField> fields =
        [
            new("name", doc.Name, 120, null),
            new("framework.name", framework.Name, 0, FrameworkNames),
            new("framework.version", framework.Version, 30, null),
            new("framework.language", framework.Language, 0, Languages),
            new("framework.packageManager", framework.PackageManager, 0, PackageManagers),
            new("framework.projectType", framework.ProjectType, ProseLimits.FrameworkProjectType, null),
            new("architecture", doc.Architecture, ProseLimits.Architecture, null),
            new("routingStrategy", doc.RoutingStrategy, ProseLimits.RoutingStrategy, null),
        ];

        for (int i = 0; i < styling.Count; i++)
            fields.Add(new($"framework.styling[{i}]", styling[i], 30, null));

        for (int i = 0; i < directories.Count; i++)
        {
            ProductMapDirectory? directory = directories[i];
            if (directory is null)
                continue;
            fields.Add(new($"directories[{i}].path", directory.Path, 200, null));
            fields.Add(new($"directories[{i}].purpose", directory.Purpose, ProseLimits.DirectoryPurpose, null));
        }

        for (int i = 0; i < entryPoints.Count; i++)
        {
            ProductMapEntryPoint? entryPoint = entryPoints[i];
            if (entryPoint is null)
                continue;
            fields.Add(new($"entryPoints[{i}].path", entryPoint.Path, 200, null));
            fields.Add(new($"entryPoints[{i}].role", entryPoint.Role, ProseLimits.EntryPointRole, null));
        }

        for (int i = 0; i < routes.Count; i++)
        {
            ProductMapRoute? route = routes[i];
            if (route is null)
                continue;
            fields.Add(new($"routes[{i}].path", route.Path, 0, null));
            fields.Add(new($"routes[{i}].file", route.File, 200, null));
            fields.Add(new($"routes[{i}].kind", route.Kind, 0, RouteKinds));
            fields.Add(new($"routes[{i}].method", route.Method, 0, RouteMethods, true));
            fields.Add(new($"routes[{i}].title", route.Title, ProseLimits.RouteTitle, null));
            fields.Add(new($"routes[{i}].source", route.Source, 0, Sources, true));
        }

        for (int i = 0; i < components.Count; i++)
        {
            ProductMapComponent? component = components[i];
            if (component is null)
                continue;
            fields.Add(new($"components[{i}].name", component.Name, 80, null));
            fields.Add(new($"components[{i}].path", component.Path, 200, null));
            fields.Add(new($"components[{i}].responsibility", component.Responsibility, ProseLimits.ComponentResponsibility, null));
            fields.Add(new($"components[{i}].source", component.Source, 0, Sources, true));

            IReadOnlyList<string> keyProps = component.KeyProps ?? [];
            for (int j = 0; j < keyProps.Count; j++)
                fields.Add(new($"components[{i}].keyProps[{j}]", keyProps[j], 40, null));
        }

        for (int i = 0; i < runtime.Count; i++)
        {
            ProductMapDependency? dependency = runtime[i];
            if (dependency is null)
                continue;
            fields.Add(new($"dependencies.runtime[{i}].name", dependency.Name, 100, null));
            fields.Add(new($"dependencies.runtime[{i}].version", dependency.Version, 40, null));
        }

        for (int i = 0; i < dev.Count; i++)
        {
            ProductMapDependency? dependency = dev[i];
            if (dependency is null)
                continue;
            fields.Add(new($"dependencies.dev[{i}].name", dependency.Name, 100, null));
            fields.Add(new($"dependencies.dev[{i}].version", dependency.Version, 40, null));
        }

        for (int i = 0; i < ui.Count; i++)
            fields.Add(new($"dependencies.ui[{i}]", ui[i], 100, null));

        for (int i = 0; i < patterns.Count; i++)
            fields.Add(new($"patterns[{i}]", patterns[i], ProseLimits.Pattern, null));

        foreach (KeyValuePair<string, ProductMapProvenance> entry in
                 doc.Provenance ?? new Dictionary<string, ProductMapProvenance>())
        {
            if (entry.Value is null)
                continue;
            fields.Add(new($"provenance.{entry.Key}.method", entry.Value.Method, 0, ProvenanceMethods));

            IReadOnlyList<string> sourceIds = entry.Value.SourceIds ?? [];
            for (int i = 0; i < sourceIds.Count; i++)
                fields.Add(new($"provenance.{entry.Key}.sourceIds[{i}]", sourceIds[i], 0, null));
        }

        for (int i = 0; i < previewCards.Count; i++)
        {
            ProductMapPreviewCard? card = previewCards[i];
            if (card is null)
                continue;

            fields.Add(new($"previewCards[{i}].name", card.Name, 80, null));
            fields.Add(new($"previewCards[{i}].group", card.Group, 80, null));
            fields.Add(new($"previewCards[{i}].subtitle", card.Subtitle, 160, null));
            fields.Add(new($"previewCards[{i}].viewport", card.Viewport, 20, null));
            fields.Add(new($"previewCards[{i}].path", card.Path, 200, null));
            fields.Add(new($"previewCards[{i}].text", card.Text, PreviewCardReader.MaxTextChars, null));

            IReadOnlyList<string> assets = card.Assets ?? [];
            for (int j = 0; j < assets.Count; j++)
                fields.Add(new($"previewCards[{i}].assets[{j}]", assets[j], 100, null));
        }

        // PM_V04 - enum value out of its closed set. A null is allowed only for the optional enum
        // members (route method and the provenance source kinds).
        foreach (StringField field in fields)
        {
            if (field.Allowed is null)
                continue;

            if (field.Value is null)
            {
                if (!field.EnumOptional)
                    violations.Add(new DesignViolation("PM_V04", field.Path, "is required but was absent."));
                continue;
            }

            if (!field.Allowed.Contains(field.Value))
            {
                violations.Add(new DesignViolation(
                    "PM_V04",
                    field.Path,
                    $"\"{field.Value}\" is not one of the allowed values."));
            }
        }

        // PM_V05 - string over its length limit.
        foreach (StringField field in fields)
        {
            if (field.MaxLength <= 0 || field.Value is null)
                continue;
            if (field.Value.Length > field.MaxLength)
            {
                violations.Add(new DesignViolation(
                    "PM_V05",
                    field.Path,
                    $"value is {field.Value.Length} characters; the limit is {field.MaxLength}."));
            }
        }

        // PM_V06 - control or bidirectional character.
        foreach (StringField field in fields)
        {
            if (field.Value is not null && HasControlOrBidi(field.Value))
                violations.Add(new DesignViolation("PM_V06", field.Path, "value contains a control or bidirectional character."));
        }

        // PM_V07 - URL-like string.
        foreach (StringField field in fields)
        {
            if (field.Value is not null && IsUrlLike(field.Value))
                violations.Add(new DesignViolation("PM_V07", field.Path, "value looks like a URL."));
        }

        // PM_V08 - list over its cap.
        CheckCap(violations, "directories", directories.Count, 20);
        CheckCap(violations, "entryPoints", entryPoints.Count, 30);
        CheckCap(violations, "routes", routes.Count, 300);
        CheckCap(violations, "components", components.Count, 300);
        CheckCap(violations, "framework.styling", styling.Count, 8);
        CheckCap(violations, "dependencies.runtime", runtime.Count, 150);
        CheckCap(violations, "dependencies.dev", dev.Count, 100);
        CheckCap(violations, "dependencies.ui", ui.Count, 40);
        CheckCap(violations, "patterns", patterns.Count, 12);
        CheckCap(violations, "previewCards", previewCards.Count, PreviewCardReader.MaxCards);

        for (int i = 0; i < previewCards.Count; i++)
        {
            ProductMapPreviewCard? card = previewCards[i];
            if (card is null)
                continue;
            CheckCap(violations, $"previewCards[{i}].assets", card.Assets?.Count ?? 0, PreviewCardReader.MaxAssetsPerCard);
        }

        for (int i = 0; i < components.Count; i++)
        {
            ProductMapComponent? component = components[i];
            if (component is null)
                continue;
            CheckCap(violations, $"components[{i}].keyProps", component.KeyProps?.Count ?? 0, 8);
        }

        // PM_V09 - page and layout route paths must start with '/'.
        for (int i = 0; i < routes.Count; i++)
        {
            ProductMapRoute? route = routes[i];
            if (route is null || route.Kind is not ("page" or "layout"))
                continue;
            if (route.Path is null || !route.Path.StartsWith('/'))
            {
                violations.Add(new DesignViolation(
                    "PM_V09",
                    $"routes[{i}].path",
                    "a page or layout route path must start with '/'."));
            }
        }

        // PM_V10 - route files and component paths must be relative.
        for (int i = 0; i < routes.Count; i++)
        {
            ProductMapRoute? route = routes[i];
            if (route?.File is not null && !IsRelativePath(route.File))
                violations.Add(new DesignViolation("PM_V10", $"routes[{i}].file", "route file must be a relative path."));
        }

        for (int i = 0; i < components.Count; i++)
        {
            ProductMapComponent? component = components[i];
            if (component?.Path is not null && !IsRelativePath(component.Path))
                violations.Add(new DesignViolation("PM_V10", $"components[{i}].path", "component path must be a relative path."));
        }

        for (int i = 0; i < previewCards.Count; i++)
        {
            ProductMapPreviewCard? card = previewCards[i];
            if (card?.Path is not null && !IsRelativePath(card.Path))
            {
                violations.Add(new DesignViolation(
                    "PM_V10", $"previewCards[{i}].path", "preview card path must be a relative path."));
            }

            IReadOnlyList<string> assets = card?.Assets ?? [];
            for (int j = 0; j < assets.Count; j++)
            {
                if (assets[j] is not null && !IsRelativePath(assets[j]))
                {
                    violations.Add(new DesignViolation(
                        "PM_V10", $"previewCards[{i}].assets[{j}]", "preview card asset must be a relative name."));
                }
            }
        }

        // PM_V11 - dependency name shape.
        for (int i = 0; i < runtime.Count; i++)
        {
            string? name = runtime[i]?.Name;
            if (name is null || !DependencyNamePattern.IsMatch(name))
                violations.Add(new DesignViolation("PM_V11", $"dependencies.runtime[{i}].name", "dependency name has an invalid shape."));
        }

        for (int i = 0; i < dev.Count; i++)
        {
            string? name = dev[i]?.Name;
            if (name is null || !DependencyNamePattern.IsMatch(name))
                violations.Add(new DesignViolation("PM_V11", $"dependencies.dev[{i}].name", "dependency name has an invalid shape."));
        }

        // PM_V12 - duplicate components (name + path) and routes (path + method).
        var componentKeys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < components.Count; i++)
        {
            ProductMapComponent? component = components[i];
            if (component is null)
                continue;
            string key = (component.Name ?? "") + "\u0000" + (component.Path ?? "");
            if (!componentKeys.Add(key))
                violations.Add(new DesignViolation("PM_V12", $"components[{i}]", "duplicate component name and path."));
        }

        var routeKeys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < routes.Count; i++)
        {
            ProductMapRoute? route = routes[i];
            if (route is null)
                continue;
            string key = (route.Path ?? "") + "\u0000" + (route.Method ?? "");
            if (!routeKeys.Add(key))
                violations.Add(new DesignViolation("PM_V12", $"routes[{i}]", "duplicate route path and method."));
        }

        return new DesignValidationResult(violations);
    }

    private static void CheckCap(List<DesignViolation> violations, string path, int count, int cap)
    {
        if (count > cap)
            violations.Add(new DesignViolation("PM_V08", path, $"contains {count} items; the limit is {cap}."));
    }

    internal static bool HasControlOrBidi(string value)
    {
        foreach (char c in value)
        {
            if (c <= '\u001F' || c == '\u007F')
                return true;
            if (c >= '\u202A' && c <= '\u202E')
                return true;
            if (c >= '\u2066' && c <= '\u2069')
                return true;
        }

        return false;
    }

    private static bool IsUrlLike(string value) =>
        UrlLikePattern.IsMatch(value)
        || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase);

    private static bool IsRelativePath(string path)
    {
        if (path.StartsWith('/'))
            return false;
        if (path.Contains("..", StringComparison.Ordinal))
            return false;
        if (path.Contains('\\'))
            return false;
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            return false;
        return true;
    }

    private readonly record struct StringField(
        string Path,
        string? Value,
        int MaxLength,
        IReadOnlySet<string>? Allowed,
        bool EnumOptional)
    {
        public StringField(string path, string? value, int maxLength, IReadOnlySet<string>? allowed)
            : this(path, value, maxLength, allowed, false)
        {
        }
    }
}
