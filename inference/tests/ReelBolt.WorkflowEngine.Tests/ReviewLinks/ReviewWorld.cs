#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Shared.Auth;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Editing.ReviewLinks;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Notifications;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Tests.Outputs;

namespace ReelBolt.WorkflowEngine.Tests.ReviewLinks;

/// <summary>
/// One organization (the owner is its Owner) with a project, a timeline and one passed full-timeline render over
/// an in-memory object store, on a shared in-memory database. Controllers get their OWN context: the owner side
/// acts as a member of the organization, the public side as the anonymous request it is (no tenant organization,
/// so the named Tenant filter hides every filtered row unless the code ignores it explicitly).
/// </summary>
internal sealed class ReviewWorld : IDisposable
{
    public const string Bucket = "reelbolt-review-bucket";
    public static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    public const string Origin = "https://app.example.test";

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly List<InferenceApiDbContext> _contexts = [];

    public FixedClock Clock { get; } = new(Now);

    /// <summary>The shared in-memory database name, for a test that hosts the real pipeline.</summary>
    public string DbNameForTests => _dbName;
    public Guid UserId { get; } = Guid.NewGuid();
    public Guid OrgId => TestTenancy.OrgOf(UserId);
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
    public Mock<IAmazonS3> S3 { get; } = new();
    public InferenceApiDbContext Seed { get; }
    public Project Project { get; }
    public EditTimelineEntity Timeline { get; }
    public Guid ExecutionId { get; private set; }
    public Guid ResultId { get; private set; }
    public string RenderKey { get; private set; } = string.Empty;

