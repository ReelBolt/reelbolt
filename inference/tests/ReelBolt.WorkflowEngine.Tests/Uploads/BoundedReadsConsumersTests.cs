using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Agents;
using ReelBolt.Inference.Api.Consumers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

public class BoundedReadsConsumersTests
{
    private static InferenceApiDbContext NewContext(string name) =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(name).Options);

    private static async Task<(InferenceApiDbContext Db, Project Project, ProjectFile File, Mock<IFileStorageService> Storage)>
        SeedIndexerAsync(string dbName, long sizeBytes)
    {
        InferenceApiDbContext db = NewContext(dbName);
        var project = new Project { Id = Guid.NewGuid(), Name = "p", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() };
        var file = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "a.txt",
            StorageKey = "k",
            SizeBytes = sizeBytes,
            MimeType = "text/plain",
            Category = "userFiles"
        };
        db.Projects.Add(project);
        db.ProjectFiles.Add(file);
        await db.SaveChangesAsync();
        return (db, project, file, new Mock<IFileStorageService>());
    }

    private static ProjectFileIndexingConsumer BuildIndexer(
        InferenceApiDbContext db,
        IFileStorageService storage,
        IFileChunker chunker,
        int maxTextBytes) =>
        new(
            db,
            storage,
            chunker,
            Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(),
            Mock.Of<IVectorIndexService>(),
            NullLogger<ProjectFileIndexingConsumer>.Instance,
            options: Options.Create(new FileIndexingOptions { MaxTextBytes = maxTextBytes }));

    private static Mock<ConsumeContext<ProjectFileIndexingRequested>> UpsertContext(Guid projectId, Guid fileId)
    {
        Mock<ConsumeContext<ProjectFileIndexingRequested>> context = new();
        context.SetupGet(c => c.Message).Returns(new ProjectFileIndexingRequested
        {
            ProjectId = projectId,
            FileId = fileId,
            Operation = "Upsert"
        });
        context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
        return context;
    }

    private sealed class RecordingChunker : IFileChunker
    {
        public string? LastContent { get; private set; }

        public IReadOnlyList<FileChunk> Chunk(string content, string fileName)
        {
            LastContent = content;
            return [];
        }
    }

    private sealed class NeverCalledChunker : IFileChunker
    {
        public bool Called { get; private set; }

        public IReadOnlyList<FileChunk> Chunk(string content, string fileName)
        {
            Called = true;
            return [];
        }
    }

    [Fact]
    public async Task Indexer_skips_an_oversize_file_without_downloading()
    {
        string dbName = Guid.NewGuid().ToString();
        (InferenceApiDbContext db, Project project, ProjectFile file, Mock<IFileStorageService> storage) =
            await SeedIndexerAsync(dbName, sizeBytes: 5000);

        ProjectFileIndexingConsumer consumer = BuildIndexer(db, storage.Object, new RecordingChunker(), maxTextBytes: 1000);

        await consumer.Consume(UpsertContext(project.Id, file.Id).Object);

        storage.Verify(
            s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        ProjectFile reloaded = await db.ProjectFiles.AsNoTracking().FirstAsync(f => f.Id == file.Id);
        reloaded.IndexingStatus.Should().Be(FileIndexingStatus.Failed);
        reloaded.IndexingError.Should().Be("too_large");
    }

    [Fact]
    public async Task Indexer_treats_a_lying_size_as_too_large()
    {
        string dbName = Guid.NewGuid().ToString();
        var chunker = new NeverCalledChunker();
        (InferenceApiDbContext db, Project project, ProjectFile file, Mock<IFileStorageService> storage) =
            await SeedIndexerAsync(dbName, sizeBytes: 10);

        storage.Setup(s => s.DownloadAsync(project.Id, "k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 5000))));

        ProjectFileIndexingConsumer consumer = BuildIndexer(db, storage.Object, chunker, maxTextBytes: 1000);

        await consumer.Consume(UpsertContext(project.Id, file.Id).Object);

        ProjectFile reloaded = await db.ProjectFiles.AsNoTracking().FirstAsync(f => f.Id == file.Id);
        reloaded.IndexingStatus.Should().Be(FileIndexingStatus.Failed);
        reloaded.IndexingError.Should().Be("too_large");
        chunker.Called.Should().BeFalse();
    }

    private const long TenMiB = 10L * 1024 * 1024;

    private static async Task<ProjectFile> SeedKindAsync(
        InferenceApiDbContext db, ProjectFile file, string name, string mime, SummaryStatus summary = SummaryStatus.Pending)
    {
        file.OriginalFileName = name;
        file.MimeType = mime;
        file.SummaryStatus = summary;
        await db.SaveChangesAsync();
        return file;
    }

    [Fact]
    public async Task Indexer_marks_an_oversize_video_not_indexed_instead_of_failed()
    {
        (InferenceApiDbContext db, Project project, ProjectFile file, Mock<IFileStorageService> storage) =
            await SeedIndexerAsync(Guid.NewGuid().ToString(), sizeBytes: TenMiB);
        await SeedKindAsync(db, file, "clip.mp4", "video/mp4");
        storage.Setup(s => s.DownloadAsync(project.Id, "k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x6D, 0x70, 0x34, 0x32]));

        await BuildIndexer(db, storage.Object, new NeverCalledChunker(), maxTextBytes: 4 * 1024 * 1024)
            .Consume(UpsertContext(project.Id, file.Id).Object);

        ProjectFile reloaded = await db.ProjectFiles.AsNoTracking().FirstAsync(f => f.Id == file.Id);
        reloaded.IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
        reloaded.IndexingError.Should().BeNull();
    }

    [Fact]
    public async Task Indexer_sends_an_oversize_png_to_the_vision_branch()
    {
        (InferenceApiDbContext db, Project project, ProjectFile file, Mock<IFileStorageService> storage) =
            await SeedIndexerAsync(Guid.NewGuid().ToString(), sizeBytes: TenMiB);
        await SeedKindAsync(db, file, "shot.png", "image/png", SummaryStatus.Pending);
        storage.Setup(s => s.DownloadAsync(project.Id, "k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]));

        await BuildIndexer(db, storage.Object, new NeverCalledChunker(), maxTextBytes: 4 * 1024 * 1024)
            .Consume(UpsertContext(project.Id, file.Id).Object);

        ProjectFile reloaded = await db.ProjectFiles.AsNoTracking().FirstAsync(f => f.Id == file.Id);
        reloaded.IndexingStatus.Should().Be(FileIndexingStatus.Pending);
        reloaded.IndexingError.Should().BeNull();
    }

    [Fact]
    public async Task Indexer_fails_an_oversize_text_file_with_a_non_text_mime_as_too_large()
    {
        (InferenceApiDbContext db, Project project, ProjectFile file, Mock<IFileStorageService> storage) =
            await SeedIndexerAsync(Guid.NewGuid().ToString(), sizeBytes: TenMiB);
        await SeedKindAsync(db, file, "data.json", "application/json");
        storage.Setup(s => s.DownloadAsync(project.Id, "k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("{\"a\":1}")));
        var chunker = new NeverCalledChunker();

        await BuildIndexer(db, storage.Object, chunker, maxTextBytes: 4 * 1024 * 1024)
            .Consume(UpsertContext(project.Id, file.Id).Object);

        ProjectFile reloaded = await db.ProjectFiles.AsNoTracking().FirstAsync(f => f.Id == file.Id);
        reloaded.IndexingStatus.Should().Be(FileIndexingStatus.Failed);
        reloaded.IndexingError.Should().Be("too_large");
        chunker.Called.Should().BeFalse();
    }

    [Fact]
    public async Task Indexer_indexes_a_small_file_as_before()
    {
        string dbName = Guid.NewGuid().ToString();
        var chunker = new RecordingChunker();
        (InferenceApiDbContext db, Project project, ProjectFile file, Mock<IFileStorageService> storage) =
            await SeedIndexerAsync(dbName, sizeBytes: 12);

        storage.Setup(s => s.DownloadAsync(project.Id, "k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("twelve chars")));

        ProjectFileIndexingConsumer consumer = BuildIndexer(db, storage.Object, chunker, maxTextBytes: 1000);

        await consumer.Consume(UpsertContext(project.Id, file.Id).Object);

        chunker.LastContent.Should().Be("twelve chars");
        ProjectFile reloaded = await db.ProjectFiles.AsNoTracking().FirstAsync(f => f.Id == file.Id);
        reloaded.IndexingStatus.Should().Be(FileIndexingStatus.Indexed);
        reloaded.IndexingError.Should().BeNull();
    }

    private sealed class CapturingSummarizerAgent : IReelBoltAgent
    {
        public string? Prompt { get; private set; }

        public string Name => "fake-summarizer";
        public string Description => "fake";
        public string SystemPrompt => string.Empty;
        public AgentType AgentType => AgentType.FileSummarizerAgent;
        public IReadOnlyList<AIFunction> Tools => [];
        public string? OutputSchemaJson => null;
        public Type? OutputSchemaType => null;

        public Task<AgentRunResult> RunAsync(string prompt, Guid? agentDefinitionId = null, CancellationToken ct = default)
        {
            Prompt = prompt;
            return Task.FromResult(new AgentRunResult { Output = "summary" });
        }
    }

    [Fact]
    public async Task Summarizer_passes_at_most_cap_characters_and_marks_truncation()
    {
        string dbName = Guid.NewGuid().ToString();

        using (InferenceApiDbContext seed = NewContext(dbName))
        {
            var project = new Project { Id = Guid.NewGuid(), Name = "p", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() };
            var file = new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                OriginalFileName = "big.txt",
                StorageKey = "k",
                SizeBytes = 5000,
                MimeType = "text/plain",
                Category = "userFiles",
                SummaryStatus = SummaryStatus.Pending
            };
            seed.Projects.Add(project);
            seed.ProjectFiles.Add(file);
            await seed.SaveChangesAsync();

            var agent = new CapturingSummarizerAgent();
            Mock<IAgentRegistry> registry = new();
            registry.Setup(r => r.GetByType(AgentType.FileSummarizerAgent)).Returns(agent);

            Mock<IFileStorageService> storage = new();
            storage.Setup(s => s.DownloadAsync(project.Id, "k", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 5000))));

            var services = new ServiceCollection();
            services.AddScoped(_ => NewContext(dbName));
            services.AddScoped(_ => registry.Object);
            services.AddScoped(_ => storage.Object);
            ServiceProvider provider = services.BuildServiceProvider();

            var service = new FileSummarizationService(
                new ChannelBackgroundTaskQueue<FileSummarizationTask>(10),
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<FileSummarizationService>.Instance,
                Options.Create(new FileSummarizationOptions { MaxTextBytes = 1000 }));

            await service.ProcessAsync(new FileSummarizationTask(file.Id), CancellationToken.None);

            agent.Prompt.Should().NotBeNull();
            agent.Prompt!.Should().Contain("[truncated]");
            agent.Prompt!.Should().EndWith("[END FILE CONTENT]");
            Regex.Matches(agent.Prompt, "x+").Max(m => m.Length).Should().BeLessThanOrEqualTo(1000);
            Regex.Matches(agent.Prompt, "x+").Max(m => m.Length).Should().Be(1000);

            using InferenceApiDbContext check = NewContext(dbName);
            ProjectFile reloaded = await check.ProjectFiles.AsNoTracking().FirstAsync(f => f.Id == file.Id);
            reloaded.SummaryStatus.Should().Be(SummaryStatus.Done);
            reloaded.AgentSummary.Should().Be("summary");
        }
    }
}
