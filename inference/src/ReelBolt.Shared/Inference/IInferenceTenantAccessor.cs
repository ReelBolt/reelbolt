namespace ReelBolt.Shared.Inference;

/// <summary>
/// The organization an inference-provider resolution runs for. The resolver is a singleton, so it
/// cannot take a scoped tenant context; each service supplies a singleton accessor over its own
/// ambient identity (the Inference API reads <c>ITenantContext</c>, the WorkflowEngine reads the
/// current <c>WorkflowExecutionContext</c>).
/// </summary>
public interface IInferenceTenantAccessor
{
    /// <summary>
    /// The organization whose providers are visible, or null for the platform-only view (platform
    /// rows only, never any organization's own). Null is also what a call outside any tenant
    /// scope gets: fail closed to platform-managed providers.
    /// </summary>
    Guid? OrganizationId { get; }

    /// <summary>
    /// Forces <see cref="OrganizationId"/> to null for the current async flow. For platform-owned
    /// work that happens to run inside a request or execution (platform documentation indexing and
    /// search, whose vectors must live in the platform's embedding space, never an org's).
    /// </summary>
    IDisposable BeginPlatformScope();
}

/// <summary>
/// Shared plumbing for <see cref="IInferenceTenantAccessor"/> implementations: the platform-scope
/// override is identical in both services, only the ambient organization differs.
/// </summary>
public abstract class InferenceTenantAccessorBase : IInferenceTenantAccessor
{
    private readonly AsyncLocal<bool> _platformScope = new();

    /// <summary>The ambient organization, ignoring any platform scope.</summary>
    protected abstract Guid? AmbientOrganizationId { get; }

    /// <inheritdoc />
    public Guid? OrganizationId => _platformScope.Value ? null : AmbientOrganizationId;

    /// <inheritdoc />
    public IDisposable BeginPlatformScope()
    {
        bool previous = _platformScope.Value;
        _platformScope.Value = true;
        return new Restore(_platformScope, previous);
    }

    private sealed class Restore(AsyncLocal<bool> slot, bool previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                slot.Value = previous;
        }
    }
}
