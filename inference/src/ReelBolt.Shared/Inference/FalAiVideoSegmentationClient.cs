using System.Globalization;
using System.Text.Json;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// fal.ai's text-prompted VIDEO segmentation endpoint, <c>fal-ai/sam-3-1/video-rle</c>, behind
/// <see cref="IVideoSegmentationClient"/> and the shared <see cref="FalQueueClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One endpoint, schema read before sending.</b> <c>video_url</c> (required), <c>prompt</c>, optional
/// <c>point_prompts</c>/<c>box_prompts</c> (<c>object_id, x, y, label, frame_index</c>), <c>max_num_objects</c>
/// 1..128, <c>detection_threshold</c> 0.01..1, <c>apply_mask</c>, <c>return_object_rles</c>,
/// <c>boundingbox_zip</c>. Any other model id is refused: nothing about its schema has been read, and a
/// silently ignored field looks like a working request. The body always pins
/// <c>apply_mask: false</c>, <c>return_object_rles: true</c> and <c>boundingbox_zip: false</c>, and the
/// submit carries <c>X-Fal-Store-IO: 0</c> so the vendor does not retain the clip.
/// </para>
/// <para>
/// <b>Coordinates.</b> The contract's prompts are NORMALISED 0..1; the vendor's schema takes SOURCE
/// PIXELS (the sibling's point-prompt trial used <c>x: 380, y: 880</c> on a 768x1344 clip and hit the
/// object). This client converts against the frame geometry its caller probed, so the conversion is
/// explicit and testable rather than assumed in either direction.
/// </para>
/// <para>
/// <b>Prompt handling.</b> The prompt is control-character-stripped, whitespace-collapsed and capped at
/// <see cref="MaxPromptLength"/> characters. It is sent only to the vendor; it never reaches a command
/// line, an ffmpeg argv or a log line, and neither does the presigned URL.
/// </para>
/// <para>
/// <b>Timeout.</b> <see cref="VideoSegmentationRequest.Timeout"/> bounds the poll loop (the queue client's
/// own budget), not the ambient HttpClient default. The submit and the result fetch are single requests.
/// </para>
/// </remarks>
public sealed class FalAiVideoSegmentationClient : IVideoSegmentationClient
{
    /// <summary>The public queue host; a provider row's endpoint overrides it when the caller builds the HttpClient.</summary>
    public const string DefaultEndpoint = FalQueueClient.DefaultEndpoint;

    /// <summary>The one endpoint this client has a read schema for.</summary>
    public const string Sam31VideoRle = "fal-ai/sam-3-1/video-rle";

    /// <summary>The house cap on a prompt, matching the other generated-prompt caps.</summary>
    public const int MaxPromptLength = 200;

    /// <summary>
    /// The ceiling on one <c>video-rle</c> result document: 64 MiB, read through a running byte counter
    /// so an oversized body is refused instead of buffered.
    /// </summary>
    /// <remarks>
    /// <b>Why 64 MiB, measured.</b> On the committed real responses (768x1344, ~1.0 Mpixel) one object
    /// mask is ~6 400–7 700 characters of RLE text, and a two-object frame is ~20 KB of document
    /// (<c>TestData/prompted-region-effects/sam31-rle</c>). The default cap — 60 s at 30 fps — is 1 800
    /// frames, so a legitimate two-object answer at that geometry is ~36 MB; 64 MiB holds that with room
    /// for the higher resolutions customers actually send. The body is also read into a string and then
    /// parsed into a <see cref="JsonDocument"/>, so peak memory is roughly three times this number —
    /// bounded, and far below <see cref="HttpClient"/>'s own ~2 GiB, which is an engine-wide OOM rather
    /// than a step failure. A legitimate document above this ceiling fails loudly and refundably; it
    /// never hangs a worker, because the frame count, the per-frame slot ceiling and the total frame-slot
    /// bound (all enforced inside <see cref="Sam31VideoRleResponseReader"/>) are what bound the decode
    /// work, not this cap.
    /// </remarks>
    public const int MaxResultBytes = 64 * 1024 * 1024;

