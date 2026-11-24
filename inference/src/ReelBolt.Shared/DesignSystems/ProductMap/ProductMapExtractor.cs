using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReelBolt.Shared.DesignSystems.Importers;

namespace ReelBolt.Shared.DesignSystems.ProductMap;

/// <summary>
/// The outcome of one static product-map extraction: the document plus everything the extractor
/// noticed while reading its input (truncations and validator violations).
/// </summary>
public sealed record ProductMapExtraction(
    ProductMapDocument Map,
    IReadOnlyList<ImportDiagnostic> Diagnostics);

/// <summary>
/// Builds a <see cref="ProductMapDocument"/> from the text files a caller supplies (SPEC 7, 22).
/// Pure and total: it never touches IO or the network and never throws. Candidate selection is the
/// caller's job; this extractor simply consumes whatever <see cref="ImportFile"/> list it is given,
/// applies the caps and answers a deterministic document. Ordering is by path ordinal so two runs
/// over the same input serialize to the same canonical JSON.
/// </summary>
public static class ProductMapExtractor
{
    /// <summary>The largest number of files read in one extraction.</summary>
    public const int MaxFilesConsidered = 3000;

    /// <summary>The largest number of bytes read from one file.</summary>
    public const int MaxFileBytes = 256 * 1024;

    /// <summary>The largest number of bytes read across all files.</summary>
    public const int MaxTotalBytes = 24 * 1024 * 1024;

    /// <summary>The largest number of routes emitted.</summary>
    public const int MaxRoutes = 300;

    /// <summary>The largest number of components emitted.</summary>
    public const int MaxComponents = 300;

    /// <summary>The diagnostic code used whenever a cap was hit.</summary>
    public const string TruncatedCode = "TRUNCATED";

    private const int MaxDirectories = 20;
    private const int MaxEntryPoints = 30;
    private const int MaxPatterns = 12;
    private const int MaxRuntimeDependencies = 150;
    private const int MaxDevDependencies = 100;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    private static readonly RegexOptions SafeOptions =
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    private static readonly string[] LockfileNames =
        ["package-lock.json", "pnpm-lock.yaml", "yarn.lock", "bun.lockb", "bun.lock"];

    private static readonly string[] LockfileManagers = ["npm", "pnpm", "yarn", "bun", "bun"];

    private static readonly string[] ComponentExtensions = [".tsx", ".jsx", ".vue", ".svelte"];

    private static readonly Regex DependencyNamePattern = new(
        @"^[@a-z0-9][a-z0-9._/@-]{0,99}$", SafeOptions, RegexTimeout);

    private static readonly Regex ImportPattern = new(
        @"import\s+(?<clause>[^;""']+?)\s+from\s+[""'](?<path>[^""']{1,300})[""']",
        SafeOptions, RegexTimeout);

    private static readonly Regex SideEffectImportPattern = new(
        @"import\s+[""'](?<path>[^""']{1,300})[""']", SafeOptions, RegexTimeout);

    private static readonly Regex DefaultFunctionPattern = new(
        @"export\s+default\s+function\s+(?<name>[A-Za-z_$][\w$]*)", SafeOptions, RegexTimeout);

    private static readonly Regex DefaultIdentifierPattern = new(
        @"export\s+default\s+(?<name>[A-Z][\w$]*)", SafeOptions, RegexTimeout);

    private static readonly Regex NamedExportPattern = new(
        @"export\s+(?:const|function)\s+(?<name>[A-Z][\w$]*)", SafeOptions, RegexTimeout);

    private static readonly Regex RouteMethodPattern = new(
        @"export\s+(?:async\s+)?function\s+(?<method>GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)\b",
        SafeOptions, RegexTimeout);

    private static readonly Regex JsxRoutePattern = new(
        "<Route[^>]{0,200}?path=[\"']([^\"']{1,200})[\"']", SafeOptions, RegexTimeout);

    private static readonly Regex ObjectRoutePattern = new(
        "path:\\s*[\"'](/[^\"']{0,200})[\"']", SafeOptions, RegexTimeout);

    private static readonly Regex KeyPropPattern = new(
        @"(?<name>[A-Za-z_$][\w$]*)\s*(?<optional>\?)?\s*:", SafeOptions, RegexTimeout);

    private static readonly HashSet<string> UiDependencyNames = new(StringComparer.Ordinal)
    {
        "tailwindcss", "@mantine/core", "@mui/material", "styled-components", "@emotion/react",
        "sass", "@chakra-ui/react", "lucide-react", "@tabler/icons-react", "framer-motion",
    };

