namespace ReelBolt.Inference.Api.Services.History;

/// <summary>
/// The async-local "do not protect these bytes" scope (contract section 3). Quarantine moves run
/// inside it: a quarantined object must never be copied into the project's history, and the caller
/// erases the file's history separately.
/// </summary>
public static class ProjectHistoryProtection
{
    private static readonly AsyncLocal<bool> Suppressed = new();

    /// <summary>True while the current async flow is inside a <see cref="Suppress"/> scope.</summary>
    public static bool IsSuppressed => Suppressed.Value;

    /// <summary>
    /// Disables copy-on-change for the current async flow until the returned scope is disposed. The
    /// previous value is restored, so nested scopes compose.
    /// </summary>
    public static IDisposable Suppress()
    {
        bool previous = Suppressed.Value;
        Suppressed.Value = true;
        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Suppressed.Value = previous;
        }
    }
}
