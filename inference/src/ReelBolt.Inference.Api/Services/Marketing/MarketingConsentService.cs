using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Account;
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>
/// The request evidence stored with a consent change, truncated to the column widths.
/// <see cref="SiteVerified"/> (never stored) is true only for the marketing site's trusted forward
/// (contract C3), which the site sends only after its bot check: the one kind of anonymous request
/// that still gets a confirmation mail above the domain and global alarm thresholds (C6).
/// </summary>
public sealed record MarketingRequestEvidence(string? Ip, string? UserAgent, bool SiteVerified = false)
{
    public const int MaxIpLength = 64;
    public const int MaxUserAgentLength = 256;

    public const string SiteTokenHeader = "X-ReelBolt-Site-Token";
    public const string SiteClientIpHeader = "X-ReelBolt-Client-IP";

    /// <summary>
    /// The client IP under contract C3, and the User-Agent. <c>X-ReelBolt-Client-IP</c> (the visitor
    /// the marketing site forwards for) is trusted only when <paramref name="siteForwardToken"/> is
    /// configured and the request's <c>X-ReelBolt-Site-Token</c> equals it (fixed-time); otherwise it
    /// is ignored and the IP is the <c>X-Real-IP</c> header nginx sets, else the connection address.
    /// </summary>
    public static MarketingRequestEvidence From(HttpContext http, string? siteForwardToken)
    {
        bool siteVerified = IsTrustedSiteForward(http, siteForwardToken);
        string? ip = siteVerified ? ForwardedClientIp(http) : null;
        if (ip is null)
        {
            ip = http.Request.Headers["X-Real-IP"].ToString();
            if (string.IsNullOrWhiteSpace(ip))
            {
                ip = http.Connection.RemoteIpAddress?.ToString();
            }
        }

        string? agent = http.Request.Headers.UserAgent.ToString();
        return new MarketingRequestEvidence(
            Truncate(ip?.Trim(), MaxIpLength),
            Truncate(string.IsNullOrWhiteSpace(agent) ? null : agent, MaxUserAgentLength),
            siteVerified);
    }

    private static bool IsTrustedSiteForward(HttpContext http, string? siteForwardToken)
    {
        string expected = siteForwardToken?.Trim() ?? string.Empty;
        if (expected.Length == 0)
        {
            return false;
        }

        // Hashing both sides first keeps the comparison fixed-time in the length as well.
        byte[] presented = SHA256.HashData(Encoding.UTF8.GetBytes(http.Request.Headers[SiteTokenHeader].ToString()));
        return CryptographicOperations.FixedTimeEquals(presented, SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }

    private static string? ForwardedClientIp(HttpContext http)
    {
        string forwarded = http.Request.Headers[SiteClientIpHeader].ToString().Trim();
        return IPAddress.TryParse(forwarded, out IPAddress? address) ? address.ToString() : null;
    }

    internal static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}

/// <summary>The GET/PUT <c>consent</c> response shape.</summary>
public sealed record MarketingConsentView(
    string Status,
    string? WordingVersion,
    DateTime? ConsentedAt,
    DateTime? ConfirmedAt,
    DateTime? WithdrawnAt)
{
    public static readonly MarketingConsentView None = new("none", null, null, null, null);
}

/// <summary>The result of a signed-in change: either the new state, or a stale wording version.</summary>
public sealed record MarketingConsentUpdate(bool StaleWording, string CurrentWordingVersion, MarketingConsentView View);

public enum MarketingSubscribeOutcome
{
    Accepted,
    InvalidEmail,
    StaleWording,
}

public sealed record MarketingAdminCounts(int Pending, int OptedIn, int Withdrawn, int Exportable);

public sealed record MarketingLastExport(DateTime ExportedAt, int RowCount);

/// <summary>The GET <c>admin/consents</c> response shape.</summary>
public sealed record MarketingAdminSummary(MarketingAdminCounts Counts, MarketingLastExport? LastExport);

/// <summary>One CSV export, ready to stream.</summary>
public sealed record MarketingCsvExport(byte[] Content, string FileName, int RowCount);

/// <summary>
/// The marketing-consent store: one current-state row per normalized email plus an append-only event
/// log, with exactly one event per state change. Consent is per individual email, never per org.
/// Withdrawal keeps the row; only account deletion and the pending sweeper delete rows.
/// </summary>
public sealed class MarketingConsentService
{
    public const string ConfirmTemplate = "marketing-confirm";

    private static readonly JsonNamingPolicy Camel = JsonNamingPolicy.CamelCase;

    private readonly InferenceApiDbContext _db;
    private readonly IMarketingLinkTokens _tokens;
    private readonly IMarketingSuppressionHasher _suppression;
    private readonly IMarketingMailOutbox _outbox;
    private readonly MarketingWording _wording;
    private readonly MarketingOptions _options;
    private readonly CloudStorageOptions _cloudStorage;
    private readonly IMailRateCounterStore _counters;
    private readonly ILogger<MarketingConsentService> _logger;
    private readonly TimeProvider _clock;

