#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Inference.Api.Services.Notifications;
using ReelBolt.Inference.Api.Services.SocialPublishing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.CloudStorage.Core;
using ReelBolt.WorkflowEngine.Tests.GitHub;

namespace ReelBolt.WorkflowEngine.Tests.SocialPublishing;

/// <summary>A social platform in memory: no network, no credential (none exists). Records every token it was called with.</summary>
internal sealed class FakeSocialPlatform(SocialPlatformKind kind = SocialPlatformKind.YouTube) : ISocialPlatform
{
    public SocialPlatformKind Kind { get; } = kind;
    public bool IsConfigured { get; set; } = true;
    public IReadOnlyList<string> Scopes { get; } = ["scope.upload"];

    public SocialTokenSet TokensToIssue { get; set; } = new("issued-access", "issued-refresh", DateTime.UtcNow.AddHours(1), "scope.upload");
    public SocialAccountInfo Account { get; set; } = new("chan-1", "A Channel", "https://img.example/a.png");
    public SocialTokenSet? TokensToRefresh { get; set; }
    public SocialApiException? RefreshFailure { get; set; }
    public TimeSpan RefreshDelay { get; set; } = TimeSpan.Zero;
    public List<string> RefreshTokensUsed { get; } = [];
    public List<string> CodesExchanged { get; } = [];
    public List<SocialTokenSet> Revoked { get; } = [];
    public bool RevokeFails { get; set; }

    public Uri BuildAuthorizationUrl(string state, string codeChallenge, Uri redirectUri) =>
        new($"https://vendor.example/authorize?state={Uri.EscapeDataString(state)}&code_challenge={Uri.EscapeDataString(codeChallenge)}&redirect_uri={Uri.EscapeDataString(redirectUri.AbsoluteUri)}");

    public Task<SocialTokenSet> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct)
    {
        CodesExchanged.Add(code);
        return Task.FromResult(TokensToIssue);
    }

    public async Task<SocialTokenSet> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        RefreshTokensUsed.Add(refreshToken);
        if (RefreshDelay > TimeSpan.Zero) await Task.Delay(RefreshDelay, ct);
        if (RefreshFailure is not null) throw RefreshFailure;
        return TokensToRefresh ?? new SocialTokenSet($"access-{RefreshTokensUsed.Count}", $"refresh-{RefreshTokensUsed.Count}", DateTime.UtcNow.AddHours(1), null);
    }

    public Task<SocialAccountInfo> GetAccountAsync(string accessToken, CancellationToken ct) => Task.FromResult(Account);

    public Task RevokeAsync(SocialTokenSet tokens, CancellationToken ct)
    {
        Revoked.Add(tokens);
        if (RevokeFails) throw new SocialApiException("boom", SocialErrorKind.ServerError);
        return Task.CompletedTask;
    }

    public Task<SocialPublishResult> PublishAsync(string accessToken, SocialPublishRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<SocialMetricsSnapshot> ReadMetricsAsync(string accessToken, string remotePostId, CancellationToken ct) =>
        throw new NotSupportedException();
}

/// <summary>A refresh lock that records how often it was taken (the real one is the same class).</summary>
internal sealed class RecordingRefreshLock : SocialRefreshLock
{
    public int Acquisitions;

    public override Task<SocialRefreshGuard> AcquireAsync(InferenceApiDbContext db, Guid accountId, CancellationToken ct)
    {
        Interlocked.Increment(ref Acquisitions);
        return base.AcquireAsync(db, accountId, ct);
    }
}

internal sealed class SocialFakeUser(Guid userId, Guid? organizationId, OrganizationRole? role = OrganizationRole.Owner, bool authenticated = true) : ICurrentUser
{
    public Guid UserId { get; } = userId;
    public string Email => "user@example.com";
    public bool IsAuthenticated { get; } = authenticated;
    public Guid? OrganizationId { get; } = organizationId;
    public OrganizationRole? OrgRole { get; } = role;
    public bool IsPlatformAdmin => false;
}

/// <summary>Captures every log line so a test can prove no token reached a log.</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory, ILoggerProvider
{
    public List<string> Lines { get; } = [];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(Lines);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (lines) lines.Add(formatter(state, exception) + (exception?.ToString() ?? string.Empty));
        }
    }
}

