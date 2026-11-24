#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.GitHub;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.WorkflowEngine.Tests.GitHub;

/// <summary>
/// Test scaffolding for the GitHub App integration (docs/github-app.md). Nothing here reaches the
/// network: <see cref="FakeGitHubAppClient"/> stands in for api.github.com, so the tests exercise the
/// authorization, idempotency and tenancy rules rather than GitHub's uptime.
/// </summary>
internal static class GitHubTestData
{
    public const string WebhookSecret = "a-webhook-secret-used-only-in-tests";
    public const long InstallationId = 4242;
    public const long RepositoryId = 987654321;
    public const string RepositoryFullName = "acme/promo-video";

    public static GitHubOptions Configured() => new()
    {
        AppId = "123456",
        PrivateKeyPem = "unused-in-tests",   // FakeGitHubAppClient never mints a JWT
        WebhookSecret = WebhookSecret
    };

    public static Project NewProject(Guid organizationId, Guid ownerId, string name = "P") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        OwnerId = ownerId,
        OrganizationId = organizationId,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    public static ProjectGitHubLink NewLink(Guid projectId, Guid organizationId, Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        OrganizationId = organizationId,
        InstallationId = InstallationId,
        RepositoryId = RepositoryId,
        RepositoryFullName = RepositoryFullName,
        RepositoryDefaultBranch = "main",
        CreatedByUserId = userId,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    /// <summary>The body of a push to the linked repository, byte-for-byte what GitHub would send.</summary>
    public static string PushPayload(string after, string? branch = "main", DateTime? timestamp = null, long repositoryId = RepositoryId, string fullName = RepositoryFullName, long installationId = InstallationId, bool deleted = false)
    {
        string when = (timestamp ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ssZ");
        return $"{{\"ref\":\"refs/heads/{branch}\",\"after\":\"{after}\",\"deleted\":{(deleted ? "true" : "false")},"
             + $"\"repository\":{{\"id\":{repositoryId},\"full_name\":\"{fullName}\",\"default_branch\":\"main\"}},"
             + $"\"installation\":{{\"id\":{installationId}}},"
             + $"\"head_commit\":{{\"id\":\"{after}\",\"timestamp\":\"{when}\"}}}}";
    }
}

/// <summary>
/// A tenant context whose system scope actually works, so controller and worker paths can be driven
/// exactly as the host drives them. Unscoped reads are system reads, which is what makes seeding
/// straightforward.
/// </summary>
internal sealed class TestTenant : ITenantContext
{
    private readonly AsyncLocal<Scope?> _scope = new();

    private sealed record Scope(Guid? OrganizationId, bool IsSystem);

    public Guid? OrganizationId => _scope.Value?.OrganizationId;

    public bool IsSystem => _scope.Value?.IsSystem ?? true;

    public IDisposable BeginSystemScope() => Push(new Scope(null, true));

    public IDisposable BeginOrganizationScope(Guid organizationId) => Push(new Scope(organizationId, false));

    private IDisposable Push(Scope next)
    {
        Scope? previous = _scope.Value;
        _scope.Value = next;
        return new Restore(_scope, previous);
    }

    private sealed class Restore(AsyncLocal<Scope?> slot, Scope? previous) : IDisposable
    {
        public void Dispose() => slot.Value = previous;
    }
}

/// <summary>
/// api.github.com, in memory. Records every request so a test can assert what a pull actually fetched
/// (which is how "a redelivery downloads nothing" is proved rather than asserted).
/// </summary>
internal sealed class FakeGitHubAppClient : IGitHubAppClient
{
    public bool IsConfigured { get; set; } = true;

    public Dictionary<string, GitHubRepositoryInfo> Repositories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, GitHubInstallationInfo> Installations { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, GitHubRepoTree> Trees { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> BranchHeads { get; } = new(StringComparer.Ordinal);

    public List<string> BlobRequests { get; } = [];
    public List<string> TreeRequests { get; } = [];
    public Exception? TreeFailure { get; set; }

    // ---- user-to-server (OAuth) surface -------------------------------------

    /// <summary>False by default, so a test that does not care about the OAuth flow cannot accidentally enable it.</summary>
    public bool IsUserOAuthConfigured { get; set; }

    /// <summary>What an authorization-code exchange returns. Null means GitHub refused the code.</summary>
    public GitHubUserTokens? TokensToIssue { get; set; }

    /// <summary>What a refresh returns. Null means GitHub refused it — an expired or revoked grant.</summary>
    public GitHubUserTokens? TokensToRefresh { get; set; }

    /// <summary>The person <c>GET /user</c> reports for any token.</summary>
    public GitHubUserIdentity Identity { get; set; } = new(1, "octocat", "https://avatars.example/octocat.png");

    /// <summary>Installations the connected person may use, keyed by installation id.</summary>
    public Dictionary<long, GitHubUserInstallation> UserInstallations { get; } = [];

    /// <summary>Repositories per installation, as <c>GET /user/installations/{id}/repositories</c> answers.</summary>
    public Dictionary<long, List<GitHubUserRepository>> InstallationRepositories { get; } = [];

    /// <summary>
    /// One level of a tree, keyed by <c>{owner}/{repo}@{refOrSha}</c>. Only set for the folder-picker
    /// tests, which is why <see cref="GetTreeLevelAsync"/> throws rather than returning empty when a
    /// key is missing: an empty folder and an unknown one are different answers.
    /// </summary>
    public Dictionary<string, GitHubTreeLevel> TreeLevels { get; } = new(StringComparer.Ordinal);

    public List<string> CodesExchanged { get; } = [];
    public List<string> RefreshTokensUsed { get; } = [];

    /// <summary>
    /// Every user access token this client was called with, in order. This is how a test proves which
    /// token was actually spent — after a refresh, or after a revocation — rather than assuming.
    /// </summary>
    public List<string> UserTokensSeen { get; } = [];

    public int TreeLevelRequests { get; private set; }

    public void AddUserRepository(long installationId, string accountLogin, long repositoryId, string fullName, string defaultBranch = "main", bool isPrivate = true, string accountType = "User", string? selection = "selected")
    {
        UserInstallations[installationId] = new GitHubUserInstallation(installationId, accountLogin, accountType, selection);
        if (!InstallationRepositories.TryGetValue(installationId, out List<GitHubUserRepository>? list))
        {
            list = [];
            InstallationRepositories[installationId] = list;
        }

        list.Add(new GitHubUserRepository(repositoryId, fullName, defaultBranch, isPrivate, installationId, accountLogin));
    }

    /// <summary>Registers one directory level as <c>{owner}/{repo}@{refOrSha}</c>.</summary>
    public void AddTreeLevel(string owner, string repo, string referenceOrSha, params GitHubTreeEntry[] entries) =>
        TreeLevels[$"{owner}/{repo}@{referenceOrSha}"] = new GitHubTreeLevel(entries, Truncated: false);

    public Task<GitHubUserTokens> ExchangeUserCodeAsync(string code, string codeVerifier, CancellationToken ct)
    {
        CodesExchanged.Add(code);
        return TokensToIssue is { } tokens
            ? Task.FromResult(tokens)
            : throw new GitHubApiException("GitHub did not return a user access token: bad_verification_code.", System.Net.HttpStatusCode.BadGateway);
    }

    public Task<GitHubUserTokens?> RefreshUserTokenAsync(string refreshToken, CancellationToken ct)
    {
        RefreshTokensUsed.Add(refreshToken);
        return Task.FromResult(TokensToRefresh);
    }

    public Task<GitHubUserIdentity?> GetUserAsync(string userAccessToken, CancellationToken ct) =>
        Task.FromResult<GitHubUserIdentity?>(Identity);

    public Task<IReadOnlyList<GitHubUserInstallation>> ListUserInstallationsAsync(string userAccessToken, CancellationToken ct)
    {
        UserTokensSeen.Add(userAccessToken);
        return Task.FromResult<IReadOnlyList<GitHubUserInstallation>>(UserInstallations.Values.ToList());
    }

    public Task<IReadOnlyList<GitHubUserRepository>> ListInstallationRepositoriesAsync(string userAccessToken, long installationId, string accountLogin, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<GitHubUserRepository>>(
            InstallationRepositories.TryGetValue(installationId, out List<GitHubUserRepository>? list) ? list : []);

    /// <summary>Thrown by <see cref="GetTreeLevelAsync"/> when set: GitHub failing a folder read.</summary>
    public Exception? TreeLevelFailure { get; set; }

    public Task<GitHubTreeLevel> GetTreeLevelAsync(long installationId, string owner, string repo, string referenceOrSha, CancellationToken ct)
    {
        TreeLevelRequests++;
        if (TreeLevelFailure is not null) throw TreeLevelFailure;
        return TreeLevels.TryGetValue($"{owner}/{repo}@{referenceOrSha}", out GitHubTreeLevel? level)
            ? Task.FromResult(level)
            : throw new GitHubApiException($"Tree {referenceOrSha} not found.", System.Net.HttpStatusCode.NotFound);
    }

    public void AddRepository(string fullName, long installationId, string defaultBranch = "main", string? accountLogin = "acme")
    {
        Repositories[fullName] = new GitHubRepositoryInfo(GitHubTestData.RepositoryId, fullName, defaultBranch);
        if (accountLogin is not null)
            Installations[fullName] = new GitHubInstallationInfo(installationId, accountLogin, "all");
    }

    /// <summary>Registers a tree whose blobs are the given text files (plus an implicit sha per path).</summary>
    public void AddTree(string commitSha, IReadOnlyDictionary<string, string> files)
    {
        List<GitHubTreeEntry> entries = [];
        foreach ((string path, string content) in files)
        {
            string sha = Sha(path, content);
            Blobs[sha] = Encoding.UTF8.GetBytes(content);
            entries.Add(new GitHubTreeEntry(path, "blob", sha, Encoding.UTF8.GetByteCount(content)));
        }

        Trees[commitSha] = new GitHubRepoTree(commitSha, entries, Truncated: false);
    }

    public static string Sha(string path, string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(path + "\n" + content))).ToLowerInvariant();

    public Task<GitHubRepositoryInfo?> GetRepositoryAsync(string owner, string repo, CancellationToken ct) =>
        Task.FromResult(Repositories.TryGetValue($"{owner}/{repo}", out GitHubRepositoryInfo? info) ? info : null);

    public Task<GitHubInstallationInfo?> GetInstallationForRepositoryAsync(string owner, string repo, CancellationToken ct) =>
        Task.FromResult(Installations.TryGetValue($"{owner}/{repo}", out GitHubInstallationInfo? info) ? info : null);

    public Task<string> GetBranchHeadAsync(long installationId, string owner, string repo, string branch, CancellationToken ct)
    {
        if (BranchHeads.TryGetValue($"{owner}/{repo}@{branch}", out string? head)) return Task.FromResult(head);
        throw new GitHubApiException($"{owner}/{repo}@{branch} not found.", System.Net.HttpStatusCode.NotFound);
    }

    /// <summary>Branch names per <c>{owner}/{repo}</c>, as <c>GET /repos/{o}/{r}/branches</c> answers.</summary>
    public Dictionary<string, List<string>> Branches { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<GitHubBranchList> ListBranchesAsync(long installationId, string owner, string repo, CancellationToken ct) =>
        Branches.TryGetValue($"{owner}/{repo}", out List<string>? names)
            ? Task.FromResult(new GitHubBranchList(names, Truncated: false))
            : Task.FromResult(new GitHubBranchList(
                BranchHeads.Keys
                    .Where(k => k.StartsWith($"{owner}/{repo}@", StringComparison.Ordinal))
                    .Select(k => k[(owner.Length + repo.Length + 2)..])
                    .ToList(),
                Truncated: false));

    public Task<GitHubRepoTree> GetTreeAsync(long installationId, string owner, string repo, string commitSha, CancellationToken ct)
    {
        TreeRequests.Add(commitSha);
        if (TreeFailure is not null) throw TreeFailure;
        if (Trees.TryGetValue(commitSha, out GitHubRepoTree? tree)) return Task.FromResult(tree);
        throw new GitHubApiException($"Tree {commitSha} not found.", System.Net.HttpStatusCode.NotFound);
    }

    /// <summary>Called with each blob SHA as it is requested, before it is answered. Lets a test interrupt a pull.</summary>
    public Action<string>? OnBlobRequested { get; set; }

    /// <summary>
    /// A blob that is not registered answers the way the real client does for a blob GitHub answers 404
    /// for: <see cref="GitHubBlobUnreadableException"/>, which a pull skips — not a GitHub failure.
    /// </summary>
    public Task<byte[]> GetBlobAsync(long installationId, string owner, string repo, string blobSha, CancellationToken ct)
    {
        BlobRequests.Add(blobSha);
        OnBlobRequested?.Invoke(blobSha);
        return Blobs.TryGetValue(blobSha, out byte[]? content)
            ? Task.FromResult(content)
            : throw new GitHubBlobUnreadableException(blobSha, GitHubBlobUnreadableException.NotFound);
    }
}

/// <summary>
/// <see cref="IFileStorageService"/> in memory. Storage keys follow the real implementation's shape
/// (<c>projects/{id}/{category}/{directory}/{fileId}{ext}</c>), so the assertions about rewritten files
/// and deleted keys mean the same thing they would against S3.
/// </summary>
internal sealed class InMemoryFileStorage : IFileStorageService
{
    public Task<string?> GetObjectETagAsync(Guid projectId, string storageKey, CancellationToken ct) =>
        Task.FromResult<string?>(Objects.ContainsKey(storageKey) ? $"\"{storageKey.GetHashCode():x8}\"" : null);

    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

    public int Uploads { get; private set; }
    public int Deletes { get; private set; }

    public Task<StoredFileObject> UploadAsync(
        Guid projectId,
        Guid fileId,
        Stream content,
        string fileName,
        string contentType,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct,
        string category = "userFiles",
        string? originalPath = null)
    {
        using MemoryStream buffer = new();
        content.CopyTo(buffer);
        Uploads++;

        string normalizedPath;
        try
        {
            normalizedPath = ProjectFilePath.NormalizeRelativePath(originalPath ?? fileName);
        }
        catch (Exception)
        {
            normalizedPath = ProjectFilePath.NormalizeRelativePath(fileName);
        }

        string? directory = ProjectFilePath.GetDirectoryPath(normalizedPath);
        string storageFileName = ProjectFilePath.BuildStorageFileName(fileId, normalizedPath);
        string key = ProjectFilePath.BuildStorageKey(projectId, category, directory, storageFileName);
        Objects[key] = buffer.ToArray();

        return Task.FromResult(new StoredFileObject(
            key,
            "bucket",
            ProjectFilePath.BuildStoragePrefix(projectId, category),
            null,
            category,
            ProjectFilePath.GetFileName(normalizedPath),
            normalizedPath,
            directory,
            storageFileName));
    }

    public Task<Stream> DownloadAsync(Guid projectId, string storageKey, CancellationToken ct) =>
        Objects.TryGetValue(storageKey, out byte[]? bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes))
            : throw new FileNotFoundException(storageKey);

    /// <summary>When set, every delete throws it after recording the attempt (a storage provider failure).</summary>
    public Exception? DeleteFailure { get; set; }

    public Task DeleteAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        Deletes++;
        if (DeleteFailure is not null) throw DeleteFailure;
        Objects.Remove(storageKey);
        return Task.CompletedTask;
    }

    public Task<int> DeleteManyAsync(Guid projectId, IReadOnlyCollection<string> storageKeys, CancellationToken ct)
    {
        int removed = 0;
        foreach (string key in storageKeys)
        {
            Deletes++;
            if (Objects.Remove(key)) removed++;
        }

        return Task.FromResult(removed);
    }

    public Task CopyAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct)
    {
        Objects[destinationStorageKey] = Objects[sourceStorageKey];
        return Task.CompletedTask;
    }

    public Task MoveAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct)
    {
        if (Objects.Remove(sourceStorageKey, out byte[]? bytes)) Objects[destinationStorageKey] = bytes;
        return Task.CompletedTask;
    }

    /// <summary>
    /// The bucket name the production implementation reports. A fixed string is enough here: the
    /// assertions that matter are about storage KEYS and byte content, not about which bucket.
    /// </summary>
    public string BucketName => "reelforge";

    /// <summary>
    /// The HEAD half of the interface, derived from what was actually uploaded rather than
    /// faked. A caller asking whether an object exists, and how big it is, must get the truth
    /// here for the same reason it must against S3 - a pull that trusts a claim instead of the
    /// stored bytes is the bug this interface member exists to prevent.
    /// </summary>
    public Task<StoredObjectMetadata?> GetMetadataAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        // Content type is not retained by this fake; the size is, and that is the field callers
        // assert on.
        StoredObjectMetadata? result = Objects.TryGetValue(storageKey, out byte[]? bytes)
            ? new StoredObjectMetadata(bytes.Length, null)
            : null;
        return Task.FromResult(result);
    }
}

