using System;
using System.Text.Json;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// The "needs attention" view of a run that finished <c>Passed</c> although it is not a clean pass:
/// either a review loop stopped below its minimum score (<c>review_below_minimum</c>), or the compile
/// shipped with some declared narration lines never spoken (<c>narration_incomplete</c>). Parsed from
/// <c>workflow_executions.attention_json</c> (written by the engine); the run's status is unchanged.
/// </summary>
public sealed record ExecutionAttention(string Reason, int StepOrder, int Score, int MinScore, int Iterations, string Message)
{
    /// <summary>The status the assistant reports for a Passed run that needs attention.</summary>
    public const string StatusName = "NeedsAttention";

    /// <summary>
    /// A Passed run whose compile's voiceover node shows some of the declared narration lines were
    /// never spoken. Its attention JSON keeps the review-below-minimum field set and adds the line
    /// counts the narration-specific message is built from.
    /// </summary>
    public const string NarrationIncompleteReason = "narration_incomplete";

    /// <summary>The user-facing wording, exact.</summary>
    public static string BuildMessage(int iterations, int score, int minScore) =>
        $"The review stopped after {iterations} {(iterations == 1 ? "round" : "rounds")} at {score}/10; the target was {minScore}/10. The film is ready but did not reach the quality bar.";

    /// <summary>
    /// The user-facing wording for a Passed run that shipped with narration missing: how many of the
    /// declared lines were actually spoken, and how many of those had to be re-anchored to a nearby
    /// kept clip because the edit cut away the moment they were written for.
    /// </summary>
    public static string BuildNarrationMessage(int spokenLines, int declaredLines, int reanchoredLines)
    {
        string lines = declaredLines == 1 ? "line was" : "lines were";
        string message = $"Only {spokenLines} of {declaredLines} narration {lines} spoken in the film.";

        int moved = Math.Clamp(reanchoredLines, 0, spokenLines);
        if (moved > 0)
            message += $" {moved} of them {(moved == 1 ? "was" : "were")} re-anchored to a nearby kept clip.";

        return message + " The film is ready but the narration is incomplete.";
    }

    /// <summary>
    /// The <c>workflow_executions.attention_json</c> payload for narratively-incomplete output. Same
    /// fields as the review-exhausted shape (so every reader keeps parsing it), plus the line counts.
    /// </summary>
    public static string ToNarrationJson(
        int stepOrder, int score, int minScore, int iterations,
        int spokenLines, int declaredLines, int reanchoredLines)
    {
        var root = new System.Text.Json.Nodes.JsonObject
        {
            ["reason"] = NarrationIncompleteReason,
            ["stepOrder"] = stepOrder,
            ["score"] = score,
            ["minScore"] = minScore,
            ["iterations"] = iterations,
            ["spokenLines"] = spokenLines,
            ["declaredLines"] = declaredLines
        };
        if (reanchoredLines > 0)
            root["reanchoredLineCount"] = reanchoredLines;
        return root.ToJsonString();
    }

    /// <summary>The camelCase object the assistant tools return as <c>needsAttention</c>.</summary>
    public object ToToolShape() => new { reason = Reason, stepOrder = StepOrder, score = Score, minScore = MinScore, iterations = Iterations, message = Message };

    /// <summary>Parses the stored JSON; null for null, blank or malformed input.</summary>
    public static ExecutionAttention? TryParse(string? attentionJson)
    {
        if (string.IsNullOrWhiteSpace(attentionJson))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(attentionJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("reason", out JsonElement reason) || reason.ValueKind != JsonValueKind.String
                || !TryInt(root, "stepOrder", out int stepOrder)
                || !TryInt(root, "score", out int score)
                || !TryInt(root, "minScore", out int minScore)
                || !TryInt(root, "iterations", out int iterations))
                return null;

            string reasonName = reason.GetString()!;
            if (string.Equals(reasonName, NarrationIncompleteReason, StringComparison.Ordinal))
            {
                // The narration message needs the counts the compile reported; a row missing them is
                // malformed rather than a generic review-exhausted sentence about a problem it is not.
                if (!TryInt(root, "spokenLines", out int spokenLines)
                    || !TryInt(root, "declaredLines", out int declaredLines)
                    || spokenLines < 0 || declaredLines < spokenLines)
                    return null;

                int reanchoredLines = TryInt(root, "reanchoredLineCount", out int reanchored) ? reanchored : 0;
                return new ExecutionAttention(reasonName, stepOrder, score, minScore, iterations,
                    BuildNarrationMessage(spokenLines, declaredLines, reanchoredLines));
            }

            return new ExecutionAttention(reasonName, stepOrder, score, minScore, iterations,
                BuildMessage(iterations, score, minScore));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value);
    }
}
