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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.InferenceApiReliability;

/// <summary>
/// Finding (critical): uploads blocked on LLM summarization — the bounded queue was AWAITED inside
/// the upload request, so a 233-file repository took ~15 minutes and a 2-byte file 28 seconds. Uploads
/// now persist Pending and offer the file without waiting; a sweeper drains what did not fit. Also
/// the C2 single-file rename contract.
/// </summary>
public class ProjectFilesReliabilityTests
{
    private sealed class Harness
    {
        public string DbName { get; } = Guid.NewGuid().ToString();
        public InferenceApiDbContext Db { get; }
        public Guid OwnerId { get; } = Guid.NewGuid();
        public Project Project { get; }
        public Mock<IFileStorageService> Storage { get; } = new();
        public Mock<IPublishEndpoint> Publish { get; } = new();
        public ChannelBackgroundTaskQueue<FileSummarizationTask> Queue { get; }

        public Harness(int queueCapacity = 100)
        {
            Db = NewContext();
            Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = OwnerId, OrganizationId = TestTenancy.OrgOf(OwnerId) };
            Db.Projects.Add(Project);
            Db.SaveChanges();
            Queue = new ChannelBackgroundTaskQueue<FileSummarizationTask>(queueCapacity);

            Storage.Setup(s => s.UploadAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid projectId, Guid fileId, Stream _, string name, string _, IReadOnlyDictionary<string, string>? _, CancellationToken _, string category, string? path) =>
                    new StoredFileObject(
                        $"projects/{projectId}/{category}/{fileId:N}_{name}", "bucket", $"projects/{projectId}/{category}", null,
                        category, name, path ?? name, null, $"{fileId:N}_{name}"));
        }

        public InferenceApiDbContext NewContext() =>
            new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(DbName).Options);

        public ProjectFilesController Controller(Guid? asUser = null)
        {
            var controller = new ProjectFilesController(
                Db, new User(asUser ?? OwnerId), TestTenancy.Access(Db, new User(asUser ?? OwnerId)), Storage.Object, Queue, Publish.Object,
                new VectorSearchQueryService(Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(), Mock.Of<IVectorIndexService>()));
            controller.ControllerContext.HttpContext = new DefaultHttpContext();
            return controller;
        }

        public FileSummarizationService Summarizer(FileSummarizationOptions? options = null)
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => NewContext());
            return new FileSummarizationService(
                Queue,
                services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                NullLogger<FileSummarizationService>.Instance,
                Options.Create(options ?? new FileSummarizationOptions()));
        }

        public ProjectFile AddFile(
            string name,
            SummaryStatus status = SummaryStatus.Pending,
            string category = "userFiles",
            string mime = "text/plain",
            string? directory = null,
            DateTime? uploadedAt = null,
            CsamScanStatus csam = CsamScanStatus.Clean)
        {
            var file = new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = Project.Id,
                OriginalFileName = name,
                OriginalPath = directory is null ? name : $"{directory}/{name}",
                DirectoryPath = directory,
                Category = category,
                MimeType = mime,
                StorageKey = $"projects/{Project.Id}/{category}/{Guid.NewGuid():N}_{name}",
                StorageFileName = name,
                SizeBytes = 2,
                SummaryStatus = status,
                UploadedAt = uploadedAt ?? DateTime.UtcNow,
                CsamStatus = csam
            };
            Db.ProjectFiles.Add(file);
            Db.SaveChanges();
            return file;
        }
    }

    private sealed class User(Guid id) : ICurrentUser
    {
        public Guid UserId { get; } = id;
        public string Email => "owner@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public System.Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    private static IFormFile TextFile(string name, string content = "hi")
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/plain"
        };
    }

    // ---------------------------------------------------------------- uploads never wait

    [Fact]
    public async Task An_upload_returns_immediately_when_the_summarization_queue_is_full()
    {
        var h = new Harness(queueCapacity: 1);
        h.Queue.TryQueue(new FileSummarizationTask(Guid.NewGuid())).Should().BeTrue("the filler occupies the only slot");
        h.Queue.FreeCapacity.Should().Be(0);

        ProjectFilesController controller = h.Controller();
        controller.ControllerContext.HttpContext.Request.ContentType = "multipart/form-data; boundary=x";
        controller.ControllerContext.HttpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>());

        Task<ActionResult<ProjectFileResponse>> upload = controller.Upload(h.Project.Id, TextFile("tiny.txt"), CancellationToken.None);
        Task winner = await Task.WhenAny(upload, Task.Delay(TimeSpan.FromSeconds(10)));

        winner.Should().BeSameAs(upload, "an upload must never wait for the summarizer to free a slot");
        ActionResult<ProjectFileResponse> result = await upload;
        ((ObjectResult)result.Result!).StatusCode.Should().Be(201);

        ProjectFile stored = h.NewContext().ProjectFiles.Single(f => f.OriginalFileName == "tiny.txt");
        stored.SummaryStatus.Should().Be(SummaryStatus.Pending, "the database row, not the queue, is what remembers the work");
        h.Queue.IsTracked(new FileSummarizationTask(stored.Id)).Should().BeFalse("the full queue refused it, so the sweeper owns it now");
    }

    [Fact]
    public async Task The_sweeper_leaves_a_row_alone_until_its_csam_verdict_has_settled()
    {
        // Cloud and GitHub sync, assistant copies: a row starts Pending and no model may see it before the scan.
        var h = new Harness(queueCapacity: 10);
        ProjectFile pending = h.AddFile("p.txt", csam: CsamScanStatus.Pending);
        ProjectFile scanning = h.AddFile("s.txt", csam: CsamScanStatus.Scanning);
        ProjectFile held = h.AddFile("q.txt", csam: CsamScanStatus.Quarantined);
        ProjectFile clean = h.AddFile("c.txt", csam: CsamScanStatus.Clean);
        ProjectFile unanswered = h.AddFile("n.txt", csam: CsamScanStatus.NotScanned);
        FileSummarizationService service = h.Summarizer();

        (await service.SweepOnceAsync(CancellationToken.None)).Should().Be(2);

        h.Queue.IsTracked(new FileSummarizationTask(clean.Id)).Should().BeTrue();
        h.Queue.IsTracked(new FileSummarizationTask(unanswered.Id)).Should().BeTrue();
        h.Queue.IsTracked(new FileSummarizationTask(pending.Id)).Should().BeFalse();
        h.Queue.IsTracked(new FileSummarizationTask(scanning.Id)).Should().BeFalse();
        h.Queue.IsTracked(new FileSummarizationTask(held.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task The_sweeper_queues_pending_rows_once_room_frees_up_and_never_twice()
    {
        var h = new Harness(queueCapacity: 2);
        ProjectFile a = h.AddFile("a.txt", uploadedAt: DateTime.UtcNow.AddMinutes(-2));
        ProjectFile b = h.AddFile("b.txt", uploadedAt: DateTime.UtcNow.AddMinutes(-1));
        ProjectFile c = h.AddFile("c.txt");
        FileSummarizationService service = h.Summarizer();

        (await service.SweepOnceAsync(CancellationToken.None)).Should().Be(2, "only two fit; oldest first");
        h.Queue.IsTracked(new FileSummarizationTask(a.Id)).Should().BeTrue();
        h.Queue.IsTracked(new FileSummarizationTask(b.Id)).Should().BeTrue();
        h.Queue.IsTracked(new FileSummarizationTask(c.Id)).Should().BeFalse();

        (await service.SweepOnceAsync(CancellationToken.None)).Should().Be(0, "a full queue and already-queued rows queue nothing");

        // The worker takes one and is still processing it: the slot is free, but the id is in flight.
        FileSummarizationTask taken = await h.Queue.DequeueAsync(CancellationToken.None);
        taken.FileId.Should().Be(a.Id);

        (await service.SweepOnceAsync(CancellationToken.None)).Should().Be(1, "only c is new; a is in flight and b is queued");
        h.Queue.IsTracked(new FileSummarizationTask(c.Id)).Should().BeTrue();

        h.Queue.Complete(taken);
        h.Queue.IsTracked(new FileSummarizationTask(a.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task The_sweeper_skips_media_and_engine_files_and_resets_rows_stuck_in_processing()
    {
        var h = new Harness();
        ProjectFile video = h.AddFile("clip.mp4", mime: "video/mp4");
        ProjectFile engine = h.AddFile("plan.json", category: "agentFiles", mime: "application/json");
        ProjectFile stuck = h.AddFile("stuck.txt", status: SummaryStatus.Processing, uploadedAt: DateTime.UtcNow.AddHours(-2));
        ProjectFile working = h.AddFile("working.txt", status: SummaryStatus.Processing, uploadedAt: DateTime.UtcNow);

        int queued = await h.Summarizer(new FileSummarizationOptions { StuckProcessingMinutes = 15 }).SweepOnceAsync(CancellationToken.None);

        queued.Should().Be(1);
        h.Queue.IsTracked(new FileSummarizationTask(stuck.Id)).Should().BeTrue();
        h.Queue.IsTracked(new FileSummarizationTask(video.Id)).Should().BeFalse("R22: binaries never reach the text summarizer");
        h.Queue.IsTracked(new FileSummarizationTask(engine.Id)).Should().BeFalse("engine-written files were never summarized");
        h.Queue.IsTracked(new FileSummarizationTask(working.Id)).Should().BeFalse("recently started work is not stuck");

        using InferenceApiDbContext fresh = h.NewContext();
        fresh.ProjectFiles.Single(f => f.Id == stuck.Id).SummaryStatus.Should().Be(SummaryStatus.Pending);
        fresh.ProjectFiles.Single(f => f.Id == working.Id).SummaryStatus.Should().Be(SummaryStatus.Processing);
    }

    [Fact]
    public void The_queue_refuses_a_duplicate_and_accepts_it_again_once_completed()
    {
        var queue = new ChannelBackgroundTaskQueue<FileSummarizationTask>(10);
        var task = new FileSummarizationTask(Guid.NewGuid());

        queue.TryQueue(task).Should().BeTrue();
        queue.TryQueue(task).Should().BeFalse();
        queue.Complete(task);
        queue.TryQueue(task).Should().BeTrue();
    }

    // ---------------------------------------------------------------- C2 rename

    [Fact]
    public async Task Rename_changes_the_display_name_and_keeps_the_storage_key()
    {
        var h = new Harness();
        ProjectFile file = h.AddFile("clp_07.mp4", mime: "video/mp4", directory: "broll");
        string key = file.StorageKey;

        ActionResult<ProjectFileResponse> result = await h.Controller()
            .Rename(h.Project.Id, file.Id, new RenameProjectFileRequest("  sunset over the bay.mp4 "), CancellationToken.None);

        var body = (ProjectFileResponse)((OkObjectResult)result.Result!).Value!;
        body.OriginalFileName.Should().Be("sunset over the bay.mp4");
        body.OriginalPath.Should().Be("broll/sunset over the bay.mp4");
        body.DirectoryPath.Should().Be("broll");
        body.StorageKey.Should().Be(key, "a rename never moves the object, so every reference to it keeps working");
        h.Storage.Verify(s => s.MoveAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Rename_is_409_when_a_sibling_already_has_the_name_but_fine_in_another_folder()
    {
        var h = new Harness();
        h.AddFile("Intro.mp4", mime: "video/mp4", directory: "broll");
        h.AddFile("outro.mp4", mime: "video/mp4", directory: "music");
        ProjectFile file = h.AddFile("clip.mp4", mime: "video/mp4", directory: "broll");

        (await h.Controller().Rename(h.Project.Id, file.Id, new RenameProjectFileRequest("intro.mp4"), CancellationToken.None))
            .Result.Should().BeOfType<ConflictObjectResult>("names are compared case-insensitively within a folder");

        (await h.Controller().Rename(h.Project.Id, file.Id, new RenameProjectFileRequest("outro.mp4"), CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>("the clash is per folder");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b.mp4")]
    [InlineData("a\\b.mp4")]
    [InlineData("..")]
    [InlineData("tab\there.mp4")]
    public async Task Rename_rejects_an_invalid_name(string name)
    {
        var h = new Harness();
        ProjectFile file = h.AddFile("clip.mp4", mime: "video/mp4");

        (await h.Controller().Rename(h.Project.Id, file.Id, new RenameProjectFileRequest(name), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Rename_rejects_a_name_longer_than_255_characters_and_scopes_to_the_owner()
    {
        var h = new Harness();
        ProjectFile file = h.AddFile("clip.mp4", mime: "video/mp4");

        (await h.Controller().Rename(h.Project.Id, file.Id, new RenameProjectFileRequest(new string('a', 256)), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Controller().Rename(h.Project.Id, file.Id, new RenameProjectFileRequest(new string('a', 255)), CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>();

        (await h.Controller(asUser: Guid.NewGuid()).Rename(h.Project.Id, file.Id, new RenameProjectFileRequest("x.mp4"), CancellationToken.None))
            .Result.Should().BeOfType<NotFoundResult>();
        (await h.Controller().Rename(h.Project.Id, Guid.NewGuid(), new RenameProjectFileRequest("x.mp4"), CancellationToken.None))
            .Result.Should().BeOfType<NotFoundResult>();
    }
}
