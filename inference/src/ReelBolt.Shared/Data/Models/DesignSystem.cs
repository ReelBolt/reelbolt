using ReelBolt.Shared.DesignSystems;

namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// The root of one design system (SPEC 22). <see cref="Kind"/> is one of
/// <see cref="DesignSystemKinds.Design"/> or <see cref="DesignSystemKinds.ProductMap"/> and is a
/// plain string, never an enum, because the set is append-only and persisted verbatim.
///
/// <para>
/// <b>Phase 4 mapping.</b> <see cref="ParentLibraryId"/> is mapped by both DbContexts and
/// <c>design_system_libraries</c> is mapped by both as well: the Phase 4 migration (SPEC 16.1) adds
/// <c>design_system_libraries</c> and <c>design_systems.parent_library_id</c> with an FK to
/// <c>design_system_libraries(id)</c>. A library parents project rows only, enforced by the
/// <c>ck_design_systems_parent_library_project</c> CHECK.
/// </para>
/// </summary>
public class DesignSystem
{
    public Guid Id { get; set; }

    public Guid? ProjectId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public Guid? CurrentVersionId { get; set; }

    public string? WatchJson { get; set; }

    /// <summary>Phase 4: the library this project design system descends from (null on library rows).</summary>
    public Guid? ParentLibraryId { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One organization's design-system library (SPEC 16.1). The library's document container is the
/// <see cref="DesignSystem"/> named by <see cref="DesignSystemId"/>: a row with <c>project_id NULL</c>
/// and <c>kind = 'design'</c>, so a library is addressable by its own id rather than through a
/// project. Organization-owned with no platform rows, so it is one of the few tables carrying the
/// named <c>"Tenant"</c> query filter; project rows descend from it via
/// <see cref="DesignSystem.ParentLibraryId"/>.
/// </summary>
public class DesignSystemLibrary
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid DesignSystemId { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One inert asset an organization library owns: a sanitized SVG or one of the four font containers
/// (SPEC 16.1, T-010). It hangs off <see cref="DesignSystemLibrary"/> by <see cref="LibraryId"/> with a
/// cascade, so deleting the library deletes its rows and the object store is swept by the route.
/// It carries no organization column of its own and therefore no tenant filter: the boundary is the
/// filtered <c>design_system_libraries</c> parent every read resolves first.
/// </summary>
public class DesignSystemLibraryAsset
{
    public Guid Id { get; set; }

    /// <summary>FK to <c>design_system_libraries(id)</c>; cascades on delete.</summary>
    public Guid LibraryId { get; set; }

    /// <summary>The sanitized original base name (≤ 200), unique inside the library.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary><c>image/svg+xml</c> or one of <c>font/woff</c>, <c>font/woff2</c>, <c>font/ttf</c>, <c>font/otf</c>.</summary>
    public string MimeType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>The 64 lowercase hex characters of the stored (sanitized) bytes, computed server-side.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary><c>design-libraries/{orgId}/{libraryId}/assets/{assetId}/{name}</c> in the object store.</summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>Logo, icon, preview or font; optional.</summary>
    public string? Role { get; set; }

    public string? Variant { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One immutable version of a <see cref="DesignSystem"/>. The document is stored as jsonb and its
/// digest is a lowercase hex SHA-256 recomputed server-side, never trusted from a client.
/// </summary>
public class DesignSystemVersion
{
    public Guid Id { get; set; }

    public Guid DesignSystemId { get; set; }

    public int VersionNumber { get; set; }

    public DesignVersionState State { get; set; }

    public string DocumentJson { get; set; } = string.Empty;

    public string DocumentDigest { get; set; } = string.Empty;

    public DesignVersionOrigin Origin { get; set; }

    public Guid? BaseVersionId { get; set; }

    public Guid? WorkflowExecutionId { get; set; }

    public string? CoverageJson { get; set; }

    public Guid CreatedByUserId { get; set; }

    public Guid? ApprovedByUserId { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One source a <see cref="DesignSystemVersion"/> was read from, with the facts needed to tell
/// whether it is still current.
/// </summary>
public class DesignSystemSource
{
    public Guid Id { get; set; }

    public Guid DesignSystemId { get; set; }

    public Guid VersionId { get; set; }

    public DesignSourceKind SourceKind { get; set; }

    public Guid? ProjectFileId { get; set; }

    public string Path { get; set; } = string.Empty;

    public string Importer { get; set; } = string.Empty;

    public string ContentSha256 { get; set; } = string.Empty;

    public string? SourceRevision { get; set; }

    public long SizeBytes { get; set; }

    public DesignSourceStatus Status { get; set; }

    public DateTime LastCheckedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastChangedAt { get; set; }
}

/// <summary>
/// A proposed change to a <see cref="DesignSystem"/>, raised when a source drifts or on request.
/// </summary>
public class DesignSystemChangeProposal
{
    public Guid Id { get; set; }

    public Guid DesignSystemId { get; set; }

    public Guid BaseVersionId { get; set; }

    public Guid ProposedVersionId { get; set; }

    public DesignProposalTrigger Trigger { get; set; }

    public double DriftScore { get; set; }

    public string DiffJson { get; set; } = string.Empty;

    public DesignProposalStatus Status { get; set; }

    public string ProposedDigest { get; set; } = string.Empty;

    public Guid? DecidedByUserId { get; set; }

    public DateTime? DecidedAt { get; set; }

    public string? DecisionNote { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
