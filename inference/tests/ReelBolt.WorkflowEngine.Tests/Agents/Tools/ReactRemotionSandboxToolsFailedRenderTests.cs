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
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>
/// T-009: a failed in-sandbox render is named and bounded.
///
/// <para>
/// Before this change <c>RenderVideoAndUploadToStorage</c> returned <c>{success:false, error,
/// outputTail, guidance}</c> to the model and nothing carried the failure to the step: an Author
/// that then produced no <c>outputStorageKey</c> ended with <c>FailureCode == null</c>,
/// <c>ErrorDetails = "Author step did not produce a rendered media artifact (missing
/// outputStorageKey)."</c> and the run showed <c>"Something went wrong on our side while running
/// this step."</c> (STEP_INTERNAL_ERROR). The tests below pin the new sentence, the
/// PlatformInternal class (refund), and the per-composition failed-render cap.
/// </para>
/// </summary>
public class ReactRemotionSandboxToolsFailedRenderTests
{
    private const string OutputFileName = "promo.mp4";
    private const string ExitStatusOne = "execution failed: exit status 1";

    private static Dictionary<string, string> Sources() => new()
    {
        ["src/root.tsx"] = "import {Composition} from 'remotion';\nexport const Root = () => null;\n"
    };

    // ------------------------------------------------------------------
    // The model-facing tool result is untouched; the step now learns why.
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_exit_one_render_failure_is_recorded_with_the_exit_code()
    {
        RenderHarness h = new(Sources(), failure: new RenderFailure(ExitStatusOne, "Error: could not render"));
        RenderFailureLedger ledger = new();

        JsonElement result;
        using (ledger.Publish())
            result = await h.RenderAsync();

        // The model still gets the actionable tool error it always got…
        result.GetProperty("success").GetBoolean().Should().BeFalse();

        // …and the step is told what actually happened.
        ledger.FailedCount("Main").Should().Be(1);
        ledger.LastMessage.Should().Contain("exited with code 1");
        ledger.LastMessage.Should().NotContain("Something went wrong on our side");
        ledger.TerminalRefused.Should().BeFalse();
    }

    [Fact]
    public async Task An_ordinary_exit_one_failure_does_not_claim_the_renderer_was_killed()
    {
        RenderHarness h = new(
            Sources(),
            failure: new RenderFailure(ExitStatusOne, "Error: Element type is invalid: expected a string but got: object"));
        RenderFailureLedger ledger = new();

        using (ledger.Publish())
            await h.RenderAsync();

        ledger.LastMessage.Should().Contain("exited with code 1");
        ledger.LastMessage.Should().NotContain("killed");
        ledger.LastMessage.Should().NotContain("out of memory");
        ledger.LastMessage.Should().NotContain("Something went wrong on our side");
    }

    [Fact]
    public async Task A_sigkill_or_ENOMEM_render_says_the_renderer_was_killed_likely_out_of_memory()
    {
        RenderHarness h = new(
            Sources(),
            failure: new RenderFailure(ExitStatusOne, "signal: killed\nSIGKILL\n[ 123.4] constraint_memcg invoked OOM-killer"));

        RenderFailureLedger ledger = new();
        using (ledger.Publish())
            await h.RenderAsync();

        ledger.LastMessage.Should().Contain("exited with code 1");
        ledger.LastMessage.Should().Contain("killed").And.Contain("out of memory");
        ledger.LastMessage.Should().NotContain("Something went wrong on our side");
    }

    [Theory]
    [InlineData("SIGKILL")]
    [InlineData("Killed")]
    [InlineData("ENOMEM")]
    [InlineData("out of memory")]
    [InlineData("constraint_memcg")]
    public void Every_kill_marker_the_producer_may_write_is_recognised(string marker)
    {
        (int? exitCode, bool killed) = ReactRemotionSandboxTools.ReadRenderFailureSignals(
            ExitStatusOne, "Error: renderer stopped\n" + marker);

        exitCode.Should().Be(1);
        killed.Should().BeTrue();
        ReactRemotionSandboxTools.RenderFailureUserMessage(exitCode, killed)
            .Should().Contain("killed (likely out of memory)");
    }

