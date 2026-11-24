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
/// QA-P1-11 (T-113 item 4): <see cref="ReactRemotionSandboxTools.StageGeneratedClips"/> refuses a
/// screen-content composition — a step whose own render a LATER <c>VideoCompile</c> of the same run
/// pins as insert content (<c>insertContentRef</c> from=Step naming it). That compile composites the
/// RENDER, so a staged generated clip would never appear; staging one is the footgun the guard
/// stops. The allowed cases (a compile pinning some OTHER step, or no persisted step result in
/// scope at all) are pinned too, so the guard cannot be widened by accident.
/// </summary>
public class InsertScreenContentStagingGuardTests
{
    private readonly Guid _executionId = Guid.NewGuid();
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _definitionId = Guid.NewGuid();
    private readonly Guid _authorStepResultId = Guid.NewGuid();
    private readonly Dictionary<string, string> _objects = new();
    private readonly List<string> _written = new();
    private ServiceProvider _provider = null!;

    private const int GenerateOrder = 2;
    private const int AuthorOrder = 3;

    private static object Clip(string id, string status, string? key) =>
        new { clipId = id, status, storageKey = key, purpose = "bg", prompt = "p", durationSeconds = 4.0 };

    private string ClipKey(int order) =>
        $"projects/{_projectId}/agentFiles/generated/{_executionId}/step-{order}-v1.mp4";

    /// <summary>
    /// One execution: a VideoGenerate step with a deliverable clip, an Author step whose persisted
    /// result <paramref name="scopeNamesAuthorStep"/> puts in the tool's scope, and — when
    /// <paramref name="pinStepOrder"/> is set — a later VideoCompile whose <c>insertContentRef</c>
    /// (from=Step) names that step order.
    /// </summary>
    private async Task<ReactRemotionSandboxTools> BuildAsync(int? pinStepOrder, bool scopeNamesAuthorStep)
    {
        string dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(dbName));
        _provider = services.BuildServiceProvider();
        using (IServiceScope scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = _executionId,
                ProjectId = _projectId,
                WorkflowDefinitionId = _definitionId
            });

            var generateStep = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = _definitionId,
                StepType = StepType.VideoGenerate,
                StepOrder = GenerateOrder
            };
            string manifestKey = $"projects/{_projectId}/agentFiles/video-analysis/{_executionId}/step-{GenerateOrder}-generated-clips.json";
            _objects[manifestKey] = JsonSerializer.Serialize(new { version = 1, clips = new[] { Clip("v1", "succeeded", ClipKey(GenerateOrder)) } });
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = _executionId,
                WorkflowStepId = generateStep.Id,
                WorkflowStep = generateStep,
                Status = StepStatus.Completed,
                ArtifactStorageKey = manifestKey
            });

            var authorStep = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = _definitionId,
                StepType = StepType.Agent,
                StepOrder = AuthorOrder
            };
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = _authorStepResultId,
                WorkflowExecutionId = _executionId,
                WorkflowStepId = authorStep.Id,
                WorkflowStep = authorStep,
                Status = StepStatus.Completed,
                ArtifactStorageKey = $"projects/{_projectId}/agentFiles/generated/{_executionId}/step-{AuthorOrder}-render.mp4"
            });

            if (pinStepOrder is { } pinned)
            {
                int compileOrder = Math.Max(pinned, AuthorOrder) + 1;
                db.WorkflowSteps.Add(new WorkflowStep
                {
                    Id = Guid.NewGuid(),
                    WorkflowDefinitionId = _definitionId,
                    StepType = StepType.VideoCompile,
                    StepOrder = compileOrder,
                    VideoCompileConfigJson = JsonSerializer.Serialize(new
                    {
                        version = 1,
                        decision = new { from = "Previous" },
                        analysisStepOrder = 1,
                        enableInserts = true,
                        insertContentRef = new { from = "Step", stepOrder = pinned }
                    })
                });
            }

            await db.SaveChangesAsync();
        }

        var s3 = new Mock<Amazon.S3.IAmazonS3>(MockBehavior.Strict);
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
        accessor.Setup(a => a.Current).Returns(new WorkflowExecutionContext
        {
            ExecutionId = _executionId,
            ProjectId = _projectId,
            CorrelationId = "t",
            StepResultId = scopeNamesAuthorStep ? _authorStepResultId : null
        });
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
    public async Task A_later_compile_pinning_this_steps_render_refuses_staging_and_names_the_pin()
    {
        var tool = await BuildAsync(pinStepOrder: AuthorOrder, scopeNamesAuthorStep: true);

        JsonElement root = JsonSerializer.Deserialize<JsonElement>(await tool.StageGeneratedClips());

        root.GetProperty("ok").GetBoolean().Should().BeFalse();
        string error = root.GetProperty("error").GetString()!;
        error.Should().Contain($"step {AuthorOrder}").And.Contain("insert content").And.Contain("render");
        root.GetProperty("staged").GetArrayLength().Should().Be(0);
        _written.Should().BeEmpty("a refused composition must not write a clip into the sandbox");
    }

    [Fact]
    public async Task A_compile_pinning_a_different_step_leaves_staging_allowed()
    {
        var tool = await BuildAsync(pinStepOrder: GenerateOrder, scopeNamesAuthorStep: true);

        JsonElement root = JsonSerializer.Deserialize<JsonElement>(await tool.StageGeneratedClips());

        root.GetProperty("staged").GetArrayLength().Should().Be(1);
        _written.Should().Contain("public/generated/v1.mp4");
    }

    [Fact]
    public async Task With_no_persisted_step_result_in_scope_staging_is_allowed()
    {
        var tool = await BuildAsync(pinStepOrder: AuthorOrder, scopeNamesAuthorStep: false);

        JsonElement root = JsonSerializer.Deserialize<JsonElement>(await tool.StageGeneratedClips());

        root.GetProperty("staged").GetArrayLength().Should().Be(1);
        _written.Should().Contain("public/generated/v1.mp4");
    }
}
