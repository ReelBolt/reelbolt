using System;
using System.Linq;
using System.Net;
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
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Storage;

/// <summary>
/// A registered object's size is what the object store holds, never the caller's claim. The claim comes from a
/// media-session stat, which on a remote session is the runner's own answer, and storage usage sums
/// <see cref="ProjectFile.SizeBytes"/>: a runner that reports 0 must not register a free file.
/// </summary>
public sealed class RegisterStoredObjectSizeTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Mock<IAmazonS3> _s3 = new();

    public RegisterStoredObjectSizeTests()
    {
        string dbName = Guid.NewGuid().ToString();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(dbName));
        _provider = services.BuildServiceProvider();
        _s3.SetupGet(c => c.Config).Returns(new AmazonS3Config());
    }

    public void Dispose() => _provider.Dispose();

    private ProjectFileWorkspace Workspace() => new(
        _s3.Object, _provider.GetRequiredService<IServiceScopeFactory>(), new ConfigurationBuilder().Build(),
        NullLogger<ProjectFileWorkspace>.Instance);

    private string Key => $"projects/{_projectId}/outputFiles/{Guid.NewGuid()}.mp4";

    [Fact]
    public async Task The_row_records_the_stored_size_not_an_under_reported_claim()
    {
        string key = Key;
        _s3.Setup(c => c.GetObjectMetadataAsync(It.Is<GetObjectMetadataRequest>(r => r.Key == key), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = 5_000_000 });

        ProjectWorkspaceFile file = await Workspace().RegisterStoredObjectAsync(
            _projectId, key, "render.mp4", "video/mp4", sizeBytes: 0,
            SummaryStatus.Done, FileIndexingStatus.NotIndexed, CancellationToken.None);

        file.SizeBytes.Should().Be(5_000_000);
        using IServiceScope scope = _provider.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        (await db.ProjectFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == file.Id)).SizeBytes.Should().Be(5_000_000);
    }

    [Fact]
    public async Task An_unreadable_stored_size_fails_closed_and_registers_nothing()
    {
        _s3.Setup(c => c.GetObjectMetadataAsync(It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("not found") { StatusCode = HttpStatusCode.NotFound });

        Func<Task> act = () => Workspace().RegisterStoredObjectAsync(
            _projectId, Key, "render.mp4", "video/mp4", sizeBytes: 1234,
            SummaryStatus.Done, FileIndexingStatus.NotIndexed, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        using IServiceScope scope = _provider.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        db.ProjectFiles.IgnoreQueryFilters().Any().Should().BeFalse();
    }
}