    /// <summary>
    /// The submit header that keeps the clip out of fal's store. "0" is fal's documented "do not store
    /// the input/output on the vendor side"; the request is the only place it applies.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> StoreIoHeaders =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Fal-Store-IO"] = "0" };

    /// <summary>Every endpoint id this client can call, for a picker or a test.</summary>
    public static IReadOnlyCollection<string> KnownModels { get; } = [Sam31VideoRle];

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly IVideoSegmentationFrameSizeProbe? _frameSizeProbe;
    private readonly Guid? _providerId;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _pollTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task>? _delay;
    private readonly string _probeModel;

    /// <param name="frameSizeProbe">
    /// The caller's own measurement of the source clip. Required before any HTTP request: the vendor's
    /// answer states no width or height, and guessing one would shear every mask. See
    /// <see cref="IVideoSegmentationFrameSizeProbe"/>.
    /// </param>
    /// <param name="providerId">The provider row id, used only inside the non-secret fingerprint.</param>
    /// <param name="probeModel">The endpoint a connection test checks; defaults to <see cref="Sam31VideoRle"/>.</param>
    public FalAiVideoSegmentationClient(
        HttpClient httpClient,
        string apiKey,
        IVideoSegmentationFrameSizeProbe? frameSizeProbe = null,
        Guid? providerId = null,
        TimeSpan? pollInterval = null,
        TimeSpan? pollTimeout = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        string? probeModel = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _frameSizeProbe = frameSizeProbe;
        _providerId = providerId;
        _pollInterval = pollInterval ?? FalQueueClient.DefaultPollInterval;
        _pollTimeout = pollTimeout ?? FalQueueClient.DefaultPollTimeout;
        _delay = delay;
        _probeModel = string.IsNullOrWhiteSpace(probeModel) ? Sam31VideoRle : probeModel;
    }

    /// <inheritdoc />
    public async Task<VideoSegmentationResult> SegmentVideoAsync(VideoSegmentationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        string model = string.IsNullOrWhiteSpace(request.Model) ? Sam31VideoRle : request.Model;
        RequireSchema(model);
        RequireVideoUrl(request.VideoUrl);

        if (_frameSizeProbe is null)
        {
            // Fail closed BEFORE spending: a missing geometry is not something the vendor's answer can
            // repair, and a guessed width produces a plausible-looking sheared mask.
            throw new InvalidOperationException(
                "No IVideoSegmentationFrameSizeProbe is configured for '" + model + "'. The vendor's response " +
                "states no frame width or height, so the RLE cannot be decoded without the caller's own probe " +
                "of the clip. Refusing to guess a width.");
        }

        VideoSegmentationFrameSize size = await _frameSizeProbe.ProbeAsync(request.VideoUrl, ct);

        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new VideoSegmentationBadResponseException(
                VideoSegmentationBadResponseException.FailureCode + ": the frame-size probe answered " +
                size.Width + "x" + size.Height + " for the source clip, which cannot index a mask");
        }

        string prompt = SanitizePrompt(request.Prompt);
        if (prompt.Length == 0 && (request.PointPrompts is null or { Count: 0 }) && (request.BoxPrompts is null or { Count: 0 }))
        {
            throw new ArgumentException(
                "'" + model + "' needs either a non-empty prompt or at least one point/box prompt. An empty " +
                "request would segment nothing and still be billed.", nameof(request));
        }

        Dictionary<string, object?> body = new()
        {
            ["video_url"] = request.VideoUrl.AbsoluteUri
        };

        if (prompt.Length > 0) body["prompt"] = prompt;

        if (request.PointPrompts is { Count: > 0 } pointPrompts)
        {
            body["point_prompts"] = pointPrompts.Select(p => PointBody(p, size)).ToList();
        }

        if (request.BoxPrompts is { Count: > 0 } boxPrompts)
        {
            body["box_prompts"] = boxPrompts.Select(b => BoxBody(b, size)).ToList();
        }

