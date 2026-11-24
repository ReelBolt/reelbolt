using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Inference.Api.Services.Inference;

/// <summary>Outcome of testing a saved provider: either a test result, or a refusal to test at all.</summary>
/// <param name="Result">The test result (already persisted to the row's <c>LastTest*</c> columns).</param>
/// <param name="Rejection">Why the row could not be tested (unsupported kind/capability, disallowed endpoint).</param>
public sealed record ProviderTestOutcome(TestInferenceProviderResponse? Result, string? Rejection)
{
    public static ProviderTestOutcome Tested(TestInferenceProviderResponse result) => new(result, null);
    public static ProviderTestOutcome Rejected(string error) => new(null, error);
}

/// <summary>
/// Runs the per-capability connectivity test for an inference provider: a 1-token chat ping, a silent-WAV
/// transcription, a 1x1-JPEG vision ping, the decision calibration question, a no-cost video-generation
/// query, a short speech synthesis, or a one-word embedding.
/// </summary>
/// <remarks>
/// Shared by <see cref="InferenceProvidersController"/> (the admin page's Test button) and the
/// assistant's admin-gated <c>TestInferenceProvider</c> tool, so both run the same guards and the same
/// probes and persist the same <c>LastTestAt</c>/<c>LastTestOk</c>/<c>LastTestError</c>. It never throws
/// for a provider-side failure: every error is folded into an <c>ok: false</c> result whose
/// <c>error</c> says what went wrong, with the API key scrubbed — the point of a test is to explain a
/// misconfiguration, and "Provider test failed." on its own explained nothing.
/// Authorization is the CALLER's job (the controller and the tool both check admin first).
/// </remarks>
public sealed class InferenceProviderTester
{
    internal const string DisallowedEndpointMessage =
        "Endpoint must be a public https/http URL; internal/private/loopback addresses are not allowed.";

    private const int MaxErrorDetailChars = 400;

    private readonly InferenceApiDbContext _db;
    private readonly ISecretProtector _secretProtector;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly ITranscriptionClientFactory _transcriptionClientFactory;
    private readonly IEmbeddingClientFactory _embeddingClientFactory;
    private readonly IDecisionClientFactory _decisionClientFactory;
    private readonly IVideoGenerationClientFactory _videoGenerationClientFactory;
    private readonly ISpeechSynthesisClientFactory _speechSynthesisClientFactory;
    private readonly IImageClientFactory _imageClientFactory;
    private readonly IImageAnalysisClientFactory _imageAnalysisClientFactory;
    private readonly IAudioGenerationClientFactory _audioGenerationClientFactory;
    private readonly ILogger _logger;

    public InferenceProviderTester(
        InferenceApiDbContext db,
        ISecretProtector secretProtector,
        IChatClientFactory chatClientFactory,
        ITranscriptionClientFactory transcriptionClientFactory,
        IEmbeddingClientFactory embeddingClientFactory,
        IDecisionClientFactory decisionClientFactory,
        IVideoGenerationClientFactory videoGenerationClientFactory,
        ISpeechSynthesisClientFactory speechSynthesisClientFactory,
        ILogger<InferenceProviderTester> logger,
        IImageClientFactory? imageClientFactory = null,
        IAudioGenerationClientFactory? audioGenerationClientFactory = null)
        : this(db, secretProtector, chatClientFactory, transcriptionClientFactory, embeddingClientFactory,
            decisionClientFactory, videoGenerationClientFactory, speechSynthesisClientFactory, (ILogger)logger, imageClientFactory,
            audioGenerationClientFactory: audioGenerationClientFactory)
    {
    }

    internal InferenceProviderTester(
        InferenceApiDbContext db,
        ISecretProtector secretProtector,
        IChatClientFactory chatClientFactory,
        ITranscriptionClientFactory transcriptionClientFactory,
        IEmbeddingClientFactory embeddingClientFactory,
        IDecisionClientFactory decisionClientFactory,
        IVideoGenerationClientFactory videoGenerationClientFactory,
        ISpeechSynthesisClientFactory speechSynthesisClientFactory,
        ILogger logger,
        // Optional and LAST so the handful of existing test call sites that build a tester directly
        // keep compiling; a missing factory simply means no image probes, which is what a test that
        // only exercises chat/decision paths wants anyway.
        IImageClientFactory? imageClientFactory = null,
        IImageAnalysisClientFactory? imageAnalysisClientFactory = null,
        IAudioGenerationClientFactory? audioGenerationClientFactory = null)
    {
        _db = db;
        _secretProtector = secretProtector;
        _chatClientFactory = chatClientFactory;
        _transcriptionClientFactory = transcriptionClientFactory;
        _embeddingClientFactory = embeddingClientFactory;
        _decisionClientFactory = decisionClientFactory;
        _videoGenerationClientFactory = videoGenerationClientFactory;
        _speechSynthesisClientFactory = speechSynthesisClientFactory;
        _imageClientFactory = imageClientFactory ?? new ImageClientFactory();
        _imageAnalysisClientFactory = imageAnalysisClientFactory ?? new ImageAnalysisClientFactory();
        _audioGenerationClientFactory = audioGenerationClientFactory ?? new AudioGenerationClientFactory();
        _logger = logger;
    }

