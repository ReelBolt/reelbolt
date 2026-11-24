using System.Linq.Expressions;
using ReelBolt.Shared.Data.Models.Billing;

namespace ReelBolt.Inference.Api.Services.Billing.Reconcile;

/// <summary>
/// What counts as a stuck watch — the one predicate the sweep (its count, gauge and <c>billing.change_stuck</c>
/// log), the admin attention list and the daily report share, so the three cannot disagree.
/// </summary>
public static class BillingReconcileBacklog
{
    /// <summary>The <see cref="BillingReconcileWatch.LastOutcome"/> a check that found no live subscription writes.</summary>
    public const string AwaitingNothingOutcome = nameof(ReconcileOutcome.NothingToReconcile);

    /// <summary>
    /// A watch whose change is older than <paramref name="stuckBefore"/> — except a
    /// <see cref="BillingReconciler.CheckoutKind"/> watch whose last check asked the provider and found no live
    /// subscription (<c>NothingToReconcile</c> without an error: an abandoned checkout, routine, not a lost webhook).
    /// That one is still rechecked on its <c>CheckoutRecheck</c> cadence; a checkout never checked, last
    /// ProviderUnavailable, Stale or TooRecent, or NothingToReconcile because the org has no billing customer to ask
    /// about (<see cref="BillingReconciler.NoCustomerError"/>), still counts.
    /// Translatable by EF (Npgsql and Sqlite): constants and column comparisons only.
    /// </summary>
    public static Expression<Func<BillingReconcileWatch, bool>> IsStuck(DateTime stuckBefore)
        => w => w.ChangeRequestedAt != null
            && w.ChangeRequestedAt <= stuckBefore
            && !(w.ChangeKind == BillingReconciler.CheckoutKind && w.LastOutcome == AwaitingNothingOutcome && w.LastError == null);

    /// <summary>The admin attention filter: a watch whose reconcile keeps failing, or <see cref="IsStuck"/>.</summary>
    public static Expression<Func<BillingReconcileWatch, bool>> IsFailingOrStuck(DateTime stuckBefore)
    {
        Expression<Func<BillingReconcileWatch, bool>> stuck = IsStuck(stuckBefore);
        ParameterExpression w = stuck.Parameters[0];
        Expression failing = Expression.GreaterThan(
            Expression.Property(w, nameof(BillingReconcileWatch.ConsecutiveFailures)), Expression.Constant(0));
        return Expression.Lambda<Func<BillingReconcileWatch, bool>>(Expression.OrElse(failing, stuck.Body), w);
    }
}
