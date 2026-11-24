using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Account;

/// <summary>What happened to one organization's subscriptions on an account deletion.</summary>
/// <param name="Cancelled">Subscriptions the platform itself cancelled (the internal Trial row).</param>
/// <param name="RetainedForVendor">
/// Live provider subscriptions this canceller does not cancel. <c>IBillingProvider.CancelSubscriptionAsync</c>
/// exists, but it only schedules a cancel at period end and the row changes when the provider's webhook
/// arrives (see docs/billing.md), so this purge step does not call it: the provider-side half stays a
/// follow-up and the purge names the subscription instead of pretending it is gone.
/// </param>
/// <param name="Notes">One line per subscription, for the purge log.</param>
public sealed record AccountBillingCancellationResult(
    int Cancelled,
    int RetainedForVendor,
    IReadOnlyList<string> Notes);

/// <summary>
/// The billing step of an account deletion (C9b: "cancel the personal subscription via B9a").
/// </summary>
public interface IAccountBillingCanceller
{
    Task<AccountBillingCancellationResult> CancelForOrganizationAsync(Guid organizationId, CancellationToken ct);
}

/// <summary>
/// The shipped canceller. It cancels what the platform actually owns — an internal Trial row has no
/// provider behind it, so flipping its status is the whole cancellation — and reports, rather than
/// fabricates, what it cannot: a provider subscription needs a provider call, and B9b owns that call.
/// Billing being switched off is not an error here: no rows, no work.
/// </summary>
public sealed class AccountBillingCanceller : IAccountBillingCanceller
{
    private readonly InferenceApiDbContext _db;
    private readonly ILogger<AccountBillingCanceller> _logger;

    public AccountBillingCanceller(InferenceApiDbContext db, ILogger<AccountBillingCanceller> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<AccountBillingCancellationResult> CancelForOrganizationAsync(Guid organizationId, CancellationToken ct)
    {
        List<Subscription> live = await _db.Subscriptions
            .Where(s => s.OrganizationId == organizationId && s.Status != SubscriptionStatus.Canceled)
            .ToListAsync(ct);

        int cancelled = 0;
        int retained = 0;
        List<string> notes = [];
        foreach (Subscription subscription in live)
        {
            if (string.IsNullOrEmpty(subscription.Provider) && string.IsNullOrEmpty(subscription.ProviderSubscriptionId))
            {
                subscription.Status = SubscriptionStatus.Canceled;
                subscription.CancelAtPeriodEnd = true;
                subscription.UpdatedAt = DateTime.UtcNow;
                cancelled++;
                notes.Add($"internal subscription {subscription.Id} cancelled");
                continue;
            }

            retained++;
            notes.Add(
                $"provider subscription {subscription.Provider}/{subscription.ProviderSubscriptionId} of organization "
                + $"{organizationId} is still live and must be cancelled through the billing provider (B9b)");
            _logger.LogWarning(
                "Account deletion: organization {OrganizationId} still has a live {Provider} subscription {ProviderSubscriptionId}; "
                + "it is B9b's to cancel and needs follow-up",
                organizationId, subscription.Provider, subscription.ProviderSubscriptionId);
        }

        if (cancelled > 0)
            await _db.SaveChangesAsync(ct);

        return new AccountBillingCancellationResult(cancelled, retained, notes);
    }
}
