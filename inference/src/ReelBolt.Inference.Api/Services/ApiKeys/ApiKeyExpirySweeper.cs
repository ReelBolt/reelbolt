using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.ApiKeys;

/// <summary>
/// Hourly: warns about API keys that expire within 7 days and again within 1 day. One
/// <see cref="UserActivityNotificationRequested"/> (<see cref="UserActivityNotificationRequested.ApiKeyExpiring"/>)
/// per recipient: the key's creator (when still a member) and every Owner/Admin of the organization. A service
/// account is never a recipient. The <c>expiry_warned_*</c> stamp is saved after the publish, in one save per sweep,
/// so a restart does not repeat a warning that went out. Keys without an expiry are never touched. Off with
/// <c>PublicApi:ExpiryWarning:Enabled=false</c>.
/// </summary>
public sealed class ApiKeyExpirySweeper : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<PublicApiOptions> _options;
    private readonly ILogger<ApiKeyExpirySweeper> _logger;
    private readonly TimeProvider _time;

    public ApiKeyExpirySweeper(
        IServiceScopeFactory scopes, IOptions<PublicApiOptions> options, ILogger<ApiKeyExpirySweeper> logger, TimeProvider? time = null)
    {
        _scopes = scopes;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let the API finish migrating before the first sweep.
            await Task.Delay(TimeSpan.FromMinutes(2), _time, stoppingToken);
            using PeriodicTimer timer = new(Interval, _time);
            do
            {
                try
                {
                    int sent = await SweepOnceAsync(stoppingToken);
                    if (sent > 0)
                        _logger.LogInformation("Published {Count} API key expiry warning(s)", sent);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "The API key expiry sweep failed; it is retried on the next run");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>One sweep as of the clock's now; returns how many events it published.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        PublicApiOptions options = _options.Value;
        if (!options.ExpiryWarning.Enabled)
            return 0;

        DateTime now = _time.GetUtcNow().UtcDateTime;
        DateTime in7d = now.AddDays(7);
        DateTime in1d = now.AddDays(1);

        using IServiceScope scope = _scopes.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        IPublishEndpoint publish = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        ITenantContext? tenant = scope.ServiceProvider.GetService<ITenantContext>();
        using IDisposable system = tenant.System();

        List<ApiKey> keys = await db.ApiKeys
            .Where(k => k.RevokedAt == null && k.ExpiresAt != null && k.ExpiresAt > now
                && ((k.ExpiresAt <= in7d && k.ExpiryWarned7dAt == null) || (k.ExpiresAt <= in1d && k.ExpiryWarned1dAt == null)))
            .OrderBy(k => k.ExpiresAt)
            .Take(500)
            .ToListAsync(ct);
        if (keys.Count == 0)
            return 0;

        Guid[] orgIds = keys.Select(k => k.OrganizationId).Distinct().ToArray();
        var members = await db.OrganizationMemberships.IgnoreQueryFilters().AsNoTracking()
            .Where(m => orgIds.Contains(m.OrganizationId) && !m.User.IsServiceAccount)
            .Select(m => new { m.OrganizationId, m.UserId, m.Role })
            .ToListAsync(ct);

        int sent = 0;
        foreach (ApiKey key in keys)
        {
            DateTime expires = key.ExpiresAt!.Value;
            bool due1d = expires <= in1d && key.ExpiryWarned1dAt == null;

            HashSet<Guid> recipients = members
                .Where(m => m.OrganizationId == key.OrganizationId
                    && (m.Role is OrganizationRole.Owner or OrganizationRole.Admin || m.UserId == key.CreatedByUserId))
                .Select(m => m.UserId)
                .ToHashSet();

            try
            {
                foreach (Guid recipient in recipients)
                {
                    await publish.Publish(new UserActivityNotificationRequested
                    {
                        OrganizationId = key.OrganizationId,
                        ActorUserId = null,
                        ProjectId = Guid.Empty,
                        Type = UserActivityNotificationRequested.ApiKeyExpiring,
                        Subject = key.Name,
                        Details = expires.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                        RecipientUserId = recipient,
                    }, ct);
                    sent++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Leave the stamp unset so the next sweep retries this key.
                _logger.LogWarning(ex, "API key expiry warning not published for key {KeyId}", key.Id);
                continue;
            }

            // A key already inside its last day also skips the 7-day warning, which would only be late.
            if (due1d)
            {
                key.ExpiryWarned1dAt = now;
                key.ExpiryWarned7dAt ??= now;
            }
            else
            {
                key.ExpiryWarned7dAt = now;
            }
        }

        await db.SaveChangesAsync(ct);
        return sent;
    }
}
