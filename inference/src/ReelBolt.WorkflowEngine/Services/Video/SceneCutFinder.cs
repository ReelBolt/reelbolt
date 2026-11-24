using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>One decoded frame's luma statistics (ffmpeg <c>signalstats</c>): time, mean, 10th and 90th percentile.</summary>
public readonly record struct LumaFrame(double PtsSec, double YAvg, double YLow, double YHigh);

/// <summary>
/// <c>VideoAnalyzeStepConfig.SceneDetection = Sensitive</c> (docs/video-editing.md "Finding cuts in finished
/// films"): a second, luma-based detector for dark footage, merged with the scene-score shots.
/// </summary>
/// <remarks>
/// <para>
/// Why it exists: ffmpeg's <c>scene</c> score is an absolute frame difference, so a hard cut between two dark
/// shots (a few luma levels apart) scores far below any usable threshold and the shots stay glued together.
/// </para>
/// <para>
/// Why <c>signalstats</c> rather than <c>scdet</c>: <c>signalstats</c> reports each frame's own mean and its
/// 10th/90th luma percentiles, which is exactly what the jump must be normalised by (the frames' own dynamic
/// range, so a jump of 12 levels is a cut against a +-3 spread and noise against a +-60 spread). <c>scdet</c>
/// thresholds an absolute mean absolute frame difference, the same blind spot as <c>scene</c>. Both are per
/// frame; this one exposes the numbers. The jump must happen between two ADJACENT frames and stand out from
/// the jumps either side of it, so a fade (the same change spread over many frames) and a one-frame flash
/// (out and straight back) are never cuts.
/// </para>
/// Default (<see cref="SceneDetectionMode.Standard"/>) never reaches this class's ffmpeg pass: the standard
/// detector's result is returned untouched.
/// </remarks>
public static class SceneCutFinder
{
    /// <summary>Only frames whose mean luma is at or below this (of 255) are judged; brighter cuts are the scene score's.</summary>
    public const double DarkLumaCeiling = 100;

    /// <summary>The smallest mean-luma jump (levels) that can be a cut at all; below it is encoder noise.</summary>
    public const double MinJumpLevels = 3;

    /// <summary>The jump divided by the two frames' combined luma spread must reach this.</summary>
    public const double MinNormalisedJump = 0.35;

    /// <summary>A spread narrower than this (solid-colour frames) is treated as this wide, so a flat frame cannot score infinity.</summary>
    public const double MinSpreadLevels = 4;

    /// <summary>The cut must be this many times larger than the jumps on either side of it.</summary>
    public const double IsolationFactor = 2;

    /// <summary>
    /// The three per-frame statistics, each written to its own session file. One <c>metadata=print</c> per
    /// key rather than one unfiltered pass: an unfiltered <c>print</c> dumps every <c>signalstats</c> key
    /// (26 of them) per frame, and two filters sharing one <c>file=</c> target overwrite each other — each
    /// opens the file for writing.
    /// </summary>
    public static IReadOnlyList<string> StatKeys { get; } = ["YAVG", "YLOW", "YHIGH"];

    /// <summary>
    /// Bound on one statistics file, checked before it is read: at roughly 60 bytes a frame this is about
    /// six hours of 24 fps footage. A longer film degrades to the scene-score shots instead of pulling an
    /// unbounded frame log into memory.
    /// </summary>
    internal const int MaxStatsFileBytes = 32 * 1024 * 1024;

    private static readonly string[] BaseFlags = { "-nostdin", "-hide_banner", "-y" };
    private static readonly string[] ProtocolWhitelist = UserMediaInputFormats.InputOptions();

