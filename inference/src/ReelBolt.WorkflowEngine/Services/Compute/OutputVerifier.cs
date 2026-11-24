using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Services.Compute;

/// <inheritdoc cref="IOutputVerifier"/>
/// <summary>
/// Runner output is never trusted as delivered (docs/runner-protocol.md "Threat model", rule 2: the
/// runner binary is controlled by its owner and may be modified). Everything a runner hands back is
/// therefore treated as an untrusted claim until this class has checked it against the PLATFORM-SIDE
/// job spec:
/// <list type="number">
/// <item><b>Size</b> — a HEAD of the staged object against the job type's cap. This is the check D1
/// requires, because neither R2 nor Garage can sign a maximum length into a presigned PUT, so the
/// only place a cap can be enforced is after the upload.</item>
/// <item><b>Shape</b> — ffprobe over a presigned GET of the STAGING object, i.e. over HTTP range
/// requests rather than a download, comparing duration (± one frame, or 0.25 s), width, height,
/// codec and frame rate.</item>
/// <item><b>Integrity</b> — the engine hashes the staged object itself and compares it with the
/// digest the runner reported. The runner's value is a hint, never the answer.</item>
/// <item><b>Spot check</b> — blackdetect/freezedetect over a few short windows, on a sampled
/// percentage of <see cref="RunnerJobTarget.OrgRunner"/> outputs only.</item>
/// </list>
/// <para>
/// Only an output that passes is promoted to its final key; a failure deletes the staging object and
/// returns an <see cref="OutputVerificationResult"/> with a RUNNER_OUTPUT_* code for the caller to
/// fail the step with. Nothing here throws for a bad output — a thrown exception would look like a
/// platform fault and would be classified (and refunded) as one.
/// </para>
/// <para>
/// <b>What this cannot do.</b> Verification is a check on ONE object against the spec the platform
/// issued. It cannot tell whether the bytes are a faithful render of the workflow (a modified runner
/// that produces a well-formed, correctly-sized, correctly-timed video of the wrong content passes
/// every check here), and it cannot detect a runner that renders honestly but reports false progress.
/// The protocol says so outright, and the bound on a modified runner is the sandbox plus the fact
/// that its output is the only thing that reaches the platform.
/// </para>
/// </summary>
public sealed class OutputVerifier : IOutputVerifier
{
    /// <summary>Failure code: nothing was uploaded to the staging key.</summary>
    public const string MissingCode = "RUNNER_OUTPUT_MISSING";

    /// <summary>Failure code: the staged object is over the job type's cap.</summary>
    public const string TooLargeCode = "RUNNER_OUTPUT_TOO_LARGE";

    /// <summary>Failure code: ffprobe could not read the staged object as media.</summary>
    public const string UnreadableCode = "RUNNER_OUTPUT_UNREADABLE";

    /// <summary>Failure code: the probed duration is outside the tolerance of the spec's.</summary>
    public const string DurationMismatchCode = "RUNNER_OUTPUT_DURATION_MISMATCH";

    /// <summary>Failure code: the probed frame size differs from the spec's.</summary>
    public const string FrameSizeMismatchCode = "RUNNER_OUTPUT_FRAME_SIZE_MISMATCH";

    /// <summary>Failure code: the probed video codec differs from the spec's.</summary>
    public const string CodecMismatchCode = "RUNNER_OUTPUT_CODEC_MISMATCH";

    /// <summary>Failure code: the probed frame rate differs from the spec's.</summary>
    public const string FrameRateMismatchCode = "RUNNER_OUTPUT_FRAME_RATE_MISMATCH";

    /// <summary>Failure code: the engine's own SHA-256 of the staged object differs from the one the runner reported.</summary>
    public const string ChecksumMismatchCode = "RUNNER_OUTPUT_CHECKSUM_MISMATCH";

    /// <summary>Failure code: every sampled spot-check window was black or frozen.</summary>
    public const string BlankCode = "RUNNER_OUTPUT_BLANK";

    /// <summary>Failure code: a JSON document output did not parse as one JSON object or array, or sits outside its project.</summary>
    public const string InvalidDocumentCode = "RUNNER_OUTPUT_INVALID_DOCUMENT";

