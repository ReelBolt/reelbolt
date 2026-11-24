#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>
/// The file manager: list, download, read, edit, rename, move, delete, search and re-index for an output file made
/// before the marker (refused), a later output and an upload (served), with only the moderation flag (served) and on
/// a failed read (refused).
/// </summary>
public sealed class ProjectFilesSurfaceTests
{
    public enum Which { Earlier, Later, Upload }

    private sealed class Rig
    {
        public SurfaceWorld W { get; } = new();
        public Mock<IFileStorageService> Storage { get; } = new();
        public Mock<IPublishEndpoint> Publish { get; } = new();

        public Rig()
        {
            Set(W.EarlierFile, "out/earlier.md");
            Set(W.LaterFile, "later/later.md");
            Set(W.Upload, "mine/mine.md");
            W.Db.SaveChanges();
            Storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("# Title\nbody\n")));
            Storage.SetupGet(s => s.BucketName).Returns("reelforge");
            // An edit stores the new text under the same key.
            Storage.Setup(s => s.UploadAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid p, Guid f, Stream _, string name, string _, IReadOnlyDictionary<string, string>? _, CancellationToken _, string category, string? path) =>
                {
                    ProjectFile file = W.Db.ProjectFiles.Single(x => x.Id == f);
                    return new StoredFileObject(file.StorageKey, "reelforge", $"projects/{p}/", null, category, name, path ?? name, file.DirectoryPath, file.StorageFileName ?? name);
                });
        }

        private static void Set(ProjectFile f, string path)
        {
            f.MimeType = "text/markdown";
            f.OriginalPath = path;
            f.OriginalFileName = path[(path.LastIndexOf('/') + 1)..];
            f.DirectoryPath = path[..path.LastIndexOf('/')];
        }

        public ProjectFile File(Which w) => w switch { Which.Earlier => W.EarlierFile, Which.Later => W.LaterFile, _ => W.Upload };

        public ProjectFilesController Controller(InferenceApiDbContext? db = null, params VectorSearchChunkResult[] hits)
        {
            InferenceApiDbContext d = db ?? W.Db;
            VectorSearchQueryService search = new(new FakeEmbeddings(), new FakeIndex(hits));
            return new ProjectFilesController(
                d, W.User, W.Access(), Storage.Object, Mock.Of<IBackgroundTaskQueue<FileSummarizationTask>>(), Publish.Object, search)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };
        }
    }

    private static bool Refused(IActionResult? r) =>
        r is ObjectResult { StatusCode: 404, Value: { } v } && v.ToString()!.Contains("OUTPUT_UNAVAILABLE");

    private static bool Refused<T>(ActionResult<T> r) => Refused(r.Result);

    public static IEnumerable<object[]> Served()
    {
        yield return [SurfaceState.Clear];
        yield return [SurfaceState.ModerationOnly];
    }

    public static IEnumerable<object[]> KeptFiles() { yield return [Which.Later]; yield return [Which.Upload]; }

    // ---- list ---------------------------------------------------------------------------------

    private static List<Guid> Listed(ActionResult<List<ProjectFileResponse>> r) =>
        ((List<ProjectFileResponse>)r.Result.Should().BeOfType<OkObjectResult>().Subject.Value!).Select(f => f.Id).ToList();

    [Fact]
    public async Task The_list_leaves_out_an_earlier_output_file()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        List<Guid> ids = Listed(await r.Controller().List(r.W.Project.Id, CancellationToken.None));
        ids.Should().Contain([r.W.LaterFile.Id, r.W.Upload.Id]).And.NotContain(r.W.EarlierFile.Id);
    }

    [Theory]
    [MemberData(nameof(Served))]
    public async Task The_list_has_every_file_when_the_project_keeps_nothing(SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        Listed(await r.Controller().List(r.W.Project.Id, CancellationToken.None)).Should().Contain([r.W.EarlierFile.Id, r.W.LaterFile.Id, r.W.Upload.Id]);
    }

    [Fact]
    public async Task The_list_on_a_failed_read_leaves_out_every_output_file()
    {
        Rig r = new();
        r.W.In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = r.W.MarkerFailingDb();
        Listed(await r.Controller(failing).List(r.W.Project.Id, CancellationToken.None)).Should().NotContain(r.W.EarlierFile.Id).And.Contain(r.W.Upload.Id);
    }

    // ---- single-file routes --------------------------------------------------------------------

    private static readonly (string Name, Func<ProjectFilesController, Guid, Guid, Task<bool>> Call)[] FileRoutes =
    [
        ("Download", async (c, p, f) => Refused(await c.Download(p, f, CancellationToken.None))),
        ("GetContent", async (c, p, f) => Refused(await c.GetContent(p, f, CancellationToken.None))),
        ("UpdateContent", async (c, p, f) => Refused(await c.UpdateContent(p, f, new UpdateProjectFileContentRequest("changed"), CancellationToken.None))),
        ("Rename", async (c, p, f) => Refused(await c.Rename(p, f, new RenameProjectFileRequest("renamed.md"), CancellationToken.None))),
        ("GetPictureUsage", async (c, p, f) => Refused(await c.GetPictureUsage(p, f, CancellationToken.None))),
        ("Delete", async (c, p, f) => Refused(await c.Delete(p, f, CancellationToken.None))),
        ("ReindexFile", async (c, p, f) => Refused(await c.ReindexFile(p, f, CancellationToken.None))),
    ];

    public static IEnumerable<object[]> Routes() => FileRoutes.Select((r, i) => new object[] { i });

    public static IEnumerable<object[]> RoutesServed()
    {
        for (int i = 0; i < FileRoutes.Length; i++)
        {
            yield return [i, SurfaceState.Clear];
            yield return [i, SurfaceState.ModerationOnly];
        }
    }

    public static IEnumerable<object[]> RoutesKept()
    {
        for (int i = 0; i < FileRoutes.Length; i++)
        {
            yield return [i, Which.Later];
            yield return [i, Which.Upload];
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task An_earlier_output_file_is_refused(int route)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        (await FileRoutes[route].Call(r.Controller(), r.W.Project.Id, r.W.EarlierFile.Id)).Should().BeTrue(FileRoutes[route].Name);
        r.W.Db.ProjectFiles.Any(f => f.Id == r.W.EarlierFile.Id && f.OriginalFileName == "earlier.md").Should().BeTrue("nothing was changed or removed");
    }

    [Theory]
    [MemberData(nameof(RoutesKept))]
    public async Task A_later_output_file_and_an_upload_are_not_refused(int route, Which which)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        (await FileRoutes[route].Call(r.Controller(), r.W.Project.Id, r.File(which).Id)).Should().BeFalse(FileRoutes[route].Name);
    }

    [Theory]
    [MemberData(nameof(RoutesServed))]
    public async Task The_earlier_output_file_is_not_refused_when_the_project_keeps_nothing(int route, SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        (await FileRoutes[route].Call(r.Controller(), r.W.Project.Id, r.W.EarlierFile.Id)).Should().BeFalse(FileRoutes[route].Name);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task A_failed_read_refuses(int route)
    {
        Rig r = new();
        r.W.In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = r.W.MarkerFailingDb();
        (await FileRoutes[route].Call(r.Controller(failing), r.W.Project.Id, r.W.LaterFile.Id)).Should().BeTrue(FileRoutes[route].Name);
    }

    // ---- move and folder routes ----------------------------------------------------------------

    [Fact]
    public async Task Moving_an_earlier_output_file_is_refused_before_any_object_moves()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Refused(await r.Controller().Move(r.W.Project.Id, new MoveProjectFilesRequest([r.W.EarlierFile.Id], "elsewhere"), CancellationToken.None)).Should().BeTrue();
        r.Storage.Verify(s => s.MoveAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Moving_an_upload_is_not_refused()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Refused(await r.Controller().Move(r.W.Project.Id, new MoveProjectFilesRequest([r.W.Upload.Id], "elsewhere"), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Renaming_or_deleting_a_folder_with_an_earlier_output_file_is_refused()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Refused(await r.Controller().RenameFolder(r.W.Project.Id, new RenameFolderRequest("out", "out2"), CancellationToken.None)).Should().BeTrue();
        Refused(await r.Controller().DeleteFolder(r.W.Project.Id, new FolderDeleteRequest("out", Recursive: true), CancellationToken.None)).Should().BeTrue();
        r.W.Db.ProjectFiles.Any(f => f.Id == r.W.EarlierFile.Id).Should().BeTrue();
    }

    [Fact]
    public async Task Renaming_or_deleting_a_folder_of_uploads_is_not_refused()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Refused(await r.Controller().RenameFolder(r.W.Project.Id, new RenameFolderRequest("mine", "mine2"), CancellationToken.None)).Should().BeFalse();
        Refused(await r.Controller().DeleteFolder(r.W.Project.Id, new FolderDeleteRequest("mine2", Recursive: true), CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Served))]
    public async Task A_folder_with_the_earlier_output_file_is_not_refused_when_the_project_keeps_nothing(SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        Refused(await r.Controller().RenameFolder(r.W.Project.Id, new RenameFolderRequest("out", "out2"), CancellationToken.None)).Should().BeFalse();
        Refused(await r.Controller().DeleteFolder(r.W.Project.Id, new FolderDeleteRequest("out2", Recursive: true), CancellationToken.None)).Should().BeFalse();
    }

    // ---- search --------------------------------------------------------------------------------

    private static VectorSearchChunkResult Hit(ProjectFile f) => new(f.Id, f.OriginalPath ?? f.OriginalFileName, f.OriginalFileName, 0, 1, "en", "hello", 0.9f);

    private static List<Guid> Found(ActionResult<SearchProjectFilesResponse> r) =>
        ((SearchProjectFilesResponse)r.Result.Should().BeOfType<OkObjectResult>().Subject.Value!).Results.Select(x => x.FileId).ToList();

    [Fact]
    public async Task Search_leaves_out_text_from_an_earlier_output_file()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        ProjectFilesController c = r.Controller(null, Hit(r.W.EarlierFile), Hit(r.W.LaterFile), Hit(r.W.Upload));
        Found(await c.Search(r.W.Project.Id, new SearchProjectFilesRequest("hello"), CancellationToken.None))
            .Should().BeEquivalentTo([r.W.LaterFile.Id, r.W.Upload.Id]);
    }

    [Theory]
    [MemberData(nameof(Served))]
    public async Task Search_returns_every_hit_when_the_project_keeps_nothing(SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        ProjectFilesController c = r.Controller(null, Hit(r.W.EarlierFile), Hit(r.W.LaterFile), Hit(r.W.Upload));
        Found(await c.Search(r.W.Project.Id, new SearchProjectFilesRequest("hello"), CancellationToken.None)).Should().HaveCount(3);
    }

    [Fact]
    public async Task Search_on_a_failed_read_returns_no_output_file()
    {
        Rig r = new();
        r.W.In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = r.W.MarkerFailingDb();
        ProjectFilesController c = r.Controller(failing, Hit(r.W.EarlierFile), Hit(r.W.Upload));
        Found(await c.Search(r.W.Project.Id, new SearchProjectFilesRequest("hello"), CancellationToken.None))
            .Should().BeEquivalentTo([r.W.Upload.Id]);
    }

    // ---- bulk re-index -------------------------------------------------------------------------

    private static ReindexProjectFilesResponse Reindexed(ActionResult<ReindexProjectFilesResponse> r) =>
        (ReindexProjectFilesResponse)r.Result.Should().BeOfType<OkObjectResult>().Subject.Value!;

    [Fact]
    public async Task Bulk_reindex_counts_only_files_that_are_served()
    {
        Rig r = new();
        r.W.Db.ProjectFiles.ToList().ForEach(f => f.IndexingStatus = FileIndexingStatus.NotIndexed);
        r.W.In(SurfaceState.Held);
        ReindexProjectFilesResponse body = Reindexed(await r.Controller().ReindexProjectFiles(r.W.Project.Id, new ReindexProjectFilesRequest(MaxFiles: 10), CancellationToken.None));
        body.EligibleFiles.Should().Be(2);
        r.Publish.Verify(p => p.Publish(It.Is<ProjectFileIndexingRequested>(m => m.FileId == r.W.EarlierFile.Id), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(Served))]
    public async Task Bulk_reindex_counts_every_file_when_the_project_keeps_nothing(SurfaceState state)
    {
        Rig r = new();
        r.W.Db.ProjectFiles.ToList().ForEach(f => f.IndexingStatus = FileIndexingStatus.NotIndexed);
        r.W.In(state);
        Reindexed(await r.Controller().ReindexProjectFiles(r.W.Project.Id, new ReindexProjectFilesRequest(MaxFiles: 10), CancellationToken.None)).EligibleFiles.Should().BeGreaterThanOrEqualTo(3);
    }

    // ---- fakes ---------------------------------------------------------------------------------

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
            IReadOnlyList<VectorizedFileChunk> chunks, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteFileChunksAsync(Guid projectId, Guid fileId, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> DeleteProjectCollectionsAsync(Guid projectId, CancellationToken ct) => throw new NotSupportedException();
    }
}
