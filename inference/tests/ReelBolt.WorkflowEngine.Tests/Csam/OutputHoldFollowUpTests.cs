using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Agents;
using ReelBolt.Inference.Api.Consumers;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Csam;

/// <summary>
/// Model calls, the batch check on a failed read and the bulk reindex count, for a project whose earlier outputs are
/// not served. Each case pairs a file that must be left out with one that must still be handled.
/// </summary>
public class OutputHoldFollowUpTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private static (Project Project, ProjectFile NotServed, ProjectFile Upload) Seed(InferenceApiDbContext db, string mime = "text/plain", string ext = "txt")
    {
        Project project = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = Guid.NewGuid(), OrganizationId = Org, CsamOutputHoldAt = DateTime.UtcNow.AddHours(-1) };
        db.Projects.Add(project);
        ProjectFile notServed = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = $"made.{ext}",
            Category = "outputFiles",
            StorageKey = $"projects/{project.Id}/outputFiles/made.{ext}",
            MimeType = mime,
            SizeBytes = 4,
            SummaryStatus = SummaryStatus.Pending,
            IndexingStatus = FileIndexingStatus.Pending,
            OutputProducedAt = DateTime.UtcNow.AddHours(-3),
            UploadedAt = DateTime.UtcNow.AddHours(-3)
        };
        ProjectFile upload = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = $"mine.{ext}",
            Category = "userFiles",
            StorageKey = $"projects/{project.Id}/userFiles/mine.{ext}",
            MimeType = mime,
            SizeBytes = 4,
            SummaryStatus = SummaryStatus.Pending,
            IndexingStatus = FileIndexingStatus.Pending,
            UploadedAt = DateTime.UtcNow.AddHours(-3)
        };
        db.ProjectFiles.AddRange(notServed, upload);
        db.SaveChanges();
        return (project, notServed, upload);
    }

    // ---- summarizer and indexing consumer -----------------------------------------------------

    private static async Task<(CsamTestStorage Storage, InferenceApiDbContext Check)> SummarizeAsync(Func<Project, ProjectFile, ProjectFile, ProjectFile> pick)
    {
        string database = $"hold-summ-{Guid.NewGuid():N}";
        CsamTestStorage storage = new();
        ProjectFile target;
        using (InferenceApiDbContext seed = CsamTestSupport.NewDb(database))
        {
            (Project project, ProjectFile notServed, ProjectFile upload) = Seed(seed);
            storage.Put(notServed.StorageKey, [1, 2, 3, 4]);
            storage.Put(upload.StorageKey, [1, 2, 3, 4]);
            target = pick(project, notServed, upload);
        }

        ServiceCollection services = new();
        services.AddLogging();
        services.AddScoped(_ => CsamTestSupport.NewDb(database, new CsamFixedTenant(Org)));
        services.AddSingleton<IFileStorageService>(storage);
        services.AddSingleton(Mock.Of<IAgentRegistry>());
        await using ServiceProvider provider = services.BuildServiceProvider();
        FileSummarizationService service = new(
            Mock.Of<IBackgroundTaskQueue<FileSummarizationTask>>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FileSummarizationService>.Instance);

        await service.ProcessAsync(new FileSummarizationTask(target.Id), CancellationToken.None);

        InferenceApiDbContext check = CsamTestSupport.NewDb(database, new CsamFixedTenant(Org));
        return (storage, check);
    }

    [Fact]
    public async Task Summarizer_skips_a_file_that_is_not_served_without_reading_it()
    {
        (CsamTestStorage storage, InferenceApiDbContext check) = await SummarizeAsync((_, held, _) => held);
        using (check)
        {
            storage.Downloads.Should().Be(0, "a file that is not served is never read for a model");
            ProjectFile row = check.ProjectFiles.Single(f => f.OriginalFileName == "made.txt");
            row.SummaryStatus.Should().Be(SummaryStatus.Done);
            row.IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
        }
    }

    [Fact]
    public async Task Summarizer_still_reads_an_upload_in_a_project_whose_earlier_outputs_are_not_served()
    {
        (CsamTestStorage storage, InferenceApiDbContext check) = await SummarizeAsync((_, _, upload) => upload);
        using (check)
            storage.Downloads.Should().Be(1);
    }

    private static async Task<int> IndexAsync(bool uploadInstead)
    {
        InferenceApiDbContext db = CsamTestSupport.NewDb(new CsamFixedTenant(Org));
        (Project project, ProjectFile notServed, ProjectFile upload) = Seed(db);
        CsamTestStorage storage = new();
        storage.Put(notServed.StorageKey, [1, 2, 3, 4]);
        storage.Put(upload.StorageKey, [1, 2, 3, 4]);
        Mock<IVectorIndexService> vectors = new();
        vectors.Setup(v => v.DeleteFileChunksAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        ProjectFileIndexingConsumer consumer = new(
            db,
            storage,
            Mock.Of<IFileChunker>(),
            Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(),
            vectors.Object,
            NullLogger<ProjectFileIndexingConsumer>.Instance,
            tenant: new CsamFixedTenant(Org));
        Mock<ConsumeContext<ProjectFileIndexingRequested>> context = new();
        context.SetupGet(c => c.Message).Returns(new ProjectFileIndexingRequested
        {
            ProjectId = project.Id,
            FileId = uploadInstead ? upload.Id : notServed.Id,
            Operation = "Upsert",
            RequestedAt = DateTime.UtcNow
        });
        context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);

        try
        {
            await consumer.Consume(context.Object);
        }
        catch (Exception) when (uploadInstead)
        {
            // The chunker is a stub; only the read matters here.
        }

        return storage.Downloads;
    }

    [Fact]
    public async Task Indexing_consumer_does_not_read_a_file_that_is_not_served()
    {
        (await IndexAsync(uploadInstead: false)).Should().Be(0);
    }

    [Fact]
    public async Task Indexing_consumer_still_reads_an_upload_in_a_project_whose_earlier_outputs_are_not_served()
    {
        (await IndexAsync(uploadInstead: true)).Should().BeGreaterThan(0);
    }

    // ---- the batch check on a failed read ------------------------------------------------------

    /// <summary>A context whose project read fails, as a failed marker read does; every other read works.</summary>
    private sealed class ProjectReadFailsDb(DbContextOptions<InferenceApiDbContext> options) : InferenceApiDbContext(options)
    {
        public override DbSet<TEntity> Set<TEntity>() =>
            typeof(TEntity) == typeof(Project) ? throw new InvalidOperationException("read failed") : base.Set<TEntity>();
    }

    [Fact]
    public async Task Batch_check_on_a_failed_read_holds_a_picture_export_as_the_single_file_check_does()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        Guid projectId = Guid.NewGuid();
        ProjectFile export;
        ProjectFile plain;
        using (InferenceApiDbContext seed = new(options))
        {
            seed.Projects.Add(new Project { Id = projectId, Name = "p", CsamOutputHoldAt = DateTime.UtcNow.AddHours(-1) });
            export = new ProjectFile { Id = Guid.NewGuid(), ProjectId = projectId, StorageKey = $"projects/{projectId}/userFiles/pic.png", UploadedAt = DateTime.UtcNow };
            plain = new ProjectFile { Id = Guid.NewGuid(), ProjectId = projectId, StorageKey = $"projects/{projectId}/userFiles/note.png", UploadedAt = DateTime.UtcNow };
            EditImageDocument doc = new() { Id = Guid.NewGuid(), ProjectId = projectId };
            seed.ProjectFiles.AddRange(export, plain);
            seed.EditImageDocuments.Add(doc);
            seed.EditImageExports.Add(new EditImageExport { Id = Guid.NewGuid(), DocumentId = doc.Id, ProjectFileId = export.Id, CreatedAt = DateTime.UtcNow });
            seed.SaveChanges();
        }

        using ProjectReadFailsDb failing = new(options);
        HashSet<Guid> held = await OutputGate.HeldFileIdsAsync(failing, projectId, [export, plain], CancellationToken.None);

        held.Should().Contain(export.Id);
        held.Should().NotContain(plain.Id, "an upload that is no export of a picture is not platform-made");
    }

    // ---- bulk reindex count --------------------------------------------------------------------

    [Fact]
    public async Task Bulk_reindex_counts_and_queues_only_files_that_are_served()
    {
        InferenceApiDbContext db = CsamTestSupport.NewDb(new CsamFixedTenant(Org));
        (Project project, ProjectFile notServed, ProjectFile upload) = Seed(db);
        notServed.IndexingStatus = FileIndexingStatus.NotIndexed;
        upload.IndexingStatus = FileIndexingStatus.NotIndexed;
        db.SaveChanges();
        Mock<IProjectAccessService> access = new();
        access.Setup(a => a.GetAsync(project.Id, It.IsAny<ProjectPermission>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectAccessResult(ProjectAccessOutcome.Found, project));
        Mock<ICurrentUser> user = new();
        user.SetupGet(u => u.UserId).Returns(Guid.NewGuid());
        Mock<IPublishEndpoint> publish = new();
        ProjectFilesController controller = new(
            db, user.Object, access.Object, Mock.Of<IFileStorageService>(),
            Mock.Of<IBackgroundTaskQueue<FileSummarizationTask>>(), publish.Object, null!);

        ActionResult<ReindexProjectFilesResponse> result = await controller.ReindexProjectFiles(project.Id, new ReindexProjectFilesRequest(MaxFiles: 1), CancellationToken.None);

        ReindexProjectFilesResponse body = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<ReindexProjectFilesResponse>().Subject;
        body.EligibleFiles.Should().Be(1, "the file that is not served is not eligible");
        body.QueuedFiles.Should().Be(1, "it does not take the only slot either");
        body.HasMore.Should().BeFalse();
        publish.Verify(p => p.Publish(It.Is<ProjectFileIndexingRequested>(m => m.FileId == upload.Id), It.IsAny<CancellationToken>()), Times.Once);
    }
}