    /// <summary>Deepest nesting a JSON document output may have.</summary>
    private const int MaxJsonDepth = 64;

    /// <summary>Failure code: verification passed but the object could not be promoted.</summary>
    public const string PromoteFailedCode = "RUNNER_OUTPUT_PROMOTE_FAILED";

    private const string BlackDetectFilter = "blackdetect=d=0.5:pix_th=0.10";
    private const string FreezeDetectFilter = "freezedetect=n=-60dB:d=0.5";

    private readonly IRunnerOutputStore _store;
    private readonly IVideoToolRunner _toolRunner;
    private readonly OutputVerificationOptions _options;
    private readonly ILogger<OutputVerifier> _logger;

    public OutputVerifier(
        IRunnerOutputStore store,
        IVideoToolRunner toolRunner,
        IOptions<OutputVerificationOptions> options,
        ILogger<OutputVerifier> logger)
    {
        _store = store;
        _toolRunner = toolRunner;
        _options = options.Value;
        _logger = logger;
    }


    public async Task<OutputVerificationResult> VerifyAndPromoteAsync(RunnerOutputRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RunnerOutputSpec spec = request.Spec;
        if (spec.MaxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "A verification spec must carry a positive size cap.");

        string stagingKey = S3RunnerOutputStore.StagingKeyFor(request.JobId, request.StagingIndex);

        // --- 1. Size: the post-upload HEAD enforcement D1 requires -------------------------------
        long? size = await _store.TryGetSizeAsync(stagingKey, ct).ConfigureAwait(false);
        if (size is null)
        {
            // Nothing to delete: the object is not there. The runner was assigned the job and
            // published nothing, which is the runner's failure, not the platform's.
            return Fail(MissingCode, FailureClass.Runner,
                string.Format(CultureInfo.InvariantCulture, "The runner published no object at '{0}'.", stagingKey), 0, null);
        }

        if (size.Value > spec.MaxBytes)
        {
            await DiscardAsync(stagingKey, ct).ConfigureAwait(false);
            return Fail(TooLargeCode, FailureClass.Runner,
                string.Format(CultureInfo.InvariantCulture,
                    "The runner's output is {0} bytes, over the {1}-byte cap for this job.", size.Value, spec.MaxBytes),
                size.Value, null);
        }

        // --- 2. Shape: ffprobe the staging object over a presigned GET ---------------------------
        MediaProbeResult? probed = null;
        string? stagingUrl = null;
        bool isMedia = spec.IsMedia && !spec.IsJson && !spec.IsText;
        if (spec.IsJson || spec.IsText)
        {
            string? documentFailure = await CheckDocumentAsync(request, stagingKey, size.Value, json: spec.IsJson, ct).ConfigureAwait(false);
            if (documentFailure is not null)
            {
                await DiscardAsync(stagingKey, ct).ConfigureAwait(false);
                return Fail(InvalidDocumentCode, FailureClass.Runner, documentFailure, size.Value, null);
            }
        }

        if (isMedia)
        {
            stagingUrl = await _store.CreateStagingGetUrlAsync(
                stagingKey, TimeSpan.FromSeconds(_options.ProbeUrlTtlSeconds), ct).ConfigureAwait(false);

            probed = await ProbeAsync(stagingUrl, ct).ConfigureAwait(false);
            if (probed is null)
            {
                // The URL probe is the only check that depends on the engine host reaching the object
                // store over https. Before calling the runner a liar, read the object itself.
                (probed, string localDetail) = await ProbeDownloadedAsync(stagingKey, size.Value, spec.MaxBytes, ct).ConfigureAwait(false);
                if (probed is not null)
                {
                    // The URL is known not to work from here; the spot check would only report "no signal".
                    stagingUrl = null;
                    _logger.LogWarning(
                        "ffprobe over the presigned URL of runner job {JobId} failed but the downloaded object probed fine; accepted on the local probe",
                        request.JobId);
                }
                else
                {
                    await DiscardAsync(stagingKey, ct).ConfigureAwait(false);
                    return Fail(UnreadableCode, FailureClass.Runner,
                        "ffprobe could not read the runner's output as media (" + localDetail + ").", size.Value, null);
                }
            }

            string? shapeFailure = CompareShape(spec, probed);
            if (shapeFailure is not null)
            {
                await DiscardAsync(stagingKey, ct).ConfigureAwait(false);
                return Fail(FailureCodeForShape(spec, probed), FailureClass.Runner, shapeFailure, size.Value, null);
            }
        }

        // --- 3. Integrity: hash it ourselves ----------------------------------------------------
        string? sha256 = null;
        if (!string.IsNullOrWhiteSpace(spec.Sha256))
        {
            sha256 = await _store.TryGetSha256Async(stagingKey, size.Value, _options.MaxHashBytes, ct).ConfigureAwait(false);
            if (sha256 is null)
            {
                _logger.LogWarning(
                    "Runner output '{StagingKey}' is {SizeBytes} bytes, over the {MaxHashBytes}-byte hashing limit; the runner-reported SHA-256 could not be checked independently",
                    stagingKey, size.Value, _options.MaxHashBytes);
            }
            else if (!string.Equals(sha256, spec.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                await DiscardAsync(stagingKey, ct).ConfigureAwait(false);
                return Fail(ChecksumMismatchCode, FailureClass.Runner,
                    string.Format(CultureInfo.InvariantCulture,
                        "The runner's output hashes to {0}, not the reported {1}.", sha256, spec.Sha256.Trim()),
                    size.Value, sha256);
            }
        }
        else if (!isMedia)
        {
            // A non-media artefact has no shape to check, so the digest is the only integrity signal
            // available; compute it anyway so the result records what was actually promoted.
            sha256 = await _store.TryGetSha256Async(stagingKey, size.Value, _options.MaxHashBytes, ct).ConfigureAwait(false);
        }

        // --- 4. Spot check (sampled, OrgRunner only) --------------------------------------------
        if (probed is not null && stagingUrl is not null && ShouldSpotCheck(request))
        {
            bool blank = await SpotCheckAsync(request, stagingUrl, probed.DurationSec, ct).ConfigureAwait(false);
            if (blank)
            {
                await DiscardAsync(stagingKey, ct).ConfigureAwait(false);
                return Fail(BlankCode, FailureClass.Runner,
                    string.Format(CultureInfo.InvariantCulture,
                        "Every one of the {0} sampled windows of the runner's output was black or frozen.", _options.SpotCheckWindows),
                    size.Value, sha256);
            }
        }

        // --- 5. Promote: the object reaches its final key only now ------------------------------
        try
        {
            long promoted = await _store.PromoteAsync(stagingKey, request.FinalKey, spec.MaxBytes, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Verified runner output '{StagingKey}' ({SizeBytes} bytes, target {Target}) and promoted it to '{FinalKey}'",
                stagingKey, promoted, request.Target, request.FinalKey);
            return new OutputVerificationResult(true, null, FailureClass.None, null, promoted, sha256, request.FinalKey);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The output passed verification and the platform then failed to move it: our fault, and
            // classified as ours so the step is refunded.
            _logger.LogError(ex, "Promoting verified runner output '{StagingKey}' to '{FinalKey}' failed", stagingKey, request.FinalKey);
            return Fail(PromoteFailedCode, FailureClass.PlatformInternal,
                "The runner's output passed verification but could not be promoted: " + ex.Message, size.Value, sha256);
        }
    }


    /// <summary>
    /// Every shape comparison in one place, in the order a reader would check them. Returns null when
    /// the output matches the spec; the caller maps the failure onto a code via
    /// <see cref="FailureCodeForShape"/>, which re-derives which one it was.
    /// </summary>
    private string? CompareShape(RunnerOutputSpec spec, MediaProbeResult probed)
    {
        if (spec.DurationSeconds is { } expectedDuration)
        {
            double tolerance = DurationTolerance(spec);
            if (Math.Abs(probed.DurationSec - expectedDuration) > tolerance)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "The runner's output is {0:0.###} s, not the {1:0.###} s the job specified (tolerance {2:0.###} s).",
                    probed.DurationSec, expectedDuration, tolerance);
            }
        }

