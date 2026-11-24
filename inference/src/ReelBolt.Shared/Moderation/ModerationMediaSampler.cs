using System.Diagnostics;
using System.Globalization;

namespace ReelBolt.Shared.Moderation;

/// <summary>The outcome of one external process run.</summary>
public sealed record ProcessRunResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;

    /// <summary>
    /// True when the failure says nothing about the bytes, so the same file may succeed on a retry: a timeout, a kill by
    /// a signal (exit 129-159, which is how .NET reports 128 + signal, OOM-kill SIGKILL being 137) or ffmpeg's own
    /// out-of-memory exit (AVERROR(ENOMEM) = 244). A clean ffmpeg error (1, 234, 255 and the AVERROR tags 166-191) is
    /// not transient: the same bytes fail the same way. A fault of the host (a full disk, a quota) is transient too: it is
    /// not about the file, so it is never stored as final.
    /// </summary>
    public bool Transient =>
        TimedOut || ExitCode < 0 || ExitCode is >= 129 and <= 159 || ExitCode == 244
        || (!Succeeded && OwnErrorLines.Any(l => l.Contains("No space left on device", StringComparison.OrdinalIgnoreCase)
                                                 || l.Contains("Disk quota exceeded", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The lines of stderr that are ffmpeg's own. File metadata (a title, a comment) is printed indented, so a line that
    /// starts with white space is the file's text, not the tool's, and never decides whether a failure is final.
    /// </summary>
    private IEnumerable<string> OwnErrorLines =>
        (StdErr ?? string.Empty).Split('\n').Where(l => l.Length > 0 && !char.IsWhiteSpace(l[0]));

    /// <summary>A failure that is final for these bytes.</summary>
    public bool Permanent => !Succeeded && !Transient;
}

/// <summary>
/// Runs an external executable with a bounded timeout. Extracted so the media sampler's ffmpeg
/// argv can be tested against a fake without requiring ffmpeg (or any process) in unit tests.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct);

    /// <summary>
    /// The same run with stdout handed over one line at a time and not kept, so a very long packet list is never held as
    /// one string. The default reads the whole output and then splits it (a runner that cannot stream still works).
    /// </summary>
    async Task<ProcessRunResult> RunStreamingAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        Action<string> onStdoutLine,
        CancellationToken ct)
    {
        ProcessRunResult result = await RunAsync(fileName, arguments, timeout, ct).ConfigureAwait(false);
        foreach (string line in (result.StdOut ?? string.Empty).Split('\n'))
            onStdoutLine(line);
        return result with { StdOut = string.Empty };
    }
}

/// <summary>The production runner: <see cref="ProcessStartInfo.ArgumentList"/>, no shell, no stdin.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct) => RunCoreAsync(fileName, arguments, timeout, null, ct);

    public Task<ProcessRunResult> RunStreamingAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        Action<string> onStdoutLine,
        CancellationToken ct) => RunCoreAsync(fileName, arguments, timeout, onStdoutLine, ct);

    private static async Task<string> ReadLinesAsync(StreamReader reader, Action<string> onLine)
    {
        while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            onLine(line);
        return string.Empty;
    }

    private static async Task<ProcessRunResult> RunCoreAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        Action<string>? onStdoutLine,
        CancellationToken ct)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (string argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        process.StandardInput.Close();

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);

        // Start both readers before waiting: a child that fills a pipe buffer would otherwise block
        // forever on write while we wait for it to exit.
        Task<string> stdout = onStdoutLine is null
            ? process.StandardOutput.ReadToEndAsync(CancellationToken.None)
            : ReadLinesAsync(process.StandardOutput, onStdoutLine);
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return new ProcessRunResult(process.ExitCode, await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false), TimedOut: false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProcessRunResult(-1, string.Empty, "timed out", TimedOut: true);
        }
        finally
        {
            // Kill the tree on timeout AND on caller cancellation: a cancelled upload/step must not
            // leave ffmpeg decoding an attacker-sized file after the request is gone. Then observe
            // the readers so no task is left unobserved.
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process exited between the check and the kill: nothing to clean up.
                }
            }

            await ObserveAsync(stdout).ConfigureAwait(false);
            await ObserveAsync(stderr).ConfigureAwait(false);
        }
    }

    private static async Task ObserveAsync(Task<string> reader)
    {
        try
        {
            await reader.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The process was killed; the reader's outcome does not change the result.
        }
    }
}

/// <summary>
/// One sampled piece of media: downscaled JPEG frames and/or a 16 kHz mono WAV. Never the
/// original file. <see cref="Failure"/> says why a pass could not run, for the gate's logs.
/// </summary>
public sealed record ModerationMediaSample(
    IReadOnlyList<byte[]> Images,
    byte[]? Audio,
    ModerationContentKind Kind,
    string? Failure,
    bool Deterministic = false,
    string? SubtitleText = null,
    byte[]? SideAudio = null,
    bool TooLong = false,
    IReadOnlyList<byte[]>? ChannelAudio = null)
{
    /// <summary>
    /// Joins what two transcriptions of the same audio heard (the mono downmix and the side signal of stereo). Null only
    /// when neither exists; an empty result stays empty (nothing was said in either), so "wordless" needs both empty.
    /// </summary>
    public static string? JoinTranscripts(string? main, string? side) =>
        side is null ? main
            : main is null ? side
            : string.IsNullOrWhiteSpace(side) ? main
            : string.IsNullOrWhiteSpace(main) ? side
            : main + "\n" + side;

    /// <summary>The audio beyond the main one that is also transcribed: stereo's side signal and each audible channel of 3 to 8.</summary>
    public static IEnumerable<byte[]> ExtraAudioOf(ModerationMediaSample sample)
    {
        if (sample.SideAudio is { Length: > 0 }) yield return sample.SideAudio;
        foreach (byte[] channel in sample.ChannelAudio ?? [])
            if (channel.Length > 0) yield return channel;
    }

    /// <summary>
    /// Joins the transcript and the text of the file's subtitle streams, the one text the model reads for the file.
    /// Null when there is neither.
    /// </summary>
    public static string? WithSubtitles(string? transcript, string? subtitleText) =>
        string.IsNullOrWhiteSpace(subtitleText)
            ? transcript
            : string.IsNullOrWhiteSpace(transcript) ? subtitleText : transcript + "\n" + subtitleText;

    /// <summary>Bytes per second of the sampler's WAV (16 kHz, mono, 16-bit) and the size of its header.</summary>
    public const int WavBytesPerSecond = 32000;

    /// <summary>The header of the WAV that ffmpeg writes to a seekable file.</summary>
    public const int WavHeaderBytes = 44;

    /// <summary>
    /// The length of the extracted audio, measured from the decoded samples (never the declared length). Null when
    /// there is no audio. The extraction stops at <c>MaxAudioSeconds</c>, so a value at that limit means the track
    /// went on past what was heard.
    /// </summary>
    public double? AudioSeconds => Audio is null ? null : Math.Max(0, Audio.Length - WavHeaderBytes) / (double)WavBytesPerSecond;
}

/// <summary>Turns an uploaded image/video/audio into the small inputs the moderation model accepts.</summary>
public interface IModerationMediaSampler
{
    Task<ModerationMediaSample> SampleAsync(Stream media, string mimeType, CancellationToken ct);
}

/// <summary>
/// ffmpeg-backed sampler for content moderation. Images become one downscaled JPEG; videos become
/// one frame every <see cref="ContentModerationOptions.VideoFrameIntervalSeconds"/> (at most
/// <see cref="ContentModerationOptions.VideoMaxFrames"/>) plus a 16 kHz mono WAV; audio becomes the
/// WAV only. The temp directory is always deleted, on success or failure.
/// </summary>
/// <remarks>
/// <para>
/// Every invocation carries <c>-protocol_whitelist file -format_whitelist ...</c> copied from the
/// engine's <c>MediaDerivativeInputFormats</c>: uploads are untrusted, and a crafted HLS/concat
/// manifest opened through the <c>file</c> protocol must not reach other local files.
/// </para>
/// <para>
/// The model accepts text and images only, so a video is represented by frames plus its transcript
/// (done by the caller) and audio by its transcript; the WAV here is the input to that step, never
/// sent to the moderation API.
/// </para>
/// </remarks>
public sealed class FfmpegModerationMediaSampler : IModerationMediaSampler
{
    /// <summary>
    /// Same container demuxers as the engine's <c>MediaDerivativeInputFormats.Whitelist</c>, plus
    /// the still-image demuxers this sampler's IMAGE arm needs. Without them ffmpeg refuses a
    /// <c>.jpg</c>/<c>.png</c>/<c>.webp</c>/<c>.gif</c> input under <c>-format_whitelist</c> and the
    /// image pass silently produced no sample.
    /// </summary>
    public const string MediaInputFormats =
        "mov,matroska,mp3,wav,aac,ac3,flac,ogg,avi,mpegts,mpeg,flv,asf,aiff,caf,w64,mxf," +
        "jpeg_pipe,png_pipe,gif,webp_pipe,bmp_pipe,apng,avif";

    /// <summary>
    /// The content type the output lane passes for a stored output whose name tells nothing (no extension, <c>.bin</c> or
    /// any other the engine does not know): the file's own streams decide whether it is an image, a video or audio, never
    /// its name. Ingest never passes it.
    /// </summary>
    public const string FollowProbeMimeType = "application/x-reelbolt-follow-probe";

    /// <summary>Audio sampled for transcription never runs past this (a 10-minute source).</summary>
    private const int MaxAudioSeconds = 600;

    private const string FfmpegExecutable = "ffmpeg";
    private const string FfprobeExecutable = "ffprobe";
    private const string SampleLongSideDefault = "512";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// One total deadline over every pass for one output, so that one crafted file cannot hold the lane for the sum of
    /// the per-pass limits (about 7 minutes: 60 s passes for the probe, the declared length and the audio, 20 frames at
    /// 20 s, covers and subtitles). Measured end to end on a real 2 h, 30 fps file with a 10-minute audio listen
    /// (docs/content-moderation.md): 11 s, of which the packet list is 1.4 s. 120 s is over 10 times the real file.
    /// Past it every later pass is refused as a timeout, which is a non-deterministic failure the caller retries a
    /// bounded number of times.
    /// </summary>
    internal static readonly TimeSpan TotalDeadline = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Most packets listed for one stream (video or audio) of one file; a stream with more is refused as too long. Over the
    /// 12-hour limit with room: AAC at 48 kHz is about 2.03 million packets in 12 hours, Opus at 20 ms about 2.16 million,
    /// video at 60 fps about 2.6 million, at 120 fps about 5.2 million. The lists are read line by line and an audio list
    /// is not kept (three numbers), so the cap does not decide the memory.
    /// </summary>
    internal const int MaxPackets = 6_000_000;

    /// <summary>
    /// Threads one ffmpeg decode may use on a sampling pass. One crafted file used every core of the host; two keep
    /// the passes within the measured times (docs/content-moderation.md) while leaving the other cores to the rest of
    /// the lane.
    /// </summary>
    internal const string SamplingThreads = "2";

    /// <summary>
    /// The most pre-roll (packets before the first shown time) that is left out of the draw. An honest stream-copied cut starts at
    /// the keyframe before its first shown frame, one group of pictures (2 to 10 s typically, up to about 20 s for long-GOP
    /// archive or broadcast footage); 60 s leaves a wide margin and bounds what an uploader-set start time can hide.
    /// </summary>
    internal const double MaxPreRollSeconds = 60;

    /// <summary>
    /// How many leading packets are pre-roll: those before the container's start time, but no further than
    /// <see cref="MaxPreRollSeconds"/> past the first packet. Packets beyond the cap stay drawable.
    /// </summary>
    internal static int PreRollPackets(IReadOnlyList<double> times, double startSeconds)
    {
        if (times.Count == 0) return 0;
        double end = Math.Min(startSeconds, times[0] + MaxPreRollSeconds);
        return times.Count(t => t < end - 0.0005);
    }

    /// <summary>The most subtitle text read for one file (a full film's subtitles are about 150 KB); more is refused.</summary>
    internal const int MaxSubtitleBytes = 1_048_576;

