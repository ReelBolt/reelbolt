namespace ReelBolt.Shared.Data.Models;

/// <summary>What a <see cref="ProjectVersionItem"/> holds. Persisted by name.</summary>
public enum ProjectVersionItemKind
{
    /// <summary>A user or agent file (never an output, never a quarantined file).</summary>
    File,

    /// <summary>A Standard workflow definition, as a canonical JSON document.</summary>
    Workflow,

    /// <summary>An edit timeline, as a canonical JSON document.</summary>
    Timeline,

    /// <summary>The project's design system pointer (the design-system version row is the body).</summary>
    DesignSystem,

    /// <summary>The whole pronunciation glossary, as a canonical JSON document.</summary>
    Pronunciation,

    /// <summary>The project's name and description, as a canonical JSON document.</summary>
    Project
}

/// <summary>
/// One item of a <see cref="ProjectVersion"/> (table <c>project_version_items</c>). The row carries
/// everything needed to reach or rebuild the item; JSON bodies live deduplicated in
/// <see cref="ProjectHistoryDocument"/>, and file bytes are protected by the storage decorator.
/// </summary>
public class ProjectVersionItem
{
    public Guid Id { get; set; }

    /// <summary>The owning version. Cascade-deleted with it.</summary>
    public Guid VersionId { get; set; }

    /// <summary>Denormalised project id, for project-wide queries (blob lookup, GC, erase).</summary>
    public Guid ProjectId { get; set; }

    public ProjectVersionItemKind Kind { get; set; }

    /// <summary>File id (D format), workflow id, timeline id, or a fixed key for the singletons.</summary>
    public string ItemKey { get; set; } = string.Empty;

    /// <summary>Display name: a file path, a workflow/timeline name, the project name.</summary>
    public string? Name { get; set; }

    /// <summary>The file category (<c>userFiles</c>/<c>agentFiles</c>); null for other kinds.</summary>
    public string? Category { get; set; }

    public string? MimeType { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>
    /// The content identity at capture: <c>sha256:&lt;hex&gt;</c>, <c>etag:&lt;hex&gt;</c> or
    /// <c>unverified:&lt;fileId&gt;</c>. Null for kinds that have no bytes.
    /// </summary>
    public string? ContentId { get; set; }

    public string? StorageKey { get; set; }

    /// <summary>The store's ETag the captured bytes had; null when it could not be read.</summary>
    public string? StorageETag { get; set; }

    /// <summary>
    /// For a file item: the live row's <see cref="ProjectFile.OutputProducedAt"/> at capture (null for
    /// an upload). Kept so the output hold can be judged after the live row is gone. Not part of the
    /// manifest digest.
    /// </summary>
    public DateTime? OutputProducedAt { get; set; }

    /// <summary>
    /// For a file item: the platform made the bytes (a render, a generated clip or a picture export)
    /// as judged at capture by <c>ProjectOutputHold.IsPlatformMade</c>. Such items are what the
    /// project's output hold protects from deletion and retention GC. Not part of the manifest digest.
    /// </summary>
    public bool PlatformMade { get; set; }

    /// <summary>SHA-256 of the canonical JSON body, for document kinds.</summary>
    public string? DocumentSha256 { get; set; }

    /// <summary>The timeline revision the document was taken at.</summary>
    public int? TimelineRevision { get; set; }

    /// <summary>The pinned design-system version id (no FK: the row is the body).</summary>
    public Guid? DesignSystemVersionId { get; set; }

    /// <summary>
    /// Set by "Delete forever" and by a quarantine move: an erased item is never restorable and
    /// its blob is deleted. Null for a live item.
    /// </summary>
    public DateTime? ErasedAt { get; set; }
}
