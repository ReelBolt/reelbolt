using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Agents;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Consumers;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Inference.Api.Services.FolderSync;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Agents;
using ReelBolt.WorkflowEngine.Tests.CloudStorage.Core;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Csam;

/// <summary>
/// Quarantined content must be unreachable through every sibling path that does not go through the
/// file-serving controllers: the summarizer, the indexer, folder/device deletes, cloud-sync
/// reconciliation and the assistant's file writer.
/// </summary>
public class CsamLeakPathTests
{
    // ------------------------------------------------------------------ summarizer / indexer

    [Fact]
    public async Task Summarizer_QuarantinedFile_NeverDownloadsOrCallsTheModel()
    {
        Guid org = Guid.NewGuid();
        string database = $"csam-summarizer-{Guid.NewGuid():N}";
        CsamTestStorage storage = new();
        Guid fileId = Guid.NewGuid();

        using (InferenceApiDbContext seed = CsamTestSupport.NewDb(database))
        {
            Project project = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = Guid.NewGuid(), OrganizationId = org };
            seed.Projects.Add(project);
            seed.ProjectFiles.Add(new ProjectFile
            {
                Id = fileId,
                ProjectId = project.Id,
                OriginalFileName = "bad.txt",
                OriginalPath = "bad.txt",
                Category = "userFiles",
                StorageKey = $"projects/{project.Id}/quarantine/{fileId}/bad.txt",
                MimeType = "text/plain",
                SizeBytes = 4,
                SummaryStatus = SummaryStatus.Pending,
                CsamStatus = CsamScanStatus.Quarantined,
                UploadedAt = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        ServiceCollection services = new();
        services.AddLogging();
        services.AddScoped(_ => CsamTestSupport.NewDb(database, new CsamFixedTenant(org)));
        services.AddSingleton<IFileStorageService>(storage);
        services.AddSingleton(Mock.Of<IAgentRegistry>());
        await using ServiceProvider provider = services.BuildServiceProvider();

        FileSummarizationService service = new(
            Mock.Of<IBackgroundTaskQueue<FileSummarizationTask>>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FileSummarizationService>.Instance);

        await service.ProcessAsync(new FileSummarizationTask(fileId), CancellationToken.None);

        storage.Downloads.Should().Be(0, "a quarantined file must never be read for summarization");
    }

    [Fact]
    public async Task IndexingConsumer_QuarantinedFile_DoesNotDownload()
    {
        Guid org = Guid.NewGuid();
        InferenceApiDbContext db = CsamTestSupport.NewDb();
        Project project = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = Guid.NewGuid(), OrganizationId = org };
        db.Projects.Add(project);
        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "bad.txt",
            Category = "userFiles",
            StorageKey = $"projects/{project.Id}/quarantine/x/bad.txt",
            MimeType = "text/plain",
            SizeBytes = 4,
            IndexingStatus = FileIndexingStatus.Pending,
            CsamStatus = CsamScanStatus.Quarantined,
            UploadedAt = DateTime.UtcNow
        };
        db.ProjectFiles.Add(file);
        await db.SaveChangesAsync();

        CsamTestStorage storage = new();
        Mock<IVectorIndexService> vectors = new();
        vectors.Setup(v => v.DeleteFileChunksAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        ProjectFileIndexingConsumer consumer = new(
            db,
            storage,
            Mock.Of<IFileChunker>(),
            Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(),
            vectors.Object,
            NullLogger<ProjectFileIndexingConsumer>.Instance,
            tenant: new CsamFixedTenant(org));

        Mock<MassTransit.ConsumeContext<ProjectFileIndexingRequested>> context = new();
        context.SetupGet(c => c.Message).Returns(new ProjectFileIndexingRequested
        {
            ProjectId = project.Id,
            FileId = file.Id,
            Operation = "Upsert",
            RequestedAt = DateTime.UtcNow
        });
        context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);

        await consumer.Consume(context.Object);

        storage.Downloads.Should().Be(0, "a quarantined file must never be read for indexing");
    }

    // ------------------------------------------------------------------ brief resolver

