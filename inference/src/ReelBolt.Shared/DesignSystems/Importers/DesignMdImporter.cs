using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// Reads a <c>DESIGN.md</c>: the YAML front matter between the first two <c>---</c> lines becomes
/// tokens and the prose headings become <c>voice</c> content. Pure and total — it performs no IO,
/// never throws and reports anything hostile as an <see cref="ImportDiagnostic"/> rather than
/// interpreting it.
/// </summary>
public sealed class DesignMdImporter : IDesignImporter
{
    private const double Confidence = 0.9;
    private const int MaxKeyLength = 48;
    private const int MaxPathLength = 160;
    private const int MaxNameLength = 120;
    private const int MaxSummaryLength = 600;
    private const int MaxListItems = 12;
    private const int MaxDoLength = 140;
    private const int MaxToneLength = 80;

    /// <inheritdoc />
    public string Id => "design-md";

    /// <inheritdoc />
    public bool CanRead(string path, ReadOnlySpan<byte> head)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
                return false;

            string fileName = FileName(path);
            if (fileName.Equals("DESIGN.md", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                && !fileName.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
                return false;

            if (head.Length == 0)
                return false;

            int limit = Math.Min(head.Length, ImportLimits.HeadBytes);
            string text = Encoding.UTF8.GetString(head[..limit]);
            return HasFrontMatterMarker(text);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public DesignImportResult Import(IReadOnlyList<ImportFile> files)
    {
        var design = DesignDocument.New();
        var sources = new List<ImportSourceRef>();
        var diagnostics = new List<ImportDiagnostic>();

        if (files is not null)
        {
            foreach (ImportFile? file in files)
            {
                if (file is null)
                    continue;

                try
                {
                    ImportOne(design, file, sources, diagnostics);
                }
                catch (Exception exception)
                {
                    diagnostics.Add(Error("IMPORT_INTERNAL", file.Path ?? string.Empty, exception.GetType().Name));
                }
            }
        }

        return new DesignImportResult(design, null, sources, diagnostics);
    }

    private void ImportOne(
        JsonObject design,
        ImportFile file,
        List<ImportSourceRef> sources,
        List<ImportDiagnostic> diagnostics)
    {
        string path = file.Path ?? string.Empty;
        sources.Add(new ImportSourceRef(path, Id, Sha256(file.Content.Span), file.ProjectFileId));

        if (file.Content.Length > ImportLimits.MarkdownMaxBytes)
        {
            diagnostics.Add(Error(
                "IMPORT_TOO_LARGE",
                path,
                $"file is {file.Content.Length} bytes, over the {ImportLimits.MarkdownMaxBytes} byte limit"));
            return;
        }

        string text = Encoding.UTF8.GetString(file.Content.Span);
        if (!TrySplitFrontMatter(text, out List<string> frontLines, out int firstLineNumber, out string body))
        {
            MapProse(design, text);
            return;
        }

        if (frontLines.Count > 0)
        {
            string frontMatter = string.Join("\n", frontLines);
            JsonNode? parsed = YamlSubsetReader.Parse(frontMatter, out ImportDiagnostic? yamlDiagnostic);
            if (parsed is null)
            {
                diagnostics.Add(yamlDiagnostic is null
                    ? Error("YAML_INVALID", path, "front matter could not be parsed")
                    : yamlDiagnostic with { Path = path });
            }
            else if (parsed is JsonObject root)
            {
                MapFrontMatter(design, root, path, diagnostics, frontLines, firstLineNumber);
            }
            else
            {
                diagnostics.Add(Warn("YAML_SHAPE", path, "front matter must be a mapping"));
            }
        }

        MapProse(design, body);
    }

    private static void MapFrontMatter(
        JsonObject design,
        JsonObject root,
        string path,
        List<ImportDiagnostic> diagnostics,
        IReadOnlyList<string> frontLines,
        int firstLineNumber)
    {
        if (root.TryGetPropertyValue("name", out JsonNode? nameNode)
            && nameNode is JsonValue nameValue
            && nameValue.GetValueKind() == JsonValueKind.String)
        {
            string? name = DesignText.Sanitize(nameValue.GetValue<string>(), MaxNameLength);
            if (name is not null)
                design["name"] = name;
        }

        MapColors(design, root, path, diagnostics, frontLines, firstLineNumber);
        MapTypography(design, root, path, diagnostics, frontLines, firstLineNumber);
        MapDimensions(design, root, "radius", "radius", path, diagnostics, frontLines, firstLineNumber);
        MapDimensions(design, root, "rounded", "radius", path, diagnostics, frontLines, firstLineNumber);
        MapDimensions(design, root, "spacing", "spacing", path, diagnostics, frontLines, firstLineNumber);

        if (root.TryGetPropertyValue("components", out JsonNode? components) && components is JsonObject componentMap)
        {
            diagnostics.Add(new ImportDiagnostic(
                ImportDiagnosticSeverity.Info,
                "COMPONENTS_SKIPPED",
                path,
                $"skipped {componentMap.Count} component bodies"));
        }
    }

    private static void MapColors(
        JsonObject design,
        JsonObject root,
        string path,
        List<ImportDiagnostic> diagnostics,
        IReadOnlyList<string> frontLines,
        int firstLineNumber)
    {
        if (!root.TryGetPropertyValue("colors", out JsonNode? node) || node is not JsonObject colors)
            return;

        foreach (KeyValuePair<string, JsonNode?> entry in colors)
        {
            string tokenPath = "color." + NormalizeKey(entry.Key);
            if (tokenPath.Length > MaxPathLength)
            {
                diagnostics.Add(Warn("PATH_TOO_LONG", path, $"token path '{tokenPath}' is too long"));
                continue;
            }

            JsonNode? normalized = TokenNormalizer.Normalize("color", entry.Value, out ImportDiagnostic? diagnostic);
            if (normalized is null)
            {
                if (diagnostic is not null)
                    diagnostics.Add(diagnostic with { Path = tokenPath });
                continue;
            }

            DesignDocument.SetToken(
                design,
                tokenPath,
                "color",
                normalized,
                Provenance(path, FindLine(frontLines, firstLineNumber, entry.Key)),
                Confidence);
        }
    }

    private static void MapTypography(
        JsonObject design,
        JsonObject root,
        string path,
        List<ImportDiagnostic> diagnostics,
        IReadOnlyList<string> frontLines,
        int firstLineNumber)
    {
        if (!root.TryGetPropertyValue("typography", out JsonNode? node) || node is not JsonObject typography)
            return;

        foreach (KeyValuePair<string, JsonNode?> entry in typography)
        {
            if (entry.Value is not JsonObject)
            {
                diagnostics.Add(Warn("TYPOGRAPHY_INVALID", path, $"typography.{entry.Key} is not an object"));
                continue;
            }

            string tokenPath = "typography." + NormalizeKey(entry.Key);
            if (tokenPath.Length > MaxPathLength)
            {
                diagnostics.Add(Warn("PATH_TOO_LONG", path, $"token path '{tokenPath}' is too long"));
                continue;
            }

            JsonNode? normalized = TokenNormalizer.Normalize("typography", entry.Value, out ImportDiagnostic? diagnostic);
            if (normalized is null)
            {
                if (diagnostic is not null)
                    diagnostics.Add(diagnostic with { Path = tokenPath });
                continue;
            }

            DesignDocument.SetToken(
                design,
                tokenPath,
                "typography",
                normalized,
                Provenance(path, FindLine(frontLines, firstLineNumber, entry.Key)),
                Confidence);
        }
    }

    private static void MapDimensions(
        JsonObject design,
        JsonObject root,
        string sourceKey,
        string group,
        string path,
        List<ImportDiagnostic> diagnostics,
        IReadOnlyList<string> frontLines,
        int firstLineNumber)
    {
        if (!root.TryGetPropertyValue(sourceKey, out JsonNode? node) || node is null)
            return;

        if (node is JsonObject map)
        {
            foreach (KeyValuePair<string, JsonNode?> entry in map)
                EmitDimension(design, group, entry.Key, entry.Value, path, diagnostics, frontLines, firstLineNumber);
            return;
        }

        EmitDimension(design, group, "default", node, path, diagnostics, frontLines, firstLineNumber);
    }

    private static void EmitDimension(
        JsonObject design,
        string group,
        string name,
        JsonNode? raw,
        string path,
        List<ImportDiagnostic> diagnostics,
        IReadOnlyList<string> frontLines,
        int firstLineNumber)
    {
        string tokenPath = group + "." + NormalizeKey(name);
        if (tokenPath.Length > MaxPathLength)
        {
            diagnostics.Add(Warn("PATH_TOO_LONG", path, $"token path '{tokenPath}' is too long"));
            return;
        }

        JsonNode? normalized = TokenNormalizer.Normalize("dimension", raw, out ImportDiagnostic? diagnostic);
        if (normalized is null)
        {
            if (diagnostic is not null)
                diagnostics.Add(diagnostic with { Path = tokenPath });
            return;
        }

        DesignDocument.SetToken(
            design,
            tokenPath,
            "dimension",
            normalized,
            Provenance(path, FindLine(frontLines, firstLineNumber, name)),
            Confidence);
    }

    private static void MapProse(JsonObject design, string body)
    {
        string normalized = body.Replace("\r\n", "\n").Replace('\r', '\n');
        var summary = new StringBuilder();
        bool summaryDone = false;
        var doItems = new List<string>();
        var dontItems = new List<string>();
        var toneItems = new List<string>();
        string section = string.Empty;
        string sub = string.Empty;

        foreach (string rawLine in normalized.Split('\n'))
        {
            string trimmed = rawLine.Trim();

            if (TryHeading(trimmed, out int level, out string headingText))
            {
                string classified = ClassifyHeading(headingText);
                if (section == "dodont" && level >= 3 && (classified == "do" || classified == "dont"))
                {
                    sub = classified;
                    continue;
                }

                section = classified;
                sub = string.Empty;
                summaryDone = false;
                continue;
            }

            switch (section)
            {
                case "overview":
                    if (trimmed.Length == 0)
                    {
                        if (summary.Length > 0)
                            summaryDone = true;
                        continue;
                    }

                    if (summaryDone)
                        continue;

                    if (summary.Length > 0)
                        summary.Append(' ');
                    summary.Append(trimmed);
                    break;

                case "do":
                    if (TryBullet(trimmed, out string doItem))
                        doItems.Add(doItem);
                    break;

                case "dont":
                    if (TryBullet(trimmed, out string dontItem))
                        dontItems.Add(dontItem);
                    break;

                case "dodont" when sub == "do":
                    if (TryBullet(trimmed, out string combinedDo))
                        doItems.Add(combinedDo);
                    break;

                case "dodont" when sub == "dont":
                    if (TryBullet(trimmed, out string combinedDont))
                        dontItems.Add(combinedDont);
                    break;

                case "dodont":
                    if (TryBullet(trimmed, out string routed))
                    {
                        if (StartsWithNegative(routed))
                            dontItems.Add(routed);
                        else
                            doItems.Add(routed);
                    }

                    break;

                case "tone":
                    if (TryBullet(trimmed, out string toneItem))
                        toneItems.Add(toneItem);
                    break;
            }
        }

        var voice = new JsonObject();
        string? summaryText = DesignText.Sanitize(summary.ToString(), MaxSummaryLength);
        if (summaryText is not null)
            voice["summary"] = summaryText;

        AddList(voice, "do", doItems, MaxDoLength);
        AddList(voice, "dont", dontItems, MaxDoLength);
        AddList(voice, "tone", toneItems, MaxToneLength);

        if (voice.Count > 0)
            design["voice"] = voice;
    }

    private static void AddList(JsonObject voice, string key, List<string> items, int maxLength)
    {
        if (items.Count == 0)
            return;

        var array = new JsonArray();
        foreach (string item in items)
        {
            if (array.Count >= MaxListItems)
                break;

            string? clean = DesignText.Sanitize(item, maxLength);
            if (clean is not null)
                array.Add(clean);
        }

        if (array.Count > 0)
            voice[key] = array;
    }

    private static bool TryHeading(string trimmed, out int level, out string text)
    {
        level = 0;
        text = string.Empty;
        if (trimmed.Length == 0 || trimmed[0] != '#')
            return false;

        while (level < trimmed.Length && trimmed[level] == '#')
            level++;

        if (level > 6)
            return false;

        if (level < trimmed.Length && trimmed[level] != ' ')
            return false;

        text = trimmed[level..].Trim().TrimEnd(':');
        return true;
    }

    private static bool TryBullet(string trimmed, out string item)
    {
        item = string.Empty;
        if (trimmed.Length < 2)
            return false;

        char marker = trimmed[0];
        if (marker is not ('-' or '*' or '+') || trimmed[1] != ' ')
            return false;

        item = trimmed[2..].Trim();
        return item.Length > 0;
    }

    private static string ClassifyHeading(string text)
    {
        string lower = text.Trim().TrimEnd(':').ToLowerInvariant().Replace('’', '\'');
        if (lower.Length == 0)
            return string.Empty;

        if (lower == "overview")
            return "overview";

        bool hasDont = lower.Contains("don't", StringComparison.Ordinal) || lower.Contains("dont", StringComparison.Ordinal);
        string withoutDont = lower.Replace("don't", string.Empty, StringComparison.Ordinal).Replace("dont", string.Empty, StringComparison.Ordinal);
        bool hasDo = withoutDont.Contains("do", StringComparison.Ordinal) || lower.Contains("do's", StringComparison.Ordinal);

        if (hasDont && hasDo)
            return "dodont";
        if (hasDont)
            return "dont";
        if (lower.StartsWith("do", StringComparison.Ordinal))
            return "do";
        if (lower.StartsWith("tone", StringComparison.Ordinal) || lower.StartsWith("voice", StringComparison.Ordinal))
            return "tone";

        return string.Empty;
    }

    private static bool StartsWithNegative(string item)
    {
        string lower = item.TrimStart().ToLowerInvariant().Replace('’', '\'');
        return lower.StartsWith("don't", StringComparison.Ordinal)
            || lower.StartsWith("dont", StringComparison.Ordinal)
            || lower.StartsWith("do not", StringComparison.Ordinal)
            || lower.StartsWith("never", StringComparison.Ordinal)
            || lower.StartsWith("avoid", StringComparison.Ordinal);
    }

    private static bool TrySplitFrontMatter(
        string text,
        out List<string> frontLines,
        out int firstLineNumber,
        out string body)
    {
        frontLines = new List<string>();
        firstLineNumber = 0;
        body = text;

        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        if (lines.Length == 0)
            return false;

        if (lines[0].TrimStart('\uFEFF').Trim() != "---")
            return false;

        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() != "---")
                continue;

            for (int k = 1; k < i; k++)
                frontLines.Add(lines[k]);

            firstLineNumber = 2;
            body = string.Join("\n", lines[(i + 1)..]);
            return true;
        }

        for (int k = 1; k < lines.Length; k++)
            frontLines.Add(lines[k]);

        firstLineNumber = 2;
        body = string.Empty;
        return true;
    }

