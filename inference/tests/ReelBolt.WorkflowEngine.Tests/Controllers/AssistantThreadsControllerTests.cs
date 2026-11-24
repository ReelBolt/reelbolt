using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Controllers;

/// <summary>
/// Tests for AssistantThreadsController: every read and write is caller-scoped, and a thread that
/// belongs to somebody else is reported as 404 — never 403, which would confirm it exists.
/// </summary>
public class AssistantThreadsControllerTests
{
    private static InferenceApiDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new InferenceApiDbContext(options);
    }

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public System.Guid? OrganizationId => ReelBolt.WorkflowEngine.Tests.TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    private static AssistantThreadsController CreateController(InferenceApiDbContext db, Guid userId) =>
        new(db, new FakeCurrentUser(userId), TestTenancy.Access(db, new FakeCurrentUser(userId)));

    private static AssistantThread AddThread(
        InferenceApiDbContext db,
        Guid userId,
        string title = "New chat",
        Guid? projectId = null,
        DateTime? updatedAt = null,
        params (string Role, string Content)[] turns)
    {
        var thread = new AssistantThread
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrganizationId = TestTenancy.OrgOf(userId),
            ProjectId = projectId,
            Title = title,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = updatedAt ?? DateTime.UtcNow
        };

        int sequence = 1;
        foreach ((string role, string content) in turns)
        {
            thread.Messages.Add(new AssistantMessage
            {
                Id = Guid.NewGuid(),
                ThreadId = thread.Id,
                Role = role,
                Content = content,
                Sequence = sequence++
            });
        }

        db.AssistantThreads.Add(thread);
        db.SaveChanges();
        return thread;
    }

    [Fact]
    public async Task GetThreads_ReturnsOnlyTheCallersThreads()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();

        AssistantThread mine = AddThread(db, callerId, "Mine", turns: new[] { ("user", "hi") });
        AddThread(db, otherUserId, "Theirs");

        AssistantThreadsController controller = CreateController(db, callerId);

        ActionResult<IReadOnlyList<AssistantThreadSummaryDto>> result = await controller.ListThreads(CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        var summaries = Assert.IsAssignableFrom<IReadOnlyList<AssistantThreadSummaryDto>>(ok.Value);

        AssistantThreadSummaryDto only = Assert.Single(summaries);
        Assert.Equal(mine.Id, only.Id);
        Assert.Equal("Mine", only.Title);
    }

    [Fact]
    public async Task GetThreads_OrdersByMostRecentlyUpdated_AndHidesArchivedThreads()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();

        AssistantThread older = AddThread(db, callerId, "Older", updatedAt: DateTime.UtcNow.AddHours(-2), turns: new[] { ("user", "a") });
        AssistantThread newer = AddThread(db, callerId, "Newer", updatedAt: DateTime.UtcNow.AddHours(-1), turns: new[] { ("user", "b") });
        AssistantThread archived = AddThread(db, callerId, "Archived", updatedAt: DateTime.UtcNow, turns: new[] { ("user", "c") });
        archived.ArchivedAt = DateTime.UtcNow;
        db.SaveChanges();

        AssistantThreadsController controller = CreateController(db, callerId);

        ActionResult<IReadOnlyList<AssistantThreadSummaryDto>> result = await controller.ListThreads(CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        var summaries = Assert.IsAssignableFrom<IReadOnlyList<AssistantThreadSummaryDto>>(ok.Value);

        Assert.Equal(new[] { newer.Id, older.Id }, summaries.Select(s => s.Id));
        Assert.DoesNotContain(summaries, s => s.Id == archived.Id);
    }

    [Fact]
    public async Task GetThread_ReturnsMessagesInSequenceOrder()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        AssistantThread thread = AddThread(
            db,
            callerId,
            turns: new[] { ("user", "first"), ("assistant", "second"), ("user", "third") });

        AssistantThreadsController controller = CreateController(db, callerId);

        ActionResult<AssistantThreadDetailDto> result = await controller.GetThread(thread.Id, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        var detail = Assert.IsType<AssistantThreadDetailDto>(ok.Value);

        Assert.Equal(thread.Id, detail.Id);
        Assert.Equal(new[] { "first", "second", "third" }, detail.Messages.Select(m => m.Content));
        Assert.Equal(new[] { 1, 2, 3 }, detail.Messages.Select(m => m.Sequence));
        Assert.Equal(new[] { "user", "assistant", "user" }, detail.Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task GetThread_Returns404_WhenThreadBelongsToAnotherUser()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        AssistantThread theirs = AddThread(db, Guid.NewGuid(), "Theirs", turns: new[] { ("user", "secret") });

        AssistantThreadsController controller = CreateController(db, callerId);

        ActionResult<AssistantThreadDetailDto> result = await controller.GetThread(theirs.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetThread_Returns404_WhenThreadDoesNotExist()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();

        AssistantThreadsController controller = CreateController(db, callerId);

        ActionResult<AssistantThreadDetailDto> result = await controller.GetThread(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpdateThread_Returns404_WhenThreadBelongsToAnotherUser()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        AssistantThread theirs = AddThread(db, Guid.NewGuid(), "Theirs");

        AssistantThreadsController controller = CreateController(db, callerId);

        ActionResult<AssistantThreadSummaryDto> result =
            await controller.UpdateThread(theirs.Id, new UpdateAssistantThreadRequest(Title: "Hijacked"), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Equal("Theirs", db.AssistantThreads.Single(t => t.Id == theirs.Id).Title);
    }

    [Fact]
    public async Task DeleteThread_Returns404_WhenThreadBelongsToAnotherUser()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        AssistantThread theirs = AddThread(db, Guid.NewGuid(), "Theirs");

        AssistantThreadsController controller = CreateController(db, callerId);

        IActionResult result = await controller.DeleteThread(theirs.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.True(db.AssistantThreads.Any(t => t.Id == theirs.Id));
    }

    [Fact]
    public async Task CreateThread_PersistsAThreadOwnedByTheCaller()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();

        AssistantThreadsController controller = CreateController(db, callerId);

        ActionResult<CreatedAssistantThreadResponse> result =
            await controller.CreateThread(new CreateAssistantThreadRequest(), CancellationToken.None);

        CreatedAtActionResult created201 = Assert.IsType<CreatedAtActionResult>(result.Result);
        var created = Assert.IsType<CreatedAssistantThreadResponse>(created201.Value);

        AssistantThread thread = db.AssistantThreads.Single();
        Assert.Equal(created.Id, thread.Id);
        Assert.Equal(callerId, thread.UserId);
        Assert.Equal("New chat", thread.Title);
        Assert.Null(thread.ProjectId);
    }

    [Fact]
    public async Task CreateThread_KeepsAProjectTheCallerOwns_AndDropsOneTheyDoNot()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        var owned = new Project { Id = Guid.NewGuid(), Name = "Mine", OwnerId = callerId, OrganizationId = TestTenancy.OrgOf(callerId) };
        var foreign = new Project { Id = Guid.NewGuid(), Name = "Theirs", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() };
        db.Projects.AddRange(owned, foreign);
        db.SaveChanges();

        AssistantThreadsController controller = CreateController(db, callerId);

        CreatedAtActionResult ownedOk = Assert.IsType<CreatedAtActionResult>(
            (await controller.CreateThread(new CreateAssistantThreadRequest(ProjectId: owned.Id), CancellationToken.None)).Result);
        db.AssistantMessages.Add(new AssistantMessage { Id = Guid.NewGuid(), ThreadId = Assert.IsType<CreatedAssistantThreadResponse>(ownedOk.Value).Id, Role = "user", Content = "x", Sequence = 1 });
        db.SaveChanges();
        CreatedAtActionResult foreignOk = Assert.IsType<CreatedAtActionResult>(
            (await controller.CreateThread(new CreateAssistantThreadRequest(ProjectId: foreign.Id), CancellationToken.None)).Result);

        Guid ownedThreadId = Assert.IsType<CreatedAssistantThreadResponse>(ownedOk.Value).Id;
        Guid foreignThreadId = Assert.IsType<CreatedAssistantThreadResponse>(foreignOk.Value).Id;
        Assert.Equal(owned.Id, db.AssistantThreads.Single(t => t.Id == ownedThreadId).ProjectId);
        // A foreign project is dropped, not rejected: the thread is still created and usable.
        Assert.Null(db.AssistantThreads.Single(t => t.Id == foreignThreadId).ProjectId);
    }

    [Fact]
    public async Task CreateThread_ReusesTheCallersEmptyThread_AndSweepsStaleEmptyOnes_AndListHidesEmpties()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        AssistantThread stale = AddThread(db, callerId, updatedAt: DateTime.UtcNow.AddHours(-5));
        AssistantThread empty = AddThread(db, callerId, updatedAt: DateTime.UtcNow.AddMinutes(-1));
        AssistantThreadsController controller = CreateController(db, callerId);

        CreatedAtActionResult first = Assert.IsType<CreatedAtActionResult>(
            (await controller.CreateThread(null, CancellationToken.None)).Result);
        CreatedAtActionResult second = Assert.IsType<CreatedAtActionResult>(
            (await controller.CreateThread(null, CancellationToken.None)).Result);

        Assert.Equal(empty.Id, Assert.IsType<CreatedAssistantThreadResponse>(first.Value).Id);
        Assert.Equal(empty.Id, Assert.IsType<CreatedAssistantThreadResponse>(second.Value).Id);
        Assert.False(db.AssistantThreads.Any(t => t.Id == stale.Id));

        var list = Assert.IsAssignableFrom<IReadOnlyList<AssistantThreadSummaryDto>>(
            Assert.IsType<OkObjectResult>((await controller.ListThreads(CancellationToken.None)).Result).Value);
        Assert.Empty(list);
    }

    [Fact]
    public async Task UpdateThread_RenamesAndArchives()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        AssistantThread thread = AddThread(db, callerId, "Old title");

        AssistantThreadsController controller = CreateController(db, callerId);

        ActionResult<AssistantThreadSummaryDto> renamed =
            await controller.UpdateThread(thread.Id, new UpdateAssistantThreadRequest(Title: "New title"), CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(renamed.Result);
        Assert.Equal("New title", Assert.IsType<AssistantThreadSummaryDto>(ok.Value).Title);

        await controller.UpdateThread(thread.Id, new UpdateAssistantThreadRequest(Archived: true), CancellationToken.None);
        Assert.NotNull(db.AssistantThreads.Single(t => t.Id == thread.Id).ArchivedAt);

        // An archive-only patch must not disturb the title set by the previous rename.
        Assert.Equal("New title", db.AssistantThreads.Single(t => t.Id == thread.Id).Title);
    }

    [Fact]
    public async Task DeleteThread_RemovesTheThreadAndItsMessages()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        AssistantThread thread = AddThread(
            db, callerId, turns: new[] { ("user", "hi"), ("assistant", "hello") });

        AssistantThreadsController controller = CreateController(db, callerId);

        IActionResult result = await controller.DeleteThread(thread.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.False(db.AssistantThreads.Any(t => t.Id == thread.Id));
        Assert.False(db.AssistantMessages.Any(m => m.ThreadId == thread.Id));
    }

    private static AssistantThreadsController CreateTurnController(
        InferenceApiDbContext db, Guid userId, AssistantTurnHub hub, bool fallback)
    {
        var c = new AssistantThreadsController(
            db, new FakeCurrentUser(userId), TestTenancy.Access(db, new FakeCurrentUser(userId)), null, hub);
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        if (fallback)
            http.Request.Headers["X-ReelBolt-Slot-Fallback"] = "1";
        c.ControllerContext = new ControllerContext { HttpContext = http };
        return c;
    }

    [Fact]
    public async Task GetTurn_NoLiveTurn_WithFallbackHeader_Returns404WithIdleBody()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        AssistantThread thread = AddThread(db, callerId);
        var controller = CreateTurnController(db, callerId, new AssistantTurnHub(), fallback: true);

        var result = await controller.GetTurn(thread.Id, CancellationToken.None);

        var nf = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal("idle", Assert.IsType<AssistantTurnStateDto>(nf.Value).State);
    }

    [Fact]
    public async Task GetTurn_NoLiveTurn_WithoutHeader_Returns200Idle()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        AssistantThread thread = AddThread(db, callerId);
        var controller = CreateTurnController(db, callerId, new AssistantTurnHub(), fallback: false);

        var result = await controller.GetTurn(thread.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("idle", Assert.IsType<AssistantTurnStateDto>(ok.Value).State);
    }

    [Fact]
    public async Task GetTurn_LiveTurn_WithFallbackHeader_Returns200()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        AssistantThread thread = AddThread(db, callerId);
        var hub = new AssistantTurnHub();
        hub.Begin(thread.Id, callerId, TestTenancy.OrgOf(callerId));
        var controller = CreateTurnController(db, callerId, hub, fallback: true);

        var result = await controller.GetTurn(thread.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotEqual("idle", Assert.IsType<AssistantTurnStateDto>(ok.Value).State);
    }

    [Fact]
    public async Task GetTurn_ForeignThread_WithFallbackHeader_IsPlain404()
    {
        using InferenceApiDbContext db = CreateContext();
        AssistantThread thread = AddThread(db, Guid.NewGuid());
        Guid other = Guid.NewGuid();
        var controller = CreateTurnController(db, other, new AssistantTurnHub(), fallback: true);

        var result = await controller.GetTurn(thread.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
