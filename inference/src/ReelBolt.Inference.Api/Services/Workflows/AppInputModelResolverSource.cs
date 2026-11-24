using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// Lane L4's model source (docs/design/app-inputs.md 8.3): the models a run's organization may choose, read through
/// <see cref="InferenceProviderAdminService.SelectableRowsAsync"/>, the one definition the step editor's picker
/// (<c>GET /api/v1/inference-providers/selectable</c>) uses. Platform rows that are not fully priced and an organization's
/// own rows while its plan forbids bring-your-own are absent, so a value naming them is refused before anything is
/// charged. Another organization's row is never read (the query is scoped to the platform and this organization).
/// </summary>
public sealed class AppInputModelResolverSource(
    InferenceApiDbContext db,
    IInferenceProviderResolver? resolver = null,
    IOrganizationEntitlements? entitlements = null,
    TimeProvider? time = null) : IAppInputModelResolverSource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AppModelCandidate>> CandidatesAsync(Guid? organizationId, Guid projectId, CancellationToken ct)
    {
        // No organization (a platform-only caller): the platform's rows only; Guid.Empty matches no organization row.
        Guid org = organizationId ?? Guid.Empty;
        List<ModelCapabilityView> rows = await InferenceProviderAdminService.SelectableRowsAsync(
            db, entitlements, resolver, org, capability: null, (time ?? TimeProvider.System).GetUtcNow().UtcDateTime, ct);
        return [.. rows.Select(ToCandidate)];
    }

    /// <summary>One selectable row as a candidate.</summary>
    public static AppModelCandidate ToCandidate(ModelCapabilityView row) => new(
        row.Id, row.Capability.ToString(), row.Kind.ToString(), row.ModelName, row.Name, row.OrganizationId is null, row.IsDefault);
}
