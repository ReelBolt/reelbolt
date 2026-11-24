using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using ReelBolt.Shared.Moderation;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Builds the <see cref="IContentModerationClient"/> for a resolved provider. The contract is the
/// OpenAI shape (<c>POST {Endpoint.TrimEnd('/')}/moderations</c>), which is also what any
/// OpenAI-compatible moderation gateway speaks; this is the one kind
/// <see cref="Data.Models.InferenceProviderCapability.ContentModeration"/> is served by.
/// </summary>
public sealed class ContentModerationClient : IContentModerationClient
{
    /// <summary>
    /// Shared by every org-owned moderation row: the endpoint is on each request, not on the client, so one
    /// public-only, redirect-free client serves them all (see <see cref="ProviderRowTransport"/>).
    /// </summary>
    private static readonly HttpClient OrgRowHttpClient = ProviderRowTransport.CreateSdkHttpClient();

    private readonly OpenAICompatibleContentModerationClient _inner;

    /// <summary>
    /// Builds a client for <paramref name="provider"/> using the moderation defaults (pinned model
    /// override, 8-second call timeout) — deliberately NOT the provider row's general timeout,
    /// which is sized for generation calls and would let a failed moderation stall an upload.
    /// </summary>
    public ContentModerationClient(ResolvedContentModerationProvider provider, HttpClient? httpClient = null)
        : this(
            provider,
            new ContentModerationOptions { Model = provider.ModelName },
            httpClient)
    {
    }

    public ContentModerationClient(
        ResolvedContentModerationProvider provider,
        ContentModerationOptions options,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _inner = new OpenAICompatibleContentModerationClient(
            provider.Endpoint,
            provider.ApiKey,
            options,
            httpClient ?? (ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged)
                ? OrgRowHttpClient
                : null));
    }

    public Task<ModerationClassification> ClassifyAsync(ModerationClassifyRequest request, CancellationToken ct) =>
        _inner.ClassifyAsync(request, ct);
}

/// <summary>
/// The raw OpenAI-compatible moderation client: plain <c>HttpClient</c> + <c>System.Text.Json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every request carries exactly ONE input: text is <c>{"model": ..., "input": "&lt;text&gt;"}</c>
/// and an image is <c>{"model": ..., "input": [{"type":"image_url","image_url":{"url":
/// "data:image/jpeg;base64,..."}}]}</c>. The vendor accepts an image only as the single input of
/// its own request, so an image is never batched with another image or with the transcript. An
/// image plus text becomes two requests; the verdicts are unioned.
/// </para>
/// <para>
/// Response union: <c>flagged</c> is OR-ed, category scores keep their maximum, flagged categories
/// are OR-ed, and applied input types are unioned — so the policy sees the whole request's worst
/// signal rather than an arbitrary per-part result.
/// </para>
/// <para>
/// Failure discipline mirrors <c>GuardrailScreener</c>: a short linked timeout turns into
/// <see cref="ContentModerationUnavailableException"/>, but a CALLER cancellation is re-thrown so a
/// stopped upload/step is never reported (or treated) as a moderation outage. A non-success response
/// surfaces a bounded, redacted summary of the vendor's <c>error.type/code/param</c> and message —
/// never the request or any classified content.
/// </para>
/// </remarks>
public sealed class OpenAICompatibleContentModerationClient : IContentModerationClient
{
    private const int MaxErrorBodyBytes = 4 * 1024;
    private const int MaxSuccessBodyBytes = 1024 * 1024;
    private const long DefaultMaxImageBytes = 20L * 1024 * 1024;

    /// <summary>
    /// Cap on the whole vendor-error summary as it reaches the exception. Structured fields are
    /// redacted before it; free text never reaches it, because free text is never echoed.
    /// </summary>
    private const int MaxErrorSnippetChars = 300;

    /// <summary>
    /// How many one-input calls may be in flight at once. Bounds the fan-out while keeping a
    /// 20-frame video to a handful of round trips instead of twenty.
    /// </summary>
    private const int MaxConcurrency = 4;

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly ContentModerationOptions _options;
    private readonly int _maxTextChars;
    private readonly int _maxImages;
    private readonly long _maxImageBytes;

