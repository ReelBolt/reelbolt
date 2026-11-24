#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Editing.TimelineRetentionTestKit;

namespace ReelBolt.WorkflowEngine.Tests.Editing;

/// <summary>R01.2 / R02.4 server half: named versions, automatic checkpoints, prune and cap.</summary>
public class EditTimelineVersionsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Thirty = TimeSpan.FromDays(30);

    private static async Task<EditTimelineVersionResponse[]> VersionsAsync(EditTimelinesController c, Guid projectId, Guid id) =>
        ((await c.ListVersions(projectId, id, CancellationToken.None)).Result as OkObjectResult)!
            .Value.Should().BeOfType<EditTimelineVersionsResponse>().Subject.Versions.ToArray();

    private static async Task<ActionResult<EditTimelineResponse>> PatchAsync(
        EditTimelinesController c, Project project, EditTimelineEntity row, int baseRevision, string opsJson = "[]")
    {
        using JsonDocument ops = JsonDocument.Parse(opsJson);
        return await c.PatchEditTimeline(project.Id, row.Id,
            new PatchEditTimelineRequest(baseRevision, ops.RootElement.Clone(), Blank(project.Id, row.Id, row.Name)), CancellationToken.None);
    }

    private static void AddSnapshot(InferenceApiDbContext db, Guid timelineId, int revision, string cause, DateTime createdAt, string? name = null, DateTime? expiresAt = null) =>
        db.EditTimelineSnapshots.Add(new EditTimelineSnapshot
        {
            Id = Guid.NewGuid(),
            TimelineId = timelineId,
            Revision = revision,
            DocumentJson = "{}",
            CreatedAt = createdAt,
            Cause = cause,
            Name = name,
            ExpiresAt = expiresAt
        });

    // ───────────────────────────── list / create / name / unname ─────────────────────────────

    [Fact]
    public async Task List_ReturnsLiveSnapshotsNewestFirst_HidesExpired_Is404ForForeign_AndRefusesARoleWithoutAccess()
    {
        using InferenceApiDbContext db = NewContext();
        ManualClock clock = new(T0);
        (Project project, EditTimelineEntity row) = await SeedAsync(db, revision: 9);
        AddSnapshot(db, row.Id, 1, EditTimelineSnapshotCause.Milestone, T0.UtcDateTime.AddDays(-90));
        AddSnapshot(db, row.Id, 5, EditTimelineSnapshotCause.Idle, T0.UtcDateTime, expiresAt: T0.UtcDateTime + Thirty);
        AddSnapshot(db, row.Id, 3, EditTimelineSnapshotCause.Idle, T0.UtcDateTime.AddDays(-40), expiresAt: T0.UtcDateTime.AddDays(-10));
        AddSnapshot(db, row.Id, 7, EditTimelineSnapshotCause.Named, T0.UtcDateTime, name: "Pitch cut");
        await db.SaveChangesAsync();
        (Project other, EditTimelineEntity foreign) = await SeedAsync(db);
        EditTimelinesController controller = Controller(db, project.OwnerId, clock);

        EditTimelineVersionResponse[] versions = await VersionsAsync(controller, project.Id, row.Id);

        versions.Select(v => v.Revision).Should().Equal(7, 5, 1);
        versions[0].Name.Should().Be("Pitch cut");
        versions[0].Cause.Should().Be("named");
        versions[0].ExpiresAt.Should().BeNull();
        versions[1].ExpiresAt.Should().Be(T0.UtcDateTime + Thirty);
        (await controller.ListVersions(other.Id, foreign.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>("foreign project is 404");
        (await controller.ListVersions(project.Id, foreign.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>("foreign timeline id");
        (await controller.ListVersions(project.Id, Guid.NewGuid(), CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await Controller(db, project.OwnerId, clock, OrganizationRole.Billing).ListVersions(project.Id, row.Id, CancellationToken.None)).Result
            .Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task CreateNamed_SnapshotsTheCurrentRevision_TrimsTheName_NeverExpires_AndReusesAnExistingSnapshot()
    {
        using InferenceApiDbContext db = NewContext();
        ManualClock clock = new(T0);
        (Project project, EditTimelineEntity row) = await SeedAsync(db, revision: 4);
        EditTimelinesController controller = Controller(db, project.OwnerId, clock);

        ActionResult<EditTimelineVersionResponse> created = await controller.CreateVersion(
            project.Id, row.Id, new CreateEditTimelineVersionRequest("  Client review  ", null), CancellationToken.None);

        ObjectResult ok = created.Result.Should().BeOfType<ObjectResult>().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status201Created);
        EditTimelineVersionResponse body = ok.Value.Should().BeOfType<EditTimelineVersionResponse>().Subject;
        body.Revision.Should().Be(4);
        body.Name.Should().Be("Client review");
        body.Cause.Should().Be("named");
        body.ExpiresAt.Should().BeNull();
        body.CreatedByName.Should().BeNull("the test user has no stored display name");

        // Same revision again: the one snapshot is renamed, not duplicated.
        await controller.CreateVersion(project.Id, row.Id, new CreateEditTimelineVersionRequest("Final", "named"), CancellationToken.None);
        EditTimelineVersionResponse[] versions = await VersionsAsync(controller, project.Id, row.Id);
        versions.Should().ContainSingle().Which.Name.Should().Be("Final");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("line\nbreak")]
    [InlineData("tab\tname")]
    public async Task CreateNamed_RejectsAMissingOrControlCharacterName(string? name)
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db);
        EditTimelinesController controller = Controller(db, project.OwnerId, new ManualClock(T0));

        ActionResult<EditTimelineVersionResponse> result = await controller.CreateVersion(
            project.Id, row.Id, new CreateEditTimelineVersionRequest(name, "named"), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value.Should().BeAssignableTo<IEnumerable<EditTimelineViolation>>()
            .Which.Should().ContainSingle(v => v.Path == "name");
        (await db.EditTimelineSnapshots.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateNamed_RejectsANameOver80Characters_AcceptsExactly80_AndAnUnknownCause()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db);
        EditTimelinesController controller = Controller(db, project.OwnerId, new ManualClock(T0));

        (await controller.CreateVersion(project.Id, row.Id, new CreateEditTimelineVersionRequest(new string('x', 81), null), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await controller.CreateVersion(project.Id, row.Id, new CreateEditTimelineVersionRequest("ok", "idle"), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value.Should().BeAssignableTo<IEnumerable<EditTimelineViolation>>()
            .Which.Should().ContainSingle(v => v.Path == "cause");
        (await controller.CreateVersion(project.Id, row.Id, new CreateEditTimelineVersionRequest(new string('x', 80), null), CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status201Created);
    }

    [Fact]
    public async Task CreateVersion_IsNotFoundForForeignAndDeleted_AndNeedsEdit()
    {
        using InferenceApiDbContext db = NewContext();
        ManualClock clock = new(T0);
        (Project project, EditTimelineEntity row) = await SeedAsync(db);
        (Project other, EditTimelineEntity foreign) = await SeedAsync(db);
        EditTimelinesController controller = Controller(db, project.OwnerId, clock);
        CreateEditTimelineVersionRequest request = new("v", null);

        (await controller.CreateVersion(other.Id, foreign.Id, request, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await controller.CreateVersion(project.Id, foreign.Id, request, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await Controller(db, project.OwnerId, clock, OrganizationRole.Billing).CreateVersion(project.Id, row.Id, request, CancellationToken.None))
            .Result.Should().BeOfType<ForbidResult>();
        await controller.DeleteEditTimeline(project.Id, row.Id, CancellationToken.None);
        (await controller.CreateVersion(project.Id, row.Id, request, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task BeforeProposal_IsAnAutomaticCheckpoint_WithAnOptionalLabel_ThatExpires()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db, revision: 3);
        EditTimelinesController controller = Controller(db, project.OwnerId, new ManualClock(T0));

        ActionResult<EditTimelineVersionResponse> result = await controller.CreateVersion(
            project.Id, row.Id, new CreateEditTimelineVersionRequest(null, "beforeProposal"), CancellationToken.None);

        EditTimelineVersionResponse body = result.Result.Should().BeOfType<ObjectResult>().Subject.Value.Should().BeOfType<EditTimelineVersionResponse>().Subject;
        body.Cause.Should().Be("beforeProposal");
        body.Name.Should().BeNull();
        body.ExpiresAt.Should().Be(T0.UtcDateTime + Thirty);
    }

    [Fact]
    public async Task Rename_KeepsTheVersionForGood_AndUnname_MakesItAutomaticWithAnExpiry_Revision1NeverExpires()
    {
        using InferenceApiDbContext db = NewContext();
        ManualClock clock = new(T0);
        (Project project, EditTimelineEntity row) = await SeedAsync(db, revision: 6);
        AddSnapshot(db, row.Id, 1, EditTimelineSnapshotCause.Milestone, T0.UtcDateTime);
        AddSnapshot(db, row.Id, 5, EditTimelineSnapshotCause.Idle, T0.UtcDateTime, expiresAt: T0.UtcDateTime + Thirty);
        await db.SaveChangesAsync();
        EditTimelinesController controller = Controller(db, project.OwnerId, clock);

        EditTimelineVersionResponse named = ((await controller.NameVersion(project.Id, row.Id, 5, new NameEditTimelineVersionRequest(" Rough cut "), CancellationToken.None))
            .Result as OkObjectResult)!.Value.Should().BeOfType<EditTimelineVersionResponse>().Subject;
        named.Name.Should().Be("Rough cut");
        named.Cause.Should().Be("named");
        named.ExpiresAt.Should().BeNull();

        clock.Advance(TimeSpan.FromDays(5));
        EditTimelineVersionResponse auto = ((await controller.UnnameVersion(project.Id, row.Id, 5, CancellationToken.None))
            .Result as OkObjectResult)!.Value.Should().BeOfType<EditTimelineVersionResponse>().Subject;
        auto.Name.Should().BeNull();
        auto.Cause.Should().Be("auto");
        auto.ExpiresAt.Should().Be(T0.UtcDateTime.AddDays(5) + Thirty);

        await controller.NameVersion(project.Id, row.Id, 1, new NameEditTimelineVersionRequest("Start"), CancellationToken.None);
        EditTimelineVersionResponse origin = ((await controller.UnnameVersion(project.Id, row.Id, 1, CancellationToken.None))
            .Result as OkObjectResult)!.Value.Should().BeOfType<EditTimelineVersionResponse>().Subject;
        origin.ExpiresAt.Should().BeNull("revision 1 is the origin and never expires");
    }

    [Fact]
    public async Task NameAndUnname_404ForUnknownRevisionForeignAndExpired_400ForABadName_AndNeedEdit()
    {
        using InferenceApiDbContext db = NewContext();
        ManualClock clock = new(T0);
        (Project project, EditTimelineEntity row) = await SeedAsync(db, revision: 6);
        AddSnapshot(db, row.Id, 2, EditTimelineSnapshotCause.Idle, T0.UtcDateTime.AddDays(-40), expiresAt: T0.UtcDateTime.AddDays(-10));
        AddSnapshot(db, row.Id, 4, EditTimelineSnapshotCause.Idle, T0.UtcDateTime, expiresAt: T0.UtcDateTime + Thirty);
        await db.SaveChangesAsync();
        (Project other, EditTimelineEntity foreign) = await SeedAsync(db);
        EditTimelinesController controller = Controller(db, project.OwnerId, clock);
        NameEditTimelineVersionRequest good = new("n");

        (await controller.NameVersion(project.Id, row.Id, 3, good, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>("no such snapshot");
        (await controller.NameVersion(project.Id, row.Id, 2, good, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>("expired");
        (await controller.NameVersion(other.Id, foreign.Id, 1, good, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await controller.UnnameVersion(project.Id, foreign.Id, 4, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await controller.NameVersion(project.Id, row.Id, 4, new NameEditTimelineVersionRequest(new string('y', 81)), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await Controller(db, project.OwnerId, clock, OrganizationRole.Billing).NameVersion(project.Id, row.Id, 4, good, CancellationToken.None))
            .Result.Should().BeOfType<ForbidResult>();
        (await Controller(db, project.OwnerId, clock, OrganizationRole.Billing).UnnameVersion(project.Id, row.Id, 4, CancellationToken.None))
            .Result.Should().BeOfType<ForbidResult>();
    }

    // ───────────────────────────── server-side checkpoints ─────────────────────────────

    [Fact]
    public async Task Patch_TakesAnIdleSnapshotOfThePreviousRevision_OnlyAfterTheTwoMinuteGap_AndOnlyOncePerRevision()
    {
        using InferenceApiDbContext db = NewContext();
        ManualClock clock = new(T0);
        (Project project, EditTimelineEntity row) = await SeedAsync(db, revision: 5, updatedAt: T0.UtcDateTime);
        EditTimelinesController controller = Controller(db, project.OwnerId, clock);

        // Rapid edits (1 minute apart): no checkpoint.
        clock.Advance(TimeSpan.FromMinutes(1));
        (await PatchAsync(controller, project, row, 5)).Result.Should().BeOfType<OkObjectResult>();
        (await db.EditTimelineSnapshots.CountAsync()).Should().Be(0, "an edit within 2 minutes of the previous one takes no checkpoint");

        // After a 3 minute pause: the previous revision (6) is snapshotted, cause idle, expiring in 30 days.
        clock.Advance(TimeSpan.FromMinutes(3));
        (await PatchAsync(controller, project, row, 6)).Result.Should().BeOfType<OkObjectResult>();
        EditTimelineSnapshot idle = await db.EditTimelineSnapshots.AsNoTracking().SingleAsync();
        idle.Revision.Should().Be(6);
        idle.Cause.Should().Be("idle");
        idle.ExpiresAt.Should().Be(clock.Utc + Thirty);
        idle.CreatedByUserId.Should().Be(project.OwnerId);

        // Just after: no second one; and a pause where the revision already has a snapshot adds none.
        clock.Advance(TimeSpan.FromSeconds(10));
        await PatchAsync(controller, project, row, 7);
        AddSnapshot(db, row.Id, 8, EditTimelineSnapshotCause.Named, clock.Utc, name: "kept");
        await db.SaveChangesAsync();
        clock.Advance(TimeSpan.FromMinutes(10));
        await PatchAsync(controller, project, row, 8);
        (await db.EditTimelineSnapshots.AsNoTracking().OrderBy(s => s.Revision).Select(s => s.Revision).ToListAsync()).Should().Equal(6, 8);
        (await db.EditTimelineSnapshots.AsNoTracking().SingleAsync(s => s.Revision == 8)).Cause.Should().Be("named", "the existing snapshot is untouched");
    }

    [Fact]
    public async Task Patch_WithARestoreMarker_SnapshotsWhatItOverwrites_EvenWithoutAGap_AndStaleBaseIsStill409()
    {
        using InferenceApiDbContext db = NewContext();
        ManualClock clock = new(T0);
        (Project project, EditTimelineEntity row) = await SeedAsync(db, revision: 5, updatedAt: T0.UtcDateTime);
        EditTimelinesController controller = Controller(db, project.OwnerId, clock);

        clock.Advance(TimeSpan.FromSeconds(5));
        (await PatchAsync(controller, project, row, 5, """[{"type":"restore","revision":2}]""")).Result.Should().BeOfType<OkObjectResult>();

        EditTimelineSnapshot before = await db.EditTimelineSnapshots.AsNoTracking().SingleAsync();
        before.Revision.Should().Be(5);
        before.Cause.Should().Be("beforeRestore");

        // A stale restore is the same 409 as ever and writes nothing.
        ActionResult<EditTimelineResponse> stale = await PatchAsync(controller, project, row, 5, """[{"type":"restore","revision":2}]""");
        stale.Result.Should().BeOfType<ConflictObjectResult>();
        (await db.EditTimelineSnapshots.CountAsync()).Should().Be(1);
        (await db.EditTimelines.AsNoTracking().SingleAsync()).Revision.Should().Be(6);
    }

    [Fact]
    public async Task Render_PinsTheStartRevisionWithABeforeRenderSnapshot_ExactlyOnce()
    {
        using InferenceApiDbContext db = NewContext();
        ManualClock clock = new(T0);
        (Project project, EditTimelineEntity row) = await SeedAsync(db, revision: 3);
        db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "VideoTransform", AgentType = AgentType.VideoTransform, IsBuiltIn = true });
        await db.SaveChangesAsync();
        Mock<IWorkflowExecutionService> executions = new();
        executions.Setup(e => e.QueueAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(),
                It.IsAny<ExecutionInitiator>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<RunChoices?>(),
                WorkflowDefinitionKind.TimelineRender, It.IsAny<Guid?>(), It.IsAny<Func<CancellationToken, Task>?>()))
            .ReturnsAsync((Guid _, Guid workflowId, Guid _, string? _, ExecutionInitiator _, CancellationToken _, bool _, RunChoices? _,
                WorkflowDefinitionKind _, Guid? _, Func<CancellationToken, Task>? _) =>
                new WorkflowExecutionResponse(Guid.NewGuid(), workflowId, "Queued", null, null, 0, null, null, null, [], []));
        EditTimelinesController controller = Controller(db, project.OwnerId, clock, executions: executions.Object);

        for (int i = 0; i < 2; i++)
        {
            (await controller.RenderEditTimeline(project.Id, row.Id, new RenderEditTimelineRequest(), CancellationToken.None))
                .Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        }

        EditTimelineSnapshot pin = await db.EditTimelineSnapshots.AsNoTracking().SingleAsync();
        pin.Revision.Should().Be(3);
        pin.Cause.Should().Be("beforeRender");
        pin.ExpiresAt.Should().Be(clock.Utc + Thirty);
    }

    // ───────────────────────────── every listed version restores ─────────────────────────────

    [Fact]
    public async Task EveryListedVersion_IsServedByRevisions_AndRestoresThroughTheRestoreMarkerPatch()
    {
        using InferenceApiDbContext db = NewContext();
        ManualClock clock = new(T0);
        (Project project, EditTimelineEntity row) = await SeedAsync(db, revision: 1, updatedAt: T0.UtcDateTime);
        db.EditTimelineSnapshots.Add(new EditTimelineSnapshot
        {
            Id = Guid.NewGuid(),
            TimelineId = row.Id,
            Revision = 1,
            DocumentJson = row.DocumentJson,
            CreatedAt = T0.UtcDateTime,
            Cause = "milestone"
        });
        await db.SaveChangesAsync();
        EditTimelinesController controller = Controller(db, project.OwnerId, clock);

        // Build a history with every kind of checkpoint: edits spaced past the idle gap, a named version, a restore.
        int revision = 1;
        for (int i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(5));
            (await PatchAsync(controller, project, row, revision)).Result.Should().BeOfType<OkObjectResult>();
            revision++;
        }

        await controller.CreateVersion(project.Id, row.Id, new CreateEditTimelineVersionRequest("Milestone", null), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(10));
        (await PatchAsync(controller, project, row, revision, """[{"type":"restore","revision":1}]""")).Result.Should().BeOfType<OkObjectResult>();
        revision++;

        EditTimelineVersionResponse[] versions = await VersionsAsync(controller, project.Id, row.Id);
        versions.Should().HaveCountGreaterThan(3);
        versions.Select(v => v.Cause).Should().Contain(["milestone", "idle", "named"]);

        foreach (EditTimelineVersionResponse version in versions)
        {
            // The client's restore: GET revisions/{rev}, then an ordinary PATCH with the marker.
            ActionResult<EditTimelineRevisionResponse> fetched = await controller.GetRevision(project.Id, row.Id, version.Revision, CancellationToken.None);
            EditTimelineRevisionResponse revisionBody = fetched.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<EditTimelineRevisionResponse>().Subject;

            clock.Advance(TimeSpan.FromSeconds(5));
            using JsonDocument ops = JsonDocument.Parse($$"""[{"type":"restore","revision":{{version.Revision}}}]""");
            ActionResult<EditTimelineResponse> restored = await controller.PatchEditTimeline(
                project.Id, row.Id, new PatchEditTimelineRequest(revision, ops.RootElement.Clone(), revisionBody.Document), CancellationToken.None);
            restored.Result.Should().BeOfType<OkObjectResult>($"version {version.Revision} must restore");
            revision++;
        }

        // And the history's restorable set is the very same set.
        ActionResult<EditTimelineHistoryResponse> history = await controller.GetHistory(project.Id, row.Id, null, null, CancellationToken.None);
        EditTimelineHistoryResponse historyBody = history.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<EditTimelineHistoryResponse>().Subject;
        historyBody.RestorableRevisions.Should().Contain(versions.Select(v => v.Revision));
    }

    // ───────────────────────────── prune and cap ─────────────────────────────

    private static TimelineSnapshotPruneService PruneService(string dbName, TimeProvider clock)
    {
        ServiceCollection services = new();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
        ServiceProvider provider = services.BuildServiceProvider();
        return new TimelineSnapshotPruneService(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<TimelineSnapshotPruneService>.Instance, clock);
    }

    [Fact]
    public async Task Prune_RemovesOnlyExpiredAutomaticCheckpoints_NeverNamedNeverRevision1_AndIsIdempotent()
    {
        string name = Guid.NewGuid().ToString();
        ManualClock clock = new(T0);
        Guid timelineId;
        using (InferenceApiDbContext db = NewContext(name))
        {
            (Project project, EditTimelineEntity row) = await SeedAsync(db, revision: 60);
            timelineId = row.Id;
            DateTime old = T0.UtcDateTime.AddDays(-45);
            AddSnapshot(db, row.Id, 1, EditTimelineSnapshotCause.Milestone, old);                                   // origin: never expires
            AddSnapshot(db, row.Id, 10, EditTimelineSnapshotCause.Named, old, name: "Pinned");                     // named: never expires
            AddSnapshot(db, row.Id, 20, EditTimelineSnapshotCause.Milestone, old, expiresAt: old + Thirty);        // expired
            AddSnapshot(db, row.Id, 30, EditTimelineSnapshotCause.Idle, old, expiresAt: old + Thirty);             // expired
            AddSnapshot(db, row.Id, 40, EditTimelineSnapshotCause.Idle, T0.UtcDateTime, expiresAt: T0.UtcDateTime + Thirty);   // fresh
            AddSnapshot(db, row.Id, 50, EditTimelineSnapshotCause.Named, old, name: "Even if mis-dated", expiresAt: old + Thirty); // named wins
            await db.SaveChangesAsync();
        }

        TimelineSnapshotPruneService prune = PruneService(name, clock);
        (await prune.SweepOnceAsync(CancellationToken.None)).Should().Be(2);
        (await prune.SweepOnceAsync(CancellationToken.None)).Should().Be(0, "idempotent");

        using InferenceApiDbContext check = NewContext(name);
        (await check.EditTimelineSnapshots.Select(s => s.Revision).OrderBy(r => r).ToListAsync()).Should().Equal(1, 10, 40, 50);
        timelineId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Growth_IsBounded_ManyIdleCheckpointsThenAPrune_LeaveOnlyUnexpiredAndNamed()
    {
        string name = Guid.NewGuid().ToString();
        ManualClock clock = new(T0);
        using (InferenceApiDbContext db = NewContext(name))
        {
            (Project project, EditTimelineEntity row) = await SeedAsync(db, revision: 1, updatedAt: T0.UtcDateTime);
            AddSnapshot(db, row.Id, 1, EditTimelineSnapshotCause.Milestone, T0.UtcDateTime);
            await db.SaveChangesAsync();
            EditTimelinesController controller = Controller(db, project.OwnerId, clock);
            // 120 edits, each after a 10 hour pause, so every previous revision is checkpointed (idle).
            int revision = 1;
            for (int i = 0; i < 120; i++)
            {
                clock.Advance(TimeSpan.FromHours(10));
                (await PatchAsync(controller, project, row, revision)).Result.Should().BeOfType<OkObjectResult>();
                revision++;
                if (i == 3) await controller.CreateVersion(project.Id, row.Id, new CreateEditTimelineVersionRequest("Keep me", null), CancellationToken.None);
            }

            // 120 idle checkpoints (revisions 2..121 minus the named one) have been taken over 50 days.
            (await db.EditTimelineSnapshots.CountAsync()).Should().BeGreaterThan(100);
        }

        // Jump to 60 days after the start; only checkpoints newer than 30 days survive the prune.
        clock.Advance(TimeSpan.FromDays(10));
        TimelineSnapshotPruneService prune = PruneService(name, clock);
        await prune.SweepOnceAsync(CancellationToken.None);

        using InferenceApiDbContext check = NewContext(name);
        List<EditTimelineSnapshot> left = await check.EditTimelineSnapshots.AsNoTracking().ToListAsync();
        left.Should().OnlyContain(s => s.Revision == 1 || s.Cause == "named" || s.ExpiresAt >= clock.Utc);
        left.Should().Contain(s => s.Cause == "named" && s.Name == "Keep me");
        left.Should().Contain(s => s.Revision == 1);
        left.Count(s => s.Cause == "idle").Should().BeLessThan(100, "idle checkpoints older than 30 days are gone");
    }

    [Fact]
    public async Task NamedVersions_AreCappedAt100PerTimeline_RenamingAndUnnamingStillWork()
    {
        using InferenceApiDbContext db = NewContext();
        ManualClock clock = new(T0);
        (Project project, EditTimelineEntity row) = await SeedAsync(db, revision: 500, updatedAt: T0.UtcDateTime);
        for (int rev = 1; rev <= EditTimelinesController.MaxNamedVersionsPerTimeline; rev++)
        {
            AddSnapshot(db, row.Id, rev, EditTimelineSnapshotCause.Named, T0.UtcDateTime.AddMinutes(rev), name: $"v{rev}");
        }

        AddSnapshot(db, row.Id, 200, EditTimelineSnapshotCause.Idle, T0.UtcDateTime, expiresAt: T0.UtcDateTime + Thirty);
        await db.SaveChangesAsync();
        EditTimelinesController controller = Controller(db, project.OwnerId, clock);

        // The 101st name is refused with a plain message, by creating a version of the live revision and by naming.
        ActionResult<EditTimelineVersionResponse> created = await controller.CreateVersion(project.Id, row.Id, new CreateEditTimelineVersionRequest("Too many", null), CancellationToken.None);
        BadRequestObjectResult refused = created.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        refused.Value!.GetType().GetProperty("message")!.GetValue(refused.Value).Should().Be("This timeline already has 100 named versions. Remove a name to add another.");
        (await controller.NameVersion(project.Id, row.Id, 200, new NameEditTimelineVersionRequest("Too many"), CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();

        // Renaming an already-named version is not a new name.
        (await controller.NameVersion(project.Id, row.Id, 5, new NameEditTimelineVersionRequest("Renamed"), CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();

        // Un-naming makes room.
        (await controller.UnnameVersion(project.Id, row.Id, 5, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
        (await controller.NameVersion(project.Id, row.Id, 200, new NameEditTimelineVersionRequest("Now fits"), CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Cap_Keeps200AutomaticCheckpointsPerTimeline_DroppingTheOldest_NeverNamedOrRevision1()
    {
        using InferenceApiDbContext db = NewContext();
        ManualClock clock = new(T0);
        (Project project, EditTimelineEntity row) = await SeedAsync(db, revision: 500, updatedAt: T0.UtcDateTime);
        AddSnapshot(db, row.Id, 1, EditTimelineSnapshotCause.Milestone, T0.UtcDateTime.AddDays(-1));
        AddSnapshot(db, row.Id, 2, EditTimelineSnapshotCause.Named, T0.UtcDateTime.AddDays(-1), name: "Old name");
        for (int rev = 3; rev < 3 + TimelineSnapshotRetention.MaxAutomaticPerTimeline; rev++)
        {
            AddSnapshot(db, row.Id, rev, EditTimelineSnapshotCause.Idle, T0.UtcDateTime.AddMinutes(rev), expiresAt: T0.UtcDateTime + Thirty);
        }

        await db.SaveChangesAsync();
        EditTimelinesController controller = Controller(db, project.OwnerId, clock);

        // One more idle checkpoint arrives through a real PATCH: the oldest automatic one (revision 3) goes.
        clock.Advance(TimeSpan.FromHours(1));
        (await PatchAsync(controller, project, row, 500)).Result.Should().BeOfType<OkObjectResult>();

        List<int> revisions = await db.EditTimelineSnapshots.AsNoTracking().OrderBy(s => s.Revision).Select(s => s.Revision).ToListAsync();
        revisions.Should().Contain([1, 2, 500], "origin, named and the new checkpoint stay");
        revisions.Should().NotContain(3, "the oldest automatic checkpoint was dropped");
        (await db.EditTimelineSnapshots.CountAsync(s => s.Cause == "idle")).Should().Be(TimelineSnapshotRetention.MaxAutomaticPerTimeline);

        // The prune job also trims a timeline that is somehow over the cap.
        for (int rev = 1000; rev < 1010; rev++)
        {
            AddSnapshot(db, row.Id, rev, EditTimelineSnapshotCause.Idle, T0.UtcDateTime.AddDays(1).AddMinutes(rev), expiresAt: T0.UtcDateTime + Thirty);
        }

        await db.SaveChangesAsync();
        int pruned = await TimelineSnapshotRetention.PruneAsync(db, clock.Utc, CancellationToken.None);
        pruned.Should().Be(10);
        (await db.EditTimelineSnapshots.CountAsync(s => s.Cause == "idle")).Should().Be(TimelineSnapshotRetention.MaxAutomaticPerTimeline);
        (await db.EditTimelineSnapshots.AnyAsync(s => s.Revision == 2 && s.Name == "Old name")).Should().BeTrue();
    }

    [Fact]
    public async Task AutoCheckpointDays_ConfigChangesTheExpiry()
    {
        using InferenceApiDbContext db = NewContext();
        ManualClock clock = new(T0);
        (Project project, EditTimelineEntity row) = await SeedAsync(db, revision: 2);
        EditTimelinesController controller = Controller(db, project.OwnerId, clock, retention: new EditingRetentionOptions { AutoCheckpointDays = 7 });

        EditTimelineVersionResponse body = ((await controller.CreateVersion(project.Id, row.Id, new CreateEditTimelineVersionRequest(null, "beforeProposal"), CancellationToken.None))
            .Result as ObjectResult)!.Value.Should().BeOfType<EditTimelineVersionResponse>().Subject;

        body.ExpiresAt.Should().Be(T0.UtcDateTime.AddDays(7));
    }
}
