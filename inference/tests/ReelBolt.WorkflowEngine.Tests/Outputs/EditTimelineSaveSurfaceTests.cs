#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>
/// Saving a timeline checks each clip against the length of its source. A source the project keeps back counts as
/// unknown, so a clip that runs past it is not checked; a served source is, and the save is refused.
/// </summary>
public sealed class EditTimelineSaveSurfaceTests
{
    public enum Which { EarlierFile, LaterFile, Upload, EarlierResult, LaterResult }

    private static readonly EditTimelineSettings Settings = new(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

    private sealed class Rig
    {
        public SurfaceWorld W { get; } = new();
        public EditTimelineEntity Timeline { get; }

        public Rig()
        {
            Timeline = new EditTimelineEntity
            {
                Id = Guid.NewGuid(),
                ProjectId = W.Project.Id,
                Name = "Cut",
                DocumentJson = "{}",
                Revision = 1,
                CreatedAt = SurfaceWorld.Marker.AddDays(-9),
                UpdatedAt = SurfaceWorld.Marker.AddDays(-9),
            };
            W.Db.EditTimelines.Add(Timeline);
            // The two renders are named by no file row, so only the step result carries their key.
            W.EarlierResult.OutputStorageKey = $"projects/{W.Project.Id}/outputFiles/save-earlier.mp4";
            W.LaterResult.OutputStorageKey = $"projects/{W.Project.Id}/outputFiles/save-later.mp4";
            // Each source is two seconds long, so a clip that asks for twenty overruns it.
            foreach ((string key, Guid? fileId) in new (string, Guid?)[]
                     {
                         (W.EarlierFile.StorageKey, W.EarlierFile.Id), (W.LaterFile.StorageKey, W.LaterFile.Id), (W.Upload.StorageKey, W.Upload.Id),
                         (W.EarlierResult.OutputStorageKey!, null), (W.LaterResult.OutputStorageKey!, null),
                     })
            {
                W.Db.MediaDerivatives.Add(new MediaDerivative
                {
                    Id = Guid.NewGuid(),
                    ProjectId = W.Project.Id,
                    SourceKind = fileId is null ? MediaSourceKind.StepResult : MediaSourceKind.ProjectFile,
                    SourceProjectFileId = fileId,
                    SourceStorageKey = key,
                    Kind = MediaDerivativeKind.Thumbnail,
                    StorageKey = key + ".thumb.jpg",
                    StorageBucket = "reelforge",
                    MimeType = "image/jpeg",
                    CreatedAt = SurfaceWorld.Marker,
                    SourceProbeJson = "{\"durationSec\":2,\"fpsNum\":30,\"fpsDen\":1,\"width\":1920,\"height\":1080,\"hasAudio\":false}",
                });
            }
            W.Db.SaveChanges();
        }

        public ProjectFile File(Which w) => w switch { Which.EarlierFile => W.EarlierFile, Which.LaterFile => W.LaterFile, _ => W.Upload };

        public AssetRef RefOf(Which w) => w switch
        {
            Which.EarlierResult => new AssetRef(AssetRefKind.StepResult, null, W.EarlierResult.Id),
            Which.LaterResult => new AssetRef(AssetRefKind.StepResult, null, W.LaterResult.Id),
            _ => new AssetRef(AssetRefKind.ProjectFile, File(w).Id),
        };

        public EditTimeline Document(Which which, ShapeMask? mask = null)
        {
            MediaClip clip = new(Guid.NewGuid(), "c", 0, 600, RefOf(which), 0, Mask: mask);
            Track track = new(Guid.NewGuid(), TrackKind.Video, "V1", null, 64, false, true, false, false, null, [clip]);
            return new EditTimeline(Timeline.Id, W.Project.Id, "Cut", Settings, [track], [], null, null);
        }

        public async Task<ActionResult<EditTimelineResponse>> Save(Which which, InferenceApiDbContext? db = null, ShapeMask? mask = null, IFileStorageService? storage = null)
        {
            InferenceApiDbContext d = db ?? W.Db;
            EditTimelinesController controller = new(d, W.User, TestTenancy.Access(d, W.User), storage ?? Mock.Of<IFileStorageService>(), Mock.Of<IWorkflowExecutionService>());
            return await controller.PatchEditTimeline(W.Project.Id, Timeline.Id, new PatchEditTimelineRequest(1, null, Document(which, mask)), CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(Which.EarlierFile)]
    [InlineData(Which.EarlierResult)]
    public async Task A_clip_past_the_end_of_an_earlier_output_is_not_checked(Which which)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        (await r.Save(which)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [InlineData(Which.LaterFile)]
    [InlineData(Which.Upload)]
    [InlineData(Which.LaterResult)]
    public async Task A_clip_past_the_end_of_a_later_output_or_an_upload_is_refused_while_earlier_outputs_are_kept(Which which)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        (await r.Save(which)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(Which.EarlierFile, SurfaceState.Clear)]
    [InlineData(Which.EarlierFile, SurfaceState.ModerationOnly)]
    [InlineData(Which.EarlierResult, SurfaceState.Clear)]
    [InlineData(Which.EarlierResult, SurfaceState.ModerationOnly)]
    public async Task A_clip_past_the_end_of_the_earlier_output_is_refused_when_the_project_keeps_nothing(Which which, SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        (await r.Save(which)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(Which.LaterFile)]
    [InlineData(Which.LaterResult)]
    public async Task A_clip_past_the_end_of_an_output_is_not_checked_when_the_marker_cannot_be_read(Which which)
    {
        Rig r = new();
        r.W.In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = r.W.MarkerFailingDb();
        (await r.Save(which, failing)).Result.Should().BeOfType<OkObjectResult>();
    }

    private static ShapeMask MaskNaming(Guid analysisResultId) =>
        new(ShapeMask.SegmentationKind, AnalysisStepResultId: analysisResultId, TrackIds: ["g1"]);

    private static Mock<IFileStorageService> RecordingStorage(List<string> read)
    {
        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string key, CancellationToken _) =>
            {
                read.Add(key);
                return Task.FromResult<System.IO.Stream>(new System.IO.MemoryStream([(byte)'{', (byte)'}']));
            });
        return storage;
    }

    [Fact]
    public async Task A_mask_naming_a_later_analysis_reads_its_artifact()
    {
        Rig r = new();
        r.W.Step.StepType = StepType.VideoAnalyze;
        r.W.Db.SaveChanges();
        r.W.In(SurfaceState.Held);
        List<string> read = [];

        await r.Save(Which.Upload, mask: MaskNaming(r.W.LaterResult.Id), storage: RecordingStorage(read).Object);

        read.Should().Contain(r.W.LaterResult.ArtifactStorageKey!);
    }

    [Theory]
    [InlineData(Which.Upload)]
    [InlineData(Which.LaterFile)]
    public async Task A_mask_naming_an_earlier_analysis_does_not_read_its_artifact_while_the_project_keeps_it_back(Which which)
    {
        Rig r = new();
        r.W.Step.StepType = StepType.VideoAnalyze;
        r.W.Db.SaveChanges();
        r.W.In(SurfaceState.Held);
        List<string> read = [];

        ActionResult<EditTimelineResponse> saved = await r.Save(which, mask: MaskNaming(r.W.EarlierResult.Id), storage: RecordingStorage(read).Object);

        read.Should().NotContain(r.W.EarlierResult.ArtifactStorageKey!);
        saved.Result.Should().BeOfType<BadRequestObjectResult>();
    }
}
