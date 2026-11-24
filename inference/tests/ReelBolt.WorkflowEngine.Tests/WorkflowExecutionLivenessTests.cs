using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Consumers;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Services;
using ReelBolt.WorkflowEngine.Services.Messaging;
using ReelBolt.WorkflowEngine.Tests.Execution;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Regression tests for the failure that destroyed three consecutive multi-hour executions:
/// the workflow ran inline inside the MassTransit consumer, so RabbitMQ's <c>consumer_timeout</c>
/// (the ack deadline) doubled as a hard cap on workflow duration, and every execution that hit it
/// was written off as <c>"Cancelled by user request"</c> despite no user involvement.
///
/// <para>
/// The two properties pinned here are the two halves of that bug:
/// <list type="number">
/// <item>the consumer must return (and therefore ack) without waiting for the execution, so no ack
/// deadline can bound a workflow's runtime; and</item>
/// <item>a cancellation must be reported with the cause that was actually recorded, never an
/// assumed user request.</item>
/// </list>
/// </para>
/// </summary>
public class WorkflowExecutionLivenessTests
{
    // --- Cancellation provenance -------------------------------------------------------------

    [Fact]
    public void An_execution_cancelled_with_no_recorded_reason_is_not_blamed_on_the_user()
    {
        // This is the exact string that appeared on three real executions killed by the broker.
        // The registry must never manufacture user intent it has no evidence for.
        var registry = new ExecutionCancellationRegistry();
        Guid executionId = Guid.NewGuid();
        registry.Register(executionId, new CancellationTokenSource());

        string reason = registry.GetReason(executionId);

        reason.Should().NotContain("user request");
        reason.Should().Be(ExecutionCancellationRegistry.UnknownReason);
    }

    [Fact]
    public void A_recorded_cancellation_reason_survives_to_the_reader()
    {
        var registry = new ExecutionCancellationRegistry();
        Guid executionId = Guid.NewGuid();
        var cts = new CancellationTokenSource();
        registry.Register(executionId, cts);

        bool cancelled = registry.TryCancel(executionId, "Stopped by user 0e1f");

        cancelled.Should().BeTrue();
        cts.IsCancellationRequested.Should().BeTrue();
        registry.GetReason(executionId).Should().Be("Stopped by user 0e1f");
    }

    [Fact]
    public void TryCancel_on_an_unregistered_execution_reports_nothing_to_cancel()
    {
        // No live token registered yet: TryCancel reports "nothing to cancel" (false). The reason is
        // recorded, but Register clears it (S-53), so it never carries into a later run.
        var registry = new ExecutionCancellationRegistry();
        Guid executionId = Guid.NewGuid();

        bool cancelled = registry.TryCancel(executionId, "Stopped by user abcd");

        cancelled.Should().BeFalse();
        registry.GetReason(executionId).Should().Be("Stopped by user abcd");
    }

