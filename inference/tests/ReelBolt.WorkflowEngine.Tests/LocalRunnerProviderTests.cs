using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Services.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The LocalRunner provider kind: a model on the user's own machine, relayed through their paired desktop runner.
/// Covers the admin boundary (capability allowlist, scope, device ownership, no key), the relay client (address,
/// token, failure mapping), the failure class, the zero-credit metering and the device listing.
/// </summary>
public class LocalRunnerProviderTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeUser(Guid? orgId, OrganizationRole? role, bool platformAdmin = false) : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "t@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId => orgId;
        public OrganizationRole? OrgRole => role;
        public bool IsPlatformAdmin => platformAdmin;
    }

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    private sealed class Harness
    {
        public required InferenceApiDbContext Db { get; init; }
        public required Mock<IChatClientFactory> Chat { get; init; }
        public required Mock<ISecretProtector> Secrets { get; init; }
        public required InferenceProviderAdminService Service { get; init; }
        public List<ResolvedInferenceProvider> Resolved { get; } = [];
        public List<ChatOptions?> Options { get; } = [];

        public OrgInferenceProvidersController Org(Guid? orgId, OrganizationRole? role) => new(new FakeUser(orgId, role), Service);

        public InferenceProvidersController Platform() => new(
            Db, new FakeUser(null, null, platformAdmin: true),
            Chat.Object, new Mock<ITranscriptionClientFactory>().Object,
            new Mock<IDecisionClientFactory>().Object, new Mock<IVideoGenerationClientFactory>().Object,
            new Mock<ISpeechSynthesisClientFactory>().Object, Secrets.Object,
            new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>().Object);

        public OrgRunnerLlmController Listing(Guid? orgId, OrganizationRole? role) => new(Db, new FakeUser(orgId, role), new FixedTime(Now));

        public async Task<Guid> Device(Guid org, bool revoked = false, RunnerKind kind = RunnerKind.Desktop, string? capabilities = null,
            DateTime? lastSeen = null, string name = "Studio PC")
        {
            RunnerDevice device = new()
            {
                Id = Guid.NewGuid(),
                OrganizationId = org,
                Kind = kind,
                Name = name,
                CapabilitiesJson = capabilities,
                LastSeenAt = lastSeen,
                RevokedAt = revoked ? Now : null
            };
            Db.RunnerDevices.Add(device);
            await Db.SaveChangesAsync();
            return device.Id;
        }
    }

    private static Harness CreateHarness()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        InferenceApiDbContext db = new(options);

        Mock<ISecretProtector> secrets = new();
        secrets.Setup(s => s.Protect(It.IsAny<string>())).Returns<string>(k => "enc:" + k);
        Mock<IInferenceProviderResolver> resolver = new();
        Mock<IOrganizationEntitlements> entitlements = new();
        entitlements.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<OrganizationEntitlementSet>(new OrganizationEntitlementSet { ByoAllowed = true }));

        Mock<IChatClientFactory> chat = new();
        Mock<IChatClient> client = new();
        Harness h = null!;
        client.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((_, o, _) => h.Options.Add(o))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));
        chat.Setup(f => f.Get(It.IsAny<ResolvedInferenceProvider>()))
            .Callback<ResolvedInferenceProvider>(p => h.Resolved.Add(p))
            .Returns(client.Object);

        InferenceProviderTester tester = new(
            db, secrets.Object, chat.Object, new Mock<ITranscriptionClientFactory>().Object,
            new EmbeddingClientFactory(), new Mock<IDecisionClientFactory>().Object,
            new Mock<IVideoGenerationClientFactory>().Object, new Mock<ISpeechSynthesisClientFactory>().Object,
            NullLogger<InferenceProviderTester>.Instance);

        h = new Harness
        {
            Db = db,
            Chat = chat,
            Secrets = secrets,
            Service = new InferenceProviderAdminService(db, secrets.Object, tester, resolver.Object, entitlements.Object)
        };
        return h;
    }

    private static CreateProviderRequest Request(string endpoint, string capability = "Chat", string? apiKey = null, string name = "My laptop") =>
        ProviderTree.OneModel(name, "LocalRunner", endpoint, "llama3.2:3b", apiKey, true, true, null, capability);

    private static string? ErrorOf(ActionResult<ProviderResponse> result) =>
        (result.Result as BadRequestObjectResult)?.Value?.ToString();

    // ---------------------------------------------------------------- admin boundary

    [Theory]
    [InlineData(InferenceProviderCapability.Chat, true)]
    [InlineData(InferenceProviderCapability.Vision, true)]
    [InlineData(InferenceProviderCapability.Transcription, false)]
    [InlineData(InferenceProviderCapability.Decision, false)]
    [InlineData(InferenceProviderCapability.Embedding, true)]
    [InlineData(InferenceProviderCapability.SpeechSynthesis, false)]
    [InlineData(InferenceProviderCapability.ImageGeneration, false)]
    [InlineData(InferenceProviderCapability.VideoGeneration, false)]
    [InlineData(InferenceProviderCapability.ContentModeration, false)]
    [InlineData(InferenceProviderCapability.Rerank, false)]
    [InlineData(InferenceProviderCapability.Segmentation, false)]
    [InlineData(InferenceProviderCapability.MusicGeneration, false)]
    public void The_capability_allowlist_admits_chat_vision_and_embedding_only(InferenceProviderCapability capability, bool allowed)
    {
        InferenceProvidersController.IsUnsupportedCombination(InferenceProviderKind.LocalRunner, capability, out string error)
            .Should().Be(!allowed, error);
        ProviderCapabilityUsage.ServableByKind[InferenceProviderKind.LocalRunner].Contains(capability).Should().Be(allowed);
    }

    [Fact]
    public void Every_capability_other_than_chat_vision_and_embedding_is_refused_for_the_kind()
    {
        foreach (InferenceProviderCapability capability in ProviderCapabilityUsage.Capabilities)
        {
            bool refused = InferenceProvidersController.IsUnsupportedCombination(InferenceProviderKind.LocalRunner, capability, out _);
            refused.Should().Be(capability is not (InferenceProviderCapability.Chat or InferenceProviderCapability.Vision or InferenceProviderCapability.Embedding), capability.ToString());
        }
    }

    [Theory]
    [InlineData("Chat", true)]
    [InlineData("Vision", true)]
    [InlineData("Transcription", false)]
    [InlineData("Embedding", true)]
    [InlineData("VideoGeneration", false)]
    public async Task An_org_owner_creates_a_local_runner_only_for_chat_vision_and_embedding(string capability, bool allowed)
    {
        Harness h = CreateHarness();
        Guid device = await h.Device(OrgA);

        var result = await h.Org(OrgA, OrganizationRole.Owner).Create(Request(device.ToString(), capability), default);

        if (allowed)
        {
            result.Result.Should().BeOfType<CreatedAtActionResult>();
            InferenceProvider stored = await h.Db.InferenceProviders.IgnoreQueryFilters().SingleAsync();
            stored.Kind.Should().Be(InferenceProviderKind.LocalRunner);
            stored.OrganizationId.Should().Be(OrgA);
            stored.Endpoint.Should().Be(device.ToString("D"));
            stored.ApiKeyEncrypted.Should().BeNull();
        }
        else
        {
            result.Result.Should().BeOfType<BadRequestObjectResult>();
            (await h.Db.InferenceProviders.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task An_org_admin_may_create_and_a_member_may_not()
    {
        Harness h = CreateHarness();
        Guid device = await h.Device(OrgA);

        (await h.Org(OrgA, OrganizationRole.Admin).Create(Request(device.ToString()), default)).Result.Should().BeOfType<CreatedAtActionResult>();
        (await h.Org(OrgA, OrganizationRole.Member).Create(Request(device.ToString(), name: "other"), default)).Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task A_device_of_another_organization_is_refused()
    {
        Harness h = CreateHarness();
        Guid foreign = await h.Device(OrgB);

        var result = await h.Org(OrgA, OrganizationRole.Owner).Create(Request(foreign.ToString()), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        ErrorOf(result).Should().Contain("paired to this workspace");
        (await h.Db.InferenceProviders.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_revoked_device_is_refused()
    {
        Harness h = CreateHarness();
        Guid revoked = await h.Device(OrgA, revoked: true);

        (await h.Org(OrgA, OrganizationRole.Owner).Create(Request(revoked.ToString()), default)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("https://198.51.100.7")]
    [InlineData("00000000-0000-0000-0000-00000000dead")]
    public async Task An_endpoint_that_is_not_a_known_device_is_refused(string endpoint)
    {
        Harness h = CreateHarness();
        await h.Device(OrgA);

        (await h.Org(OrgA, OrganizationRole.Owner).Create(Request(endpoint), default)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task A_cloud_pool_worker_is_not_a_desktop_runner()
    {
        Harness h = CreateHarness();
        Guid pool = await h.Device(OrgA, kind: RunnerKind.CloudPool);

        (await h.Org(OrgA, OrganizationRole.Owner).Create(Request(pool.ToString()), default)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task An_api_key_is_refused_on_create_update_and_test()
    {
        Harness h = CreateHarness();
        Guid device = await h.Device(OrgA);
        OrgInferenceProvidersController org = h.Org(OrgA, OrganizationRole.Owner);

        var created = await org.Create(Request(device.ToString(), apiKey: "sk-secret"), default);
        created.Result.Should().BeOfType<BadRequestObjectResult>();
        ErrorOf(created).Should().Contain("no API key");

        var good = await org.Create(Request(device.ToString()), default);
        Guid id = ((ProviderResponse)((CreatedAtActionResult)good.Result!).Value!).Id;
        (await org.Update(id, new UpdateProviderRequest(ApiKey: "sk-secret"), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await org.Update(id, new UpdateProviderRequest(Endpoint: device.ToString(), ApiKey: "sk-secret"), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();

        var tested = await org.TestUnsaved(new TestProviderRequest("LocalRunner", device.ToString(), "sk-secret", "m", "Chat"), default);
        tested.Result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Db.InferenceProviders.IgnoreQueryFilters().SingleAsync()).ApiKeyEncrypted.Should().BeNull();
    }

    [Fact]
    public async Task Repointing_the_endpoint_to_a_foreign_or_revoked_device_is_refused_and_to_an_own_device_is_accepted()
    {
        Harness h = CreateHarness();
        Guid first = await h.Device(OrgA);
        Guid second = await h.Device(OrgA, name: "Second");
        Guid foreign = await h.Device(OrgB);
        Guid revoked = await h.Device(OrgA, revoked: true);
        OrgInferenceProvidersController org = h.Org(OrgA, OrganizationRole.Owner);
        Guid id = ((ProviderResponse)((CreatedAtActionResult)(await org.Create(Request(first.ToString()), default)).Result!).Value!).Id;

        (await org.Update(id, new UpdateProviderRequest(Endpoint: foreign.ToString()), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await org.Update(id, new UpdateProviderRequest(Endpoint: revoked.ToString()), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await org.Update(id, new UpdateProviderRequest(Endpoint: second.ToString()), default)).Result.Should().BeOfType<OkObjectResult>();

        (await h.Db.InferenceProviders.IgnoreQueryFilters().SingleAsync()).Endpoint.Should().Be(second.ToString("D"));
    }

    [Fact]
    public async Task Repointing_a_local_runner_to_another_own_device_is_not_caught_by_the_stored_key_rule()
    {
        // A device id has no origin, so the "key must be re-entered on a host change" rule must not apply to the kind,
        // even for a legacy row that still holds a key.
        Harness h = CreateHarness();
        Guid first = await h.Device(OrgA);
        Guid second = await h.Device(OrgA, name: "Second");
        OrgInferenceProvidersController org = h.Org(OrgA, OrganizationRole.Owner);
        Guid id = ((ProviderResponse)((CreatedAtActionResult)(await org.Create(Request(first.ToString()), default)).Result!).Value!).Id;
        InferenceProvider row = await h.Db.InferenceProviders.IgnoreQueryFilters().SingleAsync();
        row.ApiKeyEncrypted = "enc:legacy";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        (await org.Update(id, new UpdateProviderRequest(Endpoint: second.ToString()), default)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task The_platform_scope_refuses_the_kind_even_for_a_platform_admin()
    {
        Harness h = CreateHarness();
        Guid device = await h.Device(OrgA);
        InferenceProvidersController platform = h.Platform();

        var created = await platform.Create(Request(device.ToString()), default);
        created.Result.Should().BeOfType<BadRequestObjectResult>();
        ErrorOf(created).Should().Contain("workspace");

        var tested = await platform.TestUnsaved(new TestProviderRequest("LocalRunner", device.ToString(), null, "m", "Chat"), default);
        tested.Result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Db.InferenceProviders.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_second_capability_other_than_chat_vision_or_embedding_cannot_be_added_to_a_saved_model()
    {
        Harness h = CreateHarness();
        Guid device = await h.Device(OrgA);
        OrgInferenceProvidersController org = h.Org(OrgA, OrganizationRole.Owner);
        var created = await org.Create(Request(device.ToString()), default);
        ProviderResponse provider = (ProviderResponse)((CreatedAtActionResult)created.Result!).Value!;
        Guid modelId = provider.Models.Single().Id;

        (await org.CreateCapability(provider.Id, modelId, new CreateCapabilityRequest("Transcription"), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await org.CreateCapability(provider.Id, modelId, new CreateCapabilityRequest("Vision"), default))
            .Result.Should().BeOfType<OkObjectResult>();
    }

    // ---------------------------------------------------------------- the test endpoint

    [Fact]
    public async Task The_test_endpoint_pings_through_the_relay_with_a_one_token_chat_for_the_callers_org()
    {
        Harness h = CreateHarness();
        Guid device = await h.Device(OrgA);

        var result = await h.Org(OrgA, OrganizationRole.Owner)
            .TestUnsaved(new TestProviderRequest("LocalRunner", device.ToString(), null, "llama3.2:3b", "Chat"), default);

        TestInferenceProviderResponse body = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<TestInferenceProviderResponse>().Subject;
        body.Ok.Should().BeTrue("a blank one-token reply still proves the relay answered");
        h.Resolved.Should().ContainSingle();
        h.Resolved[0].Kind.Should().Be(InferenceProviderKind.LocalRunner);
        h.Resolved[0].OrganizationId.Should().Be(OrgA);
        h.Resolved[0].IsPlatformManaged.Should().BeFalse();
        h.Resolved[0].Endpoint.Should().Be(device.ToString("D"));
        h.Options.Should().ContainSingle().Which!.MaxOutputTokens.Should().Be(1);
    }

    [Fact]
    public async Task The_test_endpoint_reports_an_offline_runner_in_plain_words()
    {
        Harness h = CreateHarness();
        Guid device = await h.Device(OrgA);
        h.Chat.Setup(f => f.Get(It.IsAny<ResolvedInferenceProvider>()))
            .Throws(new LocalRunnerUnavailableException(LocalRunnerUnavailableException.ReasonFor(HttpStatusCode.ServiceUnavailable, "runner_offline"), "runner_offline"));

        var result = await h.Org(OrgA, OrganizationRole.Owner)
            .TestUnsaved(new TestProviderRequest("LocalRunner", device.ToString(), null, "m", "Chat"), default);

        TestInferenceProviderResponse body = (TestInferenceProviderResponse)((OkObjectResult)result.Result!).Value!;
        body.Ok.Should().BeFalse();
        body.Error.Should().Contain("Your local model is offline").And.Contain("desktop app");
    }

    [Fact]
    public async Task A_saved_row_whose_device_was_removed_is_not_tested()
    {
        Harness h = CreateHarness();
        Guid device = await h.Device(OrgA);
        OrgInferenceProvidersController org = h.Org(OrgA, OrganizationRole.Owner);
        ProviderResponse provider = (ProviderResponse)((CreatedAtActionResult)(await org.Create(Request(device.ToString()), default)).Result!).Value!;
        (await h.Db.RunnerDevices.SingleAsync()).RevokedAt = Now;
        await h.Db.SaveChangesAsync();

        var model = provider.Models.Single();
        var outcome = await org.TestCapability(provider.Id, model.Id, model.Capabilities.Single().Id, default);

        outcome.Result.Should().BeOfType<BadRequestObjectResult>();
        h.Resolved.Should().BeEmpty("a removed device never reaches the relay");
    }

    // ---------------------------------------------------------------- the relay client

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A refusal exactly as the real gateway sends it: the relay-error header plus the JSON body.</summary>
    private static HttpResponseMessage GatewayRefusal(HttpStatusCode status, string code, string body)
    {
        HttpResponseMessage response = Json(status, body);
        response.Headers.Add(LocalRunnerChatClient.RelayErrorHeader, code);
        return response;
    }

    private const string Completion =
        """{"id":"c1","object":"chat.completion","created":1,"model":"llama3.2:3b","choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1,"total_tokens":4}}""";

    private static ResolvedInferenceProvider Row(Guid? org, string endpoint, int timeout = 30) => new(
        Guid.NewGuid(), "my laptop", InferenceProviderKind.LocalRunner, endpoint, "llama3.2:3b", string.Empty, timeout,
        OrganizationId: org, IsPlatformManaged: false);

    private static LocalRunnerGatewayOptions Gateway(string url = "http://runner-gateway:8090/", string token = "relay-token") =>
        new() { GatewayBaseUrl = url, InternalToken = token };

    [Fact]
    public async Task The_client_posts_to_the_org_and_device_relay_route_with_the_service_token()
    {
        Guid device = Guid.NewGuid();
        CapturingHandler handler = new(_ => Json(HttpStatusCode.OK, Completion));
        IChatClient client = ChatClientFactory.BuildLocalRunner(Row(OrgA, device.ToString()), Gateway(), handler);

        ChatResponse response = await client.GetResponseAsync("hi");

        response.Text.Should().Be("hello");
        HttpRequestMessage sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.RequestUri!.ToString().Should().Be($"http://runner-gateway:8090/internal/v1/llm/orgs/{OrgA:D}/devices/{device:D}/v1/chat/completions");
        sent.Headers.Authorization!.Scheme.Should().Be("Bearer");
        sent.Headers.Authorization.Parameter.Should().Be("relay-token");
        handler.Bodies[0].Should().Contain("llama3.2:3b");
    }

    [Fact]
    public async Task The_org_comes_from_the_provider_row_and_a_row_without_one_cannot_build()
    {
        Guid device = Guid.NewGuid();
        CapturingHandler handler = new(_ => Json(HttpStatusCode.OK, Completion));
        await ChatClientFactory.BuildLocalRunner(Row(OrgB, device.ToString()), Gateway(), handler).GetResponseAsync("hi");
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Contain($"/orgs/{OrgB:D}/");

        Action noOrg = () => ChatClientFactory.BuildLocalRunner(Row(null, device.ToString()), Gateway(), handler);
        noOrg.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_missing_token_or_a_bad_device_id_fails_with_the_typed_error_and_no_request()
    {
        Guid device = Guid.NewGuid();
        CapturingHandler handler = new(_ => Json(HttpStatusCode.OK, Completion));
        string? previous = Environment.GetEnvironmentVariable(LocalRunnerGatewayOptions.InternalTokenEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(LocalRunnerGatewayOptions.InternalTokenEnvironmentVariable, null);
            Action noToken = () => ChatClientFactory.BuildLocalRunner(Row(OrgA, device.ToString()), Gateway(token: ""), handler);
            noToken.Should().Throw<LocalRunnerUnavailableException>().Which.GatewayCode.Should().Be("internal_api_disabled");
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalRunnerGatewayOptions.InternalTokenEnvironmentVariable, previous);
        }

        Action badDevice = () => ChatClientFactory.BuildLocalRunner(Row(OrgA, "https://example.com"), Gateway(), handler);
        badDevice.Should().Throw<LocalRunnerUnavailableException>().Which.GatewayCode.Should().Be("device_not_found");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void The_token_falls_back_to_the_environment_variable()
    {
        string? previous = Environment.GetEnvironmentVariable(LocalRunnerGatewayOptions.InternalTokenEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(LocalRunnerGatewayOptions.InternalTokenEnvironmentVariable, " from-env ");
            new LocalRunnerGatewayOptions().ResolveInternalToken().Should().Be("from-env");
            new LocalRunnerGatewayOptions { InternalToken = "configured" }.ResolveInternalToken().Should().Be("configured");
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalRunnerGatewayOptions.InternalTokenEnvironmentVariable, previous);
        }
    }

    [Theory]
    [InlineData(503, "runner_offline", "Your local model is offline")]
    [InlineData(409, "local_llm_disabled", "switched off")]
    [InlineData(404, "device_not_found", "no longer available")]
    [InlineData(429, "busy", "busy")]
    [InlineData(502, "local_server_unreachable", "could not reach your local model server")]
    [InlineData(504, "timeout", "did not answer in time")]
    [InlineData(503, "internal_api_disabled", "not set up")]
    [InlineData(401, "unauthorized", "not set up correctly")]
    [InlineData(413, "request_too_large", "too large for the local model relay")]
    [InlineData(502, "local_response_too_large", "answer was too large")]
    [InlineData(502, "runner_connection_lost", "connection to the desktop app was lost")]
    [InlineData(502, "cancelled", "cancelled")]
    [InlineData(502, "runner_rejected_request", "refused this request")]
    [InlineData(400, "bad_request", "not valid")]
    [InlineData(503, "registry_unavailable", "could not look up")]
    public async Task A_gateway_refusal_becomes_a_typed_plain_language_error(int status, string code, string fragment)
    {
        CapturingHandler handler = new(_ => GatewayRefusal((HttpStatusCode)status, code, $$$"""{"error":{"code":"{{{code}}}","message":"internal detail"}}"""));
        IChatClient client = ChatClientFactory.BuildLocalRunner(Row(OrgA, Guid.NewGuid().ToString()), Gateway(), handler);

        Func<Task> call = () => client.GetResponseAsync("hi");

        LocalRunnerUnavailableException thrown = (await call.Should().ThrowAsync<LocalRunnerUnavailableException>()).Which;
        thrown.GatewayCode.Should().Be(code);
        thrown.Status.Should().Be((HttpStatusCode)status);
        thrown.Message.Should().Contain(fragment).And.Contain(LocalRunnerUnavailableException.FailureCode);
        thrown.Message.Should().NotContain("internal detail");
        handler.Requests.Should().ContainSingle("a refused relay call is never retried");
    }

    [Theory]
    [InlineData(InferenceProviderKind.AzureOpenAI, false)]
    [InlineData(InferenceProviderKind.OpenAICompatible, false)]
    [InlineData(InferenceProviderKind.LocalRunner, true)]
    public void A_provider_test_row_carries_the_organization_only_for_a_local_runner(InferenceProviderKind kind, bool carriesOrg)
    {
        ResolvedInferenceProvider row = InferenceProviderTester.ChatProvider(
            Guid.NewGuid(), "x", kind, "e", "m", "k", 10, false, OrgA, InferenceProviderCapability.Chat);

        row.OrganizationId.Should().Be(carriesOrg ? OrgA : null);
        row.IsPlatformManaged.Should().BeFalse("an organization's row is tested over the public-only transport, whatever its kind");
    }

    [Fact]
    public async Task A_status_without_a_gateway_body_still_maps_by_status()
    {
        CapturingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        IChatClient client = ChatClientFactory.BuildLocalRunner(Row(OrgA, Guid.NewGuid().ToString()), Gateway(), handler);

        Func<Task> call = () => client.GetResponseAsync("hi");

        (await call.Should().ThrowAsync<LocalRunnerUnavailableException>()).Which.Status.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task A_refused_streaming_call_also_maps_and_a_dead_gateway_is_unreachable()
    {
        CapturingHandler refused = new(_ => GatewayRefusal(HttpStatusCode.ServiceUnavailable, "runner_offline", """{"error":{"code":"runner_offline"}}"""));
        IChatClient streaming = ChatClientFactory.BuildLocalRunner(Row(OrgA, Guid.NewGuid().ToString()), Gateway(), refused);
        Func<Task> consume = async () =>
        {
            await foreach (ChatResponseUpdate _ in streaming.GetStreamingResponseAsync("hi")) { }
        };
        (await consume.Should().ThrowAsync<LocalRunnerUnavailableException>()).Which.GatewayCode.Should().Be("runner_offline");

        CapturingHandler dead = new(_ => throw new HttpRequestException("connection refused"));
        IChatClient unreachable = ChatClientFactory.BuildLocalRunner(Row(OrgA, Guid.NewGuid().ToString()), Gateway(), dead);
        Func<Task> call = () => unreachable.GetResponseAsync("hi");
        (await call.Should().ThrowAsync<LocalRunnerUnavailableException>()).Which.GatewayCode.Should().Be("gateway_unreachable");
    }

    [Fact]
    public void Repointing_the_device_or_the_organization_builds_a_different_client_and_other_kinds_keep_their_key()
    {
        Guid d1 = Guid.NewGuid();
        Guid d2 = Guid.NewGuid();
        Row(OrgA, d1.ToString()).CacheKey.Should().NotBe(Row(OrgA, d2.ToString()).CacheKey);
        Row(OrgA, d1.ToString()).CacheKey.Should().NotBe(Row(OrgB, d1.ToString()).CacheKey);
        Row(OrgA, d1.ToString()).CacheKey.Should().Be(Row(OrgA, d1.ToString()).CacheKey);

        // The key of every other kind is unchanged by the organization.
        ResolvedInferenceProvider Other(Guid? org) => new(Guid.NewGuid(), "p", InferenceProviderKind.OpenAICompatible, "http://x", "m", "k", 30, OrganizationId: org);
        Other(OrgA).CacheKey.Should().Be(Other(OrgB).CacheKey);
    }

    [Fact]
    public void The_factory_serves_a_local_runner_row_through_its_gateway_options()
    {
        ChatClientFactory factory = new(null, null, Microsoft.Extensions.Options.Options.Create(Gateway()));

        using IChatClient client = factory.Get(Row(OrgA, Guid.NewGuid().ToString()));

        client.Should().NotBeNull();
    }

    // ---------------------------------------------------------------- failure class and metering

    [Fact]
    public void An_offline_local_model_is_a_byo_provider_error_never_a_platform_refund()
    {
        LocalRunnerUnavailableException offline = new(
            LocalRunnerUnavailableException.ReasonFor(HttpStatusCode.ServiceUnavailable, "runner_offline"), "runner_offline", HttpStatusCode.ServiceUnavailable);

        FailureClassifier.ForException(offline).Should().Be(FailureClass.ByoProviderError);
        FailureClassifier.TryGetCode(LocalRunnerUnavailableException.FailureCode, out FailureClass mapped).Should().BeTrue();
        mapped.Should().Be(FailureClass.ByoProviderError);
        FailureClassifier.CodeFromException(offline).Should().Be(LocalRunnerUnavailableException.FailureCode);
        FailureClassifier.Classify(FailureClass.None, FailureClassifier.CodeFromException(offline), offline).Should().Be(FailureClass.ByoProviderError);
        CreditGate.IsRefundable(FailureClass.ByoProviderError).Should().BeFalse();
    }

    [Fact]
    public async Task A_local_runner_call_meters_at_zero_credits_without_a_price_and_is_not_flagged_unpriced()
    {
        CapturingSink sink = new();
        UsageRecorder recorder = new(new EmptyPriceService(), sink, NullLogger<UsageRecorder>.Instance);
        IChatClient client = new MeteringChatClient(new UsageReportingChatClient(500, 100), Row(OrgA, Guid.NewGuid().ToString()), recorder);

        using (UsageScope.Begin(OrgA, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), UsageSource.Workflow))
            await client.GetResponseAsync("hi");
        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);

        IReadOnlyList<UsageEvent> rows = sink.Events;
        rows.Should().NotBeEmpty().And.OnlyContain(r =>
            r.BillingMode == UsageBillingMode.Byo && r.CreditsMillis == 0 && r.CostUsd == 0m && !r.Unpriced && r.ProviderKind == InferenceProviderKind.LocalRunner);
    }

    [Fact]
    public async Task The_price_book_prices_a_byo_local_runner_unit_at_zero_instead_of_throwing()
    {
        ModelPriceService prices = new(new EmptySource());

        PriceQuote quote = await prices.PriceAsync(new PriceRequest(
            Guid.NewGuid(), null, InferenceProviderKind.LocalRunner, InferenceProviderCapability.Chat, "llama3.2:3b",
            UsageKind.LlmInputTokens, null, UsageBillingMode.Byo, 1000m));

        quote.CreditsMillis.Should().Be(0);
        quote.CostUsd.Should().Be(0m);
    }

    private sealed class EmptySource : IModelPriceSource
    {
        public Task<ModelPriceSnapshot> LoadAsync(CancellationToken ct) => Task.FromResult(new ModelPriceSnapshot([], [], []));
    }

    // ---------------------------------------------------------------- the device listing

    private const string Advertised =
        """{"localLlm":{"enabled":true,"serverKind":"ollama","models":["llama3.2:3b","qwen2.5:7b"],"maxConcurrent":2},"gpu":{"vendor":"nvidia"},"publicKey":"leak"}""";

    [Fact]
    public async Task The_listing_returns_only_this_orgs_live_devices_that_advertise_a_local_model_with_exactly_the_shaped_fields()
    {
        Harness h = CreateHarness();
        Guid online = await h.Device(OrgA, capabilities: Advertised, lastSeen: Now.AddSeconds(-30), name: "Studio PC");
        Guid stale = await h.Device(OrgA, capabilities: Advertised.Replace("\"enabled\":true", "\"enabled\":false"), lastSeen: Now.AddMinutes(-5), name: "Old Mac");
        await h.Device(OrgA, capabilities: """{"gpu":{"vendor":"amd"}}""", name: "No LLM");
        await h.Device(OrgA, capabilities: Advertised, revoked: true, name: "Revoked");
        await h.Device(OrgB, capabilities: Advertised, lastSeen: Now, name: "Foreign");
        await h.Device(OrgA, kind: RunnerKind.CloudPool, capabilities: Advertised, name: "Pool");

        var result = await h.Listing(OrgA, OrganizationRole.Admin).List(default);

        RunnerLlmResponse body = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<RunnerLlmResponse>().Subject;
        body.Devices.Select(d => d.Name).Should().Equal("Old Mac", "Studio PC");
        RunnerLlmDevice studio = body.Devices.Single(d => d.DeviceId == online);
        studio.Online.Should().BeTrue();
        studio.Enabled.Should().BeTrue();
        studio.ServerKind.Should().Be("ollama");
        studio.Models.Should().Equal("llama3.2:3b", "qwen2.5:7b");
        RunnerLlmDevice old = body.Devices.Single(d => d.DeviceId == stale);
        old.Online.Should().BeFalse();
        old.Enabled.Should().BeFalse();

        string json = System.Text.Json.JsonSerializer.Serialize(body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        json.Should().Contain("\"deviceId\"").And.Contain("\"serverKind\"").And.NotContain("leak").And.NotContain("gpu").And.NotContain("maxConcurrent");
    }

    [Fact]
    public async Task The_listing_is_open_to_owner_and_admin_only()
    {
        Harness h = CreateHarness();

        (await h.Listing(OrgA, OrganizationRole.Owner).List(default)).Result.Should().BeOfType<OkObjectResult>();
        (await h.Listing(OrgA, OrganizationRole.Admin).List(default)).Result.Should().BeOfType<OkObjectResult>();
        (await h.Listing(OrgA, OrganizationRole.Member).List(default)).Result.Should().BeOfType<ForbidResult>();
        (await h.Listing(OrgA, OrganizationRole.Billing).List(default)).Result.Should().BeOfType<ForbidResult>();
        (await h.Listing(null, null).List(default)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"localLlm":"yes"}""")]
    [InlineData("""{"other":{}}""")]
    public void A_malformed_or_absent_capability_document_advertises_nothing(string json)
    {
        OrgRunnerLlmController.TryReadLocalLlm(json, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Self_reported_names_are_bounded()
    {
        string models = string.Join(",", Enumerable.Range(0, 150).Select(i => $"\"{new string('m', 300)}{i}\""));
        OrgRunnerLlmController.TryReadLocalLlm($$$"""{"localLlm":{"enabled":true,"serverKind":"{{{new string('s', 200)}}}","models":[{{{models}}}]}}""",
            out _, out string kind, out List<string> list).Should().BeTrue();

        list.Should().HaveCount(100);
        list.Should().OnlyContain(m => m.Length <= 200);
        kind.Length.Should().Be(50);
    }
}