    /// <summary>
    /// Tests a SAVED model capability with its provider's stored, decrypted key and persists the result
    /// onto the capability. <paramref name="capability"/> must have its <c>Model</c> and
    /// <c>Model.Provider</c> loaded (and tracked, so the result is saved).
    /// </summary>
    public async Task<ProviderTestOutcome> TestSavedAsync(ModelCapability capability, CancellationToken ct)
    {
        ModelCapabilityView entity = ModelCapabilityView.From(capability);

        // The same guard Create/Update/TestUnsaved apply. Without it, an unsupported row — only
        // reachable by a direct database write, which is exactly the case the factory's defensive
        // arm exists for — reaches the factory, whose explanatory NotSupportedException is then
        // swallowed by the catch-all below and reported as the generic "Provider test failed.".
        // That defeats the purpose of the one endpoint whose job is to explain misconfiguration.
        if (InferenceProvidersController.IsUnsupportedCombination(entity.Kind, entity.Capability, entity.Endpoint, out string unsupportedSaved))
        {
            return ProviderTestOutcome.Rejected(unsupportedSaved);
        }

        // The same endpoint gate Create/Update/TestUnsaved apply, judged against the STORED row's
        // own kind. TestSaved makes an outbound request, so without this a row left at a private
        // address by a direct database write — exactly the case this endpoint exists to diagnose —
        // is an outbound-I/O primitive rather than a diagnostic, for any caller holding an admin
        // token. Placed before any client is constructed, so a rejected endpoint never reaches a
        // factory at all. Deliberately the kind-aware overload, so a legitimate self-hosted OpenJev
        // row (loopback, a docker service name, host.docker.internal) still tests; the tradeoff is
        // that a legacy private OpenAICompatible row can no longer be tested from the admin UI,
        // which is the same posture TestUnsaved already takes.
        // An ORGANIZATION's row never gets the self-hosted-kind private-address exemption (a tenant aiming
        // a provider at internal addresses is SSRF, not self-hosting) — see InferenceProviderAdminService.
        if (entity.Kind == InferenceProviderKind.LocalRunner)
        {
            // The endpoint is a paired device id, not a URL: judged against the row's OWN organization, so a device
            // that was removed (or a row somehow pointing at another workspace's device) is refused before any relay call.
            if (entity.OrganizationId is not { } ownerOrg)
                return ProviderTestOutcome.Rejected(LocalRunnerProviderRules.PlatformScopeMessage);
            if (await LocalRunnerProviderRules.DeviceProblemAsync(_db, ownerOrg, entity.Endpoint, ct) is { } deviceProblem)
                return ProviderTestOutcome.Rejected(deviceProblem);
        }
        else if (InferenceProviderAdminService.IsEndpointDisallowed(
                entity.OrganizationId is { } orgId ? ProviderScope.Org(orgId) : ProviderScope.Platform,
                entity.Endpoint, entity.Kind))
        {
            return ProviderTestOutcome.Rejected(DisallowedEndpointMessage);
        }

        string apiKey = string.Empty;
        if (!string.IsNullOrEmpty(entity.ApiKeyEncrypted))
        {
            if (!_secretProtector.TryUnprotect(entity.ApiKeyEncrypted, out apiKey))
            {
                // R3 degradation path: a lost/mismatched Data Protection key ring must surface
                // as a clean, readable failure — never an unhandled exception or a 500.
                TestInferenceProviderResponse degraded = new(false, 0, "stored key could not be decrypted", null);
                await PersistTestResultAsync(capability, degraded, ct);
                return ProviderTestOutcome.Tested(degraded);
            }
        }

        TestInferenceProviderResponse result = await RunAsync(
            entity.Id, entity.Name, entity.Kind, entity.Capability, entity.Endpoint, entity.ModelName, apiKey,
            entity.TimeoutSeconds ?? DefaultTimeoutSeconds, entity.PersonalOAuthMode, ct,
            organizationId: entity.OrganizationId);

        await PersistTestResultAsync(capability, result, ct);
        return ProviderTestOutcome.Tested(result);
    }

    /// <summary>
    /// whether an oracle-priced capability's price oracle (<see cref="IVideoGenerationPriceOracle"/>, Higgsfield)
    /// answers a representative quote right now (5 seconds, 16:9). That is the only way such a capability may be enabled
    /// without a price row. Never throws for a provider-side failure (false, logged with the key scrubbed); persists
    /// nothing. <paramref name="capability"/> must have its <c>Model</c> and <c>Model.Provider</c> loaded.
    /// </summary>
    public async Task<bool> PriceOracleAnswersAsync(ModelCapability capability, CancellationToken ct)
    {
        ModelCapabilityView entity = ModelCapabilityView.From(capability);
        if (entity.Capability != InferenceProviderCapability.VideoGeneration) return false;

        string apiKey = string.Empty;
        if (!string.IsNullOrEmpty(entity.ApiKeyEncrypted) && !_secretProtector.TryUnprotect(entity.ApiKeyEncrypted, out apiKey))
            return false;

        try
        {
            IVideoGenerationClient client = _videoGenerationClientFactory.Get(new ResolvedVideoGenerationProvider(
                entity.Id, entity.Name, entity.Kind, entity.Endpoint, entity.ModelName, apiKey,
                entity.TimeoutSeconds ?? DefaultTimeoutSeconds, entity.OrganizationId, entity.OrganizationId is null,
                entity.ProviderId, entity.ModelId));
            if (client is not IVideoGenerationPriceOracle oracle) return false;

            VideoGenerationPriceQuote quote = await oracle.QuoteAsync(
                new VideoGenerationPriceQuery(entity.ModelName, "720p", "16:9", 5), ct);
            return quote.TotalUsd > 0m;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning("The price oracle of {Kind} model {Model} did not answer: {Error}",
                entity.Kind, entity.ModelName, Failure(ex, apiKey));
            return false;
        }
    }

