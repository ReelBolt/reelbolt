#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>When may the plain manifest seed run? Never over picture the film (or the editor render's own timeline) censors.</summary>
public class PlainSeedCensorGuardTests
{
    private const string NoEffectsCompile = """{"crf": 20, "mode": "Reencode", "version": 1}""";

    private sealed class World : IAsyncDisposable
    {
        public InferenceApiDbContext Db { get; } = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

        public Guid ProjectId { get; } = Guid.NewGuid();
        public WorkflowDefinition Live { get; }
        public WorkflowExecution Run { get; }

        public World()
        {
            Live = new WorkflowDefinition { Id = Guid.NewGuid(), ProjectId = ProjectId, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Run = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowDefinitionId = Live.Id, ProjectId = ProjectId };
            Db.WorkflowDefinitions.Add(Live);
            Db.WorkflowExecutions.Add(Run);
        }

        public WorkflowStep Step(Guid definitionId, int order, StepType type, string? compileJson = null, string? renderJson = null)
        {
            WorkflowStep step = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = definitionId,
                AgentDefinitionId = Guid.NewGuid(),
                StepOrder = order,
                StepType = type,
                VideoCompileConfigJson = compileJson,
                TimelineRenderConfigJson = renderJson
            };
            Db.WorkflowSteps.Add(step);
            return step;
        }

        public WorkflowStepResult Result(WorkflowStep step)
        {
            WorkflowStepResult r = new() { Id = Guid.NewGuid(), WorkflowExecutionId = Run.Id, WorkflowStepId = step.Id, TimelineStorageKey = "m.json" };
            Db.WorkflowStepResults.Add(r);
            return r;
        }

        public Guid Timeline(int revision, EditTimeline document, params (int Revision, EditTimeline Document)[] snapshots)
        {
            Guid id = Guid.NewGuid();
            Db.EditTimelines.Add(new EditTimelineEntity
            {
                Id = id,
                ProjectId = ProjectId,
                Name = "t",
                Revision = revision,
                DocumentJson = JsonSerializer.Serialize(document, EditTimelineJson.Options)
            });
            foreach ((int rev, EditTimeline doc) in snapshots)
            {
                Db.EditTimelineSnapshots.Add(new EditTimelineSnapshot { Id = Guid.NewGuid(), TimelineId = id, Revision = rev, DocumentJson = JsonSerializer.Serialize(doc, EditTimelineJson.Options) });
            }

            return id;
        }

