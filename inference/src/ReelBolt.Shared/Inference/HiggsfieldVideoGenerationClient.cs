using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Client for the Higgsfield video generation API (<c>https://api.higgsfield.ai</c>).
/// <para>
/// Over plain <see cref="HttpClient"/> + <see cref="System.Text.Json"/> — deliberately no vendor
/// SDK and no new NuGet package, exactly like <see cref="MiniMaxVideoGenerationClient"/>.
/// </para>
/// <para>
/// Higgsfield is a multi-model AGGREGATOR: a submission is addressed by the vendor's per-workflow
/// "Endpoint ID" (e.g. <c>kling-video/v3.0/std/text-to-video</c>), which
/// <see cref="VideoGenerationRequest.Model"/> carries for this kind. Verified facts and their
/// primary sources:
/// </para>
/// <list type="bullet">
/// <item>Base URL <c>https://api.higgsfield.ai</c>; auth is
/// <c>Authorization: Key &lt;KEY_ID&gt;:&lt;KEY_SECRET&gt;</c> — a two-part credential, NOT a
/// Bearer token.</item>
/// <item>Submit is endpoint-addressed — <c>POST https://api.higgsfield.ai/&lt;endpoint-id&gt;</c> —
/// and its body is <b>FLAT</b>, with the model parameters at the TOP LEVEL and NO <c>input</c>
/// wrapper, exactly as the vendor's own cURL on the model page prints it:
/// <c>--url https://api.higgsfield.ai/kling-video/v3.0/std/text-to-video … --data '{"prompt": …}'</c>
/// (https://docs.higgsfield.ai/docs/models/kling-3/standard-text-to-video.md). The
/// <c>{"input": {...}}</c> envelope this comment used to cite comes from that SAME page's
/// TypeScript snippet — <c>higgsfield.subscribe(endpoint, { input: {...}, withPolling: true })</c>
/// — where <c>input</c> and <c>withPolling</c> are SIBLINGS in the <b>client-side</b> SDK's own
/// options object and the SDK is what writes the body. <c>input</c> is a client-side option, not
/// the wire format, and the page's own cURL contradicts it. The wrapper is neither sent nor
/// fallback-attempted; see the body-construction site in <see cref="SubmitAsync"/>.</item>
/// <item>Poll: <c>GET /requests/{request_id}/status</c>
/// (https://docs.higgsfield.ai/docs/api-reference/requests/get-request-status.md).</item>
/// <item>Presigned upload: <c>POST /files/generate-upload-url</c>, then a <c>PUT</c> to the
/// returned <c>upload_url</c> carrying the returned <c>upload_headers</c> verbatim and
/// <b>no</b> Higgsfield credentials
/// (https://docs.higgsfield.ai/docs/concepts/file-uploads.md).</item>
/// </list>
/// <para>
/// Deliberately NOT coded (unverified): per-model/per-second pricing, the model roster's
/// capabilities, camera presets, Soul ID, lipsync, upscaling, motion transfer, webhooks (polling
/// is the v1 posture), and the ToS data-terms claim. Webhooks in particular are a
/// <c>?hf_webhook=&lt;url&gt;</c> query parameter, not a header, and are not implemented here.
/// </para>
/// <para>
/// <b>PRICING.</b> Higgsfield publishes no per-second or per-model price table, so
/// there is nothing to compile in and <see cref="Describe"/> has nothing to return. The only
/// authoritative price is the account- and parameter-specific authenticated
/// <c>POST /estimate/{endpoint_id}</c>, which returns <c>{credits, usd}</c>
/// (https://docs.higgsfield.ai/docs/concepts/billing-and-retention). This client therefore implements
/// <see cref="IVideoGenerationPriceOracle"/>: <see cref="QuoteAsync"/> makes that one bounded, cached
/// call per distinct configuration.
/// </para>
/// <para>
/// The purity contract, stated in full because it changed here and is easy to break by accident:
/// <see cref="Describe"/> is still pure and still makes no network call — it remains the offline
/// answer, and for Higgsfield that answer is "unpriced, therefore not offered". The network is
/// reachable from exactly ONE place, <see cref="QuoteAsync"/>, and from nowhere else in this class's
/// pricing surface. See the cache and failure semantics on <see cref="QuoteAsync"/>.
/// </para>
/// </summary>
public class HiggsfieldVideoGenerationClient : IVideoGenerationClient, IVideoGenerationPriceOracle, IVideoImageInputSupport
{
    /// <summary>Default bound on <see cref="ProbeAsync"/> so an unreachable endpoint cannot hang an admin request.</summary>
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Default bound on the SINGLE HTTP attempt <see cref="QuoteAsync"/> makes. Deliberately its own
    /// short timeout, not the provider row's <c>TimeoutSeconds</c>: that value exists to bound a
    /// multi-minute generation poll, and letting a price lookup inherit it would let a hanging vendor
    /// stall a whole plan's pre-submit estimate. Independent of the caller's token, which also bounds
    /// the call.
    /// </summary>
    public static readonly TimeSpan DefaultPriceOracleTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a SUCCESSFUL quote is served from cache. Ten minutes: long enough that pricing a whole
    /// plan costs one call per distinct configuration and that two runs of the same plan minutes apart
    /// do not re-price, short enough that a vendor price change is picked up within one working cadence
    /// rather than never.
    /// </summary>
    public static readonly TimeSpan PriceQuoteTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a FAILED quote is remembered. Shorter than <see cref="PriceQuoteTtl"/> on purpose: a
    /// failure must not be sticky enough to hide a recovered vendor, but it must be remembered at all,
    /// so a plan that prices the same configuration repeatedly against a broken oracle makes one
    /// attempt per minute rather than one per take.
    /// </summary>
    public static readonly TimeSpan PriceQuoteFailureTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Hard ceiling on cached quotes. A hot path may ask about many configurations; an unbounded
    /// dictionary keyed by vendor parameters would grow with every distinct one. On overflow the
    /// expired entries go first, then the oldest.
    /// </summary>
    public const int MaxPriceQuoteCacheEntries = 256;

    /// <summary>
    /// The literal stored credential that means "resolve from the environment". This is an
    /// EXPLICIT, admin-typed opt-in sentinel — it is <b>not</b> the ambient-credential fallback the
    /// project forbids: it is only ever reached when an administrator deliberately saved this exact
    /// string as the provider's key, and every other value (including an empty one) is used
    /// verbatim or rejected. Nothing on the non-sentinel path reads an environment variable.
    /// </summary>
    public const string EnvironmentSentinel = "env:";

    /// <summary>Sent by every request: the vendor's server SDK sends <c>higgsfield-server-js/2.0</c>.</summary>
    private const string UserAgent = "ReelBolt/1.0 (higgsfield-video-generation)";

    private const string UploadUrlPath = "/files/generate-upload-url";
    private const string RequestsPathPrefix = "/requests/";
    private const int MaxVendorMessageChars = 160;

    /// <summary>What one endpoint's own documented input schema says about input images.</summary>
    private enum HiggsfieldImageShape
    {
        /// <summary>A required first frame: the schema documents a flat <c>image_url</c> and nothing else.</summary>
        FirstFrame,

        /// <summary>A required first frame plus an optional last one: <c>image_url</c> and <c>last_image_url</c>.</summary>
        FirstAndLastFrame
    }

    /// <summary>
    /// The endpoints whose image input is VERIFIED, keyed by the endpoint id with its leading slash
    /// stripped, each mapped to the schema its own model page publishes (fetched and re-read
    /// 2026-10-09):
    /// <list type="bullet">
    /// <item><c>kling-video/v3.0/std/image-to-video</c>, <c>…/pro/image-to-video</c> and
    /// <c>…/4k/image-to-video</c>: a flat <c>image_url</c> (required) plus an optional flat
    /// <c>last_image_url</c>.</item>
    /// <item><c>kling-video/v3.0-turbo/image-to-video</c>: a flat <c>image_url</c> only — that page
    /// documents no <c>last_image_url</c>, so a last frame is refused rather than sent to a field the
    /// vendor never published.</item>
    /// </list>
    /// <para>
    /// A mapping rather than a rule, deliberately. The vendor says "Endpoint fields vary by model and
    /// workflow. Use the exact schema documented on the selected model page"
    /// (https://docs.higgsfield.ai/docs/models), so a rule such as "any path ending in
    /// /image-to-video" would be a blind global rename dressed up as a rule — exactly the reading this
    /// file already had to correct once for the <c>input</c> wrapper. An endpoint that is not listed
    /// here carries no image input: a request with images is REFUSED, with the reason naming the gap,
    /// rather than sent with a guessed field name.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, HiggsfieldImageShape> ImageEndpointsByShape = new(StringComparer.Ordinal)
    {
        ["kling-video/v3.0/std/image-to-video"] = HiggsfieldImageShape.FirstAndLastFrame,
        ["kling-video/v3.0/pro/image-to-video"] = HiggsfieldImageShape.FirstAndLastFrame,
        ["kling-video/v3.0/4k/image-to-video"] = HiggsfieldImageShape.FirstAndLastFrame,
        ["kling-video/v3.0-turbo/image-to-video"] = HiggsfieldImageShape.FirstFrame
    };

    /// <summary>
    /// The endpoints VERIFIED to be text-only, so an image offered to one of them is refused as "this
    /// workflow documents no image input" instead of as an unmapped endpoint. Their pages list
    /// <c>prompt</c>, <c>duration</c> and <c>aspect_ratio</c> and no image member at all (fetched
    /// 2026-10-09). The distinction is for the MESSAGE only — both answers are
    /// <see cref="VideoImageInputSupport.None"/>.
    /// </summary>
    private static readonly HashSet<string> TextOnlyEndpoints = new(StringComparer.Ordinal)
    {
        "kling-video/v3.0/std/text-to-video",
        "kling-video/v3.0/pro/text-to-video",
        "kling-video/v3.0/4k/text-to-video",
        "kling-video/v3.0-turbo/text-to-video"
    };

    /// <summary>
    /// Every endpoint id this client knows by name (the image mapping plus the verified text-only set), read-only,
    /// so the model-catalog seed guard can refuse a Higgsfield code the client has never heard of.
    /// </summary>
    public static IReadOnlyCollection<string> KnownEndpointIds { get; } =
        ImageEndpointsByShape.Keys.Concat(TextOnlyEndpoints).ToArray();

    /// <summary>
    /// The image media types the vendor documents for upload
    /// (https://docs.higgsfield.ai/docs/concepts/file-uploads.md). Validated BEFORE any network call,
    /// so an unsupported type never reaches the presign endpoint.
    /// </summary>
    private static readonly HashSet<string> SupportedImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/webp",
        "image/gif"
    };

    /// <summary>
    /// The header names <see cref="HttpContentHeaders"/> owns, so a vendor-returned
    /// <c>upload_headers</c> entry is routed to the content rather than the request. Verified
    /// against the documented set; an unlisted name falls back to the request collection and then to
    /// the content collection before failing loudly.
    /// </summary>
    private static readonly HashSet<string> ContentHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Allow",
        "Content-Disposition",
        "Content-Encoding",
        "Content-Language",
        "Content-Length",
        "Content-Location",
        "Content-MD5",
        "Content-Range",
        "Content-Type",
        "Expires",
        "Last-Modified"
    };

    private readonly HttpClient _httpClient;
    private readonly HttpClient _downloadClient;
    private readonly string _keyId;
    private readonly string _keySecret;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeSpan _priceOracleTimeout;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// The quote cache. One entry per distinct (model, resolution, ratio, duration) configuration,
    /// holding either a successful quote or the failure that replaced it. Guarded by
    /// <see cref="_priceCacheLock"/> rather than a concurrent collection because the overflow policy
    /// needs to inspect every entry (drop expired, then drop oldest) and that decision must be atomic
    /// with the insert.
    /// </summary>
    private readonly Dictionary<string, PriceCacheEntry> _priceCache = new(StringComparer.Ordinal);
    private readonly Lock _priceCacheLock = new();

    /// <summary>
    /// Builds a client from the STORED credential string (what the admin typed into the provider
    /// row), deriving the <c>KEY_ID</c>/<c>KEY_SECRET</c> pair via <see cref="ResolveCredentials"/>.
    /// An empty, whitespace-only, colon-less or sentinel-without-environment value throws — there is
    /// no ambient fallback.
    /// </summary>
    /// <param name="httpClient">The transport. Must not be null.</param>
    /// <param name="storedCredential">The saved credential, or the <see cref="EnvironmentSentinel"/>.</param>
    /// <param name="probeTimeout">Overrides <see cref="DefaultProbeTimeout"/>; a test seam.</param>
    /// <param name="priceOracleTimeout">Overrides <see cref="DefaultPriceOracleTimeout"/>; a test seam.</param>
    /// <param name="timeProvider">
    /// The clock the quote cache's TTLs are measured against. Defaults to the system clock; a test
    /// seam, in the same spirit as <see cref="ResolveCredentials"/>'s <c>environmentLookup</c>, so a
    /// test can prove expiry without sleeping.
    /// </param>
    public HiggsfieldVideoGenerationClient(
        HttpClient httpClient,
        string storedCredential,
        TimeSpan? probeTimeout = null,
        TimeSpan? priceOracleTimeout = null,
        TimeProvider? timeProvider = null,
        HttpClient? downloadClient = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _downloadClient = downloadClient ?? httpClient;
        (_keyId, _keySecret) = ResolveCredentials(storedCredential);
        _probeTimeout = probeTimeout ?? DefaultProbeTimeout;
        _priceOracleTimeout = priceOracleTimeout ?? DefaultPriceOracleTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Derives <c>(KeyId, KeySecret)</c> from the stored credential string.
    /// </summary>
    /// <param name="storedCredential">
    /// What the admin saved: either the literal <see cref="EnvironmentSentinel"/>, or the
    /// <c>KEY_ID:KEY_SECRET</c> pair.
    /// </param>
    /// <param name="environmentLookup">
    /// A test seam only — it exists so a test can prove the <c>env:</c> branch without mutating the
    /// process-global environment (racy under xUnit's parallel collections). Production callers
    /// never pass it, and it defaults to <see cref="Environment.GetEnvironmentVariable"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The stored credential is null/empty/whitespace-only; or it is the <c>env:</c> sentinel and no
    /// environment source supplied a usable value; or it is neither and does not have the
    /// <c>KEY_ID:KEY_SECRET</c> shape with both halves non-empty. Messages never echo the value.
    /// </exception>
    public static (string KeyId, string KeySecret) ResolveCredentials(
        string storedCredential,
        Func<string, string?>? environmentLookup = null)
    {
        if (string.IsNullOrWhiteSpace(storedCredential))
        {
            throw new ArgumentException(
                "A Higgsfield credential is required. Save either the literal sentinel 'env:' (to read " +
                "the credential from the environment) or the 'KEY_ID:KEY_SECRET' pair issued by the " +
                "vendor. No ambient environment variable is read for any other value.",
                nameof(storedCredential));
        }

        if (string.Equals(storedCredential, EnvironmentSentinel, StringComparison.Ordinal))
        {
            Func<string, string?> lookup = environmentLookup ?? Environment.GetEnvironmentVariable;

            // HF_CREDENTIALS is the preferred single-variable form; HF_API_KEY + HF_API_SECRET is the
            // split form. Reading the environment here is the EXPLICIT admin opt-in the sentinel
            // represents — not the ambient fallback the project forbids, which would apply to a
            // blank or undecryptable stored key without the admin asking for it.
            string? combined = lookup("HF_CREDENTIALS");
            if (!string.IsNullOrWhiteSpace(combined))
            {
                return SplitPair(combined);
            }

            string? keyId = lookup("HF_API_KEY");
            string? keySecret = lookup("HF_API_SECRET");
            if (!string.IsNullOrWhiteSpace(keyId) && !string.IsNullOrWhiteSpace(keySecret))
            {
                return (keyId, keySecret);
            }

            throw new ArgumentException(
                "The stored credential is the literal 'env:' sentinel, but no usable value was found. " +
                "Set HF_CREDENTIALS to 'KEY_ID:KEY_SECRET', or set both HF_API_KEY and HF_API_SECRET.",
                nameof(storedCredential));
        }

        return SplitPair(storedCredential);
    }

    /// <summary>
    /// Splits on the FIRST colon only — a secret may legitimately contain a colon. Never echoes the
    /// value it was given.
    /// </summary>
    private static (string KeyId, string KeySecret) SplitPair(string value)
    {
        int separator = value.IndexOf(':');
        if (separator < 0)
        {
            throw new ArgumentException(
                "The Higgsfield credential must be in the form 'KEY_ID:KEY_SECRET'; the stored value " +
                "contains no ':' separator.");
        }

        string keyId = value[..separator];
        string keySecret = value[(separator + 1)..];
        if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(keySecret))
        {
            throw new ArgumentException(
                "The Higgsfield credential must be in the form 'KEY_ID:KEY_SECRET' with a non-empty " +
                "key id and a non-empty key secret.");
        }

        return (keyId, keySecret);
    }

    /// <summary>
    /// Never renders either half of the credential — the record-style <c>ToString()</c> would
    /// otherwise put the API key one <c>LogInformation("... {Client}", client)</c> away from the log
    /// stream (same reasoning as <see cref="ResolvedVideoGenerationProvider.ToString"/>).
    /// </summary>
    public override string ToString() => $"HiggsfieldVideoGenerationClient({_httpClient.BaseAddress})";

    /// <summary>
    /// Normalises <see cref="VideoGenerationRequest.Model"/> into a SAFE relative path below the
    /// vendor's API host, for the path-addressed <c>POST &lt;base&gt;/&lt;endpoint-id&gt;</c>.
    /// <para>
    /// The validation is load-bearing, not cosmetic. <see cref="HttpClient"/> treats a path that
    /// begins with <c>//</c> as an AUTHORITY OVERRIDE, so <c>"//evil.com/steal"</c> resolves against
    /// the base address to <c>https://evil.com/steal</c>; a "starts with exactly one slash" test
    /// would see the leading slash, add nothing, and let it through. Every request built by
    /// <see cref="CreateRequest"/> carries <c>Authorization: Key &lt;KEY_ID&gt;:&lt;KEY_SECRET&gt;</c>,
    /// so that would hand the credential — and the prompt — to an attacker-controlled host.
    /// <c>request.Model</c> is step-config-authored, not a trusted admin-only value: an LLM-authored
    /// or imported workflow can set it. MiniMax carries no equivalent risk because its path is a
    /// constant.
    /// </para>
    /// </summary>
    /// <param name="model">The Endpoint ID, with or without leading slashes.</param>
    /// <param name="argumentName">The caller's parameter name, for the thrown exception.</param>
    /// <exception cref="ArgumentException">
    /// The value is empty once its leading slashes are stripped, or it contains a <c>//</c> or
    /// <c>://</c> sequence or a <c>..</c> path segment. The message names the offending SHAPE only —
    /// it never echoes a value that could be mistaken for a credential.
    /// </exception>
    private static string NormalizeEndpointId(string? model, string argumentName)
    {
        // Strip leading slashes FIRST, so "//evil.com/steal" cannot keep its authority-override
        // meaning by satisfying a "starts with one slash" test.
        string trimmed = (model ?? string.Empty).Trim().TrimStart('/');

        bool hasEmptyValue = trimmed.Length == 0;
        bool hasSchemeOrAuthority = trimmed.Contains("//", StringComparison.Ordinal)
            || trimmed.Contains("://", StringComparison.Ordinal);
        bool hasParentSegment = trimmed.Split('/').Any(segment => segment == "..");

        if (hasEmptyValue || hasSchemeOrAuthority || hasParentSegment)
        {
            throw new ArgumentException(
                "The model must be the vendor's Endpoint ID — a relative path below the API host, " +
                "for example 'kling-video/v3.0/std/text-to-video'. The supplied value is empty once " +
                "its leading slashes are stripped, or contains a '//' or '://' sequence or a '..' " +
                "path segment, either of which would change which host the request is sent to.",
                argumentName);
        }

        // Exactly one leading slash: enough to keep it on the base host, never two (authority
        // override) and never zero (which HttpClient would resolve relative to the base address).
        return "/" + trimmed;
    }

    /// <summary>
    /// The endpoint id as the image mapping is keyed: exactly what <see cref="NormalizeEndpointId"/>
    /// accepts, with its single leading slash stripped. One place derives it, so a query and a submit
    /// can never index the mapping differently.
    /// </summary>
    private static string RelativeEndpointId(string? model, string argumentName) =>
        NormalizeEndpointId(model, argumentName).TrimStart('/');

    /// <summary>The declared image input for an already-normalised endpoint id — offline, and by exact id only.</summary>
    private static VideoImageInputSupport DeclaredSupportFor(string relativeEndpointId) =>
        ImageEndpointsByShape.TryGetValue(relativeEndpointId, out HiggsfieldImageShape shape)
            ? shape switch
            {
                HiggsfieldImageShape.FirstAndLastFrame => VideoImageInputSupport.FirstAndLastFrame,
                _ => VideoImageInputSupport.FirstFrame
            }
            : VideoImageInputSupport.None;

    /// <summary>
    /// What the mapped endpoints accept, answered from the mapping with no network call, so a caller
    /// can refuse before the budget gate reserves anything. An endpoint outside the mapping declares
    /// <see cref="VideoImageInputSupport.None"/>: "not verified" and "documented text-only" are the
    /// same answer to a caller that must not invent one.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not a usable endpoint id.</exception>
    public VideoImageInputSupport ImageInputSupport(string model) =>
        DeclaredSupportFor(RelativeEndpointId(model, nameof(model)));

    /// <summary>
    /// The same answer as <see cref="ImageInputSupport"/>, static, for code that holds no client (the subject path
    /// table and its text-only siblings, <see cref="VideoSubjectPaths"/>).
    /// </summary>
    /// <exception cref="ArgumentException">The value is not a usable endpoint id.</exception>
    public static VideoImageInputSupport DeclaredImageInputSupportOf(string model) =>
        DeclaredSupportFor(RelativeEndpointId(model, nameof(model)));

    /// <summary>
    /// Why an endpoint that declares no image input cannot carry an image. Two cases, told apart
    /// because they need different fixes: a mapped text-to-video workflow (use its image-to-video
    /// sibling), and an endpoint whose image schema nobody has verified (add it to the mapping only
    /// against that model page's own schema).
    /// </summary>
    private static string ExplainNoImageInput(string relativeEndpointId)
    {
        if (TextOnlyEndpoints.Contains(relativeEndpointId))
        {
            return $"'{relativeEndpointId}' is the vendor's TEXT-TO-VIDEO workflow: its documented input " +
                "schema carries no image member at all, so an input image cannot be sent to it " +
                "(https://docs.higgsfield.ai/docs/models/kling-3, fetched 2026-10-09). Use the same " +
                "variant's image-to-video endpoint — for example 'kling-video/v3.0/std/image-to-video' — " +
                "which documents a flat image_url.";
        }

        return $"The image parameter mapping for '{relativeEndpointId}' is not verified, so an input image " +
            "is refused rather than guessed at. The vendor states that endpoint fields vary by model and " +
            "workflow and to use the exact schema on the selected model page " +
            "(https://docs.higgsfield.ai/docs/models). The endpoints mapped so far, all carrying the flat " +
            "image_url (and last_image_url where listed), are: " +
            string.Join(", ", ImageEndpointsByShape.Keys) + ".";
    }

    public async Task<ExternalJobHandle> SubmitAsync(VideoGenerationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        // No reference mode is declared for this client (IVideoReferenceInputSupport): a person's reference material is
        // refused rather than silently dropped, so a step asked to show someone never shows nobody.
        VideoReferenceInput.EnsureNoReferences(request, nameof(request));

        // request.Model is the vendor's Endpoint ID for this kind, and submission is path-addressed:
        // POST <base>/<endpoint-id>. Normalised and validated before a path is built.
        // Deliberately the FIRST thing after the null check, AHEAD of the image upload loop below:
        // the id is validated before anything leaves the process, so a submission whose endpoint can
        // never be addressed is rejected without ever uploading the caller's frames. This runs before
        // the per-model input schema (which varies by endpoint and cannot be checked locally); the id
        // can be checked locally and costs no egress, so it is checked first.
        string endpointId = NormalizeEndpointId(request.Model, nameof(request));
        string relativeEndpointId = endpointId.TrimStart('/');

        // WHAT THIS ENDPOINT ACCEPTS, resolved before anything is uploaded, because the upload is the
        // one leg this method cannot take back: the vendor's presigned URL is a one-way door that PUTs
        // the caller's own frame bytes to the vendor's storage. So an endpoint whose schema carries no
        // image input is refused HERE, with no request of any kind, and the count rule is the shared
        // one (a first frame is Images[0], a last frame Images[1]; more than two is never a shape a
        // video model documents).
        int imageCount = request.Images?.Count ?? 0;
        VideoImageInputSupport imageSupport = DeclaredSupportFor(relativeEndpointId);
        if (imageCount > 0 && imageSupport == VideoImageInputSupport.None)
        {
            throw new NotSupportedException(ExplainNoImageInput(relativeEndpointId));
        }

        VideoImageInput.Validate(imageSupport, request.Model, imageCount);

        // Each image is transported by the vendor's own presigned upload and referenced by the
        // returned public_url. Deliberately NOT a Garage presigned URL (the vendor's storage pulls
        // the object; it is not our bucket and our signature means nothing to it) and never base64
        // (an inline payload of that size is not the documented input shape and would blow up the
        // request body).
        var imageUrls = new List<string>(imageCount);
        if (request.Images is { Count: > 0 } images)
        {
            foreach (VideoGenerationImageInput image in images)
            {
                imageUrls.Add(await UploadInputImageAsync(image, ct));
            }
        }

        // ---------------------------------------------------------------------------------------
        // The submit REQUEST body, from the vendor's own primary source — the model page's cURL.
        //
        // PRIMARY SOURCE (fetched 2026-10-03):
        // https://docs.higgsfield.ai/docs/models/kling-3/standard-text-to-video.md — "Quick start":
        //
        //   curl --request POST \
        //     --url https://api.higgsfield.ai/kling-video/v3.0/std/text-to-video \
        //     --header "Authorization: Key ${HF_API_KEY_ID}:${HF_API_KEY_SECRET}" \
        //     --header "Content-Type: application/json" \
        //     --header "Idempotency-Key: ${IDEMPOTENCY_KEY}" \
        //     --data '{ "prompt": "A slow cinematic tracking shot along a sunlit coastal road." }'
        //
        // FLAT at the top level: the model parameters ARE the body. There is no "input" wrapper.
        //
        // WHY THE WRAPPER WAS HERE, AND WHY IT WAS WRONG. The same page prints a TypeScript snippet
        // one code block below the cURL:
        //
        //   await higgsfield.subscribe("kling-video/v3.0/std/text-to-video", {
        //     input: { "prompt": "A slow cinematic tracking shot along a sunlit coastal road." },
        //     withPolling: true,
        //   });
        //
        // "input" and "withPolling" are SIBLINGS in that options object, and "withPolling" is
        // self-evidently not a wire member — it tells the SDK to poll for the caller. The SDK takes
        // the "input" object and writes it as the request body, which is exactly why the cURL's body
        // is the unwrapped "input" value. Reading "input" as the wire envelope reads a CLIENT-SIDE
        // SDK option as the wire format; the vendor's own cURL on the SAME page contradicts it, and
        // the cURL is the wire. This is the same root-cause misreading already corrected one layer up
        // for /estimate in TryFetchQuoteAsync; fixing one and leaving the other would have been
        // incoherent. The snippet's Python arm agrees with the flat shape — it passes the arguments
        // bare (`subscribe(endpoint, arguments={...})`), with no "input" key at all.
        //
        // WHAT IS SENT. The documented input schema on that page is entirely top-level: prompt
        // (string), duration (integer, 3..15, default 5), aspect_ratio (string,
        // "16:9"|"9:16"|"1:1", default "16:9"), plus sound, elements, cfg_scale, multi_shots and
        // multi_prompt. This client sends the three a VideoGenerationRequest actually carries —
        // prompt, duration and aspect_ratio. The remaining documented members are deliberately NOT
        // invented: nothing in the request has a value for them, and writing a default for, say,
        // sound ("on") would change what the vendor renders and bills without anyone asking.
        //
        // IMAGE INPUT, FROM THE IMAGE-INPUT PAGE'S OWN PRIMARY SOURCE. The flat members below replace
        // the "input_images" ARRAY this client used to send. That array is not the REST body: it is
        // the vendor's JAVASCRIPT SDK OPTIONS surface — the same client-side surface whose "input"
        // wrapper was already corrected above — and the image-input page's cURL prints the real body:
        //
        //   curl --request POST         //     --url https://api.higgsfield.ai/kling-video/v3.0/std/image-to-video         //     … --data '{ "prompt": "A slow cinematic tracking shot…",
        //                 "image_url": "https://example.com/first-frame.jpg" }'
        //
        // The page's schema lists image_url as a REQUIRED <string> (format uri) and last_image_url as
        // an optional <string>: FLAT top-level members, not an array of {type, image_url} objects
        // (https://docs.higgsfield.ai/docs/models/kling-3/standard-image-to-video.md, fetched
        // 2026-10-09). That same page documents no aspect_ratio at all — the frame's own geometry
        // decides the output — so the ratio a text-to-video request carries is NOT sent on an image
        // request: a field the selected model page does not publish is not invented. prompt and
        // duration are documented by both pages and keep their names.
        //
        // PER ENDPOINT, NOT GLOBALLY. image_url and last_image_url are sent only for the endpoints in
        // ImageEndpointsByShape, each mapped against its own model page, and an image offered to any
        // other endpoint was refused above, before the upload. Which members a body has is therefore a
        // property of the endpoint, never of this client in general — the vendor is explicit that
        // "Endpoint fields vary by model and workflow" (https://docs.higgsfield.ai/docs/models).
        //
        // FAIL-CLOSED, ONE SHAPE, NO FALLBACK. Nothing here tries one shape and then retries another.
        // A silent fallback would make a wire-format mismatch undetectable, and fail-closed is worth
        // more than a guess that might work: a wrong body is a 4xx the caller sees, never a mischarge.
        // ---------------------------------------------------------------------------------------
        // The member order below is the serialized order, and it is load-bearing: a request with no
        // image keeps the exact legacy body (prompt, duration, aspect_ratio), which is what makes the
        // no-image path byte-identical to the shape this client has always sent.
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["prompt"] = request.PromptText,
            ["duration"] = request.DurationSeconds
        };

        if (imageUrls.Count == 0)
        {
            body["aspect_ratio"] = request.Ratio;
        }
        else
        {
            body["image_url"] = imageUrls[0];
            if (imageUrls.Count == 2)
            {
                body["last_image_url"] = imageUrls[1];
            }
        }

        using var content = new StringContent(
            JsonSerializer.Serialize(body),
            System.Text.Encoding.UTF8,
            "application/json");

        using var httpRequest = CreateRequest(HttpMethod.Post, endpointId);
        httpRequest.Content = content;

        // Idempotency-Key: documented on the vendor's own model-page cURL (quoted above, fetched
        // again 2026-10-04). Sent ONLY when the caller supplies a token — the VideoGenerate step
        // passes its persisted ledger row id, which is exactly the durable value this header needs
        // to be worth anything: a crash between the vendor accepting the task and our recording its
        // request_id now re-presents the SAME key on retry instead of risking a second charge. A
        // request with no token sends no header, byte-identical to the previous wire shape.
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            httpRequest.Headers.TryAddWithoutValidation("Idempotency-Key", request.IdempotencyKey.Trim());
        }

        using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, ct);
        ThrowOnFailure(response);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using JsonDocument jsonDoc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (jsonDoc.RootElement.ValueKind != JsonValueKind.Object
            || !jsonDoc.RootElement.TryGetProperty("request_id", out JsonElement requestIdElement)
            || requestIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(requestIdElement.GetString()))
        {
            throw new InvalidOperationException(
                "Higgsfield accepted the submission but the response carries no non-empty 'request_id' field.");
        }

        return new ExternalJobHandle(requestIdElement.GetString()!);
    }

    /// <summary>
    /// Uploads an input image through the vendor's presigned-upload flow and returns the
    /// <c>public_url</c> that a model parameter accepting an input URL expects
    /// (https://docs.higgsfield.ai/docs/concepts/file-uploads.md):
    /// <list type="number">
    /// <item>authenticated <c>POST /files/generate-upload-url</c> with
    /// <c>{"content_type": &lt;type&gt;}</c> → <c>{public_url, upload_url, content_type,
    /// upload_headers}</c> (the URL expires after one hour);</item>
    /// <item><c>PUT</c> the bytes to <c>upload_url</c> with every <c>upload_headers</c> entry applied
    /// verbatim, and <b>no</b> <c>Authorization</c> header — the docs are explicit: "Do not send
    /// Higgsfield API credentials to the presigned storage URL";</item>
    /// <item>return <c>public_url</c>.</item>
    /// </list>
    /// <para>
    /// Deliberately not part of <see cref="IVideoGenerationClient"/>: that interface is frozen, and
    /// the upload is a Higgsfield-specific precondition of building
    /// <see cref="VideoGenerationRequest.Images"/> rather than a provider-agnostic operation.
    /// </para>
    /// </summary>
    public async Task<string> UploadInputImageAsync(VideoGenerationImageInput image, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (string.IsNullOrWhiteSpace(image.ContentType))
        {
            throw new ArgumentException("An input image requires a non-empty content type.", nameof(image));
        }

        if (!SupportedImageContentTypes.Contains(image.ContentType))
        {
            throw new ArgumentException(
                $"Unsupported input image content type '{image.ContentType}'. Higgsfield documents " +
                "image/jpeg, image/jpg, image/png, image/webp and image/gif.",
                nameof(image));
        }

        if (image.Bytes is not { Length: > 0 })
        {
            throw new ArgumentException("An input image requires non-empty bytes.", nameof(image));
        }

        using var presignBody = new StringContent(
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["content_type"] = image.ContentType }),
            System.Text.Encoding.UTF8,
            "application/json");

        using var presignRequest = CreateRequest(HttpMethod.Post, UploadUrlPath);
        presignRequest.Content = presignBody;

        string uploadUrl;
        string publicUrl;
        List<KeyValuePair<string, string>> uploadHeaders = new();

        using (HttpResponseMessage presignResponse = await _httpClient.SendAsync(presignRequest, ct))
        {
            ThrowOnFailure(presignResponse);

            using var stream = await presignResponse.Content.ReadAsStreamAsync(ct);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            JsonElement root = doc.RootElement;

            uploadUrl = ReadRequiredString(root, "upload_url");
            publicUrl = ReadRequiredString(root, "public_url");

            if (root.TryGetProperty("upload_headers", out JsonElement headersElement)
                && headersElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty header in headersElement.EnumerateObject())
                {
                    if (header.Value.ValueKind == JsonValueKind.Null)
                    {
                        continue;
                    }

                    string value = header.Value.ValueKind == JsonValueKind.String
                        ? header.Value.GetString() ?? string.Empty
                        : header.Value.GetRawText();
                    uploadHeaders.Add(new KeyValuePair<string, string>(header.Name, value));
                }
            }
        }

        // The presigned URL is vendor-controlled, but this leg carries the user's own frame bytes to
        // whatever host it names, so a plaintext or non-http(s) scheme fails closed rather than being
        // followed.
        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out Uri? uploadUri)
            || uploadUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "The presigned upload URL returned by Higgsfield is not an absolute https: URI, so " +
                "the upload was not attempted.");
        }

        // No Authorization header here on purpose — this is the credential-free leg the vendor's
        // docs require. The PUT reuses the client's own HttpClient, so the client-level timeout
        // applies; no per-request timeout is layered on top of it.
        using var putRequest = new HttpRequestMessage(HttpMethod.Put, uploadUri)
        {
            Content = new ByteArrayContent(image.Bytes)
        };

        // Every returned upload_headers entry is applied verbatim, but it has to land on the right
        // collection: Content-Type (and Content-Length/Content-Encoding/Content-Disposition/…) are
        // CONTENT headers, and HttpRequestMessage.Headers rejects them — TryAddWithoutValidation
        // returns false SILENTLY, which would drop the documented 'Content-Type: image/jpeg' and send
        // the PUT as application/octet-stream. The vendor requires the upload content type to match
        // the one used to create the presigned URL, so a silent drop fails the upload outright. A
        // header that cannot be applied is therefore a loud failure, never a drop.
        foreach (KeyValuePair<string, string> header in uploadHeaders)
        {
            // The docs are explicit that Higgsfield credentials must never reach the presigned
            // storage URL, so an Authorization entry is refused rather than applied.
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The presigned upload response returned an 'Authorization' upload header. " +
                    "Higgsfield credentials must never be sent to the presigned storage URL, so the " +
                    "upload was refused.");
            }

            bool applied = IsContentHeader(header.Key)
                ? putRequest.Content.Headers.TryAddWithoutValidation(header.Key, header.Value)
                : putRequest.Headers.TryAddWithoutValidation(header.Key, header.Value)
                    || putRequest.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);

            if (!applied)
            {
                throw new InvalidOperationException(
                    $"The presigned upload response returned an upload header that could not be " +
                    $"applied: '{header.Key}'.");
            }
        }

        using HttpResponseMessage putResponse = await _httpClient.SendAsync(putRequest, ct);
        if (!putResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Uploading the input image to the presigned storage URL failed with HTTP " +
                $"{(int)putResponse.StatusCode}.",
                inner: null,
                statusCode: putResponse.StatusCode);
        }

        return publicUrl;
    }

    /// <summary>
    /// Parses the <c>RequestStatus</c> envelope and maps <c>status</c> with an EXHAUSTIVE switch over
    /// the six verified literals (https://docs.higgsfield.ai/docs/api-reference/requests/get-request-status.md
    /// and https://docs.higgsfield.ai/docs/concepts/requests.md — terminal: <c>completed</c>,
    /// <c>failed</c>, <c>nsfw</c>, <c>canceled</c>). Anything else throws rather than being silently
    /// defaulted.
    /// <para>
    /// <c>nsfw</c> is a TERMINAL, NON-BILLABLE moderation rejection with credits automatically
    /// refunded (docs/concepts/billing-and-retention + docs/help/faq: "Are NSFW-flagged requests
    /// charged? No."). <see cref="ExternalJobState"/> has no <c>Nsfw</c> member and the executor that
    /// consumes it is not this ticket's to change, so the distinction rides in
    /// <see cref="ExternalJobStatus.FailureReason"/> instead — which is also where a distinct degrade
    /// reason for "rejected and refunded" survives.
    /// </para>
    /// <para>
    /// <see cref="ExternalJobStatus.UsageTotalSeconds"/> stays <c>null</c> for every Higgsfield
    /// state: the vendor does not report metered seconds, and inventing one would overstate usage in
    /// the spend ledger.
    /// </para>
    /// </summary>
    public async Task<ExternalJobStatus> PollAsync(ExternalJobHandle handle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);

        using var httpRequest = CreateRequest(HttpMethod.Get, $"{RequestsPathPrefix}{handle.RemoteJobId}/status");

        using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, ct);
        ThrowOnFailure(response);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using JsonDocument jsonDoc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        JsonElement root = jsonDoc.RootElement;

        string statusString = ReadRequiredString(root, "status");

        ExternalJobState state = statusString switch
        {
            "queued" => ExternalJobState.Queued,
            "in_progress" => ExternalJobState.Running,
            "completed" => ExternalJobState.Succeeded,
            "failed" => ExternalJobState.Failed,
            "nsfw" => ExternalJobState.Failed,
            "canceled" => ExternalJobState.Cancelled,
            _ => throw new NotSupportedException(
                $"Unsupported Higgsfield request status: {Bounded(statusString)}")
        };

        string? downloadUrl = null;
        string? failureReason = null;

        if (state == ExternalJobState.Succeeded)
        {
            // MediaOutput = {url}. A completed response with no video, or a video with no url, has
            // nothing to hand upward: that is a hard failure, never a null DownloadUrl.
            if (!root.TryGetProperty("video", out JsonElement video)
                || video.ValueKind != JsonValueKind.Object
                || !video.TryGetProperty("url", out JsonElement urlElement)
                || urlElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(urlElement.GetString()))
            {
                throw new InvalidOperationException(
                    "Higgsfield reported status 'completed' but the response carries no 'video.url'; " +
                    "there is no download URL to return.");
            }

            downloadUrl = urlElement.GetString();
        }

        if (state == ExternalJobState.Failed)
        {
            failureReason = statusString == "nsfw"
                ? "nsfw: content rejected by moderation; the provider refunds this request."
                : ReadOptionalString(root, "error");
        }

        return new ExternalJobStatus(state, downloadUrl, failureReason, UsageTotalSeconds: null);
    }

    /// <summary>
    /// No-cost provider test: exactly one <c>GET /requests/{random uuid}/status</c>, never a POST,
    /// never <c>/estimate</c> (unverified as free), never a submit — so an admin "Test" click cannot
    /// create a task or spend anything.
    /// <para>
    /// What the vendor documents (https://docs.higgsfield.ai/docs/api-reference/requests/get-request-status.md):
    /// 200 → <c>RequestStatus</c> (<c>status</c> + <c>request_id</c> required), 401 → <c>ErrorResponse</c>,
    /// 404 → "The request does not exist or belongs to another account", and <c>ErrorResponse</c> is
    /// <c>{"detail": "&lt;string&gt;"}</c>. Synchronous error meanings
    /// (https://docs.higgsfield.ai/docs/concepts/errors.md): 400 invalid params, 401 missing/invalid
    /// credentials, <b>403 insufficient credits</b>, 404 request or model not found, 422 validation,
    /// 423 model blocked, 500 server, 503 model disabled.
    /// </para>
    /// <para>
    /// The decision table, and why it is NOT MiniMax's:
    /// </para>
    /// <list type="bullet">
    /// <item><b>401</b> → authentication rejected.</item>
    /// <item><b>403</b> → <c>Ok: false</c> but <c>AuthRejected: false</c>. These docs define 403 as
    /// INSUFFICIENT CREDITS, not as bad credentials, so reporting it as an auth failure would be a
    /// lie an admin acts on (they would re-issue a perfectly good key). Deliberately not the MiniMax
    /// rule, which folds 401 and 403 together because MiniMax documents no separate meaning.</item>
    /// <item><b>404</b> with the documented <c>{"detail": "&lt;string&gt;"}</c> body → a PROVING
    /// <c>Ok: true</c>. 404 is documented as "The request does not exist or belongs to another
    /// account", so it proves BOTH that this is the vendor's status endpoint and that the credential
    /// was accepted — an unauthenticated call is 401, not 404.</item>
    /// <item><b>200</b> whose body parses as a <c>RequestStatus</c> (a string <c>status</c>) →
    /// <c>Ok: true</c>.</item>
    /// <item>5xx, 408, 429, a network error, our own timeout, a non-JSON body, or JSON that is
    /// neither envelope → <c>Ok: false</c>, reason naming what was seen.</item>
    /// <item>400/422/423/503 → <c>Ok: false</c>, reason naming the status.</item>
    /// </list>
    /// Reasons carry at most a bounded, credential-scrubbed vendor <c>detail</c>, never the raw body.
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
            using var httpRequest = CreateRequest(
                HttpMethod.Get,
                $"{RequestsPathPrefix}{Guid.NewGuid()}/status");

            using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, timeoutCts.Token);
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

        if (status == 401)
        {
            return new VideoGenerationProbeResult(false, true, "Provider rejected authentication (HTTP 401).");
        }

        if (status == 403)
        {
            return NotOk("Provider returned HTTP 403 (insufficient credits).");
        }

        if (status >= 500 || status is 408 or 429)
        {
            return NotOk($"Provider returned HTTP {status}.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return NotOk($"Provider returned a non-JSON body (HTTP {status}, content-type {Bounded(contentType)}).");
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            string? detail = ReadOptionalString(root, "detail");
            bool hasStatus = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("status", out JsonElement statusElement)
                && statusElement.ValueKind == JsonValueKind.String;

            if (status == 404 && detail is not null)
            {
                return new VideoGenerationProbeResult(true, false,
                    "Provider answered HTTP 404 for an unknown request id with the documented error body — " +
                    "the status endpoint and the credential were both accepted.");
            }

            if (status == 200 && hasStatus)
            {
                return new VideoGenerationProbeResult(true, false,
                    "Provider answered HTTP 200 with a request-status object.");
            }

            if (status is 400 or 422 or 423)
            {
                return detail is { Length: > 0 }
                    ? NotOk($"Provider returned HTTP {status} ({Bounded(detail)}).")
                    : NotOk($"Provider returned HTTP {status}.");
            }

            if (detail is not null)
            {
                return NotOk($"Provider returned HTTP {status} ({Bounded(detail)}).");
            }

            return NotOk($"Provider returned an unrecognised JSON body (HTTP {status}).");
        }
    }

    /// <summary>
    /// FAIL-CLOSED, pure, and deliberately offline: <b>no</b> Higgsfield model is offered by this
    /// method, and it performs no network call for any input.
    /// <para>
    /// Higgsfield publishes no per-second or per-model price table, and the vendor documents no
    /// offline capability data either — no model roster, no duration range, no ratio list. So there is
    /// nothing this synchronous, no-network lookup could return that would not be an invented vendor
    /// fact, and returning a <see cref="VideoGenerationCapabilities"/> with an empty or zero-valued
    /// price table would be exactly the "$0" failure mode <see cref="VideoGenerationPricing"/> exists
    /// to prevent. An unpriced model is therefore not offered here.
    /// </para>
    /// <para>
    /// <b>This is not the only way to offer a Higgsfield model any more.</b> The priced answer comes
    /// from <see cref="QuoteAsync"/>, which makes the one bounded, cached network call the pricing
    /// path is now allowed. This method stays exactly as it was — pure, no-network, fail-closed — so
    /// that callers needing only the offline answer, and every existing test that pins this throw,
    /// keep the behaviour they had. The two are complementary, not redundant: this one says "cannot be
    /// known without asking", the oracle says "here is what asking returned".
    /// </para>
    /// </summary>
    public VideoGenerationCapabilities Describe(string model)
    {
        throw new NotSupportedException(
            $"No Higgsfield model is available ('{Bounded(model ?? string.Empty)}' is not offered by the " +
            "offline lookup). Higgsfield publishes no per-second or per-model price table; the only " +
            "authoritative price is the account- and parameter-specific POST /estimate/{endpoint_id} " +
            "response (https://docs.higgsfield.ai/docs/concepts/billing-and-retention), which Describe " +
            "cannot make because it is a pure, no-network lookup. Price the configuration through " +
            "IVideoGenerationPriceOracle.QuoteAsync (or the provider-agnostic " +
            "IVideoGenerationClient.QuoteClipAsync extension) instead.");
    }

    /// <summary>
    /// The Higgsfield price oracle: prices ONE clip at ONE exact configuration from the vendor's
    /// authenticated <c>POST /estimate/{endpoint_id}</c>, which returns <c>{credits, usd}</c>
    /// (https://docs.higgsfield.ai/docs/concepts/billing-and-retention).
    /// <para>
    /// <b>Cache.</b> Keyed by the exact configuration — <c>model ‖ resolution ‖ ratio ‖ duration</c>
    /// — joined <b>length-prefixed</b> (<c>&lt;len&gt;:&lt;value&gt;|</c>), so the join is injective and
    /// a model named <c>a</c> with resolution <c>b|c</c> cannot collide with a model named <c>a|b</c>
    /// with resolution <c>c</c>. A success is held for <see cref="PriceQuoteTtl"/>; a failure for
    /// <see cref="PriceQuoteFailureTtl"/>; the cache holds at most
    /// <see cref="MaxPriceQuoteCacheEntries"/> entries, dropping expired ones first and then the
    /// oldest.
    /// <para>
    /// The cache lives on this instance, which is what makes it effective rather than decorative:
    /// <c>VideoGenerationClientFactory</c> is a process SINGLETON and hands out one client per
    /// <c>ResolvedVideoGenerationProvider.CacheKey</c>, so the instance — and this cache — outlives a
    /// single step and a single execution. It is bounded anyway, because a client is also long-lived.
    /// Two consequences worth stating rather than leaving to be discovered: a quote is served across
    /// executions within its TTL (it is a PRICE, not a spend, so re-serving it cannot authorize
    /// anything the ledger has not already been asked about), and a quote can never cross provider
    /// rows, because the cache key starts at the resolved provider's own client.
    /// </para>
    /// <para>
    /// <b>Bounded.</b> At most one HTTP attempt per miss, under <see cref="DefaultPriceOracleTimeout"/>
    /// (or the override passed to the constructor), linked to <paramref name="ct"/>. Nothing here waits
    /// on the provider row's generation timeout.
    /// </para>
    /// <para>
    /// <b>Failure and timeout are fail-closed, and there is no third outcome.</b> A network error, a
    /// timeout, a non-2xx status, a non-JSON body, a missing <c>usd</c>, or a <c>usd</c> that is not
    /// strictly positive — as a number OR as a string, since the vendor's documented envelope writes it
    /// as a string — all throw <see cref="NotSupportedException"/> — the same exception
    /// <see cref="Describe"/> has always thrown for an unpriceable model, so an existing
    /// <c>catch (NotSupportedException)</c> at the call site needs no change and still maps a
    /// Higgsfield step to <c>CONFIG_INVALID</c> with no ledger row. The failure is then remembered for
    /// <see cref="PriceQuoteFailureTtl"/> and re-thrown from cache. This method never returns
    /// <c>0</c>, never returns <c>null</c>, and never succeeds with a price it did not read: a model
    /// the oracle cannot price stays UNOFFERABLE, which is what keeps a fail-closed spend gate from
    /// becoming fail-open because a price could not be fetched.
    /// </para>
    /// <para>
    /// <b>Request body.</b> Flat, with the model parameters at the top level and NO <c>input</c>
    /// wrapper, exactly as the vendor's own billing page documents for <c>/estimate</c>
    /// (https://docs.higgsfield.ai/docs/concepts/billing-and-retention, "Estimate a request"). The
    /// submit is flat too, and for the same reason — the <c>input</c> wrapper was a misreading of the
    /// TypeScript SDK's client-side option, corrected in <see cref="SubmitAsync"/> — but the two
    /// shapes were verified against DIFFERENT primary sources and that is still how they must be
    /// reasoned about: neither page is evidence for the other endpoint. What is sent, what is
    /// deliberately omitted (<c>prompt</c>), the cache-keying reason for that omission, and the one
    /// residual uncertainty are recorded in full at the body-construction site in
    /// <see cref="TryFetchQuoteAsync"/>. If the body is wrong the vendor answers 4xx, this method
    /// throws, and the model stays unofferable — the failure mode of an unknown wire detail here is
    /// "exactly what happens today", never a wrong price.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="query"/> is malformed.</exception>
    /// <exception cref="NotSupportedException">The configuration could not be priced. Fail-closed.</exception>
    public async Task<VideoGenerationPriceQuote> QuoteAsync(VideoGenerationPriceQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Resolution);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Ratio);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.DurationSeconds);

        // An endpoint id that could never be addressed is a fault of the step's configuration: it throws the plain
        // NotSupportedException / ArgumentException (the person must change it). Every failure past this point is the
        // vendor or the network not pricing it right now, and is VideoGenerationPriceUnavailableException.
        try
        {
            _ = NormalizeEndpointId(query.Model, nameof(query));
        }
        catch (ArgumentException ex)
        {
            // Fail closed with the oracle's one documented exception, as every unpriceable configuration does.
            throw new NotSupportedException(ex.Message);
        }

        string cacheKey = BuildPriceCacheKey(query);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (TryReadCachedQuote(cacheKey, now, out VideoGenerationPriceQuote? cached, out string? cachedFailure))
        {
            if (cached is not null)
            {
                return cached;
            }

            throw new VideoGenerationPriceUnavailableException(cachedFailure);
        }

        if (!await TryFetchQuoteAsync(query, ct))
        {
            // TryFetchQuoteAsync recorded the failure; read it back so the thrown message is the one
            // that was cached, rather than a second, differently-worded copy of it.
            bool found = TryReadCachedQuote(
                cacheKey, _timeProvider.GetUtcNow(), out _, out string? recordedFailure);
            throw new VideoGenerationPriceUnavailableException(
                found && recordedFailure is not null
                    ? recordedFailure
                    : $"Higgsfield could not price model '{Bounded(query.Model)}'.");
        }

        if (!TryReadCachedQuote(cacheKey, _timeProvider.GetUtcNow(), out VideoGenerationPriceQuote? quote, out _)
            || quote is null)
        {
            throw new VideoGenerationPriceUnavailableException(
                $"Higgsfield returned a price for model '{Bounded(query.Model)}' that could not be stored; "
                + "treating the configuration as unofferable rather than proceeding unpriced.");
        }

        return quote;
    }

    /// <summary>
    /// The one HTTP attempt. Returns <c>true</c> and stores a positive quote on success; returns
    /// <c>false</c> after storing a bounded, key-free failure reason. Never throws for a vendor or
    /// transport failure — the caller converts a <c>false</c> into the fail-closed
    /// <see cref="NotSupportedException"/>, so every failure path exits through one place.
    /// </summary>
    private async Task<bool> TryFetchQuoteAsync(VideoGenerationPriceQuery query, CancellationToken ct)
    {
        string endpointId;
        try
        {
            // Reuses the submit path's validator, so an endpoint id that could never be addressed — and
            // in particular one that would override the authority and redirect the credential and the
            // request to an attacker's host — is rejected before anything leaves the process. The
            // estimate path is path-addressed exactly like the submit, so it needs exactly this check,
            // and a model id is step-config-authored rather than admin-only.
            endpointId = NormalizeEndpointId(query.Model, nameof(query));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            StorePriceFailure(BuildPriceCacheKey(query), ex.Message);
            return false;
        }

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_priceOracleTimeout);

        try
        {
            // ---------------------------------------------------------------------------------------
            // The estimate REQUEST body, from the vendor's own primary documentation.
            //
            // PRIMARY SOURCE (fetched 2026-10-03):
            // https://docs.higgsfield.ai/docs/concepts/billing-and-retention — "Estimate a request":
            //
            //   curl --request POST \
            //     --url https://api.higgsfield.ai/estimate/higgsfield-ai/soul/v2/standard \
            //     --header "Authorization: Key ${HF_API_KEY_ID}:${HF_API_KEY_SECRET}" \
            //     --header "Content-Type: application/json" \
            //     --data '{ "prompt": "Editorial portrait in soft daylight" }'
            //
            // It shows the model parameters at the TOP LEVEL, with NO "input" wrapper. The submit
            // shape is NOT evidence for the estimate shape, and this method previously mirrored the
            // submit's then-{"input": {...}} envelope — which that page contradicts. The body is now
            // flat. (The submit was corrected to flat as well, from ITS own cURL; see SubmitAsync.
            // The two are now the same shape, but each still rests on its OWN primary source, and
            // neither page is evidence for the other endpoint.)
            //
            // WHAT IS SENT, AND WHAT IS NOT. The documented body is flat and carries the selected
            // model's own parameters ("Use the estimate endpoint with the same model parameters before
            // submitting generation"). The oracle holds exactly the three values a price can depend on
            // for this step — the model (the URL's endpoint id), the duration and the aspect ratio — so
            // those two are what the flat body carries. The vendor's one worked example prints "prompt"
            // because the model it prices (higgsfield-ai/soul/v2/standard, text-to-image) has prompt as
            // its only parameter; that is the same flat shape with that model's parameter set.
            //
            // "prompt" is deliberately still NOT sent, and the reason is NOT the old one (egress of
            // free text) — it is that this oracle is a CACHED, configuration-keyed lookup and
            // VideoGenerationPriceQuery has no prompt to send. Adding one would make the prompt part of
            // the cache key, which makes every priced clip a cache miss: a per-shot network call on the
            // pre-submit pricing hot path, which is exactly what the oracle-pricing rule condition (2) forbids
            // ("no uncached per-call pricing on a hot path"). The prompt is also not in scope at the
            // call site: VideoGenerateStepExecutor resolves the pricing basis from the step's CONFIG,
            // before any plan or prompt exists. Extending the query with a prompt is therefore a design
            // change, not a body change, and is left to a decision rather than guessed at here.
            //
            // THE UNCERTAINTY, RECORDED RATHER THAN PAPERED OVER. No credential exists for this vendor
            // and none may be requested, so this body cannot be settled empirically. If the vendor
            // requires a parameter we do not send — "prompt" is the one the model pages call mandatory
            // on SUBMIT — it answers 4xx, TryFetchQuoteAsync records a failure, and the model stays
            // unofferable: the same outcome as today, never a wrong price. That is the deliberate
            // trade: a wire-format mismatch must stay DETECTABLE, so there is exactly one shape here
            // and no fallback between shapes.
            //
            // Also checked, and no disagreement found: the vendor's machine-readable reference
            // (https://docs.higgsfield.ai/docs/openapi.json) does not document /estimate at all, and the
            // official SDK README exposes no estimate method — an ABSENCE, not a conflicting shape.
            // https://docs.higgsfield.ai/docs/concepts/billing-and-retention is consequently the only
            // primary source for this request, and it is the one followed.
            // ---------------------------------------------------------------------------------------
            var requestBody = new Dictionary<string, object?>
            {
                ["duration"] = query.DurationSeconds,
                ["aspect_ratio"] = query.Ratio
            };

            using var content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                System.Text.Encoding.UTF8,
                "application/json");

            // NormalizeEndpointId returns a path that already carries exactly one leading slash (the
            // guard against the "//authority-override" shape), so the estimate path is built by
            // prefixing "estimate" to it rather than inserting another separator.
            using HttpRequestMessage httpRequest = CreateRequest(HttpMethod.Post, $"estimate{endpointId}");
            httpRequest.Content = content;

            using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                StorePriceFailure(
                    BuildPriceCacheKey(query),
                    $"Higgsfield's price oracle returned HTTP {(int)response.StatusCode} for model "
                    + $"'{Bounded(query.Model)}'; the configuration is unofferable rather than priced at zero.");
                return false;
            }

            string body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            return TryStoreQuoteFromBody(query, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            StorePriceFailure(
                BuildPriceCacheKey(query),
                $"Higgsfield's price oracle did not respond within {_priceOracleTimeout.TotalSeconds:0.#}s for "
                + $"model '{Bounded(query.Model)}'; the configuration is unofferable rather than priced at zero.");
            return false;
        }
        catch (HttpRequestException ex)
        {
            StorePriceFailure(
                BuildPriceCacheKey(query),
                $"Network error reaching Higgsfield's price oracle for model '{Bounded(query.Model)}': "
                + $"{Bounded(ex.Message)}. The configuration is unofferable rather than priced at zero.");
            return false;
        }
    }

    /// <summary>
    /// Parses the documented <c>{credits, usd}</c> envelope and stores the quote. Anything unexpected —
    /// unparseable JSON, a missing or non-object root, a missing/negative/non-numeric <c>usd</c>, or a
    /// <c>usd</c> that rounds to a non-positive amount — is a failure, never a zero. <c>credits</c> is
    /// recorded when present and integral, and is otherwise simply absent; it never affects the price.
    /// <para>
    /// <b><c>usd</c> is accepted as a JSON number OR a JSON string.</b> The vendor's documented envelope
    /// writes BOTH members as strings — <c>{"credits": "1.500", "usd": "0.094"}</c>
    /// (https://docs.higgsfield.ai/docs/concepts/billing-and-retention) — so a number-only reader would
    /// reject every real answer, leave every Higgsfield model unofferable, and make the oracle-pricing rule a
    /// no-op against the live vendor. Reading both shapes is a wire-dialect fact taken from the primary
    /// source, not a loosening of the fail-closed rule: a string that is not a number, a blank string, a
    /// number that is zero or negative, and a null are all still failures.
    /// </para>
    /// <para>
    /// <c>credits</c> deliberately keeps its ORIGINAL number-only, integral-only reading. It never
    /// affects the price, and the documented sample <c>"1.500"</c> is not an integer either way, so
    /// widening it would change nothing about what is charged. Recorded here rather than silently, so
    /// the next reader knows the asymmetry is a decision and not an oversight.
    /// </para>
    /// </summary>
    private bool TryStoreQuoteFromBody(VideoGenerationPriceQuery query, string body)
    {
        string cacheKey = BuildPriceCacheKey(query);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            StorePriceFailure(
                cacheKey,
                $"Higgsfield's price oracle returned a non-JSON body for model '{Bounded(query.Model)}'; "
                + "the configuration is unofferable rather than priced at zero.");
            return false;
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("usd", out JsonElement usdElement)
                || !TryReadUsd(usdElement, out decimal usd)
                || usd <= 0m)
            {
                StorePriceFailure(
                    cacheKey,
                    $"Higgsfield's price oracle did not return a positive 'usd' for model "
                    + $"'{Bounded(query.Model)}'; the configuration is unofferable rather than priced at zero.");
                return false;
            }

            int? credits = root.TryGetProperty("credits", out JsonElement creditsElement)
                && creditsElement.ValueKind == JsonValueKind.Number
                && creditsElement.TryGetInt32(out int creditValue)
                    ? creditValue
                    : null;

            // The vendor's total is authoritative; the per-second rate exists only so the shared
            // VideoGenerationPricing path can be reused unchanged. Guarded here as well as in the
            // record, because a duration large enough to round the rate to zero must fail closed
            // rather than produce a quote the pricing path would reject anyway.
            decimal perSecond = usd / query.DurationSeconds;
            if (perSecond <= 0m)
            {
                StorePriceFailure(
                    cacheKey,
                    $"Higgsfield's price oracle returned a total too small to express per second for model "
                    + $"'{Bounded(query.Model)}'; the configuration is unofferable rather than priced at zero.");
                return false;
            }

            Store(cacheKey, new PriceCacheEntry(
                _timeProvider.GetUtcNow().Add(PriceQuoteTtl),
                Quote: new VideoGenerationPriceQuote(
                    model: query.Model,
                    resolution: query.Resolution,
                    ratio: query.Ratio,
                    durationSeconds: query.DurationSeconds,
                    pricePerSecondUsd: perSecond,
                    totalUsd: usd,
                    credits: credits,
                    source: VideoGenerationPriceSource.VendorEstimate),
                FailureReason: null));

            return true;
        }
    }

    /// <summary>
    /// Reads the vendor's <c>usd</c> member into a <see cref="decimal"/>, from EITHER of the two shapes
    /// the vendor's own documentation uses: a JSON number (<c>{"usd": 0.094}</c>) or a JSON string
    /// (<c>{"usd": "0.094"}</c> — the shape the billing page actually prints).
    /// <para>
    /// The string arm parses under <see cref="CultureInfo.InvariantCulture"/> with
    /// <see cref="NumberStyles.Float"/> and nothing looser: <c>Float</c> permits surrounding whitespace,
    /// a sign, a decimal point and an exponent, and deliberately does NOT permit group separators, so
    /// <c>"1,25"</c> is a failure rather than a silently mis-scaled <c>125</c>. The text is trimmed
    /// first so that a padded <c>" 0.094 "</c> — which a hand-written or generated JSON body routinely
    /// produces — is read rather than dropped. Anything else (blank, non-numeric, <c>null</c>, an
    /// object, an array) returns <c>false</c>.
    /// </para>
    /// <para>
    /// The sign and magnitude are NOT judged here: the caller applies the single <c>&gt; 0</c> rule to
    /// both arms, so "negative or zero" has exactly one implementation and cannot drift between the
    /// number and string paths.
    /// </para>
    /// </summary>
    private static bool TryReadUsd(JsonElement element, out decimal usd)
    {
        usd = 0m;

        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetDecimal(out usd);

            case JsonValueKind.String:
                string? text = element.GetString();
                return text is not null
                    && decimal.TryParse(
                        text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out usd);

            default:
                return false;
        }
    }

    /// <summary>Hard ceiling on one downloaded clip, the same as the fal and MiniMax clients.</summary>
    public const long MaxDownloadBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Downloads a generated clip. The URL comes out of a vendor response, so it is treated as untrusted: https
    /// only, no credentials in the URL, no loopback/private/link-local literal host, and the body is capped at
    /// <see cref="MaxDownloadBytes"/> (declared and actually streamed). It goes through the download client, so
    /// the address dialled is checked at connect time and no Authorization header is sent.
    /// </summary>
    public async Task<Stream> DownloadAsync(string downloadUrl, CancellationToken ct)
    {
        GeneratedClipUrlGuard.EnsureAllowed(downloadUrl);

        HttpResponseMessage response = await _downloadClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            response.EnsureSuccessStatusCode();

            long? declared = response.Content.Headers.ContentLength;
            if (declared > MaxDownloadBytes)
            {
                throw new InvalidOperationException($"Generated clip is {declared} bytes, over the {MaxDownloadBytes}-byte limit.");
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
    /// The one place an authenticated request is built: the base URL, the
    /// <c>Authorization: Key &lt;KEY_ID&gt;:&lt;KEY_SECRET&gt;</c> header (NOT Bearer), and the
    /// explicit <see cref="UserAgent"/> — the vendor's server SDK sends a comparable UA, and a
    /// request without one "may be treated differently".
    /// </summary>
    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Key", $"{_keyId}:{_keySecret}");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return request;
    }

    /// <summary>
    /// Whether <paramref name="headerName"/> belongs to <see cref="HttpContentHeaders"/> rather than
    /// <see cref="HttpRequestHeaders"/>. <c>TryAddWithoutValidation</c> on the wrong collection
    /// returns <c>false</c> without throwing, so the caller must route first and check the result.
    /// </summary>
    private static bool IsContentHeader(string headerName) => ContentHeaderNames.Contains(headerName);

    // ---------------------------------------------------------------------------------------------
    // Price-quote cache (see QuoteAsync for the contract this implements)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// One cache entry: either a positive quote (live until <see cref="PriceCacheEntry.ExpiresAtUtc"/>)
    /// or the bounded reason the last attempt failed.
    /// </summary>
    /// <param name="ExpiresAtUtc">When this entry stops being served. A success uses
    /// <see cref="PriceQuoteTtl"/>, a failure <see cref="PriceQuoteFailureTtl"/>.</param>
    /// <param name="Quote">The quote, when the entry is a success.</param>
    /// <param name="FailureReason">The fail-closed message to re-throw, when the entry is a failure.</param>
    private sealed record PriceCacheEntry(DateTimeOffset ExpiresAtUtc, VideoGenerationPriceQuote? Quote, string? FailureReason);

    /// <summary>
    /// Builds the cache key: the configuration's four components, joined. The first THREE — model,
    /// resolution and ratio — are each emitted as <c>&lt;length&gt;:&lt;value&gt;|</c>; the duration is
    /// emitted BARE, as the trailing component. Length-prefixing the string fields is what makes the
    /// join injective, so the key of (<c>model="a"</c>, <c>resolution="b|c"</c>) is not the key of
    /// (<c>model="a|b"</c>, <c>resolution="c"</c>) — a collision here would serve one configuration's
    /// price for another's, which is the one wrong answer this cache must never give.
    /// <para>
    /// The duration needs no length prefix because it is an <see cref="int"/>: its rendering contains
    /// no <c>'|'</c>, and it is last, so it is terminated by the end of the key rather than by a
    /// separator. The prefix on the three string fields is therefore sufficient for injectivity, and
    /// the claim is stated precisely here because an earlier version of this comment said all four
    /// were length-prefixed; the CODE was always right, only the comment overclaimed.
    /// </para>
    /// </summary>
    private static string BuildPriceCacheKey(VideoGenerationPriceQuery query)
    {
        return $"{query.Model.Length}:{query.Model}|"
            + $"{query.Resolution.Length}:{query.Resolution}|"
            + $"{query.Ratio.Length}:{query.Ratio}|"
            + $"{query.DurationSeconds}";
    }

    /// <summary>
    /// Reads a live entry. Returns <c>true</c> with either a quote or the failure reason; returns
    /// <c>false</c> on a miss or an expired entry, which the caller treats as "make the attempt".
    /// </summary>
    private bool TryReadCachedQuote(
        string key, DateTimeOffset now, out VideoGenerationPriceQuote? quote, out string? failureReason)
    {
        lock (_priceCacheLock)
        {
            if (!_priceCache.TryGetValue(key, out PriceCacheEntry? entry) || entry.ExpiresAtUtc <= now)
            {
                quote = null;
                failureReason = null;
                return false;
            }

            quote = entry.Quote;
            failureReason = entry.FailureReason;
            return true;
        }
    }

    /// <summary>Records a failure under the shorter failure TTL.</summary>
    private void StorePriceFailure(string key, string reason)
    {
        Store(key, new PriceCacheEntry(
            _timeProvider.GetUtcNow().Add(PriceQuoteFailureTtl), Quote: null, FailureReason: reason));
    }

    /// <summary>
    /// Inserts an entry, evicting first when the cache is at its ceiling: expired entries go first,
    /// then the entry closest to expiry (the oldest for a uniform TTL). Eviction is best-effort by
    /// design — it bounds memory, and losing an entry only costs a re-fetch.
    /// </summary>
    private void Store(string key, PriceCacheEntry entry)
    {
        lock (_priceCacheLock)
        {
            if (_priceCache.Count >= MaxPriceQuoteCacheEntries && !_priceCache.ContainsKey(key))
            {
                DateTimeOffset now = _timeProvider.GetUtcNow();

                foreach (string expired in _priceCache
                    .Where(pair => pair.Value.ExpiresAtUtc <= now)
                    .Select(pair => pair.Key)
                    .ToList())
                {
                    _priceCache.Remove(expired);
                }

                while (_priceCache.Count >= MaxPriceQuoteCacheEntries)
                {
                    string oldest = _priceCache
                        .OrderBy(pair => pair.Value.ExpiresAtUtc)
                        .Select(pair => pair.Key)
                        .First();
                    _priceCache.Remove(oldest);
                }
            }

            _priceCache[key] = entry;
        }
    }

    /// <summary>
    /// Reads a required non-empty string property, or throws naming the missing field.</summary>
    private static string ReadRequiredString(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(propertyName, out JsonElement element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new InvalidOperationException(
                $"Higgsfield response is missing the required non-empty '{propertyName}' field.");
        }

        return element.GetString()!;
    }

    /// <summary>Reads an optional string property; absent, null or non-string yields <c>null</c>.</summary>
    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(propertyName, out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return element.GetString();
    }

    private static VideoGenerationProbeResult NotOk(string reason) => new(false, false, reason);

    /// <summary>
    /// Scrubs BOTH halves of the credential and truncates, so a vendor/exception message is safe to
    /// show an admin or write to a log. The mini-max analogue bounds one secret; this client has two.
    /// </summary>
    private string Bounded(string? text)
    {
        text ??= string.Empty;

        if (_keyId.Length > 0)
        {
            text = text.Replace(_keyId, "***", StringComparison.Ordinal);
        }

        if (_keySecret.Length > 0)
        {
            text = text.Replace(_keySecret, "***", StringComparison.Ordinal);
        }

        return text.Length <= MaxVendorMessageChars ? text : text[..MaxVendorMessageChars] + "…";
    }

    /// <summary>
    /// 401/403 raise an <see cref="HttpRequestException"/> carrying the status code and a message
    /// free of the credential; every other non-success code keeps
    /// <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>. The response body is never read or
    /// embedded here.
    /// </summary>
    private static void ThrowOnFailure(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new HttpRequestException(
                "Unauthorized: Higgsfield rejected the credential", inner: null, statusCode: response.StatusCode);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new HttpRequestException(
                "Forbidden: Higgsfield reported insufficient credits", inner: null, statusCode: response.StatusCode);
        }

        response.EnsureSuccessStatusCode();
    }
}
