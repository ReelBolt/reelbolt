using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Speech synthesis client for Fish Audio's text-to-speech API. Implements the real POST /v1/tts
/// wire format, reading the WAV response under a hard byte ceiling and surfacing errors from the
/// API response.
/// </summary>
/// <remarks>
/// <para>
/// Two cloning back ends share this class: the hosted API's <c>/model</c> endpoints and a
/// self-hosted fish-speech server's reference library (<c>/v1/references/*</c>), chosen by
/// <see cref="DetectCloningApiAsync"/>. Synthesis is the same <c>POST /v1/tts</c> with
/// <c>reference_id</c> on both, so a voice cloned either way is spoken the same way.
/// </para>
/// The response is requested with <see cref="HttpCompletionOption.ResponseHeadersRead"/>, so the
/// body is streamed by this class rather than buffered by <see cref="HttpClient"/> first — the
/// caps in this file are the real bound on what the process reads (see
/// <see cref="SynthesizeAsync"/>), and the response is disposed on every path because in that mode
/// the connection stays held until it is.
/// </remarks>
public sealed class FishAudioSpeechSynthesisClient : ISpeechSynthesisClient, IVoiceCloningClient, IVoiceLibraryClient
{
    /// <summary>
    /// Hard ceiling on the size of a synthesized-audio response body. 32 MiB is roughly nine
    /// minutes of 48 kHz 16-bit mono WAV — an order of magnitude more than any single voiceover
    /// line — while still being small enough that a hostile or broken endpoint cannot exhaust the
    /// process by answering a 20-byte request with an unbounded body.
    /// </summary>
    public const int MaxAudioResponseBytes = 32 * 1024 * 1024;

    /// <summary>
    /// How much of a non-success body is examined at all. Enough for a JSON error document, far
    /// too little for an endpoint to push megabytes into the log.
    /// </summary>
    private const int MaxErrorBodyBytes = 4096;

    /// <summary>
    /// How much of that body is allowed into the exception message. The message is interpolated
    /// into <see cref="HttpRequestException"/> and logged by the caller, so it is bounded
    /// independently of the read cap above (a JSON <c>message</c> field can be arbitrarily long).
    /// </summary>
    private const int MaxErrorMessageChars = 300;

    /// <summary>The hosted API's host. Any other endpoint is probed for a self-hosted fish-speech server before cloning.</summary>
    public const string HostedApiHost = "api.fish.audio";

    /// <summary>Prefix of the reference ids this client creates on a self-hosted server (the rest is a GUID, so ids never collide and always match the server's <c>^[a-zA-Z0-9\-_ ]+$</c>).</summary>
    public const string SelfHostedReferenceIdPrefix = "reelbolt-";

    /// <summary>
    /// Which cloning API the endpoint speaks. Decided once per client (clients are cached per
    /// provider row by <see cref="SpeechSynthesisClientFactory"/>, so in practice once per row and
    /// process), and only from an HTTP answer — a connection failure is not cached, so a server
    /// that was briefly down is probed again next time.
    /// </summary>
    internal enum CloningApi
    {
        Unknown = 0,
        HostedModel = 1,
        SelfHostedReferences = 2
    }

    private readonly HttpClient _httpClient;
    private readonly string _modelOverride;
    private int _cloningApi; // CloningApi, read/written with Volatile — a race only repeats an idempotent probe.

