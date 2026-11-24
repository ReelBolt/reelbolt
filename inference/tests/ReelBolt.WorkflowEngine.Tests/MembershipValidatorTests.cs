#nullable enable
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class MembershipValidatorTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static MembershipValidator NewValidator() =>
        new(new MemoryCache(new MemoryCacheOptions()), NullLogger<MembershipValidator>.Instance);

    private static ClaimsPrincipal Principal(Guid user, string? org, string role, bool? platformAdmin = null, bool? legacyAdmin = null)
    {
        ClaimsIdentity id = new("test");
        id.AddClaim(new Claim("sub", user.ToString()));
        if (org is not null) id.AddClaim(new Claim("org", org));
        id.AddClaim(new Claim("orgRole", role));
        if (platformAdmin is { } p) id.AddClaim(new Claim("platformAdmin", p.ToString().ToLowerInvariant()));
        if (legacyAdmin is { } l) id.AddClaim(new Claim("isAdmin", l.ToString().ToLowerInvariant()));
        return new ClaimsPrincipal(id);
    }

    private static async Task<Guid> SeedUser(InferenceApiDbContext db, bool isAdmin = false, OrganizationRole? role = null)
    {
        Guid user = Guid.NewGuid();
        db.ApplicationUsers.Add(new ApplicationUser { Id = user, Email = "u@x.y", IsAdmin = isAdmin });
        if (role is { } r)
            db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = Org, UserId = user, Role = r });
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<bool> Run(MembershipValidator v, InferenceApiDbContext db, ClaimsPrincipal p, Guid user)
    {
        Guid? org = AuthClaims.ReadOrganizationId(p);
        MembershipValidator.Membership m = await v.LookupAsync(db, user, org, CancellationToken.None);
        return MembershipValidator.Apply(p, m, org, out _);
    }

    [Fact]
    public async Task Member_PassesAndRoleComesFromTheDatabase_NotTheToken()
    {
        using InferenceApiDbContext db = NewDb();
        Guid user = await SeedUser(db, role: OrganizationRole.Member);
        ClaimsPrincipal p = Principal(user, Org.ToString(), "Owner"); // token claims Owner, database says Member
        (await Run(NewValidator(), db, p, user)).Should().BeTrue();
        AuthClaims.ReadOrgRole(p).Should().Be(OrganizationRole.Member);
        p.FindAll("orgRole").Should().HaveCount(1);
    }

    [Fact]
    public async Task RemovedMember_IsRejected()
    {
        using InferenceApiDbContext db = NewDb();
        Guid user = await SeedUser(db, role: null);
        (await Run(NewValidator(), db, Principal(user, Org.ToString(), "Admin"), user)).Should().BeFalse();
    }

    [Fact]
    public async Task UnknownUser_IsRejected()
    {
        using InferenceApiDbContext db = NewDb();
        Guid ghost = Guid.NewGuid();
        (await Run(NewValidator(), db, Principal(ghost, Org.ToString(), "Admin"), ghost)).Should().BeFalse();
    }

    [Fact]
    public async Task PlatformAdminClaim_IsDroppedWhenTheDatabaseDisagrees()
    {
        using InferenceApiDbContext db = NewDb();
        Guid user = await SeedUser(db, isAdmin: false, role: OrganizationRole.Owner);
        ClaimsPrincipal p = Principal(user, Org.ToString(), "Owner", platformAdmin: true);
        (await Run(NewValidator(), db, p, user)).Should().BeTrue();
        AuthClaims.ReadPlatformAdmin(p).Should().BeFalse("a stale claim never grants platform authority");
    }

    [Fact]
    public async Task OrgOwner_WithoutPlatformAdminClaim_IsNotPlatformAdmin_EvenIfDatabaseSaysAdmin()
    {
        using InferenceApiDbContext db = NewDb();
        Guid user = await SeedUser(db, isAdmin: true, role: OrganizationRole.Owner);
        ClaimsPrincipal p = Principal(user, Org.ToString(), "Owner"); // OBO-style: no admin claim at all
        (await Run(NewValidator(), db, p, user)).Should().BeTrue();
        AuthClaims.ReadPlatformAdmin(p).Should().BeFalse("the token must carry the claim; the database only cross-checks it");
    }

    [Fact]
    public async Task LegacyIsAdminAlias_IsHonouredOnlyWhenDatabaseAgrees()
    {
        using InferenceApiDbContext db = NewDb();
        Guid admin = await SeedUser(db, isAdmin: true, role: OrganizationRole.Member);
        ClaimsPrincipal p = Principal(admin, Org.ToString(), "Member", legacyAdmin: true);
        (await Run(NewValidator(), db, p, admin)).Should().BeTrue();
        AuthClaims.ReadPlatformAdmin(p).Should().BeTrue();
        p.FindFirst("platformAdmin")!.Value.Should().Be("true");
        p.FindFirst("isAdmin")!.Value.Should().Be("true");
    }

    [Fact]
    public async Task TokenWithoutOrg_PassesWithoutAnOrgRole()
    {
        using InferenceApiDbContext db = NewDb();
        Guid user = await SeedUser(db);
        ClaimsIdentity id = new("test");
        id.AddClaim(new Claim("sub", user.ToString()));
        ClaimsPrincipal p = new(id);
        (await Run(NewValidator(), db, p, user)).Should().BeTrue();
        AuthClaims.ReadOrgRole(p).Should().BeNull();
        AuthClaims.ReadOrganizationId(p).Should().BeNull();
    }

    private static ClaimsPrincipal WithIat(ClaimsPrincipal p, DateTime issuedAtUtc)
    {
        p.Identities.First().AddClaim(new Claim("iat", new DateTimeOffset(issuedAtUtc).ToUnixTimeSeconds().ToString()));
        return p;
    }

    [Fact]
    public async Task TokenIssuedBeforeAPasswordReset_IsRejected()
    {
        using InferenceApiDbContext db = NewDb();
        Guid user = await SeedUser(db, role: OrganizationRole.Member);
        DateTime reset = DateTime.UtcNow;
        db.ApplicationUsers.Single(u => u.Id == user).TokensInvalidatedAt = reset;
        await db.SaveChangesAsync();

        ClaimsPrincipal before = WithIat(Principal(user, Org.ToString(), "Member"), reset.AddMinutes(-5));
        ClaimsPrincipal after = WithIat(Principal(user, Org.ToString(), "Member"), reset.AddMinutes(1));
        ClaimsPrincipal noIat = Principal(user, Org.ToString(), "Member");

        (await Run(NewValidator(), db, before, user)).Should().BeFalse("it predates the invalidation (401)");
        (await Run(NewValidator(), db, after, user)).Should().BeTrue();
        (await Run(NewValidator(), db, noIat, user)).Should().BeFalse("no iat fails closed once an invalidation exists");
    }

    [Fact]
    public async Task TokenIssuedInTheSameSecondAsTheReset_StillWorks()
    {
        using InferenceApiDbContext db = NewDb();
        Guid user = await SeedUser(db, role: OrganizationRole.Member);
        DateTime reset = new(2026, 10, 6, 12, 0, 0, 750, DateTimeKind.Utc);
        db.ApplicationUsers.Single(u => u.Id == user).TokensInvalidatedAt = reset;
        await db.SaveChangesAsync();

        ClaimsPrincipal sameSecond = WithIat(Principal(user, Org.ToString(), "Member"), new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc));
        (await Run(NewValidator(), db, sameSecond, user)).Should().BeTrue();
    }

    [Fact]
    public async Task NeverInvalidatedUser_AcceptsTokensWithoutIat()
    {
        using InferenceApiDbContext db = NewDb();
        Guid user = await SeedUser(db, role: OrganizationRole.Member);
        (await Run(NewValidator(), db, Principal(user, Org.ToString(), "Member"), user)).Should().BeTrue();
    }

    [Fact]
    public async Task Lookup_IsCachedWithinTheTtl_AndInvalidateAppliesChangesImmediately()
    {
        using InferenceApiDbContext db = NewDb();
        Guid user = await SeedUser(db, role: OrganizationRole.Member);
        MembershipValidator v = NewValidator();

        (await v.LookupAsync(db, user, Org, CancellationToken.None)).Role.Should().Be(OrganizationRole.Member);

        db.OrganizationMemberships.RemoveRange(db.OrganizationMemberships.Where(m => m.UserId == user));
        await db.SaveChangesAsync();

        (await v.LookupAsync(db, user, Org, CancellationToken.None)).Role.Should().Be(OrganizationRole.Member, "served from the 30 s cache");
        v.Invalidate(user, Org);
        (await v.LookupAsync(db, user, Org, CancellationToken.None)).Role.Should().BeNull();
        MembershipValidator.Ttl.Should().Be(TimeSpan.FromSeconds(30));
    }
}
