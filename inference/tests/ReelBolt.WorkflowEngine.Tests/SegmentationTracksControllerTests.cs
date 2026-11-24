#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class SegmentationTracksControllerTests
{
    private const string Artifact = """
        {"segmentationTracks":[{"id":"g1","prompt":"the dog","sourceIndex":0,"startSec":1,"endSec":3,"fps":30,"maskStorageKey":"projects/x/agentFiles/segmentation/m.mp4","maskContentHash":"abc",
        "boxKeyframes":[{"timeSec":1,"x":0.1,"y":0.2,"w":0.3,"h":0.4},{"timeSec":2,"x":0.2,"y":0.2,"w":0.3,"h":0.4}],"facts":{"firstNonEmptyFrame":30,"lastNonEmptyFrame":89}}]}
        """;

    [Fact]
    public async Task Happy_path_returns_the_tracks_without_any_storage_key()
    {
        using Fixture f = await Fixture.CreateAsync();
        Guid id = await f.AddResultAsync(f.ProjectId, StepType.VideoAnalyze, $"projects/{f.ProjectId}/agentFiles/video-analysis/r/a.json");

        ActionResult<System.Collections.Generic.IReadOnlyList<SegmentationTrackResponse>> r = await f.Controller().Get(f.ProjectId, id, default);

        object? value = r.Result.Should().BeOfType<OkObjectResult>().Subject.Value;
        string json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().Be("""[{"trackId":"g1","label":"the dog","firstFrame":30,"lastFrame":89,"fps":30,"boxKeyframes":[{"t":1,"x":0.1,"y":0.2,"w":0.3,"h":0.4},{"t":2,"x":0.2,"y":0.2,"w":0.3,"h":0.4}],"coverageEndSec":3,"maskPreview":{"state":"none"}}]""");
        json.ToLowerInvariant().Should().NotContain("storage").And.NotContain("hash").And.NotContain("projects/");
    }

    [Fact]
    public async Task A_foreign_project_a_missing_id_and_a_non_analysis_step_are_the_same_404()
    {
        using Fixture f = await Fixture.CreateAsync();
        Guid foreign = await f.AddResultAsync(f.OtherProjectId, StepType.VideoAnalyze, $"projects/{f.OtherProjectId}/agentFiles/video-analysis/r/a.json");
        Guid wrongType = await f.AddResultAsync(f.ProjectId, StepType.Agent, $"projects/{f.ProjectId}/agentFiles/video-analysis/r/a.json");
        Guid wrongPrefix = await f.AddResultAsync(f.ProjectId, StepType.VideoAnalyze, $"projects/{f.OtherProjectId}/agentFiles/video-analysis/r/a.json");

        foreach (Guid id in new[] { foreign, Guid.NewGuid(), wrongType, wrongPrefix })
        {
            (await f.Controller().Get(f.ProjectId, id, default)).Result.Should().BeOfType<NotFoundResult>();
        }
    }

    [Fact]
    public async Task A_held_result_of_another_project_is_a_plain_404_and_an_own_held_result_is_the_hold_answer()
    {
        using Fixture f = await Fixture.CreateAsync();
        DateTime marker = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        foreach (Guid projectId in new[] { f.ProjectId, f.OtherProjectId })
        {
            (await f.Db.Projects.FindAsync(projectId))!.CsamOutputHoldAt = marker;
        }

        Guid foreign = await f.AddResultAsync(f.OtherProjectId, StepType.VideoAnalyze, KeyOf(f.OtherProjectId));
        Guid own = await f.AddResultAsync(f.ProjectId, StepType.VideoAnalyze, KeyOf(f.ProjectId));
        foreach (Guid id in new[] { foreign, own })
        {
            WorkflowStepResult row = await f.Db.WorkflowStepResults.Include(r => r.WorkflowExecution).SingleAsync(r => r.Id == id);
            row.ExecutedAt = marker.AddHours(-2);
            row.WorkflowExecution.StartedAt = marker.AddHours(-2);
        }

        await f.Db.SaveChangesAsync();

        (await f.Controller().Get(f.ProjectId, foreign, default)).Result.Should().BeOfType<NotFoundResult>();
        (await f.Controller().Get(f.ProjectId, own, default)).Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public void Response_type_has_no_storage_key_member()
    {
        typeof(SegmentationTrackResponse).GetProperties().Select(p => p.Name)
            .Should().Equal("TrackId", "Label", "FirstFrame", "LastFrame", "Fps", "BoxKeyframes", "CoverageEndSec", "MaskPreview");
    }

    private static string Track(string id, int keyframes = 1, string start = "1", string end = "3")
    {
        string boxes = string.Join(",", Enumerable.Range(0, keyframes).Select(i => "{\"timeSec\":" + i + ",\"x\":0.1,\"y\":0.2,\"w\":0.3,\"h\":0.4}"));
        return "{\"id\":\"" + id + "\",\"prompt\":\"p\",\"sourceIndex\":0,\"startSec\":" + start + ",\"endSec\":" + end + ",\"fps\":30,\"boxKeyframes\":[" + boxes + "]}";
    }

    private static string ArtifactOf(params string[] tracks) => "{\"segmentationTracks\":[" + string.Join(",", tracks) + "]}";

    private static string KeyOf(Guid project) => $"projects/{project}/agentFiles/video-analysis/r/a.json";

    private static System.Collections.Generic.IReadOnlyList<SegmentationTrackResponse> Tracks(ActionResult<System.Collections.Generic.IReadOnlyList<SegmentationTrackResponse>> r) =>
        (System.Collections.Generic.IReadOnlyList<SegmentationTrackResponse>)r.Result.Should().BeOfType<OkObjectResult>().Subject.Value!;

    [Fact]
    public async Task At_most_64_tracks_are_returned()
    {
        using Fixture f = await Fixture.CreateAsync();
        Guid id = await f.AddResultAsync(f.ProjectId, StepType.VideoAnalyze, KeyOf(f.ProjectId));
        string artifact = ArtifactOf(Enumerable.Range(1, 70).Select(i => Track($"g{i}")).ToArray());

        Tracks(await f.Controller(artifact).Get(f.ProjectId, id, default)).Should().HaveCount(SegmentationTracksController.MaxTracks);
    }

    [Fact]
    public async Task A_long_track_is_thinned_to_2000_keyframes_keeping_the_first_and_the_last()
    {
        using Fixture f = await Fixture.CreateAsync();
        Guid id = await f.AddResultAsync(f.ProjectId, StepType.VideoAnalyze, KeyOf(f.ProjectId));

        var boxes = Tracks(await f.Controller(ArtifactOf(Track("g1", keyframes: 5000))).Get(f.ProjectId, id, default))[0].BoxKeyframes;

        boxes.Should().HaveCount(SegmentationTracksController.MaxKeyframesPerTrack);
        boxes[0].T.Should().Be(0);
        boxes[^1].T.Should().Be(4999);
        boxes.Select(b => b.T).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task A_result_without_tracks_and_an_unreadable_artifact_are_404()
    {
        using Fixture f = await Fixture.CreateAsync();
        Guid empty = await f.AddResultAsync(f.ProjectId, StepType.VideoAnalyze, KeyOf(f.ProjectId));
        Guid unreadable = await f.AddResultAsync(f.ProjectId, StepType.VideoAnalyze, KeyOf(f.ProjectId));

        (await f.Controller(ArtifactOf()).Get(f.ProjectId, empty, default)).Result.Should().BeOfType<NotFoundResult>();
        (await f.Controller(unreadable: true).Get(f.ProjectId, unreadable, default)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task A_non_finite_time_in_the_artifact_is_a_404_not_a_500()
    {
        using Fixture f = await Fixture.CreateAsync();
        Guid id = await f.AddResultAsync(f.ProjectId, StepType.VideoAnalyze, KeyOf(f.ProjectId));

        (await f.Controller(ArtifactOf(Track("g1", end: "\"NaN\""))).Get(f.ProjectId, id, default)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task A_second_request_is_served_from_the_cache_without_downloading_again()
    {
        using Fixture f = await Fixture.CreateAsync();
        Guid id = await f.AddResultAsync(f.ProjectId, StepType.VideoAnalyze, KeyOf(f.ProjectId));
        SegmentationTracksController controller = f.Controller();

        await controller.Get(f.ProjectId, id, default);
        await controller.Get(f.ProjectId, id, default);

        f.Downloads.Should().Be(1);
    }

    private sealed class Fixture : IDisposable
    {
        public required InferenceApiDbContext Db { get; init; }
        public Guid ProjectId { get; init; }
        public Guid OtherProjectId { get; init; }
        public Guid OwnerId { get; init; }
        private Guid _definitionId;

        public static async Task<Fixture> CreateAsync()
        {
            InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Guid owner = Guid.NewGuid();
            Project Make() => new() { Id = Guid.NewGuid(), Name = "P", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Project a = Make(), b = Make();
            db.Projects.AddRange(a, b);
            await db.SaveChangesAsync();
            return new Fixture { Db = db, ProjectId = a.Id, OtherProjectId = b.Id, OwnerId = owner };
        }

        public async Task<Guid> AddResultAsync(Guid projectId, StepType type, string key)
        {
            if (_definitionId == Guid.Empty)
            {
                _definitionId = Guid.NewGuid();
                Db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = _definitionId, ProjectId = ProjectId, Name = "w", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            }

            Guid exec = Guid.NewGuid(), step = Guid.NewGuid(), result = Guid.NewGuid();
            Db.WorkflowExecutions.Add(new WorkflowExecution { Id = exec, WorkflowDefinitionId = _definitionId, ProjectId = projectId, OrganizationId = TestTenancy.OrgOf(OwnerId) });
            Db.WorkflowSteps.Add(new WorkflowStep { Id = step, WorkflowDefinitionId = _definitionId, StepOrder = 1, StepType = type });
            Db.WorkflowStepResults.Add(new WorkflowStepResult { Id = result, WorkflowExecutionId = exec, WorkflowStepId = step, ArtifactStorageKey = key });
            await Db.SaveChangesAsync();
            return result;
        }

        public int Downloads { get; private set; }

        public SegmentationTracksController Controller(string? artifact = null, bool unreadable = false)
        {
            Mock<ICurrentUser> user = new();
            user.SetupGet(u => u.UserId).Returns(OwnerId);
            user.SetupGet(u => u.Email).Returns("t@example.com");
            user.SetupGet(u => u.IsAuthenticated).Returns(true);
            user.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(OwnerId));
            user.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Member);
            Mock<IFileStorageService> storage = new();
            storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    Downloads++;
                    if (unreadable) throw new IOException("gone");
                    return Task.FromResult((Stream)new MemoryStream(Encoding.UTF8.GetBytes(artifact ?? Artifact)));
                });
            return new SegmentationTracksController(Db, TestTenancy.Access(Db, user.Object), storage.Object, NullLogger<SegmentationTracksController>.Instance);
        }

        public void Dispose() => Db.Dispose();
    }
}
