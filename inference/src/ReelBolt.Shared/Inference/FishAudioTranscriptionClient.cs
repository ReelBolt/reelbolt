using System.Text;
using System.Text.Json;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Speech-to-text (ASR) client for Fish Audio's <c>POST /v1/asr</c> endpoint — the transcription
/// half of the Fish Audio provider kind, alongside
/// <see cref="FishAudioSpeechSynthesisClient"/>'s text-to-speech half.
/// </summary>
/// <remarks>
/// <para>
/// Plain <see cref="HttpClient"/> + <c>System.Text.Json</c>, exactly like the TTS client: Fish Audio
/// ships no official .NET SDK, and this integration must not add a NuGet dependency that could
/// disturb the <c>Microsoft.Extensions.AI.Abstractions</c>-coupled package set (see
/// docs/anthropic-provider.md).
/// </para>
/// <para>
/// <b>Wire format provenance.</b> Every field below is taken from Fish Audio's own published
/// OpenAPI document, <c>https://api.fish.audio/openapi.json</c> (OpenAPI 3.1.0, <c>info.title</c>
/// "FishAudio OpenAPI", <c>info.version</c> "1"), path <c>/v1/asr</c>, operation <c>post</c>, summary
/// "Speech to Text" — not from a blog post, a wrapper library, or inference:
/// </para>
/// <list type="bullet">
///   <item><description><c>model</c> is a request <b>header</b> (not a body field), optional, with
///   <c>default: "transcribe-1"</c> and <c>enum: ["transcribe-1", "transcribe-1-pro"]</c> — the same
///   header-carried model convention the TTS client follows for <c>s2.1-pro</c>.</description></item>
///   <item><description>Request body is <c>multipart/form-data</c> with <c>audio</c> (binary,
///   required), <c>language</c> (optional string hint) and <c>ignore_timestamps</c> (boolean,
///   <c>default: true</c>).</description></item>
///   <item><description>The 200 response is flat JSON carrying <c>text</c>, <c>duration</c> and
///   <c>segments</c> (all three required), plus optional <c>language_code</c> and
///   <c>language</c>.</description></item>
///   <item><description>Auth is the document's <c>BearerAuth</c> scheme (<c>type: http</c>,
///   <c>scheme: bearer</c>), applied on the injected <see cref="HttpClient"/> by
///   <see cref="TranscriptionClientFactory"/> — never here, so the key stays a single-writer
///   concern.</description></item>
/// </list>
/// </remarks>
public sealed class FishAudioTranscriptionClient : ITranscriptionClient
{
    /// <summary>
    /// The model sent when the provider row names none — the OpenAPI document's own <c>default</c>
    /// for the <c>model</c> header, written down once so the row and the wire agree.
    /// </summary>
    public const string DefaultModel = "transcribe-1";

    /// <summary>
    /// Hard ceiling on the size of a transcription response body. A JSON document of segments for
    /// even a multi-hour recording is orders of magnitude smaller than this; the cap exists so a
    /// hostile or broken endpoint cannot answer a small upload with an unbounded body and exhaust
    /// the process.
    /// </summary>
    public const int MaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>
    /// How much of a non-success body is examined at all. Enough for the vendor's JSON error
    /// document (<c>{status, message, reason}</c>), far too little for an endpoint to push megabytes
    /// into the log.
    /// </summary>
    private const int MaxErrorBodyBytes = 4096;

    /// <summary>
    /// How much provider-supplied text is allowed into an exception message. The message is
    /// interpolated into <see cref="HttpRequestException"/> and logged by the caller, so it is
    /// bounded independently of the read cap above (a JSON <c>message</c> field can be arbitrarily
    /// long).
    /// </summary>
    private const int MaxErrorMessageChars = 300;

    private readonly HttpClient _httpClient;
    private readonly string _modelOverride;

