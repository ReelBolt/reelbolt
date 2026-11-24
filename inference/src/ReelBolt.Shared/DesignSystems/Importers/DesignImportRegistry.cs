using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems.Merge;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// The ordered list of MVP importers and the one entry point that runs them all. <see cref="ImportAll"/>
/// is pure: it never touches IO, recomputes every digest from the supplied bytes rather than trusting
/// the caller, and never throws — anything an importer or a hostile file can do becomes a diagnostic.
/// </summary>
public static class DesignImportRegistry
{
    /// <summary>The six MVP importers, in priority order.</summary>
    public static IReadOnlyList<IDesignImporter> All { get; } =
    [
        new DtcgImporter(),
        new CssThemeImporter(),
        new DesignMdImporter(),
        new TokensStudioImporter(),
        new ShadcnRegistryImporter(),
        new StorybookIndexImporter(),
        new StyleDictionaryImporter(),
        new TailwindV3ConfigImporter(),
    ];

    /// <summary>The most diagnostics one import returns (plus one <c>DIAGNOSTICS_TRUNCATED</c> summary).</summary>
    public const int MaxDiagnostics = 200;

    private const int MaxDiagnosticMessageLength = 512;
    private const int MaxDiagnosticPathLength = 256;

    /// <summary>Runs every registered importer over <paramref name="files"/>.</summary>
    public static DesignImportResult ImportAll(IReadOnlyList<ImportFile> files, CancellationToken ct = default) => ImportAll(files, All, ct);

    /// <summary>
    /// Runs <paramref name="importers"/> in order over <paramref name="files"/>, merges whatever
    /// they produced and redacts secrets from the result and from every diagnostic and source path.
    /// </summary>
    public static DesignImportResult ImportAll(
        IReadOnlyList<ImportFile> files, IReadOnlyList<IDesignImporter> importers, CancellationToken ct = default)
    {
        if (files is null || files.Count == 0)
            return new DesignImportResult(null, null, Array.Empty<ImportSourceRef>(), Array.Empty<ImportDiagnostic>());

        IReadOnlyList<IDesignImporter> registry = importers ?? All;

        var sources = new List<ImportSourceRef>();
        var diagnostics = new List<ImportDiagnostic>();
        var designs = new List<JsonObject>();
        var productMaps = new List<ProductMapDocument>();

        foreach (IDesignImporter importer in registry)
        {
            ct.ThrowIfCancellationRequested();
            if (importer is null)
                continue;

            var mine = new List<ImportFile>();
            foreach (ImportFile file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (file is null)
                    continue;

                try
                {
                    ReadOnlySpan<byte> content = file.Content.Span;
                    ReadOnlySpan<byte> head = content[..Math.Min(ImportLimits.HeadBytes, content.Length)];
                    if (importer.CanRead(file.Path, head))
                        mine.Add(file);
                }
                catch (Exception exception)
                {
                    diagnostics.Add(new ImportDiagnostic(
                        ImportDiagnosticSeverity.Error,
                        "IMPORT_INTERNAL",
                        string.Empty,
                        exception.GetType().Name));
                }
            }

            if (mine.Count == 0)
                continue;

            DesignImportResult result;
            try
            {
                result = importer is IContextualDesignImporter contextual
                    ? contextual.Import(mine, files)
                    : importer.Import(mine);
            }
            catch (Exception exception)
            {
                diagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Error,
                    "IMPORT_INTERNAL",
                    string.Empty,
                    exception.GetType().Name));
                continue;
            }

            foreach (ImportFile file in mine)
            {
                sources.Add(new ImportSourceRef(
                    file.Path,
                    importer.Id,
                    Sha256Hex(file.Content.Span),
                    file.ProjectFileId));
            }

            if (result.Design is not null)
                designs.Add(result.Design);

            if (result.ProductMap is not null)
                productMaps.Add(result.ProductMap);

