using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>
/// Test HTTP message handler that returns success for all requests
/// </summary>
internal class TestHttpMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}

/// <summary>
/// Covers <see cref="ReactRemotionSandboxTools.StageVoiceoverAudio"/> method, specifically:
/// - Correct injection of IServiceScopeFactory to avoid captive-dependency issue
/// - Handling of missing/no-voiceover-step scenarios (returns empty staged list)
/// </summary>
public class ReactRemotionSandboxToolsTests
{
    private static IConfiguration CreateMockConfiguration()
    {
        var dict = new Dictionary<string, string?>
        {
            { "Sandbox:BaseUrl", "http://sandbox-executor:8080" },
            { "Sandbox:ApiToken", "test-token" },
            { "MinIO:BucketName", "reelforge" }
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();
    }

    private static Mock<IWorkflowExecutionContextAccessor> CreateMockAccessor(Guid executionId, Guid projectId)
    {
        var accessor = new Mock<IWorkflowExecutionContextAccessor>();
        accessor.Setup(a => a.Current)
            .Returns(new WorkflowExecutionContext
            {
                ExecutionId = executionId,
                ProjectId = projectId,
                CorrelationId = "test"
            });
        return accessor;
    }

    [Fact]
    public void StageVoiceoverAudio_Tool_CanBeInstantiatedWithIServiceScopeFactory()
    {
        // Arrange: Create a tool instance with proper IServiceScopeFactory injection
        var executionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        var services = new ServiceCollection();
        services.AddDbContext<WorkflowEngineDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        // Act: Create the tool with proper dependency injection
        var tool = new ReactRemotionSandboxTools(
            Mock.Of<IHttpClientFactory>(),
            CreateMockAccessor(executionId, projectId).Object,
            Mock.Of<IProjectFileWorkspace>(),
            Mock.Of<Amazon.S3.IAmazonS3>(),
            CreateMockConfiguration(),
            NullLogger<ReactRemotionSandboxTools>.Instance,
            scopeFactory);

        // Assert: Constructor should succeed with proper scope factory injection
        tool.Should().NotBeNull();
    }

    [Fact]
    public async Task StageVoiceoverAudio_WithNoVoiceoverStep_ReturnsEmptyStaged()
    {
        // Arrange: Setup database with no voiceover step
        var executionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        // The in-memory database name must be captured ONCE: the AddDbContext options lambda runs
        // per scope, so a Guid.NewGuid() inside it would give every scope (the seeding scope, the
        // tool's own scope) its own empty database.
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<WorkflowEngineDbContext>(options =>
            options.UseInMemoryDatabase(dbName));

        var serviceProvider = services.BuildServiceProvider();

        using (var scope = serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            var execution = new WorkflowExecution { Id = executionId, ProjectId = projectId };
            db.WorkflowExecutions.Add(execution);
            await db.SaveChangesAsync();
        }

        // The fixture is only meaningful if a FRESH scope sees it. Without this, the test would
        // pass on an empty database for entirely the wrong reason.
        using (var scope = serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            var seededExecution = await db.WorkflowExecutions.SingleAsync(e => e.Id == executionId);
            seededExecution.ProjectId.Should().Be(projectId);

            (await db.WorkflowStepResults.CountAsync(r => r.WorkflowExecutionId == executionId))
                .Should().Be(0, "this fixture deliberately seeds no step results at all");
        }

        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var tool = new ReactRemotionSandboxTools(
            Mock.Of<IHttpClientFactory>(),
            CreateMockAccessor(executionId, projectId).Object,
            Mock.Of<IProjectFileWorkspace>(),
            Mock.Of<Amazon.S3.IAmazonS3>(),
            CreateMockConfiguration(),
            NullLogger<ReactRemotionSandboxTools>.Instance,
            scopeFactory);

        // Act: Call method with no voiceover step present
        var result = await tool.StageVoiceoverAudio();

        // Assert: Should return empty staged list without throwing
        var stagedJson = JsonSerializer.Deserialize<JsonElement>(result);
        var stagedArray = stagedJson.GetProperty("staged").EnumerateArray().ToList();
        stagedArray.Should().BeEmpty();
    }

    [Fact]
    public async Task StageVoiceoverAudio_WithoutScriptwriterStep_ReturnsEmptyStaged()
    {
        // Arrange: Setup database with voiceover but no scriptwriter step
        var executionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        // The in-memory database name must be captured ONCE: the AddDbContext options lambda runs
        // per scope, so a Guid.NewGuid() inside it would give every scope (the seeding scope, the
        // tool's own scope) its own empty database.
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<WorkflowEngineDbContext>(options =>
            options.UseInMemoryDatabase(dbName));

        var serviceProvider = services.BuildServiceProvider();

        using (var scope = serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            var execution = new WorkflowExecution { Id = executionId, ProjectId = projectId };
            db.WorkflowExecutions.Add(execution);

            var voiceoverStep = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                StepType = StepType.Voiceover,
                StepOrder = 1
            };
            db.WorkflowSteps.Add(voiceoverStep);

            var voiceoverResult = new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = executionId,
                WorkflowStepId = voiceoverStep.Id,
                WorkflowStep = voiceoverStep,
                Status = StepStatus.Completed,
                OutputJson = JsonSerializer.Serialize(new { view = new { lines = new[] { new { id = "vo0", status = "ok" } } } })
            };
            db.WorkflowStepResults.Add(voiceoverResult);

            await db.SaveChangesAsync();
        }

