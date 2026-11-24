using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Skills;
using ReelBolt.Shared.Tenancy;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Coverage for the "skill assignment is editable ONLY for custom agents, never built-in ones"
/// rule (SkillsController.SetSkills) plus the AgentDefinitionResponse EffectiveSkills resolution
/// (AgentsController.MapToResponse). This is deliberately the OPPOSITE behavior from
/// AgentsController.SetInferenceProvider, which allows built-ins — see both classes' doc
/// comments.
///
/// <para>
/// WP A8 adds the organization axis: a custom agent in another organization is <c>404</c> (D5) and
/// inside the caller's own organization the action needs Owner or Admin, so a Member gets
/// <c>403</c>.
/// </para>
/// </summary>
public class SkillsControllerTests
{
    private static InferenceApiDbContext CreateContext()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new InferenceApiDbContext(options);
    }

    private static Guid Org(Guid userId) => TestTenancy.OrgOf(userId);

    private sealed class FakeCurrentUser(
        Guid userId, OrganizationRole role, Guid? organizationId = null, bool isAdmin = false) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = isAdmin;
        public Guid? OrganizationId { get; } = organizationId ?? Org(userId);
        public OrganizationRole? OrgRole { get; } = role;
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "Test";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
    }

    private static AgentDefinition BuiltInAgent(AgentType agentType, string? assignedSkillsJson = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = agentType.ToString(),
        Description = "d",
        SystemPrompt = "p",
        AgentType = agentType,
        IsBuiltIn = true,
        OrganizationId = null,
        AssignedSkillsJson = assignedSkillsJson,
        CreatedAt = DateTime.UtcNow
    };

    private static AgentDefinition CustomAgent(
        Guid organizationId, string? assignedSkillsJson = null, Guid? ownerId = null) => new()
        {
            Id = Guid.NewGuid(),
            Name = "MyCustomAgent",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.Custom,
            IsBuiltIn = false,
            OwnerId = ownerId ?? Guid.NewGuid(),
            OrganizationId = organizationId,
            AssignedSkillsJson = assignedSkillsJson,
            CreatedAt = DateTime.UtcNow
        };

    private static SkillsController CreateController(InferenceApiDbContext db, ICurrentUser user) =>
        new(db, user, new FakeWebHostEnvironment());

    // --- The single most important test: 403 for built-in agents ---

    [Fact]
    public async Task SetSkills_returns_403_forbidden_for_a_built_in_agent()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        AgentDefinition agent = BuiltInAgent(AgentType.AuthorAgent);
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();

        SkillsController controller = CreateController(db, new FakeCurrentUser(callerId, OrganizationRole.Owner));

        ActionResult<AgentDefinitionResponse> result = await controller.SetSkills(
            agent.Id, new SetAgentSkillsRequest(["remotion-markup"]), CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();

        // The column must be left untouched — the request must not have been persisted.
        AgentDefinition reloaded = await db.AgentDefinitions.SingleAsync(a => a.Id == agent.Id);
        reloaded.AssignedSkillsJson.Should().BeNull();
    }

    [Fact]
    public async Task SetSkills_succeeds_and_persists_for_a_custom_agent_in_the_callers_organization()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        AgentDefinition agent = CustomAgent(Org(callerId));
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();

        SkillsController controller = CreateController(db, new FakeCurrentUser(callerId, OrganizationRole.Admin));

        ActionResult<AgentDefinitionResponse> result = await controller.SetSkills(
            agent.Id, new SetAgentSkillsRequest(["remotion-markup", "remotion-render"]), CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        AgentDefinitionResponse response = ok.Value.Should().BeOfType<AgentDefinitionResponse>().Subject;
        response.AssignedSkills.Should().BeEquivalentTo(["remotion-markup", "remotion-render"]);
        response.EffectiveSkills.Should().BeEquivalentTo(["remotion-markup", "remotion-render"]);

        AgentDefinition reloaded = await db.AgentDefinitions.SingleAsync(a => a.Id == agent.Id);
        JsonSerializer.Deserialize<string[]>(reloaded.AssignedSkillsJson!)
            .Should().BeEquivalentTo(["remotion-markup", "remotion-render"]);
    }

    [Fact]
    public async Task SetSkills_returns_400_for_unknown_skill_names()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        AgentDefinition agent = CustomAgent(Org(callerId));
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();

        SkillsController controller = CreateController(db, new FakeCurrentUser(callerId, OrganizationRole.Owner));

        ActionResult<AgentDefinitionResponse> result = await controller.SetSkills(
            agent.Id, new SetAgentSkillsRequest(["remotion-markup", "not-a-real-skill"]), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();

        AgentDefinition reloaded = await db.AgentDefinitions.SingleAsync(a => a.Id == agent.Id);
        reloaded.AssignedSkillsJson.Should().BeNull();
    }

    // --- WP A8: the organization axis ---

    [Fact]
    public async Task SetSkills_returns_403_for_a_member_of_the_same_organization()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        AgentDefinition agent = CustomAgent(Org(callerId), ownerId: callerId);
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();

        SkillsController controller = CreateController(db, new FakeCurrentUser(callerId, OrganizationRole.Member));

        ActionResult<AgentDefinitionResponse> result = await controller.SetSkills(
            agent.Id, new SetAgentSkillsRequest(["remotion-markup"]), CancellationToken.None);

        // 403, not 404: the agent is right there in the caller's organization — the ROLE is what
        // refuses (docs/design/tenancy.md: only Owner/Admin assign skills).
        result.Result.Should().BeOfType<ForbidResult>();
        db.ChangeTracker.Clear();
        (await db.AgentDefinitions.SingleAsync(a => a.Id == agent.Id)).AssignedSkillsJson.Should().BeNull();
    }

    [Fact]
    public async Task SetSkills_returns_403_for_a_platform_admin_who_is_only_a_member_of_the_organization()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        AgentDefinition agent = CustomAgent(Org(callerId));
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();

        // Platform authority is not an org role: the ADR's row for skills is an org Owner/Admin one.
        SkillsController controller = CreateController(
            db, new FakeCurrentUser(callerId, OrganizationRole.Member, isAdmin: true));

        ActionResult<AgentDefinitionResponse> result = await controller.SetSkills(
            agent.Id, new SetAgentSkillsRequest(["remotion-markup"]), CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task SetSkills_returns_404_for_a_custom_agent_in_another_organization()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        AgentDefinition theirs = CustomAgent(Org(Guid.NewGuid()));
        db.AgentDefinitions.Add(theirs);
        await db.SaveChangesAsync();

        SkillsController controller = CreateController(db, new FakeCurrentUser(callerId, OrganizationRole.Owner));

        ActionResult<AgentDefinitionResponse> result = await controller.SetSkills(
            theirs.Id, new SetAgentSkillsRequest(["remotion-markup"]), CancellationToken.None);

        // Existence never leaks (D5): 404, even for an org Owner, and nothing is written.
        result.Result.Should().BeOfType<NotFoundResult>();
        db.ChangeTracker.Clear();
        (await db.AgentDefinitions.SingleAsync(a => a.Id == theirs.Id)).AssignedSkillsJson.Should().BeNull();
    }

    [Fact]
    public async Task SetSkills_returns_404_for_a_custom_agent_in_another_organization_for_a_platform_admin_too()
    {
        using InferenceApiDbContext db = CreateContext();
        AgentDefinition theirs = CustomAgent(Org(Guid.NewGuid()));
        db.AgentDefinitions.Add(theirs);
        await db.SaveChangesAsync();

        SkillsController controller = CreateController(
            db, new FakeCurrentUser(Guid.NewGuid(), OrganizationRole.Owner, isAdmin: true));

        ActionResult<AgentDefinitionResponse> result = await controller.SetSkills(
            theirs.Id, new SetAgentSkillsRequest(["remotion-markup"]), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    // --- EffectiveSkills resolution (AgentsController.MapToResponse) ---

    [Fact]
    public void MapToResponse_EffectiveSkills_matches_SkillCatalog_DefaultsFor_a_built_in_agent()
    {
        AgentDefinition agent = BuiltInAgent(AgentType.AuthorAgent);

        AgentDefinitionResponse response = AgentsController.MapToResponse(agent);

        response.AssignedSkills.Should().BeNull();
        response.EffectiveSkills.Should().BeEquivalentTo(SkillCatalog.DefaultsFor(AgentType.AuthorAgent));
    }

    [Fact]
    public void MapToResponse_EffectiveSkills_matches_stored_AssignedSkillsJson_for_a_custom_agent()
    {
        AgentDefinition agent = CustomAgent(Org(Guid.NewGuid()), JsonSerializer.Serialize(new[] { "remotion-captions" }));

        AgentDefinitionResponse response = AgentsController.MapToResponse(agent);

        response.AssignedSkills.Should().BeEquivalentTo(["remotion-captions"]);
        response.EffectiveSkills.Should().BeEquivalentTo(["remotion-captions"]);
    }

    [Fact]
    public void MapToResponse_EffectiveSkills_is_empty_not_null_for_a_custom_agent_with_no_assignment()
    {
        AgentDefinition agent = CustomAgent(Org(Guid.NewGuid()), assignedSkillsJson: null);

        AgentDefinitionResponse response = AgentsController.MapToResponse(agent);

        response.AssignedSkills.Should().BeNull();
        response.EffectiveSkills.Should().BeEmpty();
    }

    [Fact]
    public void MapToResponse_carries_the_owning_organization_for_a_custom_agent_and_null_for_a_built_in()
    {
        Guid organizationId = Org(Guid.NewGuid());

        AgentsController.MapToResponse(CustomAgent(organizationId)).OrganizationId.Should().Be(organizationId);
        AgentsController.MapToResponse(BuiltInAgent(AgentType.AuthorAgent)).OrganizationId.Should().BeNull();
    }

    // --- Skill listing ---

    [Fact]
    public async Task List_computes_assignedAgentCount_only_from_custom_agent_rows()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        db.AgentDefinitions.Add(BuiltInAgent(AgentType.AuthorAgent)); // never counted, even if set
        db.AgentDefinitions.Add(CustomAgent(Org(callerId), JsonSerializer.Serialize(new[] { "remotion-markup" })));
        db.AgentDefinitions.Add(CustomAgent(Org(callerId), JsonSerializer.Serialize(new[] { "remotion-markup", "remotion-render" })));
        await db.SaveChangesAsync();

        SkillsController controller = CreateController(db, new FakeCurrentUser(callerId, OrganizationRole.Member));

        ActionResult<List<SkillSummaryResponse>> result = await controller.List(CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        List<SkillSummaryResponse> skills = ok.Value.Should().BeAssignableTo<List<SkillSummaryResponse>>().Subject;
        skills.Single(s => s.Name == "remotion-markup").AssignedAgentCount.Should().Be(2);
        skills.Single(s => s.Name == "remotion-render").AssignedAgentCount.Should().Be(1);
        skills.Single(s => s.Name == "remotion-captions").AssignedAgentCount.Should().Be(0);
    }

    [Fact]
    public async Task Get_returns_404_for_unknown_skill_name()
    {
        using InferenceApiDbContext db = CreateContext();
        SkillsController controller = CreateController(db, new FakeCurrentUser(Guid.NewGuid(), OrganizationRole.Member));

        ActionResult<SkillDetailResponse> result = await controller.Get("does-not-exist", CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }
}
