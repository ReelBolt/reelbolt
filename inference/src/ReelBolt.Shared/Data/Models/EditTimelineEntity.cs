namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// The persisted document row for a project's v2 edit timeline (table <c>edit_timelines</c>,
/// Inference-API-owned).
///
/// <para>
/// Deliberately named <c>EditTimelineEntity</c> rather than <c>EditTimeline</c>: the document type
/// itself lives in <see cref="ReelBolt.Shared.Editing.EditTimeline"/> and is what
/// <see cref="DocumentJson"/> serializes. The row carries the document plus the relational columns
/// (project, revision, seed step result) the API queries on without deserializing the document.
/// </para>
/// </summary>
public class EditTimelineEntity
{
    public Guid Id { get; set; }

    /// <summary>The owning project. Cascade-deleted with the project.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>User-facing timeline name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The serialized <see cref="ReelBolt.Shared.Editing.EditTimeline"/> document (jsonb).
    /// </summary>
    public string DocumentJson { get; set; } = "{}";

    /// <summary>Monotonic document revision, starting at 1.</summary>
    public int Revision { get; set; } = 1;

    /// <summary>The workflow step result this timeline was seeded from, if any.</summary>
    public Guid? SeedStepResultId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete marker; non-null rows are hidden by the query filter.</summary>
    public DateTime? DeletedAt { get; set; }
}