    [Fact]
    public async Task BriefFileResolver_QuarantinedFile_IsNotFound()
    {
        InferenceApiDbContext db = CsamTestSupport.NewDb();
        Guid projectId = Guid.NewGuid();
        Project project = new() { Id = projectId, Name = "P", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() };
        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = "bad.txt",
            Category = "userFiles",
            StorageKey = $"projects/{projectId}/quarantine/x/bad.txt",
            MimeType = "text/plain",
            SizeBytes = 4,
            CsamStatus = CsamScanStatus.Quarantined,
            UploadedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.ProjectFiles.Add(file);
        await db.SaveChangesAsync();

        BriefResolution result = await new BriefFileResolver(db, new CsamTestStorage())
            .ResolveAsync(projectId, file.Id, null, CancellationToken.None);

        result.Text.Should().BeNull();
        result.Error.Should().Be(BriefFileResolver.NotFound,
            "a quarantined brief is absent, so its text never enters a model prompt");
    }

    // ------------------------------------------------------------------ device folder sync

    [Fact]
    public async Task DeviceDelete_SkipsQuarantinedFile_AndReportsIt()
    {
        InferenceApiDbContext db = CsamTestSupport.NewDb();
        Guid projectId = Guid.NewGuid();
        Project project = new() { Id = projectId, Name = "P", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() };
        ProjectLocalFolderLink link = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OrganizationId = project.OrganizationId,
            DeviceId = Guid.NewGuid(),
            TargetFolder = "Footage",
            DisplayPath = "D:\\Footage",
            CreatedByUserId = project.OwnerId
        };
        db.Projects.Add(project);
        db.ProjectLocalFolderLinks.Add(link);
        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = "bad.mp4",
            OriginalPath = "Footage/bad.mp4",
            Category = "userFiles",
            StorageKey = $"projects/{projectId}/quarantine/x/bad.mp4",
            MimeType = "video/mp4",
            SizeBytes = 4,
            SourceKind = LocalFolderSyncService.SourceKind,
            SourceLinkId = link.Id,
            CsamStatus = CsamScanStatus.Quarantined,
            UploadedAt = DateTime.UtcNow
        };
        db.ProjectFiles.Add(file);
        await db.SaveChangesAsync();
        CsamTestStorage storage = new();
        storage.Put(file.StorageKey, [1, 2, 3]);

        LocalFolderSyncService service = new(
            db, storage, Mock.Of<IOrganizationEntitlements>(), NullLogger<LocalFolderSyncService>.Instance);

        FolderDeleteOutcome outcome = await service.DeleteFilesWithSkippedAsync(link, ["bad.mp4"], CancellationToken.None);

        outcome.Deleted.Should().Be(0);
        outcome.Skipped.Should().Contain("Footage/bad.mp4");
        storage.DeletedKeys.Should().BeEmpty();
        storage.Objects.Should().ContainKey(file.StorageKey);
        db.ProjectFiles.Should().Contain(f => f.Id == file.Id && f.CsamStatus == CsamScanStatus.Quarantined);
    }

    [Fact]
    public async Task RecordUpload_DoesNotDeleteAQuarantinedDuplicateAtTheSamePath()
    {
        InferenceApiDbContext db = CsamTestSupport.NewDb();
        Guid projectId = Guid.NewGuid();
        Project project = new() { Id = projectId, Name = "P", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() };
        ProjectLocalFolderLink link = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OrganizationId = project.OrganizationId,
            DeviceId = Guid.NewGuid(),
            TargetFolder = "Footage",
            DisplayPath = "D:\\Footage",
            CreatedByUserId = project.OwnerId
        };
        db.Projects.Add(project);
        db.ProjectLocalFolderLinks.Add(link);
        ProjectFile quarantined = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = "bad.mp4",
            OriginalPath = "Footage/bad.mp4",
            Category = "userFiles",
            StorageKey = $"projects/{projectId}/quarantine/x/bad.mp4",
            MimeType = "video/mp4",
            SizeBytes = 4,
            SourceKind = LocalFolderSyncService.SourceKind,
            SourceLinkId = link.Id,
            CsamStatus = CsamScanStatus.Quarantined,
            UploadedAt = DateTime.UtcNow.AddHours(-1)
        };
        db.ProjectFiles.Add(quarantined);
        await db.SaveChangesAsync();
        CsamTestStorage storage = new();
        storage.Put(quarantined.StorageKey, [1, 2, 3]);

        ProjectFile written = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = "bad.mp4",
            OriginalPath = "Footage/bad.mp4",
            Category = "userFiles",
            StorageKey = $"projects/{projectId}/userFiles/new/bad.mp4",
            MimeType = "video/mp4",
            SizeBytes = 4,
            SourceKind = LocalFolderSyncService.SourceKind,
            SourceLinkId = link.Id,
            CsamStatus = CsamScanStatus.Pending,
            UploadedAt = DateTime.UtcNow
        };

        LocalFolderSyncService service = new(
            db, storage, Mock.Of<IOrganizationEntitlements>(), NullLogger<LocalFolderSyncService>.Instance);
        await service.RecordUploadAsync(link, written, CancellationToken.None);

        db.ProjectFiles.Should().Contain(f => f.Id == quarantined.Id && f.CsamStatus == CsamScanStatus.Quarantined);
        storage.DeletedKeys.Should().BeEmpty();
        storage.Objects.Should().ContainKey(quarantined.StorageKey);
    }

    [Fact]
    public async Task TakePrune_DoesNotDeleteAFailedMoveQuarantineObject()
    {
        InferenceApiDbContext db = CsamTestSupport.NewDb();
        Guid projectId = Guid.NewGuid();
        Guid workflowId = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();
        Guid stepId = Guid.NewGuid();
        string key = $"projects/{projectId}/outputFiles/{executionId}/take-1.mp4";
        Project project = new() { Id = projectId, Name = "P", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() };
        WorkflowDefinition workflow = new() { Id = workflowId, ProjectId = projectId, Name = "wf" };
        WorkflowStep step = new() { Id = stepId, WorkflowDefinitionId = workflowId, StepOrder = 1, StepType = StepType.VideoCompile };
        WorkflowExecution execution = new()
        {
            Id = executionId,
            WorkflowDefinitionId = workflowId,
            ProjectId = projectId,
            Status = ExecutionStatus.Passed,
            StartedAt = DateTime.UtcNow
        };
        WorkflowStepResult result = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = executionId,
            WorkflowStepId = stepId,
            Status = StepStatus.Completed,
            OutputStorageKey = key,
            ExecutedAt = DateTime.UtcNow
        };
        ProjectFile evidence = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = "take-1.mp4",
            Category = "outputFiles",
            StorageKey = key,
            MimeType = "video/mp4",
            SizeBytes = 4,
            CsamStatus = CsamScanStatus.Quarantined,
            // A failed move: the row still names the original key, and this is where it should have gone.
            CsamQuarantineKey = $"projects/{projectId}/quarantine/{Guid.NewGuid()}/take-1.mp4",
            UploadedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowSteps.Add(step);
        db.WorkflowExecutions.Add(execution);
        db.WorkflowStepResults.Add(result);
        db.ProjectFiles.Add(evidence);
        await db.SaveChangesAsync();
        CsamTestStorage storage = new();
        storage.Put(key, [1, 2, 3]);
        Mock<IAmazonS3> s3 = new();

        OutputsController controller = new(
            db, Mock.Of<ICurrentUser>(), Mock.Of<IProjectAccessService>(), s3.Object, storage,
            new ConfigurationBuilder().Build());

        Type takeType = typeof(OutputsController).GetNestedType("Take", BindingFlags.NonPublic)!;
        object take = Activator.CreateInstance(takeType, result, "g", 2, false)!;
        IList all = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(takeType))!;
        all.Add(take);
        MethodInfo delete = typeof(OutputsController).GetMethod(
            "DeleteTakesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)delete.Invoke(controller, [projectId, new List<WorkflowStepResult> { result }, all, CancellationToken.None])!;

        db.ProjectFiles.IgnoreQueryFilters().Should().Contain(f => f.Id == evidence.Id,
            "a failed-move quarantine row is evidence until counsel releases it");
        storage.DeletedKeys.Should().BeEmpty("the object must not be deleted");
        storage.Objects.Should().ContainKey(key);
        s3.Invocations.Should().BeEmpty("no S3 delete is issued for a held key");
    }

    // ------------------------------------------------------------------ cloud sync

    [Fact]
    public async Task CloudSyncDelete_SkipsQuarantinedFile()
    {
        CloudFixture f = new();
        Project project = await f.AddProjectAsync();
        CloudStorageConnection connection = await f.AddConnectionAsync();
        ProjectCloudLink link = await f.AddLinkAsync(project, connection);

        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "gone.mp4",
            OriginalPath = "gone.mp4",
            Category = "userFiles",
            StorageKey = $"projects/{project.Id}/quarantine/{Guid.NewGuid():N}/gone.mp4",
            StorageBucket = "bucket",
            MimeType = "video/mp4",
            SizeBytes = 4,
            SourceKind = f.SourceKind,
            SourceRevision = "rev-old",
            CsamStatus = CsamScanStatus.Quarantined,
            UploadedAt = DateTime.UtcNow
        };
        f.Db.ProjectFiles.Add(file);
        f.Storage.Objects[file.StorageKey] = [1, 2, 3];
        await f.Db.SaveChangesAsync();

        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Deleted.Should().Be(0);
        outcome.Skipped.Should().BeGreaterThan(0);
        f.Db.ProjectFiles.Should().Contain(row => row.Id == file.Id);
        f.Storage.Deletes.Should().Be(0);
        f.Storage.Objects.Should().ContainKey(file.StorageKey);
    }

    // ------------------------------------------------------------------ assistant writer

    [Fact]
    public async Task AssistantWriteProjectFile_QuarantinedExisting_CreatesANewRowAndKeepsTheQuarantinedObject()
    {
        Guid ownerId = Guid.NewGuid();
        InferenceApiDbContext db = CsamTestSupport.NewDb();
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = "P",
            OwnerId = ownerId,
            OrganizationId = TestTenancy.OrgOf(ownerId)
        };
        db.Projects.Add(project);
        ProjectFile quarantined = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "notes.md",
            OriginalPath = "notes.md",
            Category = "userFiles",
            StorageKey = $"projects/{project.Id}/quarantine/{Guid.NewGuid():N}/notes.md",
            MimeType = "text/markdown",
            SizeBytes = 3,
            CsamStatus = CsamScanStatus.Quarantined,
            UploadedAt = DateTime.UtcNow
        };
        db.ProjectFiles.Add(quarantined);
        await db.SaveChangesAsync();

        FakeFileStorage storage = new();
        storage.Objects[quarantined.StorageKey] = Encoding.UTF8.GetBytes("old");
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new OwnerUser(ownerId, project.OrganizationId), storage);
        AssistantToolContext context = new(ownerId, project.Id);

        string proposal = await InvokeAsync(provider, context, "ProposeWriteProjectFile",
            ("directoryPath", null), ("fileName", "notes.md"), ("mimeType", "text/markdown"),
            ("contentBase64", Convert.ToBase64String(Encoding.UTF8.GetBytes("new body"))));
        using JsonDocument proposalJson = JsonDocument.Parse(proposal);
        proposalJson.RootElement.GetProperty("replacesExistingFile").GetBoolean().Should().BeFalse(
            "a quarantined row is not a file the assistant may replace");
        string token = proposalJson.RootElement.GetProperty("confirmationToken").GetString()!;

        string applied = await InvokeAsync(provider, context, "ApplyWriteProjectFile",
            ("directoryPath", null), ("fileName", "notes.md"), ("mimeType", "text/markdown"),
            ("contentBase64", Convert.ToBase64String(Encoding.UTF8.GetBytes("new body"))),
            ("confirmationToken", token));
        using JsonDocument appliedJson = JsonDocument.Parse(applied);
        Guid newId = appliedJson.RootElement.GetProperty("id").GetGuid();

        newId.Should().NotBe(quarantined.Id, "the write must create a new row rather than reuse the quarantined one");
        db.ProjectFiles.Should().HaveCount(2);
        db.ProjectFiles.Single(f => f.Id == quarantined.Id).CsamStatus.Should().Be(CsamScanStatus.Quarantined);
        storage.DeletedKeys.Should().BeEmpty("the quarantined object is never deleted");
        storage.Objects.Should().ContainKey(quarantined.StorageKey);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<string> InvokeAsync(
        AssistantToolProvider provider,
        AssistantToolContext context,
        string toolName,
        params (string Key, object? Value)[] arguments)
    {
        AIFunction tool = provider.GetTools(context).Single(t => t.Name == toolName);
        AIFunctionArguments args = new();
        foreach ((string key, object? value) in arguments)
            args[key] = value;
        object? result = await tool.InvokeAsync(args);
        return result?.ToString() ?? string.Empty;
    }

    private sealed class OwnerUser(Guid userId, Guid organizationId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "owner@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool IsPlatformAdmin => false;
    }
}
