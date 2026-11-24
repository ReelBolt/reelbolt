namespace ReelBolt.Shared.Inference;

/// <summary>
/// Client for a hosted, TEXT-PROMPTED VIDEO segmentation model — "find this object in every frame of
/// this clip" — served by <c>InferenceProviderCapability.VideoSegmentation</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Declared by the contracts ticket, implemented by T-003.</b> No implementation, no DI
/// registration and no vendor call exists in this ticket; this interface and its DTOs are the shape
/// the analyze path (T-004) codes against so the provider work lands behind a fixed seam.
/// </para>
/// <para>
/// <b>Raw, pre-decode.</b> The vendor answers with run-length-encoded masks over a row-major index of
/// a W×H frame, one string per tracked object per frame. Decoding is deliberately NOT this client's
/// job: the decoder is pure first-party code that must be testable against committed real responses
/// with no HTTP client in the room, and the RLE grid's width is the one thing the vendor does not
/// state, so <see cref="VideoSegmentationResult.Width"/>/<see cref="VideoSegmentationResult.Height"/>
/// are what the decoder must use — never a re-derived size.
/// </para>
/// <para>
/// <b>No key material crosses this seam.</b> A call is made with a client the factory already holds;
/// the result carries a <c>ProviderFingerprint</c> (provider id + kind + model) for the artifact and
/// the step-cache key, never a key, a bearer token or a signed URL.
/// </para>
/// <para>
/// <b>Fail closed.</b> An implementation throws for anything it cannot represent — an unknown model,
/// a schema it has not read, a queue status outside the documented vocabulary, a body that is not the
/// documented shape. It never returns a plausible-looking empty result, because "the model found no
/// object" and "the request was malformed" must not be the same value: the caller decides, from a
/// throw, to fail the step with <c>SEGMENTATION_BAD_RESPONSE</c>/<c>SEGMENTATION_PROVIDER_ERROR</c>.
/// </para>
/// </remarks>
public interface IVideoSegmentationClient
{
    /// <summary>
    /// Segment <paramref name="request"/>'s video into per-frame masks for the prompted object(s).
    /// Throws on any refusal or transport failure; never returns a partial, silently-empty result.
    /// </summary>
    Task<VideoSegmentationResult> SegmentVideoAsync(VideoSegmentationRequest request, CancellationToken ct);
}
