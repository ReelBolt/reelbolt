using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Shared.Auth;

/// <summary>
/// Provides access to the current authenticated user's identity.
/// </summary>
public interface ICurrentUser
{
    /// <summary>Gets the user ID from the JWT sub claim.</summary>
    Guid UserId { get; }

    /// <summary>Gets the user's email from the JWT email claim.</summary>
    string Email { get; }

    /// <summary>Returns true if the user is authenticated.</summary>
    bool IsAuthenticated { get; }

    /// <summary>The caller's active organization (JWT <c>org</c> claim), or null when the token carries none.</summary>
    Guid? OrganizationId { get; }

    /// <summary>
    /// The caller's role in <see cref="OrganizationId"/>. When the Inference API's membership validator
    /// ran, this is the database role, not the role the token was issued with.
    /// </summary>
    OrganizationRole? OrgRole { get; }

    /// <summary>
    /// Platform authority (JWT <c>platformAdmin</c>, falling back to the legacy <c>isAdmin</c> alias),
    /// cross-checked against <c>application_users.is_admin</c> by the membership validator.
    /// Being an org Owner never implies it.
    /// </summary>
    bool IsPlatformAdmin { get; }

    /// <summary>
    /// False only when the JWT carries <c>emailVerified: false</c>. Running workflows requires true
    /// (403 <c>EMAIL_NOT_VERIFIED</c>); admin-created and migrated users are verified.
    /// </summary>
    bool EmailVerified => true;

    /// <summary>Obsolete alias of <see cref="IsPlatformAdmin"/>.</summary>
    [Obsolete("Use IsPlatformAdmin. IsAdmin is a one-release alias.")]
    bool IsAdmin => IsPlatformAdmin;
}
