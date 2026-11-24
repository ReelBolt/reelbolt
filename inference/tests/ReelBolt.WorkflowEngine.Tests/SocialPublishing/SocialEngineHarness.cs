#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Inference.Api.Services.Notifications;
using ReelBolt.Inference.Api.Services.SocialPublishing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.CloudStorage.Core;
using ReelBolt.WorkflowEngine.Tests.GitHub;

namespace ReelBolt.WorkflowEngine.Tests.SocialPublishing;

/// <summary>A publishing platform in memory. Counts calls, can fail, can block mid-publish. No network, no credential.</summary>
internal sealed class EnginePlatform(SocialPlatformKind kind = SocialPlatformKind.YouTube) : ISocialPlatform
{
    private int _calls;

    public SocialPlatformKind Kind { get; } = kind;
    public bool IsConfigured { get; set; } = true;
    public IReadOnlyList<string> Scopes { get; } = ["scope.upload"];
    public int PublishCalls => Volatile.Read(ref _calls);
    public List<string> TokensSeen { get; } = [];
    public List<SocialPublishRequest> Requests { get; } = [];
    public long BytesStreamed { get; private set; }
    public Exception? PublishFailure { get; set; }
    public Queue<Exception> FailuresInOrder { get; } = new();
    public TaskCompletionSource? Gate { get; set; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Uri BuildAuthorizationUrl(string state, string codeChallenge, Uri redirectUri) => new("https://vendor.example/authorize");

    public Task<SocialTokenSet> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct) => throw new NotSupportedException();

    public SocialApiException? RefreshFailure { get; set; }

    public Task<SocialTokenSet> RefreshAsync(string refreshToken, CancellationToken ct) =>
        RefreshFailure is not null ? Task.FromException<SocialTokenSet>(RefreshFailure) :
        Task.FromResult(new SocialTokenSet("refreshed-access", "refreshed-refresh", DateTime.UtcNow.AddHours(1), null));

    public Task<SocialAccountInfo> GetAccountAsync(string accessToken, CancellationToken ct) => throw new NotSupportedException();

    public Task RevokeAsync(SocialTokenSet tokens, CancellationToken ct) => Task.CompletedTask;

    public async Task<SocialPublishResult> PublishAsync(string accessToken, SocialPublishRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        lock (TokensSeen) { TokensSeen.Add(accessToken); Requests.Add(request); }
        Started.TrySetResult();
        await using Stream video = await request.OpenVideoAsync(ct);
        using MemoryStream sink = new();
        await video.CopyToAsync(sink, ct);
        BytesStreamed = sink.Length;
        if (Gate is not null) await Gate.Task.WaitAsync(ct);
        if (FailuresInOrder.Count > 0) throw FailuresInOrder.Dequeue();
        if (PublishFailure is not null) throw PublishFailure;
        return new SocialPublishResult($"remote-{Guid.NewGuid():N}", "https://youtube.example/shorts/abc");
    }

    public List<(string Token, string RemoteId)> MetricsCalls { get; } = [];
    public Func<string, SocialMetricsSnapshot>? MetricsReader { get; set; }
    public Exception? MetricsFailure { get; set; }

    public Task<SocialMetricsSnapshot> ReadMetricsAsync(string accessToken, string remotePostId, CancellationToken ct)
    {
        lock (MetricsCalls) MetricsCalls.Add((accessToken, remotePostId));
        if (MetricsFailure is not null) throw MetricsFailure;
        return Task.FromResult(MetricsReader?.Invoke(remotePostId) ?? throw new NotSupportedException());
    }
}

internal sealed class FakeOutputs : ISocialOutputResolver
{
    private readonly Dictionary<(Guid Org, Guid Project, string Kind, Guid Id), SocialResolvedOutput> _items = [];

    public void Add(Guid org, Guid project, string kind, Guid id, SocialResolvedOutput output) => _items[(org, project, kind, id)] = output;

    public void Remove(Guid org, Guid project, string kind, Guid id) => _items.Remove((org, project, kind, id));

    public Task<SocialResolvedOutput?> ResolveAsync(Guid organizationId, Guid projectId, string outputKind, Guid outputId, CancellationToken ct) =>
        Task.FromResult(_items.TryGetValue((organizationId, projectId, outputKind, outputId), out SocialResolvedOutput? o) ? o : null);
}

