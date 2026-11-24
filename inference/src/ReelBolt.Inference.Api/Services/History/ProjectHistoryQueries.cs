using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>
/// The list-row projection of a version (contract section 5). Deliberately free of storage keys,
/// ETags, content identities and digests: none of those may leave the API.
/// </summary>
public sealed record ProjectHistoryVersionInfo(
    Guid Id,
    int Number,
    string? Name,
    string Cause,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    string? CreatedByName,
    int ItemCount,
    long TotalBytes);

/// <summary>The <c>GET versions</c> body: the page, the project's retained history bytes and the window.</summary>
public sealed record ProjectHistoryVersionsPage(
    IReadOnlyList<ProjectHistoryVersionInfo> Versions,
    long HistoryBytes,
    int RetentionDays);

/// <summary>One item of a version, with the layer-1 restorability verdict (contract section 5).</summary>
public sealed record ProjectHistoryItemInfo(
    ProjectVersionItemKind Kind,
    string Key,
    string? Name,
    string? Category,
    string? MimeType,
    long SizeBytes,
    bool Restorable,
    string? NotRestorableReason);

/// <summary>A version and its items.</summary>
public sealed record ProjectHistoryVersionDetail(
    ProjectHistoryVersionInfo Version,
    IReadOnlyList<ProjectHistoryItemInfo> Items);

/// <summary>The <c>notRestorableReason</c> codes of contract section 5.</summary>
public static class ProjectHistoryRestoreReasons
{
    /// <summary>No protected copy and the live file's ETag no longer matches the captured one.</summary>
    public const string ContentGone = "CONTENT_GONE";

    /// <summary>"Delete forever" or a quarantine move already erased the item.</summary>
    public const string Erased = "ERASED";

    /// <summary>The live file is quarantined.</summary>
    public const string Quarantined = "QUARANTINED";

    /// <summary>The version predates the project's quarantine marker.</summary>
    public const string QuarantineMarker = "QUARANTINE_MARKER";

    /// <summary>The live file is held by the project's CSAM output hold (or its content-quarantine marker).</summary>
    public const string OutputHeld = "OUTPUT_HELD";

    /// <summary>The pinned design-system version row no longer exists.</summary>
    public const string DesignVersionGone = "DESIGN_VERSION_GONE";

    /// <summary>The item could not be verified at capture, so its bytes cannot be promised.</summary>
    public const string Unverified = "UNVERIFIED";

    /// <summary>Its kind has no restore handler yet (contract section 8 part 1: workflows and timelines are T-010).</summary>
    public const string NotYetSupported = "NOT_YET_SUPPORTED";

    /// <summary>An avatar currently lists the live file as a reference, so a restore may not replace it.</summary>
    public const string FileInUseByAvatar = "FILE_IN_USE_BY_AVATAR";

    /// <summary>Recreating the file at its old path would collide with another live file.</summary>
    public const string PathTaken = "PATH_TAKEN";

    /// <summary>The plan's storage quota would refuse the item's net growth.</summary>
    public const string StorageQuotaExceeded = "STORAGE_QUOTA_EXCEEDED";

    /// <summary>The referenced item is not in the version (item scope).</summary>
    public const string ItemNotFound = "ITEM_NOT_FOUND";

    /// <summary>The pinned design-system version is already the system's current version.</summary>
    public const string AlreadyCurrent = "ALREADY_CURRENT";

    /// <summary>The item's handler threw while applying it; the rest of the restore continued.</summary>
    public const string RestoreFailed = "RESTORE_FAILED";

    /// <summary>The pinned design-system version exists but cannot be restored (not an approved state).</summary>
    public const string DesignVersionInvalid = "DESIGN_VERSION_INVALID";

    /// <summary>The stored workflow's steps no longer pass the save-time validation (agent, provider or config).</summary>
    public const string WorkflowInvalid = "WORKFLOW_INVALID";

    /// <summary>The workflow has a Queued/Running run, so it was not restored or removed.</summary>
    public const string WorkflowRunActive = "WORKFLOW_RUN_ACTIVE";

    /// <summary>The stored timeline document no longer passes <c>EditTimelineValidator</c>.</summary>
    public const string TimelineInvalid = "TIMELINE_INVALID";
}

/// <summary>The <c>notices[].code</c> values of contract section 8 step 7.</summary>
public static class ProjectHistoryRestoreNotices
{
    /// <summary>
    /// A restored step asked to turn <c>allowSourceMediaEgress</c> on where the live workflow did not
    /// already hold the same grant, so the flag was written false (the customer re-grants it in the app).
    /// </summary>
    public const string EgressConsentReset = "EGRESS_CONSENT_RESET";

    /// <summary>
    /// A captured pronunciation entry no longer passes the current validator (or the per-project cap),
    /// so the set was restored without it.
    /// </summary>
    public const string PronunciationEntrySkipped = "PRONUNCIATION_ENTRY_SKIPPED";
}
