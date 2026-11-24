#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// agent-model-choice: the assistant's read-only view of the model choice. <c>ListAgents</c> says which model each agent
/// runs on for the project's workspace (<c>model</c>, a display name) and why (<c>modelSource</c>: workspace, recommended
/// or default). Names only: no id, no endpoint. The tool count and names do not change.
/// </summary>
public sealed class AssistantListAgentsModelTests
{
    private sealed class Caller(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId { get; } = TestTenancy.OrgOf(userId);
        public OrganizationRole? OrgRole => OrganizationRole.Member;
        public bool EmailVerified => true;
    }

    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private readonly Guid _me = Guid.NewGuid();
    private readonly Mock<IInferenceProviderResolver> _resolver = new();
    private readonly Project _project;
    private static readonly Guid RecommendedId = Guid.NewGuid();
    private static readonly Guid ChosenId = Guid.NewGuid();
    private static readonly Guid DefaultId = Guid.NewGuid();

    public AssistantListAgentsModelTests()
    {
        _project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "P",
            Description = "d",
            OwnerId = _me,
            OrganizationId = TestTenancy.OrgOf(_me),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Projects.Add(_project);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    private void Agent(AgentType type, Guid? recommended = null) => _db.AgentDefinitions.Add(new AgentDefinition
    {
        Id = Guid.NewGuid(),
        Name = type.ToString(),
        AgentType = type,
        IsBuiltIn = true,
        InferenceProviderId = recommended,
    });

    private void ResolvesAs(AgentType type, Guid? id, string name) => _resolver
        .Setup(r => r.ResolveAsync(type, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new ResolvedInferenceProvider(id, name, InferenceProviderKind.DeepSeek, "https://secret.example/v1", "code", "key", 60));

    private static string AsText(object? raw) => raw switch
    {
        JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString()!,
        JsonElement element => element.GetRawText(),
        _ => raw!.ToString()!
    };

    private async Task<JsonElement[]> ListAsync()
    {
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(_db, new Caller(_me), providerResolver: _resolver.Object);
        AIFunction tool = provider.GetTools(new AssistantToolContext(_me, _project.Id)).Single(t => t.Name == "ListAgents");
        object? raw = await tool.InvokeAsync(new AIFunctionArguments());
        string text = AsText(raw);
        return JsonDocument.Parse(text).RootElement.EnumerateArray().Select(a => a.Clone()).ToArray();
    }

    [Fact]
    public async Task An_agent_on_its_recommended_model_says_so_by_name()
    {
        Agent(AgentType.AuthorAgent, RecommendedId);
        ResolvesAs(AgentType.AuthorAgent, RecommendedId, "DeepSeek Flash");

        JsonElement row = (await ListAsync()).Single();

        row.GetProperty("model").GetString().Should().Be("DeepSeek Flash");
        row.GetProperty("modelSource").GetString().Should().Be("recommended");
    }

    [Fact]
    public async Task A_workspace_choice_is_reported_as_workspace()
    {
        Agent(AgentType.AuthorAgent, RecommendedId);
        _db.OrganizationAgentProviderOverrides.Add(new OrganizationAgentProviderOverride
        {
            OrganizationId = _project.OrganizationId,
            AgentType = AgentType.AuthorAgent,
            InferenceProviderId = ChosenId,
        });
        ResolvesAs(AgentType.AuthorAgent, ChosenId, "Claude Opus");

        JsonElement row = (await ListAsync()).Single();

        row.GetProperty("model").GetString().Should().Be("Claude Opus");
        row.GetProperty("modelSource").GetString().Should().Be("workspace");
    }

    [Fact]
    public async Task Another_workspaces_choice_does_not_make_this_one_workspace()
    {
        Agent(AgentType.AuthorAgent);
        _db.OrganizationAgentProviderOverrides.Add(new OrganizationAgentProviderOverride
        {
            OrganizationId = Guid.NewGuid(),
            AgentType = AgentType.AuthorAgent,
            InferenceProviderId = DefaultId,
        });
        ResolvesAs(AgentType.AuthorAgent, DefaultId, "Platform default");

        (await ListAsync()).Single().GetProperty("modelSource").GetString().Should().Be("default");
    }

    [Fact]
    public async Task No_recommendation_and_no_choice_is_default()
    {
        Agent(AgentType.ReviewAgent);
        ResolvesAs(AgentType.ReviewAgent, DefaultId, "Platform default");

        JsonElement row = (await ListAsync()).Single();

        row.GetProperty("model").GetString().Should().Be("Platform default");
        row.GetProperty("modelSource").GetString().Should().Be("default");
    }

    [Fact]
    public async Task An_agent_that_cannot_resolve_has_a_null_model_and_the_list_still_works()
    {
        Agent(AgentType.AuthorAgent);
        Agent(AgentType.ReviewAgent);
        _resolver.Setup(r => r.ResolveAsync(AgentType.AuthorAgent, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ModelUnpricedException(InferenceProviderCapability.Chat, "x"));
        ResolvesAs(AgentType.ReviewAgent, DefaultId, "Platform default");

        JsonElement[] rows = await ListAsync();

        JsonElement unresolved = rows.Single(r => r.GetProperty("AgentType").GetString() == "AuthorAgent");
        unresolved.GetProperty("model").ValueKind.Should().Be(JsonValueKind.Null);
        unresolved.GetProperty("modelSource").GetString().Should().Be("default");
        rows.Single(r => r.GetProperty("AgentType").GetString() == "ReviewAgent").GetProperty("model").GetString().Should().Be("Platform default");
    }

    [Fact]
    public async Task Without_a_resolver_the_model_is_null()
    {
        Agent(AgentType.AuthorAgent);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(_db, new Caller(_me));
        AIFunction tool = provider.GetTools(new AssistantToolContext(_me, _project.Id)).Single(t => t.Name == "ListAgents");

        object? raw = await tool.InvokeAsync(new AIFunctionArguments());

        string text = AsText(raw);
        JsonElement row = JsonDocument.Parse(text).RootElement.EnumerateArray().Single();
        row.GetProperty("model").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("modelSource").GetString().Should().Be("default");
    }

    [Fact]
    public async Task The_row_carries_names_only_no_model_id_or_endpoint()
    {
        Agent(AgentType.AuthorAgent, RecommendedId);
        ResolvesAs(AgentType.AuthorAgent, RecommendedId, "DeepSeek Flash");

        JsonElement row = (await ListAsync()).Single();

        row.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("Id", "Name", "Description", "AgentType", "model", "modelSource");
        row.GetRawText().Should().NotContain(RecommendedId.ToString()).And.NotContain("secret.example");
    }

    [Fact]
    public void The_tool_description_says_the_model_is_listed()
    {
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(_db, new Caller(_me));

        AIFunction tool = provider.GetTools(new AssistantToolContext(_me, _project.Id)).Single(t => t.Name == "ListAgents");

        tool.Description.Should().Contain("model").And.Contain("modelSource");
    }
}
