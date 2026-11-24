using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// Reads a Style Dictionary <c>config.json</c> as inert data: the config names token files through its
/// <c>source</c> globs and those files are handed to the <c>dtcg</c> importer. A JavaScript or
/// TypeScript config is NEVER executed, required, resolved or decoded as code — it only produces a
/// warning and a fallback that offers every JSON file the <c>dtcg</c> importer recognizes.
/// </summary>
public sealed class StyleDictionaryImporter : IContextualDesignImporter
{
    /// <summary>The importer identifier used in provenance and in <see cref="ImportSourceRef"/>.</summary>
    public string Id => "style-dictionary";

    private const int MaxConfigBytes = 262144;
    private const int MaxConfigs = 5;
    private const int MaxPatterns = 50;
    private const int MaxMatches = 200;
    private const int MaxDiagnostics = 200;

    private static readonly string[] JsonConfigNames =
    [
        "config.json",
        "style-dictionary.config.json",
        "sd.config.json",
    ];

    private static readonly string[] CodeConfigSuffixes =
    [
        "config.js",
        "config.mjs",
        "config.cjs",
        "config.ts",
        "config.mts",
        "config.cts",
    ];

    /// <inheritdoc />
    public bool CanRead(string path, ReadOnlySpan<byte> head)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        string name = LastSegment(path);
        if (name.Length == 0)
            return false;

        string text = head.Length == 0 ? string.Empty : Encoding.UTF8.GetString(head);

        if (IsJsonConfigName(name))
        {
            return text.Contains("\"source\"", StringComparison.Ordinal)
                && text.Contains("\"platforms\"", StringComparison.Ordinal);
        }

        if (IsCodeConfigName(name))
        {
            if (name.StartsWith("tailwind.config", StringComparison.Ordinal))
                return false;

            return text.Contains("style-dictionary", StringComparison.Ordinal)
                || text.Contains("StyleDictionary", StringComparison.Ordinal)
                || text.Contains("platforms", StringComparison.Ordinal);
        }

