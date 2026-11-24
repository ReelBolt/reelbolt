namespace ReelBolt.WorkflowEngine.Execution.Snapshots;

/// <summary>
/// Carries the running execution's <see cref="AppInputRunState"/> down every await of that execution (like
/// <c>RunBriefAmbient</c>): the Conditional executor reads the <c>input.*</c> parameters, the step-cache key reads each
/// step's component, and the hold estimator plans over the reachable steps only. Null (the default, and every run that
/// carries no app input) changes nothing anywhere.
/// </summary>
public static class AppInputsAmbient
{
    private static readonly AsyncLocal<AppInputRunState?> CurrentState = new();

    public static AppInputRunState? Current => CurrentState.Value;

    public static IDisposable Enter(AppInputRunState? state)
    {
        AppInputRunState? previous = CurrentState.Value;
        CurrentState.Value = state;
        return new Restore(previous);
    }

    private sealed class Restore(AppInputRunState? previous) : IDisposable
    {
        public void Dispose() => CurrentState.Value = previous;
    }
}
