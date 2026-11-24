using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Agents;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Consumers;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// A quarantined file's row survives — <c>List</c> reports it — but every byte path refuses it and
/// every derived-text path (assistant listing, summarizer, indexer) skips it.
/// </summary>
public class ProjectFilesModerationQuarantineTests : IDisposable
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly string _databaseName = $"moderation-quarantine-{Guid.NewGuid():N}";
    private readonly InferenceApiDbContext _db;
    private readonly Project _project;
    private readonly ProjectFile _quarantined;

    public ProjectFilesModerationQuarantineTests()
    {
        _db = NewContext();
        _project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Moderated",
            OwnerId = _userId,
            OrganizationId = TestTenancy.OrgOf(_userId)
        };
        _quarantined = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = _project.Id,
            OriginalFileName = "blocked.png",
            OriginalPath = "blocked.png",
            StorageKey = $"projects/{_project.Id}/quarantine/blocked.png",
            StorageFileName = "blocked.png",
            StorageBucket = "bucket",
            MimeType = "image/png",
            SizeBytes = 10,
            ModerationStatus = FileModerationStatus.Quarantined
        };
        ProjectFile clean = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = _project.Id,
            OriginalFileName = "allowed.png",
            OriginalPath = "allowed.png",
            StorageKey = $"projects/{_project.Id}/userFiles/allowed.png",
            StorageFileName = "allowed.png",
            StorageBucket = "bucket",
            MimeType = "image/png",
            SizeBytes = 10,
            ModerationStatus = FileModerationStatus.Clean
        };

        _db.Projects.Add(_project);
        _db.ProjectFiles.AddRange(_quarantined, clean);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private InferenceApiDbContext NewContext() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options);

    private sealed class FakeCurrentUser(Guid userId, Guid organizationId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsAdmin { get; } = false;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
    }

    private ProjectFilesController FilesController(Mock<IFileStorageService>? storage = null)
    {
        FakeCurrentUser user = new(_userId, _project.OrganizationId);
        return new ProjectFilesController(
            _db,
            user,
            TestTenancy.Access(_db, user),
            (storage ?? new Mock<IFileStorageService>()).Object,
            Mock.Of<IBackgroundTaskQueue<FileSummarizationTask>>(),
            Mock.Of<IPublishEndpoint>(),
            null!,
            logger: NullLogger<ProjectFilesController>.Instance);
    }

    [Fact]
    public async Task Download_refuses_a_quarantined_file_with_404()
    {
        Mock<IFileStorageService> storage = new();

        IActionResult result = await FilesController(storage)
            .Download(_project.Id, _quarantined.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        storage.Verify(
            s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetContent_refuses_a_quarantined_file_with_404()
    {
        Mock<IFileStorageService> storage = new();

        ActionResult<ProjectFileContentResponse> result = await FilesController(storage)
            .GetContent(_project.Id, _quarantined.Id, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
        storage.Verify(
            s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateContent_refuses_a_quarantined_file_with_404()
    {
        ActionResult<ProjectFileResponse> result = await FilesController()
            .UpdateContent(
                _project.Id,
                _quarantined.Id,
                new UpdateProjectFileContentRequest("replacement"),
                CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task The_media_stream_refuses_a_quarantined_file_with_404()
    {
        FakeCurrentUser user = new(_userId, _project.OrganizationId);
        ProjectFileMediaController controller = new(
            _db,
            user,
            TestTenancy.Access(_db, user),
            Mock.Of<IAmazonS3>(),
            Mock.Of<IConfiguration>());

        IActionResult result = await controller.Stream(_project.Id, _quarantined.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task A_derivative_of_a_quarantined_file_is_404()
    {
        MediaDerivative derivative = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = _project.Id,
            SourceKind = MediaSourceKind.ProjectFile,
            SourceProjectFileId = _quarantined.Id,
            Kind = MediaDerivativeKind.Proxy,
            StorageKey = $"projects/{_project.Id}/userFiles/proxy.mp4",
            StorageBucket = "bucket",
            MimeType = "video/mp4"
        };
        _db.MediaDerivatives.Add(derivative);
        await _db.SaveChangesAsync();

        FakeCurrentUser user = new(_userId, _project.OrganizationId);
        MediaController controller = new(
            _db,
            TestTenancy.Access(_db, user),
            Mock.Of<IFileStorageService>(),
            Mock.Of<IAmazonS3>(),
            Mock.Of<IPublishEndpoint>(),
            NullLogger<MediaController>.Instance);

        IActionResult result = await controller.GetDerivativeContent(
            _project.Id, derivative.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task List_keeps_the_row_and_reports_its_moderation_status()
    {
        ActionResult<List<ProjectFileResponse>> result =
            await FilesController().List(_project.Id, CancellationToken.None);

        List<ProjectFileResponse> files = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<List<ProjectFileResponse>>().Subject;

        files.Should().ContainSingle(f => f.Id == _quarantined.Id)
            .Which.ModerationStatus.Should().Be("Quarantined");
        files.Should().ContainSingle(f => f.OriginalFileName == "allowed.png")
            .Which.ModerationStatus.Should().Be("Clean");
    }

    [Fact]
    public void The_assistant_listing_hides_a_quarantined_file()
    {
        string json = AssistantFileListing.List(
            _project.Id,
            _db.ProjectFiles.Where(f => f.ProjectId == _project.Id),
            "Moderated",
            pathPrefix: null,
            mimePrefix: null,
            since: null,
            nameContains: null,
            limit: null,
            cursor: null);

        using JsonDocument document = JsonDocument.Parse(json);
        string[] names = document.RootElement.GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("originalFileName").GetString()!)
            .ToArray();

        names.Should().ContainSingle().Which.Should().Be("allowed.png");
    }

    [Fact]
    public async Task The_indexer_skips_a_quarantined_file()
    {
        Mock<IFileStorageService> storage = new();
        ProjectFileIndexingConsumer consumer = new(
            _db,
            storage.Object,
            Mock.Of<IFileChunker>(),
            Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(),
            Mock.Of<IVectorIndexService>(),
            NullLogger<ProjectFileIndexingConsumer>.Instance,
            options: Microsoft.Extensions.Options.Options.Create(new FileIndexingOptions()));

        Mock<ConsumeContext<ProjectFileIndexingRequested>> context = new();
        context.SetupGet(c => c.Message).Returns(new ProjectFileIndexingRequested
        {
            ProjectId = _project.Id,
            FileId = _quarantined.Id,
            Operation = "Upsert"
        });
        context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);

        await consumer.Consume(context.Object);

        using InferenceApiDbContext check = NewContext();
        ProjectFile saved = check.ProjectFiles.Single(f => f.Id == _quarantined.Id);
        saved.IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
        saved.IndexingError.Should().Be("Quarantined");
        saved.SummaryStatus.Should().Be(SummaryStatus.Done);
        storage.Verify(
            s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task The_summarizer_skips_a_quarantined_file()
    {
        Mock<IFileStorageService> storage = new();
        ServiceCollection services = new();
        services.AddScoped(_ => NewContext());
        services.AddSingleton(Mock.Of<IAgentRegistry>());
        services.AddSingleton(storage.Object);
        FileSummarizationService summarizer = new(
            Mock.Of<IBackgroundTaskQueue<FileSummarizationTask>>(),
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FileSummarizationService>.Instance);

        await summarizer.ProcessAsync(new FileSummarizationTask(_quarantined.Id), CancellationToken.None);

        using InferenceApiDbContext check = NewContext();
        ProjectFile saved = check.ProjectFiles.Single(f => f.Id == _quarantined.Id);
        saved.SummaryStatus.Should().Be(SummaryStatus.Done);
        saved.IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
        saved.IndexingError.Should().Be("Quarantined");
        storage.Verify(
            s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FindSource_does_not_return_a_quarantined_file()
    {
        OutputDescriber describer = new(
            _db, Mock.Of<IFileStorageService>(), Mock.Of<IOutputLinkService>(), Mock.Of<IPublishEndpoint>());

        (await describer.FindSourceAsync(_project.Id, _quarantined.Id, CancellationToken.None))
            .Should().BeNull("a quarantined file is not a source for presigning, sampling or derivatives");
    }

    [Fact]
    public async Task Describe_refuses_a_quarantined_file_and_mints_no_link()
    {
        OutputDescriber describer = new(
            _db, Mock.Of<IFileStorageService>(), Mock.Of<IOutputLinkService>(), Mock.Of<IPublishEndpoint>());

        (await describer.DescribeAsync(_project, _quarantined.Id, _userId, CancellationToken.None))
            .Should().BeNull("a quarantined file must never mint an output link");
    }

    [Theory]
    [InlineData("Block", 404)]
    [InlineData(null, 409)]
    public async Task A_derivative_of_a_blocked_step_result_is_404_and_of_a_pending_one_is_409(string? verdict, int status)
    {
        Guid definitionId = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();
        Guid stepResultId = Guid.NewGuid();
        Guid derivativeId = Guid.NewGuid();
        _db.WorkflowDefinitions.Add(new WorkflowDefinition
        {
            Id = definitionId,
            Name = "wf",
            ProjectId = _project.Id
        });
        _db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowDefinitionId = definitionId,
            ProjectId = _project.Id,
            OrganizationId = _project.OrganizationId,
            Status = ExecutionStatus.Passed
        });
        _db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = stepResultId,
            WorkflowExecutionId = executionId,
            WorkflowStepId = Guid.NewGuid(),
            Status = StepStatus.Completed,
            OutputStorageKey = $"projects/{_project.Id}/outputFiles/render.mp4"
        });
        _db.MediaDerivatives.Add(new MediaDerivative
        {
            Id = derivativeId,
            ProjectId = _project.Id,
            SourceKind = MediaSourceKind.StepResult,
            SourceStepResultId = stepResultId,
            Kind = MediaDerivativeKind.Proxy,
            StorageKey = $"projects/{_project.Id}/agentFiles/media-derivatives/x/proxy.mp4",
            StorageBucket = "bucket",
            MimeType = "video/mp4"
        });
        if (verdict is not null)
        {
            _db.OutputModerationVerdicts.Add(new OutputModerationVerdict
            {
                Id = Guid.NewGuid(),
                OrganizationId = _project.OrganizationId,
                ProjectId = _project.Id,
                StorageKey = $"projects/{_project.Id}/outputFiles/render.mp4",
                SourceVersion = "e1:1",
                State = verdict,
                ReasonMessage = "no",
                ModelSnapshot = "m"
            });
        }

        await _db.SaveChangesAsync();

        FakeCurrentUser user = new(_userId, _project.OrganizationId);
        MediaController controller = new(
            _db,
            TestTenancy.Access(_db, user),
            StreamingStorage(),
            Mock.Of<IAmazonS3>(),
            Mock.Of<IPublishEndpoint>(),
            NullLogger<MediaController>.Instance,
            BlockingPublish());

        IActionResult result = await controller.GetDerivativeContent(
            _project.Id, derivativeId, CancellationToken.None);

        if (status == 404)
        {
            result.Should().BeOfType<NotFoundResult>("a blocked source stays hidden");
        }
        else
        {
            result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409, "pending answers the code download answers");
            ((ObjectResult)result).Value!.ToString().Should().Contain("CONTENT_MODERATION_PENDING");
        }
    }

    private static IFileStorageService StreamingStorage()
    {
        var storage = new Mock<IFileStorageService>();
        storage
            .Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1]));
        storage
            .Setup(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectMetadata(1, "video/mp4", "\"e1\""));
        return storage.Object;
    }

    private PublishModeration BlockingPublish()
    {
        var providers = new Mock<IInferenceProviderResolver>();
        providers
            .Setup(p => p.ResolvePlatformTranscriptionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedTranscriptionProvider?)null);

        return new PublishModeration(
            new BlockingGate(),
            StreamingStorage(),
            new ImageSampler(),
            providers.Object,
            Mock.Of<ITranscriptionClientFactory>(),
            _db,
            Microsoft.Extensions.Options.Options.Create(new ContentModerationOptions { Enabled = true }),
            NullLogger<PublishModeration>.Instance);
    }

    private sealed class BlockingGate : IContentModerationGate
    {
        public Task<ModerationDecision> ModerateAsync(
            ModerationInput input, ModerationAttribution attribution, CancellationToken ct) =>
            Task.FromResult(new ModerationDecision(
                ModerationAction.Block, Available: true, ReviewQueued: false, "sexual",
                "This video may contain sexual content.", "hash"));

        public Task<bool> ScreenGenerationPromptAsync(
            string text, IReadOnlyList<ModerationImageInput> images, ModerationAttribution attribution, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<bool> ScreenGeneratedTextAsync(
            string text, string site, ModerationAttribution attribution, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<bool> ScreenPublishAsync(
            ModerationInput input, ModerationAttribution attribution, CancellationToken ct) =>
            Task.FromResult(false);
    }

    private sealed class ImageSampler : IModerationMediaSampler
    {
        public Task<ModerationMediaSample> SampleAsync(Stream media, string mimeType, CancellationToken ct) =>
            Task.FromResult(new ModerationMediaSample(
                [new byte[] { 9, 9 }], null, ModerationContentKind.Image, null));
    }
}