        return false;
    }

    /// <inheritdoc />
    public DesignImportResult Import(IReadOnlyList<ImportFile> files) => Import(files, files);

    /// <inheritdoc />
    public DesignImportResult Import(IReadOnlyList<ImportFile> claimed, IReadOnlyList<ImportFile> all)
    {
        IReadOnlyList<ImportFile> claim = claimed ?? Array.Empty<ImportFile>();
        IReadOnlyList<ImportFile> everything = all ?? Array.Empty<ImportFile>();

        var diagnostics = new List<ImportDiagnostic>();
        var sources = new List<ImportSourceRef>();
        var configs = new List<ImportFile>();
        int extraConfigs = 0;

        foreach (ImportFile? file in claim)
        {
            if (file is null)
                continue;

            if (configs.Count < MaxConfigs)
                configs.Add(file);
            else
                extraConfigs++;
        }

        if (extraConfigs > 0)
            diagnostics.Add(Warn("TRUNCATED", string.Empty, "only the first five config files are read"));

        var claimedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (ImportFile config in configs)
            claimedPaths.Add(config.Path ?? string.Empty);

        var patterns = new List<(string Pattern, string Directory)>();
        bool fallback = false;

        foreach (ImportFile config in configs)
        {
            string path = config.Path ?? string.Empty;
            sources.Add(new ImportSourceRef(path, Id, Sha256(config.Content.Span), config.ProjectFileId));

            if (config.Content.Length > MaxConfigBytes)
            {
                diagnostics.Add(Error("IMPORT_TOO_LARGE", path, "config is larger than the accepted size"));
                continue;
            }

            if (IsCodeConfigName(LastSegment(path)))
            {
                diagnostics.Add(Warn("SD_CONFIG_CODE_NOT_EXECUTED", path, "a JavaScript or TypeScript config is never executed"));
                fallback = true;
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(
                    config.Content,
                    new JsonDocumentOptions
                    {
                        MaxDepth = ImportLimits.JsonMaxDepth,
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                    });
            }
            catch (JsonException)
            {
                diagnostics.Add(Warn("SD_CONFIG_INVALID", path, "config is not valid JSON"));
                continue;
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    diagnostics.Add(Warn("SD_CONFIG_INVALID", path, "config root must be an object"));
                    continue;
                }

                if (root.TryGetProperty("include", out _))
                    diagnostics.Add(Info("SD_INCLUDE_IGNORED", path, "include is never resolved"));

                if (!root.TryGetProperty("source", out JsonElement source) || source.ValueKind != JsonValueKind.Array)
                {
                    diagnostics.Add(Warn("SD_CONFIG_INVALID", path, "source must be an array of strings"));
                    continue;
                }

                if (source.GetArrayLength() > MaxPatterns)
                {
                    diagnostics.Add(Warn("SD_CONFIG_INVALID", path, "source names too many patterns"));
                    continue;
                }

                string directory = ConfigDirectory(path);
                int index = 0;
                bool stringsOnly = true;
                var candidates = new List<string>();

                foreach (JsonElement item in source.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        stringsOnly = false;
                        break;
                    }

                    candidates.Add(item.GetString() ?? string.Empty);
                }

                if (!stringsOnly)
                {
                    diagnostics.Add(Warn("SD_CONFIG_INVALID", path, "source must be an array of strings"));
                    continue;
                }

                foreach (string candidate in candidates)
                {
                    if (!ImportGlob.IsSafePattern(candidate))
                    {
                        diagnostics.Add(Warn("SD_GLOB_REJECTED", path, $"source[{index}] was not used"));
                        index++;
                        continue;
                    }

                    patterns.Add((candidate, directory));
                    index++;
                }
            }
        }

        var matched = new List<ImportFile>();
        var matchedPaths = new HashSet<string>(StringComparer.Ordinal);
        bool truncatedMatches = false;
        int nonJsonMatches = 0;

        void Consider(ImportFile? file)
        {
            if (file is null)
                return;

            string candidate = file.Path ?? string.Empty;
            if (claimedPaths.Contains(candidate) || matchedPaths.Contains(candidate))
                return;

            if (matched.Count >= MaxMatches)
            {
                truncatedMatches = true;
                return;
            }

            matchedPaths.Add(candidate);
            matched.Add(file);
            if (!candidate.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                nonJsonMatches++;
        }

        foreach ((string pattern, string directory) in patterns)
        {
            string stripped = StripLeadingDotSlash(pattern);
            string effective = directory.Length == 0 ? stripped : directory + "/" + stripped;
            bool found = false;

            foreach (ImportFile? file in everything)
            {
                if (file is null)
                    continue;

                if (ImportGlob.IsMatch(effective, file.Path ?? string.Empty))
                {
                    found = true;
                    Consider(file);
                }
            }

            if (!found && directory.Length > 0)
            {
                foreach (ImportFile? file in everything)
                {
                    if (file is null)
                        continue;

                    if (ImportGlob.IsMatch(stripped, file.Path ?? string.Empty))
                        Consider(file);
                }
            }
        }

        IDesignImporter? dtcg = FindDtcg();

        if (fallback && dtcg is not null)
        {
            foreach (ImportFile? file in everything)
            {
                if (file is null)
                    continue;

                string candidate = file.Path ?? string.Empty;
                if (claimedPaths.Contains(candidate))
                    continue;

                if (!candidate.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;

                ReadOnlySpan<byte> content = file.Content.Span;
                ReadOnlySpan<byte> head = content[..Math.Min(ImportLimits.HeadBytes, content.Length)];
                if (dtcg.CanRead(candidate, head))
                    Consider(file);
            }
        }

        if (nonJsonMatches > 0)
            diagnostics.Add(Info("SD_NON_JSON_SOURCE", string.Empty, "a matched source is not a JSON file"));

        if (truncatedMatches)
            diagnostics.Add(Warn("TRUNCATED", string.Empty, "only the first two hundred matched files are used"));

        if (matched.Count == 0)
        {
            diagnostics.Add(Warn("NO_TOKENS_RECOGNIZED", string.Empty, "no token files matched the style dictionary source globs"));
            return new DesignImportResult(null, null, sources, diagnostics);
        }

        if (dtcg is null)
        {
            diagnostics.Add(Error("SD_CONFIG_INVALID", string.Empty, "dtcg importer unavailable"));
            return new DesignImportResult(null, null, sources, diagnostics);
        }

        DesignImportResult tokenResult = dtcg.Import(matched);

        var combined = new List<ImportDiagnostic>(diagnostics);
        if (tokenResult.Diagnostics is not null)
            combined.AddRange(tokenResult.Diagnostics);

        List<ImportDiagnostic> finalDiagnostics;
        if (combined.Count > MaxDiagnostics)
        {
            finalDiagnostics = combined.GetRange(0, MaxDiagnostics);
            finalDiagnostics.Add(Info("TRUNCATED", string.Empty, "only the first two hundred diagnostics are kept"));
        }
        else
        {
            finalDiagnostics = combined;
        }

        return new DesignImportResult(tokenResult.Design, null, sources, finalDiagnostics);
    }

    private static IDesignImporter? FindDtcg()
    {
        foreach (IDesignImporter? importer in DesignImportRegistry.All)
        {
            if (importer is not null && string.Equals(importer.Id, "dtcg", StringComparison.Ordinal))
                return importer;
        }

        return null;
    }

    private static string LastSegment(string path)
    {
        string name = path ?? string.Empty;
        int slash = name.LastIndexOf('/');
        if (slash >= 0)
            name = name[(slash + 1)..];

        int backslash = name.LastIndexOf('\\');
        if (backslash >= 0)
            name = name[(backslash + 1)..];

        return name.ToLowerInvariant();
    }

    private static string ConfigDirectory(string path)
    {
        string normalized = StripLeadingDotSlash(path ?? string.Empty);
        int slash = normalized.LastIndexOf('/');
        string directory = slash < 0 ? string.Empty : normalized[..slash];
        return directory == "." ? string.Empty : directory;
    }

    private static string StripLeadingDotSlash(string value) =>
        value.StartsWith("./", StringComparison.Ordinal) ? value[2..] : value;

    private static bool IsJsonConfigName(string name)
    {
        foreach (string candidate in JsonConfigNames)
        {
            if (string.Equals(name, candidate, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsCodeConfigName(string name)
    {
        foreach (string suffix in CodeConfigSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ImportDiagnostic Info(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Info, code, path, message);

    private static ImportDiagnostic Warn(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Warn, code, path, message);

    private static ImportDiagnostic Error(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Error, code, path, message);
}
