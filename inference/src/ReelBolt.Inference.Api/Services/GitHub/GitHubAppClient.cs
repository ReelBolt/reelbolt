using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ReelBolt.Inference.Api.Services.GitHub;

/// <summary>A repository as GitHub reports it. <see cref="Id"/> is the stable identity; the name is not.</summary>
public sealed record GitHubRepositoryInfo(long Id, string FullName, string DefaultBranch);

/// <summary>The installation GitHub says covers a repository.</summary>
public sealed record GitHubInstallationInfo(long Id, string AccountLogin, string? RepositorySelection);

/// <summary>One entry of a git tree. Directories are reported too and are skipped by the caller.</summary>
public sealed record GitHubTreeEntry(string Path, string Type, string Sha, long Size);

/// <summary>A committed tree listing, plus GitHub's own <c>truncated</c> flag.</summary>
public sealed record GitHubRepoTree(string CommitSha, IReadOnlyList<GitHubTreeEntry> Entries, bool Truncated);

/// <summary>
/// One level of a repository's tree — the children of a single directory, not the whole recursive
/// listing. This is what a folder picker browses: each returned <c>tree</c> entry carries the SHA that
/// names its own subtree, so the next level is one request away and no listing is ever truncated by
/// the size of the repository.
/// </summary>
public sealed record GitHubTreeLevel(IReadOnlyList<GitHubTreeEntry> Entries, bool Truncated);

/// <summary>
/// A repository's branch names, first page only (<see cref="Truncated"/> when GitHub had more), in
/// GitHub's own order.
/// </summary>
public sealed record GitHubBranchList(IReadOnlyList<string> Names, bool Truncated);

/// <summary>The person behind a user access token, as <c>GET /user</c> reports them.</summary>
public sealed record GitHubUserIdentity(long Id, string Login, string? AvatarUrl);

/// <summary>
/// A user access token (and, when the app has opted in to token expiration, its refresh token).
/// Never logged, never serialized to a response.
/// </summary>
public sealed record GitHubUserTokens(
    string AccessToken,
    string? RefreshToken,
    DateTime? AccessTokenExpiresAt,
    DateTime? RefreshTokenExpiresAt);

/// <summary>
/// One installation of this app that the connected person may use. The person reaches it as an owner,
/// a collaborator or through an organization membership — GitHub decides, which is why the picker can
/// never offer something the person cannot see.
/// </summary>
public sealed record GitHubUserInstallation(long Id, string AccountLogin, string AccountType, string? RepositorySelection);

/// <summary>
/// A repository the connected person may pick: one the app is installed on <b>and</b> the person can
/// already reach. GitHub's own answer, with the installation that covers it.
/// </summary>
public sealed record GitHubUserRepository(long Id, string FullName, string DefaultBranch, bool IsPrivate, long InstallationId, string AccountLogin);

/// <summary>A GitHub REST call that came back non-success. Carries the status, never the response body.</summary>
public sealed class GitHubApiException : Exception
{
    public GitHubApiException(string message, HttpStatusCode statusCode) : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}

/// <summary>
/// One blob GitHub answered without usable content: it does not have it (404), or it sent it in an
/// encoding other than base64, or the base64 does not decode. Deliberately <b>not</b> a
/// <see cref="GitHubApiException"/>: GitHub itself answered fine, so nothing may reword this as "GitHub
/// had a problem answering", and a pull skips the one file instead of failing. Carries the blob SHA and
/// a short reason code, never the response body.
/// </summary>
public sealed class GitHubBlobUnreadableException : Exception
{
    public const string NotFound = "not_found";
    public const string UnsupportedEncoding = "unsupported_encoding";
    public const string Malformed = "malformed";

    public GitHubBlobUnreadableException(string blobSha, string reason)
        : base($"GitHub returned no readable content for blob {blobSha} ({reason}).")
    {
        BlobSha = blobSha;
        Reason = reason;
    }

    public string BlobSha { get; }
    public string Reason { get; }
}

