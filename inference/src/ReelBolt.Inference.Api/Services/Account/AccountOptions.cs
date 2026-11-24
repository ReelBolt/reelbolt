using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Marketing;

namespace ReelBolt.Inference.Api.Services.Account;

/// <summary>
/// Settings for the Inference API's two account-lifecycle side effects: the export archive and the
/// deletion mail. Section <c>Account</c>.
/// </summary>
public sealed class AccountOptions
{
    public const string SectionName = "Account";

    /// <summary>
    /// Base URL of the Go API's private surface. <c>/internal/*</c> is deliberately not routed by
    /// nginx, so this is only reachable inside the compose network (or on the same host in dev).
    /// </summary>
    public string InternalBaseUrl { get; set; } = "http://go-api:8080";

    /// <summary>
    /// Shared secret the Go API requires on <c>POST /internal/mail/send</c> (C0's decision: all
    /// outbound mail is sent by the Go API, which owns the SMTP credentials and the templates).
    /// Unset means "no internal mail": every send is skipped and logged, never failed.
    /// </summary>
    public string InternalToken { get; set; } = string.Empty;

    /// <summary>
    /// The origin the export archive's presigned GET is minted against (this side's
    /// <c>MinIO:PublicEndpoint</c> / <c>MinIO:Endpoint</c>), declared so the Go mailer can
    /// tell whether it will accept the link. Set it to the origin the Go side allows — its
    /// <c>MAIL_LINK_HOSTS</c> entry (with the port, when presigning against anything else).
    /// When the minted link names a different origin, the mail goes out without the link (a log
    /// line says so) instead of being refused; the archive stays available through the export
    /// endpoint. Unset: the link is sent as is and the Go side decides (it logs a refusal).
    /// </summary>
    public string MailLinkEndpoint { get; set; } = string.Empty;

    public AccountExportOptions Export { get; set; } = new();
    public AccountPurgeOptions Purge { get; set; } = new();
}

/// <summary>Settings for <see cref="AccountExportService"/>. Section <c>Account:Export</c>.</summary>
public sealed class AccountExportOptions
{
    /// <summary>When false, <c>POST /api/v1/account/export</c> answers 404 and the sweeper stops.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often one person may ask for an export (C0: exactly one per 24 hours).</summary>
    public int ThrottleHours { get; set; } = 24;

    /// <summary>How long the download link stays valid, and how long the archive stays on disk.</summary>
    public int LinkTtlDays { get; set; } = 7;

    /// <summary>
    /// How many bytes of the user's original files are packed INTO the archive. Beyond this the file
    /// is listed in the manifest instead of copied, so an export of a large footage library stays a
    /// download and not a full mirror. Default 2 GiB.
    /// </summary>
    public long MaxPackedBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Object-store prefix every export lives under, outside any project prefix.</summary>
    public string Prefix { get; set; } = "exports";

    /// <summary>How often the sweeper deletes archives past their 7-day life.</summary>
    public int SweepIntervalMinutes { get; set; } = 60;

    /// <summary>Objects listed per sweep page, so one sweep cannot walk a huge bucket.</summary>
    public int SweepMaxObjects { get; set; } = 5000;
}

/// <summary>Settings for <see cref="AccountPurgeService"/>. Section <c>Account:Purge</c>.</summary>
public sealed class AccountPurgeOptions
{
    /// <summary>When false the purge never runs (self-host keeps every account forever).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>C0: 30 days between the deletion request and the purge, cancellable by logging in.</summary>
    public int GraceDays { get; set; } = 30;

    /// <summary>How often the sweep looks for due accounts.</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Purge an account that never verified its email after this many days. C0 makes this a
    /// <b>cloud-only</b> behaviour (signup spam is a cloud cost), so it also requires
    /// <c>Tenancy:Mode=Cloud</c>; off by default.
    /// </summary>
    public bool PurgeUnverified { get; set; }

