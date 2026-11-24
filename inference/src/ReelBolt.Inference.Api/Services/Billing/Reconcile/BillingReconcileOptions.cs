using Microsoft.Extensions.Options;

namespace ReelBolt.Inference.Api.Services.Billing.Reconcile;

/// <summary>
/// Lost-webhook recovery (<c>Billing:Reconcile</c>). Everything is a no-op while <c>Billing:Enabled</c> is
/// false or <see cref="Enabled"/> is false. Every number must be greater than 0 (validated on start).
/// </summary>
public sealed class BillingReconcileOptions
{
    public const string SectionName = "Billing:Reconcile";

    /// <summary>Master switch for the reconciler (lazy heal, sweep, admin resync).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long after the provider's change a missing webhook counts as lost (default 10 min).</summary>
    public int StuckAfterSeconds { get; set; } = 600;

    /// <summary>How often the sweep runs.</summary>
    public int SweepIntervalSeconds { get; set; } = 300;

    /// <summary>How long after start the first sweep waits.</summary>
    public int StartupDelaySeconds { get; set; } = 60;

    /// <summary>Most organizations one sweep reconciles.</summary>
    public int MaxPerSweep { get; set; } = 50;

    /// <summary>
    /// How soon the sweep looks again at an overdue renewal or cancellation it checked and found nothing due on.
    /// A watch whose change is still due is retried on the next sweep instead (a checkout every
    /// <see cref="CheckoutRecheckMinutes"/>).
    /// </summary>
    public int RecheckAfterMinutes { get; set; } = 360;

    /// <summary>How soon the sweep looks again at a started checkout whose subscription is not live yet (default 30 min).</summary>
    public int CheckoutRecheckMinutes { get; set; } = 30;

    /// <summary>How long a started checkout is watched for its lost <c>subscription.created</c>.</summary>
    public int CheckoutWatchHours { get; set; } = 48;

    public TimeSpan StuckAfter => TimeSpan.FromSeconds(StuckAfterSeconds);

    public TimeSpan SweepInterval => TimeSpan.FromSeconds(SweepIntervalSeconds);

    public TimeSpan StartupDelay => TimeSpan.FromSeconds(StartupDelaySeconds);

    public TimeSpan RecheckAfter => TimeSpan.FromMinutes(RecheckAfterMinutes);

    public TimeSpan CheckoutWatch => TimeSpan.FromHours(CheckoutWatchHours);

    public TimeSpan CheckoutRecheck => TimeSpan.FromMinutes(CheckoutRecheckMinutes);
}

/// <summary>Refuses a <see cref="BillingReconcileOptions"/> with any number that is not greater than 0.</summary>
public sealed class BillingReconcileOptionsValidator : IValidateOptions<BillingReconcileOptions>
{
    public ValidateOptionsResult Validate(string? name, BillingReconcileOptions options)
    {
        List<string> failures = [];
        Check(options.StuckAfterSeconds, nameof(options.StuckAfterSeconds));
        Check(options.SweepIntervalSeconds, nameof(options.SweepIntervalSeconds));
        Check(options.StartupDelaySeconds, nameof(options.StartupDelaySeconds));
        Check(options.MaxPerSweep, nameof(options.MaxPerSweep));
        Check(options.RecheckAfterMinutes, nameof(options.RecheckAfterMinutes));
        Check(options.CheckoutWatchHours, nameof(options.CheckoutWatchHours));
        Check(options.CheckoutRecheckMinutes, nameof(options.CheckoutRecheckMinutes));
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);

        void Check(int value, string key)
        {
            if (value <= 0)
            {
                failures.Add($"{BillingReconcileOptions.SectionName}:{key} must be greater than 0 (got {value}).");
            }
        }
    }
}
