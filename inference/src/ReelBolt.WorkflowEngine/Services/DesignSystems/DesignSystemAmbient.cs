using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Services.DesignSystems;

/// <summary>
/// Carries the running execution's <see cref="DesignSystemRunState"/> to the agents it runs, exactly
/// like <c>RunStyleAmbient</c> carries the editing style: agents are singletons shared by every
/// concurrent execution, so the state cannot live on the agent. <see cref="Enter"/> is called once
/// at the top of <c>WorkflowExecutorService.ExecuteAsync</c> and the value flows down every
/// <c>await</c> of that execution, including background tasks a room or a parallel step starts.
/// </summary>
public static class DesignSystemAmbient
{
    private static readonly AsyncLocal<DesignSystemRunState?> CurrentState = new();

    /// <summary>The running execution's pinned design/product state, or null when it has none.</summary>
    public static DesignSystemRunState? Current => CurrentState.Value;

    /// <summary>Makes <paramref name="state"/> current until the returned scope is disposed.</summary>
    public static IDisposable Enter(DesignSystemRunState? state)
    {
        DesignSystemRunState? previous = CurrentState.Value;
        CurrentState.Value = state;
        return new Restore(previous);
    }

    /// <summary>
    /// <paramref name="systemPrompt"/> plus the design block then the product block for
    /// <paramref name="agentType"/>. Returned unchanged — the same string instance — when there is
    /// no state or the role has no view.
    /// </summary>
    public static string Decorate(string systemPrompt, AgentType agentType)
    {
        if (CurrentState.Value is not { } state)
            return systemPrompt;

        (string? design, string? product) = state.BlocksFor(agentType);
        if (design is null && product is null)
            return systemPrompt;

        string result = systemPrompt;
        if (design is not null)
            result += "\n\n" + design;
        if (product is not null)
            result += "\n\n" + product;

        return result;
    }

    private sealed class Restore(DesignSystemRunState? previous) : IDisposable
    {
        public void Dispose() => CurrentState.Value = previous;
    }
}
