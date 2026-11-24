using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.CloudStorage;

/// <summary>
/// What starting an authorization produced: the URL to send the browser to, or the reason it cannot
/// start (<c>provider_not_configured</c>, <c>public_base_url_missing</c>), which the API answers <c>503</c>.
/// </summary>
public sealed record CloudOAuthStart(string? AuthorizeUrl, string? Error)
{
    public const string ProviderNotConfigured = "provider_not_configured";
    public const string PublicBaseUrlMissing = "public_base_url_missing";
}

/// <summary>How an authorization finished. <see cref="CloudOAuthCompletion.Code"/> is the short token the dashboard reads.</summary>
public enum CloudOAuthOutcome
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

/// <summary>
/// The result of a callback. <see cref="ReturnPath"/> comes from our own state row (validated when it was
/// stored), never from the callback's query string; null means the default page.
/// </summary>
public sealed record CloudOAuthCompletion(CloudOAuthOutcome Outcome, string? ReturnPath)
{
    public bool Succeeded => Outcome == CloudOAuthOutcome.Connected;

    public string Code => Outcome switch
    {
        CloudOAuthOutcome.Connected => "connected",
        CloudOAuthOutcome.NotConfigured => "not_configured",
        CloudOAuthOutcome.Denied => "denied",
        CloudOAuthOutcome.ExpiredState => "expired",
        CloudOAuthOutcome.SessionMismatch => "session_mismatch",
        CloudOAuthOutcome.InvalidState => "invalid_state",
        CloudOAuthOutcome.AccountUnavailable => "account_unavailable",
        _ => "exchange_failed"
    };
}

/// <summary>
/// Connects a person's Google Drive / OneDrive / Dropbox account to a workspace and keeps its access
/// token usable (docs/design/cloud-drive-sync.md "Core services"). The provider-specific half is
/// <see cref="ICloudStorageProvider"/>; this class is the same for all three.
///
/// <para>
/// <b>The OAuth defences.</b> A <c>state</c> this deployment minted, stored only as a SHA-256 hash, living
/// <see cref="StateLifetime"/> and spendable exactly once; PKCE (S256) with the verifier encrypted in our
/// database, so an intercepted code is worthless; and the ticket is bound to the person who started it:
/// the callback, reached by a top-level navigation that carries the session cookie, must be made by that
/// same signed-in person, or nothing is exchanged and the ticket is left alone. That binding is what stops
/// somebody from sending a victim a consent link that would connect the VICTIM's drive to the
/// attacker's account. Who is connected, and in which workspace, is read from our ticket, never from the URL.
/// </para>
/// </summary>
public interface ICloudStorageConnectionService
{
    Task<CloudOAuthStart> StartAsync(CloudStorageProviderKind kind, Guid userId, Guid organizationId, string? returnTo, CancellationToken ct);

    /// <summary>
    /// Finishes an authorization. <paramref name="callerUserId"/> is the signed-in person making the
    /// callback request (null when the request carries no valid session).
    /// </summary>
    Task<CloudOAuthCompletion> CompleteAsync(
        CloudStorageProviderKind kind,
        string? state,
        string? code,
        string? error,
        Guid? callerUserId,
        CancellationToken ct);

    /// <summary>The connection, only when it belongs to this person in this workspace.</summary>
    Task<CloudStorageConnection?> FindOwnedAsync(Guid connectionId, Guid userId, Guid organizationId, CancellationToken ct);

    /// <summary>
    /// A usable access token. Refreshes when the stored one expires within <see cref="RefreshMargin"/>
    /// (or always, with <paramref name="forceRefresh"/>), storing a rotated refresh token. A refresh the
    /// provider refuses sets <see cref="CloudStorageConnection.LastError"/> and throws
    /// <see cref="CloudStorageErrorKind.Unauthorized"/>: the person must reconnect.
    /// </summary>
    Task<string> GetAccessTokenAsync(CloudStorageConnection connection, bool forceRefresh, CancellationToken ct);

