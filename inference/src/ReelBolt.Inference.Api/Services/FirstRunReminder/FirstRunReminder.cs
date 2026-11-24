namespace ReelBolt.Inference.Api.Services.FirstRunReminder;

/// <summary>Where a reminder stands. Stored as text.</summary>
public static class FirstRunReminderStates
{
    /// <summary>Claimed before the send; a crash or an unknown outcome leaves it here and it is never retried.</summary>
    public const string Claimed = "Claimed";

    public const string Sent = "Sent";

    public const string Failed = "Failed";

    /// <summary>A per-user opt-out recorded ahead of any send (support or a future settings toggle).</summary>
    public const string OptedOut = "OptedOut";
}

/// <summary>
/// One row per user, ever (table <c>first_run_reminders</c>, primary key = user): the claim that makes "at most one
/// reminder per user" true even across replicas. No address, no body. Cascades with the account.
/// </summary>
public class FirstRunReminderRecord
{
    public Guid UserId { get; set; }

    public string State { get; set; } = FirstRunReminderStates.Claimed;

    public DateTime ClaimedAt { get; set; }

    public DateTime? SentAt { get; set; }

    /// <summary><c>en</c> or <c>it</c>.</summary>
    public string Locale { get; set; } = "en";

    /// <summary>The <see cref="FirstRunReminderConsentMode"/> name the send was authorised under.</summary>
    public string ConsentMode { get; set; } = string.Empty;

    /// <summary>A stable, address-free failure code, when <see cref="State"/> is Failed.</summary>
    public string? ErrorCode { get; set; }
}