internal sealed class FakeModeration : ISocialPostModeration
{
    public SocialModerationVerdict Verdict { get; set; } = new(true, string.Empty, string.Empty);
    public int Screened;

    public Task<SocialModerationVerdict> ScreenAsync(SocialPost post, SocialResolvedOutput output, CancellationToken ct)
    {
        Interlocked.Increment(ref Screened);
        return Task.FromResult(Verdict);
    }
}

internal sealed class FakeEntitlements : IOrganizationEntitlements
{
    public bool SocialAllowed { get; set; } = true;

    public ValueTask<OrganizationEntitlementSet> GetAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(OrganizationEntitlementSet.Unlimited with { SocialPublishingAllowed = SocialAllowed, Tier = "test" });
}

/// <summary>Everything one engine or posts-route test needs, on a real relational schema (SQLite) with the host's wiring.</summary>
internal sealed class EngineFixture : IDisposable
{
    private readonly SqliteConnection _keeper;
    private readonly string _connectionString;

    public EngineFixture(Action<SocialPublishEngineOptions>? configure = null, SocialPlatformKind kind = SocialPlatformKind.YouTube)
    {
        string name = $"engine{Guid.NewGuid():N}";
        _connectionString = $"Data Source=file:{name}?mode=memory&cache=shared;Foreign Keys=False";
        _keeper = new SqliteConnection(_connectionString);
        _keeper.Open();
        DbOptions = new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connectionString).Options;
        Db = NewDb();
        Db.Database.EnsureCreated();

        Platform = new EnginePlatform(kind);
        Registry = new SocialPlatformRegistry([Platform]);
        Options = new SocialPublishingOptions();
        Options.Engine.LeaseSeconds = 5;
        Options.Engine.BackoffSeconds = 60;
        Options.Engine.MonthlyPostCapPerOrganization = 0;
        Options.Engine.DailyBudgetYouTube = 0;
        Options.Engine.DailyBudgetLinkedIn = 0;
        Options.YouTube.MaxPerOrgPerDay = 0;
        configure?.Invoke(Options.Engine);