    public static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public ReviewWorld()
    {
        Seed = NewDb(new TestTenancy.MutableTenant());
        Seed.ApplicationUsers.Add(new ApplicationUser { Id = UserId, Email = "owner@example.com", DisplayName = "Olivia Owner" });
        Seed.Organizations.Add(new Organization { Id = OrgId, Name = "Studio", Slug = $"studio-{OrgId:N}", Kind = OrganizationKind.Team });
        Seed.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = OrgId, UserId = UserId, Role = OrganizationRole.Owner });
        Project = new Project { Id = Guid.NewGuid(), Name = "Launch", OwnerId = UserId, OrganizationId = OrgId };
        Seed.Projects.Add(Project);
        Guid timelineId = Guid.NewGuid();
        Timeline = new EditTimelineEntity
        {
            Id = timelineId,
            ProjectId = Project.Id,
            Name = "Client cut",
            DocumentJson = "{\"settings\":{\"rate\":{\"num\":30,\"den\":1}}}",
            Revision = 3,
        };
        Seed.EditTimelines.Add(Timeline);
        Seed.SaveChanges();

        S3.Setup(s => s.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((_, key, _) => Objects.TryGetValue(key, out byte[]? b)
                ? Task.FromResult(new GetObjectMetadataResponse { ContentLength = b.Length })
                : throw Missing());
        S3.Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .Returns<GetObjectRequest, CancellationToken>((req, _) =>
            {
                if (!Objects.TryGetValue(req.Key, out byte[]? b)) throw Missing();
                byte[] slice = req.ByteRange is { } r ? b[(int)r.Start..((int)r.End + 1)] : b;
                return Task.FromResult(new GetObjectResponse { ResponseStream = new MemoryStream(slice), HttpStatusCode = HttpStatusCode.OK });
            });

        AddRender();
    }

    private static AmazonS3Exception Missing() => new("missing") { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchKey" };

    public static byte[] Bytes(int n) => Enumerable.Range(0, n).Select(i => (byte)(i % 251)).ToArray();

    public InferenceApiDbContext NewDb(TestTenancy.MutableTenant tenant)
    {
        InferenceApiDbContext db = new(
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(_dbName).Options, tenant);
        _contexts.Add(db);
        return db;
    }

    /// <summary>A fresh context acting as a member of the given organization (default: the owner's).</summary>
    public InferenceApiDbContext MemberDb(Guid? organizationId = null)
    {
        TestTenancy.MutableTenant tenant = new();
        tenant.ActAsOrganization(organizationId ?? OrgId);
        return NewDb(tenant);
    }

    /// <summary>A fresh context for an anonymous request: not system, no organization.</summary>
    public InferenceApiDbContext AnonymousDb()
    {
        TestTenancy.MutableTenant tenant = new();
        tenant.ActAsOrganization(null);
        return NewDb(tenant);
    }

    /// <summary>Adds a render (workflow, execution, step result, object). Returns its execution id.</summary>
    public (Guid ExecutionId, Guid ResultId, string Key) AddRender(
        string fileName = "final.mp4",
        ExecutionStatus status = ExecutionStatus.Passed,
        TimelineRenderPreset preset = TimelineRenderPreset.Hd1080p16x9,
        TimelineRenderRange? range = null,
        string? audioJobKind = null,
        int revision = 3,
        double durationSec = 10,
        DateTime? queuedAt = null,
        bool storeObject = true,
        Guid? timelineId = null,
        int objectBytes = 1000)
    {
        Guid tl = timelineId ?? Timeline.Id;
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = Project.Id, Name = "Render: Client cut", Kind = WorkflowDefinitionKind.TimelineRender };
        TimelineRenderStepConfig config = new(tl, Revision: revision, Range: range, Preset: preset, AudioJobKind: audioJobKind);
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.TimelineRender,
            Label = "Render",
            TimelineRenderConfigJson = JsonSerializer.Serialize(config, ConfigJson),
        };
        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            ProjectId = Project.Id,
            OrganizationId = OrgId,
            Status = status,
            StartedAt = (queuedAt ?? Now.UtcDateTime).AddSeconds(1),
            QueuedAt = queuedAt ?? Now.UtcDateTime,
        };
        string key = $"projects/{Project.Id}/outputFiles/{execution.Id}/{fileName}";
        WorkflowStepResult result = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = step.Id,
            Status = StepStatus.Completed,
            OutputStorageKey = key,
            OutputJson = JsonSerializer.Serialize(new { revision, durationSec }),
            CompletedAt = Now.UtcDateTime,
        };
        Seed.WorkflowDefinitions.Add(workflow);
        Seed.WorkflowSteps.Add(step);
        Seed.WorkflowExecutions.Add(execution);
        Seed.WorkflowStepResults.Add(result);
        Seed.SaveChanges();
        if (storeObject) Objects[key] = Bytes(objectBytes);

        ExecutionId = execution.Id;
        ResultId = result.Id;
        RenderKey = key;
        return (execution.Id, result.Id, key);
    }

    public static ICurrentUser User(Guid userId, Guid orgId, OrganizationRole role = OrganizationRole.Owner) =>
        new OutputsUser(userId, orgId, role);

    public ReviewLinksController Owner(
        Guid? userId = null,
        OrganizationRole role = OrganizationRole.Owner,
        Guid? organizationId = null,
        PublishModeration? moderation = null,
        ReviewLinkOptions? options = null,
        Dictionary<string, string?>? config = null)
    {
        Guid uid = userId ?? UserId;
        Guid org = organizationId ?? (userId is null ? OrgId : TestTenancy.OrgOf(uid));
        InferenceApiDbContext db = MemberDb(org);
        ICurrentUser user = User(uid, org, role);
        Dictionary<string, string?> settings = config ?? new() { ["OutputLinks:PublicBaseUrl"] = Origin };
        return new ReviewLinksController(
            db, user, new ProjectAccessService(db, user), new ReviewRenderResolver(db),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            Options.Create(options ?? new ReviewLinkOptions()), moderation, Clock)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    public sealed record PublicHarness(ReviewLinksPublicController Controller, InferenceApiDbContext Db, Mock<IUserActivityNotifier> Notifier);

    public PublicHarness Public(
        ReviewLinkWriteLimiter? limiter = null,
        ReviewLinkOptions? options = null,
        PublishModeration? moderation = null,
        string? range = null,
        Mock<IUserActivityNotifier>? notifier = null)
    {
        InferenceApiDbContext db = AnonymousDb();
        Mock<IUserActivityNotifier> activity = notifier ?? new Mock<IUserActivityNotifier>();
        ReviewRenderResolver renders = new(db);
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = Bucket }).Build();
        ReviewLinksPublicController controller = new(
            db, new ReviewLinkResolver(db, renders, Clock), limiter ?? new ReviewLinkWriteLimiter(),
            new ReviewLinkActivityNotifier(db, activity.Object, Clock), S3.Object, config,
            Options.Create(options ?? new ReviewLinkOptions()), moderation, Clock)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        if (range is not null) controller.Request.Headers.Range = range;
        return new PublicHarness(controller, db, activity);
    }

    /// <summary>Creates a link through the owner controller; returns its id and the one-time token.</summary>
    public async Task<(Guid Id, string Token)> CreateLinkAsync(
        string? label = null, int? days = null, bool? allowComments = null, Guid? renderExecutionId = null, ReviewLinkOptions? options = null)
    {
        IActionResult result = await Owner(options: options).Create(
            Project.Id, Timeline.Id, new CreateReviewLinkRequest(label, renderExecutionId, days, allowComments), CancellationToken.None);
        ObjectResult created = result.Should_BeStatus(StatusCodes.Status201Created);
        CreateReviewLinkResponse body = (CreateReviewLinkResponse)created.Value!;
        return (body.Id, TokenOf(body.Url));
    }

    public static string TokenOf(string url) => url[(url.LastIndexOf('/') + 1)..];

    /// <summary>
    /// A moderation service reading the stored verdict the render already has (fail-closed tests): the
    /// action Block stores a block, Unavailable with <c>available: false</c> a recorded outage, anything
    /// else an allow. Publish only reads; it never judges, so no gate or sampler is involved.
    /// </summary>
    public PublishModeration Moderation(ModerationAction action, bool available = true)
    {
        InferenceApiDbContext db = NewDb(new TestTenancy.MutableTenant());
        bool outage = action == ModerationAction.Unavailable || !available;
        db.OutputModerationVerdicts.RemoveRange(db.OutputModerationVerdicts.Where(v => v.StorageKey == RenderKey));
        db.OutputModerationVerdicts.Add(new OutputModerationVerdict
        {
            Id = Guid.NewGuid(),
            OrganizationId = OrgId,
            ProjectId = Project.Id,
            StorageKey = RenderKey,
            SourceVersion = "etag:2",
            State = outage ? nameof(ModerationAction.Unavailable) : action.ToString(),
            ReasonMessage = action == ModerationAction.Block ? "Not allowed." : null,
            ModelSnapshot = "omni-moderation-2024-09-26"
        });
        db.SaveChanges();

        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectMetadata(2, "video/mp4", "\"etag\""));
        return new PublishModeration(
            new Mock<IContentModerationGate>().Object, storage.Object, new Mock<IModerationMediaSampler>().Object,
            new Mock<IInferenceProviderResolver>().Object, new Mock<ITranscriptionClientFactory>().Object,
            db, Options.Create(new ContentModerationOptions { Enabled = true }),
            NullLogger<PublishModeration>.Instance, new MemoryCache(new MemoryCacheOptions()));
    }

    public void Dispose()
    {
        foreach (InferenceApiDbContext db in _contexts) db.Dispose();
    }
}

internal static class ReviewAssertions
{
    /// <summary>The result as an <see cref="ObjectResult"/> with this status code.</summary>
    public static ObjectResult Should_BeStatus(this IActionResult result, int status)
    {
        ObjectResult obj = result as ObjectResult ?? throw new Xunit.Sdk.XunitException($"expected an ObjectResult, got {result.GetType().Name}");
        if ((obj.StatusCode ?? 200) != status) throw new Xunit.Sdk.XunitException($"expected {status}, got {obj.StatusCode}: {JsonSerializer.Serialize(obj.Value)}");
        return obj;
    }

    /// <summary>True for the one uniform answer: 404, JSON, body <c>{}</c>.</summary>
    public static bool IsUniformNotFound(this IActionResult result) =>
        result is ContentResult { StatusCode: StatusCodes.Status404NotFound, ContentType: "application/json", Content: "{}" };
}
