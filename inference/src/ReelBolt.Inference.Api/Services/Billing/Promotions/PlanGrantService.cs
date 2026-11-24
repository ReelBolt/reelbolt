using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Billing.Promotions;

/// <summary>
/// Platform-admin manual plan grants: a Paddle-free <c>subscriptions</c> row with
/// <c>provider = NULL</c>, and the audit row that says who gave it and why. The row is a real
/// subscription, so the product's plan gates apply immediately; no parallel entitlement model.
/// </summary>
public interface IPlanGrantService
{
    /// <summary>The plans an admin may gift: active, the Trial plan excluded.</summary>
    Task<IReadOnlyList<Plan>> ListGrantablePlansAsync(CancellationToken ct);

    /// <summary>
    /// Grants <paramref name="dto"/>'s plan to the org. A replayed <c>requestId</c> answers with the
    /// original grant (and <c>Replayed = true</c>); a provider-backed subscription refuses.
    /// </summary>
    Task<PromotionOutcome<PlanGrantOutcome>> GrantAsync(Guid orgId, GrantPlanDto dto, Guid adminUserId, CancellationToken ct);

    /// <summary>The org's manual grants, newest first.</summary>
    Task<IReadOnlyList<AdminSubscriptionGrant>> ListAsync(Guid orgId, CancellationToken ct);

    /// <summary>Ends one manual grant now (period to now, <see cref="SubscriptionStatus.Canceled"/>).</summary>
    Task<PromotionOutcome<AdminSubscriptionGrant>> RevokeAsync(
        Guid orgId, Guid grantId, RevokePlanGrantDto dto, Guid adminUserId, CancellationToken ct);
}

