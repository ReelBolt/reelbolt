using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReelBolt.Shared.Inference;

/// <summary>One speaker turn reported by a diarizing provider (ids are opaque, per response).</summary>
public sealed record SpeakerTurn(string Speaker, double StartSec, double EndSec);

/// <summary>
/// Provider-agnostic speaker-label helpers. Labels are the provider's own opaque ids and are only
/// consistent inside ONE response; nothing here guesses a speaker.
/// </summary>
public static partial class TranscriptionDiarization
{
    public const string FishDiarizingModel = "transcribe-1-pro";
    public const string OpenAiDiarizingModel = "gpt-4o-transcribe-diarize";

    /// <summary>Only Fish <c>transcribe-1-pro</c> diarizes; <c>transcribe-1</c> does not.</summary>
    public static bool FishModelDiarizes(string? model) =>
        string.Equals(model?.Trim(), FishDiarizingModel, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// First-party table of OpenAI-protocol models that return diarized segments. <c>gpt-transcribe</c>
    /// is not listed: it is not documented to diarize.
    /// </summary>
    public static bool OpenAiModelDiarizes(string? model) =>
        string.Equals(model?.Trim(), OpenAiDiarizingModel, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"<\|[^|>]*\|>")]
    private static partial Regex MarkerRegex();

    /// <summary>Removes inline <c>&lt;|speaker:N|&gt;</c> style markers and collapses the gap they leave.</summary>
    public static string StripMarkers(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("<|", StringComparison.Ordinal))
            return text;
        return Regex.Replace(MarkerRegex().Replace(text, " "), @"\s{2,}", " ").Trim();
    }

    /// <summary>Parses Fish <c>speaker_turns</c> [{speaker,start,end}]; absent or malformed gives empty.</summary>
    public static IReadOnlyList<SpeakerTurn> ParseFishTurns(JsonElement root)
    {
        if (!root.TryGetProperty("speaker_turns", out JsonElement el) || el.ValueKind != JsonValueKind.Array)
            return Array.Empty<SpeakerTurn>();
        List<SpeakerTurn> turns = new();
        foreach (JsonElement t in el.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object
                || !t.TryGetProperty("speaker", out JsonElement sp) || sp.ValueKind != JsonValueKind.String
                || !t.TryGetProperty("start", out JsonElement s) || s.ValueKind != JsonValueKind.Number
                || !t.TryGetProperty("end", out JsonElement e) || e.ValueKind != JsonValueKind.Number)
                continue;
            string id = sp.GetString() ?? "";
            if (id.Length == 0) continue;
            turns.Add(new SpeakerTurn(id, s.GetDouble(), e.GetDouble()));
        }
        return turns;
    }

    /// <summary>
    /// The speaker of the turn containing the midpoint of [start,end]; null when no turn contains
    /// it (a gap) — never the nearest turn. If turns overlap, the first listed wins.
    /// </summary>
    public static string? SpeakerAt(IReadOnlyList<SpeakerTurn> turns, double startSec, double endSec)
    {
        if (turns.Count == 0) return null;
        double mid = (startSec + endSec) / 2.0;
        foreach (SpeakerTurn t in turns)
        {
            if (mid >= t.StartSec && mid < t.EndSec)
                return t.Speaker;
        }
        return null;
    }

    /// <summary>
    /// Maps an OpenAI <c>diarized_json</c> body: segments [{start,end,text,speaker}], no words.
    /// </summary>
    public static TranscriptResult ParseOpenAiDiarized(JsonElement root)
    {
        string text = root.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? "" : "";
        List<TranscriptSegment> segments = new();
        if (root.TryGetProperty("segments", out JsonElement segs) && segs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement s in segs.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                string segText = s.TryGetProperty("text", out JsonElement x) && x.ValueKind == JsonValueKind.String
                    ? x.GetString() ?? "" : "";
                double start = s.TryGetProperty("start", out JsonElement a) && a.ValueKind == JsonValueKind.Number ? a.GetDouble() : 0;
                double end = s.TryGetProperty("end", out JsonElement b) && b.ValueKind == JsonValueKind.Number ? b.GetDouble() : 0;
                string? speaker = s.TryGetProperty("speaker", out JsonElement sp) && sp.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(sp.GetString()) ? sp.GetString() : null;
                segments.Add(new TranscriptSegment(segText, start, end, Speaker: speaker));
            }
        }
        string? lang = root.TryGetProperty("language", out JsonElement l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
        return new TranscriptResult(text, segments, Array.Empty<TranscriptWord>(), lang);
    }

    /// <summary>
    /// Chunk-merge policy: speaker ids are opaque per response, so label "A" in chunk 1 and "A" in
    /// chunk 2 may be different people. With a single chunk labels are kept as-is. With more than one
    /// chunk, labels are DROPPED (set to null) rather than guessed or falsely joined.
    /// </summary>
    public static TranscriptResult MergeChunkSpeakers(TranscriptResult merged, int chunkCount)
    {
        if (chunkCount <= 1) return merged;
        if (!merged.Segments.Any(s => s.Speaker != null) && !merged.Words.Any(w => w.Speaker != null))
            return merged;
        return merged with
        {
            Segments = merged.Segments.Select(s => s with { Speaker = null }).ToList(),
            Words = merged.Words.Select(w => w with { Speaker = null }).ToList()
        };
    }
}
