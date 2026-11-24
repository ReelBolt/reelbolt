using ReelBolt.Shared.DesignSystems;

namespace ReelBolt.Inference.Api.Services.DesignSystems;

/// <summary>
/// The one design-system document container a call operates on, with the tenancy decision already
/// made by the caller (SPEC 16.1, P4-04). A project scope names the project's resolved
/// <c>design_systems</c> row (or <see cref="Guid.Empty"/> before one exists); a library scope names
/// the organization library's container row — a <c>design_systems</c> row with <c>project_id NULL</c>
/// and <c>kind = 'design'</c> — which the caller resolved from the filtered
/// <c>design_system_libraries</c> set, never from a caller-supplied <c>design_systems</c> id.
/// <para>
/// The type carries no document and no behaviour: it is the identity a shared
/// <see cref="DesignSystemService"/> core works against, so the project path and the org library path
/// cannot drift apart on validation, digesting, version numbering or the approve/reject/restore state
/// machine.
/// </para>
/// </summary>
public sealed record DesignSystemScope
{
    private DesignSystemScope(Guid designSystemId, string kind, bool isLibrary, Guid organizationId, Guid projectId)
    {
        DesignSystemId = designSystemId;
        Kind = kind;
        IsLibrary = isLibrary;
        OrganizationId = organizationId;
        ProjectId = projectId;
    }

    /// <summary>
    /// The resolved <c>design_systems</c> row id, or <see cref="Guid.Empty"/> when the caller is about
    /// to create the row.
    /// </summary>
    public Guid DesignSystemId { get; }

    /// <summary>
    /// The document kind: always <see cref="DesignSystemKinds.Design"/> for a library;
    /// <see cref="DesignSystemKinds.Design"/> or <see cref="DesignSystemKinds.ProductMap"/> for a project.
    /// </summary>
    public string Kind { get; }

    /// <summary>True when the container is an organization library, false for a project's system.</summary>
    public bool IsLibrary { get; }

    /// <summary>
    /// The owning organization for a library scope; <see cref="Guid.Empty"/> for a project scope. Used
    /// only so the library resolver can prove org membership before the service is called.
    /// </summary>
    public Guid OrganizationId { get; }

    /// <summary>
    /// The owning project for a project scope; <see cref="Guid.Empty"/> for a library scope. The service
    /// only resolves a row that belongs to this project.
    /// </summary>
    public Guid ProjectId { get; }

    /// <summary>A project's document container for <paramref name="kind"/>.</summary>
    public static DesignSystemScope Project(Guid designSystemId, string kind, Guid projectId) =>
        new(designSystemId, kind, isLibrary: false, Guid.Empty, projectId);

    /// <summary>An organization library's container, whose kind is always <see cref="DesignSystemKinds.Design"/>.</summary>
    public static DesignSystemScope Library(Guid designSystemId, Guid organizationId) =>
        new(designSystemId, DesignSystemKinds.Design, isLibrary: true, organizationId, Guid.Empty);
}
