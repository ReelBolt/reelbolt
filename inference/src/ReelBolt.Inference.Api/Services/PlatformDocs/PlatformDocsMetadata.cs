using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.PlatformDocs;

/// <summary>
/// Retrieval metadata of one chunk (docs/design/workflow-apps.md 7.4). Declared by the optional
/// <c>kb:</c> front matter, else derived by a deterministic scan, so every existing page has usable
/// metadata without edits.
/// </summary>
public sealed record PlatformDocsChunkMeta
{
    public static readonly PlatformDocsChunkMeta Empty = new();

    /// <summary>One of <see cref="PlatformDocsMetadata.Kinds"/>.</summary>
    public string Kind { get; init; } = "reference";
    public IReadOnlyList<string> StepTypes { get; init; } = [];
    public IReadOnlyList<string> AgentTypes { get; init; } = [];
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public IReadOnlyList<string> TemplateKeys { get; init; } = [];
    public IReadOnlyList<string> Goals { get; init; } = [];

    /// <summary>One of <see cref="PlatformDocsMetadata.CostClasses"/>, or null when not declared.</summary>
    public string? CostClass { get; init; }
    public string Locale { get; init; } = "en";
    public string HeadingPath { get; init; } = string.Empty;
    public string? PrevId { get; init; }
    public string? NextId { get; init; }
    public string DocVersion { get; init; } = string.Empty;

    /// <summary>Canonical text of every field, fed into the corpus hash so a metadata-only edit re-indexes.</summary>
    internal string Canonical() => string.Join(
        "|", Kind, string.Join(',', StepTypes), string.Join(',', AgentTypes), string.Join(',', Capabilities),
        string.Join(',', TemplateKeys), string.Join(',', Goals), CostClass ?? "", Locale, HeadingPath,
        PrevId ?? "", NextId ?? "", DocVersion);
}

/// <summary>Front matter parsing, the deterministic scan and the sparse (BM25) token stream.</summary>
public static class PlatformDocsMetadata
{
    public static readonly IReadOnlyList<string> Kinds =
        ["recipe", "step-card", "concept", "how-to", "reference", "user-guide", "generated-fact"];

    public static readonly IReadOnlyList<string> CostClasses = ["free", "credits", "voice", "generation"];

    private static readonly Lazy<NameScanner> StepTypeNames = new(() => new NameScanner(Enum.GetNames<StepType>()));
    private static readonly Lazy<NameScanner> AgentTypeNames = new(() => new NameScanner(Enum.GetNames<AgentType>()));
    private static readonly Lazy<NameScanner> CapabilityNames = new(() => new NameScanner(Enum.GetNames<InferenceProviderCapability>()));

