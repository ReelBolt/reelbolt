#nullable enable
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The render list's appended context (workflow, run number, final vs draft, length, size and the
/// run's C1 warnings) — what lets the Renders tab say which video is the finished one instead of
/// listing bare GUID file names.
/// </summary>
public class OutputsControllerTests
{
    private static InferenceApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public System.Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    private static OutputsController CreateController(InferenceApiDbContext db, Guid userId, IAmazonS3? s3 = null) =>
        new(db, new FakeCurrentUser(userId), TestTenancy.Access(db, new FakeCurrentUser(userId)), s3 ?? new Mock<IAmazonS3>().Object,
            Mock.Of<ReelBolt.Inference.Api.Services.Storage.IFileStorageService>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" }).Build());

    [Fact]
    public async Task ListOutputs_ReturnsRunContext_FinalVersusDraft_AndRunWarnings()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        DateTime t0 = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = t0, UpdatedAt = t0 };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "60s product story", CreatedAt = t0, UpdatedAt = t0 };
        WorkflowStep voiceStep = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 2, StepType = StepType.Voiceover, Label = "Narration" };
        WorkflowStep compileStep = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoCompile, Label = "Compile" };
        WorkflowExecution earlier = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id, Status = ExecutionStatus.Failed, StartedAt = t0 };
        WorkflowExecution later = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id, Status = ExecutionStatus.Passed, StartedAt = t0.AddHours(1), CompletedAt = t0.AddHours(2) };

        string Key(Guid exec, string name) => $"projects/{project.Id}/outputFiles/{exec:D}/{name}";
        WorkflowStepResult draft = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = later.Id,
            WorkflowStepId = compileStep.Id,
            Status = StepStatus.Completed,
            OutputStorageKey = Key(later.Id, "a.mp4"),
            IterationNumber = 1,
            ExecutedAt = t0.AddHours(1.2),
            CompletedAt = t0.AddHours(1.3),
            OutputJson = """{"status":"completed","outputDurationSec":58.5}""",
        };
        WorkflowStepResult final = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = later.Id,
            WorkflowStepId = compileStep.Id,
            Status = StepStatus.Completed,
            OutputStorageKey = Key(later.Id, "b.mp4"),
            IterationNumber = 2,
            ExecutedAt = t0.AddHours(1.5),
            CompletedAt = t0.AddHours(1.6),
            OutputJson = """{"status":"completed","outputDurationSec":61.25,"output":{"width":1080,"height":1920},"warnings":[{"code":"narration_dropped","message":"2 narration lines could not be placed."}]}""",
        };
        WorkflowStepResult voiceover = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = later.Id,
            WorkflowStepId = voiceStep.Id,
            Status = StepStatus.Completed,
            ExecutedAt = t0.AddHours(1.1),
            OutputJson = """{"warnings":[{"code":"voice_provider_fallback","message":"A backup voice was used."},{"code":"narration_dropped","message":"2 narration lines could not be placed."}]}""",
        };
        WorkflowStepResult failedRunRender = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = earlier.Id,
            WorkflowStepId = compileStep.Id,
            Status = StepStatus.Completed,
            OutputStorageKey = Key(earlier.Id, "c.mp4"),
            ExecutedAt = t0.AddMinutes(10),
            CompletedAt = t0.AddMinutes(20),
            OutputJson = "not json at all",
        };

        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowSteps.AddRange(voiceStep, compileStep);
        db.WorkflowExecutions.AddRange(earlier, later);
        db.WorkflowStepResults.AddRange(draft, final, voiceover, failedRunRender);
        await db.SaveChangesAsync();

        ActionResult<List<OutputVideoResponse>> result = await CreateController(db, ownerId).ListOutputs(project.Id, CancellationToken.None);

        List<OutputVideoResponse> outputs = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<List<OutputVideoResponse>>().Subject;
        // Original fields and order (most recent first) unchanged; the voiceover result has no render.
        outputs.Select(o => o.StepResultId).Should().Equal(final.Id, draft.Id, failedRunRender.Id);
        outputs[0].FileName.Should().Be("b.mp4");

        OutputVideoResponse f = outputs[0];
        f.WorkflowName.Should().Be("60s product story");
        f.WorkflowDefinitionId.Should().Be(workflow.Id);
        f.RunNumber.Should().Be(2);
        f.RunStartedAt.Should().Be(later.StartedAt);
        f.ExecutionStatus.Should().Be("Passed");
        f.StepLabel.Should().Be("Compile");
        f.StepType.Should().Be("VideoCompile");
        f.IterationNumber.Should().Be(2);
        f.DurationSec.Should().Be(61.25);
        f.Width.Should().Be(1080);
        f.Height.Should().Be(1920);
        f.IsFinal.Should().BeTrue();
        // Every warning of the run, from every step, deduplicated.
        f.Warnings!.Select(w => w.Code).Should().BeEquivalentTo(new[] { "narration_dropped", "voice_provider_fallback" });

        OutputVideoResponse d = outputs[1];
        d.IsFinal.Should().BeFalse("an earlier attempt of a passed run is a draft");
        d.DurationSec.Should().Be(58.5);
        d.Width.Should().BeNull();
        d.Warnings.Should().HaveCount(2, "warnings belong to the run, not only the step that raised them");

        OutputVideoResponse failed = outputs[2];
        failed.RunNumber.Should().Be(1);
        failed.IsFinal.Should().BeFalse("a run that did not pass has no finished video");
        failed.DurationSec.Should().BeNull("malformed OutputJson degrades to unknown, never an error");
        failed.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ListOutputs_StillListsARender_WhoseStepAndWorkflowAreGone()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = Guid.NewGuid(), ProjectId = project.Id, Status = ExecutionStatus.Passed };
        WorkflowStepResult render = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = Guid.NewGuid(),
            Status = StepStatus.Completed,
            OutputStorageKey = $"projects/{project.Id}/outputFiles/x/edited.mp4",
            ExecutedAt = DateTime.UtcNow,
        };
        db.Projects.Add(project);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(render);
        await db.SaveChangesAsync();

        ActionResult<List<OutputVideoResponse>> result = await CreateController(db, ownerId).ListOutputs(project.Id, CancellationToken.None);

        OutputVideoResponse only = ((List<OutputVideoResponse>)((OkObjectResult)result.Result!).Value!).Single();
        only.FileName.Should().Be("edited.mp4");
        only.WorkflowName.Should().BeNull();
        only.StepLabel.Should().BeNull();
        only.IsFinal.Should().BeTrue();
    }

    // QA-P0-22: a generated clip under agentFiles/generated/ is an addressable output exactly like a render
    // (ProjectOutputKeys; GetOutput and the output links already accept it), so the REST outputs list and
    // download must accept it too, under the same project and nothing else.

    private static (Project Project, WorkflowStepResult Clip, Guid OwnerId) SeedGeneratedClip(InferenceApiDbContext db, Guid? ownerId = null)
    {
        Guid owner = ownerId ?? Guid.NewGuid();
        DateTime t0 = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = t0, UpdatedAt = t0 };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "Film", CreatedAt = t0, UpdatedAt = t0 };
        WorkflowStep generate = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoGenerate, Label = "Shots" };
        WorkflowExecution run = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id, Status = ExecutionStatus.Passed, StartedAt = t0, CompletedAt = t0 };
        WorkflowStepResult clip = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = generate.Id,
            Status = StepStatus.Completed,
            OutputStorageKey = $"projects/{project.Id}/agentFiles/generated/{run.Id}/step-1-v1.mp4",
            ExecutedAt = t0,
            CompletedAt = t0
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowSteps.Add(generate);
        db.WorkflowExecutions.Add(run);
        db.WorkflowStepResults.Add(clip);
        db.SaveChanges();
        return (project, clip, owner);
    }

    [Fact]
    public async Task ListOutputs_includes_a_generated_clip_under_agentFiles_generated()
    {
        using InferenceApiDbContext db = CreateContext();
        (Project project, WorkflowStepResult clip, Guid ownerId) = SeedGeneratedClip(db);

        ActionResult<List<OutputVideoResponse>> result = await CreateController(db, ownerId).ListOutputs(project.Id, CancellationToken.None);

        List<OutputVideoResponse> outputs = (List<OutputVideoResponse>)((OkObjectResult)result.Result!).Value!;
        outputs.Should().ContainSingle().Which.StepResultId.Should().Be(clip.Id);
        outputs[0].FileName.Should().Be("step-1-v1.mp4");
        outputs[0].StepType.Should().Be("VideoGenerate");
    }

    [Fact]
    public async Task ListOutputs_never_lists_a_generated_clip_of_another_project()
    {
        using InferenceApiDbContext db = CreateContext();
        (_, _, Guid ownerId) = SeedGeneratedClip(db);
        Project other = new() { Id = Guid.NewGuid(), Name = "Other", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(other);
        WorkflowExecution run = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = Guid.NewGuid(), ProjectId = other.Id, Status = ExecutionStatus.Passed };
        db.WorkflowExecutions.Add(run);
        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = Guid.NewGuid(),
            Status = StepStatus.Completed,
            OutputStorageKey = $"projects/{Guid.NewGuid()}/agentFiles/generated/{run.Id}/step-1-v1.mp4",
            ExecutedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        ActionResult<List<OutputVideoResponse>> result = await CreateController(db, ownerId).ListOutputs(other.Id, CancellationToken.None);

        ((List<OutputVideoResponse>)((OkObjectResult)result.Result!).Value!).Should().BeEmpty(
            "the generated prefix is this project's own, like the outputFiles one");
    }

    [Fact]
    public async Task DownloadOutput_streams_a_generated_clip_instead_of_forbidding_it()
    {
        using InferenceApiDbContext db = CreateContext();
        (Project project, WorkflowStepResult clip, Guid ownerId) = SeedGeneratedClip(db);
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(c => c.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = 10 });
        s3.Setup(c => c.GetObjectAsync(It.IsAny<Amazon.S3.Model.GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse { ResponseStream = new MemoryStream(new byte[10]) });
        OutputsController controller = CreateController(db, ownerId, s3.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        IActionResult result = await controller.DownloadOutput(project.Id, clip.Id, CancellationToken.None);

        result.Should().BeOfType<FileStreamResult>("a generated clip is streamed, not forbidden");
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".ts")]
    [InlineData(".mpg")]
    public async Task DownloadOutput_refuses_a_file_that_can_never_be_checked_with_final_true_and_plain_words(string extension)
    {
        using InferenceApiDbContext db = CreateContext();
        (Project project, WorkflowStepResult clip, Guid ownerId) = SeedGeneratedClip(db);
        clip.OutputStorageKey = clip.OutputStorageKey!.Replace(".mp4", extension, StringComparison.Ordinal);
        db.OutputModerationVerdicts.Add(new ReelBolt.Inference.Api.Services.Moderation.OutputModerationVerdict
        {
            Id = Guid.NewGuid(),
            OrganizationId = project.OrganizationId,
            ProjectId = project.Id,
            StorageKey = clip.OutputStorageKey,
            SourceVersion = "e1:10",
            State = nameof(ReelBolt.Shared.Moderation.ModerationAction.Unavailable),
            ReasonCode = ReelBolt.Shared.Moderation.ModerationReasonCodes.Unclassified,
            ModelSnapshot = "m",
            NextAttemptAt = ReelBolt.Inference.Api.Services.Moderation.OutputModerationBackoff.Never
        });
        db.SaveChanges();
        Mock<ReelBolt.Inference.Api.Services.Storage.IFileStorageService> storage = new();
        storage.Setup(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReelBolt.Inference.Api.Services.Storage.StoredObjectMetadata(10, "video/mp4", "\"e1\""));
        ReelBolt.Inference.Api.Services.Moderation.PublishModeration moderation = new(
            new Mock<ReelBolt.Shared.Moderation.IContentModerationGate>().Object, storage.Object,
            new Mock<ReelBolt.Shared.Moderation.IModerationMediaSampler>().Object,
            new Mock<ReelBolt.Shared.Inference.IInferenceProviderResolver>().Object,
            new Mock<ReelBolt.Shared.Inference.ITranscriptionClientFactory>().Object, db,
            Microsoft.Extensions.Options.Options.Create(new ReelBolt.Shared.Moderation.ContentModerationOptions { Enabled = true }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReelBolt.Inference.Api.Services.Moderation.PublishModeration>.Instance);
        FakeCurrentUser user = new(ownerId);
        OutputsController controller = new(db, user, TestTenancy.Access(db, user), new Mock<IAmazonS3>().Object,
            Mock.Of<ReelBolt.Inference.Api.Services.Storage.IFileStorageService>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" }).Build(), moderation);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        IActionResult result = await controller.DownloadOutput(project.Id, clip.Id, CancellationToken.None);

        ObjectResult refusal = result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(409);
        string json = System.Text.Json.JsonSerializer.Serialize(refusal.Value);
        json.Should().Contain("\"final\":true");
        json.Should().Contain("Re-export it in a common format");
    }

    [Fact]
    public async Task DownloadOutput_is_an_attachment_under_the_chosen_name_only_when_asked_to_save()
    {
        using InferenceApiDbContext db = CreateContext();
        (Project project, WorkflowStepResult clip, Guid ownerId) = SeedGeneratedClip(db);
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(c => c.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = 10 });
        s3.Setup(c => c.GetObjectAsync(It.IsAny<Amazon.S3.Model.GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GetObjectResponse { ResponseStream = new MemoryStream(new byte[10]) });
        OutputsController controller = CreateController(db, ownerId, s3.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        await controller.DownloadOutput(project.Id, clip.Id, CancellationToken.None);
        controller.Response.Headers.ContentDisposition.ToString().Should().StartWith("inline;", "playback streams inline");

        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        await controller.DownloadOutput(project.Id, clip.Id, CancellationToken.None, "my \"reel\"/final");
        string disposition = controller.Response.Headers.ContentDisposition.ToString();
        disposition.Should().StartWith("attachment;", "a save request must not navigate to a bare player");
        disposition.Should().Contain("my reelfinal.mp4", "the chosen name keeps the stored extension and loses header-injection characters");
    }

    [Fact]
    public async Task DownloadOutput_forbids_a_generated_clip_of_another_project()
    {
        using InferenceApiDbContext db = CreateContext();
        (_, _, Guid ownerId) = SeedGeneratedClip(db);
        Project other = new() { Id = Guid.NewGuid(), Name = "Other", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(other);
        WorkflowExecution run = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = Guid.NewGuid(), ProjectId = other.Id, Status = ExecutionStatus.Passed };
        var foreign = new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = Guid.NewGuid(),
            Status = StepStatus.Completed,
            OutputStorageKey = $"projects/{Guid.NewGuid()}/agentFiles/generated/{run.Id}/step-1-v1.mp4",
            ExecutedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow
        };
        db.WorkflowExecutions.Add(run);
        db.WorkflowStepResults.Add(foreign);
        db.SaveChanges();

        OutputsController controller = CreateController(db, ownerId);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        IActionResult result = await controller.DownloadOutput(other.Id, foreign.Id, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }
}
