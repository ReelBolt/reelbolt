namespace ReelBolt.Shared.Tenancy;

/// <summary>
/// A member's role inside one organization. Persisted as a string, so appending a member needs no
/// migration; never rename or reorder existing members. Platform-wide authority is NOT a role:
/// it is the separate <c>platformAdmin</c> claim.
/// </summary>
public enum OrganizationRole
{
    /// <summary>Everything, including deleting the org and managing billing. Every org keeps at least one.</summary>
    Owner,

    /// <summary>Members, invites, org providers and project deletion; no ownership transfer or org deletion.</summary>
    Admin,

    /// <summary>Creates and works in projects; cannot manage people or providers.</summary>
    Member,

    /// <summary>Billing pages only. No access to projects, files, agents or providers.</summary>
    Billing
}
