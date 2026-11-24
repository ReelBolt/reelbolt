using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Covers the bug where a step's <see cref="WorkflowStep.CacheMode"/> override
/// ("Always"/"Never") was silently dropped by the Inference API: it was missing from
/// <see cref="CreateWorkflowStepRequest"/>/<see cref="WorkflowStepResponse"/> and from
/// <c>WorkflowsController.CreateStep</c>'s request-to-entity mapping, so a caller-supplied
/// value never reached the entity and GET always echoed back null/Default regardless of
/// what was POSTed.
/// </summary>
public class WorkflowsControllerCacheModeTests
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
        public bool IsPlatformAdmin { get; } = true;
        public System.Guid? OrganizationId => ReelBolt.WorkflowEngine.Tests.TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    private static WorkflowsController CreateController(InferenceApiDbContext db, Guid ownerId)
    {
        WorkflowTemplateProvisioningService templateService = new(
            db,
            NullLogger<WorkflowTemplateProvisioningService>.Instance,
            new ConfigurationBuilder().Build());

        IWorkflowExecutionService executionService = new WorkflowExecutionService(
            db,
            Mock.Of<IPublishEndpoint>(),
            NullLogger<WorkflowExecutionService>.Instance);

        return new WorkflowsController(
            db,
            new FakeCurrentUser(ownerId), TestTenancy.Access(db, new FakeCurrentUser(ownerId)),
            Mock.Of<IPublishEndpoint>(),
            templateService,
            executionService,
            NullLogger<WorkflowsController>.Instance);
    }

    [Theory]
    [InlineData("Always")]
    [InlineData("Never")]
    public async Task Create_persists_and_roundtrips_a_step_level_CacheMode_override(string cacheMode)
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();

        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "AuthorAgent",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();

        WorkflowsController controller = CreateController(db, ownerId);

        CreateWorkflowStepRequest stepReq = new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: null, CacheMode: cacheMode);
        CreateWorkflowRequest request = new("wf", new List<CreateWorkflowStepRequest> { stepReq });

        ActionResult<WorkflowDefinitionResponse> createResult = await controller.Create(project.Id, request, CancellationToken.None);
        var created = ((ObjectResult)createResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;
        created.Steps.Single().CacheMode.Should().Be(cacheMode);

        // Verify the entity itself, not just the response DTO.
        WorkflowStep persisted = await db.WorkflowSteps.SingleAsync(s => s.WorkflowDefinitionId == created.Id);
        persisted.CacheMode.Should().Be(Enum.Parse<StepCacheMode>(cacheMode));

        // And that a fresh GET (a separate read path) echoes it back too.
        ActionResult<WorkflowDefinitionResponse> getResult = await controller.Get(project.Id, created.Id, CancellationToken.None);
        var fetched = ((OkObjectResult)getResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;
        fetched.Steps.Single().CacheMode.Should().Be(cacheMode);
    }

    [Fact]
    public async Task Create_leaves_CacheMode_null_when_not_supplied()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();

        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "AuthorAgent",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();

        WorkflowsController controller = CreateController(db, ownerId);

        CreateWorkflowStepRequest stepReq = new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: null);
        CreateWorkflowRequest request = new("wf", new List<CreateWorkflowStepRequest> { stepReq });

        ActionResult<WorkflowDefinitionResponse> createResult = await controller.Create(project.Id, request, CancellationToken.None);
        var created = ((ObjectResult)createResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;

        created.Steps.Single().CacheMode.Should().BeNull();
    }
}
