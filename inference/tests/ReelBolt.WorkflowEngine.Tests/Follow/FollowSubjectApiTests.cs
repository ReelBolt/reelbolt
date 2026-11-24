#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Follow;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Shared.Billing;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Follow;

/// <summary>The follow-subject routes: the free local tracking job and the follow path of an editor clip.</summary>
public class FollowSubjectApiTests
{
    private static readonly SubjectTrackRequestBody Box = new(2.5, new SubjectTrackBoxBody(0.4, 0.3, 0.1, 0.2));

    private static ObjectResult Obj<T>(ActionResult<T> r) => r.Result.Should().BeAssignableTo<ObjectResult>().Subject;

    // ---------------------------------------------------------------------------------------- Part A: start

    [Fact]
    public async Task Start_queues_one_hidden_feature_run_with_the_placed_region_and_no_egress()
    {
        using Harness h = await Harness.CreateAsync();

        ObjectResult accepted = Obj(await h.Tracks().Start(h.ProjectId, h.FileRef, Box, default));

        accepted.StatusCode.Should().Be(202);
        SubjectTrackQueuedResponse queued = (SubjectTrackQueuedResponse)accepted.Value!;
        queued.Status.Should().Be("queued");
        h.QueueCalls.Should().Be(1);
        h.LastQueuedKind.Should().Be(WorkflowDefinitionKind.ObjectAnalysis);
        h.LastQueuedExecutionId.Should().Be(queued.ExecutionId);

        WorkflowDefinition workflow = await h.Db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync();
        workflow.Kind.Should().Be(WorkflowDefinitionKind.ObjectAnalysis);
        workflow.Name.Should().Be("Follow subject");
        WorkflowStep step = workflow.Steps.Should().ContainSingle().Subject;
        step.StepType.Should().Be(StepType.VideoAnalyze);
        VideoAnalyzeStepConfig config = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(step.VideoAnalyzeConfigJson!, Harness.ConfigJson)!;
        config.AllowSourceMediaEgress.Should().BeFalse();
        config.Vision.Should().Be(VideoVisionMode.Off);
        config.Transcription.Should().Be(VideoTranscriptionMode.Off);
        config.Source.ProjectFileId.Should().Be(h.FileId);
        config.CallsAnyProvider.Should().BeFalse();
        ObjectTrackTarget target = config.TrackObjects.Should().ContainSingle().Subject;
        target.Method.Should().Be(ObjectTrackMethod.Feature);
        target.AtSec.Should().Be(2.5);
        // The engine's placed region order is TL, TR, BL, BR.
        target.Region.Should().Be(new VideoInsertSurfaceQuad(0.4, 0.3, 0.5, 0.3, 0.4, 0.5, 0.5, 0.5));
        RunRateClassifier.Classify([step]).Should().Be(RunRateBucket.Free);
        StepCreditDefaults.IsComputeOnly(step).Should().BeTrue();
        (await h.Db.ObjectEffectRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_identical_request_in_flight_answers_the_existing_execution()
    {
        using Harness h = await Harness.CreateAsync();
        SubjectTrackQueuedResponse first = (SubjectTrackQueuedResponse)Obj(await h.Tracks().Start(h.ProjectId, h.FileRef, Box, default)).Value!;

        ObjectResult again = Obj(await h.Tracks().Start(h.ProjectId, h.FileRef, Box, default));

        again.StatusCode.Should().Be(202);
        ((SubjectTrackQueuedResponse)again.Value!).ExecutionId.Should().Be(first.ExecutionId);
        h.QueueCalls.Should().Be(1);
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(1);

        // A different box is a new run.
        Obj(await h.Tracks().Start(h.ProjectId, h.FileRef, Box with { AtSec = 3 }, default)).StatusCode.Should().Be(202);
        h.QueueCalls.Should().Be(2);
    }

    [Fact]
    public async Task Start_authorization_and_gates()
    {
        using (Harness h = await Harness.CreateAsync(verified: false))
        {
            Obj(await h.Tracks().Start(h.ProjectId, h.FileRef, Box, default)).StatusCode.Should().Be(403);
            h.QueueCalls.Should().Be(0);
        }

        using (Harness h = await Harness.CreateAsync(role: OrganizationRole.Billing))
        {
            (await h.Tracks().Start(h.ProjectId, h.FileRef, Box, default)).Result.Should().BeOfType<ForbidResult>();
        }

        using (Harness h = await Harness.CreateAsync())
        {
            // A foreign user: 404. An unknown source: 404. An audio file: 422.
            (await h.Tracks(Guid.NewGuid()).Start(h.ProjectId, h.FileRef, Box, default)).Result.Should().BeOfType<NotFoundResult>();
            (await h.Tracks().Start(h.ProjectId, "file:" + Guid.NewGuid(), Box, default)).Result.Should().BeOfType<NotFoundResult>();
            (await h.Tracks().Start(h.ProjectId, "bogus", Box, default)).Result.Should().BeOfType<NotFoundResult>();
            Obj(await h.Tracks().Start(h.ProjectId, "file:" + h.AudioFileId, Box, default)).StatusCode.Should().Be(422);
            h.QueueCalls.Should().Be(0);
            (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
        }
    }

    [Theory]
    [InlineData(-1, 0.1, 0.1, 0.2, 0.2)]
    [InlineData(double.NaN, 0.1, 0.1, 0.2, 0.2)]
    [InlineData(1, 0.1, 0.1, 0.005, 0.2)]
    [InlineData(1, 0.1, 0.1, 0.2, 0.005)]
    [InlineData(1, -0.1, 0.1, 0.2, 0.2)]
    [InlineData(1, 0.9, 0.1, 0.2, 0.2)]
    [InlineData(1, 0.1, 0.95, 0.2, 0.2)]
    [InlineData(99, 0.1, 0.1, 0.2, 0.2)] // after the 10 s source ends
    public async Task Start_refuses_an_invalid_body_with_400_and_writes_nothing(double at, double x, double y, double w, double hh)
    {
        using Harness h = await Harness.CreateAsync();

        ObjectResult r = Obj(await h.Tracks().Start(h.ProjectId, h.FileRef, new SubjectTrackRequestBody(at, new SubjectTrackBoxBody(x, y, w, hh)), default));

        r.StatusCode.Should().Be(400);
        h.QueueCalls.Should().Be(0);
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Start_with_no_box_is_400()
    {
        using Harness h = await Harness.CreateAsync();
        Obj(await h.Tracks().Start(h.ProjectId, h.FileRef, new SubjectTrackRequestBody(1, null), default)).StatusCode.Should().Be(400);
        Obj(await h.Tracks().Start(h.ProjectId, h.FileRef, null, default)).StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Start_checks_credits_when_billing_is_on_and_rate_limits_per_user()
    {
        using (Harness h = await Harness.CreateAsync(billingPoor: true))
        {
            ObjectResult r = Obj(await h.Tracks().Start(h.ProjectId, h.FileRef, Box, default));
            r.StatusCode.Should().Be(402);
            h.QueueCalls.Should().Be(0);
            h.LastPreflightStep.Should().NotBeNull();
            StepCreditDefaults.IsComputeOnly(h.LastPreflightStep!).Should().BeTrue();
        }

        using (Harness h = await Harness.CreateAsync())
        {
            await h.SeedRecentRunsAsync(60, SubjectTrackService.WorkflowName);
            Obj(await h.Tracks().Start(h.ProjectId, h.FileRef, Box, default)).StatusCode.Should().Be(429);
            h.QueueCalls.Should().Be(0);
        }
    }

    [Fact]
    public async Task A_refused_plan_start_rolls_back_the_hidden_workflow()
    {
        using Harness h = await Harness.CreateAsync();
        h.QueueThrows = new PlanLimitException(new PlanGateViolation("PLAN_LIMIT", "x", "limit", 402));
        Obj(await h.Tracks().Start(h.ProjectId, h.FileRef, Box, default)).StatusCode.Should().Be(402);
    }

    // ---------------------------------------------------------------------------------------- Part A: status

    [Fact]
    public async Task Status_maps_queued_running_failed_and_cancelled()
    {
        using Harness h = await Harness.CreateAsync();
        SubjectTrackQueuedResponse q = (SubjectTrackQueuedResponse)Obj(await h.Tracks().Start(h.ProjectId, h.FileRef, Box, default)).Value!;

        async Task<SubjectTrackStatusResponse> Get() => (SubjectTrackStatusResponse)Obj(await h.Tracks().Get(h.ProjectId, h.FileRef, q.ExecutionId, default)).Value!;

        (await Get()).Status.Should().Be("queued");
        await h.SetRunAsync(q.ExecutionId, ExecutionStatus.Running);
        (await Get()).Status.Should().Be("running");
        await h.SetRunAsync(q.ExecutionId, ExecutionStatus.Cancelled);
        (await Get()).Should().Match<SubjectTrackStatusResponse>(s => s.Status == "failed" && s.FailureCode == "RUN_CANCELLED");
        await h.SetRunAsync(q.ExecutionId, ExecutionStatus.Failed);
        SubjectTrackStatusResponse failed = await Get();
        failed.Status.Should().Be("failed");
        failed.Message.Should().NotContain("Segmentation").And.NotContain("projects/");
    }

    [Fact]
    public async Task Status_succeeds_with_the_best_feature_track_and_decimates_to_2000_boxes()
    {
        using Harness h = await Harness.CreateAsync();
        SubjectTrackQueuedResponse q = (SubjectTrackQueuedResponse)Obj(await h.Tracks().Start(h.ProjectId, h.FileRef, Box, default)).Value!;
        Guid result = await h.CompleteRunAsync(q.ExecutionId, Harness.Artifact(h.FileId,
            Harness.Region("r0", 20), Harness.Region("r1", 3000), Harness.Region("r2", 50, method: "chroma")));

        SubjectTrackStatusResponse s = (SubjectTrackStatusResponse)Obj(await h.Tracks().Get(h.ProjectId, h.FileRef, q.ExecutionId, default)).Value!;

        s.Status.Should().Be("succeeded");
        s.AnalysisStepResultId.Should().Be(result);
        s.Track!.TrackId.Should().Be("r1");
        s.Track.Boxes.Should().HaveCount(2000);
        s.Track.Boxes[0].T.Should().Be(0);
        s.Track.Boxes[^1].T.Should().BeApproximately(2999 * 0.01, 1e-9);
        s.Track.Boxes[0].W.Should().BeApproximately(0.1, 1e-9);
    }

    [Fact]
    public async Task Status_without_a_feature_track_is_failed_FOLLOW_SUBJECT_NOT_FOUND()
    {
        using Harness h = await Harness.CreateAsync();
        SubjectTrackQueuedResponse q = (SubjectTrackQueuedResponse)Obj(await h.Tracks().Start(h.ProjectId, h.FileRef, Box, default)).Value!;
        await h.CompleteRunAsync(q.ExecutionId, Harness.Artifact(h.FileId));

        SubjectTrackStatusResponse s = (SubjectTrackStatusResponse)Obj(await h.Tracks().Get(h.ProjectId, h.FileRef, q.ExecutionId, default)).Value!;

        s.Status.Should().Be("failed");
        s.FailureCode.Should().Be("FOLLOW_SUBJECT_NOT_FOUND");
        s.Message.Should().Be("Could not follow that subject. Try a tighter box on a clear frame.");
    }

    [Fact]
    public async Task Status_is_404_for_a_foreign_or_unrelated_run_and_403_below_view()
    {
        using Harness h = await Harness.CreateAsync();
        SubjectTrackQueuedResponse q = (SubjectTrackQueuedResponse)Obj(await h.Tracks().Start(h.ProjectId, h.FileRef, Box, default)).Value!;

        (await h.Tracks(Guid.NewGuid()).Get(h.ProjectId, h.FileRef, q.ExecutionId, default)).Result.Should().BeOfType<NotFoundResult>();
        (await h.Tracks().Get(h.ProjectId, h.FileRef, Guid.NewGuid(), default)).Result.Should().BeOfType<NotFoundResult>();
        // The run exists but names another media.
        (await h.Tracks().Get(h.ProjectId, "file:" + Guid.NewGuid(), q.ExecutionId, default)).Result.Should().BeOfType<NotFoundResult>();
        // An ordinary object-analysis (segmentation) run is not a follow run.
        Guid other = await h.AddSegmentationRunAsync();
        (await h.Tracks().Get(h.ProjectId, h.FileRef, other, default)).Result.Should().BeOfType<NotFoundResult>();

        using Harness billing = await Harness.CreateAsync(role: OrganizationRole.Billing);
        (await billing.Tracks().Get(billing.ProjectId, billing.FileRef, Guid.NewGuid(), default)).Result.Should().BeOfType<ForbidResult>();
    }

    // ---------------------------------------------------------------------------------------- Part B: follow

    private static FollowRequestBody Follow(Harness h, Guid stepResult, string trackId = "r0", Guid? item = null, int? revision = null) =>
        new(item ?? h.ClipId, revision, new FollowTrackBody(stepResult, trackId), null, null, null, null);

    [Fact]
    public async Task Follow_computes_source_anchored_keys_in_range_and_writes_nothing()
    {
        using Harness h = await Harness.CreateAsync();
        Guid result = await h.AddArtifactResultAsync(Harness.Artifact(h.FileId, Harness.Region("r0", 300, sweep: true)));
        int revisionBefore = (await h.Db.EditTimelines.SingleAsync()).Revision;

        ObjectResult ok = Obj(await h.Follow().Follow(h.ProjectId, h.TimelineId, Follow(h, result), default));

        ok.Value.Should().BeOfType<FollowResponse>();
        FollowResponse f = (FollowResponse)ok.Value!;
        f.Revision.Should().Be(revisionBefore);
        f.ItemId.Should().Be(h.ClipId);
        f.Keys.Count.Should().BeInRange(2, 256);
        f.Keys[0].SourceSec.Should().BeApproximately(2.0, 1e-6);   // clip's source in: frame 60 at 30 fps
        f.Keys[^1].SourceSec.Should().BeApproximately(5.0, 1e-6);  // 90 frames at 1x
        f.Keys.Should().OnlyContain(k => Math.Abs(k.PositionX) <= 2 && Math.Abs(k.PositionY) <= 2 && k.Scale > 0 && k.Scale <= 8);
        f.Keys.Select(k => k.SourceSec).Should().BeInAscendingOrder();
        f.Assumed.Should().Be(new FollowAssumedResponse(1080, 1920, 1920, 1080));
        f.ZoomCap.Should().BeGreaterThanOrEqualTo(1);

        // Deterministic and write-free.
        FollowResponse again = (FollowResponse)Obj(await h.Follow().Follow(h.ProjectId, h.TimelineId, Follow(h, result), default)).Value!;
        JsonSerializer.Serialize(again).Should().Be(JsonSerializer.Serialize(f));
        (await h.Db.EditTimelines.SingleAsync()).Revision.Should().Be(revisionBefore);
        h.QueueCalls.Should().Be(0);
    }

    [Fact]
    public async Task Follow_range_uses_constant_speed()
    {
        using Harness h = await Harness.CreateAsync(clip: new ClipSpec(Rate: 2.0));
        Guid result = await h.AddArtifactResultAsync(Harness.Artifact(h.FileId, Harness.Region("r0", 300, sweep: true)));

        FollowResponse f = (FollowResponse)Obj(await h.Follow().Follow(h.ProjectId, h.TimelineId, Follow(h, result), default)).Value!;

        f.Keys[^1].SourceSec.Should().BeApproximately(2.0 + 6.0, 1e-6); // 90 timeline frames at 2x = 180 source frames
    }

    [Fact]
    public async Task Follow_refuses_a_clip_that_is_cropped_reframed_stabilized_or_retimed()
    {
        (ClipSpec Edit, string Code, string Word)[] cases =
        [
            (new ClipSpec(Crop: new ClipCrop(Left: 0.1)), "FOLLOW_CLIP_CONFLICT", "crop"),
            (new ClipSpec(Reframe: new ReframeTarget(0.5, 0.5)), "FOLLOW_CLIP_CONFLICT", "reframe"),
            (new ClipSpec(Stabilize: true), "FOLLOW_CLIP_CONFLICT", "stabiliz"),
            (new ClipSpec(Hold: true), "FOLLOW_CLIP_SPEED", "freeze"),
        ];
        foreach ((ClipSpec edit, string code, string word) in cases)
        {
            using Harness h = await Harness.CreateAsync(clip: edit);
            Guid result = await h.AddArtifactResultAsync(Harness.Artifact(h.FileId, Harness.Region("r0", 50)));
            ObjectResult r = Obj(await h.Follow().Follow(h.ProjectId, h.TimelineId, Follow(h, result), default));
            r.StatusCode.Should().Be(409);
            string json = JsonSerializer.Serialize(r.Value);
            json.Should().Contain(code);
            json.ToLowerInvariant().Should().Contain(word);
        }
    }

    [Fact]
    public async Task Follow_allows_reverse_and_constant_speed()
    {
        using Harness h = await Harness.CreateAsync(clip: new ClipSpec(Rate: 0.5, Reverse: true));
        Guid result = await h.AddArtifactResultAsync(Harness.Artifact(h.FileId, Harness.Region("r0", 300, sweep: true)));
        Obj(await h.Follow().Follow(h.ProjectId, h.TimelineId, Follow(h, result), default)).Value.Should().BeOfType<FollowResponse>();
    }

    [Fact]
    public async Task Follow_track_and_clip_refusals()
    {
        using Harness h = await Harness.CreateAsync();
        Guid good = await h.AddArtifactResultAsync(Harness.Artifact(h.FileId, Harness.Region("r0", 50), Harness.Region("r9", 0)));

        // Unknown clip.
        ObjectResult noClip = Obj(await h.Follow().Follow(h.ProjectId, h.TimelineId, Follow(h, good, item: Guid.NewGuid()), default));
        noClip.StatusCode.Should().Be(404);
        JsonSerializer.Serialize(noClip.Value).Should().Contain("FOLLOW_CLIP_NOT_FOUND");

        // Unknown or foreign step result: bare 404.
        (await h.Follow().Follow(h.ProjectId, h.TimelineId, Follow(h, Guid.NewGuid()), default)).Result.Should().BeOfType<NotFoundResult>();

        // Unknown track id, and an id that is not a track id at all.
        Obj(await h.Follow().Follow(h.ProjectId, h.TimelineId, Follow(h, good, "r5"), default)).StatusCode.Should().Be(404);
        Obj(await h.Follow().Follow(h.ProjectId, h.TimelineId, Follow(h, good, "zz"), default)).StatusCode.Should().Be(404);
        JsonSerializer.Serialize(Obj(await h.Follow().Follow(h.ProjectId, h.TimelineId, Follow(h, good, "g0"), default)).Value)
            .Should().Contain("FOLLOW_TRACK_NOT_FOUND");

        // A track with no boxes.
        ObjectResult empty = Obj(await h.Follow().Follow(h.ProjectId, h.TimelineId, Follow(h, good, "r9"), default));
        empty.StatusCode.Should().Be(422);
        JsonSerializer.Serialize(empty.Value).Should().Contain("FOLLOW_TRACK_EMPTY");

        // A track made from another video.
        Guid other = await h.AddArtifactResultAsync(Harness.Artifact(Guid.NewGuid(), Harness.Region("r0", 50)));
        ObjectResult otherSource = Obj(await h.Follow().Follow(h.ProjectId, h.TimelineId, Follow(h, other), default));
        otherSource.StatusCode.Should().Be(422);
        JsonSerializer.Serialize(otherSource.Value).Should().Contain("FOLLOW_TRACK_OTHER_SOURCE");

        // A bad enum is 400; a missing timeline is 404; a role below Edit is 403.
        Obj(await h.Follow().Follow(h.ProjectId, h.TimelineId, Follow(h, good) with { Tightness = "Huge" }, default)).StatusCode.Should().Be(400);
        (await h.Follow().Follow(h.ProjectId, Guid.NewGuid(), Follow(h, good), default)).Result.Should().BeOfType<NotFoundResult>();
        (await h.Follow(Guid.NewGuid()).Follow(h.ProjectId, h.TimelineId, Follow(h, good), default)).Result.Should().BeOfType<NotFoundResult>();
        using Harness billing = await Harness.CreateAsync(role: OrganizationRole.Billing);
        (await billing.Follow().Follow(billing.ProjectId, billing.TimelineId, Follow(billing, good), default)).Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Follow_reads_the_requested_revision_snapshot()
    {
        using Harness h = await Harness.CreateAsync();
        Guid result = await h.AddArtifactResultAsync(Harness.Artifact(h.FileId, Harness.Region("r0", 300, sweep: true)));
        await h.BumpRevisionWithSnapshotAsync(oldRevision: 1);

        FollowResponse old = (FollowResponse)Obj(await h.Follow().Follow(h.ProjectId, h.TimelineId, Follow(h, result, revision: 1), default)).Value!;
        old.Revision.Should().Be(1);
        FollowResponse latest = (FollowResponse)Obj(await h.Follow().Follow(h.ProjectId, h.TimelineId, Follow(h, result), default)).Value!;
        latest.Revision.Should().Be(2);
        (await h.Follow().Follow(h.ProjectId, h.TimelineId, Follow(h, result, revision: 7), default)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Follow_hold_check_runs_only_for_a_result_of_this_project()
    {
        using Harness h = await Harness.CreateAsync();
        Guid mine = await h.AddArtifactResultAsync(Harness.Artifact(h.FileId, Harness.Region("r0", 50)));
        using Harness foreign = await Harness.CreateAsync();
        Guid theirs = await foreign.AddArtifactResultAsync(Harness.Artifact(foreign.FileId, Harness.Region("r0", 50)));
        // Hold the foreign project's outputs: a foreign id must still be the plain empty 404.
        Project fp = await foreign.Db.Projects.IgnoreQueryFilters().SingleAsync();
        fp.CsamOutputHoldAt = DateTime.UtcNow.AddDays(-1);
        foreach (WorkflowStepResult r in await foreign.Db.WorkflowStepResults.ToListAsync()) r.ExecutedAt = DateTime.UtcNow.AddDays(-2);
        await foreign.Db.SaveChangesAsync();

        (await foreign.Follow().Follow(foreign.ProjectId, foreign.TimelineId, Follow(foreign, theirs), default)).Result.Should().BeAssignableTo<ObjectResult>();
        (await h.Follow().Follow(h.ProjectId, h.TimelineId, Follow(h, theirs), default)).Result.Should().BeOfType<NotFoundResult>();

        // A held result in this project answers its hold response.
        Project p = await h.Db.Projects.IgnoreQueryFilters().SingleAsync();
        p.CsamOutputHoldAt = DateTime.UtcNow.AddDays(-1);
        foreach (WorkflowStepResult r in await h.Db.WorkflowStepResults.ToListAsync()) r.ExecutedAt = DateTime.UtcNow.AddDays(-2);
        await h.Db.SaveChangesAsync();
        (await h.Follow().Follow(h.ProjectId, h.TimelineId, Follow(h, mine), default)).Result.Should().BeOfType<ObjectResult>()
            .Which.Value.Should().NotBeOfType<FollowResponse>();
    }

    [Fact]
    public async Task Follow_runs_and_paid_object_searches_have_separate_run_rate_counts()
    {
        // 60 follow runs block follow (Free bucket) but not a paid object search.
        using (Harness h = await Harness.CreateAsync())
        {
            await h.SeedRecentRunsAsync(60, SubjectTrackService.WorkflowName);
            Obj(await h.Tracks().Start(h.ProjectId, h.FileRef, Box, default)).StatusCode.Should().Be(429);
            (await h.ObjectAnalysisRateRefusalAsync()).Should().BeNull();
        }

        // 10 paid object searches do not count against follow runs.
        using (Harness h = await Harness.CreateAsync())
        {
            await h.SeedRecentRunsAsync(10, "Find object: car");
            Obj(await h.Tracks().Start(h.ProjectId, h.FileRef, Box, default)).StatusCode.Should().Be(202);
            (await h.ObjectAnalysisRateRefusalAsync()).Should().NotBeNull();
        }

        // 10 follow runs leave the paid allowance untouched.
        using (Harness h = await Harness.CreateAsync())
        {
            await h.SeedRecentRunsAsync(10, SubjectTrackService.WorkflowName);
            (await h.ObjectAnalysisRateRefusalAsync()).Should().BeNull();
        }
    }

    // ---------------------------------------------------------------------------------------- harness

    private sealed record ClipSpec(
        double Rate = 1.0, bool Reverse = false, bool Hold = false, bool Stabilize = false, ClipCrop? Crop = null, ReframeTarget? Reframe = null);

    private sealed class Harness : IDisposable
    {
        public static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        public required InferenceApiDbContext Db { get; init; }
        public Guid ProjectId { get; init; }
        public Guid OwnerId { get; init; }
        public Guid FileId { get; init; }
        public Guid AudioFileId { get; init; }
        public Guid TimelineId { get; init; }
        public Guid ClipId { get; init; }
        public string FileRef => "file:" + FileId;
        public int QueueCalls { get; private set; }
        public WorkflowDefinitionKind? LastQueuedKind { get; private set; }
        public Guid? LastQueuedExecutionId { get; private set; }
        public WorkflowStep? LastPreflightStep { get; private set; }
        public Exception? QueueThrows { get; set; }
        private bool _verified;
        private OrganizationRole _role;
        private bool _billingPoor;
        private string _artifact = "{}";
        private readonly Mock<IFileStorageService> _storage = new();

        public void Dispose() => Db.Dispose();

        public static async Task<Harness> CreateAsync(
            bool verified = true, OrganizationRole role = OrganizationRole.Member, bool billingPoor = false, ClipSpec? clip = null)
        {
            InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

            Guid owner = Guid.NewGuid();
            Project project = new()
            {
                Id = Guid.NewGuid(),
                Name = "p",
                OwnerId = owner,
                OrganizationId = TestTenancy.OrgOf(owner),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Projects.Add(project);
            string key = $"projects/{project.Id}/userFiles/v.mp4";
            ProjectFile video = new() { Id = Guid.NewGuid(), ProjectId = project.Id, OriginalFileName = "v.mp4", StorageKey = key, MimeType = "video/mp4" };
            ProjectFile audio = new() { Id = Guid.NewGuid(), ProjectId = project.Id, OriginalFileName = "a.mp3", StorageKey = key + ".mp3", MimeType = "audio/mpeg" };
            db.ProjectFiles.AddRange(video, audio);
            db.MediaDerivatives.Add(new MediaDerivative
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                SourceProjectFileId = video.Id,
                SourceStorageKey = key,
                Kind = MediaDerivativeKind.Proxy,
                StorageKey = "d/proxy.mp4",
                CacheKey = "k",
                SourceProbeJson = new MediaSourceProbe(10, 30000, 1000, 1920, 1080, "h264", "aac", "mp4", 2, 48000, null, true).ToJson()
            });
            db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "VideoTransform", AgentType = AgentType.VideoTransform, IsBuiltIn = true });

            // A 1080x1920 timeline at 30 fps with one clip: record frames 0..90, source in frame 60.
            Guid clipId = Guid.NewGuid();
            ClipSpec spec = clip ?? new ClipSpec();
            MediaClip mc = new(clipId, "clip", 0, 90, new AssetRef(AssetRefKind.ProjectFile, video.Id), SourceIn: 60,
                PlaybackRate: spec.Rate, Crop: spec.Crop, Reverse: spec.Reverse, Hold: spec.Hold, Stabilize: spec.Stabilize, Reframe: spec.Reframe);
            Track track = new(Guid.NewGuid(), TrackKind.Video, "V1", null, 1, false, true, false, false, null, [mc]);
            EditTimeline doc = new(2, Guid.NewGuid(), project.Id, "t",
                new EditTimelineSettings(1080, 1920, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0), [track], []);
            EditTimelineEntity entity = new()
            {
                Id = doc.Id,
                ProjectId = project.Id,
                Name = "t",
                DocumentJson = JsonSerializer.Serialize(doc, EditTimelineJson.Options),
                Revision = 1
            };
            db.EditTimelines.Add(entity);
            await db.SaveChangesAsync();

            return new Harness
            {
                Db = db,
                ProjectId = project.Id,
                OwnerId = owner,
                FileId = video.Id,
                AudioFileId = audio.Id,
                TimelineId = entity.Id,
                ClipId = clipId,
                _verified = verified,
                _role = role,
                _billingPoor = billingPoor
            };
        }

        private Mock<ICurrentUser> User(Guid? user = null)
        {
            Guid id = user ?? OwnerId;
            Mock<ICurrentUser> u = new();
            u.SetupGet(x => x.UserId).Returns(id);
            u.SetupGet(x => x.Email).Returns("t@example.com");
            u.SetupGet(x => x.IsAuthenticated).Returns(true);
            u.SetupGet(x => x.EmailVerified).Returns(_verified);
            u.SetupGet(x => x.OrganizationId).Returns(TestTenancy.OrgOf(id));
            u.SetupGet(x => x.OrgRole).Returns(_role);
            return u;
        }

        private void SetupStorage() =>
            _storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult((Stream)new MemoryStream(Encoding.UTF8.GetBytes(_artifact))));

        public SubjectTracksController Tracks(Guid? user = null)
        {
            Mock<ICurrentUser> u = User(user);
            Mock<IWorkflowExecutionService> executions = new();
            executions.Setup(e => e.QueueAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(),
                    It.IsAny<ExecutionInitiator>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<RunChoices?>(),
                    It.IsAny<WorkflowDefinitionKind>(), It.IsAny<Guid?>(), It.IsAny<Func<CancellationToken, Task>?>()))
                .ReturnsAsync((Guid projectId, Guid workflowId, Guid userId, string? _, ExecutionInitiator _, CancellationToken _, bool _, RunChoices? _,
                    WorkflowDefinitionKind kind, Guid? executionId, Func<CancellationToken, Task>? _) =>
                {
                    if (QueueThrows is not null) throw QueueThrows;
                    QueueCalls++;
                    LastQueuedKind = kind;
                    LastQueuedExecutionId = executionId;
                    Db.WorkflowExecutions.Add(new WorkflowExecution
                    {
                        Id = executionId!.Value,
                        WorkflowDefinitionId = workflowId,
                        ProjectId = projectId,
                        OrganizationId = TestTenancy.OrgOf(OwnerId),
                        InitiatedByUserId = userId,
                        Status = ExecutionStatus.Queued,
                        QueuedAt = DateTime.UtcNow
                    });
                    Db.SaveChanges();
                    return new WorkflowExecutionResponse(executionId.Value, workflowId, "Queued", null, null, 0, null, null, null, [], []);
                });
            SetupStorage();

            Mock<IWorkflowCostEstimator> costs = new();
            costs.SetupGet(c => c.BillingEnabled).Returns(_billingPoor);
            costs.Setup(c => c.PreflightAsync(It.IsAny<Guid?>(), It.IsAny<IReadOnlyList<WorkflowStep>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid? _, IReadOnlyList<WorkflowStep> steps, CancellationToken _) =>
                {
                    LastPreflightStep = steps[0];
                    return new WorkflowPreflightResult(
                        WorkflowPreflightOutcome.InsufficientCredits, new WorkflowCostEstimate(true, false, 500, 900, 900, []), 100);
                });

            SubjectTrackService service = new(Db, executions.Object, _storage.Object, NullLogger<SubjectTrackService>.Instance, TimeProvider.System, costs.Object);
            return new SubjectTracksController(u.Object, TestTenancy.Access(Db, u.Object), service);
        }

        public EditTimelineFollowController Follow(Guid? user = null)
        {
            Mock<ICurrentUser> u = User(user);
            SetupStorage();
            FollowPathService service = new(Db, _storage.Object, NullLogger<FollowPathService>.Instance);
            return new EditTimelineFollowController(TestTenancy.Access(Db, u.Object), service);
        }

        public async Task SetRunAsync(Guid executionId, ExecutionStatus status)
        {
            WorkflowExecution e = await Db.WorkflowExecutions.SingleAsync(x => x.Id == executionId);
            e.Status = status;
            await Db.SaveChangesAsync();
        }

        /// <summary>Marks the run passed and gives it a VideoAnalyze step result whose artifact is <paramref name="artifact"/>.</summary>
        public async Task<Guid> CompleteRunAsync(Guid executionId, string artifact)
        {
            await SetRunAsync(executionId, ExecutionStatus.Passed);
            WorkflowExecution e = await Db.WorkflowExecutions.SingleAsync(x => x.Id == executionId);
            WorkflowStep step = await Db.WorkflowSteps.SingleAsync(x => x.WorkflowDefinitionId == e.WorkflowDefinitionId);
            _artifact = artifact;
            Guid result = Guid.NewGuid();
            Db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = result,
                WorkflowExecutionId = executionId,
                WorkflowStepId = step.Id,
                Status = StepStatus.Completed,
                CompletedAt = DateTime.UtcNow,
                ArtifactStorageKey = $"projects/{ProjectId}/agentFiles/video-analysis/r/a.json"
            });
            await Db.SaveChangesAsync();
            return result;
        }

        /// <summary>A step result of this project (own hidden run) carrying <paramref name="artifact"/>, for the follow route.</summary>
        public async Task<Guid> AddArtifactResultAsync(string artifact)
        {
            Guid definition = Guid.NewGuid(), stepId = Guid.NewGuid(), execution = Guid.NewGuid();
            Db.WorkflowDefinitions.Add(new WorkflowDefinition
            {
                Id = definition,
                ProjectId = ProjectId,
                Name = "w",
                Kind = WorkflowDefinitionKind.ObjectAnalysis,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            Db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = execution,
                WorkflowDefinitionId = definition,
                ProjectId = ProjectId,
                OrganizationId = TestTenancy.OrgOf(OwnerId),
                Status = ExecutionStatus.Passed
            });
            Db.WorkflowSteps.Add(new WorkflowStep { Id = stepId, WorkflowDefinitionId = definition, StepOrder = 1, StepType = StepType.VideoAnalyze });
            _artifact = artifact;
            Guid result = Guid.NewGuid();
            Db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = result,
                WorkflowExecutionId = execution,
                WorkflowStepId = stepId,
                Status = StepStatus.Completed,
                CompletedAt = DateTime.UtcNow,
                ArtifactStorageKey = $"projects/{ProjectId}/agentFiles/video-analysis/r/a.json"
            });
            await Db.SaveChangesAsync();
            return result;
        }

        public async Task<Guid> AddSegmentationRunAsync()
        {
            Guid definition = Guid.NewGuid(), stepId = Guid.NewGuid(), execution = Guid.NewGuid();
            Db.WorkflowDefinitions.Add(new WorkflowDefinition
            {
                Id = definition,
                ProjectId = ProjectId,
                Name = "w",
                Kind = WorkflowDefinitionKind.ObjectAnalysis,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            Db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = execution,
                WorkflowDefinitionId = definition,
                ProjectId = ProjectId,
                OrganizationId = TestTenancy.OrgOf(OwnerId),
                Status = ExecutionStatus.Queued
            });
            string config = JsonSerializer.Serialize(new VideoAnalyzeStepConfig(
                Version: 1, Source: new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: FileId),
                TrackObjects: [new ObjectTrackTarget("car", Method: ObjectTrackMethod.Segmentation)]), ConfigJson);
            Db.WorkflowSteps.Add(new WorkflowStep
            {
                Id = stepId,
                WorkflowDefinitionId = definition,
                StepOrder = 1,
                StepType = StepType.VideoAnalyze,
                VideoAnalyzeConfigJson = config
            });
            await Db.SaveChangesAsync();
            return execution;
        }

