using ReelBolt.Inference.Api.Services.CloudStorage;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>
/// Marketing-consent settings. Section <c>Marketing</c> (compose: <c>Marketing__PublicBaseUrl</c> …).
/// Consent does not depend on <c>Billing:Enabled</c>.
/// </summary>
public sealed class MarketingOptions
{
    public const string SectionName = "Marketing";

    /// <summary>
    /// The public origin the confirm and unsubscribe links point at
    /// (<c>{PublicBaseUrl}/app/marketing/confirm?token=…</c>, <c>{PublicBaseUrl}/app/unsubscribe?token=…</c>).
    /// Unset: falls back to <c>CloudStorage:PublicBaseUrl</c> (compose <c>PUBLIC_BASE_URL</c>), the
    /// deployment's existing public-origin setting. With neither set, no confirmation mail is sent and
    /// the CSV export is refused, rather than shipping links that go nowhere.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>How long a double-opt-in confirm link stays valid.</summary>
    public int ConfirmTtlDays { get; set; } = 7;

    public const int DefaultConfirmMailboxCooldownMinutes = 10;
    public const int DefaultConfirmMailboxPerDay = 3;
    public const int DefaultConfirmPerIpPerHour = 5;
    public const int DefaultConfirmPerDomainPerHour = 30;
    public const int DefaultConfirmGlobalPerDay = 200;
    public const int DefaultConfirmGlobalHardCeilingPerDay = 1000;

    /// <summary>
    /// Per mailbox: at most one confirmation mail per this many minutes, per literal address (on the
    /// consent row's <c>last_confirmation_sent_at</c>) and per canonical mailbox (sliding window in
    /// <c>mail_rate_counters</c>). Non-positive: 10.
    /// </summary>
    public int ConfirmMailboxCooldownMinutes { get; set; } = DefaultConfirmMailboxCooldownMinutes;

    /// <summary>
    /// Per canonical mailbox: at most this many confirmation mails per 24 hours (sliding). A genuine
    /// visitor whose mailbox was spent by someone else still sees "check your email" and can ask again
    /// once the window has slid on. Non-positive: 3.
    /// </summary>
    public int ConfirmMailboxPerDay { get; set; } = DefaultConfirmMailboxPerDay;

    /// <summary>Per client IP (IPv4 /32, IPv6 /64), per hour (sliding): the tight first layer. Non-positive: 5.</summary>
    public int ConfirmPerIpPerHour { get; set; } = DefaultConfirmPerIpPerHour;

    /// <summary>
    /// Per recipient domain, per hour (sliding): an ALARM threshold. Above it a warning is logged once
    /// per window and only bot-checked requests (the marketing site's trusted forward) still get a
    /// mail; direct anonymous calls get none. Non-positive: 30.
    /// </summary>
    public int ConfirmPerDomainPerHour { get; set; } = DefaultConfirmPerDomainPerHour;

    /// <summary>All anonymous confirmation mails per 24 hours (sliding): an ALARM threshold like the domain one. Non-positive: 200.</summary>
    public int ConfirmGlobalPerDay { get; set; } = DefaultConfirmGlobalPerDay;

    /// <summary>All anonymous confirmation mails per 24 hours (sliding): a hard ceiling that refuses everyone (alert). Non-positive: 1000.</summary>
    public int ConfirmGlobalHardCeilingPerDay { get; set; } = DefaultConfirmGlobalHardCeilingPerDay;

    public int EffectiveConfirmMailboxCooldownMinutes =>
        ConfirmMailboxCooldownMinutes > 0 ? ConfirmMailboxCooldownMinutes : DefaultConfirmMailboxCooldownMinutes;

    public int EffectiveConfirmMailboxPerDay => ConfirmMailboxPerDay > 0 ? ConfirmMailboxPerDay : DefaultConfirmMailboxPerDay;

    public int EffectiveConfirmPerIpPerHour => ConfirmPerIpPerHour > 0 ? ConfirmPerIpPerHour : DefaultConfirmPerIpPerHour;

    public int EffectiveConfirmPerDomainPerHour => ConfirmPerDomainPerHour > 0 ? ConfirmPerDomainPerHour : DefaultConfirmPerDomainPerHour;

    public int EffectiveConfirmGlobalPerDay => ConfirmGlobalPerDay > 0 ? ConfirmGlobalPerDay : DefaultConfirmGlobalPerDay;

    public int EffectiveConfirmGlobalHardCeilingPerDay =>
        ConfirmGlobalHardCeilingPerDay > 0 ? ConfirmGlobalHardCeilingPerDay : DefaultConfirmGlobalHardCeilingPerDay;

    /// <summary>
    /// The shared secret the marketing site sends as <c>X-ReelBolt-Site-Token</c> (contract C3; compose
    /// <c>Marketing__SiteForwardToken</c>). Only when it is set and matches is the site's
    /// <c>X-ReelBolt-Client-IP</c> trusted, and only such a forward (the site ran its bot check)
    /// still gets a mail above the domain and global alarm thresholds. Unset, that header is always
    /// ignored, every site opt-in shares the site server's IP bucket, and a warning is logged at startup.
    /// </summary>
    public string? SiteForwardToken { get; set; }

    /// <summary>Pending (never confirmed) rows older than this are deleted with their events.</summary>
    public int PendingRetentionDays { get; set; } = 30;

    /// <summary>How often the pending-row sweeper runs. 0 disables it.</summary>
    public int PendingSweepIntervalHours { get; set; } = 24;

    /// <summary>Overrides the wording registry location (default: next to the assembly, under <c>Marketing/</c>).</summary>
    public string? WordingPath { get; set; }

    /// <summary>
    /// The origin links are minted against, without a trailing slash, or null when neither this
    /// section nor <c>CloudStorage:PublicBaseUrl</c> holds an absolute http(s) URL.
    /// </summary>
    public string? ResolveBaseUrl(CloudStorageOptions? cloudStorage)
    {
        foreach (string? candidate in new[] { PublicBaseUrl, cloudStorage?.PublicBaseUrl })
        {
            string? trimmed = candidate?.Trim().TrimEnd('/');
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            {
                return trimmed;
            }
        }

        return null;
    }
}