internal static class GitHubTestDb
{
    public static InferenceApiDbContext New(TestTenant tenant) =>
        New(tenant, $"github-{Guid.NewGuid():N}", new InMemoryDatabaseRoot());

    /// <summary>
    /// A context over a NAMED store. Two contexts made with the same name and root see the same rows,
    /// which is how a test plays the request and the background worker with separate contexts — and how
    /// it proves what was actually saved rather than what one context still tracks.
    /// </summary>
    public static InferenceApiDbContext New(TestTenant tenant, string name, InMemoryDatabaseRoot root) =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase(name, root)
                .Options,
            tenant);
}

/// <summary>
/// The background half of an on-demand sync (<see cref="GitHubSyncQueue"/> + <see cref="GitHubSyncWorker"/>),
/// wired the way the host wires it: each sync gets its own DI scope and its own context over the test's
/// store. <see cref="DrainAsync"/> runs whatever is queued, so a test decides exactly when "later" is.
/// </summary>
internal sealed class GitHubSyncHarness : IDisposable
{
    private readonly ServiceProvider _provider;

    public GitHubSyncHarness(
        TestTenant tenant,
        Func<InferenceApiDbContext> newDb,
        IGitHubAppClient gitHub,
        IFileStorageService storage,
        Microsoft.Extensions.Options.IOptions<GitHubOptions> options)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(tenant);
        services.AddScoped(_ => newDb());
        services.AddSingleton(gitHub);
        services.AddSingleton(storage);
        services.AddSingleton(options);
        services.AddScoped<IGitHubRepositoryPullService, GitHubRepositoryPullService>();
        _provider = services.BuildServiceProvider();