    /// <summary>
    /// The document built from <paramref name="files"/> and the diagnostics found on the way. Never
    /// throws; an empty or hostile input still produces a valid, empty map.
    /// </summary>
    public static ProductMapExtraction Extract(IReadOnlyList<ImportFile> files, CancellationToken ct = default)
    {
        var diagnostics = new List<ImportDiagnostic>();

        var considered = new List<SourceFile>();

        try
        {
            IReadOnlyList<ImportFile> input = files ?? [];
            List<ImportFile> ordered = input
                .Where(static file => file is not null && !string.IsNullOrEmpty(file.Path))
                .OrderBy(static file => NormalizePath(file.Path), StringComparer.Ordinal)
                .ToList();

            long totalBytes = 0;
            foreach (ImportFile file in ordered)
            {
                ct.ThrowIfCancellationRequested();
                if (considered.Count >= MaxFilesConsidered)
                {
                    AddTruncated(
                        diagnostics,
                        null,
                        $"only the first {MaxFilesConsidered} files were considered.");
                    break;
                }

                string path = NormalizePath(file.Path);
                int length = Math.Min(file.Content.Length, MaxFileBytes);
                if (length < file.Content.Length)
                {
                    AddTruncated(
                        diagnostics,
                        path,
                        $"the file exceeds the {MaxFileBytes}-byte per-file cap and was truncated.");
                }

                if (totalBytes + length > MaxTotalBytes)
                {
                    AddTruncated(
                        diagnostics,
                        path,
                        $"the {MaxTotalBytes}-byte total cap was reached; remaining files were skipped.");
                    break;
                }

                totalBytes += length;
                string text = length == 0
                    ? string.Empty
                    : Encoding.UTF8.GetString(file.Content.Span[..length]);
                considered.Add(new SourceFile(path, text));
            }

            ProductMapDocument map = Build(considered, diagnostics, ct);

            // Comments, route literals and file paths are attacker-controlled prose: every caller
            // (import, drift, step executor) gets a map that is already free of recognizable credentials.
            map = DesignImportRegistry.RedactProductMap(map, diagnostics) ?? map;

            try
            {
                DesignValidationResult validation = ProductMapValidator.Validate(map);
                foreach (DesignViolation violation in validation.Violations)
                {
                    diagnostics.Add(new ImportDiagnostic(
                        ImportDiagnosticSeverity.Error,
                        violation.Code,
                        violation.Path,
                        violation.Message));
                }
            }
            catch
            {
                // The validator is not supposed to throw; a pure extractor must not either.
            }

            return new ProductMapExtraction(map, diagnostics);
        }
        catch (OperationCanceledException)
        {
            // Cooperative cancellation (the drift worker's per-item timeout) is the one thing allowed out.
            throw;
        }
        catch
        {
            // Last-resort guard: the contract is "never throws".
            var fallback = new ProductMapDocument(
                ProductMapDocument.SchemaId,
                null,
                new ProductMapFramework("other", "", "javascript", [], "unknown", "unknown"),
                null,
                null,
                [],
                [],
                [],
                [],
                new ProductMapDependencies([], [], []),
                [],
                new Dictionary<string, ProductMapProvenance>(StringComparer.Ordinal));
            return new ProductMapExtraction(fallback, diagnostics);
        }
    }

    private static ProductMapDocument Build(
        IReadOnlyList<SourceFile> files,
        List<ImportDiagnostic> diagnostics,
        CancellationToken ct)
    {
        var runtimeDependencies = new Dictionary<string, string>(StringComparer.Ordinal);
        var devDependencies = new Dictionary<string, string>(StringComparer.Ordinal);
        string? packageName = null;

        List<SourceFile> packages = files
            .Where(static file => IsBasename(file.Path, "package.json"))
            .OrderBy(static file => Depth(file.Path))
            .ThenBy(static file => file.Path, StringComparer.Ordinal)
            .ToList();

        foreach (SourceFile package in packages)
        {
            (Dictionary<string, string> packageRuntime, Dictionary<string, string> packageDev,
                string? parsedName) = ParsePackage(package.Text);
            packageName ??= parsedName;
            foreach (KeyValuePair<string, string> entry in packageRuntime)
                runtimeDependencies.TryAdd(entry.Key, entry.Value);
            foreach (KeyValuePair<string, string> entry in packageDev)
                devDependencies.TryAdd(entry.Key, entry.Value);
        }

        var allDependencies = new Dictionary<string, string>(runtimeDependencies, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in devDependencies)
            allDependencies.TryAdd(entry.Key, entry.Value);

        (string frameworkName, string frameworkPackage) = MatchFramework(allDependencies);
        string frameworkVersion = VersionOf(allDependencies, frameworkPackage);

        bool hasTypeScript = allDependencies.ContainsKey("typescript");
        bool hasTsConfig = files.Any(static file => IsTsConfig(file.Path));
        bool hasJavaScript = files.Any(static file =>
            file.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || file.Path.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase));
        bool typed = hasTypeScript || hasTsConfig;
        string language = typed
            ? (hasJavaScript ? "mixed" : "typescript")
            : "javascript";

        List<string> styling = DetectStyling(allDependencies, files);
        List<string> uiDependencies = allDependencies.Keys
            .Where(static name =>
                UiDependencyNames.Contains(name)
                || name.StartsWith("@radix-ui/", StringComparison.Ordinal))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .Take(40)
            .ToList();

        string packageManager = DetectPackageManager(files);