    private static readonly Regex FrameHeader = new(
        @"frame:\s*(\d+)\s+pts:\s*\S+\s+pts_time:\s*(-?[0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled);

    private static readonly Regex StatLine = new(
        @"lavfi\.signalstats\.(YAVG|YLOW|YHIGH)=(-?[0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled);

    /// <summary>
    /// Argv of the single luma-statistics pass: video only, no output file, no network protocols, and each
    /// statistic written to its own session file (<paramref name="statFiles"/>, one per
    /// <see cref="StatKeys"/> entry) instead of to the log. The log is a bounded tail
    /// (<see cref="FfmpegVideoToolRunner"/> keeps the last 1,000,000 characters), so on a long film every
    /// early frame — the early dark cuts this pass exists to find — used to be discarded before it was
    /// parsed.
    /// </summary>
    public static string[] BuildArgs(string inputPath, int threads, IReadOnlyList<string> statFiles)
    {
        ArgumentNullException.ThrowIfNull(statFiles);
        if (statFiles.Count != StatKeys.Count)
            throw new ArgumentException("one file per luma statistic is required", nameof(statFiles));

        List<string> args = new(BaseFlags) { "-loglevel", "info" };
        args.AddRange(ProtocolWhitelist);
        args.Add("-threads");
        args.Add(FfmpegArgvFormat.Number(threads));
        args.Add("-i");
        args.Add(inputPath);
        args.Add("-an");
        args.Add("-vf");
        args.Add("signalstats," + string.Join(",", StatKeys.Select((key, index) =>
            $"metadata=print:key=lavfi.signalstats.{key}:file='{EscapeFilterPath(statFiles[index])}'")));
        args.Add("-f");
        args.Add("null");
        args.Add("-");
        return args.ToArray();
    }

    /// <summary>Escapes a path for a single-quoted filter option. Session paths are our own, so this is belt-and-braces.</summary>
    private static string EscapeFilterPath(string path) =>
        path.Replace("\\", "/").Replace("'", "\\'").Replace(":", "\\:");

    /// <summary>
    /// Per-frame luma statistics from the three files <see cref="BuildArgs"/> has ffmpeg write (average,
    /// low and high), joined by frame index and in frame order. A frame missing any of the three is skipped.
    /// </summary>
    public static IReadOnlyList<LumaFrame> ParseFrames(string yavg, string ylow, string yhigh)
    {
        SortedDictionary<int, (double Pts, double? Value)> average = ParseStatFile(yavg);
        SortedDictionary<int, (double Pts, double? Value)> low = ParseStatFile(ylow);
        SortedDictionary<int, (double Pts, double? Value)> high = ParseStatFile(yhigh);

        List<LumaFrame> frames = [];
        foreach ((int frame, (double pts, double? avg)) in average)
        {
            if (avg is not { } avgValue
                || !low.TryGetValue(frame, out (double Pts, double? Value) l) || l.Value is not { } lowValue
                || !high.TryGetValue(frame, out (double Pts, double? Value) h) || h.Value is not { } highValue)
            {
                continue;
            }

            frames.Add(new LumaFrame(pts, avgValue, lowValue, highValue));
        }

        return frames;
    }

    /// <summary>One statistics file, by frame index: the frame's own <c>pts_time</c> and its single value.</summary>
    private static SortedDictionary<int, (double Pts, double? Value)> ParseStatFile(string text)
    {
        SortedDictionary<int, (double Pts, double? Value)> byFrame = new();
        int current = -1;
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Match header = FrameHeader.Match(line);
            if (header.Success)
            {
                current = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
                double pts = double.Parse(header.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
                byFrame.TryAdd(current, (pts, null));
                continue;
            }

            Match stat = StatLine.Match(line);
            if (!stat.Success || current < 0 || !byFrame.TryGetValue(current, out (double Pts, double? Value) entry))
                continue;

            byFrame[current] = stat.Groups[1].Value is "YAVG" or "YLOW" or "YHIGH"
                ? entry with { Value = double.Parse(stat.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture) }
                : entry;
        }

        return byFrame;
    }

    /// <summary>The time of the first frame of every new shot found in dark footage, ascending.</summary>
    public static IReadOnlyList<double> FindCuts(IReadOnlyList<LumaFrame> frames)
    {
        List<double> cuts = new();
        if (frames.Count < 2)
            return cuts;

        double Jump(int i) => i >= 1 && i < frames.Count ? Math.Abs(frames[i].YAvg - frames[i - 1].YAvg) : 0;

        for (int i = 1; i < frames.Count; i++)
        {
            LumaFrame before = frames[i - 1], after = frames[i];
            if (Math.Max(before.YAvg, after.YAvg) > DarkLumaCeiling)
                continue;

            double jump = Jump(i);
            if (jump < MinJumpLevels)
                continue;

            double spread = Math.Max(
                MinSpreadLevels,
                Math.Max(before.YHigh, after.YHigh) - Math.Min(before.YLow, after.YLow));
            if (jump / spread < MinNormalisedJump)
                continue;

            // Within one frame: it must stand out from the jumps either side (a fade moves every frame
            // the same amount; a flash jumps out and straight back).
            if (jump < IsolationFactor * Math.Max(Jump(i - 1), Jump(i + 1)))
                continue;

            cuts.Add(after.PtsSec);
        }

        return cuts;
    }

    /// <summary>
    /// The scene-score spans with every dark cut that is at least <paramref name="minShotSec"/> from the file
    /// edges, from every scene boundary and from every other accepted cut. Scene boundaries are never dropped.
    /// </summary>
    public static IReadOnlyList<(double StartSec, double EndSec)> Merge(
        IReadOnlyList<(double StartSec, double EndSec)> sceneSpans,
        IReadOnlyList<double> darkCuts,
        double totalDurationSec,
        double minShotSec)
    {
        if (darkCuts.Count == 0)
            return sceneSpans;

        List<double> boundaries = sceneSpans.Skip(1).Select(s => s.StartSec).ToList();
        foreach (double cut in darkCuts.OrderBy(c => c))
        {
            if (cut < minShotSec || cut > totalDurationSec - minShotSec)
                continue;
            if (boundaries.Any(b => Math.Abs(b - cut) < minShotSec))
                continue;
            boundaries.Add(cut);
        }

        return ShowinfoOutputParser.BuildShotSpans(boundaries.OrderBy(b => b).ToList(), totalDurationSec);
    }

    /// <summary>
    /// The shot spans of a source under <paramref name="mode"/>. <see cref="SceneDetectionMode.Standard"/> is
    /// exactly one call to <paramref name="standard"/>; <see cref="SceneDetectionMode.Sensitive"/> adds the
    /// luma pass and merges it with that result; <see cref="SceneDetectionMode.Soft"/> adds the lagged-difference pass.
    /// </summary>
    public static async Task<IReadOnlyList<(double StartSec, double EndSec)>> DetectAsync(
        IShotDetector standard,
        IMediaWorkspace workspace,
        string virtualPath,
        SceneDetectionMode mode,
        double sceneThreshold,
        double totalDurationSec,
        double minShotSec,
        VideoEditingOptions options,
        CancellationToken ct,
        (int Num, int Den)? sourceFps = null) =>
        (await DetectWithMethodsAsync(
            standard, workspace, virtualPath, mode, sceneThreshold, totalDurationSec, minShotSec, options, ct, sourceFps)
            .ConfigureAwait(false)).Spans;

    /// <summary>
    /// <see cref="DetectAsync"/> plus the start times of the boundaries the soft pass found
    /// (<see cref="SceneDetectionMode.Soft"/>; empty for every other mode).
    /// <see cref="SceneDetectionMode.Soft"/> = the standard shots plus <see cref="SoftTransitionDetector"/>'s boundaries.
    /// </summary>
    public static async Task<SceneDetectionResult> DetectWithMethodsAsync(
        IShotDetector standard,
        IMediaWorkspace workspace,
        string virtualPath,
        SceneDetectionMode mode,
        double sceneThreshold,
        double totalDurationSec,
        double minShotSec,
        VideoEditingOptions options,
        CancellationToken ct,
        (int Num, int Den)? sourceFps = null)
    {
        IReadOnlyList<(double StartSec, double EndSec)> sceneSpans = await standard
            .DetectShotsAsync(workspace, virtualPath, sceneThreshold, totalDurationSec, ct).ConfigureAwait(false);
        if (mode == SceneDetectionMode.Soft)
        {
            return await DetectSoftAsync(
                workspace, virtualPath, sceneSpans, totalDurationSec, minShotSec, options, sourceFps ?? (25, 1), ct)
                .ConfigureAwait(false);
        }

        if (mode != SceneDetectionMode.Sensitive)
            return new SceneDetectionResult(sceneSpans, []);

        string mediaPath = await workspace.MaterializeAsync(virtualPath, ct).ConfigureAwait(false);

        // Statistics go to session files, not to the log: the log is a bounded TAIL, so on a long film it
        // holds only the last frames and the early dark cuts were never parsed (T-211 item 7).
        string stem = $"luma-{Guid.NewGuid():N}";
        List<string> statVirtualPaths = [.. StatKeys.Select(key => $"{stem}-{key}.txt")];
        List<string> statArgvPaths = [];
        foreach (string statVirtualPath in statVirtualPaths)
            statArgvPaths.Add(await workspace.MaterializeAsync(statVirtualPath, ct).ConfigureAwait(false));

        VideoToolResult result = await workspace
            .RunFfmpegAsync(
                BuildArgs(mediaPath, options.FfmpegThreads, statArgvPaths),
                SourceTimeoutPolicy.Analyze(options),
                ct)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw VideoToolTimedOutException.For(
                $"ffmpeg luma-jump scene detection failed for '{virtualPath}' (exitCode={result.ExitCode}, timedOut={result.TimedOut}).",
                result.TimedOut, options.AnalysisProxyMinDurationSeconds);
        }

        string[] stats = new string[StatKeys.Count];
        try
        {
            for (int i = 0; i < stats.Length; i++)
            {
                stats[i] = Encoding.UTF8.GetString(
                    await workspace.ReadAsync(statVirtualPaths[i], MaxStatsFileBytes, ct).ConfigureAwait(false));
            }
        }
        catch (MediaWorkspaceFileTooLargeException)
        {
            // Above the bound (roughly six hours of 24 fps footage) the scene-score shots are still a
            // usable answer, and this pass never fails a step it can only improve.
            return new SceneDetectionResult(sceneSpans, []);
        }

        return new SceneDetectionResult(
            Merge(sceneSpans, FindCuts(ParseFrames(stats[0], stats[1], stats[2])), totalDurationSec, minShotSec), []);
    }

    private static async Task<SceneDetectionResult> DetectSoftAsync(
        IMediaWorkspace workspace,
        string virtualPath,
        IReadOnlyList<(double StartSec, double EndSec)> sceneSpans,
        double totalDurationSec,
        double minShotSec,
        VideoEditingOptions options,
        (int Num, int Den) fps,
        CancellationToken ct)
    {
        fps = SoftTransitionDetector.SamplingFps(fps.Num, fps.Den);
        double fpsValue = fps.Num / (double)fps.Den;

        string mediaPath = await workspace.MaterializeAsync(virtualPath, ct).ConfigureAwait(false);
        string thumbVirtualPath = $"soft-{Guid.NewGuid():N}.rgb";
        string thumbArgvPath = await workspace.MaterializeAsync(thumbVirtualPath, ct).ConfigureAwait(false);

        byte[] thumbs;
        try
        {
            VideoToolResult result = await workspace
                .RunFfmpegAsync(
                    SoftTransitionDetector.BuildArgs(
                        mediaPath, options.FfmpegThreads, fps.Num, fps.Den, thumbArgvPath,
                        SoftTransitionDetector.FrameCap(totalDurationSec, fpsValue)),
                    SourceTimeoutPolicy.Analyze(options),
                    ct)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                // The soft pass only improves the standard shots, so it never fails the step: a failed or timed-out decode leaves
                // the scene-score shots as they were. A cancelled run still cancels.
                ct.ThrowIfCancellationRequested();
                return new SceneDetectionResult(sceneSpans, []);
            }

            try
            {
                thumbs = await workspace.ReadAsync(thumbVirtualPath, SoftTransitionDetector.MaxThumbBytes, ct).ConfigureAwait(false);
            }
            catch (MediaWorkspaceFileTooLargeException)
            {
                return new SceneDetectionResult(sceneSpans, []);
            }
        }
        finally
        {
            await DiscardAsync(workspace, thumbVirtualPath, thumbArgvPath).ConfigureAwait(false);
        }

        return SoftTransitionDetector.Merge(
            sceneSpans,
            SoftTransitionDetector.FindBoundaries(thumbs, fpsValue),
            totalDurationSec,
            minShotSec);
    }

    /// <summary>
    /// Best-effort removal of a scratch file on every exit path (success, failure, timeout, cancellation).
    /// The workspace has no delete operation, so the file is truncated through it and, when the materialised
    /// path is on this host, deleted.
    /// </summary>
    private static async Task DiscardAsync(IMediaWorkspace workspace, string virtualPath, string argvPath)
    {
        try { await workspace.WriteBytesAsync(virtualPath, [], CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* cleanup never masks the real outcome */ }

        try { File.Delete(argvPath); }
        catch (Exception) { /* not local, or already gone */ }
    }
}
