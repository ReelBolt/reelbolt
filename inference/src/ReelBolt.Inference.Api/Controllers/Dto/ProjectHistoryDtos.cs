using System.Text.Json.Nodes;

namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// The <c>GET /history/versions</c> body (contract section 5). Deliberately carries no storage key,
/// ETag, content identity, digest or bucket: those never leave the API. <see cref="CanEdit"/> and
/// <see cref="CanDelete"/> say what the caller may do with this project, from the same access rule
/// the write routes enforce.
/// </summary>
public sealed record ProjectHistoryVersionsResponse(
    IReadOnlyList<ProjectHistoryVersionResponse> Versions,
    long HistoryBytes,
    int RetentionDays,
    bool CanEdit,
    bool CanDelete);

/// <summary>One version as the list shows it.</summary>
public sealed record ProjectHistoryVersionResponse(
    Guid Id,
    int Number,
    string? Name,
    string Cause,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    string? CreatedByName,
    int ItemCount,
    long TotalBytes);

/// <summary>
/// A version with its items. <see cref="ProjectHistoryItemResponse.Restorable"/> and
/// <see cref="ProjectHistoryItemResponse.NotRestorableReason"/> are computed by the history service.
/// </summary>
public sealed record ProjectHistoryVersionDetailResponse(
    Guid Id,
    int Number,
    string? Name,
    string Cause,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    string? CreatedByName,
    int ItemCount,
    long TotalBytes,
    IReadOnlyList<ProjectHistoryItemResponse> Items);

/// <summary>One item of a version; the reason is one of the contract section 5 codes when not restorable.</summary>
public sealed record ProjectHistoryItemResponse(
    string Kind,
    string Key,
    string? Name,
    string? Category,
    string? MimeType,
    long SizeBytes,
    bool Restorable,
    string? NotRestorableReason);

/// <summary>The <c>POST /history/versions</c> body: the name the caller saves the version under.</summary>
public sealed record CreateProjectHistoryVersionRequest(string? Name);

/// <summary>The <c>PUT /history/versions/{id}/name</c> body; a null/blank name un-names the version.</summary>
public sealed record RenameProjectHistoryVersionRequest(string? Name);

/// <summary>The <c>GET versions/{id}/changes</c> body (contract section 7).</summary>
public sealed record ProjectHistoryChangesResponse(
    IReadOnlyList<ProjectHistoryItemChangeResponse> Changes,
    int UnchangedCount,
    bool Truncated);

/// <summary>One item that changed between two manifests.</summary>
public sealed record ProjectHistoryItemChangeResponse(
    string Kind,
    string Key,
    string? Name,
    string? PreviousName,
    string Change);

/// <summary>
/// One item diff. Only the members named by <see cref="Mode"/> are populated: <see cref="Hunks"/> for a
/// text file, <see cref="FromSize"/>/<see cref="ToSize"/> for a binary or too-large file, and the
/// kind-specific body for a document item.
/// </summary>
public sealed record ProjectHistoryItemDiffResponse(
    string Kind,
    string Key,
    string? Name,
    string Mode,
    IReadOnlyList<ProjectHistoryHunkResponse>? Hunks,
    long? FromSize,
    long? ToSize,
    ProjectHistoryWorkflowDiffResponse? Workflow,
    ProjectHistoryTimelineDiffResponse? Timeline,
    JsonNode? DesignSystem,
    ProjectHistoryPronunciationDiffResponse? Pronunciation,
    ProjectHistoryProjectDiffResponse? Project);

/// <summary>One unified-diff hunk with 3 context lines.</summary>
public sealed record ProjectHistoryHunkResponse(
    int FromStart,
    int FromCount,
    int ToStart,
    int ToCount,
    IReadOnlyList<ProjectHistoryDiffLineResponse> Lines);

/// <summary>One hunk line: <c>" "</c>, <c>"+"</c> or <c>"-"</c>.</summary>
public sealed record ProjectHistoryDiffLineResponse(string Op, string Text);

/// <summary>The workflow step-list diff.</summary>
public sealed record ProjectHistoryWorkflowDiffResponse(
    IReadOnlyList<ProjectHistoryWorkflowStepResponse> Added,
    IReadOnlyList<ProjectHistoryWorkflowStepResponse> Removed,
    IReadOnlyList<ProjectHistoryWorkflowStepChangeResponse> Changed);

/// <summary>A step identified by order, type and agent name.</summary>
public sealed record ProjectHistoryWorkflowStepResponse(int Order, string? Type, string? AgentName);

/// <summary>A step present on both sides with its differing document fields.</summary>
public sealed record ProjectHistoryWorkflowStepChangeResponse(
    int Order,
    string? Type,
    string? AgentName,
    IReadOnlyList<string> ChangedFields);

/// <summary>The timeline revision and summary diff.</summary>
public sealed record ProjectHistoryTimelineDiffResponse(
    int FromRevision,
    int ToRevision,
    ProjectHistoryTimelineSummaryResponse FromSummary,
    ProjectHistoryTimelineSummaryResponse ToSummary);

/// <summary>Tracks, clips and the record length in frames.</summary>
public sealed record ProjectHistoryTimelineSummaryResponse(int Tracks, int Clips, long DurationFrames);

/// <summary>Pronunciation terms added, removed and changed.</summary>
public sealed record ProjectHistoryPronunciationDiffResponse(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Changed);

/// <summary>The project item diff: only the fields that changed.</summary>
public sealed record ProjectHistoryProjectDiffResponse(
    ProjectHistoryFieldChangeResponse? Name,
    ProjectHistoryFieldChangeResponse? Description);

/// <summary>One field's before/after value.</summary>
public sealed record ProjectHistoryFieldChangeResponse(string? From, string? To);


/// <summary>One item to restore (scope item).</summary>
public sealed record ProjectHistoryRestoreItemRequest(string? Kind, string? Key);

/// <summary>The restore and restore-preview body: <c>scope</c> is "item" or "project".</summary>
public sealed record ProjectHistoryRestoreRequest(
    string? Scope,
    IReadOnlyList<ProjectHistoryRestoreItemRequest>? Items);

/// <summary>An item a restore cannot apply, with its reason code.</summary>
public sealed record ProjectHistoryRestoreBlockedResponse(string Kind, string Key, string? Name, string Reason);

/// <summary>The restore preview: what would change, what is blocked, bytes to copy.</summary>
public sealed record ProjectHistoryRestorePreviewResponse(
    string Scope,
    IReadOnlyList<ProjectHistoryItemChangeResponse> Changes,
    IReadOnlyList<ProjectHistoryRestoreBlockedResponse> Blocked,
    long EstimateBytes);

/// <summary>An item a restore applied or removed.</summary>
public sealed record ProjectHistoryRestoreEntryResponse(string Kind, string Key, string? Name);

/// <summary>An item a restore left alone.</summary>
public sealed record ProjectHistoryRestoreSkippedResponse(string Kind, string Key, string? Name, string Reason);

/// <summary>A notice attached to an applied item.</summary>
public sealed record ProjectHistoryRestoreNoticeResponse(string Kind, string Key, string Code);

/// <summary>The restore result; partial success is normal.</summary>
public sealed record ProjectHistoryRestoreResponse(
    Guid SafetyVersionId,
    IReadOnlyList<ProjectHistoryRestoreEntryResponse> Restored,
    IReadOnlyList<ProjectHistoryRestoreEntryResponse> Removed,
    IReadOnlyList<ProjectHistoryRestoreSkippedResponse> Skipped,
    IReadOnlyList<ProjectHistoryRestoreNoticeResponse> Notices);