    [Fact]
    public void A_reason_recorded_before_registration_is_cleared_when_the_run_registers()
    {
        // S-53: a Stop that arrives before its run registers belongs to no run. Register clears it
        // before publishing the token, so the new run is not cancelled and reports the unknown cause.
        var registry = new ExecutionCancellationRegistry();
        Guid executionId = Guid.NewGuid();
        var cts = new CancellationTokenSource();

        bool cancelled = registry.TryCancel(executionId, "Stopped by user abcd");
        registry.Register(executionId, cts);

        cancelled.Should().BeFalse();
        registry.GetReason(executionId).Should().Be(ExecutionCancellationRegistry.UnknownReason);
        cts.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void A_stop_after_registration_keeps_its_reason()
    {
        var registry = new ExecutionCancellationRegistry();
        Guid executionId = Guid.NewGuid();
        var cts = new CancellationTokenSource();
        registry.Register(executionId, cts);

        bool cancelled = registry.TryCancel(executionId, "Stopped by user 0e1f");

        cancelled.Should().BeTrue();
        cts.IsCancellationRequested.Should().BeTrue();
        registry.GetReason(executionId).Should().Be("Stopped by user 0e1f");
    }

    [Fact]
    public void Removing_an_execution_clears_its_recorded_reason()
    {
        var registry = new ExecutionCancellationRegistry();
        Guid executionId = Guid.NewGuid();
        registry.TryCancel(executionId, "Stopped by user abcd");

        registry.Remove(executionId);

        registry.GetReason(executionId).Should().Be(ExecutionCancellationRegistry.UnknownReason);
    }

    // --- Liveness: the consumer must not hold the delivery open for the execution -------------

    [Fact]
    public async Task The_consumer_acks_without_waiting_for_the_execution_to_finish()
    {
        // The regression itself. A workflow that never completes must still let Consume return:
        // before the fix, Consume awaited ExecuteAsync, so the delivery stayed unacked for the
        // execution's whole life and RabbitMQ killed the channel at consumer_timeout.
        var executionGate = new TaskCompletionSource();
        var executionStarted = new TaskCompletionSource();

        await using ServiceProvider provider = BuildProvider(async ct =>
        {
            executionStarted.TrySetResult();
            await executionGate.Task.WaitAsync(ct);
        });

        var runner = provider.GetRequiredService<WorkflowExecutionRunner>();
        var consumer = new WorkflowExecutionRequestedConsumer(
            runner, NullLogger<WorkflowExecutionRequestedConsumer>.Instance);

        Task consume = consumer.Consume(new FakeConsumeContext(Guid.NewGuid(), "corr-1"));

        // The assertion that matters: Consume completes while the execution is still running.
        await consume.WaitAsync(TimeSpan.FromSeconds(10));
        await executionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        runner.RunningCount.Should().Be(1);

        executionGate.TrySetResult();
    }

    [Fact]
    public async Task A_cancelled_delivery_token_does_not_cancel_an_already_dispatched_execution()
    {
        // The other half of the coupling: even once the consumer returns, the execution must not
        // be holding the transport's token. Cancelling it here stands in for the broker closing
        // the channel; the execution must be unaffected.
        var observed = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource();

        await using ServiceProvider provider = BuildProvider(async ct =>
        {
            await release.Task;
            observed.TrySetResult(ct.IsCancellationRequested);
        });

        var runner = provider.GetRequiredService<WorkflowExecutionRunner>();
        var consumer = new WorkflowExecutionRequestedConsumer(
            runner, NullLogger<WorkflowExecutionRequestedConsumer>.Instance);

        using var deliveryCts = new CancellationTokenSource();

        // WaitAsync, not a bare await: if this regresses to awaiting the execution inline, the
        // consumer never returns, and an unbounded await here would hang the whole test run
        // instead of failing it.
        await consumer.Consume(new FakeConsumeContext(Guid.NewGuid(), "corr-2", deliveryCts.Token))
            .WaitAsync(TimeSpan.FromSeconds(10));

        await deliveryCts.CancelAsync();
        release.TrySetResult();

        bool executionSawCancellation = await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        executionSawCancellation.Should().BeFalse(
            "the transport's ack deadline must not be able to cancel a running workflow");
    }

    // --- S-49: one run per execution id per process ------------------------------------------

    [Fact]
    public async Task A_second_delivery_of_an_execution_already_running_here_is_refused_and_never_executes()
    {
        // A redelivered message for a run this process is still executing must be acknowledged and
        // dropped at the runner, before ExecuteAsync is called, so it cannot touch the live run's
        // registry token or its lease.
        Guid id = Guid.NewGuid();
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        int calls = 0;

        using ServiceProvider empty = new ServiceCollection().BuildServiceProvider();
        var leases = new ExecutionLeaseService(
            empty.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<ExecutionLeaseService>.Instance);

        await using ServiceProvider provider = BuildProvider(async ct =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                leases.Track(id);
                started.TrySetResult();
                await gate.Task.WaitAsync(ct);
            }
        }, leases: leases);

