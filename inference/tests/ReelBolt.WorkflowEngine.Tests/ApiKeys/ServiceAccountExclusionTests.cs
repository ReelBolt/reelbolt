#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Inference;
using ReelBolt.WorkflowEngine.Tests.Editing;
using ReelBolt.WorkflowEngine.Tests.Marketing;
using Xunit;
using EditClock = ReelBolt.WorkflowEngine.Tests.Editing.ManualClock;
using static ReelBolt.WorkflowEngine.Tests.Editing.TimelineRetentionTestKit;

namespace ReelBolt.WorkflowEngine.Tests.ApiKeys;

/// <summary>public-api T-010: service accounts are never a person (JWTs, marketing, actorKind, expiry warnings, engine identity).</summary>
public sealed class ServiceAccountExclusionTests
{
    private static readonly Guid Org = Guid.NewGuid();

    // ---------------------------------------------------------------- JWT

    private static ClaimsPrincipal Principal(Guid user, string? actor = null, bool platformAdmin = false)
    {
        ClaimsIdentity id = new("test");
        id.AddClaim(new Claim("sub", user.ToString()));
        id.AddClaim(new Claim("org", Org.ToString()));
        id.AddClaim(new Claim("orgRole", "Member"));
        id.AddClaim(new Claim("platformAdmin", platformAdmin ? "true" : "false"));
        if (actor is not null) id.AddClaim(new Claim("act", actor));
        return new ClaimsPrincipal(id);
    }

