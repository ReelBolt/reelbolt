namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// A media bin: a user-created folder inside one project's media pool (table <c>media_bins</c>,
/// Inference-API-owned). Bins form a tree via <see cref="ParentBinId"/> and hold media through
/// <see cref="MediaBinItem"/> rows; they organise the pool for the editor's media panel and carry
/// no media of their own.
/// </summary>
public class MediaBin
{
    public Guid Id { get; set; }

    /// <summary>The owning project. Cascade-deleted with the project.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>User-facing bin name. Required and trimmed by the API (1..120 characters).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The containing bin, or null for a root bin. A self-reference, mapped with
    /// <c>DeleteBehavior.Restrict</c> on purpose: a database-level self-cascade would delete an
    /// entire subtree from one row delete, silently and without a bound, and bins are soft-deleted
    /// anyway (<see cref="DeletedAt"/>) so the subtree walk stays explicit in the controller where
    /// it can be capped and cycle-checked.
    /// </summary>
    public Guid? ParentBinId { get; set; }

    /// <summary>
    /// Display order among siblings, ascending. Server-assigned on create (appended to the end of
    /// the parent's children) and rewritten by an explicit move; never derived from the name, so
    /// renames never reorder the panel.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// P6: when set, this is a SMART bin — its items are the pool sources that match the saved
    /// <c>MediaSmartFilter</c> (JSON), computed at read time; it holds no pinned rows and refuses pins.
    /// Null for an ordinary bin.
    /// </summary>
    public string? SmartFilterJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete marker; non-null rows are hidden by the query filter.</summary>
    public DateTime? DeletedAt { get; set; }
}