        var runner = provider.GetRequiredService<WorkflowExecutionRunner>();
        try
        {
            AdmissionDecision first = await runner.DispatchAsync(id, "corr-1", CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            AdmissionDecision second = await runner.DispatchAsync(id, "corr-2", CancellationToken.None);

            // A wrongly admitted second run would start at once; give it up to 1 s to show itself.
            await Task.Delay(TimeSpan.FromSeconds(1));

            first.Admitted.Should().BeTrue();
            second.Admitted.Should().BeFalse();
            second.Refusal.Should().Be(AdmissionRefusal.AlreadyRunningHere);
            second.Reason.Should().Be("already_running_here");
            calls.Should().Be(1);
            leases.TrackedIds.Should().Contain(id);
            runner.RunningCount.Should().Be(1);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_delivery_after_the_run_has_exited_is_admitted_again()
    {
        // The claim is released when the run ends, so a later delivery of the same id is a fresh run.
        Guid id = Guid.NewGuid();
        int calls = 0;

        await using ServiceProvider provider = BuildProvider(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        var runner = provider.GetRequiredService<WorkflowExecutionRunner>();

        (await runner.DispatchAsync(id, "corr-1", CancellationToken.None)).Admitted.Should().BeTrue();
        await WaitForAsync(() => runner.RunningCount == 0);

        // The runner removes _running before _claimed in its ContinueWith, so for a brief window the
        // second dispatch can still see the claim held and be refused AlreadyRunningHere. That window
        // is transient: retry the refusal, bounded, instead of racing it.
        AdmissionDecision again = await runner.DispatchAsync(id, "corr-2", CancellationToken.None);
        for (int attempt = 1; !again.Admitted && again.Refusal == AdmissionRefusal.AlreadyRunningHere && attempt < 50; attempt++)
        {
            await Task.Delay(20);
            again = await runner.DispatchAsync(id, "corr-2", CancellationToken.None);
        }
        await WaitForAsync(() => runner.RunningCount == 0);

        again.Admitted.Should().BeTrue();
        calls.Should().Be(2);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met within 10 s.");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Concurrency_is_bounded_by_MaxConcurrency_once_execution_moved_off_the_consumer()
    {
        // Acking immediately means MassTransit's ConcurrentMessageLimit no longer bounds how many
        // executions run at once; the runner's own semaphore has to, or the fix would trade a
        // timeout bug for an unbounded-parallelism bug.
        var gate = new TaskCompletionSource();
        int concurrent = 0;
        int peak = 0;

        await using ServiceProvider provider = BuildProvider(async ct =>
        {
            int now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref peak, now);
            await gate.Task.WaitAsync(ct);
            Interlocked.Decrement(ref concurrent);
        }, maxConcurrency: 2);

        var runner = provider.GetRequiredService<WorkflowExecutionRunner>();

        using var admissionCts = new CancellationTokenSource();
        Task third = Task.Run(async () =>
        {
            await runner.DispatchAsync(Guid.NewGuid(), "c1", admissionCts.Token);
            await runner.DispatchAsync(Guid.NewGuid(), "c2", admissionCts.Token);
            // Third dispatch must block: both slots are occupied by executions sitting on `gate`.
            await runner.DispatchAsync(Guid.NewGuid(), "c3", admissionCts.Token);
        });

        await Task.Delay(300);
        peak.Should().BeLessThanOrEqualTo(2);
        third.IsCompleted.Should().BeFalse("the third dispatch must wait for a free slot");

        gate.TrySetResult();
        await third.WaitAsync(TimeSpan.FromSeconds(10));
        peak.Should().BeLessThanOrEqualTo(2);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen = Volatile.Read(ref target);
        while (value > seen)
        {
            int prior = Interlocked.CompareExchange(ref target, value, seen);
            if (prior == seen) return;
            seen = prior;
        }
    }

    /// <summary>
    /// Builds a provider whose <see cref="WorkflowExecutorService"/> is replaced by a stub running
    /// <paramref name="body"/>, so these tests exercise the dispatch/liveness plumbing without
    /// standing up a database, a message bus, or any agents.
    /// </summary>
    private static ServiceProvider BuildProvider(
        Func<CancellationToken, Task> body, int maxConcurrency = 4, ExecutionLeaseService? leases = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkflowEngine:MaxConcurrency"] = maxConcurrency.ToString()
            })
            .Build());
        services.AddSingleton<ExecutionCancellationRegistry>();
        // The runner evicts the per-execution decision memo on every exit path (plan §1 D4), so it
        // takes the memoizer as a required dependency — this harness must supply the same singleton
        // the production container registers.
        services.AddSingleton<IDecisionQuestionMemoizer, DecisionQuestionMemoizer>();
        services.AddScoped<WorkflowExecutorService>(_ => new StubExecutorService(body));
        // F11: the runner takes the admission guard as a required dependency, so a harness that
        // registered a runner without one would fail to resolve rather than silently skip the
        // kill switches in production. These tests are about liveness, not switches: nothing is paused.
        services.AddSingleton<IPlatformSwitches, TestPlatformSwitches>();
        services.AddSingleton(sp => new ExecutionAdmissionGuard(
            sp.GetRequiredService<IPlatformSwitches>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ExecutionAdmissionGuard>.Instance));
        if (leases is not null)
        {
            services.AddSingleton(leases);
        }

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

        public override Task ExecuteAsync(Guid executionId, string correlationId, CancellationToken ct) => _body(ct);
    }

