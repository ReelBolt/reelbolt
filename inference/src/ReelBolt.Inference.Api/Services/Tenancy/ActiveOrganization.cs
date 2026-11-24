using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Services.Tenancy;

internal static class ActiveOrganization
{
    /// <summary>
    /// The caller's active organization, for stamping a row being created. A create site that has
    /// not already authorized the organization reaches here only with a token that has none, which
    /// is a client bug (the filters would hide the row anyway), so it fails loudly.
    /// </summary>
    public static Guid Require(ICurrentUser user) =>
        user.OrganizationId ?? throw new InvalidOperationException("The request has no active organization.");
}
