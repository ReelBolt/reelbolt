using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Replay;

namespace ReelBolt.WorkflowEngine.Execution.Caching;

/// <summary>
/// The step-cache key component that covers what a step reads from EARLIER steps by step order
/// (<see cref="StepCacheKeyInputs.UpstreamDigest"/>, schema v5). The dependency analysis says which steps
/// those are; the run's <see cref="StepOutputHistoryEntry"/> list says what they produced this time. Each
/// is framed length-prefixed (output text, output storage key, artifact storage key), so a changed
/// analysis artifact, a re-rendered source video or a changed decision always changes the key —
/// over-inclusion (a step that does not really read something) only costs a miss, never a wrong hit.
/// </summary>
public static class StepUpstreamDigest
{
    public static string Compute(WorkflowStepDependencyGraph graph, WorkflowStep step, IReadOnlyList<StepOutputHistoryEntry> history)
    {
        StringBuilder sb = new();
        foreach (int order in graph.DependsOn(step.StepOrder).Keys.Order())
        {
            // The freshest entry for that order: a review loop's re-run supersedes the earlier one.
            StepOutputHistoryEntry? entry = history.LastOrDefault(h => h.StepOrder == order);
            sb.Append(order.ToString(CultureInfo.InvariantCulture)).Append('|');
            if (entry is null)
            {
                sb.Append("-\n");
                continue;
            }

            Append(sb, entry.Output);
            Append(sb, entry.OutputStorageKey);
            Append(sb, entry.ArtifactStorageKey);
            sb.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static void Append(StringBuilder sb, string? value)
    {
        string v = value ?? "\0null";
        sb.Append(v.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(v).Append('|');
    }
}
