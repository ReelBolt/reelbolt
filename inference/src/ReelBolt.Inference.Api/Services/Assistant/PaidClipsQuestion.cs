using System.Text.Json;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// The answer <c>ExecuteWorkflow</c> gives, instead of running anything, when a workflow generates paid video
/// clips and the caller has not yet said whether to reuse the earlier ones or make new ones.
/// </summary>
/// <remarks>
/// One implementation for two callers: the tool, and the permission gate. The gate answers this itself rather
/// than calling the tool — "the tool will only ask a question" is a claim about a database read that the tool
/// repeats later, and a workflow edited in between would turn that claim false and start a paid run with no
/// card. The gate never lets the tool run on that claim.
/// </remarks>
public static class PaidClipsQuestion
{
    public static string Build(string workflowName, string projectName, IEnumerable<string?> videoGenerateConfigs)
    {
        decimal? cap = 0m;
        foreach (string? json in videoGenerateConfigs)
        {
            try
            {
                var config = JsonSerializer.Deserialize<VideoGenerateStepConfig>(json ?? "{}", VideoGenerateStepConfig.JsonOptions);
                cap = config is { MaxSpendCredits: > 0m } ? cap + config.MaxSpendCredits : null;
            }
            catch (JsonException)
            {
                cap = null;
            }

            if (cap is null)
                break;
        }

        return JsonSerializer.Serialize(new
        {
            executed = false,
            needsDecision = true,
            message = "This workflow generates paid video clips. Ask the user: reuse previously generated clips (free, default) or regenerate them (costs money)? Then call ExecuteWorkflow again with regenerateVideoClips=false or true.",
            estimatedMaxCostUsd = cap,
            estimateNote = "Upper bound: the sum of the video steps' Max Spend caps; null when it cannot be computed.",
            workflowName,
            projectName
        });
    }
}