    /// <summary>Dispatches to the capability's probe. Never persists anything.</summary>
    public async Task<TestInferenceProviderResponse> RunAsync(
        Guid? id,
        string name,
        InferenceProviderKind kind,
        InferenceProviderCapability capability,
        string endpoint,
        string modelName,
        string apiKey,
        int timeoutSeconds,
        bool personalOAuthMode,
        CancellationToken ct,
        Guid? organizationId = null) =>
        capability switch
        {
            InferenceProviderCapability.Transcription => await RunTranscriptionTestAsync(
                new ResolvedTranscriptionProvider(id, name, kind, endpoint, modelName, apiKey, timeoutSeconds,
                    IsPlatformManaged: organizationId is null), ct),
            InferenceProviderCapability.Vision => await RunVisionTestAsync(
                ChatProvider(id, name, kind, endpoint, modelName, apiKey, timeoutSeconds, personalOAuthMode, organizationId, capability), ct),
            InferenceProviderCapability.Decision => await RunDecisionTestAsync(
                new ResolvedDecisionProvider(id, name, kind, endpoint, modelName, apiKey, timeoutSeconds,
                    IsPlatformManaged: organizationId is null), ct),
            InferenceProviderCapability.VideoGeneration => await RunVideoGenerationTestAsync(
                new ResolvedVideoGenerationProvider(id, name, kind, endpoint, modelName, apiKey, timeoutSeconds,
                    IsPlatformManaged: organizationId is null), ct),
            InferenceProviderCapability.Embedding => await RunEmbeddingTestAsync(
                new ResolvedEmbeddingProvider(id, name, kind, endpoint, modelName, apiKey, timeoutSeconds,
                    OrganizationId: organizationId, IsPlatformManaged: organizationId is null), ct),
            InferenceProviderCapability.SpeechSynthesis => await RunSpeechSynthesisTestAsync(
                new ResolvedInferenceProvider(id, name, kind, endpoint, modelName, apiKey, timeoutSeconds, personalOAuthMode,
                    IsPlatformManaged: organizationId is null), ct),
            // Generation, the instruction edit and the six per-job editor capabilities all probe through
            // IImageClient.ProbeAsync, which never generates; the capability rides on the resolved row so a
            // fal.ai probe can also check the model serves THAT job.
            _ when ImageJobCapabilities.IsImageJob(capability) => await RunImageTestAsync(
                new ResolvedImageProvider(id, name, kind, capability, endpoint, modelName, apiKey, timeoutSeconds,
                    IsPlatformManaged: organizationId is null), ct),
            InferenceProviderCapability.Segmentation => await RunImageAnalysisTestAsync(
                new ResolvedImageAnalysisProvider(id, name, kind, InferenceProviderCapability.Segmentation, endpoint, modelName, apiKey, timeoutSeconds,
                    IsPlatformManaged: organizationId is null), ct),
            InferenceProviderCapability.Depth => await RunImageAnalysisTestAsync(
                new ResolvedImageAnalysisProvider(id, name, kind, InferenceProviderCapability.Depth, endpoint, modelName, apiKey, timeoutSeconds,
                    IsPlatformManaged: organizationId is null), ct),
            // Video segmentation has NO no-cost probe. fal's empty-body probe trick works for the image
            // endpoints because their schema marks image_url required and a missing field is refused before
            // dispatch; this endpoint would need a real video_url, and submitting one is a paid call. Falling
            // through to the chat ping (the default arm) would send a chat request to the queue host and
            // report a failure that says nothing about this row, so the refusal is explicit.
            InferenceProviderCapability.VideoSegmentation => new TestInferenceProviderResponse(
                Ok: false,
                LatencyMs: 0,
                Error: "Provider test failed: VideoSegmentation has no no-cost connectivity probe — a test would " +
                       "have to submit a video and be billed. Save the row and let the analyze step's first call " +
                       "verify it; a missing key or an unrouted model surfaces there as SEGMENTATION_PROVIDER_ERROR.",
                ResponsePreview: null),
            // Music and sound effects probe through IAudioGenerationClient.ProbeAsync, which never generates: fal
            // reads the status of a random request id, ElevenLabs lists its models.
            InferenceProviderCapability.MusicGeneration or InferenceProviderCapability.SoundEffectGeneration =>
                await RunAudioGenerationTestAsync(
                    new ResolvedAudioGenerationProvider(id, name, kind, capability, endpoint, modelName, apiKey, timeoutSeconds,
                    IsPlatformManaged: organizationId is null), ct),
            // Audio isolation is configured-only: a real test would upload audio and be billed per minute, and the
            // chat ping (the default arm) would hit a queue route that does not serve it. The only free check is
            // that the row's model is one this platform has a read schema and a price for.
            InferenceProviderCapability.AudioIsolation => AudioIsolationConfigurationTest(kind, modelName),
            // Hosted video enhancement is configured-only: a real test would upload a clip and be billed per output
            // (or compute) second, and the chat ping would hit a queue route that does not serve it. The only free
            // check is that the row's model is one this platform has a read schema and a price for.
            InferenceProviderCapability.VideoUpscale or InferenceProviderCapability.FrameInterpolation
                or InferenceProviderCapability.VideoOutpaint =>
                VideoEnhanceConfigurationTest(kind, capability, modelName),
            // Lip-sync is configured-only: a real test would upload a face and a speech clip and be billed per output
            // second; the chat ping (the default arm) would hit a queue route that does not serve it.
            InferenceProviderCapability.LipSync => LipSyncConfigurationTest(kind, modelName),
            // Moderation has NO no-cost probe. A real test would have to classify some content, and
            // sending a chat ping to /moderations (the default arm) would report a failure that says
            // nothing about this row. Say what is true instead: the row is configured and the first
            // piece of real content exercises it.
            InferenceProviderCapability.ContentModeration => new TestInferenceProviderResponse(
                Ok: true,
                LatencyMs: 0,
                Error: null,
                ResponsePreview: "ContentModeration is configured; it is exercised on real content."),
            // Rerank is configured-only, like moderation: a /rerank ping against a hosted reranker is a
            // billed search that ships text to the vendor, and the chat ping (the default arm) would hit
            // a route this row does not serve. The first real documentation search exercises it.
            InferenceProviderCapability.Rerank => new TestInferenceProviderResponse(
                Ok: true,
                LatencyMs: 0,
                Error: null,
                ResponsePreview: "Rerank is configured; it is exercised on the first documentation search."),
            // WebSearch is configured-only: a test search is billed per call and ships a query to the vendor, and
            // the chat ping (the default arm) would hit a route this row does not serve.
            InferenceProviderCapability.WebSearch => new TestInferenceProviderResponse(
                Ok: true,
                LatencyMs: 0,
                Error: null,
                ResponsePreview: "WebSearch is configured; it is exercised on the first agent search."),
            _ => await RunTestAsync(
                ChatProvider(id, name, kind, endpoint, modelName, apiKey, timeoutSeconds, personalOAuthMode, organizationId, capability), ct)
        };

