using System;
using System.Threading;
using Xunit.Sdk;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

/// <summary>
/// Runs hostile-input work on a background thread with a hard deadline so a hang or a quadratic blow-up
/// fails the test instead of wedging the runner (a runaway background thread cannot keep the host alive).
/// </summary>
internal static class HostileInput
{
    public static T RunWithin<T>(TimeSpan limit, Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
        };

        long started = Environment.TickCount64;
        thread.Start();
        if (!thread.Join(limit))
            throw new XunitException($"Hostile input did not complete within {limit.TotalSeconds:0.#} s (hang or super-linear work).");

        if (error is not null)
            throw new XunitException("Hostile input work threw: " + error);

        long elapsed = Environment.TickCount64 - started;
        if (elapsed > limit.TotalMilliseconds)
            throw new XunitException($"Hostile input took {elapsed} ms (limit {limit.TotalMilliseconds} ms).");

        return result;
    }
}
