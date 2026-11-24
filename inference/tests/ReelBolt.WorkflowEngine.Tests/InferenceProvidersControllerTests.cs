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
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Tests for FishAudio/SpeechSynthesis capability validation in InferenceProvidersController.
/// Validates that unsupported kind/capability combinations are rejected at the API boundary.
/// </summary>
public class InferenceProvidersControllerTests
{
    [Fact]
    public void IsUnsupportedCombination_rejects_FishAudio_with_Chat()
    {
        var result = IsUnsupportedCombinationTest(
            InferenceProviderKind.FishAudio,
            InferenceProviderCapability.Chat,
            out var error);

        result.Should().BeTrue();
        error.Should().Contain("FishAudio");
        error.Should().Contain("SpeechSynthesis");
    }

    [Fact]
    public void IsUnsupportedCombination_accepts_FishAudio_with_Transcription()
    {
        // Fish Audio publishes a speech-to-text endpoint — POST /v1/asr, verified against the
        // vendor's own OpenAPI document at https://api.fish.audio/openapi.json (path /v1/asr,
        // operation post, summary "Speech to Text"). A FishAudio row is therefore no longer
        // TTS-only, and this pairing is ACCEPTED — the inverse of what this test pinned before.
        var result = IsUnsupportedCombinationTest(
            InferenceProviderKind.FishAudio,
            InferenceProviderCapability.Transcription,
            out var error);

        result.Should().BeFalse();
        error.Should().BeEmpty();

        // The REAL controller rule agrees — this file's mirror is only as good as that check.
        InferenceProvidersController.IsUnsupportedCombination(
                InferenceProviderKind.FishAudio, InferenceProviderCapability.Transcription, out string realError)
            .Should().BeFalse(realError);
    }

    [Fact]
    public void IsUnsupportedCombination_really_rejects_every_other_FishAudio_capability()
    {
        // The rule is a negative test against the TWO capabilities Fish Audio serves, so a
        // future capability added to the enum must be rejected by default rather than admitted
        // by the fallthrough. Asserted against the REAL controller method, not this file's
        // mirror, because that fallthrough is exactly what this guards.
        foreach (InferenceProviderCapability capability in new[]
                 {
                     InferenceProviderCapability.Chat,
                     InferenceProviderCapability.Vision,
                     InferenceProviderCapability.Decision,
                     InferenceProviderCapability.VideoGeneration,
                     InferenceProviderCapability.Embedding
                 })
        {
            InferenceProvidersController.IsUnsupportedCombination(
                    InferenceProviderKind.FishAudio, capability, out string error)
                .Should().BeTrue($"FishAudio must still reject {capability}");
            error.Should().NotBeEmpty();
        }

        // The message check is scoped to Chat, Vision and Decision: those three are answered by the
        // FishAudio rule itself, whereas VideoGeneration and Embedding are caught by the MiniMax and
        // Embedding allowlists that run above it. The rejection is asserted for all five above,
        // which is the property that matters; pinning the wording of a rule another arm owns would
        // only make this test brittle.
        foreach (InferenceProviderCapability capability in new[]
                 {
                     InferenceProviderCapability.Chat,
                     InferenceProviderCapability.Vision,
                     InferenceProviderCapability.Decision
                 })
        {
            InferenceProvidersController.IsUnsupportedCombination(
                    InferenceProviderKind.FishAudio, capability, out string error)
                .Should().BeTrue();
            error.Should().Contain("SpeechSynthesis").And.Contain("Transcription");
        }
    }

    [Fact]
    public void IsUnsupportedCombination_really_accepts_both_FishAudio_capabilities()
    {
        // Both halves of the kind, through the real rule — the TTS half must not have been
        // broken by lifting the ASR half.
        foreach (InferenceProviderCapability capability in new[]
                 {
                     InferenceProviderCapability.SpeechSynthesis,
                     InferenceProviderCapability.Transcription
                 })
        {
            InferenceProvidersController.IsUnsupportedCombination(
                    InferenceProviderKind.FishAudio, capability, out string error)
                .Should().BeFalse(error);
        }
    }

