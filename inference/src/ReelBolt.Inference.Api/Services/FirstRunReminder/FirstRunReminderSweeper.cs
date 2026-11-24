using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.FirstRunReminder;

/// <summary>What one pass did. Counts only: nothing here names a person.</summary>
public sealed record FirstRunReminderPassResult(
    bool Ran,
    int Examined,
    int Eligible,
    int WouldSend,
    int Claimed,
    int Sent,
    int Failed,
    bool CapReached = false,
    bool QuietHours = false,
    bool IdentityMissing = false,
    string? SenderNotReady = null);

/// <summary>
/// Finds users whose FIRST project is 1 to 2 hours old, who have not run anything, and sends each at most one
/// reminder, ever. Off by default (<c>FirstRunReminder:Enabled</c>), and with <c>DryRun</c> (default true) it only counts.
/// The row in <c>first_run_reminders</c> is claimed before the send, so a crash or a second replica can never send twice.
/// Logs counts and <c>rcpt=</c> refs only. See docs/design/first-run-reminder-mail.md.
/// </summary>
public sealed class FirstRunReminderSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<FirstRunReminderOptions> _options;
    private readonly ILogger<FirstRunReminderSweeper> _logger;
    private readonly TimeProvider _clock;

    public FirstRunReminderSweeper(
        IServiceScopeFactory scopes,
        IOptions<FirstRunReminderOptions> options,
        ILogger<FirstRunReminderSweeper> logger,
        TimeProvider? clock = null)
    {
        _scopes = scopes;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            return;
        }

        try
        {
            // Let the API finish migrating first.
            await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
            using PeriodicTimer timer = new(TimeSpan.FromMinutes(Math.Clamp(_options.Value.PollIntervalMinutes, 1, 60)));
            do
            {
                try
                {
                    FirstRunReminderPassResult r = await SweepOnceAsync(_clock.GetUtcNow().UtcDateTime, stoppingToken);
                    if (r.Ran && (r.Examined > 0 || r.Claimed > 0))
                    {
                        _logger.LogInformation(
                            "First-run reminder pass dryRun={DryRun} examined={Examined} eligible={Eligible} wouldSend={WouldSend} sent={Sent} failed={Failed} capReached={Cap}",
                            _options.Value.DryRun, r.Examined, r.Eligible, r.WouldSend, r.Sent, r.Failed, r.CapReached);
                    }

                    if (r.SenderNotReady is not null)
                    {
                        _logger.LogWarning("First-run reminder is on but the sender is not ready ({Reason}); nothing is claimed or sent", r.SenderNotReady);
                    }

                    if (r.IdentityMissing)
                    {
                        _logger.LogWarning("First-run reminder is on but the controller name, postal address or public base URL is not configured; nothing is sent");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError("The first-run reminder pass failed; it is retried on the next run: {Error}", MailLogRef.DescribeError(ex));
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>One pass at <paramref name="now"/> (UTC). Flag off: returns at once, touches nothing.</summary>
    public async Task<FirstRunReminderPassResult> SweepOnceAsync(DateTime now, CancellationToken ct)
    {
        FirstRunReminderOptions opt = _options.Value;
        if (!opt.Enabled)
        {
            return new FirstRunReminderPassResult(false, 0, 0, 0, 0, 0, 0);
        }

        if (opt.ConsentMode == FirstRunReminderConsentMode.ServiceMessage && !opt.ServiceMessageCounselReviewed)
        {
            return new FirstRunReminderPassResult(true, 0, 0, 0, 0, 0, 0, SenderNotReady: "service_message_not_approved");
        }

        if (InQuietHours(now, opt))
        {
            return new FirstRunReminderPassResult(true, 0, 0, 0, 0, 0, 0, QuietHours: true);
        }

        int minAge = Math.Max(1, opt.MinAgeMinutes);
        int maxAge = Math.Max(minAge, opt.MaxAgeMinutes);
        DateTime newest = now.AddMinutes(-minAge);
        DateTime oldest = now.AddMinutes(-maxAge);
        int batch = Math.Clamp(opt.BatchSize, 1, 500);

        using IServiceScope scope = _scopes.CreateScope();
        // A hosted service has no organization: without the system scope the tenant filter on projects matches nothing.
        using IDisposable? systemScope = scope.ServiceProvider.GetService<ITenantContext>()?.BeginSystemScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

        // Projects created inside the window, oldest first, read page by page: owners who turn out ineligible (admin,
        // already reminded, not opted in) must not starve the eligible ones behind them. One candidate per owner.
        const int PageSize = 200;
        const int MaxPages = 100;
        async IAsyncEnumerable<(Guid Id, Guid OwnerId, DateTime CreatedAt)> CandidatesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            HashSet<Guid> seen = [];
            for (int page = 0; page < MaxPages; page++)
            {
                var rows = await db.Projects.AsNoTracking()
                    .Where(p => p.CreatedAt >= oldest && p.CreatedAt <= newest)
                    .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)
                    .Select(p => new { p.Id, p.OwnerId, p.CreatedAt })
                    .Skip(page * PageSize).Take(PageSize)
                    .ToListAsync(token);
                foreach (var row in rows)
                {
                    if (seen.Add(row.OwnerId))
                    {
                        yield return (row.Id, row.OwnerId, row.CreatedAt);
                    }
                }

                if (rows.Count < PageSize)
                {
                    yield break;
                }
            }
        }

        DateTime dayStart = now.Date;
        int claimedToday = await db.FirstRunReminders.CountAsync(r => r.ClaimedAt >= dayStart, ct);
        int cap = Math.Max(0, opt.DailyCap);

        string? baseUrl = null;
        FirstRunReminderIdentity? identity = null;
        bool identityMissing = false;
        IFirstRunReminderSender? sender = null;
        string? notReady = null;
        if (!opt.DryRun)
        {
            ResolveSendConfig(scope.ServiceProvider, out baseUrl, out identity);
            identityMissing = baseUrl is null || identity is null;
            sender = scope.ServiceProvider.GetRequiredService<IFirstRunReminderSender>();
            // Before any claim: a missing key must not burn anyone's one reminder.
            notReady = sender.NotReadyReason;
        }

        IMarketingSuppressionHasher? suppression = scope.ServiceProvider.GetService<IMarketingSuppressionHasher>();

        MailLogRef logRef = scope.ServiceProvider.GetService<MailLogRef>() ?? MailLogRef.Ephemeral();
        IMarketingLinkTokens? tokens = scope.ServiceProvider.GetService<IMarketingLinkTokens>();
        HashSet<string> excluded = opt.ExcludedEmailDomains
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Trim().TrimStart('@').ToLowerInvariant())
            .ToHashSet();

        int examined = 0, eligible = 0, wouldSend = 0, claimed = 0, sent = 0, failed = 0;
        bool capReached = false;
        await foreach (var candidate in CandidatesAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (eligible >= batch)
            {
                break;
            }

            examined++;
            Guid ownerId = candidate.OwnerId;
            DateTime firstCreated = candidate.CreatedAt;

            var user = await db.ApplicationUsers.AsNoTracking()
                .Where(u => u.Id == ownerId
                    && u.EmailVerifiedAt != null
                    && u.DeletionRequestedAt == null
                    && !u.IsAdmin
                    && !u.IsServiceAccount
                    && !db.OrganizationMemberships.Any(m => m.UserId == u.Id && m.Organization.Kind == OrganizationKind.Platform)
                    && !db.FirstRunReminders.Any(r => r.UserId == u.Id)
                    // Not the first project: an older one exists, so the user is past their first run.
                    && !db.Projects.Any(p => p.OwnerId == u.Id && p.CreatedAt < firstCreated)
                    && !db.WorkflowExecutions.Any(e => e.Project.OwnerId == u.Id))
                .Select(u => new { u.Id, u.Email, u.Locale })
                .FirstOrDefaultAsync(ct);
            if (user is null)
            {
                continue;
            }

            string email = MarketingEmail.Normalize(user.Email);
            if (!MarketingEmail.IsValid(email) || excluded.Contains(MarketingEmail.DomainOf(email)))
            {
                continue;
            }

            if (await ConsentVerdictAsync(db, suppression, email, opt.ConsentMode, ct) is null)
            {
                continue;
            }

            eligible++;
            if (claimedToday + claimed + wouldSend >= cap)
            {
                capReached = true;
                break;
            }

            if (opt.DryRun)
            {
                wouldSend++;
                continue;
            }

            if (identityMissing || notReady is not null || sender is null)
            {
                continue;
            }

            string locale = FirstRunReminderMail.NormalizeLocale(user.Locale);
            FirstRunReminderRecord record = new()
            {
                UserId = user.Id,
                State = FirstRunReminderStates.Claimed,
                ClaimedAt = now,
                Locale = locale,
                ConsentMode = opt.ConsentMode.ToString(),
            };
            db.FirstRunReminders.Add(record);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Another replica claimed this user first.
                db.Entry(record).State = EntityState.Detached;
                continue;
            }

            claimed++;

            // Re-read after the claim: a withdrawal that landed in between must win, and the unsubscribe link needs the row.
            Guid? consentId = await ConsentVerdictAsync(db, suppression, email, opt.ConsentMode, ct);
            if (consentId is null)
            {
                record.State = FirstRunReminderStates.OptedOut;
                await db.SaveChangesAsync(ct);
                continue;
            }

            string? unsubscribe = tokens is null
                ? null
                : $"{baseUrl}/app/unsubscribe?token={Uri.EscapeDataString(tokens.CreateUnsubscribeToken(consentId.Value))}";
            if (unsubscribe is null)
            {
                // No working opt-out path: never send, and never leave the user claimed as Sent.
                record.State = FirstRunReminderStates.Failed;
                record.ErrorCode = "no_opt_out_path";
                await db.SaveChangesAsync(ct);
                failed++;
                continue;
            }

            string runUrl = $"{baseUrl}/app/projects/{candidate.Id}/workflows";
            FirstRunReminderMessage message = FirstRunReminderMail.Build(email, logRef.For(email), locale, runUrl, identity!, unsubscribe);
            FirstRunReminderSendResult result = await sender.SendAsync(message, ct);
            if (result.Outcome == FirstRunReminderSendOutcome.Sent)
            {
                record.State = FirstRunReminderStates.Sent;
                record.SentAt = _clock.GetUtcNow().UtcDateTime;
                sent++;
            }
            else
            {
                record.State = FirstRunReminderStates.Failed;
                record.ErrorCode = result.ErrorCode ?? "not_sent";
                failed++;
            }

            await db.SaveChangesAsync(ct);
        }

        return new FirstRunReminderPassResult(true, examined, eligible, wouldSend, claimed, sent, failed, capReached, false, identityMissing, notReady);
    }

    /// <summary>
    /// The consent row id when this address may get the reminder, else null. Every mode: not Withdrawn, not suppressed
    /// (erased address), and a row must exist so an unsubscribe link can be built. <c>RequireMarketingOptIn</c>
    /// additionally needs the campaign sendable rule (<see cref="MarketingConsentService.SendableRows"/>).
    /// </summary>
    private static async Task<Guid?> ConsentVerdictAsync(
        InferenceApiDbContext db,
        IMarketingSuppressionHasher? suppression,
        string email,
        FirstRunReminderConsentMode mode,
        CancellationToken ct)
    {
        var row = await db.MarketingConsents.AsNoTracking()
            .Where(c => c.Email == email)
            .Select(c => new { c.Id, c.Status })
            .FirstOrDefaultAsync(ct);
        if (row is null || row.Status == MarketingConsentStatus.Withdrawn)
        {
            return null;
        }

        if (suppression is not null)
        {
            string hash = suppression.Hash(email);
            if (await db.MarketingSuppressions.AnyAsync(x => x.EmailHmac == hash, ct))
            {
                return null;
            }
        }

        if (mode == FirstRunReminderConsentMode.RequireMarketingOptIn
            && !await MarketingConsentService.SendableRows(db).AnyAsync(c => c.Id == row.Id, ct))
        {
            return null;
        }

        return row.Id;
    }

    /// <summary>True when <paramref name="utcNow"/> falls in the quiet hours of the configured zone.</summary>
    public static bool InQuietHours(DateTime utcNow, FirstRunReminderOptions opt)
    {
        int start = opt.QuietHoursStartHour, end = opt.QuietHoursEndHour;
        if (start == end)
        {
            return false;
        }

        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(opt.QuietHoursTimeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
        }

        int hour = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone).Hour;
        return start < end ? hour >= start && hour < end : hour >= start || hour < end;
    }

    private static void ResolveSendConfig(IServiceProvider sp, out string? baseUrl, out FirstRunReminderIdentity? identity)
    {
        baseUrl = sp.GetRequiredService<IOptions<MarketingOptions>>().Value
            .ResolveBaseUrl(sp.GetRequiredService<IOptions<CloudStorageOptions>>().Value);
        MarketingCampaignOptions c = sp.GetRequiredService<IOptions<MarketingCampaignOptions>>().Value;
        identity = string.IsNullOrWhiteSpace(c.FooterControllerName) || string.IsNullOrWhiteSpace(c.FooterAddress)
            ? null
            : new FirstRunReminderIdentity(c.FooterControllerName, c.FooterVatNumber, c.FooterAddress);
    }
}
