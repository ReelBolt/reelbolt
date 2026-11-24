using Microsoft.Extensions.Configuration;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>
/// <c>Marketing:Campaigns:*</c>. Drafts, dry run and test send work once <see cref="Enabled"/> is set; a send to the list
/// additionally needs <see cref="ListSendEnabled"/>, the Brevo key and <see cref="BrevoListId"/> (all off or unset by default).
/// The footer identity and postal address come from configuration only (env
/// <c>MARKETING_CONTROLLER_NAME</c>, <c>MARKETING_FOOTER_ADDRESS</c>), never from the repo.
/// </summary>
public sealed class MarketingCampaignOptions
{
    public const string SectionName = "Marketing:Campaigns";

    /// <summary>Master switch; false: the campaign endpoints answer 404.</summary>
    public bool Enabled { get; set; }

    /// <summary>Env <c>BREVO_CAMPAIGN_API_KEY</c> (a key separate from any other). Never committed, never logged.</summary>
    public string? BrevoCampaignApiKey { get; set; }

    /// <summary>The managed Brevo list the campaign is sent to (rebuilt from our consent list before each send). 0 or empty: not configured.</summary>
    [ConfigurationKeyName("_BrevoListIdComputed")]
    public long BrevoListId
    {
        get => ParseId(BrevoListIdSetting);
        set => BrevoListIdSetting = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The bound text of <c>BrevoListId</c>. A string so that nothing the operator types can fault the host: a blank, white
    /// space or non-numeric value reads as not configured (0) and <see cref="MarketingStartupChecks"/> warns about it.
    /// </summary>
    [ConfigurationKeyName("BrevoListId")]
    public string? BrevoListIdSetting { get; set; }

    /// <summary>The Brevo list that holds only <see cref="TestRecipient"/> for test sends.</summary>
    [ConfigurationKeyName("_BrevoTestListIdComputed")]
    public long BrevoTestListId
    {
        get => ParseId(BrevoTestListIdSetting);
        set => BrevoTestListIdSetting = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The bound value of <c>BrevoTestListId</c>; see <see cref="BrevoListIdSetting"/>.</summary>
    [ConfigurationKeyName("BrevoTestListId")]
    public string? BrevoTestListIdSetting { get; set; }

    /// <summary>A list id from configuration text: a positive whole number, else 0 (not configured).</summary>
    internal static long ParseId(string? text)
        => long.TryParse(text?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long v) && v > 0 ? v : 0;

    /// <summary>True when a list id was given but is not usable: not empty, and not a positive whole number ("0" is the documented not-configured value).</summary>
    internal static bool IsMalformedId(string? text)
        => !string.IsNullOrEmpty(text) && text.Trim() != "0" && ParseId(text) == 0;

    /// <summary>Default false. While false nothing is written to the managed list and no campaign is sent.</summary>
    public bool ListSendEnabled { get; set; }

    /// <summary>Env <c>BREVO_WEBHOOK_TOKEN</c> for the Brevo webhook (read by the webhook, blank = 404).</summary>
    public string? WebhookToken { get; set; }

    /// <summary>Pause between Brevo calls, under the contacts limit of 10 per second. 0 disables (tests).</summary>
    public int BrevoMinCallIntervalMs { get; set; } = 120;

    /// <summary>Pause between polls of a Brevo process. 0 polls back to back (tests).</summary>
    public int ProcessPollMs { get; set; } = 2000;

    /// <summary>How long one Brevo process may take before the run fails closed.</summary>
    public int ProcessWaitSeconds { get; set; } = 600;

    /// <summary>Contacts per import call.</summary>
    public int ImportChunkSize { get; set; } = 1000;

    /// <summary>A Syncing row untouched this long is taken as interrupted (or SendUnknown, once the send call began) on the next worker pass.</summary>
    public int SyncStaleMinutes { get; set; } = 30;

    /// <summary>A stale Syncing row whose managed-list read-back keeps failing is settled (interrupted, or SendUnknown) without staging anything once it is this many hours old. Default 24.</summary>
    public int SyncRecoveryMaxAgeHours { get; set; } = 24;

    /// <summary>Recovery stages erasures from the managed list only when at most this many addresses on it have no consent row. Above it nothing is staged. Default 25.</summary>
    public int RecoveryMaxStagedErasures { get; set; } = 25;

    /// <summary>A Sending row older than this many hours (Brevo keeps it in review, or reports a status nobody maps) may be settled by an admin with resolve. Default 24.</summary>
    public int SendingResolveAfterHours { get; set; } = 24;

    /// <summary>How often results of Sending campaigns are refreshed.</summary>
    public int ResultsRefreshMinutes { get; set; } = 10;

    /// <summary>The marketing sender address.</summary>
    public string FromAddress { get; set; } = "hello@reelbolt.ai";

    /// <summary>Env <c>MARKETING_TEST_RECIPIENT</c>; no default. Blank: test-send is refused.</summary>
    public string? TestRecipient { get; set; }

    /// <summary>Footer: who operates the service (env <c>MARKETING_CONTROLLER_NAME</c>).</summary>
    public string? FooterControllerName { get; set; }

    /// <summary>Footer: VAT number (public business data); env <c>MARKETING_FOOTER_VAT_NUMBER</c>. Blank: line omitted.</summary>
    public string? FooterVatNumber { get; set; } = "IT02862070030";

    /// <summary>Footer: the postal address (env <c>MARKETING_FOOTER_ADDRESS</c>).</summary>
    public string? FooterAddress { get; set; }

    /// <summary>Test sends allowed per admin per hour.</summary>
    public int TestSendsPerHour { get; set; } = 5;

    public string BrevoApiBase { get; set; } = "https://api.brevo.com/v3";

    public string FromName { get; set; } = "ReelBolt";
}
