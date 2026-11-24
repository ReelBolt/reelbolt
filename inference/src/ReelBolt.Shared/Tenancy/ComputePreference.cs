namespace ReelBolt.Shared.Tenancy;

/// <summary>
/// Where an organization prefers its media jobs (render, compile, analyse extraction) to run.
/// Stored on <c>organizations.compute_preference</c> as a string (A1), append-only. Whether a
/// runner may be used at all is a separate entitlement (<see cref="OrganizationEntitlementSet.RunnerAllowed"/>).
/// </summary>
public enum ComputePreference
{
    /// <summary>Behaves as <see cref="CloudOnly"/> until a runner is paired, then as <see cref="PreferRunner"/>.</summary>
    Auto,

    /// <summary>Never use a runner.</summary>
    CloudOnly,

    /// <summary>Use a paired, online runner when one is eligible; otherwise fall back to the cloud pool.</summary>
    PreferRunner,

    /// <summary>Use a runner or wait; never fall back to the cloud pool.</summary>
    RunnerOnly
}
