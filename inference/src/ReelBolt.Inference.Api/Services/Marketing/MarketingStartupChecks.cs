using Microsoft.Extensions.Options;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>
/// One warning each at startup for the marketing and mail settings whose absence weakens a control
/// without breaking anything visibly: an unset <c>Marketing:SiteForwardToken</c> (N-05) and an unset
/// <c>Mail:LogRefKey</c> (contract C4).
/// </summary>
public sealed class MarketingStartupChecks : IHostedService
{
    /// <summary>The Warning raised when <c>Marketing:SiteForwardToken</c> is empty.</summary>
    public static readonly EventId SiteForwardTokenMissing = new(41_610, "MarketingSiteForwardTokenMissing");

    /// <summary>The Warning raised when <c>Mail:LogRefKey</c> is empty.</summary>
    public static readonly EventId LogRefKeyMissing = new(41_611, "MailLogRefKeyMissing");

    /// <summary>The Warning raised when <c>Marketing:Campaigns:BrevoListId</c> or <c>BrevoTestListId</c> is set to something that is not a list id.</summary>
    public static readonly EventId BrevoListIdInvalid = new(41_612, "MarketingBrevoListIdInvalid");

    private readonly MarketingCampaignOptions? _campaigns;
    private readonly MarketingContactSyncOptions? _contactSync;
    private readonly MarketingOptions _marketing;
    private readonly MailLogRef _logRef;
    private readonly ILogger<MarketingStartupChecks> _logger;

    public MarketingStartupChecks(
        IOptions<MarketingOptions> marketing, MailLogRef logRef, ILogger<MarketingStartupChecks> logger,
        IOptions<MarketingCampaignOptions>? campaigns = null, IOptions<MarketingContactSyncOptions>? contactSync = null)
    {
        _contactSync = contactSync?.Value;
        _campaigns = campaigns?.Value;
        _marketing = marketing.Value;
        _logRef = logRef;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_marketing.SiteForwardToken))
        {
            _logger.LogWarning(SiteForwardTokenMissing,
                "Marketing:SiteForwardToken (SITE_FORWARD_TOKEN) is not set: the marketing site's X-ReelBolt-Client-IP is ignored, "
                + "so every opt-in forwarded by the site shares the site server's per-IP bucket, and no request counts as bot-checked "
                + "above the domain and global alarm thresholds. Set the same secret on the site and the API.");
        }

        if (!_logRef.KeyConfigured)
        {
            _logger.LogWarning(LogRefKeyMissing,
                "Mail:LogRefKey (MAIL_LOG_REF_KEY) is not set: mail log references use a random per-process key, "
                + "so they match neither the Go API's references nor this process's after a restart.");
        }

        if (_campaigns is not null
            && (MarketingCampaignOptions.IsMalformedId(_campaigns.BrevoListIdSetting) || MarketingCampaignOptions.IsMalformedId(_campaigns.BrevoTestListIdSetting)))
        {
            _logger.LogWarning(BrevoListIdInvalid,
                "Marketing:Campaigns:BrevoListId (BREVO_LIST_ID) or BrevoTestListId (BREVO_TEST_LIST_ID) is not a whole number: "
                + "it is read as not configured, so no campaign can be sent to a list until it is fixed.");
        }

        if (_contactSync is not null && _campaigns is not null)
        {
            bool configured = !string.IsNullOrWhiteSpace(_campaigns.BrevoCampaignApiKey) && _campaigns.BrevoListId > 0;
            _logger.LogInformation("Brevo contact sync: active={Active} enabled={Enabled} keyAndListConfigured={Configured}",
                _contactSync.Enabled && configured, _contactSync.Enabled, configured);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
