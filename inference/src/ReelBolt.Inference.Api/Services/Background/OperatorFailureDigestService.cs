using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Account;
using ReelBolt.Inference.Api.Services.Activity;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems.Importers;

namespace ReelBolt.Inference.Api.Services.Background;

/// <summary>
/// Tunables of the daily operator digest (config section <c>Operations:FailureDigest</c>). Off unless
/// <see cref="Enabled"/>; when off the service neither queries nor sends. Env: <c>OPERATOR_DIGEST_ENABLED</c>,
/// <c>OPERATOR_DIGEST_EMAIL</c>.
/// </summary>
public sealed record FailureDigestOptions
{
    public const string SectionName = "Operations:FailureDigest";

    /// <summary>Send the digest at all. Default false.</summary>
    public bool Enabled { get; init; }

    /// <summary>The operator's address. Required when enabled; also the only address the Go relay accepts for this mail.</summary>
    public string? RecipientEmail { get; init; }

    /// <summary>UTC hour of day from which the day's digest is due (0-23).</summary>
    public int HourUtc { get; init; } = 6;

    /// <summary>How far back the digest looks, in hours (1-168).</summary>
    public int LookbackHours { get; init; } = 24;

    /// <summary>Most failure lines and most feedback lines the mail lists (1-200).</summary>
    public int MaxRows { get; init; } = 50;

    /// <summary>Send a mail even when there are no failures and no feedback in the window. Default false.</summary>
    public bool SendWhenEmpty { get; init; }

    /// <summary>How often the service checks whether the day's digest is due, in minutes.</summary>
    public int PollMinutes { get; init; } = 15;

    /// <summary>Wait before the first check, so a restarting service does not act the moment it boots.</summary>
    public int InitialDelaySeconds { get; init; } = 60;
}

