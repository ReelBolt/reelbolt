namespace ReelBolt.Shared.Tenancy;

/// <summary>Deployment shape, from <c>Tenancy:Mode</c> (Go API: <c>TENANCY_MODE</c>). Persisted nowhere.</summary>
public enum TenancyMode
{
    /// <summary>
    /// Today's behaviour: admin-invited users, a bootstrap <see cref="WellKnownOrganizations.DefaultId"/>
    /// team org everyone joins, no billing, every entitlement unlimited. The default.
    /// </summary>
    SelfHosted,

    /// <summary>Self-serve signup, Personal workspaces until a user creates a Team, plan-driven entitlements.</summary>
    Cloud
}

/// <summary>Binding for the <c>Tenancy</c> configuration section.</summary>
public sealed class TenancyOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Tenancy";

    /// <summary>Deployment shape; <see cref="TenancyMode.SelfHosted"/> when unset.</summary>
    public TenancyMode Mode { get; set; } = TenancyMode.SelfHosted;

    /// <summary>
    /// Entitlements handed to every org by the config-backed default
    /// (<see cref="ConfigOrganizationEntitlements"/>) until billing replaces it with plan data.
    /// The defaults are all-allowed, which is exactly self-host behaviour.
    /// </summary>
    public OrganizationEntitlementSet DefaultEntitlements { get; set; } = OrganizationEntitlementSet.Unlimited;
}
