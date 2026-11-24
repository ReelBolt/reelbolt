#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Services.EditTimelines;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.EditTimelines;

/// <summary>The server-side timeline writer: the same checks and commit as PATCH, without a browser.</summary>
public class EditTimelineWriterTests
{
    private static ServerEditMutation Rename(EditTimeline doc, string name) =>
        new(doc with { Name = name }, "[{\"type\":\"setName\"}]");

    [Fact]
    public async Task An_edit_bumps_the_revision_and_appends_one_op_row()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync();

        ServerEditResult r = await f.Writer().ApplyServerEditAsync(f.ProjectId, f.TimelineId, d => Rename(d, "New"), default);

        r.Outcome.Should().Be(ServerEditOutcome.Applied);
        r.Revision.Should().Be(2);
        (await f.RevisionAsync()).Should().Be(2);
        EditTimelineOp op = await f.Db.EditTimelineOps.SingleAsync();
        (op.TimelineId, op.Revision, op.OpsJson).Should().Be((f.TimelineId, 2, "[{\"type\":\"setName\"}]"));
        (await f.ReadTimelineAsync()).Name.Should().Be("New");
    }

    [Fact]
    public async Task A_mutation_with_nothing_to_do_writes_no_revision()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync();

        ServerEditResult r = await f.Writer().ApplyServerEditAsync(f.ProjectId, f.TimelineId, _ => null, default);

        r.Outcome.Should().Be(ServerEditOutcome.NoChange);
        (await f.RevisionAsync()).Should().Be(1);
        (await f.Db.EditTimelineOps.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Every_twentieth_revision_keeps_a_milestone_snapshot()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync(startRevision: 19);

        ServerEditResult r = await f.Writer().ApplyServerEditAsync(f.ProjectId, f.TimelineId, d => Rename(d, "At twenty"), default);

        r.Revision.Should().Be(20);
        EditTimelineSnapshot snapshot = await f.Db.EditTimelineSnapshots.SingleAsync(s => s.Revision == 20);
        snapshot.Cause.Should().Be(EditTimelineSnapshotCause.Milestone);
        snapshot.DocumentJson.Should().Contain("At twenty");
    }

    [Fact]
    public async Task A_concurrent_edit_is_retried_once_from_a_fresh_read()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync();
        int calls = 0;

        ServerEditResult r = await f.Writer().ApplyServerEditAsync(f.ProjectId, f.TimelineId, d =>
        {
            if (++calls == 1)
            {
                // Another writer commits between this read and this commit.
                using var other = f.NewDb();
                other.EditTimelines.Where(t => t.Id == f.TimelineId).ExecuteUpdate(s => s.SetProperty(t => t.Revision, 2));
            }

            return Rename(d, "Mine");
        }, default);

        r.Outcome.Should().Be(ServerEditOutcome.Applied);
        r.Revision.Should().Be(3, "the retry rebased onto the revision the other writer left");
        calls.Should().Be(2);
        (await f.Db.EditTimelineOps.SingleAsync()).Revision.Should().Be(3);
    }

    [Fact]
    public async Task A_second_lost_race_is_a_conflict_and_writes_nothing_of_its_own()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync();
        int calls = 0;

        ServerEditResult r = await f.Writer().ApplyServerEditAsync(f.ProjectId, f.TimelineId, d =>
        {
            calls++;
            using var other = f.NewDb();
            other.EditTimelines.Where(t => t.Id == f.TimelineId).ExecuteUpdate(s => s.SetProperty(t => t.Revision, t => t.Revision + 1));
            return Rename(d, "Never");
        }, default);

        r.Outcome.Should().Be(ServerEditOutcome.Conflict);
        calls.Should().Be(2);
        (await f.Db.EditTimelineOps.CountAsync()).Should().Be(0);
        (await f.ReadTimelineAsync()).Name.Should().Be("Cut");
    }

    [Fact]
    public async Task A_document_the_validator_refuses_is_surfaced_and_never_stored()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync();
        RegionClipEffect tooMany(int i) => new("blur", 0.5, new ShapeMask(ShapeMask.SegmentationKind, AnalysisStepResultId: Guid.NewGuid(), TrackIds: ["g1"]));

        ServerEditResult r = await f.Writer().ApplyServerEditAsync(f.ProjectId, f.TimelineId, d =>
            new ServerEditMutation(
                d with
                {
                    Tracks = [d.Tracks[0] with { Items = [ServerEditFixture.Clip(f.ClipIds[0], "Clip 1", 0, 30, f.FileId, Enumerable.Range(0, 5).Select(tooMany).ToList())] }]
                },
                "[]"), default);

        r.Outcome.Should().Be(ServerEditOutcome.Invalid);
        r.Violations.Should().Contain(v => v.Path.Contains("regionEffects", StringComparison.Ordinal));
        (await f.RevisionAsync()).Should().Be(1);
        (await f.Db.EditTimelineOps.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Every_read_is_pinned_to_the_project_so_another_projects_timeline_is_not_found()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync();
        int calls = 0;

        ServerEditResult r = await f.Writer().ApplyServerEditAsync(f.OtherProjectId, f.TimelineId, d => { calls++; return Rename(d, "Hijack"); }, default);

        r.Outcome.Should().Be(ServerEditOutcome.NotFound);
        calls.Should().Be(0);
        (await f.RevisionAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_oversized_op_log_is_refused()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync();

        ServerEditResult r = await f.Writer().ApplyServerEditAsync(f.ProjectId, f.TimelineId,
            d => new ServerEditMutation(d, JsonSerializer.Serialize(new string('x', 1024 * 1024 + 1))), default);

        r.Outcome.Should().Be(ServerEditOutcome.TooLarge);
        (await f.RevisionAsync()).Should().Be(1);
    }
}
