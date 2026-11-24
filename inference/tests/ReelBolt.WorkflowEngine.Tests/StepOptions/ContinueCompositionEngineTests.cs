using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Compositions;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Tests.Compositions;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.StepOptions;

/// <summary>
/// Run-time half of "continue from run X's composition": the Author step's sandbox is restored from the
/// resolved workspace version BEFORE the agent runs, and a source outside the run's own project fails
/// the step as <c>COMPOSITION_SOURCE_NOT_FOUND</c> without restoring anything.
/// </summary>
public sealed class ContinueCompositionEngineTests : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Project = Guid.NewGuid();

    private readonly CompositionHarness _h = new();
    private readonly DictionarySessionFactory _sessions = new();

    public void Dispose() => _h.Dispose();

    // --- resolution + restore -----------------------------------------------------------------------

    [Fact]
    public async Task An_execution_id_restores_the_latest_version_of_that_runs_workspace_before_the_agent_runs()
    {
        (Guid source, CompositionWorkspaceVersion v1) = await CapturedRunAsync(Project, "v1");
        CompositionWorkspaceVersion v2 = await CaptureAgainAsync(source, Project, "v2");
        Recorder agent = new();
        StepExecutionContext context = await NewRunAsync(Project, $$"""{"executionId":"{{source}}"}""");

        StepExecutionResult result = await Executor(agent).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        agent.ImportsSeenAtRun.Should().Be(1, "the restore happens before the agent is handed the prompt");
        FakeWorkspaceSession sandbox = (FakeWorkspaceSession)_sessions.Sessions[context.Execution.Id];
        Encoding.UTF8.GetString(sandbox.Files["src/Intro.tsx"]).Should().Be("v2", $"the latest version ({v2.VersionNumber}) is restored, not {v1.VersionNumber}");
        agent.Prompt.Should().StartWith(CompositionContinuation.Note);
    }

    [Fact]
    public async Task A_version_id_restores_exactly_that_version()
    {
        (Guid source, CompositionWorkspaceVersion v1) = await CapturedRunAsync(Project, "v1");
        await CaptureAgainAsync(source, Project, "v2");
        Recorder agent = new();
        StepExecutionContext context = await NewRunAsync(Project, $$"""{"versionId":"{{v1.Id}}"}""");

        StepExecutionResult result = await Executor(agent).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        FakeWorkspaceSession sandbox = (FakeWorkspaceSession)_sessions.Sessions[context.Execution.Id];
        Encoding.UTF8.GetString(sandbox.Files["src/Intro.tsx"]).Should().Be("v1");
    }

    [Theory]
    [InlineData("execution")]
    [InlineData("version")]
    public async Task A_source_from_another_project_fails_without_restoring_and_never_runs_the_agent(string kind)
    {
        (Guid source, CompositionWorkspaceVersion version) = await CapturedRunAsync(Guid.NewGuid(), "theirs");
        Recorder agent = new();
        string json = kind == "execution" ? $$"""{"executionId":"{{source}}"}""" : $$"""{"versionId":"{{version.Id}}"}""";
        StepExecutionContext context = await NewRunAsync(Project, json);

        StepExecutionResult result = await Executor(agent).ExecuteAsync(context);

        AssertSourceNotFound(result);
        agent.Ran.Should().BeFalse();
        _sessions.Sessions.Should().NotContainKey(context.Execution.Id, "no sandbox is touched for a refused source");
    }

    /// <summary>
    /// T-014, fail closed: a version from a sibling project of the SAME organization — named directly,
    /// through its run, or through a version row whose own project column was forged to ours — is refused
    /// before the archive is ever downloaded or anything is imported into a sandbox. The own-project case
    /// is the control proving the spy sees a real restore.
    /// </summary>
    [Theory]
    [InlineData("own", 1)]
    [InlineData("sibling-version", 0)]
    [InlineData("sibling-execution", 0)]
    [InlineData("forged-version-project", 0)]
    public async Task A_sibling_projects_version_never_reaches_the_archive_download_or_the_sandbox_import(string source, int expectedDownloads)
    {
        Guid sibling = Guid.NewGuid();
        (Guid run, CompositionWorkspaceVersion version) = await CapturedRunAsync(source == "own" ? Project : sibling, "theirs");
        if (source == "forged-version-project")
        {
            using WorkflowEngineDbContext db = _h.Db();
            CompositionWorkspaceVersion row = await db.CompositionWorkspaceVersions.SingleAsync(v => v.Id == version.Id);
            row.ProjectId = Project; // the workspace still belongs to the sibling project
            await db.SaveChangesAsync();
        }

        int downloads = 0;
        Mock<ICompositionWorkspaceStore> store = new();
        store.Setup(s => s.DownloadArchiveAsync(It.IsAny<CompositionWorkspaceVersion>(), It.IsAny<CancellationToken>()))
            .Returns<CompositionWorkspaceVersion, CancellationToken>((v, ct) => { downloads++; return _h.Store.DownloadArchiveAsync(v, ct); });
        var restorer = new CompositionWorkspaceRestorer(store.Object, _h.Configuration, NullLogger<CompositionWorkspaceRestorer>.Instance);
        string json = source == "sibling-execution" ? $$"""{"executionId":"{{run}}"}""" : $$"""{"versionId":"{{version.Id}}"}""";
        StepExecutionContext context = await NewRunAsync(Project, json);

        StepExecutionResult result = await Executor(new Recorder(), restorer).ExecuteAsync(context);

        downloads.Should().Be(expectedDownloads);
        if (expectedDownloads == 0)
        {
            AssertSourceNotFound(result);
            _sessions.Sessions.Should().NotContainKey(context.Execution.Id, "no sandbox is created, so nothing is imported");
        }
        else
        {
            result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
            ((FakeWorkspaceSession)_sessions.Sessions[context.Execution.Id]).Imports.Should().Be(1);
        }
    }

    [Fact]
    public async Task A_missing_source_fails_the_same_way_as_a_foreign_one()
    {
        Recorder agent = new();
        StepExecutionContext context = await NewRunAsync(Project, $$"""{"executionId":"{{Guid.NewGuid()}}"}""");

        StepExecutionResult result = await Executor(agent).ExecuteAsync(context);

        AssertSourceNotFound(result);
        agent.Ran.Should().BeFalse();
    }

    [Fact]
    public async Task A_run_of_the_same_project_with_no_captured_composition_is_not_found()
    {
        Guid source = Guid.NewGuid();
        await SeedRunAsync(source, Project);
        StepExecutionContext context = await NewRunAsync(Project, $$"""{"executionId":"{{source}}"}""");

        AssertSourceNotFound(await Executor(new Recorder()).ExecuteAsync(context));
    }

    [Fact]
    public async Task A_restore_that_fails_is_a_platform_failure_and_the_agent_never_runs()
    {
        (Guid source, _) = await CapturedRunAsync(Project, "v1");
        _h.Objects.Clear(); // the archive is gone from the object store
        Recorder agent = new();
        StepExecutionContext context = await NewRunAsync(Project, $$"""{"executionId":"{{source}}"}""");

        StepExecutionResult result = await Executor(agent).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be(CompositionContinuation.RestoreFailedCode);
        result.FailureClass.Should().Be(FailureClass.PlatformInternal);
        agent.Ran.Should().BeFalse();
    }

    [Fact]
    public async Task An_agent_without_sandbox_authoring_is_refused_at_run_time_too()
    {
        (Guid source, _) = await CapturedRunAsync(Project, "v1");
        StepExecutionContext context = await NewRunAsync(Project, $$"""{"executionId":"{{source}}"}""", AgentType.ReviewAgent);

        StepExecutionResult result = await Executor(new Recorder(AgentType.ReviewAgent)).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be(CompositionContinuation.RequiresAuthoringCode);
        _sessions.Sessions.Should().NotContainKey(context.Execution.Id);
    }

    [Fact]
    public async Task A_step_without_the_option_is_untouched()
    {
        Recorder agent = new();
        StepExecutionContext context = await NewRunAsync(Project, continueJson: null);

        StepExecutionResult result = await Executor(agent).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        agent.Prompt.Should().NotContain("[CONTINUING COMPOSITION]");
        _sessions.Sessions.Should().BeEmpty();
    }

    // --- lineage --------------------------------------------------------------------------------------

    [Fact]
    public async Task Later_captures_form_the_new_runs_own_workspace_and_leave_the_source_lineage_alone()
    {
        (Guid source, CompositionWorkspaceVersion v1) = await CapturedRunAsync(Project, "v1");
        StepExecutionContext context = await NewRunAsync(Project, $$"""{"executionId":"{{source}}"}""");
        await Executor(new Recorder()).ExecuteAsync(context);

        // The new run's render capture (what ReactRemotionSandboxTools does after a render) lands in
        // the run's OWN origin workspace, so revisions on the new run work.
        FakeWorkspaceSession sandbox = (FakeWorkspaceSession)_sessions.Sessions[context.Execution.Id];
        sandbox.Exists = true;
        CompositionCaptureResult captured = await _h.Capture.CaptureAsync(sandbox, new CompositionCaptureRequest
        {
            ProjectId = Project,
            OrganizationId = Org,
            ExecutionId = context.Execution.Id,
        }, CancellationToken.None);

        using WorkflowEngineDbContext db = _h.Db();
        CompositionWorkspace own = await db.CompositionWorkspaces.SingleAsync(w => w.OriginExecutionId == context.Execution.Id);
        captured.Version!.WorkspaceId.Should().Be(own.Id);
        (await db.CompositionWorkspaceVersions.CountAsync(v => v.WorkspaceId == v1.WorkspaceId))
            .Should().Be(1, "the source run's lineage is never modified");
    }

    [Fact]
    public async Task A_second_pass_of_the_same_run_keeps_its_own_work_instead_of_restoring_the_source_again()
    {
        (Guid source, _) = await CapturedRunAsync(Project, "v1");
        StepExecutionContext context = await NewRunAsync(Project, $$"""{"executionId":"{{source}}"}""");
        await Executor(new Recorder()).ExecuteAsync(context);
        FakeWorkspaceSession sandbox = (FakeWorkspaceSession)_sessions.Sessions[context.Execution.Id];
        sandbox.Exists = true;
        sandbox.Files["src/Intro.tsx"] = Encoding.UTF8.GetBytes("edited by pass one");
        await _h.Capture.CaptureAsync(sandbox, new CompositionCaptureRequest
        {
            ProjectId = Project,
            OrganizationId = Org,
            ExecutionId = context.Execution.Id
        }, CancellationToken.None);

        Recorder second = new();
        StepExecutionResult result = await Executor(second).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        sandbox.Imports.Should().Be(1, "a review-loop pass must not wipe this run's own edits");
        Encoding.UTF8.GetString(sandbox.Files["src/Intro.tsx"]).Should().Be("edited by pass one");
        second.Prompt.Should().StartWith(CompositionContinuation.Note);
    }

    // ------------------------------------------------------------------------------------------------

    private static void AssertSourceNotFound(StepExecutionResult result)
    {
        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be(CompositionContinuation.SourceNotFoundCode);
        result.FailureClass.Should().Be(FailureClass.UserInput);
        result.Output.Should().Contain(CompositionContinuation.SourceNotFoundCode);
    }

    private sealed class Recorder(AgentType type = AgentType.AuthorAgent) : IReelBoltAgent
    {
        public Func<int>? ImportsProbe { get; set; }
        public int? ImportsSeenAtRun { get; private set; }
        public string? Prompt { get; private set; }
        public bool Ran { get; private set; }
        public Guid? AgentId => null;
        public string Name => type.ToString();
        public string Description => "test";
        public string SystemPrompt => string.Empty;
        public AgentType AgentType => type;
        public IReadOnlyList<Microsoft.Extensions.AI.AIFunction> Tools => [];
        public string? OutputSchemaJson => null;
        public Type? OutputSchemaType => null;

        public Task<AgentRunResult> RunAsync(string prompt, Guid? agentDefinitionId = null, CancellationToken ct = default)
        {
            Ran = true;
            Prompt = prompt;
            ImportsSeenAtRun = ImportsProbe?.Invoke();
            return Task.FromResult(new AgentRunResult { Output = "{\"ok\":true}", Success = true, TokensUsed = 1 });
        }
    }

    private AgentStepExecutor Executor(Recorder agent, ICompositionWorkspaceRestorer? restorer = null)
    {
        agent.ImportsProbe = () => _sessions.Sessions.Values.OfType<FakeWorkspaceSession>().Sum(s => s.Imports);

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<ICompositionWorkspaceRestorer>(restorer ?? _h.Restorer);
        services.AddSingleton<ISandboxSessionFactory>(_sessions);
        services.AddScoped(_ => _h.Db()); // the harness's in-memory engine database
        ServiceProvider provider = services.BuildServiceProvider();

        Mock<IAgentRegistry> registry = new();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent);
        Mock<IWorkflowExecutionContextAccessor> accessor = new();
        accessor.Setup(a => a.BeginScope(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(Mock.Of<IDisposable>());
        // An Author step must deliver a render; the fake run "rendered" one.
        accessor.Setup(a => a.Current).Returns(new WorkflowExecutionContext
        {
            ExecutionId = Guid.NewGuid(),
            ProjectId = Project,
            CorrelationId = "test",
            PendingOutputStorageKey = $"projects/{Project}/outputFiles/render.mp4"
        });

        return new AgentStepExecutor(
            registry.Object,
            accessor.Object,
            Mock.Of<IMotionGraphicsPlacementAnnotator>(),
            new ConfigurationBuilder().Build(),
            Mock.Of<IDecisionGate>(),
            NullLogger<AgentStepExecutor>.Instance,
            services: provider);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_version_made_before_the_projects_last_quarantine_is_not_restored_but_a_later_one_is(bool older)
    {
        (Guid source, _) = await CapturedRunAsync(Project, "v1");
        DateTime marker = DateTime.UtcNow.AddHours(-1);
        using (WorkflowEngineDbContext db = _h.Db())
        {
            (await db.WorkflowExecutions.SingleAsync(e => e.Id == source)).FirstStartedAt = older ? marker.AddHours(-1) : marker.AddHours(1);
            db.Projects.Add(new Project { Id = Project, Name = "p", ContentQuarantinedAt = marker });
            await db.SaveChangesAsync();
        }

        Recorder agent = new();
        StepExecutionContext context = await NewRunAsync(Project, $$"""{"executionId":"{{source}}"}""");

        StepExecutionResult result = await Executor(agent).ExecuteAsync(context);

        if (older)
        {
            AssertSourceNotFound(result);
            agent.Ran.Should().BeFalse();
            _sessions.Sessions.Should().NotContainKey(context.Execution.Id, "nothing is restored");
        }
        else
        {
            result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
            agent.ImportsSeenAtRun.Should().Be(1);
        }
    }

    private async Task SeedRunAsync(Guid id, Guid projectId)
    {
        using WorkflowEngineDbContext db = _h.Db();
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = id,
            ProjectId = projectId,
            OrganizationId = Org,
            WorkflowDefinitionId = Guid.NewGuid(),
            Status = ExecutionStatus.Passed
        });
        await db.SaveChangesAsync();
    }

    private async Task<(Guid Source, CompositionWorkspaceVersion Version)> CapturedRunAsync(Guid projectId, string intro)
    {
        Guid source = Guid.NewGuid();
        await SeedRunAsync(source, projectId);
        CompositionWorkspaceVersion version = await CaptureAgainAsync(source, projectId, intro);
        return (source, version);
    }

    private async Task<CompositionWorkspaceVersion> CaptureAgainAsync(Guid source, Guid projectId, string intro)
    {
        FakeWorkspaceSession film = new(source);
        film.Files["package.json"] = Encoding.UTF8.GetBytes("{}");
        film.Files["src/Intro.tsx"] = Encoding.UTF8.GetBytes(intro);
        CompositionCaptureResult captured = await _h.Capture.CaptureAsync(film, new CompositionCaptureRequest
        {
            ProjectId = projectId,
            OrganizationId = Org,
            ExecutionId = source
        }, CancellationToken.None);
        return captured.Version!;
    }

    private async Task<StepExecutionContext> NewRunAsync(Guid projectId, string? continueJson, AgentType agentType = AgentType.AuthorAgent)
    {
        Guid run = Guid.NewGuid();
        await SeedRunAsync(run, projectId);
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.Agent,
            ContinueCompositionJson = continueJson,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), Name = "Author", AgentType = agentType, IsBuiltIn = true }
        };
        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = run, ProjectId = projectId, OrganizationId = Org },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = "Make the film.",
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }
}
