using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.History;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>
/// A manifest diff between two versions, or between a version and the live project (contract section 7).
/// Items are matched by <c>(kind, key)</c>; every entry names the change and, when the display name moved,
/// the name it had before.
/// </summary>
public sealed record ProjectHistoryItemChange(
    ProjectVersionItemKind Kind,
    string Key,
    string? Name,
    string? PreviousName,
    string Change);

/// <summary>The <c>change</c> values of contract section 7.</summary>
public static class ProjectHistoryChangeKinds
{
    public const string Added = "added";
    public const string Removed = "removed";
    public const string Changed = "changed";
    public const string Moved = "moved";
    public const string ChangedAndMoved = "changedAndMoved";
}

/// <summary>The changes body: the capped list, how many matched items did not change, and the cap flag.</summary>
public sealed record ProjectHistoryChanges(
    IReadOnlyList<ProjectHistoryItemChange> Changes,
    int UnchangedCount,
    bool Truncated);

/// <summary>One workflow step as a step-list diff identifies it (order + type + agent name).</summary>
public sealed record ProjectHistoryWorkflowStep(int Order, string? Type, string? AgentName);

/// <summary>A step present on both sides whose definition differs; <c>ChangedFields</c> are the differing document fields.</summary>
public sealed record ProjectHistoryWorkflowStepChange(
    int Order,
    string? Type,
    string? AgentName,
    IReadOnlyList<string> ChangedFields);

/// <summary>The workflow step-list diff: added and removed steps, and changed steps with their changed fields.</summary>
public sealed record ProjectHistoryWorkflowDiff(
    IReadOnlyList<ProjectHistoryWorkflowStep> Added,
    IReadOnlyList<ProjectHistoryWorkflowStep> Removed,
    IReadOnlyList<ProjectHistoryWorkflowStepChange> Changed);

/// <summary>A timeline summary: tracks, clips and the record length in frames.</summary>
public sealed record ProjectHistoryTimelineSummary(int Tracks, int Clips, long DurationFrames);

/// <summary>A timeline diff: revisions either side and their summary.</summary>
public sealed record ProjectHistoryTimelineDiff(
    int FromRevision,
    int ToRevision,
    ProjectHistoryTimelineSummary FromSummary,
    ProjectHistoryTimelineSummary ToSummary);

/// <summary>Pronunciation terms added, removed and changed (same term, different spelling/casing).</summary>
public sealed record ProjectHistoryPronunciationDiff(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Changed);

/// <summary>One project field that changed; absent fields are null on the diff.</summary>
public sealed record ProjectHistoryFieldChange(string? From, string? To);

/// <summary>The project item diff: only the fields that changed.</summary>
public sealed record ProjectHistoryProjectDiff(
    ProjectHistoryFieldChange? Name,
    ProjectHistoryFieldChange? Description);

/// <summary>The <c>mode</c> values of a contract section 7 item diff.</summary>
public static class ProjectHistoryDiffModes
{
    public const string Text = "text";
    public const string Binary = "binary";
    public const string TooLarge = "tooLarge";

    /// <summary>The bytes are not served (CSAM output hold or quarantine marker); no content is included.</summary>
    public const string Unavailable = "unavailable";
    public const string Workflow = "workflow";
    public const string Timeline = "timeline";
    public const string DesignSystem = "designSystem";
    public const string Pronunciation = "pronunciation";
    public const string Project = "project";
}

/// <summary>
/// One item diff. Only the members named by <see cref="Mode"/> are populated: hunks for a text file,
/// sizes for a binary/too-large file, and the kind-specific body for the document kinds.
/// </summary>
public sealed record ProjectHistoryItemDiff(
    ProjectVersionItemKind Kind,
    string Key,
    string? Name,
    string Mode,
    IReadOnlyList<LineDiffHunk>? Hunks = null,
    long? FromSize = null,
    long? ToSize = null,
    ProjectHistoryWorkflowDiff? Workflow = null,
    ProjectHistoryTimelineDiff? Timeline = null,
    JsonObject? DesignSystem = null,
    ProjectHistoryPronunciationDiff? Pronunciation = null,
    ProjectHistoryProjectDiff? Project = null);

/// <summary>A file item's bytes at a version, with the metadata the safe-download headers need.</summary>
public sealed record ProjectHistoryItemContent(
    Stream Content,
    string? MimeType,
    string? FileName,
    long SizeBytes);
