using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class InferenceProvidersControllerDecisionTests
{
    private static InferenceApiDbContext CreateContext()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new InferenceApiDbContext(options);
    }

    private sealed class FakeCurrentUser(Guid userId, bool isAdmin) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = isAdmin;
        public System.Guid? OrganizationId => null;
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => null;
    }

    // --- Create endpoint tests ---

    [Fact]
    public async Task Create_accepts_capability_Decision()
    {
        using InferenceApiDbContext db = CreateContext();
        var chatClientFactory = new Mock<IChatClientFactory>();
        var transcriptionClientFactory = new Mock<ITranscriptionClientFactory>();
        var decisionClientFactory = new Mock<IDecisionClientFactory>();
        var videoGenerationClientFactory = new Mock<IVideoGenerationClientFactory>();
        var speechSynthesisClientFactory = new Mock<ISpeechSynthesisClientFactory>();
        var secretProtector = new Mock<ISecretProtector>();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>();

        InferenceProvidersController controller = new(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            chatClientFactory.Object, transcriptionClientFactory.Object, decisionClientFactory.Object,
            videoGenerationClientFactory.Object, speechSynthesisClientFactory.Object,
            secretProtector.Object, logger.Object);

        CreateProviderRequest request = ProviderTree.OneModel(
            Name: "TestDecisionProvider",
            Kind: "OpenAICompatible",
            Endpoint: "https://example.com/api",
            ModelName: "test-model",
            ApiKey: "test-key",
            IsDefault: false,
            IsEnabled: true,
            TimeoutSeconds: null,
            Capability: "Decision"
        );

        ActionResult<ProviderResponse> result = await controller.Create(request, CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        var body = ((CreatedAtActionResult)result.Result!).Value;
        var response = ((ProviderResponse)body!).Flat();
        response.Capability.Should().Be("Decision");
    }

    [Fact]
    public async Task Create_rejects_capability_Decision_for_kind_Anthropic_with_400()
    {
        using InferenceApiDbContext db = CreateContext();
        var chatClientFactory = new Mock<IChatClientFactory>();
        var transcriptionClientFactory = new Mock<ITranscriptionClientFactory>();
        var decisionClientFactory = new Mock<IDecisionClientFactory>();
        var videoGenerationClientFactory = new Mock<IVideoGenerationClientFactory>();
        var speechSynthesisClientFactory = new Mock<ISpeechSynthesisClientFactory>();
        var secretProtector = new Mock<ISecretProtector>();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>();

        InferenceProvidersController controller = new(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            chatClientFactory.Object, transcriptionClientFactory.Object, decisionClientFactory.Object,
            videoGenerationClientFactory.Object, speechSynthesisClientFactory.Object,
            secretProtector.Object, logger.Object);

        CreateProviderRequest request = ProviderTree.OneModel(
            Name: "AnthropicDecision",
            Kind: "Anthropic",
            Endpoint: "https://example.com",
            ModelName: "claude-opus-5",
            ApiKey: "test-key",
            IsDefault: false,
            IsEnabled: true,
            TimeoutSeconds: null,
            Capability: "Decision"
        );

        ActionResult<ProviderResponse> result = await controller.Create(request, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        var badRequest = (BadRequestObjectResult)result.Result!;
        badRequest.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task Create_rejects_capability_Decision_for_kind_Gemini_with_400()
    {
        using InferenceApiDbContext db = CreateContext();
        var chatClientFactory = new Mock<IChatClientFactory>();
        var transcriptionClientFactory = new Mock<ITranscriptionClientFactory>();
        var decisionClientFactory = new Mock<IDecisionClientFactory>();
        var videoGenerationClientFactory = new Mock<IVideoGenerationClientFactory>();
        var speechSynthesisClientFactory = new Mock<ISpeechSynthesisClientFactory>();
        var secretProtector = new Mock<ISecretProtector>();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>();

        InferenceProvidersController controller = new(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            chatClientFactory.Object, transcriptionClientFactory.Object, decisionClientFactory.Object,
            videoGenerationClientFactory.Object, speechSynthesisClientFactory.Object,
            secretProtector.Object, logger.Object);

        CreateProviderRequest request = ProviderTree.OneModel(
            Name: "GeminiDecision",
            Kind: "Gemini",
            Endpoint: "https://generativelanguage.googleapis.com/v1beta",
            ModelName: "gemini-2.0-flash",
            ApiKey: "test-key",
            IsDefault: false,
            IsEnabled: true,
            TimeoutSeconds: null,
            Capability: "Decision"
        );

        ActionResult<ProviderResponse> result = await controller.Create(request, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_rejects_capability_Chat_for_kind_TypeSafe_with_400()
    {
        using InferenceApiDbContext db = CreateContext();
        var chatClientFactory = new Mock<IChatClientFactory>();
        var transcriptionClientFactory = new Mock<ITranscriptionClientFactory>();
        var decisionClientFactory = new Mock<IDecisionClientFactory>();
        var videoGenerationClientFactory = new Mock<IVideoGenerationClientFactory>();
        var speechSynthesisClientFactory = new Mock<ISpeechSynthesisClientFactory>();
        var secretProtector = new Mock<ISecretProtector>();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>();

        InferenceProvidersController controller = new(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            chatClientFactory.Object, transcriptionClientFactory.Object, decisionClientFactory.Object,
            videoGenerationClientFactory.Object, speechSynthesisClientFactory.Object,
            secretProtector.Object, logger.Object);

        CreateProviderRequest request = ProviderTree.OneModel(
            Name: "TypeSafeChat",
            Kind: "TypeSafe",
            Endpoint: "https://typesafe.example.com",
            ModelName: "ts-model",
            ApiKey: "test-key",
            IsDefault: false,
            IsEnabled: true,
            TimeoutSeconds: null,
            Capability: "Chat"
        );

        ActionResult<ProviderResponse> result = await controller.Create(request, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- OpenJev (self-hosted System One) kind/capability boundary ---

    private static InferenceProvidersController CreateController(InferenceApiDbContext db) => new(
        db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
        new Mock<IChatClientFactory>().Object,
        new Mock<ITranscriptionClientFactory>().Object,
        new Mock<IDecisionClientFactory>().Object,
        new Mock<IVideoGenerationClientFactory>().Object,
        new Mock<ISpeechSynthesisClientFactory>().Object,
        new Mock<ISecretProtector>().Object,
        new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>().Object);

    private static CreateProviderRequest OpenJevRequest(string capability) => ProviderTree.OneModel(
        Name: $"OpenJev{capability}",
        Kind: "OpenJev",
        // A literal public address, never a hostname: IsDisallowedEndpoint resolves a hostname via
        // DNS and rejects it when resolution fails, so a hostname makes these tests depend on a
        // resolver and turns an unrelated network failure into a 400 for the wrong reason.
        Endpoint: "https://93.184.216.7",
        ModelName: "open-jev-2b",
        ApiKey: "test-key",
        IsDefault: false,
        IsEnabled: true,
        TimeoutSeconds: null,
        Capability: capability);

    [Fact]
    public async Task Create_accepts_capability_Decision_for_kind_OpenJev()
    {
        using InferenceApiDbContext db = CreateContext();

        ActionResult<ProviderResponse> result =
            await CreateController(db).Create(OpenJevRequest("Decision"), CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        var response = ((ProviderResponse)((CreatedAtActionResult)result.Result!).Value!).Flat();
        response.Capability.Should().Be("Decision");
    }

    [Theory]
    [InlineData("Chat")]
    [InlineData("Transcription")]
    [InlineData("Vision")]
    [InlineData("VideoGeneration")]
    public async Task Create_rejects_kind_OpenJev_for_every_non_Decision_capability_with_400(string capability)
    {
        using InferenceApiDbContext db = CreateContext();

        ActionResult<ProviderResponse> result =
            await CreateController(db).Create(OpenJevRequest(capability), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_rejects_a_model_capability_left_blank_because_no_capability_is_assumed()
    {
        // The flat contract read an omitted capability as Chat, which an OpenJev row can never serve. The tree API
        // assumes nothing: a model's capability must be named, and a blank one is refused before anything is written.
        using InferenceApiDbContext db = CreateContext();
        CreateProviderRequest request = OpenJevRequest("Decision") with
        {
            Models = [new CreateModelRequest("open-jev-2b", "OpenJev blank", true, [new CreateCapabilityRequest(null)])]
        };

        ActionResult<ProviderResponse> result =
            await CreateController(db).Create(request, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        ErrorMessage((BadRequestObjectResult)result.Result!).Should().Be("Capability is required.");
        (await db.InferenceProviders.CountAsync()).Should().Be(0);
    }

    // --- Test endpoint tests ---

    [Fact]
    public async Task Test_returns_ok_true_when_the_calibration_question_answers_yes_with_high_probability()
    {
        using InferenceApiDbContext db = CreateContext();
        var chatClientFactory = new Mock<IChatClientFactory>();
        var transcriptionClientFactory = new Mock<ITranscriptionClientFactory>();
        var decisionClientFactory = new Mock<IDecisionClientFactory>();
        var speechSynthesisClientFactory = new Mock<ISpeechSynthesisClientFactory>();
        var secretProtector = new Mock<ISecretProtector>();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>();

        // Setup mock decision client to return high confidence Noul answer
        var mockDecisionClient = new Mock<IDecisionClient>();
        mockDecisionClient.Setup(c => c.DecideAsync(
            It.IsAny<DecisionState>(),
            It.IsAny<IReadOnlyList<DecisionQuestion>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionResult(
                "test-model",
                new Dictionary<string, DecisionAnswer>
                {
                    {
                        "Noul",
                        new DecisionAnswer(
                            "Noul",
                            DecisionKind.Noul,
                            null,
                            null,
                            0.97,
                            null,
                            new Dictionary<string, double> { { "true", 0.97 }, { "false", 0.03 } }
                        )
                    }
                },
                10,
                5
            ));

        decisionClientFactory.Setup(f => f.Get(It.IsAny<ResolvedDecisionProvider>()))
            .Returns(mockDecisionClient.Object);

        var videoGenerationClientFactory = new Mock<IVideoGenerationClientFactory>();

        InferenceProvidersController controller = new(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            chatClientFactory.Object, transcriptionClientFactory.Object, decisionClientFactory.Object,
            videoGenerationClientFactory.Object, speechSynthesisClientFactory.Object,
            secretProtector.Object, logger.Object);

        TestProviderRequest request = new(
            Kind: "OpenAICompatible",
            Endpoint: "https://example.com/api",
            ModelCode: "test-model",
            ApiKey: "test-key",
            Capability: "Decision"
        );

        ActionResult<TestInferenceProviderResponse> result = await controller.TestUnsaved(request, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        var body = ((OkObjectResult)result.Result!).Value;
        var response = (TestInferenceProviderResponse)body!;
        response.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task Test_returns_ok_false_when_the_calibration_question_is_not_confidently_yes()
    {
        using InferenceApiDbContext db = CreateContext();
        var chatClientFactory = new Mock<IChatClientFactory>();
        var transcriptionClientFactory = new Mock<ITranscriptionClientFactory>();
        var decisionClientFactory = new Mock<IDecisionClientFactory>();
        var videoGenerationClientFactory = new Mock<IVideoGenerationClientFactory>();
        var speechSynthesisClientFactory = new Mock<ISpeechSynthesisClientFactory>();
        var secretProtector = new Mock<ISecretProtector>();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>();

        // Setup mock decision client to return low confidence Noul answer
        var mockDecisionClient = new Mock<IDecisionClient>();
        mockDecisionClient.Setup(c => c.DecideAsync(
            It.IsAny<DecisionState>(),
            It.IsAny<IReadOnlyList<DecisionQuestion>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionResult(
                "test-model",
                new Dictionary<string, DecisionAnswer>
                {
                    {
                        "Noul",
                        new DecisionAnswer(
                            "Noul",
                            DecisionKind.Noul,
                            null,
                            null,
                            0.4,
                            null,
                            new Dictionary<string, double> { { "true", 0.4 }, { "false", 0.6 } }
                        )
                    }
                },
                10,
                5
            ));

        decisionClientFactory.Setup(f => f.Get(It.IsAny<ResolvedDecisionProvider>()))
            .Returns(mockDecisionClient.Object);

        InferenceProvidersController controller = new(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            chatClientFactory.Object, transcriptionClientFactory.Object, decisionClientFactory.Object,
            videoGenerationClientFactory.Object, speechSynthesisClientFactory.Object,
            secretProtector.Object, logger.Object);

        TestProviderRequest request = new(
            Kind: "OpenAICompatible",
            Endpoint: "https://example.com/api",
            ModelCode: "test-model",
            ApiKey: "test-key",
            Capability: "Decision"
        );

        ActionResult<TestInferenceProviderResponse> result = await controller.TestUnsaved(request, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        var body = ((OkObjectResult)result.Result!).Value;
        var response = (TestInferenceProviderResponse)body!;
        response.Ok.Should().BeFalse();
    }

    // --- Endpoint address classification: the OpenJev self-hosted relaxation ---
    //
    // OpenJev is a self-hosted System One backend, so the addresses it legitimately lives at are
    // loopback (the compose service's own 127.0.0.1:${DECISION_PORT:-8791} bind), a docker service
    // name, or host.docker.internal — all of which the generic rule classifies as private,
    // loopback, or unresolvable. These tests pin both halves: OpenJev accepts them, and every
    // other kind is byte-for-byte unchanged.

    private const string EndpointRejectedMessage =
        "Endpoint must be a public https/http URL; internal/private/loopback addresses are not allowed.";

    /// <summary>
    /// Rebuilt from the enum exactly as the controller builds it, so this asserts the SAME
    /// message text rather than a hand-copied subset of the valid kinds.
    /// </summary>
    private static readonly string ExpectedInvalidKindMessage =
        $"Invalid kind 'Nope'. Expected one of {string.Join(", ", Enum.GetNames<InferenceProviderKind>().Select(n => $"'{n}'"))}.";

    private static string ErrorMessage(BadRequestObjectResult result)
    {
        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        return doc.RootElement.GetProperty("error").GetString() ?? string.Empty;
    }

    private static CreateProviderRequest NonOpenJevRequest(string endpoint) => ProviderTree.OneModel(
        Name: $"NonOpenJev-{Guid.NewGuid():N}",
        Kind: "DeepSeek",
        Endpoint: endpoint,
        ModelName: "test-model",
        ApiKey: "test-key",
        IsDefault: false,
        IsEnabled: true,
        TimeoutSeconds: null,
        Capability: "Chat");

    private static UpdateProviderRequest EndpointUpdate(string endpoint) => new(Endpoint: endpoint);

    /// <summary>
    /// Seeds a row straight onto the context with an EXPLICIT endpoint. Going through the
    /// database rather than through <c>Create</c> is deliberate: the interesting rows for the
    /// kind-only-PUT cases below sit at addresses <c>Create</c> refuses for their pre-flip kind
    /// (an OpenJev row on loopback, for instance), so a Create-based seed could never produce
    /// the state under test at all.
    /// </summary>
    private static async Task<ModelCapabilityView> SeedProviderAtEndpointAsync(
        InferenceApiDbContext db, InferenceProviderKind kind, InferenceProviderCapability capability, string endpoint)
    {
        ModelCapabilityView entity = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = Guid.NewGuid(),
            Name = $"Seed-{kind}-{Guid.NewGuid():N}",
            Kind = kind,
            Capability = capability,
            Endpoint = endpoint,
            ModelName = "seed-model",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.AddFlat(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    private static Task<ModelCapabilityView> SeedProviderAsync(
        InferenceApiDbContext db, InferenceProviderKind kind, InferenceProviderCapability capability) =>
        // A literal public address, so the seed itself never depends on a resolver.
        SeedProviderAtEndpointAsync(db, kind, capability, "https://93.184.216.7");

    [Fact]
    public async Task Create_accepts_OpenJev_with_a_loopback_endpoint()
    {
        using InferenceApiDbContext db = CreateContext();
        CreateProviderRequest request = OpenJevRequest("Decision") with { Endpoint = "http://127.0.0.1:8791" };

        ActionResult<ProviderResponse> result =
            await CreateController(db).Create(request, CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Create_accepts_OpenJev_with_a_docker_service_hostname()
    {
        // `decision` is deliberately a name that need not resolve. The OpenJev path performs NO
        // DNS lookup at all, so this assertion holds whether or not the test host happens to
        // resolve it — and never turns an unrelated resolver failure into a 400 for the wrong
        // reason. (The non-OpenJev negative control below uses a literal address for the same
        // reason, in the other direction.)
        using InferenceApiDbContext db = CreateContext();
        CreateProviderRequest request = OpenJevRequest("Decision") with { Endpoint = "http://decision:8791" };

        ActionResult<ProviderResponse> result =
            await CreateController(db).Create(request, CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Create_accepts_OpenJev_with_host_docker_internal()
    {
        using InferenceApiDbContext db = CreateContext();
        CreateProviderRequest request =
            OpenJevRequest("Decision") with { Endpoint = "http://host.docker.internal:8791" };

        ActionResult<ProviderResponse> result =
            await CreateController(db).Create(request, CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Theory]
    // No scheme at all: not an absolute URI.
    [InlineData("decision:8791")]
    [InlineData("127.0.0.1:8791")]
    [InlineData("/v1/systemone")]
    // Absolute, but not http(s).
    [InlineData("ftp://127.0.0.1:8791")]
    [InlineData("file:///etc/passwd")]
    public async Task Create_rejects_kind_OpenJev_with_a_non_http_endpoint_with_400(string endpoint)
    {
        // The address-classification skip must not also skip the absolute-http(s)-URL requirement.
        using InferenceApiDbContext db = CreateContext();
        CreateProviderRequest request = OpenJevRequest("Decision") with { Endpoint = endpoint };

        ActionResult<ProviderResponse> result =
            await CreateController(db).Create(request, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        ErrorMessage((BadRequestObjectResult)result.Result!).Should().Be(EndpointRejectedMessage);
    }

    [Fact]
    public async Task Create_rejects_a_loopback_endpoint_for_a_non_OpenJev_kind_with_400()
    {
        using InferenceApiDbContext db = CreateContext();

        ActionResult<ProviderResponse> result =
            await CreateController(db).Create(NonOpenJevRequest("http://127.0.0.1:8791"), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        ErrorMessage((BadRequestObjectResult)result.Result!).Should().Be(EndpointRejectedMessage);
    }

    [Fact]
    public async Task Create_rejects_a_private_endpoint_for_a_non_OpenJev_kind_with_400()
    {
        using InferenceApiDbContext db = CreateContext();

        ActionResult<ProviderResponse> result =
            await CreateController(db).Create(NonOpenJevRequest("http://192.168.1.10:8000"), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        ErrorMessage((BadRequestObjectResult)result.Result!).Should().Be(EndpointRejectedMessage);
    }

    [Fact]
    public async Task Create_still_accepts_a_public_endpoint_for_a_non_OpenJev_kind()
    {
        // Positive control for the negative controls above: the relaxation did not widen any
        // other kind's ACCEPTED set either — a public address is still fine for them.
        using InferenceApiDbContext db = CreateContext();

        ActionResult<ProviderResponse> result =
            await CreateController(db).Create(NonOpenJevRequest("https://93.184.216.7/v1"), CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Create_rejects_an_invalid_kind_with_its_existing_message_before_the_endpoint_check()
    {
        // The endpoint here is one that only a PARSED OpenJev kind could accept. If the endpoint
        // check ran before the kind was parsed (its old position), this would come back with the
        // endpoint message instead — so asserting the kind message also pins the ordering.
        using InferenceApiDbContext db = CreateContext();
        CreateProviderRequest request = OpenJevRequest("Decision") with
        {
            Kind = "Nope",
            Endpoint = "http://127.0.0.1:8791"
        };

        ActionResult<ProviderResponse> result =
            await CreateController(db).Create(request, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        ErrorMessage((BadRequestObjectResult)result.Result!).Should().Be(ExpectedInvalidKindMessage);
        ExpectedInvalidKindMessage.Should().Contain("'OpenJev'");
    }

    [Fact]
    public async Task Update_accepts_a_loopback_endpoint_for_kind_OpenJev()
    {
        using InferenceApiDbContext db = CreateContext();
        ModelCapabilityView entity = await SeedProviderAsync(db, InferenceProviderKind.OpenJev, InferenceProviderCapability.Decision);

        ActionResult<ProviderResponse> result = await CreateController(db)
            .Update(entity.ProviderId, EndpointUpdate("http://127.0.0.1:8791"), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        var response = (ProviderResponse)((OkObjectResult)result.Result!).Value!;
        response.Endpoint.Should().Be("http://127.0.0.1:8791");
    }

    [Fact]
    public async Task Update_accepts_a_docker_service_hostname_for_kind_OpenJev()
    {
        using InferenceApiDbContext db = CreateContext();
        ModelCapabilityView entity = await SeedProviderAsync(db, InferenceProviderKind.OpenJev, InferenceProviderCapability.Decision);

        ActionResult<ProviderResponse> result = await CreateController(db)
            .Update(entity.ProviderId, EndpointUpdate("http://decision:8791"), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Update_rejects_a_non_http_endpoint_for_kind_OpenJev_with_400()
    {
        using InferenceApiDbContext db = CreateContext();
        ModelCapabilityView entity = await SeedProviderAsync(db, InferenceProviderKind.OpenJev, InferenceProviderCapability.Decision);

        ActionResult<ProviderResponse> result = await CreateController(db)
            .Update(entity.ProviderId, EndpointUpdate("ftp://127.0.0.1:8791"), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        ErrorMessage((BadRequestObjectResult)result.Result!).Should().Be(EndpointRejectedMessage);
    }

    [Fact]
    public async Task Update_rejects_a_loopback_endpoint_for_a_non_OpenJev_kind_with_400()
    {
        using InferenceApiDbContext db = CreateContext();
        ModelCapabilityView entity = await SeedProviderAsync(db, InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat);

        ActionResult<ProviderResponse> result = await CreateController(db)
            .Update(entity.ProviderId, EndpointUpdate("http://127.0.0.1:8791"), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        ErrorMessage((BadRequestObjectResult)result.Result!).Should().Be(EndpointRejectedMessage);
    }

    // --- The kind belongs to the provider account and is fixed at creation ---
    //
    // The flat contract let a PUT change the kind, and the endpoint gate then had to be judged against the
    // EFFECTIVE (endpoint, kind) pair, or a row created as OpenJev — the kind IsDisallowedEndpoint(endpoint, kind)
    // exempts from every address rule — could be flipped to a non-exempt kind with its private endpoint riding along.
    // The tree API removes the attack instead of guarding it: the update body has no kind, so the stored kind is the
    // one the gate judges every new endpoint against, and a stored private endpoint can never lose its exemption.

    [Fact]
    public void Update_body_has_no_kind_so_an_exempt_row_can_never_be_flipped_off_its_exemption()
    {
        typeof(UpdateProviderRequest).GetProperties().Select(p => p.Name)
            .Should().NotContain("Kind", "the kind is fixed when the provider account is created");
    }

    [Theory]
    // Loopback.
    [InlineData("http://127.0.0.1:8791")]
    // Link-local, i.e. the cloud instance-metadata address.
    [InlineData("http://169.254.169.254/latest/meta-data")]
    // CGNAT shared space.
    [InlineData("http://100.64.0.1:8791")]
    // RFC 1918 private.
    [InlineData("http://10.0.0.5:8080")]
    // IPv6 unique-local.
    [InlineData("http://[fd00::1]:8791")]
    public async Task Update_judges_a_new_private_endpoint_against_the_stored_non_exempt_kind_and_writes_nothing(
        string privateEndpoint)
    {
        using InferenceApiDbContext db = CreateContext();
        const string publicEndpoint = "https://93.184.216.7/v1";
        ModelCapabilityView entity = await SeedProviderAtEndpointAsync(
            db, InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat, publicEndpoint);

        ActionResult<ProviderResponse> result = await CreateController(db)
            .Update(entity.ProviderId, EndpointUpdate(privateEndpoint), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        ErrorMessage((BadRequestObjectResult)result.Result!).Should().Be(EndpointRejectedMessage);

        // ChangeTracker.Clear() first, so the re-read is about what was persisted, not the tracked instance.
        db.ChangeTracker.Clear();
        ModelCapabilityView stored = await db.FlatProviders().SingleAsync(p => p.Id == entity.Id);
        stored.Kind.Should().Be(InferenceProviderKind.DeepSeek);
        stored.Endpoint.Should().Be(publicEndpoint);
    }

    [Fact]
    public async Task Update_of_an_OpenJev_account_on_a_private_endpoint_keeps_its_kind_and_endpoint()
    {
        // The exemption is not weakened: a PUT that touches something else on a self-hosted OpenJev account keeps
        // its loopback endpoint and its kind.
        using InferenceApiDbContext db = CreateContext();
        const string privateEndpoint = "http://127.0.0.1:8791";
        ModelCapabilityView entity = await SeedProviderAtEndpointAsync(
            db, InferenceProviderKind.OpenJev, InferenceProviderCapability.Decision, privateEndpoint);

        ActionResult<ProviderResponse> result = await CreateController(db)
            .Update(entity.ProviderId, new UpdateProviderRequest(Name: "jev-renamed", TimeoutSeconds: 30), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        var response = (ProviderResponse)((OkObjectResult)result.Result!).Value!;
        response.Kind.Should().Be("OpenJev");
        response.Endpoint.Should().Be(privateEndpoint);
        response.Name.Should().Be("jev-renamed");
    }

    [Fact]
    public async Task Create_accepts_a_private_endpoint_for_OpenAICompatible()
    {
        using InferenceApiDbContext db = CreateContext();
        CreateProviderRequest req = ProviderTree.OneModel(
            Name: $"Local-{Guid.NewGuid():N}", Kind: "OpenAICompatible", Endpoint: "http://100.89.1.2:8000/v1",
            ModelName: "m", ApiKey: "k", IsDefault: false, IsEnabled: true, TimeoutSeconds: null, Capability: "Chat");
        var result = await CreateController(db).Create(req, CancellationToken.None);
        result.Result.Should().NotBeOfType<BadRequestObjectResult>();
    }

    // --- TestSaved: the same endpoint gate, judged against the STORED row (T-208's second change) ---
    //
    // TestSaved (`POST /{id}/test`) is the one provider endpoint that performs OUTBOUND I/O against
    // a row it did not just validate. Without the gate, a row left at a private address by a direct
    // database write — exactly the case this endpoint exists to diagnose — is an outbound-I/O
    // primitive for any caller holding an admin token.
    //
    // The gate sits immediately after the IsUnsupportedCombination guard and BEFORE the API key is
    // decrypted and before any client is constructed, so a rejection must leave zero outbound calls
    // AND zero persisted test result. Both are asserted below: a status-code-only test would pass on
    // a gate placed AFTER the call, which is precisely the bug being pinned. The decision client is
    // therefore STUBBED to a working answer rather than left to return null — with the gate removed
    // the stub makes the call SUCCEED, so the negative control fails on the assertion rather than
    // silently coinciding with a failure the endpoint would have reported anyway.
    //
    // Deliberately the kind-aware overload, so a legitimate self-hosted OpenJev row (loopback, a
    // docker service name, host.docker.internal) still tests. That half is asserted as hard as the
    // rejection: "not the rejection 400" would be satisfied by any other 400/404, so the positive
    // case asserts the row was ACTUALLY tested — a client built from the loopback endpoint, and a
    // success persisted.

    /// <summary>
    /// The controller plus the decision-client factory still reachable, so a test can assert on
    /// <c>Get(...)</c> never having been called — the execution proof, not just the status code —
    /// and can stub the client. Same plumbing as <see cref="CreateController"/>; that helper is left
    /// exactly as it is, since every pre-existing test builds its controller through it.
    /// </summary>
    private sealed record ControllerHarness(
        InferenceProvidersController Controller,
        Mock<IDecisionClientFactory> DecisionClientFactory);

    private static ControllerHarness CreateHarness(
        InferenceApiDbContext db, Mock<IDecisionClientFactory> decisionClientFactory) => new(
        new InferenceProvidersController(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            new Mock<IChatClientFactory>().Object,
            new Mock<ITranscriptionClientFactory>().Object,
            decisionClientFactory.Object,
            new Mock<IVideoGenerationClientFactory>().Object,
            new Mock<ISpeechSynthesisClientFactory>().Object,
            new Mock<ISecretProtector>().Object,
            new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>().Object),
        decisionClientFactory);

    /// <summary>
    /// Stubs the factory to hand back a decision client that answers the endpoint's fixed
    /// calibration question ("Is 2 greater than 1?") the way a WORKING backend does — renormalized
    /// P(Yes) above the 0.9 threshold — so both cases below can distinguish "the row was really
    /// tested and succeeded" from "the gate fired".
    /// </summary>
    private static void StubHighConfidenceNoul(Mock<IDecisionClientFactory> factory)
    {
        var client = new Mock<IDecisionClient>();
        client.Setup(c => c.DecideAsync(
                It.IsAny<DecisionState>(),
                It.IsAny<IReadOnlyList<DecisionQuestion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionResult(
                "seed-model",
                new Dictionary<string, DecisionAnswer>
                {
                    {
                        "Noul",
                        new DecisionAnswer(
                            "Noul",
                            DecisionKind.Noul,
                            null,
                            null,
                            0.97,
                            null,
                            new Dictionary<string, double> { { "true", 0.97 }, { "false", 0.03 } }
                        )
                    }
                },
                10,
                5));

        factory.Setup(f => f.Get(It.IsAny<ResolvedDecisionProvider>())).Returns(client.Object);
    }

    [Theory]
    // Loopback — the compose service's own bind, and the address the positive case below uses.
    [InlineData("http://127.0.0.1:8791")]
    // Link-local, i.e. the cloud instance-metadata address.
    [InlineData("http://169.254.169.254/latest/meta-data")]
    // RFC 1918 private.
    [InlineData("http://10.0.0.5:8080")]
    public async Task TestSaved_rejects_a_stored_private_endpoint_for_a_non_exempt_kind_with_400(
        string privateEndpoint)
    {
        using InferenceApiDbContext db = CreateContext();
        ModelCapabilityView entity = await SeedProviderAtEndpointAsync(
            db, InferenceProviderKind.DeepSeek, InferenceProviderCapability.Decision, privateEndpoint);
        ControllerHarness harness = CreateHarness(db, new Mock<IDecisionClientFactory>());
        StubHighConfidenceNoul(harness.DecisionClientFactory);

        ActionResult<TestInferenceProviderResponse> result =
            await harness.Controller.TestSaved(db, entity.Id, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        ErrorMessage((BadRequestObjectResult)result.Result!).Should().Be(EndpointRejectedMessage);

        // Execution proof, not just the status code. The stub above would have made the call
        // SUCCEED, so a gate placed after the factory call still returns 200 here — and this
        // assertion is what catches that, together with the persisted-result assertion below.
        harness.DecisionClientFactory.Verify(
            f => f.Get(It.IsAny<ResolvedDecisionProvider>()), Times.Never);

        // ChangeTracker.Clear() first: without it the re-read below would be served by the SAME
        // tracked instance, and the assertion would not be about what was actually persisted.
        db.ChangeTracker.Clear();
        ModelCapabilityView stored = await db.FlatProviders().SingleAsync(p => p.Id == entity.Id);
        stored.LastTestAt.Should().BeNull();
        stored.LastTestOk.Should().BeNull();
    }

    [Fact]
    public async Task TestSaved_tests_a_stored_OpenJev_decision_row_on_a_loopback_endpoint()
    {
        // The exemption must not be WEAKENED by the gate. TestSaved deliberately uses the kind-aware
        // overload so a legitimate self-hosted OpenJev row still tests; this asserts it is really
        // tested rather than merely "not rejected".
        using InferenceApiDbContext db = CreateContext();
        const string privateEndpoint = "http://127.0.0.1:8791";
        ModelCapabilityView entity = await SeedProviderAtEndpointAsync(
            db, InferenceProviderKind.OpenJev, InferenceProviderCapability.Decision, privateEndpoint);
        ControllerHarness harness = CreateHarness(db, new Mock<IDecisionClientFactory>());
        StubHighConfidenceNoul(harness.DecisionClientFactory);

        ActionResult<TestInferenceProviderResponse> result =
            await harness.Controller.TestSaved(db, entity.Id, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        var response = (TestInferenceProviderResponse)((OkObjectResult)result.Result!).Value!;
        response.Ok.Should().BeTrue();

        // The row reached the DECISION branch with its stored loopback endpoint intact — i.e. the
        // gate let it through and the real test path ran, rather than the request dying earlier for
        // an unrelated reason.
        harness.DecisionClientFactory.Verify(
            f => f.Get(It.Is<ResolvedDecisionProvider>(p => p.Endpoint == privateEndpoint)), Times.Once);

        db.ChangeTracker.Clear();
        ModelCapabilityView stored = await db.FlatProviders().SingleAsync(p => p.Id == entity.Id);
        stored.LastTestAt.Should().NotBeNull();
        stored.LastTestOk.Should().BeTrue();
    }
}
