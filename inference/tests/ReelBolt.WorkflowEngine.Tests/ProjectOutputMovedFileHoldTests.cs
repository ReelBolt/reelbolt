using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Tests.CloudStorage.Core;
using ReelBolt.WorkflowEngine.Tests.Uploads;
using ReelBolt.Inference.Api.Services.Background;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A platform-made file is judged by its produced-at stamp, which a move never rewrites; a run by its first start,
/// which a Retry never rewrites. Every row here looks new and user-owned by its mutable fields.
/// </summary>
public sealed class ProjectOutputMovedFileHoldTests
{
    private static readonly DateTime Marker = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = Marker.AddDays(-3);
    private static readonly DateTime Later = Marker.AddDays(3);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class World
    {
        public InferenceApiDbContext Db { get; } = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid UserId { get; } = Guid.NewGuid();
        public Guid ProjectId { get; } = Guid.NewGuid();
        public Mock<IFileStorageService> Storage { get; } = new();

        public World()
        {
            Db.Projects.Add(new Project
            {
                Id = ProjectId,
                Name = "p",
                OwnerId = UserId,
                OrganizationId = TestTenancy.OrgOf(UserId),
                CsamOutputHoldAt = Marker,
            });
            Db.SaveChanges();
        }

        /// <summary>A file an output move left under userFiles: new upload time, user key, old produced-at.</summary>
        public ProjectFile MovedOutput(string path = "folder/made.mp4", DateTime? producedAt = null)
        {
            ProjectFile f = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = ProjectId,
                OriginalFileName = path.Split('/')[^1],
                OriginalPath = path,
                DirectoryPath = path.Contains('/') ? path[..path.LastIndexOf('/')] : null,
                Category = "userFiles",
                StorageKey = $"projects/{ProjectId}/userFiles/{Guid.NewGuid():N}.mp4",
                StorageBucket = "b",
                MimeType = "video/mp4",
                SizeBytes = 5,
                UploadedAt = Later,
                OutputProducedAt = producedAt ?? Old,
            };
            Db.ProjectFiles.Add(f);
            Db.SaveChanges();
            return f;
        }

        public ProjectFilesController Controller()
        {
            CloudFakeCurrentUser user = new(UserId, TestTenancy.OrgOf(UserId));
            return new ProjectFilesController(
                Db, user, TestTenancy.Access(Db, user), Storage.Object, Mock.Of<IBackgroundTaskQueue<FileSummarizationTask>>(), Mock.Of<IPublishEndpoint>(),
                null!, logger: NullLogger<ProjectFilesController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };
        }

