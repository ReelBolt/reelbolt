namespace ReelBolt.Shared.Inference;

/// <summary>
/// The source clip's frame geometry, as a caller's own probe measured it.
/// </summary>
/// <param name="Width">Pixels per row; the RLE row-major index's width.</param>
/// <param name="Height">Rows.</param>
/// <param name="FrameCount">Frames in the clip when the probe knows them; null when it does not.</param>
public sealed record VideoSegmentationFrameSize(int Width, int Height, int? FrameCount = null);

/// <summary>
/// Supplies the frame geometry a <see cref="IVideoSegmentationClient"/> needs and the vendor does not
/// report.
/// </summary>
/// <remarks>
/// <b>Why this seam exists.</b> <c>fal-ai/sam-3-1/video-rle</c>'s response carries no width, height or
/// frame count: its <c>rle</c> strings are a linear index over the source frame and nothing in the
/// document says what that frame is. The contracts ticket expected the vendor to report it
/// (<see cref="VideoSegmentationResult.Width"/>'s own remark); the real responses (T-002's replay pack)
/// do not. Rather than guess a width — the failure mode is a silently sheared mask — the client asks its
/// caller. The analyze path (T-004) already probes the clip for the analyze step and supplies the real
/// numbers; a caller with no probe gets a refusal, never a default.
/// </remarks>
public interface IVideoSegmentationFrameSizeProbe
{
    /// <summary>Measures the clip behind <paramref name="videoUrl"/> (a short-TTL presigned GET).</summary>
    Task<VideoSegmentationFrameSize> ProbeAsync(Uri videoUrl, CancellationToken ct);
}

/// <summary>The no-cost outcome of a provider connection test against the video-segmentation endpoint.</summary>
/// <param name="IsOk">True when the endpoint exists and the key was accepted.</param>
/// <param name="Reason">What was seen, bounded and key-scrubbed; null when ok.</param>
public sealed record VideoSegmentationProbeResult(bool IsOk, string? Reason);
