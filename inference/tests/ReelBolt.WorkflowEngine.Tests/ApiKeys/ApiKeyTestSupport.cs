using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.WorkflowEngine.Tests.ApiKeys;

internal static class ApiKeyTestSupport
{
    public static InferenceApiDbContext NewDb(string? name = null) => new(
        new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    public sealed class FakeUser(Guid? orgId, OrganizationRole? role, Guid? userId = null) : ICurrentUser
    {
        public Guid UserId { get; } = userId ?? Guid.NewGuid();
        public string Email => "t@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId => orgId;
        public OrganizationRole? OrgRole => role;
        public bool IsPlatformAdmin => false;
    }

    public sealed class FakeKeyContext(Guid? keyId = null, Guid[]? projects = null) : IApiKeyContext
    {
        public bool IsApiKey => keyId is not null;
        public Guid? KeyId => keyId;
        public Guid? OrganizationId => null;
        public System.Collections.Generic.IReadOnlyList<Guid>? AllowedProjectIds => projects;
        public System.Collections.Generic.IReadOnlyCollection<string> Scopes => [];
    }

    public static async Task<(ApplicationUser User, Organization Org)> SeedCreatorAsync(
        InferenceApiDbContext db, OrganizationRole role = OrganizationRole.Owner, bool platformAdmin = false)
    {
        ApplicationUser user = new() { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@example.com", DisplayName = "Creator", IsAdmin = platformAdmin, EmailVerifiedAt = DateTime.UtcNow };
        Organization org = new() { Id = Guid.NewGuid(), Name = "Org" };
        db.ApplicationUsers.Add(user);
        db.Organizations.Add(org);
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = org.Id, UserId = user.Id, Role = role });
        await db.SaveChangesAsync();
        return (user, org);
    }

    public static CreateApiKeyCommand Command(Guid orgId, Guid creator, DateTime? expires = null, string[]? scopes = null, Guid[]? projects = null) =>
        new(orgId, creator, "CI key", scopes ?? ["runs.read"], projects, expires, null, null, null);

    public static ApiKeyService Service(InferenceApiDbContext db, TimeProvider? time = null) =>
        new(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<ApiKeyService>.Instance, time);
}