/// <summary>Startup validation: an enabled digest with no usable recipient (or silly bounds) stops the host.</summary>
public sealed class FailureDigestOptionsValidator : IValidateOptions<FailureDigestOptions>
{
    public ValidateOptionsResult Validate(string? name, FailureDigestOptions o)
    {
        if (!o.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> errors = [];
        // The address itself is never echoed: it is personal data and this message reaches the log.
        if (string.IsNullOrWhiteSpace(o.RecipientEmail) || !System.Net.Mail.MailAddress.TryCreate(o.RecipientEmail.Trim(), out var parsed)
            || parsed.Address != o.RecipientEmail.Trim())
        {
            errors.Add("Operations:FailureDigest:RecipientEmail (OPERATOR_DIGEST_EMAIL) must be one bare e-mail address when the digest is enabled.");
        }

        if (o.HourUtc is < 0 or > 23) errors.Add("Operations:FailureDigest:HourUtc must be 0-23.");
        if (o.LookbackHours is < 1 or > 168) errors.Add("Operations:FailureDigest:LookbackHours must be 1-168.");
        if (o.MaxRows is < 1 or > 200) errors.Add("Operations:FailureDigest:MaxRows must be 1-200.");
        if (o.PollMinutes < 1) errors.Add("Operations:FailureDigest:PollMinutes must be at least 1.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

/// <summary>What one check of <see cref="OperatorFailureDigestService"/> did.</summary>
public enum FailureDigestOutcome
{
    /// <summary>Disabled: nothing was queried.</summary>
    Disabled,

    /// <summary>Today's slot has not started yet.</summary>
    NotDue,

    /// <summary>Another replica (or an earlier check) already took today's digest.</summary>
    AlreadyClaimed,

    /// <summary>Queried, nothing to report, and SendWhenEmpty is off.</summary>
    Empty,

    /// <summary>The relay accepted the mail.</summary>
    Sent,

    /// <summary>The mail could not be sent for a transient reason; the claim was released so the next check retries (at most <see cref="OperatorFailureDigestService.MaxAttemptsPerDay"/> attempts a day).</summary>
    SendFailed,

    /// <summary>The relay refused the mail with a 4xx. Not retried today: the claim is kept and one error is logged.</summary>
    Refused,

    /// <summary>Today's attempts are used up; nothing was queried or sent.</summary>
    Exhausted
}

/// <summary>
/// The operator digest: once a day, ONE mail through the Go relay with the platform-caused step failures
/// (refunded to the platform), runs by final status and the feedback reports of the window.
///
/// <para>
/// <b>Once per day across replicas.</b> Every replica runs this loop; the one whose
/// <see cref="IMailRateCounterStore.TryMarkOnceAsync"/> insert of the day's marker row (bucket
/// <see cref="ClaimBucket"/>, window = the day's slot start) wins does the work, the others see a
/// conflict and stop. No new table: <c>mail_rate_counters</c> is the API's own and its sweeper trims it.
/// A failed send deletes the marker again so a later check retries.
/// </para>
///
/// <para>
/// <b>Read-only on engine tables</b> (AsNoTracking, no writes). <b>No personal data</b> in the mail: counts,
/// closed codes, step types, run ids, and error / feedback text passed through <see cref="SecretRedactor"/>
/// and cut to <see cref="ExcerptLength"/> characters.
/// </para>
/// </summary>
public sealed class OperatorFailureDigestService : BackgroundService
{
    /// <summary>Claim bucket in <c>mail_rate_counters</c>; one marker row per day.</summary>
    public const string ClaimBucket = "operator-digest:failures";

    /// <summary>Characters of an error or a feedback message kept in the mail.</summary>
    public const int ExcerptLength = 160;

    /// <summary>Most send attempts a day (each attempt queries once). Counted on <see cref="AttemptBucket"/>.</summary>
    public const int MaxAttemptsPerDay = 3;

    /// <summary>Attempt counter in <c>mail_rate_counters</c> (same day window as <see cref="ClaimBucket"/>).</summary>
    public const string AttemptBucket = "operator-digest:failures:attempts";

    /// <summary>Hard cap on the text taken from a column before any regex runs; only bounds regex cost.</summary>
    private const int ExcerptInputCap = 16000;

    /// <summary>Failure code shown for a failed step that recorded none.</summary>
    public const string UncodedCode = "UNCODED";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<FailureDigestOptions> _options;
    private readonly ILogger<OperatorFailureDigestService> _logger;
    private readonly TimeProvider _clock;

    public OperatorFailureDigestService(
        IServiceScopeFactory scopeFactory,
        IOptions<FailureDigestOptions> options,
        ILogger<OperatorFailureDigestService> logger,
        TimeProvider? clock = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        FailureDigestOptions options = _options.Value;
        if (!options.Enabled)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.InitialDelaySeconds)), _clock, stoppingToken);
            using PeriodicTimer timer = new(TimeSpan.FromMinutes(Math.Max(1, options.PollMinutes)), _clock);
            do
            {
                try
                {
                    await CheckOnceAsync(_clock.GetUtcNow(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // An observation, never a dependency: the next check tries again.
                    _logger.LogError(ex, "The operator digest could not be produced");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>One check at <paramref name="now"/>. Public and clock-injected so a test runs a day in milliseconds.</summary>
    public async Task<FailureDigestOutcome> CheckOnceAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        FailureDigestOptions options = _options.Value;
        if (!options.Enabled)
        {
            return FailureDigestOutcome.Disabled;
        }

        DateTime to = now.UtcDateTime;
        DateTime slot = to.Date.AddHours(options.HourUtc);
        if (to < slot)
        {
            return FailureDigestOutcome.NotDue;
        }

        using IServiceScope scope = _scopeFactory.CreateScope();
        IMailRateCounterStore counters = scope.ServiceProvider.GetRequiredService<IMailRateCounterStore>();
        if (!await counters.TryMarkOnceAsync(ClaimBucket, DateTime.SpecifyKind(slot, DateTimeKind.Utc), ct))
        {
            return FailureDigestOutcome.AlreadyClaimed;
        }

        // Only the replica that won today's claim counts attempts, so the count is exact. The count survives a
        // released claim: three attempts a day, however they fail.
        int attempt = await counters.IncrementAsync(AttemptBucket, DateTime.SpecifyKind(slot, DateTimeKind.Utc), ct);
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        if (attempt > MaxAttemptsPerDay)
        {
            return FailureDigestOutcome.Exhausted; // the claim stays: nobody else tries today
        }

        bool release = true;
        try
        {
            // The window is anchored to the scheduled slot, not to the moment of the check: a restart or a retry
            // later in the day reads the same span, and consecutive days leave no gap and no overlap.
            DateTime windowEnd = slot;
            DateTime from = windowEnd.AddHours(-options.LookbackHours);
            OperatorDigestData digest = FitToRelay(await BuildAsync(db, from, windowEnd, options.MaxRows, ct));
            to = windowEnd;
            if (digest.IsEmpty && !options.SendWhenEmpty)
            {
                _logger.LogInformation("Operator digest {From:o}..{To:o}: nothing to report, no mail sent", from, to);
                release = false; // today's answer is "nothing": keep the claim
                return FailureDigestOutcome.Empty;
            }

            IAccountMailClient mail = scope.ServiceProvider.GetRequiredService<IAccountMailClient>();
            AccountMailResult result = await mail.SendWithStatusAsync(
                new AccountMail(options.RecipientEmail!.Trim(), "operator_digest", new AccountMailData { Digest = digest }, "en"), ct);
            _logger.LogInformation(
                "Operator digest {From:o}..{To:o}: {FailedSteps} platform-failed steps in {Groups} groups, {Feedback} feedback reports, mail {Result} (attempt {Attempt}/{Max})",
                from, to, digest.Groups.Sum(g => g.Count), digest.Groups.Count, digest.FeedbackCount, result.Sent ? "sent" : "NOT sent", attempt, MaxAttemptsPerDay);
            if (result.Sent)
            {
                release = false;
                return FailureDigestOutcome.Sent;
            }

            if (result.HttpStatus is >= 400 and < 500)
            {
                // A refusal (wrong recipient, bad token, bad body) does not pass by waiting: one error, no retry today.
                _logger.LogError("The Go relay refused the operator digest with status {Status}; not retrying today", result.HttpStatus);
                release = false;
                return FailureDigestOutcome.Refused;
            }

            release = attempt < MaxAttemptsPerDay;
            if (!release)
            {
                _logger.LogError("The operator digest could not be sent in {Max} attempts; giving up for today", MaxAttemptsPerDay);
            }

            return FailureDigestOutcome.SendFailed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            release = attempt < MaxAttemptsPerDay;
            _logger.LogError(ex, "The operator digest attempt {Attempt}/{Max} failed", attempt, MaxAttemptsPerDay);
            return FailureDigestOutcome.SendFailed;
        }
        finally
        {
            if (release && !ct.IsCancellationRequested)
            {
                // Not sent for a reason that may pass (relay down, query failed): free today's claim so a
                // later check retries.
                await ReleaseOnFailureAsync(db, slot, CancellationToken.None);
            }
        }
    }

    private async Task ReleaseOnFailureAsync(InferenceApiDbContext db, DateTime slot, CancellationToken ct)
    {
        try
        {
            DateTime utc = DateTime.SpecifyKind(slot, DateTimeKind.Utc);
            ReelBolt.Shared.Data.Models.Marketing.MailRateCounter? row =
                await db.MailRateCounters.FirstOrDefaultAsync(c => c.Bucket == ClaimBucket && c.WindowStart == utc, ct);
            if (row is not null)
            {
                db.MailRateCounters.Remove(row);
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The operator digest claim could not be released");
        }
    }

    /// <summary>The read-only query and its shaping. Public for tests.</summary>
    public static async Task<OperatorDigestData> BuildAsync(
        InferenceApiDbContext db, DateTime from, DateTime to, int maxRows, CancellationToken ct)
    {
        // The engine's own rule for "the platform bears it", walked over the enum, never a hand list.
        // None is included: IsRefunded(None) is true, so an unclassified failure is refunded and the platform bears it.
        FailureClass[] refunded = Enum.GetValues<FailureClass>()
            .Where(ActivityFailureRefund.IsRefunded)
            .ToArray();

        var failed = db.WorkflowStepResults.AsNoTracking().IgnoreQueryFilters()
            .Where(r => r.Status == StepStatus.Failed && refunded.Contains(r.FailureClass)
                // The same rows as COALESCE(completed_at, executed_at) in [from, to), written so the two partial indexes on
                // failed steps (ix_workflow_step_results_failed_completed_at, ..._failed_uncompleted_executed_at) can serve it.
                && ((r.CompletedAt >= from && r.CompletedAt < to)
                    || (r.CompletedAt == null && r.ExecutedAt >= from && r.ExecutedAt < to)))
            .Select(r => new
            {
                At = r.CompletedAt ?? r.ExecutedAt,
                r.FailureCode,
                r.FailureClass,
                r.ErrorDetails,
                StepType = r.WorkflowStep.StepType,
                RunId = r.WorkflowExecutionId,
                OrganizationId = r.WorkflowExecution.OrganizationId
            });

        var groupRows = await failed
            .GroupBy(r => new { r.FailureCode, r.StepType })
            .Select(g => new
            {
                g.Key.FailureCode,
                g.Key.StepType,
                Count = g.Count(),
                Organizations = g.Select(x => x.OrganizationId).Distinct().Count(),
                First = g.Min(x => x.At),
                Last = g.Max(x => x.At)
            })
            .ToListAsync(ct);

        int totalFailed = groupRows.Sum(g => g.Count);
        var lines = await failed.OrderByDescending(r => r.At).Take(maxRows).ToListAsync(ct);

        var runRows = await db.WorkflowExecutions.AsNoTracking().IgnoreQueryFilters()
            .Where(e => (e.Status == ExecutionStatus.Passed || e.Status == ExecutionStatus.Failed || e.Status == ExecutionStatus.Cancelled)
                && e.CompletedAt != null && e.CompletedAt >= from && e.CompletedAt < to)
            .GroupBy(e => e.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var feedbackWindow = db.FeedbackReports.AsNoTracking().IgnoreQueryFilters()
            .Where(f => f.CreatedAt >= from && f.CreatedAt < to);
        int feedbackCount = await feedbackWindow.CountAsync(ct);
        var feedbackRows = await feedbackWindow.OrderByDescending(f => f.CreatedAt).Take(maxRows)
            .Select(f => new { f.CreatedAt, f.Kind, f.Message })
            .ToListAsync(ct);

        return new OperatorDigestData
        {
            WindowHours = (int)Math.Round((to - from).TotalHours),
            GeneratedAt = Stamp(to),
            Runs = runRows.OrderBy(r => r.Status).Select(r => new DigestCount { Label = r.Status.ToString(), Count = r.Count }).ToList(),
            Groups = groupRows
                .OrderByDescending(g => g.Count).ThenBy(g => g.FailureCode, StringComparer.Ordinal)
                .Select(g => new DigestGroup
                {
                    Code = g.FailureCode ?? UncodedCode,
                    StepType = g.StepType.ToString(),
                    Count = g.Count,
                    Organizations = g.Organizations,
                    First = Stamp(g.First),
                    Last = Stamp(g.Last)
                }).ToList(),
            Failures = lines.Select(l => new DigestFailure
            {
                Time = Stamp(l.At),
                StepType = l.StepType.ToString(),
                Code = l.FailureCode ?? UncodedCode,
                // An unclassified failure is an agent's own report and quotes model output: no text at all for it.
                Error = l.FailureClass == FailureClass.None ? string.Empty : Excerpt(CutOutputPreview(l.ErrorDetails)),
                RunId = l.RunId.ToString()
            }).ToList(),
            FailuresOmitted = Math.Max(0, totalFailed - lines.Count),
            FeedbackCount = feedbackCount,
            Feedback = feedbackRows.Select(f => new DigestFeedback { Time = Stamp(f.CreatedAt), Kind = f.Kind, Text = Excerpt(f.Message) }).ToList()
        };
    }

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex StorageKey = new(
        @"projects/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}[^\s""'<>]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>
    /// Any URI-shaped token (<c>scheme://...</c>; the separator may also be percent-encoded once or twice, mixed
    /// (<c>https%3A//</c>) or JSON-escaped (<c>https:\/\/</c>); http, s3, redis, postgres, amqp and the like) up to whitespace, a
    /// double quote, an angle bracket or a control character. It becomes <c>[link]</c> as a whole: no host, path, query,
    /// fragment or userinfo is parsed or kept, because a password may contain any of <c>@ : / ? # '</c>. Trailing
    /// <c>) ] , . ;</c> that close the surrounding prose stay outside the token.
    /// A password may also contain a space, a double quote or an angle bracket, which would end the token early: when the
    /// token ends before any <c>@</c> and an <c>@</c> follows within the same line (a short run of text later), everything
    /// through the text after that <c>@</c> is taken as well (the userinfo was cut, so the host and path are the tail).
    /// </summary>
    private static readonly Regex Url = new(
        @"[a-z][a-z0-9+.\-]{1,20}(?::|%3A|%253A)(?:\\?/|%2F|%252F){2}(?:[^\s""<>\x00-\x1f@]*[ ""<>][^\r\n@]{0,80}@[^\s""<>\x00-\x1f]*|(?:[^\s""<>\x00-\x1f]*[^\s""<>\x00-\x1f)\],.;])?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>
    /// <c>user:password@host[:port][/path]</c> without a scheme (a connection string quoted bare), any host, dotted or not. Only
    /// a userinfo that holds a <c>:</c> counts, so a plain e-mail address is left to <see cref="Email"/>.
    /// </summary>
    private static readonly Regex SchemelessCredentials = new(
        @"(?<![\w@.\-])[^\s:@/""'<>\[\]]+:[^\s@""<>]+@(?:\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9._\-]+)(?::\d+)?(?:/[^\s""<>]*)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex CookieHeader = new(
        @"(?<k>\b(?:set-)?cookie[ \t]*:[ \t]*)[^\r\n]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex GoogleAccessToken = new(
        @"\bya29\.[A-Za-z0-9_\-]{10,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex AuthorizationHeader = new(
        @"(?<k>authorization[ \t]*[:=][ \t]*)(?:(?:bearer|basic|key|token|apikey)[ \t]+)?[^\s,;""']+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex JsonSecret = new(
        @"(?<k>""[A-Za-z0-9_\-]*(?:password|passwd|pwd|secret|token|key|sig|signature|credential|authorization)[A-Za-z0-9_\-]*""\s*:\s*)""(?:[^""\\]|\\.)*""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex SecretParam = new(
        @"(?<k>(?:password|passwd|pwd|secret|token|key|sig|signature|credential)s?[""']?)(?<sep>[ \t]*[:=]\s*)(?:""[^""]*""|'[^']*'|[""']?[^;&\s""']+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex BearerToken = new(
        @"\bbearer[ \t]+[A-Za-z0-9._~+/=\-]{8,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex ApiKeyShape = new(
        @"\bsk[-_][A-Za-z0-9_\-]{8,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex Email = new(
        @"[\p{L}\p{M}\p{N}._%+\-]+[@\uFF20\uFE6B][\p{L}\p{M}\p{N}\-]+(?:[.\uFF0E\u3002][\p{L}\p{M}\p{N}\-]+)*[.\uFF0E\u3002][\p{L}\p{M}]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>
    /// The digest-only scrub, on top of <see cref="SecretRedactor"/> (which is shared and untouched): storage keys
    /// <c>projects/&lt;guid&gt;/...</c> become <c>projects/[redacted]</c>; every URL becomes <c>[link]</c> (a presigned
    /// URL's path is a tenant object key); <c>password=</c>, <c>token=</c>, <c>key=</c>
    /// and friends lose their value; e-mail addresses become <c>[email]</c>.
    /// </summary>
    public static string Scrub(string text) => ScrubRest(ScrubUrls(text));

    /// <summary>
    /// The URL pass, run BEFORE the shared redactor (which would turn <c>scheme://user:pass@</c> into a bare marker and leave
    /// the host path behind): every URL, plain or percent-encoded, becomes <c>[link]</c> in its entirety.
    /// </summary>
    private static string ScrubUrls(string text)
    {
        // Compatibility folding turns fullwidth letters and punctuation into their plain forms before matching.
        string result = Url.Replace(text.Normalize(NormalizationForm.FormKC), "[link]");
        return SchemelessCredentials.Replace(result, "[redacted]");
    }

    private static string ScrubRest(string result)
    {
        result = CookieHeader.Replace(result, m => m.Groups["k"].Value + "[redacted]");
        result = StorageKey.Replace(result, "projects/[redacted]");
        result = AuthorizationHeader.Replace(result, m => m.Groups["k"].Value + "[redacted]");
        result = JsonSecret.Replace(result, m => m.Groups["k"].Value + "\"[redacted]\"");
        result = SecretParam.Replace(result, m => m.Groups["k"].Value + m.Groups["sep"].Value.Trim() + (m.Groups["sep"].Value.Contains(':') ? " " : string.Empty) + "[redacted]");
        result = BearerToken.Replace(result, "Bearer [redacted]");
        result = ApiKeyShape.Replace(result, "[redacted]");
        result = GoogleAccessToken.Replace(result, "[redacted]");
        return Email.Replace(result, "[email]");
    }

    /// <summary>
    /// Redacts the WHOLE text first (a secret cut in half would no longer match its pattern; the only cut before
    /// that is a generous hard cap that bounds regex cost), then collapses whitespace and keeps the first
    /// <see cref="ExcerptLength"/> characters.
    /// </summary>
    public static string Excerpt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string bounded = text.Length > ExcerptInputCap ? text[..ExcerptInputCap] : text;
        string redacted;
        try
        {
            ScrubProbe?.Invoke(bounded);
            (redacted, _) = SecretRedactor.Redact(ScrubUrls(bounded));
            redacted = ScrubRest(redacted);
        }
        catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException)
        {
            // One unscrubbable text must not fail the day's digest, and nothing unscrubbed may be sent.
            return UnreadableExcerpt;
        }

        StringBuilder sb = new(ExcerptLength + 1);
        bool space = false;
        foreach (char c in redacted)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
            if (sb.Length >= ExcerptLength)
            {
                break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Cuts a failure text at the agent executor's "Output preview:" marker, which is followed by model output.</summary>
    public static string? CutOutputPreview(string? text)
    {
        if (text is null)
        {
            return null;
        }

        int at = text.IndexOf(OutputPreviewMarker, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? text : text[..at];
    }

    /// <summary>
    /// A test seam: called with each text before it is scrubbed, so a test can throw <see cref="RegexMatchTimeoutException"/> for a
    /// marked text and see the row fall back to <see cref="UnreadableExcerpt"/>. Null in production.
    /// </summary>
    internal static Action<string>? ScrubProbe { get; set; }

    /// <summary>What a text that could not be scrubbed within the regex time limit becomes.</summary>
    public const string UnreadableExcerpt = "[unreadable]";

    private const string OutputPreviewMarker = "Output preview:";

    /// <summary>Rows the Go relay keeps per list (it drops the rest), its group and run caps, and the body budget under its 64 KB read limit.</summary>
    public const int RelayMaxRows = 100;

    public const int RelayMaxGroups = 50;
    public const int RelayMaxRuns = 10;
    public const int RelayBodyBudgetBytes = 48 * 1024;

    /// <summary>
    /// How the digest is serialized for the relay: non-ASCII text stays as UTF-8 instead of six-byte <c>\uXXXX</c>
    /// escapes, which would triple the size of a day of non-Latin error text.
    /// </summary>
    public static readonly System.Text.Json.JsonSerializerOptions WireOptions = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Makes the digest fit what the relay takes: its list caps, then a serialized size under the budget, dropping the
    /// oldest lines first. Every failure line left out is added to <c>FailuresOmitted</c>.
    /// </summary>
    public static OperatorDigestData FitToRelay(OperatorDigestData digest)
    {
        if (digest.Runs.Count > RelayMaxRuns) digest.Runs.RemoveRange(RelayMaxRuns, digest.Runs.Count - RelayMaxRuns);
        if (digest.Groups.Count > RelayMaxGroups) digest.Groups.RemoveRange(RelayMaxGroups, digest.Groups.Count - RelayMaxGroups);
        DropFailures(digest, digest.Failures.Count - RelayMaxRows);
        if (digest.Feedback.Count > RelayMaxRows) digest.Feedback.RemoveRange(RelayMaxRows, digest.Feedback.Count - RelayMaxRows);

        while (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(digest, WireOptions).Length > RelayBodyBudgetBytes
            && (digest.Failures.Count > 0 || digest.Feedback.Count > 0))
        {
            if (digest.Failures.Count >= digest.Feedback.Count)
            {
                DropFailures(digest, 1);
            }
            else
            {
                digest.Feedback.RemoveAt(digest.Feedback.Count - 1);
            }
        }

        return digest;
    }

    private static void DropFailures(OperatorDigestData digest, int count)
    {
        if (count <= 0)
        {
            return;
        }

        digest.Failures.RemoveRange(digest.Failures.Count - count, count);
        digest.FailuresOmitted += count;
    }

    private static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}

/// <summary>The structured body of the operator_digest mail; field names match the Go relay's <c>OperatorDigest</c>.</summary>
public sealed class OperatorDigestData
{
    [JsonPropertyName("windowHours")] public int WindowHours { get; set; }
    [JsonPropertyName("generatedAt")] public string GeneratedAt { get; set; } = string.Empty;
    [JsonPropertyName("runs")] public List<DigestCount> Runs { get; set; } = [];
    [JsonPropertyName("groups")] public List<DigestGroup> Groups { get; set; } = [];
    [JsonPropertyName("failures")] public List<DigestFailure> Failures { get; set; } = [];
    [JsonPropertyName("failuresOmitted")] public int FailuresOmitted { get; set; }
    [JsonPropertyName("feedbackCount")] public int FeedbackCount { get; set; }
    [JsonPropertyName("feedback")] public List<DigestFeedback> Feedback { get; set; } = [];

    /// <summary>No platform failure and no feedback: run counts alone are not worth a mail.</summary>
    [JsonIgnore] public bool IsEmpty => Groups.Count == 0 && FeedbackCount == 0;
}

public sealed class DigestCount
{
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("count")] public int Count { get; set; }
}

public sealed class DigestGroup
{
    [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;
    [JsonPropertyName("stepType")] public string StepType { get; set; } = string.Empty;
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("organizations")] public int Organizations { get; set; }
    [JsonPropertyName("first")] public string First { get; set; } = string.Empty;
    [JsonPropertyName("last")] public string Last { get; set; } = string.Empty;
}

public sealed class DigestFailure
{
    [JsonPropertyName("time")] public string Time { get; set; } = string.Empty;
    [JsonPropertyName("stepType")] public string StepType { get; set; } = string.Empty;
    [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;
    [JsonPropertyName("error")] public string Error { get; set; } = string.Empty;
    [JsonPropertyName("runId")] public string RunId { get; set; } = string.Empty;
}

public sealed class DigestFeedback
{
    [JsonPropertyName("time")] public string Time { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
}
