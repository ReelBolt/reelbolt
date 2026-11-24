using System.ClientModel;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Audio;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Constructs <see cref="ITranscriptionClient"/> instances for an Azure OpenAI (Whisper
/// deployment), an OpenAI-compatible (whisper.cpp-server, faster-whisper-server, speaches,
/// LiteLLM, ...) backend, or Fish Audio's hosted <c>POST /v1/asr</c> ASR API, and caches them by
/// <see cref="ResolvedTranscriptionProvider.CacheKey"/> so repeated resolutions of the same
/// configuration reuse the same client/HttpClient. Mirrors <see cref="ChatClientFactory"/> exactly
/// for the two OpenAI-shaped kinds, including the no-key placeholder handling (Risk R5).
/// </summary>
public sealed class TranscriptionClientFactory : ITranscriptionClientFactory
{
    /// <summary>
    /// <see cref="ApiKeyCredential"/> throws on a null/empty string, but OpenAI-compatible
    /// self-hosted ASR deployments (whisper.cpp-server, faster-whisper-server) commonly run with
    /// no key at all. Same convention as <see cref="ChatClientFactory"/>'s NoKeyPlaceholder.
    /// </summary>
    private const string NoKeyPlaceholder = "not-required";

    private readonly ConcurrentDictionary<string, ITranscriptionClient> _clients = new();

    private readonly IUsageRecorder? _recorder;
    private readonly ILogger<TranscriptionClientFactory>? _logger;

    /// <param name="recorder">When supplied, every client handed out meters audio seconds (B3c); null returns it raw.</param>
    public TranscriptionClientFactory(IUsageRecorder? recorder = null, ILogger<TranscriptionClientFactory>? logger = null)
    {
        _recorder = recorder;
        _logger = logger;
    }

    public ITranscriptionClient Get(ResolvedTranscriptionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        bool orgOwned = ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged);
        ITranscriptionClient inner = _clients.GetOrAdd(ProviderRowTransport.CacheKey(provider.CacheKey, orgOwned), _ => Build(provider));

        // Wrapped per call, outside the cache: the owning organization is not part of CacheKey.
        return _recorder is null ? inner : new MeteringTranscriptionClient(inner, provider, _recorder, _logger);
    }

    private static ITranscriptionClient Build(ResolvedTranscriptionProvider provider) => provider.Kind switch
    {
        InferenceProviderKind.AzureOpenAI => BuildAzureOpenAI(provider),
        InferenceProviderKind.OpenAICompatible => BuildOpenAICompatible(provider),
        InferenceProviderKind.FishAudio => BuildFishAudio(provider),

        // Anthropic ships no speech-to-text endpoint, so there is nothing to build here. This
        // should be unreachable — InferenceProvidersController rejects the combination when the
        // row is created or updated — but a row predating that validation, or written straight to
        // the database, would arrive here, and a bare "unsupported kind" would send whoever hits
        // it looking for a bug in the wrong place.
        InferenceProviderKind.Anthropic => throw new NotSupportedException(
            $"Inference provider '{provider.Name}' is an Anthropic provider, which cannot serve " +
            "the Transcription capability — Anthropic exposes no speech-to-text API. Configure a " +
            "Transcription provider of kind AzureOpenAI or OpenAICompatible (for example the " +
            "bundled `whisper` service), or FishAudio (hosted Fish Audio ASR), instead."),

        // Google Gemini does not expose an OpenAI-compatible speech-to-text endpoint.
        InferenceProviderKind.Gemini => throw new NotSupportedException(
            $"Inference provider '{provider.Name}' is a Gemini provider, which cannot serve " +
            "the Transcription capability — Google Gemini does not expose an OpenAI-compatible speech-to-text API. " +
            "Configure a Transcription provider of kind AzureOpenAI or OpenAICompatible (for example the " +
            "bundled `whisper` service), or FishAudio (hosted Fish Audio ASR), instead."),

        // DeepSeek does not expose an OpenAI-compatible speech-to-text endpoint.
        InferenceProviderKind.DeepSeek => throw new NotSupportedException(
            $"Inference provider '{provider.Name}' is a DeepSeek provider, which cannot serve " +
            "the Transcription capability — DeepSeek does not expose an OpenAI-compatible speech-to-text API. " +
            "Configure a Transcription provider of kind AzureOpenAI or OpenAICompatible (for example the " +
            "bundled `whisper` service), or FishAudio (hosted Fish Audio ASR), instead."),

        _ => throw new NotSupportedException($"Unsupported inference provider kind '{provider.Kind}'.")
    };

    private static ITranscriptionClient BuildAzureOpenAI(ResolvedTranscriptionProvider provider)
    {
        AzureOpenAIClientOptions options = new()
        {
            NetworkTimeout = TimeSpan.FromSeconds(provider.TimeoutSeconds)
        };
        if (ProviderRowTransport.CreateSdkTransport(
                ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged)) is { } transport)
        {
            options.Transport = transport;
        }

        AzureOpenAIClient client = new(
            new Uri(provider.Endpoint),
            new ApiKeyCredential(provider.ApiKey),
            options);

        // AzureOpenAIClient.GetAudioClient(...) returns the derived AzureAudioClient at runtime,
        // but it is assignable to the base AudioClient - the same polymorphism ChatClientFactory
        // relies on for .GetChatClient(...).AsIChatClient(). Holding it as AudioClient here means
        // OpenAiAudioTranscriptionClient never needs to know which branch built it.
        AudioClient audioClient = client.GetAudioClient(provider.ModelName);
        return new OpenAiAudioTranscriptionClient(audioClient);
    }

    /// <summary>
    /// OpenAI-compatible backends go through a raw multipart POST instead of the OpenAI SDK's
    /// typed <see cref="AudioTranscriptionOptions"/> so a <c>vad_filter=false</c> form field can be
    /// sent — the SDK has no property for it. This is specifically to work around a real deployed
    /// bug: speaches (the reference "OpenAI-compatible" self-hosted ASR server this app is
    /// documented to use — see CLAUDE.md's <c>whisper</c> service) defaults faster-whisper's VAD
    /// filter ON via a private, environment-variable-immune Pydantic field
    /// (<c>_unstable_vad_filter</c>), and that default was verified live to make segmentation
    /// wildly unstable — the SAME audio, differing by inaudible resampler noise, produced
    /// terminal-sentence-punctuation rates from 11% to 63% depending on VAD, because VAD changes
    /// faster-whisper's internal seek/window alignment, not just which audio it drops. Turning it
    /// off (an OpenAI Whisper API default) collapsed that instability without any latency or
    /// accuracy cost in side-by-side testing. Azure OpenAI deployments do not exhibit this bug and
    /// keep going through the SDK unchanged below.
    /// </summary>
    private static ITranscriptionClient BuildOpenAICompatible(ResolvedTranscriptionProvider provider)
    {
        string apiKey = string.IsNullOrWhiteSpace(provider.ApiKey) ? NoKeyPlaceholder : provider.ApiKey;
        HttpClient httpClient = ProviderRowTransport.CreateHttpClient(
            ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged));
        // A BaseAddress with no trailing slash makes HttpClient's relative-URI combining
        // REPLACE its last path segment instead of appending to it (e.g. "http://h/v1" +
        // "audio/transcriptions" -> "http://h/audio/transcriptions", dropping "/v1" entirely) —
        // force the trailing slash so PostAsync("audio/transcriptions", ...) below actually
        // lands on ".../v1/audio/transcriptions".
        httpClient.BaseAddress = new Uri(provider.Endpoint.TrimEnd('/') + "/");
        httpClient.Timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds);
        if (apiKey != NoKeyPlaceholder)
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return new RawHttpAudioTranscriptionClient(httpClient, provider.ModelName);
    }

    /// <summary>
    /// Fish Audio's hosted ASR API — <c>POST {endpoint}/v1/asr</c>, the transcription half of the
    /// <c>FishAudio</c> kind whose text-to-speech half is <see cref="FishAudioSpeechSynthesisClient"/>
    /// (see docs/voiceover.md "Fish Audio transcription"). Same plain-HttpClient shape as the TTS
    /// arm, deliberately: Fish ships no .NET SDK, so neither half may acquire one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Credential rule: the stored key, sent verbatim, and no <c>env:</c> sentinel.</b> The key
    /// is written straight onto the client's <c>Authorization: Bearer</c> header and nothing is ever
    /// read from the container environment, so a row can never spend the host's own credential.
    /// </para>
    /// <para>
    /// <b>A blank key THROWS here</b> — it does not degrade to an unauthenticated request the way
    /// the OpenAI-compatible arm above does. The asymmetry is deliberate. Fish's ASR endpoint is a
    /// hosted, key-authenticated service — <c>/v1/asr</c> is published in the vendor's own OpenAPI
    /// document for <c>api.fish.audio</c> — and unlike the TTS side there is no documented no-key
    /// self-hosted contract the blank-key case could be serving, so an empty key can only mean an
    /// empty form field or a key that failed to decrypt. That is a misconfiguration the admin needs
    /// told about at the point of use, not an unauthenticated request whose 401 arrives three steps
    /// into a workflow. Same reasoning as <c>ChatClientFactory</c>'s DeepSeek arm, which throws
    /// rather than silently resolving ambient credentials.
    /// </para>
    /// <para>
    /// Redirects are refused for the same SSRF reason as the TTS arm: the endpoint comes from an
    /// operator-supplied row and was validated at write time, so following a 3xx would hand the
    /// request (and the Authorization header with it) to a second host that was never validated.
    /// A self-hosted fish-speech server is a legitimate target for this kind — <c>FishAudio</c> is
    /// one of the three kinds exempt from the controller's private-address guard — so the base
    /// address is taken as given rather than pinned to the vendor host.
    /// </para>
    /// </remarks>
    private static ITranscriptionClient BuildFishAudio(ResolvedTranscriptionProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Endpoint))
            throw new InvalidOperationException(
                $"Inference provider '{provider.Name}' has no endpoint; a Fish Audio ASR provider is only reachable at the address an operator supplied.");

        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidOperationException(
                $"Inference provider '{provider.Name}' has no API key. A FishAudio transcription provider needs the Fish Audio API key stored on the row — ReelBolt sends that key verbatim and never falls back to an ambient environment variable, so an empty key cannot be made to work from the container environment. Re-enter the key, or point the step at a different Transcription provider.");

        SocketsHttpHandler handler = ProviderRowTransport.CreateNoRedirectHandler(
            ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged));
        HttpClient httpClient = new(handler)
        {
            BaseAddress = new Uri(provider.Endpoint.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds)
        };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);

        return new FishAudioTranscriptionClient(httpClient, provider.ModelName);
    }

    /// <summary>
    /// Adapts an <see cref="OpenAI.Audio.AudioClient"/> (plain, or Azure's derived
    /// <c>AzureAudioClient</c>) to ReelBolt's own <see cref="ITranscriptionClient"/> so callers
    /// never touch the SDK type directly.
    /// </summary>
    private sealed class OpenAiAudioTranscriptionClient : ITranscriptionClient
    {
        private readonly AudioClient _audioClient;

        public OpenAiAudioTranscriptionClient(AudioClient audioClient)
        {
            _audioClient = audioClient;
        }

        public async Task<TranscriptResult> TranscribeAsync(
            Stream wav,
            string fileName,
            string? language,
            bool wordTimestamps,
            CancellationToken ct)
        {
            AudioTranscriptionOptions options = new()
            {
                ResponseFormat = AudioTranscriptionFormat.Verbose,
                TimestampGranularities = wordTimestamps
                    ? AudioTimestampGranularities.Word | AudioTimestampGranularities.Segment
                    : AudioTimestampGranularities.Segment
            };

            if (!string.IsNullOrWhiteSpace(language))
            {
                options.Language = language;
            }

            ClientResult<AudioTranscription> result =
                await _audioClient.TranscribeAudioAsync(wav, fileName, options, ct);
            AudioTranscription transcription = result.Value;

            List<TranscriptSegment> segments = transcription.Segments
                .Select(s => new TranscriptSegment(
                    s.Text, s.StartTime.TotalSeconds, s.EndTime.TotalSeconds,
                    s.NoSpeechProbability, s.AverageLogProbability, s.CompressionRatio))
                .ToList();

            List<TranscriptWord> words = transcription.Words
                .Select(w => new TranscriptWord(w.Word, w.StartTime.TotalSeconds, w.EndTime.TotalSeconds))
                .ToList();

            return new TranscriptResult(transcription.Text, segments, words, transcription.Language);
        }
    }

    /// <summary>
    /// Raw multipart <c>/audio/transcriptions</c> client for OpenAI-compatible backends — see
    /// <see cref="BuildOpenAICompatible"/>'s remarks for why this bypasses the OpenAI SDK entirely
    /// rather than merely wrapping <see cref="AudioClient"/>: the SDK does not expose a
    /// <c>vad_filter</c> option and this is the one field that actually needs sending.
    /// </summary>
    private sealed class RawHttpAudioTranscriptionClient : ITranscriptionClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _modelName;

        public RawHttpAudioTranscriptionClient(HttpClient httpClient, string modelName)
        {
            _httpClient = httpClient;
            _modelName = modelName;
        }

        public async Task<TranscriptResult> TranscribeAsync(
            Stream wav,
            string fileName,
            string? language,
            bool wordTimestamps,
            CancellationToken ct)
        {
            using MultipartFormDataContent form = new();

            StreamContent fileContent = new(wav);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(fileContent, "file", fileName);
            form.Add(new StringContent(_modelName), "model");
            bool diarize = TranscriptionDiarization.OpenAiModelDiarizes(_modelName);
            if (diarize)
            {
                // Diarizing models take neither word timestamps nor vad_filter; chunking_strategy is
                // required for audio over 30 s. Segments carry the speaker; no words are returned.
                form.Add(new StringContent("diarized_json"), "response_format");
                form.Add(new StringContent("auto"), "chunking_strategy");
            }
            else
            {
                form.Add(new StringContent("verbose_json"), "response_format");
                form.Add(new StringContent("false"), "vad_filter");
                form.Add(new StringContent("segment"), "timestamp_granularities[]");
                if (wordTimestamps)
                {
                    form.Add(new StringContent("word"), "timestamp_granularities[]");
                }
            }
            if (!string.IsNullOrWhiteSpace(language))
            {
                form.Add(new StringContent(language), "language");
            }

            using HttpResponseMessage response =
                await _httpClient.PostAsync("audio/transcriptions", form, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Transcription request failed with {(int)response.StatusCode} {response.StatusCode}: {body}");
            }

            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            if (diarize)
                return TranscriptionDiarization.ParseOpenAiDiarized(root);

            string text = root.TryGetProperty("text", out JsonElement textEl) ? textEl.GetString() ?? "" : "";
            string? responseLanguage =
                root.TryGetProperty("language", out JsonElement langEl) ? langEl.GetString() : null;

            List<TranscriptSegment> segments = new();
            if (root.TryGetProperty("segments", out JsonElement segmentsEl) && segmentsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement seg in segmentsEl.EnumerateArray())
                {
                    segments.Add(new TranscriptSegment(
                        seg.GetProperty("text").GetString() ?? "",
                        seg.GetProperty("start").GetDouble(),
                        seg.GetProperty("end").GetDouble(),
                        OptionalDouble(seg, "no_speech_prob"),
                        OptionalDouble(seg, "avg_logprob"),
                        OptionalDouble(seg, "compression_ratio")));
                }
            }

            List<TranscriptWord> words = new();
            if (root.TryGetProperty("words", out JsonElement wordsEl) && wordsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement w in wordsEl.EnumerateArray())
                {
                    words.Add(new TranscriptWord(
                        w.GetProperty("word").GetString() ?? "",
                        w.GetProperty("start").GetDouble(),
                        w.GetProperty("end").GetDouble()));
                }
            }

            return new TranscriptResult(text, segments, words, responseLanguage);
        }

        /// <summary>A numeric segment field when present, else null — backends that omit Whisper's confidence figures still parse.</summary>
        private static double? OptionalDouble(JsonElement element, string name) =>
            element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    }
}
