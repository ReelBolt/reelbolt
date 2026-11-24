using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// The interleave guard on the solo <c>Agent(VideoStoryEditor)</c> path — the twin of the edit room's
/// <c>EditRoomStepExecutor.CheckDecisionPolicy</c> (docs/video-editing.md "Using every clip offered").
///
/// <para>
/// Not the step-level retry: a failed step is retried by <c>WorkflowExecutorService</c> and FAILS the
/// workflow once retries run out, and a content policy must never do that. So the guard spends exactly
/// one extra call inside this step, with the precise feedback, keeps whichever answer satisfies it
/// (the original when neither does), and records the verdict as a top-level <c>"interleave"</c> node on
/// the step's output. The compile step re-derives the same verdict for review, and the ReviewLoop caps
/// the score when it is still unsatisfied.
/// </para>
///
/// <para>
/// Inert — the agent's result returned untouched, byte for byte — whenever the run did not offer two
/// or more usable clips, when no <c>VideoAnalyze</c> view can be found in the step's history, or when
/// the output carries no parseable decision (the schema check upstream owns that case).
/// </para>
/// </summary>
internal static class VideoStoryEditorInterleaveEnforcer
{
    private static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<AgentRunResult> EnforceAsync(
        StepExecutionContext context, IReelBoltAgent agent, string stepInput, AgentRunResult result, ILogger logger)
    {
        JsonNode? viewRoot = FindAnalyzeView(context);
        if (viewRoot is null)
            return result;

        InterleaveOffer offer = InterleaveOffer.FromView(viewRoot);
        if (offer.UsableSources.Count < 2)
            return result;

        (JsonObject? decisionNode, VideoEditDecisionOutput? decision) = ParseDecision(result.Output);
        if (decisionNode is null || decision is null)
            return result;

        InterleaveVerdict verdict = InterleaveGuard.Evaluate(offer, decision, context.UserRequest);
        AgentRunResult chosen = result;
        bool retried = false;
        int extraInput = 0, extraOutput = 0, extraTotal = 0;
        bool extraInputObserved = false, extraOutputObserved = false;

        if (!verdict.Satisfied && verdict.Reason != InterleaveGuard.ReasonNothingKept)
        {
            retried = true;
            string feedback = InterleaveGuard.BuildRetryFeedback(offer, verdict, roomProposal: null);
            string retryInput =
                $"{stepInput}\n\n---\nInterleave check (one retry):\n{feedback}\n" +
                "Return ONLY a single JSON object matching the VideoEditDecisionOutput schema.";

            logger.LogInformation(
                "Agent step {StepOrder}: VideoStoryEditor decision failed the interleave guard ({Reason}); retrying once with feedback",
                context.Step.StepOrder, verdict.Reason);

            try
            {
                AgentRunResult retry = await agent.RunAsync(retryInput, context.Step.AgentDefinitionId, context.CancellationToken);
                extraTotal = retry.TokensUsed;
                extraInput = retry.InputTokens ?? 0;
                extraOutput = retry.OutputTokens ?? 0;
                extraInputObserved = retry.InputTokens.HasValue;
                extraOutputObserved = retry.OutputTokens.HasValue;

                (JsonObject? retryNode, VideoEditDecisionOutput? retryDecision) =
                    retry.Success ? ParseDecision(retry.Output) : (null, null);
                if (retryNode is not null && retryDecision is not null)
                {
                    InterleaveVerdict retryVerdict = InterleaveGuard.Evaluate(offer, retryDecision, context.UserRequest);
                    if (retryVerdict.Satisfied)
                    {
                        chosen = retry;
                        decisionNode = retryNode;
                        verdict = retryVerdict;
                    }
                }
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Agent step {StepOrder}: interleave retry failed; keeping the original decision",
                    context.Step.StepOrder);
            }
        }

        decisionNode["interleave"] = verdict.ToJson(retried);

        return new AgentRunResult
        {
            Output = decisionNode.ToJsonString(),
            TokensUsed = result.TokensUsed + extraTotal,
            InputTokens = result.InputTokens.HasValue || extraInputObserved ? (result.InputTokens ?? 0) + extraInput : null,
            OutputTokens = result.OutputTokens.HasValue || extraOutputObserved ? (result.OutputTokens ?? 0) + extraOutput : null,
            PrimaryProviderId = result.PrimaryProviderId,
            PrimaryModelName = result.PrimaryModelName,
            ToolCalls = chosen.ToolCalls,
            Reasoning = chosen.Reasoning,
            Success = true,
            ModelCalls = chosen.ModelCalls,
            PeakContextChars = chosen.PeakContextChars,
            CharsCompacted = chosen.CharsCompacted,
            ToolLoopCapReached = chosen.ToolLoopCapReached
        };
    }

    /// <summary>
    /// The most recent prior step whose output is a <c>VideoAnalyze</c> envelope — a <c>view</c>
    /// object carrying a <c>shots</c> array. That is the offer the story editor was shown in every
    /// shipped template (the step immediately before it), and the latest one in any custom chain.
    /// </summary>
    internal static JsonNode? FindAnalyzeView(StepExecutionContext context)
    {
        for (int i = context.StepOutputHistory.Count - 1; i >= 0; i--)
        {
            string output = context.StepOutputHistory[i].Output;
            if (string.IsNullOrWhiteSpace(output) || !output.Contains("\"view\"", StringComparison.Ordinal))
                continue;

            try
            {
                string? json = RobustJsonExtractor.ExtractJsonObject(output);
                if (json is not null && JsonNode.Parse(json) is JsonObject root && root["view"]?["shots"] is JsonArray)
                    return root;
            }
            catch (JsonException)
            {
                // Not an analysis envelope; keep looking further back.
            }
        }

        return null;
    }

    private static (JsonObject? Node, VideoEditDecisionOutput? Decision) ParseDecision(string? output)
    {
        string? json = RobustJsonExtractor.ExtractLastJsonObject(output ?? string.Empty);
        if (json is null)
            return (null, null);

        try
        {
            if (JsonNode.Parse(json) is not JsonObject node)
                return (null, null);
            VideoEditDecisionOutput? decision = node.Deserialize<VideoEditDecisionOutput>(DecisionJsonOptions);
            if (decision is null)
                return (null, null);
            decision.Keep ??= [];
            return (node, decision);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
