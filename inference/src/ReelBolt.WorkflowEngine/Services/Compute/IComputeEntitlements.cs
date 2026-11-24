using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Services.Compute;

/// <summary>
/// One organization's compute-side answer, as the selector needs it: the org's own
/// <see cref="ComputePreference"/> (A1, <c>organizations.compute_preference</c>) combined with the
/// plan entitlement that says whether a runner may be used at all (B8a,
/// <see cref="OrganizationEntitlementSet.RunnerAllowed"/>).
/// <para>
/// <see cref="CloudPoolAllowed"/> has no plan column behind it yet — every shipped plan may use the
/// pool — so it is carried here as an explicit member rather than assumed, which is what makes the
/// "nothing is available" arm of the selector a reachable, tested case instead of dead code.
/// </para>
/// </summary>
public sealed record ComputeEntitlements(
    ComputePreference Preference,
    bool RunnerAllowed,
    bool CloudPoolAllowed)
{
    /// <summary>Everything allowed, nothing restricted: the self-host answer (Billing disabled) and the config default.</summary>
    public static ComputeEntitlements Unlimited { get; } = new(ComputePreference.Auto, RunnerAllowed: true, CloudPoolAllowed: true);

    /// <summary>
    /// The org's EFFECTIVE preference: <see cref="ComputePreference.RunnerOnly"/> is only meaningful
    /// when a runner is actually permitted, so an org whose plan withholds
    /// <see cref="OrganizationEntitlementSet.RunnerAllowed"/> is downgraded to
    /// <see cref="ComputePreference.CloudOnly"/> rather than being sent to a runner it may not use.
    /// </summary>
    public ComputePreference EffectivePreference => RunnerAllowed ? Preference : ComputePreference.CloudOnly;
}

/// <summary>
/// D6: the organization facts the compute selector reads. Deliberately its own contract rather than
/// another method on <see cref="IOrganizationEntitlements"/>: the entitlement set is a plan question
/// resolved inside the Inference API's billing stack, while the compute preference is a column of
/// <c>organizations</c> the engine maps read-only. One implementation joins the two.
/// </summary>
public interface IComputeEntitlements
{
    /// <summary>
    /// The compute entitlements of <paramref name="organizationId"/>. Read per decision and never
    /// cached: a user who switches to <c>CloudOnly</c> must stop landing on their desktop runner on
    /// the very next step.
    /// </summary>
    ValueTask<ComputeEntitlements> GetAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The production <see cref="IComputeEntitlements"/>: <see cref="IOrganizationEntitlements"/> for the
/// plan half, plus a read of <c>organizations.compute_preference</c> through a short-lived DI scope
/// (the <see cref="WorkflowEngineDbContext"/> is scoped, this service is not).
/// <para>
/// <b>Fail-open, and only in the safe direction.</b> An organization row the engine cannot find — a
/// deleted org, or an execution whose organization predates the tenancy backfill — reads as
/// <see cref="ComputePreference.Auto"/> with the plan's own runner entitlement, which is exactly the
/// column default. It never fabricates a preference that routes work somewhere the org did not ask
/// for: <c>Auto</c> behaves as <c>CloudOnly</c> until a runner is paired.
/// </para>
/// </summary>
public sealed class DbComputeEntitlements(
    IOrganizationEntitlements plans,
    IServiceScopeFactory scopeFactory) : IComputeEntitlements
{
    /// <inheritdoc />
    public async ValueTask<ComputeEntitlements> GetAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        OrganizationEntitlementSet set = await plans.GetAsync(organizationId, cancellationToken).ConfigureAwait(false);

        ComputePreference preference = ComputePreference.Auto;
        if (organizationId != Guid.Empty)
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            preference = await db.Organizations
                .Where(o => o.Id == organizationId)
                .Select(o => o.ComputePreference)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return new ComputeEntitlements(preference, set.RunnerAllowed, CloudPoolAllowed: true);
    }
}