    // ------------------------------------------------------------------
    // The per-composition cap: two failures of the SAME composition by default,
    // then the next render of it never runs.
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_cap_refuses_the_third_render_without_executing_it()
    {
        ReactRemotionSandboxTools.DefaultMaxFailedRendersPerStep.Should().Be(2,
            "Compute:Render:MaxFailedRendersPerStep ships at 2");
        RenderHarness h = new(Sources(), failure: new RenderFailure(ExitStatusOne, "Error: nope"));
        RenderFailureLedger ledger = new();

        // One ledger is shared by every attempt of a step (the same StepExecutionContext), and one
        // Publish scope is one execution-context scope: attempt 1, attempt 2, then attempt 3.
        using (ledger.Publish())
            (await h.RenderAsync()).GetProperty("success").GetBoolean().Should().BeFalse();

        using (ledger.Publish())
            (await h.RenderAsync()).GetProperty("success").GetBoolean().Should().BeFalse();

        using (ledger.Publish())
        {
            JsonElement third = await h.RenderAsync();

            third.GetProperty("ok").GetBoolean().Should().BeFalse();
            third.GetProperty("terminal").GetBoolean().Should().BeTrue();
            third.GetProperty("code").GetString().Should().Be("RENDER_FAILED");
            third.GetProperty("error").GetString().Should().Contain("exited with code 1");
        }

        h.RenderExecutions.Should().Be(2, "the refused call must not reach the sandbox");
        ledger.FailedCount("Main").Should().Be(2);
        ledger.TerminalRefused.Should().BeTrue();
        ledger.TerminalRefusedFor("Main").Should().BeTrue();
    }

    [Fact]
    public async Task Two_different_compositions_failing_once_each_do_not_trip_the_cap()
    {
        RenderHarness h = new(Sources(), failure: new RenderFailure(ExitStatusOne, "Error: nope"));
        RenderFailureLedger ledger = new();

        using (ledger.Publish())
        {
            // One failure for each of two different overlay compositions: neither reaches the cap.
            (await h.RenderAsync("Main", "promo.mp4")).GetProperty("success").GetBoolean().Should().BeFalse();
            (await h.RenderAsync("LowerThird", "lower-third.mp4")).GetProperty("success").GetBoolean().Should().BeFalse();

            ledger.FailedCount("Main").Should().Be(1);
            ledger.FailedCount("LowerThird").Should().Be(1);
            ledger.TerminalRefused.Should().BeFalse();

            // Each composition still gets another attempt: its own second failure executes…
            (await h.RenderAsync("Main", "promo.mp4")).GetProperty("success").GetBoolean().Should().BeFalse();
            ledger.FailedCount("Main").Should().Be(2);

            // …and only then is that composition's next render refused, while LowerThird can still run.
            (await h.RenderAsync("Main", "promo.mp4")).GetProperty("terminal").GetBoolean().Should().BeTrue();
            (await h.RenderAsync("LowerThird", "lower-third.mp4")).GetProperty("success").GetBoolean().Should().BeFalse();
        }

        // Main was executed twice, LowerThird twice; the refused Main call never reached the sandbox.
        h.RenderExecutions.Should().Be(4);
        ledger.FailedCount("LowerThird").Should().Be(2);
        ledger.TerminalRefused.Should().BeTrue();
        ledger.TerminalRefusedFor("Main").Should().BeTrue();
        ledger.TerminalRefusedFor("LowerThird").Should().BeFalse("its cap was reached but no call was refused");
    }

    [Fact]
    public async Task The_cap_is_configurable()
    {
        RenderHarness h = new(
            Sources(),
            failure: new RenderFailure(ExitStatusOne, "Error: nope"),
            maxFailedRendersPerStep: 1);
        RenderFailureLedger ledger = new();

        using (ledger.Publish())
        {
            await h.RenderAsync();
            JsonElement second = await h.RenderAsync();
            second.GetProperty("terminal").GetBoolean().Should().BeTrue();
        }

        h.RenderExecutions.Should().Be(1);
    }

