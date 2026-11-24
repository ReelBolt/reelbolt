using System;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Test scaffolding for the tenant-scoped controllers. Every test user lives in an organization
/// of their own (<see cref="OrgOf"/>), so "somebody else's project" is a project in another
/// organization, which D5 answers with 404.
/// </summary>
internal static class TestTenancy
{
    /// <summary>A stable organization id derived from a user id.</summary>
    public static Guid OrgOf(Guid userId)
    {
        byte[] bytes = userId.ToByteArray();
        bytes[0] ^= 0x5A;
        bytes[15] ^= 0xA5;
        return new Guid(bytes);
    }

    /// <summary>
    /// A tenant a test can re-point after seeding: starts in a system scope (so setup sees every
    /// row), then <see cref="ActAs"/> narrows it to one user's organization.
    /// </summary>
    public sealed class MutableTenant : ITenantContext
    {
        public Guid? OrganizationId { get; private set; }
        public bool IsSystem { get; private set; } = true;
        public void ActAs(Guid userId) => ActAsOrganization(OrgOf(userId));
        public void ActAsOrganization(Guid? organizationId) { OrganizationId = organizationId; IsSystem = false; }
        public IDisposable BeginSystemScope() => throw new NotSupportedException();
        public IDisposable BeginOrganizationScope(Guid organizationId) => throw new NotSupportedException();
    }

    public static IProjectAccessService Access(InferenceApiDbContext db, ICurrentUser user) =>
        new ProjectAccessService(db, user);
}
