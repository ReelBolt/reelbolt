namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Detects silent audio spans in a media file via ffmpeg's <c>silencedetect</c> filter
/// (plan §2 / WS2). Spans are half-open <c>[StartSec, EndSec)</c>, sorted ascending.
/// </summary>
public interface ISilenceDetector
{
    /// <summary>
    /// Runs <c>-af silencedetect=noise={thresholdDb}dB:d={minSilenceSeconds}</c> over the session
    /// file at <paramref name="virtualPath"/> and returns the detected silence spans.
    /// </summary>
    /// <param name="totalDurationSec">
    /// The file's probed duration (from <see cref="IMediaProbe"/>), used only to close a trailing
    /// silence span that runs to end-of-stream (ffmpeg logs <c>silence_start</c> for it but never
    /// a matching <c>silence_end</c> since the stream simply ends). When <c>null</c>, such a
    /// trailing span is dropped rather than guessed at.
    /// </param>
    Task<IReadOnlyList<(double StartSec, double EndSec)>> DetectAsync(
        IMediaWorkspace workspace,
        string virtualPath,
        double thresholdDb,
        double minSilenceSeconds,
        double? totalDurationSec,
        CancellationToken ct);

    /// <summary>
    /// Compatibility overload for a caller that holds an absolute local path rather than a media
    /// workspace — VideoAnalyze, its last caller, now goes through <see cref="IMediaWorkspace"/>.
    /// The concrete implementation roots a
    /// <see cref="LocalMediaWorkspace"/> at the file's own directory, which resolves to the very
    /// same path.
    /// </summary>
    Task<IReadOnlyList<(double StartSec, double EndSec)>> DetectAsync(
        string localFilePath,
        double thresholdDb,
        double minSilenceSeconds,
        double? totalDurationSec,
        CancellationToken ct);
}