    // ------------------------------------------------------------------
    // A successful render is untouched, and records nothing.
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_successful_render_is_unchanged_and_records_no_failure()
    {
        RenderHarness h = new(Sources());
        RenderFailureLedger ledger = new();

        JsonElement result;
        using (ledger.Publish())
            result = await h.RenderAsync();

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("storageKey").GetString()
            .Should().Be($"projects/{h.ProjectId}/outputFiles/{h.ExecutionId}/{OutputFileName}");
        h.Uploads.Should().Be(1);
        ledger.HasFailed.Should().BeFalse();
        ledger.TerminalRefused.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // AgentStepExecutor: no artifact + a recorded render failure = RENDER_FAILED.
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_author_that_renders_nothing_after_a_failed_render_fails_with_RENDER_FAILED()
    {
        RenderHarness h = new(Sources(), failure: new RenderFailure(ExitStatusOne, "Error: could not render"));
        StepExecutionContext context = h.AuthorContext();

        IReelBoltAgent agent = h.AgentThatCallsRenderOnce("The film could not be rendered.");

        AgentStepExecutor executor = new(
            Registry(agent),
            h.Accessor,
            Mock.Of<IMotionGraphicsPlacementAnnotator>(),
            EmptyConfig(),
            Mock.Of<IDecisionGate>(),
            NullLogger<AgentStepExecutor>.Instance);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be(FailureMessages.RenderFailedCode);
        result.FailureClass.Should().Be(FailureClass.PlatformInternal, "a render failure is refunded");
        result.ErrorDetails.Should().Contain("exited with code 1");
        result.UserMessage.Should().Contain("exited with code 1");
        result.ErrorDetails.Should().NotContain("Something went wrong on our side");
    }

    [Fact]
    public async Task A_terminal_cap_refusal_fails_the_step_even_when_the_model_answered()
    {
        RenderHarness h = new(Sources(), failure: new RenderFailure(ExitStatusOne, "Error: could not render"));
        StepExecutionContext context = h.AuthorContext();

        // Two failed renders of the Author's master composition from earlier attempts: this attempt's
        // first render must be refused without executing, and a step left with no film must fail
        // with RENDER_FAILED.
        context.RenderFailures.RecordFailure("Main", "The video renderer exited with code 1. This was on our side, so credits for this step were refunded; run it again.");
        context.RenderFailures.RecordFailure("Main", "The video renderer exited with code 1. This was on our side, so credits for this step were refunded; run it again.");

        AgentStepExecutor executor = new(
            Registry(h.AgentThatCallsRenderOnce("I finished the film.")),
            h.Accessor,
            Mock.Of<IMotionGraphicsPlacementAnnotator>(),
            EmptyConfig(),
            Mock.Of<IDecisionGate>(),
            NullLogger<AgentStepExecutor>.Instance);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be(FailureMessages.RenderFailedCode);
        result.FailureClass.Should().Be(FailureClass.PlatformInternal);
        result.ErrorDetails.Should().Contain("exited with code 1");
        h.RenderExecutions.Should().Be(0, "the cap refuses the call before it reaches the sandbox");
        context.RenderFailures.TerminalRefused.Should().BeTrue();
    }

    [Fact]
    public async Task A_terminal_refusal_does_not_fail_a_step_that_already_has_its_artifact()
    {
        RenderHarness h = new(Sources(), failure: new RenderFailure(ExitStatusOne, "Error: could not render"));
        StepExecutionContext context = h.PlannerContext();
        string artifactStorageKey = h.ArtifactStorageKey();

        // The step already rendered one overlay successfully, and a DIFFERENT composition of the
        // same planner step has hit its own cap. The terminal refusal must not fail the step.
        context.RenderFailures.RecordFailure("Overlay", "The video renderer exited with code 1. This was on our side, so credits for this step were refunded; run it again.");
        context.RenderFailures.RecordFailure("Overlay", "The video renderer exited with code 1. This was on our side, so credits for this step were refunded; run it again.");

        AgentStepExecutor executor = new(
            Registry(h.AgentThatSetsArtifactThenRendersCapped(artifactStorageKey, "Overlay")),
            h.Accessor,
            Mock.Of<IMotionGraphicsPlacementAnnotator>(),
            EmptyConfig(),
            Mock.Of<IDecisionGate>(),
            NullLogger<AgentStepExecutor>.Instance);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, "the step has its artifact and must finish with it");
        result.FailureCode.Should().BeNull();
        result.OutputStorageKey.Should().Be(artifactStorageKey);
        h.RenderExecutions.Should().Be(0, "the capped composition's render never reached the sandbox");
        context.RenderFailures.TerminalRefused.Should().BeTrue();
    }

