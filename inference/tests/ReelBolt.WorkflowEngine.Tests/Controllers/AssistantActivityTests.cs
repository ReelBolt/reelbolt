#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Controllers;

/// <summary>The background-activity endpoint and the thread read marker.</summary>
public class AssistantActivityTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fake(Guid userId, Guid? org = null) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId { get; } = org ?? TestTenancy.OrgOf(userId);
        public OrganizationRole? OrgRole => OrganizationRole.Member;
    }

    private static InferenceApiDbContext Db() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private readonly Clock _clock = new();

    private AssistantActivityController Activity(InferenceApiDbContext db, Guid user, AssistantTurnHub? hub = null, Guid? org = null)
    {
        var controller = new AssistantActivityController(db, new Fake(user, org), hub ?? new AssistantTurnHub(), _clock);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };
        return controller;
    }

    private static AssistantThreadsController Threads(InferenceApiDbContext db, Guid user) =>
        new(db, new Fake(user), TestTenancy.Access(db, new Fake(user)));

    private AssistantThread AddThread(InferenceApiDbContext db, Guid user, string title = "Chat", int read = 0, Guid? org = null)
    {
        var t = new AssistantThread
        {
            Id = Guid.NewGuid(),
            UserId = user,
            OrganizationId = org ?? TestTenancy.OrgOf(user),
            Title = title,
            ReadThroughSequence = read,
            UpdatedAt = _clock.Now.UtcDateTime,
            CreatedAt = _clock.Now.UtcDateTime
        };
        db.AssistantThreads.Add(t);
        db.SaveChanges();
        return t;
    }

    private void Say(InferenceApiDbContext db, AssistantThread t, string role, string content, int sequence, string? meta = null)
    {
        db.AssistantMessages.Add(new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = t.Id,
            Role = role,
            Content = content,
            Sequence = sequence,
            MetadataJson = meta,
            CreatedAt = _clock.Now.UtcDateTime
        });
        db.SaveChanges();
    }

    private async Task<AssistantActivityDto> GetAsync(AssistantActivityController c)
    {
        _clock.Now = _clock.Now.AddSeconds(3); // past the 2 s response cache
        return (AssistantActivityDto)((OkObjectResult)(await c.Get(default)).Result!).Value!;
    }

    [Fact]
    public void A_new_thread_reads_zero_and_the_backfill_sql_marks_existing_threads_read_through_their_last_message()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            Guid user = Guid.NewGuid();
            AssistantThread t = AddThread(db, user);
            t.ReadThroughSequence.Should().Be(0);
            Say(db, t, "user", "hi", 1);
            Say(db, t, "assistant", "yo", 2);
            AssistantThread empty = AddThread(db, user, "empty");

            // The migration's backfill statement.
            db.Database.ExecuteSqlRaw(
                "UPDATE assistant_threads AS t SET read_through_sequence = " +
                "COALESCE((SELECT MAX(m.sequence) FROM assistant_messages m WHERE m.thread_id = t.id), 0);");

            db.ChangeTracker.Clear();
            db.AssistantThreads.Single(x => x.Id == t.Id).ReadThroughSequence.Should().Be(2);
            db.AssistantThreads.Single(x => x.Id == empty.Id).ReadThroughSequence.Should().Be(0);
        }
    }

    [Fact]
    public async Task Marking_read_is_monotonic_defaults_to_latest_is_idempotent_and_foreign_threads_are_404()
    {
        using InferenceApiDbContext db = Db();
        Guid user = Guid.NewGuid();
        AssistantThread t = AddThread(db, user);
        Say(db, t, "user", "q", 1);
        Say(db, t, "assistant", "a", 2);
        AssistantThreadsController c = Threads(db, user);

        (await c.MarkRead(t.Id, new MarkAssistantThreadReadRequest(1), default)).Should().BeOfType<NoContentResult>();
        db.AssistantThreads.Single().ReadThroughSequence.Should().Be(1);
        await c.MarkRead(t.Id, new MarkAssistantThreadReadRequest(0), default);
        db.AssistantThreads.Single().ReadThroughSequence.Should().Be(1, "never lowered");
        await c.MarkRead(t.Id, null, default);
        db.AssistantThreads.Single().ReadThroughSequence.Should().Be(2, "default is the latest");
        await c.MarkRead(t.Id, new MarkAssistantThreadReadRequest(99), default);
        db.AssistantThreads.Single().ReadThroughSequence.Should().Be(2, "capped at the latest message");
        (await c.MarkRead(t.Id, null, default)).Should().BeOfType<NoContentResult>("idempotent");

        (await Threads(db, Guid.NewGuid()).MarkRead(t.Id, null, default)).Should().BeOfType<NotFoundResult>();
        (await c.MarkRead(Guid.NewGuid(), null, default)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Opening_a_thread_does_not_mark_it_read()
    {
        using InferenceApiDbContext db = Db();
        Guid user = Guid.NewGuid();
        AssistantThread t = AddThread(db, user);
        Say(db, t, "assistant", "a", 1);
        await Threads(db, user).GetThread(t.Id, default);
        db.AssistantThreads.Single().ReadThroughSequence.Should().Be(0);
    }

    [Fact]
    public async Task An_assistant_message_shows_as_unread_until_the_thread_is_marked_read_and_carries_no_content()
    {
        using InferenceApiDbContext db = Db();
        Guid user = Guid.NewGuid();
        AssistantThread t = AddThread(db, user, "Launch film");
        AssistantActivityController c = Activity(db, user);
        (await GetAsync(c)).Unread.Should().BeEmpty();

        Say(db, t, "user", "SECRET question", 1);
        Say(db, t, "assistant", "SECRET answer", 2);
        AssistantActivityDto dto = await GetAsync(c);
        AssistantUnreadDto unread = dto.Unread.Should().ContainSingle().Subject;
        (unread.ThreadId, unread.Title, unread.Sequence, unread.Kind).Should().Be((t.Id, "Launch film", 2, "reply"));
        JsonSerializer.Serialize(dto).Should().NotContain("SECRET");

        await Threads(db, user).MarkRead(t.Id, null, default);
        (await GetAsync(c)).Unread.Should().BeEmpty();
    }

    [Fact]
    public async Task A_reply_to_a_wake_turn_is_kind_run_wake()
    {
        using InferenceApiDbContext db = Db();
        Guid user = Guid.NewGuid();
        AssistantThread t = AddThread(db, user);
        Say(db, t, "user", "[Run update] ...", 1, new AssistantWakeInfo(Guid.NewGuid(), "Passed", "Film").ToMetadataJson());
        Say(db, t, "assistant", "done", 2);
        (await GetAsync(Activity(db, user))).Unread.Single().Kind.Should().Be("run_wake");
    }

    [Fact]
    public async Task Threads_older_than_seven_days_or_archived_are_not_listed_and_the_list_is_capped_at_20_newest_first()
    {
        using InferenceApiDbContext db = Db();
        Guid user = Guid.NewGuid();
        for (int i = 0; i < 25; i++)
        {
            AssistantThread t = AddThread(db, user, "T" + i);
            t.UpdatedAt = _clock.Now.UtcDateTime.AddMinutes(-i);
            Say(db, t, "assistant", "a", 1);
        }

        AssistantThread old = AddThread(db, user, "old");
        old.UpdatedAt = _clock.Now.UtcDateTime.AddDays(-8);
        Say(db, old, "assistant", "a", 1);
        AssistantThread archived = AddThread(db, user, "archived");
        archived.ArchivedAt = _clock.Now.UtcDateTime;
        Say(db, archived, "assistant", "a", 1);
        db.SaveChanges();

        AssistantActivityDto dto = await GetAsync(Activity(db, user));
        dto.Unread.Should().HaveCount(20);
        dto.Unread.Select(u => u.Title).Should().NotContain(["old", "archived"]);
        dto.Unread.First().Title.Should().Be("T0");
    }

    [Fact]
    public async Task Writing_reflects_a_live_hub_turn_and_disappears_at_completion_per_user_and_workspace()
    {
        using InferenceApiDbContext db = Db();
        Guid user = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        AssistantThread mine = AddThread(db, user, "Mine");
        AssistantThread theirs = AddThread(db, other, "Theirs");
        var hub = new AssistantTurnHub();
        AssistantLiveTurn turn = hub.Begin(mine.Id, user, TestTenancy.OrgOf(user));
        turn.Kind = "run_wake";
        hub.Begin(theirs.Id, other, TestTenancy.OrgOf(other));

        AssistantActivityDto dto = await GetAsync(Activity(db, user, hub));
        AssistantWritingDto w = dto.Writing.Should().ContainSingle().Subject;
        (w.ThreadId, w.Title, w.State, w.Kind).Should().Be((mine.Id, "Mine", "running", "run_wake"));

        turn.MarkAwaitingApproval(Guid.NewGuid());
        (await GetAsync(Activity(db, user, hub))).Writing.Single().State.Should().Be("awaiting_approval");

        // A caller acting in another workspace does not see it.
        (await GetAsync(Activity(db, user, hub, org: Guid.NewGuid()))).Writing.Should().BeEmpty();

        turn.Complete(AssistantTurnState.Completed);
        (await GetAsync(Activity(db, user, hub))).Writing.Should().BeEmpty();
    }

    [Fact]
    public async Task Pending_watches_count_only_the_callers_open_ones_and_other_users_see_nothing_of_the_callers_threads()
    {
        using InferenceApiDbContext db = Db();
        Guid user = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        AssistantThread t = AddThread(db, user);
        Say(db, t, "assistant", "a", 1);
        db.AssistantRunWatches.AddRange(
            new AssistantRunWatch { Id = Guid.NewGuid(), ThreadId = t.Id, ExecutionId = Guid.NewGuid(), UserId = user, OrganizationId = TestTenancy.OrgOf(user), ProjectId = Guid.NewGuid() },
            new AssistantRunWatch { Id = Guid.NewGuid(), ThreadId = t.Id, ExecutionId = Guid.NewGuid(), UserId = user, OrganizationId = TestTenancy.OrgOf(user), ProjectId = Guid.NewGuid(), Status = AssistantRunWatchStatus.Fired });
        db.SaveChanges();

        (await GetAsync(Activity(db, user))).PendingWatches.Should().Be(1);
        AssistantActivityDto theirs = await GetAsync(Activity(db, other));
        (theirs.PendingWatches, theirs.Unread.Count, theirs.Writing.Count).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task The_response_cache_is_bounded()
    {
        using InferenceApiDbContext db = Db();
        for (int i = 0; i < AssistantActivityController.MaxCacheEntries + 50; i++)
        {
            AssistantActivityController c = Activity(db, Guid.NewGuid());
            await c.Get(default);
        }

        AssistantActivityController.CacheCount.Should().BeLessThanOrEqualTo(AssistantActivityController.MaxCacheEntries);
    }
}
