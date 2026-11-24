using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Shared.Billing;

/// <summary>One plan a workspace could move to: the catalogue's key and display name, and what the plan includes.</summary>
public sealed record UpgradePlanOption(string Key, string Name, OrganizationEntitlementSet Entitlements);

/// <summary>
/// Which catalogue plan unlocks a plan feature (<see cref="PlanGate"/> keys). Display only: nothing here authorizes
/// anything; a refusal is decided by <see cref="PlanGate"/> alone and merely carries the answer. The plan name is read
/// from the catalogue rows, never typed here, and no price is involved.
/// </summary>
public static class PlanUnlocks
{
    /// <summary>The feature keys a plan can lack, in the order the billing summary lists them.</summary>
    public static readonly IReadOnlyList<string> Features =
    [
        PlanGate.VideoGenerationFeature, PlanGate.AvatarsFeature, PlanGate.AudioGenerationFeature, PlanGate.RoomsFeature,
        PlanGate.SegmentationFeature, PlanGate.AudioCleanupFeature, PlanGate.VideoEnhanceFeature, PlanGate.SocialPublishingFeature,
        PlanGate.ApiFeature, PlanGate.ResolutionFeature
    ];

    /// <summary>
    /// Whether <paramref name="set"/> includes <paramref name="feature"/>. For <see cref="PlanGate.ResolutionFeature"/> the
    /// plan must render at least <paramref name="shortSide"/> pixels (its short side). An unknown feature is never included.
    /// </summary>
    public static bool Allows(OrganizationEntitlementSet set, string feature, int? shortSide = null) => feature switch
    {
        PlanGate.VideoGenerationFeature => set.VideoGenAllowed,
        PlanGate.AvatarsFeature => set.AvatarsAllowed,
        PlanGate.AudioGenerationFeature => set.AudioGenAllowed,
        PlanGate.RoomsFeature => set.RoomsAllowed,
        PlanGate.SegmentationFeature => set.SegmentationAllowed,
        PlanGate.AudioCleanupFeature => set.AudioCleanupAllowed,
        PlanGate.VideoEnhanceFeature => set.VideoEnhanceAllowed,
        PlanGate.SocialPublishingFeature => set.SocialPublishingAllowed,
        PlanGate.ApiFeature or PlanGate.ApiWriteFeature => set.ApiAccess,
        PlanGate.ResolutionFeature => shortSide is { } side && set.MaxOutputHeight >= side,
        _ => false
    };

    /// <summary>The first plan (catalogue order, so the lowest) of <paramref name="plans"/> that includes the feature, else null.</summary>
    public static UpgradePlanOption? Lowest(IReadOnlyList<UpgradePlanOption>? plans, string feature, int? shortSide = null)
    {
        if (plans is null) return null;
        foreach (UpgradePlanOption option in plans)
        {
            if (Allows(option.Entitlements, feature, shortSide)) return option;
        }

        return null;
    }

    /// <summary>The purchasable plans (subscription and per-seat), in catalogue order, as options.</summary>
    public static IReadOnlyList<UpgradePlanOption> OptionsFrom(IEnumerable<Plan> catalogue) =>
        [.. catalogue
            .Where(p => p.IsActive && p.Kind is PlanKind.Subscription or PlanKind.PerSeat)
            .OrderBy(p => p.SortOrder)
            .Select(p => new UpgradePlanOption(p.Key, p.Name, PlanEntitlements.ToSet(p)))];

    /// <summary>
    /// For each feature <paramref name="current"/> lacks, the lowest catalogue plan that includes it, as the billing summary's
    /// <c>entitlements.unlocks</c>. Resolution is the next step up from the plan's cap. Empty when the set carries no catalogue.
    /// </summary>
    public static IReadOnlyList<object> UnlocksFor(OrganizationEntitlementSet current)
    {
        List<object> unlocks = [];
        foreach (string feature in Features)
        {
            int? shortSide = null;
            if (feature == PlanGate.ResolutionFeature)
            {
                if (current.MaxOutputHeight == OrganizationEntitlementSet.UnlimitedInt) continue;
                shortSide = current.MaxOutputHeight + 1;
            }
            else if (Allows(current, feature))
            {
                continue;
            }

            if (Lowest(current.UpgradePlans, feature, shortSide) is { } plan)
                unlocks.Add(new { feature, planKey = plan.Key, planName = plan.Name });
        }

        return unlocks;
    }

    /// <summary>
    /// <paramref name="violation"/> with the plan that unlocks its feature, taken from the catalogue the entitlement set was
    /// read with. Unchanged when the set carries none (billing off, no catalogue) or no plan includes the feature.
    /// </summary>
    public static PlanGateViolation Unlocking(this PlanGateViolation violation, OrganizationEntitlementSet set, int? shortSide = null)
    {
        UpgradePlanOption? plan = Lowest(set.UpgradePlans, violation.Feature, shortSide);
        return plan is null ? violation : violation with { UnlockPlan = plan.Key, UnlockPlanName = plan.Name };
    }
}
