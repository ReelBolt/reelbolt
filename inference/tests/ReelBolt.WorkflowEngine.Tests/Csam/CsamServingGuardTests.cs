using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Csam;

/// <summary>
/// Every serving surface treats a quarantined row as absent: list and search omit it; download,
/// content, update, picture usage, delete and the inline media stream answer 404.
/// </summary>
public class CsamServingGuardTests
{
    [Fact]
    public async Task List_ExcludesQuarantined()
    {
        Fixture f = new();

        ActionResult<List<ProjectFileResponse>> result = await f.Controller.List(f.Project.Id, CancellationToken.None);

        List<ProjectFileResponse> files = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<List<ProjectFileResponse>>().Subject;
        files.Should().ContainSingle().Which.Id.Should().Be(f.Clean.Id);
    }

    [Fact]
    public async Task Download_Quarantined_ReturnsNotFound()
    {
        Fixture f = new();
        IActionResult result = await f.Controller.Download(f.Project.Id, f.Quarantined.Id, CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetContent_Quarantined_ReturnsNotFound()
    {
        Fixture f = new();
        ActionResult<ProjectFileContentResponse> result = await f.Controller.GetContent(f.Project.Id, f.Quarantined.Id, CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task UpdateContent_Quarantined_ReturnsNotFound()
    {
        Fixture f = new();
        ActionResult<ProjectFileResponse> result = await f.Controller.UpdateContent(
            f.Project.Id, f.Quarantined.Id, null!, CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task PictureUsage_Quarantined_ReturnsNotFound()
    {
        Fixture f = new();
        ActionResult<PictureUsageResponse> result = await f.Controller.GetPictureUsage(f.Project.Id, f.Quarantined.Id, CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Delete_Quarantined_ReturnsNotFound()
    {
        Fixture f = new();
        IActionResult result = await f.Controller.Delete(f.Project.Id, f.Quarantined.Id, CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task MediaStream_Quarantined_ReturnsNotFound()
    {
        Fixture f = new();
        Mock<ICurrentUser> user = new();
        Mock<IProjectAccessService> access = new();
        access.Setup(a => a.GetAsync(f.Project.Id, It.IsAny<ProjectPermission>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectAccessResult(ProjectAccessOutcome.Found, f.Project));
        ProjectFileMediaController media = new(
            f.Db, user.Object, access.Object, Mock.Of<IAmazonS3>(), new ConfigurationBuilder().Build());

        IActionResult result = await media.Stream(f.Project.Id, f.Quarantined.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Search_ExcludesQuarantined()
    {
        Fixture f = new();

        ActionResult<SearchProjectFilesResponse> result = await f.Controller.Search(
            f.Project.Id, new SearchProjectFilesRequest("hello"), CancellationToken.None);

        SearchProjectFilesResponse response = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<SearchProjectFilesResponse>().Subject;
        response.Results.Should().ContainSingle().Which.FileId.Should().Be(f.Clean.Id);
    }

    [Fact]
    public void FileUnavailable_IsTheGeneric422Body()
    {
        ObjectResult result = ProjectFilesController.FileUnavailable();

        result.StatusCode.Should().Be(422);
        string json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        json.Should().Contain("FILE_UNAVAILABLE");
        json.Should().Contain("This file cannot be used.");
        json.Should().NotContain("csam");
        json.Should().NotContain("quarantine");
    }

    [Fact]
    public async Task ReindexFile_Quarantined_IsNotFound()
    {
        Fixture f = new();
        IActionResult result = await f.Controller.ReindexFile(f.Project.Id, f.Quarantined.Id, CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task DeleteFolder_WithAQuarantinedFile_LeavesRowObjectAndAudit()
    {
        Fixture f = new();
        f.Quarantined.DirectoryPath = "bad";
        f.Quarantined.OriginalPath = "bad/bad.jpg";
        f.Db.CsamScanAudits.Add(new CsamScanAudit
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            ProjectFileId = f.Quarantined.Id,
            ProjectFileName = f.Quarantined.OriginalFileName,
            Status = "match",
            Quarantined = true,
            QuarantineSucceeded = true,
            CreatedAt = DateTime.UtcNow
        });
        await f.Db.SaveChangesAsync();

        IActionResult result = await f.Controller.DeleteFolder(
            f.Project.Id, new FolderDeleteRequest("bad", Recursive: true), CancellationToken.None);

        ObjectResult conflict = result.Should().BeOfType<ObjectResult>().Subject;
        conflict.StatusCode.Should().Be(409);
        System.Text.Json.JsonSerializer.Serialize(conflict.Value).Should().Contain("FILE_UNAVAILABLE");

        f.Db.ProjectFiles.Should().Contain(row => row.Id == f.Quarantined.Id);
        f.Db.CsamScanAudits.Should().ContainSingle();
        f.Storage.Verify(s => s.DeleteManyAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()),
            Times.Never, "the quarantined object must never be deleted");
    }

    [Fact]
    public async Task Move_WithAQuarantinedFile_IsRefused()
    {
        Fixture f = new();
        string quarantineKey = f.Quarantined.StorageKey;

        ActionResult<List<ProjectFileResponse>> result = await f.Controller.Move(
            f.Project.Id, new MoveProjectFilesRequest([f.Quarantined.Id], "moved"), CancellationToken.None);

        ObjectResult conflict = result.Result.Should().BeOfType<ObjectResult>().Subject;
        conflict.StatusCode.Should().Be(409);
        f.Quarantined.StorageKey.Should().Be(quarantineKey, "moving a quarantined row would break its evidence key");
        f.Storage.Verify(
            s => s.MoveAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RenameFolder_WithAQuarantinedFile_IsRefused()
    {
        Fixture f = new();
        f.Quarantined.DirectoryPath = "bad";
        f.Quarantined.OriginalPath = "bad/bad.jpg";
        await f.Db.SaveChangesAsync();

        IActionResult result = await f.Controller.RenameFolder(
            f.Project.Id, new RenameFolderRequest("bad", "worse"), CancellationToken.None);

        ObjectResult conflict = result.Should().BeOfType<ObjectResult>().Subject;
        conflict.StatusCode.Should().Be(409);
        f.Db.ProjectFiles.Should().Contain(row => row.Id == f.Quarantined.Id && row.OriginalPath == "bad/bad.jpg",
            "a quarantined row's path and object are evidence and must not be renamed");
    }

    [Fact]
    public async Task DeletingAProjectFile_KeepsItsAuditRow()
    {
        Fixture f = new();
        f.Db.CsamScanAudits.Add(new CsamScanAudit
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            ProjectFileId = f.Clean.Id,
            ProjectFileName = f.Clean.OriginalFileName,
            Status = "clean",
            CreatedAt = DateTime.UtcNow
        });
        await f.Db.SaveChangesAsync();

        f.Db.ProjectFiles.Remove(f.Clean);
        await f.Db.SaveChangesAsync();

        CsamScanAudit audit = f.Db.CsamScanAudits.Should().ContainSingle().Subject;
        audit.ProjectFileName.Should().Be(f.Clean.OriginalFileName,
            "the name snapshot keeps the admin list readable after the file is gone");
    }

    // ------------------------------------------------------------------ engine outputs on a marked project

    private static async Task<(Fixture F, ProjectFile Output)> MarkedWithOutputAsync(int producedOffsetSeconds, bool marked = true)
    {
        Fixture f = new();
        // These cases are about the reuse marker alone. A CSAM-flagged row would also hold every output of the project,
        // which has its own tests.
        f.Db.ProjectFiles.RemoveRange(f.Db.ProjectFiles.Where(r => r.CsamStatus == CsamScanStatus.Quarantined));
        DateTime marker = DateTime.UtcNow.AddDays(-1);
        if (marked)
            f.Db.Projects.Single(p => p.Id == f.Project.Id).ContentQuarantinedAt = marker;
        ProjectFile output = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            OriginalFileName = "film.mp4",
            OriginalPath = "out/film.mp4",
            DirectoryPath = "out",
            Category = "outputFiles",
            StorageKey = $"projects/{f.Project.Id}/outputFiles/out/film.mp4",
            MimeType = "video/mp4",
            SizeBytes = 4,
            CsamStatus = CsamScanStatus.Clean,
            OutputProducedAt = marker.AddSeconds(producedOffsetSeconds)
        };
        f.Db.ProjectFiles.Add(output);
        await f.Db.SaveChangesAsync();
        return (f, output);
    }

    [Fact]
    public async Task Move_Rename_and_RenameFolder_of_an_old_output_on_a_marked_project_are_refused_before_any_object_moves()
    {
        (Fixture f, ProjectFile output) = await MarkedWithOutputAsync(-3600);
        string key = output.StorageKey;

        ObjectResult move = (await f.Controller.Move(
            f.Project.Id, new MoveProjectFilesRequest([output.Id], "moved", "userFiles"), CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Subject;
        ObjectResult rename = (await f.Controller.Rename(
            f.Project.Id, output.Id, new RenameProjectFileRequest("other.mp4"), CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Subject;
        ObjectResult folder = (await f.Controller.RenameFolder(
                f.Project.Id, new RenameFolderRequest("out", "elsewhere"), CancellationToken.None))
            .Should().BeOfType<ObjectResult>().Subject;

        foreach (ObjectResult refused in new[] { move, rename, folder })
        {
            refused.StatusCode.Should().Be(409);
            System.Text.Json.JsonSerializer.Serialize(refused.Value).Should().Contain("FILE_UNAVAILABLE");
        }

        ProjectFile after = f.Db.ProjectFiles.Single(r => r.Id == output.Id);
        after.StorageKey.Should().Be(key);
        after.Category.Should().Be("outputFiles");
        after.OriginalFileName.Should().Be("film.mp4");
        f.Storage.Verify(
            s => s.MoveAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_clean_upload_moves_freely_on_a_marked_project()
    {
        (Fixture f, _) = await MarkedWithOutputAsync(-3600);

        ActionResult<List<ProjectFileResponse>> result = await f.Controller.Move(
            f.Project.Id, new MoveProjectFilesRequest([f.Clean.Id], "moved"), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        f.Db.ProjectFiles.Single(r => r.Id == f.Clean.Id).DirectoryPath.Should().Be("moved");
    }

    [Fact]
    public async Task An_output_produced_after_the_marker_can_be_moved_and_keeps_its_stamp()
    {
        (Fixture f, ProjectFile output) = await MarkedWithOutputAsync(3600);
        DateTime stamp = output.OutputProducedAt!.Value;

        ActionResult<List<ProjectFileResponse>> result = await f.Controller.Move(
            f.Project.Id, new MoveProjectFilesRequest([output.Id], "moved", "userFiles"), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        ProjectFile after = f.Db.ProjectFiles.Single(r => r.Id == output.Id);
        after.Category.Should().Be("userFiles");
        after.OutputProducedAt.Should().Be(stamp, "a move never writes the stamp");
    }

    [Fact]
    public async Task A_move_on_an_unmarked_project_leaves_the_stamp_and_a_later_marker_then_refuses_the_next_move()
    {
        (Fixture f, ProjectFile output) = await MarkedWithOutputAsync(-3600, marked: false);
        DateTime stamp = output.OutputProducedAt!.Value;

        (await f.Controller.Move(f.Project.Id, new MoveProjectFilesRequest([output.Id], "moved", "userFiles"), CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>();
        f.Db.ProjectFiles.Single(r => r.Id == output.Id).OutputProducedAt.Should().Be(stamp);

        f.Db.Projects.Single(p => p.Id == f.Project.Id).ContentQuarantinedAt = stamp.AddHours(1);
        await f.Db.SaveChangesAsync();
        (await f.Controller.Move(f.Project.Id, new MoveProjectFilesRequest([output.Id], "again", "outputFiles"), CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409);
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Project project = new()
            {
                Id = Guid.NewGuid(),
                Name = "CSAM",
                OwnerId = Guid.NewGuid(),
                OrganizationId = Guid.NewGuid()
            };
            Db = CsamTestSupport.NewDb();
            Db.Projects.Add(project);
            Clean = new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                OriginalFileName = "clean.txt",
                Category = "userFiles",
                StorageKey = $"projects/{project.Id}/userFiles/clean.txt",
                MimeType = "text/plain",
                SizeBytes = 4,
                CsamStatus = CsamScanStatus.Clean
            };
            Quarantined = new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                OriginalFileName = "bad.jpg",
                Category = "userFiles",
                StorageKey = $"projects/{project.Id}/quarantine/bad.jpg",
                MimeType = "image/jpeg",
                SizeBytes = 4,
                CsamStatus = CsamScanStatus.Quarantined
            };
            Db.ProjectFiles.AddRange(Clean, Quarantined);
            Db.SaveChanges();
            Project = project;

            Mock<IProjectAccessService> access = new();
            access.Setup(a => a.GetAsync(project.Id, It.IsAny<ProjectPermission>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProjectAccessResult(ProjectAccessOutcome.Found, project));
            Mock<ICurrentUser> user = new();
            user.SetupGet(u => u.UserId).Returns(Guid.NewGuid());
            Storage = new Mock<IFileStorageService>();

            VectorSearchQueryService search = new(
                new FakeEmbeddings(),
                new FakeIndex(
                    new VectorSearchChunkResult(Clean.Id, "clean.txt", "clean.txt", 0, 1, "en", "hello", 0.9f),
                    new VectorSearchChunkResult(Quarantined.Id, "bad.jpg", "bad.jpg", 0, 1, "en", "hello", 0.8f)));

            Controller = new ProjectFilesController(
                Db,
                user.Object,
                access.Object,
                Storage.Object,
                Mock.Of<IBackgroundTaskQueue<FileSummarizationTask>>(),
                Mock.Of<IPublishEndpoint>(),
                search);
        }

        public Mock<IFileStorageService> Storage { get; }

        public InferenceApiDbContext Db { get; }
        public Project Project { get; }
        public ProjectFile Clean { get; }
        public ProjectFile Quarantined { get; }
        public ProjectFilesController Controller { get; }
    }

    private sealed class FakeEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(new float[] { 1, 2, 3 }) { ModelId = "test-model" })));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class FakeIndex(params VectorSearchChunkResult[] results) : IVectorIndexService
    {
        public Task<IReadOnlyList<VectorSearchChunkResult>> SearchAsync(
            Guid projectId, string? embeddingModel, ReadOnlyMemory<float> queryVector, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VectorSearchChunkResult>>(results);

        public Task UpsertFileChunksAsync(
            Guid projectId, string? embeddingModel, Guid fileId, string filePath, string fileName,
            IReadOnlyList<VectorizedFileChunk> chunks, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteFileChunksAsync(Guid projectId, Guid fileId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<int> DeleteProjectCollectionsAsync(Guid projectId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