    /// <summary>Age at which an unverified account is purged when <see cref="PurgeUnverified"/> is on.</summary>
    public int UnverifiedDays { get; set; } = 7;

    /// <summary>Accounts purged per sweep, so one run cannot monopolise the process.</summary>
    public int MaxPerSweep { get; set; } = 25;

    /// <summary>Objects deleted per S3 multi-object delete call (the hard API limit).</summary>
    public int DeleteBatchSize { get; set; } = 1000;
}

/// <summary>One <c>MailData</c> field set, matching the Go mailer's data bag.</summary>
/// <param name="Locale">Optional language for a recipient with no account (an account's saved locale wins on the Go side); the Go API allowlists it.</param>
public sealed record AccountMail(string To, string Template, AccountMailData Data, string? Locale = null);

/// <summary>The fields the Go templates render from; unset fields are omitted.</summary>
public sealed class AccountMailData
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("link")] public string? Link { get; set; }
    [JsonPropertyName("expiresIn")] public string? ExpiresIn { get; set; }
    [JsonPropertyName("orgName")] public string? OrgName { get; set; }
    [JsonPropertyName("inviterName")] public string? InviterName { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("deletionDate")] public string? DeletionDate { get; set; }
    [JsonPropertyName("balance")] public string? Balance { get; set; }

    /// <summary>A closed reason category (marketplace_notice only); the Go template maps it to fixed wording.</summary>
    [JsonPropertyName("reason")] public string? Reason { get; set; }

    /// <summary>The structured body of an operator_digest mail only (counts, codes, run ids, redacted excerpts).</summary>
    [JsonPropertyName("digest")] public ReelBolt.Inference.Api.Services.Background.OperatorDigestData? Digest { get; set; }
}

/// <summary>
/// Sends a transactional mail through the Go API's <c>POST /internal/mail/send</c>, which is the
/// only place SMTP credentials exist (C0: "the Go API owns all outbound mail"). Every call is
/// best-effort: a mail failure must never fail the operation that triggered it, so this returns
/// false and logs instead of throwing.
/// </summary>
public interface IAccountMailClient
{
    /// <summary>The internal mail endpoint's HTTP client name.</summary>
    static string HttpClientName => "AccountInternalMail";

    Task<bool> SendAsync(AccountMail mail, CancellationToken ct);

    /// <summary>
    /// Like <see cref="SendAsync"/> but also says which HTTP status the relay answered (null when it was never reached
    /// or the send was skipped), so a caller can tell a refusal that will not pass (4xx) from a transient failure.
    /// The default knows no status.
    /// </summary>
    async Task<AccountMailResult> SendWithStatusAsync(AccountMail mail, CancellationToken ct)
        => new(await SendAsync(mail, ct), null);
}

/// <summary>Outcome of <see cref="IAccountMailClient.SendWithStatusAsync"/>.</summary>
public sealed record AccountMailResult(bool Sent, int? HttpStatus);

public sealed class GoAccountMailClient : IAccountMailClient
{
    /// <summary>
    /// The closed template list C0 defines: the endpoint accepts nothing else, so a compromised
    /// caller cannot use it as an open relay for arbitrary content.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedTemplates =
        new HashSet<string>(StringComparer.Ordinal) { "export_ready", "deletion_scheduled", "low_balance", "welcome", "invite", "verify_email", "reset_password", "marketing-confirm", "marketplace_notice", "operator_digest" };

    private readonly HttpClient _http;
    private readonly AccountOptions _options;
    private readonly ILogger<GoAccountMailClient> _logger;
    private readonly MailLogRef _logRef;