    private static readonly Lazy<Regex> TemplateKeyNames = new(() =>
        new Regex(
            @"(?<![\w-])(" + string.Join('|', WorkflowTemplateCatalog.GetAll()
                .Select(t => t.Key).OrderByDescending(k => k.Length).Select(Regex.Escape)) + @")(?![\w-])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant));

    /// <summary>
    /// Finds enum names the way a person writes code, not the way they write prose. A compound name
    /// (<c>VideoCompile</c>) matches anywhere, exact case. A single capitalised word that is also plain English
    /// (<c>Agent</c>, <c>Extract</c>, <c>Decision</c>, <c>Chat</c>) matches only inside backticks or next to the
    /// word step / agent / capability / type, so a sentence that merely starts with it is not tagged.
    /// </summary>
    private sealed class NameScanner
    {
        private readonly Regex _compound;
        private readonly Regex[] _single;

        public NameScanner(IEnumerable<string> names)
        {
            string[] all = names.OrderByDescending(n => n.Length).ToArray();
            string[] compound = all.Where(n => n.Skip(1).Any(char.IsUpper)).Select(Regex.Escape).ToArray();
            string[] single = all.Where(n => !n.Skip(1).Any(char.IsUpper)).Select(Regex.Escape).ToArray();
            const RegexOptions o = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            _compound = new Regex(@"(?<!\w)(" + string.Join('|', compound) + @")(?!\w)", o);
            string n1 = string.Join('|', single);
            _single =
            [
                new Regex("`(" + n1 + ")`", o),
                new Regex(@"(?<!\w)(?i:step|agent|capability|type)\s+(?:(?i:type)\s+)?(" + n1 + @")(?!\w)", o),
                new Regex(@"(?<!\w)(" + n1 + @")\s+(?i:step|agent|capability)(?!\w)", o)
            ];
        }

        public IReadOnlyList<string> Scan(string text) =>
            _single.Prepend(_compound).SelectMany(r => r.Matches(text).Select(m => m.Groups[1].Value))
                .Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList();
    }

    /// <summary>Exact (case-sensitive) <see cref="StepType"/> names found in <paramref name="text"/>, sorted.</summary>
    public static IReadOnlyList<string> ScanStepTypes(string text) => StepTypeNames.Value.Scan(text);

    public static IReadOnlyList<string> ScanAgentTypes(string text) => AgentTypeNames.Value.Scan(text);

    public static IReadOnlyList<string> ScanCapabilities(string text) => CapabilityNames.Value.Scan(text);

    /// <summary>Catalog template keys found as whole tokens (<c>quick-win-promo</c> does not match inside <c>quick-win-promo-ds</c>).</summary>
    public static IReadOnlyList<string> ScanTemplateKeys(string text) =>
        TemplateKeyNames.Value.Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Parses the optional <c>kb:</c> block of a front matter (a scalar, an inline <c>[a, b]</c> list or a
    /// <c>- item</c> block list per key). Unknown keys are ignored; unknown <c>kind</c>/<c>costClass</c>
    /// values are dropped so a typo falls back to the scan/folder rule instead of poisoning a filter.
    /// </summary>
    internal static Dictionary<string, List<string>> ParseKb(string frontMatter)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        bool inKb = false;
        string? listKey = null;
        foreach (string raw in frontMatter.Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0)
                continue;

            if (!char.IsWhiteSpace(line[0]))
            {
                inKb = line.Trim() == "kb:";
                listKey = null;
                continue;
            }

            if (!inKb)
                continue;

            string trimmed = line.Trim();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) && listKey is not null)
            {
                result[listKey].Add(Unquote(StripComment(trimmed[2..])));
                continue;
            }

            int colon = trimmed.IndexOf(':');
            if (colon <= 0)
                continue;

            string key = trimmed[..colon].Trim();
            string value = StripComment(trimmed[(colon + 1)..]).Trim();
            result[key] = [];
            listKey = null;
            if (value.Length == 0)
                listKey = key;
            else if (value.StartsWith('[') && value.EndsWith(']'))
                result[key].AddRange(SplitInline(value[1..^1]));
            else
                result[key].Add(Unquote(value));
        }

