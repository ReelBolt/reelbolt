using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Moq;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// Tests for <see cref="WorkflowExecutionService"/>.
/// </summary>
public class WorkflowExecutionServiceTests
{
    private static InferenceApiDbContext CreateContext()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new InferenceApiDbContext(options);
    }

    [Fact]
    public async Task ExecuteAsync_WithValidProjectAndWorkflow_PersistsQueuedExecutionAndPublishesEvent()
    {
        // Arrange
        using InferenceApiDbContext db = CreateContext();
        Guid projectId = Guid.NewGuid();
        Guid workflowId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        string userRequest = "Test request";

        Project project = new()
        {
            Id = projectId,
            Name = "Test Project",
            OwnerId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        WorkflowDefinition workflow = new()
        {
            Id = workflowId,
            Name = "Test Workflow",
            ProjectId = projectId,
            RequiresUserInput = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        await db.SaveChangesAsync();

        var mockPublishEndpoint = new Mock<IPublishEndpoint>();

        var service = new WorkflowExecutionService(
            db,
            mockPublishEndpoint.Object,
            NullLogger<WorkflowExecutionService>.Instance);

        // Act
        var result = await service.ExecuteAsync(
            projectId,
            workflowId,
            userId,
            userRequest,
            ExecutionInitiator.Human,
            CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(ExecutionStatus.Queued.ToString());
        result.UserRequest.Should().Be(userRequest);

        // Verify the execution was persisted
        var persistedExecution = await db.WorkflowExecutions.FirstOrDefaultAsync(
            e => e.Id == result.Id);
        persistedExecution.Should().NotBeNull();
        persistedExecution!.Status.Should().Be(ExecutionStatus.Queued);
        persistedExecution.WorkflowDefinitionId.Should().Be(workflowId);
        persistedExecution.ProjectId.Should().Be(projectId);
        persistedExecution.InitiatedByUserId.Should().Be(userId);
        persistedExecution.UserRequest.Should().Be(userRequest);

        // Verify the event was published
        mockPublishEndpoint.Verify(
            e => e.Publish(
                It.Is<WorkflowExecutionRequested>(msg =>
                    msg.ExecutionId == result.Id &&
                    msg.WorkflowDefinitionId == workflowId &&
                    msg.ProjectId == projectId &&
                    msg.InitiatedByUserId == userId &&
                    msg.UserRequest == userRequest),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithAssistantInitiator_PersistsWithCorrectInitiator()
    {
        // Arrange
        using InferenceApiDbContext db = CreateContext();
        Guid projectId = Guid.NewGuid();
        Guid workflowId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();

        Project project = new()
        {
            Id = projectId,
            Name = "Test Project",
            OwnerId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        WorkflowDefinition workflow = new()
        {
            Id = workflowId,
            Name = "Test Workflow",
            ProjectId = projectId,
            RequiresUserInput = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        await db.SaveChangesAsync();

        var mockPublishEndpoint = new Mock<IPublishEndpoint>();

        var service = new WorkflowExecutionService(
            db,
            mockPublishEndpoint.Object,
            NullLogger<WorkflowExecutionService>.Instance);

        // Act
        var result = await service.ExecuteAsync(
            projectId,
            workflowId,
            userId,
            null,
            ExecutionInitiator.Assistant,
            CancellationToken.None);

        // Assert
        var persistedExecution = await db.WorkflowExecutions.FirstOrDefaultAsync(
            e => e.Id == result.Id);
        persistedExecution.Should().NotBeNull();
        persistedExecution!.Initiator.Should().Be(ExecutionInitiator.Assistant);
    }

    [Fact]
    public async Task ExecuteAsync_WithUnknownProject_ThrowsWorkflowNotFoundException()
    {
        // Arrange
        using InferenceApiDbContext db = CreateContext();
        Guid unknownProjectId = Guid.NewGuid();
        Guid workflowId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();

        var mockPublishEndpoint = new Mock<IPublishEndpoint>();

        var service = new WorkflowExecutionService(
            db,
            mockPublishEndpoint.Object,
            NullLogger<WorkflowExecutionService>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<WorkflowNotFoundException>(
            () => service.ExecuteAsync(
                unknownProjectId,
                workflowId,
                userId,
                null,
                ExecutionInitiator.Human,
                CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_WithUnknownWorkflow_ThrowsWorkflowNotFoundException()
    {
        // Arrange
        using InferenceApiDbContext db = CreateContext();
        Guid projectId = Guid.NewGuid();
        Guid unknownWorkflowId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();

        Project project = new()
        {
            Id = projectId,
            Name = "Test Project",
            OwnerId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var mockPublishEndpoint = new Mock<IPublishEndpoint>();

        var service = new WorkflowExecutionService(
            db,
            mockPublishEndpoint.Object,
            NullLogger<WorkflowExecutionService>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<WorkflowNotFoundException>(
            () => service.ExecuteAsync(
                projectId,
                unknownWorkflowId,
                userId,
                null,
                ExecutionInitiator.Human,
                CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_WithWorkflowInDifferentProject_ThrowsWorkflowNotFoundException()
    {
        // Arrange
        using InferenceApiDbContext db = CreateContext();
        Guid projectId = Guid.NewGuid();
        Guid otherProjectId = Guid.NewGuid();
        Guid workflowId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();

        Project project = new()
        {
            Id = projectId,
            Name = "Test Project",
            OwnerId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        Project otherProject = new()
        {
            Id = otherProjectId,
            Name = "Other Project",
            OwnerId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        WorkflowDefinition workflow = new()
        {
            Id = workflowId,
            Name = "Test Workflow",
            ProjectId = otherProjectId,
            RequiresUserInput = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Projects.Add(project);
        db.Projects.Add(otherProject);
        db.WorkflowDefinitions.Add(workflow);
        await db.SaveChangesAsync();

        var mockPublishEndpoint = new Mock<IPublishEndpoint>();

        var service = new WorkflowExecutionService(
            db,
            mockPublishEndpoint.Object,
            NullLogger<WorkflowExecutionService>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<WorkflowNotFoundException>(
            () => service.ExecuteAsync(
                projectId,
                workflowId,
                userId,
                null,
                ExecutionInitiator.Human,
                CancellationToken.None));
    }
}
