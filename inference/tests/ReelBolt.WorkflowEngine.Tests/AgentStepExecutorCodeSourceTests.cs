using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Code analyzers fail fast, before any model call, when the project holds no readable source (an archive is not
/// code), and a failed step like that is not retried by the outer loop.
/// </summary>
public class AgentStepExecutorCodeSourceTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Guid _projectId = Guid.NewGuid();

    private ServiceProvider Services()
    {
        ServiceCollection sc = new();
        sc.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(_dbName));
        return sc.BuildServiceProvider();
    }

    private async Task AddFileAsync(ServiceProvider sp, string name, string mime, string? sourceKind = null,
        FileModerationStatus moderation = FileModerationStatus.Clean, CsamScanStatus csam = CsamScanStatus.Clean)
    {
        using IServiceScope scope = sp.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            OriginalFileName = name,
            MimeType = mime,
            Category = "userFiles",
            SourceKind = sourceKind,
            ModerationStatus = moderation,
            CsamStatus = csam
        });
        await db.SaveChangesAsync();
    }

    private static Mock<IReelBoltAgent> MockAgent(AgentType type)
    {
        var agent = new Mock<IReelBoltAgent>();
        agent.Setup(a => a.Name).Returns(type.ToString());
        agent.Setup(a => a.AgentType).Returns(type);
        agent.Setup(a => a.Tools).Returns(Array.Empty<Microsoft.Extensions.AI.AIFunction>());
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Success = true, Output = "{}" });
        return agent;
    }

    private static AgentStepExecutor Executor(IReelBoltAgent agent, IServiceProvider services)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent);
        var accessor = new Mock<IWorkflowExecutionContextAccessor>();
        accessor.Setup(a => a.BeginScope(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(Mock.Of<IDisposable>());
        accessor.Setup(a => a.Current).Returns((WorkflowExecutionContext?)null);
        return new AgentStepExecutor(
            registry.Object, accessor.Object, Mock.Of<IMotionGraphicsPlacementAnnotator>(),
            new ConfigurationBuilder().Build(), Mock.Of<IDecisionGate>(), NullLogger<AgentStepExecutor>.Instance,
            services: services);
    }

    private StepExecutionContext Context(AgentType type)
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.Agent,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), Name = "a", AgentType = type, IsBuiltIn = true }
        };
        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = _projectId, OrganizationId = Guid.NewGuid() },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "t",
            CancellationToken = CancellationToken.None
        };
    }

    private static void NeverRan(Mock<IReelBoltAgent> agent) =>
        agent.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);

    [Fact]
    public async Task An_analyzer_with_only_a_zip_fails_naming_the_archive_and_never_calls_the_model()
    {
        using ServiceProvider sp = Services();
        await AddFileAsync(sp, "app-source.zip", "application/zip");
        Mock<IReelBoltAgent> agent = MockAgent(AgentType.CodeStructureAnalyzer);

        StepExecutionResult r = await Executor(agent.Object, sp).ExecuteAsync(Context(AgentType.CodeStructureAnalyzer));

        r.Status.Should().Be(StepStatus.Failed);
        r.FailureClass.Should().Be(FailureClass.UserInput);
        r.FailureCode.Should().Be("CODE_ARCHIVE_UNREADABLE");
        r.Retryable.Should().BeFalse();
        r.TokensUsed.Should().Be(0);
        r.UserMessage.Should().Contain("app-source.zip").And.Contain("connect GitHub");
        NeverRan(agent);
    }

    [Fact]
    public async Task An_archive_past_the_first_five_hundred_files_still_gets_the_archive_message()
    {
        using ServiceProvider sp = Services();
        for (int i = 0; i < 510; i++)
            await AddFileAsync(sp, $"a{i:000}.bin", "application/octet-stream");
        await AddFileAsync(sp, "zzz-source.zip", "application/zip");
        Mock<IReelBoltAgent> agent = MockAgent(AgentType.CodeStructureAnalyzer);

        StepExecutionResult r = await Executor(agent.Object, sp).ExecuteAsync(Context(AgentType.CodeStructureAnalyzer));

        r.FailureCode.Should().Be("CODE_ARCHIVE_UNREADABLE");
        r.UserMessage.Should().Contain("zzz-source.zip");
    }

    [Fact]
    public async Task A_synced_zip_and_a_quarantined_code_file_are_not_readable_source()
    {
        using ServiceProvider sp = Services();
        await AddFileAsync(sp, "repo.zip", "application/zip", sourceKind: "github");
        await AddFileAsync(sp, "bad.ts", "text/plain", csam: CsamScanStatus.Quarantined);
        Mock<IReelBoltAgent> agent = MockAgent(AgentType.CodeStructureAnalyzer);

        StepExecutionResult r = await Executor(agent.Object, sp).ExecuteAsync(Context(AgentType.CodeStructureAnalyzer));

        r.FailureCode.Should().Be("CODE_ARCHIVE_UNREADABLE");
        NeverRan(agent);
    }

    [Fact]
    public async Task An_analyzer_with_no_files_fails_with_no_readable_source()
    {
        using ServiceProvider sp = Services();
        Mock<IReelBoltAgent> agent = MockAgent(AgentType.DependencyAnalyzer);

        StepExecutionResult r = await Executor(agent.Object, sp).ExecuteAsync(Context(AgentType.DependencyAnalyzer));

        r.Status.Should().Be(StepStatus.Failed);
        r.FailureCode.Should().Be("NO_READABLE_SOURCE");
        r.FailureClass.Should().Be(FailureClass.UserInput);
        NeverRan(agent);
    }

    [Theory]
    [InlineData("index.ts", "text/plain", null)]
    [InlineData("package.json", "application/json", null)]
    [InlineData("repo", "application/octet-stream", "github")]
    public async Task An_analyzer_with_a_readable_file_runs(string name, string mime, string? sourceKind)
    {
        using ServiceProvider sp = Services();
        await AddFileAsync(sp, name, mime, sourceKind);
        Mock<IReelBoltAgent> agent = MockAgent(AgentType.RouteAndApiAnalyzer);

        StepExecutionResult r = await Executor(agent.Object, sp).ExecuteAsync(Context(AgentType.RouteAndApiAnalyzer));

        r.FailureCode.Should().NotBe("NO_READABLE_SOURCE").And.NotBe("CODE_ARCHIVE_UNREADABLE");
        agent.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_non_analyzer_agent_with_no_files_still_runs()
    {
        using ServiceProvider sp = Services();
        Mock<IReelBoltAgent> agent = MockAgent(AgentType.DirectorAgent);

        await Executor(agent.Object, sp).ExecuteAsync(Context(AgentType.DirectorAgent));

        agent.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_quarantined_text_file_alone_does_not_count_as_source()
    {
        using ServiceProvider sp = Services();
        await AddFileAsync(sp, "index.ts", "text/plain", moderation: FileModerationStatus.Quarantined);
        Mock<IReelBoltAgent> agent = MockAgent(AgentType.StyleAndThemeExtractor);

        StepExecutionResult r = await Executor(agent.Object, sp).ExecuteAsync(Context(AgentType.StyleAndThemeExtractor));

        r.FailureCode.Should().Be("NO_READABLE_SOURCE");
        NeverRan(agent);
    }

    [Fact]
    public void The_code_reading_set_is_the_five_analyzers()
    {
        CodeSourceGuard.CodeReadingAgents.Should().BeEquivalentTo(new[]
        {
            AgentType.CodeStructureAnalyzer, AgentType.DependencyAnalyzer, AgentType.ComponentInventoryAnalyzer,
            AgentType.RouteAndApiAnalyzer, AgentType.StyleAndThemeExtractor
        });
        // Every member is a real AgentType whose name says it analyzes or extracts from code (drift guard).
        foreach (AgentType t in CodeSourceGuard.CodeReadingAgents)
            Enum.IsDefined(t).Should().BeTrue();
        Enum.GetValues<AgentType>().Where(t => t.ToString().EndsWith("Analyzer") || t == AgentType.StyleAndThemeExtractor)
            .Should().BeSubsetOf(CodeSourceGuard.CodeReadingAgents,
                "a new *Analyzer agent type must be added to the code-reading set or consciously excluded here");
    }
}
