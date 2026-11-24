using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Inference;

/// <summary>
/// The organization an Inference API provider resolution runs for: the request's active
/// organization, or the one a background job opened with <c>BeginOrganizationScope</c>. A system
/// scope (migrations, seeding, cross-org sweeps) is the platform-only view, never "every org".
/// </summary>
public sealed class ApiInferenceTenantAccessor(ITenantContext tenant) : InferenceTenantAccessorBase
{
    protected override Guid? AmbientOrganizationId => tenant.IsSystem ? null : tenant.OrganizationId;
}
