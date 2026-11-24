#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.GitHub;

namespace ReelBolt.WorkflowEngine.Tests.CloudStorage.Core;

/// <summary>
/// A cloud drive in memory: no network, no vendor credential (none exists). Records every token it
/// was called with, so a test proves which token was spent rather than assuming it.
/// </summary>
internal sealed class FakeCloudStorageProvider(CloudStorageProviderKind kind = CloudStorageProviderKind.GoogleDrive) : ICloudStorageProvider
{
    public CloudStorageProviderKind Kind { get; } = kind;

    public bool IsConfigured { get; set; } = true;

    // ---- OAuth ----
    public CloudTokenSet? TokensToIssue { get; set; } = new("issued-access", "issued-refresh", DateTime.UtcNow.AddHours(1), "read");
    public CloudAccountInfo Account { get; set; } = new("acct-1", "person@example.com", "A Person");
    public CloudTokenSet? TokensToRefresh { get; set; }
    public CloudStorageApiException? RefreshFailure { get; set; }
    public List<string> RefreshTokensUsed { get; } = [];
    public List<string> CodesExchanged { get; } = [];
    public List<CloudTokenSet> Revoked { get; } = [];

    // ---- tokens ----
    /// <summary>Access tokens the provider answers 401 for.</summary>
    public HashSet<string> RejectedTokens { get; } = new(StringComparer.Ordinal);
    public List<string> TokensSeen { get; } = [];

    // ---- content ----
    public Dictionary<string, (CloudFileEntry Entry, byte[] Content)> Files { get; } = new(StringComparer.Ordinal);
    public bool Truncated { get; set; }
    public int Unsupported { get; set; }
    public Exception? ListFailure { get; set; }
    public Dictionary<string, Exception> DownloadFailures { get; } = new(StringComparer.Ordinal);
    /// <summary>Content served for an item regardless of its declared size, with no Length (a lying listing).</summary>
    public Dictionary<string, byte[]> StreamOverrides { get; } = new(StringComparer.Ordinal);
    public List<string> Downloads { get; } = [];
    public Func<CloudFileEntry, Task>? OnDownload { get; set; }

    public CloudFolderInfo Folder { get; set; } = new("folder-1", "Videos", "/Videos");
    public Exception? FolderFailure { get; set; }
    public List<CloudLocation> FoldersRequested { get; } = [];
    public List<CloudDrive> Drives { get; } = [new(null, "My Drive", CloudDriveKind.Personal)];

    public void AddFile(string path, string content, string? revision = null) =>
        AddFile(path, Encoding.UTF8.GetBytes(content), revision);

    public void AddFile(string path, byte[] content, string? revision = null)
    {
        string id = "id-" + path;
        Files[path] = (new CloudFileEntry(id, path, content.Length, revision ?? $"rev-{content.Length}-{path}", "text/plain"), content);
    }

    public void RemoveFile(string path) => Files.Remove(path);

    public Uri BuildAuthorizationUrl(string state, string codeChallenge, Uri redirectUri) =>
        new($"https://vendor.example/authorize?state={Uri.EscapeDataString(state)}&code_challenge={Uri.EscapeDataString(codeChallenge)}&redirect_uri={Uri.EscapeDataString(redirectUri.AbsoluteUri)}");