    [Fact]
    public void IsUnsupportedCombination_rejects_FishAudio_with_Vision()
    {
        var result = IsUnsupportedCombinationTest(
            InferenceProviderKind.FishAudio,
            InferenceProviderCapability.Vision,
            out var error);

        result.Should().BeTrue();
        error.Should().Contain("FishAudio");
    }

    [Fact]
    public void IsUnsupportedCombination_rejects_FishAudio_with_Decision()
    {
        var result = IsUnsupportedCombinationTest(
            InferenceProviderKind.FishAudio,
            InferenceProviderCapability.Decision,
            out var error);

        result.Should().BeTrue();
        error.Should().Contain("FishAudio");
    }

    [Fact]
    public void IsUnsupportedCombination_rejects_AzureOpenAI_with_SpeechSynthesis()
    {
        var result = IsUnsupportedCombinationTest(
            InferenceProviderKind.AzureOpenAI,
            InferenceProviderCapability.SpeechSynthesis,
            out var error);

        result.Should().BeTrue();
        error.Should().Contain("SpeechSynthesis");
        error.Should().Contain("FishAudio");
    }

    [Fact]
    public void IsUnsupportedCombination_accepts_OpenAICompatible_with_SpeechSynthesis()
    {
        // Phase 4 of the voiceover work (docs/voiceover.md "A local OpenAI-compatible TTS server"):
        // the bundled speaches container speaks OpenAI's /v1/audio/speech, so this pairing is the
        // zero-cost local TTS path and is ACCEPTED — the inverse of what this test pinned before.
        var result = IsUnsupportedCombinationTest(
            InferenceProviderKind.OpenAICompatible,
            InferenceProviderCapability.SpeechSynthesis,
            out var error);

        result.Should().BeFalse();
        error.Should().BeEmpty();

        // The REAL controller rule agrees — this file's mirror is only as good as that check.
        InferenceProvidersController.IsUnsupportedCombination(
                InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.SpeechSynthesis, out string realError)
            .Should().BeFalse(realError);
    }

    [Fact]
    public void IsUnsupportedCombination_still_rejects_cloud_chat_kinds_for_SpeechSynthesis()
    {
        foreach (InferenceProviderKind kind in new[] { InferenceProviderKind.AzureOpenAI, InferenceProviderKind.Anthropic, InferenceProviderKind.Gemini, InferenceProviderKind.DeepSeek })
        {
            InferenceProvidersController.IsUnsupportedCombination(kind, InferenceProviderCapability.SpeechSynthesis, out string error)
                .Should().BeTrue($"{kind} has no OpenAI-compatible speech endpoint ReelBolt verified");
            error.Should().Contain("FishAudio").And.Contain("OpenAICompatible");
        }
    }

