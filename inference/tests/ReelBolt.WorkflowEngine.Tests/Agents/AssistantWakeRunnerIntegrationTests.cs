#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Controllers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// The REAL <see cref="AssistantWakeTurnRunner"/> end to end: a fresh DI scope, a principal built from the database,
/// the real <see cref="AssistantChatController"/> writing to a discarding response, a scripted model, a counting charge service.
/// </summary>
public class AssistantWakeRunnerIntegrationTests
{
    /// <summary>Production-like composition: the chat test DI underneath, with the request-scoped identity services built from the runner's own HttpContext.</summary>
    private sealed class Host : IServiceScopeFactory
    {
        public required IServiceProvider Inner { get; init; }
        public required Billing.FakeNonWorkflowCharges Charges { get; init; }
        public ICurrentUser? Current { get; private set; }
        public IProjectAccessService? Access { get; private set; }

        /// <summary>What ICurrentUser read from the runner's principal while the turn ran (the HttpContext is gone afterwards).</summary>
        public (Guid User, Guid? Org, OrganizationRole? Role, string Email)? Seen { get; set; }

        public IServiceScope CreateScope()
        {
            IServiceScope inner = Inner.CreateScope();
            var accessor = new HttpContextAccessor();
            var db = inner.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            var current = new CurrentUser(accessor);
            Current = current;
            Access = new ProjectAccessService(db, current);
            return new Scope(inner, new Composite(inner.ServiceProvider, accessor, current, Access, Charges, seen => Seen = seen));
        }

        private sealed class Scope(IServiceScope inner, IServiceProvider provider) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = provider;
            public void Dispose() => inner.Dispose();
        }

        private sealed class Composite(IServiceProvider inner, IHttpContextAccessor accessor, ICurrentUser current, IProjectAccessService access, INonWorkflowChargeService charges, Action<(Guid, Guid?, OrganizationRole?, string)> onSeen)
            : IServiceProvider, IServiceScopeFactory
        {
            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(IHttpContextAccessor)) return accessor;
                if (serviceType == typeof(ICurrentUser)) return current;
                if (serviceType == typeof(IProjectAccessService)) return access;
                if (serviceType == typeof(INonWorkflowChargeService))
                {
                    onSeen((current.UserId, current.OrganizationId, current.OrgRole, current.Email));
                    return charges;
                }

