using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Downloads one project music file, decodes it to 16 kHz mono through the existing
/// <see cref="IAudioExtractor"/>, and measures its beat with <see cref="MusicBeatAnalyzer"/> (see
/// docs/video-editing.md "Cutting to the beat"). Shared by the analyze step (beat facts for the
/// editor) and the compile step (<c>BeatSync</c> when the analyze step measured nothing).
/// </summary>
public static class MusicBeatProbe
{
    /// <summary>
    /// The workspace arm: the music file is staged into the media session and the WAV is decoded
    /// there, so the measurement runs wherever the workspace is — on this host for a local run, on
    /// the paired desktop runner for a remote one (D11). This is the shape every caller should use;
    /// the <see cref="VideoScratchSpace"/> overload below is the absolute-path compatibility adapter
    /// D9a left for the call sites that had not been ported yet.
    /// <para>
    /// The two arms are byte-identical for a local workspace: the virtual names used here are
    /// exactly the scratch file names the other overload builds, and <see cref="LocalMediaWorkspace"/>
    /// resolves them to exactly the paths <see cref="VideoScratchSpace.GetPath"/> returns. That is
    /// what let D11's "a remote session has no local scratch, so skip the probe" workaround go away.
    /// </para>
    /// </summary>
    /// <param name="workspace">
    /// The media session to measure in. It must carry the project scope <paramref name="file"/>'s
    /// storage key belongs to — <see cref="IMediaWorkspace.StageAsync"/> enforces that.
    /// </param>
    /// <param name="namePrefix">
    /// Session-relative prefix for the two files this probe produces — <c>{namePrefix}{ext}</c> for
    /// the staged music and <c>{namePrefix}.wav</c> for the decode — so two probes in one session
    /// cannot collide.
    /// </param>
    /// <returns>The measured grid, or null when the file carries no beat or the measurement failed (use <see cref="TryMeasureAsync"/> to tell the two apart). Throws only on cancellation.</returns>
    public static async Task<MusicBeatAnalysis?> MeasureAsync(
        IMediaWorkspace workspace, IAudioExtractor extractor, ProjectWorkspaceFile file,
        string namePrefix, CancellationToken ct)
        => (await TryMeasureAsync(workspace, extractor, file, namePrefix, logger: null, ct).ConfigureAwait(false)).Analysis;

