using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>A soft transition: the frame time it is centred on and its lagged-difference score (0..1).</summary>
public readonly record struct SoftBoundary(double TimeSec, double Score);

/// <summary>The shot spans under <c>SceneDetection: Soft</c> and the start times of the soft-found boundaries among them.</summary>
public sealed record SceneDetectionResult(
    IReadOnlyList<(double StartSec, double EndSec)> Spans,
    IReadOnlyList<double> SoftBoundaries);

/// <summary>
/// <c>VideoAnalyzeStepConfig.SceneDetection = Soft</c>: finds the dissolves, whips and zoom/warp changes that
/// ffmpeg's scene score (&gt; 0.30) misses, as real shot boundaries.
/// </summary>
/// <remarks>
/// A real 19.8 s clip held 7 angle changes: 2 hard (scene score 0.40, 0.58) and 5 soft (scene score 0.05-0.08).
/// A frame difference taken <see cref="LagSec"/> apart (frames t-0.1 s and t+0.1 s) on tiny thumbnails scores
/// every one of them above <see cref="Threshold"/>, while a static shot and sensor flicker stay far below.
/// Pure core (<see cref="FindBoundaries"/>, <see cref="Merge"/>) over a thumbnail frame sequence, plus one ffmpeg
/// decode pass that produces the thumbnails (<see cref="BuildArgs"/>). Scores are integer byte sums, so a
/// plateau (a hard cut scores the same for every frame within the lag) is detected exactly and centred.
/// </remarks>
public static class SoftTransitionDetector
{
    /// <summary>Thumbnail width in pixels.</summary>
    public const int ThumbWidth = 24;

    /// <summary>Thumbnail height in pixels.</summary>
    public const int ThumbHeight = 42;

    /// <summary>Bytes of one rgb24 thumbnail.</summary>
    public const int FrameBytes = ThumbWidth * ThumbHeight * 3;

    /// <summary>Half the difference span: frames t-<c>LagSec</c>/2 and t+<c>LagSec</c>/2 are compared.</summary>
    public const double LagSec = 0.2;

    /// <summary>Mean absolute difference (0..1) a local maximum must exceed.</summary>
    public const double Threshold = 0.14;

    /// <summary>Maxima closer than this are one transition; the larger one is kept.</summary>
    public const double MergeSec = 0.6;

    /// <summary>A soft boundary this close to a hard one (or to another accepted one) is dropped; the hard one wins.</summary>
    public const double HardDedupeSec = 0.3;

    /// <summary>
    /// Bound on the thumbnail file, checked before it is read: about 21,000 frames, roughly 12 minutes at
    /// 30 fps. A longer source degrades to its scene-score shots (this pass never fails a step it can only improve).
    /// </summary>
    internal const int MaxThumbBytes = 64 * 1024 * 1024;

    /// <summary>The sampling rate never exceeds this (a source tagged 1000 fps would otherwise explode the frame count).</summary>
    public const int MaxSamplingFps = 120;

    /// <summary>The sampling rate never goes below this.</summary>
    public const int MinSamplingFps = 1;

    /// <summary>The source rate clamped to [<see cref="MinSamplingFps"/>, <see cref="MaxSamplingFps"/>] as a ratio; a non-positive rate is 25.</summary>
    public static (int Num, int Den) SamplingFps(int num, int den)
    {
        if (num <= 0 || den <= 0)
            return (25, 1);
        double fps = num / (double)den;
        if (fps > MaxSamplingFps)
            return (MaxSamplingFps, 1);
        return fps < MinSamplingFps ? (MinSamplingFps, 1) : (num, den);
    }

    /// <summary>Frames the thumbnail file can hold within <see cref="MaxThumbBytes"/>.</summary>
    public const int MaxFrames = MaxThumbBytes / FrameBytes;

    /// <summary>The frame cap for a source of this duration at this rate: its own length plus slack, never above <see cref="MaxFrames"/>.</summary>
    public static int FrameCap(double totalDurationSec, double fps) =>
        (int)Math.Clamp(Math.Ceiling(Math.Max(0, totalDurationSec) * fps) + 2, 1, MaxFrames);

    private static readonly string[] BaseFlags = { "-nostdin", "-hide_banner", "-y" };
    private static readonly string[] ProtocolWhitelist = { "-protocol_whitelist", "file" };