            if (result.Diagnostics is not null)
                diagnostics.AddRange(result.Diagnostics);
        }

        ct.ThrowIfCancellationRequested();
        JsonObject? design = designs.Count > 0 ? DesignDocumentMerger.Merge(null, designs, ct) : null;

        ProductMapDocument? productMap = null;
        if (productMaps.Count > 0)
        {
            productMap = productMaps[0];
            for (int i = 1; i < productMaps.Count; i++)
                productMap = ProductMapMerger.Merge(productMap, productMaps[i], ct);
        }

        if (design is null && productMap is null)
        {
            diagnostics.Add(new ImportDiagnostic(
                ImportDiagnosticSeverity.Error,
                "IMPORT_NOTHING_RECOGNIZED",
                string.Empty,
                "no registered importer recognized any input file"));
        }

        return RedactResult(new DesignImportResult(design, productMap, sources, diagnostics));
    }

    /// <summary>
    /// The one redaction pass over an import result: every string the design carries (values and
    /// keys), the product map, every diagnostic message and path (capped, errors first) and every
    /// source path. Readers that build their own result call this so they cannot drift from
    /// <see cref="ImportAll"/>.
    /// </summary>
    internal static DesignImportResult RedactResult(DesignImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var diagnostics = new List<ImportDiagnostic>();
        if (result.Diagnostics is not null)
            diagnostics.AddRange(result.Diagnostics);

        JsonObject? design = result.Design;
        if (design is not null)
            RedactNode(design, string.Empty, diagnostics);

        ProductMapDocument? productMap = RedactProductMap(result.ProductMap, diagnostics);

        var redactedDiagnostics = new List<ImportDiagnostic>(Math.Min(diagnostics.Count, MaxDiagnostics + 1));
        foreach (ImportDiagnostic diagnostic in CapDiagnostics(diagnostics))
        {
            (string message, _) = SecretRedactor.Redact(diagnostic.Message ?? string.Empty);
            message = Clamp(message, MaxDiagnosticMessageLength);
            (string diagnosticPath, _) = SecretRedactor.Redact(diagnostic.Path ?? string.Empty);
            diagnosticPath = Clamp(diagnosticPath, MaxDiagnosticPathLength);
            redactedDiagnostics.Add(diagnostic with { Message = message, Path = diagnosticPath });
        }

        IReadOnlyList<ImportSourceRef> sources = result.Sources ?? [];
        var redactedSources = new List<ImportSourceRef>(sources.Count);
        foreach (ImportSourceRef source in sources)
        {
            (string path, _) = SecretRedactor.Redact(source.Path ?? string.Empty);
            redactedSources.Add(source with { Path = path });
        }

        return new DesignImportResult(design, productMap, redactedSources, redactedDiagnostics);
    }

    /// <summary>
    /// Keeps at most <see cref="MaxDiagnostics"/> diagnostics (errors first, original order otherwise) and
    /// appends one <c>DIAGNOSTICS_TRUNCATED</c> summary, so a hostile file cannot inflate the response.
    /// </summary>
    private static List<ImportDiagnostic> CapDiagnostics(List<ImportDiagnostic> diagnostics)
    {
        if (diagnostics.Count <= MaxDiagnostics)
            return diagnostics;

        var keep = new bool[diagnostics.Count];
        int kept = 0;
        for (int i = 0; i < diagnostics.Count && kept < MaxDiagnostics; i++)
        {
            if (diagnostics[i].Severity == ImportDiagnosticSeverity.Error)
            {
                keep[i] = true;
                kept++;
            }
        }

        for (int i = 0; i < diagnostics.Count && kept < MaxDiagnostics; i++)
        {
            if (!keep[i])
            {
                keep[i] = true;
                kept++;
            }
        }

        var capped = new List<ImportDiagnostic>(MaxDiagnostics + 1);
        for (int i = 0; i < diagnostics.Count; i++)
        {
            if (keep[i])
                capped.Add(diagnostics[i]);
        }

        capped.Add(new ImportDiagnostic(
            ImportDiagnosticSeverity.Warn,
            "DIAGNOSTICS_TRUNCATED",
            string.Empty,
            $"{diagnostics.Count - kept} more diagnostic(s) were omitted"));
        return capped;
    }

    private static string Clamp(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        if (value.Length <= max)
            return value;

        int cut = max - 3;
        if (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
            cut--;

        return value[..cut] + "...";
    }

    /// <summary>Redacts every string (values and keys) of <paramref name="map"/>, noting each hit in <paramref name="diagnostics"/>.</summary>
    internal static ProductMapDocument? RedactProductMap(ProductMapDocument? map, List<ImportDiagnostic> diagnostics)
    {
        if (map is null)
            return null;

        JsonObject node = ProductMapJson.ToNode(map);
        RedactNode(node, string.Empty, diagnostics);
        return ProductMapJson.Parse(node.ToJsonString()) ?? map;
    }

    private static void RedactNode(JsonNode? node, string path, List<ImportDiagnostic> diagnostics)
    {
        switch (node)
        {
            case JsonObject obj:
                RedactKeys(obj, path, diagnostics);
                foreach (string key in obj.Select(static property => property.Key).ToList())
                {
                    JsonNode? child = obj[key];
                    string childPath = path.Length == 0 ? key : path + "." + key;
                    if (TryRedactString(child, childPath, diagnostics, out string? redacted))
                        obj[key] = redacted;
                    else
                        RedactNode(child, childPath, diagnostics);
                }

                break;

            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    JsonNode? item = array[i];
                    string childPath = path + "[" + i + "]";
                    if (TryRedactString(item, childPath, diagnostics, out string? redacted))
                        array[i] = redacted;
                    else
                        RedactNode(item, childPath, diagnostics);
                }

                break;
        }
    }

    /// <summary>
    /// Renames any property whose key looks like a credential to <c>redacted</c> (suffixed <c>-2</c>, <c>-3</c>…
    /// on collision, which keeps the key inside the document path alphabet), preserving property order.
    /// </summary>
    private static void RedactKeys(JsonObject obj, string path, List<ImportDiagnostic> diagnostics)
    {
        HashSet<string>? renamed = null;
        foreach (KeyValuePair<string, JsonNode?> property in obj)
        {
            if (SecretRedactor.Redact(property.Key).count > 0)
                (renamed ??= new HashSet<string>(StringComparer.Ordinal)).Add(property.Key);
        }

        if (renamed is null)
            return;

        var items = obj.Select(static property => property).ToList();
        var taken = new HashSet<string>(items.Select(static item => item.Key).Where(key => !renamed.Contains(key)), StringComparer.Ordinal);
        obj.Clear();
        foreach (KeyValuePair<string, JsonNode?> item in items)
        {
            string key = item.Key;
            if (renamed.Contains(key))
            {
                diagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Warn,
                    "SECRET_REDACTED",
                    path,
                    "redacted 1 secret(s) in a key"));
                key = DesignKeys.Unique(taken, "redacted");
            }
            else
            {
                taken.Add(key);
            }

            obj.Add(key, item.Value);
        }
    }

    private static bool TryRedactString(JsonNode? node, string path, List<ImportDiagnostic> diagnostics, out string? redacted)
    {
        redacted = null;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
            return false;

        (string text, int count) = SecretRedactor.Redact(value.GetValue<string>());
        if (count <= 0)
            return false;

        diagnostics.Add(new ImportDiagnostic(
            ImportDiagnosticSeverity.Warn,
            "SECRET_REDACTED",
            path,
            $"redacted {count} secret(s)"));
        redacted = text;
        return true;
    }

    private static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