        // The fixture is only meaningful if a FRESH scope sees it. Without this, the test would
        // pass on an empty database for entirely the wrong reason: the tool would take its
        // "no Voiceover step" early return instead of the "no ScriptwriterAgent step" one.
        using (var scope = serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

            var seededVoiceoverResult = await db.WorkflowStepResults
                .Include(r => r.WorkflowStep)
                .SingleAsync(r => r.WorkflowExecutionId == executionId);
            seededVoiceoverResult.WorkflowStep.StepType.Should().Be(StepType.Voiceover);
            seededVoiceoverResult.Status.Should().Be(StepStatus.Completed);

            // ... and there is genuinely no ScriptwriterAgent step, which is the scenario under
            // test (the Voiceover branch above must NOT be the one returning the empty list).
            (await db.WorkflowStepResults
                .Include(r => r.WorkflowStep)
                .ThenInclude(s => s.AgentDefinition)
                .CountAsync(r => r.WorkflowExecutionId == executionId
                    && r.WorkflowStep.StepType == StepType.Agent
                    && r.WorkflowStep.AgentDefinition.AgentType == AgentType.ScriptwriterAgent
                    && r.Status == StepStatus.Completed))
                .Should().Be(0);
        }

        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var tool = new ReactRemotionSandboxTools(
            Mock.Of<IHttpClientFactory>(),
            CreateMockAccessor(executionId, projectId).Object,
            Mock.Of<IProjectFileWorkspace>(),
            Mock.Of<Amazon.S3.IAmazonS3>(),
            CreateMockConfiguration(),
            NullLogger<ReactRemotionSandboxTools>.Instance,
            scopeFactory);

        // Act: Call method with no scriptwriter step
        var result = await tool.StageVoiceoverAudio();

