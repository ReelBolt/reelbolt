using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Billing;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.Inference;

/// <summary>
/// T-037: a self-hosted default the seed prices at zero (on an OpenAICompatible or OpenJev provider) must not be pointed at
/// a hosted endpoint by an admin edit, because the zero would then price a hosted model at nothing. The seeder ends those
/// rows at its next boot, but an endpoint edit happens between boots, so the admin surfaces refuse it up front.
/// Applies only while <see cref="UnpricedCapabilityGate.Applies"/> (billing on); with billing off the edit is allowed and
/// the next boot with billing on closes the rows.
/// </summary>
internal static class SelfHostedZeroPriceGate
{
    /// <summary>
    /// True when a platform capability under <paramref name="providerId"/> holds a zero-cost row in force at <paramref name="at"/>
    /// with no credits override, on a provider of kind OpenAICompatible or OpenJev. The seed flags exactly those kinds with
    /// zero rows, so no notes marker is needed, and a row written before a marker existed is caught too.
    /// </summary>
    public static Task<bool> HasOpenRowsAsync(InferenceApiDbContext db, Guid providerId, DateTime at, CancellationToken ct) =>
        OpenSelfHostedZeroRows(db, providerId, at).AnyAsync(ct);

    /// <summary>The zero rows in force at <paramref name="at"/> that hold a self-hosted default on <paramref name="providerId"/>.</summary>
    private static IQueryable<ModelCapabilityPrice> OpenSelfHostedZeroRows(InferenceApiDbContext db, Guid providerId, DateTime at) =>
        db.ModelCapabilityPrices.Where(p =>
            p.EffectiveFrom <= at && (p.EffectiveTo == null || p.EffectiveTo > at)
            && p.CostUsdPerUnit == 0m
            && p.CreditsMillisPerUnitOverride == null
            && p.ModelCapability.OrganizationId == null
            && p.ModelCapability.Model.ProviderId == providerId
            && (p.ModelCapability.Model.Provider.Kind == InferenceProviderKind.OpenAICompatible
                || p.ModelCapability.Model.Provider.Kind == InferenceProviderKind.OpenJev));

    private static readonly Lazy<ModelCatalogSeed> CommittedSeed = new(ModelCatalogSeed.LoadDefault);

    /// <summary>True when the committed seed flags (kind, model code, capability) as a self-hosted default (selfHostedOnly).</summary>
    public static bool IsSelfHostedOnlyEntry(InferenceProviderKind kind, string modelCode, string capability) =>
        CommittedSeed.Value.Capabilities.Any(c => c.SelfHostedOnly && c.Kind == kind
            && string.Equals(c.ModelCode, modelCode, StringComparison.Ordinal)
            && string.Equals(c.Capability, capability, StringComparison.Ordinal));

    /// <summary>
    /// SEC-18: the refusal for a model-code or capability edit, or null when it may proceed. <paramref name="post"/> holds the
    /// capabilities whose (model code, capability) the edit changes, as they stand after it (the caller lists only changed
    /// ones). A capability that holds an open zero row is refused unless that (kind, model code, capability) is still one of
    /// the self-hosted seed entries. Billing off allows it.
    /// </summary>
    public static async Task<string?> IdentityRefusalAsync(InferenceApiDbContext db, IInferenceProviderResolver? resolver, Guid providerId,
        string providerName, InferenceProviderKind postKind, IReadOnlyList<(Guid CapabilityId, string ModelCode, string Capability)> post,
        DateTime at, CancellationToken ct)
    {
        if (post.Count == 0 || !UnpricedCapabilityGate.Applies(resolver)) return null;

        Guid[] ids = [.. post.Select(p => p.CapabilityId)];
        HashSet<Guid> open = [.. await OpenSelfHostedZeroRows(db, providerId, at)
            .Where(p => ids.Contains(p.ModelCapabilityId))
            .Select(p => p.ModelCapabilityId)
            .Distinct()
            .ToListAsync(ct)];
        foreach ((Guid capabilityId, string modelCode, string capability) in post)
        {
            if (open.Contains(capabilityId) && !IsSelfHostedOnlyEntry(postKind, modelCode, capability))
                return IdentityRefusalMessage();
        }

        return null;
    }

    /// <summary>The refusal for a model-code or capability edit on a model that holds an open self-hosted zero (SEC-18).</summary>
    public static string IdentityRefusalMessage() =>
        "This model is priced at zero as a self-hosted default; add a price for it before changing its model.";

    /// <summary>
    /// The refusal for pointing <paramref name="providerName"/> at a hosted endpoint while it holds an open self-hosted zero.
    /// Adding a price for the model closes the zero row (the price write ends the seed-owned row it supersedes), and then the
    /// edit is allowed.
    /// </summary>
    public static string RefusalMessage(string providerName) =>
        $"'{providerName}' is priced at zero as a self-hosted default; add a price for it before pointing it at a hosted endpoint.";

    /// <summary>
    /// The refusal for an edit on <paramref name="providerId"/>, or null when it may proceed. The edit is judged only when it
    /// sets an endpoint or changes the kind; then the post-edit pair (kind, endpoint) must stay zero-eligible: a kind of
    /// OpenAICompatible or OpenJev on a local endpoint. Refused when that fails while a zero row is in force. Billing off,
    /// or no open zero row, allows it.
    /// </summary>
    public static async Task<string?> RefusalAsync(InferenceApiDbContext db, IInferenceProviderResolver? resolver, Guid providerId,
        string providerName, InferenceProviderKind currentKind, InferenceProviderKind? newKind, string currentEndpoint, string? newEndpoint,
        DateTime at, CancellationToken ct)
    {
        bool kindChanges = newKind is { } k && k != currentKind;
        if (newEndpoint is null && !kindChanges) return null;
        if (!UnpricedCapabilityGate.Applies(resolver)) return null;

        InferenceProviderKind postKind = newKind ?? currentKind;
        string postEndpoint = newEndpoint ?? currentEndpoint;
        if (IsZeroKind(postKind) && SelfHostedEndpoint.IsLocal(postEndpoint)) return null;
        return await HasOpenRowsAsync(db, providerId, at, ct) ? RefusalMessage(providerName) : null;
    }

    /// <summary>The kinds a seed zero may be priced on (see <see cref="HasOpenRowsAsync"/>).</summary>
    public static bool IsZeroKind(InferenceProviderKind kind) =>
        kind is InferenceProviderKind.OpenAICompatible or InferenceProviderKind.OpenJev;
}
