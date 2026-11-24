using System.Collections.Concurrent;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;

namespace ReelBolt.WorkflowEngine.Services.Video.Tracking;

/// <summary>
/// Builds the fal.ai client for <see cref="InferenceProviderCapability.VideoSegmentation"/>. The
/// <see cref="HttpClient"/> is cached per resolved provider, so repeated targets reuse one connection
/// pool; the client itself is built per call around the caller's own probe. Mirrors
/// <see cref="ImageAnalysisClientFactory"/>'s fal arm.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the client is never cached with its probe.</b> The shape reader the client calls
/// (<c>Sam31VideoRleResponseReader</c>) validates every run against the frame the caller probed —
/// width, height AND frame count — and the vendor's answer states none of them. A client cached per
/// provider and width x height (as this factory once did) kept the FIRST source's frame count: a step
/// whose source was swapped for a shorter clip of the same size had the new clip's answer checked
/// against the old clip's length and refused (run 0b464e84: 108 entries against a stale 192). Building
/// the client per call around <paramref name="frameSize"/> makes the probe always the current source's.
/// </para>
/// <para>
/// <b>Metering.</b> Applied OUTSIDE the cache, exactly like the sibling factories: the decorator carries
/// the organization the call is billed to, and that is deliberately not part of the cache key. The
/// wrapped client records <see cref="UsageKind.VideoSegmentationFrames"/> (the vendor's 16-frame block
/// rounded up) for every call the vendor actually performed.
/// </para>
/// </remarks>
public sealed class FalAiVideoSegmentationClientFactory : IVideoSegmentationClientFactory
{
    private readonly ConcurrentDictionary<string, HttpClient> _httpClients = new();
    private readonly IUsageRecorder? _recorder;
    private readonly ILogger<FalAiVideoSegmentationClientFactory>? _logger;
    private readonly HttpMessageHandler? _handler;

    /// <param name="recorder">
    /// When supplied, every client handed out meters the frames it bought; null returns it raw. The
    /// engine registers the singleton recorder, so a segmentation call can never be silently free.
    /// </param>
    /// <param name="logger">Carries a metering failure; never a prompt and never a URL.</param>
    /// <param name="handler">
    /// The message handler the fal queue calls travel over; null means the row's own
    /// (<see cref="ProviderRowTransport.CreateHandler"/>: public-only for an org-owned row). It exists so a test can drive the whole factory through a stub
    /// handler with no network and no key, the same seam <c>FigmaVariablesClient</c> uses.
    /// </param>
    public FalAiVideoSegmentationClientFactory(
        IUsageRecorder? recorder = null,
        ILogger<FalAiVideoSegmentationClientFactory>? logger = null,
        HttpMessageHandler? handler = null)
    {
        _recorder = recorder;
        _logger = logger;
        _handler = handler;
    }

    /// <inheritdoc />
    public IVideoSegmentationClient Get(ResolvedImageAnalysisProvider provider, VideoSegmentationFrameSize frameSize)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // The admin boundary already refuses this combination; this is the second line of defence, so a
        // row written straight to the database fails with a sentence rather than a null reference.
        if (provider.Kind != InferenceProviderKind.FalAi)
        {
            throw new NotSupportedException(
                $"Inference provider '{provider.Name}' is of kind '{provider.Kind}', which is not supported " +
                "for text-prompted video segmentation. Only FalAi is supported today.");
        }

        if (frameSize is null || frameSize.Width <= 0 || frameSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameSize),
                "A segmentation client decodes run lists indexed over the source frame, so it needs a " +
                "positive width and height; the caller's own probe supplies them.");
        }

        IVideoSegmentationClient inner = Build(provider, frameSize);
        return _recorder is null
            ? inner
            : new MeteringVideoSegmentationClient(inner, provider, _recorder, _logger);
    }

    private IVideoSegmentationClient Build(ResolvedImageAnalysisProvider provider, VideoSegmentationFrameSize frameSize)
    {
        // The queue host has a documented default, so a blank endpoint is filled in rather than thrown
        // on — unlike the self-hosted kinds, where a blank endpoint has no correct answer. The stored key
        // is sent verbatim and nothing is read from the environment.
        string endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? FalAiVideoSegmentationClient.DefaultEndpoint
            : provider.Endpoint;

        // One HttpClient per provider (its endpoint is part of the provider's cache key): only the transport
        // is shared, never the probe. An org-owned row dials public addresses only, so ownership is part of the
        // key too: a platform row's client is never reused for an org row.
        bool orgOwned = ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged);
        HttpClient httpClient = _httpClients.GetOrAdd(ProviderRowTransport.CacheKey(provider.CacheKey, orgOwned), _ => new HttpClient(_handler ?? ProviderRowTransport.CreateHandler(orgOwned))
        {
            BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"),
            // The client bounds its own poll loop with the request's Timeout, so the transport must not
            // cut a legitimately queued job off first.
            Timeout = Timeout.InfiniteTimeSpan
        });

        return new FalAiVideoSegmentationClient(
            httpClient,
            provider.ApiKey,
            new FixedFrameSizeProbe(frameSize),
            provider.ProviderId,
            probeModel: provider.ModelName);
    }

    /// <summary>The caller's own measurement, handed to the client instead of a second ffprobe.</summary>
    private sealed class FixedFrameSizeProbe(VideoSegmentationFrameSize size) : IVideoSegmentationFrameSizeProbe
    {
        public Task<VideoSegmentationFrameSize> ProbeAsync(Uri videoUrl, CancellationToken ct) =>
            Task.FromResult(size);
    }
}