    /// <summary>
    /// The resolved row a chat or vision probe runs against. For a LocalRunner row only, the owning organization rides along: a LocalRunner
    /// client is addressed by organization and device, so it cannot be built without it. Any organization's row is marked not platform-managed,
    /// which is what gives its probe the public-only transport.
    /// </summary>
    internal static ResolvedInferenceProvider ChatProvider(
        Guid? id, string name, InferenceProviderKind kind, string endpoint, string modelName, string apiKey,
        int timeoutSeconds, bool personalOAuthMode, Guid? organizationId, InferenceProviderCapability capability) =>
        new(id, name, kind, endpoint, modelName, apiKey, timeoutSeconds, personalOAuthMode,
            OrganizationId: kind == InferenceProviderKind.LocalRunner ? organizationId : null,
            // An org's row is never platform-managed: its test dials public addresses only (ProviderRowTransport).
            IsPlatformManaged: organizationId is null,
            Capability: capability == InferenceProviderCapability.Vision ? capability : InferenceProviderCapability.Chat);

    /// <summary>
    /// "Provider test failed: {exception type}: {message}", bounded and with the API key scrubbed, so an
    /// admin (or the assistant reading <c>LastTestError</c>) can tell a refused connection from a 404
    /// model from a TLS error instead of seeing one generic sentence for all of them.
    /// </summary>
    internal static string Failure(Exception ex, string? apiKey)
    {
        Exception root = ex;
        while (root.InnerException is not null && (root is AggregateException || root.Message.Length == 0))
            root = root.InnerException;

        string message = root.Message;
        if (!string.IsNullOrEmpty(apiKey) && apiKey.Length >= 4)
            message = message.Replace(apiKey, "***", StringComparison.Ordinal);
        message = message.ReplaceLineEndings(" ").Trim();
        if (message.Length > MaxErrorDetailChars)
            message = message[..MaxErrorDetailChars] + "…";

        return message.Length == 0
            ? $"Provider test failed: {root.GetType().Name}."
            : $"Provider test failed: {root.GetType().Name}: {message}";
    }

    /// <summary>Timeout applied when a provider row sets none.</summary>
    public const int DefaultTimeoutSeconds = 300;

    private async Task<TestInferenceProviderResponse> RunTestAsync(ResolvedInferenceProvider provider, CancellationToken ct)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            IChatClient chatClient = _chatClientFactory.Get(provider);
            List<ChatMessage> messages = new() { new ChatMessage(ChatRole.User, "ping") };
            // Reasoning models (e.g. Qwen3 with thinking mode on) spend output tokens on a
            // hidden reasoning chain before emitting any visible content — a too-tight budget
            // gets exhausted mid-thought and comes back with content: null, which used to read
            // as success here (no emptiness check) even though nothing useful was returned.
            ChatOptions options = new() { MaxOutputTokens = PingMaxOutputTokens(provider.Kind) };

            ChatResponse response = await chatClient.GetResponseAsync(messages, options, ct);
            stopwatch.Stop();

            string? preview = response.Text;
            if (!string.IsNullOrEmpty(preview) && preview.Length > 200)
            {
                preview = preview[..200];
            }

