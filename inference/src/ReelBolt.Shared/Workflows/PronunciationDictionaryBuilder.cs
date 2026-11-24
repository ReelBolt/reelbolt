using System.Text.Json;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// Builds the pronunciation dictionary a Voiceover step sends with a line (see docs/voiceover.md
/// "Pronunciation dictionary"). Deterministic and first-party end to end:
/// <list type="bullet">
/// <item><b>Candidate terms come from the code analysis</b>, never from the model's prose: the
/// dependency names, framework, project type and component names the analysis agents already
/// extracted (<c>DependencyAnalysisOutput</c>, <c>CodeStructureOutput</c>,
/// <c>ComponentInventoryOutput</c>, read leniently from any step output in the run's history).
/// A term that the analysis did not name is never pronounced specially, so "go" in ordinary prose
/// is not read as Golang.</item>
/// <item><b>Phonemes come from <see cref="Table"/></b>, a fixed first-party IPA table of the terms
/// a code promo routinely mispronounces. A term the table does not know contributes nothing —
/// failing closed, because the phoneme alphabet the engine expects is only verified for what the
/// table carries.</item>
/// <item><b>An entry is sent only for a line whose text actually contains the term</b>, as a whole
/// word; the vendor matches keys by plain substring, so three-letter acronyms are sent
/// case-sensitively (<c>API</c> never rewrites "rapid") and nothing shorter than three characters
/// is ever sent.</item>
/// <item><b>The cap degrades, never drops</b>: beyond <c>maxEntries</c> the entries that appear
/// later in the line are dropped, in order, and the rest still go.</item>
/// </list>
/// </summary>
public static class PronunciationDictionaryBuilder
{
    /// <summary>
    /// The first-party phoneme table: lowercase term → IPA. Deliberately a table in code rather
    /// than a config knob, like the colour-grade and SFX tables — the admin surface is the term
    /// list the analysis produces, not the phonemes.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["kubectl"] = "kjuːb kənˈtroʊl",
        ["kubernetes"] = "kuːbərˈnɛtiːz",
        ["nginx"] = "ˈɛndʒɪn ɛks",
        ["postgresql"] = "ˈpoʊstɡrɛs kjuː ɛl",
        ["postgres"] = "ˈpoʊstɡrɛs",
        ["mysql"] = "maɪ ɛs kjuː ɛl",
        ["sqlite"] = "ɛs kjuː ɛl aɪt",
        ["sql"] = "ˈsiːkwəl",
        ["redis"] = "ˈrɛdɪs",
        ["graphql"] = "ɡræf kjuː ɛl",
        ["oauth"] = "oʊ ɔːθ",
        ["json"] = "ˈdʒeɪsən",
        ["yaml"] = "ˈjæməl",
        ["grpc"] = "dʒiː ɑːr piː siː",
        ["vite"] = "viːt",
        ["vitest"] = "viː tɛst",
        ["nuxt"] = "nʌkst",
        ["svelte"] = "svɛlt",
        ["vue"] = "vjuː",
        ["deno"] = "ˈdiːnoʊ",
        ["mantine"] = "mænˈtiːn",
        ["remotion"] = "riːˈmoʊʃən",
        ["ffmpeg"] = "ɛf ɛf ɛm pɛɡ",
        ["supabase"] = "ˈsuːpə beɪs",
        ["vercel"] = "vərˈsɛl",
        ["qdrant"] = "ˈkwɒdrənt",
        ["rabbitmq"] = "ˈræbɪt ɛm kjuː",
        ["minio"] = "mɪn aɪ oʊ",
        ["golang"] = "ˈɡoʊ læŋ",
        ["dotnet"] = "dɒt nɛt",
        ["csharp"] = "siː ʃɑːrp",
        ["npm"] = "ɛn piː ɛm",
        ["pnpm"] = "piː ɛn piː ɛm",
        ["eslint"] = "iː ɛs lɪnt",
        ["jira"] = "ˈdʒɪrə",
        ["zustand"] = "ˈtsuːʃtant",
        ["chakra"] = "ˈtʃʌkrə",
        ["mongodb"] = "ˈmɒŋɡoʊ diː biː",
        ["grafana"] = "ɡrəˈfɑːnə",
        ["prometheus"] = "prəˈmiːθiəs",
        ["anthropic"] = "ænˈθrɒpɪk",
        ["gemini"] = "ˈdʒɛmɪnaɪ",
        ["wasm"] = "ˈwɒzəm",
        ["webgl"] = "wɛb dʒiː ɛl",
        ["linux"] = "ˈlɪnəks",
        ["ubuntu"] = "ʊˈbʊntuː",
        ["azure"] = "ˈæʒər",
        ["api"] = "eɪ piː aɪ",
        ["sdk"] = "ɛs diː keɪ",
        ["cli"] = "siː ɛl aɪ",
        ["url"] = "juː ɑːr ɛl",
        ["css"] = "siː ɛs ɛs",
        ["html"] = "eɪtʃ tiː ɛm ɛl",
        ["saas"] = "sæs",
        ["jwt"] = "dʒeɪ dʌbəljuː tiː",
        ["mcp"] = "ɛm siː piː",
        ["sse"] = "ɛs ɛs iː",
        ["svg"] = "ɛs viː dʒiː",
        ["jpeg"] = "ˈdʒeɪpɛɡ",
        ["reelbolt"] = "riːl boʊlt",
    };

    /// <summary>
    /// Extracts candidate terms from any code-analysis output JSON in the run's history. Lenient
    /// by design: unknown shapes contribute nothing, and a property that is not where a schema
    /// put it is simply not read. Terms are the lowercase alphanumeric tokens of dependency names
    /// (<c>@mantine/core</c> → <c>mantine</c>, <c>core</c>), the framework and project type, and
    /// component names.
    /// </summary>
    public static IReadOnlySet<string> ExtractAnalysisTerms(IEnumerable<string?> stepOutputs)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? output in stepOutputs)
        {
            if (string.IsNullOrWhiteSpace(output))
                continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(output);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    continue;

                AddString(terms, root, "framework");
                AddString(terms, root, "projectType");
                AddString(terms, root, "packageManager");
                AddNameArray(terms, root, "dependencies", "name");
                AddStringArray(terms, root, "devDependencies");
                AddNameArray(terms, root, "components", "name");
            }
            catch (JsonException)
            {
                // Not JSON, or not an object we understand: contributes no terms.
            }
        }

        return terms;
    }

    /// <summary>
    /// The entries for one line: every analysis term that has a table pronunciation and appears in
    /// <paramref name="lineText"/> as a whole word, in order of first appearance, capped at
    /// <paramref name="maxEntries"/> (clamped 1..64).
    /// </summary>
    public static IReadOnlyList<PronunciationEntry> BuildForLine(
        string lineText, IReadOnlySet<string> analysisTerms, int maxEntries)
    {
        if (string.IsNullOrWhiteSpace(lineText) || analysisTerms.Count == 0)
            return [];

        int cap = Math.Clamp(maxEntries, 1, 64);
        List<(int Position, PronunciationEntry Entry)> found = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string term in analysisTerms)
        {
            if (term.Length < 3 || !Table.TryGetValue(term, out string? ipa) || !seen.Add(term))
                continue;

            Match match = Regex.Match(
                lineText, $@"(?<![\p{{L}}\p{{Nd}}]){Regex.Escape(term)}(?![\p{{L}}\p{{Nd}}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success)
                continue;

            // Send the key with the casing the line actually uses, and case-sensitively when it is
            // a short all-caps acronym: the vendor matches by substring, and a lowercase "api"
            // would rewrite the inside of "rapid".
            string key = match.Value;
            bool caseSensitive = key.Length <= 4 && key.ToUpperInvariant() == key;
            found.Add((match.Index, new PronunciationEntry(key, ipa, caseSensitive)));
        }

        return found.OrderBy(f => f.Position).Take(cap).Select(f => f.Entry).ToList();
    }

    /// <summary>
    /// <see cref="BuildForLine"/> plus the author's overrides. Overrides go FIRST (they win over the
    /// table for the same term) and are never dropped before table entries by the cap; they work
    /// with the dictionary mode Off. With no overrides the result equals <see cref="BuildForLine"/>.
    /// </summary>
    public static IReadOnlyList<PronunciationEntry> BuildForLineWithOverrides(
        string lineText, IReadOnlySet<string> analysisTerms, int maxEntries,
        IReadOnlyList<PronunciationOverride>? overrides)
    {
        if (overrides is not { Count: > 0 } || string.IsNullOrWhiteSpace(lineText))
            return BuildForLine(lineText, analysisTerms, maxEntries);

        int cap = Math.Clamp(maxEntries, 1, 64);
        List<(int Position, PronunciationEntry Entry)> found = [];
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (PronunciationOverride o in overrides)
        {
            if (o is null || string.IsNullOrEmpty(o.Term) || string.IsNullOrEmpty(o.Ipa))
                continue;
            string term = o.Term.Normalize(System.Text.NormalizationForm.FormC);
            if (!seen.Add(term))
                continue;

            Match match = Regex.Match(
                lineText, $@"(?<![\p{{L}}\p{{Nd}}]){Regex.Escape(term)}(?![\p{{L}}\p{{Nd}}])",
                (o.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
            if (match.Success)
            {
                // Only a MATCHING override replaces the table entry for that word.
                claimed.Add(match.Value);
                found.Add((match.Index, new PronunciationEntry(match.Value, o.Ipa, o.CaseSensitive)));
            }
        }

        List<PronunciationEntry> result = found.OrderBy(f => f.Position).Select(f => f.Entry).Take(cap).ToList();
        foreach (PronunciationEntry t in BuildForLine(lineText, analysisTerms, cap))
        {
            if (result.Count >= cap)
                break;
            if (!claimed.Contains(t.Key))
                result.Add(t);
        }

        return result;
    }

    private static void AddString(HashSet<string> terms, JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            AddTokens(terms, value.GetString());
    }

    private static void AddStringArray(HashSet<string> terms, JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            return;
        foreach (JsonElement item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
                AddTokens(terms, item.GetString());
    }

    private static void AddNameArray(HashSet<string> terms, JsonElement root, string property, string nameProperty)
    {
        if (!root.TryGetProperty(property, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            return;
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty(nameProperty, out JsonElement name) && name.ValueKind == JsonValueKind.String)
                AddTokens(terms, name.GetString());
        }
    }

    private static void AddTokens(HashSet<string> terms, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        foreach (string token in Regex.Split(value, @"[^\p{L}\p{Nd}]+", RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            // Canonical lowercase: the set is read case-insensitively, and the line's own casing is
            // what the emitted key carries (see BuildForLine).
            if (token.Length >= 3)
                terms.Add(token.ToLowerInvariant());
        }

        // "C#" and ".NET" have no alphanumeric-only spelling; recognise them by their own marks.
        if (value.Contains("C#", StringComparison.OrdinalIgnoreCase))
            terms.Add("csharp");
        if (value.Contains(".NET", StringComparison.OrdinalIgnoreCase))
            terms.Add("dotnet");
    }
}
