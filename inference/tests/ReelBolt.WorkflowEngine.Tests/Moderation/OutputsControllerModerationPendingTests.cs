#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Moderation;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// The output view (the route the player probes with a ranged request): a verdict that is still pending is a
/// bounded poll (202 + Retry-After), never an error; blocked and unavailable keep their fail-closed answers.
/// </summary>
public sealed class OutputsControllerModerationPendingTests
{
    private const string Version = "\"e1\"";

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    private static InferenceApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static (Project Project, WorkflowStepResult Render, Guid OwnerId) SeedRender(InferenceApiDbContext db)
    {
        Guid owner = Guid.NewGuid();
        DateTime t0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = "Proj",
            OwnerId = owner,
            OrganizationId = TestTenancy.OrgOf(owner),
            CreatedAt = t0,
            UpdatedAt = t0
        };
        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            ProjectId = project.Id,
            Status = ExecutionStatus.Passed,
            StartedAt = t0,
            CompletedAt = t0
        };
        WorkflowStepResult render = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = Guid.NewGuid(),
            Status = StepStatus.Completed,
            OutputStorageKey = $"projects/{project.Id}/outputFiles/{run.Id}/film.mp4",
            ExecutedAt = t0,
            CompletedAt = t0
        };
        db.Projects.Add(project);
        db.WorkflowExecutions.Add(run);
        db.WorkflowStepResults.Add(render);
        db.SaveChanges();
        return (project, render, owner);
    }

    private static OutputsController Controller(
        InferenceApiDbContext db, Guid ownerId, PublishModeration moderation, IConfiguration? configuration = null)
    {
        FakeCurrentUser user = new(ownerId);
        IConfiguration config = configuration ?? new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" })
            .Build();
        OutputsController controller = new(
            db, user, TestTenancy.Access(db, user), new Mock<IAmazonS3>().Object, Mock.Of<IFileStorageService>(),
            config, moderation);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static PublishModeration Moderation(InferenceApiDbContext db)
    {
        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectMetadata(10, "video/mp4", Version));
        return new PublishModeration(
            new Mock<IContentModerationGate>().Object, storage.Object,
            new Mock<IModerationMediaSampler>().Object,
            new Mock<IInferenceProviderResolver>().Object,
            new Mock<ITranscriptionClientFactory>().Object, db,
            Options.Create(new ContentModerationOptions { Enabled = true }),
            NullLogger<PublishModeration>.Instance);
    }

    [Fact]
    public async Task A_pending_output_is_202_with_the_default_short_retry_after()
    {
        using InferenceApiDbContext db = CreateContext();
        (Project project, WorkflowStepResult render, Guid owner) = SeedRender(db);
        OutputsController controller = Controller(db, owner, Moderation(db));

        IActionResult result = await controller.DownloadOutput(project.Id, render.Id, CancellationToken.None);

        ObjectResult accepted = result.Should().BeOfType<ObjectResult>().Subject;
        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted, "the video is not ready yet; the caller polls");
        controller.Response.Headers["Retry-After"].ToString().Should().Be("5");
        JsonSerializer.Serialize(accepted.Value).Should().Contain("CONTENT_MODERATION_PENDING");
    }

    [Fact]
    public async Task The_pending_retry_after_is_configurable()
    {
        using InferenceApiDbContext db = CreateContext();
        (Project project, WorkflowStepResult render, Guid owner) = SeedRender(db);
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MinIO:BucketName"] = "reelforge",
                ["ContentModeration:PendingRetryAfterSeconds"] = "7"
            })
            .Build();
        OutputsController controller = Controller(db, owner, Moderation(db), config);

        IActionResult result = await controller.DownloadOutput(project.Id, render.Id, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        controller.Response.Headers["Retry-After"].ToString().Should().Be("7");
    }

    [Fact]
    public async Task A_blocked_output_is_still_422_and_a_final_unavailable_still_409()
    {
        using InferenceApiDbContext db = CreateContext();
        (Project project, WorkflowStepResult blocked, Guid owner) = SeedRender(db);
        db.OutputModerationVerdicts.Add(new OutputModerationVerdict
        {
            Id = Guid.NewGuid(),
            OrganizationId = project.OrganizationId,
            ProjectId = project.Id,
            StorageKey = blocked.OutputStorageKey,
            SourceVersion = "e1:10",
            State = nameof(ModerationAction.Block),
            ReasonMessage = "This video may contain violent content.",
            ModelSnapshot = "m"
        });
        WorkflowStepResult unavailable = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = blocked.WorkflowExecutionId,
            WorkflowStepId = Guid.NewGuid(),
            Status = StepStatus.Completed,
            OutputStorageKey = $"projects/{project.Id}/outputFiles/x/other.mp4",
            ExecutedAt = blocked.ExecutedAt,
            CompletedAt = blocked.CompletedAt
        };
        db.WorkflowStepResults.Add(unavailable);
        db.OutputModerationVerdicts.Add(new OutputModerationVerdict
        {
            Id = Guid.NewGuid(),
            OrganizationId = project.OrganizationId,
            ProjectId = project.Id,
            StorageKey = unavailable.OutputStorageKey,
            SourceVersion = "e1:10",
            State = nameof(ModerationAction.Unavailable),
            ReasonCode = ModerationReasonCodes.Unclassified,
            ModelSnapshot = "m",
            NextAttemptAt = OutputModerationBackoff.Never
        });
        db.SaveChanges();
        OutputsController controller = Controller(db, owner, Moderation(db));

        IActionResult blockedResult = await controller.DownloadOutput(project.Id, blocked.Id, CancellationToken.None);
        ObjectResult blockedRefusal = blockedResult.Should().BeOfType<ObjectResult>().Subject;
        blockedRefusal.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);

        IActionResult unavailableResult = await controller.DownloadOutput(project.Id, unavailable.Id, CancellationToken.None);
        unavailableResult.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        controller.Response.Headers["Retry-After"].ToString().Should().BeEmpty("only a pending answer asks for a retry");
    }
}
