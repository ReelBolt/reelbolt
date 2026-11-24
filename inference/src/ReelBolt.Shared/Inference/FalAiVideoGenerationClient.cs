using System.Globalization;
using System.Text.Json;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// fal.ai — video generation over the same queue contract as <see cref="FalAiImageClient"/> and
/// <see cref="FalAiAnalysisClient"/>, with IMAGE-TO-VIDEO first and text-to-video alongside.
/// </summary>
/// <remarks>
/// <para>
/// <b>One client, one catalogue, many endpoints.</b> fal exposes one queue contract over every model it
/// hosts, so this kind is ONE <see cref="IVideoGenerationClient"/> and the endpoint is the request's
/// <c>Model</c>, which a <c>VideoGenerate</c> step takes from <see cref="Catalog"/> (one default per
/// direction, overridable). Nothing is guessed: every endpoint was transcribed from its own OpenAPI
/// schema (fetched 2026-10-16; <c>docs/fal-ai-models.md</c> carries every field with its URL), an enum
/// is validated BEFORE it is sent (fal silently ignores an unknown value and returns a plausible clip
/// for a request nobody made), and a required field is required.
/// </para>
/// <para>
/// <b>The frames travel as data URIs, and that is a documented compromise.</b>
/// <see cref="VideoGenerationRequest.Images"/> carries bytes, and this service holds no upload path of
/// its own, so a frame is embedded as <c>data:image/png;base64,…</c>. fal documents that form
/// platform-wide ("Models also accept base64-encoded data URIs" — fal.ai/docs/documentation/model-apis/fal-cdn.md,
/// read 2026-10-16) while warning that a URL "is the universal format that works with every model", and
/// only some endpoints restate it per field (Vidu Q4's <c>image_url</c>: "URL or base64 data URI of the
/// image to use as the first frame"). Per-endpoint acceptance of a data URI is therefore on
/// <c>docs/fal-ai-models.md</c>'s "must measure with a real key" list rather than claimed here.
/// </para>
/// <para>
/// <b>The handle carries the vendor's own tracking URLs, not just its request id.</b> fal scopes those
/// URLs to the <b>app</b> (<c>{owner}/{app}</c>) rather than to the endpoint, and the endpoint-shaped
/// URLs its documentation shows answer HTTP 405 (measured without a credential, 2026-10-16) — so a
/// caller resuming a job from a persisted id alone (the <c>VideoGenerate</c> step's crash-resume path,
/// which polls instead of re-submitting) cannot rebuild them. <see cref="ExternalJobHandle.RemoteJobId"/>
/// is therefore <c>{requestId}|{statusUrl}|{responseUrl}</c>, and a submission is never repeated just
/// because a poll timed out: at fal's prices a second submission is a second clip.
/// </para>
/// <para>
/// <b>No idempotency key is sent.</b> fal's queue documents none (unlike Higgsfield's
/// <c>Idempotency-Key</c> header or ComfyUI's caller-chosen <c>prompt_id</c>), so
/// <see cref="VideoGenerationRequest.IdempotencyKey"/> is ignored rather than invented into a header
/// the vendor may reject; the persisted request id is what makes a retry resume instead of re-buying.
/// </para>
/// <para>
/// <b>What is deliberately not forwarded.</b> <c>generate_audio</c> (its schema default is <c>true</c>
/// on every endpoint that has it, and the rate cards bill the with-audio tier, so the platform must not
/// assume a rate the request does not buy), <c>multi_prompt</c> storyboards (mutually exclusive with the
/// prompt, and the step's own plan already IS the storyboard), <c>negative_prompt</c> (the domain request
/// has no such field, and inventing one would be a filtering decision nobody asked for), and every seed.
/// </para>
/// <para>
/// <b>A person's media is not kept by the vendor.</b> fal keeps a request's inputs for 30 days unless the submit
/// carries <c>X-Fal-Store-IO: 0</c> (<c>docs/fal-ai-models.md</c>). An ordinary request does not send it (unchanged);
/// a request that carries an avatar's media (<see cref="VideoGenerationRequest.References"/> or
/// <see cref="VideoGenerationRequest.NoVendorRetention"/>, avatars phase 3) does. Whether the talking-head routes
/// honour it is on the must-measure list.
/// </para>
/// <para>
/// <b>Talking heads</b> (<see cref="FalVideoJob.TalkingHead"/>, avatars phase 3): the request carries exactly one
/// picture (the avatar's still, <see cref="VideoGenerationRequest.Images"/>[0]) and exactly one audio reference (the
/// speech), declared through <see cref="IVideoReferenceInputSupport"/> as
/// <see cref="VideoReferenceInputSupport.AnimatesStill"/>. The audio travels as a data URI like the frames; its length
/// is checked against the endpoint's bounds and the request's duration before anything is sent.
/// </para>
/// <para>
/// <b>Reference-to-video</b> (<see cref="FalVideoJob.ReferenceToVideo"/>, docs/design/avatar-objects.md): the request
/// carries no frame and one to <see cref="FalVideoEndpoint.MaxReferenceImages"/> reference PICTURES of its subjects
/// (people and objects), each sent as a data URI in the endpoint's own array field. Reference videos and audio are not
/// sent on any fal route (see the catalogue entries for why). The whole body is kept under
/// <see cref="MaxReferenceRequestBytes"/>. Every other endpoint still refuses reference material.
/// </para>
/// </remarks>
public sealed class FalAiVideoGenerationClient : IVideoGenerationClient, IVideoImageInputSupport, IVideoReferenceInputSupport
{
    /// <summary>The public queue host. A provider row's endpoint overrides it.</summary>
    public const string DefaultEndpoint = FalQueueClient.DefaultEndpoint;

