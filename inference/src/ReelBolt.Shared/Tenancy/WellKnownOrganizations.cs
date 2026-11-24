namespace ReelBolt.Shared.Tenancy;

/// <summary>
/// Fixed identifiers of the two organizations that exist in every deployment. They are constants
/// (not generated) so the A1 backfill migration, the Go API, the seeders and tests all agree on
/// them without a lookup. Never change a value: rows reference them by id.
/// </summary>
public static class WellKnownOrganizations
{
    /// <summary>
    /// The <see cref="OrganizationKind.Platform"/> org. Owns the platform-managed inference
    /// providers and the platform admins. Nobody works in it: it is never offered as an active
    /// workspace and never owns projects.
    /// </summary>
    public static readonly Guid PlatformId = new("5c1f0000-0000-4000-8000-000000000001");

    /// <summary>
    /// The bootstrap <see cref="OrganizationKind.Team"/> org of a self-hosted install
    /// (<see cref="TenancyMode.SelfHosted"/>) that admin-invited users join, preserving today's
    /// behaviour.
    /// </summary>
    public static readonly Guid DefaultId = new("5c1f0000-0000-4000-8000-000000000002");

    /// <summary>Slug of the Platform org (unique index on <c>organizations.slug</c>).</summary>
    public const string PlatformSlug = "platform";

    /// <summary>Slug of the Default org.</summary>
    public const string DefaultSlug = "default";

    /// <summary>True when <paramref name="id"/> is one of the two well-known organizations.</summary>
    public static bool IsWellKnown(Guid id) => id == PlatformId || id == DefaultId;
}