        return result;
    }

    /// <summary>Drops a trailing YAML comment (<c> # ...</c>) that is not inside quotes.</summary>
    private static string StripComment(string value)
    {
        char quote = '\0';
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '#' && (i == 0 || char.IsWhiteSpace(value[i - 1])))
            {
                return value[..i];
            }
        }

        return value;
    }

    private static IEnumerable<string> SplitInline(string inner)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        foreach (char c in inner)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0'; else current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == ',')
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        parts.Add(current.ToString().Trim());
        return parts.Where(p => p.Length > 0);
    }

    private static string Unquote(string v) => v.Trim().Trim('"', '\'');

    /// <summary>Folder rule for <c>kind</c> when the front matter does not declare one.</summary>
    internal static string DefaultKind(string relativePath) =>
        relativePath.StartsWith("user-guide/recipes/", StringComparison.Ordinal) ? "recipe"
        : relativePath.StartsWith("user-guide/", StringComparison.Ordinal) ? "user-guide"
        : relativePath.StartsWith("assistant-kb/", StringComparison.Ordinal) ? "concept"
        : "reference";

    /// <summary>Builds one chunk's metadata: declared values win per field, the scan fills the rest.</summary>
    internal static PlatformDocsChunkMeta Build(
        string relativePath, string title, string section, string text,
        IReadOnlyDictionary<string, List<string>> declared, string docVersion)
    {
        List<string>? Declared(string key) => declared.TryGetValue(key, out List<string>? v) && v.Count > 0 ? v : null;

        string kind = Declared("kind")?[0] is { } k && Kinds.Contains(k, StringComparer.Ordinal) ? k : DefaultKind(relativePath);
        string? cost = Declared("costClass")?[0] is { } c && CostClasses.Contains(c, StringComparer.Ordinal) ? c : null;
        string scanned = section.Length > 0 ? $"{section}\n{text}" : text;

        // Declared enum names are kept only if they are real names, for the same reason as above.
        IReadOnlyList<string> Pick(string key, IReadOnlyList<string> scan, IReadOnlyCollection<string>? valid)
        {
            List<string>? d = Declared(key);
            if (d is null)
                return scan;
            // Declared values are merged with this section's own scan, never replace it: a page-level declaration
            // must not hide what a particular section is about (nor, alone, make every section match).
            IEnumerable<string> kept = valid is null ? d : d.Where(valid.Contains);
            return kept.Concat(scan).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList();
        }

        return new PlatformDocsChunkMeta
        {
            Kind = kind,
            StepTypes = Pick("stepTypes", ScanStepTypes(scanned), Enum.GetNames<StepType>()),
            AgentTypes = Pick("agentTypes", ScanAgentTypes(scanned), Enum.GetNames<AgentType>()),
            Capabilities = Pick("capabilities", ScanCapabilities(scanned), Enum.GetNames<InferenceProviderCapability>()),
            TemplateKeys = Pick("templateKeys", ScanTemplateKeys(scanned), null),
            Goals = Declared("goals")?.ToList() ?? [],
            CostClass = cost,
            Locale = "en",
            HeadingPath = section.Length > 0 ? $"# {title} > ## {section}" : $"# {title}",
            DocVersion = docVersion
        };
    }

    /// <summary>12 hex chars of SHA-256 over the normalised file content (diagnostics only).</summary>
    internal static string DocVersionOf(string markdown) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(markdown)))[..12].ToLowerInvariant();

    /// <summary>
    /// The sparse-side token stream: Unicode-folded (diacritics removed), lowercased, split on every
    /// non-letter/digit, one-character tokens dropped. No stemming (v1).
    /// </summary>
    public static IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        var folded = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            folded.Append(char.ToLowerInvariant(c));
        }

        var token = new StringBuilder();
        foreach (char c in folded.ToString())
        {
            if (char.IsLetterOrDigit(c))
            {
                token.Append(c);
                continue;
            }

            if (token.Length > 1)
                yield return token.ToString();
            token.Clear();
        }

        if (token.Length > 1)
            yield return token.ToString();
    }

    /// <summary>
    /// Term frequencies of <paramref name="text"/> as a sparse vector: indices are a stable FNV-1a hash of the
    /// term (ascending, colliding terms summed), values the raw counts (the server applies IDF).
    /// </summary>
    public static (float[] Values, uint[] Indices) SparseTermFrequencies(string text)
    {
        var counts = new SortedDictionary<uint, float>();
        foreach (string token in Tokenize(text))
        {
            uint index = TermIndex(token);
            counts[index] = counts.GetValueOrDefault(index) + 1f;
        }

        return (counts.Values.ToArray(), counts.Keys.ToArray());
    }

    /// <summary>The query-side sparse vector: each distinct term once (the document side carries the frequencies).</summary>
    public static (float[] Values, uint[] Indices) SparseQueryTerms(string text)
    {
        uint[] indices = Tokenize(text).Select(TermIndex).Distinct().OrderBy(i => i).ToArray();
        return (indices.Select(_ => 1f).ToArray(), indices);
    }

    internal static uint TermIndex(string term)
    {
        uint hash = 2166136261;
        foreach (byte b in Encoding.UTF8.GetBytes(term))
            hash = (hash ^ b) * 16777619;
        return hash;
    }
}