    /// <summary>
    /// Creates a new Fish Audio transcription client.
    /// </summary>
    /// <param name="httpClient">
    /// The HTTP client configured with the Fish Audio base address and, for the hosted API, its
    /// <c>Authorization: Bearer</c> header. A blank stored key is rejected by
    /// <see cref="TranscriptionClientFactory"/> before this type is reached — this client has no
    /// <c>env:</c> sentinel and never reads a credential from the environment.
    /// </param>
    /// <param name="modelOverride">
    /// The provider row's model name, used when non-empty — see <see cref="ResolveEffectiveModel"/>.
    /// </param>
    public FishAudioTranscriptionClient(HttpClient httpClient, string? modelOverride)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _modelOverride = modelOverride ?? string.Empty;
    }

    /// <summary>
    /// The model this client sends in the <c>model</c> header: the provider row's model name when
    /// non-empty, else <see cref="DefaultModel"/>.
    /// </summary>
    /// <remarks>
    /// Public and static on purpose, mirroring
    /// <see cref="FishAudioSpeechSynthesisClient.ResolveEffectiveModel"/>. A caller that
    /// re-implemented this precedence could record a model the request never used.
    /// </remarks>
    public static string ResolveEffectiveModel(string? clientModelOverride) =>
        string.IsNullOrWhiteSpace(clientModelOverride) ? DefaultModel : clientModelOverride;

    /// <summary>
    /// Transcribes one WAV stream through Fish Audio's <c>POST /v1/asr</c>.
    /// </summary>
    /// <param name="wav">The audio, already a bounded chunk (callers chunk long tracks upstream).</param>
    /// <param name="fileName">The filename put on the multipart <c>audio</c> part.</param>
    /// <param name="language">
    /// An optional language hint. Fish's own wording: "Optional hint. The language is auto-detected
    /// regardless; the detected language is returned as <c>language_code</c>." Omitted from the form
    /// entirely when blank, so the vendor's own default applies.
    /// </param>
    /// <param name="wordTimestamps">
    /// Accepted for <see cref="ITranscriptionClient"/> compatibility and <b>deliberately unused</b>.
    /// The <c>/v1/asr</c> response schema is <c>text</c>/<c>duration</c>/<c>segments</c>/
    /// <c>language_code</c>/<c>language</c> and its <c>ASRSegment</c> is exactly
    /// <c>{text, start, end}</c> — the endpoint returns no per-word timings at any setting, and Fish
    /// documents no request field that would produce them. Word timings are therefore reported as an
    /// empty list rather than interpolated from segment bounds, which would be fabricated data
    /// dressed as a measurement. Callers that need word-level alignment should use a different
    /// Transcription provider for that step.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<TranscriptResult> TranscribeAsync(
        Stream wav,
        string fileName,
        string? language,
        bool wordTimestamps,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(wav);

        string url = new Uri(_httpClient.BaseAddress?.ToString() ?? "https://api.fish.audio")
            .ToString().TrimEnd('/') + "/v1/asr";

        using var form = new MultipartFormDataContent();

        // Part name is `audio` — the OpenAPI schema's only required property, and the name the
        // vendor's own handler reads. It is a binary string, so the content type rides on the part.
        var audio = new StreamContent(wav);
        audio.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "audio", string.IsNullOrWhiteSpace(fileName) ? "audio.wav" : fileName);

        // `ignore_timestamps` defaults to TRUE in the vendor schema, i.e. the default request comes
        // back WITHOUT the precise timings this platform needs. ReelBolt always wants segments, so the
        // field is sent explicitly as "false" rather than left to the vendor default. Fish's own
        // description of the cost: it "will increase the latency in audio shorter than 30 seconds".
        form.Add(new StringContent("false", Encoding.UTF8), "ignore_timestamps");

        if (!string.IsNullOrWhiteSpace(language))
        {
            form.Add(new StringContent(language, Encoding.UTF8), "language");
        }

        string effectiveModel = ResolveEffectiveModel(_modelOverride);

        // Diarization is selected by the provider row's model name: an admin sets it to
        // `transcribe-1-pro` (the only Fish ASR model that diarizes; `transcribe-1` ignores the
        // field). Any other model sends a byte-identical request to before.
        if (TranscriptionDiarization.FishModelDiarizes(effectiveModel))
        {
            form.Add(new StringContent("auto", Encoding.UTF8), "diarize");
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        httpRequest.Headers.Add("model", effectiveModel);

        // ResponseHeadersRead, NOT the buffering default: this method's own bounded loop is then the
        // first and only reader of the body, so MaxResponseBytes is a real cap rather than a check on
        // an allocation HttpClient has already made. The trade is that the connection is held until
        // the response is disposed, so the using declaration sits at statement scope: the non-success
        // path disposes it as the exception propagates too.
        using HttpResponseMessage response = await _httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await ReadBoundedAsync(response.Content, MaxErrorBodyBytes, ct);
            throw new HttpRequestException(ExtractErrorMessage(errorBody, response.StatusCode));
        }

        string body = await ReadBoundedAsync(response.Content, MaxResponseBytes, ct);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return ParseTranscript(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("Fish Audio's transcription response was not JSON.", ex);
        }
    }

    /// <summary>
    /// Maps the documented <c>/v1/asr</c> 200 body onto ReelBolt's provider-agnostic
    /// <see cref="TranscriptResult"/>. Only <c>text</c>, <c>duration</c> and <c>segments</c> are
    /// required by the schema; <c>language_code</c>/<c>language</c> are optional and are read
    /// defensively so a response that omits them still parses.
    /// </summary>
    internal static TranscriptResult ParseTranscript(JsonElement root)
    {
        string text = root.TryGetProperty("text", out JsonElement textEl) && textEl.ValueKind == JsonValueKind.String
            ? textEl.GetString() ?? string.Empty
            : string.Empty;

        IReadOnlyList<SpeakerTurn> turns = TranscriptionDiarization.ParseFishTurns(root);
        if (turns.Count > 0)
            text = TranscriptionDiarization.StripMarkers(text);

        List<TranscriptSegment> segments = new();
        if (root.TryGetProperty("segments", out JsonElement segmentsEl) && segmentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement segment in segmentsEl.EnumerateArray())
            {
                if (segment.ValueKind != JsonValueKind.Object)
                    continue;

                string segmentText = segment.TryGetProperty("text", out JsonElement segText) && segText.ValueKind == JsonValueKind.String
                    ? segText.GetString() ?? string.Empty
                    : string.Empty;

                // The three Whisper-family confidence figures (no_speech_prob / avg_logprob /
                // compression_ratio) that VideoAnalyze uses to discard segments recognised from music
                // or silence are NOT part of Fish's ASRSegment schema, so they stay at the
                // TranscriptSegment defaults — null, meaning "the backend does not report this",
                // which is exactly what they encode. They are not defaulted to 0, which would read as
                // a confident transcription of speech.
                double segStart = NumberOrZero(segment, "start");
                double segEnd = NumberOrZero(segment, "end");
                segments.Add(new TranscriptSegment(
                    TranscriptionDiarization.StripMarkers(segmentText),
                    segStart,
                    segEnd,
                    Speaker: TranscriptionDiarization.SpeakerAt(turns, segStart, segEnd)));
            }
        }

        // The vendor's own guidance is explicit: `language_code` (ISO 639-1, e.g. "en") is the field
        // to use in code and `language` (the English display name, e.g. "English") is "for display
        // only". Prefer the code, fall back to the name so a response carrying only one still
        // reports something; both absent means null.
        string? language = OptionalString(root, "language_code") ?? OptionalString(root, "language");

        // No words: see TranscribeAsync's `wordTimestamps` remarks — the endpoint has no such field.
        return new TranscriptResult(text, segments, Array.Empty<TranscriptWord>(), language);
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double NumberOrZero(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> of a response body and decodes it as UTF-8. The
    /// remainder is left unread on the wire rather than materialized, and the declared
    /// <c>Content-Length</c> is not trusted on its own — the running total is what is actually
    /// enforced, because a hostile endpoint can simply omit or understate it.
    /// </summary>
    private static async Task<string> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        long? declaredLength = content.Headers.ContentLength;
        if (declaredLength > maxBytes)
        {
            throw new HttpRequestException(TooLargeMessage(declaredLength.Value, maxBytes));
        }

        await using Stream source = await content.ReadAsStreamAsync(ct);
        MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        long total = 0;

        try
        {
            int read;
            while ((read = await source.ReadAsync(chunk.AsMemory(), ct)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new HttpRequestException(TooLargeMessage(total, maxBytes));
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch
        {
            buffer.Dispose();
            throw;
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// The size-limit message. It names the cap and the offending size and deliberately carries no
    /// part of the payload — the point of the limit is that this string never becomes the body it is
    /// describing. Same wording shape as the TTS client's.
    /// </summary>
    private static string TooLargeMessage(long size, int maxBytes) =>
        $"Fish Audio returned a {size}-byte response, which exceeds the {maxBytes}-byte limit for a " +
        "transcription response. Reading stopped at the limit; the bytes beyond it were never retained.";

    /// <summary>
    /// Pulls the vendor's own explanation out of a non-success body. Fish's OpenAPI document defines
    /// the error shape for 401/402/503 as <c>{status, message, reason?}</c>, so <c>message</c> and
    /// <c>reason</c> are read first and the raw body is the fallback for anything else (a proxy's HTML
    /// error page, say). Same field order, same bounding, and the same status suffix as the TTS
    /// client's, so an admin sees one vocabulary whether a Fish call failed on TTS or on ASR.
    /// </summary>
    private static string ExtractErrorMessage(string responseBody, System.Net.HttpStatusCode statusCode)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            JsonElement root = doc.RootElement;

            foreach (string field in new[] { "message", "reason" })
            {
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty(field, out JsonElement value) &&
                    value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(value.GetString()))
                {
                    return $"{Truncate(value.GetString()!)} ({(int)statusCode})";
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON — fall through to the raw body, which is what a proxy error page looks like.
        }

        return $"{Truncate(responseBody)} ({(int)statusCode})";
    }

    private static string Truncate(string value) =>
        value.Length <= MaxErrorMessageChars ? value : value[..MaxErrorMessageChars] + "…";
}
