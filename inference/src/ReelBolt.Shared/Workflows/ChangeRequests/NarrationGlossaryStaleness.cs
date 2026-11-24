using System.Text.Json;

namespace ReelBolt.Shared.Workflows.ChangeRequests;

/// <summary>
/// Which lines of an earlier narration were spoken with a pronunciation glossary that is no longer the project's
/// (docs/change-requests.md "Pronunciations saved after the run"). A change that leaves a line untouched normally
/// carries its audio over; a line whose spoken form would differ under the glossary as it stands now is not carried:
/// it is made again, priced and listed in the estimate like any named line. Pure; the engine and the API ask the same
/// question here so the price shown is the work done.
/// </summary>
public static class NarrationGlossaryStaleness
{
    /// <summary>
    /// The glossary a narration step speaks with now: <paramref name="Effective"/> is what is substituted (the project's
    /// entries minus the terms the step's own overrides name), <paramref name="Digest"/> the digest of ALL the project's
    /// entries, as the step records it in its output.
    /// </summary>
    public sealed record CurrentGlossary(IReadOnlyList<GlossaryEntry> Effective, string? Digest)
    {
        public static CurrentGlossary From(IReadOnlyList<GlossaryEntry> all, IEnumerable<string>? stepOverrideTerms)
        {
            HashSet<string> covered = new(
                (stepOverrideTerms ?? []).Select(t => (t ?? string.Empty).Normalize(System.Text.NormalizationForm.FormC)),
                StringComparer.OrdinalIgnoreCase);
            List<GlossaryEntry> effective = covered.Count == 0
                ? [.. all]
                : [.. all.Where(g => !covered.Contains(g.Term.Normalize(System.Text.NormalizationForm.FormC)))];
            return new CurrentGlossary(effective, PronunciationRespeller.Digest(all));
        }
    }

    /// <summary>
    /// True when speaking <paramref name="writtenText"/> under <paramref name="current"/> gives something other than
    /// <paramref name="spokenBefore"/> (the form the earlier run recorded; null = the earlier run recorded none, so it
    /// spoke the written text itself or an older glossary we cannot see).
    /// </summary>
    public static bool IsStale(string? writtenText, string? spokenBefore, IReadOnlyList<GlossaryEntry> current)
    {
        if (string.IsNullOrWhiteSpace(writtenText) || current.Count == 0)
            return !string.IsNullOrEmpty(spokenBefore)
                && !string.Equals(spokenBefore, writtenText, StringComparison.Ordinal);
        string now = PronunciationRespeller.Apply(writtenText, current).Text;
        string before = (spokenBefore ?? writtenText).Normalize(System.Text.NormalizationForm.FormC);
        return !string.Equals(now, before, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ids (<c>vo{n}</c>) of the lines of a Voiceover step output that were spoken with a glossary other than
    /// <paramref name="current"/>. When the output records the digest of the glossary it was made with and it equals
    /// the current one, nothing is stale. An output made before spoken forms were recorded is judged on the written text.
    /// </summary>
    public static IReadOnlySet<string> StaleLineIds(string? outputJson, CurrentGlossary glossary)
    {
        HashSet<string> stale = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(outputJson))
            return stale;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(outputJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return stale;
            if (SameGlossary(root, glossary.Digest))
                return stale;
            if (!root.TryGetProperty("view", out JsonElement view) || view.ValueKind != JsonValueKind.Object
                || !view.TryGetProperty("lines", out JsonElement lines) || lines.ValueKind != JsonValueKind.Array)
                return stale;
            foreach (JsonElement line in lines.EnumerateArray())
            {
                if (line.ValueKind != JsonValueKind.Object || Str(line, "id") is not { } id)
                    continue;
                if (Str(line, "status") != "ok")
                    continue;
                if (IsStale(Str(line, "text"), Str(line, "spoken"), glossary.Effective))
                    stale.Add(id);
            }
        }
        catch (JsonException)
        {
            // An output we cannot read has no lines to judge.
        }

        return stale;
    }

    /// <summary>The glossary digest recorded in a Voiceover output (<c>meta.pronunciation.glossaryDigest</c>) equals <paramref name="now"/>.</summary>
    public static bool SameGlossary(JsonElement outputRoot, string? now)
    {
        if (now is null
            || !outputRoot.TryGetProperty("meta", out JsonElement meta) || meta.ValueKind != JsonValueKind.Object
            || !meta.TryGetProperty("pronunciation", out JsonElement pron) || pron.ValueKind != JsonValueKind.Object)
            return false;
        return string.Equals(Str(pron, "glossaryDigest"), now, StringComparison.Ordinal);
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