/// <summary>
/// The GitHub App's REST surface: app-JWT calls (who is this repository, which installation covers it)
/// and installation-token calls (read its tree and its blobs). No SDK — a plain typed
/// <see cref="HttpClient"/>, the same choice the Paddle, MiniMax and Fish clients made.
///
/// <para>
/// <b>Auth is two-tier and that is deliberate.</b> The app JWT authenticates ReelBolt <i>as the app</i>
/// and is the only credential that can ask "does an installation cover this repository?" — which is the
/// question that decides whether a link may exist at all. The installation token authenticates as the
/// installation and can read exactly the repositories it was granted, so a pull can never reach past
/// what the owner of the GitHub account installed the app on. Neither token is ever logged.
/// </para>
/// </summary>
public interface IGitHubAppClient
{
    /// <summary>False when app id or private key is missing; every method then throws rather than pretending.</summary>
    bool IsConfigured { get; }

    /// <summary>The repository, or null when it does not exist or the app cannot see it.</summary>
    Task<GitHubRepositoryInfo?> GetRepositoryAsync(string owner, string repo, CancellationToken ct);

    /// <summary>The installation covering the repository, or null when the app is not installed on it.</summary>
    Task<GitHubInstallationInfo?> GetInstallationForRepositoryAsync(string owner, string repo, CancellationToken ct);

    /// <summary>The commit a branch currently points at.</summary>
    Task<string> GetBranchHeadAsync(long installationId, string owner, string repo, string branch, CancellationToken ct);

    /// <summary>
    /// The repository's branches, with an installation token. One page of
    /// <see cref="GitHubAppClient.MaxBranchesListed"/>; a longer list is reported as truncated.
    /// </summary>
    Task<GitHubBranchList> ListBranchesAsync(long installationId, string owner, string repo, CancellationToken ct);

    /// <summary>The full recursive tree of a commit.</summary>
    Task<GitHubRepoTree> GetTreeAsync(long installationId, string owner, string repo, string commitSha, CancellationToken ct);

    /// <summary>
    /// The decoded bytes of one blob; an empty array for a zero-byte file. Throws
    /// <see cref="GitHubBlobUnreadableException"/> for a blob GitHub does not have or does not send as
    /// base64, and <see cref="GitHubApiException"/> only when GitHub itself fails the request.
    /// </summary>
    Task<byte[]> GetBlobAsync(long installationId, string owner, string repo, string blobSha, CancellationToken ct);

    /// <summary>
    /// The children of one tree, without <c>recursive=1</c>. <paramref name="referenceOrSha"/> is a
    /// branch or tag name for the root, and a subtree SHA for a directory below it.
    /// </summary>
    Task<GitHubTreeLevel> GetTreeLevelAsync(long installationId, string owner, string repo, string referenceOrSha, CancellationToken ct);

    /// <summary>
    /// False when the app has no client id, client secret or callback URL, so no person can connect an
    /// account; every user-token method then throws rather than pretending.
    /// </summary>
    bool IsUserOAuthConfigured { get; }

    /// <summary>Exchanges an authorization code (with its PKCE verifier) for a user access token.</summary>
    Task<GitHubUserTokens> ExchangeUserCodeAsync(string code, string codeVerifier, CancellationToken ct);

    /// <summary>
    /// Exchanges a refresh token for a fresh user access token. Null when GitHub refused it — a refresh
    /// token that expired, or an authorization the person revoked, both of which mean "reconnect".
    /// </summary>
    Task<GitHubUserTokens?> RefreshUserTokenAsync(string refreshToken, CancellationToken ct);

    /// <summary>Who the token belongs to, as GitHub reports it.</summary>
    Task<GitHubUserIdentity?> GetUserAsync(string userAccessToken, CancellationToken ct);

    /// <summary>Every installation of this app the person may use.</summary>
    Task<IReadOnlyList<GitHubUserInstallation>> ListUserInstallationsAsync(string userAccessToken, CancellationToken ct);

    /// <summary>Every repository the person may use under one installation.</summary>
    Task<IReadOnlyList<GitHubUserRepository>> ListInstallationRepositoriesAsync(string userAccessToken, long installationId, string accountLogin, CancellationToken ct);
}

/// <inheritdoc />
public sealed class GitHubAppClient : IGitHubAppClient
{
    /// <summary>Name of the shared <see cref="HttpClient"/> this client is built over.</summary>
    public const string HttpClientName = "GitHubApp";

    /// <summary>Branches one listing returns: GitHub's own maximum page size.</summary>
    public const int MaxBranchesListed = 100;

    private readonly HttpClient _http;
    private readonly GitHubOptions _options;
    private readonly ILogger<GitHubAppClient> _logger;
    private readonly ConcurrentDictionary<long, InstallationToken> _tokens = new();

