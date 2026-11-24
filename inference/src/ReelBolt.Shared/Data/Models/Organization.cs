using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// A tenant: a Personal workspace, a Team, or the Platform itself (see docs/design/tenancy.md).
/// Owned by the Inference API schema (<c>organizations</c>); the Go API and the engine map it too.
/// </summary>
public class Organization
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Globally unique, URL-safe identifier.</summary>
    public string Slug { get; set; } = string.Empty;

    public OrganizationKind Kind { get; set; }

    /// <summary>
    /// The creating user. Null for the Platform and Default orgs. For a Personal org this is the
    /// workspace's only member, and a partial unique index allows one Personal org per creator.
    /// </summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>Where this org prefers its media jobs to run (R4). Stored as a string.</summary>
    public ComputePreference ComputePreference { get; set; } = ComputePreference.Auto;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }

    public ICollection<OrganizationMembership> Memberships { get; set; } = new List<OrganizationMembership>();
}