    private static async Task<(Guid User, MembershipValidator.Membership Membership)> SeedServiceAsync(bool isAdmin = false)
    {
        using InferenceApiDbContext db = ApiKeyTestSupport.NewDb();
        Guid id = Guid.NewGuid();
        db.ApplicationUsers.Add(new ApplicationUser { Id = id, Email = "apikey+x@service.invalid", IsServiceAccount = true, IsAdmin = isAdmin });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = Org, UserId = id, Role = OrganizationRole.Member });
        await db.SaveChangesAsync();
        MembershipValidator v = new(new MemoryCache(new MemoryCacheOptions()), NullLogger<MembershipValidator>.Instance);
        return (id, await v.LookupAsync(db, id, Org, CancellationToken.None));
    }

    [Fact]
    public async Task A_service_account_session_token_is_refused()
    {
        (Guid user, MembershipValidator.Membership m) = await SeedServiceAsync();

        m.IsServiceAccount.Should().BeTrue();
        MembershipValidator.Apply(Principal(user), m, Org, out string? failure).Should().BeFalse();
        failure.Should().NotBeNullOrEmpty();
        MembershipValidator.Apply(Principal(user, actor: "someone-else"), m, Org, out _).Should().BeFalse();
    }

    [Fact]
    public async Task The_engine_on_behalf_of_token_is_accepted_and_never_platform_admin()
    {
        (Guid user, MembershipValidator.Membership m) = await SeedServiceAsync(isAdmin: true);
        ClaimsPrincipal p = Principal(user, actor: OnBehalfOfTokenIssuer.ActorValue, platformAdmin: true);

        MembershipValidator.Apply(p, m, Org, out _).Should().BeTrue();

        AuthClaims.ReadPlatformAdmin(p).Should().BeFalse();
    }

    [Fact]
    public async Task A_person_token_is_unchanged()
    {
        using InferenceApiDbContext db = ApiKeyTestSupport.NewDb();
        Guid id = Guid.NewGuid();
        db.ApplicationUsers.Add(new ApplicationUser { Id = id, Email = "p@x.y", IsAdmin = true });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = Org, UserId = id, Role = OrganizationRole.Member });
        await db.SaveChangesAsync();
        MembershipValidator v = new(new MemoryCache(new MemoryCacheOptions()), NullLogger<MembershipValidator>.Instance);
        MembershipValidator.Membership m = await v.LookupAsync(db, id, Org, CancellationToken.None);
        ClaimsPrincipal p = Principal(id, platformAdmin: true);

        MembershipValidator.Apply(p, m, Org, out _).Should().BeTrue();

        AuthClaims.ReadPlatformAdmin(p).Should().BeTrue();
    }

    // ---------------------------------------------------------------- engine identity

    [Fact]
    public async Task The_engine_resolves_a_service_account_initiator_to_a_member_identity()
    {
        string name = Guid.NewGuid().ToString();
        ServiceCollection services = new();
        services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        ServiceProvider sp = services.BuildServiceProvider();
        Guid svc = Guid.NewGuid();
        Guid execution = Guid.NewGuid();
        using (IServiceScope scope = sp.CreateScope())
        {
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            db.ApplicationUsers.Add(new ApplicationUser { Id = svc, Email = "apikey+k@service.invalid", IsServiceAccount = true });
            db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = Org, UserId = svc, Role = OrganizationRole.Member });
            db.WorkflowExecutions.Add(new WorkflowExecution { Id = execution, OrganizationId = Org, InitiatedByUserId = svc });
            await db.SaveChangesAsync();
        }

        ExecutionIdentity? identity = await new ExecutionUserResolver(sp.GetRequiredService<IServiceScopeFactory>())
            .ResolveAsync(execution, CancellationToken.None);

        identity.Should().NotBeNull();
        identity!.UserId.Should().Be(svc);
        identity.OrgRole.Should().Be(OrganizationRole.Member);
        identity.OrganizationId.Should().Be(Org);
    }

    // ---------------------------------------------------------------- marketing

    [Fact]
    public async Task Marketing_never_selects_a_service_account()
    {
        using MarketingHarness h = new();
        Guid svc = Guid.NewGuid();
        Guid person = Guid.NewGuid();
        using (InferenceApiDbContext db = h.NewDb())
        {
            db.ApplicationUsers.Add(new ApplicationUser { Id = svc, Email = "apikey+z@service.invalid", DisplayName = "CI key", IsServiceAccount = true, EmailVerifiedAt = DateTime.UtcNow });
            db.ApplicationUsers.Add(new ApplicationUser { Id = person, Email = "p@example.com", DisplayName = "Pat", EmailVerifiedAt = DateTime.UtcNow });
            db.MarketingConsents.Add(new MarketingConsent
            {
                Id = Guid.NewGuid(),
                Email = "apikey+z@service.invalid",
                UserId = svc,
                Status = MarketingConsentStatus.OptedIn,
                WordingVersion = "v1",
                Source = MarketingConsentSource.Signup
            });
            await db.SaveChangesAsync();
        }

        using (InferenceApiDbContext db = h.NewDb())
        {
            (await h.NewService(db).IsEmailVerifiedAsync(svc, CancellationToken.None)).Should().BeFalse();
            (await h.NewService(db).IsEmailVerifiedAsync(person, CancellationToken.None)).Should().BeTrue();
        }

        using (InferenceApiDbContext db = h.NewDb())
        {
            int changed = await new MarketingNameReconciler(db, h.Clock).ReconcileAsync(CancellationToken.None);
            changed.Should().Be(0);
        }
    }

    // ---------------------------------------------------------------- actorKind

    [Fact]
    public async Task A_version_made_by_an_api_key_carries_the_key_name_and_actorKind_apiKey()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db, revision: 3);
        Guid svc = Guid.NewGuid();
        Guid pat = Guid.NewGuid();
        db.ApplicationUsers.Add(new ApplicationUser { Id = svc, Email = "apikey+q@service.invalid", DisplayName = "Nightly render", IsServiceAccount = true });
        db.ApplicationUsers.Add(new ApplicationUser { Id = pat, Email = "pat@example.com", DisplayName = "Pat" });
        db.EditTimelineSnapshots.Add(new EditTimelineSnapshot
        {
            Id = Guid.NewGuid(),
            TimelineId = row.Id,
            Revision = 2,
            DocumentJson = "{}",
            CreatedAt = DateTime.UtcNow,
            Cause = EditTimelineSnapshotCause.Named,
            Name = "by key",
            CreatedByUserId = svc
        });
        db.EditTimelineSnapshots.Add(new EditTimelineSnapshot
        {
            Id = Guid.NewGuid(),
            TimelineId = row.Id,
            Revision = 1,
            DocumentJson = "{}",
            CreatedAt = DateTime.UtcNow,
            Cause = EditTimelineSnapshotCause.Named,
            Name = "by person",
            CreatedByUserId = pat
        });
        await db.SaveChangesAsync();
        EditTimelinesControllerHandle controller = new(Controller(db, project.OwnerId, new EditClock(DateTimeOffset.UtcNow)));

        EditTimelineVersionResponse[] versions = await controller.ListAsync(project.Id, row.Id);

        EditTimelineVersionResponse key = versions.Single(v => v.Revision == 2);
        key.CreatedByName.Should().Be("Nightly render");
        key.ActorKind.Should().Be("apiKey");
        versions.Single(v => v.Revision == 1).ActorKind.Should().Be("person");
    }

    private sealed class EditTimelinesControllerHandle(ReelBolt.Inference.Api.Controllers.EditTimelinesController c)
    {
        public async Task<EditTimelineVersionResponse[]> ListAsync(Guid projectId, Guid id) =>
            ((await c.ListVersions(projectId, id, CancellationToken.None)).Result as OkObjectResult)!
                .Value.Should().BeOfType<EditTimelineVersionsResponse>().Subject.Versions.ToArray();
    }

    // ---------------------------------------------------------------- expiry sweeper

    private sealed class Rig
    {
        public readonly EditClock Clock = new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
        public readonly List<UserActivityNotificationRequested> Sent = [];
        public readonly ServiceProvider Provider;
        public readonly PublicApiOptions Options = new() { Enabled = true };

        public Rig()
        {
            string name = Guid.NewGuid().ToString();
            Mock<IPublishEndpoint> publish = new();
            publish.Setup(p => p.Publish(It.IsAny<UserActivityNotificationRequested>(), It.IsAny<CancellationToken>()))
                .Callback<UserActivityNotificationRequested, CancellationToken>((m, _) => Sent.Add(m))
                .Returns(Task.CompletedTask);
            ServiceCollection services = new();
            services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(name)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddSingleton<ITenantContext>(new TenantContext(new HttpContextAccessor()));
            services.AddScoped(_ => publish.Object);
            Provider = services.BuildServiceProvider();
        }

        public ApiKeyExpirySweeper Sweeper() =>
            new(Provider.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Options.Options.Create(Options),
                NullLogger<ApiKeyExpirySweeper>.Instance, Clock);

        public async Task<T> DbAsync<T>(Func<InferenceApiDbContext, Task<T>> f)
        {
            using IServiceScope scope = Provider.CreateScope();
            return await f(scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>());
        }
    }

    private sealed record Seed(Guid OrgId, Guid Creator, Guid Owner, Guid Admin, Guid Plain, Guid Service, Guid KeyId);

    private static async Task<Seed> SeedKeyAsync(Rig rig, DateTime? expires, Guid? orgId = null)
    {
        Guid org = orgId ?? Guid.NewGuid();
        Seed s = new(org, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await rig.DbAsync(async db =>
        {
            db.Organizations.Add(new Organization { Id = org, Name = "Org", Slug = "o-" + org.ToString("N")[..8], Kind = OrganizationKind.Team });
            foreach ((Guid id, OrganizationRole role, bool svc) in new[]
            {
                (s.Creator, OrganizationRole.Member, false), (s.Owner, OrganizationRole.Owner, false), (s.Admin, OrganizationRole.Admin, false),
                (s.Plain, OrganizationRole.Member, false), (s.Service, OrganizationRole.Member, true)
            })
            {
                db.ApplicationUsers.Add(new ApplicationUser { Id = id, Email = $"{id:N}@example.com", IsServiceAccount = svc });
                db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = org, UserId = id, Role = role });
            }

            db.ApiKeys.Add(new ApiKey
            {
                Id = s.KeyId,
                OrganizationId = org,
                CreatedByUserId = s.Creator,
                ServiceUserId = s.Service,
                Name = "Deploy key",
                KeyId = Guid.NewGuid().ToString("N")[..12],
                SecretHash = new string('a', 64),
                LastFour = "abcd",
                Scopes = ["runs.read"],
                ExpiresAt = expires
            });
            await db.SaveChangesAsync();
            return 0;
        });
        return s;
    }

    [Fact]
    public async Task Seven_day_warning_goes_to_creator_owner_and_admin_once_never_a_service_account()
    {
        Rig rig = new();
        Seed s = await SeedKeyAsync(rig, rig.Clock.Utc.AddDays(5).AddHours(3));

        int first = await rig.Sweeper().SweepOnceAsync(CancellationToken.None);
        int second = await rig.Sweeper().SweepOnceAsync(CancellationToken.None);

        first.Should().Be(3);
        second.Should().Be(0);
        rig.Sent.Select(e => e.RecipientUserId!.Value).Should().BeEquivalentTo([s.Creator, s.Owner, s.Admin]);
        rig.Sent.Should().OnlyContain(e => e.Type == "api_key_expiring" && e.Subject == "Deploy key" && e.ProjectId == Guid.Empty && e.OrganizationId == s.OrgId);
        rig.Sent[0].Details.Should().Be(rig.Clock.Utc.AddDays(5).AddHours(3).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
        ApiKey key = await rig.DbAsync(db => db.ApiKeys.IgnoreQueryFilters().SingleAsync());
        key.ExpiryWarned7dAt.Should().NotBeNull();
        key.ExpiryWarned1dAt.Should().BeNull();
    }

    [Fact]
    public async Task One_day_warning_follows_the_seven_day_one_after_time_passes()
    {
        Rig rig = new();
        await SeedKeyAsync(rig, rig.Clock.Utc.AddDays(5));
        await rig.Sweeper().SweepOnceAsync(CancellationToken.None);
        rig.Sent.Clear();

        rig.Clock.Advance(TimeSpan.FromDays(4).Add(TimeSpan.FromHours(2)));
        int sent = await rig.Sweeper().SweepOnceAsync(CancellationToken.None);

        sent.Should().Be(3);
        (await rig.DbAsync(db => db.ApiKeys.IgnoreQueryFilters().SingleAsync())).ExpiryWarned1dAt.Should().NotBeNull();
        (await rig.Sweeper().SweepOnceAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task No_expiry_revoked_far_off_and_already_expired_keys_are_skipped_and_the_flag_turns_it_off()
    {
        Rig rig = new();
        await SeedKeyAsync(rig, null);
        await SeedKeyAsync(rig, rig.Clock.Utc.AddDays(30));
        await SeedKeyAsync(rig, rig.Clock.Utc.AddDays(-1));
        Seed revoked = await SeedKeyAsync(rig, rig.Clock.Utc.AddDays(2));
        await rig.DbAsync(async db =>
        {
            (await db.ApiKeys.IgnoreQueryFilters().SingleAsync(k => k.Id == revoked.KeyId)).RevokedAt = rig.Clock.Utc;
            await db.SaveChangesAsync();
            return 0;
        });

        (await rig.Sweeper().SweepOnceAsync(CancellationToken.None)).Should().Be(0);

        Seed due = await SeedKeyAsync(rig, rig.Clock.Utc.AddDays(2));
        rig.Options.ExpiryWarning.Enabled = false;
        (await rig.Sweeper().SweepOnceAsync(CancellationToken.None)).Should().Be(0);
        rig.Sent.Should().BeEmpty();
        (await rig.DbAsync(db => db.ApiKeys.IgnoreQueryFilters().SingleAsync(k => k.Id == due.KeyId))).ExpiryWarned7dAt.Should().BeNull();
    }
}
