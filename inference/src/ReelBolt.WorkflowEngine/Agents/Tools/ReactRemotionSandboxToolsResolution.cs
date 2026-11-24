using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// Carries the <c>outputResolution</c> report the Author's render tool returned (see
/// <c>OutputResolutionReport.ForRemotion</c>) into the Author step's own output, at the top level where
/// the API's <c>GetWorkflowExecution</c> warning scan reads it. The tool result alone never reached the
/// step output, which is the agent's closing text.
/// </summary>
public static class AuthorRenderResolution
{
    private const string RenderToolName = "RenderVideoAndUploadToStorage";

    /// <summary>
    /// <paramref name="output"/> with the last render's report added. A JSON object output keeps its own
    /// properties — bare, fenced or prose-wrapped, the object itself is taken so a downstream reader that
    /// extracts the JSON object still finds it; only text with no JSON object at all is wrapped as
    /// <c>{"message": text, "outputResolution": ...}</c>. Unchanged (byte-identical) when no render
    /// reported a resolution.
    /// </summary>
    public static string Merge(string output, IReadOnlyList<AgentToolCallTrace> toolCalls)
    {
        JsonNode? report = LastReport(toolCalls);
        if (report is null)
            return output;

        JsonObject? target = null;
        if (!string.IsNullOrWhiteSpace(output))
        {
            bool bareJson = false;
            try
            {
                JsonNode? parsed = JsonNode.Parse(output);
                bareJson = parsed is not null;
                target = parsed as JsonObject;
            }
            catch (JsonException)
            {
                // Not bare JSON: try the object inside a fence or prose below.
            }

            // QA-P0-23: a fenced (```json {...} ```) or prose-wrapped JSON object is the Author's real
            // output. Take the object itself rather than wrapping the whole text, which hid it from every
            // downstream reader that extracts the (first/last) JSON object — e.g. the loop-back manifest note.
            // Only when the text is NOT bare JSON: a bare array (or scalar) is left whole and wrapped below,
            // rather than collapsed to the first object mined out of it.
            if (!bareJson)
                target ??= EmbeddedJsonObject(output);
        }

        target ??= new JsonObject { ["message"] = output };
        target["outputResolution"] = report;
        return target.ToJsonString();
    }

    /// <summary>The first balanced JSON object embedded in <paramref name="text"/>, or null when there is none.</summary>
    private static JsonObject? EmbeddedJsonObject(string text)
    {
        try
        {
            return RobustJsonExtractor.ExtractJsonObject(text) is { } json
                ? JsonNode.Parse(json) as JsonObject
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode? LastReport(IReadOnlyList<AgentToolCallTrace> toolCalls)
    {
        for (int i = toolCalls.Count - 1; i >= 0; i--)
        {
            AgentToolCallTrace call = toolCalls[i];
            if (!string.Equals(call.ToolName, RenderToolName, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(call.Result)
                || !call.Result.Contains("outputResolution", StringComparison.Ordinal))
                continue;
            try
            {
                if (JsonNode.Parse(call.Result) is JsonObject result && result["outputResolution"] is JsonObject report)
                    return JsonNode.Parse(report.ToJsonString());
            }
            catch (JsonException)
            {
                // A non-JSON tool result carries no report.
            }
        }

        return null;
    }
}
