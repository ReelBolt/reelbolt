using ReelBolt.Shared.Workflows;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Tests.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Controllers;

/// <summary>
/// Regression tests for the adversarial review of the permission gate. The critical finding: the card builder
/// matched argument names case-insensitively and the tool binder matches them exactly, so a single mis-cased
/// key made the card describe one action while the tool performed another — or made the builder find "no
/// target", which used to mean "no card, just run it". Each test below is one of the reproductions, run through
/// the real chat endpoint and the real function-calling loop.
/// </summary>
public class AssistantApprovalHardeningTests
{
    private delegate ChatMessage ModelStep(IReadOnlyList<ChatMessage> shown);

    private static IAgentChatClientProvider ScriptedModel(params ModelStep[] steps)
    {
        int call = 0;
        var client = new Mock<IChatClient>();
        client
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((messages, _, _) =>
            {
                List<ChatMessage> shown = messages.ToList();
                if (shown.Count > 0 && shown[0].Role == ChatRole.System
                    && shown[0].Text.StartsWith("You name chat conversations", StringComparison.Ordinal))
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "A title")));

                int index = Interlocked.Increment(ref call) - 1;
                return Task.FromResult(new ChatResponse(steps[Math.Min(index, steps.Length - 1)](shown)));
            });

        var provider = new Mock<IAgentChatClientProvider>();
        provider
            .Setup(p => p.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        return provider.Object;
    }

    private static ModelStep Calls(string tool, Func<IReadOnlyList<ChatMessage>, Dictionary<string, object?>> arguments) =>
        shown => new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), tool, arguments(shown))]);

    private static ModelStep Says(string text) => _ => new ChatMessage(ChatRole.Assistant, text);

    private static string LastToolResult(IReadOnlyList<ChatMessage> shown)
    {
        object? result = shown.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last().Result;
        return result switch
        {
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString()!,
            JsonElement e => e.GetRawText(),
            _ => result?.ToString() ?? string.Empty
        };
    }

    private static string TokenIn(string toolResult)
    {
        using JsonDocument doc = JsonDocument.Parse(toolResult);
        return doc.RootElement.EnumerateObject()
            .First(p => string.Equals(p.Name, "confirmationToken", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
    }

    private sealed class PassGuard : IAssistantExecutionGuard
    {
        public async Task<RunRateDecision> TryAcquireAsync(Guid userId, Guid projectId, RunRateBucket bucket, string source, Func<Task<Guid?>> insertCallback, CancellationToken ct)
        {
            await insertCallback();
            return new RunRateDecision(true, bucket, 1, 10, 3600, 0);
        }
    }

    private sealed class RealToolsController : AssistantChatController
    {
        private readonly InferenceApiDbContext _db;
        private readonly ICurrentUser _user;
        private readonly IWorkflowExecutionService _execution;

        public RealToolsController(InferenceApiDbContext db, ICurrentUser user, IAgentChatClientProvider provider, IWorkflowExecutionService execution)
            : base(db, user, provider, TestTenancy.Access(db, user))
        {
            _db = db;
            _user = user;
            _execution = execution;
        }

        protected override AssistantToolProvider CreateToolProvider(
            IWorkflowEditDiffService diffService, IWorkflowExecutionService executionService, IAssistantExecutionGuard executionGuard) =>
            AssistantToolTestFactory.CreateProvider(_db, _user, executionGuard: new PassGuard(), executionService: _execution);
    }

    private sealed record Turn(
        AssistantChatController Controller, MemoryStream Body, AssistantThread Thread, AssistantApprovalBroker Broker, Guid UserId)
    {
        public List<JsonElement> Actions() =>
            Encoding.UTF8.GetString(Body.ToArray()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.StartsWith("data: ", StringComparison.Ordinal) && !l.StartsWith("data: [DONE]", StringComparison.Ordinal))
                .Select(l => JsonSerializer.Deserialize<JsonElement>(l["data: ".Length..]))
                .Where(f => f.GetProperty("type").GetString() == "data-assistantAction")
                .Select(f => f.GetProperty("data"))
                .ToList();

        public async Task<JsonElement> WaitForPendingAsync()
        {
            for (int i = 0; i < 1000; i++)
            {
                foreach (JsonElement action in Actions())
                    if (action.GetProperty("state").GetString() == "pending")
                        return action;
                await Task.Delay(10);
            }

            throw new TimeoutException("No card arrived: " + Encoding.UTF8.GetString(Body.ToArray()));
        }

        public void Answer(JsonElement card, bool allow) =>
            Broker.Answer(card.GetProperty("id").GetGuid(), UserId, TestTenancy.OrgOf(UserId), allow, alwaysAllow: false);
    }

    private static Turn NewTurn(InferenceApiDbContext db, Guid user, IAgentChatClientProvider model, IWorkflowExecutionService? execution = null)
    {
        db.ApplicationUsers.Add(new ApplicationUser { Id = user, Email = "t@example.com", DisplayName = "T", AssistantAutoApprove = false });
        var thread = new AssistantThread
        {
            Id = Guid.NewGuid(),
            UserId = user,
            OrganizationId = TestTenancy.OrgOf(user),
            Title = "New chat",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.AssistantThreads.Add(thread);
        db.SaveChanges();

        (AssistantChatController controller, MemoryStream body) = AssistantChatControllerTests.CreateController(
            db, user, model, new RealToolsController(db, new FakeCurrentUser(user), model, execution ?? Mock.Of<IWorkflowExecutionService>()));
        return new Turn(controller, body, thread, controller.HttpContext.RequestServices.GetRequiredService<AssistantApprovalBroker>(), user);
    }

    private static Project SeedProject(InferenceApiDbContext db, Guid owner, string name = "Launch Teaser")
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = name,
            OwnerId = owner,
            OrganizationId = TestTenancy.OrgOf(owner),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.SaveChanges();
        return project;
    }

    // ---------------------------------------------------------------------
    // Reproduction A: a wrongly-cased key used to skip the card entirely
    // ---------------------------------------------------------------------

    [Fact]
    public async Task A_wrongly_cased_project_key_cannot_skip_the_card_or_redirect_the_action()
    {
        using InferenceApiDbContext db = AssistantChatControllerTests.CreateContext();
        Guid owner = Guid.NewGuid();
        Project project = SeedProject(db, owner);
        Turn turn = NewTurn(db, owner, ScriptedModel(
            Calls("ProposeDeleteProject", _ => new() { ["projectId"] = project.Id.ToString() }),
            Calls("ApplyDeleteProject", shown => new()
            {
                // The builder used to read this (a project that does not exist), find nothing and return "no
                // card"; the tool ignored it (exact-name binding), fell back to the page's project and deleted it.
                ["ProjectId"] = Guid.NewGuid().ToString(),
                ["confirmationToken"] = TokenIn(LastToolResult(shown))
            }),
            Says("done")));

        Task chat = turn.Controller.ChatStream(new AssistantChatRequest(turn.Thread.Id, "delete it", ProjectId: project.Id), CancellationToken.None);

        JsonElement card = await turn.WaitForPendingAsync();
        card.GetProperty("title").GetString().Should().Be("Delete the project “Launch Teaser”");
        (await db.Projects.AnyAsync(p => p.Id == project.Id)).Should().BeTrue("nothing is deleted while the card waits");

        turn.Answer(card, allow: false);
        await chat.WaitAsync(TimeSpan.FromSeconds(20));
        (await db.Projects.AnyAsync(p => p.Id == project.Id)).Should().BeTrue("the user said no");
    }

    // ---------------------------------------------------------------------
    // Reproduction B: duplicate case-variants used to start a paid run with no card
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Duplicate_case_variants_of_a_key_cannot_start_a_run_without_a_card()
    {
        using InferenceApiDbContext db = AssistantChatControllerTests.CreateContext();
        Guid owner = Guid.NewGuid();
        Project project = SeedProject(db, owner);
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Product reel",
            ProjectId = project.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.WorkflowDefinitions.Add(workflow);
        db.SaveChanges();

        int runs = 0;
        var execution = new Mock<IWorkflowExecutionService>();
        execution
            .Setup(e => e.ExecuteAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<ExecutionInitiator>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<RunChoices?>()))
            .Callback(() => runs++)
            .ReturnsAsync((WorkflowExecutionResponse)null!);

        Turn turn = NewTurn(db, owner, ScriptedModel(
            Calls("ExecuteWorkflow", _ => new()
            {
                ["WorkflowId"] = Guid.NewGuid().ToString(), // the builder used to read this one and find "nothing to approve"
                ["workflowId"] = workflow.Id.ToString(),    // ...while the tool bound this one and ran it
                ["userRequest"] = null,
                ["regenerateVideoClips"] = false
            }),
            Says("started")), execution.Object);

        Task chat = turn.Controller.ChatStream(new AssistantChatRequest(turn.Thread.Id, "run it", ProjectId: project.Id), CancellationToken.None);

        JsonElement card = await turn.WaitForPendingAsync();
        card.GetProperty("title").GetString().Should().Be("Run the workflow “Product reel”");
        runs.Should().Be(0, "the run waits for the user's answer");

        turn.Answer(card, allow: false);
        await chat.WaitAsync(TimeSpan.FromSeconds(20));
        runs.Should().Be(0);
    }

    // ---------------------------------------------------------------------
    // Reproduction C: the card named one project, the tool created another
    // ---------------------------------------------------------------------

    [Fact]
    public async Task The_card_and_the_tool_always_read_the_same_value_for_a_name()
    {
        using InferenceApiDbContext db = AssistantChatControllerTests.CreateContext();
        Guid owner = Guid.NewGuid();
        Turn turn = NewTurn(db, owner, ScriptedModel(
            Calls("ProposeCreateProject", _ => new() { ["name"] = "Real Name", ["description"] = null }),
            Calls("ApplyCreateProject", shown => new()
            {
                ["Name"] = "Harmless Name", // the card used to describe this one...
                ["name"] = "Real Name",     // ...while the tool created this one
                ["description"] = null,
                ["confirmationToken"] = TokenIn(LastToolResult(shown))
            }),
            Says("ok")));

        Task chat = turn.Controller.ChatStream(new AssistantChatRequest(turn.Thread.Id, "make a project"), CancellationToken.None);

        JsonElement card = await turn.WaitForPendingAsync();
        turn.Answer(card, allow: true);
        await chat.WaitAsync(TimeSpan.FromSeconds(20));

        string created = (await db.Projects.SingleAsync()).Name;
        card.GetProperty("title").GetString().Should().Be($"Create the project “{created}”",
            "whatever the user allowed is what was created");
    }

    // ---------------------------------------------------------------------
    // The interceptor hands the card and the tool one canonical dictionary
    // ---------------------------------------------------------------------

    private sealed class CapturingGate : IAssistantApprovalGate
    {
        public AIFunctionArguments? Seen { get; private set; }

        public void NoteProposal(string toolName, object? result) { }

        public async Task<object?> RunAsync(string toolName, AIFunctionArguments args, Func<ValueTask<object?>> run, CancellationToken ct)
        {
            Seen = args;
            return await run();
        }
    }

    [Fact]
    public async Task The_interceptor_drops_keys_that_are_not_a_parameter_name_before_the_gate_or_the_tool_see_them()
    {
        using InferenceApiDbContext db = AssistantChatControllerTests.CreateContext();
        Guid owner = Guid.NewGuid();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner));
        var gate = new CapturingGate();
        provider.ApprovalGate = gate;

        AIFunction tool = provider.GetTools(new AssistantToolContext(owner)).Single(t => t.Name == "ApplyCreateProject");
        await tool.InvokeAsync(new AIFunctionArguments
        {
            ["Name"] = "Wrong case",
            ["name"] = "Right case",
            ["description"] = null,
            ["confirmationToken"] = "x",
            ["somethingElse"] = "ignored"
        });

        gate.Seen.Should().NotBeNull();
        gate.Seen!.Keys.Should().BeEquivalentTo("name", "description", "confirmationToken");
        gate.Seen["name"].Should().Be("Right case");
    }

    [Fact]
    public async Task The_function_binder_matches_argument_names_exactly_which_the_canonical_copy_relies_on()
    {
        // A tripwire: if a future Microsoft.Extensions.AI binds names case-insensitively, the card/tool
        // agreement above is no longer implied by canonicalisation alone, and this fails first.
        using InferenceApiDbContext db = AssistantChatControllerTests.CreateContext();
        Guid owner = Guid.NewGuid();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner));
        AIFunction tool = provider.GetTools(new AssistantToolContext(owner)).Single(t => t.Name == "ProposeCreateProject");

        Func<Task> act = async () => await tool.InvokeAsync(new AIFunctionArguments { ["Name"] = "x", ["description"] = null });

        await act.Should().ThrowAsync<ArgumentException>("a mis-cased key does not satisfy a required parameter");
    }

    // ---------------------------------------------------------------------
    // Cards that show what is really being approved
    // ---------------------------------------------------------------------

    private sealed class AdminUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "admin@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => true;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Owner;
    }

    /// <summary>
    /// Fixed figures, with a <c>RequiredMillis</c> of its own (the preflight's hold, above the displayed high on
    /// a cold start), so a card that showed the range alone, or rounded the requirement down, would be caught.
    /// Every overload answers, the resolution ones included: the card prices through the preflight.
    /// </summary>
    private sealed class StubEstimator(long lowMillis, long highMillis, long requiredMillis, long fixedMaxMillis = 0) : IWorkflowCostEstimator
    {
        public bool BillingEnabled => true;

        private WorkflowCostEstimate Estimate => new WorkflowCostEstimate(true, true, lowMillis, highMillis, requiredMillis, [])
        {
            TypicalMillis = (lowMillis + highMillis) / 2,
            FixedMaxMillis = fixedMaxMillis
        };

        // An affordable run: the verdict is "allowed", and the card must still show the figures it carries.
        private WorkflowPreflightResult Allowed => new(WorkflowPreflightOutcome.Allowed, Estimate, 10_000_000);

        public Task<WorkflowCostEstimate> EstimateAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(Estimate);

        public Task<WorkflowCostEstimate> EstimateAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, int? outputResolution, CancellationToken ct = default) =>
            Task.FromResult(Estimate);

        public Task<WorkflowPreflightResult> PreflightAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(Allowed);

        public Task<WorkflowPreflightResult> PreflightAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, int? outputResolution, CancellationToken ct = default) =>
            Task.FromResult(Allowed);

        public Task<WorkflowCostEstimate> EstimateChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            throw new NotSupportedException("the card prices a whole workflow");

        public Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            throw new NotSupportedException("the card prices a whole workflow");
    }

    private static AIFunctionArguments Args(params (string Key, object? Value)[] values)
    {
        var args = new AIFunctionArguments();
        foreach ((string key, object? value) in values)
            args[key] = value;
        return args;
    }

    private static (InferenceApiDbContext db, Guid user, Project project, AssistantApprovalCardBuilder builder) Builder(
        ICurrentUser? user = null, IWorkflowCostEstimator? estimator = null)
    {
        InferenceApiDbContext db = AssistantChatControllerTests.CreateContext();
        Guid id = user?.UserId ?? Guid.NewGuid();
        ICurrentUser caller = user ?? new FakeCurrentUser(id);
        Project project = SeedProject(db, id);
        return (db, id, project, new AssistantApprovalCardBuilder(db, TestTenancy.Access(db, caller), caller, estimator));
    }

    [Fact]
    public async Task A_provider_update_card_shows_where_requests_and_the_stored_key_will_go()
    {
        Guid admin = Guid.NewGuid();
        var (db, _, project, builder) = Builder(new AdminUser(admin));
        using (db)
        {
            var provider = new ModelCapabilityView
            {
                Id = Guid.NewGuid(),
                Name = "Main chat",
                Kind = InferenceProviderKind.OpenAICompatible,
                Capability = InferenceProviderCapability.Chat,
                Endpoint = "https://old.example",
                ModelName = "m",
                OrganizationId = null,
                CreatedAt = DateTime.UtcNow
            };
            db.AddFlat(provider);
            await db.SaveChangesAsync();

            ApprovalVerdict verdict = await builder.BuildAsync(
                "ApplyUpdateInferenceProvider",
                Args(("providerId", provider.Id), ("endpoint", "https://evil.example/v1/chat?token=abc"), ("isDefault", true), ("apiKey", (object?)null)),
                new AssistantToolContext(admin, project.Id), proposal: null, CancellationToken.None);

            ApprovalCard card = verdict.Card!;
            card.Title.Should().Be("Change the AI service connection “Main chat”");
            card.Changes!.Select(c => c.Text).Should().Contain("Endpoint: https://evil.example",
                "an admin must see where the stored key will be sent — host only, never the path or query");
            card.Changes!.Select(c => c.Text).Should().Contain("Becomes the default connection for its purpose");
            string.Join(" ", card.Changes!.Select(c => c.Text)).Should().NotContain("token=abc");
        }
    }

    [Fact]
    public async Task A_provider_update_card_names_the_other_models_an_endpoint_or_key_change_also_moves()
    {
        Guid admin = Guid.NewGuid();
        var (db, _, project, builder) = Builder(new AdminUser(admin));
        using (db)
        {
            Guid account = Guid.NewGuid();
            ModelCapabilityView chat = new()
            {
                Id = Guid.NewGuid(),
                ProviderId = account,
                ProviderName = "Shared account",
                Name = "Main chat",
                Kind = InferenceProviderKind.OpenAICompatible,
                Capability = InferenceProviderCapability.Chat,
                Endpoint = "https://old.example",
                ModelName = "m1",
                CreatedAt = DateTime.UtcNow
            };
            ModelCapabilityView embeddings = new()
            {
                Id = Guid.NewGuid(),
                ProviderId = account,
                ProviderName = "Shared account",
                Name = "Search index",
                Kind = InferenceProviderKind.OpenAICompatible,
                Capability = InferenceProviderCapability.Embedding,
                Endpoint = "https://old.example",
                ModelName = "m2",
                CreatedAt = DateTime.UtcNow
            };
            db.AddFlat(chat, embeddings);
            await db.SaveChangesAsync();

            ApprovalCard keyCard = (await builder.BuildAsync("ApplyUpdateInferenceProvider",
                Args(("providerId", chat.Id), ("apiKey", "sk-rotated")), new AssistantToolContext(admin, project.Id), null, CancellationToken.None)).Card!;
            ApprovalCard endpointCard = (await builder.BuildAsync("ApplyUpdateInferenceProvider",
                Args(("providerId", chat.Id), ("endpoint", "https://new.example")), new AssistantToolContext(admin, project.Id), null, CancellationToken.None)).Card!;
            ApprovalCard defaultCard = (await builder.BuildAsync("ApplyUpdateInferenceProvider",
                Args(("providerId", chat.Id), ("isDefault", true)), new AssistantToolContext(admin, project.Id), null, CancellationToken.None)).Card!;

            keyCard.Changes!.Select(c => c.Text).Should().Contain(t => t.Contains("“Search index”"),
                "the key belongs to the account, so the admin must see every model it also changes");
            endpointCard.Changes!.Select(c => c.Text).Should().Contain(t => t.Contains("“Search index”"));
            defaultCard.Changes!.Select(c => c.Text).Should().NotContain(t => t.Contains("Search index"),
                "a capability-level change moves nothing else");
        }
    }

    /// <summary>Two models ("Main chat", "Search index") under one platform account; the chat one is returned.</summary>
    private static async Task<(ModelCapabilityView Chat, ModelCapabilityView Embeddings)> SharedAccountAsync(
        InferenceApiDbContext db, bool accountEnabled = true)
    {
        Guid account = Guid.NewGuid();
        ModelCapabilityView chat = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = account,
            ProviderName = "Shared account",
            Name = "Main chat",
            Kind = InferenceProviderKind.OpenAICompatible,
            Capability = InferenceProviderCapability.Chat,
            Endpoint = "https://old.example",
            ModelName = "m1",
            CreatedAt = DateTime.UtcNow
        };
        ModelCapabilityView embeddings = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = account,
            ProviderName = "Shared account",
            Name = "Search index",
            Kind = InferenceProviderKind.OpenAICompatible,
            Capability = InferenceProviderCapability.Embedding,
            Endpoint = "https://old.example",
            ModelName = "m2",
            CreatedAt = DateTime.UtcNow
        };
        db.AddFlat(chat, embeddings);
        await db.SaveChangesAsync();
        if (!accountEnabled)
        {
            db.TrackedCapability(chat.Id).Model.Provider.IsEnabled = false;
            await db.SaveChangesAsync();
        }

        return (chat, embeddings);
    }

    [Fact]
    public async Task A_provider_update_card_shows_timeout_and_oauth_changes_and_every_account_level_change_names_the_siblings()
    {
        Guid admin = Guid.NewGuid();
        var (db, _, project, builder) = Builder(new AdminUser(admin));
        using (db)
        {
            (ModelCapabilityView chat, _) = await SharedAccountAsync(db, accountEnabled: false);
            AssistantToolContext context = new(admin, project.Id);

            async Task<List<string>> Lines(params (string Key, object? Value)[] args) =>
                (await builder.BuildAsync("ApplyUpdateInferenceProvider", Args([("providerId", chat.Id), .. args]), context, null,
                    CancellationToken.None)).Card!.Changes!.Select(c => c.Text).ToList();

            List<string> timeout = await Lines(("timeoutSeconds", 45));
            List<string> oauthOn = await Lines(("personalOAuthMode", true));
            List<string> oauthOff = await Lines(("personalOAuthMode", false));
            List<string> switchedOn = await Lines(("isEnabled", true));
            List<string> switchedOff = await Lines(("isEnabled", false));

            timeout.Should().Contain("Request timeout: 45 seconds");
            oauthOn.Should().Contain(t => t.Contains("own sign-in") && !t.Contains("instead of each"));
            oauthOff.Should().Contain(t => t.Contains("stored key"));
            foreach (List<string> card in new[] { timeout, oauthOn, oauthOff })
                card.Should().Contain(t => t.Contains("“Search index”"), "the timeout and the OAuth mode belong to the account");
            switchedOn.Should().Contain(t => t.Contains("“Search index”") && t.Contains("account"),
                "switching a capability on switches its account on, which brings back every model under it");
            switchedOff.Should().NotContain(t => t.Contains("Search index"), "switching off touches only this capability");
            AssistantAdminTools.AccountChanges(null, null, null, null, isEnabled: true).Should().BeTrue(
                "the card and the tool decide with one predicate");
            AssistantAdminTools.AccountChanges(null, null, null, null, isEnabled: false).Should().BeFalse();
        }
    }

    /// <summary>The kind belongs to the provider account (the tool counts a kind change as account-level): the card says so.</summary>
    [Fact]
    public async Task A_provider_update_card_for_a_kind_change_names_the_models_sharing_the_account()
    {
        Guid admin = Guid.NewGuid();
        var (db, _, project, builder) = Builder(new AdminUser(admin));
        using (db)
        {
            (ModelCapabilityView chat, _) = await SharedAccountAsync(db);
            AssistantToolContext context = new(admin, project.Id);

            List<string> lines = (await builder.BuildAsync("ApplyUpdateInferenceProvider",
                    Args(("providerId", chat.Id), ("kind", "Anthropic")), context, null, CancellationToken.None))
                .Card!.Changes!.Select(c => c.Text).ToList();

            lines.Should().Contain(t => t.Contains("“Search index”"), "a kind change changes every model under the account");
        }
    }

    [Fact]
    public async Task A_provider_update_card_for_a_name_or_model_change_names_the_models_other_capabilities()
    {
        Guid admin = Guid.NewGuid();
        var (db, _, project, builder) = Builder(new AdminUser(admin));
        using (db)
        {
            ModelCapability chat = db.AddFlat(new ModelCapabilityView
            {
                Id = Guid.NewGuid(),
                Name = "Claude",
                Kind = InferenceProviderKind.Anthropic,
                Capability = InferenceProviderCapability.Chat,
                Endpoint = "https://api.anthropic.com",
                ModelName = "claude",
                CreatedAt = DateTime.UtcNow
            });
            db.Add(new ModelCapability
            {
                Id = Guid.NewGuid(),
                ModelId = chat.ModelId,
                OrganizationId = null,
                Capability = InferenceProviderCapability.Vision,
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            AssistantToolContext context = new(admin, project.Id);

            async Task<List<string>> Lines(params (string Key, object? Value)[] args) =>
                (await builder.BuildAsync("ApplyUpdateInferenceProvider", Args([("providerId", chat.Id), .. args]), context, null,
                    CancellationToken.None)).Card!.Changes!.Select(c => c.Text).ToList();

            (await Lines(("name", "Claude 2"))).Should().Contain(t => t.Contains("Vision"),
                "the name is the model's, so its Vision use is renamed too");
            (await Lines(("modelName", "claude-2"))).Should().Contain(t => t.Contains("Vision"));
            (await Lines(("isDefault", true))).Should().NotContain(t => t.Contains("Vision"));
        }
    }

    [Fact]
    public async Task A_provider_delete_card_says_the_account_and_its_stored_key_go_with_the_last_model()
    {
        Guid admin = Guid.NewGuid();
        var (db, _, project, builder) = Builder(new AdminUser(admin));
        using (db)
        {
            (ModelCapabilityView chat, _) = await SharedAccountAsync(db);
            ModelCapabilityView alone = new()
            {
                Id = Guid.NewGuid(),
                Name = "Lonely",
                Kind = InferenceProviderKind.OpenAICompatible,
                Capability = InferenceProviderCapability.Transcription,
                Endpoint = "https://asr.example",
                ModelName = "whisper",
                ApiKeyEncrypted = "cipher",
                ApiKeyLastFour = "1234",
                CreatedAt = DateTime.UtcNow
            };
            db.AddFlat(alone);
            await db.SaveChangesAsync();
            AssistantToolContext context = new(admin, project.Id);

            ApprovalCard last = (await builder.BuildAsync("ApplyDeleteInferenceProvider", Args(("providerId", alone.Id)), context, null,
                CancellationToken.None)).Card!;
            ApprovalCard shared = (await builder.BuildAsync("ApplyDeleteInferenceProvider", Args(("providerId", chat.Id)), context, null,
                CancellationToken.None)).Card!;

            last.Summary.Should().Contain("account").And.Contain("stored key");
            shared.Summary.Should().NotContain("stored key", "the account still serves “Search index”");
        }
    }

    [Fact]
    public async Task Provider_cards_never_describe_the_system_compute_capability()
    {
        Guid admin = Guid.NewGuid();
        var (db, _, project, builder) = Builder(new AdminUser(admin));
        using (db)
        {
            db.AddFlat(new ModelCapabilityView
            {
                Id = WellKnownModelCatalog.ComputeCapabilityId,
                ProviderId = WellKnownModelCatalog.ComputeProviderId,
                ProviderName = WellKnownModelCatalog.ComputeProviderName,
                Name = WellKnownModelCatalog.ComputeModelDisplayName,
                Kind = InferenceProviderKind.OpenAICompatible,
                Capability = InferenceProviderCapability.Compute,
                Endpoint = string.Empty,
                ModelName = WellKnownModelCatalog.ComputeModelCode,
                IsSystem = true,
                IsDefault = true,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            AssistantToolContext context = new(admin, project.Id);

            foreach (string tool in new[] { "ApplyUpdateInferenceProvider", "ApplyDeleteInferenceProvider" })
            {
                ApprovalVerdict verdict = await builder.BuildAsync(tool,
                    Args(("providerId", WellKnownModelCatalog.ComputeCapabilityId), ("name", "x")), context, null, CancellationToken.None);
                verdict.Card.Should().BeNull(tool);
                verdict.Refusal.Should().NotBeNull(tool);
            }
        }
    }

    [Fact]
    public async Task A_provider_create_card_under_an_existing_account_names_the_account_instead_of_an_endpoint()
    {
        Guid admin = Guid.NewGuid();
        var (db, _, project, builder) = Builder(new AdminUser(admin));
        using (db)
        {
            ModelCapabilityView existing = new()
            {
                Id = Guid.NewGuid(),
                ProviderId = Guid.NewGuid(),
                ProviderName = "Anthropic account",
                Name = "Claude",
                Kind = InferenceProviderKind.Anthropic,
                Capability = InferenceProviderCapability.Chat,
                Endpoint = "https://api.anthropic.com",
                ModelName = "claude",
                CreatedAt = DateTime.UtcNow
            };
            db.AddFlat(existing);
            await db.SaveChangesAsync();

            ApprovalCard card = (await builder.BuildAsync("ApplyCreateInferenceProvider",
                Args(("name", "Claude vision"), ("providerId", existing.ProviderId), ("capability", "Vision"), ("modelName", "claude")),
                new AssistantToolContext(admin, project.Id), null, CancellationToken.None)).Card!;
            ApprovalVerdict unknown = await builder.BuildAsync("ApplyCreateInferenceProvider",
                Args(("name", "x"), ("providerId", Guid.NewGuid())), new AssistantToolContext(admin, project.Id), null, CancellationToken.None);

            card.Changes!.Select(c => c.Text).Should().Contain(t => t.Contains("“Anthropic account”"));
            card.Changes!.Select(c => c.Text).Should().NotContain(t => t.StartsWith("Endpoint"));
            unknown.Card.Should().BeNull();
            unknown.Refusal.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task A_provider_card_for_a_caller_who_is_not_a_platform_admin_is_refused_not_run()
    {
        var (db, user, project, builder) = Builder();
        using (db)
        {
            ApprovalVerdict verdict = await builder.BuildAsync(
                "ApplyCreateInferenceProvider", Args(("name", "X")), new AssistantToolContext(user, project.Id), null, CancellationToken.None);

            verdict.Card.Should().BeNull();
            verdict.Answer.Should().BeNull();
            verdict.Refusal.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task A_run_card_shows_the_estimated_cost_for_an_affordable_run_and_calls_it_paid()
    {
        var (db, user, project, builder) = Builder(estimator: new StubEstimator(lowMillis: 8_000, highMillis: 12_000, requiredMillis: 15_200));
        using (db)
        {
            var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Product reel", ProjectId = project.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.WorkflowDefinitions.Add(workflow);
            await db.SaveChangesAsync();

            ApprovalVerdict verdict = await builder.BuildAsync(
                AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflow.Id)),
                new AssistantToolContext(user, project.Id), null, CancellationToken.None);

            verdict.Card!.Cost.Should().Be("needs up to 16 credits (usually about 10)");
            verdict.Card.Risk.Should().Be(AssistantActionRisk.Paid);
        }
    }

    [Fact]
    public async Task A_run_card_adds_the_generated_video_ceiling_and_calls_a_fixed_price_only_run_paid()
    {
        foreach ((long low, long high, long required, string expected) in new[]
        {
            (8_000L, 12_000L, 175_200L, "needs up to 176 credits (usually about 10), plus up to 160 for generated video"),
            (0L, 0L, 160_000L, "needs up to 160 credits, including up to 160 for generated video")
        })
        {
            var (db, user, project, builder) = Builder(estimator: new StubEstimator(low, high, required, fixedMaxMillis: 160_000));
            using (db)
            {
                var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "B-roll", ProjectId = project.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
                db.WorkflowDefinitions.Add(workflow);
                await db.SaveChangesAsync();

                ApprovalVerdict verdict = await builder.BuildAsync(
                    AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflow.Id)),
                    new AssistantToolContext(user, project.Id), null, CancellationToken.None);

                verdict.Card!.Cost.Should().Be(expected);
                verdict.Card.Risk.Should().Be(AssistantActionRisk.Paid);
            }
        }
    }

    [Fact]
    public async Task A_run_of_a_workflow_that_makes_paid_clips_is_flagged_even_when_reusing_them()
    {
        var (db, user, project, builder) = Builder();
        using (db)
        {
            var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "With clips", ProjectId = project.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoGenerate });
            db.WorkflowDefinitions.Add(workflow);
            await db.SaveChangesAsync();

            ApprovalVerdict verdict = await builder.BuildAsync(
                AssistantApprovalCatalog.ExecuteWorkflowTool, Args(("workflowId", workflow.Id), ("regenerateVideoClips", false)),
                new AssistantToolContext(user, project.Id), null, CancellationToken.None);

            verdict.Card!.Risk.Should().Be(AssistantActionRisk.Paid, "first-time generation spends money even when the answer is 'reuse'");
            verdict.Card.Changes!.Select(c => c.Text).Should().Contain(t => t.Contains("paid"));
        }
    }

    [Fact]
    public async Task A_workflow_edit_card_says_when_the_workflow_is_also_renamed_and_what_kind_each_step_is()
    {
        var (db, user, project, builder) = Builder();
        using (db)
        {
            var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Product reel", ProjectId = project.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.WorkflowDefinitions.Add(workflow);
            await db.SaveChangesAsync();
            var proposal = new ProposalRecord("ProposeWorkflow", JsonDocument.Parse(
                """{"additions":[{"stepOrder":3,"stepType":"VideoGenerate","summary":"Review"}],"removals":[],"modifications":[],"confirmationToken":"t"}""").RootElement.Clone());

            ApprovalVerdict verdict = await builder.BuildAsync(
                "ApplyWorkflow", Args(("workflowId", workflow.Id), ("name", "Totally different"), ("confirmationToken", "t")),
                new AssistantToolContext(user, project.Id), proposal, CancellationToken.None);

            IEnumerable<string> lines = verdict.Card!.Changes!.Select(c => c.Text);
            lines.Should().Contain("Rename the workflow to “Totally different”");
            lines.Should().Contain("Step 3: Review (video generate)", "a paid step is never hidden behind an innocent label");
        }
    }

    [Fact]
    public async Task A_name_cannot_break_out_of_its_quotes_and_pose_as_part_of_the_sentence()
    {
        InferenceApiDbContext db = AssistantChatControllerTests.CreateContext();
        using (db)
        {
            Guid owner = Guid.NewGuid();
            Project project = SeedProject(db, owner, "Teaser” and also delete “Everything");
            var user = new FakeCurrentUser(owner);
            var builder = new AssistantApprovalCardBuilder(db, TestTenancy.Access(db, user), user);

            ApprovalVerdict verdict = await builder.BuildAsync(
                "ApplyDeleteProject", Args(("projectId", project.Id)), new AssistantToolContext(owner, project.Id), null, CancellationToken.None);

            verdict.Card!.Title.Should().Be("Delete the project “Teaser' and also delete 'Everything”");
        }
    }

    [Fact]
    public async Task Changing_a_projects_description_to_nothing_says_that_the_description_goes()
    {
        var (db, user, project, builder) = Builder();
        using (db)
        {
            project.Description = "A 30 second teaser";
            await db.SaveChangesAsync();

            ApprovalVerdict verdict = await builder.BuildAsync(
                "ApplyUpdateProject", Args(("projectId", project.Id), ("name", project.Name), ("description", (object?)null)),
                new AssistantToolContext(user, project.Id), null, CancellationToken.None);

            verdict.Card!.Summary.Should().Be("The project's description will be removed.");
        }
    }

    [Fact]
    public async Task A_paid_picture_run_with_no_proposal_to_read_stays_paid()
    {
        var (db, user, project, builder) = Builder();
        using (db)
        {
            ApprovalVerdict verdict = await builder.BuildAsync(
                "ApplyRunPictureTool", Args(("tool", "upscale"), ("documentId", Guid.NewGuid()), ("confirmationToken", "t")),
                new AssistantToolContext(user, project.Id), proposal: null, CancellationToken.None);

            verdict.Card!.Risk.Should().Be(AssistantActionRisk.Paid, "no proposal means no reason to believe it is free");
        }
    }
}
