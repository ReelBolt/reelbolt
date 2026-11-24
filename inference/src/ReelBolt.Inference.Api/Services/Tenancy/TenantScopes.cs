using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Tenancy;

/// <summary>
/// Null-tolerant scope helpers for background paths. A host that registers no
/// <see cref="ITenantContext"/> (some unit-test containers) has no tenant filter either, so there
/// is nothing to scope and a no-op disposable is correct; production always registers one.
/// </summary>
internal static class TenantScopes
{
    private sealed class Noop : IDisposable
    {
        public static readonly Noop Instance = new();
        public void Dispose() { }
    }

    public static IDisposable System(this ITenantContext? tenant) =>
        tenant?.BeginSystemScope() ?? Noop.Instance;

    public static IDisposable Organization(this ITenantContext? tenant, Guid organizationId) =>
        tenant?.BeginOrganizationScope(organizationId) ?? Noop.Instance;
}