        public static string RenderConfig(Guid timelineId, int? revision = null) =>
            JsonSerializer.Serialize(new TimelineRenderStepConfig(timelineId, Revision: revision), new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });

        public async Task<bool> RevealsAsync(WorkflowStepResult result)
        {
            await Db.SaveChangesAsync();
            return await PlainSeedCensorGuard.PlainSeedWouldRevealAsync(Db, ProjectId, result, CancellationToken.None);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static Track VideoTrack(params Item[] items) =>
        new(Guid.NewGuid(), TrackKind.Video, "Video", null, 100, false, true, false, false, null, items);

    private static MediaClip Clip(IReadOnlyList<ClipEffect>? effects = null, bool segmentationMask = false, bool regionEffect = false)
    {
        ShapeMask mask = new(ShapeMask.SegmentationKind, AnalysisStepResultId: Guid.NewGuid(), TrackIds: ["g1"]);
        return new MediaClip(Guid.NewGuid(), "c", 0, 300, new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: Guid.NewGuid()),
            Effects: effects, Mask: segmentationMask ? mask : null,
            RegionEffects: regionEffect ? [new RegionClipEffect("blur", 1.0, mask)] : null);
    }

    private static EditTimeline DocOf(IReadOnlyList<Track>? tracks, params EditSequence[] sequences) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "t",
            new EditTimelineSettings(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0), tracks!, [], null, null, sequences: sequences);

    private static async Task<bool> RenderOfAsync(EditTimeline doc, int? pinned = 1, params (int Revision, EditTimeline Document)[] snapshots)
    {
        await using World w = new();
        WorkflowStep step = w.Step(w.Live.Id, 1, StepType.TimelineRender, renderJson: World.RenderConfig(w.Timeline(2, doc, snapshots), pinned is null ? null : (pinned == 1 ? 2 : pinned)));
        return await w.RevealsAsync(w.Result(step));
    }

    [Fact]
    public async Task A_mask_or_region_effect_inside_a_nested_sequence_refuses_even_two_levels_down()
    {
        EditSequence inner = new(Guid.NewGuid(), "inner", [VideoTrack(Clip(regionEffect: true))]);
        EditSequence outer = new(Guid.NewGuid(), "outer", [VideoTrack(new CompoundClip(Guid.NewGuid(), "n", 0, 300, inner.Id))]);
        EditTimeline doc = DocOf([VideoTrack(new CompoundClip(Guid.NewGuid(), "n", 0, 300, outer.Id))], outer, inner);

        (await RenderOfAsync(doc)).Should().BeTrue();
        (await RenderOfAsync(DocOf([VideoTrack(Clip(segmentationMask: true))]))).Should().BeTrue();
    }

    [Fact]
    public async Task A_plain_nested_timeline_still_seeds()
    {
        EditSequence inner = new(Guid.NewGuid(), "inner", [VideoTrack(Clip())]);
        EditTimeline doc = DocOf([VideoTrack(new CompoundClip(Guid.NewGuid(), "n", 0, 300, inner.Id))], inner);

        (await RenderOfAsync(doc)).Should().BeFalse();
    }

    [Theory]
    [InlineData("blur", true)]
    [InlineData("pixelate", true)]
    [InlineData("grain", false)]
    public async Task A_whole_clip_blur_or_pixelate_is_refused_and_a_look_effect_is_not(string effect, bool refused)
    {
        (await RenderOfAsync(DocOf([VideoTrack(Clip(effects: [new ClipEffect(effect, 0.5)]))]))).Should().Be(refused);
    }

    [Fact]
    public async Task An_adjustment_clip_that_blurs_everything_below_is_refused()
    {
        AdjustmentClip adjust = new(Guid.NewGuid(), RecordIn: 0, RecordOut: 300, Effects: [new ClipEffect("pixelate", 0.5)]);

        (await RenderOfAsync(DocOf([VideoTrack(Clip()), VideoTrack(adjust)]))).Should().BeTrue();
    }

    [Fact]
    public async Task A_stored_document_with_missing_tracks_fails_closed_instead_of_throwing()
    {
        (await RenderOfAsync(DocOf(null))).Should().BeTrue();
    }

    [Fact]
    public async Task An_unpinned_render_is_refused_when_any_stored_revision_of_its_timeline_is_censor_shaped()
    {
        (await RenderOfAsync(DocOf([VideoTrack(Clip())]), pinned: null, (1, DocOf([VideoTrack(Clip(regionEffect: true))])))).Should().BeTrue();
        (await RenderOfAsync(DocOf([VideoTrack(Clip())]), pinned: null, (1, DocOf([VideoTrack(Clip())])))).Should().BeFalse();
    }

    [Fact]
    public async Task A_re_snapshotted_legacy_run_whose_same_order_step_has_another_type_judges_both()
    {
        await using World w = new();
        WorkflowDefinition snapshot = new() { Id = Guid.NewGuid(), ProjectId = w.ProjectId, Name = "snap", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        w.Db.WorkflowDefinitions.Add(snapshot);
        w.Run.SnapshotDefinitionId = snapshot.Id;
        w.Step(snapshot.Id, 2, StepType.VideoAnalyze);
        WorkflowStep live = w.Step(w.Live.Id, 2, StepType.VideoCompile, compileJson: ProdRun04ef3b0aConfigs.Step2Compile);

        (await w.RevealsAsync(w.Result(live))).Should().BeTrue();
    }

    private static EditTimeline Doc(bool censored)
    {
        AssetRef asset = new(AssetRefKind.ProjectFile, ProjectFileId: Guid.NewGuid());
        ShapeMask mask = new(ShapeMask.SegmentationKind, AnalysisStepResultId: Guid.NewGuid(), TrackIds: ["g1"]);
        MediaClip clip = new(Guid.NewGuid(), "c", 0, 300, asset,
            RegionEffects: censored ? [new RegionClipEffect("blur", 1.0, mask)] : null);
        Track track = new(Guid.NewGuid(), TrackKind.Video, "Video", null, 100, false, true, false, false, null, [clip]);
        return new EditTimeline(Guid.NewGuid(), Guid.NewGuid(), "t",
            new EditTimelineSettings(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0), [track], [], null, null);
    }

    [Fact]
    public async Task An_editor_render_of_a_timeline_with_a_region_effect_refuses_the_plain_seed()
    {
        await using World w = new();
        WorkflowStep step = w.Step(w.Live.Id, 1, StepType.TimelineRender, renderJson: World.RenderConfig(w.Timeline(2, Doc(censored: true))));

        (await w.RevealsAsync(w.Result(step))).Should().BeTrue();
    }

    [Fact]
    public async Task An_editor_render_of_a_plain_timeline_still_seeds_the_tradeoff_of_the_fail_closed_rule()
    {
        await using World w = new();
        WorkflowStep step = w.Step(w.Live.Id, 1, StepType.TimelineRender, renderJson: World.RenderConfig(w.Timeline(2, Doc(censored: false))));

        (await w.RevealsAsync(w.Result(step))).Should().BeFalse();
    }

    [Fact]
    public async Task An_editor_render_whose_timeline_cannot_be_read_fails_closed()
    {
        await using World w = new();
        WorkflowStep step = w.Step(w.Live.Id, 1, StepType.TimelineRender, renderJson: World.RenderConfig(Guid.NewGuid()));

        (await w.RevealsAsync(w.Result(step))).Should().BeTrue();
    }

    [Fact]
    public async Task An_editor_render_judges_the_revision_it_rendered_not_the_current_one()
    {
        await using World w = new();
        // Revision 1 (rendered) had the blur; the user later removed it (current revision 3 is clean).
        Guid timeline = w.Timeline(3, Doc(censored: false), (1, Doc(censored: true)));
        WorkflowStep step = w.Step(w.Live.Id, 1, StepType.TimelineRender, renderJson: World.RenderConfig(timeline, revision: 1));

        (await w.RevealsAsync(w.Result(step))).Should().BeTrue();
    }

    [Fact]
    public async Task The_guard_judges_the_settings_the_run_used_from_its_snapshot_not_the_live_step()
    {
        await using World w = new();
        // The run's own (snapshot) step censored; the live step was edited afterwards to censor nothing.
        WorkflowDefinition snapshot = new() { Id = Guid.NewGuid(), ProjectId = w.ProjectId, Name = "snap", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        w.Db.WorkflowDefinitions.Add(snapshot);
        w.Run.SnapshotDefinitionId = snapshot.Id;
        w.Step(snapshot.Id, 2, StepType.VideoCompile, compileJson: ProdRun04ef3b0aConfigs.Step2Compile);
        WorkflowStep live = w.Step(w.Live.Id, 2, StepType.VideoCompile, compileJson: NoEffectsCompile);

        (await w.RevealsAsync(w.Result(live))).Should().BeTrue();
    }

    [Fact]
    public async Task A_legacy_run_without_a_snapshot_falls_back_to_the_live_step()
    {
        await using World w = new();
        WorkflowStep live = w.Step(w.Live.Id, 2, StepType.VideoCompile, compileJson: ProdRun04ef3b0aConfigs.Step2Compile);

        (await w.RevealsAsync(w.Result(live))).Should().BeTrue();
    }
}
