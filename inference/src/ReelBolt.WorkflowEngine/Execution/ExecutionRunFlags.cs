using System;
using System.Collections.Concurrent;

namespace ReelBolt.WorkflowEngine.Execution;

/// <summary>
/// Per-execution switches that arrive on the execution request and are read by step executors
/// that cannot take them from the step config. Currently one: <c>RegenerateVideoClips</c>, the
/// user's answer to "reuse previously generated paid clips, or pay for fresh ones?".
/// </summary>
/// <remarks>
/// In-memory on purpose: the flag only matters while the execution runs in this process, and the
/// default (absent) is the SAFE answer, reuse. A hard process kill therefore degrades to "reuse",
/// never to an unrequested spend.
/// </remarks>
public sealed class ExecutionRunFlags
{
    private readonly ConcurrentDictionary<Guid, bool> _regenerate = new();

    public void SetRegenerateVideoClips(Guid executionId, bool value)
    {
        if (value)
        {
            _regenerate[executionId] = true;
        }
        else
        {
            _regenerate.TryRemove(executionId, out _);
        }
    }

    public bool RegenerateVideoClips(Guid executionId) => _regenerate.ContainsKey(executionId);

    public void Clear(Guid executionId) => _regenerate.TryRemove(executionId, out _);
}
