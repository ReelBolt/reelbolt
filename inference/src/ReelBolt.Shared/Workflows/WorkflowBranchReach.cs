using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// Which steps of a workflow run on every path through it, read from the step definitions alone with
/// the engine's own control flow: a <see cref="StepType.Conditional"/> step continues at its true or its
/// false branch's step order (the next step when a target is missing or names no step; the end of the run for the
/// token <c>"end"</c>), a
/// <see cref="StepType.ReviewLoop"/> step at the next step or back at its loop target, and every other
/// step at the next one. A step a branch can skip is not on every path. The pre-run estimate uses it for
/// the bottom of the displayed range (the profile template's LLM fallback runs only on the
/// branch where the files miss a part, so its cheapest run is the free path).
/// </summary>
public static class WorkflowBranchReach
{
    /// <summary>
    /// One flag per entry of <paramref name="steps"/> (in the caller's order): true when the step runs on
    /// every path from the first step (by step order) to the end of the run. A workflow with no
    /// Conditional step has no branch, so every flag is true.
    /// </summary>
    public static bool[] OnEveryPath(IReadOnlyList<WorkflowStep> steps)
    {
        bool[] always = new bool[steps.Count];
        Array.Fill(always, true);
        if (steps.Count == 0 || !steps.Any(s => s.StepType == StepType.Conditional))
        {
            return always;
        }

        // Run order, as the engine walks it (ordered by step order; FindIndex picks the first match).
        int[] runOrder = [.. Enumerable.Range(0, steps.Count).OrderBy(i => steps[i].StepOrder)];
        List<WorkflowStep> ordered = [.. runOrder.Select(i => steps[i])];
        int end = ordered.Count;
        int[][] next = [.. ordered.Select((step, i) => Successors(ordered, step, i))];

        for (int skipped = 1; skipped < ordered.Count; skipped++)
        {
            always[runOrder[skipped]] = !ReachesEnd(next, end, skipped);
        }

        return always;
    }

    private static int[] Successors(List<WorkflowStep> ordered, WorkflowStep step, int index)
    {
        int fallThrough = index + 1;
        return step.StepType switch
        {
            StepType.Conditional => [Target(ordered, step.TrueBranchStepOrder, fallThrough), Target(ordered, step.FalseBranchStepOrder, fallThrough)],
            StepType.ReviewLoop when step.LoopTargetStepOrder is { } loop
                && ordered.FindIndex(s => s.StepOrder == loop) is var target and >= 0 => [fallThrough, target],
            _ => [fallThrough]
        };
    }

    private static int Target(List<WorkflowStep> ordered, string? branch, int fallThrough)
    {
        // "end" finishes the run there (docs/design/app-inputs.md 9.4): the end of the run, not the next step.
        if (string.Equals(branch?.Trim(), Apps.StaticBranchResolver.EndToken, StringComparison.OrdinalIgnoreCase))
        {
            return ordered.Count;
        }

        if (!int.TryParse(branch, out int order))
        {
            return fallThrough;
        }

        int index = ordered.FindIndex(s => s.StepOrder == order);
        return index < 0 ? fallThrough : index;
    }

    /// <summary>Whether the end of the run is reachable from the first step without visiting <paramref name="avoided"/>.</summary>
    private static bool ReachesEnd(int[][] next, int end, int avoided)
    {
        bool[] seen = new bool[end + 1];
        Stack<int> pending = new();
        pending.Push(0);
        seen[0] = true;
        while (pending.Count > 0)
        {
            int at = pending.Pop();
            if (at == end)
            {
                return true;
            }

            foreach (int to in next[at])
            {
                int clamped = Math.Clamp(to, 0, end);
                if (clamped != avoided && !seen[clamped])
                {
                    seen[clamped] = true;
                    pending.Push(clamped);
                }
            }
        }

        return false;
    }
}