    public Task<CloudTokenSet> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct)
    {
        CodesExchanged.Add(code);
        return TokensToIssue is { } tokens
            ? Task.FromResult(tokens)
            : throw new CloudStorageApiException("token exchange refused", CloudStorageErrorKind.Unauthorized, HttpStatusCode.BadRequest);
    }

    public Task<CloudTokenSet> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        RefreshTokensUsed.Add(refreshToken);
        if (RefreshFailure is not null) throw RefreshFailure;
        return TokensToRefresh is { } tokens
            ? Task.FromResult(tokens)
            : throw new CloudStorageApiException("invalid_grant", CloudStorageErrorKind.Unauthorized, HttpStatusCode.BadRequest);
    }

    public Task<CloudAccountInfo> GetAccountAsync(string accessToken, CancellationToken ct)
    {
        Spend(accessToken);
        return Task.FromResult(Account);
    }

    public Task RevokeAsync(CloudTokenSet tokens, CancellationToken ct)
    {
        Revoked.Add(tokens);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CloudDrive>> ListDrivesAsync(string accessToken, CancellationToken ct)
    {
        Spend(accessToken);
        return Task.FromResult<IReadOnlyList<CloudDrive>>(Drives);
    }

    public Task<CloudFolderInfo> GetFolderAsync(string accessToken, CloudLocation location, CancellationToken ct)
    {
        Spend(accessToken);
        FoldersRequested.Add(location);
        if (FolderFailure is not null) throw FolderFailure;
        return Task.FromResult(Folder);
    }

    public Task<CloudFolderPage> ListFolderAsync(string accessToken, CloudLocation location, int maxItems, CancellationToken ct)
    {
        Spend(accessToken);
        if (FolderFailure is not null) throw FolderFailure;
        List<CloudItem> items = Files.Values.Select(f => new CloudItem(f.Entry.ItemId, f.Entry.RelativePath, false, f.Entry.Size, null, f.Entry.MimeType)).Take(maxItems).ToList();
        return Task.FromResult(new CloudFolderPage(Folder, items, Files.Count > maxItems));
    }

    public Task<CloudTreeListing> ListTreeAsync(string accessToken, CloudLocation root, int maxEntries, CancellationToken ct)
    {
        Spend(accessToken);
        if (ListFailure is not null) throw ListFailure;
        List<CloudFileEntry> entries = Files.Values.Select(f => f.Entry).Take(maxEntries).ToList();
        return Task.FromResult(new CloudTreeListing(entries, Truncated || Files.Count > maxEntries, Unsupported));
    }

    public async Task<CloudDownload> OpenDownloadAsync(string accessToken, CloudLocation root, CloudFileEntry file, CancellationToken ct)
    {
        Spend(accessToken);
        Downloads.Add(file.RelativePath);
        if (OnDownload is not null) await OnDownload(file);
        if (DownloadFailures.TryGetValue(file.ItemId, out Exception? failure)) throw failure;
        if (StreamOverrides.TryGetValue(file.ItemId, out byte[]? overridden))
            return new CloudDownload(new MemoryStream(overridden), null, null);
        (CloudFileEntry _, byte[] content) = Files.Values.First(f => f.Entry.ItemId == file.ItemId);
        return new CloudDownload(new MemoryStream(content), content.Length, "text/plain");
    }

    private void Spend(string accessToken)
    {
        TokensSeen.Add(accessToken);
        if (RejectedTokens.Contains(accessToken))
            throw new CloudStorageApiException("401", CloudStorageErrorKind.Unauthorized, HttpStatusCode.Unauthorized);
    }
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
    public DateTime UtcNow => _now.UtcDateTime;
}

internal sealed class CloudFakeCurrentUser(Guid userId, Guid? organizationId, bool authenticated = true) : ICurrentUser
{
    public Guid UserId { get; } = userId;
    public string Email => "user@example.com";
    public bool IsAuthenticated { get; } = authenticated;
    public Guid? OrganizationId { get; } = organizationId;
    public OrganizationRole? OrgRole => OrganizationRole.Owner;
    public bool IsPlatformAdmin => false;
}

/// <summary>
/// Everything one cloud test needs, wired exactly as the host wires it, over an in-memory database
/// whose name is known (so a second context can observe what the first has saved).
/// </summary>
internal sealed class CloudFixture
{
    public CloudFixture(
        CloudStorageProviderKind kind = CloudStorageProviderKind.GoogleDrive,
        bool configured = true,
        string? publicBaseUrl = "https://reelbolt.ai")
    {
        DbName = $"cloud-{Guid.NewGuid():N}";
        Db = NewContext();
        Provider = new FakeCloudStorageProvider(kind) { IsConfigured = configured };
        Registry = new CloudStorageProviderRegistry([Provider]);
        Options = new CloudStorageOptions { PublicBaseUrl = publicBaseUrl };
        Options.For(kind).ClientId = configured ? "client-id" : null;
        Options.For(kind).ClientSecret = configured ? "client-secret" : null;
        Connections = new CloudStorageConnectionService(
            Db, Registry, Microsoft.Extensions.Options.Options.Create(Options), Secrets, NullLogger<CloudStorageConnectionService>.Instance, Clock);
        Publisher
            .Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
            .Callback<MediaDerivativesRequested, CancellationToken>((message, _) => Published.Add(message))
            .Returns(Task.CompletedTask);
        Sync = new CloudSyncService(
            Db, Registry, Connections, Storage, Microsoft.Extensions.Options.Options.Create(Options), NullLogger<CloudSyncService>.Instance, Clock,
            publishEndpoint: Publisher.Object, csamMatcher: Csam);
    }

