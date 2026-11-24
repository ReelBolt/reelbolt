using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// T-206: how a <c>VideoCompile</c> treats a graphics plan written by a <c>PinInsert</c> step. That plan is built by
/// first-party C# from references the server resolved itself (a project file id, or the stored output of an earlier
/// step or step result of THIS project), so its insert keys are not re-anchored to this run's render prefix the way
/// a model-written plan's are — they may legitimately name an earlier run's output. They are still checked: inside
/// this project, no traversal, and a video or image by extension.
/// </summary>
internal static class PinnedPlanTrust
{
    /// <summary>True when <paramref name="planRef"/> resolves to a <c>PinInsert</c> step of this workflow.</summary>
    public static bool IsPinInsertPlan(StepExecutionContext context, ExtractInputRef? planRef)
    {
        if (planRef is null)
            return false;

        int? order = planRef.From switch
        {
            // The same entry ResolveDecisionJson reads for Previous: the latest non-empty output.
            ExtractInputSource.Previous => context.StepOutputHistory.LastOrDefault(h => !string.IsNullOrWhiteSpace(h.Output))?.StepOrder,
            ExtractInputSource.Step => planRef.StepOrder,
            _ => null
        };

        return order is { } o && context.AllSteps.Any(s => s.StepOrder == o && s.StepType == StepType.PinInsert);
    }

    /// <summary>The key itself when a pinned insert may use it, else null (the caller drops it as invalid).</summary>
    public static string? AcceptPinnedKey(string? key, Guid projectId)
    {
        if (string.IsNullOrWhiteSpace(key)
            || !key.StartsWith($"projects/{projectId}/", StringComparison.Ordinal)
            || key.Contains("..", StringComparison.Ordinal)
            || key.Contains('\\'))
        {
            return null;
        }

        string mime = InsertContentRefResolver.MimeTypeFor(Path.GetFileName(key));
        return mime.StartsWith("video/", StringComparison.Ordinal) || mime.StartsWith("image/", StringComparison.Ordinal)
            ? key
            : null;
    }
}
