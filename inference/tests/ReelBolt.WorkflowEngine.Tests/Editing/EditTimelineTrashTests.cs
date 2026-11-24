#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Editing.TimelineRetentionTestKit;

namespace ReelBolt.WorkflowEngine.Tests.Editing;

/// <summary>R01.1 / R04.3 server half: the trash list, restore and the purge job.</summary>
public class EditTimelineTrashTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TrashList_ShowsOnlyThisProjectsDeletedTimelines_WithDeletedAtAndPurgesAt_AndDefaultListHidesThem()
    {
        using InferenceApiDbContext db = NewContext();
        ManualClock clock = new(T0);
        (Project project, EditTimelineEntity gone) = await SeedAsync(db, name: "Gone");
        EditTimelineEntity kept = await AddTimelineAsync(db, project, name: "Kept");
        (Project other, EditTimelineEntity foreign) = await SeedAsync(db, name: "Foreign");

        EditTimelinesController controller = Controller(db, project.OwnerId, clock);
        (await controller.DeleteEditTimeline(project.Id, gone.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();
        await Controller(db, other.OwnerId, clock).DeleteEditTimeline(other.Id, foreign.Id, CancellationToken.None);

        var trash = (await controller.ListEditTimelines(project.Id, "deleted", CancellationToken.None)).Result
            .Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<System.Collections.Generic.List<EditTimelineSummaryResponse>>().Subject;
        trash.Should().ContainSingle();
        trash[0].Id.Should().Be(gone.Id);
        trash[0].Name.Should().Be("Gone");
        trash[0].DeletedAt.Should().NotBeNull();
        trash[0].PurgesAt.Should().Be(trash[0].DeletedAt!.Value.AddDays(30));

        var live = (await controller.ListEditTimelines(project.Id, null, CancellationToken.None)).Result
            .Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<System.Collections.Generic.List<EditTimelineSummaryResponse>>().Subject;
        live.Select(t => t.Id).Should().Equal(kept.Id);
        live[0].DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task TrashList_IsNotFoundForAnotherTenantsProject_AndRefusedForARoleWithoutAccess()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, _) = await SeedAsync(db);
        (Project other, _) = await SeedAsync(db);

        (await Controller(db, project.OwnerId).ListEditTimelines(other.Id, "deleted", CancellationToken.None)).Result
            .Should().BeOfType<NotFoundResult>("a foreign project is 404, never 403");
        (await Controller(db, project.OwnerId, role: OrganizationRole.Billing).ListEditTimelines(project.Id, "deleted", CancellationToken.None)).Result
            .Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task TrashList_DropsATimelineOncePastTheRetentionWindow()
    {
        using InferenceApiDbContext db = NewContext();
        ManualClock clock = new(T0);
        (Project project, EditTimelineEntity row) = await SeedAsync(db);
        EditTimelinesController controller = Controller(db, project.OwnerId, clock);
        await controller.DeleteEditTimeline(project.Id, row.Id, CancellationToken.None);

        clock.Advance(TimeSpan.FromDays(29));
        ((await controller.ListEditTimelines(project.Id, "deleted", CancellationToken.None)).Result as OkObjectResult)!
            .Value.Should().BeAssignableTo<System.Collections.Generic.List<EditTimelineSummaryResponse>>().Subject.Should().ContainSingle();

        clock.Advance(TimeSpan.FromDays(2));
        ((await controller.ListEditTimelines(project.Id, "deleted", CancellationToken.None)).Result as OkObjectResult)!
            .Value.Should().BeAssignableTo<System.Collections.Generic.List<EditTimelineSummaryResponse>>().Subject.Should().BeEmpty();
    }

    [Fact]
    public async Task Restore_BringsTheSameRowBack_NameKept_CommentsRouteWorksAgain()
    {
        using InferenceApiDbContext db = NewContext();
        ManualClock clock = new(T0);
        (Project project, EditTimelineEntity row) = await SeedAsync(db, name: "My cut");
        db.EditTimelineComments.Add(new EditTimelineComment
        {
            Id = Guid.NewGuid(),
            TimelineId = row.Id,
            ProjectId = project.Id,
            Frame = 3,
            Body = "hello",
            AuthorName = "A",
            AuthorUserId = project.OwnerId,
            CreatedAt = T0.UtcDateTime
        });
        await db.SaveChangesAsync();
        EditTimelinesController controller = Controller(db, project.OwnerId, clock);
        await controller.DeleteEditTimeline(project.Id, row.Id, CancellationToken.None);
        (await controller.ListComments(project.Id, row.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();

        ActionResult<EditTimelineSummaryResponse> restored = await controller.RestoreEditTimeline(project.Id, row.Id, CancellationToken.None);

        EditTimelineSummaryResponse body = restored.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<EditTimelineSummaryResponse>().Subject;
        body.Id.Should().Be(row.Id);
        body.Name.Should().Be("My cut");
        body.DeletedAt.Should().BeNull();
        (await db.EditTimelines.AsNoTracking().SingleAsync(t => t.Id == row.Id)).DeletedAt.Should().BeNull();
        (await controller.GetEditTimeline(project.Id, row.Id, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
        (await controller.ListComments(project.Id, row.Id, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Restore_Is404ForNotDeleted_Expired_Foreign_AndAnUnknownId_AndNeedsEdit()
    {
        using InferenceApiDbContext db = NewContext();
        ManualClock clock = new(T0);
        (Project project, EditTimelineEntity live) = await SeedAsync(db);
        EditTimelineEntity old = await AddTimelineAsync(db, project, name: "Old");
        (Project other, EditTimelineEntity foreign) = await SeedAsync(db);
        EditTimelinesController controller = Controller(db, project.OwnerId, clock);
        await controller.DeleteEditTimeline(project.Id, old.Id, CancellationToken.None);
        await Controller(db, other.OwnerId, clock).DeleteEditTimeline(other.Id, foreign.Id, CancellationToken.None);

        (await controller.RestoreEditTimeline(project.Id, live.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>("not deleted");
        (await controller.RestoreEditTimeline(project.Id, Guid.NewGuid(), CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await controller.RestoreEditTimeline(other.Id, foreign.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>("foreign project");
        (await controller.RestoreEditTimeline(project.Id, foreign.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>("foreign timeline id");
        (await Controller(db, project.OwnerId, clock, OrganizationRole.Billing).RestoreEditTimeline(project.Id, old.Id, CancellationToken.None)).Result
            .Should().BeOfType<ForbidResult>();

        clock.Advance(TimeSpan.FromDays(31));
        (await controller.RestoreEditTimeline(project.Id, old.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>("past the retention window");
        (await db.EditTimelines.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Id == old.Id)).DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Purge_RemovesExpiredTimelinesWithTheirOpsSnapshotsAndComments_KeepsTheRest_AndIsIdempotent()
    {
        string name = Guid.NewGuid().ToString();
        ManualClock clock = new(T0);
        Guid expiredId, freshId, liveId;
        using (InferenceApiDbContext db = NewContext(name))
        {
            (Project project, EditTimelineEntity expired) = await SeedAsync(db, name: "Expired");
            EditTimelineEntity fresh = await AddTimelineAsync(db, project, name: "Fresh");
            EditTimelineEntity live = await AddTimelineAsync(db, project, name: "Live");
            expiredId = expired.Id; freshId = fresh.Id; liveId = live.Id;
            EditTimelinesController controller = Controller(db, project.OwnerId, clock);
            await controller.DeleteEditTimeline(project.Id, expired.Id, CancellationToken.None);
            clock.Advance(TimeSpan.FromDays(20));
            await controller.DeleteEditTimeline(project.Id, fresh.Id, CancellationToken.None);
            clock.Advance(TimeSpan.FromDays(11));
            foreach (EditTimelineEntity t in new[] { expired, fresh })
            {
                db.EditTimelineOps.Add(new EditTimelineOp { Id = Guid.NewGuid(), TimelineId = t.Id, Revision = 2, OpsJson = "[]", CreatedAt = T0.UtcDateTime });
                db.EditTimelineSnapshots.Add(new EditTimelineSnapshot { Id = Guid.NewGuid(), TimelineId = t.Id, Revision = 1, DocumentJson = "{}", CreatedAt = T0.UtcDateTime });
                db.EditTimelineComments.Add(new EditTimelineComment { Id = Guid.NewGuid(), TimelineId = t.Id, ProjectId = project.Id, Frame = 1, Body = "x", AuthorName = "A", AuthorUserId = project.OwnerId, CreatedAt = T0.UtcDateTime });
            }

            await db.SaveChangesAsync();
        }

        ServiceCollection services = new();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
        using ServiceProvider provider = services.BuildServiceProvider();
        TimelineTrashPurgeService purge = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EditingRetentionOptions()),
            NullLogger<TimelineTrashPurgeService>.Instance,
            clock);

        (await purge.SweepOnceAsync(CancellationToken.None)).Should().Be(1);
        (await purge.SweepOnceAsync(CancellationToken.None)).Should().Be(0, "a second run finds nothing");

        using InferenceApiDbContext check = NewContext(name);
        (await check.EditTimelines.IgnoreQueryFilters().Select(t => t.Id).ToListAsync()).Should().BeEquivalentTo([freshId, liveId]);
        (await check.EditTimelineOps.Select(o => o.TimelineId).ToListAsync()).Should().OnlyContain(id => id == freshId);
        (await check.EditTimelineSnapshots.Select(o => o.TimelineId).ToListAsync()).Should().OnlyContain(id => id == freshId);
        (await check.EditTimelineComments.IgnoreQueryFilters().Select(o => o.TimelineId).ToListAsync()).Should().OnlyContain(id => id == freshId);
        expiredId.Should().NotBe(freshId);
    }

    [Fact]
    public async Task TrashRetentionDays_ConfigChangesTheWindow()
    {
        using InferenceApiDbContext db = NewContext();
        ManualClock clock = new(T0);
        (Project project, EditTimelineEntity row) = await SeedAsync(db);
        EditTimelinesController controller = Controller(db, project.OwnerId, clock, retention: new EditingRetentionOptions { TrashRetentionDays = 7 });
        await controller.DeleteEditTimeline(project.Id, row.Id, CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(8));
        (await controller.RestoreEditTimeline(project.Id, row.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
    }
}
