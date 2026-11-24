#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>A hand-off package lists a source that is not served as missing, never with its key.</summary>
public sealed class HandOffSurfaceTests
{
    private static readonly EditTimelineSettings Settings = new(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

    private static EditTimeline Doc(SurfaceWorld w)
    {
        AssetRef[] refs =
        [
            new(AssetRefKind.ProjectFile, w.EarlierFile.Id), new(AssetRefKind.ProjectFile, w.LaterFile.Id), new(AssetRefKind.ProjectFile, w.Upload.Id),
            new(AssetRefKind.StepResult, null, w.EarlierResult.Id), new(AssetRefKind.StepResult, null, w.LaterResult.Id),
        ];
        Item[] clips = refs.Select((r, i) => (Item)new MediaClip(Guid.NewGuid(), "c" + i, i * 60, i * 60 + 60, r, 0)).ToArray();
        Track track = new(Guid.NewGuid(), TrackKind.Video, "V1", null, 64, false, true, false, false, null, clips);
        return new EditTimeline(Guid.NewGuid(), w.Project.Id, "Cut", Settings, [track], [], null, null);
    }

    private static async Task<HashSet<Guid>> Resolvable(SurfaceWorld w, InferenceApiDbContext? db = null)
    {
        Mock<IFileStorageService> storage = new();
        storage.Setup(x => x.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectMetadata(8, "video/mp4"));
        HandOffPlan plan = await HandOffPlanner.PlanAsync(db ?? w.Db, storage.Object, w.Project.Id, Doc(w), 50, CancellationToken.None);
        return plan.Sources.Where(s => s.Resolvable).Select(s => s.Reference.ProjectFileId ?? s.Reference.StepResultId!.Value).ToHashSet();
    }

    [Fact]
    public async Task Earlier_outputs_are_listed_as_missing_and_the_rest_is_packed()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        (await Resolvable(w)).Should().BeEquivalentTo([w.LaterFile.Id, w.Upload.Id, w.LaterResult.Id]);
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task Every_source_is_packed_when_the_project_keeps_nothing(SurfaceState state)
    {
        SurfaceWorld w = new SurfaceWorld().In(state);
        (await Resolvable(w)).Should().BeEquivalentTo([w.EarlierFile.Id, w.LaterFile.Id, w.Upload.Id, w.EarlierResult.Id, w.LaterResult.Id]);
    }

    [Fact]
    public async Task No_output_is_packed_when_the_marker_cannot_be_read()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = w.MarkerFailingDb();
        (await Resolvable(w, failing)).Should().BeEquivalentTo([w.Upload.Id]);
    }
}
