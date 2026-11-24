using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Background;

/// <summary>
/// Tunables of the MCP OAuth sweep (config section <c>McpOAuth:Purge</c>).
/// </summary>
/// <remarks>
/// <b>On by default.</b> This is table hygiene, not a deployment feature: the four OAuth/MCP tables
/// exist in every deployment that ran the migration, and without the sweep
/// <c>oauth_authorization_codes</c>, <c>oauth_refresh_tokens</c> and <c>oauth_clients</c> grow without
/// bound (dynamic client registration is anonymous). Every deletion is of a row that is already
/// unusable, so the sweep is invisible to a working client; disabling it is an escape hatch, not the
/// intended state.
/// </remarks>
public sealed record McpOAuthPurgeOptions
{
    public const string SectionName = "McpOAuth:Purge";

    /// <summary>Run the sweep at all. Default true (see the remarks on this type).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How often to sweep, in minutes.</summary>
    public int IntervalMinutes { get; init; } = 60;

    /// <summary>Wait before the first sweep, so a restarting service does not sweep on every boot.</summary>
    public int InitialDelaySeconds { get; init; } = 60;

    /// <summary>
    /// How long an <b>unclaimed</b> client row (never used — <c>last_used_at IS NULL</c>) is kept after
    /// registration. Dynamic registration is anonymous, so an abandoned registration is a row nobody
    /// will ever use; seven days is long enough for a person to finish an interactive login.
    /// </summary>
    public int UnclaimedClientRetentionDays { get; init; } = 7;

    /// <summary>
    /// How long a <b>revoked</b> refresh token is kept after it was revoked, in hours. The default (24)
    /// deliberately outlives the moment of rotation: a rotated token that is replayed shortly
    /// afterwards must still be found, because finding it is what revokes the whole grant
    /// (docs/design/mcp-oauth.md §4). Raise it to keep that detection for the token's full lifetime
    /// (720 = the 30-day refresh lifetime); lower it to reclaim the rows sooner. An <b>expired</b>
    /// token is deleted regardless of this window.
    /// </summary>
    public int RevokedTokenGraceHours { get; init; } = 24;

    /// <summary>Rows per delete statement. Bounds how long one statement holds locks.</summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>Ceiling per table per sweep; the next sweep continues where this one stopped.</summary>
    public int MaxRowsPerTablePerRun { get; init; } = 5000;
}

/// <summary>What one sweep deleted.</summary>
public sealed record McpOAuthPurgeResult(
    int AuthorizationCodesDeleted,
    int RefreshTokensDeleted,
    int ClientsDeleted,
    DateTime SweptAt)
{
    /// <summary>Every row this sweep removed.</summary>
    public int Total => AuthorizationCodesDeleted + RefreshTokensDeleted + ClientsDeleted;
}

/// <summary>
/// Periodically removes the MCP OAuth rows that can never be used again, so the four tables the OAuth
/// feature added stay bounded (docs/design/mcp-oauth.md §5).
/// </summary>
/// <remarks>
/// <para>
/// <b>What goes.</b> (1) Authorization codes that are expired or already consumed — a code is
/// single-use and short-lived, so a dead one is pure residue. (2) Refresh tokens that are expired, or
/// revoked longer ago than <see cref="McpOAuthPurgeOptions.RevokedTokenGraceHours"/>. (3) Clients that
/// registered, were never used, and are older than
/// <see cref="McpOAuthPurgeOptions.UnclaimedClientRetentionDays"/>.
/// </para>
/// <para>
/// <b>Why a client needs a second look.</b> "Unclaimed" is read from <c>last_used_at</c>, which the Go
/// authorization server stamps best-effort when it issues a token; a stamp that failed would leave a
/// client that is very much in use looking abandoned for ever. So a candidate is also required to have
/// <b>no live credential</b> — no unrevoked, unexpired refresh token and no unconsumed, unexpired
/// authorization code naming it. Deleting a row another request is about to use is the one way a
/// "safe" purge could still break a working client, and that check is what rules it out.
/// </para>
/// <para>
/// <b>Idempotent and bounded.</b> Every deletion targets rows that are already unusable, so a second
/// sweep over the same instant deletes nothing, and any row that crosses its expiry between two sweeps
/// is picked up by the next one. Deletes run in batches of
/// <see cref="McpOAuthPurgeOptions.BatchSize"/> through <c>ExecuteDelete</c> (one statement per batch,
/// no change-tracker load), capped per table per sweep — so a large backlog is reclaimed over a few
/// runs rather than in one long lock. Nothing here is on a request path.
/// </para>
/// </remarks>
public sealed class McpOAuthPurgeService : BackgroundService
{
    /// <summary>Hard cap on a configured batch, so one statement can never be unbounded.</summary>
    public const int MaxBatchSize = 1000;

