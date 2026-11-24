using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Tests.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>
/// D8: what <c>RenderVideoAndUploadToStorage</c> does about the two answers
/// <see cref="ISandboxSession.ResolvePublishRouteAsync"/> can give it. A LOCAL render is uploaded
/// straight to its final key, exactly as before this work package — nothing crossed a machine
/// boundary. A RUNNER-BACKED render is published into that job's staging key space with a presigned
/// PUT, and reaches its final key only through D12's verifier; a rejected output fails the render
/// rather than being stored.
/// </summary>
public class ReactRemotionSandboxToolsStagingTests
{
    private const string OutputFileName = "promo.mp4";

    [Fact]
    public async Task A_runner_backed_render_publishes_into_the_jobs_staging_key_space_and_promotes_only_after_verification()
    {
        Guid executionId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();

        string storageKey = $"projects/{projectId}/outputFiles/{executionId}/{OutputFileName}";
        FakeRendererSession session = new(executionId)
        {
            Route = new SandboxPublishRoute(true, jobId, RunnerJobTarget.CloudPool),
            PublishResult = new SandboxTransfer(2048, new string('a', 64))
        };

        FakePresignedUrlService presigner = new();
        FakeOutputVerifier verifier = new()
        {
            Verdict = new OutputVerificationResult(true, null, FailureClass.None, null, 4096, null, storageKey)
        };

        Mock<IAmazonS3> s3 = new();
        ReactRemotionSandboxTools tool = Build(executionId, projectId, session, s3, presigner, verifier);

        string json = await tool.RenderVideoAndUploadToStorage("Main", OutputFileName);

        using JsonDocument document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("storageKey").GetString().Should().Be(storageKey);
        document.RootElement.GetProperty("sizeBytes").GetInt64().Should().Be(4096);
        document.RootElement.GetProperty("fileName").GetString().Should().Be(OutputFileName);

        // The runner was handed ONE object and ONE method: a PUT into its own job's staging key space.
        presigner.Puts.Should().ContainSingle();
        presigner.Puts[0].JobId.Should().Be(jobId);
        presigner.Puts[0].ContentType.Should().Be("video/mp4");

        session.Publishes.Should().ContainSingle();
        session.Publishes[0].Path.Should().Be("out/" + OutputFileName);
        session.Publishes[0].Url.Should().Be("https://objects.test/put?key=" + Uri.EscapeDataString(
            "runner-staging/" + jobId.ToString("D") + "/1"));

        // ...and the final key is reached only through D12, addressed by the SAME staging index.
        verifier.Requests.Should().ContainSingle();
        RunnerOutputRequest request = verifier.Requests[0];
        request.ProjectId.Should().Be(projectId);
        request.JobId.Should().Be(jobId);
        request.StagingIndex.Should().Be(1);
        request.FinalKey.Should().Be(storageKey);
        request.Target.Should().Be(RunnerJobTarget.CloudPool);
        request.Spec.MaxBytes.Should().Be(ReactRemotionSandboxTools.DefaultRenderMaxBytes);

        // The runner's digest is a HINT, carried into the spec, and never the answer.
        request.Spec.Sha256.Should().Be(new string('a', 64));

        // The engine never wrote the render itself: the promoted object IS the runner's bytes.
        s3.Verify(
            s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_rejected_runner_render_fails_the_tool_rather_than_being_stored()
    {
        Guid executionId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();

        FakeRendererSession session = new(executionId)
        {
            Route = new SandboxPublishRoute(true, jobId, RunnerJobTarget.OrgRunner)
        };

        FakeOutputVerifier verifier = new()
        {
            Verdict = new OutputVerificationResult(
                false, "RUNNER_OUTPUT_DURATION_MISMATCH", FailureClass.Runner, "expected 10s, got 4s", 2048, null, null)
        };

        Mock<IAmazonS3> s3 = new();
        ReactRemotionSandboxTools tool = Build(executionId, projectId, session, s3, new FakePresignedUrlService(), verifier);

        Func<Task> act = () => tool.RenderVideoAndUploadToStorage("Main", OutputFileName);

        (await act.Should().ThrowAsync<RunnerOutputRejectedException>())
            .Which.Code.Should().Be("RUNNER_OUTPUT_DURATION_MISMATCH");

        s3.Verify(
            s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_local_render_uploads_straight_to_its_final_key_with_the_metadata_it_has_always_carried()
    {
        Guid executionId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();

        byte[] render = Encoding.ASCII.GetBytes("a local render");
        FakeRendererSession session = new(executionId)
        {
            Route = new SandboxPublishRoute(false, null, null),
            RawBytes = render
        };

        PutObjectRequest? uploaded = null;
        Mock<IAmazonS3> s3 = new();
        s3.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((request, _) => uploaded = request)
            .ReturnsAsync(new PutObjectResponse());

        ReactRemotionSandboxTools tool = Build(executionId, projectId, session, s3, new FakePresignedUrlService(), new FakeOutputVerifier());

        string json = await tool.RenderVideoAndUploadToStorage("Main", OutputFileName);

        using JsonDocument document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("storageKey").GetString()
            .Should().Be($"projects/{projectId}/outputFiles/{executionId}/{OutputFileName}");
        document.RootElement.GetProperty("sizeBytes").GetInt64().Should().Be(render.LongLength);

        uploaded.Should().NotBeNull();
        uploaded!.Key.Should().Be($"projects/{projectId}/outputFiles/{executionId}/{OutputFileName}");
        uploaded.ContentType.Should().Be("video/mp4");
        uploaded.Metadata["workflow-execution-id"].Should().Be(executionId.ToString());
        uploaded.Metadata["composition-id"].Should().Be("Main");
        uploaded.Metadata["category"].Should().Be("outputFiles");

        // Nothing was staged and nothing was verified: there is no machine boundary on this arm.
        session.Publishes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_successful_video_render_snapshots_its_workspace_and_a_failing_snapshot_never_fails_the_render()
    {
        Guid executionId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        FakeRendererSession session = new(executionId) { Route = new SandboxPublishRoute(false, null, null) };
        Mock<IAmazonS3> s3 = new();
        s3.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PutObjectResponse());

        ReelBolt.WorkflowEngine.Services.Compositions.CompositionCaptureRequest? captured = null;
        Mock<ReelBolt.WorkflowEngine.Services.Compositions.ICompositionWorkspaceCapture> capture = new();
        capture.Setup(c => c.TryCaptureAsync(session, It.IsAny<ReelBolt.WorkflowEngine.Services.Compositions.CompositionCaptureRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ISandboxSession, ReelBolt.WorkflowEngine.Services.Compositions.CompositionCaptureRequest, CancellationToken>((_, request, _) => captured = request)
            .ThrowsAsync(new InvalidOperationException("object store down"));

        ReactRemotionSandboxTools tool = Build(
            executionId, projectId, session, s3, new FakePresignedUrlService(), new FakeOutputVerifier(),
            workspaceCapture: capture.Object, organizationId: organizationId);

        string json = await tool.RenderVideoAndUploadToStorage("Main", OutputFileName);

        using JsonDocument document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue("a snapshot failure never takes back a finished render");
        captured.Should().NotBeNull();
        captured!.ExecutionId.Should().Be(executionId);
        captured.OrganizationId.Should().Be(organizationId);
        captured.CompositionId.Should().Be("Main");
        captured.RenderOutputStorageKey.Should().Be($"projects/{projectId}/outputFiles/{executionId}/{OutputFileName}");
        captured.Lineage.Should().BeNull("outside a revision a capture continues the execution's own lineage");
    }

    [Fact]
    public async Task Voiceover_audio_is_staged_from_a_presigned_get_of_the_key_the_line_carries()
    {
        Guid executionId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        string lineKey = $"projects/{projectId}/agentFiles/voiceover/lines/{new string('c', 64)}.wav";

        using ServiceProvider services = SeedVoiceoverExecution(
            executionId,
            projectId,
            ["Scene 0 voiceover"],
            [("vo0", "ok", lineKey)]);

        FakeRendererSession session = new(executionId) { Route = new SandboxPublishRoute(false, null, null) };
        Mock<IAmazonS3> s3 = new();

        ReactRemotionSandboxTools tool = Build(
            executionId,
            projectId,
            session,
            s3,
            new FakePresignedUrlService(),
            new FakeOutputVerifier(),
            services.GetRequiredService<IServiceScopeFactory>());

        string json = await tool.StageVoiceoverAudio();

        using JsonDocument document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("staged").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("voiceover/scene-0.wav");

        // The bytes moved through a presigned GET of the line's OWN object, not through a read of it
        // by the engine — and the engine never touched the object store itself.
        session.Stages.Should().ContainSingle();
        // ...into public/, the only folder staticFile() reads, while the composition is told the
        // staticFile() path above.
        session.Stages[0].Path.Should().Be("public/voiceover/scene-0.wav");
        session.Stages[0].Url.Should().Contain(Uri.EscapeDataString(lineKey));
        s3.Verify(
            x => x.GetObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task After_a_retry_the_author_stages_the_new_attempts_voiceover_not_the_superseded_one()
    {
        Guid executionId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        string oldKey = $"projects/{projectId}/agentFiles/voiceover/lines/{new string('a', 64)}.wav";
        string newKey = $"projects/{projectId}/agentFiles/voiceover/lines/{new string('b', 64)}.wav";

        using ServiceProvider services = SeedVoiceoverExecution(
            executionId,
            projectId,
            ["Scene 0 voiceover"],
            [("vo0", "ok", newKey)],
            supersededLines: [("vo0", "ok", oldKey)]);

        FakeRendererSession session = new(executionId) { Route = new SandboxPublishRoute(false, null, null) };
        ReactRemotionSandboxTools tool = Build(
            executionId,
            projectId,
            session,
            new Mock<IAmazonS3>(),
            new FakePresignedUrlService(),
            new FakeOutputVerifier(),
            services.GetRequiredService<IServiceScopeFactory>());

        await tool.StageVoiceoverAudio();

        session.Stages.Should().ContainSingle();
        session.Stages[0].Url.Should().Contain(Uri.EscapeDataString(newKey), "the retry's own narration is the one the film carries");
        session.Stages[0].Url.Should().NotContain(Uri.EscapeDataString(oldKey));
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private static ReactRemotionSandboxTools Build(
        Guid executionId,
        Guid projectId,
        FakeRendererSession session,
        Mock<IAmazonS3> s3,
        IPresignedUrlService presigner,
        IOutputVerifier verifier,
        IServiceScopeFactory? scopeFactory = null,
        ReelBolt.WorkflowEngine.Services.Compositions.ICompositionWorkspaceCapture? workspaceCapture = null,
        Guid? organizationId = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" })
            .Build();

        WorkflowExecutionContextAccessor accessor = new();
        accessor.BeginScope(executionId, projectId, "correlation", organizationId);

        return new ReactRemotionSandboxTools(
            Mock.Of<IHttpClientFactory>(),
            accessor,
            Mock.Of<IProjectFileWorkspace>(),
            s3.Object,
            configuration,
            NullLogger<ReactRemotionSandboxTools>.Instance,
            scopeFactory ?? Mock.Of<IServiceScopeFactory>(),
            mediaProbe: null,
            usageRecorder: null,
            videoToolRunner: null,
            videoEditingOptions: null,
            sandboxSessions: new FixedSessionFactory(session),
            presignedUrls: presigner,
            outputVerifier: verifier,
            workspaceCapture: workspaceCapture);
    }

    private sealed class FixedSessionFactory(ISandboxSession session) : ISandboxSessionFactory
    {
        public ISandboxSession Create(Guid executionId) => session;
    }

    /// <summary>
    /// A workspace session whose route, exec result and byte movement the test states. Everything the
    /// render path does NOT depend on answers the empty plausible thing, so a test that starts passing
    /// because of an unrelated call is visible immediately.
    /// </summary>
    private sealed class FakeRendererSession(Guid executionId) : ISandboxSession
    {
        public Guid ExecutionId { get; } = executionId;

        public SandboxPublishRoute Route { get; set; } = new(false, null, null);

        public List<SandboxPublishRequest> Publishes { get; } = [];

        public List<SandboxStageRequest> Stages { get; } = [];

        public SandboxTransfer PublishResult { get; set; } = new(1, null);

        /// <summary>The bytes a raw read of the workspace serves.</summary>
        public byte[] RawBytes { get; set; } = Encoding.ASCII.GetBytes("render");

        public Task<SandboxPublishRoute> ResolvePublishRouteAsync(CancellationToken cancellationToken)
            => Task.FromResult(Route);

        public Task<SandboxTransfer> PublishToUrlAsync(SandboxPublishRequest request, CancellationToken cancellationToken)
        {
            Publishes.Add(request);
            return Task.FromResult(PublishResult);
        }

        public Task<SandboxTransfer> StageFromUrlAsync(SandboxStageRequest request, CancellationToken cancellationToken)
        {
            Stages.Add(request);
            return Task.FromResult(new SandboxTransfer(4, null));
        }

        public Task<SandboxRawFile> OpenRawFileAsync(string path, int commandTimeoutSeconds, CancellationToken cancellationToken)
        {
            HttpClient client = new();
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(RawBytes)
            };
            return Task.FromResult(new SandboxRawFile(
                client, response, new MemoryStream(RawBytes), RawBytes.LongLength));
        }

        public Task<SandboxExecResult> ExecAsync(string command, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken cancellationToken)
            => Task.FromResult(new SandboxExecResult(
                Succeeded: true,
                Payload: new SandboxExecResponse("rendered", null),
                StatusCode: 0,
                RawBody: "{\"output\":\"rendered\"}",
                RawError: string.Empty,
                TransportError: null));

        public Task<SandboxInfo> EnsureAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SandboxInfo("s", ExecutionId.ToString(), "c", ".", DateTime.UtcNow, DateTime.UtcNow));

        public Task<SandboxInfo> GetAsync(CancellationToken cancellationToken) => EnsureAsync(cancellationToken);

        public Task<SandboxStatusResponse> StatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SandboxStatusResponse(true, true, true, true, "c", ".", DateTime.UtcNow, DateTime.UtcNow));

        public Task<IReadOnlyList<SandboxFileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SandboxFileEntry>>([]);

        public Task<SandboxTextFile> ReadTextAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(new SandboxTextFile(path, string.Empty));

        public Task<SandboxActionResult> WriteTextAsync(string path, string content, CancellationToken cancellationToken)
            => Task.FromResult(new SandboxActionResult(true));

        public Task WriteBytesAsync(string path, byte[] content, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<SandboxActionResult> DeletePathAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(new SandboxActionResult(true));

        public Task<SandboxExecResponse> InstallPackagesAsync(IReadOnlyList<string> packages, CancellationToken cancellationToken)
            => Task.FromResult(new SandboxExecResponse(null, "not available"));

        public Task<SandboxActionResult> CompleteAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SandboxActionResult(true));
    }

    /// <summary>The minimum a completed Voiceover + Scriptwriter pair needs to exist for the staging tool.</summary>
    private static ServiceProvider SeedVoiceoverExecution(
        Guid executionId,
        Guid projectId,
        string[] sceneVoiceovers,
        (string Id, string Status, string StorageKey)[] lines,
        (string Id, string Status, string StorageKey)[]? supersededLines = null)
    {
        ServiceCollection services = new();
        string database = Guid.NewGuid().ToString();
        services.AddDbContext<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext>(
            options => options.UseInMemoryDatabase(database));

        ServiceProvider provider = services.BuildServiceProvider();

        using IServiceScope scope = provider.CreateScope();
        ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext db =
            scope.ServiceProvider.GetRequiredService<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext>();

        db.WorkflowExecutions.Add(new WorkflowExecution { Id = executionId, ProjectId = projectId });

        AgentDefinition scriptwriter = new()
        {
            Id = Guid.NewGuid(),
            AgentType = AgentType.ScriptwriterAgent,
            GeneratesOutput = true
        };
        db.AgentDefinitions.Add(scriptwriter);

        WorkflowStep scriptwriterStep = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepType = StepType.Agent,
            AgentDefinitionId = scriptwriter.Id,
            AgentDefinition = scriptwriter,
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

        WorkflowStep voiceoverStep = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepType = StepType.Voiceover,
            StepOrder = 2
        };
        db.WorkflowSteps.Add(voiceoverStep);

        // A Retry keeps the replaced attempt's row on the same step, older and stamped superseded (Retry).
        if (supersededLines is not null)
        {
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = executionId,
                WorkflowStepId = voiceoverStep.Id,
                Status = StepStatus.Completed,
                ExecutedAt = DateTime.UtcNow.AddMinutes(-10),
                SupersededAt = DateTime.UtcNow.AddMinutes(-5),
                OutputJson = JsonSerializer.Serialize(new
                {
                    view = new
                    {
                        lines = supersededLines.Select(l => new { id = l.Id, status = l.Status, storageKey = l.StorageKey }).ToArray()
                    }
                })
            });
        }

        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = executionId,
            WorkflowStepId = voiceoverStep.Id,
            Status = StepStatus.Completed,
            OutputJson = JsonSerializer.Serialize(new
            {
                view = new
                {
                    lines = lines.Select(l => new { id = l.Id, status = l.Status, storageKey = l.StorageKey }).ToArray()
                }
            })
        });

        db.SaveChanges();
        return provider;
    }
}