    public OpenAICompatibleContentModerationClient(
        string endpoint,
        string apiKey,
        ContentModerationOptions? options = null,
        HttpClient? httpClient = null)
    {
        _endpoint = (endpoint ?? string.Empty).TrimEnd('/');
        _apiKey = apiKey ?? string.Empty;
        _options = options ?? new ContentModerationOptions();
        _httpClient = httpClient ?? SharedHttpClient;
        _maxTextChars = Math.Max(1, _options.MaxTextChars);
        _maxImages = Math.Max(0, _options.MaxImagesPerRequest);
        _maxImageBytes = _options.MaxImageBytes > 0 ? _options.MaxImageBytes : DefaultMaxImageBytes;
    }

    public async Task<ModerationClassification> ClassifyAsync(ModerationClassifyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        string text = (request.Text ?? string.Empty).Trim();
        IReadOnlyList<ModerationImageInput> images = request.Images ?? [];

        // Nothing to classify is NOT "clean". Returning an empty Allow here would let a caller that
        // failed to sample any frames (or an empty prompt) pass the publish gate. Unavailable makes
        // the gate apply its own stance: fail open on generation/ingest/text, fail closed at publish.
        if (text.Length == 0 && images.Count == 0)
            throw new ContentModerationUnavailableException("There is no content to classify.");

        // Same cap the wire body has always applied: further frames are dropped.
        IReadOnlyList<ModerationImageInput> cappedImages = images.Take(_maxImages).ToArray();
        string model = string.IsNullOrWhiteSpace(request.Model) ? _options.Model : request.Model;

        foreach (ModerationImageInput image in cappedImages)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (image.Bytes.LongLength > _maxImageBytes)
            {
                throw new ArgumentException(
                    $"A moderation image may be at most {_maxImageBytes} bytes; this one is {image.Bytes.LongLength}.",
                    nameof(ModerationClassifyRequest.Images));
            }
        }

        if (text.Length == 0 && cappedImages.Count == 0)
            throw new ContentModerationUnavailableException("There is no content to classify.");

        // One input per request: the vendor accepts an image only as the sole input of its own
        // request, so every image is classified alone and the transcript is its own text-only call.
        // Input order is text first, then images, so the union's input types stay stable.
        List<Func<CancellationToken, Task<ModerationClassification>>> calls =
            new(cappedImages.Count + 1);
        if (text.Length > 0)
            calls.Add(token => SendTextAsync(model, text, token));
        foreach (ModerationImageInput image in cappedImages)
        {
            ModerationImageInput captured = image;
            calls.Add(token => SendImageAsync(model, captured, token));
        }

