namespace ReelBolt.Shared.Tenancy;

/// <summary>
/// What one organization is allowed to do. This is the single entitlement contract of the platform:
/// tenancy (A9a/A12), billing (B8a: output limits, watermark, rooms) and compute (D6: runner
/// selection) all read it, and nothing else defines a second one.
/// <para>
/// A0 ships <see cref="ConfigOrganizationEntitlements"/>; B8a replaces it with plan data. With
/// <c>Billing:Enabled=false</c> (self-host) the implementation returns
/// <see cref="OrganizationEntitlementSet.Unlimited"/> for every org.
/// </para>
/// <para>
/// Callers must treat the result as a per-call decision and never cache it beyond a request or a
/// step: a downgrade takes effect on the next read.
/// </para>
/// </summary>
public interface IOrganizationEntitlements
{
    /// <summary>The full entitlement set for <paramref name="organizationId"/>.</summary>
    ValueTask<OrganizationEntitlementSet> GetAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

/// <summary>Convenience accessors over <see cref="IOrganizationEntitlements.GetAsync"/>.</summary>
public static class OrganizationEntitlementsExtensions
{
    /// <summary>May the org register its own (bring-your-own) inference providers? (decision D3)</summary>
    public static async ValueTask<bool> AllowsByoProvidersAsync(this IOrganizationEntitlements entitlements, Guid organizationId, CancellationToken cancellationToken = default)
        => (await entitlements.GetAsync(organizationId, cancellationToken).ConfigureAwait(false)).ByoAllowed;

    /// <summary>Executions the org may run at once.</summary>
    public static async ValueTask<int> MaxConcurrentExecutionsAsync(this IOrganizationEntitlements entitlements, Guid organizationId, CancellationToken cancellationToken = default)
        => (await entitlements.GetAsync(organizationId, cancellationToken).ConfigureAwait(false)).MaxConcurrentExecutions;

    /// <summary>Plan key of the org (a catalog id such as <c>trial</c>; <c>selfhost</c> when billing is off).</summary>
    public static async ValueTask<string> TierAsync(this IOrganizationEntitlements entitlements, Guid organizationId, CancellationToken cancellationToken = default)
        => (await entitlements.GetAsync(organizationId, cancellationToken).ConfigureAwait(false)).Tier;
}
