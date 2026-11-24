#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using FolderDeleteRequest = ReelBolt.Inference.Api.Controllers.Dto.FolderDeleteRequest;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Tests.Uploads;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// A reference file is held by its avatar with <c>ON DELETE NO ACTION</c> (docs/design/avatars.md), so every path that
/// deletes or rewrites a project file decides what to do with one: user-facing deletes and replacements answer
/// <c>409 FILE_IN_USE_BY_AVATAR</c> naming the avatar; background paths keep the file. The background paths' own
/// tests live beside their harnesses (storage retention, sync, takes, voices, account purge, assistant tools).
/// </summary>
public class AvatarFileDeletePathTests
{
    private static ProjectFilesController Files(H h, InferenceApiDbContext db) =>
        new(db, h.Owner(), TestTenancy.Access(db, h.Owner()), h.Storage.Object,
            new ChannelBackgroundTaskQueue<FileSummarizationTask>(16), Mock.Of<IPublishEndpoint>(),
            new VectorSearchQueryService(Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(), Mock.Of<IVectorIndexService>()))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    [Fact]
    public async Task Deleting_a_reference_file_is_409_naming_the_avatar_and_deletes_nothing()
    {
        var h = new H();
        ProjectFile face = h.AddImage();
        using InferenceApiDbContext db = h.NewDb();
        ProjectAvatar avatar = AvatarSeed.Attach(db, h.Project.Id, "Anna", face);

        (int status, JsonElement body) = H.Read(await Files(h, db).Delete(h.Project.Id, face.Id, CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be(AvatarFileUse.InUseCode);
        body.GetProperty("avatarId").GetGuid().Should().Be(avatar.Id);
        body.GetProperty("avatarName").GetString().Should().Be("Anna");
        body.GetProperty("message").GetString().Should().Contain("\"Anna\"");
        (await db.ProjectFiles.AnyAsync(f => f.Id == face.Id)).Should().BeTrue();
        h.DeletedKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_a_folder_that_holds_a_reference_file_is_refused_whole()
    {
        var h = new H();
        ProjectFile face = h.AddImage(tweak: f => f.DirectoryPath = "people");
        ProjectFile other = h.AddImage("other.png", salt: 2, tweak: f => f.DirectoryPath = "people");
        using InferenceApiDbContext db = h.NewDb();
        AvatarSeed.Attach(db, h.Project.Id, "Anna", face);

        (int status, JsonElement body) = H.Read(await Files(h, db).DeleteFolder(h.Project.Id, new FolderDeleteRequest("people", Recursive: true), CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be(AvatarFileUse.InUseCode);
        (await db.ProjectFiles.CountAsync(f => f.Id == face.Id || f.Id == other.Id)).Should().Be(2);
    }

    [Fact]
    public async Task An_ordinary_file_still_deletes()
    {
        var h = new H();
        ProjectFile face = h.AddImage();
        ProjectFile loose = h.AddImage("loose.png", salt: 3);
        using InferenceApiDbContext db = h.NewDb();
        AvatarSeed.Attach(db, h.Project.Id, "Anna", face);

        H.Read(await Files(h, db).Delete(h.Project.Id, loose.Id, CancellationToken.None)).Status.Should().Be(204);
    }

    [Fact]
    public async Task An_upload_that_would_replace_a_reference_file_is_refused_at_creation_and_at_completion()
    {
        var u = new DirectUploadHarness();
        ProjectFile face = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = u.Project.Id,
            OriginalFileName = "face.png",
            MimeType = "image/png",
            StorageKey = $"projects/{u.Project.Id}/userFiles/face.png",
            StorageBucket = "b",
            SizeBytes = 4,
            Category = "userFiles"
        };
        u.Db.ProjectFiles.Add(face);
        u.Db.SaveChanges();

        // A ticket issued BEFORE the file became a reference...
        DirectUploadTicket early = await u.Create(u.Request(fileName: "face.png", contentType: "image/png", replacesFileId: face.Id));
        AvatarSeed.Attach(u.Db, u.Project.Id, "Anna", face);

        Func<Task> create = () => u.Create(u.Request(fileName: "face.png", contentType: "image/png", replacesFileId: face.Id));
        (await create.Should().ThrowAsync<DirectUploadRefusedException>())
            .Which.Should().Match<DirectUploadRefusedException>(e => e.Code == AvatarFileUse.InUseCode && e.StatusCode == 409);

        // ...cannot replace its bytes at completion either.
        Func<Task> complete = () => u.Service.CompleteAsync(early, new DirectUploadCompletion([]), CancellationToken.None);
        (await complete.Should().ThrowAsync<DirectUploadRefusedException>()).Which.Code.Should().Be(AvatarFileUse.InUseCode);
        (await u.Db.ProjectFiles.AsNoTracking().SingleAsync(f => f.Id == face.Id)).StorageKey.Should().Be(face.StorageKey);
    }

    [Fact]
    public async Task The_engine_reads_the_same_reference_set_so_a_superseded_render_an_avatar_lists_is_kept()
    {
        // VideoCompileStepExecutor.RetireSupersededRegistrationsAsync filters its superseded rows through this exact
        // call on the engine's read-only mapping of project_avatar_references.
        using var engine = new WorkflowEngineDbContext(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid projectId = Guid.NewGuid();
        ProjectFile render = new() { Id = Guid.NewGuid(), ProjectId = projectId, OriginalFileName = "r.mp4", StorageKey = "k1", MimeType = "video/mp4" };
        ProjectFile other = new() { Id = Guid.NewGuid(), ProjectId = projectId, OriginalFileName = "o.mp4", StorageKey = "k2", MimeType = "video/mp4" };
        engine.ProjectFiles.AddRange(render, other);
        engine.SaveChanges();
        AvatarSeed.Attach(engine, projectId, "Anna", render);

        (await AvatarFileUse.ReferencedFileIdsAsync(engine, [render.Id, other.Id], CancellationToken.None))
            .Should().Equal(render.Id);
        (await AvatarFileUse.FirstUseAsync(engine, Guid.NewGuid(), [render.Id], CancellationToken.None))
            .Should().BeNull("another project's avatar is never named");
    }
}
