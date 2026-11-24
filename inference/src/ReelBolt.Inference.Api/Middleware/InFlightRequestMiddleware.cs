using ReelBolt.Shared.Drain;

namespace ReelBolt.Inference.Api.Middleware;

/// <summary>Counts HTTP requests in flight (drain activeWork). Excludes nothing; changes no response.</summary>
public sealed class InFlightRequestCounter : IDrainWorkSource
{
    private int _count;

    public string Name => "httpRequests";

    public int ActiveCount => Volatile.Read(ref _count);

    public void Enter() => Interlocked.Increment(ref _count);

    public void Exit() => Interlocked.Decrement(ref _count);
}

public sealed class InFlightRequestMiddleware(RequestDelegate next, InFlightRequestCounter counter)
{
    public async Task InvokeAsync(HttpContext context)
    {
        counter.Enter();
        try
        {
            await next(context);
        }
        finally
        {
            counter.Exit();
        }
    }
}

/// <summary>Live wake turns (not tied to a request), plus the wake sweeper's own gate.</summary>
public sealed class WakeTurnDrainWork : IDrainWorkSource
{
    public string Name => "assistantWakeTurns";

    public int ActiveCount => Services.Assistant.AssistantRunWakeSweeper.RunningWakeTurns;
}
