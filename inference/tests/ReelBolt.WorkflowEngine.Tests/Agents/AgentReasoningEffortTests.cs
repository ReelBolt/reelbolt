using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Production;
using ReelBolt.WorkflowEngine.Agents.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// Reasoning effort as a first-class per-agent field: the seeded defaults, the resolution order a run
/// applies (definition row, then configuration, then the class default) and the resolver lookup that
/// feeds it.
/// </summary>
public class AgentReasoningEffortTests
{
    // ---- the seeder and the class defaults cannot drift --------------------------------------------

    [Fact]
    public void Every_workflow_agent_class_default_equals_the_value_the_seeder_writes()
    {
        using CompositionRootHarness root = CompositionRootHarness.Build(
            "workflow-engine",
            (services, configuration) => services.AddWorkflowEngineServices(configuration));
        using IServiceScope scope = root.Services.CreateScope();

        List<ReelBoltAgentBase> agents = scope.ServiceProvider.GetServices<IReelBoltAgent>()
            .OfType<ReelBoltAgentBase>().ToList();

        agents.Should().HaveCount(31, "every engine agent carries a default effort and must be seeded with the same one");

        foreach (ReelBoltAgentBase agent in agents)
        {
            DatabaseSeeder.BuiltInReasoningEfforts.Should().ContainKey(agent.AgentType, $"{agent.Name} needs a seeded effort");
            DatabaseSeeder.BuiltInReasoningEfforts[agent.AgentType].Should().Be(
                agent.DefaultReasoningEffort,
                $"{agent.Name}: the class default is what runs while the column is NULL, so the seed must not disagree with it");
        }

        // The four agents the owner lowered from xhigh (execution 1aa8d6f8: deliberation was not the bottleneck).
        foreach (AgentType lowered in new[]
                 {
                     AgentType.AuthorAgent, AgentType.DirectorAgent,
                     AgentType.RemotionComponentTranslator, AgentType.AnimationStrategyAgent
                 })
        {
            DatabaseSeeder.BuiltInReasoningEfforts[lowered].Should().Be("low");
        }
    }

    [Fact]
    public void Every_seeded_value_is_in_the_vocabulary()
    {
        DatabaseSeeder.BuiltInReasoningEfforts.Values.Should().OnlyContain(v => AgentReasoningEffort.IsValid(v));
    }

    // ---- seeding keeps an admin's value ---------------------------------------------------------------

    [Fact]
    public async Task Seeding_a_fresh_database_writes_the_shipped_effort_into_each_built_in_row()
    {
        using InferenceApiDbContext db = CreateDb();

        await DatabaseSeeder.SeedAgentDefinitionsAsync(db);

        AgentDefinition author = await db.AgentDefinitions.SingleAsync(a => a.AgentType == AgentType.AuthorAgent && a.IsBuiltIn);
        AgentDefinition editor = await db.AgentDefinitions.SingleAsync(a => a.AgentType == AgentType.VideoStoryEditor && a.IsBuiltIn);
        AgentDefinition assistant = await db.AgentDefinitions.SingleAsync(a => a.AgentType == AgentType.Assistant && a.IsBuiltIn);

        author.ReasoningEffort.Should().Be("low");
        editor.ReasoningEffort.Should().Be("none");
        assistant.ReasoningEffort.Should().BeNull("the assistant is not a workflow agent and has no seeded effort");
    }