    public GitHubAppClient(HttpClient http, IOptions<GitHubOptions> options, ILogger<GitHubAppClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _options.HasAppCredentials;

    public async Task<GitHubRepositoryInfo?> GetRepositoryAsync(string owner, string repo, CancellationToken ct)
    {
        // Mapped through an explicit wire type rather than deserialized into the record directly. The
        // two are not the same shape: GitHub spells these fields full_name and default_branch, and a
        // direct bind silently yields nulls for both, which would link a repository under a null name.
        //
        // Read with an INSTALLATION token: GitHub answers 401 to the app JWT on any /repos route except
        // /repos/{o}/{r}/installation, which is how the app finds the installation in the first place.
        // A repository the app is not installed on is therefore "not found" from here.
        GitHubInstallationInfo? installation = await GetInstallationForRepositoryAsync(owner, repo, ct);
        if (installation is null) return null;

        string token = await InstallationTokenAsync(installation.Id, ct);
        RepositoryWire? wire = await GetAsync<RepositoryWire>($"/repos/{Seg(owner)}/{Seg(repo)}", token, ct);
        if (wire is null) return null;

        return new GitHubRepositoryInfo(
            wire.Id,
            wire.FullName ?? $"{owner}/{repo}",
            string.IsNullOrWhiteSpace(wire.DefaultBranch) ? "main" : wire.DefaultBranch);
    }

    public async Task<GitHubInstallationInfo?> GetInstallationForRepositoryAsync(string owner, string repo, CancellationToken ct)
    {
        // account.login is nested, so this one could not be bound to a flat record even in principle.
        InstallationDetailWire? wire = await GetAsync<InstallationDetailWire>(
            $"/repos/{Seg(owner)}/{Seg(repo)}/installation", AppJwt(), ct);
        if (wire is null) return null;

        return new GitHubInstallationInfo(
            wire.Id,
            wire.Account?.Login ?? string.Empty,
            wire.RepositorySelection);
    }

    public async Task<string> GetBranchHeadAsync(long installationId, string owner, string repo, string branch, CancellationToken ct)
    {
        GitRefWire? reference = await GetAsync<GitRefWire>(
            $"/repos/{Seg(owner)}/{Seg(repo)}/git/ref/heads/{Seg(branch, keepSlashes: true)}",
            await InstallationTokenAsync(installationId, ct),
            ct);

        if (reference?.Object?.Sha is not { Length: > 0 } sha)
            throw new GitHubApiException($"{owner}/{repo}@{branch} did not resolve to a commit.", HttpStatusCode.NotFound);

        return sha;
    }

    public async Task<GitHubBranchList> ListBranchesAsync(long installationId, string owner, string repo, CancellationToken ct)
    {
        List<BranchWire>? wire = await GetAsync<List<BranchWire>>(
            $"/repos/{Seg(owner)}/{Seg(repo)}/branches?per_page={MaxBranchesListed}",
            await InstallationTokenAsync(installationId, ct),
            ct);

        if (wire is null)
            throw new GitHubApiException($"The branches of {owner}/{repo} were not found.", HttpStatusCode.NotFound);

        List<string> names = wire
            .Select(b => b.Name)
            .Where(n => n is { Length: > 0 })
            .Select(n => n!)
            .ToList();

        return new GitHubBranchList(names, wire.Count >= MaxBranchesListed);
    }

    public async Task<GitHubRepoTree> GetTreeAsync(long installationId, string owner, string repo, string commitSha, CancellationToken ct)
    {
        TreeWire? wire = await GetAsync<TreeWire>(
            $"/repos/{Seg(owner)}/{Seg(repo)}/git/trees/{Seg(commitSha)}?recursive=1",
            await InstallationTokenAsync(installationId, ct),
            ct);

        if (wire is null)
            throw new GitHubApiException($"Tree {commitSha} of {owner}/{repo} was not found.", HttpStatusCode.NotFound);

        List<GitHubTreeEntry> entries = (wire.Tree ?? [])
            .Where(e => e.Path is { Length: > 0 } && e.Sha is { Length: > 0 })
            .Select(e => new GitHubTreeEntry(e.Path!, e.Type ?? "blob", e.Sha!, e.Size ?? 0))
            .ToList();

        return new GitHubRepoTree(commitSha, entries, wire.Truncated);
    }

    public async Task<byte[]> GetBlobAsync(long installationId, string owner, string repo, string blobSha, CancellationToken ct)
    {
        BlobWire? wire = await GetAsync<BlobWire>(
            $"/repos/{Seg(owner)}/{Seg(repo)}/git/blobs/{Seg(blobSha)}",
            await InstallationTokenAsync(installationId, ct),
            ct);

        return DecodeBlob(wire?.Encoding, wire?.Content, found: wire is not null, blobSha);
    }

    /// <summary>
    /// The bytes of a blob answer. A zero-byte file is a VALID answer — GitHub sends it as
    /// <c>{"encoding":"base64","content":""}</c> — and decodes to an empty array: treating it as a
    /// failure made every pull of a repository holding a <c>.gitkeep</c> fail with a fake "GitHub 502".
    /// A blob GitHub does not have (404), one in another encoding, or one whose base64 does not decode
    /// is <see cref="GitHubBlobUnreadableException"/>: one unreadable file, which the pull skips, never a
    /// GitHub outage.
    /// </summary>
    internal static byte[] DecodeBlob(string? encoding, string? content, bool found, string blobSha)
    {
        if (!found)
            throw new GitHubBlobUnreadableException(blobSha, GitHubBlobUnreadableException.NotFound);

        if (!string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
            throw new GitHubBlobUnreadableException(blobSha, GitHubBlobUnreadableException.UnsupportedEncoding);

        if (string.IsNullOrEmpty(content)) return [];

        try
        {
            // GitHub wraps the base64 at 60 characters; Convert ignores the line breaks.
            return Convert.FromBase64String(content);
        }
        catch (FormatException)
        {
            throw new GitHubBlobUnreadableException(blobSha, GitHubBlobUnreadableException.Malformed);
        }
    }

    public async Task<GitHubTreeLevel> GetTreeLevelAsync(long installationId, string owner, string repo, string referenceOrSha, CancellationToken ct)
    {
        // No ?recursive=1 here, and that is the point: one level per request, so a picker can walk into
        // a directory of a repository far too large for GitHub to return in one listing.
        TreeWire? wire = await GetAsync<TreeWire>(
            $"/repos/{Seg(owner)}/{Seg(repo)}/git/trees/{Seg(referenceOrSha, keepSlashes: true)}",
            await InstallationTokenAsync(installationId, ct),
            ct);

        if (wire is null)
            throw new GitHubApiException($"Tree {referenceOrSha} of {owner}/{repo} was not found.", HttpStatusCode.NotFound);

        List<GitHubTreeEntry> entries = (wire.Tree ?? [])
            .Where(e => e.Path is { Length: > 0 } && e.Sha is { Length: > 0 })
            .Select(e => new GitHubTreeEntry(e.Path!, e.Type ?? "blob", e.Sha!, e.Size ?? 0))
            .ToList();

        return new GitHubTreeLevel(entries, wire.Truncated);
    }

    // ------------------------------------------------- user-to-server (OAuth)

    public bool IsUserOAuthConfigured => _options.HasUserOAuthCredentials;

    public async Task<GitHubUserTokens> ExchangeUserCodeAsync(string code, string codeVerifier, CancellationToken ct)
    {
        RequireUserOAuth();

        // The client secret goes in a form BODY, never a query string: a URL is the part of a request
        // that ends up in an access log or a proxy's trace.
        List<KeyValuePair<string, string>> fields =
        [
            new("client_id", _options.ClientId!),
            new("client_secret", _options.ClientSecret!),
            new("code", code),
            new("redirect_uri", _options.OAuthCallbackUrl!),
            new("code_verifier", codeVerifier)
        ];

        UserTokenWire? wire = await PostFormAsync<UserTokenWire>(_options.OAuthTokenUrl, fields, ct);
        if (wire?.AccessToken is not { Length: > 0 } token)
        {
            // GitHub answers 200 with {"error":"bad_verification_code",…} for a code it will not redeem.
            // The code is echoed as the reason because it is a GitHub-defined token, not remote prose.
            throw new GitHubApiException(
                $"GitHub did not return a user access token: {wire?.Error ?? "no token in the response"}.",
                HttpStatusCode.BadGateway);
        }

        return ToTokens(wire);
    }

    public async Task<GitHubUserTokens?> RefreshUserTokenAsync(string refreshToken, CancellationToken ct)
    {
        RequireUserOAuth();

        List<KeyValuePair<string, string>> fields =
        [
            new("client_id", _options.ClientId!),
            new("client_secret", _options.ClientSecret!),
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken)
        ];

        UserTokenWire? wire = await PostFormAsync<UserTokenWire>(_options.OAuthTokenUrl, fields, ct);

        // A refused refresh is an ANSWER, not a fault: the refresh token expired (six months) or the
        // person revoked ReelBolt. Both mean "ask them to connect again", which is why this returns
        // null instead of throwing.
        return wire?.AccessToken is { Length: > 0 } ? ToTokens(wire) : null;
    }

    public async Task<GitHubUserIdentity?> GetUserAsync(string userAccessToken, CancellationToken ct)
    {
        UserWire? wire = await GetAsync<UserWire>("/user", userAccessToken, ct);
        if (wire is null || wire.Id <= 0 || wire.Login is not { Length: > 0 }) return null;

        return new GitHubUserIdentity(wire.Id, wire.Login, wire.AvatarUrl);
    }

    public async Task<IReadOnlyList<GitHubUserInstallation>> ListUserInstallationsAsync(string userAccessToken, CancellationToken ct)
    {
        List<InstallationWire> wire = await GetAllPagesAsync<InstallationWire>(
            "/user/installations",
            w => w.Installations,
            userAccessToken,
            ct);

        return wire
            .Where(i => i.Account?.Login is { Length: > 0 })
            .Select(i => new GitHubUserInstallation(i.Id, i.Account!.Login!, i.Account.Type ?? "User", i.RepositorySelection))
            .ToList();
    }

    public async Task<IReadOnlyList<GitHubUserRepository>> ListInstallationRepositoriesAsync(
        string userAccessToken,
        long installationId,
        string accountLogin,
        CancellationToken ct)
    {
        List<UserRepositoryWire> wire = await GetAllPagesAsync<UserRepositoryWire>(
            $"/user/installations/{installationId}/repositories",
            w => w.Repositories,
            userAccessToken,
            ct);

        return wire
            .Where(r => r.Id > 0 && r.FullName is { Length: > 0 })
            .Select(r => new GitHubUserRepository(
                r.Id,
                r.FullName!,
                r.DefaultBranch ?? "main",
                r.Private,
                installationId,
                accountLogin))
            .ToList();
    }

    /// <summary>
    /// Walks a paginated GitHub list endpoint, bounded by <see cref="GitHubOptions.MaxRepositoriesListed"/>
    /// so a person with thousands of repositories cannot make one dashboard request unbounded work.
    /// </summary>
    private async Task<List<TItem>> GetAllPagesAsync<TItem>(
        string path,
        Func<PagedWire<TItem>, List<TItem>?> select,
        string token,
        CancellationToken ct)
    {
        List<TItem> items = [];
        int page = 1;
        int perPage = 100;

        while (items.Count < _options.MaxRepositoriesListed)
        {
            using HttpRequestMessage request = new(
                HttpMethod.Get,
                Absolute($"{path}?per_page={perPage}&page={page}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd("ReelBolt");

            using HttpResponseMessage response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub GET {Path} answered {Status}", path, (int)response.StatusCode);
                throw new GitHubApiException($"GitHub answered {(int)response.StatusCode} for {path}.", response.StatusCode);
            }

            PagedWire<TItem>? wire = await response.Content.ReadFromJsonAsync<PagedWire<TItem>>(cancellationToken: ct);
            List<TItem> batch = wire is null ? [] : select(wire) ?? [];

            items.AddRange(batch);

            // A short page is the last page; there is no need to read Link headers for that.
            if (batch.Count < perPage) break;
            page++;

            // A hard stop on the page count as well, so a malformed response that always looks full
            // cannot loop forever.
            if (page > 50) break;
        }

        return items;
    }

    private static GitHubUserTokens ToTokens(UserTokenWire wire)
    {
        DateTime now = DateTime.UtcNow;
        return new GitHubUserTokens(
            wire.AccessToken!,
            wire.RefreshToken is { Length: > 0 } ? wire.RefreshToken : null,
            wire.ExpiresIn is { } accessSeconds && accessSeconds > 0 ? now.AddSeconds(accessSeconds) : null,
            wire.RefreshTokenExpiresIn is { } refreshSeconds && refreshSeconds > 0 ? now.AddSeconds(refreshSeconds) : null);
    }

    private void RequireUserOAuth()
    {
        if (!_options.HasUserOAuthCredentials)
        {
            throw new GitHubApiException(
                "Connecting a GitHub account is not configured (GitHub:ClientId / GitHub:ClientSecret / GitHub:OAuthCallbackUrl).",
                HttpStatusCode.ServiceUnavailable);
        }
    }

    private async Task<T?> PostFormAsync<T>(string absoluteUrl, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken ct)
        where T : class
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(absoluteUrl));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("ReelBolt");
        // No Authorization header: this call IS the authentication, and it is the app's client secret
        // — not the app JWT, and not any installation token — that authenticates it.
        request.Content = new FormUrlEncodedContent(fields);

        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("GitHub OAuth token endpoint answered {Status}", (int)response.StatusCode);
            throw new GitHubApiException($"GitHub answered {(int)response.StatusCode} at its token endpoint.", response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }

    // ------------------------------------------------------------------ auth

    /// <summary>
    /// The app JWT: RS256, <c>iss</c> = the app id, no audience (GitHub rejects one), a 9-minute life
    /// with the not-before backdated a minute for clock skew. Never logged.
    /// </summary>
    private string AppJwt()
    {
        string? pem = _options.ResolvePrivateKeyPem();
        if (string.IsNullOrWhiteSpace(_options.AppId) || string.IsNullOrWhiteSpace(pem))
            throw new GitHubApiException("The GitHub App is not configured (GitHub:AppId / GitHub:PrivateKeyPem).", HttpStatusCode.ServiceUnavailable);

        // The RSA object is NOT disposed per call: IdentityModel caches signature providers by key, so a
        // `using` here left the cache holding a disposed key and every call after the first threw
        // ObjectDisposedException (a 500 on the repo folder browser). One key per PEM, reused.
        RsaSecurityKey key = SigningKeyFor(pem);

        DateTime now = DateTime.UtcNow;
        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = _options.AppId,
            IssuedAt = now.AddSeconds(-60),
            NotBefore = now.AddSeconds(-60),
            Expires = now.AddMinutes(9),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static readonly object SigningKeyGate = new();
    private static (string Pem, RsaSecurityKey Key)? _signingKey;

    private static RsaSecurityKey SigningKeyFor(string pem)
    {
        lock (SigningKeyGate)
        {
            if (_signingKey is { } cached && cached.Pem == pem)
                return cached.Key;

            RSA rsa = RSA.Create();
            try
            {
                rsa.ImportFromPem(pem);
            }
            catch (ArgumentException ex)
            {
                rsa.Dispose();
                throw new GitHubApiException($"The configured GitHub App private key is not a readable PEM: {ex.Message}", HttpStatusCode.ServiceUnavailable);
            }

            RsaSecurityKey key = new(rsa) { CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false } };
            _signingKey = (pem, key);
            return key;
        }
    }

    /// <summary>
    /// An installation access token, cached until shortly before it expires. Every call that reads
    /// repository content goes through here, so the scope of a pull is exactly the scope GitHub granted
    /// the installation.
    /// </summary>
    private async Task<string> InstallationTokenAsync(long installationId, CancellationToken ct)
    {
        if (_tokens.TryGetValue(installationId, out InstallationToken? cached) && cached.Usable)
            return cached.Token;

        using HttpRequestMessage request = new(HttpMethod.Post, Absolute($"/app/installations/{installationId}/access_tokens"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AppJwt());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        // GitHub answers 403 to any request without a User-Agent.
        request.Headers.UserAgent.ParseAdd("ReelBolt");

        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new GitHubApiException($"GitHub refused an installation token for installation {installationId} ({(int)response.StatusCode}).", response.StatusCode);

        TokenWire? wire = await response.Content.ReadFromJsonAsync<TokenWire>(cancellationToken: ct);
        if (wire?.Token is not { Length: > 0 } token)
            throw new GitHubApiException($"GitHub returned no token for installation {installationId}.", HttpStatusCode.BadGateway);

        InstallationToken fresh = new(token, wire.ExpiresAt.UtcDateTime.AddMinutes(-2));
        _tokens[installationId] = fresh;
        return fresh.Token;
    }

    // ------------------------------------------------------------------ transport

    private async Task<T?> GetAsync<T>(string pathAndQuery, string token, CancellationToken ct) where T : class
    {
        using HttpRequestMessage request = new(HttpMethod.Get, Absolute(pathAndQuery));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("ReelBolt");

        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
        {
            // The body is deliberately not echoed: it is remote text and this message reaches logs.
            _logger.LogWarning("GitHub GET {Path} answered {Status}", pathAndQuery, (int)response.StatusCode);
            throw new GitHubApiException($"GitHub answered {(int)response.StatusCode} for {pathAndQuery}.", response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }

    private Uri Absolute(string pathAndQuery) => new(_options.ApiBaseUrl.TrimEnd('/') + pathAndQuery);

    /// <summary>
    /// Percent-encodes one path segment. Branch names may keep their slashes — GitHub expects them —
    /// but then no piece may be empty, <c>.</c> or <c>..</c>: <see cref="Uri"/> would collapse those
    /// before the request leaves, and the caller would be talking to a different REST route than the
    /// one it named. Callers validate first (<see cref="GitHubBranchName"/>); this is the second line.
    /// A single segment is checked the same way, since <c>..</c> survives percent-encoding unchanged.
    /// </summary>
    internal static string Seg(string value, bool keepSlashes = false)
    {
        string[] pieces = keepSlashes ? value.Split('/') : [value];
        foreach (string piece in pieces)
        {
            if (piece.Length == 0 || piece == "." || piece == "..")
                throw new GitHubApiException("A repository path segment was empty or a dot segment; the request was not sent.", HttpStatusCode.BadRequest);
        }

        return string.Join('/', pieces.Select(Uri.EscapeDataString));
    }

    private sealed record InstallationToken(string Token, DateTime ExpiresAt)
    {
        public bool Usable => DateTime.UtcNow < ExpiresAt;
    }

    private sealed class GitRefWire
    {
        [JsonPropertyName("object")] public GitRefObject? Object { get; set; }
    }

    private sealed class GitRefObject
    {
        [JsonPropertyName("sha")] public string? Sha { get; set; }
    }

    /// <summary>One element of <c>GET /repos/{owner}/{repo}/branches</c>.</summary>
    private sealed class BranchWire
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class TreeWire
    {
        [JsonPropertyName("tree")] public List<TreeEntryWire>? Tree { get; set; }
        [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    }

    private sealed class TreeEntryWire
    {
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("sha")] public string? Sha { get; set; }
        [JsonPropertyName("size")] public long? Size { get; set; }
    }

    private sealed class BlobWire
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("encoding")] public string? Encoding { get; set; }
    }

    private sealed class TokenWire
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("expires_at")] public DateTimeOffset ExpiresAt { get; set; }
    }

