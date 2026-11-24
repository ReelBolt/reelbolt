using System;
using System.IO;
using System.Linq;
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
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A new engine output row records when its output was made: the earlier of the current run's first start and now, the
/// epoch when the start is unknown, never null (null means "not an output"). Covered for the three methods that make a
/// project-file row.
/// </summary>
public sealed class OutputProducedStampTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly WorkflowExecutionContextAccessor _accessor = new();
    private readonly Guid _projectId = Guid.NewGuid();

    public OutputProducedStampTests()
    {
        string dbName = Guid.NewGuid().ToString();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(dbName));
        _provider = services.BuildServiceProvider();
    }

    public void Dispose() => _provider.Dispose();

    private ProjectFileWorkspace Workspace()
    {
        Mock<IAmazonS3> s3 = new();
        s3.SetupGet(c => c.Config).Returns(new AmazonS3Config());
        s3.Setup(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PutObjectResponse());
        s3.Setup(c => c.GetObjectMetadataAsync(It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse());
        return new ProjectFileWorkspace(
            s3.Object, _provider.GetRequiredService<IServiceScopeFactory>(), new ConfigurationBuilder().Build(),
            NullLogger<ProjectFileWorkspace>.Instance, _accessor);
    }

    private async Task<Guid> SeedRunAsync(DateTime? firstStartedAt)
    {
        Guid executionId = Guid.NewGuid();
        using (IServiceScope scope = _provider.CreateScope())
        {
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            db.WorkflowExecutions.Add(new WorkflowExecution { Id = executionId, ProjectId = _projectId, FirstStartedAt = firstStartedAt });
            await db.SaveChangesAsync();
        }

        return executionId;
    }

    // The ambient context is an AsyncLocal: it must be opened by the test method itself, not by an awaited helper.
    private IDisposable Begin(Guid executionId) => _accessor.BeginScope(executionId, _projectId, "stamp-test");

    private async Task<DateTime?> StampOfAsync(Guid fileId)
    {
        using IServiceScope scope = _provider.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        return (await db.ProjectFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == fileId)).OutputProducedAt;
    }

    public enum Method { Register, UploadBinary, WriteText }

    private async Task<Guid> MakeOutputAsync(Method method, string category = "outputFiles")
    {
        ProjectFileWorkspace ws = Workspace();
        switch (method)
        {
            case Method.Register:
                return (await ws.RegisterStoredObjectAsync(
                    _projectId, $"projects/{_projectId}/{category}/a.mp4", "a.mp4", "video/mp4", 10,
                    SummaryStatus.Done, FileIndexingStatus.NotIndexed, CancellationToken.None, category)).Id;
            case Method.UploadBinary:
                string local = Path.GetTempFileName();
                await File.WriteAllBytesAsync(local, [1, 2, 3]);
                try
                {
                    return (await ws.UploadBinaryFileAsync(
                        _projectId, local, "b.mp4", "video/mp4", SummaryStatus.Done, FileIndexingStatus.NotIndexed,
                        CancellationToken.None, category)).Id;
                }
                finally
                {
                    File.Delete(local);
                }
            default:
                return (await ws.WriteTextFileAsync(_projectId, "c.txt", "hello", "text/plain", CancellationToken.None, category)).Id;
        }
    }

    [Theory]
    [InlineData(Method.Register)]
    [InlineData(Method.UploadBinary)]
    [InlineData(Method.WriteText)]
    public async Task A_run_first_started_before_now_stamps_its_first_start(Method method)
    {
        DateTime first = DateTime.UtcNow.AddDays(-3);
        using IDisposable scope = Begin(await SeedRunAsync(first));

        Guid id = await MakeOutputAsync(method);

        (await StampOfAsync(id)).Should().Be(first, "the output is no newer than the run that made it");
    }

    [Theory]
    [InlineData(Method.Register)]
    [InlineData(Method.UploadBinary)]
    [InlineData(Method.WriteText)]
    public async Task An_unknown_start_is_held_with_the_epoch_never_null(Method method)
    {
        using IDisposable unknownStart = Begin(await SeedRunAsync(null));
        Guid withContext = await MakeOutputAsync(method);
        (await StampOfAsync(withContext)).Should().Be(DateTime.UnixEpoch);

        unknownStart.Dispose();
        Guid noRun = await MakeOutputAsync(method);
        (await StampOfAsync(noRun)).Should().Be(DateTime.UnixEpoch, "an output with no run behind it is held too");
    }

    [Theory]
    [InlineData(Method.Register)]
    [InlineData(Method.UploadBinary)]
    [InlineData(Method.WriteText)]
    public async Task A_row_that_is_not_an_output_gets_no_stamp(Method method)
    {
        using IDisposable scope = Begin(await SeedRunAsync(DateTime.UtcNow.AddDays(-3)));

        Guid id = await MakeOutputAsync(method, category: "userFiles");

        (await StampOfAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task A_run_first_started_after_the_marker_is_visible_and_one_before_it_is_hidden()
    {
        DateTime marker = DateTime.UtcNow.AddDays(-1);
        using (IServiceScope scope = _provider.CreateScope())
        {
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            db.Projects.Add(new Project { Id = _projectId, Name = "p", ContentQuarantinedAt = marker });
            await db.SaveChangesAsync();
        }

        Guid before, after, unknown;
        using (Begin(await SeedRunAsync(marker.AddHours(-2))))
            before = await MakeOutputAsync(Method.Register);
        using (Begin(await SeedRunAsync(marker.AddHours(2))))
            after = await MakeOutputAsync(Method.Register);
        using (Begin(await SeedRunAsync(null)))
            unknown = await MakeOutputAsync(Method.Register);

        using IServiceScope read = _provider.CreateScope();
        WorkflowEngineDbContext engine = read.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        Guid[] visible = await engine.ProjectFiles.Select(f => f.Id).ToArrayAsync();
        visible.Should().Contain(after).And.NotContain(before).And.NotContain(unknown);
    }
}