        if (spec.Width is { } width && probed.Width != width)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "The runner's output is {0} px wide, not the {1} px the job specified.", probed.Width, width);
        }

        if (spec.Height is { } height && probed.Height != height)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "The runner's output is {0} px tall, not the {1} px the job specified.", probed.Height, height);
        }

        if (!string.IsNullOrWhiteSpace(spec.VideoCodec) &&
            !string.Equals(probed.VideoCodec, spec.VideoCodec.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return string.Format(CultureInfo.InvariantCulture,
                "The runner's output is {0}, not the {1} the job specified.", probed.VideoCodec ?? "unencoded", spec.VideoCodec.Trim());
        }

        if (spec.FpsNum is { } fpsNum && spec.FpsDen is { } fpsDen && fpsDen > 0 &&
            (probed.FpsNum != fpsNum || probed.FpsDen != fpsDen))
        {
            return string.Format(CultureInfo.InvariantCulture,
                "The runner's output runs at {0}/{1} fps, not the {2}/{3} the job specified.",
                probed.FpsNum, probed.FpsDen, fpsNum, fpsDen);
        }

        return null;
    }

    /// <summary>
    /// Which shape check <see cref="CompareShape"/> failed. Re-derived rather than returned alongside
    /// the message so there is exactly one place the comparisons live; the branches are the same
    /// conditions in the same order.
    /// </summary>
    private string FailureCodeForShape(RunnerOutputSpec spec, MediaProbeResult probed)
    {
        if (spec.DurationSeconds is { } expectedDuration &&
            Math.Abs(probed.DurationSec - expectedDuration) > DurationTolerance(spec))
        {
            return DurationMismatchCode;
        }

        if ((spec.Width is { } width && probed.Width != width) ||
            (spec.Height is { } height && probed.Height != height))
        {
            return FrameSizeMismatchCode;
        }

        if (!string.IsNullOrWhiteSpace(spec.VideoCodec) &&
            !string.Equals(probed.VideoCodec, spec.VideoCodec.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return CodecMismatchCode;
        }

        return FrameRateMismatchCode;
    }

    /// <summary>One frame at the spec's rate, or the configured floor when the rate is unknown.</summary>
    private double DurationTolerance(RunnerOutputSpec spec)
    {
        if (spec.FpsNum is { } num && spec.FpsDen is { } den && num > 0 && den > 0)
            return Math.Max(_options.DurationToleranceSeconds, (double)den / num);

        return _options.DurationToleranceSeconds;
    }

    /// <summary>
    /// The protocol whitelist of the staging probe. <c>https</c> is not a transport by itself: ffmpeg
    /// opens it as <c>https → tls → tcp</c> and checks every layer against the whitelist, so the
    /// earlier <c>file,http,https</c> rejected every presigned URL with "Protocol 'tls' not on
    /// whitelist" and the verifier refused every runner-built render. <c>tcp</c> and <c>tls</c> only
    /// carry the http(s) the list already admitted; they add no scheme a hostile file could name that
    /// http/https did not.
    /// </summary>
    internal const string ProbeProtocolWhitelist = "file,http,https,tcp,tls";

    /// <summary>
    /// The demuxers the verifier will open, by ffmpeg's own names: the self-contained containers a
    /// render or a published media file can be (MP4 family, Matroska/WebM, WAV, MP3, AAC, FLAC, Ogg,
    /// AVI, MPEG-TS, JPEG and PNG). The point is what is NOT here. A runner is untrusted by
    /// construction, and with http/https/tcp/tls admitted a "render" that is really an HLS playlist
    /// (or DASH, concat, SDP) makes the engine's own ffprobe fetch whatever URL the file names —
    /// measured: one hostile <c>.m3u8</c> produced one request to a listener on the probing host.
    /// Without this list the verifier meant to contain the runner would be the thing carrying its
    /// requests into the private network.
    /// </summary>
    internal const string ProbeFormatWhitelist =
        "mov,mp4,m4a,3gp,3g2,mj2,matroska,webm,wav,mp3,aac,flac,ogg,avi,mpegts,jpeg_pipe,png_pipe";

    /// <summary>
    /// ffprobe against the staging URL. Its argv differs from FfmpegArgvBuilder.BuildProbeArgs in
    /// exactly one respect and that difference is load-bearing: the protocol whitelist admits
    /// http/https (and the tcp/tls layers beneath them, see <see cref="ProbeProtocolWhitelist"/>),
    /// because the input here IS an http URL. That is safe for the same reason the rest of
    /// this feature pins it to file — the URL is minted by the platform, points at our own bucket, and
    /// is never influenced by anything a runner sent.
    /// </summary>
    private async Task<MediaProbeResult?> ProbeAsync(string url, CancellationToken ct)
    {
        string[] args = new[]
        {
            "-v", "error",
            "-print_format", "json",
            "-show_format", "-show_streams",
            "-protocol_whitelist", ProbeProtocolWhitelist,
            "-format_whitelist", ProbeFormatWhitelist,
            url
        };

        VideoToolResult result = await _toolRunner
            .RunFfprobeAsync(args, TimeSpan.FromSeconds(_options.ProbeTimeoutSeconds), ct)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "ffprobe of the runner's staged output failed (exitCode={ExitCode}, timedOut={TimedOut}): {StdErr}",
                result.ExitCode, result.TimedOut, Truncate(RedactUrls(result.StdErr)));
            return null;
        }

        try
        {
            return FfprobeOutputParser.Parse(result.StdOut);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffprobe of the runner's staged output returned JSON this engine could not parse");
            return null;
        }
    }


    /// <summary>
    /// Sampled spot check (D12 step 3): decode a few short windows in place and flag the ones a
    /// blackdetect/freezedetect segment covers at least half of. The output fails only when EVERY
    /// sampled window is blank.
    /// <para>
    /// The windows are chosen with a PRNG seeded from the job id: deterministic for a given job (so a
    /// retry samples the same windows, and a test can pin them) but varied and unguessable across
    /// jobs, which is what stops a runner from rendering only the windows a fixed sampler would look
    /// at. A failed or timed-out spot-check pass does NOT fail the output — it is the weakest of the
    /// four checks and the three structural ones have already passed — so it degrades to "no signal".
    /// </para>
    /// </summary>
    private async Task<bool> SpotCheckAsync(RunnerOutputRequest request, string url, double durationSeconds, CancellationToken ct)
    {
        int windows = Math.Max(1, _options.SpotCheckWindows);
        double windowSeconds = Math.Max(0.1, _options.SpotCheckWindowSeconds);
        if (durationSeconds <= 0)
        {
            _logger.LogWarning("Spot check skipped for runner job {JobId}: the probed duration is unknown", request.JobId);
            return false;
        }

        var random = new Random(StableSeed(request.JobId));
        double latestStart = Math.Max(0, durationSeconds - windowSeconds);

        int blankWindows = 0;
        int evaluatedWindows = 0;
        for (int i = 0; i < windows; i++)
        {
            double start = random.NextDouble() * latestStart;
            (bool ran, bool blank) = await SpotCheckWindowAsync(url, start, windowSeconds, ct).ConfigureAwait(false);
            if (!ran)
                continue;

            evaluatedWindows++;
            if (blank)
                blankWindows++;
        }

        if (evaluatedWindows == 0)
        {
            _logger.LogWarning("Spot check for runner job {JobId} produced no usable result; the output keeps its other checks", request.JobId);
            return false;
        }

        if (blankWindows == evaluatedWindows)
        {
            _logger.LogWarning(
                "Spot check failed for runner job {JobId}: all {Windows} sampled windows were black or frozen", request.JobId, evaluatedWindows);
            return true;
        }

        return false;
    }

    /// <summary>One spot-check window. "ran" is false when ffmpeg failed, timed out, or printed nothing to judge — all of which mean "no signal", not "blank".</summary>
    private async Task<(bool Ran, bool Blank)> SpotCheckWindowAsync(string url, double start, double windowSeconds, CancellationToken ct)
    {
        string[] args = new[]
        {
            "-nostdin", "-hide_banner", "-y",
            "-loglevel", "info",
            "-ss", FfmpegArgvFormat.Number(start),
            "-t", FfmpegArgvFormat.Number(windowSeconds),
            // The same containment as the probe: this call reads the same untrusted object and used to
            // carry no whitelist at all.
            "-protocol_whitelist", ProbeProtocolWhitelist,
            "-format_whitelist", ProbeFormatWhitelist,
            "-i", url,
            "-vf", BlackDetectFilter + "," + FreezeDetectFilter,
            "-an",
            "-f", "null",
            "-"
        };

        VideoToolResult result = await _toolRunner
            .RunFfmpegAsync(args, TimeSpan.FromSeconds(_options.SpotCheckTimeoutSeconds), ct)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "Spot-check window at {Start}s of the runner's staged output failed (exitCode={ExitCode}, timedOut={TimedOut})",
                start, result.ExitCode, result.TimedOut);
            return (false, false);
        }

        // A window counts as blank when the detector reported a segment covering at least half of it.
        // The detectors' own minimum duration is what makes any reported segment meaningful at all.
        double minimumBlankSeconds = windowSeconds / 2;
        bool blank =
            ReportsLongEnoughSegment(result.StdErr, "black_duration:", minimumBlankSeconds) ||
            ReportsLongEnoughSegment(result.StdErr, "freeze_duration:", minimumBlankSeconds);

        // Some ffmpeg builds report the START of a segment at EOF without ever writing its end, so a
        // window that begins mid-black would otherwise look clean. The start marker only counts when it
        // sits at the very beginning of the window, where there is nothing before it to have ended.
        if (!blank)
        {
            blank =
                ReportsSegmentAtWindowStart(result.StdErr, "black_start:") ||
                ReportsSegmentAtWindowStart(result.StdErr, "freeze_start:");
        }

        return (true, blank);
    }

    private static bool ReportsLongEnoughSegment(string stderr, string marker, double minimumSeconds)
    {
        foreach (double duration in ReadNumbersAfter(stderr, marker))
        {
            if (duration >= minimumSeconds)
                return true;
        }

        return false;
    }

    private static bool ReportsSegmentAtWindowStart(string stderr, string marker)
    {
        foreach (double start in ReadNumbersAfter(stderr, marker))
        {
            if (start <= 0.05)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Every number following <paramref name="marker"/> anywhere in <paramref name="text"/>, without a
    /// regex (the digits are walked by hand). Deliberately tolerant: ffmpeg's own log formatting is not
    /// a contract, so anything unparseable is skipped rather than throwing.
    /// </summary>
    private static IEnumerable<double> ReadNumbersAfter(string text, string marker)
    {
        int index = text.IndexOf(marker, StringComparison.Ordinal);
        while (index >= 0)
        {
            int cursor = index + marker.Length;
            int start = cursor;
            while (cursor < text.Length && (char.IsDigit(text[cursor]) || text[cursor] is '.' or '-' or '+'))
                cursor++;

            if (cursor > start &&
                double.TryParse(text[start..cursor], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                yield return value;
            }

            index = text.IndexOf(marker, cursor, StringComparison.Ordinal);
        }
    }

    private bool ShouldSpotCheck(RunnerOutputRequest request)
    {
        if (request.Target != RunnerJobTarget.OrgRunner)
            return false;

        int percent = Math.Clamp(_options.SpotCheckPercent, 0, 100);
        if (percent <= 0)
            return false;
        if (percent >= 100)
            return true;

        return StableBucket(request.JobId) < percent;
    }

    /// <summary>0-99, derived from the job id's own bytes — stable across processes and releases, unlike a string hash code.</summary>
    private static int StableBucket(Guid jobId) => (int)(BitConverter.ToUInt32(jobId.ToByteArray(), 0) % 100u);

    private static int StableSeed(Guid jobId)
    {
        byte[] bytes = jobId.ToByteArray();
        return (int)(BitConverter.ToUInt32(bytes, 0) ^ BitConverter.ToUInt32(bytes, 4));
    }

    private async Task DiscardAsync(string stagingKey, CancellationToken ct)
    {
        try
        {
            await _store.DiscardAsync(stagingKey, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort: the object is unreferenced and the lifecycle rule on runner-staging/
            // expires it anyway. Failing here would replace a precise verification failure with a
            // storage error.
            _logger.LogWarning(ex, "Could not delete the rejected staging object '{StagingKey}'", stagingKey);
        }
    }

    private static OutputVerificationResult Fail(
        string code, FailureClass failureClass, string message, long sizeBytes, string? sha256) =>
        new(false, code, failureClass, message, sizeBytes, sha256, null);

    private static string Truncate(string value, int max = 2000) => value.Length <= max ? value : value[^max..];

    /// <summary>
    /// The document check for a JSON output: the final key must sit under the request's own project,
    /// and the staged bytes must parse as exactly one JSON object or array within a nesting bound.
    /// Returns the reason it did not, or null. The content is read, never kept or logged.
    /// </summary>
    private async Task<string?> CheckDocumentAsync(RunnerOutputRequest request, string stagingKey, long size, bool json, CancellationToken ct)
    {
        string prefix = string.Format(CultureInfo.InvariantCulture, "projects/{0:D}/", request.ProjectId);
        if (!request.FinalKey.StartsWith(prefix, StringComparison.Ordinal) || request.FinalKey.Contains("..", StringComparison.Ordinal))
            return "The document's destination is outside the job's project.";
        if (size == 0)
            return "The document is empty.";

        long cap = _options.MaxHashBytes > 0 ? Math.Min(request.Spec.MaxBytes, _options.MaxHashBytes) : request.Spec.MaxBytes;
        // Captions and sidecars are small; a text document gets a much lower cap than a JSON one, checked from
        // the object's reported size before anything is downloaded.
        if (!json)
            cap = Math.Min(cap, MaxTextDocumentBytes);
        if (size > cap)
            return string.Format(CultureInfo.InvariantCulture, "The document is {0} bytes, too large to validate.", size);

        string path = Path.Combine(Path.GetTempPath(), "rb-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            await SecondLook.WaitAsync(ct).ConfigureAwait(false);
            byte[]? head;
            try
            {
                head = await _store.TryDownloadAsync(stagingKey, path, cap, ct).ConfigureAwait(false);
            }
            finally
            {
                SecondLook.Release();
            }
            if (head is null)
                return "The document could not be downloaded for validation.";

            // Streaming validation: memory stays bounded by one chunk (text) or one token (JSON), not by the document.
            return json ? ValidateJsonFile(path) : ValidateTextFile(path);
        }
        catch (System.Text.Json.JsonException)
        {
            return "The runner's output is not valid JSON.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "The document could not be read for validation.";
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Largest text document (SRT, WebVTT, captions) the verifier accepts: 16 MiB.</summary>
    internal const long MaxTextDocumentBytes = 16L * 1024 * 1024;

    /// <summary>
    /// A text sidecar (SRT, WebVTT) has no shape to parse: it must decode as strict UTF-8 and carry no NUL
    /// byte. Read in 64 KiB chunks through a streaming decoder (a multi-byte sequence split across chunks is
    /// carried over), so memory stays bounded whatever the file size; the size cap is enforced by the caller
    /// and again here from the file length. Returns the reason it did not pass, or null.
    /// </summary>
    internal static string? ValidateTextFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length > MaxTextDocumentBytes)
            return "The text document is too large to validate.";

        System.Text.Decoder decoder = new System.Text.UTF8Encoding(false, true).GetDecoder();
        byte[] buffer = new byte[64 * 1024];
        char[] chars = new char[System.Text.Encoding.UTF8.GetMaxCharCount(buffer.Length) + 1];
        try
        {
            while (true)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                bool final = read == 0;
                if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0)
                    return "The text document contains binary data.";
                _ = decoder.GetChars(buffer, 0, read, chars, 0, flush: final);
                if (final)
                    return null;
            }
        }
        catch (System.Text.DecoderFallbackException)
        {
            return "The text document is not valid UTF-8.";
        }
    }

    /// <summary>Largest single JSON token (a string, say) the streaming check will buffer.</summary>
    private const int MaxJsonTokenBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Reads the file in blocks through <see cref="System.Text.Json.Utf8JsonReader"/> and requires one
    /// object or array and nothing after it. Throws <see cref="System.Text.Json.JsonException"/> for bad JSON.
    /// </summary>
    internal static string? ValidateJsonFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] buffer = new byte[64 * 1024];
        int length = 0;
        bool checkedBom = false;
        System.Text.Json.JsonReaderState state = new(new System.Text.Json.JsonReaderOptions { MaxDepth = MaxJsonDepth });
        System.Text.Json.JsonTokenType root = System.Text.Json.JsonTokenType.None;

        while (true)
        {
            if (length == buffer.Length)
            {
                if (buffer.Length >= MaxJsonTokenBytes)
                    return "The document holds a single value too large to validate.";
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            int read = stream.Read(buffer, length, buffer.Length - length);
            bool final = read == 0;
            length += read;

            if (!checkedBom && (length >= 3 || final))
            {
                checkedBom = true;
                if (length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                {
                    Buffer.BlockCopy(buffer, 3, buffer, 0, length - 3);
                    length -= 3;
                }
            }
            if (!checkedBom)
                continue;

            System.Text.Json.Utf8JsonReader reader = new(buffer.AsSpan(0, length), final, state);
            while (reader.Read())
            {
                if (root == System.Text.Json.JsonTokenType.None)
                    root = reader.TokenType;
            }
            state = reader.CurrentState;
            int consumed = (int)reader.BytesConsumed;
            if (final)
                break;
            length -= consumed;
            Buffer.BlockCopy(buffer, consumed, buffer, 0, length);
        }

        return root is System.Text.Json.JsonTokenType.StartObject or System.Text.Json.JsonTokenType.StartArray
            ? null
            : "The document is not a JSON object or array.";
    }

    /// <summary>
    /// Downloads the staged object (bounded by the job's cap) and probes it as a local file with only the
    /// <c>file</c> protocol. Returns the probe, or null plus a short, URL-free description (size, leading
    /// bytes, ffprobe's complaint) for the failure message and the log.
    /// </summary>
    private async Task<(MediaProbeResult? Probe, string Detail)> ProbeDownloadedAsync(
        string stagingKey, long size, long maxBytes, CancellationToken ct)
    {
        string path = Path.Combine(Path.GetTempPath(), "rb-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Bounded by the hashing limit as well as the job cap, and one second look at a time, so a
            // hostile runner cannot turn the fallback into a disk-filling download on the engine host.
            long cap = _options.MaxHashBytes > 0 ? Math.Min(maxBytes, _options.MaxHashBytes) : maxBytes;
            if (size > cap)
                return (null, string.Format(CultureInfo.InvariantCulture, "{0} bytes, too large for a second look", size));

            await SecondLook.WaitAsync(ct).ConfigureAwait(false);
            byte[]? head;
            try
            {
                head = await _store.TryDownloadAsync(stagingKey, path, cap, ct).ConfigureAwait(false);
            }
            finally
            {
                SecondLook.Release();
            }
            if (head is null)
                return (null, string.Format(CultureInfo.InvariantCulture, "{0} bytes, not downloadable for a second look", size));

            string hex = Convert.ToHexStringLower(head);
            if (size == 0)
                return (null, "the object is empty");

            VideoToolResult result = await _toolRunner.RunFfprobeAsync(
                new[]
                {
                    "-v", "error", "-print_format", "json", "-show_format", "-show_streams",
                    "-protocol_whitelist", "file", "-format_whitelist", ProbeFormatWhitelist, path
                },
                TimeSpan.FromSeconds(_options.ProbeTimeoutSeconds), ct).ConfigureAwait(false);

            if (result.Succeeded)
            {
                try { return (FfprobeOutputParser.Parse(result.StdOut), string.Empty); }
                catch (Exception ex) { _logger.LogWarning(ex, "ffprobe of the downloaded staged output returned JSON this engine could not parse"); }
            }

            string complaint = Truncate(RedactUrls(result.StdErr.Replace(path, "[object]")), 300).Trim();
            _logger.LogWarning(
                "ffprobe of the downloaded staged output '{StagingKey}' failed (size={SizeBytes}, firstBytes={FirstBytes}, exitCode={ExitCode}): {StdErr}",
                stagingKey, size, hex, result.ExitCode, complaint);
            return (null, string.Format(CultureInfo.InvariantCulture, "{0} bytes, starts with {1}: {2}", size, hex, complaint));
        }
        finally
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not delete verification scratch file");
            }
        }
    }

    private static readonly SemaphoreSlim SecondLook = new(1, 1);

    private static readonly System.Text.RegularExpressions.Regex UrlPattern = new(
        @"https?://[^\s'""]*[^\s'"":,;.)]", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// ffprobe/ffmpeg echo the input URL in their errors. A presigned URL carries its signature in the
    /// query string and names the account host, so the whole URL is replaced before the text is logged.
    /// </summary>
    internal static string RedactUrls(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : UrlPattern.Replace(text, "[staging url]");
}
