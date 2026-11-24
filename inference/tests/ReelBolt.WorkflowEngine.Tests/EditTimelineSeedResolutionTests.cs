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
using ReelBolt.Shared.Auth;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Seeding a timeline from a manifest whose source is a workflow RENDER (a step result, not a
/// project file): the real <c>SeedAssetResolver</c> through the real controller, no mocked resolver.
/// </summary>
public class EditTimelineSeedResolutionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_render_of_the_same_or_an_earlier_run_seeds_V1_with_its_camera_audio(bool sameExecution)
    {
        await using Harness h = await Harness.CreateAsync();
        WorkflowExecution seedExecution = await h.AddExecutionAsync(h.Project.Id);
        WorkflowExecution renderExecution = sameExecution ? seedExecution : await h.AddExecutionAsync(h.Project.Id);
        string renderKey = $"projects/{h.Project.Id}/outputFiles/render-{Guid.NewGuid():N}.mp4";
        WorkflowStepResult render = await h.AddStepResultAsync(renderExecution, outputKey: renderKey);
        h.AddAudioProbe(renderKey, hasAudio: true);
        await h.Db.SaveChangesAsync();

        EditTimelineResponse body = await h.SeedAsync(seedExecution, Manifest(h.Project.Id, seedExecution.Id, new TimelineSource(0, renderKey, 10, 1920, 1080, 30)));

        Track v1 = body.Document.Tracks.Single(t => t.Id == EditTimelineSeeder.TrackId("V1"));
        MediaClip cut = v1.Items.Should().ContainSingle().Subject.Should().BeOfType<MediaClip>().Subject;
        cut.AssetRef.Kind.Should().Be(AssetRefKind.StepResult);
        cut.AssetRef.StepResultId.Should().Be(render.Id);
        MediaClip camera = body.Document.Tracks.Single(t => t.Id == EditTimelineSeeder.TrackId("A1"))
            .Items.Should().ContainSingle().Subject.Should().BeOfType<MediaClip>().Subject;
        camera.AssetRef.StepResultId.Should().Be(render.Id);
        body.UnplacedItems.Should().NotBeNull().And.BeEmpty();
        EditTimelineValidator.Validate(body.Document, await h.ValidationContextAsync(body.Document)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_render_of_another_project_is_unresolved_reported_and_never_placed()
    {
        await using Harness h = await Harness.CreateAsync();
        Project other = await h.AddProjectAsync();
        WorkflowExecution foreignExecution = await h.AddExecutionAsync(other.Id);
        string foreignKey = $"projects/{other.Id}/outputFiles/render.mp4";
        WorkflowStepResult foreign = await h.AddStepResultAsync(foreignExecution, outputKey: foreignKey);
        WorkflowExecution execution = await h.AddExecutionAsync(h.Project.Id);

        EditTimelineResponse body = await h.SeedAsync(execution, Manifest(h.Project.Id, execution.Id, new TimelineSource(0, foreignKey, 10, 1920, 1080, 30)));

        body.Document.Tracks.Should().BeEmpty("the only cut was unplaceable");
        UnplacedTimelineItemResponse item = body.UnplacedItems.Should().ContainSingle().Subject;
        item.Should().Be(new UnplacedTimelineItemResponse("V1", "cut-1", "Cut one", "source_unresolved"));
        string wire = JsonSerializer.Serialize(body, Json);
        wire.Should().NotContain(foreign.Id.ToString()).And.NotContain("outputFiles").And.NotContain(foreignKey);
        wire.Should().Contain("\"unplacedItems\":[{\"track\":\"V1\"");
    }

    [Theory]
    [InlineData("stepResult")]
    [InlineData("projectFile")]
    public async Task A_trimmed_or_still_source_resolves_through_its_identity_when_its_key_is_a_scratch_key(string kind)
    {
        await using Harness h = await Harness.CreateAsync();
        WorkflowExecution execution = await h.AddExecutionAsync(h.Project.Id);
        WorkflowStepResult render = await h.AddStepResultAsync(execution, outputKey: $"projects/{h.Project.Id}/outputFiles/r.mp4");
        ProjectFile file = await h.AddProjectFileAsync("shoot.mp4", $"projects/{h.Project.Id}/userFiles/shoot.mp4");
        string scratch = $"projects/{h.Project.Id}/agentFiles/video-analysis/{execution.Id}/trim-0.mp4";
        TimelineSource source = kind == "stepResult"
            ? new TimelineSource(0, scratch, 4, 1920, 1080, 30, StepResultId: render.Id)
            : new TimelineSource(0, scratch, 4, 1920, 1080, 30, ProjectFileId: file.Id);

        EditTimelineResponse body = await h.SeedAsync(execution, Manifest(h.Project.Id, execution.Id, source));

        MediaClip cut = body.Document.Tracks.Single().Items.Should().ContainSingle().Subject.Should().BeOfType<MediaClip>().Subject;
        if (kind == "stepResult") cut.AssetRef.StepResultId.Should().Be(render.Id);
        else cut.AssetRef.ProjectFileId.Should().Be(file.Id);
        body.UnplacedItems.Should().BeEmpty();
    }

    [Fact]
    public async Task A_trimmed_source_adds_its_window_start_to_the_clips_source_range()
    {
        await using Harness h = await Harness.CreateAsync();
        WorkflowExecution execution = await h.AddExecutionAsync(h.Project.Id);
        ProjectFile file = await h.AddProjectFileAsync("shoot.mp4", $"projects/{h.Project.Id}/userFiles/shoot.mp4");
        // Window starts at 10s of the original; the cut is 1s into the window, 4s long.
        TimelineManifest manifest = Manifest(h.Project.Id, execution.Id,
            new TimelineSource(0, "scratch/trim.mp4", 6, 1920, 1080, 30, ProjectFileId: file.Id, TrimInSec: 10));
        manifest = manifest with
        {
            Tracks = [manifest.Tracks![0] with { Items = [manifest.Tracks[0].Items![0] with { SourceStartSec = 1, SourceEndSec = 5 }] }]
        };

        EditTimelineResponse body = await h.SeedAsync(execution, manifest);

        MediaClip cut = body.Document.Tracks.Single().Items.Should().ContainSingle().Subject.Should().BeOfType<MediaClip>().Subject;
        cut.SourceIn.Should().Be(330, "(1s into the window + the 10s window start) x 30fps");
        (cut.RecordOut - cut.RecordIn).Should().Be(120);
        cut.AssetRef.ProjectFileId.Should().Be(file.Id);
        JsonSerializer.Serialize(new TimelineSource(0, "k", 1, 2, 3, 30), Json).Should().NotContain("trimInSec");
    }

    [Fact]
    public async Task An_identity_owned_by_another_project_is_ignored_and_the_item_is_reported()
    {
        await using Harness h = await Harness.CreateAsync();
        Project other = await h.AddProjectAsync();
        h.Db.ProjectFiles.Add(new ProjectFile { Id = Guid.NewGuid(), ProjectId = other.Id, OriginalFileName = "x.mp4", StorageKey = "k/x.mp4", MimeType = "video/mp4", SizeBytes = 1, UploadedAt = DateTime.UtcNow });
        await h.Db.SaveChangesAsync();
        Guid foreignFile = h.Db.ProjectFiles.Single(f => f.ProjectId == other.Id).Id;
        WorkflowExecution execution = await h.AddExecutionAsync(h.Project.Id);

        EditTimelineResponse body = await h.SeedAsync(execution, Manifest(h.Project.Id, execution.Id,
            new TimelineSource(0, "scratch/none.mp4", 4, 1920, 1080, 30, ProjectFileId: foreignFile)));

        body.Document.Tracks.Should().BeEmpty();
        body.UnplacedItems.Should().ContainSingle().Which.Reason.Should().Be("source_unresolved");
        JsonSerializer.Serialize(body, Json).Should().NotContain(foreignFile.ToString());
    }

    [Fact]
    public async Task An_old_manifest_without_identity_still_resolves_by_key_and_a_project_file_wins_over_a_render()
    {
        await using Harness h = await Harness.CreateAsync();
        WorkflowExecution execution = await h.AddExecutionAsync(h.Project.Id);
        string sharedKey = $"projects/{h.Project.Id}/outputFiles/registered.mp4";
        await h.AddStepResultAsync(execution, outputKey: sharedKey);
        ProjectFile registered = await h.AddProjectFileAsync("registered.mp4", sharedKey);
        string plainRenderKey = $"projects/{h.Project.Id}/outputFiles/plain.mp4";
        WorkflowStepResult plain = await h.AddStepResultAsync(execution, outputKey: plainRenderKey);

        EditTimelineResponse body = await h.SeedAsync(execution, Manifest(h.Project.Id, execution.Id,
            new TimelineSource(0, sharedKey, 4, 1920, 1080, 30), new TimelineSource(1, plainRenderKey, 4, 1920, 1080, 30)));

        List<MediaClip> cuts = body.Document.Tracks.Single().Items.Cast<MediaClip>().ToList();
        cuts[0].AssetRef.Should().Be(new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: registered.Id));
        cuts[1].AssetRef.Should().Be(new AssetRef(AssetRefKind.StepResult, StepResultId: plain.Id));
    }

    [Fact]
    public async Task An_unresolvable_insert_asset_is_reported_as_asset_unresolved_and_the_rest_still_seeds()
    {
        await using Harness h = await Harness.CreateAsync();
        WorkflowExecution execution = await h.AddExecutionAsync(h.Project.Id);
        string renderKey = $"projects/{h.Project.Id}/outputFiles/r.mp4";
        await h.AddStepResultAsync(execution, outputKey: renderKey);
        TimelineManifest manifest = Manifest(h.Project.Id, execution.Id, new TimelineSource(0, renderKey, 10, 1920, 1080, 30)) with
        {
            Tracks =
            [
                .. Manifest(h.Project.Id, execution.Id, new TimelineSource(0, renderKey, 10, 1920, 1080, 30)).Tracks!,
                new TimelineTrack("t-ins", "inserts", "Inserts", 1,
                    [new TimelineItem(Id: "ins-1", TimelineStartSec: 0, TimelineEndSec: 2, AssetStorageKey: "projects/x/agentFiles/gone.png")])
            ]
        };

        EditTimelineResponse body = await h.SeedAsync(execution, manifest);

        body.Document.Tracks.Should().ContainSingle(t => t.Id == EditTimelineSeeder.TrackId("V1"));
        body.UnplacedItems.Should().ContainSingle().Which.Should().Be(new UnplacedTimelineItemResponse("V2", "ins-1", null, "asset_unresolved"));
    }

    [Fact]
    public void Source_identity_is_omitted_from_the_manifest_json_when_null()
    {
        JsonSerializer.Serialize(new TimelineSource(0, "k", 1, 2, 3, 30), Json).Should().NotContain("Id\"");
        JsonSerializer.Serialize(new TimelineSource(0, "k", 1, 2, 3, 30, StepResultId: Guid.Empty), Json).Should().Contain("\"stepResultId\"");
    }

    private static TimelineManifest Manifest(Guid projectId, Guid executionId, params TimelineSource[] sources) =>
        new(
            Version: 1,
            ProjectId: projectId,
            ExecutionId: executionId,
            OutputVideo: new TimelineOutputVideo(1920, 1080, 30, 8, "renders/out.mp4"),
            Sources: sources,
            Tracks:
            [
                new TimelineTrack("track-video", "video", "Video", 0,
                    sources.Select((_, i) => new TimelineItem(
                        Id: i == 0 ? "cut-1" : $"cut-{i + 1}",
                        TimelineStartSec: i * 4,
                        TimelineEndSec: i * 4 + 4,
                        SourceIndex: i,
                        SourceStartSec: 0,
                        SourceEndSec: 4,
                        Label: i == 0 ? "Cut one" : null)).ToList())
            ]);

    private sealed class Harness : IAsyncDisposable
    {
        public required InferenceApiDbContext Db { get; init; }
        public required Project Project { get; init; }
        public Mock<IFileStorageService> Storage { get; } = new();

        public static async Task<Harness> CreateAsync()
        {
            var options = new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            InferenceApiDbContext db = new(options);
            Harness h = new() { Db = db, Project = null! };
            Project project = await h.AddProjectAsync(db);
            return new Harness { Db = db, Project = project };
        }

        public Task<Project> AddProjectAsync() => AddProjectAsync(Db);

        private async Task<Project> AddProjectAsync(InferenceApiDbContext db)
        {
            Guid ownerId = Guid.NewGuid();
            Project project = new()
            {
                Id = Guid.NewGuid(),
                Name = "P",
                OwnerId = ownerId,
                OrganizationId = TestTenancy.OrgOf(ownerId),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            return project;
        }

        public async Task<ProjectFile> AddProjectFileAsync(string name, string storageKey)
        {
            ProjectFile file = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = Project.Id,
                OriginalFileName = name,
                StorageKey = storageKey,
                MimeType = "video/mp4",
                SizeBytes = 1,
                UploadedAt = DateTime.UtcNow
            };
            Db.ProjectFiles.Add(file);
            await Db.SaveChangesAsync();
            return file;
        }

        public async Task<WorkflowExecution> AddExecutionAsync(Guid projectId)
        {
            WorkflowDefinition definition = new() { Id = Guid.NewGuid(), ProjectId = projectId, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            WorkflowExecution execution = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = definition.Id, ProjectId = projectId };
            Db.WorkflowDefinitions.Add(definition);
            Db.WorkflowExecutions.Add(execution);
            await Db.SaveChangesAsync();
            return execution;
        }

        public async Task<WorkflowStepResult> AddStepResultAsync(WorkflowExecution execution, string? outputKey = null, string? timelineKey = null)
        {
            WorkflowStep step = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = execution.WorkflowDefinitionId, AgentDefinitionId = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Agent };
            WorkflowStepResult result = new()
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = execution.Id,
                WorkflowStepId = step.Id,
                OutputStorageKey = outputKey,
                TimelineStorageKey = timelineKey,
                ExecutedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            };
            Db.WorkflowSteps.Add(step);
            Db.WorkflowStepResults.Add(result);
            await Db.SaveChangesAsync();
            return result;
        }

        public void AddAudioProbe(string sourceKey, bool hasAudio) =>
            Db.MediaDerivatives.Add(new MediaDerivative
            {
                Id = Guid.NewGuid(),
                ProjectId = Project.Id,
                SourceProjectFileId = null,
                SourceStorageKey = sourceKey,
                Kind = MediaDerivativeKind.Proxy,
                StorageKey = "d/" + Guid.NewGuid(),
                CacheKey = Guid.NewGuid().ToString(),
                SourceProbeJson = new MediaSourceProbe(4, 30, 1, 1920, 1080, "h264", hasAudio ? "aac" : null, "mp4", 2, 48000, null, HasAudio: hasAudio).ToJson()
            });

        public async Task<EditTimelineResponse> SeedAsync(WorkflowExecution execution, TimelineManifest manifest)
        {
            string manifestKey = $"projects/{Project.Id}/outputFiles/manifest-{Guid.NewGuid():N}.json";
            WorkflowStepResult seed = await AddStepResultAsync(execution, timelineKey: manifestKey);
            Storage.Setup(s => s.DownloadAsync(Project.Id, manifestKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, Json))));

            ICurrentUser user = StubUser(Project.OwnerId);
            EditTimelinesController controller = new(
                Db, user, TestTenancy.Access(Db, user), Storage.Object,
                new Mock<ReelBolt.Inference.Api.Services.Workflows.IWorkflowExecutionService>().Object);

            ActionResult<EditTimelineResponse> result = await controller.CreateEditTimeline(
                Project.Id, new CreateEditTimelineRequest("Seeded", seed.Id), CancellationToken.None);
            ObjectResult created = result.Result.Should().BeOfType<ObjectResult>().Subject;
            created.StatusCode.Should().Be(201);
            return created.Value.Should().BeOfType<EditTimelineResponse>().Subject;
        }

        public async Task<EditTimelineValidationContext> ValidationContextAsync(EditTimeline document)
        {
            var fileOwners = await Db.ProjectFiles.ToDictionaryAsync(f => f.Id, f => f.ProjectId);
            var resultOwners = await Db.WorkflowStepResults.Select(r => new { r.Id, r.WorkflowExecution.ProjectId }).ToDictionaryAsync(r => r.Id, r => r.ProjectId);
            return new EditTimelineValidationContext(Project.Id, new Dictionary<AssetRef, long>(), fileOwners, resultOwners);
        }

        private static ICurrentUser StubUser(Guid userId)
        {
            Mock<ICurrentUser> u = new();
            u.SetupGet(x => x.UserId).Returns(userId);
            u.SetupGet(x => x.Email).Returns("test@example.com");
            u.SetupGet(x => x.IsAuthenticated).Returns(true);
            u.SetupGet(x => x.IsPlatformAdmin).Returns(false);
            u.SetupGet(x => x.OrganizationId).Returns(TestTenancy.OrgOf(userId));
            u.SetupGet(x => x.OrgRole).Returns(ReelBolt.Shared.Tenancy.OrganizationRole.Member);
            return u.Object;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
