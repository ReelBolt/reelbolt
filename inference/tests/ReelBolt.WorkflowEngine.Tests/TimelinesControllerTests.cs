#nullable enable
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class TimelinesControllerTests
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
        public System.Guid? OrganizationId => ReelBolt.WorkflowEngine.Tests.TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    [Fact]
    public async Task ListTimelines_ReturnsNotFound_WhenProjectDoesNotExist()
    {
        using InferenceApiDbContext db = CreateContext();
        TimelinesController controller = new(db, new FakeCurrentUser(Guid.NewGuid()), TestTenancy.Access(db, new FakeCurrentUser(Guid.NewGuid())));

        ActionResult<List<ProjectTimelineResponse>> result = await controller.ListTimelines(Guid.NewGuid(), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ListTimelines_ReturnsNotFound_WhenProjectIsInAnotherOrganization()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        TimelinesController controller = new(db, new FakeCurrentUser(otherUserId), TestTenancy.Access(db, new FakeCurrentUser(otherUserId)));

        ActionResult<List<ProjectTimelineResponse>> result = await controller.ListTimelines(project.Id, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ListTimelines_ReturnsEmptyList_WhenNoTimelinesExistForProject()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        TimelinesController controller = new(db, new FakeCurrentUser(ownerId), TestTenancy.Access(db, new FakeCurrentUser(ownerId)));

        ActionResult<List<ProjectTimelineResponse>> result = await controller.ListTimelines(project.Id, CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        List<ProjectTimelineResponse> timelines = ok.Value.Should().BeOfType<List<ProjectTimelineResponse>>().Subject;
        timelines.Should().BeEmpty();
    }

    [Fact]
    public async Task ListTimelines_ReturnsTimeline_WhenOutputJsonContainsTimelineStorageKey()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id };
        WorkflowStep step = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 1 };
        string timelineKey = $"projects/{project.Id}/agentFiles/video-analysis/{execution.Id:D}/step-1-timeline.json";
        WorkflowStepResult stepResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = step.Id,
            OutputJson = $"{{\"timelineStorageKey\": \"{timelineKey}\", \"outputDurationSec\": 45.5}}",
            TimelineStorageKey = timelineKey,
            ExecutedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow.AddMinutes(-4)
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowSteps.Add(step);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(stepResult);
        await db.SaveChangesAsync();

        TimelinesController controller = new(db, new FakeCurrentUser(ownerId), TestTenancy.Access(db, new FakeCurrentUser(ownerId)));

        ActionResult<List<ProjectTimelineResponse>> result = await controller.ListTimelines(project.Id, CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        List<ProjectTimelineResponse> timelines = ok.Value.Should().BeOfType<List<ProjectTimelineResponse>>().Subject;
        timelines.Should().HaveCount(1);
        ProjectTimelineResponse item = timelines.Single();
        item.StepResultId.Should().Be(stepResult.Id);
        item.WorkflowExecutionId.Should().Be(execution.Id);
        item.WorkflowStepId.Should().Be(step.Id);
        item.TimelineStorageKey.Should().Be(timelineKey);
        item.OutputVideoStorageKey.Should().BeNull();
        item.FileName.Should().Be("step-1-timeline.json");
        item.DurationSec.Should().Be(45.5);
        item.CreatedAt.Should().Be(stepResult.CompletedAt!.Value);
    }

    [Fact]
    public async Task ListTimelines_ReturnsTimeline_WhenOutputStorageKeyMatchesPrefix()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id };
        WorkflowStep step = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 2 };
        string outputVideoKey = $"projects/{project.Id}/outputFiles/{execution.Id:D}/final-render.mp4";
        string timelineKey = $"projects/{project.Id}/agentFiles/video-analysis/{execution.Id:D}/step-2-timeline.json";
        WorkflowStepResult stepResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = step.Id,
            OutputStorageKey = outputVideoKey,
            OutputJson = $"{{\"timelineStorageKey\": \"{timelineKey}\", \"outputDurationSec\": 12.0}}",
            TimelineStorageKey = timelineKey,
            ExecutedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowSteps.Add(step);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(stepResult);
        await db.SaveChangesAsync();

        TimelinesController controller = new(db, new FakeCurrentUser(ownerId), TestTenancy.Access(db, new FakeCurrentUser(ownerId)));

        ActionResult<List<ProjectTimelineResponse>> result = await controller.ListTimelines(project.Id, CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        List<ProjectTimelineResponse> timelines = ok.Value.Should().BeOfType<List<ProjectTimelineResponse>>().Subject;
        timelines.Should().HaveCount(1);
        ProjectTimelineResponse item = timelines.Single();
        item.StepResultId.Should().Be(stepResult.Id);
        item.TimelineStorageKey.Should().Be(timelineKey);
        item.OutputVideoStorageKey.Should().Be(outputVideoKey);
        item.FileName.Should().Be("final-render.mp4");
        item.DurationSec.Should().Be(12.0);
    }

    [Fact]
    public async Task ListTimelines_DerivesTimelineStorageKey_WhenOutputStorageKeyPresentAndWorkflowStepPresent()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id };
        WorkflowStep step = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoCompile };
        string outputVideoKey = $"projects/{project.Id}/outputFiles/{execution.Id:D}/video-compile.mp4";
        string derivedTimelineKey = $"projects/{project.Id}/agentFiles/video-analysis/{execution.Id:D}/step-3-timeline.json";
        WorkflowStepResult stepResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = step.Id,
            OutputStorageKey = outputVideoKey,
            OutputJson = null,
            TimelineStorageKey = derivedTimelineKey,
            ExecutedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowSteps.Add(step);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(stepResult);
        await db.SaveChangesAsync();

        TimelinesController controller = new(db, new FakeCurrentUser(ownerId), TestTenancy.Access(db, new FakeCurrentUser(ownerId)));

        ActionResult<List<ProjectTimelineResponse>> result = await controller.ListTimelines(project.Id, CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        List<ProjectTimelineResponse> timelines = ok.Value.Should().BeOfType<List<ProjectTimelineResponse>>().Subject;
        timelines.Should().HaveCount(1);
        ProjectTimelineResponse item = timelines.Single();
        item.TimelineStorageKey.Should().Be(derivedTimelineKey);
        item.OutputVideoStorageKey.Should().Be(outputVideoKey);
        item.FileName.Should().Be("video-compile.mp4");
        item.DurationSec.Should().BeNull();
    }

    [Fact]
    public async Task ListTimelines_ExcludesStepResultsFromOtherProjects()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project1 = new() { Id = Guid.NewGuid(), Name = "Proj1", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        Project project2 = new() { Id = Guid.NewGuid(), Name = "Proj2", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow1 = new() { Id = Guid.NewGuid(), ProjectId = project1.Id, Name = "wf1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow2 = new() { Id = Guid.NewGuid(), ProjectId = project2.Id, Name = "wf2", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution execution1 = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow1.Id, ProjectId = project1.Id };
        WorkflowExecution execution2 = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow2.Id, ProjectId = project2.Id };

        string timelineKey1 = $"projects/{project1.Id}/agentFiles/video-analysis/step-1-timeline.json";
        string timelineKey2 = $"projects/{project2.Id}/agentFiles/video-analysis/step-1-timeline.json";

        WorkflowStepResult stepResult1 = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution1.Id,
            WorkflowStepId = Guid.NewGuid(),
            OutputJson = $"{{\"timelineStorageKey\": \"{timelineKey1}\"}}",
            TimelineStorageKey = timelineKey1,
            ExecutedAt = DateTime.UtcNow
        };
        WorkflowStepResult stepResult2 = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution2.Id,
            WorkflowStepId = Guid.NewGuid(),
            OutputJson = $"{{\"timelineStorageKey\": \"{timelineKey2}\"}}",
            TimelineStorageKey = timelineKey2,
            ExecutedAt = DateTime.UtcNow
        };

        db.Projects.AddRange(project1, project2);
        db.WorkflowDefinitions.AddRange(workflow1, workflow2);
        db.WorkflowExecutions.AddRange(execution1, execution2);
        db.WorkflowStepResults.AddRange(stepResult1, stepResult2);
        await db.SaveChangesAsync();

        TimelinesController controller = new(db, new FakeCurrentUser(ownerId), TestTenancy.Access(db, new FakeCurrentUser(ownerId)));

        ActionResult<List<ProjectTimelineResponse>> result = await controller.ListTimelines(project1.Id, CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        List<ProjectTimelineResponse> timelines = ok.Value.Should().BeOfType<List<ProjectTimelineResponse>>().Subject;
        timelines.Should().HaveCount(1);
        timelines.Single().StepResultId.Should().Be(stepResult1.Id);
    }

    [Fact]
    public async Task ListTimelines_ExcludesStepResultsWithoutTimelineOrOutputVideo()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id };

        WorkflowStepResult stepResultWithoutTimeline = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = Guid.NewGuid(),
            OutputJson = "{\"status\": \"completed\", \"summary\": \"done\"}",
            OutputStorageKey = null,
            ArtifactStorageKey = null,
            ExecutedAt = DateTime.UtcNow
        };

        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(stepResultWithoutTimeline);
        await db.SaveChangesAsync();

        TimelinesController controller = new(db, new FakeCurrentUser(ownerId), TestTenancy.Access(db, new FakeCurrentUser(ownerId)));

        ActionResult<List<ProjectTimelineResponse>> result = await controller.ListTimelines(project.Id, CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        List<ProjectTimelineResponse> timelines = ok.Value.Should().BeOfType<List<ProjectTimelineResponse>>().Subject;
        timelines.Should().BeEmpty();
    }

    [Fact]
    public async Task ListTimelines_OrdersByMostRecentFirst_CompletedAtOrExecutedAt()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id };

        DateTime now = DateTime.UtcNow;
        string oldTimelineKey = $"projects/{project.Id}/agentFiles/video-analysis/old-timeline.json";
        string newTimelineKey = $"projects/{project.Id}/agentFiles/video-analysis/new-timeline.json";

        WorkflowStepResult olderResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = Guid.NewGuid(),
            OutputJson = $"{{\"timelineStorageKey\": \"{oldTimelineKey}\"}}",
            TimelineStorageKey = oldTimelineKey,
            ExecutedAt = now.AddMinutes(-30),
            CompletedAt = now.AddMinutes(-25)
        };
        WorkflowStepResult newerResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = Guid.NewGuid(),
            OutputJson = $"{{\"timelineStorageKey\": \"{newTimelineKey}\"}}",
            TimelineStorageKey = newTimelineKey,
            ExecutedAt = now.AddMinutes(-10),
            CompletedAt = now.AddMinutes(-5)
        };

        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.AddRange(olderResult, newerResult);
        await db.SaveChangesAsync();

        TimelinesController controller = new(db, new FakeCurrentUser(ownerId), TestTenancy.Access(db, new FakeCurrentUser(ownerId)));

        ActionResult<List<ProjectTimelineResponse>> result = await controller.ListTimelines(project.Id, CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        List<ProjectTimelineResponse> timelines = ok.Value.Should().BeOfType<List<ProjectTimelineResponse>>().Subject;
        timelines.Should().HaveCount(2);
        timelines[0].StepResultId.Should().Be(newerResult.Id);
        timelines[1].StepResultId.Should().Be(olderResult.Id);
    }

    [Fact]
    public async Task ListTimelines_HandlesJsonNumberAsTimelineStorageKey_Returns200WithDefaultedFields()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id };
        WorkflowStep step = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 1 };
        string timelineKey = $"projects/{project.Id}/agentFiles/video-analysis/{execution.Id:D}/step-1-timeline.json";
        WorkflowStepResult stepResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = step.Id,
            OutputJson = "{\"timelineStorageKey\": 12345}",  // JSON number instead of string
            TimelineStorageKey = timelineKey,  // DB column has the correct key
            ExecutedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow.AddMinutes(-4)
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowSteps.Add(step);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(stepResult);
        await db.SaveChangesAsync();

        TimelinesController controller = new(db, new FakeCurrentUser(ownerId), TestTenancy.Access(db, new FakeCurrentUser(ownerId)));

        ActionResult<List<ProjectTimelineResponse>> result = await controller.ListTimelines(project.Id, CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        List<ProjectTimelineResponse> timelines = ok.Value.Should().BeOfType<List<ProjectTimelineResponse>>().Subject;
        timelines.Should().HaveCount(1);
        ProjectTimelineResponse item = timelines.Single();
        item.StepResultId.Should().Be(stepResult.Id);
        item.TimelineStorageKey.Should().Be(timelineKey);  // From TimelineStorageKey column
        item.DurationSec.Should().BeNull();  // Defaulted
    }

    [Fact]
    public async Task ListTimelines_HandlesInvalidJson_Returns200WithDefaultedFields()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id };
        WorkflowStep step = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 1 };
        string timelineKey = $"projects/{project.Id}/agentFiles/video-analysis/{execution.Id:D}/step-1-timeline.json";
        WorkflowStepResult stepResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = step.Id,
            OutputJson = "{invalid json",  // Malformed JSON
            TimelineStorageKey = timelineKey,  // DB column has the correct key
            ExecutedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow.AddMinutes(-4)
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowSteps.Add(step);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(stepResult);
        await db.SaveChangesAsync();

        TimelinesController controller = new(db, new FakeCurrentUser(ownerId), TestTenancy.Access(db, new FakeCurrentUser(ownerId)));

        ActionResult<List<ProjectTimelineResponse>> result = await controller.ListTimelines(project.Id, CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        List<ProjectTimelineResponse> timelines = ok.Value.Should().BeOfType<List<ProjectTimelineResponse>>().Subject;
        timelines.Should().HaveCount(1);
        ProjectTimelineResponse item = timelines.Single();
        item.StepResultId.Should().Be(stepResult.Id);
        item.TimelineStorageKey.Should().Be(timelineKey);  // From TimelineStorageKey column
        item.DurationSec.Should().BeNull();  // Defaulted
    }

    [Fact]
    public async Task ListTimelines_ExcludesRowsWithNullTimelineStorageKey()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Proj", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id };
        WorkflowStep step = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 1 };
        WorkflowStepResult stepResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = step.Id,
            OutputJson = "{\"someField\": \"value\"}",
            TimelineStorageKey = null,  // Null timelineStorageKey should be excluded
            ExecutedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowSteps.Add(step);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(stepResult);
        await db.SaveChangesAsync();

        TimelinesController controller = new(db, new FakeCurrentUser(ownerId), TestTenancy.Access(db, new FakeCurrentUser(ownerId)));

        ActionResult<List<ProjectTimelineResponse>> result = await controller.ListTimelines(project.Id, CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        List<ProjectTimelineResponse> timelines = ok.Value.Should().BeOfType<List<ProjectTimelineResponse>>().Subject;
        timelines.Should().BeEmpty();
    }

    private static async Task<List<ProjectTimelineResponse>> ListFinalScenarioAsync(
        Func<Project, WorkflowStep, List<(WorkflowExecution Run, WorkflowStepResult Result)>> build,
        Func<Project, WorkflowStep, WorkflowStepResult, int>? unused = null)
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        DateTime t0 = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);
        Project project = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = t0, UpdatedAt = t0 };
        WorkflowDefinition wf = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "15s reel", CreatedAt = t0, UpdatedAt = t0 };
        WorkflowStep step = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = wf.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoCompile, Label = "Compile" };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(wf);
        db.WorkflowSteps.Add(step);
        foreach (var (run, result) in build(project, step))
        {
            run.WorkflowDefinitionId = wf.Id;
            run.ProjectId = project.Id;
            if (db.WorkflowExecutions.Local.All(e => e.Id != run.Id)) db.WorkflowExecutions.Add(run);
            result.WorkflowExecutionId = run.Id;
            result.WorkflowStepId = step.Id;
            db.WorkflowStepResults.Add(result);
        }
        await db.SaveChangesAsync();
        TimelinesController c = new(db, new FakeCurrentUser(ownerId), TestTenancy.Access(db, new FakeCurrentUser(ownerId)));
        var res = await c.ListTimelines(project.Id, CancellationToken.None);
        return res.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<List<ProjectTimelineResponse>>().Subject;
    }

    private static WorkflowStepResult Compile(Project p, string name, int hour, bool timeline = true) => new()
    {
        Id = Guid.NewGuid(),
        Status = StepStatus.Completed,
        OutputStorageKey = $"projects/{p.Id}/outputFiles/{name}.mp4",
        TimelineStorageKey = timeline ? $"projects/{p.Id}/agentFiles/{name}-timeline.json" : null,
        ExecutedAt = new DateTime(2026, 10, 5, hour, 0, 0, DateTimeKind.Utc),
        CompletedAt = new DateTime(2026, 10, 5, hour, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task ListTimelines_ChainedRun_MarksOnlyLastCompileFinal()
    {
        WorkflowExecution run = new() { Id = Guid.NewGuid(), Status = ExecutionStatus.Passed, StartedAt = new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc) };
        WorkflowStepResult? first = null, last = null;
        List<ProjectTimelineResponse> list = await ListFinalScenarioAsync((p, _) =>
        {
            first = Compile(p, "a", 10);
            last = Compile(p, "b", 11);
            return [(run, first), (run, last)];
        });
        list.Should().HaveCount(2);
        list.Single(t => t.StepResultId == last!.Id).IsFinal.Should().BeTrue();
        list.Single(t => t.StepResultId == first!.Id).IsFinal.Should().BeFalse();
        list.All(t => t.WorkflowName == "15s reel" && t.RunNumber == 1 && t.StepLabel == "Compile").Should().BeTrue();
    }

    [Fact]
    public async Task ListTimelines_TwoRuns_GiveTwoFinalsAndRunNumbers()
    {
        WorkflowExecution r1 = new() { Id = Guid.NewGuid(), Status = ExecutionStatus.Passed, StartedAt = new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc) };
        WorkflowExecution r2 = new() { Id = Guid.NewGuid(), Status = ExecutionStatus.Passed, StartedAt = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc) };
        List<ProjectTimelineResponse> list = await ListFinalScenarioAsync((p, _) =>
            [(r1, Compile(p, "a", 10)), (r2, Compile(p, "b", 13))]);
        list.Count(t => t.IsFinal).Should().Be(2);
        list.Single(t => t.WorkflowExecutionId == r2.Id).RunNumber.Should().Be(2);
    }

    [Fact]
    public async Task ListTimelines_RunWhoseFinalHasNoTimelineKey_HasNoFinalRow()
    {
        WorkflowExecution run = new() { Id = Guid.NewGuid(), Status = ExecutionStatus.Passed, StartedAt = new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc) };
        List<ProjectTimelineResponse> list = await ListFinalScenarioAsync((p, _) =>
            [(run, Compile(p, "a", 10)), (run, Compile(p, "b", 11, timeline: false))]);
        list.Should().HaveCount(1);
        list.Single().IsFinal.Should().BeFalse();
    }
}
