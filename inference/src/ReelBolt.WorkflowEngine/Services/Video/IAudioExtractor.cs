namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Extracts a 16 kHz mono signed-16-bit PCM WAV from a local source video — the fixed format both
/// <see cref="ISilenceDetector"/> and WS3's ASR pipeline expect (plan §2/§3). Both methods write
/// to <paramref name="outputWavPath"/>, never returning audio bytes in memory (R21).
/// </summary>
public interface IAudioExtractor
{
    /// <summary>Extracts the full audio track into the workspace.</summary>
    Task ExtractWavAsync(
        IMediaWorkspace workspace, string inputVirtualPath, string outputVirtualPath, CancellationToken ct);

    /// <summary>Extracts the half-open <c>[startSec, endSec)</c> range into the workspace.</summary>
    Task ExtractWavRangeAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        string outputVirtualPath,
        double startSec,
        double endSec,
        CancellationToken ct);

    /// <summary>
    /// Compatibility overloads for a caller that holds absolute local paths rather than a media
    /// workspace — the executors that used to drive them (VideoCompile, then VideoAnalyze) now go
    /// through <see cref="IMediaWorkspace"/>, so this is the adapter for a direct-construction
    /// consumer. The concrete implementation roots one
    /// <see cref="LocalMediaWorkspace"/> at the input file's own directory — both paths sit in the
    /// same scratch directory in every current caller — and addresses both files by name, which
    /// resolves to the very same pair of paths.
    /// </summary>
    Task ExtractWavAsync(string inputVideoPath, string outputWavPath, CancellationToken ct);

    /// <summary>
    /// Extracts only the half-open <c>[startSec, endSec)</c> range — the shape
    /// <c>TranscriptChunkPlanner.PlanChunks</c> returns each chunk in, so a chunk tuple can be
    /// passed straight through as <paramref name="startSec"/>/<paramref name="endSec"/> with no
    /// reshaping at the call site.
    /// </summary>
    Task ExtractWavRangeAsync(
        string inputVideoPath, string outputWavPath, double startSec, double endSec, CancellationToken ct);
}
