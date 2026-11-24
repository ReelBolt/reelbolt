namespace ReelBolt.Shared.Inference;

/// <summary>
/// Client for an external image generation / editing provider.
/// </summary>
/// <remarks>
/// <b>One client, two capabilities.</b> <see cref="GenerateAsync"/> serves
/// <c>InferenceProviderCapability.ImageGeneration</c> and
/// <see cref="EditAsync"/>/<see cref="InpaintAsync"/> serve <c>ImageEdit</c>, because every vendor
/// we integrate exposes them from the same host, the same credential and — for MiniMax and BFL —
/// the same endpoint path with different body fields. Splitting them into two interfaces would mean
/// two factories, two resolved types and two registrations for one HTTP client.
/// <para>
/// They are still two <c>InferenceProvider</c> rows with two independent "at most one default"
/// constraints, so the operator decision of which vendor generates and which edits is preserved;
/// this interface is about code reuse, not about collapsing the configuration surface.
/// </para>
/// <para>
/// <b>An operation a vendor does not document must throw.</b> <see cref="InpaintAsync"/> on a vendor
/// with no mask API throws <see cref="NotSupportedException"/> — the same discipline
/// <c>TranscriptionClientFactory</c> applies to Anthropic — rather than degrading into the nearest
/// operation it does support, which would silently produce a different edit than the one asked for.
/// <see cref="Describe"/> is how a caller finds out before it calls.
/// </para>
/// </remarks>
public interface IImageClient
{
    /// <summary>Text (+ optional reference image) to image.</summary>
    Task<GeneratedImageSet> GenerateAsync(ImageGenerationRequest request, CancellationToken ct);

    /// <summary>
    /// Instruction edit: an existing image plus a sentence. Throws
    /// <see cref="NotSupportedException"/> when <see cref="Describe"/> says the model does not
    /// document instruction editing.
    /// </summary>
    Task<GeneratedImageSet> EditAsync(ImageEditRequest request, CancellationToken ct);

    /// <summary>
    /// Mask-based inpaint (black preserved, white regenerated). Throws
    /// <see cref="NotSupportedException"/> when <see cref="Describe"/> says the model does not
    /// document mask inpainting.
    /// </summary>
    Task<GeneratedImageSet> InpaintAsync(ImageInpaintRequest request, CancellationToken ct);

    /// <summary>
    /// Runs one <see cref="ImageOperation"/> (expand, replace background, relight, upscale) on the
    /// picture at <see cref="ImageTransformRequest.ImageUrl"/>. A client whose vendor documents no
    /// endpoint for that operation throws <see cref="NotSupportedException"/> naming the vendor
    /// basis, and the caller answers <c>OPERATION_UNSUPPORTED</c> before anything is sent.
    /// </summary>
    Task<GeneratedImageSet> TransformAsync(ImageTransformRequest request, CancellationToken ct);

    /// <summary>
    /// No-cost provider test. It must never be able to produce (and therefore bill for) an image:
    /// each implementation sends a request the vendor is documented to REJECT on validation, and
    /// reads the rejection as proof that the endpoint exists and the key was accepted. Bounds
    /// itself with its own timeout; throws only if <paramref name="ct"/> is cancelled.
    /// </summary>
    Task<ImageProbeResult> ProbeAsync(CancellationToken ct);

    /// <summary>Describes the capabilities of a specific model, from the vendor's own documentation.</summary>
    ImageModelCapabilities Describe(string model);
}

/// <summary>Factory for creating <see cref="IImageClient"/> instances.</summary>
public interface IImageClientFactory
{
    /// <summary>
    /// Gets or creates a client configured with the given provider settings, cached by
    /// <see cref="ResolvedImageProvider.CacheKey"/>.
    /// </summary>
    IImageClient Get(ResolvedImageProvider provider);

    /// <summary>
    /// The same client WITHOUT the metering decorator, for a caller that records the usage itself once the result is
    /// delivered (the decorator records the moment the provider answers, which cannot be taken back). The default is
    /// <see cref="Get"/>, so a factory with no decorator needs no override.
    /// </summary>
    IImageClient GetUnmetered(ResolvedImageProvider provider) => Get(provider);
}