        var routes = new List<ProductMapRoute>();
        var routeKeys = new HashSet<string>(StringComparer.Ordinal);
        var entryPoints = new List<ProductMapEntryPoint>();
        var components = new List<ProductMapComponent>();

        ct.ThrowIfCancellationRequested();
        Dictionary<string, HashSet<string>> importers = CollectImporters(files);

        bool sawAppRouter = false;
        bool sawPagesRouter = false;
        bool sawSvelteKit = false;
        bool sawNuxt = false;
        bool sawAstro = false;
        bool sawGenericRouter = false;

        foreach (SourceFile file in files)
        {
            ct.ThrowIfCancellationRequested();
            string[] segments = Segments(file.Path);
            if (segments.Length == 0)
                continue;

            string fileName = segments[^1];
            string stem = Stem(fileName);
            string extension = Extension(fileName);

            CollectRoutes(
                file, segments, stem, extension,
                routes, routeKeys,
                ref sawAppRouter, ref sawPagesRouter, ref sawSvelteKit,
                ref sawNuxt, ref sawAstro, ref sawGenericRouter);

            CollectEntryPoint(file, stem, entryPoints);
            CollectComponent(file, segments, stem, extension, importers, components);
        }

        routes = routes
            .OrderBy(static route => route.Path, StringComparer.Ordinal)
            .ThenBy(static route => route.Method ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static route => route.Kind, StringComparer.Ordinal)
            .ThenBy(static route => route.File, StringComparer.Ordinal)
            .ToList();

        if (routes.Count > MaxRoutes)
        {
            AddTruncated(diagnostics, null, $"only the first {MaxRoutes} routes were kept.");
            routes = routes.Take(MaxRoutes).ToList();
        }

        components = components
            .OrderByDescending(static component => component.Core)
            .ThenBy(static component => component.Path, StringComparer.Ordinal)
            .ToList();

        if (components.Count > MaxComponents)
        {
            AddTruncated(
                diagnostics, null, $"only the first {MaxComponents} components were kept.");
            components = components.Take(MaxComponents).ToList();
        }

        entryPoints = entryPoints
            .OrderBy(static entryPoint => entryPoint.Path, StringComparer.Ordinal)
            .Take(MaxEntryPoints)
            .ToList();

        ct.ThrowIfCancellationRequested();
        List<ProductMapDirectory> directories = BuildDirectories(files);
        string? routing = ChooseRouting(sawAppRouter, sawPagesRouter, sawSvelteKit, sawNuxt, sawAstro, sawGenericRouter);
        string projectType = sawAppRouter || sawPagesRouter || routes.Count > 0
            ? "web-application"
            : "library";

        List<string> patterns = BuildPatterns(
            frameworkName, language, styling, routes, components, routing);

        List<ProductMapDependency> runtime = runtimeDependencies
            .Where(static entry => DependencyNamePattern.IsMatch(entry.Key))
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .Take(MaxRuntimeDependencies)
            .Select(static entry => new ProductMapDependency(entry.Key, Sanitize(entry.Value, 40)))
            .ToList();

        List<ProductMapDependency> dev = devDependencies
            .Where(static entry => DependencyNamePattern.IsMatch(entry.Key))
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .Take(MaxDevDependencies)
            .Select(static entry => new ProductMapDependency(entry.Key, Sanitize(entry.Value, 40)))
            .ToList();

        string? name = DesignText.Sanitize(packageName, 120);

        var framework = new ProductMapFramework(
            frameworkName,
            frameworkVersion,
            language,
            styling,
            packageManager,
            projectType);

        Dictionary<string, ProductMapProvenance> provenance = BuildProvenance(
            routes, components, entryPoints, packages);

