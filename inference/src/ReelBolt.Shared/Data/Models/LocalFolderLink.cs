namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One folder on a person's computer, mirrored into a project by the ReelBolt desktop app
/// (<c>project_local_folder_links</c>; docs/design/local-folder-sync-and-storage.md, Part 1).
///
/// <para>
/// One-way, computer to project: files in the folder are created in the project under
/// <see cref="TargetFolder"/>, rewritten in place when their content changes, and deleted when they
/// disappear from the folder. A file a person uploaded, an agent wrote or another source synced is never
/// touched, and nothing is ever written to the computer. Files a link wrote carry
/// <c>project_files.source_kind = 'local_folder'</c>, <c>source_link_id</c> = this link and
/// <c>source_revision</c> = the SHA-256 of their content.
/// </para>
///
/// <para>
/// A child of <see cref="Project"/>: no tenant query filter, reached through the project access check
/// (dashboard) or through the signed device's own organization (desktop app). A project may hold several
/// links, each in its own target folder, so two links can never collide.
/// </para>
/// </summary>
public class ProjectLocalFolderLink
{
    public const string PauseReasonGuard = "guard";
    public const string PauseReasonUser = "user";

    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>The project's (and the device's) organization at link time.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>The paired desktop computer (<c>runner_devices</c>) that syncs this folder.</summary>
    public Guid DeviceId { get; set; }

    /// <summary>
    /// Project-relative folder the link's files land in (e.g. <c>Footage</c>). Normalized with the
    /// <c>GitHubIncludePaths</c> rules; unique per project, and never inside or around another link's folder.
    /// </summary>
    public string TargetFolder { get; set; } = string.Empty;

    /// <summary>What the computer calls the folder (≤ 512 chars). Display only, never matched on.</summary>
    public string DisplayPath { get; set; } = string.Empty;

    /// <summary>The device's pairing user when the link was made. Audit only.</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>Set while the link is paused; uploads and deletes are then refused (<c>409 link_paused</c>).</summary>
    public DateTime? PausedAt { get; set; }

    /// <summary>
    /// Why the link is paused: <see cref="PauseReasonGuard"/> (the mass-delete guard; lifted by the desktop
    /// app confirming the deletions) or <see cref="PauseReasonUser"/> (a person paused it). Null when not paused.
    /// </summary>
    public string? PauseReason { get; set; }

    /// <summary>When the last complete manifest from the computer was reconciled.</summary>
    public DateTime? LastReconciledAt { get; set; }

    /// <summary>When the last file was written or deleted by this link.</summary>
    public DateTime? LastChangeAt { get; set; }

    /// <summary>How many project files this link currently holds.</summary>
    public int FileCount { get; set; }

    /// <summary>Plain-language reason the link needs attention (the mass-delete guard). Null otherwise.</summary>
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
