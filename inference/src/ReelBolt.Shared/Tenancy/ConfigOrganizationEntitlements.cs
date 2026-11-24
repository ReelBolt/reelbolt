namespace ReelBolt.Shared.Tenancy;

/// <summary>
/// The A0 default <see cref="IOrganizationEntitlements"/>: every org gets
/// <see cref="TenancyOptions.DefaultEntitlements"/>. B8a replaces it with plan data. DI
/// registration belongs to the first WP that consumes it (A6/A9a).
/// </summary>
public sealed class ConfigOrganizationEntitlements(TenancyOptions options) : IOrganizationEntitlements
{
    /// <inheritdoc />
    public ValueTask<OrganizationEntitlementSet> GetAsync(Guid organizationId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(options.DefaultEntitlements);
}