                if (serviceType == typeof(IServiceScopeFactory)) return this;
                return inner.GetService(serviceType);
            }

            public IServiceScope CreateScope() => throw new NotSupportedException();
        }
    }

    private sealed record World(Host Host, AssistantWakeTurnRunner Runner, InferenceApiDbContext Db, Guid User, Project Project, AssistantThread Thread, AssistantTurnHub Hub, AssistantApprovalBroker Broker);

    private static World Build(IAgentChatClientProvider model, bool auto = false, IConfiguration? configuration = null)
    {
        // Two contexts over one in-memory store: the DI scope owns (and disposes) one, the test reads through the other.
        var options = new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new InferenceApiDbContext(options);
        var diDb = new InferenceApiDbContext(options);
        Guid user = Guid.NewGuid();
        db.ApplicationUsers.Add(new ApplicationUser { Id = user, Email = "w@example.com", DisplayName = "W", AssistantAutoApprove = auto, EmailVerifiedAt = DateTime.UtcNow });
        var project = new Project { Id = Guid.NewGuid(), Name = "Launch Teaser", OwnerId = user, OrganizationId = TestTenancy.OrgOf(user), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        var thread = new AssistantThread { Id = Guid.NewGuid(), UserId = user, OrganizationId = TestTenancy.OrgOf(user), ProjectId = project.Id };
        db.AssistantThreads.Add(thread);
        db.SaveChanges();

        var hub = new AssistantTurnHub();
        (AssistantChatController c, _) = AssistantChatControllerTests.CreateController(diDb, user, model, configuration: configuration, hub: hub);
        IServiceProvider root = c.HttpContext.RequestServices;
        var host = new Host { Inner = root, Charges = new Billing.FakeNonWorkflowCharges() };
        return new World(host, new AssistantWakeTurnRunner(host), db, user, project, thread, hub, root.GetRequiredService<AssistantApprovalBroker>());
    }

    private static AssistantRunWatch Watch(World w) => new()
    {
        Id = Guid.NewGuid(),
        ThreadId = w.Thread.Id,
        ExecutionId = Guid.NewGuid(),
        UserId = w.User,
        OrganizationId = TestTenancy.OrgOf(w.User),
        ProjectId = w.Project.Id,
        WorkflowName = "Film"
    };

    private static WakeIdentity Identity(World w, OrganizationRole role = OrganizationRole.Admin) =>
        new(w.User, "w@example.com", TestTenancy.OrgOf(w.User), role, false, true);

    private static Task<WakeOutcome> Run(World w, AssistantRunWatch watch, OrganizationRole role = OrganizationRole.Admin)
    {
        var run = new AssistantWakeRun(watch.ExecutionId, ExecutionStatus.Passed, "Film");
        return w.Runner.RunAsync(watch, Identity(w, role), AssistantRunWakeText.Build(watch.ExecutionId, ExecutionStatus.Passed), run, CancellationToken.None);
    }

    private static IAgentChatClientProvider Scripted(params Func<IReadOnlyList<ChatMessage>, ChatMessage>[] steps)
    {
        int call = 0;
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((messages, _, _) =>
            {
                List<ChatMessage> shown = messages.ToList();
                if (shown.Count > 0 && shown[0].Role == ChatRole.System && shown[0].Text.StartsWith("You name chat conversations", StringComparison.Ordinal))
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "A title")));
                int i = Interlocked.Increment(ref call) - 1;
                return Task.FromResult(new ChatResponse(steps[Math.Min(i, steps.Length - 1)](shown)));
            });
        var provider = new Mock<IAgentChatClientProvider>();
        provider.Setup(p => p.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
        return provider.Object;
    }

    private static Func<IReadOnlyList<ChatMessage>, ChatMessage> Says(string t) => _ => new ChatMessage(ChatRole.Assistant, t);

    private static Func<IReadOnlyList<ChatMessage>, ChatMessage> Calls(string tool, Func<IReadOnlyList<ChatMessage>, Dictionary<string, object?>> args) =>
        shown => new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), tool, args(shown))]);

    private static string Token(IReadOnlyList<ChatMessage> shown)
    {
        object? r = shown.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last().Result;
        string text = r is JsonElement { ValueKind: JsonValueKind.String } e ? e.GetString()! : r is JsonElement j ? j.GetRawText() : r?.ToString() ?? "";
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.EnumerateObject().First(p => string.Equals(p.Name, "confirmationToken", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
    }

    [Fact]
    public async Task The_runner_persists_the_reply_after_the_wake_turn_holds_and_settles_once_and_the_principal_reaches_the_identity_services()
    {
        World w = Build(Scripted(Says("The run passed; carrying on.")));
        AssistantRunWatch watch = Watch(w);

        WakeOutcome outcome = await Run(w, watch, OrganizationRole.Admin);

        outcome.Kind.Should().Be(WakeOutcomeKind.Fired);
        List<AssistantMessage> messages = w.Db.AssistantMessages.OrderBy(m => m.Sequence).ToList();
        messages.Select(m => m.Role).Should().Equal("user", "assistant");
        messages[0].Content.Should().Be(AssistantRunWakeText.Build(watch.ExecutionId, ExecutionStatus.Passed));
        messages[0].MetadataJson.Should().Contain("run_wake");
        messages[1].Content.Should().Be("The run passed; carrying on.");
        outcome.MessageId.Should().Be(messages[0].Id);

        // The identity the turn ran as is the one built from the database, not the stored watch.
        w.Host.Seen.Should().Be((w.User, (Guid?)TestTenancy.OrgOf(w.User), (OrganizationRole?)OrganizationRole.Admin, "w@example.com"));

        // One hold, settled once; the live turn is a run_wake turn in the hub.
        w.Host.Charges.Turns.Should().ContainSingle();
        w.Host.Charges.TurnSettlements.Should().ContainSingle();
        w.Host.Charges.TurnReleases.Should().BeEmpty();
        AssistantLiveTurn live = w.Hub.Find(w.Thread.Id)!;
        live.Kind.Should().Be("run_wake");
        live.State.Should().Be(AssistantTurnState.Completed);
    }

    [Fact]
    public async Task A_project_the_user_can_no_longer_view_cancels_the_runner_with_no_turn()
    {
        World w = Build(Scripted(Says("x")));
        AssistantRunWatch watch = Watch(w);
        w.Db.Projects.Remove(w.Project);
        w.Db.SaveChanges();

        WakeOutcome outcome = await Run(w, watch);

        (outcome.Kind, outcome.Reason).Should().Be((WakeOutcomeKind.Cancelled, "project_gone"));
        w.Db.AssistantMessages.Should().BeEmpty();
        w.Host.Charges.Turns.Should().BeEmpty();
    }

    [Fact]
    public async Task In_ask_mode_a_wake_turn_waits_on_a_card_that_expires_and_the_write_never_runs()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Assistant:ApprovalTimeoutSeconds"] = "1" }).Build();
        World ask = BuildWithDelete(auto: false, config);

        Task<WakeOutcome> turn = Run(ask, Watch(ask));
        bool sawCard = false;
        for (int i = 0; i < 400 && !turn.IsCompleted; i++)
        {
            if (ask.Broker.OpenCount > 0) { sawCard = true; break; }
            await Task.Delay(10);
        }

        WakeOutcome outcome = await turn.WaitAsync(TimeSpan.FromSeconds(30));

        sawCard.Should().BeTrue("ask mode puts the write behind a card even with nobody attached");
        (await ask.Db.Projects.AnyAsync(p => p.Id == ask.Project.Id)).Should().BeTrue("the card expired, so the delete never ran");
        ask.Broker.OpenCount.Should().Be(0);
        outcome.Kind.Should().Be(WakeOutcomeKind.Fired);
    }

    [Fact]
    public async Task In_auto_mode_a_wake_turn_runs_the_write_with_no_card()
    {
        World ask = BuildWithDelete(auto: true, null);

        WakeOutcome outcome = await Run(ask, Watch(ask));

        outcome.Kind.Should().Be(WakeOutcomeKind.Fired);
        (await ask.Db.Projects.AnyAsync(p => p.Id == ask.Project.Id)).Should().BeFalse("auto mode is the user's own setting and a wake turn follows it");
        ask.Broker.OpenCount.Should().Be(0);
    }

    // The script must name the project of the world it runs in, so the world is built first and the model reads it lazily.
    private static World BuildWithDelete(bool auto, IConfiguration? config)
    {
        Project? project = null;
        IAgentChatClientProvider model = Scripted(
            Calls("ProposeDeleteProject", _ => new() { ["projectId"] = project!.Id.ToString() }),
            Calls("ApplyDeleteProject", shown => new() { ["projectId"] = project!.Id.ToString(), ["confirmationToken"] = Token(shown) }),
            Says("Done."));
        World w = Build(model, auto, config);
        project = w.Project;
        return w;
    }

    [Fact]
    public async Task The_assistants_chat_client_never_carries_a_second_tool_loop()
    {
        IChatClient inner = new Mock<IChatClient>().Object;
        var provider = new Mock<IAgentChatClientProvider>();
        provider.Setup(p => p.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(inner);
        AssistantTurnGuard guard = AssistantTurnGuard.ForCall(null, null, null, null, new ServiceCollection().BuildServiceProvider(), new AssistantToolContext(Guid.NewGuid()));

        IChatClient client = await guard.Wrap(provider.Object, AssistantChatLimits.MaxToolIterations(null)).GetAsync(AgentType.Assistant, null, default);

        // Exactly one FunctionInvokingChatClient is in the chain, and it is the capped one: walk the delegating chain and count.
        int loops = 0;
        for (IChatClient? c = client; c is not null; c = (c as DelegatingChatClient)?.GetType().GetProperty("InnerClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(c) as IChatClient)
        {
            if (c is FunctionInvokingChatClient) loops++;
        }

        loops.Should().Be(1);
        client.GetService<FunctionInvokingChatClient>()!.MaximumIterationsPerRequest.Should().Be(80);
    }
}