    [Fact]
    public void IsUnsupportedCombination_accepts_FishAudio_with_SpeechSynthesis()
    {
        var result = IsUnsupportedCombinationTest(
            InferenceProviderKind.FishAudio,
            InferenceProviderCapability.SpeechSynthesis,
            out var error);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsUnsupportedCombination_accepts_AzureOpenAI_with_Chat()
    {
        var result = IsUnsupportedCombinationTest(
            InferenceProviderKind.AzureOpenAI,
            InferenceProviderCapability.Chat,
            out var error);

        result.Should().BeFalse();
    }

    /// <summary>
    /// Mirror of the private IsUnsupportedCombination method in InferenceProvidersController.
    /// This allows us to test the validation logic directly.
    /// </summary>
    private static bool IsUnsupportedCombinationTest(
        InferenceProviderKind kind,
        InferenceProviderCapability capability,
        out string error)
    {
        if (kind == InferenceProviderKind.TypeSafe && capability == InferenceProviderCapability.Chat)
        {
            error = "TypeSafe providers cannot serve the Chat capability.";
            return true;
        }

        if (kind == InferenceProviderKind.TypeSafe && capability == InferenceProviderCapability.Transcription)
        {
            error = "TypeSafe providers cannot serve the Transcription capability.";
            return true;
        }

        if (kind == InferenceProviderKind.TypeSafe && capability == InferenceProviderCapability.Vision)
        {
            error = "TypeSafe providers cannot serve the Vision capability.";
            return true;
        }

        if (kind == InferenceProviderKind.Anthropic && capability == InferenceProviderCapability.Transcription)
        {
            error = "Anthropic providers cannot serve the Transcription capability.";
            return true;
        }

        if (kind == InferenceProviderKind.Gemini && capability == InferenceProviderCapability.Transcription)
        {
            error = "Google Gemini providers cannot serve the Transcription capability.";
            return true;
        }

        if (kind == InferenceProviderKind.DeepSeek && capability == InferenceProviderCapability.Transcription)
        {
            error = "DeepSeek providers cannot serve the Transcription capability.";
            return true;
        }

        if (kind == InferenceProviderKind.Anthropic && capability == InferenceProviderCapability.Decision)
        {
            error = "Anthropic providers cannot serve the Decision capability.";
            return true;
        }

        if (kind == InferenceProviderKind.Gemini && capability == InferenceProviderCapability.Decision)
        {
            error = "Google Gemini providers cannot serve the Decision capability.";
            return true;
        }

        // FishAudio restrictions: can only serve SpeechSynthesis and Transcription
        if (kind == InferenceProviderKind.FishAudio &&
            capability != InferenceProviderCapability.SpeechSynthesis &&
            capability != InferenceProviderCapability.Transcription)
        {
            error = "FishAudio providers can only serve the SpeechSynthesis and Transcription capabilities.";
            return true;
        }

        // SpeechSynthesis restriction: served by FishAudio and OpenAICompatible only
        if (capability == InferenceProviderCapability.SpeechSynthesis &&
            kind != InferenceProviderKind.FishAudio && kind != InferenceProviderKind.OpenAICompatible)
        {
            error = kind + " providers cannot serve the SpeechSynthesis capability. Use a provider of kind 'FishAudio' " +
                    "or 'OpenAICompatible' for text-to-speech.";
            return true;
        }

        error = string.Empty;
        return false;
    }

    // --- Endpoint (SSRF) validation ---

    /// <summary>
    /// The unsaved-config test path used to skip the endpoint gate Create/Update apply, which made
    /// it the one place an admin could aim a provider at the cluster's own network — the metadata
    /// address, another container, the Docker bridge — and have the server make the request.
    /// </summary>
    /// <remarks>
    /// The assertion that matters is the second one: the 400 alone would still pass if the request
    /// had merely been rejected after a client was built. Here the factory is wired to hand back a
    /// real client over a capturing transport, so "no request left the process" is checked against
    /// an actual <see cref="HttpMessageHandler"/> that would have carried it.
    /// </remarks>
    [Fact]
    public async Task TestUnsaved_rejects_a_non_public_endpoint_with_400_and_never_reaches_a_client()
    {
        using InferenceApiDbContext db = CreateContext();
        var chatClientFactory = new Mock<IChatClientFactory>();
        var transcriptionClientFactory = new Mock<ITranscriptionClientFactory>();
        var decisionClientFactory = new Mock<IDecisionClientFactory>();
        var videoGenerationClientFactory = new Mock<IVideoGenerationClientFactory>();
        var speechSynthesisClientFactory = new Mock<ISpeechSynthesisClientFactory>();
        var secretProtector = new Mock<ISecretProtector>();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>();

        var capturingHandler = new CapturingHandler();
        speechSynthesisClientFactory
            .Setup(f => f.Get(It.IsAny<ResolvedSpeechSynthesisProvider>()))
            .Returns(new FishAudioSpeechSynthesisClient(
                new HttpClient(capturingHandler) { BaseAddress = new Uri("http://127.0.0.1:8080/") },
                "fish-audio-model"));

        InferenceProvidersController controller = new(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            chatClientFactory.Object, transcriptionClientFactory.Object, decisionClientFactory.Object,
            videoGenerationClientFactory.Object, speechSynthesisClientFactory.Object,
            secretProtector.Object, logger.Object);

        TestProviderRequest request = new(
            Kind: "AzureOpenAI",   // cloud-only kind: private addresses stay blocked (OpenAICompatible/FishAudio/OpenJev are exempt)
            Endpoint: "http://127.0.0.1:8080",
            ModelCode: "fish-audio-model",
            ApiKey: "test-key",
            Capability: "Chat");

        ActionResult<TestInferenceProviderResponse> result = await controller.TestUnsaved(request, CancellationToken.None);

        // Same 400 shape Create/Update return.
        var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value!.ToString().Should().Contain(
            "Endpoint must be a public https/http URL; internal/private/loopback addresses are not allowed.");

        speechSynthesisClientFactory.Verify(f => f.Get(It.IsAny<ResolvedSpeechSynthesisProvider>()), Times.Never);
        capturingHandler.RequestCount.Should().Be(0);
    }

    /// <summary>
    /// The gate has to run on the endpoint the inheritance block settled on, not on the raw request
    /// field: when a saved id is supplied without a key, the caller sends no endpoint at all and
    /// the saved row's (attacker-chosen, at write time) endpoint is what a client would be pointed
    /// at.
    /// </summary>
    [Fact]
    public async Task TestUnsaved_rejects_a_non_public_endpoint_inherited_from_a_saved_provider()
    {
        using InferenceApiDbContext db = CreateContext();
        DateTime now = DateTime.UtcNow;
        ModelCapabilityView provider = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = Guid.NewGuid(),
            Name = "InternalOpenAi",
            Kind = InferenceProviderKind.AzureOpenAI,
            Capability = InferenceProviderCapability.Chat,
            Endpoint = "http://10.0.0.5:9000",
            ModelName = "fish-audio-model",
            ApiKeyEncrypted = null,
            ApiKeyLastFour = null,
            IsDefault = false,
            IsEnabled = true,
            TimeoutSeconds = null,
            PersonalOAuthMode = false,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.AddFlat(provider);
        await db.SaveChangesAsync();

        var speechSynthesisClientFactory = new Mock<ISpeechSynthesisClientFactory>();

        InferenceProvidersController controller = new(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            new Mock<IChatClientFactory>().Object, new Mock<ITranscriptionClientFactory>().Object,
            new Mock<IDecisionClientFactory>().Object, new Mock<IVideoGenerationClientFactory>().Object,
            speechSynthesisClientFactory.Object, new Mock<ISecretProtector>().Object,
            new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>().Object);

        // No endpoint, no kind, no key: everything is inherited from the saved provider account.
        TestProviderRequest request = new(ProviderId: provider.ProviderId);

        ActionResult<TestInferenceProviderResponse> result = await controller.TestUnsaved(request, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        speechSynthesisClientFactory.Verify(f => f.Get(It.IsAny<ResolvedSpeechSynthesisProvider>()), Times.Never);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://127.255.255.254:8080")]
    [InlineData("http://0.0.0.0:8080")]
    [InlineData("http://10.0.0.5:8080")]
    [InlineData("http://172.16.0.1:8080")]
    [InlineData("http://172.31.255.254:8080")]
    [InlineData("http://192.168.1.1:8080")]
    [InlineData("http://169.254.169.254:8080")]
    [InlineData("http://100.64.0.1:8080")]
    [InlineData("http://100.127.255.254:8080")]
    [InlineData("http://[::1]:8080")]
    [InlineData("http://[::]:8080")]
    [InlineData("http://[fe80::1]:8080")]
    [InlineData("http://[fc00::1]:8080")]
    [InlineData("http://[fd12:3456:789a::1]:8080")]
    [InlineData("http://[::ffff:127.0.0.1]:8080")]
    [InlineData("http://[::ffff:10.0.0.1]:8080")]
    [InlineData("http://[::ffff:192.168.1.1]:8080")]
    [InlineData("http://[::ffff:169.254.169.254]:8080")]
    [InlineData("ftp://8.8.8.8/")]
    [InlineData("not a url")]
    public void IsDisallowedEndpoint_rejects_non_public_literal_addresses(string endpoint)
    {
        // Literal IP hosts throughout, so no test here depends on DNS.
        InferenceProvidersController.IsDisallowedEndpoint(endpoint).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://8.8.8.8/v1")]
    [InlineData("http://1.1.1.1:443/")]
    [InlineData("https://[2606:4700:4700::1111]/v1")]
    [InlineData("http://[2001:4860:4860::8888]:8080/")]
    [InlineData("http://100.63.255.255:8080")]
    [InlineData("http://100.128.0.1:8080")]
    [InlineData("http://172.15.255.255:8080")]
    [InlineData("http://172.32.0.1:8080")]
    public void IsDisallowedEndpoint_allows_public_literal_addresses(string endpoint)
    {
        // The boundary cases above are the neighbours of a blocked range: 100.63/100.128 sit
        // either side of CGNAT's 100.64/10, and 172.15/172.32 either side of 172.16/12.
        InferenceProvidersController.IsDisallowedEndpoint(endpoint).Should().BeFalse();
    }

    /// <summary>
    /// A hostname can resolve to several addresses, and only one of them has to be private for the
    /// name to be unusable — which record a given connect picks is not something the caller
    /// controls, so "most of them are public" is not a property worth trusting.
    /// </summary>
    [Fact]
    public void IsDisallowedAddressSet_rejects_any_set_containing_a_non_public_address()
    {
        InferenceProvidersController.IsDisallowedAddressSet(
            new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.5") })
            .Should().BeTrue("one private record makes the whole name unusable");

        InferenceProvidersController.IsDisallowedAddressSet(
            new[] { IPAddress.Parse("10.0.0.5"), IPAddress.Parse("93.184.216.34") })
            .Should().BeTrue("position in the set must not matter");
    }

    [Fact]
    public void IsDisallowedAddressSet_allows_a_set_of_only_public_addresses()
    {
        InferenceProvidersController.IsDisallowedAddressSet(
            new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("2606:4700:4700::1111") })
            .Should().BeFalse();
    }

    [Fact]
    public void IsDisallowedAddressSet_rejects_an_empty_set()
    {
        InferenceProvidersController.IsDisallowedAddressSet(Array.Empty<IPAddress>())
            .Should().BeTrue("a name that resolved to nothing is a name we cannot vouch for");
    }

    /// <summary>
    /// Counts what would actually leave the process, so a test can assert that nothing did.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    // --- Integration tests for SpeechSynthesis capability ---

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

    /// <summary>
    /// Helper to create a minimal valid WAV stream with non-zero duration.
    /// Creates a 16-bit mono PCM WAV at 16kHz with 0.3 seconds of silence.
    /// </summary>
    private static MemoryStream CreateTestWav()
    {
        const int sampleRateHz = 16_000;
        const short channels = 1;
        const short bitsPerSample = 16;
        const double durationSeconds = 0.3;

        int bytesPerSample = bitsPerSample / 8;
        int sampleCount = (int)(sampleRateHz * durationSeconds);
        int dataSize = sampleCount * channels * bytesPerSample;
        int byteRate = sampleRateHz * channels * bytesPerSample;
        short blockAlign = (short)(channels * bytesPerSample);

        MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            // RIFF header
            writer.Write("RIFF"u8.ToArray());
            writer.Write(36 + dataSize);
            writer.Write("WAVE"u8.ToArray());

            // fmt subchunk (PCM)
            writer.Write("fmt "u8.ToArray());
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write(channels);
            writer.Write(sampleRateHz);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);

            // data subchunk — all-zero samples, i.e. silence
            writer.Write("data"u8.ToArray());
            writer.Write(dataSize);
            writer.Write(new byte[dataSize]);
        }

        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task Create_rejects_FishAudio_with_Chat_with_400()
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
            Name: "FishAudioChat",
            Kind: "FishAudio",
            Endpoint: "https://api.fish.audio",
            ModelName: "fish-audio-model",
            ApiKey: "test-key",
            IsDefault: false,
            IsEnabled: true,
            TimeoutSeconds: null,
            Capability: "Chat"
        );

        ActionResult<ProviderResponse> result = await controller.Create(request, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        var badRequest = (BadRequestObjectResult)result.Result!;
        badRequest.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task Create_rejects_AzureOpenAI_with_SpeechSynthesis_with_400()
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
            Name: "AzureOpenAITTS",
            Kind: "AzureOpenAI",
            Endpoint: "https://example.openai.azure.com",
            ModelName: "gpt-4o-mini",
            ApiKey: "test-key",
            IsDefault: false,
            IsEnabled: true,
            TimeoutSeconds: null,
            Capability: "SpeechSynthesis"
        );

        ActionResult<ProviderResponse> result = await controller.Create(request, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task TestSaved_SpeechSynthesis_calls_RunSpeechSynthesisTestAsync_and_persists_LastTestOk()
    {
        using InferenceApiDbContext db = CreateContext();
        var chatClientFactory = new Mock<IChatClientFactory>();
        var transcriptionClientFactory = new Mock<ITranscriptionClientFactory>();
        var decisionClientFactory = new Mock<IDecisionClientFactory>();
        var videoGenerationClientFactory = new Mock<IVideoGenerationClientFactory>();
        var speechSynthesisClientFactory = new Mock<ISpeechSynthesisClientFactory>();
        var secretProtector = new Mock<ISecretProtector>();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>();

        // Create a saved FishAudio/SpeechSynthesis provider
        DateTime now = DateTime.UtcNow;
        ModelCapabilityView provider = new()
        {
            Id = Guid.NewGuid(),
            Name = "FishAudioTTS",
            Kind = InferenceProviderKind.FishAudio,
            Capability = InferenceProviderCapability.SpeechSynthesis,
            Endpoint = "https://api.fish.audio",
            ModelName = "fish-audio-model",
            ApiKeyEncrypted = null,
            ApiKeyLastFour = null,
            IsDefault = false,
            IsEnabled = true,
            TimeoutSeconds = null,
            PersonalOAuthMode = false,
            CreatedAt = now,
            UpdatedAt = now,
            LastTestAt = null,
            LastTestOk = null,
            LastTestError = null
        };
        db.AddFlat(provider);
        await db.SaveChangesAsync();

        // Mock the speech synthesis client to return a valid WAV
        var mockSpeechSynthesisClient = new Mock<ISpeechSynthesisClient>();
        mockSpeechSynthesisClient.Setup(c => c.SynthesizeAsync(
            It.IsAny<SpeechRequest>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SynthesizedSpeech(CreateTestWav(), "audio/wav"));

        speechSynthesisClientFactory.Setup(f => f.Get(It.IsAny<ResolvedSpeechSynthesisProvider>()))
            .Returns(mockSpeechSynthesisClient.Object);

        secretProtector.Setup(s => s.TryUnprotect(null, out It.Ref<string>.IsAny))
            .Returns((string? encrypted, out string decrypted) =>
            {
                decrypted = string.Empty;
                return true;
            });

        InferenceProvidersController controller = new(
            db, new FakeCurrentUser(Guid.NewGuid(), isAdmin: true),
            chatClientFactory.Object, transcriptionClientFactory.Object, decisionClientFactory.Object,
            videoGenerationClientFactory.Object, speechSynthesisClientFactory.Object,
            secretProtector.Object, logger.Object);

        ActionResult<TestInferenceProviderResponse> result = await controller.TestSaved(db, provider.Id, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        var body = ((OkObjectResult)result.Result!).Value;
        var response = (TestInferenceProviderResponse)body!;
        response.Ok.Should().BeTrue();

        // Verify LastTestOk was persisted
        var refreshedProvider = await db.FlatProviders().FirstOrDefaultAsync(p => p.Id == provider.Id);
        refreshedProvider.Should().NotBeNull();
        refreshedProvider!.LastTestOk.Should().BeTrue();
        refreshedProvider.LastTestAt.Should().NotBeNull();
    }
}
