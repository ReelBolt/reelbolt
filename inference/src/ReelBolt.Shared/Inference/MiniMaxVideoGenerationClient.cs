namespace ReelBolt.Shared.Inference;

/// <summary>
/// Client for MiniMax video generation API (https://api.minimax.io).
/// Implements the v2 API endpoints for submitting, polling, and downloading video generation tasks.
/// </summary>
public class MiniMaxVideoGenerationClient : IVideoGenerationClient, IVideoImageInputSupport, IVideoReferenceInputSupport
{
    /// <summary>Default bound on <see cref="ProbeAsync"/> so an unreachable endpoint cannot hang an admin request.</summary>
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// MiniMax's documented per-image ceiling (30 MB). Read as decimal (30,000,000 bytes) rather than
    /// as 30 MiB: where a vendor writes "MB" and means one of the two, the smaller byte count is the
    /// only reading that cannot exceed what it published.
    /// </summary>
    public const long MaxImageBytes = 30_000_000;

    /// <summary>MiniMax's documented whole-request-body ceiling (64 MB), read as decimal bytes for the same reason as <see cref="MaxImageBytes"/>.</summary>
    public const long MaxRequestBodyBytes = 64_000_000;

    /// <summary>The low end of MiniMax's documented per-side pixel range for an input image.</summary>
    public const int MinImageSidePixels = 256;

    /// <summary>The high end of MiniMax's documented per-side pixel range for an input image.</summary>
    public const int MaxImageSidePixels = 5760;

    /// <summary>The low end of MiniMax's documented aspect-ratio (w/h) range for an input image.</summary>
    public const double MinImageAspectRatio = 0.4;

    /// <summary>The high end of MiniMax's documented aspect-ratio (w/h) range for an input image.</summary>
    public const double MaxImageAspectRatio = 2.5;

    private const string QueryPathPrefix = "/v2/query/video_generation/";
    private const int MaxVendorMessageChars = 160;

