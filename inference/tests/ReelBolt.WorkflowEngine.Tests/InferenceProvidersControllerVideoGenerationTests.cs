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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class InferenceProvidersControllerVideoGenerationTests
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
    public async Task Create_accepts_capability_VideoGeneration_with_kind_MiniMax()
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
            Name: "MiniMaxVideoGen",
            Kind: "MiniMax",
            Endpoint: "https://example.com/api",
            ModelName: "minimax-video-01",
            ApiKey: "test-key",
            IsDefault: false,
            IsEnabled: true,
            TimeoutSeconds: null,
            Capability: "VideoGeneration"
        );

        ActionResult<ProviderResponse> result = await controller.Create(request, CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        var body = ((CreatedAtActionResult)result.Result!).Value;
        var response = ((ProviderResponse)body!).Flat();
        response.Capability.Should().Be("VideoGeneration");
    }

    [Fact]
    public async Task Create_rejects_capability_VideoGeneration_for_kind_OpenAICompatible_with_400()
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
            Name: "OpenAIVideoGenAttempt",
            Kind: "OpenAICompatible",
            Endpoint: "https://example.com/api",
            ModelName: "test-model",
            ApiKey: "test-key",
            IsDefault: false,
            IsEnabled: true,
            TimeoutSeconds: null,
            Capability: "VideoGeneration"
        );

        ActionResult<ProviderResponse> result = await controller.Create(request, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        var badRequest = (BadRequestObjectResult)result.Result!;
        badRequest.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task Create_rejects_capability_VideoGeneration_for_kind_Anthropic_with_400()
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
            Name: "AnthropicVideoGenAttempt",
            Kind: "Anthropic",
            Endpoint: "https://example.com",
            ModelName: "claude-opus-5",
            ApiKey: "test-key",
            IsDefault: false,
            IsEnabled: true,
            TimeoutSeconds: null,
            Capability: "VideoGeneration"
        );

        ActionResult<ProviderResponse> result = await controller.Create(request, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_rejects_kind_MiniMax_for_capability_Chat_with_400()
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
            Name: "MiniMaxChat",
            Kind: "MiniMax",
            Endpoint: "https://example.com/api",
            ModelName: "minimax-chat-01",
            ApiKey: "test-key",
            IsDefault: false,
            IsEnabled: true,
            TimeoutSeconds: null,
            Capability: "Chat"
        );

        ActionResult<ProviderResponse> result = await controller.Create(request, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_rejects_kind_MiniMax_for_capability_Transcription_with_400()
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
            Name: "MiniMaxTranscription",
            Kind: "MiniMax",
            Endpoint: "https://example.com/api",
            ModelName: "minimax-transcribe",
            ApiKey: "test-key",
            IsDefault: false,
            IsEnabled: true,
            TimeoutSeconds: null,
            Capability: "Transcription"
        );

        ActionResult<ProviderResponse> result = await controller.Create(request, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- Update endpoint tests ---

    [Fact]
    public async Task AddCapability_rejects_VideoGeneration_under_an_OpenAICompatible_model_with_400()
    {
        using InferenceApiDbContext db = CreateContext();
        var chatClientFactory = new Mock<IChatClientFactory>();
        var transcriptionClientFactory = new Mock<ITranscriptionClientFactory>();
        var decisionClientFactory = new Mock<IDecisionClientFactory>();
        var videoGenerationClientFactory = new Mock<IVideoGenerationClientFactory>();
        var speechSynthesisClientFactory = new Mock<ISpeechSynthesisClientFactory>();
        var secretProtector = new Mock<ISecretProtector>();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>();

        // Create an existing provider
        ModelCapabilityView existingProvider = new()
        {
            Id = Guid.NewGuid(),
            Name = "ExistingChat",
            Kind = InferenceProviderKind.OpenAICompatible,
            Capability = InferenceProviderCapability.Chat,
            Endpoint = "https://example.com/api",
            ModelName = "test-model",
            ApiKeyEncrypted = "encrypted",
            IsDefault = false,
            IsEnabled = true,
            TimeoutSeconds = null,
            CreatedAt = DateTime.UtcNow
        };

        db.AddFlat(existingProvider);
        await db.SaveChangesAsync();

        InferenceProvidersController controller = new(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            chatClientFactory.Object, transcriptionClientFactory.Object, decisionClientFactory.Object,
            videoGenerationClientFactory.Object, speechSynthesisClientFactory.Object,
            secretProtector.Object, logger.Object);

        ActionResult<CapabilityResponse> result =
            await controller.AddCapabilityBeside(db, existingProvider.Id, "VideoGeneration", CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task AddCapability_rejects_Chat_under_a_MiniMax_model_with_400()
    {
        using InferenceApiDbContext db = CreateContext();
        var chatClientFactory = new Mock<IChatClientFactory>();
        var transcriptionClientFactory = new Mock<ITranscriptionClientFactory>();
        var decisionClientFactory = new Mock<IDecisionClientFactory>();
        var videoGenerationClientFactory = new Mock<IVideoGenerationClientFactory>();
        var speechSynthesisClientFactory = new Mock<ISpeechSynthesisClientFactory>();
        var secretProtector = new Mock<ISecretProtector>();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>();

        // Create an existing MiniMax provider
        ModelCapabilityView existingProvider = new()
        {
            Id = Guid.NewGuid(),
            Name = "ExistingMiniMax",
            Kind = InferenceProviderKind.MiniMax,
            Capability = InferenceProviderCapability.VideoGeneration,
            Endpoint = "https://example.com/api",
            ModelName = "minimax-video-01",
            ApiKeyEncrypted = "encrypted",
            IsDefault = false,
            IsEnabled = true,
            TimeoutSeconds = null,
            CreatedAt = DateTime.UtcNow
        };

        db.AddFlat(existingProvider);
        await db.SaveChangesAsync();

        InferenceProvidersController controller = new(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            chatClientFactory.Object, transcriptionClientFactory.Object, decisionClientFactory.Object,
            videoGenerationClientFactory.Object, speechSynthesisClientFactory.Object,
            secretProtector.Object, logger.Object);

        ActionResult<CapabilityResponse> result =
            await controller.AddCapabilityBeside(db, existingProvider.Id, "Chat", CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- Test endpoint tests ---

    [Fact]
    public async Task TestSaved_succeeds_for_VideoGeneration_capability_with_valid_auth()
    {
        using InferenceApiDbContext db = CreateContext();
        var chatClientFactory = new Mock<IChatClientFactory>();
        var transcriptionClientFactory = new Mock<ITranscriptionClientFactory>();
        var decisionClientFactory = new Mock<IDecisionClientFactory>();
        var videoGenerationClientFactory = new Mock<IVideoGenerationClientFactory>();
        var speechSynthesisClientFactory = new Mock<ISpeechSynthesisClientFactory>();
        var secretProtector = new Mock<ISecretProtector>();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>();

        // Setup mock video generation client
        var mockVideoGenerationClient = new Mock<IVideoGenerationClient>();
        mockVideoGenerationClient
            .Setup(c => c.ProbeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoGenerationProbeResult(Ok: true, AuthRejected: false, Reason: "Provider answered HTTP 400 (bad_request_error)"));

        // Ensure SubmitAsync is never called
        mockVideoGenerationClient
            .Setup(c => c.SubmitAsync(
                It.IsAny<VideoGenerationRequest>(),
                It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("SubmitAsync should not be called during test"));

        videoGenerationClientFactory
            .Setup(f => f.Get(It.IsAny<ResolvedVideoGenerationProvider>()))
            .Returns(mockVideoGenerationClient.Object);

        secretProtector
            .Setup(sp => sp.TryUnprotect(It.IsAny<string>(), out It.Ref<string>.IsAny))
            .Callback((string encrypted, ref string decrypted) =>
            {
                decrypted = "decrypted-key";
            })
            .Returns(true);

        // Create an existing provider
        ModelCapabilityView existingProvider = new()
        {
            Id = Guid.NewGuid(),
            Name = "TestMiniMaxProvider",
            Kind = InferenceProviderKind.MiniMax,
            Capability = InferenceProviderCapability.VideoGeneration,
            Endpoint = "https://example.com/api",
            ModelName = "minimax-video-01",
            ApiKeyEncrypted = "encrypted-key",
            IsDefault = false,
            IsEnabled = true,
            TimeoutSeconds = null,
            CreatedAt = DateTime.UtcNow
        };

        db.AddFlat(existingProvider);
        await db.SaveChangesAsync();

        InferenceProvidersController controller = new(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            chatClientFactory.Object, transcriptionClientFactory.Object, decisionClientFactory.Object,
            videoGenerationClientFactory.Object, speechSynthesisClientFactory.Object,
            secretProtector.Object, logger.Object);

        ActionResult<TestInferenceProviderResponse> result = await controller.TestSaved(db, existingProvider.Id, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        var body = ((OkObjectResult)result.Result!).Value;
        var response = (TestInferenceProviderResponse)body!;
        response.Ok.Should().BeTrue();

        // Verify SubmitAsync was never called
        mockVideoGenerationClient.Verify(
            c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TestSaved_returns_ok_false_for_VideoGeneration_capability_on_auth_failure()
    {
        using InferenceApiDbContext db = CreateContext();
        var chatClientFactory = new Mock<IChatClientFactory>();
        var transcriptionClientFactory = new Mock<ITranscriptionClientFactory>();
        var decisionClientFactory = new Mock<IDecisionClientFactory>();
        var videoGenerationClientFactory = new Mock<IVideoGenerationClientFactory>();
        var speechSynthesisClientFactory = new Mock<ISpeechSynthesisClientFactory>();
        var secretProtector = new Mock<ISecretProtector>();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>();

        // Setup mock video generation client: the probe reports an auth rejection
        var mockVideoGenerationClient = new Mock<IVideoGenerationClient>();
        mockVideoGenerationClient
            .Setup(c => c.ProbeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoGenerationProbeResult(Ok: false, AuthRejected: true, Reason: "Provider rejected authentication (HTTP 401)."));

        videoGenerationClientFactory
            .Setup(f => f.Get(It.IsAny<ResolvedVideoGenerationProvider>()))
            .Returns(mockVideoGenerationClient.Object);

        secretProtector
            .Setup(sp => sp.TryUnprotect(It.IsAny<string>(), out It.Ref<string>.IsAny))
            .Callback((string encrypted, ref string decrypted) =>
            {
                decrypted = "decrypted-key";
            })
            .Returns(true);

        // Create an existing provider
        ModelCapabilityView existingProvider = new()
        {
            Id = Guid.NewGuid(),
            Name = "TestMiniMaxProvider",
            Kind = InferenceProviderKind.MiniMax,
            Capability = InferenceProviderCapability.VideoGeneration,
            Endpoint = "https://example.com/api",
            ModelName = "minimax-video-01",
            ApiKeyEncrypted = "encrypted-key",
            IsDefault = false,
            IsEnabled = true,
            TimeoutSeconds = null,
            CreatedAt = DateTime.UtcNow
        };

        db.AddFlat(existingProvider);
        await db.SaveChangesAsync();

        InferenceProvidersController controller = new(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            chatClientFactory.Object, transcriptionClientFactory.Object, decisionClientFactory.Object,
            videoGenerationClientFactory.Object, speechSynthesisClientFactory.Object,
            secretProtector.Object, logger.Object);

        ActionResult<TestInferenceProviderResponse> result = await controller.TestSaved(db, existingProvider.Id, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        var body = ((OkObjectResult)result.Result!).Value;
        var response = (TestInferenceProviderResponse)body!;
        response.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task TestUnsaved_succeeds_for_VideoGeneration_capability_with_valid_auth()
    {
        using InferenceApiDbContext db = CreateContext();
        var chatClientFactory = new Mock<IChatClientFactory>();
        var transcriptionClientFactory = new Mock<ITranscriptionClientFactory>();
        var decisionClientFactory = new Mock<IDecisionClientFactory>();
        var videoGenerationClientFactory = new Mock<IVideoGenerationClientFactory>();
        var speechSynthesisClientFactory = new Mock<ISpeechSynthesisClientFactory>();
        var secretProtector = new Mock<ISecretProtector>();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>();

        // Setup mock video generation client
        var mockVideoGenerationClient = new Mock<IVideoGenerationClient>();
        mockVideoGenerationClient
            .Setup(c => c.ProbeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoGenerationProbeResult(Ok: true, AuthRejected: false, Reason: "Provider answered HTTP 400 (bad_request_error)"));

        // Ensure SubmitAsync is never called
        mockVideoGenerationClient
            .Setup(c => c.SubmitAsync(
                It.IsAny<VideoGenerationRequest>(),
                It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("SubmitAsync should not be called during test"));

        videoGenerationClientFactory
            .Setup(f => f.Get(It.IsAny<ResolvedVideoGenerationProvider>()))
            .Returns(mockVideoGenerationClient.Object);

        InferenceProvidersController controller = new(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            chatClientFactory.Object, transcriptionClientFactory.Object, decisionClientFactory.Object,
            videoGenerationClientFactory.Object, speechSynthesisClientFactory.Object,
            secretProtector.Object, logger.Object);

        TestProviderRequest request = new(
            Kind: "MiniMax",
            Endpoint: "https://example.com/api",
            ModelCode: "minimax-video-01",
            ApiKey: "test-key",
            Capability: "VideoGeneration"
        );

        ActionResult<TestInferenceProviderResponse> result = await controller.TestUnsaved(request, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        var body = ((OkObjectResult)result.Result!).Value;
        var response = (TestInferenceProviderResponse)body!;
        response.Ok.Should().BeTrue();

        // Verify SubmitAsync was never called
        mockVideoGenerationClient.Verify(
            c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TestUnsaved_returns_ok_false_for_VideoGeneration_capability_on_auth_failure()
    {
        using InferenceApiDbContext db = CreateContext();
        var chatClientFactory = new Mock<IChatClientFactory>();
        var transcriptionClientFactory = new Mock<ITranscriptionClientFactory>();
        var decisionClientFactory = new Mock<IDecisionClientFactory>();
        var videoGenerationClientFactory = new Mock<IVideoGenerationClientFactory>();
        var speechSynthesisClientFactory = new Mock<ISpeechSynthesisClientFactory>();
        var secretProtector = new Mock<ISecretProtector>();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>();

        // Setup mock video generation client: the probe reports an auth rejection
        var mockVideoGenerationClient = new Mock<IVideoGenerationClient>();
        mockVideoGenerationClient
            .Setup(c => c.ProbeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoGenerationProbeResult(Ok: false, AuthRejected: true, Reason: "Provider rejected authentication (HTTP 401)."));

        videoGenerationClientFactory
            .Setup(f => f.Get(It.IsAny<ResolvedVideoGenerationProvider>()))
            .Returns(mockVideoGenerationClient.Object);

        InferenceProvidersController controller = new(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            chatClientFactory.Object, transcriptionClientFactory.Object, decisionClientFactory.Object,
            videoGenerationClientFactory.Object, speechSynthesisClientFactory.Object,
            secretProtector.Object, logger.Object);

        TestProviderRequest request = new(
            Kind: "MiniMax",
            Endpoint: "https://example.com/api",
            ModelCode: "minimax-video-01",
            ApiKey: "test-key",
            Capability: "VideoGeneration"
        );

        ActionResult<TestInferenceProviderResponse> result = await controller.TestUnsaved(request, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        var body = ((OkObjectResult)result.Result!).Value;
        var response = (TestInferenceProviderResponse)body!;
        response.Ok.Should().BeFalse();
    }

    private sealed class StatusCodeHandler(System.Net.HttpStatusCode code) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent("""{"error":{"message":"denied"}}""")
            });
        }
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized)]
    [InlineData(System.Net.HttpStatusCode.Forbidden)]
    public async Task TestUnsaved_probe_through_real_MiniMax_client_reports_auth_rejection(System.Net.HttpStatusCode code)
    {
        using InferenceApiDbContext db = CreateContext();
        var videoGenerationClientFactory = new Mock<IVideoGenerationClientFactory>();
        var speechSynthesisClientFactory = new Mock<ISpeechSynthesisClientFactory>();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>();

        // A real MiniMax client over a stub handler: no request ever leaves the process.
        StatusCodeHandler handler = new(code);
        HttpClient httpClient = new(handler) { BaseAddress = new Uri("https://api.minimax.io") };
        MiniMaxVideoGenerationClient realClient = new(httpClient, "test-key");

        videoGenerationClientFactory
            .Setup(f => f.Get(It.IsAny<ResolvedVideoGenerationProvider>()))
            .Returns(realClient);

        InferenceProvidersController controller = new(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            new Mock<IChatClientFactory>().Object, new Mock<ITranscriptionClientFactory>().Object,
            new Mock<IDecisionClientFactory>().Object,
            videoGenerationClientFactory.Object, speechSynthesisClientFactory.Object,
            new Mock<ISecretProtector>().Object, logger.Object);

        TestProviderRequest request = new(
            Kind: "MiniMax",
            Endpoint: "https://api.minimax.io",
            ModelCode: "MiniMax-H3",
            ApiKey: "test-key",
            Capability: "VideoGeneration"
        );

        ActionResult<TestInferenceProviderResponse> result = await controller.TestUnsaved(request, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        var response = (TestInferenceProviderResponse)((OkObjectResult)result.Result!).Value!;
        response.Ok.Should().BeFalse();
        response.Error.Should().Be("Provider rejected authentication.");
        handler.Calls.Should().Be(1);
    }

    // --- Honest probe: ok only on a proving vendor response (T-040) ---

    private const string SavedProbeKey = "saved-secret-key-987";

    /// <summary>Handler that answers every request via <c>respond</c> and records method + path.</summary>
    private sealed class ProbeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath));
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(System.Net.HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private const string VendorInvalidTaskIdBody =
        """{"type":"error","error":{"type":"bad_request_error","message":"invalid task_id (2013)","http_code":"400"},"request_id":"r"}""";

    private static async Task<(TestInferenceProviderResponse Response, ModelCapabilityView Saved, ProbeHandler Handler)> TestSavedThroughRealClient(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        using InferenceApiDbContext db = CreateContext();
        ProbeHandler handler = new(respond);
        MiniMaxVideoGenerationClient realClient = new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.minimax.io") }, SavedProbeKey);

        var videoGenerationClientFactory = new Mock<IVideoGenerationClientFactory>();
        var speechSynthesisClientFactory = new Mock<ISpeechSynthesisClientFactory>();
        videoGenerationClientFactory
            .Setup(f => f.Get(It.IsAny<ResolvedVideoGenerationProvider>()))
            .Returns(realClient);

        var secretProtector = new Mock<ISecretProtector>();
        secretProtector
            .Setup(sp => sp.TryUnprotect(It.IsAny<string>(), out It.Ref<string>.IsAny))
            .Callback((string encrypted, ref string decrypted) => { decrypted = SavedProbeKey; })
            .Returns(true);

        ModelCapabilityView provider = new()
        {
            Id = Guid.NewGuid(),
            Name = "SavedMiniMax",
            Kind = InferenceProviderKind.MiniMax,
            Capability = InferenceProviderCapability.VideoGeneration,
            Endpoint = "https://api.minimax.io",
            ModelName = "MiniMax-H3",
            ApiKeyEncrypted = "encrypted-key",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };
        db.AddFlat(provider);
        await db.SaveChangesAsync();

        InferenceProvidersController controller = new(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            new Mock<IChatClientFactory>().Object, new Mock<ITranscriptionClientFactory>().Object,
            new Mock<IDecisionClientFactory>().Object,
            videoGenerationClientFactory.Object, speechSynthesisClientFactory.Object,
            secretProtector.Object,
            new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>().Object);

        ActionResult<TestInferenceProviderResponse> result = await controller.TestSaved(db, provider.Id, CancellationToken.None);
        result.Result.Should().BeOfType<OkObjectResult>();
        var response = (TestInferenceProviderResponse)((OkObjectResult)result.Result!).Value!;
        ModelCapabilityView saved = await db.FlatProviders().AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        return (response, saved, handler);
    }

    [Fact]
    public async Task TestSaved_proving_vendor_response_is_ok_and_persists_LastTestOk_true()
    {
        var (response, saved, handler) = await TestSavedThroughRealClient(_ =>
            Json(System.Net.HttpStatusCode.BadRequest, VendorInvalidTaskIdBody));

        response.Ok.Should().BeTrue();
        response.Error.Should().BeNull();
        saved.LastTestOk.Should().BeTrue();
        saved.LastTestError.Should().BeNull();
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].Path.Should().StartWith("/v2/query/video_generation/");
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.InternalServerError)]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable)]
    public async Task TestSaved_5xx_is_not_ok_and_persists_LastTestOk_false(System.Net.HttpStatusCode code)
    {
        var (response, saved, handler) = await TestSavedThroughRealClient(_ =>
            Json(code, """{"type":"error","error":{"type":"server_error","message":"internal error (1000)","http_code":"500"}}"""));

        response.Ok.Should().BeFalse();
        response.Error.Should().Contain(((int)code).ToString());
        response.Error.Should().NotContain(SavedProbeKey);
        saved.LastTestOk.Should().BeFalse();
        saved.LastTestError.Should().Be(response.Error);
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task TestSaved_network_error_is_not_ok_and_persists_LastTestOk_false()
    {
        var (response, saved, handler) = await TestSavedThroughRealClient(_ =>
            throw new HttpRequestException("Connection refused (api.minimax.io:443)"));

        response.Ok.Should().BeFalse();
        response.Error.Should().Contain("Network error");
        response.Error.Should().NotContain(SavedProbeKey);
        saved.LastTestOk.Should().BeFalse();
        saved.LastTestError.Should().Be(response.Error);
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task TestSaved_html_200_is_not_ok()
    {
        var (response, saved, _) = await TestSavedThroughRealClient(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("<html>captive portal</html>", System.Text.Encoding.UTF8, "text/html")
        });

        response.Ok.Should().BeFalse();
        response.Error.Should().NotContain("captive portal");
        saved.LastTestOk.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TestUnsaved_5xx_or_network_error_is_not_ok_with_error(bool networkError)
    {
        using InferenceApiDbContext db = CreateContext();
        ProbeHandler handler = new(_ => networkError
            ? throw new HttpRequestException("Connection refused")
            : Json(System.Net.HttpStatusCode.BadGateway, "{}"));
        MiniMaxVideoGenerationClient realClient = new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.minimax.io") }, "unsaved-key-555");
        var videoGenerationClientFactory = new Mock<IVideoGenerationClientFactory>();
        var speechSynthesisClientFactory = new Mock<ISpeechSynthesisClientFactory>();
        videoGenerationClientFactory
            .Setup(f => f.Get(It.IsAny<ResolvedVideoGenerationProvider>()))
            .Returns(realClient);

        InferenceProvidersController controller = new(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            new Mock<IChatClientFactory>().Object, new Mock<ITranscriptionClientFactory>().Object,
            new Mock<IDecisionClientFactory>().Object,
            videoGenerationClientFactory.Object, speechSynthesisClientFactory.Object,
            new Mock<ISecretProtector>().Object,
            new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>().Object);

        ActionResult<TestInferenceProviderResponse> result = await controller.TestUnsaved(
            new TestProviderRequest(Kind: "MiniMax", Endpoint: "https://api.minimax.io",
                ModelCode: "MiniMax-H3", ApiKey: "unsaved-key-555", Capability: "VideoGeneration"),
            CancellationToken.None);

        var response = (TestInferenceProviderResponse)((OkObjectResult)result.Result!).Value!;
        response.Ok.Should().BeFalse();
        response.Error.Should().NotBeNullOrWhiteSpace();
        response.Error.Should().NotContain("unsaved-key-555");
        response.Error.Should().Contain(networkError ? "Network error" : "502");
    }

    [Fact]
    public async Task TestUnsaved_unexpected_exception_from_probe_is_not_ok()
    {
        using InferenceApiDbContext db = CreateContext();
        var client = new Mock<IVideoGenerationClient>();
        client.Setup(c => c.ProbeAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var videoGenerationClientFactory = new Mock<IVideoGenerationClientFactory>();
        var speechSynthesisClientFactory = new Mock<ISpeechSynthesisClientFactory>();
        videoGenerationClientFactory
            .Setup(f => f.Get(It.IsAny<ResolvedVideoGenerationProvider>()))
            .Returns(client.Object);

        InferenceProvidersController controller = new(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            new Mock<IChatClientFactory>().Object, new Mock<ITranscriptionClientFactory>().Object,
            new Mock<IDecisionClientFactory>().Object,
            videoGenerationClientFactory.Object, speechSynthesisClientFactory.Object,
            new Mock<ISecretProtector>().Object,
            new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>().Object);

        ActionResult<TestInferenceProviderResponse> result = await controller.TestUnsaved(
            new TestProviderRequest(Kind: "MiniMax", Endpoint: "https://api.minimax.io",
                ModelCode: "MiniMax-H3", ApiKey: "k", Capability: "VideoGeneration"),
            CancellationToken.None);

        var response = (TestInferenceProviderResponse)((OkObjectResult)result.Result!).Value!;
        response.Ok.Should().BeFalse();
        response.Error.Should().NotContain("boom");
        client.Verify(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Higgsfield (T-602): the appended kind, in both directions of the explicit allowlist ---

    private static InferenceProvidersController Controller(
        InferenceApiDbContext db, IVideoGenerationClientFactory videoGenerationClientFactory) =>
        Controller(db, videoGenerationClientFactory, new Mock<ISecretProtector>().Object);

    private static InferenceProvidersController Controller(
        InferenceApiDbContext db,
        IVideoGenerationClientFactory videoGenerationClientFactory,
        ISecretProtector secretProtector) =>
        new(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            new Mock<IChatClientFactory>().Object, new Mock<ITranscriptionClientFactory>().Object,
            new Mock<IDecisionClientFactory>().Object,
            videoGenerationClientFactory,
            // Master added ISpeechSynthesisClientFactory to the controller constructor inside the
            // same window this branch added the Higgsfield kind. The merge of this file is clean
            // textually but not semantically — the auto-merge kept this branch's call site, which
            // predates the new parameter — so the argument is supplied here.
            new Mock<ISpeechSynthesisClientFactory>().Object,
            secretProtector,
            new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>().Object);

    /// <summary>
    /// The controller answers rejections with <c>BadRequest(new { error = "..." })</c>; the property is
    /// only reachable by reflection because the payload is an anonymous type.
    /// </summary>
    private static string RejectionMessage(ActionResult result)
    {
        var badRequest = (BadRequestObjectResult)result;
        object? value = badRequest.Value;
        value.Should().NotBeNull();
        System.Reflection.PropertyInfo? errorProperty = value!.GetType().GetProperty("error");
        errorProperty.Should().NotBeNull("the 400 payload is documented to carry an 'error' string");
        return (string?)errorProperty!.GetValue(value) ?? string.Empty;
    }

    private static CreateProviderRequest CreateRequest(string kind, string capability) => ProviderTree.OneModel(
        Name: $"Provider-{kind}-{capability}",
        Kind: kind,
        Endpoint: "https://api.higgsfield.ai",
        ModelName: "kling-video/v3.0/std/text-to-video",
        ApiKey: "hf-key-id:hf-key-secret",
        IsDefault: false,
        IsEnabled: true,
        TimeoutSeconds: null,
        Capability: capability);

    private static ModelCapabilityView StoredProvider(string name, InferenceProviderKind kind, InferenceProviderCapability capability)
    {
        ModelCapabilityView provider = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Kind = kind,
            Capability = capability,
            Endpoint = "https://api.higgsfield.ai",
            ModelName = "kling-video/v3.0/std/text-to-video",
            ApiKeyEncrypted = "encrypted",
            IsDefault = false,
            IsEnabled = true,
            TimeoutSeconds = null,
            CreatedAt = DateTime.UtcNow
        };
        return provider;
    }

    [Fact]
    public async Task Create_accepts_kind_Higgsfield_with_capability_VideoGeneration()
    {
        using InferenceApiDbContext db = CreateContext();
        InferenceProvidersController controller = Controller(db, new Mock<IVideoGenerationClientFactory>().Object);

        ActionResult<ProviderResponse> result =
            await controller.Create(CreateRequest("Higgsfield", "VideoGeneration"), CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        var response = ((ProviderResponse)((CreatedAtActionResult)result.Result!).Value!).Flat();
        response.Kind.Should().Be("Higgsfield");
        response.Capability.Should().Be("VideoGeneration");
    }

    [Theory]
    [InlineData("Chat")]
    [InlineData("Transcription")]
    [InlineData("Vision")]
    [InlineData("Decision")]
    public async Task Create_rejects_kind_Higgsfield_for_any_capability_other_than_VideoGeneration(string capability)
    {
        // A Higgsfield row that claims Chat, Transcription, Vision or Decision would resolve into a
        // path that talks the wrong wire protocol entirely, so the rejection belongs at the boundary
        // where the row is written, not at first use inside a workflow.
        using InferenceApiDbContext db = CreateContext();
        InferenceProvidersController controller = Controller(db, new Mock<IVideoGenerationClientFactory>().Object);

        ActionResult<ProviderResponse> result =
            await controller.Create(CreateRequest("Higgsfield", capability), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        RejectionMessage(result.Result!).Should().Contain(capability);
    }

    [Theory]
    [InlineData("OpenAICompatible")]
    [InlineData("Anthropic")]
    [InlineData("Gemini")]
    [InlineData("DeepSeek")]
    [InlineData("TypeSafe")]
    [InlineData("OpenJev")]
    [InlineData("AzureOpenAI")]
    public async Task Create_rejects_capability_VideoGeneration_for_every_kind_but_MiniMax_and_Higgsfield(
        string kind)
    {
        // The allowlist has to READ as an explicit set. Asserting only the 400 would pass for any
        // rejection reason at all — including a typo that rejects everything — so this asserts the
        // message names BOTH allowed kinds.
        using InferenceApiDbContext db = CreateContext();
        InferenceProvidersController controller = Controller(db, new Mock<IVideoGenerationClientFactory>().Object);

        ActionResult<ProviderResponse> result =
            await controller.Create(CreateRequest(kind, "VideoGeneration"), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        string message = RejectionMessage(result.Result!);
        message.Should().Contain("MiniMax");
        message.Should().Contain("Higgsfield");
    }

    [Fact]
    public async Task AddModel_accepts_a_second_VideoGeneration_model_under_a_stored_Higgsfield_account()
    {
        using InferenceApiDbContext db = CreateContext();
        ModelCapabilityView existing = StoredProvider(
            "ExistingHiggsfield", InferenceProviderKind.Higgsfield, InferenceProviderCapability.VideoGeneration);
        db.AddFlat(existing);
        await db.SaveChangesAsync();
        (Guid providerId, _) = db.ParentsOf(existing.Id);

        InferenceProvidersController controller = Controller(db, new Mock<IVideoGenerationClientFactory>().Object);

        ActionResult<ModelResponse> result = await controller.CreateModel(providerId,
            new CreateModelRequest("kling-video/v3.0/pro/text-to-video", "Higgsfield pro", true,
                [new CreateCapabilityRequest("VideoGeneration")]),
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        var response = (ModelResponse)((OkObjectResult)result.Result!).Value!;
        response.Capabilities.Should().ContainSingle().Which.Capability.Should().Be("VideoGeneration");
        response.Capabilities[0].PriceSource.Should().Be("Oracle", "Higgsfield is billed from the vendor's own quote");
    }

    [Theory]
    [InlineData("Chat")]
    [InlineData("Transcription")]
    [InlineData("Vision")]
    [InlineData("Decision")]
    public async Task AddCapability_rejects_anything_but_VideoGeneration_under_a_Higgsfield_model(string capability)
    {
        using InferenceApiDbContext db = CreateContext();
        ModelCapabilityView existing = StoredProvider(
            "ExistingHiggsfield", InferenceProviderKind.Higgsfield, InferenceProviderCapability.VideoGeneration);
        db.AddFlat(existing);
        await db.SaveChangesAsync();

        InferenceProvidersController controller = Controller(db, new Mock<IVideoGenerationClientFactory>().Object);

        ActionResult<CapabilityResponse> result =
            await controller.AddCapabilityBeside(db, existing.Id, capability, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        RejectionMessage(result.Result!).Should().Contain(capability);
    }

    [Fact]
    public async Task AddCapability_rejects_VideoGeneration_under_a_stored_OpenAICompatible_model_with_a_message_naming_both_allowed_kinds()
    {
        using InferenceApiDbContext db = CreateContext();
        ModelCapabilityView existing = StoredProvider(
            "ExistingChat", InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.Chat);
        db.AddFlat(existing);
        await db.SaveChangesAsync();

        InferenceProvidersController controller = Controller(db, new Mock<IVideoGenerationClientFactory>().Object);

        ActionResult<CapabilityResponse> result =
            await controller.AddCapabilityBeside(db, existing.Id, "VideoGeneration", CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        string message = RejectionMessage(result.Result!);
        message.Should().Contain("MiniMax");
        message.Should().Contain("Higgsfield");
    }

    /// <summary>
    /// A real <see cref="HiggsfieldVideoGenerationClient"/> over a stub handler, mirroring the
    /// real-MiniMax probe tests above: no request ever leaves the process.
    /// </summary>
    private static async Task<(TestInferenceProviderResponse Response, ModelCapabilityView Saved, ProbeHandler Handler)>
        TestSavedThroughRealHiggsfieldClient()
    {
        using InferenceApiDbContext db = CreateContext();
        ProbeHandler handler = new(_ => Json(System.Net.HttpStatusCode.Unauthorized,
            """{"detail":"Invalid credentials"}"""));
        HiggsfieldVideoGenerationClient realClient = new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.higgsfield.ai") },
            SavedHiggsfieldCredential);

        var videoGenerationClientFactory = new Mock<IVideoGenerationClientFactory>();
        videoGenerationClientFactory
            .Setup(f => f.Get(It.IsAny<ResolvedVideoGenerationProvider>()))
            .Returns(realClient);

        var secretProtector = new Mock<ISecretProtector>();
        secretProtector
            .Setup(sp => sp.TryUnprotect(It.IsAny<string>(), out It.Ref<string>.IsAny))
            .Callback((string encrypted, ref string decrypted) => { decrypted = SavedHiggsfieldCredential; })
            .Returns(true);

        ModelCapabilityView provider = new()
        {
            Id = Guid.NewGuid(),
            Name = "SavedHiggsfield",
            Kind = InferenceProviderKind.Higgsfield,
            Capability = InferenceProviderCapability.VideoGeneration,
            Endpoint = "https://api.higgsfield.ai",
            ModelName = "kling-video/v3.0/std/text-to-video",
            ApiKeyEncrypted = "encrypted-key",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };
        db.AddFlat(provider);
        await db.SaveChangesAsync();

        InferenceProvidersController controller = Controller(db, videoGenerationClientFactory.Object, secretProtector.Object);

        ActionResult<TestInferenceProviderResponse> result = await controller.TestSaved(db, provider.Id, CancellationToken.None);
        result.Result.Should().BeOfType<OkObjectResult>();
        var response = (TestInferenceProviderResponse)((OkObjectResult)result.Result!).Value!;
        ModelCapabilityView saved = await db.FlatProviders().AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        return (response, saved, handler);
    }

    private const string SavedHiggsfieldCredential = "hf-saved-id:hf-saved-secret";

    [Fact]
    public async Task TestSaved_Higgsfield_probe_is_a_single_GET_and_never_submits()
    {
        // The acceptance criterion: the /test endpoint performs a NO-COST probe for Higgsfield. A
        // probe that submitted would create a task and spend money on an admin's Test click.
        var (response, saved, handler) = await TestSavedThroughRealHiggsfieldClient();


        response.Ok.Should().BeFalse();
        response.Error.Should().Be("Provider rejected authentication.");
        response.Error.Should().NotContain("hf-saved-id");
        response.Error.Should().NotContain("hf-saved-secret");
        saved.LastTestOk.Should().BeFalse();
        saved.LastTestError.Should().Be(response.Error);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].Path.Should().StartWith("/requests/");
        handler.Requests[0].Path.Should().EndWith("/status");
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task TestUnsaved_Higgsfield_probe_reports_auth_rejection_without_exposing_the_credential()
    {
        using InferenceApiDbContext db = CreateContext();
        ProbeHandler handler = new(_ => Json(System.Net.HttpStatusCode.Unauthorized,
            $$"""{"detail":"key {{SavedHiggsfieldCredential}} rejected"}"""));
        HiggsfieldVideoGenerationClient realClient = new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.higgsfield.ai") },
            SavedHiggsfieldCredential);

        var videoGenerationClientFactory = new Mock<IVideoGenerationClientFactory>();
        videoGenerationClientFactory
            .Setup(f => f.Get(It.IsAny<ResolvedVideoGenerationProvider>()))
            .Returns(realClient);

        InferenceProvidersController controller = Controller(db, videoGenerationClientFactory.Object);

        ActionResult<TestInferenceProviderResponse> result = await controller.TestUnsaved(
            new TestProviderRequest(
                Kind: "Higgsfield", Endpoint: "https://api.higgsfield.ai",
                ModelCode: "kling-video/v3.0/std/text-to-video", ApiKey: SavedHiggsfieldCredential,
                Capability: "VideoGeneration"),
            CancellationToken.None);

        var response = (TestInferenceProviderResponse)((OkObjectResult)result.Result!).Value!;
        response.Ok.Should().BeFalse();
        response.Error.Should().Be("Provider rejected authentication.");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }
}
