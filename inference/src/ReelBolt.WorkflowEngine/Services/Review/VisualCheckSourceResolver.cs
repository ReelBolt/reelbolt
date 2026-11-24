using ReelBolt.WorkflowEngine.Execution;

namespace ReelBolt.WorkflowEngine.Services.Review;

/// <summary>
/// Finds the output a ReviewLoop's visual check samples: the most recent playable output in the loop's
/// window — the history entries from the loop target's step order up to (not including) the review step.
/// For a Remotion template that is the Author/Agent step's render; for a video-editing template it is the
/// VideoCompile step's compile. Both reach the history as the step result's <c>OutputStorageKey</c>.
///
/// <para>
/// Only an engine-recorded key is ever used — never a path from any step's output text — and only one
/// that is a video file under this execution's own project prefix with no traversal segment, so the
/// check can never be pointed at another project's object or a URL.
/// </para>
/// </summary>
public static class VisualCheckSourceResolver
{
    private static readonly string[] PlayableExtensions = [".mp4", ".mov", ".m4v", ".webm", ".mkv"];

    public static VisualCheckSource? Resolve(StepExecutionContext context) => ReviewFilmResolver.Resolve(context);
}