        // Assert: Should return empty staged list
        var stagedJson = JsonSerializer.Deserialize<JsonElement>(result);
        var stagedArray = stagedJson.GetProperty("staged").EnumerateArray().ToList();
        stagedArray.Should().BeEmpty();
    }

    [Fact]
    public async Task StageVoiceoverAudio_WithNToKMappingAndOkStatuses_StagedArrayMapsCorrectly()
    {
        // Arrange: Setup database with 3-scene scriptwriter (middle empty), 2 voiceover lines (both ok)
        var executionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var services = new ServiceCollection();
        services.AddDbContext<WorkflowEngineDbContext>(options =>
            options.UseInMemoryDatabase(dbName));

        var serviceProvider = services.BuildServiceProvider();

        Guid scriptwriterStepId;
        Guid voiceoverStepId;

        using (var scope = serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            var execution = new WorkflowExecution { Id = executionId, ProjectId = projectId };
            db.WorkflowExecutions.Add(execution);

            // Scriptwriter step with 3 scenes (middle one empty)
            var scriptwriterAgent = new AgentDefinition
            {
                Id = Guid.NewGuid(),
                AgentType = AgentType.ScriptwriterAgent,
                GeneratesOutput = true
            };
            db.AgentDefinitions.Add(scriptwriterAgent);

            scriptwriterStepId = Guid.NewGuid();
            var scriptwriterStep = new WorkflowStep
            {
                Id = scriptwriterStepId,
                WorkflowDefinitionId = Guid.NewGuid(),
                StepType = StepType.Agent,
                AgentDefinitionId = scriptwriterAgent.Id,
                AgentDefinition = scriptwriterAgent,
                StepOrder = 1
            };
            db.WorkflowSteps.Add(scriptwriterStep);

            var scriptwriterOutput = new
            {
                scenes = new[]
                {
                    new { voiceover = "Scene 0 voiceover" },
                    new { voiceover = "" },
                    new { voiceover = "Scene 2 voiceover" }
                }
            };

            var scriptwriterResult = new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = executionId,
                WorkflowStepId = scriptwriterStepId,
                WorkflowStep = scriptwriterStep,
                Status = StepStatus.Completed,
                OutputJson = JsonSerializer.Serialize(scriptwriterOutput)
            };
            db.WorkflowStepResults.Add(scriptwriterResult);

            // Voiceover step with 2 lines (both ok)
            voiceoverStepId = Guid.NewGuid();
            var voiceoverStep = new WorkflowStep
            {
                Id = voiceoverStepId,
                WorkflowDefinitionId = Guid.NewGuid(),
                StepType = StepType.Voiceover,
                StepOrder = 2
            };
            db.WorkflowSteps.Add(voiceoverStep);

            var voiceoverResult = new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = executionId,
                WorkflowStepId = voiceoverStepId,
                Status = StepStatus.Completed,
                OutputJson = JsonSerializer.Serialize(new
                {
                    view = new
                    {
                        lines = new[]
                        {
                            new { id = "vo0", status = "ok" },
                            new { id = "vo1", status = "ok" }
                        }
                    }
                })
            };
            db.WorkflowStepResults.Add(voiceoverResult);

            await db.SaveChangesAsync();
        }

        // Mock S3 to return trivial WAV bytes
        var s3ClientMock = new Mock<Amazon.S3.IAmazonS3>();
        s3ClientMock
            .Setup(s => s.GetObjectAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string bucket, string key, CancellationToken ct) =>
            {
                // Create a trivial WAV file (minimal valid WAV header + silence)
                var wavBytes = new byte[] { 0x52, 0x49, 0x46, 0x46 }; // "RIFF"
                var stream = new MemoryStream(wavBytes);
                return new Amazon.S3.Model.GetObjectResponse
                {
                    ResponseStream = stream,
                    HttpStatusCode = System.Net.HttpStatusCode.OK
                };
            });

        // Mock HTTP client factory to return success for sandbox PUT
        var httpClientFactoryMock = new Mock<IHttpClientFactory>();
        httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new TestHttpMessageHandler()));

        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var tool = new ReactRemotionSandboxTools(
            httpClientFactoryMock.Object,
            CreateMockAccessor(executionId, projectId).Object,
            Mock.Of<IProjectFileWorkspace>(),
            s3ClientMock.Object,
            CreateMockConfiguration(),
            NullLogger<ReactRemotionSandboxTools>.Instance,
            scopeFactory);

        // Act: Call method
        var result = await tool.StageVoiceoverAudio();

        // Assert: Should return ["voiceover/scene-0.wav", "voiceover/scene-2.wav"]
        // (scene 1 skipped because it's empty, vo1 maps to scene 2)
        var stagedJson = JsonSerializer.Deserialize<JsonElement>(result);
        var stagedArray = stagedJson.GetProperty("staged").EnumerateArray().Select(e => e.GetString()).ToList();
        stagedArray.Should().Equal("voiceover/scene-0.wav", "voiceover/scene-2.wav");
    }

    [Fact]
    public async Task StageVoiceoverAudio_WithFailedStatusLine_SkipsFailedLine()
    {
        // Arrange: Setup database with 3-scene scriptwriter (middle empty), 2 voiceover lines (second failed)
        var executionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var services = new ServiceCollection();
        services.AddDbContext<WorkflowEngineDbContext>(options =>
            options.UseInMemoryDatabase(dbName));

        var serviceProvider = services.BuildServiceProvider();

        Guid scriptwriterStepId;
        Guid voiceoverStepId;

        using (var scope = serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            var execution = new WorkflowExecution { Id = executionId, ProjectId = projectId };
            db.WorkflowExecutions.Add(execution);

            // Scriptwriter step with 3 scenes (middle one empty)
            var scriptwriterAgent = new AgentDefinition
            {
                Id = Guid.NewGuid(),
                AgentType = AgentType.ScriptwriterAgent,
                GeneratesOutput = true
            };
            db.AgentDefinitions.Add(scriptwriterAgent);

            scriptwriterStepId = Guid.NewGuid();
            var scriptwriterStep = new WorkflowStep
            {
                Id = scriptwriterStepId,
                WorkflowDefinitionId = Guid.NewGuid(),
                StepType = StepType.Agent,
                AgentDefinitionId = scriptwriterAgent.Id,
                AgentDefinition = scriptwriterAgent,
                StepOrder = 1
            };
            db.WorkflowSteps.Add(scriptwriterStep);

            var scriptwriterOutput = new
            {
                scenes = new[]
                {
                    new { voiceover = "Scene 0 voiceover" },
                    new { voiceover = "" },
                    new { voiceover = "Scene 2 voiceover" }
                }
            };

            var scriptwriterResult = new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = executionId,
                WorkflowStepId = scriptwriterStepId,
                WorkflowStep = scriptwriterStep,
                Status = StepStatus.Completed,
                OutputJson = JsonSerializer.Serialize(scriptwriterOutput)
            };
            db.WorkflowStepResults.Add(scriptwriterResult);

            // Voiceover step with 2 lines (second one failed)
            voiceoverStepId = Guid.NewGuid();
            var voiceoverStep = new WorkflowStep
            {
                Id = voiceoverStepId,
                WorkflowDefinitionId = Guid.NewGuid(),
                StepType = StepType.Voiceover,
                StepOrder = 2
            };
            db.WorkflowSteps.Add(voiceoverStep);

            var voiceoverResult = new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = executionId,
                WorkflowStepId = voiceoverStepId,
                Status = StepStatus.Completed,
                OutputJson = JsonSerializer.Serialize(new
                {
                    view = new
                    {
                        lines = new[]
                        {
                            new { id = "vo0", status = "ok" },
                            new { id = "vo1", status = "failed" }
                        }
                    }
                })
            };
            db.WorkflowStepResults.Add(voiceoverResult);

            await db.SaveChangesAsync();
        }

        // Mock S3 to return trivial WAV bytes
        var s3ClientMock = new Mock<Amazon.S3.IAmazonS3>();
        s3ClientMock
            .Setup(s => s.GetObjectAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string bucket, string key, CancellationToken ct) =>
            {
                // Create a trivial WAV file (minimal valid WAV header + silence)
                var wavBytes = new byte[] { 0x52, 0x49, 0x46, 0x46 }; // "RIFF"
                var stream = new MemoryStream(wavBytes);
                return new Amazon.S3.Model.GetObjectResponse
                {
                    ResponseStream = stream,
                    HttpStatusCode = System.Net.HttpStatusCode.OK
                };
            });

        // Mock HTTP client factory to return success for sandbox PUT
        var httpClientFactoryMock = new Mock<IHttpClientFactory>();
        httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new TestHttpMessageHandler()));

        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var tool = new ReactRemotionSandboxTools(
            httpClientFactoryMock.Object,
            CreateMockAccessor(executionId, projectId).Object,
            Mock.Of<IProjectFileWorkspace>(),
            s3ClientMock.Object,
            CreateMockConfiguration(),
            NullLogger<ReactRemotionSandboxTools>.Instance,
            scopeFactory);

        // Act: Call method
        var result = await tool.StageVoiceoverAudio();

        // Assert: Should return ["voiceover/scene-0.wav"] only
        // (scene 1 skipped because it's empty, vo1 has status "failed" so scene 2 doesn't get staged)
        var stagedJson = JsonSerializer.Deserialize<JsonElement>(result);
        var stagedArray = stagedJson.GetProperty("staged").EnumerateArray().Select(e => e.GetString()).ToList();
        stagedArray.Should().Equal("voiceover/scene-0.wav");
    }

    /// <summary>
    /// Pins the EXACT S3 object keys <see cref="ReactRemotionSandboxTools.StageVoiceoverAudio"/>
    /// asks the object store for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>VoiceoverStepExecutor</c> uploads each synthesized WAV through
    /// <c>IProjectFileWorkspace.UploadArtifactAsync(projectId, tempPath,
    /// $"voiceover/{executionId}/{lineId}.wav", "audio/wav", ct)</c>, and
    /// <c>ProjectFileWorkspace</c> prefixes that with <c>projects/{projectId}/agentFiles/</c>. The
    /// real object therefore lives at
    /// <c>projects/{projectId}/agentFiles/voiceover/{executionId}/{lineId}.wav</c>.
    /// </para>
    /// <para>
    /// The pre-existing tests in this file cannot catch a mismatch here because they match
    /// <c>GetObjectAsync</c> with <c>It.IsAny&lt;string&gt;()</c> for the key. Requesting the
    /// prefix-less key raises <c>NoSuchKey</c>, the surrounding <c>catch</c> logs and swallows it,
    /// and the tool returns <c>{"staged":[]}</c> — the promo silently renders with no voiceover at
    /// all. This test makes that failure loud.
    /// </para>
    /// <para>
    /// The expected keys are written as LITERALS on purpose rather than via
    /// <c>ProjectFilePath.BuildStorageKey</c>, so the test pins the contract independently of the
    /// helper it is meant to be checking. All ids are fixed so the literals are stable.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task StageVoiceoverAudio_RequestsPrefixedS3ObjectKeys_AndNeverThePrefixlessKey()
    {
        // Arrange: same 3-scene (middle empty) / 2-ok-line fixture as
        // StageVoiceoverAudio_WithNToKMappingAndOkStatuses_StagedArrayMapsCorrectly, but with
        // fixed ids so every expected key below is a literal.
        var executionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        // VoiceoverStepExecutor numbers its lines as $"vo{index}", so these are the ids the
        // producer actually emits.
        const string lineId0 = "vo0";
        const string lineId1 = "vo1";

        const string expectedKey0 =
            "projects/11111111-1111-1111-1111-111111111111/agentFiles/voiceover/22222222-2222-2222-2222-222222222222/vo0.wav";
        const string expectedKey1 =
            "projects/11111111-1111-1111-1111-111111111111/agentFiles/voiceover/22222222-2222-2222-2222-222222222222/vo1.wav";

        // The historical bug: the prefix-less key the tool used to ask for.
        const string legacyKey0 = "voiceover/22222222-2222-2222-2222-222222222222/vo0.wav";
        const string legacyKey1 = "voiceover/22222222-2222-2222-2222-222222222222/vo1.wav";

        // The in-memory database name must be captured ONCE: the AddDbContext options lambda runs
        // per scope, so a Guid.NewGuid() inside it would give every scope (the seeding scope, the
        // tool's own scope) its own empty database.
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<WorkflowEngineDbContext>(options =>
            options.UseInMemoryDatabase(dbName));

        var serviceProvider = services.BuildServiceProvider();

        using (var scope = serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            var execution = new WorkflowExecution { Id = executionId, ProjectId = projectId };
            db.WorkflowExecutions.Add(execution);

            // Scriptwriter step with 3 scenes (middle one empty)
            var scriptwriterAgent = new AgentDefinition
            {
                Id = Guid.NewGuid(),
                AgentType = AgentType.ScriptwriterAgent,
                GeneratesOutput = true
            };
            db.AgentDefinitions.Add(scriptwriterAgent);

            var scriptwriterStepId = Guid.NewGuid();
            var scriptwriterStep = new WorkflowStep
            {
                Id = scriptwriterStepId,
                WorkflowDefinitionId = Guid.NewGuid(),
                StepType = StepType.Agent,
                AgentDefinitionId = scriptwriterAgent.Id,
                AgentDefinition = scriptwriterAgent,
                StepOrder = 1
            };
            db.WorkflowSteps.Add(scriptwriterStep);

            var scriptwriterOutput = new
            {
                scenes = new[]
                {
                    new { voiceover = "Scene 0 voiceover" },
                    new { voiceover = "" },
                    new { voiceover = "Scene 2 voiceover" }
                }
            };

            var scriptwriterResult = new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = executionId,
                WorkflowStepId = scriptwriterStepId,
                WorkflowStep = scriptwriterStep,
                Status = StepStatus.Completed,
                OutputJson = JsonSerializer.Serialize(scriptwriterOutput)
            };
            db.WorkflowStepResults.Add(scriptwriterResult);

            // Voiceover step with 2 lines (both ok)
            var voiceoverStepId = Guid.NewGuid();
            var voiceoverStep = new WorkflowStep
            {
                Id = voiceoverStepId,
                WorkflowDefinitionId = Guid.NewGuid(),
                StepType = StepType.Voiceover,
                StepOrder = 2
            };
            db.WorkflowSteps.Add(voiceoverStep);

            var voiceoverResult = new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = executionId,
                WorkflowStepId = voiceoverStepId,
                Status = StepStatus.Completed,
                OutputJson = JsonSerializer.Serialize(new
                {
                    view = new
                    {
                        lines = new[]
                        {
                            new { id = lineId0, status = "ok" },
                            new { id = lineId1, status = "ok" }
                        }
                    }
                })
            };
            db.WorkflowStepResults.Add(voiceoverResult);

            await db.SaveChangesAsync();
        }

        // Mock S3 to serve the two real object keys ONLY, and to fail every other key the way a
        // real object store would (NoSuchKey) so a wrong key cannot silently succeed.
        var requestedKeys = new List<string>();
        var s3ClientMock = new Mock<Amazon.S3.IAmazonS3>();
        s3ClientMock
            .Setup(s => s.GetObjectAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string bucket, string key, CancellationToken ct) =>
            {
                // Recorded on entry, so the key is captured even when the branch below throws.
                requestedKeys.Add(key);

                if (key != expectedKey0 && key != expectedKey1)
                {
                    throw new Amazon.S3.AmazonS3Exception($"NoSuchKey: {key}")
                    {
                        StatusCode = System.Net.HttpStatusCode.NotFound,
                        ErrorCode = "NoSuchKey"
                    };
                }

                // Trivial WAV bytes (minimal valid WAV header)
                var wavBytes = new byte[] { 0x52, 0x49, 0x46, 0x46 }; // "RIFF"
                return new Amazon.S3.Model.GetObjectResponse
                {
                    ResponseStream = new MemoryStream(wavBytes),
                    HttpStatusCode = System.Net.HttpStatusCode.OK
                };
            });

        // Mock HTTP client factory to return success for the sandbox PUT
        var httpClientFactoryMock = new Mock<IHttpClientFactory>();
        httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new TestHttpMessageHandler()));

        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var tool = new ReactRemotionSandboxTools(
            httpClientFactoryMock.Object,
            CreateMockAccessor(executionId, projectId).Object,
            Mock.Of<IProjectFileWorkspace>(),
            s3ClientMock.Object,
            CreateMockConfiguration(),
            NullLogger<ReactRemotionSandboxTools>.Instance,
            scopeFactory);

        // Act
        var result = await tool.StageVoiceoverAudio();

        var stagedJson = JsonSerializer.Deserialize<JsonElement>(result);
        var stagedArray = stagedJson.GetProperty("staged").EnumerateArray().Select(e => e.GetString()).ToList();

        // Assert: the tool asked for the two keys VoiceoverStepExecutor actually uploaded, in order.
        // (The `because` clause carries the tool's own result so a RED run shows the swallowed
        // NoSuchKey as `{"staged":[]}` right next to the mismatching keys.)
        requestedKeys.Should().Equal(
            new[] { expectedKey0, expectedKey1 },
            $"StageVoiceoverAudio must request the exact keys VoiceoverStepExecutor uploaded; tool returned {result}");

        // Assert: the historical prefix-less key can never come back.
        s3ClientMock.Verify(
            s => s.GetObjectAsync(It.IsAny<string>(), legacyKey0, It.IsAny<CancellationToken>()),
            Times.Never);
        s3ClientMock.Verify(
            s => s.GetObjectAsync(It.IsAny<string>(), legacyKey1, It.IsAny<CancellationToken>()),
            Times.Never);

        // Assert: exactly one GET per staged line, no extra probing.
        s3ClientMock.Verify(
            s => s.GetObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        // Assert: the n-to-k mapping and the skip-empty/skip-non-ok behaviour still hold.
        stagedArray.Should().Equal("voiceover/scene-0.wav", "voiceover/scene-2.wav");
    }

    // =======================================================================
    // T-066 — the staging tool consumes the content-addressed "storageKey"
    // (condition 1) and stops swallowing a missing object (condition 5)
    // =======================================================================

    /// <summary>
    /// Seeds the minimum the tool reads: one execution, a Scriptwriter step carrying
    /// <paramref name="sceneVoiceovers"/> (scene order IS the scene position), and a Voiceover step
    /// carrying <paramref name="voiceoverLines"/> as (id, status, storageKey). A null storageKey
    /// means the property is absent from the JSON entirely — exactly what the producer writes for a
    /// skipped/failed line.
    /// </summary>
    private static ServiceProvider SeedVoiceoverExecution(
        Guid executionId,
        Guid projectId,
        string[] sceneVoiceovers,
        (string Id, string Status, string? StorageKey)[] voiceoverLines)
    {
        // The name must be hoisted: a Guid generated INSIDE the options lambda is evaluated on every
        // DbContext construction, so each scope would open a different, empty database.
        string dbName = Guid.NewGuid().ToString();

        var services = new ServiceCollection();
        services.AddDbContext<WorkflowEngineDbContext>(options =>
            options.UseInMemoryDatabase(dbName));

        ServiceProvider serviceProvider = services.BuildServiceProvider();

        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

        db.WorkflowExecutions.Add(new WorkflowExecution { Id = executionId, ProjectId = projectId });

        var scriptwriterAgent = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            AgentType = AgentType.ScriptwriterAgent,
            GeneratesOutput = true
        };
        db.AgentDefinitions.Add(scriptwriterAgent);

        var scriptwriterStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepType = StepType.Agent,
            AgentDefinitionId = scriptwriterAgent.Id,
            AgentDefinition = scriptwriterAgent,
            StepOrder = 1
        };
        db.WorkflowSteps.Add(scriptwriterStep);

        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = executionId,
            WorkflowStepId = scriptwriterStep.Id,
            WorkflowStep = scriptwriterStep,
            Status = StepStatus.Completed,
            OutputJson = JsonSerializer.Serialize(new
            {
                scenes = sceneVoiceovers.Select(v => new { voiceover = v }).ToArray()
            })
        });

        var voiceoverStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepType = StepType.Voiceover,
            StepOrder = 2
        };
        db.WorkflowSteps.Add(voiceoverStep);

        // Hand-built so a null StorageKey omits the property, mirroring the producer's
        // JsonIgnore(WhenWritingNull) — a fixture that always wrote the key would never exercise
        // the fallback branch.
        var linesJson = new System.Text.StringBuilder("[");
        for (int i = 0; i < voiceoverLines.Length; i++)
        {
            var line = voiceoverLines[i];
            if (i > 0)
                linesJson.Append(',');
            linesJson.Append($"{{\"id\":{JsonSerializer.Serialize(line.Id)},\"status\":{JsonSerializer.Serialize(line.Status)}");
            if (line.StorageKey is not null)
                linesJson.Append($",\"storageKey\":{JsonSerializer.Serialize(line.StorageKey)}");
            linesJson.Append('}');
        }
        linesJson.Append(']');

        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = executionId,
            WorkflowStepId = voiceoverStep.Id,
            WorkflowStep = voiceoverStep,
            Status = StepStatus.Completed,
            OutputJson = $"{{\"view\":{{\"lines\":{linesJson}}}}}"
        });

        db.SaveChanges();

        return serviceProvider;
    }

    private static Mock<Amazon.S3.IAmazonS3> CreateS3ReturningRiffWave(Action<string> keyCaptured = null)
    {
        var s3 = new Mock<Amazon.S3.IAmazonS3>();
        s3.Setup(s => s.GetObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, key, _) => keyCaptured?.Invoke(key))
            .ReturnsAsync((string _, string _, CancellationToken _) => new Amazon.S3.Model.GetObjectResponse
            {
                // A canonical container, not arbitrary bytes: this tool now hands the payload to
                // VoiceoverStepExecutor's content-addressed sibling, so tests stay honest about the
                // shape a real artifact has.
                ResponseStream = new MemoryStream([
                    0x52, 0x49, 0x46, 0x46, 0x28, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45]),
                HttpStatusCode = System.Net.HttpStatusCode.OK
            });
        return s3;
    }

    private static ReactRemotionSandboxTools CreateVoiceoverTool(
        Guid executionId,
        Guid projectId,
        IServiceScopeFactory scopeFactory,
        Amazon.S3.IAmazonS3 s3Client,
        Microsoft.Extensions.Logging.ILogger<ReactRemotionSandboxTools> logger)
    {
        var httpClientFactoryMock = new Mock<IHttpClientFactory>();
        httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new TestHttpMessageHandler()));

        return new ReactRemotionSandboxTools(
            httpClientFactoryMock.Object,
            CreateMockAccessor(executionId, projectId).Object,
            Mock.Of<IProjectFileWorkspace>(),
            s3Client,
            CreateMockConfiguration(),
            logger,
            scopeFactory);
    }

    [Fact]
    public async Task StageVoiceoverAudio_RequestsTheContentAddressedStorageKeyEachLineCarries()
    {
        Guid executionId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();

        string keyA = $"projects/{projectId}/agentFiles/voiceover/lines/{new string('a', 64)}.wav";
        string keyB = $"projects/{projectId}/agentFiles/voiceover/lines/{new string('b', 64)}.wav";

        using ServiceProvider serviceProvider = SeedVoiceoverExecution(
            executionId,
            projectId,
            ["Scene 0 voiceover", "", "Scene 2 voiceover"],
            [("vo0", "ok", keyA), ("vo1", "ok", keyB)]);

        List<string> requestedKeys = [];
        var s3 = CreateS3ReturningRiffWave(requestedKeys.Add);

        var tool = CreateVoiceoverTool(
            executionId, projectId,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            s3.Object,
            NullLogger<ReactRemotionSandboxTools>.Instance);

        var result = await tool.StageVoiceoverAudio();

        JsonElement json = JsonSerializer.Deserialize<JsonElement>(result);
        json.GetProperty("staged").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("voiceover/scene-0.wav", "voiceover/scene-2.wav");

        requestedKeys.Should().Equal(
            new[] { keyA, keyB },
            $"the tool must GET the content-addressed keys the produced line carries, because a "
            + "cache-hit execution has no object under its own id; tool returned " + result);

        requestedKeys.Should().NotContain(
            k => k.Contains(executionId.ToString(), StringComparison.Ordinal),
            "no object exists under the executing execution's id for a line that came from the step cache");
    }

    [Fact]
    public async Task StageVoiceoverAudio_MissingObject_IsReportedInFailedAndLoggedAtWarningWithoutThrowing()
    {
        // CONDITION 5, staging layer. A missing object used to be swallowed by the generic catch
        // into an Error-level log while the tool still answered {"staged":[]} — so AuthorAgent saw
        // success and the promo rendered without narration.
        Guid executionId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();

        string missingKey = $"projects/{projectId}/agentFiles/voiceover/lines/{new string('c', 64)}.wav";
        string presentKey = $"projects/{projectId}/agentFiles/voiceover/lines/{new string('d', 64)}.wav";

        using ServiceProvider serviceProvider = SeedVoiceoverExecution(
            executionId,
            projectId,
            ["Scene 0 voiceover", "", "Scene 2 voiceover"],
            [("vo0", "ok", missingKey), ("vo1", "ok", presentKey)]);

        var s3 = CreateS3ReturningRiffWave();

        // Configured AFTER the success setup: Moq resolves overlapping setups to the most recently
        // configured one, so exactly one line's object is absent.
        s3.Setup(x => x.GetObjectAsync(It.IsAny<string>(), missingKey, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Amazon.S3.AmazonS3Exception("NoSuchKey")
            {
                StatusCode = System.Net.HttpStatusCode.NotFound,
                ErrorCode = "NoSuchKey"
            });

        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<ReactRemotionSandboxTools>>();

        var tool = CreateVoiceoverTool(
            executionId, projectId,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            s3.Object,
            logger.Object);

        // Act — must not throw.
        var result = await tool.StageVoiceoverAudio();

        // Assert: still a JSON object, `staged` holds only the line that worked.
        JsonElement json = JsonSerializer.Deserialize<JsonElement>(result);
        json.ValueKind.Should().Be(JsonValueKind.Object, "the tool never throws and always answers with JSON");

        json.GetProperty("staged").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(new[] { "voiceover/scene-2.wav" }, "the one good line is still staged");

        JsonElement failed = json.GetProperty("failed");
        failed.EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("lineId").GetString().Should().Be("vo0");
        failed.EnumerateArray().Single().GetProperty("scenePosition").GetInt32().Should().Be(0);
        failed.EnumerateArray().Single().GetProperty("reason").GetString().Should().Be("object_not_found");

        // Assert: the full key is logged, at Warning, together with the line and the scene.
        logger.Verify(
            l => l.Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains(missingKey, StringComparison.Ordinal) &&
                    state.ToString()!.Contains("vo0", StringComparison.Ordinal)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce,
            "a missing voiceover object must be a Warning naming the full key and the line id, "
            + "not an Error swallowed next to every other failure");
    }
}
