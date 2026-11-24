namespace ReelBolt.Shared.Tenancy;

/// <summary>
/// What an organization is. Persisted as a string, so appending a member needs no migration;
/// never rename or reorder existing members.
/// </summary>
public enum OrganizationKind
{
    /// <summary>A user's own workspace: exactly one member (the Owner), no invites, cannot be deleted or left.</summary>
    Personal,

    /// <summary>A shared workspace with the roles Owner, Admin, Member and Billing.</summary>
    Team,

    /// <summary>The platform itself: owns platform-managed providers and platform admins. Not a workspace.</summary>
    Platform
}
