#nullable enable
using System;
using System.Collections.Generic;
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
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.EditTimelines;

/// <summary>
/// QA repro (T-043, uncommitted): the finalizer's "already written" check keys on (analysis step result, effect word) only,
/// so a second request on the SAME analysis with the same effect but a different invert or strength is reported applied
/// while the timeline is left unchanged.
/// </summary>
public class ObjectEffectApplierDedupeReproTests
{
    private static ObjectEffectApplier Applier(ServerEditFixture f)
    {
        Mock<IWorkflowExecutionService> executions = new();
        ObjectAnalysisService service = new(f.Db, executions.Object, f.Storage.Object, NullLogger<ObjectAnalysisService>.Instance, TimeProvider.System, null, null);
        return new ObjectEffectApplier(f.Db, service, f.Writer(), NullLogger<ObjectEffectApplier>.Instance);
    }

    private static async Task<ObjectEffectRequest> AddRowAsync(ServerEditFixture f, Guid executionId, bool invert, double strength, DateTime createdAt)
    {
        ObjectEffectRequest row = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = TestTenancy.OrgOf(f.OwnerId),
            ProjectId = f.ProjectId,
            ExecutionId = executionId,
            SourceKind = ObjectEffectRequest.SourceFile,
            SourceId = f.FileId.ToString("D"),
            Prompt = "the license plate",
            MatchScope = ObjectMatchScope.AllInstances.ToString(),
            Effect = "Blur",
            Strength = strength,
            Invert = invert,
            FeatherPx = 4,
            TimelineId = f.TimelineId,
            TimelineItemIdsJson = JsonSerializer.Serialize(f.ClipIds.Select(i => i.ToString("D"))),
            Status = ObjectEffectRequest.StatusQueued,
            CreatedByUserId = f.OwnerId,
            CreatedAt = createdAt
        };
        f.Db.ObjectEffectRequests.Add(row);
        await f.Db.SaveChangesAsync();
        return row;
    }

    [Fact]
    public async Task A_second_request_on_the_same_analysis_with_everything_except_the_object_is_written_not_silently_skipped()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync(clips: 1);
        Guid execution = Guid.NewGuid();
        await f.AddRunAsync(execution, ExecutionStatus.Passed);
        await AddRowAsync(f, execution, invert: false, strength: 0.8, DateTime.UtcNow.AddMinutes(-1));
        (await Applier(f).SweepOnceAsync(default)).Should().Be(1);

        // "Now blur everything except the license plate": the same analysis is reused (RequestAsync adds a row on the same run).
        ObjectEffectRequest second = await AddRowAsync(f, execution, invert: true, strength: 0.8, DateTime.UtcNow);
        await Applier(f).SweepOnceAsync(default);

        ObjectEffectRequest after = await f.Db.ObjectEffectRequests.AsNoTracking().SingleAsync(r => r.Id == second.Id);
        MediaClip clip = f.Clips(await f.ReadTimelineAsync()).Single();
        after.Status.Should().Be(ObjectEffectRequest.StatusApplied);
        clip.RegionEffects.Should().Contain(e => e.Mask.Invert,
            "the request is recorded as applied, so the inverted blur it asked for must be on the clip");
    }
}
