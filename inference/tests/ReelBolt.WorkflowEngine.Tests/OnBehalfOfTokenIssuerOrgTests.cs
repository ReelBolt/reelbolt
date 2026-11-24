#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Moq;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// D6: engine on-behalf-of tokens carry the execution's org and the initiator's current role, NEVER platform
/// authority, and a user who is no longer a member gets no token at all (fail closed).
/// </summary>
public class OnBehalfOfTokenIssuerOrgTests
{
    private const string Key = "0123456789abcdef0123456789abcdef-test";
    private static readonly Guid Org = Guid.NewGuid();

    private static OnBehalfOfTokenIssuer NewIssuer() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "iss",
            ["Jwt:Audience"] = "aud",
            ["Jwt:SigningKey"] = Key
        }).Build());

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Admin)]
    [InlineData(OrganizationRole.Member)]
    public void Issue_CarriesOrgAndRole_AndNeverAnyPlatformAdminClaim(OrganizationRole role)
    {
        string token = NewIssuer().Issue(Guid.NewGuid(), "a@b.c", Org, role, Guid.NewGuid());
        JsonWebToken jwt = new(token);
        jwt.GetPayloadValue<string>("org").Should().Be(Org.ToString());
        jwt.GetPayloadValue<string>("orgRole").Should().Be(role.ToString());
        jwt.Claims.Should().NotContain(c => c.Type == "platformAdmin" || c.Type == "isAdmin",
            "an engine call never carries platform authority, even for a platform-admin initiator");
    }

    private sealed class Capture : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Auth;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c)
        {
            Auth = r.Headers.Authorization;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static ServiceProvider Services(string db)
    {
        ServiceCollection sc = new();
        sc.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(db));
        return sc.BuildServiceProvider();
    }

    private static async Task<(Guid Exec, Guid User)> Seed(ServiceProvider sp, bool member, bool platformAdmin, OrganizationRole role = OrganizationRole.Admin)
    {
        using IServiceScope scope = sp.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        Guid user = Guid.NewGuid(), exec = Guid.NewGuid();
        db.ApplicationUsers.Add(new ApplicationUser { Id = user, Email = "init@x.y", IsAdmin = platformAdmin });
        db.WorkflowExecutions.Add(new WorkflowExecution { Id = exec, OrganizationId = Org, InitiatedByUserId = user });
        if (member)
            db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = Org, UserId = user, Role = role });
        await db.SaveChangesAsync();
        return (exec, user);
    }

    private static async Task<AuthenticationHeaderValue?> SendFor(ServiceProvider sp, Guid exec)
    {
        Mock<IWorkflowExecutionContextAccessor> acc = new();
        acc.SetupGet(a => a.Current).Returns(new WorkflowExecutionContext { ExecutionId = exec, ProjectId = Guid.NewGuid(), CorrelationId = "c" });
        Capture inner = new();
        InferenceApiAuthHandler handler = new(acc.Object,
            new ExecutionUserResolver(sp.GetRequiredService<IServiceScopeFactory>()), NewIssuer(),
            NullLogger<InferenceApiAuthHandler>.Instance)
        { InnerHandler = inner };
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://x") };
        await client.GetAsync("/y");
        return inner.Auth;
    }

    [Fact]
    public async Task Resolver_UsesExecutionOrganization_AndInitiatorsCurrentRole()
    {
        using ServiceProvider sp = Services(nameof(Resolver_UsesExecutionOrganization_AndInitiatorsCurrentRole));
        var (exec, user) = await Seed(sp, member: true, platformAdmin: true, OrganizationRole.Admin);

        ExecutionIdentity? id = await new ExecutionUserResolver(sp.GetRequiredService<IServiceScopeFactory>())
            .ResolveAsync(exec, CancellationToken.None);

        id.Should().NotBeNull();
        id!.UserId.Should().Be(user);
        id.OrganizationId.Should().Be(Org);
        id.OrgRole.Should().Be(OrganizationRole.Admin);
    }

    [Fact]
    public async Task RemovedMember_GetsNoToken()
    {
        using ServiceProvider sp = Services(nameof(RemovedMember_GetsNoToken));
        var (exec, _) = await Seed(sp, member: false, platformAdmin: false);

        (await SendFor(sp, exec)).Should().BeNull("a user who is no longer a member of the execution's org must not be impersonated");
    }

    [Fact]
    public async Task CurrentMember_GetsTokenWithOrgClaims_AndNoPlatformAuthority()
    {
        using ServiceProvider sp = Services(nameof(CurrentMember_GetsTokenWithOrgClaims_AndNoPlatformAuthority));
        var (exec, _) = await Seed(sp, member: true, platformAdmin: true, OrganizationRole.Member);

        AuthenticationHeaderValue? auth = await SendFor(sp, exec);

        auth.Should().NotBeNull();
        JsonWebToken jwt = new(auth!.Parameter);
        jwt.GetPayloadValue<string>("orgRole").Should().Be("Member");
        jwt.Claims.Should().NotContain(c => c.Type == "platformAdmin" || c.Type == "isAdmin");
    }

    private static async Task<(Guid Exec, Guid User, Guid KeyId)> SeedKeyed(
        ServiceProvider sp, bool revoked = false, DateTime? expires = null, bool keyExists = true)
    {
        using IServiceScope scope = sp.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        Guid user = Guid.NewGuid(), exec = Guid.NewGuid(), keyId = Guid.NewGuid();
        db.ApplicationUsers.Add(new ApplicationUser { Id = user, Email = "svc@service.invalid", IsServiceAccount = true });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = Org, UserId = user, Role = OrganizationRole.Member });
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = exec,
            OrganizationId = Org,
            InitiatedByUserId = user,
            ApiKeyId = keyId
        });
        if (keyExists)
        {
            db.ApiKeys.Add(new ApiKey
            {
                Id = keyId,
                OrganizationId = Org,
                ServiceUserId = user,
                CreatedByUserId = user,
                Name = "key",
                KeyId = Guid.NewGuid().ToString("N")[..12],
                SecretHash = "h",
                LastFour = "abcd",
                RevokedAt = revoked ? DateTime.UtcNow : null,
                ExpiresAt = expires
            });
        }
        await db.SaveChangesAsync();
        return (exec, user, keyId);
    }

    [Fact]
    public async Task Resolver_KeyStartedRun_KeepsTheKeyId_AndActiveKeyGetsTokenWithIt()
    {
        using ServiceProvider sp = Services(nameof(Resolver_KeyStartedRun_KeepsTheKeyId_AndActiveKeyGetsTokenWithIt));
        var (exec, user, keyId) = await SeedKeyed(sp);

        ExecutionIdentity? id = await new ExecutionUserResolver(sp.GetRequiredService<IServiceScopeFactory>())
            .ResolveAsync(exec, CancellationToken.None);

        id.Should().NotBeNull();
        id!.ApiKeyId.Should().Be(keyId);
        AuthenticationHeaderValue? auth = await SendFor(sp, exec);
        new JsonWebToken(auth!.Parameter).GetPayloadValue<string>("api_key_id").Should().Be(keyId.ToString());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Resolver_KeyStartedRun_GetsNoTokenWhenTheKeyIsRevokedOrMissing(bool revoked, bool keyExists)
    {
        using ServiceProvider sp = Services($"Resolver_key_{revoked}_{keyExists}");
        var (exec, _, _) = await SeedKeyed(sp, revoked: revoked, keyExists: keyExists);

        (await new ExecutionUserResolver(sp.GetRequiredService<IServiceScopeFactory>())
                .ResolveAsync(exec, CancellationToken.None))
            .Should().BeNull("a revoked or missing key must stop minting on-behalf-of tokens");
        (await SendFor(sp, exec)).Should().BeNull();
    }

    [Fact]
    public async Task Resolver_KeyStartedRun_GetsNoTokenWhenTheKeyExpired()
    {
        using ServiceProvider sp = Services(nameof(Resolver_KeyStartedRun_GetsNoTokenWhenTheKeyExpired));
        var (exec, _, _) = await SeedKeyed(sp, expires: DateTime.UtcNow.AddMinutes(-1));

        (await new ExecutionUserResolver(sp.GetRequiredService<IServiceScopeFactory>())
                .ResolveAsync(exec, CancellationToken.None))
            .Should().BeNull();
    }
}