    /// <summary>The endpoint catalogue: one default per direction plus the documented alternatives.</summary>
    public static FalVideoCatalog Catalog => FalVideoCatalog.Instance;

    /// <summary>Hard ceiling on one downloaded clip (a 20 s generated clip is a few MB).</summary>
    public const long MaxDownloadBytes = 512L * 1024 * 1024;

    /// <summary>
    /// The most this platform puts in one reference-to-video request body (the pictures travel inline as base64 data
    /// URIs). fal documents no body ceiling for its queue, so this is the PLATFORM's bound, chosen to stay far below
    /// any plausible one: a caller shares it out with <see cref="VideoReferenceInput.SelectShared"/> and leaves out,
    /// with a reported reason, what does not fit. On the must-measure list of docs/fal-ai-models.md.
    /// </summary>
    public const long MaxReferenceRequestBytes = 24L * 1024 * 1024;

    private const int MaxVendorMessageChars = 300;
    private const char HandleSeparator = '|';

    private readonly FalQueueClient _queue;

    /// <summary>The same queue, whose submits carry <c>X-Fal-Store-IO: 0</c>: used for a person's media only.</summary>
    private readonly FalQueueClient _privateQueue;
    private readonly HttpClient _downloadClient;
    private readonly string _apiKey;
    private readonly string? _probeModel;

    /// <param name="probeModel">
    /// The endpoint id <see cref="ProbeAsync"/> submits against, taken from the provider row so a
    /// connection test checks the model an admin typed rather than some other reachable endpoint.
    /// </param>
    public FalAiVideoGenerationClient(
        HttpClient httpClient,
        string apiKey,
        HttpClient? downloadClient = null,
        TimeSpan? pollInterval = null,
        TimeSpan? pollTimeout = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        string? probeModel = null)
    {
        _queue = new FalQueueClient(httpClient, apiKey, downloadClient, pollInterval, pollTimeout, delay);
        _privateQueue = new FalQueueClient(httpClient, apiKey, downloadClient, pollInterval, pollTimeout, delay,
            FalAiVideoSegmentationClient.StoreIoHeaders);
        _downloadClient = downloadClient ?? httpClient;
        _apiKey = apiKey ?? string.Empty;
        _probeModel = probeModel;
    }

    // ------------------------------------------------------------------ IVideoGenerationClient

    public async Task<ExternalJobHandle> SubmitAsync(VideoGenerationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        FalVideoEndpoint endpoint = Require(request.Model);

        // Only a talking-head route (its speech track) and a reference-to-video route (its subjects' pictures) take
        // reference material, validated in BuildBody; every other endpoint refuses it rather than silently dropping
        // it, so a step asked to show someone or something never shows nothing.
        if (endpoint.Job is not (FalVideoJob.TalkingHead or FalVideoJob.ReferenceToVideo))
        {
            VideoReferenceInput.EnsureNoReferences(request, nameof(request));
        }

        Dictionary<string, object?> body = BuildBody(endpoint, request);
        FalQueueClient queue = CarriesPersonalMedia(request) ? _privateQueue : _queue;
        FalSubmission submission = await queue.SubmitAsync(endpoint.Id, body, ct);

        if (string.IsNullOrEmpty(submission.RequestId))
        {
            throw new InvalidOperationException(
                "fal.ai accepted the submission for '" + endpoint.Id + "' but returned no request_id, so the job " +
                "cannot be tracked or resumed. Treat the take's outcome as unknown.");
        }

        return new ExternalJobHandle(EncodeHandle(submission));
    }

