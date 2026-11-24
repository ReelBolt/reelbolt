using System.Text;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Webhooks;

/// <summary>The HTTP client deliveries go through (a seam: tests hand in a stub handler).</summary>
public interface IWebhookHttpClientFactory
{
    HttpClient Create();
}

/// <summary>
/// One shared client on <see cref="GeneratedClipUrlGuard.CreateUserUrlHandler"/>: every connection is refused unless
/// all the host's addresses are public (checked at connect time, so a DNS change after create does not help an
/// attacker), no redirect is followed, no proxy, no cookies. The per-request timeout is the worker's.
/// </summary>
public sealed class WebhookHttpClientFactory : IWebhookHttpClientFactory
{
    private readonly HttpClient _client = new(GeneratedClipUrlGuard.CreateUserUrlHandler()) { Timeout = Timeout.InfiniteTimeSpan };

    public HttpClient Create() => _client;
}

/// <summary>
/// Sends the queued <c>webhook_deliveries</c> (public-api T-009). Every <c>PublicApi:Webhooks:PollSeconds</c> it claims
/// due rows (Postgres <c>FOR UPDATE SKIP LOCKED</c>, a short lease so a crash returns the row), at most one per
/// endpoint and <c>BatchSize</c> in all, and POSTs them with at most <c>Concurrency</c> in flight. A 2xx is delivered;
/// anything else (a 3xx included, never followed) is retried after 1 m, 5 m, 30 m, 2 h, 6 h and 12 h, then abandoned.
/// An endpoint that has had no success for <c>DisableAfterHours</c> is disabled and its organization's Owners and
/// Admins are told. Deliveries older than <c>RetentionDays</c> are deleted once a day.
/// </summary>
/// <remarks>
/// Test deliveries (<c>webhook.test</c>) are sent once and never retried, and do not count towards the endpoint's
/// health. Response bodies are never kept beyond a 200-character sanitized snippet of a failed response, and never logged.
/// </remarks>
public sealed class WebhookDeliveryWorker(
    IServiceScopeFactory scopes,
    IOptions<PublicApiOptions> options,
    IWebhookHttpClientFactory httpFactory,
    WebhookSecretProtector secrets,
    ILogger<WebhookDeliveryWorker> logger,
    TimeProvider? time = null) : BackgroundService
{
    public const string UserAgent = "ReelBolt-Webhooks/1";
    public const int SnippetLength = 200;

    /// <summary>The wait after the 1st, 2nd ... failed attempt; one more failure after the last abandons the delivery.</summary>
    public static readonly TimeSpan[] RetrySchedule =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(12)
    ];

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTime _lastPurge = DateTime.MinValue;
    private DateTime _lastSecretSweep = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (options.Value.Enabled)
                    {
                        await TickAsync(stoppingToken);
                        DateTime now = _time.GetUtcNow().UtcDateTime;
                        if (now - _lastSecretSweep >= TimeSpan.FromHours(1))
                        {
                            await SweepExpiredSecretsAsync(stoppingToken);
                            _lastSecretSweep = now;
                        }

                        if (now - _lastPurge >= TimeSpan.FromDays(1))
                        {
                            await PurgeAsync(stoppingToken);
                            _lastPurge = now;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Webhook delivery pass failed: {Error}", ex.GetType().Name);
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.Webhooks.PollSeconds)), _time, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>One pass (also the test seam). Returns how many deliveries it attempted.</summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        List<string> claimed;
        using (IServiceScope scope = scopes.CreateScope())
        {
            using IDisposable system = scope.ServiceProvider.GetService<ITenantContext>().System();
            claimed = await ClaimAsync(scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>(), ct);
        }

        if (claimed.Count == 0) return 0;

        using SemaphoreSlim gate = new(Math.Max(1, options.Value.Webhooks.Concurrency));
        await Task.WhenAll(claimed.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await DeliverAsync(id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Webhook delivery {DeliveryId} failed unexpectedly: {Error}", id, ex.GetType().Name);
            }
            finally
            {
                gate.Release();
            }
        }));
        return claimed.Count;
    }

    /// <summary>
    /// Claims due deliveries of enabled endpoints: the oldest of each endpoint, up to the batch size, leased.
    /// On Postgres the one-per-endpoint choice happens IN SQL (<c>ROW_NUMBER() OVER (PARTITION BY endpoint_id
    /// ORDER BY next_attempt_at) = 1</c>) before the LIMIT, so one busy endpoint cannot crowd the others out of
    /// the batch window (S-M6). The CTE picks the ids; the outer select locks exactly those rows.
    /// </summary>
    internal async Task<List<string>> ClaimAsync(InferenceApiDbContext db, CancellationToken ct)
    {
        DateTime now = _time.GetUtcNow().UtcDateTime;
        int batch = Math.Max(1, options.Value.Webhooks.BatchSize);

        if (db.Database.IsNpgsql())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            List<WebhookDelivery> locked = await db.WebhookDeliveries.FromSqlInterpolated($"""
                WITH due AS (
                    SELECT d.id,
                           ROW_NUMBER() OVER (PARTITION BY d.endpoint_id ORDER BY d.next_attempt_at, d.id) AS rn
                    FROM webhook_deliveries d
                    JOIN webhook_endpoints e ON e.id = d.endpoint_id
                    WHERE d.status IN ('pending', 'failed') AND d.next_attempt_at <= {now} AND e.enabled
                )
                SELECT d.* FROM webhook_deliveries d
                JOIN due ON due.id = d.id AND due.rn = 1
                ORDER BY d.next_attempt_at, d.id
                LIMIT {batch}
                FOR UPDATE OF d SKIP LOCKED
                """).ToListAsync(ct);
            foreach (WebhookDelivery row in locked) row.NextAttemptAt = now + Lease;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return locked.Select(r => r.Id).ToList();
        }

        // Other providers (tests): same rule, no row lock (one worker).
        HashSet<Guid> enabled = (await db.WebhookEndpoints.AsNoTracking().Where(e => e.Enabled).Select(e => e.Id).ToListAsync(ct)).ToHashSet();
        List<WebhookDelivery> due = (await db.WebhookDeliveries
                .Where(d => (d.Status == WebhookDelivery.Pending || d.Status == WebhookDelivery.Failed) && d.NextAttemptAt <= now)
                .ToListAsync(ct))
            .Where(d => enabled.Contains(d.EndpointId))
            .OrderBy(d => d.NextAttemptAt)
            .ToList();
        List<WebhookDelivery> picked = OnePerEndpoint(due, batch);
        foreach (WebhookDelivery row in picked) row.NextAttemptAt = now + Lease;
        await db.SaveChangesAsync(ct);
        return picked.Select(r => r.Id).ToList();
    }

    private static List<WebhookDelivery> OnePerEndpoint(IEnumerable<WebhookDelivery> due, int batch) =>
        due.OrderBy(d => d.NextAttemptAt).GroupBy(d => d.EndpointId).Select(g => g.First()).Take(batch).ToList();

    /// <summary>One attempt at one claimed delivery, in a scope of its own.</summary>
    private async Task DeliverAsync(string deliveryId, CancellationToken ct)
    {
        using IServiceScope scope = scopes.CreateScope();
        using IDisposable system = scope.ServiceProvider.GetService<ITenantContext>().System();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

        WebhookDelivery? delivery = await db.WebhookDeliveries.FirstOrDefaultAsync(d => d.Id == deliveryId, ct);
        if (delivery is null) return;
        WebhookEndpoint? endpoint = await db.WebhookEndpoints.IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == delivery.EndpointId, ct);
        if (endpoint is null || !endpoint.Enabled) return;

        WebhookOptions settings = options.Value.Webhooks;
        DateTime now = _time.GetUtcNow().UtcDateTime;
        (int? status, string? error) = await SendAsync(endpoint, delivery, now, settings, ct);

        delivery.Attempts++;
        delivery.LastStatusCode = status;
        bool isTest = delivery.EventType == WebhookEventTypes.Test;
        bool success = status is >= 200 and < 300;
        if (success)
        {
            delivery.Status = WebhookDelivery.Delivered;
            delivery.DeliveredAt = now;
            delivery.LastError = null;
            if (!isTest)
            {
                endpoint.LastSuccessAt = now;
                endpoint.FailingSince = null;
            }

            await db.SaveChangesAsync(ct);
            return;
        }

        delivery.LastError = error ?? $"HTTP {status}";
        if (isTest || delivery.Attempts > RetrySchedule.Length)
        {
            delivery.Status = WebhookDelivery.Abandoned;
        }
        else
        {
            delivery.Status = WebhookDelivery.Failed;
            delivery.NextAttemptAt = now + RetrySchedule[delivery.Attempts - 1];
        }

        bool disabled = false;
        if (!isTest)
        {
            endpoint.FailingSince ??= now;
            if (now - endpoint.FailingSince.Value >= TimeSpan.FromHours(Math.Max(1, settings.DisableAfterHours)))
            {
                endpoint.Enabled = false;
                endpoint.DisabledReason = $"No successful delivery for {settings.DisableAfterHours} hours.";
                disabled = true;
            }
        }

        await db.SaveChangesAsync(ct);
        if (disabled) await NotifyDisabledAsync(scope.ServiceProvider, db, endpoint, ct);
    }

    /// <summary>The POST. Returns the status code (null when no response) and, on failure, a short sanitized reason.</summary>
    private async Task<(int? Status, string? Error)> SendAsync(
        WebhookEndpoint endpoint, WebhookDelivery delivery, DateTime now, WebhookOptions settings, CancellationToken ct)
    {
        List<string> signingSecrets = [];
        if (!secrets.TryUnprotect(endpoint.SecretEncrypted, out string current)) return (null, "The signing secret could not be read.");
        signingSecrets.Add(current);
        if (endpoint.SecretPreviousEncrypted is not null && endpoint.SecretRotatedAt is { } rotated
            && now < rotated.AddHours(settings.PreviousSecretHours)
            && secrets.TryUnprotect(endpoint.SecretPreviousEncrypted, out string previous))
            signingSecrets.Add(previous);

        try
        {
            GeneratedClipUrlGuard.EnsureAllowed(endpoint.Url);
            if (!WebhookUrlValidator.IsAllowedPort(endpoint.Url))
                return (null, "The endpoint URL is not allowed (https, a public host, port 443 or 8443).");
        }
        catch (InvalidOperationException)
        {
            return (null, "The endpoint URL is not allowed (https, a public host, port 443 or 8443).");
        }

        long timestamp = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)).ToUnixTimeSeconds();
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint.Url)
        {
            Content = new StringContent(delivery.PayloadJson, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation(StandardWebhookSigner.IdHeader, delivery.Id);
        request.Headers.TryAddWithoutValidation(StandardWebhookSigner.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(StandardWebhookSigner.SignatureHeader,
            StandardWebhookSigner.SignAll(signingSecrets, delivery.Id, timestamp, delivery.PayloadJson));

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)));
        try
        {
            using HttpResponseMessage response = await httpFactory.Create()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            int code = (int)response.StatusCode;
            if (code is >= 200 and < 300) return (code, null);
            if (code is >= 300 and < 400) return (code, $"HTTP {code}: redirects are not followed.");

            string snippet = await ReadSnippetAsync(response, timeout.Token);
            return (code, snippet.Length == 0 ? $"HTTP {code}" : $"HTTP {code}: {snippet}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, $"Timed out after {settings.TimeoutSeconds} s.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            // One generic reason for every connect-time refusal: telling a private address apart from a dead
            // host would leak the internal topology to the endpoint owner.
            return (null, "The request failed.");
        }
    }

    /// <summary>Up to 200 sanitized characters of a failed response's body; the rest is never read.</summary>
    private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            byte[] buffer = new byte[SnippetLength * 4];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(read), ct);
                if (n == 0) break;
                read += n;
            }

            return WebhookEndpointService.Sanitize(Encoding.UTF8.GetString(buffer, 0, read), SnippetLength);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private async Task NotifyDisabledAsync(IServiceProvider services, InferenceApiDbContext db, WebhookEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            IPublishEndpoint publish = services.GetRequiredService<IPublishEndpoint>();
            List<Guid> admins = await db.OrganizationMemberships.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.OrganizationId == endpoint.OrganizationId && !m.User.IsServiceAccount
                    && (m.Role == OrganizationRole.Owner || m.Role == OrganizationRole.Admin))
                .Select(m => m.UserId)
                .Distinct()
                .ToListAsync(ct);

            string host = Uri.TryCreate(endpoint.Url, UriKind.Absolute, out Uri? uri) ? uri.Host : "webhook";
            foreach (Guid recipient in admins)
            {
                await publish.Publish(new UserActivityNotificationRequested
                {
                    OrganizationId = endpoint.OrganizationId,
                    ActorUserId = null,
                    ProjectId = Guid.Empty,
                    Type = UserActivityNotificationRequested.WebhookDisabled,
                    Subject = host,
                    Details = endpoint.DisabledReason,
                    RecipientUserId = recipient
                }, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Webhook disabled notification not published for endpoint {EndpointId}: {Error}", endpoint.Id, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Nulls the previous secret of every endpoint whose <c>PreviousSecretHours</c> have passed (S-L8), so the
    /// retired secret does not linger in the database. Returns how many endpoints were swept.
    /// </summary>
    public async Task<int> SweepExpiredSecretsAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopes.CreateScope();
        using IDisposable system = scope.ServiceProvider.GetService<ITenantContext>().System();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        DateTime cutoff = _time.GetUtcNow().UtcDateTime.AddHours(-Math.Max(1, options.Value.Webhooks.PreviousSecretHours));

        List<WebhookEndpoint> rows = await db.WebhookEndpoints.IgnoreQueryFilters()
            .Where(e => e.SecretPreviousEncrypted != null && e.SecretRotatedAt != null && e.SecretRotatedAt <= cutoff)
            .ToListAsync(ct);
        foreach (WebhookEndpoint row in rows)
        {
            row.SecretPreviousEncrypted = null;
            row.SecretRotatedAt = null;
        }

        if (rows.Count > 0) await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    /// <summary>Deletes deliveries older than the retention window. Returns how many went.</summary>
    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopes.CreateScope();
        using IDisposable system = scope.ServiceProvider.GetService<ITenantContext>().System();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        DateTime cutoff = _time.GetUtcNow().UtcDateTime.AddDays(-Math.Max(1, options.Value.Webhooks.RetentionDays));

        if (db.Database.IsNpgsql())
            return await db.WebhookDeliveries.Where(d => d.CreatedAt < cutoff).ExecuteDeleteAsync(ct);

        List<WebhookDelivery> old = await db.WebhookDeliveries.Where(d => d.CreatedAt < cutoff).ToListAsync(ct);
        db.WebhookDeliveries.RemoveRange(old);
        await db.SaveChangesAsync(ct);
        return old.Count;
    }
}
