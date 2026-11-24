using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The two deterministic intelligibility facts the compile step publishes on its <c>voiceover</c>
/// node for <c>VideoReviewAgent</c> (see docs/voiceover.md "Review facts"):
/// <list type="bullet">
/// <item><c>voiceover.headroom</c> — how far each applied narration line sits ABOVE the dialogue
/// of the shot it starts over, in dB: the line WAV's own mean RMS minus the analyze step's measured
/// shot RMS. Positive is narration above dialogue; a small or negative number is narration a
/// listener has to fish out from under the take.</item>
/// <item><c>voiceover.wer</c> — the word error rate of what the recogniser heard in the synthesized
/// line against the sanitized text the step asked it to speak. Deterministic Levenshtein over
/// normalized tokens. A high number means the synthesis is mispronouncing or garbling the words —
/// a fact, not a judgment call.</item>
/// </list>
/// Both nodes are ALWAYS present on the voiceover node and carry <c>applicable:false</c> (never an
/// absent key, never a throw) when they cannot be computed — no applied lines, a WAV the RMS
/// sampler cannot read, no measured shot audio, or no word alignment.
/// </summary>
public static class NarrationIntelligibilityFacts
{
    public static JsonObject BuildHeadroomNode(IReadOnlyList<(string LineId, double? LineRmsDbfs, double? ShotRmsDbfs)> inputs)
    {
        var lines = new JsonArray();
        List<double> headrooms = [];
        foreach ((string lineId, double? lineRms, double? shotRms) in inputs)
        {
            if (lineRms is null || shotRms is null || !double.IsFinite(lineRms.Value) || !double.IsFinite(shotRms.Value))
                continue;
            double headroom = Math.Round(lineRms.Value - shotRms.Value, 2);
            headrooms.Add(headroom);
            lines.Add(new JsonObject
            {
                ["lineId"] = lineId,
                ["lineRmsDbfs"] = Math.Round(lineRms.Value, 2),
                ["dialogueRmsDbfs"] = Math.Round(shotRms.Value, 2),
                ["headroomDb"] = headroom
            });
        }

        if (headrooms.Count == 0)
        {
            return new JsonObject
            {
                ["applicable"] = false,
                ["reason"] = inputs.Count == 0 ? "no_applied_lines" : "not_measurable"
            };
        }

        return new JsonObject
        {
            ["applicable"] = true,
            ["measuredLineCount"] = headrooms.Count,
            ["meanHeadroomDb"] = Math.Round(headrooms.Average(), 2),
            ["minHeadroomDb"] = Math.Round(headrooms.Min(), 2),
            ["lines"] = lines
        };
    }

    public static JsonObject BuildWerNode(IReadOnlyList<(string LineId, string? Text, List<CaptionSourceWord>? Words)> inputs)
    {
        var lines = new JsonArray();
        List<double> wers = [];
        foreach ((string lineId, string? text, List<CaptionSourceWord>? words) in inputs)
        {
            if (string.IsNullOrWhiteSpace(text) || words is null || words.Count == 0)
                continue;
            string[] reference = Normalize(text);
            if (reference.Length == 0)
                continue;
            string[] hypothesis = AlignSpelling(reference, Normalize(string.Join(' ', words.Select(w => w.Text))));
            double wer = Math.Round(WordErrorRate(reference, hypothesis), 3);
            wers.Add(wer);
            lines.Add(new JsonObject
            {
                ["lineId"] = lineId,
                ["referenceWords"] = reference.Length,
                ["recognizedWords"] = hypothesis.Length,
                ["wer"] = wer
            });
        }

        if (wers.Count == 0)
        {
            return new JsonObject
            {
                ["applicable"] = false,
                ["reason"] = inputs.Count == 0 ? "no_applied_lines" : "no_word_alignment"
            };
        }

        return new JsonObject
        {
            ["applicable"] = true,
            ["measuredLineCount"] = wers.Count,
            ["meanWer"] = Math.Round(wers.Average(), 3),
            ["maxWer"] = Math.Round(wers.Max(), 3),
            ["lines"] = lines
        };
    }

    /// <summary>Mean RMS (dBFS, power-averaged over 250 ms windows) of a 16-bit PCM WAV, or null when the file cannot be read or sampled.</summary>
    public static double? TryMeasureMeanRmsDbfs(string localPath)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(localPath);
            IReadOnlyList<WavRmsSampler.RmsWindow> windows = WavRmsSampler.Sample(bytes, 250);
            if (windows.Count == 0)
                return null;

