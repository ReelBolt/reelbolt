using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Inference.Api.Services.Notifications;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.SocialPublishing;

/// <summary>What starting an authorization produced: the URL to send the browser to, or why it cannot start (answered 503).</summary>
public sealed record SocialOAuthStart(string? AuthorizeUrl, string? Error)
{
    public const string ProviderNotConfigured = "provider_not_configured";
    public const string PublicBaseUrlMissing = "public_base_url_missing";
}

/// <summary>How an authorization finished. <see cref="SocialOAuthCompletion.Code"/> is the short token the dashboard reads.</summary>
public enum SocialOAuthOutcome
{
    Connected,
    NotConfigured,
    Denied,
    InvalidState,
    ExpiredState,
    SessionMismatch,
    ExchangeFailed,
    AccountUnavailable
}

/// <summary>The result of a callback. <see cref="ReturnPath"/> comes from our own state row, never from the query string.</summary>
public sealed record SocialOAuthCompletion(SocialOAuthOutcome Outcome, string? ReturnPath)
{
    public bool Succeeded => Outcome == SocialOAuthOutcome.Connected;

    public string Code => Outcome switch
    {
        SocialOAuthOutcome.Connected => "connected",
        SocialOAuthOutcome.NotConfigured => "not_configured",
        SocialOAuthOutcome.Denied => "denied",
        SocialOAuthOutcome.ExpiredState => "expired",
        SocialOAuthOutcome.SessionMismatch => "session_mismatch",
        SocialOAuthOutcome.InvalidState => "invalid_state",
        SocialOAuthOutcome.AccountUnavailable => "account_unavailable",
        _ => "exchange_failed"
    };
}

/// <summary>
/// Connects an organization's social account and keeps its access token usable. Modelled on
/// <see cref="ICloudStorageConnectionService"/> (hashed single-use state, PKCE with the verifier encrypted,
/// tokens through <c>ISecretProtector</c>, refresh with a 2-minute margin and one forced retry on 401), with
/// one difference: the refresh runs under a cross-replica guard (<see cref="SocialRefreshLock"/>), because a
/// refresh token that rotates is lost if two replicas spend it at once.
/// </summary>
public interface ISocialConnectionService
{
    Task<SocialOAuthStart> StartAsync(SocialPlatformKind kind, Guid userId, Guid organizationId, string? returnTo, CancellationToken ct);

    /// <summary>Finishes an authorization. <paramref name="callerUserId"/> is the signed-in person making the callback (null without a session).</summary>
    Task<SocialOAuthCompletion> CompleteAsync(
        SocialPlatformKind kind, string? state, string? code, string? error, Guid? callerUserId, CancellationToken ct);

    /// <summary>A usable access token; refreshes under the account's guard when it expires within the margin (or always, with <paramref name="forceRefresh"/>).</summary>
    Task<string> GetAccessTokenAsync(SocialAccount account, bool forceRefresh, CancellationToken ct);

    /// <summary>Runs one platform call with the account's token; on <see cref="SocialErrorKind.Unauthorized"/> refreshes once and retries once.</summary>
    Task<T> CallAsync<T>(SocialAccount account, Func<string, Task<T>> call, CancellationToken ct);

    /// <summary>
    /// Revokes at the platform (best effort), deletes the tokens now, deletes the account's metrics and cancels its
    /// unsent posts. The row stays as <c>revoked</c> so posted history keeps its account. False when the account is not
    /// this organization's.
    /// </summary>
    Task<bool> DisconnectAsync(Guid accountId, Guid organizationId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Organization deletion is a soft delete, so nothing cascades: revokes (at the platform, best effort), deletes the
    /// tokens and metrics of, and cancels the queued posts of, up to <paramref name="limit"/> accounts of deleted
    /// organizations. Returns how many it finished; the sweeper calls it each pass.
    /// </summary>
    Task<int> PurgeDeletedOrganizationsAsync(int limit, CancellationToken ct);
}

/// <inheritdoc />
public sealed class SocialConnectionService : ISocialConnectionService
{
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    private readonly InferenceApiDbContext _db;
    private readonly ISocialPlatformRegistry _platforms;
    private readonly SocialPublishingOptions _options;
    private readonly ISecretProtector _secrets;
    private readonly SocialRefreshLock _refreshLock;
    private readonly TimeProvider _clock;
    private readonly IUserActivityNotifier? _notifier;
    private readonly ILogger<SocialConnectionService> _logger;

