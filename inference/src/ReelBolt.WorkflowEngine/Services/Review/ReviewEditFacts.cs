using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;

namespace ReelBolt.WorkflowEngine.Services.Review;

/// <summary>
/// The measured edit-quality facts the review loop judges (docs/video-editing.md "Footage edit quality gate"),
/// read from the compile output: cuts that land inside a spoken word, how much of the spoken source the cut list
/// dropped, and where the sound effects sit. They also drive deterministic score ceilings, so the reviewer's
/// opinion cannot lift a film past a measured defect. Pure; a missing or malformed node reads as "not measured".
/// </summary>
public static class ReviewEditFacts
{
    public const int CutsInsideWordsScoreCap = 5;
    public const int DroppedSpeechScoreCap = 6;
    public const int SfxScoreCap = 6;

    /// <summary>Above this share of dropped speech, with no target length asked for, the edit is capped.</summary>
    public const double DroppedSpeechCapPct = 35;

    /// <summary>
    /// A cue whose peak gain (<c>gainDb</c>, dBFS; the clips are peak-normalised) sits this far (or more) under the measured
    /// program loudness cannot be heard. The program loudness is the only level the compile measures (not dialogue alone), so
    /// the fact is skipped when it is missing, and it is an approximation: a -18 dBFS cue under -20 LUFS is louder, not inaudible.
    /// </summary>
    public const double InaudibleBelowDialogueDb = 14;

    public sealed record Facts(
        int? CutsInsideWords,
        double? LastKeptSourceSec,
        double? LastSpeechSourceSec,
        double? DroppedSpeechPct,
        double TargetLengthSec,
        int SfxCues,
        int SfxOnEvent,
        int SfxOverSpeech,
        double? SfxMinGainDb,
        int SfxInaudible);

    public static Facts? Read(string? compileOutput)
    {
        if (string.IsNullOrWhiteSpace(compileOutput))
            return null;
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(RobustJsonExtractor.ExtractJsonObject(compileOutput) ?? compileOutput) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is null)
            return null;
        JsonObject? cov = root["speechCoverage"] as JsonObject;
        JsonObject? covSfx = cov?["sfx"] as JsonObject;
        int? cuts = Int(root["speechSafeCuts"]?["cutsInsideWords"]);
        if (cov is null && cuts is null)
            return null;

        double? dialogue = Dbl(root["loudness"]?["measuredLufs"]);
        int sfxCues = Int(covSfx?["cues"]) ?? 0;
        int inaudible = 0;
        if (dialogue is { } level && root["sfx"]?["cues"] is JsonArray cueNodes)
            inaudible = cueNodes.Count(c => Dbl(c?["gainDb"]) is { } g && g <= level - InaudibleBelowDialogueDb);

        return new Facts(
            cuts,
            Dbl(cov?["lastKeptSourceSec"]),
            Dbl(cov?["lastSpeechSourceSec"]),
            Dbl(cov?["droppedSpeechPct"]),
            Dbl(cov?["targetLengthSec"]) ?? 0,
            sfxCues,
            Int(covSfx?["onEvent"]) ?? 0,
            Int(covSfx?["overSpeech"]) ?? 0,
            Dbl(covSfx?["minGainDb"]),
            inaudible);
    }

    /// <summary>The ceiling these facts put on a score, or null when none applies, with the plain-words reason.</summary>
    public static (int Cap, string Reason)? Cap(Facts? f)
    {
        if (f is null)
            return null;
        if (f.CutsInsideWords is > 0)
            return (CutsInsideWordsScoreCap, "cuts land inside spoken words");
        if (f.SfxCues > 0 && f.SfxOverSpeech > 0)
            return (SfxScoreCap, "sound effects sit over speech");
        if (f.SfxCues > 0 && f.SfxInaudible >= f.SfxCues)
            return (SfxScoreCap, "every sound effect is inaudible under the dialogue");
        if (f.DroppedSpeechPct is > DroppedSpeechCapPct && f.TargetLengthSec <= 0)
            return (DroppedSpeechScoreCap, "most of the spoken source was dropped");
        return null;
    }

    public static int ApplyCaps(int score, string? compileOutput) =>
        Cap(Read(compileOutput)) is { } cap ? Math.Min(score, cap.Cap) : score;

    public static string? CapReason(int score, string? compileOutput) =>
        Cap(Read(compileOutput)) is { } cap && cap.Cap == score ? cap.Reason : null;

    /// <summary>The lines added to the <c>[TIMELINE FACTS]</c> block; empty when nothing was measured.</summary>
    public static IReadOnlyList<string> Lines(string? compileOutput)
    {
        if (Read(compileOutput) is not { } f)
            return [];
        List<string> lines = [];
        if (f.CutsInsideWords is { } c)
            lines.Add($"Measured: cutsInsideWords {c}.");
        if (f.DroppedSpeechPct is { } d)
            lines.Add($"Measured source coverage: lastKeptSourceSec {Num(f.LastKeptSourceSec)}, lastSpeechSourceSec {Num(f.LastSpeechSourceSec)}, droppedSpeechPct {Num(d)}"
                + (f.TargetLengthSec > 0 ? $", brief target length {Num(f.TargetLengthSec)} s." : ", no target length in the brief."));
        if (f.SfxCues > 0)
            lines.Add($"Measured sfx: cues {f.SfxCues}, onEvent {f.SfxOnEvent}, overSpeech {f.SfxOverSpeech}, minGainDb {Num(f.SfxMinGainDb)}, inaudible {f.SfxInaudible}.");
        return lines;
    }

    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue(out double d) ? (int)d : null;
    private static double? Dbl(JsonNode? n) => n is JsonValue v && v.TryGetValue(out double d) ? d : null;
    private static string Num(double? v) => v is { } x ? x.ToString("0.##", CultureInfo.InvariantCulture) : "?";
}
