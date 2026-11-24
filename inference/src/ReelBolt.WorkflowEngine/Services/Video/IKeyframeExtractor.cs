namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Extracts a single representative frame from a local source video as a JPEG — the Phase 2
/// vision-captioning building block (see docs/video-editing.md "Vision captioning"). Mirrors
/// <see cref="IAudioExtractor"/>/<see cref="IFrameGridSampler"/>'s calling convention exactly (a
/// thin seam over <see cref="IVideoToolRunner"/> built from a dedicated
/// <see cref="FfmpegArgvBuilder"/> method) so a future phase adding its own new ffmpeg operation
/// (e.g. Phase 3's compile-time overlay rendering) has a single established pattern to follow.
/// </summary>
public interface IKeyframeExtractor
{
    /// <summary>
    /// Writes a single JPEG frame sampled at <paramref name="atSec"/> to
    /// <paramref name="outputVirtualPath"/>, downscaled to at most <paramref name="maxWidth"/> pixels
    /// wide (never upscaled, aspect ratio preserved). Never returns image bytes in memory — same
    /// "write to a session file" discipline as <see cref="IAudioExtractor"/>.
    /// </summary>
    Task ExtractKeyframeAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        string outputVirtualPath,
        double atSec,
        int maxWidth,
        CancellationToken ct);

    /// <summary>
    /// Phase 4 (<c>KeyframesPerShot</c> &gt; 1) — writes ONE contact-sheet JPEG hstacking a frame
    /// from each of <paramref name="atSecs"/> (left to right). A separate method, not an overload
    /// of <see cref="ExtractKeyframeAsync(IMediaWorkspace, string, string, double, int, CancellationToken)"/>,
    /// so <c>KeyframesPerShot == 1</c> keeps taking the exact original single-frame path
    /// byte-for-byte.
    /// </summary>
    Task ExtractContactSheetAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        string outputVirtualPath,
        IReadOnlyList<double> atSecs,
        int maxWidth,
        CancellationToken ct);

    /// <summary>
    /// The same contact sheet, with <paramref name="maxRunTime"/> as the longest ffmpeg itself may run for it (never
    /// longer than the usual analysis time). The wait for the shared ffmpeg lane is not part of it. An extractor that
    /// does not know better makes the sheet with its usual time.
    /// </summary>
    Task ExtractContactSheetAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        string outputVirtualPath,
        IReadOnlyList<double> atSecs,
        int maxWidth,
        TimeSpan maxRunTime,
        CancellationToken ct) =>
        ExtractContactSheetAsync(workspace, inputVirtualPath, outputVirtualPath, atSecs, maxWidth, ct);

    /// <summary>
    /// One frame, cropped to a normalized region (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="width"/>, <paramref name="height"/> as fractions of the frame) and scaled to
    /// at most <paramref name="maxWidth"/> — the zoomed view an object locator refines a coarse
    /// box on. Every number is first-party; nothing in the filter is model-supplied text.
    /// </summary>
    Task ExtractCroppedKeyframeAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        string outputVirtualPath,
        double atSec,
        double x,
        double y,
        double width,
        double height,
        int maxWidth,
        CancellationToken ct) =>
        throw new NotSupportedException("This keyframe extractor cannot crop.");

    /// <summary>
    /// Compatibility overloads for a caller that holds absolute local paths rather than a media
    /// workspace — the executors that used to drive them (VideoCompile, then VideoAnalyze) now go
    /// through <see cref="IMediaWorkspace"/>, so this is the adapter for a direct-construction
    /// consumer. The concrete implementation roots one
    /// <see cref="LocalMediaWorkspace"/> at the input file's own directory — input and output sit in
    /// the same scratch directory in every current caller — and addresses both by name, which
    /// resolves to the very same pair of paths.
    /// </summary>
    Task ExtractKeyframeAsync(
        string inputVideoPath, string outputJpgPath, double atSec, int maxWidth, CancellationToken ct);

    /// <inheritdoc cref="ExtractKeyframeAsync(string, string, double, int, CancellationToken)"/>
    Task ExtractContactSheetAsync(
        string inputVideoPath, string outputJpgPath, IReadOnlyList<double> atSecs, int maxWidth, CancellationToken ct);

    /// <inheritdoc cref="ExtractKeyframeAsync(string, string, double, int, CancellationToken)"/>
    Task ExtractCroppedKeyframeAsync(
        string inputVideoPath, string outputJpgPath, double atSec,
        double x, double y, double width, double height, int maxWidth, CancellationToken ct) =>
        throw new NotSupportedException("This keyframe extractor cannot crop.");
}
