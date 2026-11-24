namespace ReelBolt.Shared.Data.Models;

/// <summary>Where one zip unpack is in its life. Stored by name.</summary>
public enum ArchiveUnpackRunStatus
{
    Pending,
    Running,
    Done,
    Failed,
}

/// <summary>
/// One request to unpack an uploaded zip into a project's files. A background worker claims Pending rows,
/// streams the zip to the import worker, writes the validated files and (on full success) removes the zip.
///
/// <para>
/// Owned by the Inference API (migration <c>AddProjectArchiveUnpacks</c>). No tenant query filter: a project child
/// reached only through a project-authorized route or the background worker under a system scope
/// (<c>TenantBoundaryInventoryTests</c>); <see cref="OrganizationId"/> is denormalized, never a guard. There is no
/// by-id route. <see cref="SourceFileId"/> becomes null when the zip row is removed.
/// </para>
/// </summary>
public class ProjectArchiveUnpack
{
    public const int MaxSkippedSamples = 20;

    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>The zip row. Set to null by the database when that row is deleted.</summary>
    public Guid? SourceFileId { get; set; }

    public string ArchiveName { get; set; } = string.Empty;
    public long ArchiveSizeBytes { get; set; }

    /// <summary>The project directory the files were (or will be) written under; empty until the worker picks it.</summary>
    public string TargetDirectory { get; set; } = string.Empty;

    public ArchiveUnpackRunStatus Status { get; set; } = ArchiveUnpackRunStatus.Pending;

    /// <summary>A fixed code (ARCHIVE_TOO_LARGE, NOT_A_ZIP, STORAGE_QUOTA_EXCEEDED, ...) when <see cref="ArchiveUnpackRunStatus.Failed"/>.</summary>
    public string? FailureCode { get; set; }

    public int FilesCreated { get; set; }
    public long BytesCreated { get; set; }

    /// <summary>JSON: <c>{"counts":{"REASON":n},"samples":[{"path":"..","reason":".."}]}</c>, at most <see cref="MaxSkippedSamples"/> samples.</summary>
    public string? SkippedJson { get; set; }

    public Guid RequestedByUserId { get; set; }
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}
