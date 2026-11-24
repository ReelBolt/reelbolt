using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Controllers;

/// <summary>
/// WP A8: custom agents belong to an organization, so every agent route answers on the org axis.
/// The rule under test is D5 — a custom agent in ANOTHER organization is <c>404</c> (existence never
/// leaks), and the same resource in the caller's own organization with too low a role is <c>403</c>.
/// A built-in agent is shared by every organization and is therefore visible everywhere.
///
/// <para>
/// These tests deliberately build the <see cref="InferenceApiDbContext"/> WITHOUT a tenant context
/// for most cases, so the EF tenant filter is inert and only the controller's own explicit
/// <c>OrganizationId</c> comparison can produce the right answer. That is the second, independent
/// enforcement; <see cref="List_hides_another_organizations_custom_agent_through_the_tenant_filter_too"/>
/// covers the first one, with a real <see cref="ITenantContext"/>.
/// </para>
/// </summary>
public class AgentsControllerTests
{
    private static InferenceApiDbContext CreateContext(ITenantContext? tenant = null)
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return tenant is null ? new InferenceApiDbContext(options) : new InferenceApiDbContext(options, tenant);
    }

    private sealed class FakeCurrentUser(
        Guid userId, Guid? organizationId, OrganizationRole? role, bool isPlatformAdmin = false) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = isPlatformAdmin;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
    }

    private static Guid Org(Guid userId) => ReelBolt.WorkflowEngine.Tests.TestTenancy.OrgOf(userId);

    private static FakeCurrentUser Caller(Guid userId, OrganizationRole role, bool isPlatformAdmin = false) =>
        new(userId, Org(userId), role, isPlatformAdmin);

    /// <summary>A caller in an organization that is not derived from their user id (a teammate).</summary>
    private static FakeCurrentUser CallerIn(
        Guid userId, Guid organizationId, OrganizationRole role, bool isPlatformAdmin = false) =>
        new(userId, organizationId, role, isPlatformAdmin);

    private static AgentsController CreateController(InferenceApiDbContext db, ICurrentUser user) =>
        new(db, user, new ProjectAccessService(db, user)) { ControllerContext = new ControllerContext() };

    private static AgentDefinition BuiltIn(AgentType agentType = AgentType.AuthorAgent) => new()
    {
        Id = Guid.NewGuid(),
        Name = agentType.ToString(),
        Description = "d",
        SystemPrompt = "p",
        AgentType = agentType,
        IsBuiltIn = true,
        OrganizationId = null,
        CreatedAt = DateTime.UtcNow
    };

    private static AgentDefinition Custom(Guid organizationId, Guid ownerId, string name = "MyAgent") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Description = "d",
        SystemPrompt = "p",
        AgentType = AgentType.Custom,
        IsBuiltIn = false,
        OwnerId = ownerId,
        OrganizationId = organizationId,
        CreatedAt = DateTime.UtcNow
    };

    private static ModelCapabilityView ChatProvider() => new()
    {
        Id = Guid.NewGuid(),
        Name = "chat",
        Kind = InferenceProviderKind.OpenAICompatible,
        Capability = InferenceProviderCapability.Chat,
        Endpoint = "http://localhost:8000/v1",
        ModelName = "m",
        IsEnabled = true
    };

    // ------------------------------------------------------------------
    // Built-ins are shared; custom agents are not
    // ------------------------------------------------------------------

    [Fact]
    public async Task List_returns_built_ins_and_only_this_organizations_custom_agents()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        Guid stranger = Guid.NewGuid();

        AgentDefinition builtIn = BuiltIn();
        AgentDefinition mine = Custom(Org(me), Guid.NewGuid(), "Mine");
        AgentDefinition theirs = Custom(Org(stranger), stranger, "Theirs");
        db.AgentDefinitions.AddRange(builtIn, mine, theirs);
        await db.SaveChangesAsync();

        AgentsController controller = CreateController(db, Caller(me, OrganizationRole.Member));

        ActionResult<List<AgentDefinitionResponse>> result = await controller.List(CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        // List maps lazily, so the payload is an IEnumerable, not a materialized List.
        List<AgentDefinitionResponse> agents = ((IEnumerable<AgentDefinitionResponse>)ok.Value!).ToList();
        agents.Select(a => a.Id).Should().BeEquivalentTo([builtIn.Id, mine.Id]);
        agents.Single(a => a.Id == mine.Id).OrganizationId.Should().Be(Org(me));
        agents.Single(a => a.Id == builtIn.Id).OrganizationId.Should().BeNull();
    }

    [Fact]
    public async Task Get_returns_a_built_in_agent_to_a_caller_in_any_organization()
    {
        using InferenceApiDbContext db = CreateContext();
        AgentDefinition builtIn = BuiltIn();
        db.AgentDefinitions.Add(builtIn);
        await db.SaveChangesAsync();

        foreach (Guid userId in new[] { Guid.NewGuid(), Guid.NewGuid() })
        {
            AgentsController controller = CreateController(db, Caller(userId, OrganizationRole.Member));

            ActionResult<AgentDefinitionResponse> result = await controller.Get(builtIn.Id, CancellationToken.None);

            OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
            ok.Value.Should().BeOfType<AgentDefinitionResponse>().Subject.Id.Should().Be(builtIn.Id);
        }
    }

    [Fact]
    public async Task Get_returns_404_for_another_organizations_custom_agent()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        AgentDefinition theirs = Custom(Org(Guid.NewGuid()), Guid.NewGuid());
        db.AgentDefinitions.Add(theirs);
        await db.SaveChangesAsync();

        AgentsController controller = CreateController(db, Caller(me, OrganizationRole.Member));

        ActionResult<AgentDefinitionResponse> result = await controller.Get(theirs.Id, CancellationToken.None);

        // 404, never 403: a stranger must not learn that the guessed id exists.
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task List_hides_another_organizations_custom_agent_through_the_tenant_filter_too()
    {
        var tenant = new ReelBolt.WorkflowEngine.Tests.TestTenancy.MutableTenant();
        using InferenceApiDbContext db = CreateContext(tenant);

        Guid me = Guid.NewGuid();
        AgentDefinition builtIn = BuiltIn();
        AgentDefinition mine = Custom(Org(me), me, "Mine");
        AgentDefinition theirs = Custom(Org(Guid.NewGuid()), Guid.NewGuid(), "Theirs");
        db.AgentDefinitions.AddRange(builtIn, mine, theirs);
        await db.SaveChangesAsync();

        // Narrow the context to the caller's organization: the EF query filter alone must hide the
        // foreign custom agent, so the controller's explicit check is the second line, not the only.
        tenant.ActAs(me);

        AgentsController controller = CreateController(db, Caller(me, OrganizationRole.Member));
        ActionResult<List<AgentDefinitionResponse>> result = await controller.List(CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        // List maps lazily, so the payload is an IEnumerable, not a materialized List.
        List<AgentDefinitionResponse> agents = ((IEnumerable<AgentDefinitionResponse>)ok.Value!).ToList();
        agents.Select(a => a.Id).Should().BeEquivalentTo([builtIn.Id, mine.Id]);

        ActionResult<AgentDefinitionResponse> get = await controller.Get(theirs.Id, CancellationToken.None);
        get.Result.Should().BeOfType<NotFoundResult>();
    }

    // ------------------------------------------------------------------
    // Mutations
    // ------------------------------------------------------------------

    [Fact]
    public async Task Update_by_a_teammate_in_the_same_organization_succeeds()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        Guid teammate = Guid.NewGuid();
        AgentDefinition agent = Custom(Org(me), teammate);   // created by somebody else in my org
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();

        AgentsController controller = CreateController(db, Caller(me, OrganizationRole.Member));

        ActionResult<AgentDefinitionResponse> result = await controller.Update(
            agent.Id, new UpdateAgentRequest("Renamed", "d2", "p2", null, null), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        (await db.AgentDefinitions.SingleAsync(a => a.Id == agent.Id)).Name.Should().Be("Renamed");
    }

    [Fact]
    public async Task Update_returns_404_for_another_organizations_custom_agent_and_writes_nothing()
    {
        using InferenceApiDbContext db = CreateContext();
        AgentDefinition theirs = Custom(Org(Guid.NewGuid()), Guid.NewGuid(), "Theirs");
        db.AgentDefinitions.Add(theirs);
        await db.SaveChangesAsync();

        AgentsController controller = CreateController(db, Caller(Guid.NewGuid(), OrganizationRole.Member));

        ActionResult<AgentDefinitionResponse> result = await controller.Update(
            theirs.Id, new UpdateAgentRequest("Renamed", "d2", "p2", null, null), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
        db.ChangeTracker.Clear();
        (await db.AgentDefinitions.SingleAsync(a => a.Id == theirs.Id)).Name.Should().Be("Theirs");
    }

    [Fact]
    public async Task Delete_returns_404_for_another_organizations_custom_agent_and_removes_nothing()
    {
        using InferenceApiDbContext db = CreateContext();
        AgentDefinition theirs = Custom(Org(Guid.NewGuid()), Guid.NewGuid(), "Theirs");
        db.AgentDefinitions.Add(theirs);
        await db.SaveChangesAsync();

        AgentsController controller = CreateController(db, Caller(Guid.NewGuid(), OrganizationRole.Member));

        IActionResult result = await controller.Delete(theirs.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        (await db.AgentDefinitions.AnyAsync(a => a.Id == theirs.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Update_and_delete_are_refused_for_a_built_in_agent()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        AgentDefinition builtIn = BuiltIn();
        db.AgentDefinitions.Add(builtIn);
        await db.SaveChangesAsync();

        AgentsController controller = CreateController(db, Caller(me, OrganizationRole.Owner));

        (await controller.Update(builtIn.Id, new UpdateAgentRequest("x", "y", "z", null, null), CancellationToken.None))
            .Result.Should().BeOfType<ForbidResult>();
        (await controller.Delete(builtIn.Id, CancellationToken.None)).Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Create_stamps_the_callers_organization()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        AgentsController controller = CreateController(db, Caller(me, OrganizationRole.Member));

        ActionResult<AgentDefinitionResponse> result = await controller.Create(
            new CreateAgentRequest("New", "d", "p", null, null), CancellationToken.None);

        CreatedAtActionResult created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(201);
        AgentDefinitionResponse response = created.Value.Should().BeOfType<AgentDefinitionResponse>().Subject;
        response.OrganizationId.Should().Be(Org(me));

        (await db.AgentDefinitions.SingleAsync(a => a.Id == response.Id)).OrganizationId.Should().Be(Org(me));
    }

    // ------------------------------------------------------------------
    // Roles (D5's 403 half)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Billing_is_refused_403_on_every_agent_route()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        AgentDefinition mine = Custom(Org(me), me);
        db.AgentDefinitions.Add(mine);
        await db.SaveChangesAsync();

        AgentsController controller = CreateController(db, Caller(me, OrganizationRole.Billing));

        (await controller.List(CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await controller.Get(mine.Id, CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await controller.Create(new CreateAgentRequest("n", "d", "p", null, null), CancellationToken.None))
            .Result.Should().BeOfType<ForbidResult>();
        (await controller.Update(mine.Id, new UpdateAgentRequest("n", "d", "p", null, null), CancellationToken.None))
            .Result.Should().BeOfType<ForbidResult>();
        (await controller.Delete(mine.Id, CancellationToken.None)).Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task A_caller_with_no_active_organization_gets_404()
    {
        using InferenceApiDbContext db = CreateContext();
        AgentDefinition builtIn = BuiltIn();
        db.AgentDefinitions.Add(builtIn);
        await db.SaveChangesAsync();

        var noOrg = new FakeCurrentUser(Guid.NewGuid(), organizationId: null, role: null);
        AgentsController controller = CreateController(db, noOrg);

        (await controller.List(CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await controller.Get(builtIn.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
    }

    // ------------------------------------------------------------------
    // The per-agent inference-provider override
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetInferenceProvider_on_a_built_in_stays_platform_admin_only()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        AgentDefinition builtIn = BuiltIn();
        db.AgentDefinitions.Add(builtIn);
        await db.SaveChangesAsync();

        AgentsController orgOwner = CreateController(db, Caller(me, OrganizationRole.Owner));
        ActionResult<AgentDefinitionResponse> refused = await orgOwner.SetInferenceProvider(
            builtIn.Id, new SetAgentInferenceProviderRequest(null), CancellationToken.None);

        // An org Owner is not a platform admin: the built-in row IS the platform default.
        refused.Result.Should().BeOfType<ForbidResult>();

        AgentsController platformAdmin = CreateController(db, Caller(me, OrganizationRole.Owner, isPlatformAdmin: true));
        ActionResult<AgentDefinitionResponse> allowed = await platformAdmin.SetInferenceProvider(
            builtIn.Id, new SetAgentInferenceProviderRequest(null), CancellationToken.None);

        allowed.Result.Should().BeOfType<OkObjectResult>();
    }

    /// <summary>AUTHZ-IB-7: the built-in row is the platform default, so it takes a platform capability only.</summary>
    [Fact]
    public async Task SetInferenceProvider_on_a_built_in_refuses_a_capability_owned_by_the_admins_own_organization()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        AgentDefinition builtIn = BuiltIn();
        ModelCapabilityView own = ChatProvider();
        own.OrganizationId = Org(me);
        ModelCapabilityView platform = ChatProvider();
        db.AgentDefinitions.Add(builtIn);
        db.AddFlat(own);
        db.AddFlat(platform);
        await db.SaveChangesAsync();
        AgentsController platformAdmin = CreateController(db, Caller(me, OrganizationRole.Owner, isPlatformAdmin: true));

        (await platformAdmin.SetInferenceProvider(builtIn.Id, new SetAgentInferenceProviderRequest(own.Id), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await db.AgentDefinitions.SingleAsync(a => a.Id == builtIn.Id)).InferenceProviderId.Should().BeNull();

        (await platformAdmin.SetInferenceProvider(builtIn.Id, new SetAgentInferenceProviderRequest(platform.Id), CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>();
        (await db.AgentDefinitions.SingleAsync(a => a.Id == builtIn.Id)).InferenceProviderId.Should().Be(platform.Id);
    }

    [Fact]
    public async Task SetInferenceProvider_on_a_custom_agent_requires_org_admin()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid owner = Guid.NewGuid();
        Guid member = Guid.NewGuid();
        Guid organization = Org(owner);
        AgentDefinition agent = Custom(organization, owner);
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();

        // Same organization, too low a role: 403 — including for a PLATFORM admin, because the ADR
        // gives a custom agent's provider override to the organization's Owner/Admin, not to
        // platform authority.
        ActionResult<AgentDefinitionResponse> refused = await CreateController(db, CallerIn(member, organization, OrganizationRole.Member))
            .SetInferenceProvider(agent.Id, new SetAgentInferenceProviderRequest(null), CancellationToken.None);
        refused.Result.Should().BeOfType<ForbidResult>();

        ActionResult<AgentDefinitionResponse> refusedForPlatformAdmin = await CreateController(
                db, CallerIn(Guid.NewGuid(), organization, OrganizationRole.Member, isPlatformAdmin: true))
            .SetInferenceProvider(agent.Id, new SetAgentInferenceProviderRequest(null), CancellationToken.None);
        refusedForPlatformAdmin.Result.Should().BeOfType<ForbidResult>();

        // An Admin of that organization may, without holding platform authority.
        ActionResult<AgentDefinitionResponse> allowed = await CreateController(db, CallerIn(owner, organization, OrganizationRole.Admin))
            .SetInferenceProvider(agent.Id, new SetAgentInferenceProviderRequest(null), CancellationToken.None);
        allowed.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task SetInferenceProvider_assigns_a_chat_provider_to_an_org_admin()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        AgentDefinition agent = Custom(Org(me), me);
        ModelCapabilityView provider = ChatProvider();
        db.AgentDefinitions.Add(agent);
        db.AddFlat(provider);
        await db.SaveChangesAsync();

        AgentsController controller = CreateController(db, Caller(me, OrganizationRole.Owner));

        ActionResult<AgentDefinitionResponse> result = await controller.SetInferenceProvider(
            agent.Id, new SetAgentInferenceProviderRequest(provider.Id), CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<AgentDefinitionResponse>().Subject.InferenceProviderId.Should().Be(provider.Id);
        (await db.AgentDefinitions.SingleAsync(a => a.Id == agent.Id)).InferenceProviderId.Should().Be(provider.Id);
    }

    [Fact]
    public async Task SetInferenceProvider_returns_404_for_another_organizations_custom_agent()
    {
        using InferenceApiDbContext db = CreateContext();
        AgentDefinition theirs = Custom(Org(Guid.NewGuid()), Guid.NewGuid());
        db.AgentDefinitions.Add(theirs);
        await db.SaveChangesAsync();

        // Even a platform admin gets 404 here: the row is another organization's, not the platform's.
        AgentsController controller = CreateController(
            db, Caller(Guid.NewGuid(), OrganizationRole.Owner, isPlatformAdmin: true));

        ActionResult<AgentDefinitionResponse> result = await controller.SetInferenceProvider(
            theirs.Id, new SetAgentInferenceProviderRequest(null), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    // ------------------------------------------------------------------
    // Reasoning effort: same authority as the provider override
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetReasoningEffort_on_a_built_in_is_platform_admin_only_and_stores_the_normalized_value()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        AgentDefinition builtIn = BuiltIn();
        db.AgentDefinitions.Add(builtIn);
        await db.SaveChangesAsync();

        ActionResult<AgentDefinitionResponse> refused = await CreateController(db, Caller(me, OrganizationRole.Owner))
            .SetReasoningEffort(builtIn.Id, new SetAgentReasoningEffortRequest("low"), CancellationToken.None);
        refused.Result.Should().BeOfType<ForbidResult>();
        (await db.AgentDefinitions.SingleAsync(a => a.Id == builtIn.Id)).ReasoningEffort.Should().BeNull("a refusal writes nothing");

        ActionResult<AgentDefinitionResponse> allowed = await CreateController(db, Caller(me, OrganizationRole.Owner, isPlatformAdmin: true))
            .SetReasoningEffort(builtIn.Id, new SetAgentReasoningEffortRequest(" XHigh "), CancellationToken.None);

        OkObjectResult ok = allowed.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<AgentDefinitionResponse>().Subject.ReasoningEffort.Should().Be("xhigh");
        (await db.AgentDefinitions.SingleAsync(a => a.Id == builtIn.Id)).ReasoningEffort.Should().Be("xhigh");
    }

    [Theory]
    [InlineData("high")]
    [InlineData("max")]
    [InlineData("")]
    [InlineData("loud")]
    public async Task SetReasoningEffort_rejects_a_value_outside_the_vocabulary_with_400_and_writes_nothing(string value)
    {
        using InferenceApiDbContext db = CreateContext();
        AgentDefinition builtIn = BuiltIn();
        builtIn.ReasoningEffort = "low";
        db.AgentDefinitions.Add(builtIn);
        await db.SaveChangesAsync();

        ActionResult<AgentDefinitionResponse> result = await CreateController(
                db, Caller(Guid.NewGuid(), OrganizationRole.Owner, isPlatformAdmin: true))
            .SetReasoningEffort(builtIn.Id, new SetAgentReasoningEffortRequest(value), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await db.AgentDefinitions.SingleAsync(a => a.Id == builtIn.Id)).ReasoningEffort.Should().Be("low");
    }

    [Fact]
    public async Task SetReasoningEffort_null_clears_the_stored_value()
    {
        using InferenceApiDbContext db = CreateContext();
        AgentDefinition builtIn = BuiltIn();
        builtIn.ReasoningEffort = "medium";
        db.AgentDefinitions.Add(builtIn);
        await db.SaveChangesAsync();

        ActionResult<AgentDefinitionResponse> result = await CreateController(
                db, Caller(Guid.NewGuid(), OrganizationRole.Owner, isPlatformAdmin: true))
            .SetReasoningEffort(builtIn.Id, new SetAgentReasoningEffortRequest(null), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        (await db.AgentDefinitions.SingleAsync(a => a.Id == builtIn.Id)).ReasoningEffort.Should().BeNull();
    }

    [Fact]
    public async Task SetReasoningEffort_on_a_custom_agent_requires_org_admin_and_404s_across_organizations()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid owner = Guid.NewGuid();
        Guid organization = Org(owner);
        AgentDefinition agent = Custom(organization, owner);
        AgentDefinition theirs = Custom(Org(Guid.NewGuid()), Guid.NewGuid());
        db.AgentDefinitions.AddRange(agent, theirs);
        await db.SaveChangesAsync();

        // Platform authority buys nothing on an organization's own agent (A8).
        (await CreateController(db, CallerIn(Guid.NewGuid(), organization, OrganizationRole.Member, isPlatformAdmin: true))
            .SetReasoningEffort(agent.Id, new SetAgentReasoningEffortRequest("low"), CancellationToken.None))
            .Result.Should().BeOfType<ForbidResult>();

        (await CreateController(db, CallerIn(owner, organization, OrganizationRole.Admin))
            .SetReasoningEffort(agent.Id, new SetAgentReasoningEffortRequest("none"), CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>();
        (await db.AgentDefinitions.SingleAsync(a => a.Id == agent.Id)).ReasoningEffort.Should().Be("none");

        (await CreateController(db, Caller(Guid.NewGuid(), OrganizationRole.Owner, isPlatformAdmin: true))
            .SetReasoningEffort(theirs.Id, new SetAgentReasoningEffortRequest("low"), CancellationToken.None))
            .Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Get_exposes_the_stored_reasoning_effort()
    {
        using InferenceApiDbContext db = CreateContext();
        AgentDefinition builtIn = BuiltIn();
        builtIn.ReasoningEffort = "low";
        db.AgentDefinitions.Add(builtIn);
        await db.SaveChangesAsync();

        ActionResult<AgentDefinitionResponse> result = await CreateController(db, Caller(Guid.NewGuid(), OrganizationRole.Member))
            .Get(builtIn.Id, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<AgentDefinitionResponse>().Subject.ReasoningEffort.Should().Be("low");
    }
}
