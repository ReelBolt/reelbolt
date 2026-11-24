using System.Security.Claims;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Auth;

/// <summary>
/// Extracts current user identity from the HTTP context JWT claims.
/// </summary>
public class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid UserId
    {
        get
        {
            string? sub = _httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? _httpContextAccessor.HttpContext?.User.FindFirstValue("sub");
            if (!Guid.TryParse(sub, out Guid id))
                throw new InvalidOperationException("Authenticated request is missing a valid user id claim.");
            return id;
        }
    }

    public string Email =>
        _httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.Email)
        ?? _httpContextAccessor.HttpContext?.User.FindFirstValue("email")
        ?? string.Empty;

    public bool IsAuthenticated =>
        _httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated ?? false;

    public Guid? OrganizationId => AuthClaims.ReadOrganizationId(_httpContextAccessor.HttpContext?.User);

    public OrganizationRole? OrgRole => AuthClaims.ReadOrgRole(_httpContextAccessor.HttpContext?.User);

    public bool EmailVerified => AuthClaims.ReadEmailVerified(_httpContextAccessor.HttpContext?.User);

    public bool IsPlatformAdmin => AuthClaims.ReadPlatformAdmin(_httpContextAccessor.HttpContext?.User);
}