        UserId = Guid.NewGuid();
        OrganizationId = TestTenancy.OrgOf(UserId);
        ProjectId = Guid.NewGuid();
        OutputId = Guid.NewGuid();
        Notifier = new Mock<IUserActivityNotifier>();
        Notifier.Setup(n => n.SocialPublishingAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((Guid org, Guid? project, Guid user, string subject, string details, CancellationToken _) => Notices.Add((org, project, user, subject, details)))
            .ReturnsAsync(true);
        Storage = new Mock<IFileStorageService>();
        Storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[2048]));

        Seed();
    }

    public List<(Guid Org, Guid? Project, Guid User, string Subject, string Details)> Notices { get; } = [];
    public DbContextOptions<InferenceApiDbContext> DbOptions { get; }
    public InferenceApiDbContext Db { get; }
    public TestTenancy.MutableTenant Tenant { get; } = new();
    public EnginePlatform Platform { get; }
    public SocialPlatformRegistry Registry { get; }
    public SocialPublishingOptions Options { get; }
    public FakeSecretProtector Secrets { get; } = new();
    public ManualTimeProvider Clock { get; } = new();
    public FakeOutputs Outputs { get; } = new();
    public FakeModeration Moderation { get; } = new();
    public FakeEntitlements Entitlements { get; } = new();
    public Mock<IUserActivityNotifier> Notifier { get; }
    public Mock<IFileStorageService> Storage { get; }
    public Guid UserId { get; }
    public Guid OrganizationId { get; }
    public Guid ProjectId { get; }
    public Guid OutputId { get; }
    public SocialAccount Account { get; private set; } = null!;

    public InferenceApiDbContext NewDb() => new(DbOptions);

    private void Seed()
    {
        Db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Org", Slug = $"org-{OrganizationId:N}"[..20], Kind = OrganizationKind.Personal });
        Db.Projects.Add(new Project { Id = ProjectId, Name = "P", OwnerId = UserId, OrganizationId = OrganizationId });
        Db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = OrganizationId, UserId = UserId, Role = OrganizationRole.Owner });
        Db.SaveChanges();
        Account = AddAccount();
        SetOutput(GoodOutput());
    }

    public SocialResolvedOutput GoodOutput() => new(
        ProjectId, $"projects/{ProjectId}/outputFiles/final.mp4", "video/mp4", 2048, DurationSeconds: 30, Width: 1080, Height: 1920,
        ProducedAt: Clock.UtcNow.AddHours(-1), RunStartedAt: Clock.UtcNow.AddHours(-1), IsProjectFile: false, FileFlagged: false);

    public void SetOutput(SocialResolvedOutput output) =>
        Outputs.Add(OrganizationId, ProjectId, SocialOutputKinds.StepResult, OutputId, output);

    public SocialAccount AddAccount(Guid? organizationId = null, string status = SocialAccountStatus.Active)
    {
        SocialAccount account = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId ?? OrganizationId,
            Platform = Platform?.Kind ?? SocialPlatformKind.YouTube,
            RemoteAccountId = $"chan-{Guid.NewGuid():N}",
            DisplayName = "A Channel",
            ProtectedAccessToken = Secrets.Protect("access-1"),
            ProtectedRefreshToken = Secrets.Protect("refresh-1"),
            AccessTokenExpiresAt = DateTime.UtcNow.AddHours(1),
            Status = status,
            ConnectedByUserId = UserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        Db.SocialAccounts.Add(account);
        Db.SaveChanges();
        return account;
    }

    public SocialPost AddPost(
        string status = SocialPostStatus.Scheduled, TimeSpan? dueIn = null, SocialAccount? account = null,
        Guid? createdBy = null, string? title = "A title", string? body = "A body", int attempts = 0)
    {
        SocialPost post = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = OrganizationId,
            ProjectId = ProjectId,
            OutputKind = SocialOutputKinds.StepResult,
            OutputId = OutputId,
            SocialAccountId = (account ?? Account).Id,
            Platform = Platform.Kind,
            Format = "short",
            Title = title,
            Body = body,
            HashtagsJson = "[\"#one\"]",
            OptionsJson = "{\"privacy\":\"public\",\"aiGenerated\":true}",
            ScheduledAt = Clock.UtcNow + (dueIn ?? TimeSpan.Zero),
            TimeZone = "Europe/Rome",
            Status = status,
            Attempts = attempts,
            CreatedByUserId = createdBy ?? UserId,
            CreatedAt = Clock.UtcNow,
            UpdatedAt = Clock.UtcNow
        };
        Db.SocialPosts.Add(post);
        Db.SaveChanges();
        return post;
    }

    /// <summary>Seeds <paramref name="count"/> publish slots (what earlier posts took) for an organization on a UTC day.</summary>
    public void AddLedger(Guid organizationId, DateTime day, int count = 1, SocialPlatformKind? platform = null)
    {
        for (int i = 0; i < count; i++)
        {
            Db.SocialPublishLedger.Add(new SocialPublishLedgerEntry
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                Platform = platform ?? Platform.Kind,
                PostId = Guid.NewGuid(),
                Day = DateTime.SpecifyKind(day.Date, DateTimeKind.Utc),
                CreatedAt = day
            });
        }

        Db.SaveChanges();
    }

    public SocialPublishingService NewEngine(InferenceApiDbContext? db = null)
    {
        InferenceApiDbContext context = db ?? NewDb();
        SocialConnectionService connections = new(
            context, Registry, Microsoft.Extensions.Options.Options.Create(Options), Secrets, new SocialRefreshLock(),
            NullLogger<SocialConnectionService>.Instance, Clock, Notifier.Object);
        return new SocialPublishingService(
            context, DbOptions, Registry, connections, Outputs, Storage.Object, Moderation, Entitlements, Notifier.Object,
            Microsoft.Extensions.Options.Options.Create(Options), NullLogger<SocialPublishingService>.Instance, Clock);
    }

    /// <summary>Claims and publishes one post the way a sweep does. Null when the claim was refused.</summary>
    public async Task<SocialClaim?> RunAsync(SocialPost post)
    {
        SocialPublishingService engine = NewEngine();
        SocialClaim? claim = await engine.TryClaimAsync(post.Id, CancellationToken.None);
        if (claim is not null) await engine.PublishClaimedAsync(claim, CancellationToken.None);
        return claim;
    }

    public async Task<SocialPost> ReloadAsync(Guid id) => await NewDb().SocialPosts.AsNoTracking().SingleAsync(p => p.Id == id);

    public void Dispose()
    {
        Db.Dispose();
        _keeper.Dispose();
    }
}
