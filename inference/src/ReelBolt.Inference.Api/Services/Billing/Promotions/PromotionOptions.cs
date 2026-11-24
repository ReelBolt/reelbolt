namespace ReelBolt.Inference.Api.Services.Billing.Promotions;

/// <summary>Binding for <c>Billing:Promotions</c> (contract C8).</summary>
public sealed class PromotionOptions
{
    public const string SectionName = "Billing:Promotions";

    /// <summary>How long an unpaid checkout reserves its promotions: the provider checkout's lifetime.</summary>
    public int CheckoutReservationMinutes { get; set; } = 60;

    /// <summary>Checkouts carrying a promotion one user may create per hour.</summary>
    public int CheckoutsPerUserPerHour { get; set; } = 10;

    /// <summary>
    /// How often <see cref="PromotionCheckoutSweeper"/> cancels the provider transactions of expired or
    /// superseded reservations (contract C10). Zero or less switches the sweeper off.
    /// </summary>
    public int CheckoutSweepIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// S-11: the longest a checkout waits for its promotions' locks, in-process gate and database lock
    /// together, before it answers a retryable <see cref="PromotionErrors.CheckoutBusy"/>. Validated on
    /// start to 1..60000 (a bad value fails the boot); the service's own floor of 1 stays for direct construction.
    /// </summary>
    public int CheckoutLockWaitMilliseconds { get; set; } = 5000;
}