        return new ProductMapDocument(
            ProductMapDocument.SchemaId,
            name,
            framework,
            BuildArchitecture(frameworkName, routing),
            routing,
            directories,
            entryPoints,
            routes,
            components,
            new ProductMapDependencies(runtime, dev, uiDependencies),
            patterns,
            provenance);
    }

    private static void CollectRoutes(
        SourceFile file,
        string[] segments,
        string stem,
        string extension,
        List<ProductMapRoute> routes,
        HashSet<string> routeKeys,
        ref bool sawAppRouter,
        ref bool sawPagesRouter,
        ref bool sawSvelteKit,
        ref bool sawNuxt,
        ref bool sawAstro,
        ref bool sawGenericRouter)
    {
        // Next.js app router: (src/)?app/**.
        if (TryUnderRoot(segments, "app", out string[] appRelative) && appRelative.Length >= 1)
        {
            string appFile = appRelative[^1];
            string appStem = Stem(appFile);
            string appExtension = Extension(appFile);
            string[] appDirectories = appRelative[..^1];

            if (appStem == "page" && IsRouteFileExtension(appExtension))
            {
                sawAppRouter = true;
                AddRoute(
                    routes, routeKeys,
                    new ProductMapRoute(
                        RoutePath(appDirectories), file.Path, "page", null, null, false, "static"));
            }
            else if (appStem == "layout" && IsRouteFileExtension(appExtension))
            {
                sawAppRouter = true;
                // A layout shares its path with the page beside it; "ANY" keeps the validator's
                // (path, method) uniqueness rule satisfied while still recording the kind.
                AddRoute(
                    routes, routeKeys,
                    new ProductMapRoute(
                        RoutePath(appDirectories), file.Path, "layout", "ANY", null, false, "static"));
            }
            else if (appStem == "route" && appExtension is ".ts" or ".js")
            {
                sawAppRouter = true;
                string path = RoutePath(appDirectories);
                List<string> methods = RouteMethods(file.Text);
                if (methods.Count == 0)
                {
                    AddRoute(
                        routes, routeKeys,
                        new ProductMapRoute(path, file.Path, "api", "ANY", null, false, "static"));
                }
                else
                {
                    foreach (string method in methods)
                    {
                        AddRoute(
                            routes, routeKeys,
                            new ProductMapRoute(path, file.Path, "api", method, null, false, "static"));
                    }
                }
            }
        }

        // Astro: src/pages/** (checked before the Next pages router so .astro/.md never collide).
        if (segments.Length >= 2 && segments[0] == "src" && segments[1] == "pages"
            && extension is ".astro" or ".md" or ".mdx" or ".html")
        {
            sawAstro = true;
            string[] directories = segments[2..^1];
            AddRoute(
                routes, routeKeys,
                new ProductMapRoute(
                    RoutePathWithStem(directories, stem), file.Path, "page", null, null, false, "static"));
        }

        // Nuxt: (src/)?pages/**/*.vue.
        if (extension == ".vue"
            && (segments[0] == "pages"
                || (segments.Length >= 2 && segments[0] == "src" && segments[1] == "pages")))
        {
            sawNuxt = true;
            string[] directories = segments[0] == "pages" ? segments[1..^1] : segments[2..^1];
            AddRoute(
                routes, routeKeys,
                new ProductMapRoute(
                    RoutePathWithStem(directories, stem), file.Path, "page", null, null, false, "static"));
        }

        // Next.js pages router: (src/)?pages/** except the framework files.
        if (TryUnderRoot(segments, "pages", out string[] pagesRelative) && pagesRelative.Length >= 1)
        {
            string pagesFile = pagesRelative[^1];
            string pagesStem = Stem(pagesFile);
            string pagesExtension = Extension(pagesFile);
            if (pagesExtension is ".tsx" or ".jsx" or ".ts" or ".js" or ".mdx"
                && pagesStem is not ("_app" or "_document" or "_error"))
            {
                sawPagesRouter = true;
                string[] directories = pagesRelative[..^1];
                bool isApi = directories.Length > 0 && directories[0] == "api";
                AddRoute(
                    routes, routeKeys,
                    new ProductMapRoute(
                        RoutePathWithStem(directories, pagesStem),
                        file.Path,
                        isApi ? "api" : "page",
                        isApi ? "ANY" : null,
                        null,
                        false,
                        "static"));
            }
        }

        // SvelteKit: (src/)?routes/**/+page.* and +server.*.
        if ((segments[0] == "src" && segments.Length >= 2 && segments[1] == "routes")
            || segments[0] == "routes")
        {
            string[] routesRelative = segments[0] == "src" ? segments[2..] : segments[1..];
            if (routesRelative.Length >= 1)
            {
                string routesFile = routesRelative[^1];
                string[] directories = routesRelative[..^1];
                if (routesFile.StartsWith("+page.", StringComparison.Ordinal))
                {
                    sawSvelteKit = true;
                    AddRoute(
                        routes, routeKeys,
                        new ProductMapRoute(
                            RoutePath(directories), file.Path, "page", null, null, false, "static"));
                }
                else if (routesFile.StartsWith("+server.", StringComparison.Ordinal))
                {
                    sawSvelteKit = true;
                    AddRoute(
                        routes, routeKeys,
                        new ProductMapRoute(
                            RoutePath(directories), file.Path, "api", "ANY", null, false, "static"));
                }
            }
        }

        // React Router / generic: rout* or App.* files carrying a declared path.
        if (file.Path.Contains("rout", StringComparison.OrdinalIgnoreCase)
            || file.Path.Contains("App.", StringComparison.Ordinal))
        {
            if (TryJsxRoute(file.Text, out string jsxPath))
            {
                sawGenericRouter = true;
                AddRoute(
                    routes, routeKeys,
                    new ProductMapRoute(jsxPath, file.Path, "page", null, null, false, "static"));
            }

            foreach (string objectPath in ObjectRoutePaths(file.Text))
            {
                sawGenericRouter = true;
                AddRoute(
                    routes, routeKeys,
                    new ProductMapRoute(objectPath, file.Path, "page", null, null, false, "static"));
            }
        }
    }

    private static void CollectEntryPoint(
        SourceFile file,
        string stem,
        List<ProductMapEntryPoint> entryPoints)
    {
        string path = file.Path;
        string? role = null;

        if (IsUnder(file.Path, "app") && stem == "layout")
            role = "root layout";
        else if (path.StartsWith("src/main.", StringComparison.Ordinal))
            role = "application entry";
        else if (path.StartsWith("src/index.", StringComparison.Ordinal))
            role = "application entry";
        else if (path.StartsWith("pages/_app.", StringComparison.Ordinal))
            role = "pages application shell";
        else if (path.StartsWith("src/App.", StringComparison.Ordinal))
            role = "application entry";
        else if (path is "server.ts" or "server.js")
            role = "server entry";

        if (role is not null)
            entryPoints.Add(new ProductMapEntryPoint(path, role));
    }

    private static void CollectComponent(
        SourceFile file,
        string[] segments,
        string stem,
        string extension,
        Dictionary<string, HashSet<string>> importers,
        List<ProductMapComponent> components)
    {
        if (!ComponentExtensions.Contains(extension))
            return;

        string[] directories = segments[..^1];
        bool underComponents = directories.Any(static segment => segment is "components" or "ui");
        if (!underComponents)
            return;

        string name = ExtractComponentName(file.Text, stem, directories);
        List<string> keyProps = ExtractKeyProps(file.Text, name);
        string responsibility = ExtractResponsibility(file.Text);

        bool inUi = directories.Any(static segment => segment == "ui");
        bool importedOften = importers.TryGetValue(name, out HashSet<string>? importerSet)
            && importerSet.Count(path => path != file.Path) >= 3;

        components.Add(new ProductMapComponent(
            name,
            file.Path,
            responsibility,
            keyProps,
            inUi || importedOften,
            "static"));
    }

    private static Dictionary<string, HashSet<string>> CollectImporters(
        IReadOnlyList<SourceFile> files)
    {
        var importers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (SourceFile file in files)
        {
            foreach (string match in ImportNames(file.Text))
            {
                if (!importers.TryGetValue(match, out HashSet<string>? set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    importers[match] = set;
                }

                set.Add(file.Path);
            }
        }

        return importers;
    }

    private static IEnumerable<string> ImportNames(string text)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in ImportPattern.Matches(text))
        {
            AddClauseNames(match.Groups["clause"].Value, names);
            string? pathName = SpecifierBasename(match.Groups["path"].Value);
            if (pathName is not null)
                names.Add(pathName);
        }

        foreach (Match match in SideEffectImportPattern.Matches(text))
        {
            string? pathName = SpecifierBasename(match.Groups["path"].Value);
            if (pathName is not null)
                names.Add(pathName);
        }

        return names;
    }

    private static void AddClauseNames(string clause, HashSet<string> names)
    {
        foreach (string raw in clause.Split(','))
        {
            string part = raw.Trim().Trim('{', '}', ' ');
            if (part.Length == 0 || part[0] == '*')
                continue;
            if (part.StartsWith("type ", StringComparison.Ordinal))
                part = part[5..].Trim();

            int asIndex = part.IndexOf(" as ", StringComparison.Ordinal);
            string token = (asIndex >= 0 ? part[..asIndex] : part).Trim().Trim('{', '}', ' ');
            if (IsIdentifier(token))
                names.Add(token);
        }
    }

    private static string? SpecifierBasename(string specifier)
    {
        int slash = specifier.LastIndexOf('/');
        string name = slash >= 0 ? specifier[(slash + 1)..] : specifier;
        int dot = name.LastIndexOf('.');
        if (dot > 0)
            name = name[..dot];
        return IsIdentifier(name) ? name : null;
    }

    private static string ExtractComponentName(string text, string stem, string[] directories)
    {
        Match match = DefaultFunctionPattern.Match(text);
        if (match.Success)
            return SanitizeName(match.Groups["name"].Value);

        match = DefaultIdentifierPattern.Match(text);
        if (match.Success)
            return SanitizeName(match.Groups["name"].Value);

        match = NamedExportPattern.Match(text);
        if (match.Success)
            return SanitizeName(match.Groups["name"].Value);

        string fileStem = stem == "index" && directories.Length > 0 ? directories[^1] : stem;
        return SanitizeName(PascalCase(fileStem));
    }

    private static List<string> ExtractKeyProps(string text, string name)
    {
        var required = new List<string>();
        var optional = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var pattern = new Regex(
                @"(?:interface|type)\s+" + Regex.Escape(name) + @"Props\s*(?:=)?\s*\{(?<body>[^}]*)\}",
                SafeOptions,
                RegexTimeout);
            Match match = pattern.Match(text);
            if (!match.Success)
                return required;

            foreach (Match prop in KeyPropPattern.Matches(match.Groups["body"].Value))
            {
                string propName = prop.Groups["name"].Value;
                if (!seen.Add(propName))
                    continue;
                bool isOptional = prop.Groups["optional"].Success;
                if (isOptional)
                {
                    if (optional.Count < 8)
                        optional.Add(propName);
                }
                else if (required.Count < 8)
                {
                    required.Add(propName);
                }
            }
        }
        catch
        {
            // Malformed input simply yields no key props.
        }

        var result = new List<string>(required);
        foreach (string prop in optional)
        {
            if (result.Count >= 8)
                break;
            result.Add(prop);
        }

        return result;
    }

    private static string ExtractResponsibility(string text)
    {
        string? comment = null;

        int jsdoc = text.IndexOf("/**", StringComparison.Ordinal);
        if (jsdoc >= 0)
        {
            int end = text.IndexOf("*/", jsdoc + 3, StringComparison.Ordinal);
            if (end > jsdoc)
                comment = text[(jsdoc + 3)..end];
        }

        if (comment is null)
        {
            var builder = new StringBuilder();
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    builder.AppendLine(trimmed[2..]);
                else if (builder.Length > 0)
                    break;
                else if (trimmed.Length > 0)
                    break;
            }

            if (builder.Length > 0)
                comment = builder.ToString();
        }

        if (comment is null)
            return string.Empty;

        string cleaned = comment.Replace('*', ' ');
        int stop = -1;
        for (int i = 0; i < cleaned.Length; i++)
        {
            char c = cleaned[i];
            if (c is '.' or '!' or '?')
            {
                stop = i + 1;
                break;
            }
        }

        string sentence = stop > 0 ? cleaned[..stop] : cleaned;
        return SafeText(sentence, 160);
    }

    private static List<ProductMapDirectory> BuildDirectories(IReadOnlyList<SourceFile> files)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (SourceFile file in files)
        {
            string[] segments = Segments(file.Path);
            for (int i = 1; i < segments.Length; i++)
                paths.Add(string.Join('/', segments[..i]));
        }

        return paths
            .Take(MaxDirectories)
            .Select(static path => new ProductMapDirectory(
                SafeText(path, 200),
                DirectoryPurpose(path)))
            .ToList();
    }

    private static string DirectoryPurpose(string path)
    {
        string[] segments = path.Split('/');
        if (segments.Any(static segment => segment is "components" or "ui"))
            return "UI components";
        if (segments.Any(static segment => segment == "api"))
            return "API routes";
        if (segments.Length > 0 && segments[0] == "app")
            return "application routes";
        if (segments.Length > 0 && segments[0] == "pages")
            return "page routes";
        if (segments.Length > 0 && segments[0] == "src")
            return "source";
        if (segments.Length > 0 && segments[0] == "lib")
            return "shared library";
        return "source";
    }

    private static Dictionary<string, ProductMapProvenance> BuildProvenance(
        IReadOnlyList<ProductMapRoute> routes,
        IReadOnlyList<ProductMapComponent> components,
        IReadOnlyList<ProductMapEntryPoint> entryPoints,
        IReadOnlyList<SourceFile> packages)
    {
        var provenance = new Dictionary<string, ProductMapProvenance>(StringComparer.Ordinal)
        {
            ["framework"] = new("static", packages.Select(static file => file.Path).ToList()),
            ["dependencies"] = new("static", packages.Select(static file => file.Path).ToList()),
            ["routes"] = new(
                "static",
                routes.Select(static route => route.File)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static path => path, StringComparer.Ordinal)
                    .ToList()),
            ["components"] = new(
                "static",
                components.Select(static component => component.Path)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static path => path, StringComparer.Ordinal)
                    .ToList()),
            ["entryPoints"] = new(
                "static",
                entryPoints.Select(static entryPoint => entryPoint.Path)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static path => path, StringComparer.Ordinal)
                    .ToList()),
        };

        return provenance;
    }

    private static List<string> BuildPatterns(
        string frameworkName,
        string language,
        IReadOnlyList<string> styling,
        IReadOnlyList<ProductMapRoute> routes,
        IReadOnlyList<ProductMapComponent> components,
        string? routing)
    {
        var patterns = new List<string>();
        void Add(string value)
        {
            if (patterns.Count < MaxPatterns && !patterns.Contains(value, StringComparer.Ordinal))
                patterns.Add(value);
        }

        Add(frameworkName);
        Add(language);
        if (routing is not null)
            Add(routing);
        if (components.Count > 0)
            Add("component-inventory");
        if (routes.Any(static route => route.Kind == "api"))
            Add("rest-api");
        if (styling.Contains("tailwind", StringComparer.Ordinal))
            Add("tailwind-utility");
        if (styling.Contains("shadcn", StringComparer.Ordinal))
            Add("shadcn-components");

        return patterns;
    }

    private static string? ChooseRouting(
        bool appRouter,
        bool pagesRouter,
        bool svelteKit,
        bool nuxt,
        bool astro,
        bool generic)
    {
        if (appRouter)
            return "app-router";
        if (pagesRouter)
            return "pages-router";
        if (svelteKit)
            return "sveltekit";
        if (nuxt)
            return "nuxt";
        if (astro)
            return "astro";
        if (generic)
            return "react-router";
        return null;
    }

    private static string? BuildArchitecture(string frameworkName, string? routing)
    {
        string value = routing is null ? frameworkName : $"{frameworkName} {routing}";
        return SafeTextOrNull(value, 600);
    }

    private static List<string> DetectStyling(
        IReadOnlyDictionary<string, string> dependencies,
        IReadOnlyList<SourceFile> files)
    {
        var styling = new List<string>();

        void Add(string value)
        {
            if (!styling.Contains(value, StringComparer.Ordinal) && styling.Count < 8)
                styling.Add(value);
        }

        if (dependencies.ContainsKey("tailwindcss"))
            Add("tailwind");
        if (dependencies.ContainsKey("@mantine/core"))
            Add("mantine");
        if (dependencies.ContainsKey("@mui/material"))
            Add("mui");
        if (dependencies.ContainsKey("styled-components"))
            Add("styled-components");
        if (dependencies.ContainsKey("@emotion/react"))
            Add("emotion");
        if (dependencies.ContainsKey("sass"))
            Add("sass");
        if (dependencies.ContainsKey("@chakra-ui/react"))
            Add("chakra");
        if (files.Any(static file => IsBasename(file.Path, "components.json")))
            Add("shadcn");

        return styling;
    }

    private static string DetectPackageManager(IReadOnlyList<SourceFile> files)
    {
        for (int i = 0; i < LockfileNames.Length; i++)
        {
            if (files.Any(file => IsBasename(file.Path, LockfileNames[i])))
                return LockfileManagers[i];
        }

        return "unknown";
    }

    private static (string Name, string Package) MatchFramework(
        IReadOnlyDictionary<string, string> dependencies)
    {
        if (dependencies.ContainsKey("next"))
            return ("next", "next");

        string? remix = dependencies.Keys
            .Where(static key => key.StartsWith("@remix-run/", StringComparison.Ordinal))
            .OrderBy(static key => key, StringComparer.Ordinal)
            .FirstOrDefault();
        if (remix is not null)
            return ("remix", remix);

        if (dependencies.ContainsKey("nuxt"))
            return ("nuxt", "nuxt");
        if (dependencies.ContainsKey("@sveltejs/kit"))
            return ("svelte", "@sveltejs/kit");
        if (dependencies.ContainsKey("svelte"))
            return ("svelte", "svelte");
        if (dependencies.ContainsKey("@angular/core"))
            return ("angular", "@angular/core");
        if (dependencies.ContainsKey("astro"))
            return ("astro", "astro");
        if (dependencies.ContainsKey("vue"))
            return ("vue", "vue");
        if (dependencies.ContainsKey("vite"))
            return ("vite", "vite");
        if (dependencies.ContainsKey("react-scripts"))
            return ("react", "react-scripts");
        if (dependencies.ContainsKey("react"))
            return ("react", "react");
        if (dependencies.ContainsKey("express"))
            return ("express", "express");
        if (dependencies.ContainsKey("fastify"))
            return ("other", "fastify");

        return ("other", string.Empty);
    }

    private static string VersionOf(IReadOnlyDictionary<string, string> dependencies, string package)
    {
        if (package.Length == 0 || !dependencies.TryGetValue(package, out string? raw))
            return string.Empty;

        string value = raw.Trim();
        while (value.Length > 0 && value[0] is '^' or '~' or '>' or '=' or '<' or 'v')
            value = value[1..];

        int space = value.IndexOf(' ');
        if (space > 0)
            value = value[..space];

        return SafeText(value, 30);
    }

    private static (Dictionary<string, string> Runtime, Dictionary<string, string> Dev, string? Name)
        ParsePackage(string text)
    {
        var runtime = new Dictionary<string, string>(StringComparer.Ordinal);
        var dev = new Dictionary<string, string>(StringComparer.Ordinal);
        string? name = null;

        try
        {
            JsonNode? node = JsonNode.Parse(text);
            if (node is not JsonObject obj)
                return (runtime, dev, name);

            if (obj["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out string? parsed))
                name = parsed;

            ReadDependencies(obj["dependencies"] as JsonObject, runtime);
            ReadDependencies(obj["devDependencies"] as JsonObject, dev);
        }
        catch
        {
            // A malformed package.json yields no dependencies; the extractor stays total.
        }

        return (runtime, dev, name);
    }

    private static void ReadDependencies(JsonObject? source, Dictionary<string, string> target)
    {
        if (source is null)
            return;

        foreach (KeyValuePair<string, JsonNode?> entry in source)
        {
            if (entry.Value is JsonValue value && value.TryGetValue<string>(out string? text)
                && text is not null)
            {
                target[entry.Key] = text;
            }
        }
    }

    private static List<string> RouteMethods(string text)
    {
        var methods = new List<string>();
        foreach (Match match in RouteMethodPattern.Matches(text))
        {
            string method = match.Groups["method"].Value;
            if (method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE")
            {
                if (!methods.Contains(method, StringComparer.Ordinal))
                    methods.Add(method);
            }
        }

        methods.Sort(StringComparer.Ordinal);
        return methods;
    }

    private static bool TryJsxRoute(string text, out string path)
    {
        path = string.Empty;
        try
        {
            Match match = JsxRoutePattern.Match(text);
            if (match.Success && match.Groups[1].Value.StartsWith('/'))
            {
                path = match.Groups[1].Value;
                return true;
            }
        }
        catch
        {
            // A regex timeout means "no route", never a throw.
        }

        return false;
    }

    private static IEnumerable<string> ObjectRoutePaths(string text)
    {
        var paths = new List<string>();
        try
        {
            foreach (Match match in ObjectRoutePattern.Matches(text))
            {
                string value = match.Groups[1].Value;
                if (value.StartsWith('/'))
                    paths.Add(value);
            }
        }
        catch
        {
            // A regex timeout means "no routes", never a throw.
        }

        return paths.Distinct(StringComparer.Ordinal).OrderBy(static path => path, StringComparer.Ordinal);
    }

    private static void AddRoute(
        List<ProductMapRoute> routes,
        HashSet<string> keys,
        ProductMapRoute route)
    {
        string key = route.Path + "\u0000" + (route.Method ?? string.Empty);
        if (keys.Add(key))
            routes.Add(route);
    }

    private static string RoutePath(string[] directories)
    {
        var parts = new List<string>();
        foreach (string segment in directories)
        {
            string? converted = ConvertSegment(segment);
            if (converted is not null)
                parts.Add(converted);
        }

        return parts.Count == 0 ? "/" : "/" + string.Join('/', parts);
    }

    private static string RoutePathWithStem(string[] directories, string stem)
    {
        var parts = new List<string>(directories);
        if (stem != "index")
            parts.Add(stem);
        return RoutePath([.. parts]);
    }

    private static string? ConvertSegment(string segment)
    {
        if (segment.Length == 0 || segment[0] == '_' || segment[0] == '@')
            return null;
        if (segment.Length >= 2 && segment[0] == '(' && segment[^1] == ')')
            return null;

        if (segment.StartsWith("[[...", StringComparison.Ordinal) && segment.EndsWith("]]", StringComparison.Ordinal))
            return "*" + segment[5..^2] + "?";
        if (segment.StartsWith("[...", StringComparison.Ordinal) && segment.EndsWith("]", StringComparison.Ordinal))
            return "*" + segment[4..^1];
        if (segment[0] == '[' && segment[^1] == ']')
            return ":" + segment[1..^1];

        return segment;
    }

    private static bool TryUnderRoot(string[] segments, string root, out string[] relative)
    {
        if (segments.Length >= 1 && segments[0] == root)
        {
            relative = segments[1..];
            return true;
        }

        if (segments.Length >= 2 && segments[0] == "src" && segments[1] == root)
        {
            relative = segments[2..];
            return true;
        }

        relative = [];
        return false;
    }

    private static bool IsUnder(string path, string root)
    {
        string[] segments = Segments(path);
        return segments.Length >= 1 && (segments[0] == root
            || (segments.Length >= 2 && segments[0] == "src" && segments[1] == root));
    }

    private static bool IsRouteFileExtension(string extension) =>
        extension is ".tsx" or ".jsx" or ".ts" or ".js" or ".mdx";

    private static bool IsBasename(string path, string name)
    {
        int slash = path.LastIndexOf('/');
        string basename = slash >= 0 ? path[(slash + 1)..] : path;
        return string.Equals(basename, name, StringComparison.Ordinal);
    }

    private static bool IsTsConfig(string path)
    {
        int slash = path.LastIndexOf('/');
        string basename = slash >= 0 ? path[(slash + 1)..] : path;
        return basename.StartsWith("tsconfig", StringComparison.Ordinal)
            && basename.EndsWith(".json", StringComparison.Ordinal);
    }

    private static int Depth(string path)
    {
        int depth = 0;
        foreach (char c in path)
        {
            if (c == '/')
                depth++;
        }

        return depth;
    }

    private static string[] Segments(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string Stem(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot <= 0 ? fileName : fileName[..dot];
    }

    private static string Extension(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot < 0 ? string.Empty : fileName[dot..].ToLowerInvariant();
    }

    private static string NormalizePath(string path)
    {
        string normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.TrimStart('/');
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0)
            return false;
        char first = value[0];
        if (!char.IsLetter(first) && first != '_' && first != '$')
            return false;
        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '$')
                return false;
        }

        return true;
    }

    private static string PascalCase(string value)
    {
        var builder = new StringBuilder();
        foreach (string part in value.Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            builder.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1)
                builder.Append(part[1..]);
        }

        return builder.Length == 0 ? "Component" : builder.ToString();
    }

    private static string SanitizeName(string value) =>
        DesignText.Sanitize(value, 80) ?? "Component";

    private static string Sanitize(string? value, int maxLength) =>
        DesignText.Sanitize(value, maxLength) ?? string.Empty;

    private static string SafeText(string value, int maxLength) =>
        DesignText.Sanitize(value, maxLength) ?? string.Empty;

    private static string? SafeTextOrNull(string value, int maxLength) =>
        DesignText.Sanitize(value, maxLength);

    private static void AddTruncated(List<ImportDiagnostic> diagnostics, string? path, string message) =>
        diagnostics.Add(new ImportDiagnostic(
            ImportDiagnosticSeverity.Warn, TruncatedCode, path ?? string.Empty, message));

    private sealed record SourceFile(string Path, string Text);
}
