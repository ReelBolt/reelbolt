using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.PlatformDocs;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Tests.Agents;
using ReelBolt.WorkflowEngine.Tests.Controllers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.InferenceApiReliability;

/// <summary>
/// Findings: the assistant sat on "Searching the documentation…" for minutes (the docs search waited
/// on an embedding server busy re-indexing, with no bound, and the label never changed afterwards);
/// background summarization competed with it on the chat provider; and an unanswered question
/// vanished from view after navigating away.
/// </summary>
public class AssistantResponsivenessTests
{
    private static readonly Guid Owner = Guid.NewGuid();

    private static InferenceApiDbContext Db() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>An index that never answers until cancelled — an embedding server busy re-indexing.</summary>
    private sealed class HangingDocsSearch : IPlatformDocsSearch
    {
        public async Task<PlatformDocsSearchResult> SearchAsync(string query, int limit, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    // ---------------------------------------------------------------- bounded docs search

    [Fact]
    public async Task A_hanging_docs_search_times_out_with_guidance_and_the_label_moves_on()
    {
        using InferenceApiDbContext db = Db();
        var tools = new AssistantKnowledgeTools(
            db, new HangingDocsSearch(), AssistantToolTestFactory.TemplateProvisioning(db),
            Options.Create(new PlatformDocsOptions { SearchTimeoutSeconds = 1 }));
        AIFunction search = tools.GetTools(new AssistantToolContext(Owner)).Single(t => t.Name == "SearchPlatformDocs");

        var captions = new List<string>();
        AIFunction wrapped = AssistantToolProgress.WithProgress(
            search,
            (caption, _) => { captions.Add(caption); return Task.CompletedTask; },
            (caption, _) => { captions.Add(caption); return Task.CompletedTask; });

        var stopwatch = Stopwatch.StartNew();
        object? raw = await wrapped.InvokeAsync(new AIFunctionArguments { ["query"] = "how do captions work" });
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the search is bounded, never an indefinite wait");

        string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw!.ToString()!;
        using JsonDocument doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("available").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("status").GetString().Should().Be("timeout");
        doc.RootElement.GetProperty("guidance").GetString().Should().Contain("Answer from what you already know");

        captions.Should().Equal("Searching the documentation", "Docs index still building — answering without it");
    }

    [Fact]
    public async Task A_docs_index_still_building_tells_the_model_to_carry_on_without_it()
    {
        using InferenceApiDbContext db = Db();
        var tools = new AssistantKnowledgeTools(
            db,
            new FakePlatformDocsSearch(new PlatformDocsSearchResult(PlatformDocsSearchStatus.NotIndexed, [], "still indexing")),
            AssistantToolTestFactory.TemplateProvisioning(db));
        AIFunction search = tools.GetTools(new AssistantToolContext(Owner)).Single(t => t.Name == "SearchPlatformDocs");

        object? raw = await search.InvokeAsync(new AIFunctionArguments { ["query"] = "x" });

        AssistantKnowledgeTools.IsUnavailableSearchResult(raw).Should().BeTrue();
        AssistantToolProgress.CaptionAfter("SearchPlatformDocs", raw).Should().Be("Docs index still building — answering without it");
    }

    [Fact]
    public void After_any_other_tool_the_label_returns_to_thinking()
    {
        AssistantToolProgress.CaptionAfter("ListProjects", "[]").Should().Be("Thinking");
        AssistantToolProgress.CaptionAfter("SearchPlatformDocs", """{"available":true,"results":[]}""").Should().Be("Thinking");
    }

    // ---------------------------------------------------------------- interactive priority

    [Fact]
    public async Task Background_work_does_not_wait_when_nobody_is_chatting()
    {
        var monitor = new AssistantActivityMonitor();
        (await monitor.YieldToInteractiveAsync(TimeSpan.FromSeconds(30), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Background_work_waits_for_an_assistant_turn_and_resumes_when_it_ends()
    {
        var monitor = new AssistantActivityMonitor();
        IDisposable turn = monitor.BeginTurn();
        monitor.IsInteractiveBusy.Should().BeTrue();

        Task<bool> waiting = monitor.YieldToInteractiveAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(300);
        waiting.IsCompleted.Should().BeFalse("summarization yields while someone is waiting for an answer");

        turn.Dispose();
        turn.Dispose(); // idempotent
        (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        monitor.ActiveTurns.Should().Be(0);
    }

    [Fact]
    public async Task The_yield_is_bounded_so_a_long_chat_never_starves_background_work()
    {
        var monitor = new AssistantActivityMonitor();
        using IDisposable turn = monitor.BeginTurn();

        var stopwatch = Stopwatch.StartNew();
        await monitor.YieldToInteractiveAsync(TimeSpan.FromMilliseconds(400), CancellationToken.None);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    // ---------------------------------------------------------------- thread history truthfulness

    private sealed class FakeUser(Guid id) : ICurrentUser
    {
        public Guid UserId { get; } = id;
        public string Email => "owner@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public System.Guid? OrganizationId => ReelBolt.WorkflowEngine.Tests.TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    private static AssistantThread AddThread(InferenceApiDbContext db, string title, params string[] roles)
    {
        var thread = new AssistantThread
        {
            Id = Guid.NewGuid(),
            UserId = Owner,
            OrganizationId = TestTenancy.OrgOf(Owner),
            Title = title,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.AssistantThreads.Add(thread);
        for (int i = 0; i < roles.Length; i++)
        {
            db.AssistantMessages.Add(new AssistantMessage
            {
                Id = Guid.NewGuid(),
                ThreadId = thread.Id,
                Role = roles[i],
                Content = $"{roles[i]} {i}",
                Sequence = i + 1,
                CreatedAt = DateTime.UtcNow
            });
        }
        db.SaveChanges();
        return thread;
    }

    [Fact]
    public async Task The_thread_list_tells_an_unanswered_question_from_one_still_being_answered()
    {
        using InferenceApiDbContext db = Db();
        AssistantThread answered = AddThread(db, "answered", "user", "assistant");
        AssistantThread died = AddThread(db, "died", "user");
        AssistantThread running = AddThread(db, "running", "user");

        var registry = new AssistantTurnRegistry();
        using IDisposable _ = registry.Begin(running.Id);

        var controller = new AssistantThreadsController(db, new FakeUser(Owner), TestTenancy.Access(db, new FakeUser(Owner)), registry);
        var rows = ((IEnumerable<AssistantThreadSummaryDto>)((OkObjectResult)(await controller.ListThreads(CancellationToken.None)).Result!).Value!)
            .ToDictionary(r => r.Id);

        rows[answered.Id].AwaitingReply.Should().BeFalse();
        rows[answered.Id].ReplyInProgress.Should().BeFalse();
        rows[died.Id].AwaitingReply.Should().BeTrue("its request died, so the panel offers to ask again");
        rows[died.Id].ReplyInProgress.Should().BeFalse();
        rows[running.Id].AwaitingReply.Should().BeFalse();
        rows[running.Id].ReplyInProgress.Should().BeTrue();

        var detail = (AssistantThreadDetailDto)((OkObjectResult)(await controller.GetThread(running.Id, CancellationToken.None)).Result!).Value!;
        detail.ReplyInProgress.Should().BeTrue();
    }

    [Fact]
    public async Task Asking_an_unanswered_question_again_does_not_duplicate_it()
    {
        using InferenceApiDbContext db = Db();
        var thread = new AssistantThread { Id = Guid.NewGuid(), UserId = Owner, OrganizationId = TestTenancy.OrgOf(Owner), Title = "Make me a reel", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.AssistantThreads.Add(thread);
        db.AssistantMessages.Add(new AssistantMessage { Id = Guid.NewGuid(), ThreadId = thread.Id, Role = "user", Content = "Make me a reel", Sequence = 1, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();

        (AssistantChatController controller, _) = AssistantChatControllerTests.CreateController(
            db, Owner, AssistantChatControllerTests.ChatProviderReturning("Here is a plan."));

        await controller.ChatStream(new AssistantChatRequest(thread.Id, "Make me a reel"), CancellationToken.None);

        List<AssistantMessage> turns = db.AssistantMessages.Where(m => m.ThreadId == thread.Id).OrderBy(m => m.Sequence).ToList();
        turns.Select(t => (t.Role, t.Content, t.Sequence)).Should().Equal(
            ("user", "Make me a reel", 1),
            ("assistant", "Here is a plan.", 2));
    }

    [Fact]
    public async Task A_reply_is_still_saved_when_the_browser_leaves_mid_turn()
    {
        using InferenceApiDbContext db = Db();
        var thread = new AssistantThread { Id = Guid.NewGuid(), UserId = Owner, OrganizationId = TestTenancy.OrgOf(Owner), Title = "New chat", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.AssistantThreads.Add(thread);
        db.SaveChanges();

        var modelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseModel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chatClient = new Mock<IChatClient>();
        chatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>(async (msgs, _, token) =>
            {
                List<ChatMessage> list = msgs.ToList();
                if (list.Count > 0 && list[0].Role == ChatRole.System && list[0].Text.StartsWith("You name chat conversations", StringComparison.Ordinal))
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Reel plan"));

                modelStarted.TrySetResult();
                await releaseModel.Task.WaitAsync(token);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Finished answer"));
            });
        var provider = new Mock<IAgentChatClientProvider>();
        provider.Setup(p => p.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(chatClient.Object);

        (AssistantChatController controller, _) = AssistantChatControllerTests.CreateController(db, Owner, provider.Object);
        var registry = new AssistantTurnRegistry();
        var monitor = new AssistantActivityMonitor();
        IServiceProvider inner = controller.HttpContext.RequestServices;
        controller.HttpContext.RequestServices = new ExtraServices(inner, registry, monitor);

        using var browser = new CancellationTokenSource();
        Task turn = controller.ChatStream(new AssistantChatRequest(thread.Id, "Make me a reel"), browser.Token);

        await modelStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        registry.IsInFlight(thread.Id).Should().BeTrue();
        monitor.IsInteractiveBusy.Should().BeTrue();

        browser.Cancel(); // the customer navigated away
        releaseModel.SetResult();
        await turn.WaitAsync(TimeSpan.FromSeconds(10));

        db.AssistantMessages.Where(m => m.ThreadId == thread.Id).OrderBy(m => m.Sequence)
            .Select(m => m.Role + ":" + m.Content).ToList()
            .Should().Equal("user:Make me a reel", "assistant:Finished answer");
        registry.IsInFlight(thread.Id).Should().BeFalse();
        monitor.IsInteractiveBusy.Should().BeFalse();
    }

    private sealed class ExtraServices(IServiceProvider inner, AssistantTurnRegistry registry, AssistantActivityMonitor monitor) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(AssistantTurnRegistry) ? registry
            : serviceType == typeof(AssistantActivityMonitor) ? monitor
            : inner.GetService(serviceType);
    }
}
