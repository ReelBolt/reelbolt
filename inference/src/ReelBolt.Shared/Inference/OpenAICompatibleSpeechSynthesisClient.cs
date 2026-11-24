using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Speech synthesis over an OpenAI-compatible <c>POST {base}/audio/speech</c> endpoint — the
/// LOCAL TTS path (see docs/voiceover.md "A local OpenAI-compatible TTS server"): the bundled
/// <c>speaches</c> container (kokoro/piper voices), vLLM-style servers, or OpenAI itself. Plain
/// <c>HttpClient</c> + <c>System.Text.Json</c>, no new NuGet package.
/// </summary>
/// <remarks>
/// Wire format verified against the OpenAI OpenAPI specification
/// (<c>CreateSpeechRequest</c>, 2026-10-04): a JSON body with the REQUIRED <c>model</c>,
/// <c>input</c> (max 4096 characters) and <c>voice</c>, plus <c>response_format</c> (<c>wav</c>
/// here — the canonical format every consumer of a line already expects) and <c>speed</c>. The
/// response body is the audio bytes. There is no pronunciation-dictionary field on this contract,
/// so <see cref="SpeechRequest.Pronunciation"/> is deliberately ignored rather than smuggled into
/// the text. The same response-size ceilings as the Fish client apply, for the same reason.
/// </remarks>
public sealed class OpenAICompatibleSpeechSynthesisClient : ISpeechSynthesisClient
{
    /// <summary>The voice sent when neither the line nor the step config names one — OpenAI's own default, and the one every compatible server documents first.</summary>
    public const string DefaultVoice = "alloy";

    /// <summary>OpenAI's documented input ceiling; a longer line is refused here rather than silently truncated by a vendor.</summary>
    public const int MaxInputChars = 4096;

    private const int MaxErrorBodyBytes = 4096;
    private const int MaxErrorMessageChars = 300;

    private readonly HttpClient _httpClient;
    private readonly string _model;

    public OpenAICompatibleSpeechSynthesisClient(HttpClient httpClient, string? model)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _model = string.IsNullOrWhiteSpace(model) ? "tts-1" : model;
    }

    /// <summary>The model actually sent: the request override when non-empty, else the provider row's model, else <c>tts-1</c>.</summary>
    public static string ResolveEffectiveModel(string? requestModelOverride, string? clientModel)
    {
        if (!string.IsNullOrEmpty(requestModelOverride))
            return requestModelOverride;
        if (!string.IsNullOrEmpty(clientModel))
            return clientModel;
        return "tts-1";
    }

    public async Task<SynthesizedSpeech> SynthesizeAsync(SpeechRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Text.Length > MaxInputChars)
        {
            throw new ArgumentException(
                $"The OpenAI-compatible speech endpoint accepts at most {MaxInputChars} characters of input; this line has {request.Text.Length}.",
                nameof(request));
        }

        // The base address is the provider row's endpoint, which the admin form documents as
        // including the /v1 suffix (http://host:8000/v1) — so the path appended here is relative.
        string baseAddress = _httpClient.BaseAddress?.ToString().TrimEnd('/') ?? "https://api.openai.com/v1";
        var url = new Uri(baseAddress + "/audio/speech");

        var body = new OpenAISpeechRequest
        {
            Model = ResolveEffectiveModel(request.ModelOverride, _model),
            Input = request.Text,
            Voice = string.IsNullOrWhiteSpace(request.VoiceId) ? DefaultVoice : request.VoiceId,
            ResponseFormat = "wav",
            Speed = 1
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json")
        };

        using HttpResponseMessage response = await _httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.IsSuccessStatusCode)
        {
            Stream audio = await ReadBoundedAudioAsync(response, ct);
            return new SynthesizedSpeech(audio, "audio/wav");
        }

        string errorBody = await ReadBoundedErrorBodyAsync(response.Content, ct);
        throw new HttpRequestException(ExtractErrorMessage(errorBody, (int)response.StatusCode));
    }

    private static async Task<Stream> ReadBoundedAudioAsync(HttpResponseMessage response, CancellationToken ct)
    {
        long? declared = response.Content.Headers.ContentLength;
        if (declared > FishAudioSpeechSynthesisClient.MaxAudioResponseBytes)
            throw new HttpRequestException(TooLarge(declared.Value));

        await using Stream source = await response.Content.ReadAsStreamAsync(ct);
        MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        long total = 0;
        try
        {
            int read;
            while ((read = await source.ReadAsync(chunk.AsMemory(), ct)) > 0)
            {
                total += read;
                if (total > FishAudioSpeechSynthesisClient.MaxAudioResponseBytes)
                    throw new HttpRequestException(TooLarge(total));
                buffer.Write(chunk, 0, read);
            }
        }
        catch
        {
            buffer.Dispose();
            throw;
        }

        buffer.Seek(0, SeekOrigin.Begin);
        return buffer;
    }

    private static string TooLarge(long size) =>
        $"The speech endpoint returned a {size}-byte response, which exceeds the " +
        $"{FishAudioSpeechSynthesisClient.MaxAudioResponseBytes}-byte limit for synthesized audio. Reading stopped at the limit.";

    private static async Task<string> ReadBoundedErrorBodyAsync(HttpContent content, CancellationToken ct)
    {
        await using Stream source = await content.ReadAsStreamAsync(ct);
        byte[] buffer = new byte[MaxErrorBodyBytes];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await source.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0) break;
            total += read;
        }

        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static string ExtractErrorMessage(string body, int statusCode)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            // OpenAI's error envelope is {"error":{"message":...}}; compatible servers vary.
            if (doc.RootElement.TryGetProperty("error", out JsonElement error))
            {
                string? message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out JsonElement m)
                    ? m.GetString()
                    : error.ValueKind == JsonValueKind.String ? error.GetString() : null;
                if (!string.IsNullOrEmpty(message))
                    return $"{Truncate(message)} ({statusCode})";
            }

            if (doc.RootElement.TryGetProperty("message", out JsonElement top) && !string.IsNullOrEmpty(top.GetString()))
                return $"{Truncate(top.GetString()!)} ({statusCode})";
            if (doc.RootElement.TryGetProperty("detail", out JsonElement detail) && detail.ValueKind == JsonValueKind.String)
                return $"{Truncate(detail.GetString()!)} ({statusCode})";
        }
        catch (JsonException)
        {
            // Raw body below.
        }

        return $"{Truncate(body)} ({statusCode})";
    }

    private static string Truncate(string value) =>
        value.Length <= MaxErrorMessageChars ? value : value[..MaxErrorMessageChars] + "…";

    private sealed class OpenAISpeechRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("input")]
        public string Input { get; set; } = string.Empty;

        [JsonPropertyName("voice")]
        public string Voice { get; set; } = DefaultVoice;

        [JsonPropertyName("response_format")]
        public string ResponseFormat { get; set; } = "wav";

        [JsonPropertyName("speed")]
        public double Speed { get; set; } = 1;
    }
}
