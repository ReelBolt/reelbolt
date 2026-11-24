using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The cross-run analysis reference (<c>AnalysisStepResultId</c>) of VideoCompile and ThumbnailGenerate gets the same
/// result gate as every other stored source: a result that may predate the project's quarantine marker, or was superseded,
/// is answered as not found.
/// </summary>
public sealed class QuarantineAnalysisGateTests
{
    private static readonly DateTime Marker = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Scope(ServiceProvider provider) : IServiceScope
    {
        public IServiceProvider ServiceProvider => provider;
        public void Dispose() { }
    }

    private sealed class Factory(ServiceProvider provider) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(provider);
    }

    private static async Task<(ServiceProvider Provider, Guid ProjectId, Guid ResultId)> SeedAsync(
        int firstStartOffset, int executedOffset, bool superseded)
    {
        string dbName = Guid.NewGuid().ToString();
        ServiceCollection services = new();
        services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(dbName));
        ServiceProvider provider = services.BuildServiceProvider();
        Guid projectId = Guid.NewGuid(), resultId = Guid.NewGuid();
        using IServiceScope scope = provider.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        WorkflowExecution run = new() { Id = Guid.NewGuid(), ProjectId = projectId, FirstStartedAt = Marker.AddSeconds(firstStartOffset) };
        db.Projects.Add(new Project { Id = projectId, Name = "p", ContentQuarantinedAt = Marker });
        db.WorkflowExecutions.Add(run);
        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = resultId,
            WorkflowExecutionId = run.Id,
            WorkflowStepId = Guid.NewGuid(),
            Status = StepStatus.Completed,
            ExecutedAt = Marker.AddSeconds(executedOffset),
            ArtifactStorageKey = $"projects/{projectId}/agentFiles/video-analysis/{run.Id}/analysis.json",
            SupersededAt = superseded ? Marker.AddHours(5) : null
        });
        await db.SaveChangesAsync();
        return (provider, projectId, resultId);
    }

    private static T Executor<T>(ServiceProvider provider) where T : class
    {
        T instance = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        typeof(T).GetField("_scopeFactory", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, new Factory(provider));
        typeof(T).GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, NullLogger<T>.Instance);
        return instance;
    }

    private static StepExecutionContext Context(Guid projectId) => new()
    {
        Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = projectId },
        Step = new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 2 },
        AllSteps = [],
        AccumulatedOutput = string.Empty,
        StepOutputHistory = [],
        CurrentStepIndex = 0,
        IterationCount = 0,
        CorrelationId = "analysis-gate-test",
        CancellationToken = CancellationToken.None
    };

    public static TheoryData<int, int, bool, bool> Cases() => new()
    {
        { -3600, -3600, false, false },
        { -3600, 7200, false, false },  // run began before the marker, finished after it
        { 7200, -3600, false, false },
        { 7200, 7200, true, false },    // superseded
        { 7200, 7200, false, true }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task VideoCompile_resolves_a_named_analysis_artifact_only_when_the_result_gate_allows_it(
        int firstStart, int executed, bool superseded, bool allowed)
    {
        (ServiceProvider provider, Guid projectId, Guid resultId) = await SeedAsync(firstStart, executed, superseded);
        using ServiceProvider _ = provider;
        VideoCompileStepExecutor executor = Executor<VideoCompileStepExecutor>(provider);
        VideoCompileStepConfig config = new(1, new ExtractInputRef(ExtractInputSource.Step, 1), 1, AnalysisStepResultId: resultId);

        MethodInfo method = typeof(VideoCompileStepExecutor).GetMethod("ResolveAnalysisArtifactKeyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var answer = await (Task<(string? Key, string? Error)>)method.Invoke(executor, [Context(projectId), config])!;

        (answer.Key is not null).Should().Be(allowed, answer.Error);
        if (!allowed)
            answer.Error.Should().Contain("was not found");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ThumbnailGenerate_resolves_a_named_analysis_artifact_only_when_the_result_gate_allows_it(
        int firstStart, int executed, bool superseded, bool allowed)
    {
        (ServiceProvider provider, Guid projectId, Guid resultId) = await SeedAsync(firstStart, executed, superseded);
        using ServiceProvider _ = provider;
        ThumbnailGenerateStepExecutor executor = Executor<ThumbnailGenerateStepExecutor>(provider);
        ThumbnailGenerateStepConfig config = new(1, 1, AnalysisStepResultId: resultId);

        MethodInfo method = typeof(ThumbnailGenerateStepExecutor).GetMethod("ResolveAnalysisArtifactKeyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var answer = await (Task<(string? Key, string? Error)>)method.Invoke(executor, [Context(projectId), config, CancellationToken.None])!;

        (answer.Key is not null).Should().Be(allowed, answer.Error);
        if (!allowed)
            answer.Error.Should().Contain("was not found");
    }
}