    private static bool HasFrontMatterMarker(string text)
    {
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        if (lines.Length == 0)
            return false;

        if (lines[0].TrimStart('\uFEFF').Trim() != "---")
            return false;

        for (int i = 1; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed == "---")
                return false;

            if (trimmed.StartsWith("colors:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("typography:", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string FileName(string path)
    {
        int slash = path.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static string NormalizeKey(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (char c in key)
        {
            char lower = char.ToLowerInvariant(c);
            builder.Append(IsKeyCharacter(lower) ? lower : '-');
        }

        int start = 0;
        while (start < builder.Length && !IsKeyStart(builder[start]))
            start++;

        string normalized = builder.ToString(start, builder.Length - start);
        if (normalized.Length == 0)
            normalized = "token";
        if (normalized.Length > MaxKeyLength)
            normalized = normalized[..MaxKeyLength];

        return normalized;
    }

    private static bool IsKeyCharacter(char c) =>
        (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '_' or '-';

    private static bool IsKeyStart(char c) =>
        (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');

    private static int FindLine(IReadOnlyList<string> frontLines, int firstLineNumber, string key)
    {
        for (int i = 0; i < frontLines.Count; i++)
        {
            string trimmed = frontLines[i].Trim();
            if (trimmed.StartsWith(key + ":", StringComparison.Ordinal)
                || trimmed.StartsWith("- " + key + ":", StringComparison.Ordinal)
                || trimmed == key)
                return firstLineNumber + i;
        }

        return firstLineNumber;
    }

    private static TokenProvenance Provenance(string path, int line)
    {
        string loc = path + ":" + line.ToString(CultureInfo.InvariantCulture);
        if (loc.Length > 200)
            loc = loc[..200];

        return new TokenProvenance("imported", "design-md", null, loc);
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ImportDiagnostic Error(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Error, code, path, message);

    private static ImportDiagnostic Warn(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Warn, code, path, message);
}
