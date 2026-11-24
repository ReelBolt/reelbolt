using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Services.Telemetry;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>onboarding-and-guides T-001: the per-user onboarding state and its product events.</summary>
public class OnboardingControllerTests
{
    /// <summary>Loses a first-insert race: on the first save it lets a competing writer create the row, then throws a unique violation.</summary>
    private sealed class RacingDb(DbContextOptions<InferenceApiDbContext> options, ITenantContext tenant, Guid userId) : InferenceApiDbContext(options, tenant)
    {
        private bool _raced;

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!_raced && ChangeTracker.Entries<UserOnboardingState>().Any(e => e.State == EntityState.Added))
            {
                _raced = true;
                await using var other = new InferenceApiDbContext(options, tenant);
                other.UserOnboardingStates.Add(new UserOnboardingState { UserId = userId, TourStatus = "skipped", TourVersion = 1 });
                await other.SaveChangesAsync(cancellationToken);
                throw new DbUpdateException("dup", new Npgsql.PostgresException("dup", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.UniqueViolation));
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class Rig
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public TestTenancy.MutableTenant Tenant { get; } = new();
        public InferenceApiDbContext Db { get; }
        public RecordingProductEventLog Log { get; } = new();
        public bool Ingest { get; set; } = true;

        public Rig(bool racing = false)
        {
            var options = new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            Db = racing ? new RacingDb(options, Tenant, UserId) : new InferenceApiDbContext(options, Tenant);
        }

        public OnboardingController Controller(Guid? as_ = null)
        {
            Guid uid = as_ ?? UserId;
            Tenant.ActAs(uid);
            var u = new Mock<ICurrentUser>();
            u.SetupGet(x => x.UserId).Returns(uid);
            u.SetupGet(x => x.IsAuthenticated).Returns(true);
            u.SetupGet(x => x.OrganizationId).Returns(TestTenancy.OrgOf(uid));
            return new OnboardingController(
                Db,
                u.Object,
                Options.Create(new AlphaTelemetryOptions { ClientIngestEnabled = Ingest }),
                Log);
        }
    }

    private static OnboardingResponse Body(ActionResult<OnboardingResponse> r) => (OnboardingResponse)((OkObjectResult)r.Result!).Value!;

    private static HashSet<string> Done(OnboardingResponse r) => r.Checklist.Items.Where(i => i.Done).Select(i => i.Key).ToHashSet();

    [Fact]
    public async Task Get_returns_the_closed_ordered_shape_with_defaults()
    {
        var rig = new Rig();
        OnboardingResponse r = Body(await rig.Controller().Get(default));
        r.Tour.Status.Should().Be("pending");
        r.Tour.Version.Should().BeNull();
        r.Checklist.Dismissed.Should().BeFalse();
        r.Checklist.Items.Select(i => i.Key).Should().Equal("create_project", "upload_file", "run_video", "open_result", "try_assistant", "open_editor");
        r.Checklist.Items.Should().OnlyContain(i => !i.Done);
    }

    [Fact]
    public async Task Derived_items_tick_from_the_callers_own_data_and_stay_ticked()
    {
        var rig = new Rig();
        Guid org = TestTenancy.OrgOf(rig.UserId);
        Guid project = Guid.NewGuid();
        rig.Db.Projects.Add(new Project { Id = project, Name = "p", OwnerId = rig.UserId, OrganizationId = org });
        rig.Db.ProjectFiles.Add(new ProjectFile { Id = Guid.NewGuid(), ProjectId = project, OriginalFileName = "a.mp4" });
        var thread = new AssistantThread { Id = Guid.NewGuid(), UserId = rig.UserId, OrganizationId = org };
        rig.Db.AssistantThreads.Add(thread);
        rig.Db.AssistantMessages.Add(new AssistantMessage { Id = Guid.NewGuid(), ThreadId = thread.Id, Role = "user", Content = "hi", Sequence = 1 });
        await rig.Db.SaveChangesAsync();

        Done(Body(await rig.Controller().Get(default))).Should().BeEquivalentTo(["create_project", "upload_file", "try_assistant"]);

        // Sticky: delete the project, the items stay done.
        rig.Tenant.ActAs(rig.UserId);
        rig.Db.ProjectFiles.RemoveRange(rig.Db.ProjectFiles);
        rig.Db.Projects.RemoveRange(rig.Db.Projects);
        await rig.Db.SaveChangesAsync();
        Done(Body(await rig.Controller().Get(default))).Should().BeEquivalentTo(["create_project", "upload_file", "try_assistant"]);
    }