    public SocialConnectionService(
        InferenceApiDbContext db,
        ISocialPlatformRegistry platforms,
        IOptions<SocialPublishingOptions> options,
        ISecretProtector secrets,
        SocialRefreshLock refreshLock,
        ILogger<SocialConnectionService> logger,
        TimeProvider? clock = null,
        IUserActivityNotifier? notifier = null)
    {
        _notifier = notifier;
        _db = db;
        _platforms = platforms;
        _options = options.Value;
        _secrets = secrets;
        _refreshLock = refreshLock;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    // ------------------------------------------------------------------ OAuth

    public async Task<SocialOAuthStart> StartAsync(SocialPlatformKind kind, Guid userId, Guid organizationId, string? returnTo, CancellationToken ct)
    {
        ISocialPlatform? platform = _platforms.Get(kind);
        if (platform is not { IsConfigured: true }) return new SocialOAuthStart(null, SocialOAuthStart.ProviderNotConfigured);

        Uri? redirectUri = _options.RedirectUriFor(kind);
        if (redirectUri is null) return new SocialOAuthStart(null, SocialOAuthStart.PublicBaseUrlMissing);

        DateTime now = Now();
        await DiscardSpentStatesAsync(userId, now, ct);

        string state = Base64Url(RandomNumberGenerator.GetBytes(32));
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));

        // Built before the ticket is stored, so a platform that cannot build a URL leaves nothing behind.
        Uri authorizeUrl = platform.BuildAuthorizationUrl(state, CloudStorageConnectionService.Challenge(verifier), redirectUri);

        _db.SocialOAuthStates.Add(new SocialOAuthState
        {
            Id = Guid.NewGuid(),
            StateHash = CloudStorageConnectionService.Hash(state),
            UserId = userId,
            OrganizationId = organizationId,
            Platform = kind,
            ProtectedCodeVerifier = _secrets.Protect(verifier),
            ReturnPath = CloudStorageConnectionService.NormalizeReturnPath(returnTo),
            CreatedAt = now,
            ExpiresAt = now + StateLifetime
        });
        await _db.SaveChangesAsync(ct);

        return new SocialOAuthStart(authorizeUrl.AbsoluteUri, null);
    }

    public async Task<SocialOAuthCompletion> CompleteAsync(
        SocialPlatformKind kind, string? state, string? code, string? error, Guid? callerUserId, CancellationToken ct)
    {
        ISocialPlatform? platform = _platforms.Get(kind);
        if (platform is not { IsConfigured: true }) return new SocialOAuthCompletion(SocialOAuthOutcome.NotConfigured, null);

        bool denied = !string.IsNullOrWhiteSpace(error);
        SocialOAuthState? ticket = null;
        if (!string.IsNullOrWhiteSpace(state) && state.Length <= 512)
        {
            string stateHash = CloudStorageConnectionService.Hash(state);
            ticket = await _db.SocialOAuthStates
                .FirstOrDefaultAsync(s => s.StateHash == stateHash && s.ConsumedAt == null && s.Platform == kind, ct);
        }

        if (ticket is null)
        {
            // Unknown, already spent, minted for another platform, or never minted here. A replay finds no row.
            if (!denied) _logger.LogWarning("Refused a {Platform} OAuth callback carrying a state that is unknown or already spent", kind);
            return new SocialOAuthCompletion(denied ? SocialOAuthOutcome.Denied : SocialOAuthOutcome.InvalidState, null);
        }

        // Bound to the person who started it. Deliberately NOT consumed on a mismatch, so somebody who knows
        // the state cannot burn the legitimate person's authorization with one request.
        if (callerUserId is not { } caller || caller != ticket.UserId)
        {
            _logger.LogWarning("Refused a {Platform} OAuth callback made by somebody other than the person who started it", kind);
            return new SocialOAuthCompletion(SocialOAuthOutcome.SessionMismatch, null);
        }

        DateTime now = Now();
        ticket.ConsumedAt = now;

        if (ticket.ExpiresAt <= now)
        {
            await _db.SaveChangesAsync(ct);
            return new SocialOAuthCompletion(SocialOAuthOutcome.ExpiredState, ticket.ReturnPath);
        }

        if (denied)
        {
            await _db.SaveChangesAsync(ct);
            return new SocialOAuthCompletion(SocialOAuthOutcome.Denied, ticket.ReturnPath);
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            await _db.SaveChangesAsync(ct);
            return new SocialOAuthCompletion(SocialOAuthOutcome.InvalidState, ticket.ReturnPath);
        }

        Uri? redirectUri = _options.RedirectUriFor(kind);
        if (redirectUri is null)
        {
            await _db.SaveChangesAsync(ct);
            return new SocialOAuthCompletion(SocialOAuthOutcome.NotConfigured, ticket.ReturnPath);
        }

        if (!_secrets.TryUnprotect(ticket.ProtectedCodeVerifier, out string verifier) || string.IsNullOrWhiteSpace(verifier))
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogError("Could not decrypt a {Platform} OAuth code verifier; the Data Protection key ring may have changed", kind);
            return new SocialOAuthCompletion(SocialOAuthOutcome.ExchangeFailed, ticket.ReturnPath);
        }

