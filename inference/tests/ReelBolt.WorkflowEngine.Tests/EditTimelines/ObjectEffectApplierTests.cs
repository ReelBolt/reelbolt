#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.EditTimelines;
using ReelBolt.Inference.Api.Services.ObjectAnalysis;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.EditTimelines;

/// <summary>The finalizer that writes a finished analysis's regionEffects onto the clips the assistant named, and the shared service it rests on.</summary>
public class ObjectEffectApplierTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CountingWriter(IEditTimelineWriter inner, Action? before = null) : IEditTimelineWriter
    {
        public int Calls { get; private set; }

        public Task<ServerEditResult> ApplyServerEditAsync(Guid projectId, Guid timelineId, Func<EditTimeline, ServerEditMutation?> mutate, CancellationToken ct)
        {
            Calls++;
            before?.Invoke();
            return inner.ApplyServerEditAsync(projectId, timelineId, mutate, ct);
        }
    }

    private static ObjectAnalysisService Service(ServerEditFixture f, InferenceApiDbContext? db = null, Action<Guid>? onQueue = null)
    {
        Mock<IWorkflowExecutionService> executions = new();
        executions.Setup(e => e.QueueAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(),
                It.IsAny<ExecutionInitiator>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<RunChoices?>(),
                It.IsAny<WorkflowDefinitionKind>(), It.IsAny<Guid?>(), It.IsAny<Func<CancellationToken, Task>?>()))
            .ReturnsAsync((Guid _, Guid workflowId, Guid _, string? _, ExecutionInitiator _, CancellationToken _, bool _, RunChoices? _,
                WorkflowDefinitionKind _, Guid? executionId, Func<CancellationToken, Task>? _) =>
            {
                onQueue?.Invoke(executionId!.Value);
                return new WorkflowExecutionResponse(executionId ?? Guid.NewGuid(), workflowId, "Queued", null, null, 0, null, null, null, [], []);
            });
        return new ObjectAnalysisService(db ?? f.Db, executions.Object, f.Storage.Object, NullLogger<ObjectAnalysisService>.Instance, TimeProvider.System, null, null);
    }

    private static ObjectEffectApplier Applier(
        ServerEditFixture f, InferenceApiDbContext? db = null, IEditTimelineWriter? writer = null, TimeProvider? clock = null)
    {
        InferenceApiDbContext context = db ?? f.Db;
        return new ObjectEffectApplier(
            context, Service(f, context), writer ?? f.Writer(context), NullLogger<ObjectEffectApplier>.Instance, clock);
    }

    private static async Task<ObjectEffectRequest> AddRowAsync(
        ServerEditFixture f, IEnumerable<Guid>? items, string status = ObjectEffectRequest.StatusQueued, string effect = "Blur", bool withIntent = true, string prompt = "the license plate")
    {
        ObjectEffectRequest row = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = TestTenancy.OrgOf(f.OwnerId),
            ProjectId = f.ProjectId,
            ExecutionId = Guid.NewGuid(),
            SourceKind = ObjectEffectRequest.SourceFile,
            SourceId = f.FileId.ToString("D"),
            Prompt = prompt,
            MatchScope = ObjectMatchScope.AllInstances.ToString(),
            Effect = effect,
            Strength = 0.8,
            Invert = false,
            FeatherPx = 4,
            TimelineId = withIntent ? f.TimelineId : null,
            TimelineItemIdsJson = withIntent ? JsonSerializer.Serialize((items ?? []).Select(i => i.ToString("D"))) : null,
            Status = status,
            CreatedByUserId = f.OwnerId,
            CreatedAt = DateTime.UtcNow
        };
        f.Db.ObjectEffectRequests.Add(row);
        await f.Db.SaveChangesAsync();
        return row;
    }

    private static async Task<ObjectEffectRequest> ReloadAsync(ServerEditFixture f, Guid id) =>
        await f.Db.ObjectEffectRequests.AsNoTracking().SingleAsync(r => r.Id == id);

    // ------------------------------------------------------------------------------------------------ apply

    [Fact]
    public async Task A_passed_analysis_is_written_onto_every_named_clip_in_one_revision()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync(clips: 3);
        ObjectEffectRequest row = await AddRowAsync(f, f.ClipIds);
        Guid stepResult = await f.AddRunAsync(row.ExecutionId, ExecutionStatus.Passed);

        (await Applier(f).SweepOnceAsync(default)).Should().Be(1);

        EditTimeline doc = await f.ReadTimelineAsync();
        foreach (MediaClip clip in f.Clips(doc))
        {
            RegionClipEffect effect = clip.RegionEffects.Should().ContainSingle().Subject;
            (effect.Effect, effect.Strength, effect.Enabled).Should().Be(("blur", 0.8, true));
            ShapeMask mask = effect.Mask;
            (mask.Kind, mask.AnalysisStepResultId, mask.Invert, mask.FeatherPx).Should().Be((ShapeMask.SegmentationKind, stepResult, false, 4.0));
            mask.TrackIds.Should().Equal("g1");
        }

        (await f.RevisionAsync()).Should().Be(2, "all clips are written in ONE edit");
        ObjectEffectRequest after = await ReloadAsync(f, row.Id);
        (after.Status, after.AppliedRevision, after.FinalizedAt is not null).Should().Be((ObjectEffectRequest.StatusApplied, 2L, true));
        after.FailureMessage.Should().BeNull();

        EditTimelineOp op = await f.Db.EditTimelineOps.SingleAsync();
        JsonElement[] ops = JsonDocument.Parse(op.OpsJson).RootElement.EnumerateArray().ToArray();
        ops.Should().HaveCount(3);
        ops.Select(o => o.GetProperty("type").GetString()).Should().OnlyContain(t => t == "setProperty");
        ops.Select(o => o.GetProperty("key").GetString()).Should().OnlyContain(k => k == "regionEffects");
        ops.Select(o => Guid.Parse(o.GetProperty("itemId").GetString()!)).Should().BeEquivalentTo(f.ClipIds);
        ops[0].GetProperty("value")[0].GetProperty("mask").GetProperty("kind").GetString().Should().Be("segmentation");

        // The stored document passes the validator on its own (the writer would have refused it otherwise).
        EditTimelineValidationContext context = await new EditTimelineValidationContextBuilder(f.Db, f.Storage.Object, NullLogger.Instance)
            .BuildAsync(f.ProjectId, doc, default);
        EditTimelineValidator.Validate(doc, context).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_clip_that_hit_the_cap_or_is_not_covered_is_skipped_and_the_rest_still_apply()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync(clips: 4);
        ObjectEffectRequest row = await AddRowAsync(f, f.ClipIds);
        Guid stepResult = await f.AddRunAsync(row.ExecutionId, ExecutionStatus.Passed);
        RegionClipEffect old(int i) => new("pixelate", 0.5, new ShapeMask(ShapeMask.SegmentationKind, AnalysisStepResultId: stepResult, TrackIds: ["g1"]));
        EditTimeline seeded = await f.ReadTimelineAsync();
        List<Item> items =
        [
            f.Clips(seeded).ElementAt(0),
            ServerEditFixture.Clip(f.ClipIds[1], "Second", 30, 60, f.FileId),
            ServerEditFixture.Clip(f.ClipIds[2], "Full", 60, 90, f.FileId, Enumerable.Range(0, 4).Select(old).ToList()),
            ServerEditFixture.Clip(f.ClipIds[3], "Too long", 90, 90 + 30 * 200, f.FileId)
        ];
        await f.Db.EditTimelines.Where(t => t.Id == f.TimelineId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.DocumentJson, JsonSerializer.Serialize(f.Document(items), EditTimelineJson.Options)));

        (await Applier(f).SweepOnceAsync(default)).Should().Be(1);

        EditTimeline doc = await f.ReadTimelineAsync();
        MediaClip[] clips = f.Clips(doc).ToArray();
        clips[0].RegionEffects.Should().ContainSingle(e => e.Effect == "blur");
        clips[1].RegionEffects.Should().ContainSingle(e => e.Effect == "blur");
        clips[2].RegionEffects.Should().HaveCount(4).And.OnlyContain(e => e.Effect == "pixelate");
        clips[3].RegionEffects.Should().BeNullOrEmpty();

        ObjectEffectRequest after = await ReloadAsync(f, row.Id);
        after.Status.Should().Be(ObjectEffectRequest.StatusApplied);
        after.FailureMessage.Should().Contain("Added to 2 of 4 clips").And.Contain("Full").And.Contain("a clip could not take the effect");
    }

    [Fact]
    public async Task An_edit_made_after_the_request_sends_it_to_review_and_writes_nothing()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync(clips: 2);
        ObjectEffectRequest row = await AddRowAsync(f, f.ClipIds);
        await f.AddRunAsync(row.ExecutionId, ExecutionStatus.Passed);
        f.Db.EditTimelineOps.Add(new EditTimelineOp { Id = Guid.NewGuid(), TimelineId = f.TimelineId, Revision = 50, OpsJson = "[]", CreatedAt = row.CreatedAt.AddSeconds(30) });
        await f.Db.SaveChangesAsync();
        CountingWriter writer = new(f.Writer(f.Db));

        (await Applier(f, writer: writer).SweepOnceAsync(default)).Should().Be(0);

        writer.Calls.Should().Be(0);
        (await ReloadAsync(f, row.Id)).Status.Should().Be(ObjectEffectRequest.StatusNeedsReview);
        f.Clips(await f.ReadTimelineAsync()).Should().OnlyContain(c => c.RegionEffects == null || c.RegionEffects.Count == 0);
        (await Applier(f, writer: writer).SweepOnceAsync(default)).Should().Be(0);
        (await ReloadAsync(f, row.Id)).Status.Should().Be(ObjectEffectRequest.StatusNeedsReview, "idempotent");
    }

    [Fact]
    public async Task An_edit_within_the_skew_margin_of_the_request_is_not_a_change()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync(clips: 1);
        ObjectEffectRequest row = await AddRowAsync(f, f.ClipIds);
        await f.AddRunAsync(row.ExecutionId, ExecutionStatus.Passed);
        f.Db.EditTimelineOps.Add(new EditTimelineOp { Id = Guid.NewGuid(), TimelineId = f.TimelineId, Revision = 50, OpsJson = "[]", CreatedAt = row.CreatedAt.AddMilliseconds(400) });
        await f.Db.SaveChangesAsync();

        (await Applier(f).SweepOnceAsync(default)).Should().Be(1);

        (await ReloadAsync(f, row.Id)).Status.Should().Be(ObjectEffectRequest.StatusApplied);
    }

    [Fact]
    public async Task A_deleted_target_goes_to_review_instead_of_being_skipped()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync(clips: 2);
        ObjectEffectRequest row = await AddRowAsync(f, f.ClipIds);
        await f.AddRunAsync(row.ExecutionId, ExecutionStatus.Passed);
        EditTimeline seeded = await f.ReadTimelineAsync();
        await f.Db.EditTimelines.Where(t => t.Id == f.TimelineId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.DocumentJson, JsonSerializer.Serialize(f.Document([f.Clips(seeded).First()]), EditTimelineJson.Options)));

        (await Applier(f).SweepOnceAsync(default)).Should().Be(0);

        (await ReloadAsync(f, row.Id)).Status.Should().Be(ObjectEffectRequest.StatusNeedsReview);
        f.Clips(await f.ReadTimelineAsync()).Should().OnlyContain(c => c.RegionEffects == null || c.RegionEffects.Count == 0);
    }

    [Fact]
    public async Task A_discarded_request_still_lets_its_analysis_be_reused()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync(clips: 1);
        ObjectEffectRequest row = await AddRowAsync(f, f.ClipIds, ObjectEffectRequest.StatusDiscarded);
        await f.AddRunAsync(row.ExecutionId, ExecutionStatus.Passed);

        Project project = await f.Db.Projects.AsNoTracking().SingleAsync(p => p.Id == f.ProjectId);
        ObjectAnalysisCaller caller = new(f.OwnerId, project.OrganizationId, project, EmailVerified: true);
        row.Prompt.Should().Be("the license plate");

        ObjectAnalysisResult<ObjectAnalysisQueuedResponse> again = await Service(f).RequestAsync(
            caller, "file:" + f.FileId, new ObjectAnalysisRequestBody("the license plate", null, "Blur", 0.8),
            new ObjectAnalysisIntent(f.TimelineId, f.ClipIds), default);

        again.SuccessStatus.Should().Be(StatusCodes.Status200OK);
        again.Value!.ExecutionId.Should().Be(row.ExecutionId, "a discarded result's analysis is reused, no new run");
    }

    [Fact]
    public async Task A_clip_now_playing_another_video_sends_the_request_to_review_without_writing()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync(clips: 1);
        EditTimeline seeded = await f.ReadTimelineAsync();
        await f.Db.EditTimelines.Where(t => t.Id == f.TimelineId).ExecuteUpdateAsync(s => s.SetProperty(t => t.DocumentJson,
            JsonSerializer.Serialize(f.Document([ServerEditFixture.Clip(f.ClipIds[0], "Elsewhere", 0, 30, f.OtherFileId)]), EditTimelineJson.Options)));
        ObjectEffectRequest row = await AddRowAsync(f, f.ClipIds);
        await f.AddRunAsync(row.ExecutionId, ExecutionStatus.Passed);

        (await Applier(f).SweepOnceAsync(default)).Should().Be(0);

        ObjectEffectRequest after = await ReloadAsync(f, row.Id);
        after.Status.Should().Be(ObjectEffectRequest.StatusNeedsReview);
        (await f.RevisionAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_failed_run_marks_the_request_failed_with_the_customer_sentence_and_touches_nothing()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync();
        ObjectEffectRequest row = await AddRowAsync(f, f.ClipIds);
        await f.AddRunAsync(row.ExecutionId, ExecutionStatus.Failed, StepStatus.Failed, "SEGMENTATION_PROVIDER_ERROR");

        (await Applier(f).SweepOnceAsync(default)).Should().Be(0);

        ObjectEffectRequest after = await ReloadAsync(f, row.Id);
        (after.Status, after.FailureCode).Should().Be((ObjectEffectRequest.StatusFailed, "SEGMENTATION_PROVIDER_ERROR"));
        after.FailureMessage.Should().Be(ObjectAnalysisMessages.For("SEGMENTATION_PROVIDER_ERROR"));
        (await f.RevisionAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Apply_one_applies_only_that_request_and_never_sweeps_the_others()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync(clips: 2);
        ObjectEffectRequest first = await AddRowAsync(f, [f.ClipIds[0]]);
        ObjectEffectRequest other = await AddRowAsync(f, [f.ClipIds[1]], effect: "Pixelate", prompt: "the face");
        await f.AddRunAsync(first.ExecutionId, ExecutionStatus.Passed);
        await f.AddRunAsync(other.ExecutionId, ExecutionStatus.Passed);

        (await Applier(f).ApplyOneAsync(Guid.NewGuid(), first.Id, default)).Should().BeFalse("a row of another project is never touched");
        (await ReloadAsync(f, first.Id)).Status.Should().Be(ObjectEffectRequest.StatusQueued);

        (await Applier(f).ApplyOneAsync(f.ProjectId, first.Id, default)).Should().BeTrue();

        (await ReloadAsync(f, first.Id)).Status.Should().Be(ObjectEffectRequest.StatusApplied);
        (await ReloadAsync(f, other.Id)).Status.Should().Be(ObjectEffectRequest.StatusQueued, "the other ready request waits for the sweeper");
        MediaClip[] clips = f.Clips(await f.ReadTimelineAsync()).ToArray();
        clips[0].RegionEffects.Should().ContainSingle();
        clips[1].RegionEffects.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task The_same_object_and_effect_asked_again_with_a_new_strength_changes_that_entry_and_says_so()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync(clips: 2);
        ObjectEffectRequest first = await AddRowAsync(f, f.ClipIds);
        await f.AddRunAsync(first.ExecutionId, ExecutionStatus.Passed);
        (await Applier(f).SweepOnceAsync(default)).Should().Be(1);

        ObjectEffectRequest again = await AddRowAsync(f, f.ClipIds);
        await f.Db.ObjectEffectRequests.Where(r => r.Id == again.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ExecutionId, first.ExecutionId).SetProperty(r => r.Strength, 0.3).SetProperty(r => r.Status, ObjectEffectRequest.StatusSucceeded));
        (await Applier(f).SweepOnceAsync(default)).Should().Be(1);

        MediaClip[] clips = f.Clips(await f.ReadTimelineAsync()).ToArray();
        clips.Should().OnlyContain(c => c.RegionEffects!.Count == 1 && c.RegionEffects[0].Strength == 0.3);
        ObjectEffectRequest after = await ReloadAsync(f, again.Id);
        (after.Status, after.AppliedRevision).Should().Be((ObjectEffectRequest.StatusApplied, 3L));
        after.FailureMessage.Should().Contain("changed the existing effect on 2");
        JsonDocument.Parse((await f.Db.EditTimelineOps.AsNoTracking().SingleAsync(o => o.Revision == 3)).OpsJson).RootElement.GetArrayLength().Should().Be(2);

        // Asked a third time with identical values: nothing is written, and it says so.
        ObjectEffectRequest same = await AddRowAsync(f, f.ClipIds);
        await f.Db.ObjectEffectRequests.Where(r => r.Id == same.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ExecutionId, first.ExecutionId).SetProperty(r => r.Strength, 0.3).SetProperty(r => r.Status, ObjectEffectRequest.StatusSucceeded));
        await Applier(f).SweepOnceAsync(default);
        (await f.RevisionAsync()).Should().Be(3);
        (await ReloadAsync(f, same.Id)).FailureMessage.Should().Contain("left 2 as they were");
    }

    [Fact]
    public async Task The_status_response_exposes_the_apply_step_and_its_note()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync(clips: 2);
        await f.Db.EditTimelines.Where(t => t.Id == f.TimelineId).ExecuteUpdateAsync(s => s.SetProperty(t => t.DocumentJson,
            JsonSerializer.Serialize(f.Document([
                ServerEditFixture.Clip(f.ClipIds[0], "Mine", 0, 30, f.FileId),
                ServerEditFixture.Clip(f.ClipIds[1], "Too long", 30, 30 + 30 * 200, f.FileId)]), EditTimelineJson.Options)));
        ObjectEffectRequest row = await AddRowAsync(f, f.ClipIds);
        await f.AddRunAsync(row.ExecutionId, ExecutionStatus.Passed);
        string media = "file:" + f.FileId;

        (await Service(f, f.NewDb()).GetAsync(f.ProjectId, media, row.Id, default)).Value!.ApplyStatus.Should().BeNull("nothing is applied yet");
        await Applier(f).SweepOnceAsync(default);

        ObjectAnalysisResponse applied = (await Service(f, f.NewDb()).GetAsync(f.ProjectId, media, row.Id, default)).Value!;
        (applied.Status, applied.ApplyStatus).Should().Be(("succeeded", "applied"));
        applied.ApplyNote.Should().Contain("Added to 1 of 2").And.Contain("could not take the effect");

        // And a request that could not be applied at all.
        await f.Db.EditTimelines.Where(t => t.Id == f.TimelineId).ExecuteUpdateAsync(s => s.SetProperty(t => t.DeletedAt, DateTime.UtcNow));
        ObjectEffectRequest lost = await AddRowAsync(f, f.ClipIds);
        await f.Db.ObjectEffectRequests.Where(r => r.Id == lost.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ExecutionId, row.ExecutionId).SetProperty(r => r.Prompt, "the license plate").SetProperty(r => r.Status, ObjectEffectRequest.StatusSucceeded));
        await Applier(f).SweepOnceAsync(default);
        ObjectAnalysisResponse failed = (await Service(f, f.NewDb()).GetAsync(f.ProjectId, media, lost.Id, default)).Value!;
        (failed.ApplyStatus, failed.ApplyNote).Should().Be(("apply_failed", "The timeline is no longer available."));
    }

    [Fact]
    public async Task A_run_that_is_still_going_is_left_alone()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync();
        ObjectEffectRequest row = await AddRowAsync(f, f.ClipIds);
        await f.AddRunAsync(row.ExecutionId, ExecutionStatus.Running);

        (await Applier(f).SweepOnceAsync(default)).Should().Be(0);

        (await ReloadAsync(f, row.Id)).Status.Should().Be(ObjectEffectRequest.StatusQueued);
    }

    // ------------------------------------------------------------------------------------------------ idempotence and claims

    [Fact]
    public async Task Running_the_sweep_again_changes_nothing()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync();
        ObjectEffectRequest row = await AddRowAsync(f, f.ClipIds);
        await f.AddRunAsync(row.ExecutionId, ExecutionStatus.Passed);
        (await Applier(f).SweepOnceAsync(default)).Should().Be(1);

        (await Applier(f).SweepOnceAsync(default)).Should().Be(0);

        (await f.RevisionAsync()).Should().Be(2);
        f.Clips(await f.ReadTimelineAsync()).Should().OnlyContain(c => c.RegionEffects!.Count == 1);
    }

    [Fact]
    public async Task An_abandoned_claim_is_taken_over_without_stacking_a_second_copy()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync();
        ObjectEffectRequest row = await AddRowAsync(f, f.ClipIds);
        await f.AddRunAsync(row.ExecutionId, ExecutionStatus.Passed);
        Clock clock = new();
        (await Applier(f, clock: clock).SweepOnceAsync(default)).Should().Be(1);

        // The replica wrote the timeline, then died before recording the revision.
        await f.Db.ObjectEffectRequests.Where(r => r.Id == row.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.AppliedRevision, (long?)null)
            .SetProperty(r => r.FinalizedAt, DateTime.UtcNow.AddMinutes(-10)));

        (await Applier(f, clock: clock).SweepOnceAsync(default)).Should().Be(1);

        ObjectEffectRequest after = await ReloadAsync(f, row.Id);
        (after.Status, after.AppliedRevision).Should().Be((ObjectEffectRequest.StatusApplied, 2L));
        (await f.RevisionAsync()).Should().Be(2, "nothing was written the second time");
        f.Clips(await f.ReadTimelineAsync()).Should().OnlyContain(c => c.RegionEffects!.Count == 1);
    }

    [Fact]
    public async Task A_claim_that_is_still_fresh_is_not_taken_by_a_second_sweeper()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync();
        ObjectEffectRequest row = await AddRowAsync(f, f.ClipIds);
        await f.AddRunAsync(row.ExecutionId, ExecutionStatus.Passed);

        using InferenceApiDbContext other = f.NewDb();
        int secondSweeper = -1;
        CountingWriter first = null!;
        first = new CountingWriter(f.Writer(), before: () =>
            secondSweeper = Applier(f, other).SweepOnceAsync(default).GetAwaiter().GetResult());

        // While the first sweeper holds its claim, a second one over the same database finds nothing to take.
        (await Applier(f, writer: first).SweepOnceAsync(default)).Should().Be(1);

        secondSweeper.Should().Be(0);
        first.Calls.Should().Be(1);
        (await f.RevisionAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Two_sweepers_over_a_ready_request_apply_it_once()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync();
        ObjectEffectRequest row = await AddRowAsync(f, f.ClipIds);
        await f.AddRunAsync(row.ExecutionId, ExecutionStatus.Passed);
        using InferenceApiDbContext other = f.NewDb();
        CountingWriter w1 = new(f.Writer()), w2 = new(f.Writer(other));

        int a = await Applier(f, writer: w1).SweepOnceAsync(default);
        int b = await Applier(f, other, w2).SweepOnceAsync(default);

        (a + b).Should().Be(1);
        (w1.Calls + w2.Calls).Should().Be(1);
    }

    [Fact]
    public async Task A_request_with_no_timeline_intent_is_finalized_but_nothing_is_applied()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync();
        ObjectEffectRequest row = await AddRowAsync(f, null, withIntent: false);
        await f.AddRunAsync(row.ExecutionId, ExecutionStatus.Passed);

        (await Applier(f).SweepOnceAsync(default)).Should().Be(0);

        ObjectEffectRequest after = await ReloadAsync(f, row.Id);
        (after.Status, after.AppliedRevision).Should().Be((ObjectEffectRequest.StatusSucceeded, (long?)null));
        (await f.RevisionAsync()).Should().Be(1);
        (await f.Db.EditTimelineOps.CountAsync()).Should().Be(0);
    }

    [Fact]
    public void The_interval_is_configurable_and_zero_disables_the_sweeper()
    {
        ObjectEffectApplier.IntervalSeconds(null).Should().Be(15);
        ObjectEffectApplier.IntervalSeconds(new ConfigurationBuilder().Build()).Should().Be(15);
        ObjectEffectApplier.IntervalSeconds(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ObjectEffects:ApplySweepSeconds"] = "30" }).Build()).Should().Be(30);
        ObjectEffectApplier.IntervalSeconds(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ObjectEffects:ApplySweepSeconds"] = "0" }).Build()).Should().Be(0);
    }

    [Fact]
    public async Task With_the_interval_at_zero_the_hosted_sweeper_does_nothing()
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ObjectEffects:ApplySweepSeconds"] = "0" }).Build();
        ObjectEffectApplySweeper sweeper = new(new ThrowingScopeFactory(), config, NullLogger<ObjectEffectApplySweeper>.Instance);

        await sweeper.StartAsync(default);
        await sweeper.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await sweeper.StopAsync(default);
    }

    private sealed class ThrowingScopeFactory : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
    {
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() => throw new InvalidOperationException("a disabled sweeper must not open a scope");
    }

    // ------------------------------------------------------------------------------------------------ end to end through the service

    [Fact]
    public async Task A_request_with_intent_runs_passes_is_swept_and_the_timeline_carries_a_valid_regionEffect()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync(clips: 2);
        Guid executionId = Guid.Empty;
        ObjectAnalysisService service = Service(f, onQueue: id => executionId = id);
        Project project = await f.Db.Projects.AsNoTracking().SingleAsync(p => p.Id == f.ProjectId);
        ObjectAnalysisCaller caller = new(f.OwnerId, project.OrganizationId, project, EmailVerified: true);

        ObjectAnalysisResult<ObjectAnalysisQueuedResponse> queued = await service.RequestAsync(
            caller, "file:" + f.FileId,
            new ObjectAnalysisRequestBody("the license plate", null, "Blur", 0.9, FeatherPx: 3),
            new ObjectAnalysisIntent(f.TimelineId, f.ClipIds), default);

        queued.Succeeded.Should().BeTrue();
        queued.SuccessStatus.Should().Be(StatusCodes.Status202Accepted);
        ObjectEffectRequest row = await f.Db.ObjectEffectRequests.AsNoTracking().SingleAsync();
        (row.TimelineId, row.Status, row.ExecutionId).Should().Be((f.TimelineId, ObjectEffectRequest.StatusQueued, executionId));
        JsonSerializer.Deserialize<string[]>(row.TimelineItemIdsJson!).Should().BeEquivalentTo(f.ClipIds.Select(i => i.ToString("D")));

        // Not finished yet: a sweep does nothing.
        (await Applier(f).SweepOnceAsync(default)).Should().Be(0);
        (await f.RevisionAsync()).Should().Be(1);

        // The run passes (the fake artifact stands in for the engine's output).
        Guid stepResult = await f.AddRunAsync(executionId, ExecutionStatus.Passed);
        (await Applier(f).SweepOnceAsync(default)).Should().Be(1);

        EditTimeline doc = await f.ReadTimelineAsync();
        (await f.RevisionAsync()).Should().Be(2);
        MediaClip[] clips = f.Clips(doc).ToArray();
        clips.Should().OnlyContain(c => c.RegionEffects!.Count == 1);
        clips[0].RegionEffects![0].Mask.AnalysisStepResultId.Should().Be(stepResult);
        clips[0].RegionEffects![0].Strength.Should().Be(0.9);
        EditTimelineValidationContext context = await new EditTimelineValidationContextBuilder(f.Db, f.Storage.Object, NullLogger.Instance)
            .BuildAsync(f.ProjectId, doc, default);
        EditTimelineValidator.Validate(doc, context).IsValid.Should().BeTrue();
        (await f.Db.ObjectEffectRequests.AsNoTracking().SingleAsync()).Status.Should().Be(ObjectEffectRequest.StatusApplied);

        // The same words asked again reuse the finished analysis (no new run) and still get their own row to apply.
        Guid secondClip = f.ClipIds[0];
        ObjectAnalysisResult<ObjectAnalysisQueuedResponse> again = await service.RequestAsync(
            caller, "file:" + f.FileId,
            new ObjectAnalysisRequestBody("the license plate", null, "Pixelate", 0.5),
            new ObjectAnalysisIntent(f.TimelineId, [secondClip]), default);
        again.SuccessStatus.Should().Be(StatusCodes.Status200OK);
        again.Value!.ExecutionId.Should().Be(executionId);
        (await f.Db.WorkflowDefinitions.CountAsync(w => w.Kind == WorkflowDefinitionKind.ObjectAnalysis)).Should().Be(2, "only the seeded run's definition and the first request's");
        (await Applier(f).SweepOnceAsync(default)).Should().Be(1);
        f.Clips(await f.ReadTimelineAsync()).First().RegionEffects!.Select(e => e.Effect).Should().Equal("blur", "pixelate");
    }

    [Fact]
    public async Task The_service_refuses_an_unverified_caller_and_an_intent_naming_no_clip_before_writing_anything()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync();
        ObjectAnalysisService service = Service(f);
        Project project = await f.Db.Projects.AsNoTracking().SingleAsync(p => p.Id == f.ProjectId);
        ObjectAnalysisRequestBody body = new("the license plate", null, "Blur", 0.5);

        ObjectAnalysisResult<ObjectAnalysisQueuedResponse> unverified = await service.RequestAsync(
            new ObjectAnalysisCaller(f.OwnerId, project.OrganizationId, project, EmailVerified: false), "file:" + f.FileId, body, null, default);
        ObjectAnalysisResult<ObjectAnalysisQueuedResponse> noClips = await service.RequestAsync(
            new ObjectAnalysisCaller(f.OwnerId, project.OrganizationId, project, EmailVerified: true), "file:" + f.FileId, body,
            new ObjectAnalysisIntent(f.TimelineId, []), default);

        unverified.Refusal!.StatusCode.Should().Be(403);
        noClips.Refusal!.StatusCode.Should().Be(400);
        (await f.Db.ObjectEffectRequests.CountAsync()).Should().Be(0);
        (await f.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
    }
}
