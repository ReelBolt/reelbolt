using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// A pending invitation to join an organization. The only invites table (plan decision R2):
/// only the SHA-256 hash of the token is stored, never the token itself.
/// </summary>
public class OrganizationInvite
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Email { get; set; } = string.Empty;
    public OrganizationRole Role { get; set; }

    /// <summary>Hex SHA-256 of the invite token; unique.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public Guid? InvitedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public Organization Organization { get; set; } = null!;
}
