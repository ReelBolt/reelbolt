#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Services.ObjectAnalysis;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Tests.EditTimelines;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Qa;

/// <summary>
/// QA probe (background-activity T-031): the applier must never write over a timeline the user changed after the request.
/// Pins the reclaim path, which skips the change check.
/// </summary>
public class QaObjectEffectApplierReviewTests
{
    private static ObjectEffectApplier Applier(ServerEditFixture f) => new(
        f.Db,
        new ObjectAnalysisService(f.Db, Mock.Of<IWorkflowExecutionService>(), f.Storage.Object, NullLogger<ObjectAnalysisService>.Instance, TimeProvider.System, null, null),
        f.Writer(),
        NullLogger<ObjectEffectApplier>.Instance);

    [Fact]
    public async Task Qa_a_reclaimed_claim_whose_replica_died_before_writing_still_goes_to_review_when_the_user_edited_meanwhile()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync(clips: 2);
        DateTime created = DateTime.UtcNow.AddMinutes(-20);
        ObjectEffectRequest row = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = TestTenancy.OrgOf(f.OwnerId),
            ProjectId = f.ProjectId,
            ExecutionId = Guid.NewGuid(),
            SourceKind = ObjectEffectRequest.SourceFile,
            SourceId = f.FileId.ToString("D"),
            Prompt = "the license plate",
            MatchScope = "AllInstances",
            Effect = "Blur",
            Strength = 0.8,
            FeatherPx = 4,
            TimelineId = f.TimelineId,
            TimelineItemIdsJson = JsonSerializer.Serialize(f.ClipIds.Select(i => i.ToString("D"))),
            // A replica claimed it 10 minutes ago and died BEFORE writing anything.
            Status = ObjectEffectRequest.StatusApplied,
            AppliedRevision = null,
            FinalizedAt = DateTime.UtcNow.AddMinutes(-10),
            CreatedByUserId = f.OwnerId,
            CreatedAt = created
        };
        f.Db.ObjectEffectRequests.Add(row);
        // The user edited the timeline after launching the request (a committed op batch, not ours).
        f.Db.EditTimelineOps.Add(new EditTimelineOp { Id = Guid.NewGuid(), TimelineId = f.TimelineId, Revision = 7, OpsJson = "[]", CreatedAt = created.AddMinutes(5) });
        await f.Db.SaveChangesAsync();
        await f.AddRunAsync(row.ExecutionId, ExecutionStatus.Passed);
        int before = await f.RevisionAsync();

        await Applier(f).SweepOnceAsync(CancellationToken.None);

        ObjectEffectRequest after = await f.Db.ObjectEffectRequests.AsNoTracking().SingleAsync(r => r.Id == row.Id);
        after.Status.Should().Be(ObjectEffectRequest.StatusNeedsReview, "the timeline changed after the request and nothing of ours was written");
        (await f.RevisionAsync()).Should().Be(before, "no write over a changed timeline");
    }
}