    public async Task<ExternalJobStatus> PollAsync(ExternalJobHandle handle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);
        (string requestId, string statusUrl, string responseUrl) = DecodeHandle(handle.RemoteJobId);

        FalQueueStatus status = await _queue.ReadStatusAsync(statusUrl, "job " + requestId, ct);
        switch (status.Status)
        {
            case "IN_QUEUE":
                return new ExternalJobStatus(ExternalJobState.Queued, null, null);

            case "IN_PROGRESS":
                return new ExternalJobStatus(ExternalJobState.Running, null, null);

            case "COMPLETED":
                {
                    // A COMPLETED queue entry whose status document carries 'error' is a request that RAN
                    // and failed; the vendor documents the field as "present only if the request failed".
                    if (!string.IsNullOrWhiteSpace(status.Error))
                    {
                        return new ExternalJobStatus(ExternalJobState.Failed, null,
                            Bounded(FalQueueClient.CleanVendorText(status.Error)));
                    }

                    FalResultFetch fetch = await _queue.TryFetchResultAsync(responseUrl, "job " + requestId, ct);
                    if (fetch.Output is not { } output)
                    {
                        // 4xx on the result route is the vendor REFUSING the request (its queue validates the
                        // input document when the job runs): nothing was generated, so the take is failed
                        // rather than left charged and re-polled until the step's timeout. 5xx and 401/403 are
                        // NOT that: whether such a job was billed is unknown, so they keep the take charged
                        // and are re-polled, which is the executor's own POLL_ERROR contract.
                        if (fetch.StatusCode is 400 or 422)
                        {
                            // S-57: the vendor's own sentence, cleaned and capped; never the raw body.
                            string detail = Bounded(FalQueueClient.VendorMessage(fetch.Body));
                            return new ExternalJobStatus(ExternalJobState.Failed, null,
                                "fal.ai refused the request (HTTP " + fetch.StatusCode + ")" +
                                (detail.Length > 0 ? ": " + detail : "."));
                        }

                        throw new InvalidOperationException(
                            "fal.ai reported '" + requestId + "' COMPLETED but answered HTTP " + fetch.StatusCode +
                            " fetching its result.");
                    }

                    if (!TryReadVideoUrl(output, out string? url) || string.IsNullOrWhiteSpace(url))
                    {
                        throw new InvalidOperationException(
                            "fal.ai completed '" + requestId + "' but returned no 'video.url'. A 200 carrying no video " +
                            "is not a success.");
                    }

                    return new ExternalJobStatus(ExternalJobState.Succeeded, url, null);
                }

            default:
                throw new NotSupportedException(
                    "fal.ai returned an unrecognised queue status '" + status.Status + "' for job " + requestId +
                    ". Treating an unknown state as 'still working' would burn the step's poll budget and then " +
                    "report a timeout, hiding the one fact that explains it.");
        }
    }

    /// <summary>
    /// No-cost provider test. It does NOT submit an empty body: the empty-body trick every other fal arm
    /// uses rests on the endpoint requiring an input, and <b>that premise is false for text-to-video</b> —
    /// <c>fal-ai/kling-video/v3/turbo/pro/text-to-video</c> declares <c>required: []</c> with
    /// <c>generate_audio</c> defaulting to <c>true</c>, so an empty body is a real 5 s clip and a real
    /// charge. This probe submits a body that is DELIBERATELY NOT JSON
    /// (<see cref="FalQueueClient.UnparseableProbeBody"/>): no endpoint can deserialise it into an input
    /// document, so none can dispatch, generate or bill — a guarantee that does not depend on any
    /// endpoint's required list, and which the tests assert against every catalogued endpoint's schema.
    /// <para>
    /// A model id outside the catalogue fails the test without a request, because this client would
    /// refuse to use it anyway.
    /// </para>
    /// </summary>
    public async Task<VideoGenerationProbeResult> ProbeAsync(CancellationToken ct)
    {
        string? model = string.IsNullOrWhiteSpace(_probeModel) ? null : _probeModel.Trim();
        if (model is null)
        {
            return new VideoGenerationProbeResult(false, false, "No model id is configured on this provider row.");
        }

        if (Catalog.Find(model) is null)
        {
            return new VideoGenerationProbeResult(false, false,
                "'" + model + "' is not a fal.ai video endpoint this client has a read schema for, so it would " +
                "refuse to send it a request. Use one of: " + Catalog.AllIds + ".");
        }

        FalProbeOutcome outcome = await _queue.ProbeAsync(model, FalQueueClient.UnparseableProbeBody, ct);
        return new VideoGenerationProbeResult(outcome.IsOk, outcome.AuthRejected, outcome.Reason);
    }

    /// <summary>
    /// Downloads a generated clip. The URL comes out of a vendor response, so it is treated as
    /// untrusted: https only, no credentials in the URL, no loopback/private/link-local literal host;
    /// the response must be a video/binary content type and is capped at
    /// <see cref="MaxDownloadBytes"/> (declared and actually streamed). No Authorization header is
    /// sent — the key must never reach a CDN host.
    /// </summary>
    public async Task<Stream> DownloadAsync(string downloadUrl, CancellationToken ct)
    {
        GeneratedClipUrlGuard.EnsureAllowed(downloadUrl);

        HttpResponseMessage response = await _downloadClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            response.EnsureSuccessStatusCode();

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null
                && !mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Equals("binary/octet-stream", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Unexpected content type '" + mediaType + "' for a generated clip.");
            }

            long? declared = response.Content.Headers.ContentLength;
            if (declared > MaxDownloadBytes)
            {
                throw new InvalidOperationException(
                    "Generated clip is " + declared + " bytes, over the " + MaxDownloadBytes + "-byte limit.");
            }

            Stream inner = await response.Content.ReadAsStreamAsync(ct);
            return new LimitedReadStream(inner, MaxDownloadBytes, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The capability set a <c>VideoGenerate</c> step validates its configuration against: the price
    /// table (keyed by the resolution token that is SENT), the duration range and the ratios.
    /// </summary>
    /// <remarks>
    /// A flat-priced endpoint with no resolution field reports exactly one price key, the platform's
    /// <see cref="FalVideoCatalog.NativeToken"/>; an endpoint with no aspect-ratio field reports exactly
    /// that one ratio. Both are deliberate: the step states in its own configuration that the vendor
    /// chooses that axis, instead of the platform inventing a resolution the vendor ignores or a ratio
    /// the vendor never receives. An endpoint with no schema at all throws, exactly as
    /// <see cref="MiniMaxVideoGenerationClient.Describe"/> does — a model the platform cannot price
    /// stays unofferable rather than free.
    /// </remarks>
    /// <exception cref="NotSupportedException">No schema has been read for <paramref name="model"/>.</exception>
    public VideoGenerationCapabilities Describe(string model)
    {
        string id = model?.Trim() ?? string.Empty;
        FalVideoEndpoint? endpoint = id.Length == 0 ? Catalog.DefaultFor(FalVideoJob.ImageToVideo) : Catalog.Find(id);
        if (endpoint is null)
        {
            throw new NotSupportedException(
                "Model '" + id + "' is not a fal.ai video endpoint this platform has a read schema for, so it " +
                "cannot be priced and will not be used. Endpoints with a verified schema: " + Catalog.AllIds + ".");
        }

        return new VideoGenerationCapabilities(
            endpoint.PricePerSecondByResolution,
            endpoint.MinDurationSeconds,
            endpoint.MaxDurationSeconds,
            endpoint.AllowedRatios);
    }

    /// <summary>
    /// How many input images this client's <paramref name="model"/> accepts, read from the same
    /// catalogue entry its request body is built from: an image-to-video endpoint takes a first frame
    /// and, where the endpoint's schema documents one, a last frame; a text-to-video endpoint takes
    /// none. Pure - no network call, ever.
    /// </summary>
    /// <exception cref="NotSupportedException">No schema has been read for <paramref name="model"/>.</exception>
    public VideoImageInputSupport ImageInputSupport(string model) => ImageInputSupportOf(Require(model));

    /// <summary>The image input of one catalogue entry; static so code that holds no client can ask (<see cref="VideoSubjectModels"/>).</summary>
    public static VideoImageInputSupport ImageInputSupportOf(FalVideoEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.FirstFrameField is null)
        {
            return VideoImageInputSupport.None;
        }

        return endpoint.LastFrameField is not null
            ? VideoImageInputSupport.FirstAndLastFrame
            : VideoImageInputSupport.FirstFrame;
    }

    /// <summary>
    /// What <paramref name="model"/> takes as subject material: a talking-head route animates one still and one
    /// speech track (<see cref="VideoReferenceInputSupport.AnimatesStill"/>, audio documented to drive the mouth); a
    /// reference-to-video route takes reference pictures only (no video, no audio, never with a frame); every other
    /// endpoint takes none. Read from the catalogue entry the body is built from. Pure.
    /// </summary>
    /// <exception cref="NotSupportedException">No schema has been read for <paramref name="model"/>.</exception>
    public VideoReferenceInputSupport ReferenceInputSupport(string model) => ReferenceInputSupportOf(Require(model));

    /// <summary>The reference declaration of one catalogue entry; static so code that holds no client can ask.</summary>
    public static VideoReferenceInputSupport ReferenceInputSupportOf(FalVideoEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint is { Job: FalVideoJob.ReferenceToVideo, ReferenceImagesField: not null, MaxReferenceImages: > 0 })
        {
            return new VideoReferenceInputSupport(
                MaxImages: endpoint.MaxReferenceImages, MaxVideos: 0, MaxAudio: 0, MaxFiles: endpoint.MaxReferenceImages,
                MaxVideoTotalMs: 0, MaxAudioTotalMs: 0,
                AudioDrivesSpeech: false, ExclusiveWithFrames: true);
        }

        if (endpoint.Job != FalVideoJob.TalkingHead || endpoint.AudioField is null)
        {
            return VideoReferenceInputSupport.None;
        }

        return new VideoReferenceInputSupport(
            MaxImages: 0, MaxVideos: 0, MaxAudio: 1, MaxFiles: 1,
            MaxVideoTotalMs: 0, MaxAudioTotalMs: endpoint.MaxAudioMs,
            AudioDrivesSpeech: true, ExclusiveWithFrames: false)
        {
            AnimatesStill = true,
            TakesPrompt = endpoint.PromptField is not null,
            MinAudioMs = endpoint.MinAudioMs,
        };
    }

    /// <summary>
    /// A talking-head route bills only the seconds of video it delivers (both pricing sentences, read 2026-11-06): the
    /// still and the speech are not billed as input. A reference-to-video route's pricing sentence (read 2026-11-16)
    /// names only the seconds of video generated, and the one input it bills on top (Wan's reference VIDEO) is never
    /// sent. <c>0</c> for every endpoint. Pure.
    /// </summary>
    /// <exception cref="NotSupportedException">No schema has been read for <paramref name="model"/>.</exception>
    public decimal InputMaterialCostUsd(string model, string resolution, IReadOnlyList<VideoReferenceMaterial> references)
    {
        _ = Require(model);
        return 0m;
    }

    /// <summary>True when a request carries a person's media, so its submit asks the vendor not to keep the inputs.</summary>
    internal static bool CarriesPersonalMedia(VideoGenerationRequest request) =>
        request.NoVendorRetention || request.References is { Count: > 0 };

    // ------------------------------------------------------------------ the request

    /// <summary>
    /// Resolves the endpoint. An EMPTY model means the catalogue's default image-to-video endpoint; an
    /// explicit one must be an endpoint a schema has been read for, because a field fal does not define
    /// is SILENTLY IGNORED and the caller would get a plausible clip for a request nobody made.
    /// </summary>
    private static FalVideoEndpoint Require(string? model)
    {
        string id = model?.Trim() ?? string.Empty;
        if (id.Length == 0)
        {
            return Catalog.DefaultFor(FalVideoJob.ImageToVideo);
        }

        return Catalog.Find(id) ?? throw new NotSupportedException(
            "No schema has been read for the fal.ai video endpoint '" + id + "', so this client will not send it a " +
            "request. Endpoints with a verified schema: " + Catalog.AllIds + ".");
    }

    /// <summary>
    /// Builds the request body from the typed request, refusing anything the endpoint's schema does not
    /// declare. Internal so the tests can pin exact bodies without a transport.
    /// </summary>
    internal static Dictionary<string, object?> BuildBody(FalVideoEndpoint ep, VideoGenerationRequest request)
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal);

        // ---- the frames. The platform's request carries images in order: the first frame, then (where
        // the endpoint documents one) the end frame. There is no other role either could play.
        IReadOnlyList<VideoGenerationImageInput> images = request.Images ?? [];
        if (images.Count > 2)
        {
            throw new ArgumentException(
                "'" + ep.Id + "' takes a first frame and, where documented, one end frame; " + images.Count +
                " images were given.", nameof(request));
        }

        if (ep.FirstFrameField is null)
        {
            if (images.Count > 0)
            {
                throw new ArgumentException(
                    "'" + ep.Id + "' is a text-to-video route and documents no image input, so a frame cannot be " +
                    "honoured. Use an image-to-video endpoint to animate a picture.", nameof(request));
            }
        }
        else if (images.Count == 0)
        {
            if (ep.FirstFrameRequired)
            {
                throw new ArgumentException(
                    "A first-frame image is required: '" + ep.Id + "' takes '" + ep.FirstFrameField + "'.", nameof(request));
            }
        }
        else
        {
            body[ep.FirstFrameField] = DataUri(ep, ep.FirstFrameField, images[0]);
        }

        if (images.Count == 2)
        {
            if (ep.LastFrameField is null)
            {
                throw new ArgumentException(
                    "'" + ep.Id + "' documents no end frame, so a second image cannot be honoured.", nameof(request));
            }

            body[ep.LastFrameField] = DataUri(ep, ep.LastFrameField, images[1]);
        }

        if (ep.LastFrameRequired && !body.ContainsKey(ep.LastFrameField!))
        {
            throw new ArgumentException(
                "An end-frame image is required: '" + ep.Id + "' takes '" + ep.LastFrameField + "' as well as " +
                "'" + ep.FirstFrameField + "' (it is a first-last-frame route).", nameof(request));
        }

        // ---- the driving audio (talking-head routes only). Exactly one audio reference, of a known length inside
        // the endpoint's bounds, and covered by the request's duration (what the take is reserved and billed at, since
        // the clip is as long as the audio).
        IReadOnlyList<VideoGenerationReference> references = request.References ?? [];
        if (ep.ReferenceImagesField is not null)
        {
            // ---- the reference pictures (reference-to-video routes only): one to the endpoint's ceiling, pictures
            // only, each with bytes, the whole array inside the platform's body bound.
            if (references.Count == 0)
            {
                throw new ArgumentException(
                    "'" + ep.Id + "' makes a clip from reference pictures ('" + ep.ReferenceImagesField + "'), and none " +
                    "was given.", nameof(request));
            }

            if (references.Count > ep.MaxReferenceImages)
            {
                throw new ArgumentException(
                    "'" + ep.Id + "' takes at most " + ep.MaxReferenceImages + " reference pictures; " + references.Count +
                    " were given.", nameof(request));
            }

            var uris = new List<string>(references.Count);
            long encodedBytes = 0;
            foreach (VideoGenerationReference reference in references)
            {
                if (reference.Kind != Data.Models.AvatarReferenceKind.Image)
                {
                    throw new ArgumentException(
                        "'" + ep.Id + "' takes reference PICTURES only; a reference " +
                        reference.Kind.ToString().ToLowerInvariant() + " was given.", nameof(request));
                }

                if (reference.Bytes is not { Length: > 0 })
                {
                    throw new ArgumentException(
                        "'" + ep.ReferenceImagesField + "' for '" + ep.Id + "' was given a picture with no bytes.", nameof(request));
                }

                string pictureType = string.IsNullOrWhiteSpace(reference.ContentType) ? "image/png" : reference.ContentType;
                if (!pictureType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "'" + ep.ReferenceImagesField + "' for '" + ep.Id + "' must hold pictures; got '" + pictureType + "'.",
                        nameof(request));
                }

                encodedBytes += VideoReferenceInput.Base64Length(reference.Bytes.LongLength);
                uris.Add("data:" + pictureType + ";base64," + Convert.ToBase64String(reference.Bytes));
            }

            if (encodedBytes > MaxReferenceRequestBytes)
            {
                throw new ArgumentException(
                    "The reference pictures for '" + ep.Id + "' are " + encodedBytes + " bytes once encoded, over the " +
                    MaxReferenceRequestBytes + "-byte bound this platform keeps a request under.", nameof(request));
            }

            body[ep.ReferenceImagesField] = uris;
        }
        else if (ep.AudioField is null)
        {
            if (references.Count > 0)
            {
                throw new ArgumentException(
                    "'" + ep.Id + "' takes no reference material; " + references.Count + " item(s) were given.", nameof(request));
            }
        }
        else
        {
            if (references.Count != 1 || references[0].Kind != Data.Models.AvatarReferenceKind.Audio)
            {
                throw new ArgumentException(
                    "'" + ep.Id + "' takes exactly one speech track ('" + ep.AudioField + "') and no other reference " +
                    "material; " + references.Count + " item(s) were given.", nameof(request));
            }

            VideoGenerationReference audio = references[0];
            if (audio.Bytes is not { Length: > 0 })
            {
                throw new ArgumentException("'" + ep.AudioField + "' for '" + ep.Id + "' was given audio with no bytes.", nameof(request));
            }

            if (audio.DurationMs is not { } audioMs || audioMs < ep.MinAudioMs || audioMs > ep.MaxAudioMs)
            {
                throw new ArgumentException(
                    "'" + ep.Id + "' takes a speech track from " + ep.MinAudioMs / 1000.0 + " to " + ep.MaxAudioMs / 1000.0 +
                    " seconds; this one is " + (audio.DurationMs is { } ms ? (ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " s" : "of unknown length") +
                    ".", nameof(request));
            }

            if (!ep.SendsDuration && (long)request.DurationSeconds * 1000 < audioMs)
            {
                throw new ArgumentException(
                    "'" + ep.Id + "' makes a clip as long as its speech track (" + (audioMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) +
                    " s), longer than the " + request.DurationSeconds + " s this take was priced at.", nameof(request));
            }

            string audioType = string.IsNullOrWhiteSpace(audio.ContentType) ? "audio/wav" : audio.ContentType;
            if (!audioType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("'" + ep.AudioField + "' for '" + ep.Id + "' must be audio; got '" + audioType + "'.", nameof(request));
            }

            body[ep.AudioField] = "data:" + audioType + ";base64," + Convert.ToBase64String(audio.Bytes);
        }

        // ---- the prompt
        bool hasPrompt = !string.IsNullOrWhiteSpace(request.PromptText);
        if (ep.PromptField is null)
        {
            if (hasPrompt)
            {
                throw new ArgumentException(
                    "'" + ep.Id + "' documents no prompt, so one cannot be honoured.", nameof(request));
            }
        }
        else if (!hasPrompt)
        {
            if (ep.PromptRequired)
            {
                throw new ArgumentException(
                    "A prompt is required: '" + ep.Id + "' takes '" + ep.PromptField + "'.", nameof(request));
            }
        }
        else
        {
            if (ep.PromptMaxLength is { } max && request.PromptText.Length > max)
            {
                throw new ArgumentException(
                    "The prompt for '" + ep.Id + "' is at most " + max + " characters; it is " +
                    request.PromptText.Length + ".", nameof(request));
            }

            body[ep.PromptField] = request.PromptText;
        }

        // ---- the duration, in the shape the schema declares
        int duration = request.DurationSeconds;
        if (duration < ep.MinDurationSeconds || duration > ep.MaxDurationSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(request), duration,
                "'" + ep.Id + "' documents a duration from " + ep.MinDurationSeconds + " to " +
                ep.MaxDurationSeconds + " seconds.");
        }

        string wireDuration = ep.DurationForm switch
        {
            FalDurationForm.Integer => duration.ToString(CultureInfo.InvariantCulture),
            FalDurationForm.StringNumber => duration.ToString(CultureInfo.InvariantCulture),
            FalDurationForm.StringSeconds => duration.ToString(CultureInfo.InvariantCulture) + "s",
            _ => throw new NotSupportedException("Unhandled duration form " + ep.DurationForm)
        };

        if (ep.AllowedDurations is { } allowed && !allowed.Contains(wireDuration, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "'" + ep.Id + "' only takes these durations: " + string.Join(", ", allowed) + ". Got " +
                wireDuration + ". The vendor SILENTLY IGNORES an unknown enum value, so a duration it does not " +
                "offer would come back as a different length of clip.", nameof(request));
        }

        // A talking-head route has no duration field: the clip follows the audio (checked above).
        if (ep.SendsDuration)
        {
            body["duration"] = ep.DurationForm == FalDurationForm.Integer
                ? duration
                : wireDuration;
        }

        // ---- the resolution. A null field means the vendor has none: the platform's own token says so
        // and NOTHING is sent, rather than an invented resolution the vendor would ignore.
        string resolution = request.Resolution ?? string.Empty;
        if (ep.ResolutionField is null)
        {
            if (!string.Equals(resolution, FalVideoCatalog.NativeToken, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "'" + ep.Id + "' documents no resolution field at all (the output size follows the request) and " +
                    "is priced flat per second, so the only resolution token it offers is '" +
                    FalVideoCatalog.NativeToken + "'. Got '" + resolution + "'.", nameof(request));
            }
        }
        else
        {
            if (!ep.Resolutions.Contains(resolution, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    "'" + resolution + "' is not a resolution '" + ep.Id + "' documents: " +
                    string.Join(", ", ep.Resolutions) + ". The vendor SILENTLY IGNORES an unknown enum value.",
                    nameof(request));
            }

            body[ep.ResolutionField] = resolution;
        }

        // ---- the aspect ratio, the same rule: no field means the canvas follows the input frame
        string ratio = request.Ratio ?? string.Empty;
        if (ep.RatioField is null && ep.RatioFromFirstFrame)
        {
            // The platform made the first frame at this ratio (the avatar's still), so the canvas follows it: the
            // ratio is checked against what a still can be made at, and nothing is sent.
            if (!ep.AllowedRatios.Contains(ratio, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    "'" + ratio + "' is not a ratio '" + ep.Id + "' can take through its first frame: " +
                    string.Join(", ", ep.AllowedRatios) + ".", nameof(request));
            }
        }
        else if (ep.RatioField is null)
        {
            if (!string.Equals(ratio, FalVideoCatalog.NativeToken, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "'" + ep.Id + "' documents no aspect-ratio field (the canvas follows the input image), so the " +
                    "only ratio token it offers is '" + FalVideoCatalog.NativeToken + "'. Got '" + ratio + "'.",
                    nameof(request));
            }
        }
        else
        {
            if (!ep.AllowedRatios.Contains(ratio, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    "'" + ratio + "' is not an aspect ratio '" + ep.Id + "' documents: " +
                    string.Join(", ", ep.AllowedRatios) + ". The vendor SILENTLY IGNORES an unknown enum value.",
                    nameof(request));
            }

            body[ep.RatioField] = ratio;
        }

        // ---- fields the schema marks required and the platform has no input for
        foreach ((string name, object? value) in ep.SendByDefault)
        {
            if (!body.ContainsKey(name)) body[name] = value;
        }

        return body;
    }

    /// <summary>
    /// One frame as the data URI the vendor's own documentation shows for an inline file (fal-cdn.md,
    /// "Data URIs"): the bytes this service holds, its declared media type, and base64. The content type
    /// is passed through unchanged so a JPEG stays a JPEG; a missing one is sent as <c>image/png</c>,
    /// which is what the image arm already assumes for an unlabelled upload.
    /// </summary>
    private static string DataUri(FalVideoEndpoint ep, string field, VideoGenerationImageInput image)
    {
        if (image.Bytes is not { Length: > 0 })
        {
            throw new ArgumentException(
                "'" + field + "' for '" + ep.Id + "' was given an image with no bytes.", nameof(image));
        }

        string mediaType = string.IsNullOrWhiteSpace(image.ContentType) ? "image/png" : image.ContentType;
        return "data:" + mediaType + ";base64," + Convert.ToBase64String(image.Bytes);
    }

    // ------------------------------------------------------------------ the handle

    /// <summary>
    /// The persisted handle: the vendor's request id first, then its own two tracking URLs. See the class
    /// remarks for why the URLs cannot be rebuilt from the id alone.
    /// </summary>
    private static string EncodeHandle(FalSubmission submission) =>
        submission.RequestId + HandleSeparator + submission.StatusUrl + HandleSeparator + submission.ResponseUrl;

    /// <summary>
    /// Splits a persisted handle back into its three parts. A handle this client did not write is a loud
    /// failure rather than a poll against a guessed URL.
    /// </summary>
    private static (string RequestId, string StatusUrl, string ResponseUrl) DecodeHandle(string handle)
    {
        string[] parts = (handle ?? string.Empty).Split(HandleSeparator);
        if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0)
        {
            throw new InvalidOperationException(
                "'" + ImagePayload.Bounded(handle ?? string.Empty) + "' is not a fal.ai job handle this client " +
                "wrote (expected '{requestId}|{statusUrl}|{responseUrl}'), so the job cannot be polled. A handle " +
                "written by another kind means this row's provider kind changed after the take was submitted.");
        }

        return (parts[0], parts[1], parts[2]);
    }

    /// <summary>
    /// The generated clip's URL, from the endpoint's documented output shape:
    /// <c>{"video": {"url": …}}</c> on every endpoint in the catalogue (each one's own OpenAPI schema,
    /// read 2026-10-16).
    /// </summary>
    private static bool TryReadVideoUrl(JsonElement output, out string? url)
    {
        url = null;
        if (output.ValueKind == JsonValueKind.Object
            && output.TryGetProperty("video", out JsonElement video)
            && video.ValueKind == JsonValueKind.Object)
        {
            url = FalQueueClient.ReadString(video, "url");
        }

        return url is not null;
    }

    /// <summary>Scrubs the API key and truncates, so a vendor message is safe to show a step.</summary>
    private string Bounded(string text)
    {
        if (_apiKey.Length > 0)
        {
            text = text.Replace(_apiKey, "***", StringComparison.Ordinal);
        }

        return text.Length <= MaxVendorMessageChars ? text : text[..MaxVendorMessageChars] + "…";
    }
}