        int maxObjects = SegmentationLimits.ClampObjects(request.MaxObjects);
        body["max_num_objects"] = maxObjects;
        body["detection_threshold"] = SegmentationLimits.ClampDetectionThreshold(request.DetectionThreshold);
        body["apply_mask"] = false;
        body["return_object_rles"] = true;
        body["boundingbox_zip"] = false;

        // Per-request timeout: the queue's poll budget, not the ambient HttpClient default.
        FalQueueClient queue = new(
            _httpClient,
            _apiKey,
            downloadClient: null,
            _pollInterval,
            request.Timeout ?? _pollTimeout,
            _delay,
            StoreIoHeaders);

        FalQueueResult queued = await queue.SubmitAndPollAsync(model, body, ct, MaxResultBytes);

        try
        {
            // The frame count is a precondition the reader checks before the first run is decoded. The
            // object slots are NOT: the vendor returns more than this request asked for (measured, T-015),
            // so the reader bounds the work and reports the slots it saw instead of comparing them with
            // max_num_objects.
            return Sam31VideoRleResponseReader.Read(
                queued.Output, model, size.Width, size.Height, size.FrameCount, maxObjects, Fingerprint(model));
        }
        catch (VideoSegmentationBadResponseException ex)
        {
            throw new VideoSegmentationBadResponseException(
                ex.Message + " Request id: " + (string.IsNullOrEmpty(queued.RequestId) ? "(none)" : queued.RequestId) + ".");
        }
    }

    /// <summary>
    /// A no-cost connection test: it submits the endpoint with an EMPTY body. <c>video_url</c> is required,
    /// so fal's schema validation refuses the request before a runner is dispatched — nothing is
    /// generated and nothing is billed. A 404 proves the model id is not routed; a 401/403 (or an
    /// authorization sentence) proves the key was rejected; any other validation refusal proves the
    /// endpoint exists and the key got past authentication.
    /// </summary>
    public async Task<VideoSegmentationProbeResult> ProbeAsync(CancellationToken ct)
    {
        FalQueueClient queue = new(_httpClient, _apiKey, null, _pollInterval, _pollTimeout, _delay);
        FalProbeOutcome outcome = await queue.ProbeAsync(_probeModel, ct);
        return new VideoSegmentationProbeResult(outcome.IsOk, outcome.Reason);
    }

    /// <summary>
    /// The fingerprint the result carries: kind, provider row id and model, never a key, a token or the
    /// presigned URL.
    /// </summary>
    public static string Fingerprint(Guid? providerId, string model) =>
        "FalAi|" + (providerId?.ToString("N", CultureInfo.InvariantCulture) ?? "-") + "|" + model;

    private string Fingerprint(string model) => Fingerprint(_providerId, model);

    private static void RequireSchema(string model)
    {
        if (string.Equals(model, Sam31VideoRle, StringComparison.Ordinal)) return;

        throw new ArgumentException(
            "No schema has been read for video-segmentation endpoint '" + model + "'. This client refuses to send " +
            "undocumented fields; the endpoints it can call are: " + string.Join(", ", KnownModels) + ".",
            nameof(model));
    }

    private static void RequireVideoUrl(Uri? videoUrl)
    {
        if (videoUrl is null
            || !videoUrl.IsAbsoluteUri
            || (videoUrl.Scheme != Uri.UriSchemeHttps && videoUrl.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException(
                "The source must be an absolute http(s) URL — the short-TTL presigned GET the caller minted. " +
                "A storage key, a bucket name or a relative path is not something this client can send.",
                nameof(videoUrl));
        }
    }

    /// <summary>
    /// Control characters stripped, whitespace collapsed, trimmed, capped. Deliberately no ellipsis: the
    /// result is a substring of the user's own words, not a new sentence the model might read as content.
    /// </summary>
    public static string SanitizePrompt(string? prompt)
    {
        string stripped = GeneratedPromptSanitizer.StripControlCharacters(prompt).Trim();
        if (stripped.Length == 0) return string.Empty;

        System.Text.StringBuilder builder = new(stripped.Length);
        bool lastWasSpace = false;
        foreach (char c in stripped)
        {
            bool space = char.IsWhiteSpace(c);
            if (space && lastWasSpace) continue;
            builder.Append(space ? ' ' : c);
            lastWasSpace = space;
        }

        string collapsed = builder.ToString().Trim();
        return collapsed.Length <= MaxPromptLength ? collapsed : collapsed[..MaxPromptLength].TrimEnd();
    }

    private static Dictionary<string, object?> PointBody(VideoSegmentationPointPrompt prompt, VideoSegmentationFrameSize size)
    {
        Dictionary<string, object?> body = new();
        if (prompt.ObjectId is { } objectId) body["object_id"] = RequireObjectId(objectId);
        body["x"] = ToPixels(prompt.X, size.Width, "point x");
        body["y"] = ToPixels(prompt.Y, size.Height, "point y");
        if (!string.IsNullOrWhiteSpace(prompt.Label)) body["label"] = RequireLabel(prompt.Label);
        body["frame_index"] = RequireFrameIndex(prompt.FrameIndex, size);
        return body;
    }

    private static Dictionary<string, object?> BoxBody(VideoSegmentationBoxPrompt prompt, VideoSegmentationFrameSize size)
    {
        if (prompt.Width <= 0 || prompt.Height <= 0 || prompt.X + prompt.Width > 1.0000001 || prompt.Y + prompt.Height > 1.0000001)
        {
            throw new ArgumentException(
                "Box prompt [" + prompt.X + ", " + prompt.Y + ", " + prompt.Width + ", " + prompt.Height +
                "] is not a normalised rectangle inside the frame.");
        }

        Dictionary<string, object?> body = new();
        if (prompt.ObjectId is { } objectId) body["object_id"] = RequireObjectId(objectId);
        // The vendor schema (docs/fal-ai-models.md, box_prompts) is x_min/y_min/x_max/y_max, not x/y/width/height.
        body["x_min"] = ToPixels(prompt.X, size.Width, "box x_min");
        body["y_min"] = ToPixels(prompt.Y, size.Height, "box y_min");
        body["x_max"] = ToPixels(Math.Min(1.0, prompt.X + prompt.Width), size.Width, "box x_max");
        body["y_max"] = ToPixels(Math.Min(1.0, prompt.Y + prompt.Height), size.Height, "box y_max");
        body["frame_index"] = RequireFrameIndex(prompt.FrameIndex, size);
        return body;
    }

    private static int ToPixels(double normalised, int extent, string what)
    {
        if (double.IsNaN(normalised) || normalised < 0 || normalised > 1)
        {
            throw new ArgumentException(
                "The " + what + " is " + normalised.ToString(CultureInfo.InvariantCulture) +
                ", outside the normalised 0..1 the contract uses. Refusing to guess whether it was meant as " +
                "a fraction or a source pixel.");
        }

        // 0..1 over the INCLUSIVE pixel range: 0.5 of 768 is pixel 384 of 0..767, and 1.0 is the last pixel.
        return (int)Math.Round(normalised * (extent - 1), MidpointRounding.AwayFromZero);
    }

    private static int RequireObjectId(int objectId)
    {
        if (objectId < 0)
        {
            throw new ArgumentException("A prompt's object_id cannot be negative (" + objectId + ").");
        }

        return objectId;
    }

    private static int RequireLabel(string label)
    {
        string trimmed = label.Trim();
        if (trimmed is not ("0" or "1"))
        {
            throw new ArgumentException(
                "The vendor's point/box 'label' is a positive/negative flag: '1' includes, '0' excludes. '" +
                label + "' is neither, and sending a word would be silently ignored.");
        }

        return trimmed == "1" ? 1 : 0;
    }

    private static int RequireFrameIndex(int frameIndex, VideoSegmentationFrameSize size)
    {
        if (frameIndex < 0)
        {
            throw new ArgumentException("A prompt's frame_index cannot be negative (" + frameIndex + ").");
        }

        if (size.FrameCount is { } frames && frameIndex >= frames)
        {
            throw new ArgumentException(
                "A prompt names frame " + frameIndex + ", but the clip has " + frames + " frames (0.." +
                (frames - 1) + ").");
        }

        return frameIndex;
    }
}