    [Fact]
    public async Task Other_users_and_other_org_rows_never_tick_an_item()
    {
        var rig = new Rig();
        Guid other = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        rig.Db.Projects.Add(new Project { Id = project, Name = "theirs", OwnerId = other, OrganizationId = TestTenancy.OrgOf(other) });
        rig.Db.ProjectFiles.Add(new ProjectFile { Id = Guid.NewGuid(), ProjectId = project, OriginalFileName = "x.mp4" });
        // A project the caller owns but in an organization they are not acting in.
        rig.Db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "elsewhere", OwnerId = rig.UserId, OrganizationId = Guid.NewGuid() });
        await rig.Db.SaveChangesAsync();

        Done(Body(await rig.Controller().Get(default))).Should().BeEmpty();
    }

    [Fact]
    public async Task Put_stores_tour_dismissal_and_marks_per_user_and_is_idempotent()
    {
        var rig = new Rig();
        var req = new SaveOnboardingRequest
        {
            Tour = new OnboardingTourInput { Status = "completed", Version = 2 },
            ChecklistDismissed = true,
            Marks = ["open_result"],
        };
        OnboardingResponse first = Body(await rig.Controller().Put(req, default));
        OnboardingResponse again = Body(await rig.Controller().Put(req, default));

        first.Tour.Status.Should().Be("completed");
        first.Tour.Version.Should().Be(2);
        first.Checklist.Dismissed.Should().BeTrue();
        Done(first).Should().BeEquivalentTo(["open_result"]);
        Done(again).Should().BeEquivalentTo(["open_result"]);
        rig.Db.UserOnboardingStates.Count().Should().Be(1);

        // pending = take the tour again
        Body(await rig.Controller().Put(new SaveOnboardingRequest { Tour = new OnboardingTourInput { Status = "pending", Version = 2 } }, default))
            .Tour.Status.Should().Be("pending");

        // Another user is untouched.
        OnboardingResponse other = Body(await rig.Controller(Guid.NewGuid()).Get(default));
        other.Tour.Status.Should().Be("pending");
        other.Checklist.Dismissed.Should().BeFalse();
        Done(other).Should().BeEmpty();
    }

    [Theory]
    [InlineData("bogus", null)]
    [InlineData("completed", "open_everything")]
    public async Task Put_with_an_unknown_value_is_400_and_writes_nothing(string status, string? mark)
    {
        var rig = new Rig();
        var req = new SaveOnboardingRequest
        {
            Tour = new OnboardingTourInput { Status = status, Version = 1 },
            ChecklistDismissed = true,
            Marks = mark is null ? null : [mark],
        };
        if (mark is not null) req.Tour = null;
        (await rig.Controller().Put(req, default)).Result.Should().BeOfType<BadRequestObjectResult>();
        rig.Db.UserOnboardingStates.Should().BeEmpty();
    }

    [Fact]
    public async Task Events_write_once_per_user_per_event_and_item_with_a_closed_detail()
    {
        var rig = new Rig();
        for (int i = 0; i < 2; i++)
        {
            (await rig.Controller().Events(new OnboardingEventRequest { Event = "tour_started" }, default)).Should().BeOfType<NoContentResult>();
            await rig.Controller().Events(new OnboardingEventRequest { Event = "checklist_item_done", Item = "upload_file" }, default);
        }

        await rig.Controller().Events(new OnboardingEventRequest { Event = "checklist_item_done", Item = "run_video" }, default);
        await rig.Controller(Guid.NewGuid()).Events(new OnboardingEventRequest { Event = "tour_started" }, default);

        rig.Log.Rows.Select(r => (r.Event, r.Detail)).Should().BeEquivalentTo(new (string, string?)[]
        {
            ("onboarding.tour_started", null),
            ("onboarding.checklist_item_done", "upload_file"),
            ("onboarding.checklist_item_done", "run_video"),
            ("onboarding.tour_started", null),
        });
    }

    [Theory]
    [InlineData("nope", null)]
    [InlineData("checklist_item_done", null)]
    [InlineData("checklist_item_done", "free text")]
    [InlineData("tour_started", "upload_file")]
    public async Task Events_with_an_unknown_body_is_400_and_writes_nothing(string ev, string? item)
    {
        var rig = new Rig();
        (await rig.Controller().Events(new OnboardingEventRequest { Event = ev, Item = item }, default)).Should().BeOfType<BadRequestObjectResult>();
        rig.Log.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Events_are_404_when_telemetry_ingest_is_disabled()
    {
        var rig = new Rig { Ingest = false };
        (await rig.Controller().Events(new OnboardingEventRequest { Event = "tour_started" }, default)).Should().BeOfType<NotFoundResult>();
        rig.Log.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_video_ticks_for_the_callers_execution_in_a_visible_project()
    {
        var rig = new Rig();
        Guid project = Guid.NewGuid();
        rig.Db.Projects.Add(new Project { Id = project, Name = "p", OwnerId = rig.UserId, OrganizationId = TestTenancy.OrgOf(rig.UserId) });
        rig.Db.WorkflowExecutions.Add(new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = project, InitiatedByUserId = rig.UserId, OrganizationId = TestTenancy.OrgOf(rig.UserId) });
        await rig.Db.SaveChangesAsync();
        Done(Body(await rig.Controller().Get(default))).Should().Contain("run_video");
    }

    [Fact]
    public async Task Other_users_and_other_org_executions_and_threads_never_tick()
    {
        var rig = new Rig();
        Guid other = Guid.NewGuid();
        Guid theirs = Guid.NewGuid();
        Guid elsewhere = Guid.NewGuid();
        rig.Db.Projects.Add(new Project { Id = theirs, Name = "t", OwnerId = other, OrganizationId = TestTenancy.OrgOf(other) });
        rig.Db.Projects.Add(new Project { Id = elsewhere, Name = "e", OwnerId = rig.UserId, OrganizationId = Guid.NewGuid() });
        // Someone else's execution; the caller's own execution in a project of another organization.
        rig.Db.WorkflowExecutions.Add(new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = theirs, InitiatedByUserId = other, OrganizationId = TestTenancy.OrgOf(other) });
        rig.Db.WorkflowExecutions.Add(new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = elsewhere, InitiatedByUserId = rig.UserId, OrganizationId = Guid.NewGuid() });
        var theirThread = new AssistantThread { Id = Guid.NewGuid(), UserId = other, OrganizationId = TestTenancy.OrgOf(other) };
        var elsewhereThread = new AssistantThread { Id = Guid.NewGuid(), UserId = rig.UserId, OrganizationId = Guid.NewGuid() };
        rig.Db.AssistantThreads.AddRange(theirThread, elsewhereThread);
        rig.Db.AssistantMessages.Add(new AssistantMessage { Id = Guid.NewGuid(), ThreadId = theirThread.Id, Role = "user", Content = "x", Sequence = 1 });
        rig.Db.AssistantMessages.Add(new AssistantMessage { Id = Guid.NewGuid(), ThreadId = elsewhereThread.Id, Role = "user", Content = "x", Sequence = 1 });
        await rig.Db.SaveChangesAsync();
        Done(Body(await rig.Controller().Get(default))).Should().BeEmpty();
    }

    [Fact]
    public async Task Tour_updated_at_moves_only_when_the_tour_changes()
    {
        var rig = new Rig();
        var tour = new SaveOnboardingRequest { Tour = new OnboardingTourInput { Status = "skipped", Version = 1 } };
        DateTime? first = Body(await rig.Controller().Put(tour, default)).Tour.UpdatedAt;
        first.Should().NotBeNull();
        await Task.Delay(20);
        OnboardingResponse after = Body(await rig.Controller().Put(new SaveOnboardingRequest { ChecklistDismissed = true, Marks = ["open_editor"] }, default));
        after.Tour.UpdatedAt.Should().Be(first);
        Body(await rig.Controller().Put(tour, default)).Tour.UpdatedAt.Should().Be(first);
    }

    [Fact]
    public async Task A_put_that_loses_the_first_insert_race_is_re_applied_to_the_winning_row()
    {
        var rig = new Rig(racing: true);
        OnboardingResponse r = Body(await rig.Controller().Put(
            new SaveOnboardingRequest { Tour = new OnboardingTourInput { Status = "completed", Version = 3 }, ChecklistDismissed = true }, default));
        r.Tour.Status.Should().Be("completed");
        r.Tour.Version.Should().Be(3);
        r.Checklist.Dismissed.Should().BeTrue();
        rig.Db.UserOnboardingStates.Single().TourStatus.Should().Be("completed");
    }
}