            // A one-token reply from a local reasoning model can legitimately be blank: the relay answering at all is the proof.
            bool ok = !string.IsNullOrWhiteSpace(preview) || provider.Kind == InferenceProviderKind.LocalRunner;
            return new TestInferenceProviderResponse(ok, stopwatch.ElapsedMilliseconds, ok ? null : "Provider returned an empty response.", preview);
        }
        catch (Exception ex)
        {
            // Never let a bad endpoint/model/key escape as a 500 — the whole point of this
            // endpoint is to let an admin validate a config before (or after) saving it.
            stopwatch.Stop();
            _logger.LogError(ex, "Inference provider test failed");
            // A local model that cannot be reached already carries a plain-language reason.
            string error = ex is LocalRunnerUnavailableException offline ? offline.Message : Failure(ex, provider.ApiKey);
            return new TestInferenceProviderResponse(false, stopwatch.ElapsedMilliseconds, error, null);
        }
    }

    /// <summary>
    /// Transcribes a short, in-memory synthesized silent WAV as a 1-shot connectivity ping for a
    /// Transcription-capability provider — mirrors <see cref="RunTestAsync"/>'s chat-side "ping"
    /// message. Generated in pure C# (no ffmpeg/fixture file) since this API service has no
    /// video-processing dependency and shouldn't gain one just for a health-check.
    /// </summary>
    private async Task<TestInferenceProviderResponse> RunTranscriptionTestAsync(ResolvedTranscriptionProvider provider, CancellationToken ct)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            ITranscriptionClient client = _transcriptionClientFactory.Get(provider);
            await using MemoryStream wav = BuildSilentWav();

            TranscriptResult result = await client.TranscribeAsync(wav, "ping.wav", language: null, wordTimestamps: false, ct);
            stopwatch.Stop();

            string? preview = result.Text;
            if (!string.IsNullOrEmpty(preview) && preview.Length > 200)
            {
                preview = preview[..200];
            }

            return new TestInferenceProviderResponse(true, stopwatch.ElapsedMilliseconds, null, preview);
        }
        catch (Exception ex)
        {
            // Same discipline as RunTestAsync: never let a bad endpoint/model/key escape as a 500.
            stopwatch.Stop();
            _logger.LogError(ex, "Transcription provider test failed");
            return new TestInferenceProviderResponse(false, stopwatch.ElapsedMilliseconds, Failure(ex, provider.ApiKey), null);
        }
    }

    /// <summary>
    /// Embeds the string "ping" through an Embedding-capability provider as a connectivity check and
    /// requires a non-empty vector back. The preview reports the vector length (dimension), which is
    /// what the vector index keys its collections on.
    /// </summary>
    private async Task<TestInferenceProviderResponse> RunEmbeddingTestAsync(ResolvedEmbeddingProvider provider, CancellationToken ct)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>> client =
                _embeddingClientFactory.Get(provider);
            Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>> result =
                await client.GenerateAsync(["ping"], cancellationToken: ct);
            stopwatch.Stop();

            int dimension = result.Count == 1 ? result[0].Vector.Length : 0;
            if (dimension == 0)
            {
                return new TestInferenceProviderResponse(false, stopwatch.ElapsedMilliseconds,
                    "Provider returned no embedding vector.", null);
            }

            return new TestInferenceProviderResponse(true, stopwatch.ElapsedMilliseconds, null, $"vector length {dimension}");
        }
        catch (LocalRunnerUnavailableException offline)
        {
            stopwatch.Stop();
            return new TestInferenceProviderResponse(false, stopwatch.ElapsedMilliseconds, offline.Message, null);
        }
        catch (NotSupportedException ex)
        {
            stopwatch.Stop();
            return new TestInferenceProviderResponse(false, stopwatch.ElapsedMilliseconds, ex.Message, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Embedding provider test failed");
            return new TestInferenceProviderResponse(false, stopwatch.ElapsedMilliseconds, Failure(ex, provider.ApiKey), null);
        }
    }

    /// <summary>
    /// Sends <see cref="TinyTestJpeg"/> through a Vision-capability provider as a 1-shot
    /// connectivity ping — mirrors <see cref="RunTestAsync"/>'s chat-side "ping" message and
    /// <see cref="RunTranscriptionTestAsync"/>'s synthesized-audio ping. Reuses
    /// <see cref="IChatClientFactory"/> unchanged (a vision call is just a chat call with an
    /// image content part — see <c>IInferenceProviderResolver.ResolveVisionAsync</c>), so no new
    /// client factory is needed for this test path either.
    /// </summary>
    private async Task<TestInferenceProviderResponse> RunVisionTestAsync(ResolvedInferenceProvider provider, CancellationToken ct)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            IChatClient chatClient = _chatClientFactory.Get(provider);
            ChatMessage message = new(
                ChatRole.User,
                new List<AIContent>
                {
                    new TextContent("Reply with only the word ok."),
                    new DataContent(TinyTestJpeg, "image/jpeg")
                });
            // See the matching comment in RunTestAsync — a reasoning model needs headroom beyond
            // its hidden chain-of-thought before content ever appears.
            ChatOptions options = new() { MaxOutputTokens = PingMaxOutputTokens(provider.Kind) };

            ChatResponse response = await chatClient.GetResponseAsync(new[] { message }, options, ct);
            stopwatch.Stop();

            string? preview = response.Text;
            if (!string.IsNullOrEmpty(preview) && preview.Length > 200)
            {
                preview = preview[..200];
            }

            bool ok = !string.IsNullOrWhiteSpace(preview) || provider.Kind == InferenceProviderKind.LocalRunner;
            return new TestInferenceProviderResponse(ok, stopwatch.ElapsedMilliseconds, ok ? null : "Provider returned an empty response.", preview);
        }
        catch (Exception ex)
        {
            // Same discipline as RunTestAsync/RunTranscriptionTestAsync: never let a bad
            // endpoint/model/key/non-vision-capable-model escape as a 500.
            stopwatch.Stop();
            _logger.LogError(ex, "Vision provider test failed");
            string error = ex is LocalRunnerUnavailableException offline ? offline.Message : Failure(ex, provider.ApiKey);
            return new TestInferenceProviderResponse(false, stopwatch.ElapsedMilliseconds, error, null);
        }
    }

    /// <summary>
    /// Sends a calibration question "Is 2 greater than 1?" through a Decision-capability provider
    /// as a 1-shot connectivity ping — mirrors <see cref="RunTestAsync"/>'s chat-side "ping"
    /// message, <see cref="RunTranscriptionTestAsync"/>'s synthesized-audio ping, and
    /// <see cref="RunVisionTestAsync"/>'s test-image ping.
    /// </summary>
    private async Task<TestInferenceProviderResponse> RunDecisionTestAsync(ResolvedDecisionProvider provider, CancellationToken ct)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            IDecisionClient decisionClient = _decisionClientFactory.Get(provider);
            DecisionState state = new(string.Empty);
            IReadOnlyList<DecisionQuestion> questions = new List<DecisionQuestion>
            {
                new("Noul", DecisionKind.Noul, "Is 2 greater than 1?", new List<string>())
            };

            DecisionResult result = await decisionClient.DecideAsync(state, questions, ct);
            stopwatch.Stop();

            if (result.Answers.TryGetValue("Noul", out DecisionAnswer? answer) && answer.Noul.HasValue && answer.Noul > 0.9)
            {
                return new TestInferenceProviderResponse(true, stopwatch.ElapsedMilliseconds, null, null);
            }

            string actualValue = result.Answers.TryGetValue("Noul", out var a) && a.Noul.HasValue
                ? a.Noul.Value.ToString("F2")
                : "missing";
            return new TestInferenceProviderResponse(false, stopwatch.ElapsedMilliseconds,
                $"Calibration question did not return high-confidence answer (Noul: {actualValue}, expected > 0.9).", null);
        }
        catch (Exception ex)
        {
            // Same discipline as RunTestAsync/RunTranscriptionTestAsync/RunVisionTestAsync: never
            // let a bad endpoint/model/key escape as a 500.
            stopwatch.Stop();
            _logger.LogError(ex, "Decision provider test failed");
            return new TestInferenceProviderResponse(false, stopwatch.ElapsedMilliseconds, Failure(ex, provider.ApiKey), null);
        }
    }

    /// <summary>
    /// Tests a VideoGeneration-capability provider through <see cref="IVideoGenerationClient.ProbeAsync"/>,
    /// a no-cost query of a random, nonexistent task id that never calls
    /// <see cref="IVideoGenerationClient.SubmitAsync"/>.
    /// Rule: <c>ok: true</c> ONLY when the probe reports a vendor response that proves the endpoint is the
    /// vendor's query API and the key was accepted. An auth rejection, a network error, a timeout, a 5xx,
    /// a 408/429, a non-JSON or unrecognised body, and any unexpected exception are all <c>ok: false</c>
    /// with a reason in <c>error</c> (persisted to <c>LastTestError</c> on the saved-provider path).
    /// </summary>
    private async Task<TestInferenceProviderResponse> RunVideoGenerationTestAsync(ResolvedVideoGenerationProvider provider, CancellationToken ct)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            IVideoGenerationClient videoGenerationClient = _videoGenerationClientFactory.Get(provider);
            VideoGenerationProbeResult probe = await videoGenerationClient.ProbeAsync(ct);
            stopwatch.Stop();

            if (probe.Ok)
            {
                return new TestInferenceProviderResponse(true, stopwatch.ElapsedMilliseconds, null, probe.Reason);
            }

            if (probe.AuthRejected)
            {
                return new TestInferenceProviderResponse(false, stopwatch.ElapsedMilliseconds, "Provider rejected authentication.", probe.Reason);
            }

            string reason = string.IsNullOrWhiteSpace(probe.Reason) ? "no response detail" : probe.Reason;
            return new TestInferenceProviderResponse(false, stopwatch.ElapsedMilliseconds, $"Provider test failed: {reason}", null);
        }
        catch (Exception ex)
        {
            // Never a success: an exception here means nothing proved the endpoint and key work.
            stopwatch.Stop();
            _logger.LogError(ex, "VideoGeneration provider test encountered an error");
            // Deliberately generic, unlike the other probes: the video probe already turns every
            // vendor/network outcome into a reason above, so an exception here is a bug in our own
            // code, and its message describes our internals rather than the provider.
            return new TestInferenceProviderResponse(false, stopwatch.ElapsedMilliseconds, "Provider test failed.", null);
        }
    }

    private static TestInferenceProviderResponse AudioIsolationConfigurationTest(InferenceProviderKind kind, string? modelName)
    {
        if (AudioIsolationModelCatalog.Instance.Resolve(modelName) is null || kind != InferenceProviderKind.FalAi)
        {
            return new TestInferenceProviderResponse(false, 0,
                "Provider test failed: '" + modelName + "' is not an audio-isolation model this platform has a read schema " +
                "and a price for. Known: " + AudioIsolationModelCatalog.Instance.Ids + ".", null);
        }

        return new TestInferenceProviderResponse(true, 0, null,
            "AudioIsolation is configured; it is exercised on real audio (a test would be billed per minute).");
    }

    private static TestInferenceProviderResponse LipSyncConfigurationTest(InferenceProviderKind kind, string? modelName)
    {
        if (kind != InferenceProviderKind.FalAi
            || FalAiVideoGenerationClient.Catalog.Find(modelName?.Trim())?.Job != FalVideoJob.TalkingHead)
        {
            return new TestInferenceProviderResponse(false, 0,
                "Provider test failed: '" + modelName + "' is not a talking-head model this platform has a read schema for. " +
                "Known: " + FalAiVideoGenerationClient.Catalog.IdsFor(FalVideoJob.TalkingHead) + ".", null);
        }

        return new TestInferenceProviderResponse(true, 0, null,
            "LipSync is configured; it is exercised on a real portrait and speech clip (a test would be billed per second).");
    }

    private static TestInferenceProviderResponse VideoEnhanceConfigurationTest(
        InferenceProviderKind kind, InferenceProviderCapability capability, string? modelName)
    {
        if (VideoEnhanceModelCatalog.Instance.Resolve(capability, modelName) is null || kind != InferenceProviderKind.FalAi)
        {
            return new TestInferenceProviderResponse(false, 0,
                "Provider test failed: '" + modelName + "' is not a " + capability + " model this platform has a read schema " +
                "and a price for. Known: " + VideoEnhanceModelCatalog.Instance.IdsFor(capability) + ".", null);
        }

        return new TestInferenceProviderResponse(true, 0, null,
            capability + " is configured; it is exercised on a real clip (a test would be billed per second).");
    }

    /// <summary>
    /// Tests a MusicGeneration- or SoundEffectGeneration-capability provider through
    /// <see cref="IAudioGenerationClient.ProbeAsync"/>, which creates nothing billable. A client that cannot be built
    /// (a blank key throws rather than reading the environment) is a failed test with its reason, never a 500.
    /// </summary>
    private async Task<TestInferenceProviderResponse> RunAudioGenerationTestAsync(
        ResolvedAudioGenerationProvider provider, CancellationToken ct)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            IAudioGenerationClient client = _audioGenerationClientFactory.Get(provider);
            AudioGenerationProbeResult probe = await client.ProbeAsync(ct);
            stopwatch.Stop();

            if (probe.Ok) return new TestInferenceProviderResponse(true, stopwatch.ElapsedMilliseconds, null, probe.Reason);
            if (probe.AuthRejected)
            {
                return new TestInferenceProviderResponse(false, stopwatch.ElapsedMilliseconds, "Provider rejected authentication.", probe.Reason);
            }

            string reason = string.IsNullOrWhiteSpace(probe.Reason) ? "no response detail" : probe.Reason;
            return new TestInferenceProviderResponse(false, stopwatch.ElapsedMilliseconds, $"Provider test failed: {reason}", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger.LogWarning("{Capability} provider test failed: {Error}", provider.Capability, Failure(ex, provider.ApiKey));
            return new TestInferenceProviderResponse(false, stopwatch.ElapsedMilliseconds, Failure(ex, provider.ApiKey), null);
        }
    }

    /// <summary>
    /// Tests a Segmentation- or Depth-capability provider through
    /// <see cref="IImageAnalysisClient.ProbeAsync"/>. The probe submits the row's OWN model id with an
    /// empty body, so it cannot reach a runner and cannot be billed — see the client for what its
    /// answer does and does not prove.
    /// </summary>
    private async Task<TestInferenceProviderResponse> RunImageAnalysisTestAsync(
        ResolvedImageAnalysisProvider provider, CancellationToken ct)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            IImageAnalysisClient client = _imageAnalysisClientFactory.Get(provider);
            ImageAnalysisProbeResult probe = await client.ProbeAsync(ct);
            stopwatch.Stop();

            if (probe.IsOk)
            {
                return new TestInferenceProviderResponse(true, stopwatch.ElapsedMilliseconds, null,
                    $"{provider.Kind} {provider.Capability} endpoint answered; no image was produced by this test.");
            }

            string reason = string.IsNullOrWhiteSpace(probe.Reason) ? "no response detail" : probe.Reason;
            return new TestInferenceProviderResponse(false, stopwatch.ElapsedMilliseconds, $"Provider test failed: {reason}", null);
        }
        catch (Exception ex)
        {
            // Never a success: an exception here means nothing proved the endpoint and key work.
            stopwatch.Stop();
            _logger.LogError(ex, "Image analysis provider test encountered an error");
            return new TestInferenceProviderResponse(false, stopwatch.ElapsedMilliseconds, Failure(ex, provider.ApiKey), null);
        }
    }

    /// <summary>
    /// Tests an ImageGeneration- or ImageEdit-capability provider through
    /// <see cref="IImageClient.ProbeAsync"/>.
    /// <para>
    /// The probe is designed so it CANNOT generate: each vendor implementation submits a request the
    /// vendor is documented to reject on validation (an unknown model name, an empty body), so no
    /// image can be produced and nothing can be billed. What that answer proves is narrower than the
    /// video probe's and is stated rather than implied — see each client's own <c>ProbeAsync</c>
    /// remarks. Rule: <c>ok: true</c> ONLY when the probe reports a response that establishes both
    /// that the endpoint is the vendor's image API and that the credential was accepted.
    /// </para>
    /// </summary>
    private async Task<TestInferenceProviderResponse> RunImageTestAsync(ResolvedImageProvider provider, CancellationToken ct)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            IImageClient client = _imageClientFactory.Get(provider);
            ImageProbeResult probe = await client.ProbeAsync(ct);
            stopwatch.Stop();

            if (probe.IsOk)
            {
                return new TestInferenceProviderResponse(true, stopwatch.ElapsedMilliseconds, null,
                    $"{provider.Kind} {provider.Capability} endpoint answered; no image was generated by this test.");
            }

            string reason = string.IsNullOrWhiteSpace(probe.Reason) ? "no response detail" : probe.Reason;
            return new TestInferenceProviderResponse(false, stopwatch.ElapsedMilliseconds, $"Provider test failed: {reason}", null);
        }
        catch (Exception ex)
        {
            // Never a success: an exception here means nothing proved the endpoint and key work.
            stopwatch.Stop();
            _logger.LogError(ex, "Image provider test encountered an error");
            return new TestInferenceProviderResponse(false, stopwatch.ElapsedMilliseconds, Failure(ex, provider.ApiKey), null);
        }
    }

    /// <summary>
    /// Synthesizes a short text "ok" through a SpeechSynthesis-capability provider as a 1-shot
    /// connectivity ping — mirrors <see cref="RunTestAsync"/>'s chat-side "ping" message,
    /// <see cref="RunTranscriptionTestAsync"/>'s synthesized-audio ping, and
    /// <see cref="RunVisionTestAsync"/>'s test-image ping. Validates the synthesized audio has
    /// non-zero duration with <see cref="WavDuration"/> (handles streamed headers; no ffmpeg/new dependency).
    /// Never throws — returns a clean failure response on any error (same discipline as other test methods).
    /// </summary>
    private async Task<TestInferenceProviderResponse> RunSpeechSynthesisTestAsync(ResolvedInferenceProvider provider, CancellationToken ct)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            // Construct a ResolvedSpeechSynthesisProvider from the chat provider config
            ResolvedSpeechSynthesisProvider resolvedSpeechSynthesis = new(
                provider.ProviderId, provider.Name, provider.Kind, provider.Endpoint, provider.ModelName,
                provider.ApiKey, provider.TimeoutSeconds, IsPlatformManaged: provider.IsPlatformManaged);

            ISpeechSynthesisClient synthesisClient = _speechSynthesisClientFactory.Get(resolvedSpeechSynthesis);
            SynthesizedSpeech synthesized = await synthesisClient.SynthesizeAsync(
                new SpeechRequest("ok", null, null), ct);

            stopwatch.Stop();

            // Parse WAV duration from RIFF header
            // WavDuration, not a strict RIFF parse: Fish Audio answers with a STREAMED WAV whose header
            // carries a placeholder data size (0 / 0xFFFFFFFF), which a strict parser reads as "no audio".
            double durationSec = WavDuration.TryGetSeconds(synthesized.Wav, out double duration)
                ? duration
                : 0;

            bool ok = durationSec > 0;
            return new TestInferenceProviderResponse(ok, stopwatch.ElapsedMilliseconds,
                ok ? null : "Provider returned audio with zero duration.", null);
        }
        catch (Exception ex)
        {
            // Same discipline as RunTestAsync/RunTranscriptionTestAsync/RunVisionTestAsync: never let a bad
            // endpoint/model/key escape as a 500.
            stopwatch.Stop();
            _logger.LogError(ex, "Speech synthesis provider test failed");
            // The Fish client already bounds its message (300 chars) and carries only the vendor's own
            // error text + status, so it is safe and far more useful than a bare "failed".
            string detail = ex is HttpRequestException && !string.IsNullOrWhiteSpace(ex.Message)
                ? $"Provider test failed: {ex.Message}"
                : "Provider test failed.";
            return new TestInferenceProviderResponse(false, stopwatch.ElapsedMilliseconds, detail, null);
        }
    }

    /// <summary>
    /// A trivial, structurally valid 32x32 solid-color baseline JPEG, embedded as a literal
    /// rather than generated (this API service has no image-encoding dependency and shouldn't
    /// gain one just for a health-check) — used only as the image content part of
    /// <see cref="RunVisionTestAsync"/>'s connectivity ping.
    /// <para>
    /// Was originally 1x1 — degenerate enough that a lenient self-hosted vLLM backend accepted
    /// it fine, but Anthropic's API rejected it outright with a generic
    /// <c>invalid_request_error: "Could not process image"</c>, found live testing the first
    /// real Anthropic Vision provider against it. 32x32 is still trivially small but avoids
    /// whatever minimum-size/decoding assumption the 1x1 case broke.
    /// </para>
    /// </summary>
    private static readonly byte[] TinyTestJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDABALDA4MChAODQ4SERATGCgaGBYWGDEjJR0oOjM9PDkzODdASFxOQERXRTc4UG1RV19iZ2hnPk1xeXBkeFxlZ2P/2wBDARESEhgVGC8aGi9jQjhCY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2P/wAARCAAgACADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwAoooqyAooooAKKKKACiiigD//Z");

    /// <summary>
    /// Builds a ~0.3 second, 16 kHz mono 16-bit PCM WAV of all-zero (silent) samples, with a
    /// correct 44-byte canonical WAV header, for use as a minimal ASR connectivity ping.
    /// </summary>
    private static MemoryStream BuildSilentWav()
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

    private async Task PersistTestResultAsync(ModelCapability entity, TestInferenceProviderResponse result, CancellationToken ct)
    {
        entity.LastTestAt = DateTime.UtcNow;
        entity.LastTestOk = result.Ok;
        entity.LastTestError = result.Error;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Output-token budget for a one-word connectivity ping. 128 is ample headroom for "ping" plus
    /// a short reasoning preamble on an OpenAI-shaped backend.
    /// <para>
    /// Anthropic needs more headroom: a current Claude model may spend reasoning tokens before
    /// emitting visible content, and those are charged against <c>max_tokens</c> — so a 128-token
    /// ceiling risks being consumed before any content appears, making a correctly configured
    /// provider report the misleading "Provider returned an empty response", which is then
    /// persisted to LastTestError. The Anthropic figure is
    /// <see cref="AnthropicChatOptionsAdapter.MinMaxOutputTokens"/> rather than its own literal:
    /// that adapter now applies the same floor to every Anthropic request, so a second copy here
    /// could only ever drift away from the value actually enforced on the wire.
    /// </para>
    /// </summary>
    private static int PingMaxOutputTokens(InferenceProviderKind kind) =>
        kind switch
        {
            InferenceProviderKind.Anthropic => AnthropicChatOptionsAdapter.MinMaxOutputTokens,
            InferenceProviderKind.Gemini => 1024,
            InferenceProviderKind.DeepSeek => 1024,
            InferenceProviderKind.LocalRunner => 1,
            _ => 128
        };
}