    [Fact]
    public async Task An_author_that_produces_no_artifact_without_any_render_failure_keeps_the_existing_failure()
    {
        RenderHarness h = new(Sources());   // renders start out successful if attempted; this one never is
        StepExecutionContext context = h.AuthorContext();

        IReelBoltAgent agent = AgentWithoutRendering("No film.");

        AgentStepExecutor executor = new(
            Registry(agent),
            h.Accessor,
            Mock.Of<IMotionGraphicsPlacementAnnotator>(),
            EmptyConfig(),
            Mock.Of<IDecisionGate>(),
            NullLogger<AgentStepExecutor>.Instance);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().BeNull();
        result.ErrorDetails.Should().Be(AgentStepExecutor.AuthorNeverRenderedMessage);
    }

    private static IReelBoltAgent AgentWithoutRendering(string output)
    {
        var agent = new Mock<IReelBoltAgent>();
        agent.Setup(a => a.Name).Returns("Author");
        agent.Setup(a => a.AgentType).Returns(AgentType.AuthorAgent);
        agent.Setup(a => a.Tools).Returns(Array.Empty<AIFunction>());
        agent.Setup(a => a.OutputSchemaType).Returns((Type?)null);
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = output, Success = true });
        return agent.Object;
    }

    // ------------------------------------------------------------------
    // FailureMessages / FailureClassifier wiring.
    // ------------------------------------------------------------------

    [Fact]
    public void The_codes_run_message_is_the_specific_sentence_not_the_generic_one()
    {
        FailureClassifier.ForCode("RENDER_FAILED").Should().Be(FailureClass.PlatformInternal);

        // What the user saw today instead: the generic sentence of STEP_INTERNAL_ERROR.
        FailureMessages.UserMessage(FailureMessages.StepInternalErrorCode).Should().Contain("Something went wrong on our side");

        FailureMessages.UserMessage(FailureMessages.RenderFailedCode).Should().NotContain("Something went wrong on our side");

        string run = FailureMessages.RunMessage(
            new InvalidOperationException("Step 5 (Agent) failed after 3 attempts. Last error: boom"),
            FailureClass.PlatformInternal,
            FailureMessages.RenderFailedCode,
            "The video renderer exited with code 1. This was on our side, so credits for this step were refunded; run it again.");

        run.Should().Contain("exited with code 1");
        run.Should().NotContain("Something went wrong on our side");
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private sealed record RenderFailure(string Error, string Output);

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static IAgentRegistry Registry(IReelBoltAgent agent)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent);
        return registry.Object;
    }

    private sealed class RenderHarness
    {
        private readonly WorkflowEngineDbContext _db;
        private readonly ReactRemotionSandboxTools _tool;
        private readonly ScriptedSession _session;
        private readonly Guid _executionId = Guid.NewGuid();
        private readonly Guid _projectId = Guid.NewGuid();

        public Guid ExecutionId => _executionId;
        public Guid ProjectId => _projectId;
        public WorkflowExecutionContextAccessor Accessor { get; } = new();
        public int Uploads { get; private set; }
        public int RenderExecutions => _session.RenderExecutions;

        public RenderHarness(
            Dictionary<string, string> files,
            RenderFailure? failure = null,
            int? maxFailedRendersPerStep = null)
        {
            _db = new WorkflowEngineDbContext(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
            _db.WorkflowExecutions.Add(new WorkflowExecution { Id = _executionId, ProjectId = _projectId });
            _db.SaveChanges();

            Mock<IAmazonS3> s3 = new();
            s3.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutObjectRequest, CancellationToken>((request, _) =>
                {
                    if (request.Key.EndsWith(OutputFileName, StringComparison.Ordinal))
                        Uploads++;
                })
                .ReturnsAsync(new PutObjectResponse());

            Accessor.BeginScope(_executionId, _projectId, "correlation");
            _session = new ScriptedSession(_executionId, files, failure);

            Dictionary<string, string?> settings = new() { ["MinIO:BucketName"] = "reelforge" };
            if (maxFailedRendersPerStep is { } cap)
                settings[ReactRemotionSandboxTools.MaxFailedRendersPerStepKey] = cap.ToString(System.Globalization.CultureInfo.InvariantCulture);

            _tool = new ReactRemotionSandboxTools(
                Mock.Of<IHttpClientFactory>(),
                Accessor,
                Mock.Of<IProjectFileWorkspace>(),
                s3.Object,
                new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
                NullLogger<ReactRemotionSandboxTools>.Instance,
                new ReelBolt.WorkflowEngine.Tests.FakeScopeFactory(_db),
                mediaProbe: null,
                usageRecorder: null,
                videoToolRunner: null,
                videoEditingOptions: null,
                sandboxSessions: new FixedSessionFactory(_session));
        }

        public async Task<JsonElement> RenderAsync(string compositionId = "Main", string fileName = OutputFileName)
        {
            string json = await _tool.RenderVideoAndUploadToStorage(compositionId, fileName);
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        /// <summary>The storage key a successful render of <see cref="OutputFileName"/> gets for this harness's execution.</summary>
        public string ArtifactStorageKey() =>
            $"projects/{_projectId}/outputFiles/{_executionId}/{OutputFileName}";

        /// <summary>An AuthorAgent step context wired to this harness's execution.</summary>
        public StepExecutionContext AuthorContext() => Context(AgentType.AuthorAgent, "Author");

        /// <summary>A motion-graphics planner step context (renders overlay videos, keeps its own JSON output).</summary>
        public StepExecutionContext PlannerContext() => Context(AgentType.MotionGraphicsPlanner, "MotionGraphicsPlanner");

        private StepExecutionContext Context(AgentType agentType, string agentName)
        {
            WorkflowStep step = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                AgentDefinitionId = Guid.NewGuid(),
                StepOrder = 7,
                StepType = StepType.Agent,
                AgentInputContextMode = AgentInputContextMode.FullWorkflow,
                AgentDefinition = new AgentDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = agentName,
                    AgentType = agentType,
                    Description = string.Empty,
                    SystemPrompt = string.Empty,
                    IsBuiltIn = true
                }
            };

            return new StepExecutionContext
            {
                Execution = new WorkflowExecution { Id = _executionId, ProjectId = _projectId },
                Step = step,
                AllSteps = [step],
                AccumulatedOutput = string.Empty,
                StepOutputHistory = [],
                CurrentStepIndex = 0,
                IterationCount = 0,
                CorrelationId = "correlation",
                CancellationToken = CancellationToken.None
            };
        }

        /// <summary>An agent whose run calls the real render tool once (the tool is the one under test).</summary>
        public IReelBoltAgent AgentThatCallsRenderOnce(string output)
        {
            var agent = new Mock<IReelBoltAgent>();
            agent.Setup(a => a.Name).Returns("Author");
            agent.Setup(a => a.AgentType).Returns(AgentType.AuthorAgent);
            agent.Setup(a => a.Tools).Returns(Array.Empty<AIFunction>());
            agent.Setup(a => a.OutputSchemaType).Returns((Type?)null);
            agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .Returns(async (string _, Guid? _, CancellationToken ct) =>
                {
                    await _tool.RenderVideoAndUploadToStorage("Main", OutputFileName, cancellationToken: ct);
                    return new AgentRunResult { Output = output, Success = true };
                });
            return agent.Object;
        }

        /// <summary>
        /// A planner agent that already has a rendered artifact and then asks for one more overlay
        /// of a composition that has hit its cap.
        /// </summary>
        public IReelBoltAgent AgentThatSetsArtifactThenRendersCapped(string artifactStorageKey, string compositionId)
        {
            var agent = new Mock<IReelBoltAgent>();
            agent.Setup(a => a.Name).Returns("MotionGraphicsPlanner");
            agent.Setup(a => a.AgentType).Returns(AgentType.MotionGraphicsPlanner);
            agent.Setup(a => a.Tools).Returns(Array.Empty<AIFunction>());
            agent.Setup(a => a.OutputSchemaType).Returns((Type?)null);
            agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .Returns(async (string _, Guid? _, CancellationToken ct) =>
                {
                    Accessor.Current!.PendingOutputStorageKey = artifactStorageKey;
                    await _tool.RenderVideoAndUploadToStorage(compositionId, "overlay.mp4", cancellationToken: ct);
                    return new AgentRunResult { Output = "{\"overlays\":[]}", Success = true };
                });
            return agent.Object;
        }
    }

    private sealed class FixedSessionFactory(ISandboxSession session) : ISandboxSessionFactory
    {
        public ISandboxSession Create(Guid executionId) => session;
    }

    /// <summary>A local workspace over an in-memory file tree. Renders succeed, or fail with a scripted reason.</summary>
    private sealed class ScriptedSession(Guid executionId, Dictionary<string, string> files, RenderFailure? failure) : ISandboxSession
    {
        private static readonly byte[] RenderBytes = Encoding.ASCII.GetBytes("render");
        private int _renderExecutions;

        public Guid ExecutionId { get; } = executionId;
        public int RenderExecutions => Volatile.Read(ref _renderExecutions);

        public Task<SandboxExecResult> ExecAsync(string command, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken cancellationToken)
        {
            if (failure is not null && command == "npx" && args.Count >= 2 && args[0] == "remotion" && args[1] == "render")
            {
                Interlocked.Increment(ref _renderExecutions);
                return Task.FromResult(new SandboxExecResult(
                    Succeeded: false,
                    Payload: new SandboxExecResponse(failure.Output, failure.Error),
                    StatusCode: 500,
                    RawBody: "{}",
                    RawError: failure.Error,
                    TransportError: null));
            }

            return Task.FromResult(new SandboxExecResult(
                Succeeded: true,
                Payload: new SandboxExecResponse(string.Empty, null),
                StatusCode: 200,
                RawBody: "{\"output\":\"\"}",
                RawError: "{\"output\":\"\"}",
                TransportError: null));
        }

        public Task<IReadOnlyList<SandboxFileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken)
        {
            string prefix = path.TrimEnd('/') + "/";
            List<SandboxFileEntry> entries = files.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Select(k => k[prefix.Length..])
                .Select(rest => rest.Contains('/')
                    ? new SandboxFileEntry(rest[..rest.IndexOf('/')], true, 0, DateTime.UnixEpoch)
                    : new SandboxFileEntry(rest, false, Encoding.UTF8.GetByteCount(files[prefix + rest]), DateTime.UnixEpoch))
                .DistinctBy(e => e.Name)
                .ToList();
            return Task.FromResult<IReadOnlyList<SandboxFileEntry>>(entries);
        }

        public Task<SandboxTextFile> ReadTextAsync(string path, CancellationToken cancellationToken) =>
            files.TryGetValue(path, out string? text)
                ? Task.FromResult(new SandboxTextFile(path, text))
                : throw new SandboxRequestFailedException(HttpStatusCode.NotFound, "not found");

        public Task<SandboxPublishRoute> ResolvePublishRouteAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SandboxPublishRoute(false, null, null));

        public Task<SandboxTransfer> PublishToUrlAsync(SandboxPublishRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new SandboxTransfer(1, null));

        public Task<SandboxTransfer> StageFromUrlAsync(SandboxStageRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new SandboxTransfer(4, null));

        public Task<SandboxRawFile> OpenRawFileAsync(string path, int commandTimeoutSeconds, CancellationToken cancellationToken)
        {
            HttpClient client = new();
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(RenderBytes) };
            return Task.FromResult(new SandboxRawFile(client, response, new MemoryStream(RenderBytes), RenderBytes.LongLength));
        }

        public Task<SandboxInfo> EnsureAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SandboxInfo("s", ExecutionId.ToString(), "c", ".", DateTime.UtcNow, DateTime.UtcNow));

        public Task<SandboxInfo> GetAsync(CancellationToken cancellationToken) => EnsureAsync(cancellationToken);

        public Task<SandboxStatusResponse> StatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SandboxStatusResponse(true, true, true, true, "c", ".", DateTime.UtcNow, DateTime.UtcNow));

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
}