    /// <summary>The broker: every derivative request a sync published, in order.</summary>
    public Mock<IPublishEndpoint> Publisher { get; } = new();

    public List<MediaDerivativesRequested> Published { get; } = [];

    /// <summary>The CSAM matcher a sync scans a written media row with (Clean unless a test says otherwise).</summary>
    public FakeCsamMatcher Csam { get; } = new();

    public string DbName { get; }
    public TestTenant Tenant { get; } = new();
    public InferenceApiDbContext Db { get; }
    public FakeCloudStorageProvider Provider { get; }
    public CloudStorageProviderRegistry Registry { get; }
    public CloudStorageOptions Options { get; }
    public FakeSecretProtector Secrets { get; } = new();
    public InMemoryFileStorage Storage { get; } = new();
    public ManualTimeProvider Clock { get; } = new();
    public CloudStorageConnectionService Connections { get; }
    public CloudSyncService Sync { get; }

    public Guid UserId { get; } = Guid.NewGuid();
    public Guid OrganizationId => TestTenancy.OrgOf(UserId);

    public InferenceApiDbContext NewContext() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(DbName).Options, Tenant);

    public async Task<Project> AddProjectAsync(Guid? organizationId = null, Guid? ownerId = null)
    {
        Project project = GitHubTestData.NewProject(organizationId ?? OrganizationId, ownerId ?? UserId);
        Db.Projects.Add(project);
        await Db.SaveChangesAsync();
        return project;
    }

    public async Task<CloudStorageConnection> AddConnectionAsync(
        Guid? userId = null,
        Guid? organizationId = null,
        string accessToken = "access-1",
        string? refreshToken = "refresh-1",
        TimeSpan? expiresIn = null)
    {
        CloudStorageConnection connection = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? UserId,
            OrganizationId = organizationId ?? OrganizationId,
            Provider = Provider.Kind,
            AccountId = $"acct-{Guid.NewGuid():N}",
            AccountEmail = "person@example.com",
            ProtectedAccessToken = Secrets.Protect(accessToken),
            ProtectedRefreshToken = refreshToken is null ? null : Secrets.Protect(refreshToken),
            AccessTokenExpiresAt = Clock.UtcNow + (expiresIn ?? TimeSpan.FromHours(1)),
            CreatedAt = Clock.UtcNow,
            UpdatedAt = Clock.UtcNow
        };
        Db.CloudStorageConnections.Add(connection);
        await Db.SaveChangesAsync();
        return connection;
    }

    public async Task<ProjectCloudLink> AddLinkAsync(Project project, CloudStorageConnection? connection, string? includePathsJson = null)
    {
        ProjectCloudLink link = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OrganizationId = project.OrganizationId,
            Provider = Provider.Kind,
            ConnectionId = connection?.Id,
            FolderId = "folder-1",
            FolderDisplayPath = "/Videos",
            IncludePathsJson = includePathsJson,
            CreatedByUserId = UserId
        };
        Db.ProjectCloudLinks.Add(link);
        await Db.SaveChangesAsync();
        return link;
    }

    public string SourceKind => CloudStorageProviders.SourceKind(Provider.Kind);

    public Task<List<ProjectFile>> SyncedFilesAsync(Guid projectId) =>
        Db.ProjectFiles.Where(f => f.ProjectId == projectId && f.SourceKind == SourceKind).OrderBy(f => f.OriginalPath).ToListAsync();
}

/// <summary>A matcher that settles the row it is given with a fixed status and remembers what it scanned.</summary>
internal sealed class FakeCsamMatcher : ICsmaHashMatcher
{
    public CsamScanStatus Status { get; set; } = CsamScanStatus.Clean;
    public List<Guid> Scanned { get; } = [];

    public Task<CsamScanOutcome> ScanAsync(ProjectFile file, Guid? actorUserId, CancellationToken ct)
    {
        Scanned.Add(file.Id);
        file.CsamStatus = Status;
        return Task.FromResult(new CsamScanOutcome(Status, null, null, Status == CsamScanStatus.Quarantined, null));
    }
}
