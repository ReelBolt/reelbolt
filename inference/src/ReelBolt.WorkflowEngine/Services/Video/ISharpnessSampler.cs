namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Measures native-resolution focus (Phase 4) via a dedicated per-shot ffmpeg invocation
/// (<see cref="FfmpegArgvBuilder.BuildSharpnessPatchArgs"/>) plus a pure-C# Laplacian-variance
/// metric — see docs/video-editing.md. Mirrors <see cref="IKeyframeExtractor"/>'s calling
/// convention (a thin seam over <see cref="IVideoToolRunner"/>), except this seam also reads the
/// raw output back and computes the metric itself, since the caller only wants a number.
/// </summary>
public interface ISharpnessSampler
{
    /// <summary>
    /// Returns a 0..1 focus score, or null when the patch could not be produced/parsed — never
    /// throws. <paramref name="scratchFileName"/> is a session-relative name inside
    /// <paramref name="workspace"/> for the temporary raw-patch file, and the patch is read back
    /// through <see cref="IMediaWorkspace.ReadAsync(string, CancellationToken)"/> rather than
    /// straight off this host's filesystem.
    /// </summary>
    Task<double?> MeasureAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        string scratchFileName,
        double atSec,
        int patch,
        CancellationToken ct);

    /// <summary>
    /// Compatibility overload for a caller that holds absolute local paths rather than a media
    /// workspace — VideoAnalyze, its last caller, now goes through <see cref="IMediaWorkspace"/>.
    /// The concrete implementation roots a
    /// <see cref="LocalMediaWorkspace"/> at <paramref name="scratch"/>, so the patch file resolves
    /// to the very same path <see cref="VideoScratchSpace.GetPath"/> returns.
    /// </summary>
    Task<double?> MeasureAsync(
        string inputVideoPath, VideoScratchSpace scratch, string scratchFileName,
        double atSec, int patch, CancellationToken ct);
}
