namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One protected copy of a project file's bytes, keyed by content identity (table
/// <c>project_history_blobs</c>). Written by the copy-on-change storage decorator when a live file
/// is overwritten, moved or deleted; counted against the organization's storage quota.
/// </summary>
public class ProjectHistoryBlob
{
    /// <summary>The owning project; part of the primary key. Cascade-deleted with the project.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>
    /// The item's content identity. Part of the primary key, so one blob serves every version that
    /// captured the same bytes.
    /// </summary>
    public string ContentId { get; set; } = string.Empty;

    /// <summary>
    /// <c>projects/{projectId}/history/blobs/{hex}</c> where <c>hex</c> is the SHA-256 hex of
    /// <see cref="ContentId"/>.
    /// </summary>
    public string StorageKey { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