/// <summary>Everything one social test needs, wired as the host wires it, over a named in-memory database.</summary>
internal sealed class SocialFixture
{
    public SocialFixture(bool configured = true, string? publicBaseUrl = "https://reelbolt.ai", SocialRefreshLock? refreshLock = null, string? dbName = null)
    {
        DbName = dbName ?? $"social-{Guid.NewGuid():N}";
        Notifier.Setup(n => n.SocialPublishingAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((Guid org, Guid? project, Guid user, string subject, string details, CancellationToken _) => { lock (Notices) Notices.Add((org, project, user, subject)); })
            .ReturnsAsync(true);
        Db = NewContext();
        Platform = new FakeSocialPlatform { IsConfigured = configured };
        Registry = new SocialPlatformRegistry([Platform]);
        Options = new SocialPublishingOptions { PublicBaseUrl = publicBaseUrl };
        Options.YouTube.ClientId = configured ? "client-id" : null;
        Options.YouTube.ClientSecret = configured ? "client-secret" : null;
        Lock = refreshLock ?? new RecordingRefreshLock();
        Connections = NewService(Db);
        SetRole(OrganizationId, OrganizationRole.Owner).GetAwaiter().GetResult();
        AddOrganization(OrganizationId).GetAwaiter().GetResult();
    }

    /// <summary>Sets (or with null removes) the user's CURRENT membership row in an organization.</summary>
    public async Task SetRole(Guid organizationId, OrganizationRole? role)
    {
        OrganizationMembership? row = await Db.OrganizationMemberships.FindAsync(organizationId, UserId);
        if (row is not null) Db.OrganizationMemberships.Remove(row);
        if (role is { } r) Db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = organizationId, UserId = UserId, Role = r });
        await Db.SaveChangesAsync();
    }

    public async Task AddOrganization(Guid organizationId)
    {
        if (await Db.Organizations.FindAsync(organizationId) is null)
        {
            Db.Organizations.Add(new Organization { Id = organizationId, Name = "Org", Slug = $"org-{organizationId:N}" });
            await Db.SaveChangesAsync();
        }
    }

    public Mock<IUserActivityNotifier> Notifier { get; } = new();
    public List<(Guid Org, Guid? Project, Guid User, string Subject)> Notices { get; } = [];
    public string DbName { get; }
    public TestTenant Tenant { get; } = new();
    public InferenceApiDbContext Db { get; }
    public FakeSocialPlatform Platform { get; }
    public SocialPlatformRegistry Registry { get; }
    public SocialPublishingOptions Options { get; }
    public FakeSecretProtector Secrets { get; } = new();
    public ManualTimeProvider Clock { get; } = new();
    public CapturingLoggerFactory Logs { get; } = new();
    public SocialRefreshLock Lock { get; }
    public SocialConnectionService Connections { get; }

    public Guid UserId { get; } = Guid.NewGuid();
    public Guid OrganizationId => TestTenancy.OrgOf(UserId);

    public InferenceApiDbContext NewContext() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(DbName).Options, Tenant);

    public SocialConnectionService NewService(InferenceApiDbContext db) =>
        new(db, Registry, Microsoft.Extensions.Options.Options.Create(Options), Secrets, Lock,
            Logs.CreateLogger("social") is { } ? new LoggerAdapter(Logs) : NullLogger<SocialConnectionService>.Instance, Clock, Notifier.Object);

    private sealed class LoggerAdapter(CapturingLoggerFactory factory) : ILogger<SocialConnectionService>
    {
        private readonly ILogger _inner = factory.CreateLogger("social");

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _inner.Log(logLevel, eventId, state, exception, formatter);
    }

    public async Task<SocialAccount> AddAccountAsync(
        Guid? organizationId = null,
        string accessToken = "access-1",
        string? refreshToken = "refresh-1",
        TimeSpan? expiresIn = null)
    {
        SocialAccount account = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId ?? OrganizationId,
            Platform = Platform.Kind,
            RemoteAccountId = $"chan-{Guid.NewGuid():N}",
            DisplayName = "A Channel",
            ProtectedAccessToken = Secrets.Protect(accessToken),
            ProtectedRefreshToken = refreshToken is null ? null : Secrets.Protect(refreshToken),
            AccessTokenExpiresAt = Clock.UtcNow + (expiresIn ?? TimeSpan.FromHours(1)),
            ConnectedByUserId = UserId,
            CreatedAt = Clock.UtcNow,
            UpdatedAt = Clock.UtcNow
        };
        Db.SocialAccounts.Add(account);
        await Db.SaveChangesAsync();
        return account;
    }
}