        public void AssertUntouched(ProjectFile file)
        {
            Db.ChangeTracker.Clear();
            ProjectFile row = Db.ProjectFiles.IgnoreQueryFilters().Single(f => f.Id == file.Id);
            row.StorageKey.Should().Be(file.StorageKey);
            row.OriginalPath.Should().Be(file.OriginalPath);
            Storage.Verify(s => s.DeleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    private static void AssertUnavailable(IActionResult result)
    {
        ObjectResult refused = result.Should().BeAssignableTo<ObjectResult>().Which;
        refused.StatusCode.Should().Be(OutputGate.Unavailable().StatusCode);
        System.Text.Json.JsonSerializer.Serialize(refused.Value).Should().Contain(OutputHoldResponse.Code);
    }

    [Fact]
    public async Task A_moved_output_stays_held_though_its_upload_time_and_key_look_new()
    {
        World w = new();
        ProjectFile moved = w.MovedOutput();
        (await OutputGate.IsFileHeldAsync(w.Db, moved, Ct)).Should().BeTrue();
        (await OutputGate.IsKeyHeldAsync(w.Db, w.ProjectId, moved.StorageKey, Ct)).Should().BeTrue();
        (await OutputGate.HeldFileIdsAsync(w.Db, w.ProjectId, [moved], Ct)).Should().Contain(moved.Id);
    }

    [Fact]
    public async Task An_output_produced_after_the_marker_and_a_plain_upload_are_served()
    {
        World w = new();
        ProjectFile later = w.MovedOutput(producedAt: Later);
        ProjectFile upload = w.MovedOutput("own.mp4");
        upload.OutputProducedAt = null;
        upload.UploadedAt = Old;
        await w.Db.SaveChangesAsync(Ct);
        (await OutputGate.HeldFileIdsAsync(w.Db, w.ProjectId, [later, upload], Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_result_registering_after_the_marker_from_a_run_first_started_before_it_is_held()
    {
        World w = new();
        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = w.ProjectId,
            WorkflowDefinitionId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid().ToString(),
            FirstStartedAt = Old,
            StartedAt = Later,
        };
        WorkflowStepResult result = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = Guid.NewGuid(),
            Output = "",
            ExecutedAt = Later,
            OutputStorageKey = $"projects/{w.ProjectId}/outputFiles/r.mp4",
        };
        w.Db.Add(run);
        w.Db.Add(result);
        await w.Db.SaveChangesAsync(Ct);

        (await OutputGate.IsResultHeldAsync(w.Db, result.Id, Ct)).Should().BeTrue();
        (await OutputGate.HeldResultIdsAsync(w.Db, w.ProjectId, [result.Id], Ct)).Should().Contain(result.Id);
        (await OutputGate.IsKeyHeldAsync(w.Db, w.ProjectId, result.OutputStorageKey, Ct)).Should().BeTrue();
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("first", null, false)]
    [InlineData(null, "latest", false)]
    public void An_unknown_run_start_is_held_and_a_known_one_is_used(string? first, string? latest, bool held)
    {
        DateTime? f = first is null ? null : Later;
        DateTime? l = latest is null ? null : Later;
        ProjectOutputHoldState.At(Marker).HoldsOutput(Later, ProjectOutputHold.RunStart(f, l)).Should().Be(held);
    }

    [Fact]
    public async Task Move_refuses_a_held_file()
    {
        World w = new();
        ProjectFile f = w.MovedOutput();
        var result = await w.Controller().Move(w.ProjectId, new MoveProjectFilesRequest([f.Id], "elsewhere"), Ct);
        AssertUnavailable(result.Result!);
        w.AssertUntouched(f);
    }

    [Fact]
    public async Task RenameFolder_refuses_a_folder_holding_a_held_file()
    {
        World w = new();
        ProjectFile f = w.MovedOutput();
        AssertUnavailable(await w.Controller().RenameFolder(w.ProjectId, new RenameFolderRequest("folder", "renamed"), Ct));
        w.AssertUntouched(f);
    }

    [Fact]
    public async Task DeleteFolder_refuses_a_folder_holding_a_held_file()
    {
        World w = new();
        ProjectFile f = w.MovedOutput();
        AssertUnavailable(await w.Controller().DeleteFolder(w.ProjectId, new FolderDeleteRequest("folder", Recursive: true), Ct));
        w.AssertUntouched(f);
    }

    [Fact]
    public async Task Delete_refuses_a_held_file()
    {
        World w = new();
        ProjectFile f = w.MovedOutput();
        AssertUnavailable(await w.Controller().Delete(w.ProjectId, f.Id, Ct));
        w.AssertUntouched(f);
    }

    [Fact]
    public async Task The_file_list_omits_a_moved_held_file_and_keeps_a_later_one()
    {
        World w = new();
        ProjectFile held = w.MovedOutput();
        ProjectFile later = w.MovedOutput("folder/later.mp4", Later);
        var result = await w.Controller().List(w.ProjectId, Ct);
        List<ProjectFileResponse> files = (List<ProjectFileResponse>)((OkObjectResult)result.Result!).Value!;
        files.Select(x => x.Id).Should().Equal(later.Id);
        _ = held;
    }

    [Fact]
    public async Task Preview_content_refuses_a_held_file()
    {
        World w = new();
        ProjectFile f = w.MovedOutput();
        var result = await w.Controller().GetContent(w.ProjectId, f.Id, Ct);
        AssertUnavailable(result.Result!);
        w.Storage.Verify(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Search_leaves_out_a_chunk_of_a_held_file()
    {
        World w = new();
        ProjectFile held = w.MovedOutput();
        ProjectFile later = w.MovedOutput("folder/later.mp4", Later);
        Mock<IEmbeddingGenerator<string, Embedding<float>>> embed = new();
        embed.Setup(e => e.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 1f })]));
        Mock<IVectorIndexService> index = new();
        index.Setup(i => i.SearchAsync(w.ProjectId, It.IsAny<string?>(), It.IsAny<ReadOnlyMemory<float>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchChunkResult(held.Id, "a", "a", 0, 1, "en", "synthetic", 0.9f),
                new VectorSearchChunkResult(later.Id, "b", "b", 0, 1, "en", "synthetic", 0.8f),
            ]);
        ProjectFilesController controller = new(
            w.Db, new CloudFakeCurrentUser(w.UserId, TestTenancy.OrgOf(w.UserId)), TestTenancy.Access(w.Db, new CloudFakeCurrentUser(w.UserId, TestTenancy.OrgOf(w.UserId))),
            w.Storage.Object, Mock.Of<IBackgroundTaskQueue<FileSummarizationTask>>(), Mock.Of<IPublishEndpoint>(),
            new VectorSearchQueryService(embed.Object, index.Object), logger: NullLogger<ProjectFilesController>.Instance);

        var result = await controller.Search(w.ProjectId, new SearchProjectFilesRequest("query", 5), Ct);

        SearchProjectFilesResponse body = (SearchProjectFilesResponse)((OkObjectResult)result.Result!).Value!;
        System.Text.Json.JsonSerializer.Serialize(body).Should().Contain(later.Id.ToString()).And.NotContain(held.Id.ToString());
    }

    [Fact]
    public async Task A_direct_upload_cannot_be_created_to_replace_a_held_file()
    {
        DirectUploadHarness h = new();
        ProjectFile f = h.SeedFile(storageKey: $"projects/{h.Project.Id}/userFiles/moved.mp4");
        f.OutputProducedAt = Old;
        h.Project.CsamOutputHoldAt = Marker;
        await h.Db.SaveChangesAsync(Ct);

        Func<Task> act = () => h.Create(h.Request(sizeBytes: 10, fileName: "old.mp4", replacesFileId: f.Id));

        (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which.Code.Should().Be(OutputHoldResponse.Code);
    }

    [Fact]
    public async Task A_direct_upload_replacing_a_file_held_after_its_ticket_is_refused_at_completion()
    {
        DirectUploadHarness h = new();
        ProjectFile f = h.SeedFile(storageKey: $"projects/{h.Project.Id}/userFiles/moved.mp4");
        f.OutputProducedAt = Old;
        await h.Db.SaveChangesAsync(Ct);
        string key = f.StorageKey;
        var ticket = await h.Create(h.Request(sizeBytes: 10, fileName: "old.mp4", replacesFileId: f.Id));
        h.Storage.Put(ticket.Key, 10);
        h.Project.CsamOutputHoldAt = Marker;
        await h.Db.SaveChangesAsync(Ct);

        Func<Task> act = () => h.Service.CompleteAsync(ticket, new DirectUploadCompletion([]), Ct);

        (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which.Code.Should().Be(OutputHoldResponse.Code);
        h.Db.ChangeTracker.Clear();
        h.Db.ProjectFiles.Single(x => x.Id == f.Id).StorageKey.Should().Be(key);
    }

    [Fact]
    public async Task Rename_of_an_unstamped_row_whose_bytes_are_kept_changes_nothing_and_returns_no_summary_or_key()
    {
        World w = new();
        ProjectFile f = w.MovedOutput();
        f.OutputProducedAt = null;
        f.StorageKey = $"projects/{w.ProjectId}/outputFiles/{Guid.NewGuid():N}.mp4";
        f.UploadedAt = Old;
        f.AgentSummary = "SUMMARY-MARK";
        w.Db.SaveChanges();

        var result = await w.Controller().Rename(w.ProjectId, f.Id, new RenameProjectFileRequest("renamed.mp4"), Ct);

        result.Result.Should().BeOfType<ObjectResult>();
        System.Text.Json.JsonSerializer.Serialize(((ObjectResult)result.Result!).Value).Should().NotContain("SUMMARY-MARK").And.NotContain(f.StorageKey);
        w.AssertUntouched(f);
    }
}
