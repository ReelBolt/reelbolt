using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Services;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution;

/// <summary>
/// The eviction half of plan §1 D4, wired rather than merely documented.
///
/// <para>
/// <see cref="DecisionQuestionMemoizer"/> had no production caller of
/// <see cref="IDecisionQuestionMemoizer.Evict"/>: the docs said "entries are evicted when the
/// execution completes" and the DI registration said "(oldest-first cap + Evict on completion)",
/// and neither was true — the 64-execution cap was the only bound, and each execution's own answer
/// dictionary was unbounded in question count. These tests pin the call that closes that gap:
/// <see cref="WorkflowExecutionRunner"/> evicts in the one place every execution passes through,
/// on the completed, failed <em>and</em> cancelled paths alike.
/// </para>
/// </summary>
public sealed class DecisionMemoEvictionTests
{
    private const string Site = "derush-prefilter";

    private static readonly DecisionQuestion Question = new(
        "keep s1",
        DecisionKind.Noul,
        "Should the editor keep this item?",
        []);

    private static readonly DecisionState State = new("shot s1: 3.2s, gentle motion");

    [Fact]
    public async Task A_completed_execution_is_evicted_when_it_finishes()
    {
        DecisionQuestionMemoizer memoizer = new();
        Guid executionId = Guid.NewGuid();
        Seed(memoizer, executionId);

        await using ServiceProvider provider = BuildProvider(_ => Task.CompletedTask, memoizer);

        await RunToCompletionAsync(provider, executionId);

        AssertEvicted(memoizer, executionId);
    }

    [Fact]
    public async Task A_failed_execution_is_evicted_when_it_finishes()
    {
        DecisionQuestionMemoizer memoizer = new();
        Guid executionId = Guid.NewGuid();
        Seed(memoizer, executionId);

        await using ServiceProvider provider = BuildProvider(
            _ => throw new InvalidOperationException("the execution blew up"), memoizer);

        await RunToCompletionAsync(provider, executionId);

        AssertEvicted(memoizer, executionId);
    }

    [Fact]
    public async Task A_cancelled_execution_is_evicted_when_it_finishes()
    {
        // The path that would be missed by evicting anywhere other than the finally: a cancelled
        // execution leaves through its own catch and never reaches the success path.
        DecisionQuestionMemoizer memoizer = new();
        Guid executionId = Guid.NewGuid();
        Seed(memoizer, executionId);

        await using ServiceProvider provider = BuildProvider(
            _ => throw new OperationCanceledException(), memoizer);

        await RunToCompletionAsync(provider, executionId);

        AssertEvicted(memoizer, executionId);
    }

    [Fact]
    public async Task Finishing_one_execution_never_evicts_another_executions_answers()
    {
        DecisionQuestionMemoizer memoizer = new();
        Guid finishing = Guid.NewGuid();
        Guid stillRunning = Guid.NewGuid();
        Seed(memoizer, finishing);
        Seed(memoizer, stillRunning);

        await using ServiceProvider provider = BuildProvider(_ => Task.CompletedTask, memoizer);

        await RunToCompletionAsync(provider, finishing);

        memoizer.RetainedExecutionCount.Should().Be(1);
        memoizer.TryGet(stillRunning, Site, Question, State, out _).Should().BeTrue(
            "eviction is per-execution, not a flush of the whole memo");
    }

    // --- helpers ----------------------------------------------------------------------------------

    private static void Seed(DecisionQuestionMemoizer memoizer, Guid executionId)
    {
        memoizer.Store(
            executionId,
            Site,
            Question,
            State,
            new DecisionAnswer(
                Question.Name,
                DecisionKind.Noul,
                Choice: null,
                Score: null,
                Noul: 0.9,
                Confidence: null,
                new Dictionary<string, double>()));

        memoizer.TryGet(executionId, Site, Question, State, out _).Should().BeTrue();
    }

    private static void AssertEvicted(DecisionQuestionMemoizer memoizer, Guid executionId)
    {
        memoizer.TryGet(executionId, Site, Question, State, out _).Should().BeFalse(
            "a finished execution's answers must be gone");
        memoizer.RetainedExecutionCount.Should().Be(0);
    }

    /// <summary>
    /// Dispatches the execution and waits for the runner to be done with it. <c>RunningCount</c> is
    /// decremented by the continuation that runs after <c>RunAsync</c> — i.e. after its
    /// <c>finally</c> — so observing zero guarantees the eviction has already happened.
    /// </summary>
    private static async Task RunToCompletionAsync(IServiceProvider provider, Guid executionId)
    {
        var runner = provider.GetRequiredService<WorkflowExecutionRunner>();

        await runner.DispatchAsync(executionId, "corr-memo", CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (runner.RunningCount != 0)
            await Task.Delay(10, timeout.Token);
    }

    private static ServiceProvider BuildProvider(
        Func<CancellationToken, Task> body,
        IDecisionQuestionMemoizer memoizer)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkflowEngine:MaxConcurrency"] = "4"
            })
            .Build());
        services.AddSingleton<ExecutionCancellationRegistry>();
        services.AddSingleton(memoizer);
        services.AddScoped<WorkflowExecutorService>(_ => new StubExecutorService(body));
        // F11: the runner requires the admission guard, so a harness without one does not resolve.
        // Nothing is paused here; the kill-switch behaviour has its own tests.
        services.AddSingleton<IPlatformSwitches, TestPlatformSwitches>();
        services.AddSingleton(sp => new ExecutionAdmissionGuard(
            sp.GetRequiredService<IPlatformSwitches>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ExecutionAdmissionGuard>.Instance));
        services.AddSingleton<WorkflowExecutionRunner>();
        return services.BuildServiceProvider();
    }

    private sealed class StubExecutorService : WorkflowExecutorService
    {
        private readonly Func<CancellationToken, Task> _body;

        public StubExecutorService(Func<CancellationToken, Task> body)
            : base(
                scopeFactory: null!,
                eventPublisher: null!,
                logger: NullLogger<WorkflowExecutorService>.Instance,
                executors: Array.Empty<IStepExecutor>(),
                rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
                hardeningOptions: Microsoft.Extensions.Options.Options.Create(new WorkflowHardeningOptions()),
                cancellationRegistry: new ExecutionCancellationRegistry())
            => _body = body;

        public override Task ExecuteAsync(Guid executionId, string correlationId, CancellationToken ct) =>
            _body(ct);
    }
}
