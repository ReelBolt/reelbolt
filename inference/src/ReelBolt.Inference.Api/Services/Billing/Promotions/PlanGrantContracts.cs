using Microsoft.AspNetCore.Mvc;
using ReelBolt.Shared.Data.Models.Billing;

namespace ReelBolt.Inference.Api.Services.Billing.Promotions;

/// <summary>
/// Body of <c>POST /api/v1/billing/admin/orgs/{orgId}/plan-grants</c>. Exactly one of
/// <see cref="DurationDays"/> and <see cref="PeriodEnd"/> states the period.
/// </summary>
public sealed record GrantPlanDto(Guid RequestId, string? PlanKey, int? DurationDays, DateTime? PeriodEnd, string? Reason);

/// <summary>Body of <c>POST /api/v1/billing/admin/orgs/{orgId}/plan-grants/{grantId}/revoke</c>.</summary>
public sealed record RevokePlanGrantDto(string? Reason = null);

/// <summary>An admin plan grant, and whether it was an earlier request replayed.</summary>
public sealed record PlanGrantOutcome(AdminSubscriptionGrant Grant, bool Replayed);

/// <summary>The refusals specific to a manual plan grant.</summary>
public static class PlanGrantErrors
{
    /// <summary>
    /// The organization pays through the billing provider. A gift must never sit beside — or be
    /// silently overwritten by — a subscription someone is paying for.
    /// </summary>
    public static readonly PromotionError ProviderActive = new(
        StatusCodes.Status409Conflict,
        "PLAN_GRANT_PROVIDER_ACTIVE",
        "This organization has a subscription managed by the billing provider. Change or cancel it there; a manual grant would sit beside a paid subscription.");

    /// <summary>A revoke named a subscription that belongs to the billing provider, not to a grant.</summary>
    public static readonly PromotionError NotManual = new(
        StatusCodes.Status409Conflict,
        "PLAN_GRANT_NOT_MANUAL",
        "That subscription is managed by the billing provider and cannot be revoked here.");
}

/// <summary>How a grant row reads at a point in time: an admin ended it, its period passed, or it runs.</summary>
public static class PlanGrantState
{
    public static string Of(AdminSubscriptionGrant grant, DateTime now)
        => grant.RevokedAt is not null ? "revoked" : grant.PeriodEnd <= now ? "expired" : "active";

    public static object Dto(AdminSubscriptionGrant grant, string planName, DateTime now) => new
    {
        id = grant.Id,
        organizationId = grant.OrganizationId,
        planKey = grant.PlanKey,
        planName,
        periodStart = PromotionRules.Utc(grant.PeriodStart),
        periodEnd = PromotionRules.Utc(grant.PeriodEnd),
        reason = grant.Reason,
        grantedByUserId = grant.GrantedByUserId,
        subscriptionId = grant.SubscriptionId,
        supersededSubscriptionId = grant.SupersededSubscriptionId,
        requestId = grant.RequestId,
        revokedAt = PromotionRules.Utc(grant.RevokedAt),
        revokedByUserId = grant.RevokedByUserId,
        revokeReason = grant.RevokeReason,
        status = Of(grant, now),
        createdAt = PromotionRules.Utc(grant.CreatedAt)
    };
}
