using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>
/// StageGeneratedClips finds the clips of EVERY completed VideoGenerate step of THIS run (not only the
/// latest one), accepts an optional clipIds filter, and never answers with a silent empty list.
/// </summary>
public class StageGeneratedClipsRunScopeTests
{
    private readonly Guid _executionId = Guid.NewGuid();
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Dictionary<string, string> _objects = new();
    private readonly List<string> _written = new();
    private ServiceProvider _provider = null!;

    private string Key(int order, int index) => $"projects/{_projectId}/agentFiles/generated/{_executionId}/step-{order}-v{index}.mp4";

    private static object Clip(string id, string status, string? key) => new { clipId = id, status, storageKey = key, purpose = "bg", prompt = "p", durationSeconds = 4.0 };

    private async Task<ReactRemotionSandboxTools> Build(params (int order, object[] clips)[] steps)
    {
        string dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(dbName));
        _provider = services.BuildServiceProvider();
        using (IServiceScope scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            db.WorkflowExecutions.Add(new WorkflowExecution { Id = _executionId, ProjectId = _projectId });
            foreach (var (order, clips) in steps)
            {
                var step = new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = Guid.NewGuid(), StepType = StepType.VideoGenerate, StepOrder = order };
                string manifestKey = $"projects/{_projectId}/agentFiles/video-analysis/{_executionId}/step-{order}-generated-clips.json";
                _objects[manifestKey] = JsonSerializer.Serialize(new { version = 1, clips });
                db.WorkflowStepResults.Add(new WorkflowStepResult
                {
                    Id = Guid.NewGuid(),
                    WorkflowExecutionId = _executionId,
                    WorkflowStepId = step.Id,
                    WorkflowStep = step,
                    Status = StepStatus.Completed,
                    ArtifactStorageKey = manifestKey
                });
            }
            await db.SaveChangesAsync();
        }

        var s3 = new Mock<Amazon.S3.IAmazonS3>();
        s3.Setup(s => s.GetObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string key, CancellationToken _) => new GetObjectResponse
            {
                ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes(_objects.TryGetValue(key, out var v) ? v : "bytes"))
            });
        var session = new Mock<ISandboxSession>();
        session.Setup(s => s.WriteBytesAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Callback((string p, byte[] _, CancellationToken _) => _written.Add(p)).Returns(Task.CompletedTask);
        var factory = new Mock<ISandboxSessionFactory>();
        factory.Setup(f => f.Create(It.IsAny<Guid>())).Returns(session.Object);

        var accessor = new Mock<IWorkflowExecutionContextAccessor>();
        accessor.Setup(a => a.Current).Returns(new WorkflowExecutionContext { ExecutionId = _executionId, ProjectId = _projectId, CorrelationId = "t" });
        IConfiguration cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            { "Sandbox:BaseUrl", "http://sandbox-executor:8080" }, { "Sandbox:ApiToken", "t" }, { "MinIO:BucketName", "reelforge" }
        }).Build();
        return new ReactRemotionSandboxTools(
            Mock.Of<System.Net.Http.IHttpClientFactory>(), accessor.Object, Mock.Of<IProjectFileWorkspace>(), s3.Object, cfg,
            NullLogger<ReactRemotionSandboxTools>.Instance, _provider.GetRequiredService<IServiceScopeFactory>(),
            sandboxSessions: factory.Object);
    }

    [Fact]
    public async Task A_later_generate_step_with_nothing_deliverable_does_not_hide_an_earlier_steps_clips()
    {
        var tool = await Build(
            (2, new[] { Clip("v1", "succeeded", Key(2, 1)) }),
            (5, new[] { Clip("v1", "skipped", null) }));

        JsonElement root = JsonSerializer.Deserialize<JsonElement>(await tool.StageGeneratedClips());

        root.GetProperty("staged").GetArrayLength().Should().Be(1);
        _written.Should().Contain("public/generated/v1.mp4");
    }

    [Fact]
    public async Task A_superseded_generate_result_is_ignored_and_its_clips_are_not_staged()
    {
        var tool = await Build(
            (2, new[] { Clip("v1", "succeeded", Key(2, 1)) }),
            (5, new[] { Clip("v2", "succeeded", Key(5, 2)) }));
        using (IServiceScope scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            WorkflowStepResult older = db.WorkflowStepResults.Single(r => r.WorkflowStep.StepOrder == 2);
            older.SupersededAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        JsonElement root = JsonSerializer.Deserialize<JsonElement>(await tool.StageGeneratedClips());

        root.GetProperty("staged").GetArrayLength().Should().Be(1);
        _written.Should().ContainSingle().Which.Should().Be("public/generated/v2.mp4");
    }

    [Fact]
    public async Task Clips_of_two_generate_steps_are_both_staged_and_colliding_ids_do_not_overwrite()
    {
        var tool = await Build(
            (2, new[] { Clip("v1", "succeeded", Key(2, 1)) }),
            (5, new[] { Clip("v1", "succeeded", Key(5, 1)), Clip("v2", "succeeded", Key(5, 2)) }));

        JsonElement root = JsonSerializer.Deserialize<JsonElement>(await tool.StageGeneratedClips());

        root.GetProperty("staged").GetArrayLength().Should().Be(3);
        _written.Distinct().Should().HaveCount(3);
        _written.Should().Contain("public/generated/v1.mp4").And.Contain("public/generated/v2.mp4");
    }

    [Fact]
    public async Task ClipIds_filter_stages_only_the_requested_clips_and_an_unknown_id_is_a_clear_error()
    {
        var tool = await Build((2, new[] { Clip("v1", "succeeded", Key(2, 1)), Clip("v2", "succeeded", Key(2, 2)) }));

        JsonElement some = JsonSerializer.Deserialize<JsonElement>(await tool.StageGeneratedClips(new[] { "v2" }));
        some.GetProperty("staged").GetArrayLength().Should().Be(1);
        _written.Should().Equal("public/generated/v2.mp4");

        JsonElement none = JsonSerializer.Deserialize<JsonElement>(await tool.StageGeneratedClips(new[] { "v9" }));
        none.GetProperty("ok").GetBoolean().Should().BeFalse();
        none.GetProperty("error").GetString().Should().Contain("v9").And.Contain("StageProjectFile");
    }

    [Fact]
    public async Task Nothing_to_stage_is_an_explicit_error_naming_what_was_searched_and_found()
    {
        var tool = await Build((3, new[] { Clip("v1", "failed", null), Clip("v2", "skipped", null) }));

        JsonElement root = JsonSerializer.Deserialize<JsonElement>(await tool.StageGeneratedClips());

        root.GetProperty("ok").GetBoolean().Should().BeFalse();
        string error = root.GetProperty("error").GetString()!;
        error.Should().Contain("step 3").And.Contain("2 clip").And.Contain("StageProjectFile");
        root.GetProperty("staged").GetArrayLength().Should().Be(0);
    }
}