    /// <summary>
    /// <see cref="MeasureAsync(IMediaWorkspace, IAudioExtractor, ProjectWorkspaceFile, string, CancellationToken)"/> that
    /// says WHY there is no grid: a measurement that failed carries <see cref="MusicBeatMeasurement.FailureCause"/>
    /// (and is logged with its exception class and message); a track that decoded but has no detectable beat has an
    /// empty cause. The two used to be one silent null that ended as a plain <c>no_beat_grid</c>.
    /// </summary>
    public static async Task<MusicBeatMeasurement> TryMeasureAsync(
        IMediaWorkspace workspace, IAudioExtractor extractor, ProjectWorkspaceFile file,
        string namePrefix, ILogger? logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        try
        {
            string ext = Path.GetExtension(file.StorageKey) is { Length: > 0 } e ? e : ".mp3";
            string audioVirtualPath = $"{namePrefix}{ext}";
            string wavVirtualPath = $"{namePrefix}.wav";
            await workspace.StageAsync(file.StorageKey, audioVirtualPath, ct).ConfigureAwait(false);
            await extractor.ExtractWavAsync(workspace, audioVirtualPath, wavVirtualPath, ct).ConfigureAwait(false);
            byte[] wav = await ReadWavAsync(workspace, wavVirtualPath, ct).ConfigureAwait(false);
            float[]? samples = WavRmsSampler.ReadMonoSamples(wav, out int rate);
            if (samples is null)
                return MusicBeatMeasurement.Failed("wav_unreadable");
            return new MusicBeatMeasurement(MusicBeatAnalyzer.Analyze(samples, rate), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure(ex, file, logger);
        }
    }

    /// <summary>
    /// The decoded WAV is 32,000 bytes per second, so a track longer than about 131 s is over the 4 MiB a
    /// remote session answers an inline read with. The plain read stays the first try (nothing changes for a
    /// local workspace or a short track); a refusal for size retries through the publish-then-fetch arm.
    /// </summary>
    private static async Task<byte[]> ReadWavAsync(IMediaWorkspace workspace, string wavVirtualPath, CancellationToken ct)
    {
        try
        {
            return await workspace.ReadAsync(wavVirtualPath, ct).ConfigureAwait(false);
        }
        catch (MediaWorkspaceFileTooLargeException)
        {
            return await workspace.ReadAsync(wavVirtualPath, MaxWavBytes, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The most decoded audio the grid is measured from: 64 MiB, about 35 minutes at 16 kHz mono.</summary>
    internal const int MaxWavBytes = 64 * 1024 * 1024;

    private static MusicBeatMeasurement Failure(Exception ex, ProjectWorkspaceFile file, ILogger? logger)
    {
        string cause = CauseOf(ex);
        logger?.LogWarning(
            ex, "Beat grid measurement of '{File}' failed ({Cause}): {ExceptionType}: {Message}",
            file.OriginalFileName, cause, ex.GetType().Name, ex.Message);
        return MusicBeatMeasurement.Failed(cause);
    }

    /// <summary>A short stable token for an exception: the runner's or workspace's own code when it carries one, else the class name.</summary>
    internal static string CauseOf(Exception ex) => ex switch
    {
        MediaWorkspaceFileTooLargeException => "file_too_large",
        MediaWorkspaceException m => m.Code,
        ReelBolt.WorkflowEngine.Services.Compute.RunnerSessionOpException r => r.Code,
        _ => ex.GetType().Name
    };

    /// <summary>
    /// The absolute-path compatibility arm (D9a). Kept for callers that still hold a
    /// <see cref="VideoScratchSpace"/>; new call sites belong on the workspace arm above.
    /// </summary>
    /// <inheritdoc cref="MeasureAsync(IMediaWorkspace, IAudioExtractor, ProjectWorkspaceFile, string, CancellationToken)"/>
    public static async Task<MusicBeatAnalysis?> MeasureAsync(
        IProjectFileWorkspace workspace, IAudioExtractor extractor, Guid projectId, ProjectWorkspaceFile file,
        VideoScratchSpace scratch, string scratchName, CancellationToken ct, ILogger? logger = null)
    {
        try
        {
            string ext = Path.GetExtension(file.StorageKey) is { Length: > 0 } e ? e : ".mp3";
            string local = scratch.GetPath($"{scratchName}{ext}");
            string wav = scratch.GetPath($"{scratchName}.wav");
            await workspace.DownloadStorageKeyToFileAsync(projectId, file.StorageKey, local, ct);
            await extractor.ExtractWavAsync(local, wav, ct);
            float[]? samples = WavRmsSampler.ReadMonoSamples(await File.ReadAllBytesAsync(wav, ct), out int rate);
            return samples is null ? null : MusicBeatAnalyzer.Analyze(samples, rate);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex, "Beat grid measurement of '{File}' failed: {ExceptionType}: {Message}",
                file.OriginalFileName, ex.GetType().Name, ex.Message);
            return null;
        }
    }

    /// <summary>Stable warning code for a beat grid lost to a read or decode failure (workspace, transport, extraction, unreadable WAV). Never used for a track that decoded fine but has no detectable beat.</summary>
    public const string BeatGridUnavailableCode = "beat_grid_unavailable";

    /// <summary>
    /// The step-output warning for a beat grid lost to a read or decode failure (workspace, transport, extraction or
    /// an unreadable WAV): stable code plus the cause token (see CauseOf). A track that decoded fine but has no
    /// detectable beat is NOT a failure and never produces this warning.
    /// </summary>
    public static JsonObject UnavailableWarning(string cause) => new()
    {
        ["code"] = BeatGridUnavailableCode,
        ["message"] = "The music's beat could not be measured because the track could not be read or decoded, so no beat grid is used. The step continued without it.",
        ["cause"] = cause
    };

    /// <summary>The artifact form of a measurement.</summary>
    public static VideoAnalysisMusicBeat ToFacts(MusicBeatAnalysis a) => new(
        a.Bpm, a.BeatSec, Math.Round(a.BarSec, 5), a.BeatPhaseSec, a.BarPhaseSec, a.DurationSec,
        a.DropSec, a.OutroSec, a.Confidence, a.TempoWord);

    /// <summary>The measurement back from its artifact form.</summary>
    public static MusicBeatAnalysis FromFacts(VideoAnalysisMusicBeat f) => new(
        f.Bpm, f.BeatSec, f.BeatPhaseSec, f.BarPhaseSec, f.DurationSec, f.DropSec, f.OutroSec, f.Confidence);

    /// <summary>
    /// The view node an editor reasons with: the tempo as a word and in BPM, beat and bar length,
    /// and the drop and outro both as words ("bar 9, at 13.9 s") and seconds. Facts to plan run
    /// lengths with — the editor still only ever outputs ids.
    /// </summary>
    public static JsonObject ViewNode(VideoAnalysisMusicBeat f)
    {
        static string S(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        int? Bar(double? t) => t is { } x ? (int)Math.Round((x - f.BarPhaseSec) / f.BarSec) + 1 : null;
        var node = new JsonObject
        {
            ["bpm"] = Math.Round(f.Bpm, 1),
            ["tempo"] = f.Tempo,
            ["beatSec"] = Math.Round(f.BeatSec, 3),
            ["barSec"] = Math.Round(f.BarSec, 3),
            ["lengthSec"] = Math.Round(f.DurationSec, 1),
            ["grid"] = f.Confidence >= 0.3 ? "steady" : "loose"
        };
        if (f.DropSec is { } drop)
        {
            node["drop"] = $"bar {Bar(drop)}, at {S(drop)} s";
            node["dropSec"] = Math.Round(drop, 2);
        }

        if (f.OutroSec is { } outro)
        {
            node["outro"] = $"bar {Bar(outro)}, at {S(outro)} s";
            node["outroSec"] = Math.Round(outro, 2);
        }

        return node;
    }
}

/// <summary>
/// One beat-grid measurement: the grid, or why there is none. <see cref="FailureCause"/> is null for a track that
/// decoded fine (a grid, or no detectable beat); set when the measurement itself failed.
/// </summary>
public sealed record MusicBeatMeasurement(MusicBeatAnalysis? Analysis, string? FailureCause)
{
    /// <summary>The step-output reason: <c>beat_grid_failed:&lt;cause&gt;</c>, or <c>no_beat_grid</c> for a track with no detectable beat.</summary>
    public string? Reason => Analysis is not null ? null : FailureCause is null ? "no_beat_grid" : ReasonFor(FailureCause);

    public static MusicBeatMeasurement Failed(string cause) => new(null, cause);

    public static string ReasonFor(string cause) => "beat_grid_failed:" + cause;
}