    /// <summary>Most text subtitle streams read for one file.</summary>
    internal const int MaxSubtitleStreams = 8;

    /// <summary>
    /// For two-channel audio the side signal (left minus right) is also extracted and transcribed when it is louder than
    /// this overall. The mono downmix the transcription hears cancels it, so speech recorded in opposite phase, under a
    /// bed or in one part of a file, would not be heard otherwise. Quieter than this is silence.
    /// </summary>
    internal const double SideAudibleDb = -50;

    private static readonly string[] SeekUnsafeDemuxers = ["mpegts", "mpeg"];

    private static readonly string[] TextSubtitleCodecs =
    [
        "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text", "subviewer", "subviewer1", "microdvd", "mpl2",
        "jacosub", "realtext", "sami", "stl", "vplayer", "pjs", "ttml", "eia_608"
    ];

    private readonly IProcessRunner _runner;
    private readonly ContentModerationOptions _options;
    private readonly Random _random;
    private readonly TimeSpan _deadline;
    private readonly int _maxPackets;
    private readonly AsyncLocal<SamplingClock?> _clock = new();

    public FfmpegModerationMediaSampler(IProcessRunner runner, ContentModerationOptions options)
        : this(runner, options, Random.Shared, TotalDeadline)
    {
    }

    /// <summary>Tests inject the random source (a seeded <see cref="Random"/>) and the deadline.</summary>
    internal FfmpegModerationMediaSampler(
        IProcessRunner runner, ContentModerationOptions options, Random random, TimeSpan deadline, int maxPackets = MaxPackets)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _deadline = deadline;
        _maxPackets = maxPackets;
    }

    /// <summary>The time left of one output's total deadline.</summary>
    private sealed class SamplingClock(TimeSpan limit)
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();

        public bool Expired { get; set; }

        public TimeSpan Remaining => limit - _watch.Elapsed;
    }

    /// <summary>Every process of one output runs through here, so the total deadline covers all of its passes.</summary>
    private async Task<ProcessRunResult> RunAsync(
        string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct, Action<string>? onStdoutLine = null)
    {
        SamplingClock? clock = _clock.Value;
        if (clock is null)
            return await RunUnboundedAsync(fileName, arguments, timeout, onStdoutLine, ct).ConfigureAwait(false);

        TimeSpan remaining = clock.Remaining;
        if (remaining <= TimeSpan.Zero)
        {
            clock.Expired = true;
            return new ProcessRunResult(-1, string.Empty, "total time bound exceeded", TimedOut: true);
        }

        ProcessRunResult result = await RunUnboundedAsync(
            fileName, arguments, remaining < timeout ? remaining : timeout, onStdoutLine, ct).ConfigureAwait(false);
        if (result.TimedOut && clock.Remaining <= TimeSpan.Zero)
            clock.Expired = true;
        return result;
    }

    private Task<ProcessRunResult> RunUnboundedAsync(
        string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string>? onStdoutLine, CancellationToken ct) =>
        onStdoutLine is null
            ? _runner.RunAsync(fileName, arguments, timeout, ct)
            : _runner.RunStreamingAsync(fileName, arguments, timeout, onStdoutLine, ct);

    public async Task<ModerationMediaSample> SampleAsync(Stream media, string mimeType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(media);

        SamplingClock clock = new(_deadline);
        _clock.Value = clock;
        try
        {
            ModerationMediaSample sample = await SampleWithinDeadlineAsync(media, mimeType, ct).ConfigureAwait(false);
            return clock.Expired && sample.Failure is null
                ? sample with { Failure = "sampling exceeded its total time bound", Deterministic = false }
                : sample;
        }
        finally
        {
            _clock.Value = null;
        }
    }

    private async Task<ModerationMediaSample> SampleWithinDeadlineAsync(Stream media, string mimeType, CancellationToken ct)
    {
        bool followProbe = string.Equals(mimeType?.Trim(), FollowProbeMimeType, StringComparison.OrdinalIgnoreCase);
        (ModerationContentKind? kind, string extension) = followProbe ? (ModerationContentKind.Video, ".bin") : Classify(mimeType!);
        if (kind is null)
        {
            return new ModerationMediaSample([], null, ModerationContentKind.Text,
                $"Unsupported moderation media type '{mimeType}'.", Deterministic: true);
        }

        string workDirectory = Path.Combine(Path.GetTempPath(), "reelbolt-moderation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);

        try
        {
            string inputPath = Path.Combine(workDirectory, "source" + extension);
            await using (FileStream file = File.Create(inputPath))
            {
                await media.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            // The streams decide how the file is sampled, not its name or MIME type: one rule (DecideLayout)
            // either identifies the layout positively or refuses it.
            MediaProbe probe = await ProbeAsync(inputPath, ct).ConfigureAwait(false);
            MediaLayout layout = DecideLayout(probe);

            // A declared video or image that the streams say is audio could hide a video stream: refuse it.
            // (Declared audio holding video is still sampled as video.)
            if (followProbe)
            {
                kind = layout.Kind switch
                {
                    LayoutKind.Image => ModerationContentKind.Image,
                    LayoutKind.Audio => ModerationContentKind.Audio,
                    _ => ModerationContentKind.Video
                };
            }
            else if (layout.Kind == LayoutKind.Audio && kind is ModerationContentKind.Video or ModerationContentKind.Image)
                layout = new MediaLayout(LayoutKind.Refused, -1, [], "the declared type hides the file's streams", true);

            switch (layout.Kind)
            {
                case LayoutKind.Image:
                    {
                        // Decoded, not stream-copied: an animated WebP or APNG can read as one packet.
                        (MediaMeasure? imageMeasure, bool imageTransient) =
                            await MeasureAsync(inputPath, "0:v:0", decode: true, ct).ConfigureAwait(false);
                        if (imageMeasure is null)
                        {
                            return new ModerationMediaSample([], null, ModerationContentKind.Image,
                                "the image could not be measured", Deterministic: !imageTransient);
                        }

                        return imageMeasure.Frames > 1
                            ? await SampleAnimatedImageAsync(inputPath, workDirectory, imageMeasure, ct).ConfigureAwait(false)
                            : await SampleImageAsync(inputPath, workDirectory, ct).ConfigureAwait(false);
                    }

                case LayoutKind.Video:
                    {
                        PacketList packets = await ListPacketsAsync(inputPath, layout.VideoIndex, ct).ConfigureAwait(false);
                        return await SampleVideoAsync(inputPath, workDirectory, probe, layout, packets, ct).ConfigureAwait(false);
                    }

                case LayoutKind.Audio:
                    return await SampleAudioAsync(inputPath, workDirectory, probe, layout, ct).ConfigureAwait(false);

                default:
                    return new ModerationMediaSample([], null, kind.Value, layout.Refusal, layout.Deterministic);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ModerationMediaSample([], null, kind.Value, ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(workDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A best-effort cleanup must never hide the sampling result.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The exact argv for one downscaled JPEG (also used by the tests).</summary>
    internal static string[] BuildImageArgs(string inputPath, string outputPath, int longSide)
    {
        int cap = longSide > 0 ? longSide : 512;
        return
        [
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaInputFormats,
            "-threads", SamplingThreads, "-i", inputPath,
            "-vf", $"scale='min({cap.ToString(CultureInfo.InvariantCulture)},iw)':-2:flags=lanczos",
            "-frames:v", "1",
            "-q:v", "3",
            outputPath
        ];
    }

    /// <summary>
    /// The exact argv for ONE frame at <paramref name="seconds"/> (also used by the tests): an accurate seek, so the frame
    /// returned is the first at or after the time, never the keyframe before it (<c>-noaccurate_seek</c> would hand a
    /// crafted file's keyframes to the check in place of the frame chosen). The cost grows with the distance from the
    /// previous keyframe and is bounded by the per-frame timeout and the total deadline.
    /// </summary>
    internal static string[] BuildFrameAtArgs(string inputPath, string outputPath, double seconds, int longSide, int videoIndex = 0)
    {
        int cap = longSide > 0 ? longSide : 512;
        return
        [
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaInputFormats,
            "-ss", Math.Max(0, seconds).ToString("0.######", CultureInfo.InvariantCulture),
            // The frame keeps the time it has in the container, and the filter prints it on stdout (ffmpeg's own log
            // stays at "error", so no file text is mixed into stderr): the check that this is the frame drawn.
            "-copyts", "-threads", SamplingThreads, "-i", inputPath,
            "-map", $"0:{videoIndex.ToString(CultureInfo.InvariantCulture)}",
            "-an", "-sn", "-dn",
            "-vf", $"scale='min({cap.ToString(CultureInfo.InvariantCulture)},iw)':-2:flags=lanczos,"
                   + "metadata=mode=add:key=rb:value=1,metadata=mode=print:key=rb:file=-",
            "-frames:v", "1",
            // No frame duplication or drop: after a long hold the next packet is hours away, and the default constant-rate
            // sync then writes the frame AFTER the one drawn (the hold itself is the first to print its time).
            "-fps_mode", "passthrough",
            "-q:v", "3",
            outputPath
        ];
    }

    /// <summary>
    /// The exact argv for ONE frame decoded from the START of the file, ignoring its index (the slow path): no seek, so a
    /// keyframe table that lies, an open GOP and an AVI's made-up first times cannot move it. Frames are dropped until the
    /// first one whose time is within the tolerance of <paramref name="seconds"/> in the container's own clock
    /// (<c>-copyts</c>), whatever order the file's frames come in, and that frame is written and its time printed. When no
    /// frame has that time nothing is written. With no <paramref name="seconds"/> it is the first frame the file decodes.
    /// </summary>
    internal static string[] BuildFrameFromStartArgs(string inputPath, string outputPath, double? seconds, int longSide, int videoIndex = 0)
    {
        int cap = longSide > 0 ? longSide : 512;
        string select = seconds is double at
            ? $"select='between(t,{(at - SlowSelectToleranceSeconds).ToString("0.######", CultureInfo.InvariantCulture)},{(at + SlowSelectToleranceSeconds).ToString("0.######", CultureInfo.InvariantCulture)})',"
            : string.Empty;
        return
        [
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaInputFormats,
            "-copyts", "-threads", SamplingThreads, "-i", inputPath,
            "-map", $"0:{videoIndex.ToString(CultureInfo.InvariantCulture)}",
            "-an", "-sn", "-dn",
            "-vf", select + $"scale='min({cap.ToString(CultureInfo.InvariantCulture)},iw)':-2:flags=lanczos,"
                   + "metadata=mode=add:key=rb:value=1,metadata=mode=print:key=rb:file=-",
            "-frames:v", "1",
            "-fps_mode", "passthrough",
            "-q:v", "3",
            outputPath
        ];
    }

    /// <summary>
    /// The exact argv for evenly spaced decoded frames of an animated image (also used by the tests): the frames
    /// at the given zero-based indexes, each written as its own JPEG.
    /// </summary>
    internal static string[] BuildAnimatedFramesArgs(string inputPath, string outputPattern, IReadOnlyList<long> indexes, int longSide)
    {
        int cap = longSide > 0 ? longSide : 512;
        string select = string.Join("+", indexes.Select(i => $"eq(n,{i.ToString(CultureInfo.InvariantCulture)})"));
        return
        [
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaInputFormats,
            "-threads", SamplingThreads, "-i", inputPath,
            "-map", "0:v:0",
            "-vf", $"select='{select}',scale='min({cap.ToString(CultureInfo.InvariantCulture)},iw)':-2:flags=lanczos",
            "-fps_mode", "passthrough",
            "-frames:v", indexes.Count.ToString(CultureInfo.InvariantCulture),
            "-q:v", "3",
            outputPattern
        ];
    }

    /// <summary>The exact argv for ONE attached picture, by its own stream index (also used by the tests).</summary>
    internal static string[] BuildCoverArgs(string inputPath, string outputPath, int longSide, int streamIndex = 0)
    {
        int cap = longSide > 0 ? longSide : 512;
        return
        [
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaInputFormats,
            "-threads", SamplingThreads, "-i", inputPath,
            "-map", $"0:{streamIndex.ToString(CultureInfo.InvariantCulture)}",
            "-an", "-sn", "-dn",
            "-vf", $"scale='min({cap.ToString(CultureInfo.InvariantCulture)},iw)':-2:flags=lanczos",
            "-frames:v", "1",
            "-q:v", "3",
            outputPath
        ];
    }

    /// <summary>
    /// The most audio channels judged (a 7.1 file has 8). More is a final refusal: every channel is extracted and heard on its
    /// own, and the work grows with the count.
    /// </summary>
    internal const int MaxAudioChannels = 8;

    /// <summary>
    /// The mix of two channels into one, each at 1/2 so it cannot clip: the sum (<paramref name="alternating"/> false) or
    /// left minus right, which keeps what the sum cancels: speech recorded in opposite phase.
    /// </summary>
    internal static string StereoMixFilter(bool alternating) =>
        alternating ? "pan=1c|c0=0.5*c0-0.5*c1" : "pan=1c|c0=0.5*c0+0.5*c1";

    /// <summary>All the channels averaged into one (each at 1/n so it cannot clip): the one full mix, so speech spread across channels in time is heard whole.</summary>
    internal static string FullMixFilter(int channels) =>
        "pan=mono|c0=" + string.Join('+', Enumerable.Range(0, channels).Select(i =>
            $"{(1.0 / channels).ToString("0.#######", CultureInfo.InvariantCulture)}*c{i.ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>One channel of the decoded stream, by its own index (no layout name is read, none is trusted).</summary>
    internal static string ChannelFilter(int index) => $"pan=mono|c0=c{index.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The exact argv for the 16 kHz mono WAV (also used by the tests). Two channels are mixed with their sum; more than two
    /// give the full mix of every channel here (each channel is measured and extracted on its own, see <see cref="BuildChannelArgs"/>);
    /// mono is written as it is.
    /// </summary>
    internal static string[] BuildAudioArgs(string inputPath, string outputPath, int maxSeconds = MaxAudioSeconds, int channels = 1) =>
    [
        "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
        "-protocol_whitelist", "file",
        "-format_whitelist", MediaInputFormats,
        "-threads", SamplingThreads, "-i", inputPath,
        "-vn", .. (channels > 2 ? new[] { "-af", FullMixFilter(channels) } : channels == 2 ? ["-af", StereoMixFilter(false)] : []),
        "-ac", "1", "-ar", "16000", "-acodec", "pcm_s16le",
        // Only the transcript is needed; the duration bound keeps an attacker-sized source from
        // producing a multi-gigabyte WAV (the byte cap is the second line of defence).
        "-t", Math.Max(1, maxSeconds).ToString(CultureInfo.InvariantCulture),
        outputPath
    ];

    /// <summary>The exact argv for the 16 kHz mono WAV of one channel of more-than-two-channel audio, by index (also used by the tests).</summary>
    internal static string[] BuildChannelArgs(string inputPath, string outputPath, int channel, int maxSeconds = MaxAudioSeconds) =>
    [
        "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
        "-protocol_whitelist", "file",
        "-format_whitelist", MediaInputFormats,
        "-threads", SamplingThreads, "-i", inputPath,
        "-vn", "-sn", "-dn",
        "-af", ChannelFilter(channel),
        "-ar", "16000", "-acodec", "pcm_s16le",
        "-t", Math.Max(1, maxSeconds).ToString(CultureInfo.InvariantCulture),
        outputPath
    ];

    /// <summary>
    /// The exact argv that prints the loudest RMS over any 1-second window of every channel, in one pass
    /// (<c>lavfi.astats.N.RMS_peak</c>, N from 1; per-channel measures only, so the output stays small).
    /// </summary>
    internal static string[] BuildChannelMeasureArgs(string inputPath, int maxSeconds) =>
    [
        "-nostdin", "-hide_banner", "-loglevel", "error",
        "-protocol_whitelist", "file",
        "-format_whitelist", MediaInputFormats,
        "-threads", SamplingThreads, "-i", inputPath,
        "-vn", "-sn", "-dn",
        "-t", Math.Max(1, maxSeconds).ToString(CultureInfo.InvariantCulture),
        "-af", "aresample=16000,asetnsamples=n=65536:p=0,astats=length=1:metadata=1:reset=0:measure_perchannel=RMS_peak:measure_overall=none,"
               + "ametadata=mode=print:file=-",
        "-f", "null", "-"
    ];

    /// <summary>The last (overall) loudest-second level in dB of each channel, by index; null for a channel with no reading. -inf is -120 dB.</summary>
    internal static double?[] ParseChannelPeaks(string output, int channels) => ParseChannelPeaks(output, channels, out _);

    /// <summary>
    /// As above; <paramref name="extraChannel"/> is true when a reading names a channel above the probed count (channels added
    /// later in the stream), which the caller treats as a final failure.
    /// </summary>
    internal static double?[] ParseChannelPeaks(string output, int channels, out bool extraChannel)
    {
        extraChannel = false;
        double?[] peaks = new double?[channels];
        const string prefix = "lavfi.astats.";
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith(prefix, StringComparison.Ordinal) || !line.Contains(".RMS_peak=", StringComparison.Ordinal)) continue;

            string number = line[prefix.Length..line.IndexOf('.', prefix.Length)];
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int channel) || channel < 1) continue;
            if (channel > channels)
            {
                extraChannel = true;
                continue;
            }

            string value = line[(line.IndexOf('=') + 1)..];
            double? db = value is "-inf" ? -120
                : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? Math.Max(v, -120) : null;
            // The loudest reading of the channel stands across a layout reset (a later graph restarts its meter).
            if (db is not null) peaks[channel - 1] = peaks[channel - 1] is double old ? Math.Max(old, db.Value) : db;
        }

        return peaks;
    }

    private async Task<ModerationMediaSample> SampleImageAsync(
        string inputPath, string workDirectory, CancellationToken ct)
    {
        string outputPath = Path.Combine(workDirectory, "image.jpg");
        ProcessRunResult result = await RunAsync(
            FfmpegExecutable,
            BuildImageArgs(inputPath, outputPath, _options.EffectiveSampleImageMaxLongSide),
            ProcessTimeout,
            ct).ConfigureAwait(false);

        if (!result.Succeeded || !File.Exists(outputPath))
            return new ModerationMediaSample([], null, ModerationContentKind.Image, DescribeFailure("image downscale", result));

        byte[]? image = await ReadBoundedAsync(outputPath, _options.MaxImageBytes, ct).ConfigureAwait(false);
        return image is null
            ? new ModerationMediaSample([], null, ModerationContentKind.Image,
                $"image downscale produced more than {_options.MaxImageBytes} bytes")
            : new ModerationMediaSample([image], null, ModerationContentKind.Image, null);
    }

    /// <summary>What <c>ffmpeg -i</c> reports about a file's streams. <see cref="Parsed"/> is false when nothing could be read.</summary>
    internal sealed record MediaProbe(
        double? DurationSeconds, int VideoStreams, int AudioStreams, bool Parsed, int PictureStreams = 0, bool TimedOut = false,
        IReadOnlyList<int>? RealVideoIndexes = null, IReadOnlyList<int>? PictureStreamIndexes = null, string? Demuxer = null,
        int InvalidPictures = 0, double StartSeconds = 0, int AudioChannels = 0, double? AudioStreamSeconds = null,
        IReadOnlyList<int>? TextSubtitleStreamIndexes = null, int OtherSubtitles = 0, string? AudioChannelLayout = null)
    {
        public IReadOnlyList<int> TextSubtitleIndexes => TextSubtitleStreamIndexes ?? [];

        public IReadOnlyList<int> VideoIndexes => RealVideoIndexes ?? [];

        public IReadOnlyList<int> PictureIndexes => PictureStreamIndexes ?? [];
    }

    internal enum LayoutKind { Refused, Image, Video, Audio }

    /// <summary>
    /// The one decision about how a file is sampled. <see cref="Refusal"/> set on Image/Video/Audio means "sample what
    /// can be sampled, then fail"; on <see cref="LayoutKind.Refused"/> nothing is sampled.
    /// </summary>
    internal sealed record MediaLayout(
        LayoutKind Kind, int VideoIndex, IReadOnlyList<int> Pictures, string? Refusal, bool Deterministic,
        IReadOnlyList<int>? Subtitles = null)
    {
        public IReadOnlyList<int> SubtitleIndexes => Subtitles ?? [];
    }

    /// <summary>Most attached pictures judged for one file; more than this fails.</summary>
    internal const int MaxAttachedPictures = 4;

    private static readonly string[] ImageDemuxers = ["image2", "gif", "apng", "webp_pipe", "png_pipe", "jpeg_pipe", "bmp_pipe", "tiff_pipe", "pgm_pipe", "ppm_pipe"];

    private static bool IsImageDemuxer(string? demuxer) =>
        !string.IsNullOrWhiteSpace(demuxer)
        && demuxer.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(n => ImageDemuxers.Contains(n, StringComparer.Ordinal));

    /// <summary>
    /// The single routing rule. Only a positively identified layout is sampled; everything else fails closed and
    /// deterministically (the same bytes always give the same layout). Data and subtitle streams are not counted
    /// towards the layout; the text of text subtitle streams is read and judged with the transcript, and a bitmap
    /// subtitle stream fails.
    /// </summary>
    internal static MediaLayout DecideLayout(MediaProbe probe)
    {
        if (!probe.Parsed)
            return new MediaLayout(LayoutKind.Refused, -1, [], StreamsUnreadableFailure, !probe.TimedOut);
        if (string.IsNullOrWhiteSpace(probe.Demuxer))
            return new MediaLayout(LayoutKind.Refused, -1, [], "the container format could not be identified", true);

        int video = probe.VideoStreams;
        int audio = probe.AudioStreams;
        int pictures = probe.PictureStreams;

        // 1. Image: a real image demuxer, one video stream, no audio, no attached pictures.
        if (IsImageDemuxer(probe.Demuxer))
        {
            return video == 1 && audio == 0 && pictures == 0
                ? new MediaLayout(LayoutKind.Image, probe.VideoIndexes.Count == 1 ? probe.VideoIndexes[0] : 0, [], null, false)
                : new MediaLayout(LayoutKind.Refused, -1, [], "an image with audio or extra streams", true);
        }

        if (probe.InvalidPictures > 0)
            return new MediaLayout(LayoutKind.Refused, -1, [], "an attached picture is not a single still image", true);

        IReadOnlyList<int> judged = probe.PictureIndexes.Take(MaxAttachedPictures).ToArray();
        string? tooMany = pictures > MaxAttachedPictures ? $"more than {MaxAttachedPictures} attached pictures" : null;

        // Text subtitle streams are read and judged with the transcript; a bitmap one (or one in a format not known to
        // be text) cannot be read and fails. Data streams are ignored: nothing plays them.
        IReadOnlyList<int> subtitles = probe.TextSubtitleIndexes.Take(MaxSubtitleStreams).ToArray();
        string? subtitleRefusal = probe.OtherSubtitles > 0 ? "a subtitle stream that is not text (a bitmap or unknown format)"
            : probe.TextSubtitleIndexes.Count > MaxSubtitleStreams ? $"more than {MaxSubtitleStreams} subtitle streams"
            : null;

        string? tooManyChannels = audio > 0 && probe.AudioChannels > MaxAudioChannels
            ? $"more than {MaxAudioChannels} audio channels" : null;

        // 2. Video: exactly one real video stream, at most one audio stream.
        // 3. Audio: no real video stream, exactly one audio stream.
        if (video >= 1 && probe.VideoIndexes.Count > 0)
        {
            string? refusal = video > 1 ? "more than one video stream"
                : audio > 1 ? MultipleAudioTracksFailure
                : tooManyChannels ?? tooMany ?? subtitleRefusal;
            return new MediaLayout(LayoutKind.Video, probe.VideoIndexes[0], judged, refusal, refusal is not null, subtitles);
        }

        if (video == 0 && audio == 1)
        {
            string? refusal = tooManyChannels ?? tooMany ?? subtitleRefusal;
            return new MediaLayout(LayoutKind.Audio, -1, judged, refusal, refusal is not null, subtitles);
        }

        string why = video == 0 && audio == 0 ? "no audio or video stream"
            : audio > 1 ? MultipleAudioTracksFailure
            : "an unrecognised stream layout";
        return new MediaLayout(LayoutKind.Refused, -1, [], why, true);
    }

    /// <summary>The real frame count and time a stream-copy pass reports.</summary>
    internal sealed record MediaMeasure(double? Seconds, long Frames);

    internal static MediaMeasure? ParseMeasure(string report)
    {
        if (string.IsNullOrWhiteSpace(report)) return null;

        System.Text.RegularExpressions.MatchCollection frames =
            System.Text.RegularExpressions.Regex.Matches(report, @"frame=\s*(\d+)");
        System.Text.RegularExpressions.MatchCollection times =
            System.Text.RegularExpressions.Regex.Matches(report, @"time=(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (frames.Count == 0) return null;

        double? seconds = null;
        if (times.Count > 0)
        {
            System.Text.RegularExpressions.Match t = times[^1];
            seconds = (int.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture) * 3600)
                      + (int.Parse(t.Groups[2].Value, CultureInfo.InvariantCulture) * 60)
                      + double.Parse(t.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        return new MediaMeasure(seconds, long.Parse(frames[^1].Groups[1].Value, CultureInfo.InvariantCulture));
    }

    private async Task<(MediaMeasure? Measure, bool TimedOut)> MeasureAsync(string inputPath, string map, bool decode, CancellationToken ct)
    {
        ProcessRunResult result = await RunAsync(
            FfmpegExecutable,
            ["-nostdin", "-hide_banner", "-protocol_whitelist", "file", "-format_whitelist", MediaInputFormats,
             "-threads", SamplingThreads, "-i", inputPath, "-map", map, .. (decode ? Array.Empty<string>() : ["-c", "copy"]), "-f", "null", "-"],
            ProcessTimeout, ct).ConfigureAwait(false);
        return (result.Succeeded ? ParseMeasure(result.StdErr) : null, result.Transient);
    }

    /// <summary>
    /// The length the video stream ITSELF declares, in seconds: its own duration or tag, and only for a file with
    /// no other stream the container's. Null when it declares none (then nothing is compared). The container's
    /// length is the longest of its streams, so a 10 s video with a 60 s audio track declares 60 s there.
    /// </summary>
    internal static double? ParseDeclaredVideoSeconds(string report, MediaProbe probe)
    {
        if (!string.IsNullOrWhiteSpace(report))
        {
            try
            {
                using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(report);
                if (json.RootElement.TryGetProperty("streams", out System.Text.Json.JsonElement streams)
                    && streams.ValueKind == System.Text.Json.JsonValueKind.Array
                    && streams.GetArrayLength() > 0)
                {
                    System.Text.Json.JsonElement stream = streams[0];
                    if (stream.TryGetProperty("duration", out System.Text.Json.JsonElement d)
                        && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double own)
                        && own > 0)
                    {
                        return own;
                    }

                    if (stream.TryGetProperty("tags", out System.Text.Json.JsonElement tags)
                        && tags.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        foreach (System.Text.Json.JsonProperty tag in tags.EnumerateObject())
                        {
                            if (string.Equals(tag.Name, "DURATION", StringComparison.OrdinalIgnoreCase)
                                && TimeSpan.TryParse(tag.Value.GetString(), CultureInfo.InvariantCulture, out TimeSpan span)
                                && span.TotalSeconds > 0)
                            {
                                return span.TotalSeconds;
                            }
                        }
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Unreadable: treated as not declared.
            }
        }

        return probe.AudioStreams == 0 && probe.DurationSeconds is > 0 ? probe.DurationSeconds : null;
    }

    private async Task<double?> DeclaredVideoSecondsAsync(string inputPath, MediaProbe probe, CancellationToken ct)
    {
        ProcessRunResult result = await RunAsync(
            FfprobeExecutable,
            ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", MediaInputFormats,
             "-select_streams", probe.VideoIndexes.Count == 1 ? probe.VideoIndexes[0].ToString(CultureInfo.InvariantCulture) : "v:0", "-show_entries", "stream=duration:stream_tags=DURATION", "-of", "json", inputPath],
            ProcessTimeout, ct).ConfigureAwait(false);
        return ParseDeclaredVideoSeconds(result.Succeeded ? result.StdOut : string.Empty, probe);
    }

    /// <summary>Largest ffprobe report read; anything bigger is an unreadable probe.</summary>
    internal const int MaxProbeReportChars = 1_000_000;

    // No gif or webp: a cover in either can be animated, and a cover is judged on its first frame only.
    private static readonly string[] StillPictureCodecs = ["mjpeg", "png", "bmp"];

    /// <summary>
    /// Reads <c>ffprobe -show_streams -show_format -of json</c>. Stream kind, index and attached-picture flag come from
    /// structured fields only, never from free text an uploader controls. Unreadable JSON is an unparsed probe.
    /// </summary>
    internal static MediaProbe ParseProbe(string report)
    {
        MediaProbe unreadable = new(null, 0, 0, false);
        if (string.IsNullOrWhiteSpace(report) || report.Length > MaxProbeReportChars) return unreadable;

        try
        {
            using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(report);
            System.Text.Json.JsonElement root = json.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("streams", out System.Text.Json.JsonElement streams)
                || streams.ValueKind != System.Text.Json.JsonValueKind.Array)
                return unreadable;

            double? duration = null;
            double startSeconds = 0;
            string? demuxer = null;
            if (root.TryGetProperty("format", out System.Text.Json.JsonElement format)
                && format.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (format.TryGetProperty("format_name", out System.Text.Json.JsonElement fn)
                    && fn.ValueKind == System.Text.Json.JsonValueKind.String)
                    demuxer = fn.GetString();
                if (format.TryGetProperty("duration", out System.Text.Json.JsonElement d)
                    && d.ValueKind == System.Text.Json.JsonValueKind.String
                    && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double secs))
                    duration = secs;
                if (format.TryGetProperty("start_time", out System.Text.Json.JsonElement st)
                    && st.ValueKind == System.Text.Json.JsonValueKind.String
                    && double.TryParse(st.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double startSecs)
                    && double.IsFinite(startSecs))
                    startSeconds = startSecs;
            }

            int video = 0, audio = 0, pictures = 0, invalid = 0, otherSubtitles = 0, channels = 0;
            string? channelLayout = null;
            double? audioSeconds = null;
            List<int> videoIndexes = [];
            List<int> pictureIndexes = [];
            List<int> textSubtitles = [];
            foreach (System.Text.Json.JsonElement stream in streams.EnumerateArray())
            {
                if (stream.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !stream.TryGetProperty("index", out System.Text.Json.JsonElement idx)
                    || !idx.TryGetInt32(out int index)
                    || !stream.TryGetProperty("codec_type", out System.Text.Json.JsonElement ct)
                    || ct.ValueKind != System.Text.Json.JsonValueKind.String)
                    return unreadable;

                switch (ct.GetString())
                {
                    case "audio":
                        audio++;
                        if (stream.TryGetProperty("channels", out System.Text.Json.JsonElement ch) && ch.TryGetInt32(out int chans))
                            channels = chans;
                        if (stream.TryGetProperty("channel_layout", out System.Text.Json.JsonElement cl)
                            && cl.ValueKind == System.Text.Json.JsonValueKind.String)
                            channelLayout = cl.GetString();
                        if (stream.TryGetProperty("duration", out System.Text.Json.JsonElement ad)
                            && ad.ValueKind == System.Text.Json.JsonValueKind.String
                            && double.TryParse(ad.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double adSecs)
                            && adSecs > 0)
                            audioSeconds = adSecs;
                        break;
                    case "subtitle":
                        string? subCodec = stream.TryGetProperty("codec_name", out System.Text.Json.JsonElement sc)
                                           && sc.ValueKind == System.Text.Json.JsonValueKind.String ? sc.GetString() : null;
                        if (subCodec is not null && TextSubtitleCodecs.Contains(subCodec, StringComparer.Ordinal))
                            textSubtitles.Add(index);
                        else
                            otherSubtitles++;
                        break;
                    case "video":
                        bool attached = stream.TryGetProperty("disposition", out System.Text.Json.JsonElement disp)
                                        && disp.ValueKind == System.Text.Json.JsonValueKind.Object
                                        && disp.TryGetProperty("attached_pic", out System.Text.Json.JsonElement ap)
                                        && ap.ValueKind == System.Text.Json.JsonValueKind.Number
                                        && ap.GetInt32() == 1;
                        if (!attached)
                        {
                            video++;
                            videoIndexes.Add(index);
                            break;
                        }

                        pictures++;
                        pictureIndexes.Add(index);
                        // A cover is a single still: a real video codec, or more than one frame, is not a cover.
                        string? codec = stream.TryGetProperty("codec_name", out System.Text.Json.JsonElement cn)
                                        && cn.ValueKind == System.Text.Json.JsonValueKind.String ? cn.GetString() : null;
                        bool manyFrames = stream.TryGetProperty("nb_frames", out System.Text.Json.JsonElement nf)
                                          && nf.ValueKind == System.Text.Json.JsonValueKind.String
                                          && long.TryParse(nf.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long frames)
                                          && frames > 1;
                        if (codec is null || !StillPictureCodecs.Contains(codec, StringComparer.Ordinal) || manyFrames)
                            invalid++;
                        break;
                }
            }

            return new MediaProbe(
                duration, video, audio, true, pictures, false, videoIndexes, pictureIndexes, demuxer, invalid,
                startSeconds, channels, audio == 1 ? audioSeconds : null, textSubtitles, otherSubtitles, channelLayout);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return unreadable;
        }
    }

    private async Task<MediaProbe> ProbeAsync(string inputPath, CancellationToken ct)
    {
        ProcessRunResult result = await RunAsync(
            FfprobeExecutable,
            ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", MediaInputFormats,
             "-show_streams", "-show_format", "-of", "json", inputPath],
            ProcessTimeout, ct).ConfigureAwait(false);
        if (result.Transient) return new MediaProbe(null, 0, 0, false, 0, true);
        return result.Succeeded ? ParseProbe(result.StdOut) : new MediaProbe(null, 0, 0, false);
    }

    private const string StreamsUnreadableFailure = "the streams could not be read";

    private async Task<ModerationMediaSample> SampleAudioAsync(
        string inputPath, string workDirectory, MediaProbe probe, MediaLayout layout, CancellationToken ct)
    {
        string outputPath = Path.Combine(workDirectory, "audio.wav");
        ProcessRunResult result = await RunAsync(
            FfmpegExecutable, BuildAudioArgs(inputPath, outputPath, _options.MaxAudioSeconds, probe.AudioChannels), ProcessTimeout, ct).ConfigureAwait(false);

        if (!result.Succeeded || !File.Exists(outputPath))
            return new ModerationMediaSample([], null, ModerationContentKind.Audio, DescribeFailure("audio extraction", result));

        byte[]? audio = await ReadBoundedAsync(outputPath, _options.MaxAudioBytes, ct).ConfigureAwait(false);
        if (audio is null)
        {
            return new ModerationMediaSample([], null, ModerationContentKind.Audio,
                $"audio extraction produced more than {_options.MaxAudioBytes} bytes");
        }

        // Each attached picture is judged like any other frame: one cheap keyframe pass apiece.
        (List<byte[]> images, string? coverFailure, bool coverDeterministic) =
            await SampleCoversAsync(inputPath, workDirectory, layout.Pictures, ct).ConfigureAwait(false);

        (string? subtitleText, string? subtitleFailure, bool subtitleDeterministic) =
            await ReadSubtitlesAsync(inputPath, workDirectory, layout.SubtitleIndexes, ct).ConfigureAwait(false);
        AudioCheck check = await CheckAudioAsync(
            inputPath, workDirectory, probe, audio, probe.AudioStreamSeconds ?? probe.DurationSeconds, ct).ConfigureAwait(false);
        string? audioCheck = check.Failure;
        bool audioCheckDeterministic = check.Deterministic;

        string? failure = layout.Refusal;
        bool deterministic = layout.Refusal is not null && layout.Deterministic;
        if (failure is null && coverFailure is not null)
        {
            failure = coverFailure;
            deterministic = coverDeterministic;
        }

        if (failure is null && audioCheck is not null)
        {
            failure = audioCheck;
            deterministic = audioCheckDeterministic;
        }

        if (failure is null && subtitleFailure is not null)
        {
            failure = subtitleFailure;
            deterministic = subtitleDeterministic;
        }

        return new ModerationMediaSample(images, audio, ModerationContentKind.Audio, failure, deterministic, subtitleText, check.Side, ChannelAudio: check.Channels);
    }

    private const string MultipleAudioTracksFailure = "more than one audio track";

    private async Task<(List<byte[]> Images, string? Failure, bool Deterministic)> SampleCoversAsync(
        string inputPath, string workDirectory, IReadOnlyList<int> indexes, CancellationToken ct)
    {
        List<byte[]> images = [];
        for (int i = 0; i < indexes.Count; i++)
        {
            string coverPath = Path.Combine(workDirectory, $"cover_{i + 1:000}.jpg");
            ProcessRunResult cover = await RunAsync(
                FfmpegExecutable, BuildCoverArgs(inputPath, coverPath, _options.EffectiveSampleImageMaxLongSide, indexes[i]),
                FrameCallTimeout, ct).ConfigureAwait(false);
            byte[]? bytes = cover.Succeeded && File.Exists(coverPath)
                ? await ReadBoundedAsync(coverPath, _options.MaxImageBytes, ct).ConfigureAwait(false)
                : null;
            if (bytes is null)
                return (images, "an attached picture could not be read", !cover.Transient);
            images.Add(bytes);
        }

        return (images, null, false);
    }

    /// <summary>One frame must finish within this; a file that cannot yield one is a failure, not a wait.</summary>
    private static readonly TimeSpan FrameCallTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The video packets of one stream, by presentation time, as ffprobe lists them (no decoding).</summary>
    internal sealed record PacketList(IReadOnlyList<double> Times, double LastDuration, bool Transient = false, bool TooMany = false, bool Failed = false, bool DuplicateTimes = false)
    {
        public int Count => Times.Count;

        /// <summary>
        /// The length of the video from its packets: first presentation time to the end of the last packet. A single
        /// packet without a duration is not zero long.
        /// </summary>
        public double Length => Count == 0 ? 0 : Math.Max(Times[^1] - Times[0] + LastDuration, 1.0 / 30);

        /// <summary>
        /// The same packets with the last one on screen up to <paramref name="endSeconds"/> (the end of the longest stream of
        /// the container, in the packets' own clock): the last frame of a video stays shown while a longer audio track plays.
        /// Never shortens.
        /// </summary>
        public PacketList ExtendedTo(double endSeconds) =>
            Count == 0 || !double.IsFinite(endSeconds) || endSeconds - Times[^1] <= LastDuration
                ? this
                : this with { LastDuration = endSeconds - Times[^1] };
    }

    /// <summary>
    /// Reads <c>ffprobe -show_entries packet=pts_time,dts_time,duration_time -of csv=p=0</c> (that field order). A packet
    /// takes its presentation time, else its decode time; one with neither is skipped.
    /// </summary>
    internal static PacketList ParsePackets(string report)
    {
        PacketAccumulator packets = new(keepTimes: true);
        foreach (string line in report.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            packets.Add(line);
        return packets.Build();
    }

    /// <summary>
    /// A packet list read one line at a time. With <c>keepTimes</c> off only what the end of the stream needs is kept (the
    /// count, the last two times and the duration of the last packet): the memory of an audio list of 12 hours is the same
    /// as of one of a second.
    /// </summary>
    private sealed class PacketAccumulator(bool keepTimes, int maxPackets = MaxPackets)
    {
        private readonly List<double> _times = [];
        private double _lastTime = double.NegativeInfinity, _lastDuration, _max1 = double.NegativeInfinity, _max2 = double.NegativeInfinity;

        public int Count { get; private set; }

        public void Add(string line)
        {
            // No Split and no Trim: a 12-hour audio list is two million lines, and each would allocate its fields.
            ReadOnlySpan<char> text = line.AsSpan().Trim();
            int first = text.IndexOf(',');
            if (first < 0) return;
            ReadOnlySpan<char> rest = text[(first + 1)..];
            int second = rest.IndexOf(',');
            if (second < 0) return;
            ReadOnlySpan<char> durationField = rest[(second + 1)..];
            int third = durationField.IndexOf(',');
            if (third >= 0) durationField = durationField[..third];

            double? time = ParseSeconds(text[..first]) ?? ParseSeconds(rest[..second]);
            if (time is null) return;
            Count++;
            if (keepTimes) _times.Add(time.Value);
            if (time.Value >= _max1) { _max2 = _max1; _max1 = time.Value; }
            else if (time.Value > _max2) _max2 = time.Value;
            if (time.Value >= _lastTime)
            {
                _lastTime = time.Value;
                _lastDuration = ParseSeconds(durationField) is double d and > 0 ? d : 0;
            }
        }

        public PacketList Build()
        {
            _times.Sort();
            // Two packets printed with one time cannot both be told apart by time: the frame check could not say which was drawn.
            bool duplicate = false;
            for (int i = 1; i < _times.Count && !duplicate; i++)
                duplicate = _times[i] == _times[i - 1];
            return new PacketList(_times, _lastDuration, TooMany: Count > maxPackets, DuplicateTimes: duplicate);
        }

        /// <summary>The end of the stream: its last packet plus that packet's duration, else the gap before it. Null when empty.</summary>
        public double? End
        {
            get
            {
                if (Count == 0) return null;
                double span = _lastDuration > 0 ? _lastDuration : Count > 1 ? Math.Max(0, _max1 - _max2) : 0;
                return _max1 + span;
            }
        }

        private static double? ParseSeconds(ReadOnlySpan<char> field) =>
            double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;
    }

    /// <summary>The refusal of a video with two packets of one printed time.</summary>
    internal const string DuplicateFrameTimesFailure = "two video frames share one time";

    /// <summary>The refusal of a video (or an audio track under it) that plays longer than <see cref="MaxJudgedVideoSeconds"/>.</summary>
    internal static readonly string TooLongFailure = $"the video is longer than {MaxJudgedVideoSeconds / 3600:0} hours";

    /// <summary>The packets that are on screen: without the first <paramref name="firstDrawable"/> (the pre-roll of a stream-copied cut).</summary>
    private static PacketList ShownFrom(PacketList packets, int firstDrawable) =>
        firstDrawable > 0 && firstDrawable < packets.Count ? packets with { Times = packets.Times.Skip(firstDrawable).ToArray() } : packets;

    /// <summary>
    /// Where the audio really ends, in the container's clock: the audio stream's own last packet time plus its duration, read
    /// from its packets (no decoding), bounded like the video list. Null with no failure when the stream has no packets.
    /// </summary>
    private async Task<(double? End, string? Failure, bool Transient)> AudioEndSecondsAsync(string inputPath, CancellationToken ct)
    {
        PacketAccumulator audio = new(keepTimes: false, _maxPackets);
        ProcessRunResult result = await RunAsync(
            FfprobeExecutable,
            ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", MediaInputFormats,
             "-select_streams", "a",
             "-read_intervals", $"%+#{(_maxPackets + 1).ToString(CultureInfo.InvariantCulture)}",
             "-show_entries", "packet=pts_time,dts_time,duration_time", "-of", "csv=p=0", inputPath],
            ProcessTimeout, ct, audio.Add).ConfigureAwait(false);
        if (!result.Succeeded)
            return (null, "the audio could not be measured", result.Transient);
        // Reaching the cap is a stream too long to be played inside the limit: the same refusal as one that plays too long.
        if (audio.Count > _maxPackets)
            return (null, TooLongFailure, false);
        return (audio.End, null, false);
    }

    /// <summary>The end of an audio packet list: its last packet plus that packet's duration, else the gap before it. Null when empty.</summary>
    internal static double? AudioEnd(PacketList audio)
    {
        if (audio.Count == 0) return null;
        double last = audio.Times[^1];
        double span = audio.LastDuration > 0 ? audio.LastDuration : audio.Count > 1 ? Math.Max(0, last - audio.Times[^2]) : 0;
        return last + span;
    }

    private async Task<PacketList> ListPacketsAsync(string inputPath, int videoIndex, CancellationToken ct)
    {
        // One more packet than the cap is asked for, so "more than the cap" is told from "exactly the cap".
        PacketAccumulator video = new(keepTimes: true, _maxPackets);
        ProcessRunResult result = await RunAsync(
            FfprobeExecutable,
            ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", MediaInputFormats,
             "-select_streams", videoIndex.ToString(CultureInfo.InvariantCulture),
             "-read_intervals", $"%+#{(_maxPackets + 1).ToString(CultureInfo.InvariantCulture)}",
             "-show_entries", "packet=pts_time,dts_time,duration_time", "-of", "csv=p=0", inputPath],
            ProcessTimeout, ct, video.Add).ConfigureAwait(false);
        if (!result.Succeeded)
            return new PacketList([], 0, result.Transient, Failed: true);
        return video.Build();
    }

    /// <summary>
    /// How far a printed frame time may be from the drawn packet's time and still be that packet: the rounding of the
    /// printed time and of the two clocks, nothing more. A frame of another packet is another frame.
    /// </summary>
    internal const double FrameTimeToleranceSeconds = 0.002;

    /// <summary>
    /// The window the decode from the start selects its frame in, round the drawn packet's time: narrower than packets
    /// 1 ms apart (the timebases of real files are finer than this, so an exact time is always inside it).
    /// </summary>
    internal const double SlowSelectToleranceSeconds = 0.0005;

    /// <summary>
    /// Frames of one output that may be decoded from the start of the file (the slow path, below) because the fast seek
    /// did not return the packet drawn. An honest file needs a few: the first packets of an AVI with B-frames, the
    /// leading pictures of an open GOP. A file that needs more is treated as the failure path, which is final.
    /// </summary>
    internal const int MaxSlowDecodes = 12;

    /// <summary>How often one sampling may redraw because the packet drawn is before anything the file decodes.</summary>
    internal const int MaxLeadRedraws = 4;

    /// <summary>Time allowed to one decode from the start of a file; the total deadline still bounds everything.</summary>
    private static readonly TimeSpan SlowFrameCallTimeout = TimeSpan.FromSeconds(40);

    /// <summary>The time of the frame ffmpeg wrote, from the first <c>pts_time:</c> the metadata filter printed; null when none.</summary>
    internal static double? ParseFrameTime(string stdout)
    {
        foreach (string line in (stdout ?? string.Empty).Split('\n'))
        {
            int at = line.IndexOf("pts_time:", StringComparison.Ordinal);
            if (at < 0) continue;
            string field = line[(at + 9)..].Trim();
            return double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;
        }

        return null;
    }

    /// <summary>
    /// The frame written is the packet drawn: its printed time is within <see cref="FrameTimeToleranceSeconds"/> of the
    /// drawn packet's time and no other packet is closer to it. A frame with no time cannot be shown to be the one drawn,
    /// so it is not.
    /// </summary>
    internal static bool FrameIsTheDrawnOne(PacketList packets, int drawn, double? frameTime)
    {
        if (frameTime is not double at || packets.Count == 0 || drawn < 0 || drawn >= packets.Count) return false;
        double distance = Math.Abs(at - packets.Times[drawn]);
        if (distance > FrameTimeToleranceSeconds) return false;
        if (drawn > 0 && Math.Abs(at - packets.Times[drawn - 1]) < distance) return false;
        if (drawn + 1 < packets.Count && Math.Abs(at - packets.Times[drawn + 1]) < distance) return false;
        return true;
    }

    /// <summary>The gap after each packet: to the next one, and for the last its own duration.</summary>
    private static double GapAfter(IReadOnlyList<double> times, double lastDuration, int i) =>
        i + 1 < times.Count ? times[i + 1] - times[i] : lastDuration;

    /// <summary>
    /// Which packets to judge, as zero-based positions in presentation order. At most
    /// <paramref name="max"/> of them, <c>ceil(length / interval)</c> for a short video. Fewer packets than that are
    /// all judged. Otherwise (1) the packets that stay on screen longer than <see cref="HoldSeconds"/> (or one slice of the
    /// played time, when that is shorter), decided on the REAL gap and never on a capped weight, are always judged, the
    /// longest first, up to half the budget (past it the shorter holds stay in the slices, reached in proportion to the
    /// time they fill); (2) held packets are taken out, and half of the rest are the frame on screen at a random point inside
    /// each of equal slices of the played time that remains; (3) the other half are one packet drawn at random inside each
    /// of equal strata of the positions of the rest, so a burst of many tiny packets is reached in proportion to how many it
    /// has. Padding a still to a day buys nothing: the output lane refuses over 12 hours, so the played time is bounded.
    /// Nothing is predictable and a changed file is drawn afresh. A frame drawn twice is judged once, and its place is drawn
    /// again. Packets before <paramref name="firstDrawable"/> (the pre-roll of a stream-copied cut, never shown) are not drawn.
    /// </summary>
    internal static IReadOnlyList<int> PickPackets(PacketList packets, int interval, int max, Random random, int firstDrawable = 0)
    {
        if (firstDrawable > 0 && firstDrawable < packets.Count)
        {
            PacketList shown = packets with { Times = packets.Times.Skip(firstDrawable).ToArray() };
            return [.. PickPackets(shown, interval, max, random).Select(i => i + firstDrawable)];
        }

        int n = packets.Count;
        int count = (int)Math.Clamp(Math.Ceiling(packets.Length / interval), 1, max);
        if (n <= count)
            return Enumerable.Range(0, n).ToArray();

        double[] gap = new double[n];
        double total = 0;
        for (int i = 0; i < n; i++)
        {
            gap[i] = Math.Max(0, GapAfter(packets.Times, packets.LastDuration, i));
            total += gap[i];
        }

        double holdSeconds = Math.Min(HoldSeconds, total / count);
        List<int> held = Enumerable.Range(0, n)
            .Where(i => gap[i] > holdSeconds)
            .OrderByDescending(i => gap[i]).ThenBy(_ => random.Next())
            .Take(count / 2)
            .ToList();
        HashSet<int> heldSet = [.. held];
        int[] rest = [.. Enumerable.Range(0, n).Where(i => !heldSet.Contains(i))];

        // Slices of what plays once the holds are out: real time, so a hold left in cannot be padded and one taken out
        // takes none of the draws.
        double[] cumulative = new double[rest.Length + 1];
        for (int r = 0; r < rest.Length; r++)
            cumulative[r + 1] = cumulative[r] + gap[rest[r]];
        double weighted = cumulative[rest.Length];
        if (weighted <= 0)
        {
            for (int r = 0; r < rest.Length; r++) cumulative[r + 1] = r + 1;
            weighted = rest.Length;
        }

        int draws = count - held.Count;
        int byTime = draws / 2;
        int byPosition = draws - byTime;
        SortedSet<int> picked = [.. held];

        for (int j = 0; j < byTime; j++)
        {
            double at = weighted * (j + random.NextDouble()) / byTime;
            picked.Add(rest[PacketAtWeight(cumulative, at)]);
        }

        for (int j = 0; j < byPosition; j++)
        {
            long lo = (long)j * rest.Length / byPosition;
            long hi = Math.Max(lo + 1, (long)(j + 1) * rest.Length / byPosition);
            picked.Add(rest[(int)random.NextInt64(lo, hi)]);
        }

        // A frame drawn twice is judged once; the places it leaves are drawn again at random, so the budget is kept.
        while (picked.Count < count)
            picked.Add(rest[random.Next(rest.Length)]);

        return [.. picked];
    }

    /// <summary>A packet on screen for longer than this is always judged (up to half the budget), however many tiny packets surround it.</summary>
    internal const double HoldSeconds = 60;

    /// <summary>
    /// The longest video the output lane judges, measured from its packets and the longest stream of the container: 12
    /// hours. This number is a product choice, not a technical limit: it leaves real long recordings room and refuses a length
    /// that exists to move the odds. A longer one is a final refusal on the output lane; ingest still judges what was sampled.
    /// </summary>
    internal const double MaxJudgedVideoSeconds = 12 * 3600;

    /// <summary>The packet whose weighted span holds <paramref name="at"/> (cumulative weights, so the first packet that ends after it).</summary>
    private static int PacketAtWeight(double[] cumulative, double at)
    {
        int lo = 0, hi = cumulative.Length - 2;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (cumulative[mid + 1] > at) hi = mid; else lo = mid + 1;
        }

        return lo;
    }

    /// <summary>Evenly spaced zero-based frame indexes out of <paramref name="frames"/>, at most <paramref name="max"/>.</summary>
    internal static IReadOnlyList<long> FrameIndexes(long frames, int max)
    {
        long count = Math.Clamp(frames, 1, max);
        return Enumerable.Range(0, (int)count).Select(i => Math.Min(frames - 1, (long)((i + 0.5) * frames / count))).Distinct().ToArray();
    }

    /// <summary>An animated image: its decoded frames spread evenly over the whole animation, up to the cap.</summary>
    private async Task<ModerationMediaSample> SampleAnimatedImageAsync(
        string inputPath, string workDirectory, MediaMeasure measure, CancellationToken ct)
    {
        string pattern = Path.Combine(workDirectory, "frame_%03d.jpg");
        IReadOnlyList<long> indexes = FrameIndexes(measure.Frames, _options.EffectiveVideoMaxFrames);
        ProcessRunResult run = await RunAsync(
            FfmpegExecutable,
            BuildAnimatedFramesArgs(inputPath, pattern, indexes, _options.EffectiveSampleImageMaxLongSide),
            ProcessTimeout, ct).ConfigureAwait(false);

        List<byte[]> images = await ReadFramesAsync(workDirectory, ct).ConfigureAwait(false);
        string? failure = null;
        if (images.Count == 0 || images.Count < indexes.Count || !run.Succeeded)
            failure = DescribeFailure("animation frame sampling", run);

        return new ModerationMediaSample(images, null, ModerationContentKind.Video, failure, Deterministic: failure is not null && !run.Transient);
    }

    private async Task<List<byte[]>> ReadFramesAsync(string workDirectory, CancellationToken ct)
    {
        List<byte[]> images = [];
        foreach (string path in Directory.EnumerateFiles(workDirectory, "frame_*.jpg")
                     .OrderBy(p => p, StringComparer.Ordinal)
                     .Take(_options.EffectiveVideoMaxFrames))
        {
            byte[]? frame = await ReadBoundedAsync(path, _options.MaxImageBytes, ct).ConfigureAwait(false);
            if (frame is not null) images.Add(frame);
        }

        return images;
    }

    private async Task<ModerationMediaSample> SampleVideoAsync(
        string inputPath, string workDirectory, MediaProbe probe, MediaLayout layout, PacketList packets, CancellationToken ct)
    {
        // The frames and the failures are KEPT TOGETHER: whatever sampled is returned with the reason it is not
        // the whole story, so ingest can judge what it got while the output lane refuses.
        string? failure = null;
        bool deterministic = false;
        void Fail(string reason, bool isDeterministic)
        {
            // The first reason stands, except that a permanent one replaces a transient one.
            if (failure is not null && (deterministic || !isDeterministic)) return;
            failure = reason;
            deterministic = isDeterministic;
        }

        if (!probe.Parsed) Fail(StreamsUnreadableFailure, !probe.TimedOut);

        // The frames come from the packet list: by position, never by the clock. A one-line remux that moves the last
        // frames an hour later cannot make every sample hit one frame.
        IReadOnlyList<int> picked = [];
        PacketList played = packets;
        int firstDrawable = 0;
        if (probe.Parsed)
        {
            if (packets.Failed)
            {
                Fail("the video could not be measured", !packets.Transient);
            }
            else if (packets.TooMany)
            {
                Fail(TooLongFailure, true);
            }
            else if (packets.Count == 0)
            {
                Fail("the video length could not be measured", true);
            }
            else
            {
                // Two video packets with one printed time: a final refusal, and the frames are still sampled for ingest.
                if (packets.DuplicateTimes) Fail(DuplicateFrameTimesFailure, true);

                // The last frame stays on screen while a longer stream plays (a still under an hour of audio): it counts up to the
                // end of the longest stream, in the weights, in the holds and in the 12 h limit. The declared-length check below
                // compares the video stream with its own declared length, so it keeps the packets' own length.
                // Packets before the first shown time (the pre-roll a stream-copied cut keeps for its first frame) are never
                // on screen: a seek to one returns the first shown frame, so none is drawn, and none counts in the length.
                // The start time is uploader-controlled, so the exclusion is capped: an honest copy cut keeps at most a
                // group of pictures before its first shown frame, so packets further than MaxPreRollSeconds from the
                // first packet stay drawable (a seek that returns the wrong frame there is caught by the frame check).
                firstDrawable = PreRollPackets(packets.Times, probe.StartSeconds);
                if (firstDrawable >= packets.Count) firstDrawable = 0;
                PacketList shown = ShownFrom(packets, firstDrawable);
                double length = shown.Length;
                played = packets;

                // The end of the audio is the end of the audio stream's own last packet, never a length the container
                // declares (a live webm declares none, another a low one). The video's own length is compared with its
                // declared one below, so a lie about the video's length still cannot move the draws: a video-only file is
                // never extended.
                if (probe.AudioStreams > 0)
                {
                    (double? audioEnd, string? audioFailure, bool audioTransient) = await AudioEndSecondsAsync(inputPath, ct).ConfigureAwait(false);
                    if (audioFailure is not null) Fail(audioFailure, !audioTransient);
                    else if (audioEnd is double end) played = packets.ExtendedTo(end);
                }

                if (ShownFrom(played, firstDrawable).Length > MaxJudgedVideoSeconds)
                    Fail(TooLongFailure, true);

                double? declared = await DeclaredVideoSecondsAsync(inputPath, probe, ct).ConfigureAwait(false);

                // A single packet (a still held under long audio) has no length of its own to compare.
                if (shown.Count > 1 && declared is > 0 && (length > declared * 2 || length * 2 < declared))
                    Fail("the declared duration does not match the video", true);

                picked = PickPackets(played, _options.EffectiveVideoFrameIntervalSeconds, _options.EffectiveVideoMaxFrames, _random, firstDrawable);
            }
        }

        // Seeks in MPEG-TS and MPEG-PS return the keyframe, not the requested frame: the frames below are only what a
        // crafted file shows at its keyframes, so the output lane refuses. Ingest still judges what it got.
        if (probe.Demuxer is not null
            && probe.Demuxer.Split(',', StringSplitOptions.TrimEntries).Any(d => SeekUnsafeDemuxers.Contains(d, StringComparer.Ordinal)))
            Fail("this container's seek returns the keyframe, not the frame asked for", true);

        bool anyTransient = false;
        bool wrongFrame = false;
        bool frameFailed = false;
        string? frameReason = null;
        int slowDecodes = 0, leadRedraws = 0;
        double? firstDecoded = null;
        List<int> pending = [.. picked];
        HashSet<int> tried = [.. picked];

        // A place drawn again: a random packet not tried yet and shown (false when none could be found).
        void Redraw()
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                // The same two ways the first draw uses, half each: the packet on screen at a random time of what plays, or a
                // random position among the packets, so a redraw is no more predictable than a draw.
                int again;
                if (attempt % 2 == 0 && packets.Count - firstDrawable > 1)
                {
                    double from = packets.Times[firstDrawable], to = packets.Times[packets.Count - 1];
                    double when = from + (_random.NextDouble() * (to - from));
                    int lo = firstDrawable, hi = packets.Count - 1;
                    while (lo < hi)
                    {
                        int mid = lo + ((hi - lo + 1) / 2);
                        if (packets.Times[mid] <= when) lo = mid; else hi = mid - 1;
                    }

                    again = lo;
                }
                else
                {
                    again = _random.Next(firstDrawable, packets.Count);
                }

                if (tried.Add(again)) { pending.Add(again); return; }
            }
        }

        for (int slot = 0, next = 0; slot < picked.Count && next < pending.Count; next++)
        {
            int drawn = pending[next];
            // ffmpeg's -ss counts from the container's start time, so the packet's own time is shifted back by it.
            double seek = Math.Max(0, packets.Times[drawn] - probe.StartSeconds - 0.0002);
            string framePath = Path.Combine(workDirectory, $"frame_{slot + 1:000}.jpg");
            ProcessRunResult one = await RunAsync(
                FfmpegExecutable,
                BuildFrameAtArgs(inputPath, framePath, seek, _options.EffectiveSampleImageMaxLongSide, layout.VideoIndex),
                FrameCallTimeout,
                ct).ConfigureAwait(false);

            if (one.Transient)
            {
                frameFailed = true;
                anyTransient = true;
                frameReason ??= DescribeFailure("video frame sampling", one);
                slot++;
                continue;
            }

            if (one.Succeeded && File.Exists(framePath) && FrameIsTheDrawnOne(packets, drawn, ParseFrameTime(one.StdOut)))
            {
                slot++;
                continue;
            }

            // The fast seek did not return the packet drawn (the container's seek, an index that lies, an open GOP, a
            // frame that did not decode): that frame is not the one drawn and is thrown away. The packet is decoded from
            // the start of the file instead, ignoring the index, and only that frame is accepted, by its printed time.
            if (++slowDecodes > MaxSlowDecodes)
            {
                // Too many for an honest file: the failure path. The frame stays for ingest, which judges what it got.
                wrongFrame = true;
                slot++;
                continue;
            }

            // The fast frame is set aside: it is dropped when the slow one stands in for it, and comes back (unverified,
            // for ingest) when the answer is a final refusal.
            string aside = framePath + ".fast";
            if (File.Exists(framePath)) File.Move(framePath, aside, true);

            double drawnAt = packets.Times[drawn];
            ProcessRunResult slow = await RunAsync(
                FfmpegExecutable,
                BuildFrameFromStartArgs(inputPath, framePath, drawnAt, _options.EffectiveSampleImageMaxLongSide, layout.VideoIndex),
                SlowFrameCallTimeout,
                ct).ConfigureAwait(false);
            if (slow.Transient)
            {
                File.Delete(aside);
                frameFailed = true;
                anyTransient = true;
                frameReason ??= DescribeFailure("video frame sampling from the start", slow);
                slot++;
                continue;
            }

            // (Some ffmpeg builds end with an error and no file when no frame has the time asked for; that is the same
            // answer as a clean end with no file, so both go on to the check below.)
            if (File.Exists(framePath) && FrameIsTheDrawnOne(packets, drawn, ParseFrameTime(slow.StdOut)))
            {
                File.Delete(aside);
                slot++;
                continue;
            }

            // The file decoded to its end and no frame is at the time of the packet drawn. If the first frame the file
            // decodes at all comes later than that time, this packet is before anything playable (an undecodable lead):
            // it is never shown, so it is taken out of the drawable set (it was marked tried) and its place is drawn
            // again, a bounded number of times. Anything else is final: the sample carries the failure, so what it holds
            // is never judged as if it were the frames drawn.
            if (File.Exists(framePath)) File.Delete(framePath);
            if (leadRedraws < MaxLeadRedraws)
            {
                firstDecoded ??= await FirstDecodedFrameTimeAsync(inputPath, workDirectory, layout.VideoIndex, ct).ConfigureAwait(false);
                if (firstDecoded is double first && first > drawnAt + FrameTimeToleranceSeconds)
                {
                    leadRedraws++;
                    File.Delete(aside);
                    Redraw();
                    continue;
                }
            }

            if (File.Exists(aside)) File.Move(aside, framePath, true);
            if (slow.Succeeded)
            {
                wrongFrame = true;
            }
            else
            {
                frameFailed = true;
                frameReason ??= DescribeFailure("video frame sampling from the start", slow);
            }

            slot++;
        }

        List<byte[]> images = await ReadFramesAsync(workDirectory, ct).ConfigureAwait(false);
        if (images.Count < picked.Count && !frameFailed && !wrongFrame)
        {
            frameFailed = true;
            frameReason = "ffmpeg video frame sampling produced fewer frames than asked for.";
        }

        if (frameFailed) Fail(frameReason!, !anyTransient);
        if (wrongFrame) Fail("the container returned a frame other than the one drawn, so its seek table does not match its video", true);

        string audioPath = Path.Combine(workDirectory, "audio.wav");
        ProcessRunResult audio = await RunAsync(
            FfmpegExecutable, BuildAudioArgs(inputPath, audioPath, _options.MaxAudioSeconds, probe.AudioChannels), ProcessTimeout, ct).ConfigureAwait(false);

        byte[]? audioBytes = audio.Succeeded && File.Exists(audioPath)
            ? await ReadBoundedAsync(audioPath, _options.MaxAudioBytes, ct).ConfigureAwait(false)
            : null;

        // A track that exists but could not be extracted is not "no audio": say so, so the caller
        // never judges such a video on its frames alone. Several tracks are not transcribed.
        (List<byte[]> covers, string? coverFailure, bool coverDeterministic) =
            await SampleCoversAsync(inputPath, workDirectory, layout.Pictures, ct).ConfigureAwait(false);
        images.AddRange(covers);

        (string? subtitleText, string? subtitleFailure, bool subtitleDeterministic) =
            await ReadSubtitlesAsync(inputPath, workDirectory, layout.SubtitleIndexes, ct).ConfigureAwait(false);

        AudioCheck check = audioBytes is not null
            ? await CheckAudioAsync(inputPath, workDirectory, probe, audioBytes, probe.AudioStreamSeconds, ct).ConfigureAwait(false)
            : new AudioCheck(null, false);
        string? audioCheck = check.Failure;
        bool audioCheckDeterministic = check.Deterministic;

        if (layout.Refusal is not null)
            Fail(layout.Refusal, true);
        else if (coverFailure is not null)
            Fail(coverFailure, coverDeterministic);
        else if (audioBytes is null && (!probe.Parsed || probe.AudioStreams == 1))
            Fail(DescribeFailure("audio extraction", audio), !audio.Transient);
        else if (audioCheck is not null)
            Fail(audioCheck, audioCheckDeterministic);
        else if (subtitleFailure is not null)
            Fail(subtitleFailure, subtitleDeterministic);

        return new ModerationMediaSample(images, audioBytes, ModerationContentKind.Video, failure, deterministic, subtitleText, check.Side,
            TooLong: failure == TooLongFailure, ChannelAudio: check.Channels);
    }

    /// <summary>The time of the first frame the file decodes at all, from the start (null when none or unreadable).</summary>
    private async Task<double?> FirstDecodedFrameTimeAsync(string inputPath, string workDirectory, int videoIndex, CancellationToken ct)
    {
        string path = Path.Combine(workDirectory, "first_decoded.jpg");
        ProcessRunResult run = await RunAsync(
            FfmpegExecutable,
            BuildFrameFromStartArgs(inputPath, path, null, _options.EffectiveSampleImageMaxLongSide, videoIndex),
            SlowFrameCallTimeout, ct).ConfigureAwait(false);
        double? at = run.Succeeded && File.Exists(path) ? ParseFrameTime(run.StdOut) : null;
        if (File.Exists(path)) File.Delete(path);
        return at;
    }

    /// <summary>What the audio checks found: a failure (or none), and the side signal of two-channel audio when audible.</summary>
    private sealed record AudioCheck(string? Failure, bool Deterministic, byte[]? Side = null, IReadOnlyList<byte[]>? Channels = null);

    /// <summary>
    /// What the transcription would not hear. (1) Audio decoded for much less time than its container declares (a chained
    /// Ogg heard for 5 s of 33 s) is only partly heard. (2) Any frame the decoder produces with a channel count other than the
    /// declared one (stereo that turns 5.1, 5.1 that turns stereo, mono that turns stereo) is a final refusal. (3) Stereo is heard as the sum, and its left-minus-right mix is
    /// extracted too when any 1-second window of it is audible, so speech in opposite phase is transcribed. Audio of more
    /// than two channels (the engine's own 5.1 outputs included, up to 8 channels) is heard whole and channel by channel: the full mix
    /// of every channel is the main audio, and each channel whose loudest second is audible is also extracted by its own index. All are about the
    /// bytes, so deterministic; a measuring
    /// or extracting pass that fails to run is not.
    /// Accepted limit: in an audio-only MPEG-TS or MPEG-PS file, a channel change that follows a jump in the stream's time
    /// is not seen by (2). Other containers are unaffected, and so is any file with a picture.
    /// </summary>
    private async Task<AudioCheck> CheckAudioAsync(
        string inputPath, string workDirectory, MediaProbe probe, byte[] audio, double? declaredSeconds, CancellationToken ct)
    {
        // Any change of the channel count, in any layout, is a final refusal, read from the frames the decoder produces.
        if (probe.AudioChannels >= 1)
        {
            bool changed = false;
            ProcessRunResult frames = await RunAsync(
                FfprobeExecutable, BuildFrameChannelsArgs(inputPath, _options.MaxAudioSeconds), ProcessTimeout, ct,
                line => changed |= FrameChannelsDiffer(line, probe.AudioChannels)).ConfigureAwait(false);
            if (!frames.Succeeded)
                return new AudioCheck(DescribeFailure("audio channel measurement", frames), frames.Permanent);
            if (changed)
                return new AudioCheck(ChannelChangeFailure, true);
        }

        double heard = Math.Max(0, audio.Length - ModerationMediaSample.WavHeaderBytes) / (double)ModerationMediaSample.WavBytesPerSecond;
        if (declaredSeconds is > 0)
        {
            double expected = Math.Min(declaredSeconds.Value, _options.MaxAudioSeconds);
            if (AudioHeardTooShort(heard, expected))
                return new AudioCheck($"the audio was heard for {heard:0.#} s of the {expected:0.#} s it declares", true);
        }

        // Mono is one mix; the sum of two channels was transcribed (see BuildAudioArgs).
        if (probe.AudioChannels < 2)
            return new AudioCheck(null, false);

        if (probe.AudioChannels > 2)
            return await CheckChannelsAsync(inputPath, workDirectory, probe.AudioChannels, ct).ConfigureAwait(false);

        // Two channels: left minus right is transcribed too when any second of it is audible.
        ProcessRunResult run = await RunAsync(
            FfmpegExecutable, BuildSideMeasureArgs(inputPath, _options.MaxAudioSeconds), ProcessTimeout, ct).ConfigureAwait(false);
        if (!run.Succeeded)
            return new AudioCheck(DescribeFailure("audio measurement", run), run.Permanent);

        double? peak = ParseSidePeak(run.StdOut);
        if (peak is null)
            return new AudioCheck("the audio levels could not be measured", true);
        if (!SideAudible(peak.Value))
            return new AudioCheck(null, false);

        string sidePath = Path.Combine(workDirectory, "side.wav");
        ProcessRunResult extract = await RunAsync(
            FfmpegExecutable, BuildSideArgs(inputPath, sidePath, _options.MaxAudioSeconds), ProcessTimeout, ct).ConfigureAwait(false);
        byte[]? sideBytes = extract.Succeeded && File.Exists(sidePath)
            ? await ReadBoundedAsync(sidePath, _options.MaxAudioBytes, ct).ConfigureAwait(false)
            : null;
        return sideBytes is null
            ? new AudioCheck(DescribeFailure("alternating-mix extraction", extract), extract.Permanent)
            : new AudioCheck(null, false, sideBytes);
    }

    /// <summary>
    /// More than two channels: the full mix of every channel was transcribed as the main audio; every channel, by its own index, is also measured
    /// (one pass) and extracted when its loudest second is above the audible floor. Same outage and byte bounds as the main
    /// extraction: a pass that fails, a missing reading or a WAV over the byte cap fails the file. The channel count comes from
    /// the probe, and a change of the channel count anywhere in the decoded audio is a final refusal made before this runs
    /// (see <see cref="CheckAudioAsync"/>); the meter keeps the loudest reading of each channel, so a reset of its layout cannot lower one.
    /// </summary>
    private async Task<AudioCheck> CheckChannelsAsync(string inputPath, string workDirectory, int channels, CancellationToken ct)
    {
        ProcessRunResult run = await RunAsync(
            FfmpegExecutable, BuildChannelMeasureArgs(inputPath, _options.MaxAudioSeconds), ProcessTimeout, ct).ConfigureAwait(false);
        if (!run.Succeeded)
            return new AudioCheck(DescribeFailure("audio measurement", run), run.Permanent);

        double?[] peaks = ParseChannelPeaks(run.StdOut, channels, out bool extraChannel);
        if (extraChannel)
            return new AudioCheck("the audio has more channels than its stream declares", true);
        if (peaks.Any(p => p is null))
            return new AudioCheck("the audio levels could not be measured", true);

        List<byte[]> heard = [];
        for (int channel = 0; channel < channels; channel++)
        {
            if (!SideAudible(peaks[channel]!.Value)) continue;

            string path = Path.Combine(workDirectory, $"channel_{channel}.wav");
            ProcessRunResult extract = await RunAsync(
                FfmpegExecutable, BuildChannelArgs(inputPath, path, channel, _options.MaxAudioSeconds), ProcessTimeout, ct).ConfigureAwait(false);
            byte[]? bytes = extract.Succeeded && File.Exists(path)
                ? await ReadBoundedAsync(path, _options.MaxAudioBytes, ct).ConfigureAwait(false)
                : null;
            if (bytes is null)
                return new AudioCheck(DescribeFailure($"channel {channel} extraction", extract), extract.Permanent);
            heard.Add(bytes);
            File.Delete(path);
        }

        return new AudioCheck(null, false, null, heard);
    }

    /// <summary>The refusal of audio whose decoded channel count differs from the count its stream declares.</summary>
    internal const string ChannelChangeFailure = "the audio changes its channel count";

    /// <summary>The exact argv that prints the channel count of every decoded audio frame, up to the audio limit (also used by the tests).</summary>
    internal static string[] BuildFrameChannelsArgs(string inputPath, int maxSeconds) =>
    [
        "-v", "error", "-protocol_whitelist", "file", "-format_whitelist", MediaInputFormats,
        "-select_streams", "a",
        "-read_intervals", $"%+{Math.Max(1, maxSeconds).ToString(CultureInfo.InvariantCulture)}",
        "-show_entries", "frame=channels", "-of", "csv=p=0", inputPath
    ];

    /// <summary>True when a line of that output (a frame's channel count, maybe followed by a comma) names a count other than <paramref name="declared"/>.</summary>
    internal static bool FrameChannelsDiffer(string line, int declared)
    {
        ReadOnlySpan<char> text = line.AsSpan().Trim();
        int comma = text.IndexOf(',');
        if (comma >= 0) text = text[..comma];
        return text.Length > 0 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count != declared;
    }

    /// <summary>Heard for less than three quarters of the expected time, and by more than 1.5 s: a decoder cut, not rounding.</summary>
    internal static bool AudioHeardTooShort(double heardSeconds, double expectedSeconds) =>
        heardSeconds < expectedSeconds * 0.75 && expectedSeconds - heardSeconds > 1.5;

    /// <summary>The loudest second of the alternating-sign mix is above the audible floor, so it is transcribed too.</summary>
    internal static bool SideAudible(double sideDb) => sideDb > SideAudibleDb;

    /// <summary>The exact argv for the 16 kHz mono WAV of the alternating-sign mix (also used by the tests).</summary>
    internal static string[] BuildSideArgs(string inputPath, string outputPath, int maxSeconds = MaxAudioSeconds) =>
    [
        "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
        "-protocol_whitelist", "file",
        "-format_whitelist", MediaInputFormats,
        "-threads", SamplingThreads, "-i", inputPath,
        "-vn", "-sn", "-dn",
        "-af", StereoMixFilter(true),
        "-ar", "16000", "-acodec", "pcm_s16le",
        "-t", Math.Max(1, maxSeconds).ToString(CultureInfo.InvariantCulture),
        outputPath
    ];

    /// <summary>
    /// The exact argv that prints the loudest RMS level over any 1-second window (<c>astats length=1</c>, a sliding window
    /// over the first <paramref name="maxSeconds"/> seconds, not the level of the whole range) of the alternating-sign mix,
    /// on stdout through the <c>ametadata</c> filter. A short passage in a long quiet file is still measured at its own level.
    /// </summary>
    internal static string[] BuildSideMeasureArgs(string inputPath, int maxSeconds) =>
    [
        "-nostdin", "-hide_banner", "-loglevel", "error",
        "-protocol_whitelist", "file",
        "-format_whitelist", MediaInputFormats,
        "-threads", SamplingThreads, "-i", inputPath,
        "-vn", "-sn", "-dn",
        "-t", Math.Max(1, maxSeconds).ToString(CultureInfo.InvariantCulture),
        "-af", "aresample=16000,asetnsamples=n=65536:p=0," + StereoMixFilter(true)
               + ",astats=length=1:metadata=1:reset=0,ametadata=mode=print:key=lavfi.astats.1.RMS_peak:file=-",
        "-f", "null", "-"
    ];

    /// <summary>The last (overall) loudest-second RMS level in dB from the ametadata output; -inf is read as -120 dB.</summary>
    internal static double? ParseSidePeak(string output)
    {
        double? peak = null;
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("lavfi.astats.1.RMS_peak=", StringComparison.Ordinal)) continue;

            string value = line[(line.IndexOf('=') + 1)..];
            double? db = value is "-inf" ? -120
                : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? Math.Max(v, -120) : null;
            if (db is not null) peak = db;
        }

        return peak;
    }

    /// <summary>
    /// Reads every text subtitle stream (bounded) as plain text. A stream that cannot be read, or one with more text than
    /// <see cref="MaxSubtitleBytes"/> (not all of it would be judged), is a failure.
    /// </summary>
    private async Task<(string? Text, string? Failure, bool Deterministic)> ReadSubtitlesAsync(
        string inputPath, string workDirectory, IReadOnlyList<int> indexes, CancellationToken ct)
    {
        if (indexes.Count == 0) return (null, null, false);

        System.Text.StringBuilder text = new();
        for (int i = 0; i < indexes.Count; i++)
        {
            string path = Path.Combine(workDirectory, $"subtitle_{i + 1:000}.srt");
            ProcessRunResult run = await RunAsync(
                FfmpegExecutable, BuildSubtitleArgs(inputPath, path, indexes[i]), FrameCallTimeout, ct).ConfigureAwait(false);
            if (!run.Succeeded || !File.Exists(path))
                return (null, "a subtitle stream could not be read", run.Permanent || (run.Succeeded && !File.Exists(path)));

            if (new FileInfo(path).Length > MaxSubtitleBytes - text.Length)
                return (null, $"the subtitles have more than {MaxSubtitleBytes} bytes of text", true);

            text.AppendLine(SubtitlePlainText(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)));
        }

        string joined = text.ToString().Trim();
        return (joined.Length == 0 ? null : joined, null, false);
    }

    /// <summary>The exact argv for one text subtitle stream as SubRip, bounded in size (also used by the tests).</summary>
    internal static string[] BuildSubtitleArgs(string inputPath, string outputPath, int streamIndex) =>
    [
        "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
        "-protocol_whitelist", "file",
        "-format_whitelist", MediaInputFormats,
        "-threads", SamplingThreads, "-i", inputPath,
        "-map", $"0:{streamIndex.ToString(CultureInfo.InvariantCulture)}",
        "-vn", "-an", "-dn",
        // One byte more than the cap, so a stream over the cap is seen to be over it.
        "-fs", (MaxSubtitleBytes + 1).ToString(CultureInfo.InvariantCulture),
        "-f", "srt",
        outputPath
    ];

    /// <summary>
    /// A SubRip timing line and nothing else: <c>00:00:01,000 --&gt; 00:00:02,000</c>, optionally followed by WebVTT cue
    /// settings, each a real setting name with a real value (<c>align:start</c>, <c>line:-1</c>, <c>line:50%,center</c>,
    /// <c>position:10%</c>, <c>size:80%</c>, <c>vertical:rl</c>) or SubRip's <c>X1:</c> to <c>Y2:</c> coordinates (digits). Any
    /// other word after the times, a setting with another value (<c>line:BUY size:DRUGS</c>) and a <c>region:</c> (its id is
    /// free text) is text, so it is kept.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex SubtitleTimingLine = new(
        @"^\d{1,3}:\d{2}:\d{2}[,.]\d{1,3}\s+-->\s+\d{1,3}:\d{2}:\d{2}[,.]\d{1,3}"
        + @"(\s+(align:(start|center|middle|end|left|right)|line:-?\d+(\.\d+)?%?(,(start|center|end))?"
        + @"|position:\d+(\.\d+)?%(,(line-left|center|line-right|auto))?|size:\d+(\.\d+)?%|vertical:(rl|lr)|[XY][12]:\d+))*$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The words of a SubRip file, read by cue structure: cues are separated by blank lines; a cue is an optional index line
    /// (digits only), one time line, and the rest is text. Only those two positions are dropped, so any line in a text position
    /// is judged whatever it looks like (digits, an arrow, a time). A block with no time line in its first two lines is all text.
    /// </summary>
    internal static string SubtitlePlainText(string srt)
    {
        System.Text.StringBuilder text = new();
        List<string> block = [];

        void Flush()
        {
            if (block.Count == 0) return;
            int start = 0;
            if (block.Count > 1 && block[0].All(char.IsDigit) && SubtitleTimingLine.IsMatch(block[1])) start = 2;
            else if (SubtitleTimingLine.IsMatch(block[0])) start = 1;
            for (int i = start; i < block.Count; i++) text.AppendLine(block[i]);
            block.Clear();
        }

        foreach (string raw in srt.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) Flush();
            else block.Add(line);
        }

        Flush();
        return text.ToString().TrimEnd();
    }

    private static (ModerationContentKind? Kind, string Extension) Classify(string mimeType)
    {
        string mime = (mimeType ?? string.Empty).Trim().ToLowerInvariant();
        if (mime.StartsWith("image/", StringComparison.Ordinal))
            return (ModerationContentKind.Image, ImageExtension(mime));
        if (mime.StartsWith("video/", StringComparison.Ordinal))
            return (ModerationContentKind.Video, VideoExtension(mime));
        if (mime.StartsWith("audio/", StringComparison.Ordinal))
            return (ModerationContentKind.Audio, AudioExtension(mime));
        return (null, ".bin");
    }

    /// <summary>
    /// A neutral name for every image: ffmpeg opens a file called <c>*.jpg</c> with its image-sequence
    /// reader, which is not on the format whitelist, so every JPEG was refused. Without a known
    /// extension the file is recognised by its content (<c>jpeg_pipe</c>, <c>png_pipe</c>, ...), which is
    /// on the whitelist, and a sequence pattern can never match.
    /// </summary>
    private static string ImageExtension(string mime) => ".bin";

    private static string VideoExtension(string mime) => mime switch
    {
        "video/webm" => ".webm",
        "video/quicktime" => ".mov",
        "video/x-matroska" => ".mkv",
        "video/x-msvideo" => ".avi",
        _ => ".mp4"
    };

    private static string AudioExtension(string mime) => mime switch
    {
        "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/ogg" => ".ogg",
        "audio/flac" => ".flac",
        "audio/aac" => ".aac",
        "audio/mp4" or "audio/x-m4a" or "audio/m4a" => ".m4a",
        _ => ".audio"
    };

    private static string DescribeFailure(string what, ProcessRunResult result)
    {
        if (result.TimedOut)
            return $"ffmpeg {what} timed out.";
        string detail = (result.StdErr ?? string.Empty).Trim();
        if (detail.Length > 300)
            detail = detail[^300..];
        return $"ffmpeg {what} failed (exit {result.ExitCode}){(detail.Length == 0 ? "." : ": " + detail)}";
    }

    /// <summary>
    /// Reads an ffmpeg output only when it is within <paramref name="maxBytes"/>. The bytes came
    /// from an attacker-supplied source, so neither the WAV nor a frame is ever read whole without
    /// a hard ceiling.
    /// </summary>
    private static async Task<byte[]?> ReadBoundedAsync(string path, long maxBytes, CancellationToken ct)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length <= 0 || info.Length > maxBytes) return null;
        return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
    }
}
