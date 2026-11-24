using System.Globalization;
using System.Text.Json.Nodes;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// One sentence on <c>outputSummary.loudness</c> that says what the platform-loudness pass did and
/// why, from the numbers the node already carries. <b>Pure.</b>
/// </summary>
public static class LoudnessExplanation
{
    public static void Annotate(JsonObject node)
    {
        if (Build(node) is { } text)
            node["explanation"] = text;
    }

    /// <summary>The sentence, or null for a node with no target (<c>Off</c>).</summary>
    public static string? Build(JsonObject node)
    {
        string? target = Str(node, "target");
        if (target is null or "Off" || Num(node, "targetLufs") is not { } targetLufs)
            return null;

        string targetText = $"the {target} target of {Fmt(targetLufs, "0.#")} LUFS";
        if (node["applied"] is JsonValue applied && applied.TryGetValue(out bool wasApplied) && wasApplied &&
            Num(node, "measuredLufs") is { } measured && Num(node, "gainDb") is { } gain)
        {
            string change = Math.Abs(gain) < 0.05
                ? $"already at {targetText}"
                : gain > 0
                    ? $"raised by +{Fmt(gain, "0.0")} dB to {targetText}"
                    : $"lowered by {Fmt(-gain, "0.0")} dB to {targetText}";
            // QA-P1-19 item 4: say the true peak was LIMITED only when the input peak actually exceeded the
            // target. A mix already under it was never limited, and claiming otherwise is a false fact.
            string peak = Num(node, "targetTruePeak") is { } tp
                ? Num(node, "measuredTruePeak") is { } before && before > tp
                    ? $"; true peak limited to {Fmt(tp, "0.#")} dBTP"
                    : $"; true peak already within {Fmt(tp, "0.#")} dBTP"
                : "";
            return $"Measured {Fmt(measured, "0.0")} LUFS, {change}{peak}.";
        }

        string why = Str(node, "reason") switch
        {
            "no_audio_in_program" => "the finished program has no audio",
            "audio_stream_copied" => "the audio stream is copied, not re-encoded",
            "not_measurable" => "the program's loudness could not be measured",
            "measure_failed" => "the loudness measurement failed",
            "apply_failed" => "applying the gain failed, so the mix is left as encoded",
            "normalize_failed" => "setting the loudness failed, so the mix is left as encoded",
            _ => "it did not run"
        };
        return $"The {target} target of {Fmt(targetLufs, "0.#")} LUFS was not applied: {why}.";
    }

    private static string? Str(JsonObject node, string key) =>
        node[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static double? Num(JsonObject node, string key) =>
        node[key] is JsonValue v && v.TryGetValue(out double d) ? d : null;

    private static string Fmt(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}