            // Power average — the loud windows carry the level a listener hears; averaging dB
            // values directly would let silent gaps between words drag the number down.
            double meanPower = windows.Average(w => Math.Pow(10, w.RmsDbfs / 10.0));
            if (meanPower <= 0)
                return null;
            return 10 * Math.Log10(meanPower);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Lower-cases, strips everything but letters/digits/apostrophes, drops empty tokens.</summary>
    internal static string[] Normalize(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{Nd}' ]+", " ", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim('\''))
            .Where(t => t.Length > 0)
            .ToArray();

    /// <summary>
    /// The recogniser's tokens with ITS spelling of a long reference word replaced by the reference's
    /// own: an invented name is heard correctly but written however the recogniser likes —
    /// "ReelBolt" came back as "Real Bolt" on every line and was scored a mispronunciation, which
    /// the reviewer then capped the score for. A hypothesis token (or two adjacent tokens joined)
    /// counts as the reference word when the reference is at least <see cref="MinSpellingWordLength"/>
    /// letters long and the two differ by at most a fifth of its letters (one edit for an 8-letter word:
    /// "realbolt" matches "reelbolt", "realboat" does not). Short words are compared
    /// exactly — "a"/"the" swaps and real substitutions still count.
    /// </summary>
    internal static string[] AlignSpelling(string[] reference, string[] hypothesis)
    {
        var long_ = reference.Where(r => r.Length >= MinSpellingWordLength).Distinct().ToArray();
        if (long_.Length == 0)
            return hypothesis;

        var result = new List<string>(hypothesis.Length);
        for (int i = 0; i < hypothesis.Length; i++)
        {
            string? joined = i + 1 < hypothesis.Length ? hypothesis[i] + hypothesis[i + 1] : null;
            string? viaPair = joined is null ? null : long_.FirstOrDefault(r => !reference.Contains(hypothesis[i]) && Close(r, joined));
            if (viaPair is not null)
            {
                result.Add(viaPair);
                i++;
                continue;
            }

            string token = hypothesis[i];
            result.Add(reference.Contains(token) ? token : long_.FirstOrDefault(r => Close(r, token)) ?? token);
        }

        return result.ToArray();
    }

    /// <summary>Reference words at least this long are compared by spelling distance, not exactly.</summary>
    internal const int MinSpellingWordLength = 6;

    private static bool Close(string reference, string candidate) =>
        (Math.Abs(reference.Length - candidate.Length) <= Math.Max(1, reference.Length / 5) &&
         CharDistance(reference, candidate) <= Math.Max(1, reference.Length / 5)) ||
        SoundsAlike(reference, candidate);

    /// <summary>
    /// Whether two spellings of one long word share a consonant skeleton once voiced and unvoiced
    /// pairs are merged: the recogniser wrote "ReelBolt" as "Real bold" (two edits, so not
    /// <see cref="Close"/> by spelling) and the reviewer asked for the brand to be respelled. A
    /// skeleton of at least four consonants keeps short words ("battle"/"bottle") apart.
    /// </summary>
    internal static bool SoundsAlike(string reference, string candidate)
    {
        string a = Skeleton(reference), b = Skeleton(candidate);
        return a.Length >= 4 && a == b;
    }

    private static string Skeleton(string word)
    {
        var sb = new System.Text.StringBuilder(word.Length);
        foreach (char raw in word)
        {
            char c = raw switch { 'b' => 'p', 'd' => 't', 'g' => 'k', 'v' => 'f', 'z' => 's', _ => raw };
            if ("aeiouy'".Contains(c) || !char.IsLetter(c))
                continue;
            if (sb.Length == 0 || sb[^1] != c)
                sb.Append(c);
        }

        return sb.ToString();
    }

    private static int CharDistance(string a, string b)
    {
        int[] prev = new int[b.Length + 1], cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
            prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    /// <summary>Levenshtein distance over tokens divided by the reference length (1.0 when the reference is empty and the hypothesis is not).</summary>
    internal static double WordErrorRate(string[] reference, string[] hypothesis)
    {
        if (reference.Length == 0)
            return hypothesis.Length == 0 ? 0 : 1;

        int[] previous = new int[hypothesis.Length + 1];
        int[] current = new int[hypothesis.Length + 1];
        for (int j = 0; j <= hypothesis.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= reference.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= hypothesis.Length; j++)
            {
                int cost = string.Equals(reference[i - 1], hypothesis[j - 1], StringComparison.Ordinal) ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return Math.Min(1.0, previous[hypothesis.Length] / (double)reference.Length);
    }
}
