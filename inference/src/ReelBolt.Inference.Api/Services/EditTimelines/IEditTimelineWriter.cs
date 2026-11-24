using ReelBolt.Shared.Editing;

namespace ReelBolt.Inference.Api.Services.EditTimelines;

/// <summary>What a server-side edit decided: the new document and the op-log entry (<c>setProperty</c> ops in the client's JSON shape).</summary>
public sealed record ServerEditMutation(EditTimeline Document, string OpsJson);

public enum ServerEditOutcome
{
    /// <summary>Written: <see cref="ServerEditResult.Revision"/> is the new revision.</summary>
    Applied,

    /// <summary>The mutation had nothing to change; no revision was written. <see cref="ServerEditResult.Revision"/> is the current one.</summary>
    NoChange,

    /// <summary>The timeline is not in this project (or is deleted).</summary>
    NotFound,

    /// <summary>The document or ops are over the size limits.</summary>
    TooLarge,

    /// <summary>The edited document breaks the validator; <see cref="ServerEditResult.Violations"/> says where.</summary>
    Invalid,

    /// <summary>A concurrent writer won twice (the edit was retried once from a fresh read).</summary>
    Conflict
}

public sealed record ServerEditResult(
    ServerEditOutcome Outcome,
    int Revision = 0,
    IReadOnlyList<(string Path, string Message)>? Violations = null,
    string? Message = null)
{
    public bool Succeeded => Outcome is ServerEditOutcome.Applied or ServerEditOutcome.NoChange;
}

/// <summary>Writes an edit to a timeline without a browser: the same checks and the same commit as <c>PATCH edit-timelines/{id}</c>.</summary>
public interface IEditTimelineWriter
{
    /// <summary>
    /// Reads the timeline (pinned to <paramref name="projectId"/>), lets <paramref name="mutate"/> compute the new document and its
    /// ops (return null for "nothing to do"), validates it and commits one new revision. A concurrent edit is retried once from a
    /// fresh read; <paramref name="mutate"/> therefore runs once or twice and must be a pure function of the document it is given.
    /// </summary>
    Task<ServerEditResult> ApplyServerEditAsync(
        Guid projectId, Guid timelineId, Func<EditTimeline, ServerEditMutation?> mutate, CancellationToken ct);
}