    public MarketingConsentService(
        InferenceApiDbContext db,
        IMarketingLinkTokens tokens,
        IMarketingSuppressionHasher suppression,
        IMarketingMailOutbox outbox,
        MarketingWording wording,
        IOptions<MarketingOptions> options,
        IOptions<CloudStorageOptions> cloudStorage,
        IMailRateCounterStore counters,
        ILogger<MarketingConsentService> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _tokens = tokens;
        _suppression = suppression;
        _outbox = outbox;
        _wording = wording;
        _options = options.Value;
        _cloudStorage = cloudStorage.Value;
        _counters = counters;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>The request evidence, resolving the client IP under contract C3 with this deployment's site token.</summary>
    public MarketingRequestEvidence EvidenceFrom(HttpContext http) => MarketingRequestEvidence.From(http, _options.SiteForwardToken);

    /// <summary>The wire name of a status.</summary>
    public static string StatusName(MarketingConsentStatus status) => Camel.ConvertName(status.ToString());

    /// <summary>The wire name of a source (<c>siteContact</c>, <c>acceptTerms</c> …).</summary>
    public static string SourceName(MarketingConsentSource source) => Camel.ConvertName(source.ToString());

    /// <summary>Parses a wire source name, case-insensitively.</summary>
    public static MarketingConsentSource? ParseSource(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Any(char.IsDigit)
        && Enum.TryParse(value.Trim(), ignoreCase: true, out MarketingConsentSource source)
            ? source
            : null;

    public static MarketingConsentView ViewOf(MarketingConsent? row) => row is null
        ? MarketingConsentView.None
        : new MarketingConsentView(StatusName(row.Status), row.WordingVersion, Utc(row.ConsentedAt), Utc(row.ConfirmedAt), Utc(row.WithdrawnAt));

    /// <summary>Every stored time is UTC; say so on the wire whatever the provider hands back.</summary>
    private static DateTime? Utc(DateTime? value) => value is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;

    // ---------------------------------------------------------------------------------------------
    // Signed-in consent
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the account's email is verified, read from <c>application_users</c> (N-08): the JWT
    /// <c>emailVerified</c> claim may be stale or missing, and a missing claim must not count as
    /// verified. An unknown user is not verified.
    /// </summary>
    public async Task<bool> IsEmailVerifiedAsync(Guid userId, CancellationToken ct) =>
        await _db.ApplicationUsers.AsNoTracking()
            .Where(u => u.Id == userId && !u.IsServiceAccount)
            .Select(u => u.EmailVerifiedAt)
            .FirstOrDefaultAsync(ct) is not null;

    public async Task<MarketingConsentView> GetAsync(string email, CancellationToken ct)
    {
        string normalized = MarketingEmail.Normalize(email);
        return ViewOf(await _db.MarketingConsents.AsNoTracking().FirstOrDefaultAsync(c => c.Email == normalized, ct));
    }

    /// <summary>
    /// The signed-in toggle. Opt-in sets <c>OptedIn</c> with <c>ConsentedAt</c>; <c>ConfirmedAt</c>
    /// is set when the account's email is verified, else left for verify-email to set. Opt-out sets
    /// <c>Withdrawn</c>. Repeating the current choice changes nothing and writes no event.
    /// </summary>
    public async Task<MarketingConsentUpdate> SetAsync(
        Guid userId,
        string email,
        bool optIn,
        MarketingConsentSource source,
        string? wordingVersion,
        MarketingRequestEvidence evidence,
        CancellationToken ct)
    {
        string normalized = MarketingEmail.Normalize(email);
        if (optIn && !_wording.IsCurrent(wordingVersion))
        {
            return new MarketingConsentUpdate(true, _wording.Current, await GetAsync(normalized, ct));
        }

        DateTime now = Now;
        MarketingConsent? row = await _db.MarketingConsents.FirstOrDefaultAsync(c => c.Email == normalized, ct);

        if (!optIn)
        {
            if (row is null || row.Status == MarketingConsentStatus.Withdrawn)
            {
                // Saying no again also drops a pending anonymous re-subscribe request (not a state change).
                if (row?.ResubscribeRequestedAt is not null)
                {
                    row.ResubscribeRequestedAt = null;
                    await _db.SaveChangesAsync(ct);
                }

                return new MarketingConsentUpdate(false, _wording.Current, ViewOf(row));
            }

            row.Status = MarketingConsentStatus.Withdrawn;
            ClearNames(row, now);
            row.WithdrawnAt = now;
            row.Source = source;
            row.UserId ??= userId;
            Stamp(row, evidence, now);
            AddEvent(row, MarketingConsentAction.Withdrawn, source, evidence, now);
            await _db.SaveChangesAsync(ct);
            return new MarketingConsentUpdate(false, _wording.Current, ViewOf(row));
        }

        bool verified = await _db.ApplicationUsers.AsNoTracking()
            .Where(u => u.Id == userId && !u.IsServiceAccount)
            .Select(u => u.EmailVerifiedAt)
            .FirstOrDefaultAsync(ct) is not null;

        if (row is { Status: MarketingConsentStatus.OptedIn } && row.WordingVersion == wordingVersion)
        {
            // Already in, under this wording: only a missing confirmation can still change.
            if (verified && row.ConfirmedAt is null)
            {
                row.ConfirmedAt = now;
                row.UserId ??= userId;
                Stamp(row, evidence, now);
                AddEvent(row, MarketingConsentAction.Confirmed, source, evidence, now);
                await ClearSuppressionAsync(normalized, ct);
                await _db.SaveChangesAsync(ct);
            }

            return new MarketingConsentUpdate(false, _wording.Current, ViewOf(row));
        }

        if (row is null)
        {
            row = new MarketingConsent { Id = Guid.NewGuid(), Email = normalized };
            _db.MarketingConsents.Add(row);
        }

        row.Status = MarketingConsentStatus.OptedIn;
        row.UserId = userId;
        row.WordingVersion = wordingVersion!;
        row.Source = source;
        row.ConsentedAt = now;
        row.ConfirmedAt = verified ? now : null;
        row.WithdrawnAt = null;
        row.ResubscribeRequestedAt = null;
        Stamp(row, evidence, now);
        AddEvent(row, MarketingConsentAction.Granted, source, evidence, now);
        if (verified)
        {
            await ClearSuppressionAsync(normalized, ct);
        }

        await _db.SaveChangesAsync(ct);
        return new MarketingConsentUpdate(false, _wording.Current, ViewOf(row));
    }

    // ---------------------------------------------------------------------------------------------
    // Anonymous double opt-in
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Creates or keeps a Pending row and queues a confirmation mail within the caps of
    /// <see cref="TryClaimConfirmationMailAsync"/>, and none when the email is already OptedIn.
    /// A Withdrawn email stays Withdrawn (its withdrawal is never undone or swept away by an anonymous
    /// request): the request is recorded in <see cref="MarketingConsent.ResubscribeRequestedAt"/> and
    /// the address gets a mail; only its confirm click opts it back in. The caller answers identically whatever
    /// happened here; only input errors (a malformed email, a stale wording) are distinguishable, and
    /// they depend on the request alone, never on the store.
    /// </summary>
    public async Task<MarketingSubscribeOutcome> SubscribeAsync(
        string? email,
        string? wordingVersion,
        MarketingConsentSource source,
        MarketingRequestEvidence evidence,
        CancellationToken ct,
        string? locale = null,
        string? firstName = null)
    {
        string normalized = MarketingEmail.Normalize(email);
        if (!MarketingEmail.IsValid(normalized))
        {
            return MarketingSubscribeOutcome.InvalidEmail;
        }

        if (!_wording.IsCurrent(wordingVersion))
        {
            return MarketingSubscribeOutcome.StaleWording;
        }

        DateTime now = Now;
        MarketingConsent? row = await _db.MarketingConsents.FirstOrDefaultAsync(c => c.Email == normalized, ct);
        if (row is { Status: MarketingConsentStatus.OptedIn })
        {
            return MarketingSubscribeOutcome.Accepted;
        }

        bool created = false;
        if (row is null)
        {
            row = new MarketingConsent { Id = Guid.NewGuid(), Email = normalized, Status = MarketingConsentStatus.Pending };
            _db.MarketingConsents.Add(row);
            created = true;
        }

        if (created)
        {
            row.RequestedAt = now;
            row.WordingVersion = wordingVersion!;
            row.Source = source;
            Stamp(row, evidence, now);
            AddEvent(row, MarketingConsentAction.Requested, source, evidence, now);
        }

        // The anonymous form name is accepted ONLY when this very request created the row, and only when it passes the
        // strict allowlist (MarketingName.CleanFormName). On any existing row (any status, any name source) it is ignored,
        // so nobody who knows an address can rename its contact. It stays provisional: nothing reaches Brevo before the
        // row is confirmed, and an account (UserId) is the authority once the row has one. An invalid name is ignored
        // silently; the answer is identical either way (no oracle).
        if (created && MarketingName.CleanFormName(firstName) is { } formName)
        {
            row.FirstName = formName;
            row.NameSource = MarketingNameSource.Form;
            row.NameUpdatedAt = now;
        }

        if (!created && row.Status == MarketingConsentStatus.Withdrawn && !HasOpenResubscribe(row, now))
        {
            // S-08: a withdrawal is never undone by an anonymous request. The row stays Withdrawn with
            // its withdrawal evidence; the request is tracked on its own and in the event log (which
            // carries the requested wording and source), and only the confirm click opts back in.
            row.ResubscribeRequestedAt = now;
            row.UpdatedAt = now;
            AddEvent(row, MarketingConsentAction.Requested, source, evidence, now, wordingVersion);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (created)
        {
            // A concurrent subscribe for the same address created the row first; it sent the mail.
            return MarketingSubscribeOutcome.Accepted;
        }

        string? baseUrl = _options.ResolveBaseUrl(_cloudStorage);
        if (baseUrl is null)
        {
            _logger.LogWarning(
                "Marketing:PublicBaseUrl (and CloudStorage:PublicBaseUrl) is not set, so no marketing confirmation mail was sent");
            return MarketingSubscribeOutcome.Accepted;
        }

        if (!await TryClaimConfirmationMailAsync(row, evidence, now, ct))
        {
            return MarketingSubscribeOutcome.Accepted;
        }

        int ttlDays = Math.Clamp(_options.ConfirmTtlDays, 1, 30);
        string token = _tokens.CreateConfirmToken(row.Id, _clock.GetUtcNow().AddDays(ttlDays));
        _outbox.Enqueue(
            new AccountMail(normalized, ConfirmTemplate, new AccountMailData
            {
                Link = $"{baseUrl}/app/marketing/confirm?token={Uri.EscapeDataString(token)}",
                ExpiresIn = ttlDays == 1 ? "1 day" : $"{ttlDays} days",
            }, SafeLocale(locale)),
            MarketingMailOrigin.Anonymous);
        return MarketingSubscribeOutcome.Accepted;
    }

    /// <summary>
    /// The visitor's language for the confirmation mail. Only a short code-shaped value is passed on;
    /// the Go API allowlists it against the language registry and falls back to English.
    /// </summary>
    private static string? SafeLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale) || locale.Length > 16)
        {
            return null;
        }

        foreach (char c in locale)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return null;
            }
        }