    /// <summary>
    /// The OAuth token endpoint's answer, successful or not. A refusal arrives as HTTP 200 carrying
    /// <c>error</c>, which is why the token field is nullable here and checked at the call site.
    /// </summary>
    private sealed class UserTokenWire
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public long? ExpiresIn { get; set; }
        [JsonPropertyName("refresh_token_expires_in")] public long? RefreshTokenExpiresIn { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    }

    private sealed class InstallationWire
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("account")] public AccountWire? Account { get; set; }
        [JsonPropertyName("repository_selection")] public string? RepositorySelection { get; set; }
    }

    /// <summary><c>GET /repos/{owner}/{repo}</c>.</summary>
    private sealed class RepositoryWire
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("full_name")] public string? FullName { get; set; }
        [JsonPropertyName("default_branch")] public string? DefaultBranch { get; set; }
    }

    /// <summary><c>GET /repos/{owner}/{repo}/installation</c>. The account login is nested.</summary>
    private sealed class InstallationDetailWire
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("account")] public AccountWire? Account { get; set; }
        [JsonPropertyName("repository_selection")] public string? RepositorySelection { get; set; }
    }

    /// <summary><c>GET /user</c> — the person behind a user access token.</summary>
    private sealed class UserWire
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("login")] public string? Login { get; set; }
        [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; set; }
    }

    private sealed class AccountWire
    {
        [JsonPropertyName("login")] public string? Login { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }

    private sealed class UserRepositoryWire
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("full_name")] public string? FullName { get; set; }
        [JsonPropertyName("default_branch")] public string? DefaultBranch { get; set; }
        [JsonPropertyName("private")] public bool Private { get; set; }
    }

    /// <summary>Both paginated user-token lists share this envelope shape.</summary>
    private sealed class PagedWire<TItem>
    {
        [JsonPropertyName("total_count")] public int TotalCount { get; set; }
        [JsonPropertyName("installations")] public List<TItem>? Installations { get; set; }
        [JsonPropertyName("repositories")] public List<TItem>? Repositories { get; set; }
    }
}
