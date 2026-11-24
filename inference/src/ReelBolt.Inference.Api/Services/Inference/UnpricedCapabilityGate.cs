using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.Inference;

/// <summary>
/// the admin surfaces (REST and the assistant's provider tools) never leave a PLATFORM capability enabled
/// without a price in force for every unit it meters (T-026: the resolver's "fully priced" rule, not just any one price
/// row), because the resolver would refuse to run it anyway and, before that rule, the
/// meter recorded it at zero credits (docs/metering-and-billing.md, "Unpriced models fail closed"). Applies only while
/// the resolver fails closed (<see cref="IInferenceProviderResolver.UnpricedModelsFailClosed"/>, i.e.
/// <c>Billing:Enabled</c>); organization (BYO) capabilities and the system compute provider are never affected. An
/// oracle-priced capability (Higgsfield) passes while its price oracle answers a probe.
/// </summary>
internal static class UnpricedCapabilityGate
{
    /// <summary>Whether the gate applies at all (billing on and a resolver that fails closed).</summary>
    public static bool Applies(IInferenceProviderResolver? resolver) => resolver?.UnpricedModelsFailClosed == true;

    /// <summary>
    /// Whether <paramref name="capabilityId"/> (serving <paramref name="capability"/>) is fully priced at <paramref name="at"/>:
    /// a price in force for every unit it meters, the resolver's own rule (<see cref="ModelCapabilityView.IsFullyPriced"/>).
    /// </summary>
    public static async Task<bool> IsFullyPricedAsync(
        InferenceApiDbContext db, Guid capabilityId, InferenceProviderCapability capability, DateTime at, CancellationToken ct)
    {
        List<UsageKind> units = await db.ModelCapabilityPrices
            .Where(p => p.ModelCapabilityId == capabilityId && p.EffectiveFrom <= at && (p.EffectiveTo == null || p.EffectiveTo > at))
            .Select(p => p.Unit)
            .ToListAsync(ct);
        return ModelCapabilityView.IsFullyPriced(capability, units);
    }

    /// <summary>
    /// True when <paramref name="capability"/> (its <c>Model.Provider</c> loaded) is a platform capability that must not
    /// be enabled: not fully priced (some metered unit has no price in force) and, for an oracle-priced one, an oracle
    /// that does not answer.
    /// </summary>
    public static async Task<bool> BlocksAsync(
        InferenceApiDbContext db, InferenceProviderTester? tester, ModelCapability capability, DateTime at, CancellationToken ct)
    {
        if (capability.OrganizationId is not null || capability.Model.Provider.IsSystem) return false;
        if (await IsFullyPricedAsync(db, capability.Id, capability.Capability, at, ct)) return false;
        return !(capability.PriceSource == ModelPriceSource.Oracle && tester is not null
                 && await tester.PriceOracleAnswersAsync(capability, ct));
    }

    /// <summary>
    /// The first of <paramref name="capabilities"/> (each with its <c>Model.Provider</c> loaded) that is switched on and
    /// <see cref="BlocksAsync"/>: what switching on the model or provider account above it would make usable without a
    /// price. Null when there is none.
    /// </summary>
    public static async Task<ModelCapability?> FirstExposedAsync(
        InferenceApiDbContext db, InferenceProviderTester? tester, IEnumerable<ModelCapability> capabilities, DateTime at, CancellationToken ct)
    {
        foreach (ModelCapability capability in capabilities.Where(c => c.IsEnabled))
        {
            if (await BlocksAsync(db, tester, capability, at, ct)) return capability;
        }

        return null;
    }

    /// <summary>
    /// The refusal shown when switching on <paramref name="what"/> (a model or a provider account) would make
    /// <paramref name="capability"/>, an enabled capability under it, usable with no price.
    /// </summary>
    public static string ExposesRefusalMessage(string what, ModelCapability capability) =>
        $"{what} cannot be switched on: {RefusalMessage(capability)} Or switch that capability off first.";

    /// <summary>The refusal shown when enabling (or keeping enabled) an unpriced capability.</summary>
    public static string RefusalMessage(ModelCapability capability) => capability.PriceSource == ModelPriceSource.Oracle
        ? $"'{capability.Model.DisplayName}' ({capability.Capability}) is priced by its provider's price quote, and the provider " +
          "did not answer one just now, so it cannot be switched on. Check its key and try again."
        : $"'{capability.Model.DisplayName}' ({capability.Capability}) has no price in force, so it cannot be switched on: it " +
          "would run without charging. Add a price for it first, then switch it on.";

    /// <summary>What a creation says when it left a new capability switched off.</summary>
    public static string CreatedDisabledMessage(ModelCapability capability) =>
        $"'{capability.Model.DisplayName}' ({capability.Capability}) was created switched OFF: the price book has no price " +
        "for it. Add a price, then switch it on.";
}