    [Fact]
    public async Task Reseeding_fills_a_NULL_but_never_overwrites_a_value_an_admin_set()
    {
        using InferenceApiDbContext db = CreateDb();
        await DatabaseSeeder.SeedAgentDefinitionsAsync(db);

        AgentDefinition author = await db.AgentDefinitions.SingleAsync(a => a.AgentType == AgentType.AuthorAgent && a.IsBuiltIn);
        AgentDefinition director = await db.AgentDefinitions.SingleAsync(a => a.AgentType == AgentType.DirectorAgent && a.IsBuiltIn);
        author.ReasoningEffort = "xhigh"; // an admin's choice
        director.ReasoningEffort = null;  // a row from before the column existed
        await db.SaveChangesAsync();

        await DatabaseSeeder.SeedAgentDefinitionsAsync(db);

        (await db.AgentDefinitions.AsNoTracking().SingleAsync(a => a.Id == author.Id)).ReasoningEffort.Should().Be("xhigh");
        (await db.AgentDefinitions.AsNoTracking().SingleAsync(a => a.Id == director.Id)).ReasoningEffort.Should().Be("low");
    }

    // ---- the resolver lookup ----------------------------------------------------------------------------

    [Fact]
    public async Task The_resolver_looks_up_by_definition_id_then_by_built_in_type()
    {
        Guid builtInId = Guid.NewGuid();
        Guid customId = Guid.NewGuid();
        Guid noValueId = Guid.NewGuid();
        AgentReasoningEfforts stored = new(
            new Dictionary<Guid, string?> { [builtInId] = "medium", [customId] = "none", [noValueId] = null },
            new Dictionary<AgentType, string?> { [AgentType.AuthorAgent] = "medium", [AgentType.Colorist] = "low" });
        InferenceProviderResolver resolver = CreateResolver(stored);

        (await resolver.ResolveAgentReasoningEffortAsync(AgentType.AuthorAgent, builtInId, default)).Should().Be("medium");
        (await resolver.ResolveAgentReasoningEffortAsync(AgentType.Custom, customId, default)).Should().Be("none");
        // A room step's seats name only the type.
        (await resolver.ResolveAgentReasoningEffortAsync(AgentType.Colorist, null, default)).Should().Be("low");
        // A definition id with no stored value falls to the type's row; an unknown id does too.
        (await resolver.ResolveAgentReasoningEffortAsync(AgentType.Colorist, noValueId, default)).Should().Be("low");
        (await resolver.ResolveAgentReasoningEffortAsync(AgentType.Colorist, Guid.NewGuid(), default)).Should().Be("low");
        (await resolver.ResolveAgentReasoningEffortAsync(AgentType.Custom, Guid.NewGuid(), default)).Should().BeNull();
        (await resolver.ResolveAgentReasoningEffortAsync(AgentType.ReviewAgent, null, default)).Should().BeNull();
    }

    // ---- the order a run applies ------------------------------------------------------------------------

    [Fact]
    public async Task With_nothing_stored_the_class_default_is_sent()
    {
        RecordingProvider provider = new(stored: null);

        await RunColoristAsync(provider, new Dictionary<string, string?>());

        provider.Sent!.Reasoning!.Effort.Should().Be(ReasoningEffort.None, "Colorist ships with 'none'");
    }

    [Fact]
    public async Task Configuration_beats_the_class_default()
    {
        RecordingProvider provider = new(stored: null);

        await RunColoristAsync(provider, new Dictionary<string, string?> { ["Agents:Colorist:ReasoningEffort"] = "medium" });

        provider.Sent!.Reasoning!.Effort.Should().Be(ReasoningEffort.Medium);
    }

    [Fact]
    public async Task The_stored_value_beats_configuration_and_the_class_default()
    {
        RecordingProvider provider = new(stored: "xhigh");

        await RunColoristAsync(provider, new Dictionary<string, string?> { ["Agents:Colorist:ReasoningEffort"] = "low" });

        provider.Sent!.Reasoning!.Effort.Should().Be(ReasoningEffort.ExtraHigh);
    }

    [Fact]
    public async Task The_run_names_the_definition_driving_it_when_looking_the_value_up()
    {
        RecordingProvider provider = new(stored: "low");
        Guid definitionId = Guid.NewGuid();

        await RunColoristAsync(provider, new Dictionary<string, string?>(), definitionId);

        provider.LookedUp!.Value.Item1.Should().Be(AgentType.Colorist);
        provider.LookedUp!.Value.Item2.Should().Be(definitionId);
    }