        public async Task SeedRecentRunsAsync(int count, string name = "w")
        {
            Guid definition = Guid.NewGuid();
            Db.WorkflowDefinitions.Add(new WorkflowDefinition
            {
                Id = definition,
                ProjectId = ProjectId,
                Name = name,
                Kind = WorkflowDefinitionKind.ObjectAnalysis,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            for (int i = 0; i < count; i++)
            {
                Db.WorkflowExecutions.Add(new WorkflowExecution
                {
                    Id = Guid.NewGuid(),
                    WorkflowDefinitionId = definition,
                    ProjectId = ProjectId,
                    OrganizationId = TestTenancy.OrgOf(OwnerId),
                    InitiatedByUserId = OwnerId,
                    Status = ExecutionStatus.Passed,
                    QueuedAt = DateTime.UtcNow.AddMinutes(-5)
                });
            }

            await Db.SaveChangesAsync();
        }

        public async Task<ReelBolt.Inference.Api.Services.ObjectAnalysis.ObjectAnalysisRefusal?> ObjectAnalysisRateRefusalAsync()
        {
            Project p = await Db.Projects.IgnoreQueryFilters().SingleAsync();
            ReelBolt.Inference.Api.Services.ObjectAnalysis.ObjectAnalysisService svc = new(
                Db, new Mock<IWorkflowExecutionService>().Object, _storage.Object, NullLogger<ReelBolt.Inference.Api.Services.ObjectAnalysis.ObjectAnalysisService>.Instance);
            return await svc.PeekRunRateAsync(new ReelBolt.Inference.Api.Services.ObjectAnalysis.ObjectAnalysisCaller(OwnerId, p.OrganizationId, p, true), default);
        }

        public async Task BumpRevisionWithSnapshotAsync(int oldRevision)
        {
            EditTimelineEntity t = await Db.EditTimelines.SingleAsync();
            Db.EditTimelineSnapshots.Add(new EditTimelineSnapshot { Id = Guid.NewGuid(), TimelineId = t.Id, Revision = oldRevision, DocumentJson = t.DocumentJson });
            t.Revision = oldRevision + 1;
            await Db.SaveChangesAsync();
        }

        public static string Region(string id, int keys, string method = "feature", bool sweep = false)
        {
            object[] frames = Enumerable.Range(0, keys).Select(i =>
            {
                // A box that drifts left to right across the source when sweeping, otherwise a fixed one.
                double x = sweep ? 0.2 + 0.5 * i / Math.Max(1, keys - 1) : 0.4;
                return (object)new
                {
                    timeSec = i * 0.01,
                    x0 = x,
                    y0 = 0.3,
                    x1 = x + 0.1,
                    y1 = 0.3,
                    x2 = x + 0.1,
                    y2 = 0.5,
                    x3 = x,
                    y3 = 0.5
                };
            }).ToArray();
            return JsonSerializer.Serialize(new
            {
                id,
                startSec = 0.0,
                endSec = Math.Max(0, keys - 1) * 0.01,
                keyframes = frames,
                sourceIndex = 0,
                method
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        public static string Artifact(Guid fileId, params string[] regions) =>
            "{\"media\":{\"durationSec\":10,\"fpsNum\":30,\"fpsDen\":1,\"width\":1920,\"height\":1080},"
            + $"\"sources\":[{{\"sourceIndex\":0,\"storageKey\":\"k\",\"media\":{{\"durationSec\":10,\"fpsNum\":30,\"fpsDen\":1,\"width\":1920,\"height\":1080}},\"projectFileId\":\"{fileId}\"}}],"
            + "\"insertRegions\":[" + string.Join(",", regions) + "]}";
    }
}
