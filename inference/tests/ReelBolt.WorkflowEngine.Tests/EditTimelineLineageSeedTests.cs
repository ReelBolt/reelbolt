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
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-102 acceptance (PHASE 1: committed RED). Seeding a timeline from the FINAL compile of the prod-shaped chain
/// 04ef3b0a must give the run's final video decomposed from the ORIGINAL file: clips on the project file,
/// a lower blurred canvas-fill layer, the fitted clip, and an upper segmentation-masked blur layer, validator OK.
/// Today the seeder places step 2's render (an intermediate) and drops fill and region effects.
/// </summary>
public class EditTimelineLineageSeedTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task The_final_vertical_pad_blur_compile_seeds_from_the_original_with_fill_and_masked_blur_layers()
    {
        await using Chain c = await Chain.BuildAsync();

        EditTimelineResponse body = await c.SeedFinalAsync();

        EditTimeline doc = body.Document;
        doc.Settings.Width.Should().Be(1080);
        doc.Settings.Height.Should().Be(1920);
        List<Track> video = doc.Tracks.Where(t => t.Kind == TrackKind.Video).ToList();
        video.Should().HaveCount(3, "fill (lower), fitted clip, region-effect layer (upper)");

        // 1. every video clip is on the ORIGINAL project file, never on step 2's render.
        IEnumerable<MediaClip> clips = video.SelectMany(t => t.Items).OfType<MediaClip>().ToList();
        clips.Should().OnlyContain(m => m.AssetRef.Kind == AssetRefKind.ProjectFile && m.AssetRef.ProjectFileId == c.OriginalFile.Id);

        // 2. composed cuts: keepWholeSources twice = the whole 10 s source, no offset, 1:1 speed (300 frames @ 30).
        MediaClip main = video[1].Items.OfType<MediaClip>().Should().ContainSingle().Subject;
        main.SourceIn.Should().Be(0);
        (main.RecordOut - main.RecordIn).Should().Be(300);
        main.PlaybackRate.Should().Be(1.0);
        main.Effects.Should().BeNullOrEmpty("the fitted clip itself is unblurred");

        // 3. lower layer: same source, blur effect (the canvas fill), cover-scaled: it fills the 9:16 canvas.
        MediaClip fill = video[0].Items.OfType<MediaClip>().Should().ContainSingle().Subject;
        fill.SourceIn.Should().Be(main.SourceIn);
        fill.RecordIn.Should().Be(main.RecordIn);
        fill.RecordOut.Should().Be(main.RecordOut);
        fill.Effects.Should().NotBeEmpty().And.ContainSingle(e => e.Type == "blur" && e.Enabled && e.Amount == 2.0, "the engine fill is one sigma-40 blur = one editor blur of amount 2");
        fill.Mask.Should().BeNull();
        fill.Crop.Should().NotBeNull("the cover-scaled picture overflows a 9:16 canvas, so the editor crops it to the canvas");
        main.Crop.Should().BeNull("the Pad fit shows the whole picture");

        // 4. upper layer: same clip + transform as the fitted clip, segmentation mask on the VideoAnalyze track, blur.
        MediaClip region = video[2].Items.OfType<MediaClip>().Should().ContainSingle().Subject;
        region.Mask.Should().NotBeNull();
        region.Mask!.Kind.Should().Be("segmentation");
        region.Mask.AnalysisStepResultId.Should().Be(c.AnalyzeResult.Id);
        region.Mask.TrackIds.Should().Equal("g1");
        region.Effects.Should().ContainSingle(e => e.Type == "blur" && e.Enabled);
        region.SourceIn.Should().Be(main.SourceIn);
        region.Transform.Should().BeEquivalentTo(main.Transform);

        // 5. nothing representable was dropped, so no fallback span and no provenance flag on these clips.
        body.UnplacedItems.Should().NotBeNull().And.BeEmpty();

        // 6. valid.
        EditTimelineValidator.Validate(doc, await c.ValidationContextAsync(doc)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task When_the_lineage_seed_cannot_run_and_the_step_settings_are_unreadable_the_plain_seed_is_refused_and_nothing_is_created()
    {
        await using Chain c = await Chain.BuildAsync();
        WorkflowStep step = await c.Db.WorkflowSteps.SingleAsync(s => s.Id == c.FinalResult.WorkflowStepId);
        step.VideoCompileConfigJson = "{ not json";
        await c.Db.SaveChangesAsync();

        ActionResult<EditTimelineResponse> result = await c.CallSeedFinalAsync();

        ObjectResult refused = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(409);
        JsonSerializer.Serialize(refused.Value, Json).Should().Contain("SEED_WOULD_REVEAL_CENSORED");
        (await c.Db.EditTimelines.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_guard_refuses_the_plain_seed_only_for_a_compile_with_region_effects()
    {
        await using Chain c = await Chain.BuildAsync();
        WorkflowStepResult withEffects = await c.Db.WorkflowStepResults.Include(r => r.WorkflowStep)
            .SingleAsync(r => r.WorkflowStep.StepOrder == 2 && r.WorkflowStep.StepType == StepType.VideoCompile);
        WorkflowStepResult withoutEffects = c.FinalResult;

        (await ReelBolt.Inference.Api.Services.Editing.PlainSeedCensorGuard.PlainSeedWouldRevealAsync(c.Db, c.Project.Id, withEffects, CancellationToken.None)).Should().BeTrue();

        WorkflowStep step = await c.Db.WorkflowSteps.SingleAsync(s => s.Id == withoutEffects.WorkflowStepId);
        VideoCompileStepConfig cfg = JsonSerializer.Deserialize<VideoCompileStepConfig>(step.VideoCompileConfigJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        cfg.RegionEffects.Should().BeNullOrEmpty();
        (await ReelBolt.Inference.Api.Services.Editing.PlainSeedCensorGuard.PlainSeedWouldRevealAsync(c.Db, c.Project.Id, withoutEffects, CancellationToken.None)).Should().BeFalse();
    }

    private sealed class Chain : IAsyncDisposable
    {
        public required InferenceApiDbContext Db { get; init; }
        public required Project Project { get; init; }
        public required ProjectFile OriginalFile { get; init; }
        public required WorkflowStepResult AnalyzeResult { get; init; }
        public required WorkflowStepResult FinalResult { get; init; }

        public static async Task<Chain> BuildAsync()
        {
            InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);
            Guid ownerId = Guid.NewGuid();
            Project project = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Projects.Add(project);
            string originalKey = $"projects/{project.Id}/userFiles/shoot.mp4";
            ProjectFile file = new() { Id = Guid.NewGuid(), ProjectId = project.Id, OriginalFileName = "shoot.mp4", StorageKey = originalKey, MimeType = "video/mp4", SizeBytes = 1, UploadedAt = DateTime.UtcNow };
            db.ProjectFiles.Add(file);
            WorkflowDefinition def = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            WorkflowExecution run = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = def.Id, ProjectId = project.Id };
            db.WorkflowDefinitions.Add(def);
            db.WorkflowExecutions.Add(run);
            Mock<IFileStorageService> storage = new();

            WorkflowStepResult Step(int order, StepType type, string? analyzeCfg, string? compileCfg, string? output, string? artifact, string? timeline)
            {
                WorkflowStep step = new()
                {
                    Id = Guid.NewGuid(),
                    WorkflowDefinitionId = def.Id,
                    AgentDefinitionId = Guid.NewGuid(),
                    StepOrder = order,
                    StepType = type,
                    VideoAnalyzeConfigJson = analyzeCfg,
                    VideoCompileConfigJson = compileCfg
                };
                WorkflowStepResult r = new()
                {
                    Id = Guid.NewGuid(),
                    WorkflowExecutionId = run.Id,
                    WorkflowStepId = step.Id,
                    OutputStorageKey = output,
                    ArtifactStorageKey = artifact,
                    TimelineStorageKey = timeline,
                    ExecutedAt = DateTime.UtcNow.AddSeconds(order),
                    CompletedAt = DateTime.UtcNow.AddSeconds(order)
                };
                db.WorkflowSteps.Add(step);
                db.WorkflowStepResults.Add(r);
                return r;
            }

            string root = $"projects/{project.Id}";
            string artifactKey = $"{root}/agentFiles/video-analysis/{run.Id}/artifact-1.json";
            string render2 = $"{root}/outputFiles/censored-no-bars.mp4";
            string render4 = $"{root}/outputFiles/vertical-filled.mp4";
            string m2 = $"{root}/outputFiles/manifest-2.json";
            string m4 = $"{root}/outputFiles/manifest-4.json";

            WorkflowStepResult s1 = Step(1, StepType.VideoAnalyze, ProdRun04ef3b0aConfigs.Step1Analyze.Replace(ProdRun04ef3b0aConfigs.PlaceholderProjectFileId, file.Id.ToString()), null, null, artifactKey, null);
            WorkflowStepResult s2 = Step(2, StepType.VideoCompile, null, ProdRun04ef3b0aConfigs.Step2Compile, render2, null, m2);
            Step(3, StepType.VideoAnalyze, ProdRun04ef3b0aConfigs.Step3Analyze, null, null, $"{root}/agentFiles/video-analysis/{run.Id}/artifact-3.json", null);
            WorkflowStepResult s4 = Step(4, StepType.VideoCompile, null, ProdRun04ef3b0aConfigs.Step4Compile, render4, null, m4);
            await db.SaveChangesAsync();

            // Step 1's artifact: one segmentation track "g1" ("digital speedometer"). Only the fields the lookup reads.
            string artifact = """{"sources":[{"sourceIndex":0,"projectFileId":"__FILE__"}],"segmentationTracks":[{"id":"g1","sourceIndex":0,"prompt":"digital speedometer","startSec":0,"endSec":10,"frameCount":300,"fps":30,"coverage":0.9,"confidence":0.8,"maskStorageKey":"m","maskContentHash":"h","model":"sam"}]}""";
            artifact = artifact.Replace("__FILE__", file.Id.ToString());
            storage.Setup(s => s.DownloadAsync(project.Id, artifactKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(artifact)));

            // Step 2 manifest: the ORIGINAL file, whole (keepWholeSources), 16:9. Step 4 manifest: step 2's render, whole, 9:16 canvas.
            Manifest(storage, project, run, m2, new TimelineOutputVideo(1920, 1080, 30, 10, render2), new TimelineSource(0, originalKey, 10, 1920, 1080, 30, ProjectFileId: file.Id));
            Manifest(storage, project, run, m4, new TimelineOutputVideo(1080, 1920, 30, 10, render4), new TimelineSource(0, render2, 10, 1920, 1080, 30, StepResultId: s2.Id));
            return new Chain { Db = db, Project = project, OriginalFile = file, AnalyzeResult = s1, FinalResult = s4 }.With(storage);
        }

        private Chain With(Mock<IFileStorageService> storage) { _storage = storage; return this; }

        private Mock<IFileStorageService>? _storage;

        private static void Manifest(Mock<IFileStorageService> storage, Project project, WorkflowExecution run, string key, TimelineOutputVideo output, TimelineSource source)
        {
            TimelineManifest manifest = new(1, project.Id, run.Id, output, [source],
            [
                new TimelineTrack("track-video", "video", "Video", 0,
                [
                    new TimelineItem(Id: "cut-1", TimelineStartSec: 0, TimelineEndSec: 10, SourceIndex: 0, SourceStartSec: 0, SourceEndSec: 10, Label: "Whole source")
                ])
            ]);
            storage.Setup(s => s.DownloadAsync(project.Id, key, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, Json))));
        }

        public async Task<EditTimelineResponse> SeedFinalAsync()
        {
            ActionResult<EditTimelineResponse> result = await CallSeedFinalAsync();
            ObjectResult created = result.Result.Should().BeOfType<ObjectResult>().Subject;
            created.StatusCode.Should().Be(201);
            return created.Value.Should().BeOfType<EditTimelineResponse>().Subject;
        }

        public async Task<ActionResult<EditTimelineResponse>> CallSeedFinalAsync()
        {
            Mock<ICurrentUser> u = new();
            u.SetupGet(x => x.UserId).Returns(Project.OwnerId);
            u.SetupGet(x => x.Email).Returns("test@example.com");
            u.SetupGet(x => x.IsAuthenticated).Returns(true);
            u.SetupGet(x => x.OrganizationId).Returns(TestTenancy.OrgOf(Project.OwnerId));
            u.SetupGet(x => x.OrgRole).Returns(ReelBolt.Shared.Tenancy.OrganizationRole.Member);
            EditTimelinesController controller = new(
                Db, u.Object, TestTenancy.Access(Db, u.Object), _storage!.Object,
                new Mock<ReelBolt.Inference.Api.Services.Workflows.IWorkflowExecutionService>().Object);
            return await controller.CreateEditTimeline(
                Project.Id, new CreateEditTimelineRequest("Seeded", FinalResult.Id), CancellationToken.None);
        }

        public async Task<EditTimelineValidationContext> ValidationContextAsync(EditTimeline document)
        {
            var fileOwners = await Db.ProjectFiles.ToDictionaryAsync(f => f.Id, f => f.ProjectId);
            var resultOwners = await Db.WorkflowStepResults.Select(r => new { r.Id, r.WorkflowExecution.ProjectId }).ToDictionaryAsync(r => r.Id, r => r.ProjectId);
            IReadOnlyDictionary<Guid, IReadOnlySet<string>> tracks = new Dictionary<Guid, IReadOnlySet<string>> { [AnalyzeResult.Id] = new HashSet<string> { "g1" } };
            IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, SegmentationTrackFacts>> facts = new Dictionary<Guid, IReadOnlyDictionary<string, SegmentationTrackFacts>>
            {
                [AnalyzeResult.Id] = new Dictionary<string, SegmentationTrackFacts> { ["g1"] = new SegmentationTrackFacts("g1", new AssetRef(AssetRefKind.ProjectFile, OriginalFile.Id), false, 10, null) }
            };
            return new EditTimelineValidationContext(Project.Id, new Dictionary<AssetRef, long>(), fileOwners, resultOwners, FramesPerSecond: 30, SegmentationTrackIds: tracks, SegmentationTrackFacts: facts);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
