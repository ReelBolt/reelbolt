#nullable enable
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class StepResultArtifactsControllerTests
{
    private static InferenceApiDbContext CreateContext()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new InferenceApiDbContext(options);
    }

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public System.Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    private static IConfiguration CreateConfig(string bucketName = "reelforge") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MinIO:BucketName"] = bucketName
            })
            .Build();

    [Fact]
    public async Task GetArtifact_ReturnsNotFound_WhenProjectDoesNotExist()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Mock<IAmazonS3> s3Mock = new();
        StepResultArtifactsController controller = new(db, new FakeCurrentUser(userId), TestTenancy.Access(db, new FakeCurrentUser(userId)), s3Mock.Object, CreateConfig()) { ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };

        IActionResult result = await controller.GetArtifact(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetArtifact_ReturnsNotFound_WhenProjectIsInAnotherOrganization()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        Mock<IAmazonS3> s3Mock = new();
        StepResultArtifactsController controller = new(db, new FakeCurrentUser(otherUserId), TestTenancy.Access(db, new FakeCurrentUser(otherUserId)), s3Mock.Object, CreateConfig()) { ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };

        IActionResult result = await controller.GetArtifact(project.Id, Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetArtifact_ReturnsNotFound_WhenStepResultDoesNotExist()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = userId, OrganizationId = TestTenancy.OrgOf(userId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        Mock<IAmazonS3> s3Mock = new();
        StepResultArtifactsController controller = new(db, new FakeCurrentUser(userId), TestTenancy.Access(db, new FakeCurrentUser(userId)), s3Mock.Object, CreateConfig()) { ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };

        IActionResult result = await controller.GetArtifact(project.Id, Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetArtifact_ReturnsNotFound_WhenStepResultHasNoArtifact()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = userId, OrganizationId = TestTenancy.OrgOf(userId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id };
        WorkflowStepResult stepResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = Guid.NewGuid(),
            ArtifactStorageKey = null,
            ExecutedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(stepResult);
        await db.SaveChangesAsync();

        Mock<IAmazonS3> s3Mock = new();
        StepResultArtifactsController controller = new(db, new FakeCurrentUser(userId), TestTenancy.Access(db, new FakeCurrentUser(userId)), s3Mock.Object, CreateConfig()) { ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };

        IActionResult result = await controller.GetArtifact(project.Id, stepResult.Id, CancellationToken.None);

        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.Value.Should().Be("This step result has no artifact.");
    }

    [Fact]
    public async Task GetArtifact_ReturnsForbid_WhenArtifactStorageKeyHasWrongPrefix()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = userId, OrganizationId = TestTenancy.OrgOf(userId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id };
        WorkflowStepResult stepResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = Guid.NewGuid(),
            ArtifactStorageKey = $"projects/{Guid.NewGuid()}/agentFiles/video-analysis/doc.json",
            ExecutedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(stepResult);
        await db.SaveChangesAsync();

        Mock<IAmazonS3> s3Mock = new();
        StepResultArtifactsController controller = new(db, new FakeCurrentUser(userId), TestTenancy.Access(db, new FakeCurrentUser(userId)), s3Mock.Object, CreateConfig()) { ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };

        IActionResult result = await controller.GetArtifact(project.Id, stepResult.Id, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task GetArtifact_ReturnsFileStream_WhenValid()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = userId, OrganizationId = TestTenancy.OrgOf(userId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id };
        string validKey = $"projects/{project.Id}/agentFiles/video-analysis/analysis.json";
        WorkflowStepResult stepResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = Guid.NewGuid(),
            ArtifactStorageKey = validKey,
            ExecutedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(stepResult);
        await db.SaveChangesAsync();

        Mock<IAmazonS3> s3Mock = new();
        byte[] expectedContent = Encoding.UTF8.GetBytes("{\"result\": \"success\"}");
        s3Mock.Setup(s => s.GetObjectAsync(
            It.Is<GetObjectRequest>(r => r.BucketName == "reelforge" && r.Key == validKey),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse
            {
                ResponseStream = new MemoryStream(expectedContent)
            });

        StepResultArtifactsController controller = new(db, new FakeCurrentUser(userId), TestTenancy.Access(db, new FakeCurrentUser(userId)), s3Mock.Object, CreateConfig()) { ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };

        IActionResult result = await controller.GetArtifact(project.Id, stepResult.Id, CancellationToken.None);

        var fileResult = result.Should().BeOfType<FileStreamResult>().Subject;
        fileResult.ContentType.Should().Be("application/json");
        controller.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
        controller.Response.Headers["Content-Security-Policy"].ToString().Should().Contain("sandbox");
        controller.Response.Headers["Cross-Origin-Resource-Policy"].ToString().Should().Be("same-origin");
        controller.Response.Headers["Content-Disposition"].ToString().Should().StartWith("attachment");
        using var reader = new StreamReader(fileResult.FileStream);
        (await reader.ReadToEndAsync()).Should().Be("{\"result\": \"success\"}");
    }

    // --- GetTimeline Tests ---

    [Fact]
    public async Task GetTimeline_ReturnsNotFound_WhenProjectDoesNotExist()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Mock<IAmazonS3> s3Mock = new();
        StepResultArtifactsController controller = new(db, new FakeCurrentUser(userId), TestTenancy.Access(db, new FakeCurrentUser(userId)), s3Mock.Object, CreateConfig()) { ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };

        IActionResult result = await controller.GetTimeline(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetTimeline_ReturnsNotFound_WhenProjectIsInAnotherOrganization()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        Mock<IAmazonS3> s3Mock = new();
        StepResultArtifactsController controller = new(db, new FakeCurrentUser(otherUserId), TestTenancy.Access(db, new FakeCurrentUser(otherUserId)), s3Mock.Object, CreateConfig()) { ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };

        IActionResult result = await controller.GetTimeline(project.Id, Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetTimeline_ReturnsNotFound_WhenStepResultDoesNotExist()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = userId, OrganizationId = TestTenancy.OrgOf(userId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        Mock<IAmazonS3> s3Mock = new();
        StepResultArtifactsController controller = new(db, new FakeCurrentUser(userId), TestTenancy.Access(db, new FakeCurrentUser(userId)), s3Mock.Object, CreateConfig()) { ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };

        IActionResult result = await controller.GetTimeline(project.Id, Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetTimeline_ReturnsNotFound_WhenNoTimelineGenerated()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = userId, OrganizationId = TestTenancy.OrgOf(userId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id };
        WorkflowStepResult stepResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = Guid.NewGuid(),
            ArtifactStorageKey = null,
            OutputJson = null,
            ExecutedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(stepResult);
        await db.SaveChangesAsync();

        Mock<IAmazonS3> s3Mock = new();
        StepResultArtifactsController controller = new(db, new FakeCurrentUser(userId), TestTenancy.Access(db, new FakeCurrentUser(userId)), s3Mock.Object, CreateConfig()) { ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };

        IActionResult result = await controller.GetTimeline(project.Id, stepResult.Id, CancellationToken.None);

        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.Value.Should().Be("No timeline generated for this step.");
    }

    [Fact]
    public async Task GetTimeline_ReturnsForbid_WhenTimelineStorageKeyHasInvalidPrefix()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = userId, OrganizationId = TestTenancy.OrgOf(userId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id };
        WorkflowStepResult stepResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = Guid.NewGuid(),
            OutputJson = $"{{\"timelineStorageKey\": \"projects/{Guid.NewGuid()}/agentFiles/video-analysis/step-1-timeline.json\"}}",
            ExecutedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(stepResult);
        await db.SaveChangesAsync();

        Mock<IAmazonS3> s3Mock = new();
        StepResultArtifactsController controller = new(db, new FakeCurrentUser(userId), TestTenancy.Access(db, new FakeCurrentUser(userId)), s3Mock.Object, CreateConfig()) { ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };

        IActionResult result = await controller.GetTimeline(project.Id, stepResult.Id, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task GetTimeline_ReturnsForbid_WhenTimelineStorageKeyDoesNotEndWithTimelineJson()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = userId, OrganizationId = TestTenancy.OrgOf(userId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id };
        WorkflowStepResult stepResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = Guid.NewGuid(),
            OutputJson = $"{{\"timelineStorageKey\": \"projects/{project.Id}/agentFiles/video-analysis/step-1.json\"}}",
            ExecutedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(stepResult);
        await db.SaveChangesAsync();

        Mock<IAmazonS3> s3Mock = new();
        StepResultArtifactsController controller = new(db, new FakeCurrentUser(userId), TestTenancy.Access(db, new FakeCurrentUser(userId)), s3Mock.Object, CreateConfig()) { ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };

        IActionResult result = await controller.GetTimeline(project.Id, stepResult.Id, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task GetTimeline_ReturnsNotFound_WhenS3ObjectDoesNotExist()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = userId, OrganizationId = TestTenancy.OrgOf(userId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id };
        string timelineKey = $"projects/{project.Id}/agentFiles/video-analysis/{execution.Id:D}/step-1-timeline.json";
        WorkflowStepResult stepResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = Guid.NewGuid(),
            OutputJson = $"{{\"timelineStorageKey\": \"{timelineKey}\"}}",
            ExecutedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(stepResult);
        await db.SaveChangesAsync();

        Mock<IAmazonS3> s3Mock = new();
        s3Mock.Setup(s => s.GetObjectAsync(
            It.Is<GetObjectRequest>(r => r.Key == timelineKey),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("NoSuchKey") { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchKey" });

        StepResultArtifactsController controller = new(db, new FakeCurrentUser(userId), TestTenancy.Access(db, new FakeCurrentUser(userId)), s3Mock.Object, CreateConfig()) { ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };

        IActionResult result = await controller.GetTimeline(project.Id, stepResult.Id, CancellationToken.None);

        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.Value.Should().Be("Timeline artifact not found in storage.");
    }

    [Fact]
    public async Task GetTimeline_ReturnsFileStream_WhenTimelineStorageKeyInOutputJson()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = userId, OrganizationId = TestTenancy.OrgOf(userId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id };
        string timelineKey = $"projects/{project.Id}/agentFiles/video-analysis/{execution.Id:D}/step-2-timeline.json";
        WorkflowStepResult stepResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = Guid.NewGuid(),
            OutputJson = $"{{\"timelineStorageKey\": \"{timelineKey}\"}}",
            ExecutedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(stepResult);
        await db.SaveChangesAsync();

        Mock<IAmazonS3> s3Mock = new();
        byte[] expectedContent = Encoding.UTF8.GetBytes("{\"tracks\": [], \"version\": 1}");
        s3Mock.Setup(s => s.GetObjectAsync(
            It.Is<GetObjectRequest>(r => r.BucketName == "reelforge" && r.Key == timelineKey),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse
            {
                ResponseStream = new MemoryStream(expectedContent)
            });

        StepResultArtifactsController controller = new(db, new FakeCurrentUser(userId), TestTenancy.Access(db, new FakeCurrentUser(userId)), s3Mock.Object, CreateConfig()) { ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };

        IActionResult result = await controller.GetTimeline(project.Id, stepResult.Id, CancellationToken.None);

        var fileResult = result.Should().BeOfType<FileStreamResult>().Subject;
        fileResult.ContentType.Should().Be("application/json");
        using var reader = new StreamReader(fileResult.FileStream);
        (await reader.ReadToEndAsync()).Should().Be("{\"tracks\": [], \"version\": 1}");
    }

    [Fact]
    public async Task GetTimeline_ReturnsFileStream_WhenArtifactStorageKeyEndsWithTimelineJson()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = userId, OrganizationId = TestTenancy.OrgOf(userId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id };
        string timelineKey = $"projects/{project.Id}/agentFiles/video-analysis/{execution.Id:D}/step-1-timeline.json";
        WorkflowStepResult stepResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = Guid.NewGuid(),
            ArtifactStorageKey = timelineKey,
            OutputJson = null,
            ExecutedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(stepResult);
        await db.SaveChangesAsync();

        Mock<IAmazonS3> s3Mock = new();
        byte[] expectedContent = Encoding.UTF8.GetBytes("{\"from\": \"artifactKey\"}");
        s3Mock.Setup(s => s.GetObjectAsync(
            It.Is<GetObjectRequest>(r => r.Key == timelineKey),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse
            {
                ResponseStream = new MemoryStream(expectedContent)
            });

        StepResultArtifactsController controller = new(db, new FakeCurrentUser(userId), TestTenancy.Access(db, new FakeCurrentUser(userId)), s3Mock.Object, CreateConfig()) { ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };

        IActionResult result = await controller.GetTimeline(project.Id, stepResult.Id, CancellationToken.None);

        var fileResult = result.Should().BeOfType<FileStreamResult>().Subject;
        fileResult.ContentType.Should().Be("application/json");
        using var reader = new StreamReader(fileResult.FileStream);
        (await reader.ReadToEndAsync()).Should().Be("{\"from\": \"artifactKey\"}");
    }

    [Fact]
    public async Task GetTimeline_ReturnsFileStream_WhenDerivedFromWorkflowStep()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = userId, OrganizationId = TestTenancy.OrgOf(userId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowStep step = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoCompile };
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id };
        WorkflowStepResult stepResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = step.Id,
            ArtifactStorageKey = null,
            OutputJson = "{}",
            ExecutedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowSteps.Add(step);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(stepResult);
        await db.SaveChangesAsync();

        string expectedDerivedKey = $"projects/{project.Id}/agentFiles/video-analysis/{execution.Id:D}/step-3-timeline.json";

        Mock<IAmazonS3> s3Mock = new();
        byte[] expectedContent = Encoding.UTF8.GetBytes("{\"derived\": true}");
        s3Mock.Setup(s => s.GetObjectAsync(
            It.Is<GetObjectRequest>(r => r.Key == expectedDerivedKey),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse
            {
                ResponseStream = new MemoryStream(expectedContent)
            });

        StepResultArtifactsController controller = new(db, new FakeCurrentUser(userId), TestTenancy.Access(db, new FakeCurrentUser(userId)), s3Mock.Object, CreateConfig()) { ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };

        IActionResult result = await controller.GetTimeline(project.Id, stepResult.Id, CancellationToken.None);

        var fileResult = result.Should().BeOfType<FileStreamResult>().Subject;
        fileResult.ContentType.Should().Be("application/json");
        using var reader = new StreamReader(fileResult.FileStream);
        (await reader.ReadToEndAsync()).Should().Be("{\"derived\": true}");
    }

    private static async Task<(InferenceApiDbContext Db, Guid UserId, Project Project, WorkflowStepResult StepResult)> SeedSubtitleResultAsync(
        Func<Guid, Guid, string> outputJson, StepType stepType = StepType.VideoCompile)
    {
        InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = userId, OrganizationId = TestTenancy.OrgOf(userId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowStep step = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 2, StepType = stepType };
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id };
        WorkflowStepResult stepResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = step.Id,
            OutputJson = outputJson(project.Id, execution.Id),
            ExecutedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowSteps.Add(step);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(stepResult);
        await db.SaveChangesAsync();
        return (db, userId, project, stepResult);
    }

    private static StepResultArtifactsController SubtitleController(InferenceApiDbContext db, Guid userId, Mock<IAmazonS3> s3) =>
        new(db, new FakeCurrentUser(userId), TestTenancy.Access(db, new FakeCurrentUser(userId)), s3.Object, CreateConfig()) { ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };

    [Theory]
    [InlineData("srt", "application/x-subrip")]
    [InlineData("vtt", "text/vtt")]
    public async Task GetSubtitles_ReturnsTheFileAsADownload_WhenTheOutputNamesIt(string format, string contentType)
    {
        (InferenceApiDbContext db, Guid userId, Project project, WorkflowStepResult stepResult) = await SeedSubtitleResultAsync((p, e) =>
            $"{{\"subtitleStorageKeys\":{{\"srt\":\"projects/{p}/agentFiles/video-analysis/{e:D}/step-2-subtitles.srt\",\"vtt\":\"projects/{p}/agentFiles/video-analysis/{e:D}/step-2-subtitles.vtt\"}}}}");
        using (db)
        {
            Mock<IAmazonS3> s3Mock = new();
            s3Mock.Setup(s => s.GetObjectAsync(
                    It.Is<GetObjectRequest>(r => r.BucketName == "reelforge" && r.Key.EndsWith("-subtitles." + format, StringComparison.Ordinal)),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetObjectResponse { ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes("cue")) });
            StepResultArtifactsController controller = SubtitleController(db, userId, s3Mock);

            IActionResult result = await controller.GetSubtitles(project.Id, stepResult.Id, format, CancellationToken.None);

            FileStreamResult file = result.Should().BeOfType<FileStreamResult>().Subject;
            file.ContentType.Should().Be(contentType);
            controller.Response.Headers["Content-Disposition"].ToString().Should().StartWith("attachment");
        }
    }

    [Fact]
    public async Task GetSubtitles_RefusesAnUnknownFormat()
    {
        (InferenceApiDbContext db, Guid userId, Project project, WorkflowStepResult stepResult) = await SeedSubtitleResultAsync((p, e) => "{}");
        using (db)
        {
            Mock<IAmazonS3> s3Mock = new();
            (await SubtitleController(db, userId, s3Mock).GetSubtitles(project.Id, stepResult.Id, "ass", CancellationToken.None)).Should().BeOfType<NotFoundObjectResult>();
            (await SubtitleController(db, userId, s3Mock).GetSubtitles(project.Id, stepResult.Id, "vtt", CancellationToken.None)).Should().BeOfType<NotFoundObjectResult>("the output names no vtt file");
            s3Mock.Verify(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Theory]
    [InlineData("other-project")]
    [InlineData("dot-segments")]
    [InlineData("backslash")]
    [InlineData("another-step")]
    [InlineData("empty-segment")]
    public async Task GetSubtitles_RefusesAnyKeyThatIsNotThisExecutionsOwnFile(string kind)
    {
        (InferenceApiDbContext db, Guid userId, Project project, WorkflowStepResult stepResult) = await SeedSubtitleResultAsync((p, e) =>
        {
            string key = kind switch
            {
                "other-project" => $"projects/{Guid.NewGuid()}/agentFiles/video-analysis/{e:D}/step-2-subtitles.srt",
                "dot-segments" => $"projects/{p}/agentFiles/video-analysis/../../../{Guid.NewGuid()}/x/step-2-subtitles.srt",
                "backslash" => $"projects/{p}/agentFiles/video-analysis/{e:D}\\step-2-subtitles.srt",
                "another-step" => $"projects/{p}/agentFiles/video-analysis/{Guid.NewGuid():D}/step-2-subtitles.srt",
                _ => $"projects/{p}/agentFiles/video-analysis//{e:D}/step-2-subtitles.srt"
            };
            return JsonSerializer.Serialize(new { subtitleStorageKeys = new { srt = key } });
        });
        using (db)
        {
            Mock<IAmazonS3> s3Mock = new();
            (await SubtitleController(db, userId, s3Mock).GetSubtitles(project.Id, stepResult.Id, "srt", CancellationToken.None)).Should().BeOfType<ForbidResult>();
            s3Mock.Verify(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task GetSubtitles_IgnoresAKeyOnAStepThatIsNotACompile()
    {
        (InferenceApiDbContext db, Guid userId, Project project, WorkflowStepResult stepResult) = await SeedSubtitleResultAsync((p, e) =>
            $"{{\"subtitleStorageKeys\":{{\"srt\":\"projects/{p}/agentFiles/video-analysis/{e:D}/step-2-subtitles.srt\"}}}}", StepType.Agent);
        using (db)
        {
            Mock<IAmazonS3> s3Mock = new();
            (await SubtitleController(db, userId, s3Mock).GetSubtitles(project.Id, stepResult.Id, "srt", CancellationToken.None)).Should().BeOfType<NotFoundObjectResult>();
            s3Mock.Verify(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
