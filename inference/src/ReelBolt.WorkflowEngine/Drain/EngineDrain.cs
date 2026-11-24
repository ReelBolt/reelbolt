using MassTransit;
using MassTransit.Middleware;
using ReelBolt.Shared.Drain;
using ReelBolt.WorkflowEngine.Consumers;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Drain;

/// <summary>
/// Stops the bus's receive endpoints on drain WITHOUT stopping the bus, so a running run keeps publishing.
/// Collects each <see cref="IReceiveEndpoint"/> at Ready.
/// </summary>
public sealed class EngineDrainTarget : IDrainTarget, IReceiveEndpointObserver
{
    private readonly object _gate = new();
    private readonly Dictionary<Uri, IReceiveEndpoint> _endpoints = new();
    private readonly Dictionary<Uri, Task> _stops = new();
    private readonly ILogger<EngineDrainTarget> _logger;
    private bool _drainBegun;
    private int _failed;
    private int _expected;

    private readonly CancellationTokenSource _drainCts = new();

    /// <summary>Cancelled when drain begins; releases deliveries still waiting for admission.</summary>
    public CancellationToken DrainToken => _drainCts.Token;

    public EngineDrainTarget(ILogger<EngineDrainTarget> logger) => _logger = logger;

    /// <summary>Endpoints that must have stopped before <see cref="IsFullyStopped"/> can be true.</summary>
    public int ExpectedEndpoints => Volatile.Read(ref _expected);

    /// <summary>Observes <paramref name="endpoint"/> and counts it as an endpoint drain must stop.</summary>
    public void Expect(IReceiveEndpointConfigurator endpoint)
    {
        Interlocked.Increment(ref _expected);
        endpoint.ConnectReceiveEndpointObserver(this);
    }

    public bool IsFullyStopped
    {
        get
        {
            lock (_gate)
            {
                return _drainBegun
                    && Volatile.Read(ref _failed) == 0
                    && _endpoints.Count >= ExpectedEndpoints
                    && _stops.Count == _endpoints.Count
                    && _stops.Values.All(t => t.IsCompletedSuccessfully);
            }
        }
    }

    public Task Ready(ReceiveEndpointReady ready)
    {
        lock (_gate)
        {
            _endpoints[ready.InputAddress] = ready.ReceiveEndpoint;
            if (_drainBegun)
            {
                // Became Ready after drain began (e.g. a late start or a reconnect): stop it at once.
                // A repeat Ready for a known address replaces that address's stop, so the counts stay equal.
                IReceiveEndpoint late = ready.ReceiveEndpoint;
                _stops[ready.InputAddress] = Task.Run(() => StopAsync(late));
            }
        }

        return Task.CompletedTask;
    }

    public Task Stopping(ReceiveEndpointStopping stopping) => Task.CompletedTask;

    public Task Completed(ReceiveEndpointCompleted completed) => Task.CompletedTask;

    public Task Faulted(ReceiveEndpointFaulted faulted) => Task.CompletedTask;

    /// <summary>Starts the stops in the background (a stop waits for in-flight deliveries) and returns.</summary>
    public Task BeginDrainAsync(CancellationToken cancellationToken)
    {
        _drainCts.Cancel();
        lock (_gate)
        {
            if (_drainBegun)
            {
                return Task.CompletedTask;
            }

            _drainBegun = true;
            foreach ((Uri address, IReceiveEndpoint e) in _endpoints)
            {
                _stops[address] = StopAsync(e);
            }
        }

        return Task.CompletedTask;
    }

    private async Task StopAsync(IReceiveEndpoint e)
    {
        try
        {
            await e.Stop(CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Drain: receive endpoint {Address} stopped", e.InputAddress);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failed);
            _logger.LogError(ex, "Drain: stopping receive endpoint {Address} failed", e.InputAddress);
        }
    }
}

/// <summary>
/// Thrown by a consumer whose delivery was released because drain began while it waited for admission.
/// Ignored by retry, and turned by <see cref="DrainRequeueFilter"/> into a failed receive so the broker
/// delivery is nacked with requeue instead of being moved to the _error queue.
/// </summary>
public sealed class DrainReleasedException() : Exception("Delivery released back to the broker: the engine is draining");

public sealed class DrainRequeueFilter : IFilter<ExceptionReceiveContext>
{
    /// <summary>
    /// Pause before a release is requeued. If an endpoint's Stop failed it keeps consuming, and a requeue
    /// with no pause would redeliver the same message in a hot loop.
    /// </summary>
    internal static readonly TimeSpan RequeueBackoff = TimeSpan.FromSeconds(1);

    public async Task Send(ExceptionReceiveContext context, IPipe<ExceptionReceiveContext> next)
    {
        for (Exception? e = context.Exception; e is not null; e = e.InnerException)
        {
            if (e is DrainReleasedException)
            {
                try
                {
                    await Task.Delay(RequeueBackoff, context.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Shutting down: requeue now.
                }

                // Escapes the error pipe, so the transport never moves the message to _error.
                throw new DrainReleasedException();
            }
        }

        await next.Send(context).ConfigureAwait(false);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("drainRequeue");
}

public static class EngineDrainEndpointExtensions
{
    /// <summary>Retry skips <see cref="DrainReleasedException"/>; the error pipe re-queues it instead of dead-lettering.</summary>
    public static void IgnoreDrainRelease(this IRetryConfigurator r) => r.Ignore<DrainReleasedException>();

    // DrainRequeueFilter pauses before each requeue, so an endpoint whose Stop failed cannot hot-loop.
    // The drain filter REPLACES the default error pipe, so the default fault + _error filters are re-added after it.
    public static void RequeueOnDrainRelease(this IReceiveEndpointConfigurator e)
        => e.ConfigureError(x =>
        {
            x.UseFilter(new DrainRequeueFilter());
            x.UseFilter(new GenerateFaultFilter());
            x.UseFilter(new ErrorTransportFilter());
        });
}

public sealed class RunnerDrainWork(WorkflowExecutionRunner runner) : IDrainWorkSource
{
    public string Name => "runs";

    public int ActiveCount => runner.RunningCount;
}

public sealed class DerivativeDrainWork(MediaDerivativeDispatcher dispatcher) : IDrainWorkSource
{
    public string Name => "derivatives";

    public int ActiveCount => dispatcher.ActiveCount;
}
