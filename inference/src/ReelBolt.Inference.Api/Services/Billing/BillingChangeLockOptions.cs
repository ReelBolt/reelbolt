using Microsoft.Extensions.Options;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>
/// Security S-4/S-23 (<c>Billing:ChangeLock</c>): the per-replica cap on the billing change lock's
/// unpooled connections. Validated on start: a bad value fails startup.
/// </summary>
public sealed class BillingChangeLockOptions
{
    public const string SectionName = "Billing:ChangeLock";

    /// <summary>Most lock connections this process holds at once, across every organization (at least 1).</summary>
    public int MaxConnections { get; set; } = 4;

    /// <summary>
    /// How long an attempt waits for a free connection slot before it is refused as busy (0 = never wait).
    /// </summary>
    public int SlotWaitMilliseconds { get; set; } = 2000;
}

/// <summary>Refuses <c>MaxConnections</c> below 1 and a negative <c>SlotWaitMilliseconds</c>, naming only the key.</summary>
public sealed class BillingChangeLockOptionsValidator : IValidateOptions<BillingChangeLockOptions>
{
    public ValidateOptionsResult Validate(string? name, BillingChangeLockOptions options)
    {
        List<string> failures = [];
        if (options.MaxConnections < 1)
        {
            failures.Add($"{BillingChangeLockOptions.SectionName}:{nameof(options.MaxConnections)} must be at least 1.");
        }

        if (options.SlotWaitMilliseconds < 0)
        {
            failures.Add($"{BillingChangeLockOptions.SectionName}:{nameof(options.SlotWaitMilliseconds)} must not be negative.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Security S-4/S-23: every billing change lock connection slot of this replica stayed taken for the whole
/// <see cref="BillingChangeLockOptions.SlotWaitMilliseconds"/>. Retryable; unlike a <c>null</c> claim it
/// says nothing about the organization's own change.
/// </summary>
public sealed class BillingChangeLockCapacityException : Exception
{
    public BillingChangeLockCapacityException()
        : base("Every billing change lock connection of this replica is in use.")
    {
    }

    public BillingChangeLockCapacityException(string message)
        : base(message)
    {
    }

    public BillingChangeLockCapacityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