    [Fact]
    public async Task A_stored_value_outside_the_vocabulary_is_ignored_rather_than_sent()
    {
        RecordingProvider provider = new(stored: "high");

        await RunColoristAsync(provider, new Dictionary<string, string?>());

        provider.Sent!.Reasoning!.Effort.Should().Be(ReasoningEffort.None, "falls through to the class default");
    }

    [Fact]
    public async Task A_failing_lookup_falls_back_instead_of_failing_the_run()
    {
        RecordingProvider provider = new(stored: null) { ThrowOnLookup = true };

        await RunColoristAsync(provider, new Dictionary<string, string?>());

        provider.Sent!.Reasoning!.Effort.Should().Be(ReasoningEffort.None);
    }

    [Fact]
    public async Task The_shared_chat_options_are_never_mutated_by_a_run()
    {
        RecordingProvider first = new(stored: "xhigh");
        ColoristAgent agent = CreateColorist(first, new Dictionary<string, string?>());

        await agent.RunAsync("go");
        ChatOptions? afterFirst = first.Sent;
        first.Stored = null;
        await agent.RunAsync("go");

        afterFirst!.Reasoning!.Effort.Should().Be(ReasoningEffort.ExtraHigh);
        first.Sent!.Reasoning!.Effort.Should().Be(ReasoningEffort.None, "the second run must not inherit the first run's stored value");
    }

    [Fact]
    public void An_invalid_configured_value_still_fails_at_construction()
    {
        // "high" is not in the vocabulary: a self-hosted Qwen template would 500 on it.
        Action construct = () => CreateColorist(
            new RecordingProvider(stored: null),
            new Dictionary<string, string?> { ["Agents:Colorist:ReasoningEffort"] = "high" });

        construct.Should().Throw<InvalidOperationException>().WithMessage("*high*");
    }