    /// <summary>
    /// Argv of the single decode pass: video only, constant frame rate at the source's own rate, one
    /// 24x42 rgb24 thumbnail per frame written raw to <paramref name="outputPath"/>, no network protocols.
    /// </summary>
    public static string[] BuildArgs(string inputPath, int threads, int fpsNum, int fpsDen, string outputPath, int maxFrames = MaxFrames)
    {
        if (fpsNum <= 0 || fpsDen <= 0)
            throw new ArgumentOutOfRangeException(nameof(fpsNum), "a positive frame rate is required");

        List<string> args = new(BaseFlags) { "-loglevel", "error" };
        args.AddRange(ProtocolWhitelist);
        args.Add("-threads");
        args.Add(FfmpegArgvFormat.Number(threads));
        args.Add("-i");
        args.Add(inputPath);
        args.Add("-an");
        args.Add("-vf");
        args.Add($"fps={FfmpegArgvFormat.Number(fpsNum)}/{FfmpegArgvFormat.Number(fpsDen)}," +
                 $"scale={ThumbWidth}:{ThumbHeight}:flags=area,format=rgb24");
        args.Add("-frames:v");
        args.Add(FfmpegArgvFormat.Number(Math.Clamp(maxFrames, 1, MaxFrames)));
        args.Add("-fs");
        args.Add(FfmpegArgvFormat.Number((long)MaxThumbBytes));
        args.Add("-f");
        args.Add("rawvideo");
        args.Add(outputPath);
        return args.ToArray();
    }

    /// <summary>
    /// The soft transitions in <paramref name="frames"/> (consecutive <see cref="FrameBytes"/>-byte thumbnails at
    /// <paramref name="fps"/>), ascending. Score at frame i is the mean absolute difference (/255) between
    /// frames i-lag and i+lag; a boundary is a local maximum over +-lag frames above <see cref="Threshold"/>
    /// (a plateau is centred), maxima within <see cref="MergeSec"/> keep the largest.
    /// </summary>
    public static IReadOnlyList<SoftBoundary> FindBoundaries(ReadOnlySpan<byte> frames, double fps)
    {
        int count = frames.Length / FrameBytes;
        int lag = Math.Max(1, (int)Math.Round(LagSec / 2 * fps));
        if (fps <= 0 || count < 2 * lag + 1)
            return [];

        long[] score = new long[count];
        for (int i = lag; i < count - lag; i++)
        {
            ReadOnlySpan<byte> a = frames.Slice((i - lag) * FrameBytes, FrameBytes);
            ReadOnlySpan<byte> b = frames.Slice((i + lag) * FrameBytes, FrameBytes);
            long sum = 0;
            for (int k = 0; k < FrameBytes; k++)
                sum += Math.Abs(a[k] - b[k]);
            score[i] = sum;
        }

        double limit = Threshold * 255.0 * FrameBytes;
        List<SoftBoundary> peaks = [];
        int index = lag;
        while (index < count - lag)
        {
            if (score[index] <= limit || !IsWindowMax(score, index, lag, count))
            {
                index++;
                continue;
            }

            int runEnd = index;
            while (runEnd + 1 < count - lag && score[runEnd + 1] == score[index] && IsWindowMax(score, runEnd + 1, lag, count))
                runEnd++;

            int centre = (index + runEnd + 1) / 2;
            peaks.Add(new SoftBoundary(centre / fps, score[index] / (255.0 * FrameBytes)));
            index = runEnd + 1;
        }

        List<SoftBoundary> merged = [];
        foreach (SoftBoundary peak in peaks)
        {
            if (merged.Count > 0 && peak.TimeSec - merged[^1].TimeSec < MergeSec)
            {
                if (peak.Score > merged[^1].Score)
                    merged[^1] = peak;
                continue;
            }

            merged.Add(peak);
        }

        return merged;
    }

    private static bool IsWindowMax(long[] score, int i, int lag, int count)
    {
        for (int j = Math.Max(lag, i - lag); j <= Math.Min(count - lag - 1, i + lag); j++)
        {
            if (score[j] > score[i])
                return false;
        }

        return true;
    }

    /// <summary>
    /// The scene-score spans plus every soft boundary that is at least <paramref name="minShotSec"/> (and
    /// <see cref="HardDedupeSec"/>) from the file edges and from every accepted boundary. Scene boundaries are
    /// never dropped, and they are accepted first, so a hard cut always wins over a nearby soft one.
    /// </summary>
    public static SceneDetectionResult Merge(
        IReadOnlyList<(double StartSec, double EndSec)> sceneSpans,
        IReadOnlyList<SoftBoundary> soft,
        double totalDurationSec,
        double minShotSec)
    {
        if (soft.Count == 0)
            return new SceneDetectionResult(sceneSpans, []);

        double gap = Math.Max(HardDedupeSec, minShotSec);
        List<double> boundaries = sceneSpans.Skip(1).Select(s => s.StartSec).ToList();
        List<double> accepted = [];
        foreach (SoftBoundary candidate in soft.OrderBy(c => c.TimeSec))
        {
            double t = candidate.TimeSec;
            if (t < minShotSec || t > totalDurationSec - minShotSec)
                continue;
            if (boundaries.Any(b => Math.Abs(b - t) < gap))
                continue;
            boundaries.Add(t);
            accepted.Add(t);
        }

        if (accepted.Count == 0)
            return new SceneDetectionResult(sceneSpans, []);

        return new SceneDetectionResult(
            ShowinfoOutputParser.BuildShotSpans(boundaries.OrderBy(b => b).ToList(), totalDurationSec),
            accepted);
    }
}
