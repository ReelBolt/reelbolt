using Microsoft.Extensions.Options;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The raw output of one grid-sampling ffmpeg pass: a flat RGB24 buffer of
/// <see cref="FrameCount"/> frames, each <c>GridWidth * GridHeight * 3</c> bytes, in time order.
/// Frame <c>i</c>'s sample time is exactly <c>i / EffectiveFps</c> — the <c>fps</c> filter emits
/// constant-frame-rate output from t=0, so no timestamp parsing is needed anywhere downstream,
/// only byte-offset arithmetic.
/// </summary>
public sealed record FrameGridResult(
    byte[] PixelData,
    int GridWidth,
    int GridHeight,
    double EffectiveFps,
    int FrameCount);

/// <summary>
/// Decimates and downscales a source video to a low-res raw RGB pixel grid — the single ffmpeg
/// pass every Phase 1 visual descriptor is derived from (see docs/video-editing.md). The box-
/// average downscale (<c>scale=...:flags=area</c>) is load-bearing: each output pixel is the
/// exact mean of its source block, which is what makes per-region statistics meaningful.
/// </summary>
public interface IFrameGridSampler
{
    /// <summary>
    /// <paramref name="sampleFps"/> is clamped downward for very long videos so the grid buffer
    /// never exceeds <paramref name="maxSampleFrames"/> frames:
    /// <c>effectiveFps = min(sampleFps, maxSampleFrames / totalDurationSec)</c> —
    /// <paramref name="maxSampleFrames"/> &lt;= 0 falls back to a compiled-in default rather than
    /// disabling the cap (see <c>FfmpegFrameGridSampler.ComputeEffectiveFps</c>).
    /// <paramref name="workspace"/> owns both the source and the sampler's own raw-grid file, which
    /// it writes under a session-relative name of its own choosing.
    /// </summary>
    Task<FrameGridResult> SampleAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        double sampleFps,
        int gridWidth,
        int gridHeight,
        double totalDurationSec,
        int maxSampleFrames,
        CancellationToken ct);

    /// <summary>
    /// Same pass as the workspace <c>SampleAsync</c> above with a chosen chroma decode. A separate
    /// overload (not an optional parameter) so the 8-argument call every other consumer makes keeps
    /// its exact shape. Only the insert tracker's grid passes <see cref="GridChroma.FullChroma"/>.
    /// </summary>
    Task<FrameGridResult> SampleAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        double sampleFps,
        int gridWidth,
        int gridHeight,
        double totalDurationSec,
        int maxSampleFrames,
        GridChroma chroma,
        CancellationToken ct);

    /// <summary>
    /// Same pass, decoded to 8-bit grayscale (<c>format=gray</c>, one byte per pixel) with a
    /// bicubic rather than box-average downscale — the input the feature tracker needs, at a third
    /// of the memory of RGB. <see cref="FrameGridResult.PixelData"/> then holds
    /// <c>GridWidth * GridHeight</c> bytes per frame.
    /// </summary>
    Task<FrameGridResult> SampleGrayAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        double sampleFps,
        int gridWidth,
        int gridHeight,
        double totalDurationSec,
        int maxSampleFrames,
        CancellationToken ct);

    /// <summary>
    /// Compatibility overload: <paramref name="localFilePath"/> is a local path and
    /// <paramref name="scratch"/> resolves the sampler's own scratch-file path(s) the same way every
    /// other video scratch-file consumer does, via <see cref="VideoScratchSpace.GetPath"/>.
    /// </summary>
    Task<FrameGridResult> SampleAsync(
        string localFilePath,
        VideoScratchSpace scratch,
        double sampleFps,
        int gridWidth,
        int gridHeight,
        double totalDurationSec,
        int maxSampleFrames,
        CancellationToken ct);

    /// <inheritdoc cref="SampleAsync(string, VideoScratchSpace, double, int, int, double, int, CancellationToken)"/>
    Task<FrameGridResult> SampleGrayAsync(
        string localFilePath,
        VideoScratchSpace scratch,
        double sampleFps,
        int gridWidth,
        int gridHeight,
        double totalDurationSec,
        int maxSampleFrames,
        CancellationToken ct);
}