        Worker = new GitHubSyncWorker(Queue, _provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<GitHubSyncWorker>.Instance);
    }

    public GitHubSyncQueue Queue { get; } = new();

    public GitHubSyncWorker Worker { get; }

    /// <summary>Runs every queued sync now and returns how many ran.</summary>
    public Task<int> DrainAsync() => Worker.DrainAsync(CancellationToken.None);

    public void Dispose() => _provider.Dispose();
}

/// <summary>
/// <see cref="ISecretProtector"/> without a key ring. It is deliberately a REAL transform rather than a
/// pass-through: a test can then assert that the stored column is not the token, which a pass-through
/// would make impossible and which is the entire point of the column.
/// </summary>
internal sealed class FakeSecretProtector : ReelBolt.Shared.Inference.ISecretProtector
{
    public const string Prefix = "dp:v1:";

    public string Protect(string plaintext) => Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

    public bool TryUnprotect(string? ciphertext, out string plaintext)
    {
        plaintext = string.Empty;
        if (string.IsNullOrEmpty(ciphertext) || !ciphertext.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        try
        {
            plaintext = Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext[Prefix.Length..]));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// A key ring that has been rotated or lost: nothing it wrote can be read back. Used to prove the
/// fail-closed path — an unreadable token must revoke the connection rather than be worked around.
/// </summary>
internal sealed class UnreadableSecretProtector : ReelBolt.Shared.Inference.ISecretProtector
{
    public string Protect(string plaintext) => "dp:unreadable:" + plaintext;

    public bool TryUnprotect(string? ciphertext, out string plaintext)
    {
        plaintext = string.Empty;
        return false;
    }
}

/// <summary>
/// An <see cref="IGitHubAccountService"/> for tests whose subject is something else. It answers
/// "nobody has connected GitHub", which is the state that makes
/// <c>GitHubLinkService</c>'s reachability check step aside — so every pre-existing link test keeps
/// testing exactly what it tested before account linking existed.
/// </summary>
internal sealed class NoopGitHubAccountService : IGitHubAccountService
{
    public Task<GitHubAccountView> GetViewAsync(Guid userId, Guid organizationId, CancellationToken ct) =>
        Task.FromResult(new GitHubAccountView(false, null, null, false, null, null, null));

    public Task<bool> DisconnectAsync(Guid userId, Guid organizationId, CancellationToken ct) => Task.FromResult(false);

    public Task<int> RevokeByGitHubUserIdAsync(long gitHubUserId, CancellationToken ct) => Task.FromResult(0);

    public Task<IReadOnlyList<GitHubRepositoryChoice>> ListRepositoriesAsync(Guid userId, Guid organizationId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<GitHubRepositoryChoice>>([]);

    public Task<GitHubFolderListing> ListFolderAsync(Guid userId, Guid organizationId, long repositoryId, string? path, CancellationToken ct) =>
        throw new GitHubAccountUnavailableException("not_connected");
}

/// <summary>
/// A caller who HAS connected GitHub and whose own account can use every repository the fake app is
/// installed on — the state linking now requires. Read live from the fake, so a repository a test adds
/// later is visible, and one with no installation is not (GitHub lists only installed repositories).
/// </summary>
internal sealed class ConnectedGitHubAccountService(FakeGitHubAppClient gitHub, bool connected = true) : IGitHubAccountService
{
    public int ListCalls { get; private set; }

    public Task<GitHubAccountView> GetViewAsync(Guid userId, Guid organizationId, CancellationToken ct) =>
        Task.FromResult(new GitHubAccountView(connected, connected ? "acme" : null, null, true, null, null, null));

    public Task<bool> DisconnectAsync(Guid userId, Guid organizationId, CancellationToken ct) => Task.FromResult(false);

    public Task<int> RevokeByGitHubUserIdAsync(long gitHubUserId, CancellationToken ct) => Task.FromResult(0);

    public Task<IReadOnlyList<GitHubRepositoryChoice>> ListRepositoriesAsync(Guid userId, Guid organizationId, CancellationToken ct)
    {
        ListCalls++;
        IReadOnlyList<GitHubRepositoryChoice> choices = [.. gitHub.Repositories.Values
            .Where(r => gitHub.Installations.ContainsKey(r.FullName))
            .Select(r => new GitHubRepositoryChoice(
                r.Id, r.FullName, r.DefaultBranch, true, gitHub.Installations[r.FullName].Id,
                gitHub.Installations[r.FullName].AccountLogin, "User", "all", null))];
        return Task.FromResult(choices);
    }

    public Task<GitHubFolderListing> ListFolderAsync(Guid userId, Guid organizationId, long repositoryId, string? path, CancellationToken ct) =>
        throw new GitHubAccountUnavailableException("not_connected");
}

/// <summary>
/// api.github.com's transport, captured. This exists so the real <see cref="GitHubAppClient"/> — not a
/// fake — is exercised at least once: a fake agrees with whatever the test author believed the JSON
/// looks like, and the point of these tests is to disagree with that belief where it is wrong.
/// </summary>
internal sealed class CapturingHttpHandler : HttpMessageHandler
{
    public List<CapturedRequest> Requests { get; } = [];

    public Func<HttpRequestMessage, string?, HttpResponseMessage> Responder { get; set; } =
        (_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);

    public HttpResponseMessage Json(string json) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new CapturedRequest(
            request.Method.Method,
            request.RequestUri!.ToString(),
            body,
            request.Headers.Authorization?.Parameter));

        return Responder(request, body);
    }

    internal sealed record CapturedRequest(string Method, string Url, string? Body, string? Authorization);
}
