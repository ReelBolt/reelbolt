using System.Text.Json.Nodes;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>One sound-effect cue as the coverage measure needs it: where it starts in the film, what it is anchored to, how loud.</summary>
public sealed record CoverageSfxCue(double OutputStartSec, string? AnchorId, double GainDb);

/// <summary>
/// Deterministic measures of how much of the spoken source an edit kept and where its sound effects sit
/// (docs/video-editing.md "Footage edit quality gate"). Pure: tokens, final spans and cues in, one output node out.
/// </summary>
public static class SpeechCoverage
{
    private const double Eps = 0.001;

    public static JsonObject? Build(
        IReadOnlyList<(double Start, double End, int SourceIndex)> spans,
        IReadOnlyList<SpeechToken> tokens,
        IReadOnlyList<CoverageSfxCue> cues,
        double targetDurationSec,
        ISet<int>? skipSources = null)
    {
        if (spans.Count == 0)
            return null;
        var used = spans.Select(s => s.SourceIndex).Where(i => skipSources?.Contains(i) != true).ToHashSet();
        var speech = tokens.Where(t => used.Contains(t.SourceIndex) && t.End > t.Start).ToList();
        if (speech.Count == 0 && cues.Count == 0)
            return null;

        var node = new JsonObject();
        if (speech.Count > 0)
        {
            double total = 0;
            double kept = 0;
            foreach (SpeechToken t in speech)
            {
                total += t.End - t.Start;
                foreach (var s in spans.Where(s => s.SourceIndex == t.SourceIndex))
                    kept += Math.Max(0, Math.Min(t.End, s.End) - Math.Max(t.Start, s.Start));
            }

            node["lastKeptSourceSec"] = Math.Round(spans.Where(s => used.Contains(s.SourceIndex)).Max(s => s.End), 2);
            node["lastSpeechSourceSec"] = Math.Round(speech.Max(t => t.End), 2);
            node["droppedSpeechPct"] = total > 0 ? Math.Round(100.0 * Math.Max(0, 1 - kept / total), 1) : 0;
            node["targetLengthSec"] = targetDurationSec > 0 ? Math.Round(targetDurationSec, 1) : 0;
        }

        if (cues.Count > 0)
        {
            int onEvent = cues.Count(c => IsEventAnchor(c.AnchorId));
            // Event-anchored cues are deliberately kept where the moment happens, so only shot-anchored cues count as over speech.
            int overSpeech = cues.Count(c => !IsEventAnchor(c.AnchorId) && InsideSpeech(c.OutputStartSec, spans, speech));
            node["sfx"] = new JsonObject
            {
                ["cues"] = cues.Count,
                ["onEvent"] = onEvent,
                ["overSpeech"] = overSpeech,
                ["minGainDb"] = Math.Round(cues.Min(c => c.GainDb), 1)
            };
        }

        return node;
    }

    internal static bool IsEventAnchor(string? id) =>
        id is { Length: > 1 } && id[0] == 'e' && id.Skip(1).All(char.IsDigit);

    private static bool InsideSpeech(
        double outputSec, IReadOnlyList<(double Start, double End, int SourceIndex)> spans, IReadOnlyList<SpeechToken> speech)
    {
        double at = 0;
        foreach (var s in spans)
        {
            double len = s.End - s.Start;
            if (outputSec >= at - Eps && outputSec < at + len)
            {
                double source = s.Start + Math.Max(0, outputSec - at);
                return speech.Any(t => t.SourceIndex == s.SourceIndex && t.Start + Eps < source && source < t.End - Eps);
            }

            at += len;
        }

        return false;
    }
}
