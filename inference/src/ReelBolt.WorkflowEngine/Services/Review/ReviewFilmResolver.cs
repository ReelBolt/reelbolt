using ReelBolt.WorkflowEngine.Execution;

namespace ReelBolt.WorkflowEngine.Services.Review;

/// <summary>
/// "The final film as of this review": the playable video output of the newest step (by step order) that ran
/// before the review in this execution. A VideoCompile after an Author render therefore wins over the loop
/// target's silent render. Only an engine-recorded key under this execution's own project prefix is used.
/// When nothing newer than the loop target exists, the loop target's own output is what comes back.
/// </summary>
public static class ReviewFilmResolver
{
    private static readonly string[] PlayableExtensions = [".mp4", ".mov", ".m4v", ".webm", ".mkv"];

    public static VisualCheckSource? Resolve(StepExecutionContext context)
    {
        int reviewOrder = context.Step.StepOrder;
        int windowStart = context.Step.LoopTargetStepOrder ?? int.MinValue;
        string prefix = $"projects/{context.Execution.ProjectId:D}/";

        StepOutputHistoryEntry? best = null;
        // Last wins among equal orders: after a loop-back the history holds the stale and the fresh entry.
        for (int i = context.StepOutputHistory.Count - 1; i >= 0; i--)
        {
            StepOutputHistoryEntry entry = context.StepOutputHistory[i];
            if (entry.StepOrder < windowStart || entry.StepOrder >= reviewOrder || !IsPlayable(entry.OutputStorageKey, prefix))
                continue;
            if (best is null || entry.StepOrder > best.StepOrder)
                best = entry;
        }

        return best is null ? null : new VisualCheckSource(best.StepOrder, best.OutputStorageKey!);
    }

    private static bool IsPlayable(string? key, string prefix) =>
        !string.IsNullOrWhiteSpace(key)
        && key.StartsWith(prefix, StringComparison.Ordinal)
        && !key.Contains("..", StringComparison.Ordinal)
        && !key.Contains('\\')
        && PlayableExtensions.Any(ext => key.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
}