/// <inheritdoc />
public sealed class PlanGrantService(
    InferenceApiDbContext db,
    IEntitlementCache entitlements,
    TimeProvider? time = null) : IPlanGrantService
{
    /// <summary>At most three years, matching a credit grant.</summary>
    public const int MaxDurationDays = 1095;

    /// <summary>Statuses a provider-backed subscription is still live in; any of them refuses a gift.</summary>
    private static readonly SubscriptionStatus[] Live =
    [
        SubscriptionStatus.Trialing,
        SubscriptionStatus.Active,
        SubscriptionStatus.PastDue,
        SubscriptionStatus.Paused
    ];

    private DateTime Now => (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    /// <inheritdoc />
    public async Task<IReadOnlyList<Plan>> ListGrantablePlansAsync(CancellationToken ct)
        => await db.Plans.AsNoTracking()
            .Where(p => p.IsActive && p.Kind != PlanKind.Trial)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdminSubscriptionGrant>> ListAsync(Guid orgId, CancellationToken ct)
        => await db.AdminSubscriptionGrants.AsNoTracking()
            .Where(g => g.OrganizationId == orgId)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<PromotionOutcome<PlanGrantOutcome>> GrantAsync(
        Guid orgId, GrantPlanDto dto, Guid adminUserId, CancellationToken ct)
    {
        DateTime now = Now;
        List<string> errors = [];
        if (dto.RequestId == Guid.Empty)
        {
            errors.Add("requestId is required.");
        }

        if (orgId == WellKnownOrganizations.PlatformId
            || !await db.Organizations.AnyAsync(o => o.Id == orgId && o.Kind != OrganizationKind.Platform, ct))
        {
            return PromotionErrors.NotFound;
        }

        string planKey = dto.PlanKey?.Trim() ?? string.Empty;
        Plan? plan = planKey.Length == 0
            ? null
            : await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Key == planKey, ct);
        if (plan is null || !plan.IsActive || plan.Kind == PlanKind.Trial)
        {
            errors.Add("planKey must name an active plan other than the trial plan.");
        }

        DateTime? periodEnd = dto.PeriodEnd is { } explicitEnd ? PromotionRules.ToUtc(explicitEnd) : null;
        if (periodEnd is not null && dto.DurationDays is not null)
        {
            errors.Add("Provide either durationDays or periodEnd, not both.");
        }
        else if (periodEnd is null && dto.DurationDays is not { } days)
        {
            errors.Add("durationDays or periodEnd is required.");
        }
        else if (periodEnd is null)
        {
            int duration = dto.DurationDays!.Value;
            if (duration is < 1 or > MaxDurationDays)
            {
                errors.Add($"durationDays must be 1 to {MaxDurationDays}.");
            }
            else
            {
                periodEnd = now.AddDays(duration);
            }
        }
        else if (periodEnd < now.AddDays(1) || periodEnd > now.AddYears(3))
        {
            errors.Add("periodEnd must be between 1 day and 3 years from now.");
        }

        string reason = dto.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > PromotionService.MaxReasonLength)
        {
            errors.Add($"reason must be 1 to {PromotionService.MaxReasonLength} characters.");
        }

        if (errors.Count > 0)
        {
            return PromotionError.Validation(errors);
        }

        GrantRequest request = new(orgId, plan!.Id, planKey, periodEnd!.Value, reason);
        if (await ExistingGrantAsync(request, dto.RequestId, ct) is { } replay)
        {
            return replay;
        }

        // A gift never sits beside a paid subscription: the provider owns that row, and a manual
        // Active row would fight it for the plan gate's OrderBy(UpdatedAt) choice.
        if (await db.Subscriptions.AsNoTracking()
            .AnyAsync(s => s.OrganizationId == orgId && s.Provider != null && Live.Contains(s.Status), ct))
        {
            return PlanGrantErrors.ProviderActive;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // One manual grant per organization: end the previous manual subscription (never a
            // provider-backed one, filtered above) and name it in the new audit row.
            List<Subscription> manual = await db.Subscriptions
                .Where(s => s.OrganizationId == orgId && s.Provider == null && s.Status != SubscriptionStatus.Canceled)
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync(ct);
            Guid? superseded = null;
            foreach (Subscription old in manual)
            {
                superseded ??= old.Id;
                old.Status = SubscriptionStatus.Canceled;
                old.PeriodEnd = old.PeriodEnd is { } end && end < now ? end : now;
                old.CancelAtPeriodEnd = false;
                old.UpdatedAt = now;
            }

            Subscription subscription = new()
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                PlanId = plan.Id,
                Provider = null,
                ProviderSubscriptionId = null,
                Status = SubscriptionStatus.Active,
                PeriodStart = now,
                PeriodEnd = periodEnd.Value,
                Seats = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Subscriptions.Add(subscription);

            AdminSubscriptionGrant row = new()
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                PlanId = plan.Id,
                PlanKey = planKey,
                PeriodStart = now,
                PeriodEnd = periodEnd.Value,
                Reason = reason,
                GrantedByUserId = adminUserId,
                SubscriptionId = subscription.Id,
                SupersededSubscriptionId = superseded,
                RequestId = dto.RequestId,
                CreatedAt = now
            };
            db.AdminSubscriptionGrants.Add(row);

            // Re-check inside the transaction: a Paddle webhook may have committed a provider row
            // between the check above and here. If it did, nothing is written. A webhook that commits
            // after this grant cancels it, as it cancels every other non-canceled row of the org.
            if (await db.Subscriptions.AsNoTracking()
                .AnyAsync(s => s.OrganizationId == orgId && s.Provider != null && Live.Contains(s.Status), ct))
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return PlanGrantErrors.ProviderActive;
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            entitlements.Invalidate(orgId);
            return PromotionOutcome<PlanGrantOutcome>.Success(new PlanGrantOutcome(row, false));
        }
        catch (DbUpdateException)
        {
            // A concurrent request with the same requestId won the unique index: answer with its row.
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            return await ExistingGrantAsync(request, dto.RequestId, ct)
                ?? throw new InvalidOperationException("Admin plan grant failed to save.");
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<PromotionOutcome<AdminSubscriptionGrant>> RevokeAsync(
        Guid orgId, Guid grantId, RevokePlanGrantDto dto, Guid adminUserId, CancellationToken ct)
    {
        DateTime now = Now;
        AdminSubscriptionGrant? grant = await db.AdminSubscriptionGrants
            .FirstOrDefaultAsync(g => g.Id == grantId && g.OrganizationId == orgId, ct);
        if (grant is null)
        {
            return PromotionErrors.NotFound;
        }

        string reason = dto.Reason?.Trim() ?? string.Empty;
        if (reason.Length > PromotionService.MaxReasonLength)
        {
            return PromotionError.Validation([$"reason must be at most {PromotionService.MaxReasonLength} characters."]);
        }

        // A revoke is idempotent: an already-ended grant answers with itself and writes nothing.
        if (grant.RevokedAt is not null)
        {
            return PromotionOutcome<AdminSubscriptionGrant>.Success(grant);
        }

        Subscription? subscription = await db.Subscriptions
            .FirstOrDefaultAsync(s => s.Id == grant.SubscriptionId, ct);
        if (subscription?.Provider is not null)
        {
            return PlanGrantErrors.NotManual;
        }

        if (subscription is not null)
        {
            subscription.Status = SubscriptionStatus.Canceled;
            subscription.PeriodEnd = subscription.PeriodEnd is { } end && end < now ? end : now;
            subscription.CancelAtPeriodEnd = false;
            subscription.UpdatedAt = now;
        }

        grant.RevokedAt = now;
        grant.RevokedByUserId = adminUserId;
        grant.RevokeReason = reason.Length == 0 ? null : reason;
        await db.SaveChangesAsync(ct);
        entitlements.Invalidate(orgId);
        return PromotionOutcome<AdminSubscriptionGrant>.Success(grant);
    }

    /// <summary>What a grant request asks for, compared field by field against a replayed requestId.</summary>
    private sealed record GrantRequest(Guid OrgId, Guid PlanId, string PlanKey, DateTime PeriodEnd, string Reason);

    /// <summary>
    /// The stored grant for a requestId: replayed when the request asks for exactly the same grant
    /// (org, plan, period end to the millisecond, trimmed reason), otherwise a 409, never the original
    /// grant silently standing in for a different one.
    /// </summary>
    private async Task<PromotionOutcome<PlanGrantOutcome>?> ExistingGrantAsync(GrantRequest request, Guid requestId, CancellationToken ct)
    {
        AdminSubscriptionGrant? existing = await db.AdminSubscriptionGrants.AsNoTracking()
            .FirstOrDefaultAsync(g => g.RequestId == requestId, ct);
        if (existing is null)
        {
            return null;
        }

        bool same = existing.OrganizationId == request.OrgId
            && existing.PlanId == request.PlanId
            && string.Equals(existing.PlanKey, request.PlanKey, StringComparison.Ordinal)
            && (PromotionRules.Utc(existing.PeriodEnd) - request.PeriodEnd).Duration() < TimeSpan.FromMilliseconds(1)
            && string.Equals(existing.Reason, request.Reason, StringComparison.Ordinal);
        return same
            ? PromotionOutcome<PlanGrantOutcome>.Success(new PlanGrantOutcome(existing, true))
            : PromotionErrors.RequestIdConflict;
    }
}
