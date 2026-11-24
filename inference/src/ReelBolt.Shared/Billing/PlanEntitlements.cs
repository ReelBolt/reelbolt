using System.Text.Json;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Shared.Billing;

/// <summary>
/// Maps <see cref="Plan.EntitlementsJson"/> onto <see cref="OrganizationEntitlementSet"/> (R3): the
/// JSON keys are exactly the set's camelCase property names, so a new entitlement needs no mapper
/// change. An omitted key keeps the record default (unlimited / allowed).
/// </summary>
public static class PlanEntitlements
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The entitlement set a plan grants; <c>Tier</c> is always the plan key.</summary>
    public static OrganizationEntitlementSet ToSet(Plan plan)
    {
        OrganizationEntitlementSet parsed = string.IsNullOrWhiteSpace(plan.EntitlementsJson)
            ? new OrganizationEntitlementSet()
            : JsonSerializer.Deserialize<OrganizationEntitlementSet>(plan.EntitlementsJson, Options) ?? new OrganizationEntitlementSet();
        return parsed with { Tier = plan.Key };
    }
}