        // Spent before the exchange, so a retry a moment later finds no live ticket.
        await _db.SaveChangesAsync(ct);

        // The person may have been demoted, removed or the organization deleted since they started: re-read their
        // CURRENT authority in the ticket's organization and require what the start route required.
        if (!await IsCurrentAdminAsync(ticket.UserId, ticket.OrganizationId, ct))
        {
            _logger.LogWarning("Refused a {Platform} OAuth callback: the person no longer administers the organization", kind);
            return new SocialOAuthCompletion(SocialOAuthOutcome.InvalidState, ticket.ReturnPath);
        }

        SocialTokenSet tokens;
        try
        {
            tokens = await platform.ExchangeCodeAsync(code, verifier, redirectUri, ct);
        }
        catch (SocialApiException ex)
        {
            _logger.LogWarning("{Platform} refused an authorization-code exchange ({Kind})", kind, ex.Kind);
            return new SocialOAuthCompletion(SocialOAuthOutcome.ExchangeFailed, ticket.ReturnPath);
        }

        SocialAccountInfo info;
        try
        {
            info = await platform.GetAccountAsync(tokens.AccessToken, ct);
        }
        catch (SocialApiException ex)
        {
            _logger.LogWarning("Reading the {Platform} account after a code exchange failed ({Kind})", kind, ex.Kind);
            return new SocialOAuthCompletion(SocialOAuthOutcome.AccountUnavailable, ticket.ReturnPath);
        }

        if (string.IsNullOrWhiteSpace(info.AccountId))
            return new SocialOAuthCompletion(SocialOAuthOutcome.AccountUnavailable, ticket.ReturnPath);

        await StoreAsync(ticket.UserId, ticket.OrganizationId, kind, info, tokens, ct);

