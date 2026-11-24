namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>
/// Chooses between the registered billing providers. New checkouts, previews and promotions use the
/// <see cref="Active"/> provider (<c>Billing:Provider</c>); anything on an existing subscription uses the
/// provider named on that row (<see cref="ForSubscription"/>).
/// </summary>
public static class BillingProviderSelector
{
    /// <summary>
    /// The configured provider whose name equals the selection. With no selection object, the first
    /// configured provider (the pre-selection behaviour).
    /// </summary>
    public static IBillingProvider? Active(IEnumerable<IBillingProvider> providers, BillingProviderOptions? selection)
        => selection is null
            ? providers.FirstOrDefault(p => p.IsConfigured)
            : providers.FirstOrDefault(p => p.IsConfigured && Matches(p, selection.Selected));

    /// <summary>The provider registered under <paramref name="name"/> (configured or not), or null.</summary>
    public static IBillingProvider? ByName(IEnumerable<IBillingProvider> providers, string? name)
        => name is null ? null : providers.FirstOrDefault(p => Matches(p, name));

    /// <summary>
    /// The provider named on the subscription row when it is registered and configured; otherwise the
    /// <see cref="Active"/> one (also for a subscription-less or provider-less row).
    /// </summary>
    public static IBillingProvider? ForSubscription(IEnumerable<IBillingProvider> providers, BillingProviderOptions? selection, string? subscriptionProvider)
    {
        if (!string.IsNullOrWhiteSpace(subscriptionProvider)
            && providers.FirstOrDefault(p => p.IsConfigured && Matches(p, subscriptionProvider)) is { } bound)
        {
            return bound;
        }

        return Active(providers, selection);
    }

    private static bool Matches(IBillingProvider provider, string name)
        => string.Equals(provider.Name, name, StringComparison.OrdinalIgnoreCase);
}
