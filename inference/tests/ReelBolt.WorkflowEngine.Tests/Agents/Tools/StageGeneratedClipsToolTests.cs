using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
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
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>
/// <see cref="ReactRemotionSandboxTools.StageGeneratedClips"/> (issue #93 phase 4, Remotion background
/// plates): a promo workflow with no completed <c>VideoGenerate</c> step stages nothing and says so with
/// an empty <c>staged</c> array — the common case, since most promos generate no plates — and never
/// touches the object store or the sandbox to find that out.
/// </summary>
public class StageGeneratedClipsToolTests
{
    private static IConfiguration Configuration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            { "Sandbox:BaseUrl", "http://sandbox-executor:8080" },
            { "Sandbox:ApiToken", "test-token" },
            { "MinIO:BucketName", "reelforge" }
        }).Build();

    private static Mock<IWorkflowExecutionContextAccessor> Accessor(Guid executionId, Guid projectId)
    {
        var accessor = new Mock<IWorkflowExecutionContextAccessor>();
        accessor.Setup(a => a.Current).Returns(new WorkflowExecutionContext
        {
            ExecutionId = executionId,
            ProjectId = projectId,
            CorrelationId = "test"
        });
        return accessor;
    }

    [Fact]
    public async Task With_no_completed_VideoGenerate_step_it_returns_empty_staged_and_failed_without_reading_storage()
    {
        var executionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var services = new ServiceCollection();
        services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(dbName));
        ServiceProvider provider = services.BuildServiceProvider();

        using (IServiceScope scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            db.WorkflowExecutions.Add(new WorkflowExecution { Id = executionId, ProjectId = projectId });
            // A completed VOICEOVER step with an artifact must not be mistaken for a generate step.
            var voiceoverStep = new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = Guid.NewGuid(), StepType = StepType.Voiceover, StepOrder = 1 };
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = executionId,
                WorkflowStepId = voiceoverStep.Id,
                WorkflowStep = voiceoverStep,
                Status = StepStatus.Completed,
                ArtifactStorageKey = "projects/p/agentFiles/voiceover/x.json"
            });
            await db.SaveChangesAsync();
        }

        var s3 = new Mock<Amazon.S3.IAmazonS3>(MockBehavior.Strict);
        var httpFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);

        var tool = new ReactRemotionSandboxTools(
            httpFactory.Object,
            Accessor(executionId, projectId).Object,
            Mock.Of<IProjectFileWorkspace>(),
            s3.Object,
            Configuration(),
            NullLogger<ReactRemotionSandboxTools>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>());

        string result = await tool.StageGeneratedClips();

        JsonElement root = JsonSerializer.Deserialize<JsonElement>(result);
        root.GetProperty("staged").GetArrayLength().Should().Be(0);
        root.GetProperty("failed").GetArrayLength().Should().Be(0);
        s3.VerifyNoOtherCalls();
        httpFactory.VerifyNoOtherCalls();
    }
}