    public GoAccountMailClient(
        HttpClient http, IOptions<AccountOptions> options, ILogger<GoAccountMailClient> logger, MailLogRef? logRef = null)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _logRef = logRef ?? MailLogRef.Ephemeral();
    }

    /// <summary>
    /// What a log line may say about the recipient, for every template: the
    /// keyed <c>rcpt=</c> reference, never the address. The Go relay logs the same reference.
    /// </summary>
    internal string RecipientForLog(AccountMail mail) => _logRef.For(mail.To);

    public async Task<bool> SendAsync(AccountMail mail, CancellationToken ct)
        => (await SendWithStatusAsync(mail, ct)).Sent;

    public async Task<AccountMailResult> SendWithStatusAsync(AccountMail mail, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.InternalToken))
        {
            _logger.LogWarning(
                "Account:InternalToken is not configured, so the {Template} mail to {Recipient} was not sent",
                mail.Template, RecipientForLog(mail));
            return new AccountMailResult(false, null);
        }

        if (!AllowedTemplates.Contains(mail.Template))
        {
            _logger.LogError("Refusing to send an unknown mail template {Template}", mail.Template);
            return new AccountMailResult(false, null);
        }

        // F-11: the Go API accepts a mail link's host only from its configured origins. When the
        // export link was minted against a different origin (the usual self-host case: presigned
        // against the in-cluster storage endpoint), send it without the link rather than let the
        // Go side refuse the whole mail — the archive stays available through the export endpoint.
        if (
            mail.Template == "export_ready"
            && mail.Data?.Link is { Length: > 0 })
        {
            if (
                !string.IsNullOrWhiteSpace(_options.MailLinkEndpoint)
                && Uri.TryCreate(_options.MailLinkEndpoint, UriKind.Absolute, out var endpoint)
                && (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps)
                && Uri.TryCreate(mail.Data.Link, UriKind.Absolute, out var link)
                && link.Scheme == endpoint.Scheme
                && link.Host == endpoint.Host
                && ((link.IsDefaultPort && endpoint.IsDefaultPort) || link.Port == endpoint.Port))
            {
                // Minted against the allowed endpoint; the Go side will accept it.
            }
            else if (
                string.IsNullOrWhiteSpace(_options.MailLinkEndpoint))
            {
                // No endpoint configured on this side: we cannot know whether the Go side allows
                // the minted host. Keep the link and let the Go side decide (it logs a refusal);
                // the archive stays available through the export endpoint either way.
            }
            else
            {
                _logger.LogInformation(
                    "The export link for {Recipient} was minted against {Host}, which is not the configured MailLinkEndpoint {Endpoint}; sending without it (the archive stays available through the export endpoint)",
                    RecipientForLog(mail),
                    new Uri(mail.Data.Link).Host,
                    _options.MailLinkEndpoint);
                // AccountMail is a record, so Data is init-only and cannot be
                // reassigned here. AccountMailData is a class, so clearing the
                // link on the instance mail.Data already points at is the same
                // edit, without the copy.
                mail.Data.Link = null;
            }
        }

        try
        {
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                new Uri(new Uri(_options.InternalBaseUrl), "/internal/mail/send"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.InternalToken);
            var body = new { to = mail.To, template = mail.Template, data = mail.Data, locale = mail.Locale };
            // The operator digest is sized against the relay's body limit, so it travels as UTF-8, not \uXXXX escapes.
            request.Content = mail.Template == "operator_digest"
                ? JsonContent.Create(body, options: ReelBolt.Inference.Api.Services.Background.OperatorFailureDigestService.WireOptions)
                : JsonContent.Create(body);

            using HttpResponseMessage response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return new AccountMailResult(true, (int)response.StatusCode);

            _logger.LogWarning(
                "The Go API refused the {Template} mail to {Recipient}: {Status}",
                mail.Template, RecipientForLog(mail), (int)response.StatusCode);
            return new AccountMailResult(false, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never the exception object: its message can echo the recipient or the link.
            _logger.LogWarning("Sending the {Template} mail to {Recipient} failed: {Error}",
                mail.Template, RecipientForLog(mail), MailLogRef.DescribeError(ex));
            return new AccountMailResult(false, null);
        }
    }
}