    /// <summary>
    /// Runs one provider call with the connection's token; on <see cref="CloudStorageErrorKind.Unauthorized"/>
    /// refreshes once and retries once. A second refusal propagates.
    /// </summary>
    Task<T> CallAsync<T>(CloudStorageConnection connection, Func<string, Task<T>> call, CancellationToken ct);

    /// <summary>
    /// Revokes (best effort) and deletes the connection. Links that used it stay, with no connection;
    /// their files stay too. False when it is not this person's connection in this workspace.
    /// </summary>
    Task<bool> DisconnectAsync(Guid connectionId, Guid userId, Guid organizationId, CancellationToken ct);
}

/// <inheritdoc />
public sealed partial class CloudStorageConnectionService : ICloudStorageConnectionService
{
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    public const int MaxReturnPathLength = 512;

    /// <summary>
    /// One refresh at a time per connection, across the scopes of this process: two syncs of links
    /// sharing an account must not both spend a refresh token that the provider rotates.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> RefreshGates = new();

    private readonly InferenceApiDbContext _db;
    private readonly ICloudStorageProviderRegistry _providers;
    private readonly CloudStorageOptions _options;
    private readonly ISecretProtector _secrets;
    private readonly TimeProvider _clock;
    private readonly ILogger<CloudStorageConnectionService> _logger;

    public CloudStorageConnectionService(
        InferenceApiDbContext db,
        ICloudStorageProviderRegistry providers,
        IOptions<CloudStorageOptions> options,
        ISecretProtector secrets,
        ILogger<CloudStorageConnectionService> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _providers = providers;
        _options = options.Value;
        _secrets = secrets;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    // ------------------------------------------------------------------ OAuth

    public async Task<CloudOAuthStart> StartAsync(
        CloudStorageProviderKind kind,
        Guid userId,
        Guid organizationId,
        string? returnTo,
        CancellationToken ct)
    {
        ICloudStorageProvider? provider = _providers.Get(kind);
        if (provider is not { IsConfigured: true }) return new CloudOAuthStart(null, CloudOAuthStart.ProviderNotConfigured);

        Uri? redirectUri = _options.RedirectUriFor(kind);
        if (redirectUri is null) return new CloudOAuthStart(null, CloudOAuthStart.PublicBaseUrlMissing);

        DateTime now = Now();
        await DiscardSpentStatesAsync(userId, now, ct);

        string state = Base64Url(RandomNumberGenerator.GetBytes(32));
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));

        // Built before the ticket is stored, so a provider that cannot build a URL leaves nothing behind.
        Uri authorizeUrl = provider.BuildAuthorizationUrl(state, Challenge(verifier), redirectUri);

        _db.CloudStorageOAuthStates.Add(new CloudStorageOAuthState
        {
            Id = Guid.NewGuid(),
            StateHash = Hash(state),
            UserId = userId,
            OrganizationId = organizationId,
            Provider = kind,
            ProtectedCodeVerifier = _secrets.Protect(verifier),
            ReturnPath = NormalizeReturnPath(returnTo),
            CreatedAt = now,
            ExpiresAt = now + StateLifetime
        });
        await _db.SaveChangesAsync(ct);