    /// <summary>Hard cap on a configured per-run ceiling.</summary>
    public const int MaxRowsPerTablePerRunCeiling = 100_000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<McpOAuthPurgeOptions> _options;
    private readonly ILogger<McpOAuthPurgeService> _logger;
    private readonly TimeProvider _clock;

    public McpOAuthPurgeService(
        IServiceScopeFactory scopeFactory,
        IOptions<McpOAuthPurgeOptions> options,
        ILogger<McpOAuthPurgeService> logger,
        TimeProvider? clock = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        McpOAuthPurgeOptions options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogDebug("The MCP OAuth purge is disabled (McpOAuth:Purge:Enabled)");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.InitialDelaySeconds)), stoppingToken);

            using PeriodicTimer timer = new(TimeSpan.FromMinutes(Math.Clamp(options.IntervalMinutes, 1, 24 * 60)));
            do
            {
                try
                {
                    await PurgeOnceAsync(_clock.GetUtcNow(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Hygiene, never a dependency: a database blip must not stop the service. The next
                    // sweep covers the same rows, because nothing here is one-shot.
                    _logger.LogError(ex, "The MCP OAuth purge could not be completed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>
    /// One sweep as of <paramref name="now"/>. Public and clock-injected so a test can advance time
    /// across two sweeps without waiting; returns what it deleted.
    /// </summary>
    public async Task<McpOAuthPurgeResult> PurgeOnceAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        McpOAuthPurgeOptions options = _options.Value;
        int batchSize = Math.Clamp(options.BatchSize, 1, MaxBatchSize);
        int maxRows = Math.Clamp(options.MaxRowsPerTablePerRun, batchSize, MaxRowsPerTablePerRunCeiling);

        DateTime nowUtc = now.UtcDateTime;
        DateTime revokedBefore = nowUtc.AddHours(-Math.Clamp(options.RevokedTokenGraceHours, 1, 24 * 365));
        DateTime unclaimedBefore = nowUtc.AddDays(-Math.Clamp(options.UnclaimedClientRetentionDays, 1, 365));

        using IServiceScope scope = _scopeFactory.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

        int codes = await PurgeAuthorizationCodesAsync(db, nowUtc, batchSize, maxRows, ct);
        int tokens = await PurgeRefreshTokensAsync(db, nowUtc, revokedBefore, batchSize, maxRows, ct);
        int clients = await PurgeUnclaimedClientsAsync(db, nowUtc, unclaimedBefore, batchSize, maxRows, ct);

        var result = new McpOAuthPurgeResult(codes, tokens, clients, nowUtc);
        if (result.Total > 0)
        {
            _logger.LogInformation(
                "MCP OAuth purge removed {Codes} authorization code(s), {Tokens} refresh token(s) and {Clients} unclaimed client(s)",
                result.AuthorizationCodesDeleted, result.RefreshTokensDeleted, result.ClientsDeleted);
        }
        else
        {
            _logger.LogDebug("MCP OAuth purge removed nothing");
        }

        return result;
    }

    private static async Task<int> PurgeAuthorizationCodesAsync(
        InferenceApiDbContext db, DateTime nowUtc, int batchSize, int maxRows, CancellationToken ct)
    {
        int deleted = 0;
        while (deleted < maxRows)
        {
            List<Guid> ids = await db.OAuthAuthorizationCodes
                .Where(c => c.ExpiresAt < nowUtc || c.ConsumedAt != null)
                .OrderBy(c => c.ExpiresAt)
                .Select(c => c.Id)
                .Take(Math.Min(batchSize, maxRows - deleted))
                .ToListAsync(ct);

            if (ids.Count == 0)
                break;

            int removed = await db.OAuthAuthorizationCodes
                .Where(c => ids.Contains(c.Id))
                .ExecuteDeleteAsync(ct);
            if (removed == 0)
                break;

            deleted += removed;
        }

        return deleted;
    }

    private static async Task<int> PurgeRefreshTokensAsync(
        InferenceApiDbContext db, DateTime nowUtc, DateTime revokedBefore, int batchSize, int maxRows, CancellationToken ct)
    {
        int deleted = 0;
        while (deleted < maxRows)
        {
            List<Guid> ids = await db.OAuthRefreshTokens
                .Where(t => t.ExpiresAt < nowUtc || (t.RevokedAt != null && t.RevokedAt < revokedBefore))
                .OrderBy(t => t.ExpiresAt)
                .Select(t => t.Id)
                .Take(Math.Min(batchSize, maxRows - deleted))
                .ToListAsync(ct);

            if (ids.Count == 0)
                break;

            int removed = await db.OAuthRefreshTokens
                .Where(t => ids.Contains(t.Id))
                .ExecuteDeleteAsync(ct);
            if (removed == 0)
                break;

            deleted += removed;
        }

        return deleted;
    }

    private static async Task<int> PurgeUnclaimedClientsAsync(
        InferenceApiDbContext db, DateTime nowUtc, DateTime unclaimedBefore, int batchSize, int maxRows, CancellationToken ct)
    {
        int deleted = 0;
        while (deleted < maxRows)
        {
            List<Guid> candidateIds = await db.OAuthClients
                .Where(c => c.LastUsedAt == null && c.CreatedAt < unclaimedBefore)
                .OrderBy(c => c.CreatedAt)
                .Select(c => c.Id)
                .Take(Math.Min(batchSize, maxRows - deleted))
                .ToListAsync(ct);

            if (candidateIds.Count == 0)
                break;

            List<string> candidateClientIds = await db.OAuthClients
                .Where(c => candidateIds.Contains(c.Id))
                .Select(c => c.ClientId)
                .ToListAsync(ct);

            // A candidate is only deletable when it holds nothing a live request could still use. This
            // is belt-and-braces for a best-effort `last_used_at` stamp — see the type remarks.
            List<string> busy = (await db.OAuthRefreshTokens
                    .Where(t => candidateClientIds.Contains(t.ClientId) && t.RevokedAt == null && t.ExpiresAt >= nowUtc)
                    .Select(t => t.ClientId)
                    .Distinct()
                    .ToListAsync(ct))
                .Concat(await db.OAuthAuthorizationCodes
                    .Where(c => candidateClientIds.Contains(c.ClientId) && c.ConsumedAt == null && c.ExpiresAt >= nowUtc)
                    .Select(c => c.ClientId)
                    .Distinct()
                    .ToListAsync(ct))
                .ToList();

            List<Guid> deletable = await db.OAuthClients
                .Where(c => candidateIds.Contains(c.Id) && !busy.Contains(c.ClientId))
                .Select(c => c.Id)
                .ToListAsync(ct);

            if (deletable.Count == 0)
                break;

            int removed = await db.OAuthClients
                .Where(c => deletable.Contains(c.Id))
                .ExecuteDeleteAsync(ct);
            if (removed == 0)
                break;

            deleted += removed;
        }

        return deleted;
    }
}