        return await FanOutAsync(calls, ct).ConfigureAwait(false);
    }

    private Task<ModerationClassification> SendTextAsync(string model, string text, CancellationToken ct)
    {
        string capped = text.Length > _maxTextChars ? text[.._maxTextChars] : text;

        // A plain string, not a {"type":"text"} object: the string is the base documented input
        // shape of every OpenAI-compatible moderation gateway, and the object form only exists for
        // mixing modalities — which we no longer do.
        return SendAsync(model, capped, ct);
    }

    private Task<ModerationClassification> SendImageAsync(
        string model, ModerationImageInput image, CancellationToken ct)
    {
        string mime = string.IsNullOrWhiteSpace(image.ContentType) ? "image/jpeg" : image.ContentType;
        string dataUrl = $"data:{mime};base64,{Convert.ToBase64String(image.Bytes)}";

        return SendAsync(
            model,
            new[] { new { type = "image_url", image_url = new { url = dataUrl } } },
            ct);
    }

    /// <summary>
    /// Sends one request whose <c>input</c> is exactly one element: <paramref name="input"/> is the
    /// transcript string or the one-element image array. Failures are surfaced as a
    /// <see cref="ModerationRequestFailedException"/> carrying the HTTP status (0 when the call
    /// timed out or never connected).
    /// </summary>
    private async Task<ModerationClassification> SendAsync<T>(string model, T input, CancellationToken ct)
    {
        string body = JsonSerializer.Serialize(new { model, input });

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.EffectiveTimeoutSeconds));

        try
        {
            using HttpRequestMessage httpRequest = new(HttpMethod.Post, new Uri(_endpoint + "/moderations"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            if (_apiKey.Length > 0)
                httpRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");

            using HttpResponseMessage response = await _httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Never put the vendor body (or an attacker-controlled error payload) in the
                // exception: read a hard-bounded prefix and surface a bounded, redacted summary of
                // the vendor's error object — never the request or any classified content.
                int status = (int)response.StatusCode;
                string prefix = await ReadBoundedAsync(response.Content, MaxErrorBodyBytes, timeout.Token)
                    .ConfigureAwait(false);
                throw new ModerationRequestFailedException(
                    status, $"The moderation endpoint answered {status}: {SummarizeError(prefix)}");
            }

            string responseBody = await ReadBoundedAsync(response.Content, MaxSuccessBodyBytes, timeout.Token)
                .ConfigureAwait(false);
            return Parse(responseBody, expectedResults: 1);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A cancelled upload/step is not a moderation failure; the owner of the token reports it.
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new ModerationRequestFailedException(
                0, $"The moderation call timed out after {_options.EffectiveTimeoutSeconds}s.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ModerationRequestFailedException(
                0, "The moderation endpoint could not be reached.", ex);
        }
    }

    /// <summary>
    /// Runs the one-input calls with at most <see cref="MaxConcurrency"/> in flight and unions the
    /// verdicts in input order. The first real failure stops the fan-out: no further queued call
    /// starts, and the failure is surfaced as the public exception (carrying the vendor summary).
    /// </summary>
    private async Task<ModerationClassification> FanOutAsync(
        IReadOnlyList<Func<CancellationToken, Task<ModerationClassification>>> calls,
        CancellationToken ct)
    {
        ModerationClassification?[] results = new ModerationClassification?[calls.Count];
        Exception? failure = null;
        object sync = new();
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, calls.Count),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxConcurrency,
                    CancellationToken = stop.Token
                },
                async (index, token) =>
                {
                    try
                    {
                        results[index] = await calls[index](token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // A sibling failed and cancelled the rest; the first real failure governs.
                    }
                    catch (Exception ex)
                    {
                        lock (sync)
                        {
                            failure ??= ex;
                        }

                        stop.Cancel();
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The loop observed stop.Cancel() after a sibling failed.
        }

        // A cancelled upload/step is not a moderation failure; the owner of the token reports it.
        ct.ThrowIfCancellationRequested();

        if (failure is ModerationRequestFailedException requestFailure)
            throw ToPublicFailure(requestFailure);

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();

        return UnionResults(results);
    }

    /// <summary>
    /// Unions per-call classifications in input order: <c>Flagged</c> is OR-ed, scores keep the
    /// per-category maximum, flagged categories are OR-ed, and applied input types are unioned
    /// preserving first-seen order.
    /// </summary>
    private static ModerationClassification UnionResults(IReadOnlyList<ModerationClassification?> ordered)
    {
        bool flagged = false;
        Dictionary<string, double> scores = new(StringComparer.Ordinal);
        Dictionary<string, bool> categories = new(StringComparer.Ordinal);
        List<string> inputTypes = [];
        HashSet<string> seenInputTypes = new(StringComparer.Ordinal);

        foreach (ModerationClassification? classification in ordered)
        {
            if (classification is null) continue;

            flagged |= classification.Flagged;

            foreach (KeyValuePair<string, double> score in classification.Scores)
            {
                scores[score.Key] = scores.TryGetValue(score.Key, out double existing)
                    ? Math.Max(existing, score.Value)
                    : score.Value;
            }

            foreach (KeyValuePair<string, bool> category in classification.FlaggedCategories)
            {
                categories[category.Key] = categories.TryGetValue(category.Key, out bool existing)
                    ? existing || category.Value
                    : category.Value;
            }

            foreach (string type in classification.InputTypes)
            {
                if (seenInputTypes.Add(type))
                    inputTypes.Add(type);
            }
        }

        return new ModerationClassification(flagged, scores, categories, inputTypes);
    }

    private static ContentModerationUnavailableException ToPublicFailure(ModerationRequestFailedException failure) =>
        new(failure.Message, failure.InnerException ?? failure);

    /// <summary>
    /// A failed attempt. <see cref="StatusCode"/> is the HTTP status, or 0 for a timeout/transport
    /// failure. Kept private so the public contract stays <see cref="ContentModerationUnavailableException"/>.
    /// </summary>
    private sealed class ModerationRequestFailedException : Exception
    {
        public ModerationRequestFailedException(int statusCode, string message, Exception? innerException = null)
            : base(message, innerException) => StatusCode = statusCode;

        public int StatusCode { get; }
    }

    private static ModerationClassification Parse(string json, int expectedResults)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("results", out JsonElement results)
                || results.ValueKind != JsonValueKind.Array)
            {
                throw new ContentModerationUnavailableException(
                    "The moderation response has no results[] array.");
            }

            // The API answers one result per input. Anything less means part of what we sent was not
            // classified; unioning the rest and calling it clean would hide exactly that.
            int returned = results.GetArrayLength();
            if (returned == 0)
                throw new ContentModerationUnavailableException(
                    "The moderation response has an empty results[] array.");
            if (expectedResults > 0 && returned < expectedResults)
                throw new ContentModerationUnavailableException(
                    $"The moderation response classified {returned} of {expectedResults} inputs.");

            bool flagged = false;
            Dictionary<string, double> scores = new(StringComparer.Ordinal);
            Dictionary<string, bool> categories = new(StringComparer.Ordinal);
            HashSet<string> inputTypes = new(StringComparer.Ordinal);

            foreach (JsonElement result in results.EnumerateArray())
            {
                if (result.ValueKind != JsonValueKind.Object)
                    continue;

                if (result.TryGetProperty("flagged", out JsonElement flaggedElement)
                    && flaggedElement.ValueKind is JsonValueKind.True)
                {
                    flagged = true;
                }

                if (result.TryGetProperty("category_scores", out JsonElement scoreElement)
                    && scoreElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty score in scoreElement.EnumerateObject())
                    {
                        if (score.Value.ValueKind != JsonValueKind.Number || !score.Value.TryGetDouble(out double value))
                            continue;

                        scores[score.Name] = scores.TryGetValue(score.Name, out double existing)
                            ? Math.Max(existing, value)
                            : value;
                    }
                }

                if (result.TryGetProperty("categories", out JsonElement categoryElement)
                    && categoryElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty category in categoryElement.EnumerateObject())
                    {
                        bool isFlagged = category.Value.ValueKind is JsonValueKind.True;
                        categories[category.Name] = categories.TryGetValue(category.Name, out bool existing)
                            ? existing || isFlagged
                            : isFlagged;
                    }
                }

                if (result.TryGetProperty("category_applied_input_types", out JsonElement typesElement)
                    && typesElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty types in typesElement.EnumerateObject())
                    {
                        if (types.Value.ValueKind != JsonValueKind.Array) continue;
                        foreach (JsonElement type in types.Value.EnumerateArray())
                        {
                            if (type.ValueKind == JsonValueKind.String)
                                inputTypes.Add(type.GetString()!);
                        }
                    }
                }
            }

            return new ModerationClassification(flagged, scores, categories, [.. inputTypes]);
        }
        catch (JsonException ex)
        {
            throw new ContentModerationUnavailableException("The moderation response was not valid JSON.", ex);
        }
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> from <paramref name="content"/>. A vendor error
    /// body is attacker-influenced input; it is never read whole and never reaches the exception.
    /// </summary>
    private static async Task<string> ReadBoundedAsync(
        HttpContent content, int maxBytes, CancellationToken ct)
    {
        await using Stream stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = new byte[maxBytes];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)
                .ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    /// <summary>
    /// Summarises a bounded non-success body. Only structured vocabulary is surfaced:
    /// <c>error.type</c>, <c>error.code</c>, <c>error.param</c> and the HTTP status. They are
    /// bounded vocabularies and they name almost every real cause (<c>invalid_value</c>,
    /// <c>invalid_image</c>, <c>unsupported_value</c>). A free-text value (<c>error.message</c>, a
    /// gateway's top-level <c>message</c>/<c>detail</c>/<c>error_description</c>, or
    /// <c>error</c> as a string) is NEVER echoed: it becomes <c>message omitted (N chars)</c>.
    /// The first message-bearing field is the one reported; there is no sibling fallback. A
    /// non-JSON body, or a JSON body with no recognised error field, becomes a fixed marker.
    /// </summary>
    /// <remarks>
    /// The invariant is absolute: no vendor free text ever leaves this type. A vendor 400 can
    /// reflect the request — a transcript, a data URL, a JSON body — and plain prose is
    /// indistinguishable from a genuine vendor sentence by shape, so free text is dropped rather
    /// than filtered. The structured fields are still enough to name the cause.
    /// </remarks>
    private static string SummarizeError(string body)
    {
        string snippet;
        if (string.IsNullOrWhiteSpace(body))
        {
            snippet = "(empty)";
        }
        else
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                snippet = SummarizeErrorElement(document.RootElement);
            }
            catch (JsonException)
            {
                // Never echo a non-JSON body: a proxy or a request-echoing gateway can put classified
                // content (a transcript, a data URL) there, and the redactor recognises credentials,
                // not prose or base64. The status already says what kind of failure it was.
                snippet = "(non-JSON error body)";
            }
        }

        if (snippet.Length == 0) snippet = "(empty)";
        // Every field below is redacted BEFORE it is bounded, so a credential can never be split by
        // a truncation into a fragment the redactor no longer recognises. This last bound only
        // trims a summary that is already secret-free.
        if (snippet.Length > MaxErrorSnippetChars) snippet = snippet[..MaxErrorSnippetChars];
        return snippet;
    }

    private static string SummarizeErrorElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return "(unrecognised error body)";

        if (!root.TryGetProperty("error", out JsonElement error))
        {
            // A compatible gateway may use a top-level message field instead of OpenAI's error
            // object. The FIRST message-bearing field governs; there is no sibling fallback.
            foreach (string property in TopLevelMessageFields)
            {
                if (root.TryGetProperty(property, out JsonElement value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    return OmitMessage(value.GetString(), standalone: true);
                }
            }

            return "(unrecognised error body)";
        }

        if (error.ValueKind == JsonValueKind.String)
            return OmitMessage(error.GetString(), standalone: true);

        if (error.ValueKind != JsonValueKind.Object)
            return "(unrecognised error body)";

        List<string> parts = [];
        AddErrorField(parts, "type", error, "type");
        AddErrorField(parts, "code", error, "code");
        AddErrorField(parts, "param", error, "param");

        if (error.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String)
        {
            string omitted = OmitMessage(message.GetString(), standalone: false);
            if (omitted.Length > 0) parts.Add($"message={omitted}");
        }

        return parts.Count == 0 ? "(error body with no recognised fields)" : string.Join(' ', parts);
    }

    /// <summary>Top-level fields a non-OpenAI gateway uses for its human-readable reason.</summary>
    private static readonly string[] TopLevelMessageFields = ["message", "detail", "error_description"];

    /// <summary>
    /// Never echoes vendor free text: it returns a length marker instead. The length is of the
    /// whitespace-collapsed, control-stripped value, so it describes what would have been sent
    /// without reproducing any of it.
    /// </summary>
    private static string OmitMessage(string? value, bool standalone)
    {
        string text = CollapseWhitespace(value ?? string.Empty);
        if (text.Length == 0) return string.Empty;
        return standalone ? $"message omitted ({text.Length} chars)" : $"omitted ({text.Length} chars)";
    }

    /// <summary>Collapses whitespace, strips control characters, then redacts — never truncate first.</summary>
    private static string RedactAndCollapse(string? value) =>
        DesignSystems.Importers.SecretRedactor.Redact(CollapseWhitespace(value ?? string.Empty)).text;

    private static void AddErrorField(List<string> parts, string label, JsonElement error, string property)
    {
        if (error.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            string text = RedactAndCollapse(value.GetString());
            if (text.Length > 0) parts.Add($"{label}={text}");
        }
    }

    /// <summary>
    /// One line, no control characters (an ANSI escape or a raw newline must not forge a log line),
    /// before any redaction or bounding.
    /// </summary>
    private static string CollapseWhitespace(string value)
    {
        StringBuilder printable = new(value.Length);
        foreach (char character in value)
            printable.Append(char.IsControl(character) ? ' ' : character);

        return string.Join(' ', printable.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
