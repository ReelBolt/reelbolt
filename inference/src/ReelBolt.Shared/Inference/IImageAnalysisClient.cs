namespace ReelBolt.Shared.Inference;

/// <summary>
/// Client for a hosted subject-segmentation or depth-estimation provider.
/// </summary>
/// <remarks>
/// <b>One client, two capabilities, deliberately — the same shape as <see cref="IImageClient"/>.</b>
/// <see cref="SegmentAsync"/> serves <c>InferenceProviderCapability.Segmentation</c> and
/// <see cref="EstimateDepthAsync"/> serves <c>Depth</c>. Both are served by one kind here because
/// fal exposes ONE queue contract over every endpoint it hosts: the submit, the poll, the status
/// vocabulary and the authentication are byte-identical, and only the request body and the output
/// field names differ. Two interfaces would mean two factories, two resolved types and two poll
/// loops for one HTTP client.
/// <para>
/// They remain two capabilities with two provider rows and two independent "at most one default"
/// constraints, so the operator decision of which vendor segments and which estimates depth is
/// preserved; <c>ResolvedImageAnalysisProvider.Capability</c> is what says which one a resolution
/// was for.
/// </para>
/// <para>
/// <b>An endpoint whose schema we have not read is refused.</b> <see cref="Describe"/> is the
/// authority, and every implementation dispatches on the model id against a table of schemas that
/// were actually fetched. Sending a field an endpoint does not document does not fail loudly at the
/// vendor — it is silently ignored, and the caller gets a plausible-looking result for a request
/// nobody made — so an unknown model throws <see cref="NotSupportedException"/> instead.
/// </para>
/// </remarks>
public interface IImageAnalysisClient
{
    /// <summary>Cut a subject out of an image, producing an alpha matte.</summary>
    Task<SegmentationResult> SegmentAsync(SegmentationRequest request, CancellationToken ct);

    /// <summary>Estimate a depth map for an image.</summary>
    Task<DepthResult> EstimateDepthAsync(DepthRequest request, CancellationToken ct);

    /// <summary>
    /// No-cost provider test. It must never be able to produce (and therefore bill for) an output:
    /// the implementation sends a request the vendor is documented to reject on validation, and reads
    /// the rejection as proof that the endpoint exists and the key was accepted. Bounds itself with
    /// its own timeout; throws only if <paramref name="ct"/> is cancelled.
    /// </summary>
    Task<ImageAnalysisProbeResult> ProbeAsync(CancellationToken ct);

    /// <summary>What a specific endpoint can do, from its own schema.</summary>
    ImageAnalysisModelCapabilities Describe(string model);
}

/// <summary>Factory for creating <see cref="IImageAnalysisClient"/> instances.</summary>
public interface IImageAnalysisClientFactory
{
    /// <summary>
    /// Gets or creates a client for the given provider, cached by
    /// <see cref="ResolvedImageAnalysisProvider.CacheKey"/>.
    /// </summary>
    IImageAnalysisClient Get(ResolvedImageAnalysisProvider provider);
}