    [Fact]
    public async Task The_cost_routing_wrapper_forwards_the_stored_effort_to_the_provider_it_wraps()
    {
        Mock<IAgentChatClientProvider> inner = new();
        inner.Setup(p => p.ResolveReasoningEffortAsync(AgentType.Colorist, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<string?>("medium"));
        ReelBolt.WorkflowEngine.Execution.Decision.CostRouting.CostRoutingChatClientProvider wrapper = new(
            inner.Object,
            Mock.Of<IInferenceProviderResolver>(),
            Mock.Of<IChatClientFactory>(),
            new ReelBolt.WorkflowEngine.Execution.Decision.CostRouting.CostRoutingAmbientState(),
            NullLogger<ReelBolt.WorkflowEngine.Execution.Decision.CostRouting.CostRoutingChatClientProvider>.Instance);

        (await wrapper.ResolveReasoningEffortAsync(AgentType.Colorist, null, default)).Should().Be("medium",
            "the interface default is null, so an unforwarded wrapper would silently drop every stored value");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_api_store_loads_built_ins_and_only_this_organizations_custom_agents(bool asNoOrganization)
    {
        using InferenceApiDbContext db = CreateDb();
        Guid mine = Guid.NewGuid();
        Guid theirs = Guid.NewGuid();
        AgentDefinition builtIn = Row(AgentType.AuthorAgent, isBuiltIn: true, organizationId: null, "low");
        AgentDefinition myCustom = Row(AgentType.Custom, isBuiltIn: false, mine, "none");
        AgentDefinition theirCustom = Row(AgentType.Custom, isBuiltIn: false, theirs, "xhigh");
        db.AgentDefinitions.AddRange(builtIn, myCustom, theirCustom);
        await db.SaveChangesAsync();

        AgentReasoningEfforts loaded = await new ReelBolt.Inference.Api.Services.Inference.InferenceApiProviderStore(db)
            .LoadAgentReasoningEffortsAsync(asNoOrganization ? null : mine, default);

        loaded.ById.Should().ContainKey(builtIn.Id).WhoseValue.Should().Be("low");
        loaded.ById.ContainsKey(theirCustom.Id).Should().BeFalse("another organization's agent is never loaded");
        loaded.ById.ContainsKey(myCustom.Id).Should().Be(!asNoOrganization);
        loaded.BuiltInByType[AgentType.AuthorAgent].Should().Be("low");
        loaded.BuiltInByType.ContainsKey(AgentType.Custom).Should().BeFalse();
    }

    private static AgentDefinition Row(AgentType type, bool isBuiltIn, Guid? organizationId, string effort) => new()
    {
        Id = Guid.NewGuid(),
        Name = type + Guid.NewGuid().ToString("N"),
        Description = "d",
        SystemPrompt = "p",
        AgentType = type,
        IsBuiltIn = isBuiltIn,
        OrganizationId = organizationId,
        ReasoningEffort = effort
    };

    // ---- plumbing -----------------------------------------------------------------------------------------

    private static async Task RunColoristAsync(
        RecordingProvider provider, Dictionary<string, string?> config, Guid? definitionId = null)
    {
        ColoristAgent agent = CreateColorist(provider, config);
        await agent.RunAsync("go", definitionId);
    }

    private static ColoristAgent CreateColorist(RecordingProvider provider, Dictionary<string, string?> config)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        Mock<IAgentToolProvider> tools = new();
        tools.Setup(t => t.GetTools(It.IsAny<AgentType>())).Returns([]);
        Mock<ISkillAgentToolsFactory> skills = new();
        skills.Setup(s => s.CreateBoundTools(It.IsAny<IReadOnlyList<ReelBolt.Shared.Skills.SkillDescriptor>>())).Returns([]);

        return new ColoristAgent(provider, configuration, tools.Object, skills.Object);
    }

    private static InferenceApiDbContext CreateDb() => new(
        new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static InferenceProviderResolver CreateResolver(AgentReasoningEfforts stored)
    {
        ServiceCollection services = new();
        services.AddSingleton<IInferenceProviderStore>(new StoreWithEfforts(stored));

        return new InferenceProviderResolver(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            new Passthrough(),
            NullLogger<InferenceProviderResolver>.Instance);
    }

    private sealed class StoreWithEfforts(AgentReasoningEfforts stored) : IInferenceProviderStore
    {
        public Task<IReadOnlyList<ModelCapabilityView>> LoadEnabledAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelCapabilityView>>([]);

        public Task<IReadOnlyDictionary<Guid, Guid?>> LoadAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, Guid?>>(new Dictionary<Guid, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadOrgAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<AgentType, Guid?>>(new Dictionary<AgentType, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadBuiltInAgentOverridesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<AgentType, Guid?>>(new Dictionary<AgentType, Guid?>());

        public Task<AgentReasoningEfforts> LoadAgentReasoningEffortsAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult(stored);
    }

    private sealed class Passthrough : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public bool TryUnprotect(string? ciphertext, out string plaintext)
        {
            plaintext = ciphertext ?? string.Empty;
            return ciphertext != null;
        }
    }

    /// <summary>An <see cref="IAgentChatClientProvider"/> whose client records the options it was called with.</summary>
    private sealed class RecordingProvider(string? stored) : IAgentChatClientProvider
    {
        public string? Stored { get; set; } = stored;
        public bool ThrowOnLookup { get; init; }
        public ChatOptions? Sent { get; private set; }
        public (AgentType, Guid?)? LookedUp { get; private set; }

        public ValueTask<IChatClient> GetAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct) =>
            ValueTask.FromResult<IChatClient>(new Recorder(this));

        public ValueTask<string?> ResolveReasoningEffortAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct)
        {
            LookedUp = (agentType, agentDefinitionId);
            return ThrowOnLookup ? throw new InvalidOperationException("store down") : ValueTask.FromResult(Stored);
        }

        private sealed class Recorder(RecordingProvider owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                owner.Sent = options;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}
