#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Consumers;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// An ingest-moderation quarantine (<see cref="FileModerationStatus.Quarantined"/>) hides a file from the
/// engine exactly like a CSAM quarantine: the entity's global query filter and the workspace's explicit
/// predicates both refuse it. <see cref="FileModerationStatus.Error"/> and
/// <see cref="FileModerationStatus.Scanning"/> stay readable (as in the API), and the plan storage-quota sum
/// (which ignores filters on purpose) still counts the quarantined bytes.
/// </summary>
public class ModerationQuarantineEngineFilterTests : IDisposable
{
    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly ServiceProvider _provider;

    public ModerationQuarantineEngineFilterTests()
    {
        string dbName = "moderation-quarantine-" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(dbName), ServiceLifetime.Scoped);
        services.AddDbContextFactory<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(dbName), ServiceLifetime.Scoped);
        _provider = services.BuildServiceProvider();
    }

    public void Dispose() => _provider.Dispose();

    private ProjectFile Seed(FileModerationStatus status)
    {
        string name = $"{status}.txt".ToLowerInvariant();
        var file = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = ProjectId,
            OriginalFileName = name,
            OriginalPath = name,
            Category = "userFiles",
            MimeType = "text/plain",
            StorageKey = $"projects/{ProjectId}/userFiles/{name}",
            StorageFileName = name,
            StorageBucket = "reelforge",
            SizeBytes = 1000,
            UploadedAt = DateTime.UtcNow,
            CsamStatus = CsamScanStatus.Clean,
            ModerationStatus = status
        };
        using IServiceScope scope = _provider.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        db.ProjectFiles.Add(file);
        db.SaveChanges();
        return file;
    }

    private ProjectFileWorkspace Workspace()
    {
        Mock<IAmazonS3> s3 = new();
        s3.Setup(c => c.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GetObjectResponse { ResponseStream = new MemoryStream("hello"u8.ToArray()) });
        return new ProjectFileWorkspace(s3.Object, _provider.GetRequiredService<IServiceScopeFactory>(), new ConfigurationBuilder().Build(), NullLogger<ProjectFileWorkspace>.Instance);
    }

    [Fact]
    public async Task The_dbcontext_filter_hides_only_a_quarantined_row()
    {
        ProjectFile quarantined = Seed(FileModerationStatus.Quarantined);
        ProjectFile error = Seed(FileModerationStatus.Error);
        ProjectFile scanning = Seed(FileModerationStatus.Scanning);
        ProjectFile clean = Seed(FileModerationStatus.Clean);

        using IServiceScope scope = _provider.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        Guid[] visible = await db.ProjectFiles.AsNoTracking().Select(f => f.Id).ToArrayAsync();

        visible.Should().NotContain(quarantined.Id);
        visible.Should().Contain([error.Id, scanning.Id, clean.Id]);
    }

    [Fact]
    public async Task The_workspace_lists_and_resolves_by_id_and_by_key_without_a_quarantined_file()
    {
        ProjectFile quarantined = Seed(FileModerationStatus.Quarantined);
        ProjectFile error = Seed(FileModerationStatus.Error);
        ProjectFile scanning = Seed(FileModerationStatus.Scanning);
        ProjectFileWorkspace workspace = Workspace();

        var listed = await workspace.ListFilesAsync(ProjectId, CancellationToken.None);
        listed.Select(f => f.Id).Should().NotContain(quarantined.Id);
        listed.Select(f => f.Id).Should().Contain([error.Id, scanning.Id]);

        await FluentActions.Awaiting(() => workspace.ReadFileAsync(ProjectId, quarantined.Id.ToString(), CancellationToken.None))
            .Should().ThrowAsync<KeyNotFoundException>("resolve by id");
        await FluentActions.Awaiting(() => workspace.ReadFileAsync(ProjectId, quarantined.StorageKey, CancellationToken.None))
            .Should().ThrowAsync<KeyNotFoundException>("resolve by key");
        await FluentActions.Awaiting(() => workspace.ReadFileAsync(ProjectId, quarantined.OriginalFileName, CancellationToken.None))
            .Should().ThrowAsync<KeyNotFoundException>("resolve by name");

        (await workspace.ReadFileAsync(ProjectId, error.Id.ToString(), CancellationToken.None)).Should().Contain("hello", "Error stays readable");
        (await workspace.ReadFileAsync(ProjectId, scanning.StorageKey, CancellationToken.None)).Should().Contain("hello", "Scanning stays readable");
    }

    [Fact]
    public async Task The_timeline_render_asset_lookup_does_not_resolve_a_quarantined_file()
    {
        ProjectFile quarantined = Seed(FileModerationStatus.Quarantined);
        ProjectFile error = Seed(FileModerationStatus.Error);
        MethodInfo resolve = typeof(TimelineRenderStepExecutor).GetMethod("ResolveStorageKeyAsync", BindingFlags.NonPublic | BindingFlags.Static)!;

        async Task<string?> Resolve(ProjectFile file)
        {
            using IServiceScope scope = _provider.CreateScope();
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            var task = (Task)resolve.Invoke(null, [db, ProjectId, new AssetRef(AssetRefKind.ProjectFile, file.Id), CancellationToken.None])!;
            await task;
            object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            return (string?)result.GetType().GetField("Item1")!.GetValue(result);
        }

        (await Resolve(quarantined)).Should().BeNull();
        (await Resolve(error)).Should().Be(error.StorageKey);
    }

    [Fact]
    public async Task The_media_derivatives_lookup_drops_a_request_for_a_quarantined_file()
    {
        ProjectFile quarantined = Seed(FileModerationStatus.Quarantined);
        ProjectFile error = Seed(FileModerationStatus.Error);
        var consumer = new MediaDerivativesConsumer(
            _provider.GetRequiredService<IDbContextFactory<WorkflowEngineDbContext>>(), null!, null!, NullLogger<MediaDerivativesConsumer>.Instance);
        MethodInfo current = typeof(MediaDerivativesConsumer).GetMethod("IsStillCurrentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        async Task<bool> IsCurrent(ProjectFile f) =>
            await (Task<bool>)current.Invoke(consumer, [
                new MediaDerivativesRequested(ProjectId, new MediaSourceRef(MediaSourceKind.ProjectFile, f.Id), f.Id, f.StorageKey, "etag", "text/plain",
                    [MediaDerivativeKind.Proxy], "test", DateTime.UtcNow),
                CancellationToken.None])!;

        (await IsCurrent(quarantined)).Should().BeFalse();
        (await IsCurrent(error)).Should().BeTrue();
    }

    [Fact]
    public async Task The_storage_quota_sum_still_counts_a_quarantined_file()
    {
        Seed(FileModerationStatus.Quarantined);
        Seed(FileModerationStatus.Clean);

        using IServiceScope scope = _provider.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        // The same shape StepOutputRegistration.IsOverQuotaAsync uses: filters ignored, every byte counts.
        long used = await db.ProjectFiles.AsNoTracking().IgnoreQueryFilters().Where(f => f.ProjectId == ProjectId).SumAsync(f => (long?)f.SizeBytes) ?? 0;
        used.Should().Be(2000);
    }
}
