using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Shared.Data.Models;

/// <summary>A user's role in one organization. Primary key: (organization, user).</summary>
public class OrganizationMembership
{
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public OrganizationRole Role { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Organization Organization { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;
}
