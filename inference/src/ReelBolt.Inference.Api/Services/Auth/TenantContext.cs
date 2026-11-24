using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Auth;

/// <summary>
/// <see cref="ITenantContext"/> backed by an AsyncLocal scope (background work) with the validated
/// request token as fallback. An explicit scope always wins over the request.
/// </summary>
public sealed class TenantContext(IHttpContextAccessor httpContextAccessor) : ITenantContext
{
    private sealed record Scope(Guid? OrganizationId, bool IsSystem);

    private readonly AsyncLocal<Scope?> _scope = new();

    public Guid? OrganizationId =>
        _scope.Value is { } s ? s.OrganizationId : AuthClaims.ReadOrganizationId(httpContextAccessor.HttpContext?.User);

    public bool IsSystem => _scope.Value?.IsSystem ?? false;

    public IDisposable BeginSystemScope() => Push(new Scope(null, true));

    public IDisposable BeginOrganizationScope(Guid organizationId) => Push(new Scope(organizationId, false));

    private Restore Push(Scope next)
    {
        Scope? previous = _scope.Value;
        _scope.Value = next;
        return new Restore(_scope, previous);
    }

    private sealed class Restore(AsyncLocal<Scope?> slot, Scope? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                slot.Value = previous;
        }
    }
}
