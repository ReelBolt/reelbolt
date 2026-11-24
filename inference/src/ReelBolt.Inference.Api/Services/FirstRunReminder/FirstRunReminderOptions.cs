namespace ReelBolt.Inference.Api.Services.FirstRunReminder;

/// <summary>How the reminder decides who may receive it. See docs/design/first-run-reminder-mail.md.</summary>
public enum FirstRunReminderConsentMode
{
    /// <summary>Marketing reading (default, the cautious one): only an address with an OptedIn marketing consent row.</summary>
    RequireMarketingOptIn,

    /// <summary>Service-message reading: a verified user without a Withdrawn marketing row. Needs the owner's decision and counsel.</summary>
    ServiceMessage,
}

/// <summary>
/// Section <c>FirstRunReminder</c>. <see cref="Enabled"/> defaults to false and <see cref="DryRun"/> to true: out of the
/// box the sweeper does no work, and with Enabled only it counts candidates and sends nothing. A mail can leave only
/// with <c>Enabled=true</c>, <c>DryRun=false</c> and <c>BREVO_TRANSACTIONAL_API_KEY</c> set.
/// </summary>
public sealed class FirstRunReminderOptions
{
    public const string SectionName = "FirstRunReminder";

    public bool Enabled { get; set; }

    public bool DryRun { get; set; } = true;

    public FirstRunReminderConsentMode ConsentMode { get; set; } = FirstRunReminderConsentMode.RequireMarketingOptIn;

    /// <summary>
    /// <see cref="FirstRunReminderConsentMode.ServiceMessage"/> is NOT approved (the owner chose marketing opt-in only). It does
    /// nothing unless this is also true, which must mean counsel has reviewed the reading in writing.
    /// </summary>
    public bool ServiceMessageCounselReviewed { get; set; }

    /// <summary>The first project must be at least this many minutes old.</summary>
    public int MinAgeMinutes { get; set; } = 60;

    /// <summary>... and at most this many (a user past it is never reminded).</summary>
    public int MaxAgeMinutes { get; set; } = 120;

    /// <summary>Global cap on reminders claimed per UTC day.</summary>
    public int DailyCap { get; set; } = 50;

    /// <summary>Candidates examined per pass.</summary>
    public int BatchSize { get; set; } = 50;

    public int PollIntervalMinutes { get; set; } = 10;

    /// <summary>Quiet hours in <see cref="QuietHoursTimeZone"/>: no send from start (inclusive) to end (exclusive). Equal values disable.</summary>
    public int QuietHoursStartHour { get; set; } = 21;

    public int QuietHoursEndHour { get; set; } = 8;

    public string QuietHoursTimeZone { get; set; } = "Europe/Rome";

    /// <summary>Addresses at these domains (internal or test accounts) are never reminded.</summary>
    public List<string> ExcludedEmailDomains { get; set; } = [];

    /// <summary>Env <c>BREVO_TRANSACTIONAL_API_KEY</c>. Never committed, never logged.</summary>
    public string? BrevoTransactionalApiKey { get; set; }

    /// <summary>The sender address; empty falls back to <c>Marketing:Campaigns:FromAddress</c>.</summary>
    public string? FromAddress { get; set; }

    public string FromName { get; set; } = "ReelBolt";
}
