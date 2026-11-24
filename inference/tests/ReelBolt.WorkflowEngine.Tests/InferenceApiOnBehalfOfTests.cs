#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Moq;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Inference;
using System.Text;

namespace ReelBolt.WorkflowEngine.Tests;

public class InferenceApiOnBehalfOfTests
{
    private static readonly Guid Org = Guid.Parse("5c1f0000-0000-4000-8000-000000000002");
    private const string Key = "0123456789abcdef0123456789abcdef-test";

    private static OnBehalfOfTokenIssuer NewIssuer(TimeProvider? t = null) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "iss",
            ["Jwt:Audience"] = "aud",
            ["Jwt:SigningKey"] = Key
        }).Build(), t);

    private static async Task<TokenValidationResult> Validate(string token) =>
        await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "iss",
            ValidAudience = "aud",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
        });

    [Fact]
    public async Task Issue_CarriesGoApiClaimShapeAndMarkers_AndExpiresInFiveMinutes()
    {
        Guid user = Guid.NewGuid(), exec = Guid.NewGuid();
        string token = NewIssuer().Issue(user, "a@b.c", Org, OrganizationRole.Member, exec);
        TokenValidationResult r = await Validate(token);
        r.IsValid.Should().BeTrue();
        JsonWebToken jwt = (JsonWebToken)r.SecurityToken;
        jwt.GetPayloadValue<string>("sub").Should().Be(user.ToString());
        jwt.GetPayloadValue<string>("email").Should().Be("a@b.c");
        jwt.GetPayloadValue<string>("org").Should().Be(Org.ToString());
        jwt.GetPayloadValue<string>("orgRole").Should().Be("Member");
        jwt.GetPayloadValue<string>("act").Should().Be("workflow-engine");
        jwt.GetPayloadValue<string>("executionId").Should().Be(exec.ToString());
        (jwt.ValidTo - jwt.ValidFrom).Should().BeCloseTo(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Issue_TokenIsRejectedAfterExpiry()
    {
        string token = NewIssuer().Issue(Guid.NewGuid(), "a@b.c", Org, OrganizationRole.Member, Guid.NewGuid(), TimeSpan.FromSeconds(-600));
        (await Validate(token)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Issue_RefusesShortSigningKey()
    {
        OnBehalfOfTokenIssuer issuer = new(new ConfigurationBuilder().Build());
        Action act = () => issuer.Issue(Guid.NewGuid(), "a", Org, OrganizationRole.Member, Guid.NewGuid());
        act.Should().Throw<InvalidOperationException>();
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

    private static async Task<AuthenticationHeaderValue?> Send(
        WorkflowExecutionContext? ctx, ExecutionIdentity? user, Guid? expectExec = null)
    {
        Mock<IWorkflowExecutionContextAccessor> acc = new();
        acc.SetupGet(a => a.Current).Returns(ctx);
        Mock<IExecutionUserResolver> users = new();
        users.Setup(u => u.ResolveAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(user);
        Capture inner = new();
        InferenceApiAuthHandler h = new(acc.Object, users.Object, NewIssuer(), NullLogger<InferenceApiAuthHandler>.Instance)
        { InnerHandler = inner };
        using HttpClient c = new(h) { BaseAddress = new Uri("http://x") };
        await c.GetAsync("/y");
        return inner.Auth;
    }

    private static WorkflowExecutionContext Ctx() =>
        new() { ExecutionId = Guid.NewGuid(), ProjectId = Guid.NewGuid(), CorrelationId = "c" };

    [Fact]
    public async Task Handler_AttachesBearerForInitiator() =>
        (await Send(Ctx(), new ExecutionIdentity(Guid.NewGuid(), "a@b.c", Org, OrganizationRole.Member)))!.Scheme.Should().Be("Bearer");

    [Fact]
    public async Task Handler_NoContext_SendsNoToken() =>
        (await Send(null, new ExecutionIdentity(Guid.NewGuid(), "a@b.c", Org, OrganizationRole.Owner))).Should().BeNull();

    [Fact]
    public async Task Handler_NoInitiator_SendsNoToken_NeverAServiceIdentity() =>
        (await Send(Ctx(), null)).Should().BeNull();
}

/// <summary>Engine code must reach the Inference API only through the named "InferenceApi" client.</summary>
public class InferenceApiClientDriftGuardTests
{
    [Fact]
    public void EngineSource_DoesNotBuildAdHocClientsForTheInferenceApi()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "ReelBolt.WorkflowEngine")))
            dir = Path.GetDirectoryName(dir)!;
        dir.Should().NotBeNull();
        string[] offenders = Directory.EnumerateFiles(Path.Combine(dir!, "src", "ReelBolt.WorkflowEngine"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.EndsWith("WorkflowEngineServiceCollectionExtensions.cs"))
            .Where(f => File.ReadAllText(f).Contains("InferenceApi:BaseUrl"))
            .ToArray();
        offenders.Should().BeEmpty("only the named InferenceApi client registration may read InferenceApi:BaseUrl");
    }
}