        return locale;
    }

    /// <summary>A Withdrawn row's re-subscribe request made after the withdrawal, still within the pending retention.</summary>
    private bool HasOpenResubscribe(MarketingConsent row, DateTime now) =>
        row.ResubscribeRequestedAt is { } at
        && (row.WithdrawnAt is not { } withdrawn || at > withdrawn)
        && at > now.AddDays(-Math.Max(1, _options.PendingRetentionDays));

    /// <summary>
    /// The one place the anonymous confirmation mail is capped (contracts C5, C6). In one transaction:
    /// <list type="number">
    /// <item>the literal address: a conditional UPDATE claims the row's slot at most once per
    /// <see cref="MarketingOptions.ConfirmMailboxCooldownMinutes"/> (of two concurrent subscribes for
    /// the same address only one sends, S-14; claiming also restarts a Pending row's clock, so the
    /// sweeper cannot delete it while the link just sent is still valid);</item>
    /// <item>one C5 decision over the sliding-window counters, shared by every replica: ip
    /// (<see cref="MarketingOptions.ConfirmPerIpPerHour"/>, IPv6 per /64, the tight first layer),
    /// canonical mailbox (one per cooldown, <see cref="MarketingOptions.ConfirmMailboxPerDay"/> per
    /// 24 h) and the global hard ceiling must all admit the send; the domain and global ALARM
    /// thresholds must too, unless the request is the site's bot-checked forward.</item>
    /// </list>
    /// A refused send increments nothing and the row claim is rolled back, so refused traffic cannot
    /// extend a lockout. A counter store failure refuses the mail (fail closed). Every refusal counts on
    /// <c>reelbolt.marketing.confirm_cap_hit</c>; each alarm and the ceiling warn once per window. The
    /// caller answers the same whatever this decides.
    /// </summary>
    private async Task<bool> TryClaimConfirmationMailAsync(
        MarketingConsent row, MarketingRequestEvidence evidence, DateTime now, CancellationToken ct)
    {
        TimeSpan cooldown = TimeSpan.FromMinutes(_options.EffectiveConfirmMailboxCooldownMinutes);
        DateTime resendBefore = now - cooldown;
        Guid id = row.Id;

        // Disposed without a commit, the transaction rolls the row claim back.
        await using IDbContextTransaction transaction = await _db.Database.BeginTransactionAsync(ct);
        IQueryable<MarketingConsent> due = _db.MarketingConsents
            .Where(c => c.Id == id && (c.LastConfirmationSentAt == null || c.LastConfirmationSentAt <= resendBefore));
        int claimed = row.Status == MarketingConsentStatus.Pending
            ? await due.ExecuteUpdateAsync(s => s
                .SetProperty(c => c.LastConfirmationSentAt, now)
                .SetProperty(c => c.RequestedAt, now)
                .SetProperty(c => c.UpdatedAt, now), ct)
            : await due.ExecuteUpdateAsync(s => s
                .SetProperty(c => c.LastConfirmationSentAt, now)
                .SetProperty(c => c.UpdatedAt, now), ct);
        if (claimed != 1)
        {
            CountCapHit(MailboxCap);
            return false;
        }

        string canonical = MarketingEmail.CanonicalMailbox(row.Email);
        TimeSpan hour = TimeSpan.FromHours(1);
        TimeSpan day = TimeSpan.FromDays(1);
        MailRateLimit[] limits =
        [
            new(IpCap, MailRateBuckets.ConfirmIp(evidence.Ip), hour, _options.EffectiveConfirmPerIpPerHour),
            new(MailboxCap, MailRateBuckets.ConfirmMailboxCooldown(canonical), cooldown, 1),
            new(MailboxCap, MailRateBuckets.ConfirmMailboxDay(canonical), day, _options.EffectiveConfirmMailboxPerDay),
            new(CeilingCap, MailRateBuckets.ConfirmGlobal, day, _options.EffectiveConfirmGlobalHardCeilingPerDay),
            new(DomainCap, MailRateBuckets.ConfirmDomain(MarketingEmail.DomainOf(canonical)), hour,
                _options.EffectiveConfirmPerDomainPerHour, Alarm: true),
            new(GlobalCap, MailRateBuckets.ConfirmGlobal, day, _options.EffectiveConfirmGlobalPerDay, Alarm: true),
        ];

        MailRateDecision decision;
        try
        {
            decision = await _counters.DecideAsync(
                limits, new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)),
                estimates => estimates.All(e => e.Admits || (e.Limit.Alarm && evidence.SiteVerified)), ct);
            if (decision.Allowed)
            {
                await transaction.CommitAsync(ct);
            }
            else
            {
                // Undo the row claim now (N-12): the alarm marker ReportAsync writes below must not
                // join this transaction, or it is rolled back with the claim and the once-per-window
                // warning repeats on every refused request.
                await transaction.RollbackAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                "The mail rate counters are unavailable, so no marketing confirmation mail was sent: {Error}",
                MailLogRef.DescribeError(ex));
            return false;
        }

        // Outside the claim transaction (committed or rolled back above), so the marker always persists.
        await ReportAsync(decision, ct);
        return decision.Allowed;
    }

    /// <summary>Meters and once-per-window warnings for one decision. Never fails the request.</summary>
    private async Task ReportAsync(MailRateDecision decision, CancellationToken ct)
    {
        if (!decision.Allowed
            && (decision.Estimates.FirstOrDefault(e => !e.Admits && !e.Limit.Alarm)
                ?? decision.Estimates.FirstOrDefault(e => !e.Admits)) is { } refusing)
        {
            CountCapHit(refusing.Limit.Cap);
        }

        foreach (MailRateEstimate over in decision.Estimates.Where(e => !e.Admits && (e.Limit.Alarm || e.Limit.Cap == CeilingCap)))
        {
            MailRateLimit limit = over.Limit;
            if (limit.Alarm)
            {
                MarketingDiagnostics.ConfirmAlarm.Add(1,
                    new KeyValuePair<string, object?>("alarm", limit.Cap),
                    new KeyValuePair<string, object?>("outcome", decision.Allowed ? "sent" : "refused"));
            }

            bool first;
            try
            {
                first = await _counters.TryMarkOnceAsync(MailRateBuckets.AlarmMarker(limit.Cap + ":" + limit.Bucket), over.WindowStart, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug("Could not record the {Cap} alarm marker: {Error}", limit.Cap, MailLogRef.DescribeError(ex));
                continue;
            }

            if (!first)
            {
                continue;
            }

            switch (limit.Cap)
            {
                case CeilingCap:
                    _logger.LogError(GlobalHardCeilingReached,
                        "The hard ceiling of {Limit} marketing confirmation mails per 24 h was reached (window starting {WindowStart:o}); "
                        + "no confirmation mail is sent to anyone until the sliding window falls below it",
                        limit.Limit, over.WindowStart);
                    break;
                case DomainCap:
                    _logger.LogWarning(DomainAlarmReached,
                        "Marketing confirmation requests for one recipient domain passed the alarm threshold of {Limit} per hour "
                        + "(window starting {WindowStart:o}); until it falls back only the site's bot-checked forwards get a mail. Domain: {Domain}",
                        limit.Limit, over.WindowStart, limit.Bucket["confirm:domain:".Length..]);
                    break;
                default:
                    _logger.LogWarning(GlobalCapReached,
                        "Marketing confirmation requests passed the global alarm threshold of {Limit} per 24 h (window starting {WindowStart:o}); "
                        + "until it falls back only the site's bot-checked forwards get a mail",
                        limit.Limit, over.WindowStart);
                    break;
            }
        }

    }

    private const string MailboxCap = "mailbox";
    private const string IpCap = "ip";
    private const string CeilingCap = "ceiling";
    private const string DomainCap = "domain";
    private const string GlobalCap = "global";

    /// <summary>The Warning raised once per window when the global alarm threshold is passed.</summary>
    public static readonly EventId GlobalCapReached = new(41_601, "MarketingConfirmGlobalCapReached");

    /// <summary>The Warning raised once per window and domain when the domain alarm threshold is passed.</summary>
    public static readonly EventId DomainAlarmReached = new(41_602, "MarketingConfirmDomainAlarmReached");

    /// <summary>The Error (alert) raised once per window when the global hard ceiling refuses every mail.</summary>
    public static readonly EventId GlobalHardCeilingReached = new(41_603, "MarketingConfirmGlobalHardCeilingReached");

    private static void CountCapHit(string cap) =>
        MarketingDiagnostics.ConfirmCapHit.Add(1, new KeyValuePair<string, object?>("cap", cap));

    /// <summary>
    /// The double-opt-in click (a POST from the confirm page, so link scanners cannot confirm). Sets
    /// <c>OptedIn</c> with <c>ConfirmedAt</c> and <c>ConsentedAt</c> now and the click's evidence.
    /// A Withdrawn row is revived only when its address asked to subscribe again after withdrawing
    /// (<see cref="MarketingConsent.ResubscribeRequestedAt"/>), under the wording and source of that
    /// request; otherwise an old link cannot undo the withdrawal.
    /// </summary>
    public async Task<MarketingTokenStatus> ConfirmAsync(string? token, MarketingRequestEvidence evidence, CancellationToken ct)
    {
        MarketingTokenResult result = _tokens.VerifyConfirm(token, _clock.GetUtcNow());
        if (result.Status != MarketingTokenStatus.Valid)
        {
            return result.Status;
        }

        MarketingConsent? row = await _db.MarketingConsents.FirstOrDefaultAsync(c => c.Id == result.ConsentId, ct);
        DateTime now = Now;
        if (row is null || (row.Status == MarketingConsentStatus.Withdrawn && !HasOpenResubscribe(row, now)))
        {
            return MarketingTokenStatus.Invalid;
        }

        if (row.Status == MarketingConsentStatus.OptedIn)
        {
            if (row.ConfirmedAt is null)
            {
                row.ConfirmedAt = now;
                Stamp(row, evidence, now);
                AddEvent(row, MarketingConsentAction.Confirmed, row.Source, evidence, now);
                await ClearSuppressionAsync(row.Email, ct);
                await _db.SaveChangesAsync(ct);
            }

            return MarketingTokenStatus.Valid;
        }

        if (row.Status == MarketingConsentStatus.Withdrawn)
        {
            // The request that re-opened the row carries what the person agreed to.
            Guid id = row.Id;
            DateTime since = row.ResubscribeRequestedAt!.Value;
            MarketingConsentEvent? request = await _db.MarketingConsentEvents.AsNoTracking()
                .Where(e => e.ConsentId == id && e.Action == MarketingConsentAction.Requested && e.OccurredAt >= since)
                .OrderByDescending(e => e.OccurredAt)
                .FirstOrDefaultAsync(ct);
            if (request is null)
            {
                return MarketingTokenStatus.Invalid;
            }

            row.WordingVersion = request.WordingVersion;
            row.Source = request.Source;
            row.ResubscribeRequestedAt = null;
        }

        row.Status = MarketingConsentStatus.OptedIn;
        row.ConsentedAt = now;
        row.ConfirmedAt = now;
        row.WithdrawnAt = null;
        Stamp(row, evidence, now);
        AddEvent(row, MarketingConsentAction.Confirmed, row.Source, evidence, now);
        await ClearSuppressionAsync(row.Email, ct);
        await _db.SaveChangesAsync(ct);
        return MarketingTokenStatus.Valid;
    }

    /// <summary>
    /// One-click withdrawal. True for every validly signed token — including the token of a row that
    /// no longer exists, so the answer is no list-membership oracle; false only for an invalid token.
    /// </summary>
    public async Task<bool> UnsubscribeAsync(string? token, MarketingRequestEvidence evidence, CancellationToken ct)
    {
        MarketingTokenResult result = _tokens.VerifyUnsubscribe(token);
        if (result.Status != MarketingTokenStatus.Valid)
        {
            return false;
        }

        MarketingConsent? row = await _db.MarketingConsents.FirstOrDefaultAsync(c => c.Id == result.ConsentId, ct);
        if (row is null)
        {
            return true;
        }

        if (row.Status == MarketingConsentStatus.Withdrawn)
        {
            if (row.ResubscribeRequestedAt is not null)
            {
                row.ResubscribeRequestedAt = null;
                await _db.SaveChangesAsync(ct);
            }

            return true;
        }

        DateTime now = Now;
        row.Status = MarketingConsentStatus.Withdrawn;
        ClearNames(row, now);
        row.WithdrawnAt = now;
        row.Source = MarketingConsentSource.Unsubscribe;
        Stamp(row, evidence, now);
        AddEvent(row, MarketingConsentAction.Withdrawn, MarketingConsentSource.Unsubscribe, evidence, now);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Platform admin
    // ---------------------------------------------------------------------------------------------

    public async Task<MarketingAdminSummary> GetAdminSummaryAsync(CancellationToken ct)
    {
        Dictionary<MarketingConsentStatus, int> counts = await _db.MarketingConsents.AsNoTracking()
            .GroupBy(c => c.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);
        int exportable = (await ExportableRowsAsync(ct)).Count;

        MarketingConsentExport? last = await _db.MarketingConsentExports.AsNoTracking()
            .OrderByDescending(e => e.ExportedAt)
            .FirstOrDefaultAsync(ct);

        return new MarketingAdminSummary(
            new MarketingAdminCounts(
                counts.GetValueOrDefault(MarketingConsentStatus.Pending),
                counts.GetValueOrDefault(MarketingConsentStatus.OptedIn),
                counts.GetValueOrDefault(MarketingConsentStatus.Withdrawn),
                exportable),
            last is null ? null : new MarketingLastExport(Utc(last.ExportedAt)!.Value, last.RowCount));
    }

    /// <summary>
    /// The sendable list: <c>OptedIn</c>, confirmed, and either accountless or on a verified account,
    /// minus suppressed emails. Writes a <c>marketing_consent_exports</c> row; keeps no copy. Null when
    /// no public base URL is configured (the unsubscribe links could not be built).
    /// </summary>
    public async Task<MarketingCsvExport?> ExportCsvAsync(Guid exportedByUserId, CancellationToken ct)
    {
        string? baseUrl = _options.ResolveBaseUrl(_cloudStorage);
        if (baseUrl is null)
        {
            return null;
        }

        List<MarketingConsent> rows = await ExportableRowsAsync(ct);
        StringBuilder csv = new();
        csv.Append(MarketingCsv.Header).Append("\r\n");
        foreach (MarketingConsent row in rows)
        {
            string unsubscribe = $"{baseUrl}/app/unsubscribe?token={Uri.EscapeDataString(_tokens.CreateUnsubscribeToken(row.Id))}";
            csv.AppendJoin(',',
                    MarketingCsv.Cell(row.Email),
                    MarketingCsv.Cell(row.UserId?.ToString()),
                    MarketingCsv.Cell(MarketingCsv.Date(row.ConsentedAt)),
                    MarketingCsv.Cell(MarketingCsv.Date(row.ConfirmedAt)),
                    MarketingCsv.Cell(row.WordingVersion),
                    MarketingCsv.Cell(SourceName(row.Source)),
                    MarketingCsv.Cell(unsubscribe))
                .Append("\r\n");
        }

        DateTime now = Now;
        _db.MarketingConsentExports.Add(new MarketingConsentExport
        {
            Id = Guid.NewGuid(),
            ExportedByUserId = exportedByUserId,
            ExportedAt = now,
            RowCount = rows.Count,
        });
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Platform admin {UserId} exported {RowCount} marketing consent row(s)", exportedByUserId, rows.Count);
        return new MarketingCsvExport(
            Encoding.UTF8.GetBytes(csv.ToString()),
            $"marketing-consents-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.csv",
            rows.Count);
    }

    /// <summary>The sendable set (same rules as the export), re-read now. Used by campaign dry-run only.</summary>
    public async Task<(IReadOnlyList<(Guid ConsentId, string Email)> Recipients, int SuppressedCount)> SendableRecipientsAsync(CancellationToken ct)
    {
        (List<MarketingConsent> rows, int suppressed) = await ExportableRowsWithSuppressedAsync(ct);
        return (rows.Select(r => (r.Id, r.Email)).ToList(), suppressed);
    }

    /// <summary>
    /// The sendable rule, before suppression: <c>OptedIn</c>, confirmed, not undeliverable, and either accountless or on a
    /// verified, non-service account. Shared by the export, the campaign dry run and the first-run reminder, so they cannot drift.
    /// The caller still applies the <c>marketing_suppressions</c> check.
    /// </summary>
    public static IQueryable<MarketingConsent> SendableRows(InferenceApiDbContext db)
        => db.MarketingConsents.AsNoTracking()
            .Where(c => c.Status == MarketingConsentStatus.OptedIn && c.ConfirmedAt != null && c.UndeliverableAt == null)
            .Where(c => c.UserId == null || db.ApplicationUsers.Any(u => u.Id == c.UserId && u.EmailVerifiedAt != null && !u.IsServiceAccount));

    private async Task<List<MarketingConsent>> ExportableRowsAsync(CancellationToken ct)
        => (await ExportableRowsWithSuppressedAsync(ct)).Rows;

    private async Task<(List<MarketingConsent> Rows, int Suppressed)> ExportableRowsWithSuppressedAsync(CancellationToken ct)
    {
        List<MarketingConsent> candidates = await SendableRows(_db)
            .OrderBy(c => c.Email)
            .ToListAsync(ct);
        if (candidates.Count == 0)
        {
            return (candidates, 0);
        }

        HashSet<string> suppressed = (await _db.MarketingSuppressions.AsNoTracking()
            .Select(s => s.EmailHmac)
            .ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        if (suppressed.Count == 0)
        {
            return (candidates, 0);
        }

        List<MarketingConsent> sendable = candidates.Where(c => !suppressed.Contains(_suppression.Hash(c.Email))).ToList();
        return (sendable, candidates.Count - sendable.Count);
    }

    // ---------------------------------------------------------------------------------------------

    private async Task ClearSuppressionAsync(string email, CancellationToken ct)
    {
        string hash = _suppression.Hash(email);
        List<MarketingSuppression> entries = await _db.MarketingSuppressions.Where(s => s.EmailHmac == hash).ToListAsync(ct);
        _db.MarketingSuppressions.RemoveRange(entries);

        // A confirmed opt-in is a fresh address check: an earlier undeliverable mark no longer holds.
        MarketingConsent? row = await _db.MarketingConsents.FirstOrDefaultAsync(c => c.Email == email, ct);
        if (row is { UndeliverableAt: not null })
        {
            row.UndeliverableAt = null;
            row.UndeliverableReason = null;
        }
    }

    /// <summary>
    /// Withdraws consent because the provider (Brevo) reported the person's own unsubscribe or complaint.
    /// False when there is no such row or it is already withdrawn. Never adds sending.
    /// </summary>
    public async Task<bool> WithdrawFromProviderAsync(string email, MarketingConsentSource source, CancellationToken ct)
    {
        string normalized = MarketingEmail.Normalize(email);
        MarketingConsent? row = await _db.MarketingConsents.FirstOrDefaultAsync(c => c.Email == normalized, ct);
        if (row is null || row.Status == MarketingConsentStatus.Withdrawn)
        {
            return false;
        }

        DateTime now = Now;
        MarketingRequestEvidence evidence = new(null, null);
        row.Status = MarketingConsentStatus.Withdrawn;
        ClearNames(row, now);
        row.WithdrawnAt = now;
        row.Source = source;
        Stamp(row, evidence, now);
        AddEvent(row, MarketingConsentAction.Withdrawn, source, evidence, now);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Data minimisation: a withdrawn contact keeps no name. Cleared in the same save as the withdrawal, whatever the Brevo,
    /// campaign or sync switches say; the name source stays. Never a re-opt-in resurrects an old name.
    /// </summary>
    private static void ClearNames(MarketingConsent row, DateTime now)
    {
        if (row.FirstName is null && row.LastName is null)
        {
            return;
        }

        row.FirstName = null;
        row.LastName = null;
        row.NameUpdatedAt = now;
    }

    /// <summary>Marks an address undeliverable (leaves the sendable set); consent is untouched. False when no such row.</summary>
    public async Task<bool> MarkUndeliverableAsync(string email, string reason, CancellationToken ct)
    {
        string normalized = MarketingEmail.Normalize(email);
        MarketingConsent? row = await _db.MarketingConsents.FirstOrDefaultAsync(c => c.Email == normalized, ct);
        if (row is null)
        {
            return false;
        }

        if (row.UndeliverableAt is not null)
        {
            return true; // already marked: a repeat writes nothing
        }

        row.UndeliverableAt = Now;
        row.UndeliverableReason = reason.Length > 100 ? reason[..100] : reason;
        row.UpdatedAt = Now;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static void Stamp(MarketingConsent row, MarketingRequestEvidence evidence, DateTime now)
    {
        row.Ip = MarketingRequestEvidence.Truncate(evidence.Ip, MarketingRequestEvidence.MaxIpLength);
        row.UserAgent = MarketingRequestEvidence.Truncate(evidence.UserAgent, MarketingRequestEvidence.MaxUserAgentLength);
        row.UpdatedAt = now;
    }

    private void AddEvent(
        MarketingConsent row,
        MarketingConsentAction action,
        MarketingConsentSource source,
        MarketingRequestEvidence evidence,
        DateTime now,
        string? wordingVersion = null)
    {
        _db.MarketingConsentEvents.Add(new MarketingConsentEvent
        {
            Id = Guid.NewGuid(),
            ConsentId = row.Id,
            Email = row.Email,
            UserId = row.UserId,
            Action = action,
            Source = source,
            WordingVersion = wordingVersion ?? row.WordingVersion,
            Ip = MarketingRequestEvidence.Truncate(evidence.Ip, MarketingRequestEvidence.MaxIpLength),
            UserAgent = MarketingRequestEvidence.Truncate(evidence.UserAgent, MarketingRequestEvidence.MaxUserAgentLength),
            OccurredAt = now,
        });
    }
}

/// <summary>CSV cell rules for the consent export.</summary>
public static class MarketingCsv
{
    public const string Header = "email,user_id,consented_at,confirmed_at,wording_version,source,unsubscribe_url";

    /// <summary>
    /// One cell. A value starting with <c>\t</c> or <c>\r</c>, or whose first character after any
    /// leading whitespace and quotes is <c>= + - @</c>, gets a <c>'</c> prefix so a spreadsheet never
    /// evaluates it as a formula (spreadsheets skip leading blanks and quotes, S-27); a value holding
    /// a comma, quote or line break is quoted.
    /// </summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '\t' or '\r' || FirstSignificant(value) is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    private static char? FirstSignificant(string value)
    {
        foreach (char c in value)
        {
            if (!char.IsWhiteSpace(c) && c is not '"' and not '\'')
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>ISO 8601 UTC to the second, or empty.</summary>
    public static string? Date(DateTime? value) => value is { } d
        ? DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
        : null;
}

/// <summary>
/// The marketing half of an account purge: delete the person's consent rows (by account id, or by
/// address when the account verified it) and events, and keep a
/// keyed HMAC suppression entry for an address that had opted in or withdrawn, so a re-import can
/// never mail it again. Pending and never-opted-in rows are deleted outright. Staged on the caller's
/// context, so it commits in the purge's single <c>SaveChanges</c>.
/// </summary>
public static class MarketingConsentErasure
{
    public static async Task StageAsync(
        InferenceApiDbContext db, ApplicationUser user, IMarketingSuppressionHasher? hasher, DateTime nowUtc, CancellationToken ct,
        ISecretProtector? protector = null)
    {
        // By address only when the account verified it: purging an unverified account must not erase
        // the consent of whoever subscribed with the address it typed (S-02).
        string? email = user.EmailVerifiedAt is null ? null : MarketingEmail.Normalize(user.Email);
        Guid userId = user.Id;
        // On Postgres the read takes FOR UPDATE (the purge runs in one transaction): the campaign pipeline's write-ahead
        // flag update then either waits for this purge and sees the row gone, or this read sees the flag and stages the
        // erasure. Other providers (the Sqlite and InMemory test providers) have no row lock and skip it.
        List<MarketingConsent> rows = db.Database.IsNpgsql()
            ? await db.MarketingConsents
                .FromSqlInterpolated($"SELECT * FROM marketing_consents WHERE user_id = {userId} OR (email = {email}) FOR UPDATE")
                .ToListAsync(ct)
            : await db.MarketingConsents
                .Where(c => c.UserId == userId || (email != null && c.Email == email))
                .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return;
        }

        Guid[] ids = rows.Select(r => r.Id).ToArray();
        db.MarketingConsentEvents.RemoveRange(await db.MarketingConsentEvents.Where(e => ids.Contains(e.ConsentId)).ToListAsync(ct));

        foreach (MarketingConsent row in rows)
        {
            if (row.BrevoSyncedAt is not null)
            {
                if (protector is null)
                {
                    // Without the outbox row the contact would stay at Brevo: refuse, so the purge defers and retries.
                    throw new InvalidOperationException("The secret protector is not available.");
                }

                // Protect throws on failure, which fails the purge transaction like the suppression hasher.
                db.MarketingBrevoErasures.Add(new MarketingBrevoErasure
                {
                    Id = Guid.NewGuid(),
                    ProtectedEmail = protector.Protect(row.Email),
                    CreatedAt = nowUtc,
                });
            }

            if (row.Status is MarketingConsentStatus.OptedIn or MarketingConsentStatus.Withdrawn)
            {
                if (hasher is null)
                {
                    // Deleting the row without its suppression entry would let the address be
                    // re-imported and mailed: refuse, so the purge defers and retries.
                    throw new InvalidOperationException("The marketing suppression hasher is not available.");
                }

                string hash = hasher.Hash(row.Email);
                DateTime withdrawnAt = row.WithdrawnAt ?? nowUtc;
                MarketingSuppression? existing = db.MarketingSuppressions.Local.FirstOrDefault(s => s.EmailHmac == hash)
                    ?? await db.MarketingSuppressions.FirstOrDefaultAsync(s => s.EmailHmac == hash, ct);
                if (existing is null)
                {
                    db.MarketingSuppressions.Add(new MarketingSuppression
                    {
                        Id = Guid.NewGuid(),
                        EmailHmac = hash,
                        WithdrawnAt = withdrawnAt,
                        CreatedAt = nowUtc,
                    });
                }
                else
                {
                    existing.WithdrawnAt = withdrawnAt;
                }
            }

            db.MarketingConsents.Remove(row);
        }
    }
}
