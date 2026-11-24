using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.WorkflowEngine.Drain;
using RabbitMQ.Client;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Drain;

/// <summary>
/// Live-broker proof (skipped unless REELBOLT_TEST_RABBITMQ_PORT is set): stopping a receive endpoint
/// while a delivery is blocked waiting for admission returns the message to the broker (still in the
/// queue, nothing in _error), and the bus keeps publishing.
/// </summary>
public class EngineDrainLiveBrokerTests
{
    private const string PortVariable = "REELBOLT_TEST_RABBITMQ_PORT";

    public sealed record Probe(Guid Id, bool Poison = false);

    /// <summary>Reports as skipped (not passed) when no broker port is configured.</summary>
    public sealed class LiveBrokerFactAttribute : FactAttribute
    {
        public LiveBrokerFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(PortVariable) is null)
            {
                Skip = $"Requires a live RabbitMQ: set {PortVariable} to its AMQP port (throwaway container named zdd-*).";
            }
        }
    }

    public sealed class BlockingConsumer(Gate gate) : IConsumer<Probe>
    {
        public async Task Consume(ConsumeContext<Probe> context)
        {
            if (context.Message.Poison) throw new InvalidOperationException("poison");
            gate.Entered.TrySetResult();
            Interlocked.Increment(ref gate.Deliveries);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, gate.Drain.Token);
            try { await Task.Delay(Timeout.Infinite, linked.Token); }
            catch (OperationCanceledException) when (gate.Drain.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
            {
                throw new DrainReleasedException();
            }
        }
    }

    public sealed class Gate
    {
        public int Deliveries;
        public CancellationTokenSource Drain { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task<uint> CountAsync(ushort port, string queue)
    {
        var factory = new ConnectionFactory { HostName = "127.0.0.1", Port = port, UserName = "guest", Password = "guest" };
        await using var conn = await factory.CreateConnectionAsync();
        await using var ch = await conn.CreateChannelAsync();
        try { return (await ch.QueueDeclarePassiveAsync(queue)).MessageCount; }
        catch (RabbitMQ.Client.Exceptions.OperationInterruptedException) { return 0; } // queue does not exist
    }

    private static async Task<uint> WaitForCountAsync(ushort port, string queue, uint expected)
    {
        uint n = 0;
        for (int i = 0; i < 40; i++)
        {
            n = await CountAsync(port, queue);
            if (n == expected) break;
            await Task.Delay(250);
        }
        return n;
    }

    private static ServiceProvider Build(ushort port, string queue, EngineDrainTarget target, Gate gate) => new ServiceCollection()
        .AddLogging().AddSingleton(gate)
        .AddMassTransit(x =>
        {
            x.AddConsumer<BlockingConsumer>();
            x.UsingRabbitMq((ctx, cfg) =>
            {
                cfg.Host("127.0.0.1", port, "/", h => { h.Username("guest"); h.Password("guest"); });
                cfg.ReceiveEndpoint(queue, e =>
                {
                    e.AutoDelete = false;
                    target.Expect(e);
                    e.UseMessageRetry(r => { r.Intervals(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)); r.IgnoreDrainRelease(); });
                    e.RequeueOnDrainRelease();
                    e.ConfigureConsumer<BlockingConsumer>(ctx);
                });
            });
        }).BuildServiceProvider();

    [LiveBrokerFact]
    public async Task Stopping_the_endpoint_returns_a_waiting_delivery_to_the_main_queue_not_to_error_and_the_bus_keeps_publishing()
    {
        ushort port = ushort.Parse(Environment.GetEnvironmentVariable(PortVariable)!);
        var target = new EngineDrainTarget(NullLogger<EngineDrainTarget>.Instance);
        var gate = new Gate();
        string queue = "zdd-probe-" + Guid.NewGuid().ToString("N");
        await using var sp = Build(port, queue, target, gate);
        var bus = sp.GetRequiredService<IBusControl>();
        await bus.StartAsync();
        try
        {
            await bus.Publish(new Probe(Guid.NewGuid()));
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

            gate.Drain.Cancel();
            await target.BeginDrainAsync(CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(5));
            target.IsFullyStopped.Should().BeTrue();

            (await WaitForCountAsync(port, queue, 1)).Should().Be(1, "the released delivery is back in the main queue");
            (await CountAsync(port, queue + "_error")).Should().Be(0, "a drain release is never dead-lettered");

            await bus.Publish(new Probe(Guid.NewGuid())); // bus still works
            await Task.Delay(TimeSpan.FromSeconds(3));
            gate.Deliveries.Should().Be(1, "the cancelled waiter must not be redelivered to this process");
        }
        finally
        {
            await bus.StopAsync();
        }
    }

    [LiveBrokerFact]
    public async Task A_poison_message_still_reaches_the_error_queue()
    {
        ushort port = ushort.Parse(Environment.GetEnvironmentVariable(PortVariable)!);
        var target = new EngineDrainTarget(NullLogger<EngineDrainTarget>.Instance);
        var gate = new Gate();
        string queue = "zdd-poison-" + Guid.NewGuid().ToString("N");
        await using var sp = Build(port, queue, target, gate);
        var bus = sp.GetRequiredService<IBusControl>();
        await bus.StartAsync();
        try
        {
            await bus.Publish(new Probe(Guid.NewGuid(), Poison: true));
            (await WaitForCountAsync(port, queue + "_error", 1)).Should().Be(1, "a faulted delivery goes to _error as before");
            (await CountAsync(port, queue)).Should().Be(0);
        }
        finally
        {
            await bus.StopAsync();
        }
    }
}
