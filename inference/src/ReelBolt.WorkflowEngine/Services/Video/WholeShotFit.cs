using System.Text.Json.Nodes;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Applies <see cref="WholeShotPlanner"/> to a compile's resolved span list (the call-site glue for
/// <c>TargetDurationMode.WholeShots</c>), so the executor's diff stays a single call.
/// </summary>
public static class WholeShotFit
{
    /// <summary>
    /// The spans to keep, in order, and the <c>length.fitted</c> report; the report is null when the cut
    /// already fits (nothing dropped, nothing to say).
    /// </summary>
    public static (List<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)> Spans, JsonObject? Report) Apply(
        List<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)> spans,
        double targetSec)
    {
        WholeShotPlan plan = WholeShotPlanner.Plan(spans.Select(s => s.End - s.Start).ToList(), ranks: null, targetSec);
        if (plan.Reason is not null)
        {
            return (spans, new JsonObject
            {
                ["mode"] = "WholeShots",
                ["editedSec"] = Math.Round(plan.TotalSec, 3),
                ["targetSec"] = Math.Round(targetSec, 3),
                ["achievedSec"] = Math.Round(plan.TotalSec, 3),
                ["reason"] = plan.Reason
            });
        }

        if (plan.Dropped.Count == 0)
            return (spans, null);

        JsonObject report = plan.ToReport(spans.Select(s => (s.Start, s.End, s.SourceIndex)).ToList());
        return (plan.Kept.Select(i => spans[i]).ToList(), report);
    }
}
