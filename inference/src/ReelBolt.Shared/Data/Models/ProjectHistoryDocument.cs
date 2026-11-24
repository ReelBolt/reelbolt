namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One canonical JSON body a version item points at, deduplicated by its SHA-256 inside the project
/// (table <c>project_history_documents</c>). The body is a workflow, timeline, pronunciation or
/// project document; file bytes live in storage.
/// </summary>
public class ProjectHistoryDocument
{
    /// <summary>The owning project; part of the primary key. Cascade-deleted with the project.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>Lowercase hex SHA-256 of <see cref="Json"/>; part of the primary key.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public ProjectVersionItemKind Kind { get; set; }

    /// <summary>The canonical JSON text. Never re-serialized before hashing.</summary>
    public string Json { get; set; } = string.Empty;

    /// <summary>The UTF-8 byte length of <see cref="Json"/>.</summary>
    public long SizeBytes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