        _logger.LogInformation("Connected a {Platform} account in organization {OrganizationId}", kind, ticket.OrganizationId);
        return new SocialOAuthCompletion(SocialOAuthOutcome.Connected, ticket.ReturnPath);
    }

    /// <summary>Upserts by (organization, platform, remote id). The tokens are encrypted here and nowhere else.</summary>
    private async Task StoreAsync(Guid userId, Guid organizationId, SocialPlatformKind kind, SocialAccountInfo info, SocialTokenSet tokens, CancellationToken ct)
    {
        string remoteId = Truncate(info.AccountId.Trim(), 300)!;
        SocialAccount? existing = await FindByRemoteAsync(organizationId, kind, remoteId, ct);
        if (existing is null)
        {
            existing = new SocialAccount
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                Platform = kind,
                RemoteAccountId = remoteId,
                CreatedAt = Now()
            };
            _db.SocialAccounts.Add(existing);
        }

        Apply(existing, userId, info, tokens);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two tabs finished the same authorization at once; the unique index caught the second insert.
            _db.Entry(existing).State = EntityState.Detached;
            SocialAccount? winner = await FindByRemoteAsync(organizationId, kind, remoteId, ct);
            if (winner is null) throw;
            Apply(winner, userId, info, tokens);
            await _db.SaveChangesAsync(ct);
        }
    }

    private Task<SocialAccount?> FindByRemoteAsync(Guid organizationId, SocialPlatformKind kind, string remoteId, CancellationToken ct) =>
        _db.SocialAccounts.IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .FirstOrDefaultAsync(a => a.OrganizationId == organizationId && a.Platform == kind && a.RemoteAccountId == remoteId, ct);

    private void Apply(SocialAccount account, Guid userId, SocialAccountInfo info, SocialTokenSet tokens)
    {
        account.DisplayName = Truncate(info.DisplayName, 300);
        account.AvatarUrl = Truncate(info.AvatarUrl, 2000);
        account.ProtectedAccessToken = _secrets.Protect(tokens.AccessToken);
        // A platform that issues no new refresh token on re-consent keeps the one already stored.
        if (tokens.RefreshToken is { Length: > 0 } refresh) account.ProtectedRefreshToken = _secrets.Protect(refresh);
        account.AccessTokenExpiresAt = tokens.AccessTokenExpiresAt;
        account.GrantedScopes = Truncate(tokens.Scope, 2000) ?? account.GrantedScopes;
        account.Status = SocialAccountStatus.Active;
        account.LastError = null;
        account.ConnectedByUserId = userId;
        account.UpdatedAt = Now();
    }

    private async Task DiscardSpentStatesAsync(Guid userId, DateTime now, CancellationToken ct)
    {
        List<SocialOAuthState> stale = await _db.SocialOAuthStates
            .Where(s => s.UserId == userId && (s.ConsumedAt != null || s.ExpiresAt <= now))
            .ToListAsync(ct);
        if (stale.Count == 0) return;
        _db.SocialOAuthStates.RemoveRange(stale);
        await _db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ disconnect

    public async Task<bool> DisconnectAsync(Guid accountId, Guid organizationId, Guid userId, CancellationToken ct)
    {
        // The authority is read from the database now, not from the session's claims.
        if (!await IsCurrentAdminAsync(userId, organizationId, ct)) return false;

        return await RevokeAccountAsync(accountId, organizationId, "disconnected", ct);
    }

    public async Task<int> PurgeDeletedOrganizationsAsync(int limit, CancellationToken ct)
    {
        // Organizations are only ever soft-deleted (the Go API, or the account purge), so nothing cascades: this is the
        // hook. Every account of a deleted organization that still holds tokens (or is not yet revoked) is revoked at
        // the platform, loses its tokens and metrics, and its queued posts are cancelled.
        List<(Guid Id, Guid OrganizationId)> due = (await _db.SocialAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => (a.Status != SocialAccountStatus.Revoked || a.ProtectedAccessToken != string.Empty || a.ProtectedRefreshToken != null)
                        && _db.Organizations.IgnoreQueryFilters().Any(o => o.Id == a.OrganizationId && o.DeletedAt != null))
            .OrderBy(a => a.UpdatedAt)
            .Select(a => new { a.Id, a.OrganizationId })
            .Take(Math.Max(1, limit))
            .ToListAsync(ct)).Select(a => (a.Id, a.OrganizationId)).ToList();

        int purged = 0;
        foreach ((Guid id, Guid orgId) in due)
        {
            try
            {
                if (await RevokeAccountAsync(id, orgId, "organization_deleted", ct)) purged++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Next sweep tries again; never log the exception text (it could carry a platform response).
                _logger.LogWarning("Purging social account {AccountId} of a deleted organization failed ({Type})", id, ex.GetType().Name);
                _db.ChangeTracker.Clear();
            }
        }

        return purged;
    }

    /// <summary>
    /// The shared revoke: under the account's refresh guard, revoke at the platform (best effort), blank the tokens, delete
    /// the account's metrics, cancel its unsent posts and mark it <c>revoked</c>. The row stays so posted history keeps its account.
    /// </summary>
    private async Task<bool> RevokeAccountAsync(Guid accountId, Guid organizationId, string reason, CancellationToken ct)
    {
        SocialAccount? account = await _db.SocialAccounts.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == accountId && a.OrganizationId == organizationId, ct);
        if (account is null) return false;

        // Same per-account guard as a token refresh: a refresh in flight finishes (or is refused) before the account is
        // revoked, and one that starts later reloads the row under the guard and sees `revoked`. Without this a refresh
        // could write fresh tokens and Active over a disconnected account.
        await using SocialRefreshGuard guard = await _refreshLock.AcquireAsync(_db, account.Id, ct);
        await _db.Entry(account).ReloadAsync(ct);
        if (_db.Entry(account).State == EntityState.Detached) return false;

        ISocialPlatform? platform = _platforms.Get(account.Platform);
        if (platform is { IsConfigured: true } && _secrets.TryUnprotect(account.ProtectedAccessToken, out string accessToken))
        {
            string? refreshToken = _secrets.TryUnprotect(account.ProtectedRefreshToken, out string refresh) ? refresh : null;
            try
            {
                await platform.RevokeAsync(new SocialTokenSet(accessToken, refreshToken, account.AccessTokenExpiresAt, null), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best effort: the tokens are deleted below either way.
                _logger.LogWarning("Revoking a {Platform} grant on disconnect failed ({Type})", account.Platform, ex.GetType().Name);
            }
        }

        DateTime now = Now();
        List<SocialPost> posts = await _db.SocialPosts.IgnoreQueryFilters().Where(p => p.SocialAccountId == account.Id).ToListAsync(ct);
        List<Guid> postIds = posts.Select(p => p.Id).ToList();
        List<SocialPostMetric> metrics = await _db.SocialPostMetrics.Where(m => postIds.Contains(m.PostId)).ToListAsync(ct);
        _db.SocialPostMetrics.RemoveRange(metrics);

        foreach (SocialPost post in posts.Where(p => p.Status is SocialPostStatus.Draft or SocialPostStatus.Scheduled))
        {
            post.Status = SocialPostStatus.Cancelled;
            post.FailureCode = reason == "disconnected" ? "account_disconnected" : "organization_deleted";
            post.FailureMessage = reason == "disconnected"
                ? "The account was disconnected before this post was sent."
                : "The workspace was deleted before this post was sent.";
            post.UpdatedAt = now;
        }

        account.ProtectedAccessToken = string.Empty;
        account.ProtectedRefreshToken = null;
        account.AccessTokenExpiresAt = null;
        account.Status = SocialAccountStatus.Revoked;
        account.LastError = null;
        account.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        await guard.CommitAsync(ct);

        _logger.LogInformation("Revoked ({Reason}) {Platform} account {AccountId}; {Metrics} metric row(s) deleted", reason, account.Platform, account.Id, metrics.Count);
        return true;
    }

    // ------------------------------------------------------------------ tokens

    public async Task<string> GetAccessTokenAsync(SocialAccount account, bool forceRefresh, CancellationToken ct)
    {
        if (account.Status == SocialAccountStatus.Revoked)
            throw new SocialApiException("The account was disconnected.", SocialErrorKind.Unauthorized);

        if (!forceRefresh && !IsExpiring(account) && _secrets.TryUnprotect(account.ProtectedAccessToken, out string current))
            return current;

        ISocialPlatform platform = ConfiguredPlatform(account.Platform);
        string tokenBefore = account.ProtectedAccessToken;

        // A deleted organization's accounts are never refreshed.
        if (!await _db.Organizations.AnyAsync(o => o.Id == account.OrganizationId && o.DeletedAt == null, ct))
            throw new SocialApiException($"The organization of {account.Platform} account {account.Id} no longer exists.", SocialErrorKind.Unauthorized);

        await using SocialRefreshGuard guard = await _refreshLock.AcquireAsync(_db, account.Id, ct);

        // Another scope or replica may have refreshed while this one waited: read what is stored now.
        await ReloadAsync(account, ct);

        if (!IsExpiring(account) && _secrets.TryUnprotect(account.ProtectedAccessToken, out string fresh) &&
            (!forceRefresh || !string.Equals(account.ProtectedAccessToken, tokenBefore, StringComparison.Ordinal)))
        {
            return fresh;
        }

        if (!_secrets.TryUnprotect(account.ProtectedRefreshToken, out string refreshToken) || string.IsNullOrWhiteSpace(refreshToken))
        {
            await MarkBrokenAsync(account, guard, ct);
            throw new SocialApiException($"{account.Platform} account {account.Id} has no usable refresh token.", SocialErrorKind.Unauthorized);
        }

        SocialTokenSet tokens;
        try
        {
            tokens = await platform.RefreshAsync(refreshToken, ct);
        }
        catch (SocialApiException ex) when (ex.Kind == SocialErrorKind.Unauthorized)
        {
            _logger.LogWarning("{Platform} refused to refresh account {AccountId}", account.Platform, account.Id);
            await MarkBrokenAsync(account, guard, ct);
            throw new SocialApiException($"{account.Platform} refused to refresh account {account.Id}.", SocialErrorKind.Unauthorized, ex.StatusCode);
        }

        account.ProtectedAccessToken = _secrets.Protect(tokens.AccessToken);
        if (tokens.RefreshToken is { Length: > 0 } rotated) account.ProtectedRefreshToken = _secrets.Protect(rotated);
        account.AccessTokenExpiresAt = tokens.AccessTokenExpiresAt;
        if (!string.IsNullOrWhiteSpace(tokens.Scope)) account.GrantedScopes = Truncate(tokens.Scope, 2000);
        account.Status = SocialAccountStatus.Active;
        account.LastError = null;
        account.UpdatedAt = Now();
        await _db.SaveChangesAsync(ct);
        await guard.CommitAsync(ct);

        return tokens.AccessToken;
    }

    public async Task<T> CallAsync<T>(SocialAccount account, Func<string, Task<T>> call, CancellationToken ct)
    {
        string token = await GetAccessTokenAsync(account, forceRefresh: false, ct);
        try
        {
            return await call(token);
        }
        catch (SocialApiException ex) when (ex.Kind == SocialErrorKind.Unauthorized)
        {
            // The platform may have revoked the token before its stated expiry: refresh once, retry once.
            token = await GetAccessTokenAsync(account, forceRefresh: true, ct);
            return await call(token);
        }
    }

    private bool IsExpiring(SocialAccount account) =>
        account.AccessTokenExpiresAt is { } expiry && expiry <= Now() + RefreshMargin;

    private ISocialPlatform ConfiguredPlatform(SocialPlatformKind kind)
    {
        ISocialPlatform? platform = _platforms.Get(kind);
        return platform is { IsConfigured: true }
            ? platform
            : throw new SocialApiException($"{kind} is not configured.", SocialErrorKind.NotConfigured);
    }

    private async Task ReloadAsync(SocialAccount account, CancellationToken ct)
    {
        var entry = _db.Entry(account);
        if (entry.State is EntityState.Unchanged or EntityState.Modified)
        {
            await entry.ReloadAsync(ct);
            if (entry.State == EntityState.Detached)
                throw new SocialApiException($"{account.Platform} account {account.Id} was disconnected.", SocialErrorKind.Unauthorized);
            if (account.Status == SocialAccountStatus.Revoked)
                throw new SocialApiException($"{account.Platform} account {account.Id} was disconnected.", SocialErrorKind.Unauthorized);
        }
    }

    private async Task MarkBrokenAsync(SocialAccount account, SocialRefreshGuard guard, CancellationToken ct)
    {
        // Tell the person once, on the change into reconnect_needed: later failed refreshes of an already flagged account stay quiet.
        bool firstTime = account.Status != SocialAccountStatus.ReconnectNeeded;
        account.Status = SocialAccountStatus.ReconnectNeeded;
        account.LastError =
            $"{SocialPlatforms.DisplayName(account.Platform)} no longer accepts ReelBolt's access to this account. "
            + "The access may have been removed or may have expired. Reconnect the account.";
        account.UpdatedAt = Now();
        if (_db.Entry(account).State != EntityState.Detached)
        {
            await _db.SaveChangesAsync(ct);
            await guard.CommitAsync(ct);
            if (firstTime && _notifier is not null)
            {
                string name = SocialPlatforms.DisplayName(account.Platform);
                await _notifier.SocialPublishingAsync(account.OrganizationId, null, account.ConnectedByUserId,
                    $"Reconnect your {name} account", $"{name} stopped accepting the connection. Open Publishing and reconnect it so queued posts can go out.",
                    CancellationToken.None);
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<bool> IsCurrentAdminAsync(Guid userId, Guid organizationId, CancellationToken ct)
    {
        OrganizationRole? role = await _db.OrganizationMemberships
            .Where(m => m.OrganizationId == organizationId && m.UserId == userId)
            .Select(m => (OrganizationRole?)m.Role)
            .FirstOrDefaultAsync(ct);
        return OrgAuthority.IsOrganizationAdmin(role);
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length > max ? value[..max] : value;

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
