namespace ReelBolt.Shared.Workflows;

/// <summary>
/// The CLOSED list of video-enhancement operations. Persisted by name by later layers: append-only, never rename or
/// reorder. <c>Extend</c> is the hosted-only outpainting operation and is appended last.
/// </summary>
public enum VideoEnhanceOp
{
    Upscale,
    Interpolate,
    Denoise,
    MotionBlur,

    /// <summary>
    /// Hosted outpainting: the same clip on a larger canvas with generated margins, via
    /// <see cref="Data.Models.InferenceProviderCapability.VideoOutpaint"/>. Appended last: append-only, persisted by name.
    /// </summary>
    Extend
}