        return new CloudOAuthStart(authorizeUrl.AbsoluteUri, null);
    }

    public async Task<CloudOAuthCompletion> CompleteAsync(
        CloudStorageProviderKind kind,
        string? state,
        string? code,
        string? error,
        Guid? callerUserId,
        CancellationToken ct)
    {
        ICloudStorageProvider? provider = _providers.Get(kind);
        if (provider is not { IsConfigured: true }) return new CloudOAuthCompletion(CloudOAuthOutcome.NotConfigured, null);

        bool denied = !string.IsNullOrWhiteSpace(error);
        CloudStorageOAuthState? ticket = null;
        if (!string.IsNullOrWhiteSpace(state) && state.Length <= 512)
        {
            string stateHash = Hash(state);
            ticket = await _db.CloudStorageOAuthStates
                .FirstOrDefaultAsync(s => s.StateHash == stateHash && s.ConsumedAt == null && s.Provider == kind, ct);
        }

        if (ticket is null)
        {
            // Unknown, already spent, minted for another provider, or never minted here. A replay finds no row.
            if (!denied) _logger.LogWarning("Refused a {Provider} OAuth callback carrying a state that is unknown or already spent", kind);
            return new CloudOAuthCompletion(denied ? CloudOAuthOutcome.Denied : CloudOAuthOutcome.InvalidState, null);
        }

        // Bound to the person who started it. Deliberately NOT consumed on a mismatch: somebody who knows
        // the state could otherwise burn the legitimate person's authorization with one request.
        if (callerUserId is not { } caller || caller != ticket.UserId)
        {
            _logger.LogWarning("Refused a {Provider} OAuth callback made by somebody other than the person who started it", kind);
            return new CloudOAuthCompletion(CloudOAuthOutcome.SessionMismatch, null);
        }

        DateTime now = Now();
        ticket.ConsumedAt = now;

        if (ticket.ExpiresAt <= now)
        {
            await _db.SaveChangesAsync(ct);
            return new CloudOAuthCompletion(CloudOAuthOutcome.ExpiredState, ticket.ReturnPath);
        }

        if (denied)
        {
            // The person pressed "Cancel" on the consent screen. Not an attack and not a fault.
            await _db.SaveChangesAsync(ct);
            return new CloudOAuthCompletion(CloudOAuthOutcome.Denied, ticket.ReturnPath);
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            await _db.SaveChangesAsync(ct);
            return new CloudOAuthCompletion(CloudOAuthOutcome.InvalidState, ticket.ReturnPath);
        }

        Uri? redirectUri = _options.RedirectUriFor(kind);
        if (redirectUri is null)
        {
            await _db.SaveChangesAsync(ct);
            return new CloudOAuthCompletion(CloudOAuthOutcome.NotConfigured, ticket.ReturnPath);
        }

        if (!_secrets.TryUnprotect(ticket.ProtectedCodeVerifier, out string verifier) || string.IsNullOrWhiteSpace(verifier))
        {
            // Fail closed rather than exchanging without the PKCE verifier.
            await _db.SaveChangesAsync(ct);
            _logger.LogError("Could not decrypt a {Provider} OAuth code verifier; the Data Protection key ring may have changed", kind);
            return new CloudOAuthCompletion(CloudOAuthOutcome.ExchangeFailed, ticket.ReturnPath);
        }

        // Spent before the exchange, so a retry a moment later finds no live ticket. Two simultaneous
        // callbacks are harmless for the reason GitHub's are: an authorization code is single-use.
        await _db.SaveChangesAsync(ct);

        CloudTokenSet tokens;
        try
        {
            tokens = await provider.ExchangeCodeAsync(code, verifier, redirectUri, ct);
        }
        catch (CloudStorageApiException ex)
        {
            _logger.LogWarning("{Provider} refused an authorization-code exchange ({Kind}: {Reason})", kind, ex.Kind, ex.Message);
            return new CloudOAuthCompletion(CloudOAuthOutcome.ExchangeFailed, ticket.ReturnPath);
        }

        CloudAccountInfo account;
        try
        {
            account = await provider.GetAccountAsync(tokens.AccessToken, ct);
        }
        catch (CloudStorageApiException ex)
        {
            _logger.LogWarning("Reading the {Provider} account after a code exchange failed ({Kind}: {Reason})", kind, ex.Kind, ex.Message);
            return new CloudOAuthCompletion(CloudOAuthOutcome.AccountUnavailable, ticket.ReturnPath);
        }

        if (string.IsNullOrWhiteSpace(account.AccountId))
            return new CloudOAuthCompletion(CloudOAuthOutcome.AccountUnavailable, ticket.ReturnPath);

        await StoreAsync(ticket.UserId, ticket.OrganizationId, kind, account, tokens, ct);

        _logger.LogInformation(
            "Connected a {Provider} account for user {UserId} in organization {OrganizationId}",
            kind, ticket.UserId, ticket.OrganizationId);

        return new CloudOAuthCompletion(CloudOAuthOutcome.Connected, ticket.ReturnPath);
    }

    /// <summary>Upserts by (person, workspace, provider, account id). The tokens are encrypted here and nowhere else.</summary>
    private async Task StoreAsync(
        Guid userId,
        Guid organizationId,
        CloudStorageProviderKind kind,
        CloudAccountInfo account,
        CloudTokenSet tokens,
        CancellationToken ct)
    {
        string accountId = Truncate(account.AccountId.Trim(), 300)!;
        CloudStorageConnection? existing = await FindByAccountAsync(userId, organizationId, kind, accountId, ct);
        if (existing is null)
        {
            existing = new CloudStorageConnection
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                OrganizationId = organizationId,
                Provider = kind,
                AccountId = accountId,
                CreatedAt = Now()
            };
            _db.CloudStorageConnections.Add(existing);
        }

        Apply(existing, account, tokens);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two tabs finished the same authorization at once and the unique index caught the second
            // insert. The winning row is as valid as this one: update it instead of failing.
            _db.Entry(existing).State = EntityState.Detached;
            CloudStorageConnection? winner = await FindByAccountAsync(userId, organizationId, kind, accountId, ct);
            if (winner is null) throw;
            Apply(winner, account, tokens);
            await _db.SaveChangesAsync(ct);
        }
    }

    private Task<CloudStorageConnection?> FindByAccountAsync(
        Guid userId, Guid organizationId, CloudStorageProviderKind kind, string accountId, CancellationToken ct) =>
        _db.CloudStorageConnections.FirstOrDefaultAsync(
            c => c.UserId == userId && c.OrganizationId == organizationId && c.Provider == kind && c.AccountId == accountId, ct);

    private void Apply(CloudStorageConnection connection, CloudAccountInfo account, CloudTokenSet tokens)
    {
        connection.AccountEmail = Truncate(account.Email, 320);
        connection.AccountDisplayName = Truncate(account.DisplayName, 300);
        connection.ProtectedAccessToken = _secrets.Protect(tokens.AccessToken);
        // A provider that issues no new refresh token on re-consent keeps the one already stored.
        if (tokens.RefreshToken is { Length: > 0 } refresh) connection.ProtectedRefreshToken = _secrets.Protect(refresh);
        connection.AccessTokenExpiresAt = tokens.AccessTokenExpiresAt;
        connection.GrantedScopes = Truncate(tokens.Scope, 2000) ?? connection.GrantedScopes;
        connection.LastError = null;
        connection.UpdatedAt = Now();
    }

    /// <summary>Drops this person's spent and expired tickets, on start, so an idle deployment accumulates nothing.</summary>
    private async Task DiscardSpentStatesAsync(Guid userId, DateTime now, CancellationToken ct)
    {
        List<CloudStorageOAuthState> stale = await _db.CloudStorageOAuthStates
            .Where(s => s.UserId == userId && (s.ConsumedAt != null || s.ExpiresAt <= now))
            .ToListAsync(ct);
        if (stale.Count == 0) return;
        _db.CloudStorageOAuthStates.RemoveRange(stale);
        await _db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ connections

    public Task<CloudStorageConnection?> FindOwnedAsync(Guid connectionId, Guid userId, Guid organizationId, CancellationToken ct) =>
        _db.CloudStorageConnections.FirstOrDefaultAsync(
            c => c.Id == connectionId && c.UserId == userId && c.OrganizationId == organizationId, ct);

    public async Task<bool> DisconnectAsync(Guid connectionId, Guid userId, Guid organizationId, CancellationToken ct)
    {
        CloudStorageConnection? connection = await FindOwnedAsync(connectionId, userId, organizationId, ct);
        if (connection is null) return false;

        ICloudStorageProvider? provider = _providers.Get(connection.Provider);
        if (provider is { IsConfigured: true } && _secrets.TryUnprotect(connection.ProtectedAccessToken, out string accessToken))
        {
            string? refreshToken = _secrets.TryUnprotect(connection.ProtectedRefreshToken, out string refresh) ? refresh : null;
            try
            {
                await provider.RevokeAsync(new CloudTokenSet(accessToken, refreshToken, connection.AccessTokenExpiresAt, null), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best effort: the row is deleted either way, so ReelBolt holds no token afterwards.
                _logger.LogWarning("Revoking a {Provider} token on disconnect failed: {Message}", connection.Provider, ex.Message);
            }
        }

        // The database does this too (ON DELETE SET NULL); doing it here keeps every provider honest.
        List<ProjectCloudLink> links = await _db.ProjectCloudLinks.Where(l => l.ConnectionId == connection.Id).ToListAsync(ct);
        DateTime now = Now();
        foreach (ProjectCloudLink link in links)
        {
            link.ConnectionId = null;
            link.UpdatedAt = now;
        }

        _db.CloudStorageConnections.Remove(connection);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Disconnected {Provider} connection {ConnectionId}; {Links} link(s) kept without it", connection.Provider, connection.Id, links.Count);
        return true;
    }

    // ------------------------------------------------------------------ tokens

    public async Task<string> GetAccessTokenAsync(CloudStorageConnection connection, bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && !IsExpiring(connection) && _secrets.TryUnprotect(connection.ProtectedAccessToken, out string current))
            return current;

        ICloudStorageProvider provider = ConfiguredProvider(connection.Provider);
        string tokenBefore = connection.ProtectedAccessToken;

        SemaphoreSlim gate = RefreshGates.GetOrAdd(connection.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Another scope may have refreshed while this one waited: read what is stored now.
            await ReloadAsync(connection, ct);

            if (!IsExpiring(connection) && _secrets.TryUnprotect(connection.ProtectedAccessToken, out string fresh) &&
                (!forceRefresh || !string.Equals(connection.ProtectedAccessToken, tokenBefore, StringComparison.Ordinal)))
            {
                return fresh;
            }

            if (!_secrets.TryUnprotect(connection.ProtectedRefreshToken, out string refreshToken) || string.IsNullOrWhiteSpace(refreshToken))
            {
                await MarkBrokenAsync(connection, ct);
                throw new CloudStorageApiException(
                    $"{connection.Provider} connection {connection.Id} has no usable refresh token.", CloudStorageErrorKind.Unauthorized);
            }

            CloudTokenSet tokens;
            try
            {
                tokens = await provider.RefreshAsync(refreshToken, ct);
            }
            catch (CloudStorageApiException ex) when (ex.Kind == CloudStorageErrorKind.Unauthorized)
            {
                _logger.LogWarning("{Provider} refused to refresh connection {ConnectionId}: {Message}", connection.Provider, connection.Id, ex.Message);
                await MarkBrokenAsync(connection, ct);
                throw new CloudStorageApiException(
                    $"{connection.Provider} refused to refresh connection {connection.Id}.", CloudStorageErrorKind.Unauthorized, ex.StatusCode);
            }

            connection.ProtectedAccessToken = _secrets.Protect(tokens.AccessToken);
            // Microsoft rotates refresh tokens: the new one replaces the old. Google and Dropbox send none.
            if (tokens.RefreshToken is { Length: > 0 } rotated) connection.ProtectedRefreshToken = _secrets.Protect(rotated);
            connection.AccessTokenExpiresAt = tokens.AccessTokenExpiresAt;
            if (!string.IsNullOrWhiteSpace(tokens.Scope)) connection.GrantedScopes = Truncate(tokens.Scope, 2000);
            connection.LastError = null;
            connection.UpdatedAt = Now();
            await _db.SaveChangesAsync(ct);

            return tokens.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<T> CallAsync<T>(CloudStorageConnection connection, Func<string, Task<T>> call, CancellationToken ct)
    {
        string token = await GetAccessTokenAsync(connection, forceRefresh: false, ct);
        try
        {
            return await call(token);
        }
        catch (CloudStorageApiException ex) when (ex.Kind == CloudStorageErrorKind.Unauthorized)
        {
            // The provider may have revoked the token before its stated expiry: refresh once, retry once.
            token = await GetAccessTokenAsync(connection, forceRefresh: true, ct);
            return await call(token);
        }
    }

    private bool IsExpiring(CloudStorageConnection connection) =>
        connection.AccessTokenExpiresAt is { } expiry && expiry <= Now() + RefreshMargin;

    private ICloudStorageProvider ConfiguredProvider(CloudStorageProviderKind kind)
    {
        ICloudStorageProvider? provider = _providers.Get(kind);
        return provider is { IsConfigured: true }
            ? provider
            : throw new CloudStorageApiException($"{kind} is not configured.", CloudStorageErrorKind.NotConfigured);
    }

    private async Task ReloadAsync(CloudStorageConnection connection, CancellationToken ct)
    {
        var entry = _db.Entry(connection);
        if (entry.State is EntityState.Unchanged or EntityState.Modified)
        {
            await entry.ReloadAsync(ct);
            if (entry.State == EntityState.Detached)
            {
                throw new CloudStorageApiException(
                    $"{connection.Provider} connection {connection.Id} was disconnected.", CloudStorageErrorKind.Unauthorized);
            }
        }
    }

    private async Task MarkBrokenAsync(CloudStorageConnection connection, CancellationToken ct)
    {
        connection.LastError =
            $"{CloudStorageProviders.DisplayName(connection.Provider)} no longer accepts ReelBolt's access to this account. "
            + "The access may have been removed or may have expired. Reconnect the account in Settings.";
        connection.UpdatedAt = Now();
        if (_db.Entry(connection).State != EntityState.Detached) await _db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// A same-origin dashboard path to return to after the vendor's consent screen, or null (the default
    /// page) when <paramref name="raw"/> is anything else: an absolute or protocol-relative URL, a path
    /// outside <c>/app</c>, a traversal, an encoded separator, a fragment, a control character.
    /// </summary>
    public static string? NormalizeReturnPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string value = raw.Trim();
        if (value.Length > MaxReturnPathLength) return null;

        int queryStart = value.IndexOf('?');
        string path = queryStart < 0 ? value : value[..queryStart];
        string query = queryStart < 0 ? string.Empty : value[(queryStart + 1)..];

        if (!string.Equals(path, "/app", StringComparison.Ordinal) && !path.StartsWith("/app/", StringComparison.Ordinal)) return null;
        if (!SafePath().IsMatch(path)) return null;
        foreach (string segment in path.Split('/'))
        {
            if (segment is "." or "..") return null;
        }

        if (path.Contains("//", StringComparison.Ordinal)) return null;
        if (query.Length > 0 && !SafeQuery().IsMatch(query)) return null;

        return value;
    }

    [GeneratedRegex("^[A-Za-z0-9/_.~-]+$")]
    private static partial Regex SafePath();

    [GeneratedRegex("^[A-Za-z0-9_.~=&%+-]*$")]
    private static partial Regex SafeQuery();

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length > max ? value[..max] : value;

    /// <summary>Base64url of SHA-256, unpadded: RFC 7636 §4.2 <c>S256</c>.</summary>
    internal static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
