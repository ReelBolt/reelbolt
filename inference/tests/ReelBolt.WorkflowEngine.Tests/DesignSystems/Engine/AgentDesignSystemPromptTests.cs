using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.DesignSystems;
using ReelBolt.WorkflowEngine.Services.Styles;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Engine;

/// <summary>
/// P2-17 end-to-end coverage: a pinned run's design and product blocks reach the model as the
/// agent's own instructions, after the style block, and a run with no design-system state sends the
/// style-only prompt byte for byte.
/// </summary>
public class AgentDesignSystemPromptTests
{
    private const string BasePrompt = "You write the promo script.";

    private static readonly TokenProvenance Prov = new("manual", null, null, null);

    private const string ProductJson =
        """
        {
          "schema": "reelbolt.product-map/1",
          "framework": { "name": "next", "version": "15.1", "language": "typescript", "styling": [], "packageManager": "pnpm", "projectType": "web app" },
          "routes": [{ "path": "/", "file": "app/page.tsx", "kind": "page", "requiresAuth": false }],
          "components": [{ "name": "Button", "path": "components/ui/Button.tsx", "responsibility": "Primary button.", "keyProps": ["variant"], "core": true }],
          "dependencies": { "runtime": [], "dev": [], "ui": [] }
        }
        """;

    private sealed class CapturingChatClient : IChatClient
    {
        public List<string> Seen { get; } = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Seen.Add(string.Join("\n", messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text)) + "\n" + options?.Instructions);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class TestAgent(IAgentChatClientProvider chatClients, IConfiguration configuration, ISkillAgentToolsFactory skills)
        : ReelBoltAgentBase(chatClients, configuration, "DesignSystemTestAgent", "test", AgentType.ScriptwriterAgent, BasePrompt, skills);

    [Fact]
    public async Task A_pinned_run_appends_design_and_product_blocks()
    {
        (TestAgent agent, CapturingChatClient client) = Create();
        DesignSystemRunState state = await LoadStateAsync();

        using (DesignSystemAmbient.Enter(state))
        {
            await agent.RunAsync("write it");
        }

        string instructions = client.Seen.Should().ContainSingle().Subject;
        instructions.Should().Contain("[DESIGN SYSTEM v1]");
        instructions.Should().Contain("[PRODUCT PROFILE v1]");
        instructions.IndexOf("[DESIGN SYSTEM", StringComparison.Ordinal)
            .Should().BeLessThan(instructions.IndexOf("[PRODUCT PROFILE", StringComparison.Ordinal));
        agent.SystemPrompt.Should().Be(BasePrompt, "the singleton agent's own prompt is never mutated");
    }

    [Fact]
    public async Task An_unpinned_run_sends_the_prompt_byte_identical_to_style_alone()
    {
        (TestAgent agent, CapturingChatClient client) = Create();

        using (DesignSystemAmbient.Enter(null))
        {
            await agent.RunAsync("write it");
        }

        string expected = RunStyleAmbient.Decorate(BasePrompt, AgentType.ScriptwriterAgent);
        string instructions = client.Seen.Should().ContainSingle().Subject;
        instructions.Should().EndWith(expected);
        instructions.Should().NotContain("[DESIGN SYSTEM");
        instructions.Should().NotContain("[PRODUCT PROFILE");
    }

    private static (TestAgent Agent, CapturingChatClient Client) Create()
    {
        var client = new CapturingChatClient();
        var provider = new Mock<IAgentChatClientProvider>();
        provider.Setup(p => p.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<IChatClient>(client));
        IConfiguration configuration = new ConfigurationBuilder().Build();
        return (new TestAgent(provider.Object, configuration, Mock.Of<ISkillAgentToolsFactory>()), client);
    }

    private static async Task<DesignSystemRunState> LoadStateAsync()
    {
        using WorkflowEngineDbContext db = NewDb();
        DesignSystemPin pin = await SeedAsync(db);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => db);
        services.AddScoped<PinnedDocumentLoader>();
        services.Configure<DesignSystemAmbientOptions>(options =>
        {
            options.Enabled = true;
            options.DesignMaxChars = 2400;
            options.ProductMaxChars = 3200;
        });
        using ServiceProvider sp = services.BuildServiceProvider();

        DesignSystemRunState? state = await DesignSystemRunState.LoadAsync(
            new WorkflowExecution { DesignSystemPinJson = DesignSystemPinJson.Serialize(pin) },
            sp,
            CancellationToken.None);

        state.Should().NotBeNull();
        return state!;
    }

    private static async Task<DesignSystemPin> SeedAsync(WorkflowEngineDbContext db)
    {
        JsonObject design = DesignDoc();
        string designJson = CanonicalJson.Serialize(design);
        string designDigest = CanonicalJson.Digest(design);

        string mapJson = CanonicalJson.Serialize(JsonNode.Parse(ProductJson));
        string mapDigest = CanonicalJson.Sha256Hex(mapJson);

        Guid designId = Guid.NewGuid();
        Guid productId = Guid.NewGuid();
        db.DesignSystemVersions.Add(Version(designId, 1, designJson, designDigest));
        db.DesignSystemVersions.Add(Version(productId, 1, mapJson, mapDigest));
        await db.SaveChangesAsync();

        return new DesignSystemPin(
            1,
            new DesignSystemPinMember(designId, 1, designDigest),
            new DesignSystemPinMember(productId, 1, mapDigest));
    }

    private static WorkflowEngineDbContext NewDb() =>
        new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DesignSystemVersion Version(Guid id, int number, string json, string digest) => new()
    {
        Id = id,
        DesignSystemId = Guid.NewGuid(),
        VersionNumber = number,
        State = DesignVersionState.Approved,
        DocumentJson = json,
        DocumentDigest = digest,
        Origin = DesignVersionOrigin.Manual,
        CreatedByUserId = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
    };

    private static JsonObject DesignDoc()
    {
        JsonObject doc = DesignDocument.New("Acme");
        DesignDocument.SetToken(doc, "color.brand.primary", "color", new JsonObject { ["hex"] = "#6d28d9" }, Prov, 0.95);
        DesignDocument.SetToken(doc, "typography.family.heading", "fontFamily", new JsonArray("Inter"), Prov, 1.0);
        return doc;
    }
}