    /// <summary>
    /// The image media types MiniMax documents for an <c>image_url</c> content item (JPG, JPEG, PNG,
    /// WEBP, HEIC, HEIF). Matched ordinally against the lower-cased content type — the vendor's own
    /// data-URI token is lower case.
    /// </summary>
    private static readonly HashSet<string> SupportedImageContentTypes = new(StringComparer.Ordinal)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/webp",
        "image/heic",
        "image/heif"
    };

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly TimeSpan _probeTimeout;
    private readonly HttpClient _downloadClient;

    public MiniMaxVideoGenerationClient(HttpClient httpClient, string apiKey, TimeSpan? probeTimeout = null, HttpClient? downloadClient = null)
    {
        _downloadClient = downloadClient ?? httpClient;
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _probeTimeout = probeTimeout ?? DefaultProbeTimeout;
    }

    public async Task<ExternalJobHandle> SubmitAsync(VideoGenerationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ---------------------------------------------------------------------------------------
        // The submit request body, from the vendor's own primary source — the OpenAPI schema for
        // POST /v2/video_generation (fetched 2026-10-09):
        // https://platform.minimax.io/docs/api-reference/video-generation-v2-create.md
        //
        // "content" is an ARRAY of multimodal items. An image item is
        //
        //   { "type": "image_url", "image_url": { "url": <uri> }, "role": "first_frame" }
        //
        // — the NESTED image_url OBJECT is the shape, not a flat string — with role one of
        // first_frame / last_frame, and url one of a public URL, mm_file://{file_id} or a
        // "data:image/<format>;base64,<Base64>" data URI (<format> lower case). A request carrying
        // BOTH a reference role and a first/last-frame role is rejected by the vendor (the two
        // scenarios are mutually exclusive); this client sends either the first/last-frame pair or
        // reference items (VideoGenerationRequest.References, avatars phase 2), never both.
        //
        // REQUEST BODY SIZE. The documented ceilings are 30 MB per image and 64 MB for the whole
        // body. Both are enforced HERE, locally, before anything is sent: a body the vendor would
        // refuse must fail with a reason that names the limit rather than as an opaque vendor 4xx
        // after the reservation. Base64 inflates a payload by about a third, which is exactly why the
        // whole-body check is on the serialized body and not on the sum of the raw images.
        //
        // RATIO IS OMITTED FOR EVERY IMAGE REQUEST, DELIBERATELY. The vendor documents the
        // image-to-video scenario as "the aspect ratio is determined by the input image and ratio is
        // always adaptive; passing another valid value does not error but is ignored and treated as
        // adaptive", and text-to-video as "ratio is required and cannot be adaptive". So the ratio
        // this request carries is sent on the text-only path and NOT on the image path: sending one
        // the vendor ignores would be inventing agreement with a value it never reads. This is the
        // only member whose presence depends on the images, and the no-image body below stays
        // byte-identical to the one this client has always sent.
        // ---------------------------------------------------------------------------------------
        int imageCount = request.Images?.Count ?? 0;
        int referenceCount = request.References?.Count ?? 0;

        // The model's own declaration decides, before anything is built — and only when there is an
        // image to carry, so a request with no image keeps the legacy path EXACTLY: not even the model
        // name is checked here beyond what has always been checked (nothing), which is what makes the
        // no-image body and its error behaviour byte-identical to the shape this client always sent.
        if (imageCount > 0)
        {
            VideoImageInput.Validate(ImageInputSupport(request.Model), request.Model, imageCount);
        }

        // Reference mode (docs/design/avatars.md "Vendor facts" M1..M6): checked against the model's declaration
        // here too, so a caller that skipped the pre-reserve check still cannot send what the vendor would refuse.
        if (referenceCount > 0)
        {
            EnsureReferencesAccepted(request.Model, request.References!, imageCount > 0);
        }

        var contentItems = new List<Dictionary<string, object?>>(imageCount + referenceCount + 1)
        {
            new(StringComparer.Ordinal)
            {
                ["type"] = "text",
                ["text"] = request.PromptText
            }
        };

        if (request.Images is { Count: > 0 } images)
        {
            for (int index = 0; index < images.Count; index++)
            {
                // Images[0] is the first frame, Images[1] the last frame — the one convention all
                // three video clients share (see VideoImageInputSupport).
                contentItems.Add(BuildImageContentItem(images[index], index == 0 ? "first_frame" : "last_frame"));
            }
        }

        if (request.References is { Count: > 0 } references)
        {
            foreach (VideoGenerationReference reference in references)
            {
                contentItems.Add(BuildReferenceContentItem(reference));
            }
        }

        // A Dictionary rather than an anonymous type only because the member set now varies; its
        // insertion order is the serialized order, and with no image it reproduces the legacy body
        // exactly: {"model":…,"content":[{"type":"text","text":…}],"duration":…,"resolution":…,"ratio":…}
        var requestBody = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Model,
            ["content"] = contentItems,
            ["duration"] = request.DurationSeconds,
            ["resolution"] = request.Resolution
        };

        // Reference mode is not frame-based, so the aspect is not taken from an input frame and the ratio is
        // sent, as for text-to-video. UNVERIFIED (no live call): whether the vendor reads it in reference mode.
        if (imageCount == 0)
        {
            requestBody["ratio"] = request.Ratio;
        }

        string json = System.Text.Json.JsonSerializer.Serialize(requestBody);
        EnsureBodyWithinLimit(System.Text.Encoding.UTF8.GetByteCount(json), nameof(request));

        using var content = new StringContent(
            json,
            System.Text.Encoding.UTF8,
            "application/json");

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v2/video_generation")
        {
            Content = content
        };
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _httpClient.SendAsync(httpRequest, ct);

        ThrowOnFailure(response);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var jsonDoc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);

        string taskId = jsonDoc.RootElement.GetProperty("task_id").GetString()
            ?? throw new InvalidOperationException("Response missing task_id");

        return new ExternalJobHandle(taskId);
    }

    /// <summary>
    /// One documented image content item, with every limit the vendor publishes checked locally.
    /// <para>
    /// The check order is deliberate: media type, then byte length, then the pixel header, then the
    /// published pixel range and aspect range. Each failure is an <see cref="ArgumentException"/>
    /// naming the offending value and the vendor's documented bound, so a caller sees a reason rather
    /// than a vendor 4xx.
    /// </para>
    /// <para>
    /// <b>HEIC/HEIF are refused, and that is a deliberate gap rather than an oversight.</b> MiniMax
    /// accepts them, but this repository reads pixel headers from PNG, JPEG and WebP only
    /// (<see cref="ImageHeader"/>), so an image whose size cannot be checked cannot be checked
    /// against the two documented limits that depend on it. Sending it anyway would silently skip a
    /// documented guard; the refusal names the reason and the format that could not be read.
    /// </para>
    /// </summary>
    /// <param name="image">The raw bytes and media type.</param>
    /// <param name="role">The vendor's own role token for this position: <c>first_frame</c> or <c>last_frame</c>.</param>
    private static Dictionary<string, object?> BuildImageContentItem(VideoGenerationImageInput image, string role)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (string.IsNullOrWhiteSpace(image.ContentType))
        {
            throw new ArgumentException("An input image requires a content type.", nameof(image));
        }

        string mediaType = image.ContentType.Trim().ToLowerInvariant();
        if (!SupportedImageContentTypes.Contains(mediaType))
        {
            throw new ArgumentException(
                $"Unsupported input image type '{mediaType}'. MiniMax documents JPG, JPEG, PNG, WEBP, HEIC and HEIF.",
                nameof(image));
        }

        if (image.Bytes is not { Length: > 0 })
        {
            throw new ArgumentException("An input image carries no bytes.", nameof(image));
        }

        if (image.Bytes.LongLength > MaxImageBytes)
        {
            throw new ArgumentException(
                $"The input image is {image.Bytes.LongLength} bytes, over MiniMax's documented " +
                $"{MaxImageBytes}-byte (30 MB) per-file limit.",
                nameof(image));
        }

        if (!ImageHeader.TryReadDimensions(image.Bytes, out int width, out int height))
        {
            throw new ArgumentException(
                "The input image's pixel size cannot be read locally: ReelBolt's header reader covers PNG, " +
                "JPEG and WebP, and MiniMax also accepts HEIC and HEIF. MiniMax documents " +
                $"{MinImageSidePixels}..{MaxImageSidePixels} px per side and an aspect ratio of " +
                $"{MinImageAspectRatio}..{MaxImageAspectRatio}, so an image whose own size cannot be read " +
                "cannot be checked against those limits and is refused rather than sent unchecked.",
                nameof(image));
        }

        if (width < MinImageSidePixels || width > MaxImageSidePixels
            || height < MinImageSidePixels || height > MaxImageSidePixels)
        {
            throw new ArgumentException(
                $"The input image is {width}x{height} px, outside MiniMax's documented " +
                $"{MinImageSidePixels}..{MaxImageSidePixels} px range per side.",
                nameof(image));
        }

        double aspectRatio = (double)width / height;
        if (aspectRatio < MinImageAspectRatio || aspectRatio > MaxImageAspectRatio)
        {
            throw new ArgumentException(
                $"The input image is {width}x{height} px (aspect ratio {aspectRatio:0.###}), outside " +
                $"MiniMax's documented {MinImageAspectRatio}..{MaxImageAspectRatio} aspect-ratio range.",
                nameof(image));
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "image_url",
            ["image_url"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["url"] = $"data:image/{DataUriFormat(mediaType)};base64,{Convert.ToBase64String(image.Bytes)}"
            },
            ["role"] = role
        };
    }

    /// <summary>
    /// The reference media types this client sends (docs/design/avatars.md M4..M6): the avatar limits
    /// (<see cref="Data.Models.ProjectAvatarLimits"/>) already keep pictures to JPEG/PNG/WebP, videos to MP4/MOV and
    /// audio to WAV/MP3, so these are the vendor's documented formats minus HEIC/HEIF.
    /// </summary>
    private static readonly Dictionary<string, Data.Models.AvatarReferenceKind> SupportedReferenceContentTypes = new(StringComparer.Ordinal)
    {
        ["image/jpeg"] = Data.Models.AvatarReferenceKind.Image,
        ["image/jpg"] = Data.Models.AvatarReferenceKind.Image,
        ["image/png"] = Data.Models.AvatarReferenceKind.Image,
        ["image/webp"] = Data.Models.AvatarReferenceKind.Image,
        ["video/mp4"] = Data.Models.AvatarReferenceKind.Video,
        ["video/quicktime"] = Data.Models.AvatarReferenceKind.Video,
        ["audio/wav"] = Data.Models.AvatarReferenceKind.Audio,
        ["audio/x-wav"] = Data.Models.AvatarReferenceKind.Audio,
        ["audio/wave"] = Data.Models.AvatarReferenceKind.Audio,
        ["audio/mpeg"] = Data.Models.AvatarReferenceKind.Audio,
        ["audio/mp3"] = Data.Models.AvatarReferenceKind.Audio,
    };

    /// <summary>MiniMax's documented per-video ceiling for a reference video (50 MB, M5), read as decimal bytes.</summary>
    public const long MaxReferenceVideoBytes = 50_000_000;

    /// <summary>MiniMax's documented per-file ceiling for reference audio (15 MB, M6), read as decimal bytes.</summary>
    public const long MaxReferenceAudioBytes = 15_000_000;

    /// <summary>Shortest reference video or audio MiniMax documents (2 s, M5/M6).</summary>
    public const int MinReferenceMediaMs = 2_000;

    /// <summary>Longest single reference video or audio, and the longest total per kind (15 s, M5/M6).</summary>
    public const int MaxReferenceMediaMs = 15_000;

    /// <summary>
    /// The request-level rules of reference mode: the model's declaration (counts, totals, files), no frame
    /// alongside (M3), and each item's documented type, size and length. Throws <see cref="NotSupportedException"/>
    /// for a model or combination the vendor does not take and <see cref="ArgumentException"/> for a bad item.
    /// </summary>
    private void EnsureReferencesAccepted(string model, IReadOnlyList<VideoGenerationReference> references, bool hasFrames)
    {
        VideoReferenceInputSupport support = ReferenceInputSupport(model);
        if (VideoReferenceInput.Refusal(support, model, hasFrames, wantsDrivingAudio: false) is { } refusal)
        {
            throw new NotSupportedException(refusal);
        }

        if (references.Count > support.MaxFiles)
        {
            throw new ArgumentException(
                $"This request carries {references.Count} reference files; MiniMax documents at most {support.MaxFiles}.",
                nameof(references));
        }

        foreach (Data.Models.AvatarReferenceKind kind in Enum.GetValues<Data.Models.AvatarReferenceKind>())
        {
            int count = references.Count(r => r.Kind == kind);
            if (count > support.MaxOf(kind))
            {
                throw new ArgumentException(
                    $"This request carries {count} reference {kind.ToString().ToLowerInvariant()} item(s); MiniMax documents at most {support.MaxOf(kind)}.",
                    nameof(references));
            }
        }

        if (references.Where(r => r.Kind == Data.Models.AvatarReferenceKind.Video).Sum(r => r.DurationMs ?? 0) > support.MaxVideoTotalMs
            || references.Where(r => r.Kind == Data.Models.AvatarReferenceKind.Audio).Sum(r => r.DurationMs ?? 0) > support.MaxAudioTotalMs)
        {
            throw new ArgumentException(
                $"The reference videos or the reference audio together exceed MiniMax's documented {MaxReferenceMediaMs / 1000} s total per kind.",
                nameof(references));
        }
    }

    /// <summary>
    /// One reference content item (M1/M2): <c>{"type":"image_url"|"video_url"|"audio_url", "&lt;type&gt;":{"url":…},
    /// "role":"reference_image"|"reference_video"|"reference_audio"}</c>, carried as a base64 data URI.
    /// <para>
    /// UNVERIFIED (must-measure #4 in docs/design/avatars.md): the vendor documents the data URI for pictures; whether
    /// it also accepts one for <c>video_url</c> and <c>audio_url</c>, or requires an <c>mm_file://</c> upload (valid
    /// 7 days, M8), was not checked with a live call. Inline is the design's default; a vendor refusal surfaces as
    /// <c>PROVIDER_REJECTED</c> (nothing billed) and is the signal to add the upload path.
    /// </para>
    /// </summary>
    private static Dictionary<string, object?> BuildReferenceContentItem(VideoGenerationReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Kind == Data.Models.AvatarReferenceKind.Image)
        {
            // Pictures go through the same documented checks as a frame (type, 30 MB, pixel range, aspect).
            Dictionary<string, object?> item = BuildImageContentItem(
                new VideoGenerationImageInput(reference.ContentType, reference.Bytes), "reference_image");
            return item;
        }

        string mediaType = (reference.ContentType ?? string.Empty).Trim().ToLowerInvariant();
        if (!SupportedReferenceContentTypes.TryGetValue(mediaType, out Data.Models.AvatarReferenceKind declaredKind)
            || declaredKind != reference.Kind)
        {
            throw new ArgumentException(
                $"Unsupported reference {reference.Kind.ToString().ToLowerInvariant()} type '{mediaType}'. MiniMax documents MP4 or MOV video and WAV or MP3 audio.",
                nameof(reference));
        }

        if (reference.Bytes is not { Length: > 0 })
        {
            throw new ArgumentException("A reference medium carries no bytes.", nameof(reference));
        }

        long maxBytes = reference.Kind == Data.Models.AvatarReferenceKind.Video ? MaxReferenceVideoBytes : MaxReferenceAudioBytes;
        if (reference.Bytes.LongLength > maxBytes)
        {
            throw new ArgumentException(
                $"The reference {reference.Kind.ToString().ToLowerInvariant()} is {reference.Bytes.LongLength} bytes, over MiniMax's documented {maxBytes}-byte limit.",
                nameof(reference));
        }

        if (reference.DurationMs is not { } ms || ms < MinReferenceMediaMs || ms > MaxReferenceMediaMs)
        {
            throw new ArgumentException(
                $"A reference {reference.Kind.ToString().ToLowerInvariant()} must last {MinReferenceMediaMs / 1000}..{MaxReferenceMediaMs / 1000} s (MiniMax's documented range); its length is {(reference.DurationMs is { } d ? $"{d} ms" : "unknown")}.",
                nameof(reference));
        }

        (string type, string role) = reference.Kind == Data.Models.AvatarReferenceKind.Video
            ? ("video_url", "reference_video")
            : ("audio_url", "reference_audio");
        // The label comes from the bytes, not from the declared type: the vendor derives the format from the
        // label, and refuses what it does not list. Anything that is neither WAV nor MP3 stops here, before any request.
        string uriType = reference.Kind == Data.Models.AvatarReferenceKind.Audio
            ? ReferenceAudioFormat.Sniff(reference.Bytes) ?? throw new ReferenceAudioFormatException(
                ReferenceAudioFormat.ReuploadMessage, nameof(reference))
            : mediaType;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = type,
            [type] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["url"] = $"data:{uriType};base64,{Convert.ToBase64String(reference.Bytes)}"
            },
            ["role"] = role
        };
    }

    /// <summary>
    /// The documented lower-case <c>&lt;format&gt;</c> token of the data URI. <c>image/jpg</c> is a
    /// spelling callers use rather than a media type, so it is normalised to the canonical
    /// <c>jpeg</c>; every other subtype is already the token the vendor documents.
    /// </summary>
    private static string DataUriFormat(string mediaType) => mediaType switch
    {
        "image/jpg" => "jpeg",
        _ => mediaType["image/".Length..]
    };

    public async Task<ExternalJobStatus> PollAsync(ExternalJobHandle handle, CancellationToken ct)
    {
        using var response = await SendQueryAsync(handle.RemoteJobId, ct);

        ThrowOnFailure(response);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var jsonDoc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var taskElement = jsonDoc.RootElement.GetProperty("task");
        var statusString = taskElement.GetProperty("status").GetString()
            ?? throw new InvalidOperationException("Response missing status");

        var state = statusString switch
        {
            "queued" => ExternalJobState.Queued,
            "running" => ExternalJobState.Running,
            "succeeded" => ExternalJobState.Succeeded,
            "failed" => ExternalJobState.Failed,
            "cancelled" => ExternalJobState.Cancelled,
            _ => throw new NotSupportedException($"Unsupported status: {statusString}")
        };

        string? downloadUrl = null;
        string? failureReason = null;

        if (state == ExternalJobState.Succeeded)
        {
            var content = taskElement.GetProperty("content");
            downloadUrl = content.GetProperty("url").GetString();
        }

        if (state == ExternalJobState.Failed)
        {
            failureReason = ReadFailureReason(taskElement);
        }

        int? usageTotalSeconds = state == ExternalJobState.Succeeded
            ? ReadUsageTotalSeconds(taskElement)
            : null;

        return new ExternalJobStatus(state, downloadUrl, failureReason, usageTotalSeconds);
    }

    /// <summary>
    /// The vendor's own words for a failed task: <c>task.error.{code,message}</c> as "code: message", bounded and
    /// stripped of links (a message can echo a presigned URL) and of any text that looks like a credential.
    /// Never throws: a failed task with an unexpected shape still reports as failed.
    /// </summary>
    internal static string? ReadFailureReason(System.Text.Json.JsonElement taskElement)
    {
        if (!taskElement.TryGetProperty("error", out var error) || error.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        string? message = error.TryGetProperty("message", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String ? m.GetString() : null;
        string? code = error.TryGetProperty("code", out var c)
            ? c.ValueKind == System.Text.Json.JsonValueKind.String ? c.GetString() : c.ValueKind == System.Text.Json.JsonValueKind.Number ? c.GetRawText() : null
            : null;
        string text = string.IsNullOrWhiteSpace(code) ? message ?? string.Empty : string.IsNullOrWhiteSpace(message) ? code : $"{code}: {message}";
        if (text.Length == 0)
        {
            return null;
        }

        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?i)\b[a-z][a-z0-9+.\-]{1,15}://\S+|data:[a-z]+/[a-z0-9.+\-]+;base64,\S+", "<link>");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?i)\b(bearer\s+|sk-|eyJ)[A-Za-z0-9._\-]{8,}", "<redacted>");
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    /// <summary>
    /// The one HTTP path both <see cref="PollAsync"/> and <see cref="ProbeAsync"/> use:
    /// an authenticated <c>GET /v2/query/video_generation/{taskId}</c>. Never a POST.
    /// </summary>
    private Task<HttpResponseMessage> SendQueryAsync(string taskId, CancellationToken ct)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{QueryPathPrefix}{Uri.EscapeDataString(taskId)}");
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        return _httpClient.SendAsync(httpRequest, ct);
    }

    /// <summary>
    /// No-cost provider test: one <c>GET /v2/query/video_generation/{random id}</c>, never a task create.
    /// <para>
    /// What the vendor documents, verified on 2026-09-28 against
    /// https://platform.minimax.io/docs/api-reference/video-generation-v2-query.md (OpenAPI for
    /// <c>GET /v2/query/video_generation/{task_id}</c>):
    /// a <c>200</c> body is <c>GetVideoGenerationV2Resp</c> = <c>{"task": {...}}</c>; every error is an
    /// <c>OaiError</c> — "On error the HTTP status is the real error code (401/400/429/402/422/500…) and
    /// the body is this object": <c>{"type":"error","error":{"type","message","http_code"},"request_id"}</c>
    /// with <c>error.type</c> one of <c>authorized_error</c>(401), <c>bad_request_error</c>(400),
    /// <c>rate_limit_error</c>(429), <c>insufficient_balance_error</c>(402),
    /// <c>unprocessable_entity_error</c>(422), <c>overloaded_error</c>(529), <c>server_error</c>(500);
    /// the documented 401 example message is "login fail: ... (1004)". A <c>task_id</c> outside the
    /// 7-day window "returns <c>invalid task_id</c>".
    /// </para>
    /// <para>
    /// NOT verified (no key was ever sent to the vendor): the HTTP status and exact body returned for a
    /// nonexistent task id, and whether auth is checked before the task lookup. The v2 API documents no
    /// <c>base_resp</c> in-band status, so none is recognised here. Hence the conservative rule:
    /// </para>
    /// <list type="bullet">
    /// <item>401/403, or a documented <c>OaiError</c> with <c>error.type = authorized_error</c> (in-band,
    /// any status) → <see cref="VideoGenerationProbeResult.AuthRejected"/>.</item>
    /// <item>5xx, 408, 429, a network error, our own timeout, a non-JSON body, or JSON that is neither
    /// documented envelope → not ok, with a reason naming what was seen.</item>
    /// <item>Ok only for any other status whose body is a documented envelope: <c>{"task": {object}}</c>, or
    /// an <c>OaiError</c> whose <c>error.type</c> is a string other than <c>authorized_error</c> (e.g. the
    /// <c>bad_request_error</c> "invalid task_id" the vendor answers after accepting the key).</item>
    /// </list>
    /// Reasons carry at most a bounded, key-scrubbed vendor <c>error.type</c>/<c>error.message</c>, never
    /// the raw body.
    /// </summary>
    public async Task<VideoGenerationProbeResult> ProbeAsync(CancellationToken ct)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_probeTimeout);

        int status;
        string contentType;
        string body;
        try
        {
            using var response = await SendQueryAsync(Guid.NewGuid().ToString("N"), timeoutCts.Token);
            status = (int)response.StatusCode;
            contentType = response.Content.Headers.ContentType?.MediaType ?? "none";
            body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return NotOk($"Provider did not respond within {_probeTimeout.TotalSeconds:0.#}s.");
        }
        catch (HttpRequestException ex)
        {
            return NotOk($"Network error reaching provider: {Bounded(ex.Message)}");
        }

        if (status is 401 or 403)
        {
            return new VideoGenerationProbeResult(false, true, $"Provider rejected authentication (HTTP {status}).");
        }

        if (status == 500 && IsRecordNotFound(body))
        {
            return new VideoGenerationProbeResult(true, false,
                "Provider accepted the API key and answered the test lookup with \"record not found\" (HTTP 500), as expected for a nonexistent task id.");
        }

        if (status >= 500 || status is 408 or 429)
        {
            return NotOk(DescribeFailureStatus(status, body));
        }

        System.Text.Json.JsonDocument doc;
        try
        {
            doc = System.Text.Json.JsonDocument.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return NotOk($"Provider returned a non-JSON body (HTTP {status}, content-type {Bounded(contentType)}).");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object
                && root.TryGetProperty("task", out var task)
                && task.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                return new VideoGenerationProbeResult(true, false, $"Provider answered HTTP {status} with a task object.");
            }

            if (TryReadOaiError(root, out string errorType, out string errorMessage))
            {
                if (errorType == "authorized_error")
                {
                    return new VideoGenerationProbeResult(false, true,
                        $"Provider rejected authentication (HTTP {status}, {Bounded(errorType)}).");
                }

                string detail = errorMessage.Length > 0 ? $"{errorType}: {errorMessage}" : errorType;
                return new VideoGenerationProbeResult(true, false, $"Provider answered HTTP {status} ({Bounded(detail)}).");
            }

            return NotOk($"Provider returned an unrecognised JSON body (HTTP {status}).");
        }
    }

    /// <summary>
    /// MiniMax's documented whole-request-body ceiling, enforced on the SERIALIZED body (base64
    /// inflates a payload by about a third, so a sum of raw image sizes is not the same number).
    /// <b>Internal</b> via <c>InternalsVisibleTo</c> so the ceiling can be pinned directly instead of
    /// by allocating a body that large.
    /// </summary>
    /// <exception cref="ArgumentException">The body exceeds <see cref="MaxRequestBodyBytes"/>.</exception>
    internal static void EnsureBodyWithinLimit(long bodyBytes, string? parameterName = null)
    {
        if (bodyBytes > MaxRequestBodyBytes)
        {
            throw new ArgumentException(
                $"The request body is {bodyBytes} bytes, over MiniMax's documented {MaxRequestBodyBytes}-byte " +
                "(64 MB) whole-body limit. Input images travel as base64 data URIs, which inflate them by " +
                "about a third; send fewer or smaller frames.",
                parameterName);
        }
    }

    /// <summary>
    /// The reason for a 5xx/408/429 probe answer. It used to be just "Provider returned HTTP 500.",
    /// which threw away the one thing that explains it: the vendor's own error envelope. That envelope
    /// is kept here (bounded and key-scrubbed, with MiniMax's <c>request_id</c> for a support ticket).
    /// When it is a documented <c>OaiError</c> whose type is not <c>authorized_error</c>, the reason
    /// also says the key was accepted: MiniMax checks the key BEFORE it looks the task up (observed
    /// 2026-10-04 — a missing or invalid bearer token gets 401 <c>authorized_error</c> on this route
    /// for any task id), so such a 5xx is a vendor-side failure of the lookup, not a credential
    /// problem. The probe stays <c>ok: false</c> either way: a 5xx proves nothing about whether a
    /// real generation would succeed.
    /// </summary>
    private string DescribeFailureStatus(int status, string body)
    {
        string? detail = null;
        string? requestId = null;
        bool keyAccepted = false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object
                && root.TryGetProperty("request_id", out var rid)
                && rid.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                requestId = rid.GetString();
            }

            if (TryReadOaiError(root, out string errorType, out string errorMessage))
            {
                detail = errorMessage.Length > 0 ? $"{errorType}: {errorMessage}" : errorType;
                keyAccepted = errorType != "authorized_error";
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON (e.g. a gateway error page): the status alone is all there is.
        }

        var reason = new System.Text.StringBuilder($"Provider returned HTTP {status}");
        if (detail is not null)
            reason.Append($" ({Bounded(detail)})");
        if (!string.IsNullOrEmpty(requestId))
            reason.Append($", request_id {Bounded(requestId)}");
        reason.Append('.');
        if (keyAccepted && status >= 500)
            reason.Append(" The API key was accepted (MiniMax checks it before the task lookup), so this is a server-side error on MiniMax's query endpoint; retry later or contact MiniMax support with the request_id.");
        return reason.ToString();
    }

    /// <summary>
    /// MiniMax's real answer for a task id that does not exist (observed 2026-10-04 with a valid key):
    /// HTTP 500, <c>{"type":"error","error":{"type":"server_error","message":"record not found (1000)",...}}</c>.
    /// Auth is checked first (an invalid key gets 401 <c>authorized_error</c> for the same request), so
    /// this exact answer to the probe's random id proves the key works. Matched on the message, not the
    /// <c>(1000)</c> code, which MiniMax also uses for genuine internal errors.
    /// </summary>
    private static bool IsRecordNotFound(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return TryReadOaiError(doc.RootElement, out string errorType, out string errorMessage)
                && errorType == "server_error"
                && errorMessage.StartsWith("record not found", StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>Recognises the documented <c>OaiError</c> envelope: <c>{"type":"error","error":{"type":"...", "message":"..."}}</c>.</summary>
    private static bool TryReadOaiError(System.Text.Json.JsonElement root, out string errorType, out string errorMessage)
    {
        errorType = string.Empty;
        errorMessage = string.Empty;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object
            || !root.TryGetProperty("type", out var type)
            || type.ValueKind != System.Text.Json.JsonValueKind.String
            || type.GetString() != "error"
            || !root.TryGetProperty("error", out var error)
            || error.ValueKind != System.Text.Json.JsonValueKind.Object
            || !error.TryGetProperty("type", out var innerType)
            || innerType.ValueKind != System.Text.Json.JsonValueKind.String
            || string.IsNullOrEmpty(innerType.GetString()))
        {
            return false;
        }

        errorType = innerType.GetString()!;
        if (error.TryGetProperty("message", out var message) && message.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            errorMessage = message.GetString() ?? string.Empty;
        }

        return true;
    }

    private static VideoGenerationProbeResult NotOk(string reason) => new(false, false, reason);

    /// <summary>Scrubs the API key and truncates, so a vendor/exception message is safe to show an admin.</summary>
    private string Bounded(string text)
    {
        if (_apiKey.Length > 0)
        {
            text = text.Replace(_apiKey, "***", StringComparison.Ordinal);
        }

        return text.Length <= MaxVendorMessageChars ? text : text[..MaxVendorMessageChars] + "…";
    }

    /// <summary>
    /// Reads <c>task.usage.total_seconds</c> ("Total metered seconds = input seconds + output seconds",
    /// returned only when succeeded). Anything absent, null, or not a JSON integer yields <c>null</c>.
    /// </summary>
    private static int? ReadUsageTotalSeconds(System.Text.Json.JsonElement taskElement)
    {
        if (taskElement.TryGetProperty("usage", out var usage)
            && usage.ValueKind == System.Text.Json.JsonValueKind.Object
            && usage.TryGetProperty("total_seconds", out var totalSeconds)
            && totalSeconds.ValueKind == System.Text.Json.JsonValueKind.Number
            && totalSeconds.TryGetInt32(out int value))
        {
            return value;
        }

        return null;
    }

    /// <summary>
    /// 401/403 raise an <see cref="HttpRequestException"/> carrying the status code (so callers such
    /// as the no-cost provider probe can recognise an auth failure) and a message free of the API key;
    /// every other non-success code keeps <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>.
    /// </summary>
    private static void ThrowOnFailure(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new HttpRequestException("Unauthorized: provider rejected the API key", inner: null, statusCode: response.StatusCode);
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new HttpRequestException("Forbidden: provider denied access", inner: null, statusCode: response.StatusCode);
        }

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Hard ceiling on one downloaded clip (a 15 s generated clip is a few MB).</summary>
    public const long MaxDownloadBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Downloads a generated clip. The URL comes out of a vendor response, so it is treated as
    /// untrusted: https only, no credentials in the URL, no loopback/private/link-local literal
    /// host; the response must be a video/binary content type and is capped at
    /// <see cref="MaxDownloadBytes"/> (declared and actually streamed). No Authorization header is
    /// sent (the key must never reach a CDN host).
    /// </summary>
    public async Task<Stream> DownloadAsync(string downloadUrl, CancellationToken ct)
    {
        GeneratedClipUrlGuard.EnsureAllowed(downloadUrl);

        var response = await _downloadClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            response.EnsureSuccessStatusCode();

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null
                && !mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Equals("binary/octet-stream", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unexpected content type '{Bounded(mediaType)}' for a generated clip.");
            }

            long? declared = response.Content.Headers.ContentLength;
            if (declared > MaxDownloadBytes)
            {
                throw new InvalidOperationException($"Generated clip is {declared} bytes, over the {MaxDownloadBytes}-byte limit.");
            }

            var inner = await response.Content.ReadAsStreamAsync(ct);
            return new LimitedReadStream(inner, MaxDownloadBytes, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Both models this client serves accept image input, and both accept the same two-frame shape:
    /// the vendor's image-to-video scenario is one <c>image_url</c> item with <c>role=first_frame</c>
    /// plus, optionally, a second with <c>role=last_frame</c>
    /// (https://platform.minimax.io/docs/api-reference/video-generation-v2-create.md, "Supported input
    /// combinations" and "Count: first frame ≤ 1, last frame ≤ 1", fetched 2026-10-09).
    /// <para>
    /// The model switch is <see cref="Describe"/>'s, reused rather than restated, so "this model is
    /// not offered" and "this model accepts no image" can never drift apart: an unknown model throws
    /// here exactly as it does there.
    /// </para>
    /// </summary>
    public VideoImageInputSupport ImageInputSupport(string model)
    {
        _ = Describe(model);
        return VideoImageInputSupport.FirstAndLastFrame;
    }

    /// <summary>
    /// Reference mode of the v2 endpoint (docs/design/avatars.md "Vendor facts", checked 2026-11-05): every model of
    /// this client takes <c>reference_image</c> ≤ 9, <c>reference_video</c> ≤ 3, <c>reference_audio</c> ≤ 3, at most 12
    /// files, videos and audio 2..15 s each and ≤ 15 s per kind in total (M2, M5, M6); reference mode cannot be
    /// combined with a first or last frame (M3).
    /// <para>
    /// <c>AudioDrivesSpeech</c> is <c>false</c> on purpose: the vendor's guide says reference audio keeps the "voice"
    /// consistent but does not say the person will say those words (M7; must-measure #2). Until a live call shows
    /// otherwise a step's driving <c>AvatarAudio</c> is refused on MiniMax, while the avatar's own speech samples are
    /// still sent as <c>reference_audio</c> for the voice.
    /// </para>
    /// </summary>
    public VideoReferenceInputSupport ReferenceInputSupport(string model)
    {
        _ = Describe(model);
        return ReferenceSupport;
    }

    /// <summary>The one reference declaration every MiniMax v2 model shares (see <see cref="ReferenceInputSupport"/>).</summary>
    public static VideoReferenceInputSupport ReferenceSupport { get; } = new(
        MaxImages: 9,
        MaxVideos: 3,
        MaxAudio: 3,
        MaxFiles: 12,
        MaxVideoTotalMs: MaxReferenceMediaMs,
        MaxAudioTotalMs: MaxReferenceMediaMs,
        AudioDrivesSpeech: false,
        ExclusiveWithFrames: true);

    /// <summary>
    /// The vendor's input-material charge for one take (M9, pricing page read 2026-11-05): reference pictures beyond
    /// the free allowance (first 5; first 2 on H3-Max) at a per-picture price, input video per second (at the output
    /// rate, H3-Max at its own input-video rate), audio free.
    /// <para>
    /// UNVERIFIED (must-measure #3): whether <c>reference_image</c> items count against that free allowance, and
    /// whether a reference video is billed for its full length. This reads the pricing page literally (pictures count,
    /// video billed for its whole length rounded up to the second), which is the higher of the plausible readings, so
    /// a hold sized from it is never short.
    /// </para>
    /// </summary>
    public decimal InputMaterialCostUsd(string model, string resolution, IReadOnlyList<VideoReferenceMaterial> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        return StaticInputMaterialCostUsd(model, resolution, references);
    }

    /// <summary>The same figure as <see cref="InputMaterialCostUsd"/>, static so pricing can ask without a client.</summary>
    public static decimal StaticInputMaterialCostUsd(string model, string resolution, IReadOnlyList<VideoReferenceMaterial> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        VideoGenerationCapabilities caps = DescribeModel(model);
        if (!caps.PricePerSecondByResolution.TryGetValue(resolution ?? string.Empty, out decimal outputRate))
        {
            throw new NotSupportedException($"Model {model} is not priced at resolution '{resolution}'.");
        }

        (int freeImages, decimal perImage, decimal videoRate) = model switch
        {
            "MiniMax-H3-Max" => (2, 0.074m, resolution == "480P" ? 0.0553m : 0.143m),
            _ => (5, ExtraImagePriceUsd(model), outputRate),
        };

        int images = references.Count(r => r.Kind == Data.Models.AvatarReferenceKind.Image);
        int videoSeconds = references
            .Where(r => r.Kind == Data.Models.AvatarReferenceKind.Video)
            .Sum(r => (int)Math.Ceiling((r.DurationMs ?? MaxReferenceMediaMs) / 1000.0));

        return (Math.Max(0, images - freeImages) * perImage) + (videoSeconds * videoRate);
    }

    /// <summary>
    /// The highest input-material charge one take of <paramref name="model"/> can reach inside OUR avatar limits
    /// (<see cref="Data.Models.ProjectAvatarLimits"/>: 8 pictures; 15 s of video in total), used to size a credit hold
    /// before the avatar's actual references are read.
    /// </summary>
    public static decimal MaxInputMaterialCostUsd(string model, string resolution) =>
        StaticInputMaterialCostUsd(model, resolution,
        [
            .. Enumerable.Repeat(new VideoReferenceMaterial(Data.Models.AvatarReferenceKind.Image, null),
                Math.Min(Data.Models.ProjectAvatarLimits.MaxImages, ReferenceSupport.MaxImages)),
            new VideoReferenceMaterial(Data.Models.AvatarReferenceKind.Video, ReferenceSupport.MaxVideoTotalMs),
        ]);

    /// <summary>Price of one reference picture beyond the free five (M9): H3 $0.04, Highspeed $0.03, Lite $0.02.</summary>
    private static decimal ExtraImagePriceUsd(string model) => model switch
    {
        "MiniMax-H3-Highspeed" => 0.03m,
        "MiniMax-H3-Lite" => 0.02m,
        _ => 0.04m,
    };

    /// <summary>The models <see cref="Describe"/> prices, in the order the price card lists them.</summary>
    public static IReadOnlyList<string> ModelIds { get; } = ["MiniMax-H3", "MiniMax-H3-Max", "MiniMax-H3-Highspeed", "MiniMax-H3-Lite"];

    public VideoGenerationCapabilities Describe(string model) => DescribeModel(model);

    /// <summary>
    /// The price card of <paramref name="model"/>: the one source of its resolutions, durations and ratios, static so
    /// the schema exporter can list them without constructing a client.
    /// </summary>
    public static VideoGenerationCapabilities DescribeModel(string model)
    {
        return model switch
        {
            "MiniMax-H3" => new VideoGenerationCapabilities(
                PricePerSecondByResolution: new Dictionary<string, decimal>
                {
                    { "768P", 0.08m },
                    { "2K", 0.13m }
                },
                MinDurationSeconds: 4,
                MaxDurationSeconds: 15,
                AllowedRatios: new[] { "21:9", "16:9", "4:3", "1:1", "3:4", "9:16" }),
            "MiniMax-H3-Max" => new VideoGenerationCapabilities(
                PricePerSecondByResolution: new Dictionary<string, decimal>
                {
                    { "480P", 0.05m },
                    { "768P", 0.08m }
                },
                MinDurationSeconds: 5,
                MaxDurationSeconds: 15,
                AllowedRatios: new[] { "21:9", "16:9", "4:3", "1:1", "3:4", "9:16" }),
            // The two cheaper H3 variants (docs/design/avatars.md "Vendor facts" M1/M9, pricing page read 2026-11-05):
            // the per-second prices are published. Their duration range and ratios are NOT on the pricing page; they
            // are set to H3-Max's 5..15 s and the H3 ratio list, the narrower of the documented H3 ranges, so a
            // value the vendor might refuse is refused here first. UNVERIFIED until a live call (avatars must-measure).
            "MiniMax-H3-Highspeed" => new VideoGenerationCapabilities(
                PricePerSecondByResolution: new Dictionary<string, decimal>
                {
                    { "480P", 0.04m },
                    { "768P", 0.06m }
                },
                MinDurationSeconds: 5,
                MaxDurationSeconds: 15,
                AllowedRatios: new[] { "21:9", "16:9", "4:3", "1:1", "3:4", "9:16" }),
            "MiniMax-H3-Lite" => new VideoGenerationCapabilities(
                PricePerSecondByResolution: new Dictionary<string, decimal>
                {
                    { "480P", 0.025m },
                    { "768P", 0.04m }
                },
                MinDurationSeconds: 5,
                MaxDurationSeconds: 15,
                AllowedRatios: new[] { "21:9", "16:9", "4:3", "1:1", "3:4", "9:16" }),
            _ => throw new NotSupportedException($"Model {model} is not supported")
        };
    }
}