    /// <summary>
    /// Creates a new Fish Audio speech synthesis client.
    /// </summary>
    /// <param name="httpClient">The HTTP client configured with the Fish Audio base address and authorization.</param>
    /// <param name="modelOverride">The provider row's model name, used when a request carries no override — see <see cref="ResolveEffectiveModel"/>.</param>
    public FishAudioSpeechSynthesisClient(HttpClient httpClient, string? modelOverride)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _modelOverride = modelOverride ?? string.Empty;
    }

    /// <summary>
    /// The model this client will send in the <c>model</c> header for a request carrying
    /// <paramref name="requestModelOverride"/>: the request override when non-empty, else the
    /// client (provider-row) override when non-empty, else the literal <c>s2.1-pro</c>.
    /// </summary>
    /// <remarks>
    /// Public and static on purpose. <see cref="SynthesizeAsync"/> resolves its header THROUGH it,
    /// and the voiceover producer keys its per-line content address on the same value: a caller
    /// that re-implemented this precedence would silently key artifacts on a model the request
    /// never used (the provider row's <c>ModelName</c> is the usual effective value, because
    /// <c>VoiceoverStepConfig.Model</c> defaults to null), reusing another backend's audio for the
    /// same text. One expression, one place.
    /// </remarks>
    public static string ResolveEffectiveModel(string? requestModelOverride, string? clientModelOverride)
    {
        if (!string.IsNullOrEmpty(requestModelOverride))
            return requestModelOverride;

        if (!string.IsNullOrEmpty(clientModelOverride))
            return clientModelOverride;

        return "s2.1-pro";
    }

    /// <summary>
    /// Synthesizes speech from the given text request by calling Fish Audio's POST /v1/tts endpoint.
    /// </summary>
    /// <remarks>
    /// Implements the exact wire format documented in the ticket:
    /// - Uses POST {endpoint}/v1/tts (endpoint from HttpClient.BaseAddress or defaults to https://api.fish.audio)
    /// - Sets the model header from <see cref="ResolveEffectiveModel"/> — the SAME helper the
    ///   caller-side content key is built through, so the value the executor hashes and the value
    ///   actually sent cannot drift
    /// - Authorization header already set on injected HttpClient
    /// - Sends JSON body with format/sample_rate/latency/prosody as documented
    /// - On 200: buffers raw audio bytes into MemoryStream, seeks to 0, returns SynthesizedSpeech
    /// - On non-200: throws HttpRequestException including body.message, body.reason, or raw body plus status code
    /// </remarks>
    public async Task<SynthesizedSpeech> SynthesizeAsync(SpeechRequest request, CancellationToken ct)
    {
        var endpoint = _httpClient.BaseAddress?.ToString() ?? "https://api.fish.audio";
        var url = new Uri(endpoint).ToString().TrimEnd('/') + "/v1/tts";

        // The model actually sent, resolved through the same public helper the producer hashes.
        var model = ResolveEffectiveModel(request.ModelOverride, _modelOverride);

        // Build the request
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Add("model", model);

        // Build the request body
        var body = new FishAudioTtsRequest
        {
            Text = request.Text,
            Format = "wav",
            SampleRate = 44100,
            Latency = "normal",
            Prosody = new FishAudioProsody
            {
                Speed = request.Speed ?? 1,
                Volume = 0,
                NormalizeLoudness = true
            }
        };

        // Include reference_id only if VoiceId is set
        if (!string.IsNullOrEmpty(request.VoiceId))
        {
            body.ReferenceId = request.VoiceId;
        }

        // Language drives only Fish's number/date normalisation (not stress); null means auto-detect.
        body.Language = FishLanguageTag.From(request.Language);

        // Pronunciation dictionary (docs/voiceover.md "Pronunciation dictionary"): the inline form
        // of Fish's `pronunciation_dictionary` — a list of up to 3 dictionaries, each an `items`
        // array of {key, value, case_sensitive}, where `value` is the phoneme form the engine wraps
        // in its own phoneme markers (verified against api.fish.audio/openapi.json on 2026-10-04).
        // ONE dictionary is sent, holding every entry; omitted entirely (never an empty array) when
        // there are none, so the phase-1 request body is byte-identical.
        if (request.Pronunciation is { Count: > 0 })
        {
            body.PronunciationDictionary =
            [
                new FishAudioPronunciationDictionary
                {
                    Items = request.Pronunciation
                        .Select(e => new FishAudioPronunciationItem { Key = e.Key, Value = e.Value, CaseSensitive = e.CaseSensitive })
                        .ToList()
                }
            ];
        }

        var jsonContent = JsonSerializer.Serialize(body);
        httpRequest.Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

        // ResponseHeadersRead, NOT the ResponseContentRead default. With the default, HttpClient
        // buffers the ENTIRE body (its own MaxResponseContentBufferSize ceiling, ~2 GiB) before
        // this method regains control, so both read caps below would guard an allocation that had
        // already happened — 362 MiB allocated on a 200 MiB body, measured. In headers-read mode
        // this method's own loop is genuinely the first and only reader, and the caps become the
        // real bound on what the process reads.
        //
        // The trade: in that mode the connection is held open until the response is disposed, so
        // the response MUST be disposed on every path. The using declaration below is at statement
        // scope rather than inside a branch, so the non-success path disposes it as the exception
        // propagates too. Disposing is safe on the success path because ReadAudioResponseAsync has
        // already copied the bytes into a MemoryStream it returns; the audio outlives the response.
        using HttpResponseMessage response = await _httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        // Handle the response
        if (response.IsSuccessStatusCode)
        {
            Stream audio = await ReadAudioResponseAsync(response, ct);
            return new SynthesizedSpeech(audio, "audio/wav");
        }

        // Non-success: extract error message from a bounded prefix of the response.
        var responseBody = await ReadBoundedErrorBodyAsync(response.Content, ct);
        var errorMessage = ExtractErrorMessage(responseBody, response.StatusCode);
        throw new HttpRequestException(errorMessage);
    }

    /// <summary>
    /// Creates a Fish Audio voice model from reference recordings — <c>POST /model</c> as
    /// <c>multipart/form-data</c> (see docs/voiceover.md "Voices and consent"). Wire format verified
    /// against <c>api.fish.audio/openapi.json</c> on 2026-10-04: required <c>type</c> (<c>tts</c>),
    /// <c>title</c>, <c>train_mode</c> (<c>fast</c>, instantly available) and <c>voices</c> (1–20
    /// files); <c>visibility</c> is sent as <c>private</c> explicitly, because a cloned voice is a
    /// person's biometric data and must never be listable by anyone else. <c>texts</c> is omitted —
    /// the vendor runs its own ASR over the recordings. The response's <c>_id</c> is the voice id a
    /// later <c>reference_id</c> names.
    /// </summary>
    public async Task<CreatedVoice> CreateVoiceAsync(VoiceCloneRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await DetectCloningApiAsync(ct) == CloningApi.SelfHostedReferences)
            return await CreateSelfHostedReferenceAsync(request, ct);

        if (request.References.Count is < 1 or > 20)
            throw new ArgumentException("Fish Audio accepts between 1 and 20 reference recordings.", nameof(request));

        var endpoint = _httpClient.BaseAddress?.ToString() ?? "https://api.fish.audio";
        var url = new Uri(endpoint).ToString().TrimEnd('/') + "/model";

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("tts"), "type");
        form.Add(new StringContent(request.Title), "title");
        form.Add(new StringContent("fast"), "train_mode");
        form.Add(new StringContent("private"), "visibility");
        form.Add(new StringContent("true"), "enhance_audio_quality");
        if (!string.IsNullOrWhiteSpace(request.Description))
            form.Add(new StringContent(request.Description), "description");
        foreach (VoiceReferenceAudio reference in request.References)
        {
            var part = new ByteArrayContent(reference.Bytes);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(reference.ContentType) ? "audio/wav" : reference.ContentType);
            form.Add(part, "voices", reference.FileName);
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await ReadBoundedErrorBodyAsync(response.Content, ct);
            throw new HttpRequestException(ExtractErrorMessage(errorBody, response.StatusCode));
        }

        string body = await ReadBoundedErrorBodyAsync(response.Content, ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            string? id = doc.RootElement.TryGetProperty("_id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                throw new HttpRequestException("Fish Audio created a voice model but returned no `_id`.");
            string? state = doc.RootElement.TryGetProperty("state", out var stateEl) && stateEl.ValueKind == JsonValueKind.String
                ? stateEl.GetString()
                : null;
            return new CreatedVoice(id, state);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("Fish Audio's voice-creation response was not JSON.", ex);
        }
    }

    /// <summary>How much of a library listing is read. A page of 50 voices is tens of KiB; this is a ceiling, not a target.</summary>
    private const int MaxLibraryBodyBytes = 2 * 1024 * 1024;

    /// <summary>
    /// <c>GET /model?page_size=&amp;page_number=&amp;title=&amp;language=&amp;sort_by=score</c> — the hosted
    /// API's public voice listing. ASSUMED response shape (no live call is made from this repo; listed
    /// as "must measure" in docs/voiceover.md): <c>{"total": n, "items": [{"_id", "title",
    /// "languages": [..], "tags": [..], "samples": [..]}]}</c>. A self-hosted fish-speech server has no
    /// library, so it throws <see cref="NotSupportedException"/>.
    /// </summary>
    public async Task<VoiceLibraryPage> ListLibraryVoicesAsync(VoiceLibraryQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (await DetectCloningApiAsync(ct) == CloningApi.SelfHostedReferences)
            throw new NotSupportedException("A self-hosted fish-speech server has no public voice library.");

        var endpoint = _httpClient.BaseAddress?.ToString() ?? "https://api.fish.audio";
        var url = new Uri(endpoint).ToString().TrimEnd('/') + "/model"
            + "?page_size=" + query.PageSize
            + "&page_number=" + Math.Max(1, query.Page)
            + "&sort_by=score";
        if (!string.IsNullOrWhiteSpace(query.Search)) url += "&title=" + Uri.EscapeDataString(query.Search.Trim());
        if (!string.IsNullOrWhiteSpace(query.Language)) url += "&language=" + Uri.EscapeDataString(query.Language.Trim());

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await ReadBoundedErrorBodyAsync(response.Content, ct);
            throw new HttpRequestException(ExtractErrorMessage(errorBody, response.StatusCode));
        }

        byte[] body = await ReadLibraryBodyAsync(response.Content, ct);

        try
        {
            using var doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            List<VoiceLibraryVoice> items = [];
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in arr.EnumerateArray())
                {
                    if (ParseLibraryVoice(item) is { } voice)
                        items.Add(voice);
                }
            }

            int total = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("total", out JsonElement totalEl) && totalEl.TryGetInt32(out int n) ? n : items.Count;
            return new VoiceLibraryPage(items, total);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("Fish Audio's voice listing was not JSON.", ex);
        }
    }

    /// <summary>
    /// <c>GET /model/{id}</c> — one model of the hosted API, ASSUMED to have the listing item's shape plus
    /// <c>visibility</c> (must measure; docs/voiceover.md). Only a public voice is returned: a 404, or a
    /// model whose <c>visibility</c> is present and not <c>public</c> (a private clone on the same account,
    /// an unlisted model), answers null. A self-hosted fish-speech server has no library and throws
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    public async Task<VoiceLibraryVoice?> GetLibraryVoiceAsync(string voiceId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceId);
        if (await DetectCloningApiAsync(ct) == CloningApi.SelfHostedReferences)
            throw new NotSupportedException("A self-hosted fish-speech server has no public voice library.");

        var endpoint = _httpClient.BaseAddress?.ToString() ?? "https://api.fish.audio";
        var url = new Uri(endpoint).ToString().TrimEnd('/') + "/model/" + Uri.EscapeDataString(voiceId.Trim());

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.UnprocessableEntity)
            return null;
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await ReadBoundedErrorBodyAsync(response.Content, ct);
            throw new HttpRequestException(ExtractErrorMessage(errorBody, response.StatusCode));
        }

        byte[] body = await ReadLibraryBodyAsync(response.Content, ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (root.TryGetProperty("visibility", out JsonElement visibility)
                && !(visibility.ValueKind == JsonValueKind.String && string.Equals(visibility.GetString(), "public", StringComparison.OrdinalIgnoreCase)))
                return null;
            VoiceLibraryVoice? voice = ParseLibraryVoice(root);
            // The answer must be the voice that was asked for.
            return voice is not null && string.Equals(voice.VoiceId, voiceId.Trim(), StringComparison.Ordinal) ? voice : null;
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("Fish Audio's voice answer was not JSON.", ex);
        }
    }

    private static async Task<byte[]> ReadLibraryBodyAsync(HttpContent content, CancellationToken ct)
    {
        await using Stream source = await content.ReadAsStreamAsync(ct);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[16384];
        int read;
        while ((read = await source.ReadAsync(chunk.AsMemory(), ct)) > 0)
        {
            if (buffer.Length + read > MaxLibraryBodyBytes)
                throw new HttpRequestException("Fish Audio's voice listing exceeded the size limit.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static VoiceLibraryVoice? ParseLibraryVoice(JsonElement item)
    {
        string? id = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("_id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(id)) return null;
        string title = item.TryGetProperty("title", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? id : id;
        bool hasSample = item.TryGetProperty("samples", out JsonElement samples) && samples.ValueKind == JsonValueKind.Array && samples.GetArrayLength() > 0;
        return new VoiceLibraryVoice(id, title, StringArray(item, "languages"), StringArray(item, "tags"), hasSample);
    }

    private static List<string> StringArray(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];

    /// <summary>True when <c>REELBOLT_VENDOR_DELETES_DISABLED</c> is exactly <c>true</c> (any case); then no vendor delete is sent.</summary>
    public static bool VendorDeletesDisabled =>
        string.Equals(Environment.GetEnvironmentVariable("REELBOLT_VENDOR_DELETES_DISABLED"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>DELETE /model/{id}</c>. A 2xx, or a 404 for a model that is already gone, counts as deleted;
    /// any other status is reported as not deleted rather than thrown, so the caller can still remove
    /// its own row and report the vendor outcome honestly.
    /// </summary>
    public async Task<bool> DeleteVoiceAsync(string remoteVoiceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(remoteVoiceId))
            return false;

        // Environments that run on a COPY of production data (tools/local-staging) share the real
        // vendor voice ids and the real key with prod, so a delete made there would destroy a live
        // prod voice at the vendor. REELBOLT_VENDOR_DELETES_DISABLED=true refuses every vendor
        // delete here, before any request is built. Unset (the default everywhere else) changes nothing.
        if (VendorDeletesDisabled)
            return false;

        try
        {
            if (await DetectCloningApiAsync(ct) == CloningApi.SelfHostedReferences)
                return await DeleteSelfHostedReferenceAsync(remoteVoiceId, ct);

            var endpoint = _httpClient.BaseAddress?.ToString() ?? "https://api.fish.audio";
            var url = new Uri(endpoint).ToString().TrimEnd('/') + "/model/" + Uri.EscapeDataString(remoteVoiceId);
            using HttpResponseMessage response = await _httpClient.DeleteAsync(url, ct);
            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<VoiceCloningRequirements> GetRequirementsAsync(CancellationToken ct) =>
        await DetectCloningApiAsync(ct) == CloningApi.SelfHostedReferences
            ? VoiceCloningRequirements.SelfHostedFishSpeech
            : VoiceCloningRequirements.Hosted;

    /// <summary>
    /// Hosted or self-hosted? The hosted API's own host (or no endpoint at all) is hosted without a
    /// request. Any other endpoint is probed once with <c>GET /v1/references/list</c>, the
    /// self-hosted fish-speech server's reference-library listing, which the hosted API does not
    /// serve: a 2xx JSON answer carrying a <c>reference_ids</c> array means a self-hosted server;
    /// any other HTTP answer means the hosted <c>/model</c> API (a proxy in front of
    /// api.fish.audio, say). A transport failure propagates and is not remembered.
    /// </summary>
    internal async Task<CloningApi> DetectCloningApiAsync(CancellationToken ct)
    {
        var known = (CloningApi)Volatile.Read(ref _cloningApi);
        if (known != CloningApi.Unknown)
            return known;

        Uri? baseAddress = _httpClient.BaseAddress;
        CloningApi detected = CloningApi.HostedModel;
        if (baseAddress is not null && !string.Equals(baseAddress.Host, HostedApiHost, StringComparison.OrdinalIgnoreCase))
        {
            using var probe = new HttpRequestMessage(HttpMethod.Get, SelfHostedUrl("/v1/references/list"));
            probe.Headers.Accept.ParseAdd("application/json");
            using HttpResponseMessage response = await _httpClient.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode)
            {
                string body = await ReadBoundedErrorBodyAsync(response.Content, ct);
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("reference_ids", out JsonElement ids) &&
                        ids.ValueKind == JsonValueKind.Array)
                    {
                        detected = CloningApi.SelfHostedReferences;
                    }
                }
                catch (JsonException)
                {
                    // Not the fish-speech listing. (A listing longer than the 4 KiB read is the one
                    // false negative — roughly a hundred references, far beyond a project library.)
                }
            }
        }

        Volatile.Write(ref _cloningApi, (int)detected);
        return detected;
    }

    /// <summary>
    /// <c>POST /v1/references/add</c> on a self-hosted fish-speech server, as
    /// <c>multipart/form-data</c> with <c>id</c>, <c>audio</c> and <c>text</c> — the server's own
    /// handler signature (<c>tools/server/views.py</c>), which stores the recording as
    /// <c>references/{id}/sample.wav</c> beside a <c>sample.lab</c> transcript. The id is generated
    /// here, never taken from a user. Exactly one WAV recording with a non-empty transcript is
    /// accepted (<see cref="VoiceCloningRequirements.SelfHostedFishSpeech"/>); the controller
    /// checks the same requirements first, so these throws are the second line of defence.
    /// </summary>
    private async Task<CreatedVoice> CreateSelfHostedReferenceAsync(VoiceCloneRequest request, CancellationToken ct)
    {
        if (request.References.Count != 1)
            throw new ArgumentException("A self-hosted fish-speech server takes exactly one reference recording per voice.", nameof(request));
        VoiceReferenceAudio reference = request.References[0];
        if (string.IsNullOrWhiteSpace(reference.Transcript))
            throw new ArgumentException("A self-hosted fish-speech server needs a transcript of the reference recording.", nameof(request));
        if (!IsWav(reference.Bytes))
            throw new ArgumentException("A self-hosted fish-speech server stores the reference as a WAV file; the recording is not WAV.", nameof(request));

        string referenceId = SelfHostedReferenceIdPrefix + Guid.NewGuid().ToString("N");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(referenceId), "id");
        var audio = new ByteArrayContent(reference.Bytes);
        audio.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "audio", "reference.wav");
        form.Add(new StringContent(reference.Transcript.Trim()), "text");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, SelfHostedUrl("/v1/references/add")) { Content = form };
        httpRequest.Headers.Accept.ParseAdd("application/json");
        using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        string body = await ReadBoundedErrorBodyAsync(response.Content, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(ExtractErrorMessage(body, response.StatusCode));

        bool reportedFailure;
        try
        {
            using var doc = JsonDocument.Parse(body);
            reportedFailure = doc.RootElement.ValueKind == JsonValueKind.Object &&
                              doc.RootElement.TryGetProperty("success", out JsonElement ok) &&
                              ok.ValueKind == JsonValueKind.False;
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("The fish-speech server's reference-creation response was not JSON.", ex);
        }

        if (reportedFailure)
            throw new HttpRequestException(ExtractErrorMessage(body, response.StatusCode));

        return new CreatedVoice(referenceId, "ready");
    }

    /// <summary>
    /// <c>DELETE /v1/references/delete</c> with a JSON <c>{"reference_id": …}</c> body (the
    /// server's handler is registered for DELETE, despite the path). 2xx, or 404 for a reference
    /// already gone, is deleted; anything else is reported as not deleted.
    /// </summary>
    private async Task<bool> DeleteSelfHostedReferenceAsync(string referenceId, CancellationToken ct)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Delete, SelfHostedUrl("/v1/references/delete"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new Dictionary<string, string> { ["reference_id"] = referenceId }),
                System.Text.Encoding.UTF8,
                "application/json")
        };
        httpRequest.Headers.Accept.ParseAdd("application/json");
        using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, ct);
        return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound;
    }

    private string SelfHostedUrl(string path) =>
        new Uri(_httpClient.BaseAddress?.ToString() ?? "https://api.fish.audio").ToString().TrimEnd('/') + path;

    /// <summary>A RIFF/WAVE header — the only container this client hands a self-hosted server, which stores whatever it is given as <c>sample.wav</c>.</summary>
    public static bool IsWav(byte[] bytes) =>
        bytes.Length >= 12 &&
        bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
        bytes[8] == (byte)'W' && bytes[9] == (byte)'A' && bytes[10] == (byte)'V' && bytes[11] == (byte)'E';

    /// <summary>
    /// Buffers a success response into memory with an enforced ceiling. Both the declared
    /// Content-Length and the bytes actually arriving are checked, because a hostile endpoint can
    /// simply omit or understate the former — the streaming check is what makes the cap real.
    /// This is the FIRST and ONLY reader of the response body: the request is sent with
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, so nothing upstream of this method
    /// has touched the content stream.
    /// </summary>
    private static async Task<Stream> ReadAudioResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        long? declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength > MaxAudioResponseBytes)
        {
            throw new HttpRequestException(AudioTooLargeMessage(declaredLength.Value));
        }

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
                if (total > MaxAudioResponseBytes)
                {
                    throw new HttpRequestException(AudioTooLargeMessage(total));
                }

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

    /// <summary>
    /// The size-limit message. It names the cap and the offending size and deliberately carries no
    /// part of the payload — the point of the limit is that this string never becomes the 32 MB of
    /// body it is describing. The wording describes what this class actually does: the read is
    /// streamed with ResponseHeadersRead and STOPS at the cap, so the bytes past it were never
    /// retained (it no longer claims the response was never buffered at all, which was false while
    /// the request used the default ResponseContentRead).
    /// </summary>
    private static string AudioTooLargeMessage(long size) =>
        $"Fish Audio returned a {size}-byte response, which exceeds the {MaxAudioResponseBytes}-byte " +
        $"({MaxAudioResponseBytes / (1024 * 1024)} MiB) limit for synthesized audio. Reading stopped at " +
        "the limit; the bytes beyond it were never retained.";

    /// <summary>
    /// Reads at most <see cref="MaxErrorBodyBytes"/> of a non-success body. The remainder is left
    /// unread on the wire rather than materialized.
    /// </summary>
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

    private static string ExtractErrorMessage(string responseBody, System.Net.HttpStatusCode statusCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            // Try to get 'message' field first
            if (root.TryGetProperty("message", out var messageElement))
            {
                var message = messageElement.GetString();
                if (!string.IsNullOrEmpty(message))
                {
                    return $"{Truncate(message)} ({(int)statusCode})";
                }
            }

            // Fall back to 'reason' field
            if (root.TryGetProperty("reason", out var reasonElement))
            {
                var reason = reasonElement.GetString();
                if (!string.IsNullOrEmpty(reason))
                {
                    return $"{Truncate(reason)} ({(int)statusCode})";
                }
            }
        }
        catch
        {
            // JSON parsing failed, use raw body
        }

        // Use raw body as fallback
        return $"{Truncate(responseBody)} ({(int)statusCode})";
    }

    /// <summary>
    /// Bounds any provider-supplied text before it reaches an exception message. Applied to the
    /// parsed fields as well as to the raw body: a <c>message</c> field is exactly as
    /// attacker-controlled as the bytes around it.
    /// </summary>
    private static string Truncate(string value) =>
        value.Length <= MaxErrorMessageChars
            ? value
            : value[..MaxErrorMessageChars] + "…";

    private sealed class FishAudioTtsRequest
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("reference_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ReferenceId { get; set; }

        [JsonPropertyName("language")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Language { get; set; }

        [JsonPropertyName("format")]
        public string Format { get; set; } = "wav";

        [JsonPropertyName("sample_rate")]
        public int SampleRate { get; set; } = 44100;

        [JsonPropertyName("latency")]
        public string Latency { get; set; } = "normal";

        [JsonPropertyName("prosody")]
        public FishAudioProsody Prosody { get; set; } = new();

        [JsonPropertyName("pronunciation_dictionary")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<FishAudioPronunciationDictionary>? PronunciationDictionary { get; set; }
    }

    private sealed class FishAudioPronunciationDictionary
    {
        [JsonPropertyName("items")]
        public List<FishAudioPronunciationItem> Items { get; set; } = new();
    }

    private sealed class FishAudioPronunciationItem
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;

        [JsonPropertyName("case_sensitive")]
        public bool CaseSensitive { get; set; }
    }

    private sealed class FishAudioProsody
    {
        [JsonPropertyName("speed")]
        public double Speed { get; set; } = 1;

        [JsonPropertyName("volume")]
        public int Volume { get; set; } = 0;

        [JsonPropertyName("normalize_loudness")]
        public bool NormalizeLoudness { get; set; } = true;
    }
}
