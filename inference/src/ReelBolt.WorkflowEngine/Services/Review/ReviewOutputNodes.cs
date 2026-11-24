using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Review;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;

namespace ReelBolt.WorkflowEngine.Services.Review;

/// <summary>
/// Reads and writes engine-owned nodes on a reviewer's answer the way <see cref="VisualCheckReport"/> does: on the
/// verdict object when it appears verbatim in the output, else on the root of a single-object output, else as an
/// appended object. Everything read back sits in model-written text, so callers re-validate what they read.
/// </summary>
internal static class ReviewOutputNodes
{
    private static readonly JsonSerializerOptions NodeJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Opens the engine's own record at the very end of a review's output. The record of a re-run (what it reuses, which
    /// changes it carries out) is read back only from here, never from the reviewer's text, which can hold any JSON.
    /// </summary>
    internal const string RecordMarker = ReviewEngineRecord.Marker;

    /// <summary>The output without any engine record (the reviewer's text, plus whatever was added to it in place).</summary>
    public static string WithoutRecord(string? output)
    {
        return ReviewEngineRecord.Without(output);
    }

    /// <summary>The output with <paramref name="record"/> as its engine record, replacing any record it had.</summary>
    public static string WithRecord(string? output, JsonObject record) =>
        WithoutRecord(output) + RecordMarker + record.ToJsonString(NodeJson);

    /// <summary>The engine record at the end of the output, or null when there is none or it cannot be read.</summary>
    public static JsonObject? Record(string? output)
    {
        string text = output ?? string.Empty;
        int at = text.IndexOf(RecordMarker, StringComparison.Ordinal);
        if (at < 0)
            return null;
        try
        {
            Utf8JsonReader reader = new(System.Text.Encoding.UTF8.GetBytes(text[(at + RecordMarker.Length)..]));
            return JsonNode.Parse(ref reader) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The verdict object the loop scores (a parsed copy), or null when there is none.</summary>
    public static JsonObject? Verdict(string? reviewOutput, AgentType? agentType)
    {
        reviewOutput = WithoutRecord(reviewOutput);
        if (string.IsNullOrWhiteSpace(reviewOutput))
            return null;
        try
        {
            string? verdictJson = ReviewLoopStepExecutor.ScanReviewVerdicts(reviewOutput, agentType).Verdict?.Json;
            if (verdictJson is not null && JsonNode.Parse(verdictJson) is JsonObject verdict)
                return verdict;
            return JsonNode.Parse(RobustJsonExtractor.ExtractJsonObject(reviewOutput) ?? reviewOutput) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The output with <paramref name="mutate"/> applied to the verdict object (or the fallbacks above). Never throws.</summary>
    public static string Mutate(string reviewOutput, AgentType? agentType, Action<JsonObject> mutate)
    {
        // An engine record rides at the end, untouched by whatever is added to the text before it.
        string full = reviewOutput ?? string.Empty;
        string head = WithoutRecord(full);
        return MutateText(head, agentType, mutate) + full[head.Length..];
    }

    private static string MutateText(string reviewOutput, AgentType? agentType, Action<JsonObject> mutate)
    {
        string output = reviewOutput ?? string.Empty;
        try
        {
            string? verdictJson = ReviewLoopStepExecutor.ScanReviewVerdicts(output, agentType).Verdict?.Json;
            if (verdictJson is not null)
            {
                int at = output.IndexOf(verdictJson, StringComparison.Ordinal);
                if (at >= 0 && JsonNode.Parse(verdictJson) is JsonObject verdict)
                {
                    mutate(verdict);
                    return string.Concat(output.AsSpan(0, at), verdict.ToJsonString(NodeJson), output.AsSpan(at + verdictJson.Length));
                }
            }

            if (JsonNode.Parse(output.Trim()) is JsonObject root)
            {
                mutate(root);
                return root.ToJsonString(NodeJson);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // Unreadable reviewer text: fall through to the appended form.
        }

        JsonObject wrapper = [];
        mutate(wrapper);
        return output + (output.Length > 0 ? "\n" : string.Empty) + wrapper.ToJsonString(NodeJson);
    }

    /// <summary>Adds <c>{code, message}</c> to the object's <c>warnings</c> array (the run page's list), creating it when absent.</summary>
    public static void AddWarning(JsonObject target, string code, string message)
    {
        if (target["warnings"] is not JsonArray warnings)
        {
            warnings = [];
            target["warnings"] = warnings;
        }

        warnings.Add(new JsonObject { ["code"] = code, ["message"] = message });
    }
}