    /// <summary>Minimal ConsumeContext stand-in: these tests only read Message and CancellationToken.</summary>
    private sealed class FakeConsumeContext : ConsumeContext<WorkflowExecutionRequested>
    {
        public FakeConsumeContext(Guid executionId, string correlationId, CancellationToken ct = default)
        {
            Message = new WorkflowExecutionRequested
            {
                ExecutionId = executionId,
                CorrelationId = correlationId
            };
            CancellationToken = ct;
        }

        public WorkflowExecutionRequested Message { get; }
        public CancellationToken CancellationToken { get; }

        // Everything below is unused by the consumer under test.
        public Guid? MessageId => null;
        public Guid? RequestId => null;
        public Guid? CorrelationId => null;
        public Guid? ConversationId => null;
        public Guid? InitiatorId => null;
        public DateTime? ExpirationTime => null;
        public Uri? SourceAddress => null;
        public Uri? DestinationAddress => null;
        public Uri? ResponseAddress => null;
        public Uri? FaultAddress => null;
        public DateTime? SentTime => null;
        public Headers Headers => throw new NotSupportedException();
        public HostInfo Host => throw new NotSupportedException();
        public IEnumerable<string> SupportedMessageTypes => throw new NotSupportedException();
        public ReceiveContext ReceiveContext => throw new NotSupportedException();
        public SerializerContext SerializerContext => throw new NotSupportedException();
        public Task ConsumeCompleted => Task.CompletedTask;
        public ISendEndpointProvider GetSendEndpointProvider() => throw new NotSupportedException();
        public IPublishEndpointProvider GetPublishEndpointProvider() => throw new NotSupportedException();
        public bool HasPayloadType(Type payloadType) => false;
        public bool TryGetPayload<T>(out T? payload) where T : class { payload = null; return false; }
        public T GetOrAddPayload<T>(PayloadFactory<T> payloadFactory) where T : class => throw new NotSupportedException();
        public T AddOrUpdatePayload<T>(PayloadFactory<T> addFactory, UpdatePayloadFactory<T> updateFactory) where T : class => throw new NotSupportedException();
        public bool HasMessageType(Type messageType) => messageType == typeof(WorkflowExecutionRequested);
        public bool TryGetMessage<T>(out ConsumeContext<T>? consumeContext) where T : class { consumeContext = null; return false; }
        public void AddConsumeTask(Task task) { }
        public Task RespondAsync<T>(T message) where T : class => Task.CompletedTask;
        public Task RespondAsync<T>(T message, IPipe<SendContext<T>> sendPipe) where T : class => Task.CompletedTask;
        public Task RespondAsync<T>(T message, IPipe<SendContext> sendPipe) where T : class => Task.CompletedTask;
        public Task RespondAsync(object message) => Task.CompletedTask;
        public Task RespondAsync(object message, Type messageType) => Task.CompletedTask;
        public Task RespondAsync(object message, IPipe<SendContext> sendPipe) => Task.CompletedTask;
        public Task RespondAsync(object message, Type messageType, IPipe<SendContext> sendPipe) => Task.CompletedTask;
        public Task RespondAsync<T>(object values) where T : class => Task.CompletedTask;
        public Task RespondAsync<T>(object values, IPipe<SendContext<T>> sendPipe) where T : class => Task.CompletedTask;
        public Task RespondAsync<T>(object values, IPipe<SendContext> sendPipe) where T : class => Task.CompletedTask;
        public void Respond<T>(T message) where T : class { }
        public Task<ISendEndpoint> GetSendEndpoint(Uri address) => throw new NotSupportedException();
        public Task NotifyConsumed<T>(ConsumeContext<T> context, TimeSpan duration, string consumerType) where T : class => Task.CompletedTask;
        public Task NotifyFaulted<T>(ConsumeContext<T> context, TimeSpan duration, string consumerType, Exception exception) where T : class => Task.CompletedTask;
        public Task NotifyConsumed(TimeSpan duration, string consumerType) => Task.CompletedTask;
        public Task NotifyFaulted(TimeSpan duration, string consumerType, Exception exception) => Task.CompletedTask;
        public Task Publish<T>(T message, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
        public Task Publish<T>(T message, IPipe<PublishContext<T>> publishPipe, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
        public Task Publish<T>(T message, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
        public Task Publish(object message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish(object message, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish(object message, Type messageType, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish(object message, Type messageType, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<T>(object values, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
        public Task Publish<T>(object values, IPipe<PublishContext<T>> publishPipe, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
        public Task Publish<T>(object values, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
        public ConnectHandle ConnectPublishObserver(IPublishObserver observer) => throw new NotSupportedException();
        public ConnectHandle ConnectSendObserver(ISendObserver observer) => throw new NotSupportedException();
    }
}
