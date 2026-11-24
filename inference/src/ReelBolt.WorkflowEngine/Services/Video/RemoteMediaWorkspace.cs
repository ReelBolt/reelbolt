
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// D11's remote implementation of <see cref="IMediaWorkspace"/>: the media pipeline runs on a customer's
/// paired machine (or a pool worker) and this object is the engine-side half of the conversation.
/// Every method is one op of <c>docs/runner-protocol.md</c> section "Media session ops and argv
/// validation", sent through <see cref="RunnerGatewayClient.RunSessionOpAsync"/>, so this is a
/// transport and not a redesign of the pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>The argv contract.</b> <see cref="MaterializeAsync"/> returns the VIRTUAL path unchanged - the
/// interface's own rule for a remote workspace - because the worker resolves it inside the session
/// directory it created. Every string in an argv handed back to this object is therefore a
/// session-relative name, which is also exactly what the worker's validator demands: an absolute
/// path, a path containing ".." or a path that resolves outside the session directory is
/// <c>argv_not_allowed</c>. Argv is checked on the way OUT, before it is sent, so a caller that still
/// builds argv from a host path fails here with a legible error rather than on the runner.
/// </para>
/// <para>
/// <b>Writes are staged, never dropped.</b> The media-session op set has no write op, so
/// <see cref="WriteBytesAsync"/> takes the one road the op set does offer: the engine uploads the
/// bytes as a bare artifact in the project's own key space, presigns a GET of it, and issues
/// <c>stage</c>. That is the documented op set used exactly as written, it needs no change to the
/// worker or to the protocol, and it leaves the file in the session at the name the filtergraph asks
/// for. The cost is honest and stated: two extra round trips and one small object per written file
/// (see <see cref="WriteKeyPrefix"/>). The alternative - a <c>writeFile</c> op added to the media op
/// set - is a protocol change to D4b's op table and is deliberately NOT made here.
/// </para>
/// <para>
/// <b>Publishes are verified.</b> <see cref="PublishAsync"/> PUTs to <c>runner-staging/{jobId}/{n}</c>
/// and hands the result to D12's <see cref="IOutputVerifier"/>, so an object produced by a customer's
/// machine reaches its final key only after the platform has checked it. That is what makes D11 the
/// caller that activates D12.
/// </para>
/// <para>
/// <b>Nothing here falls back to local.</b> A lost session throws. Running the step on this host after
/// a runner dropped it would double-encode, bypass verification and bill the wrong party, so the
/// failure is surfaced instead - the caller fails the step.
/// </para>
/// </remarks>
public sealed class RemoteMediaWorkspace : IMediaWorkspace
{
    /// <summary>
    /// Where a written session file parks in the project's object store between the engine and the
    /// runner: <c>projects/{projectId}/agentFiles/media-session/{jobId}/{name}</c>. Deliberately under
    /// <c>agentFiles</c> (never <c>outputFiles</c>, which <c>OutputsController</c> treats as playable)
    /// and uploaded through <c>UploadArtifactAsync</c>, which creates NO <c>ProjectFile</c> row - so a
    /// session's slot files never appear in the project's file list.
    /// </summary>
    public const string WriteKeyPrefix = "agentFiles/media-session";

    /// <summary>The virtual root this workspace reports, for diagnostics. Not a filesystem path.</summary>
    private const string SessionRoot = "/media-session";

    private readonly RunnerGatewayClient _gateway;
    private readonly IProjectFileWorkspace _projectFiles;
    private readonly IPresignedUrlService _presignedUrls;
    private readonly IOutputVerifier _verifier;
    private readonly IRunnerOutputStore _outputStore;
    private readonly RemoteMediaOptions _options;
    private readonly ILogger _logger;
    private readonly Guid _projectId;
    private readonly Guid _jobId;
    private readonly Guid _sessionId;
    private readonly RunnerJobTarget _target;

    // One op at a time per session: the gateway answers a second concurrent op 409 op_in_flight.
    private readonly SemaphoreSlim _opGate = new(1, 1);
    private int _opCount;
    private int _stagingIndex;
    private int _writeIndex;

    public RemoteMediaWorkspace(
        Guid projectId,
        Guid jobId,
        Guid sessionId,
        RunnerJobTarget target,
        RunnerGatewayClient gateway,
        IProjectFileWorkspace projectFiles,
        IPresignedUrlService presignedUrls,
        IOutputVerifier verifier,
        IRunnerOutputStore outputStore,
        RemoteMediaOptions options,
        ILogger logger)
    {
        _projectId = projectId;
        _jobId = jobId;
        _sessionId = sessionId;
        _target = target;
        _gateway = gateway;
        _projectFiles = projectFiles;
        _presignedUrls = presignedUrls;
        _verifier = verifier;
        _outputStore = outputStore;
        _options = options;
        _logger = logger;
        PublishSpec = new RunnerOutputSpec(MaxBytes: options.MaxPublishBytes);
    }

    /// <inheritdoc />
    public bool IsRemoteSession => true;

    /// <summary>How many ops this workspace has run. The plan's "op count per compile is logged" reads this.</summary>
    public int OpCount => Volatile.Read(ref _opCount);

    /// <summary>The gateway's session id, for the log line that reports the count.</summary>
    public Guid SessionId => _sessionId;

    /// <summary>
    /// The spec a publish is verified against. It starts as the size cap alone - the one expectation
    /// the platform always has, and the only cap that can be enforced at all after a presigned PUT -
    /// so a caller with a better spec (a known duration, frame size, codec or digest) narrows it by
    /// assigning one before the publish it cares about.
    /// </summary>
    public RunnerOutputSpec PublishSpec { get; set; }

    /// <inheritdoc/>
    public string RootPath => SessionRoot;

    public Task StageAsync(string storageKey, string virtualPath, CancellationToken ct) =>
        StageAsync(storageKey, virtualPath, _options.EffectiveMaxStageBytes, ct);

    /// <inheritdoc />
    /// <remarks>The worker's stage op enforces <c>maxBytes</c> while it copies; the smaller of the caller's cap and the configured one is sent.</remarks>
    public async Task StageAsync(string storageKey, string virtualPath, long maxBytes, CancellationToken ct)
    {
        string url = await _presignedUrls.CreateGetAsync(_projectId, storageKey, _options.PresignGetTtl).ConfigureAwait(false);

        RunnerSessionOpResult op = await RunAsync(
            MediaSessionOps.Stage,
            new JsonObject
            {
                ["url"] = url,
                ["path"] = RequireVirtualPath(virtualPath),
                ["maxBytes"] = Math.Min(maxBytes, _options.EffectiveMaxStageBytes)
            },
            timeout: null,
            ct).ConfigureAwait(false);

        long cap = Math.Min(maxBytes, _options.EffectiveMaxStageBytes);
        if (!op.Ok && string.Equals(op.ErrorCode, "file_too_large", StringComparison.OrdinalIgnoreCase))
            throw new MediaStageLimitExceededException(virtualPath, cap);
        RequireOk(op, "stage '" + virtualPath + "'");
        _logger.LogDebug(
            "media session: staged {StorageKey} into session {SessionId} at {VirtualPath} ({OpId})",
            storageKey, _sessionId, virtualPath, op.OpId);
    }

    public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct) =>
        RunFfmpegAsync(argv, timeout, ct, onStdOutLine: null);

    public async Task<VideoToolResult> RunFfmpegAsync(
        IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine)
    {
        ValidateArgv(argv, MediaSessionOps.Ffmpeg);

        RunnerSessionOpResult op = await RunAsync(
            MediaSessionOps.Ffmpeg,
            new JsonObject
            {
                ["argv"] = ToArgvArray(argv),
                ["timeoutSec"] = Seconds(timeout)
            },
            timeout,
            ct).ConfigureAwait(false);

        // A worker-level refusal of the op is not an exit code: argv_not_allowed, a path outside the
        // session, an exhausted disk. It maps onto the same "the process did not produce a result"
        // answer the local runner gives for a killed process, carrying the worker's own detail, so the
        // executor's existing exit-code handling keeps working - except for cancellation, which stays
        // a cancellation.
        if (!op.Ok)
        {
            return FromOpError(op);
        }

        JsonNode result = op.RequireResult();
        int exitCode = ReadInt(result, "exitCode", -1);
        string stderrTail = ReadString(result, "stderrTail") ?? string.Empty;

        // The protocol's ffmpeg op reports progress through 'progress' messages and returns only the
        // TAIL of stderr, so a remote run cannot replay every stdout line. The callback is therefore
        // fed the tail once, at the end: a caller that parses '-progress pipe:1' key=value lines still
        // sees the last block it produced and still reports a real percentage, and no caller is left
        // believing output was streamed when it was not.
        if (onStdOutLine is not null && stderrTail.Length > 0)
        {
            foreach (string line in stderrTail.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                onStdOutLine(line);
            }
        }

        return new VideoToolResult(exitCode, StdOut: string.Empty, StdErr: stderrTail, TimedOut: false);
    }

    public async Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct)
    {
        ValidateArgv(argv, MediaSessionOps.Ffprobe);

        RunnerSessionOpResult op = await RunAsync(
            MediaSessionOps.Ffprobe,
            new JsonObject
            {
                ["argv"] = ToArgvArray(argv),
                ["timeoutSec"] = Seconds(timeout)
            },
            timeout,
            ct).ConfigureAwait(false);

        if (!op.Ok)
        {
            return FromOpError(op);
        }

        JsonNode result = op.RequireResult();
        return new VideoToolResult(
            ExitCode: ReadInt(result, "exitCode", -1),
            StdOut: ReadString(result, "stdout") ?? string.Empty,
            StdErr: ReadString(result, "stderrTail") ?? string.Empty,
            TimedOut: false);
    }

    /// <summary>
    /// The virtual path itself: a remote worker resolves session-relative names inside the session
    /// directory it created, so there is no host path to hand back and none is invented.
    /// </summary>
    public Task<string> MaterializeAsync(string virtualPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(RequireVirtualPath(virtualPath));
    }

    public Task<byte[]> ReadAsync(string virtualPath, CancellationToken ct)
        => ReadAsync(virtualPath, MediaSessionOps.MaxEngineReadBytes, ct);

    public async Task<byte[]> ReadAsync(string virtualPath, int maxBytes, CancellationToken ct)
    {
        string path = RequireVirtualPath(virtualPath);

        // Inline arm: the protocol caps 'read' at 4 MiB, and a caller that asked for less than that
        // gets exactly the truncation refusal it would get locally (the worker answers
        // file_too_large, which is mapped onto MediaWorkspaceFileTooLargeException below).
        bool inline = maxBytes <= MediaSessionOps.SafeInlineReadBytes;
        int callerMaxBytes = maxBytes;
        bool statRouted = false;
        if (!inline)
        {
            // The caller's cap is above what one frame can carry as base64, so the size decides:
            // stat first, small files still go inline (capped at the safe size), the rest via storage.
            MediaWorkspaceStat stat = await StatAsync(path, ct).ConfigureAwait(false);
            if (!stat.Exists)
            {
                inline = true; // let the worker's own missing-file error surface through the read
                maxBytes = MediaSessionOps.SafeInlineReadBytes;
            }
            else if (stat.Bytes <= MediaSessionOps.SafeInlineReadBytes)
            {
                inline = true;
                statRouted = true;
                maxBytes = MediaSessionOps.SafeInlineReadBytes;
            }
            else if (stat.Bytes > maxBytes)
            {
                throw new MediaWorkspaceFileTooLargeException(path, stat.Bytes, maxBytes);
            }
        }

        if (inline)
        {
            RunnerSessionOpResult op = await RunAsync(
                MediaSessionOps.Read,
                new JsonObject { ["path"] = path, ["maxBytes"] = maxBytes },
                timeout: null,
                ct).ConfigureAwait(false);

            if (!op.Ok)
            {
                Exception failure = ReadFailure(op, path, maxBytes);

                // The stat said the file fits the inline cap, but the worker refused it as too large:
                // escape inflation of text (an "<a>"-style file grows past the frame) or growth between
                // stat and read. The CALLER's cap still allows it, so the object-storage arm decides.
                if (statRouted && failure is MediaWorkspaceFileTooLargeException)
                {
                    return await ReadOverObjectStorageAsync(path, callerMaxBytes, ct).ConfigureAwait(false);
                }

                throw failure;
            }

            JsonNode result = op.RequireResult();
            string content = ReadString(result, "content") ?? string.Empty;
            string encoding = ReadString(result, "encoding") ?? "utf8";
            byte[] bytes = string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(content)
                : Encoding.UTF8.GetBytes(content);
            return bytes;
        }

        // Larger arm, straight out of the op table: "larger content is read by publish then fetched
        // from object storage by the engine". The object never travels through the gateway socket.
        return await ReadOverObjectStorageAsync(path, maxBytes, ct).ConfigureAwait(false);
    }

    public async Task<MediaWorkspaceStat> StatAsync(string virtualPath, CancellationToken ct)
    {
        RunnerSessionOpResult op = await RunAsync(
            MediaSessionOps.Stat,
            new JsonObject { ["path"] = RequireVirtualPath(virtualPath) },
            timeout: null,
            ct).ConfigureAwait(false);

        // A stat of a path that is not there is an ANSWER, not a failure: the local workspace returns
        // Exists=false and callers branch on it. A worker that reports the missing file as an error is
        // read the same way.
        if (!op.Ok)
        {
            if (IsMissingFile(op))
            {
                return new MediaWorkspaceStat(Exists: false, Bytes: 0);
            }

            throw OpFailure(op, "stat '" + virtualPath + "'");
        }

        JsonNode result = op.RequireResult();
        return new MediaWorkspaceStat(
            Exists: ReadBool(result, "exists"),
            Bytes: ReadLong(result, "bytes", 0));
    }

    /// <summary>
    /// 'publish', then D12 verification, then promotion - in that order and with no shortcut. The
    /// object lands on <c>runner-staging/{jobId}/{n}</c> and reaches <paramref name="finalStorageKey"/>
    /// only if <see cref="IOutputVerifier"/> accepts it.
    /// </summary>
    public async Task<string> PublishAsync(
        string virtualPath, string finalStorageKey, string contentType, CancellationToken ct)
    {
        string path = RequireVirtualPath(virtualPath);
        int index = Interlocked.Increment(ref _stagingIndex);

        PresignedPut put = await _presignedUrls
            .CreatePutAsync(_jobId, index, contentType, _options.PresignPutTtl)
            .ConfigureAwait(false);

        RunnerSessionOpResult op = await RunAsync(
            MediaSessionOps.Publish,
            new JsonObject
            {
                ["path"] = path,
                ["putUrl"] = put.Url,
                ["maxBytes"] = _options.MaxPublishBytes,
                ["contentType"] = contentType
            },
            timeout: null,
            ct).ConfigureAwait(false);

        RequireOk(op, "publish '" + virtualPath + "'");

        RunnerOutputSpec spec = SpecForContentType(PublishSpec, contentType);
        string? runnerDigest = ReadString(op.RequireResult(), "sha256");
        if (!string.IsNullOrWhiteSpace(runnerDigest))
        {
            // The runner's digest is a CLAIM; the verifier hashes the object itself and compares. It is
            // only recorded here so the comparison is possible at all.
            spec = spec with { Sha256 = runnerDigest };
        }

        OutputVerificationResult verification = await _verifier.VerifyAndPromoteAsync(
            new RunnerOutputRequest(_projectId, _jobId, index, finalStorageKey, _target, spec),
            ct).ConfigureAwait(false);

        if (!verification.Verified)
        {
            throw new RunnerOutputRejectedException(
                verification.FailureCode ?? "RUNNER_OUTPUT_UNVERIFIED",
                verification.FailureClass,
                verification.Message ?? "the runner's output did not pass verification");
        }

        _logger.LogInformation(
            "media session: published {VirtualPath} to {FinalKey} ({SizeBytes} bytes, target {Target}) after verification",
            virtualPath, verification.FinalKey, verification.SizeBytes, _target);
        return verification.FinalKey ?? finalStorageKey;
    }

    /// <summary>
    /// What the platform expects of the object, from the content type IT chose for the publish (never
    /// the runner's claim): JSON is a document, validated as JSON; other text is a document with no
    /// shape to check; video, audio and images (and anything unrecognised) keep the media check.
    /// </summary>
    internal static RunnerOutputSpec SpecForContentType(RunnerOutputSpec spec, string contentType)
    {
        string type = (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        if (type == "application/json" || type.EndsWith("+json", StringComparison.Ordinal))
            return spec with { IsMedia = false, IsJson = true };
        if (IsTextContentType(type))
            return spec with { IsMedia = false, IsText = true };
        return spec;
    }

    /// <summary>Text documents the engine publishes (captions among them): never probed as media, checked as text.</summary>
    internal static bool IsTextContentType(string type)
        => type.StartsWith("text/", StringComparison.Ordinal)
           || type is "application/x-subrip" or "application/x-subtitle" or "application/ttml+xml";

    /// <inheritdoc/>
    /// <remarks>
    /// The document path. On this side the object crossed a machine boundary the engine does not
    /// control, so it is verified and promoted exactly as a published program is - the difference
    /// between this and <see cref="PublishAsync"/> is on the LOCAL arm, where there is no boundary to
    /// verify across and the caller has always taken the object store's returned key at face value.
    /// </remarks>
    public Task<string> UploadArtifactAsync(
        string virtualPath, string finalStorageKey, string contentType, CancellationToken ct)
        => PublishAsync(virtualPath, finalStorageKey, contentType, ct);

    /// <inheritdoc cref="IMediaWorkspace.WriteBytesAsync"/>
    /// <remarks>
    /// The engine side of a write is the mirror image of 'publish': upload the bytes as a bare
    /// artifact, presign a GET, and let the worker's 'stage' op place them in the session under the
    /// name the filtergraph will ask for. The object key is derived from the job and the virtual path,
    /// so the same write in a retried attempt addresses the same object.
    /// </remarks>
    public async Task WriteBytesAsync(string virtualPath, byte[] content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        string path = RequireVirtualPath(virtualPath);

        string key = WriteKeyFor(path);
        string directory = Path.Combine(Path.GetTempPath(), "reelbolt-media-session", _sessionId.ToString("D"));
        Directory.CreateDirectory(directory);
        string localPath = Path.Combine(
            directory,
            Interlocked.Increment(ref _writeIndex).ToString(CultureInfo.InvariantCulture) + "-" + Path.GetFileName(path));

        try
        {
            await File.WriteAllBytesAsync(localPath, content, ct).ConfigureAwait(false);
            // Stage the key the store reports, never one rebuilt here: the real key carries the
            // category segment (WriteKeyPrefix) that WriteKeyFor leaves out, and a rebuilt key
            // without it names an object that does not exist (the runner then sees a 404).
            string storedKey = await _projectFiles
                .UploadArtifactAsync(_projectId, localPath, key, "application/octet-stream", ct, WriteKeyPrefix)
                .ConfigureAwait(false);

            await StageAsync(storedKey, path, ct).ConfigureAwait(false);
        }
        finally
        {
            // The engine's copy is a transport buffer, not state: the session has the bytes and the
            // object store has the object, so nothing is lost by removing it here.
            TryDelete(localPath);
        }
    }

    public Task WriteTextAsync(string virtualPath, string content, Encoding encoding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(encoding);

        byte[] preamble = encoding.GetPreamble();
        byte[] body = encoding.GetBytes(content);
        return WriteBytesAsync(virtualPath, preamble.Length == 0 ? body : [.. preamble, .. body], ct);
    }

    /// <summary>
    /// The object key a written session file parks under, relative to
    /// <see cref="WriteKeyPrefix"/>. Derived from the job and the virtual path only.
    /// </summary>
    public string WriteKeyFor(string virtualPath)
    {
        string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(virtualPath)))[..16];
        string leaf = Path.GetFileName(RequireVirtualPath(virtualPath));
        return _jobId.ToString("D") + "/" + digest + "-" + leaf;
    }

    /// <summary>
    /// The 'close' op: destroy the session directory on the runner. Best effort by contract - the
    /// gateway also closes a session on execution end, on timeout and on idle, so a failure here is
    /// logged and never propagated into a step result.
    /// </summary>
    public async Task CloseAsync(CancellationToken ct)
    {
        try
        {
            RunnerSessionOpResult op = await RunAsync(MediaSessionOps.Close, new JsonObject(), timeout: null, ct).ConfigureAwait(false);
            if (!op.Ok)
            {
                _logger.LogDebug("media session {SessionId}: close op reported {Code}", _sessionId, op.ErrorCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "media session {SessionId}: close op failed; the gateway will reap it", _sessionId);
        }
    }

    /// <summary>
    /// One op, under three rules.
    /// <para>
    /// <b>Never two ops at once.</b> The gateway allows one op in flight per session and answers a second
    /// <c>409 op_in_flight</c>, so this workspace serialises its own ops (<see cref="_opGate"/>); a caller that
    /// issues two concurrently queues the second instead of failing it.
    /// </para>
    /// <para>
    /// <b>Always a deadline.</b> An op the caller gave no timeout gets <see cref="RemoteMediaOptions.EffectiveOpTimeout"/>,
    /// sent explicitly, so the gateway answers <c>504 op_timeout</c> at that deadline and the HTTP request outlasts
    /// it. (It used to get the 30 s request timeout: a long <c>stage</c> was abandoned client-side while the
    /// worker kept running it, and the next op was <c>409 op_in_flight</c>.)
    /// </para>
    /// <para>
    /// <b>An op in flight is waited for, not abandoned.</b> '409 op_in_flight' and the engine's own request
    /// timing out mean the gateway or the worker still has THIS op, so the same <c>opId</c> is asked again every
    /// <see cref="RemoteMediaOptions.OpPollIntervalMilliseconds"/> until it answers (the worker replays a
    /// repeated opId from its 256-entry cache instead of executing it twice) or the op's deadline plus
    /// <see cref="RemoteMediaOptions.OpSettleGraceSeconds"/> passes, which ends it with the last refusal. A
    /// '504 op_timeout' or a lost replica is retried once, as the protocol says. Every other refusal is
    /// returned as it is, because a retry cannot fix a lost session or a refused argv.
    /// </para>
    /// </summary>
    private async Task<RunnerSessionOpResult> RunAsync(
        string op, JsonObject args, TimeSpan? timeout, CancellationToken ct)
    {
        Interlocked.Increment(ref _opCount);
        string opId = Guid.NewGuid().ToString("N");
        bool smallOp = op is MediaSessionOps.Stat or MediaSessionOps.Read or MediaSessionOps.Close;
        TimeSpan opTimeout = timeout is { } t && t > TimeSpan.Zero
            ? t
            : smallOp ? _options.EffectiveSmallOpTimeout : _options.EffectiveOpTimeout;
        TimeSpan pollInterval = TimeSpan.FromMilliseconds(Math.Max(1, _options.OpPollIntervalMilliseconds));

        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        System.Diagnostics.Stopwatch opWatch = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation(
            "media session {SessionId}: op {Op} ({OpId}) started, deadline {Seconds:0}s",
            _sessionId, op, opId, opTimeout.TotalSeconds);
        try
        {
            DateTime giveUpAt = DateTime.UtcNow + opTimeout + TimeSpan.FromSeconds(Math.Max(0, _options.OpSettleGraceSeconds));
            bool retriedOnce = false;
            while (true)
            {
                try
                {
                    return await _gateway
                        .RunSessionOpAsync(_sessionId, op, args, opTimeout, opId, ct)
                        .ConfigureAwait(false);
                }
                catch (RunnerSessionOpException ex) when (ex.Retryable && !ex.SessionLost && !ct.IsCancellationRequested)
                {
                    bool stillRunning = ex.Code is SessionOpErrorCodes.OpInFlight or SessionOpErrorCodes.GatewayTimeout;
                    if (!stillRunning && retriedOnce)
                        throw;
                    if (stillRunning && DateTime.UtcNow >= giveUpAt)
                    {
                        _logger.LogWarning(
                            "media session {SessionId}: op {Op} ({OpId}) was still {Code} {Seconds:0}s past its deadline; giving up",
                            _sessionId, op, opId, ex.Code, _options.OpSettleGraceSeconds);
                        throw;
                    }

                    retriedOnce |= !stillRunning;
                    _logger.LogDebug(
                        "media session {SessionId}: op {Op} returned {Code}; asking again under opId {OpId}",
                        _sessionId, op, ex.Code, opId);

                    if (stillRunning)
                        await Task.Delay(pollInterval, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _logger.LogInformation(
                "media session {SessionId}: op {Op} ({OpId}) finished after {Millis} ms",
                _sessionId, op, opId, opWatch.ElapsedMilliseconds);
            _opGate.Release();
        }
    }

    /// <summary>
    /// Buffers a download without ever holding more than <paramref name="maxBytes"/>: a declared
    /// Content-Length over the cap is refused before any byte is read, and the copy itself is bounded
    /// too, because the header can be absent or wrong.
    /// </summary>
    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, string path, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } declared && declared > maxBytes)
        {
            throw new MediaWorkspaceFileTooLargeException(path, declared, maxBytes);
        }

        await using Stream body = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new MediaWorkspaceFileTooLargeException(path, buffer.Length + read, maxBytes);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private async Task<byte[]> ReadOverObjectStorageAsync(string path, int maxBytes, CancellationToken ct)
    {
        int index = Interlocked.Increment(ref _stagingIndex);
        string stagingKey = S3RunnerOutputStore.StagingKeyFor(_jobId, index);
        PresignedPut put = await _presignedUrls
            .CreatePutAsync(_jobId, index, "application/octet-stream", _options.PresignPutTtl)
            .ConfigureAwait(false);

        RunnerSessionOpResult op = await RunAsync(
            MediaSessionOps.Publish,
            new JsonObject
            {
                ["path"] = path,
                ["putUrl"] = put.Url,
                ["maxBytes"] = maxBytes,
                ["contentType"] = "application/octet-stream"
            },
            timeout: null,
            ct).ConfigureAwait(false);

        RequireOk(op, "publish '" + path + "' (oversized read)");

        try
        {
            long? size = await _outputStore.TryGetSizeAsync(stagingKey, ct).ConfigureAwait(false);
            if (size is null)
            {
                throw new MediaWorkspaceException(
                    "read_over_publish", "'" + path + "' was published but nothing landed at '" + stagingKey + "'.");
            }

            if (size.Value > maxBytes)
            {
                throw new MediaWorkspaceFileTooLargeException(path, size.Value, maxBytes);
            }

            string url = await _outputStore
                .CreateStagingGetUrlAsync(stagingKey, _options.PresignGetTtl, ct)
                .ConfigureAwait(false);

            using HttpClient client = new();
            using HttpResponseMessage response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await ReadBoundedAsync(response.Content, path, maxBytes, ct).ConfigureAwait(false);
        }
        finally
        {
            // The staging object was a transport buffer for one read; the final key is never written.
            try
            {
                await _outputStore.DiscardAsync(stagingKey, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "media session {SessionId}: discarding read staging object {Key} failed", _sessionId, stagingKey);
            }
        }
    }

    /// <summary>
    /// Runs the worker's argv validator's first two rules on the way OUT: an argv whose strings are
    /// this workspace's own virtual paths contains no absolute path and no traversal segment. Catching
    /// it here names the offending argument; catching it on the runner is an argv_not_allowed op error
    /// that says only that something was wrong.
    /// </summary>
    private static void ValidateArgv(IReadOnlyList<string> argv, string op)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count == 0)
        {
            throw new MediaWorkspaceException("argv_empty", "an " + op + " invocation was built with an empty argv");
        }

        char[] separators = ['/', (char)92];
        foreach (string arg in argv)
        {
            if (string.IsNullOrEmpty(arg))
            {
                continue;
            }

            if (Path.IsPathRooted(arg))
            {
                throw new MediaWorkspaceException(
                    "argv_absolute_path",
                    "the " + op + " argv carries the absolute path '" + arg + "'; a remote session may only be " +
                    "handed session-relative virtual paths, because the worker resolves them inside its own session directory");
            }

            if (arg.Split(separators).Contains("..", StringComparer.Ordinal))
            {
                throw new MediaWorkspaceException(
                    "argv_traversal", "the " + op + " argv carries '" + arg + "', which contains a traversal segment");
            }
        }
    }

    private static JsonArray ToArgvArray(IReadOnlyList<string> argv)
    {
        JsonArray array = [];
        foreach (string arg in argv)
        {
            array.Add(arg);
        }

        return array;
    }

    private static string RequireVirtualPath(string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath))
        {
            throw new ArgumentException("Virtual path cannot be empty.", nameof(virtualPath));
        }

        if (Path.IsPathRooted(virtualPath))
        {
            throw new ArgumentException(
                "Virtual path '" + virtualPath + "' must be session-relative, not absolute.", nameof(virtualPath));
        }

        return virtualPath.Replace((char)92, '/');
    }

    private static void RequireOk(RunnerSessionOpResult op, string what)
    {
        if (!op.Ok)
        {
            throw OpFailure(op, what);
        }
    }

    private static RunnerSessionOpException OpFailure(RunnerSessionOpResult op, string what)
        => new(
            op.ErrorCode ?? "op_failed",
            what + " failed on the runner: " + (op.Error?.Message ?? "the worker returned no detail"),
            sessionLost: false,
            retryable: op.Error?.Retryable ?? false);

    private static bool IsMissingFile(RunnerSessionOpResult op)
        => !op.Ok && op.ErrorCode is "no_such_file" or "not_found" or "file_not_found";

    private static Exception ReadFailure(RunnerSessionOpResult op, string path, int maxBytes)
    {
        if (!op.Ok && string.Equals(op.ErrorCode, "file_too_large", StringComparison.OrdinalIgnoreCase))
        {
            // The worker refuses a too-large inline read exactly as the local workspace does; the
            // caller's contract is the same exception either way. The worker does not report the real
            // size, hence the -1: the caller only branches on "too large".
            return new MediaWorkspaceFileTooLargeException(path, actualBytes: -1, maxBytes: maxBytes);
        }

        return OpFailure(op, "read '" + path + "'");
    }

    /// <summary>
    /// An op that did not produce a result. The executor's ffmpeg handling branches on
    /// <see cref="VideoToolResult.TimedOut"/> and the exit code, so a worker-reported cancellation
    /// becomes a cancellation and everything else becomes a non-zero, non-timeout result carrying the
    /// worker's own words in stderr.
    /// </summary>
    private static VideoToolResult FromOpError(RunnerSessionOpResult op)
    {
        if (string.Equals(op.ErrorCode, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            throw new OperationCanceledException(
                "the runner cancelled the media session op: " + (op.Error?.Message ?? "no detail"));
        }

        bool timedOut = string.Equals(op.ErrorCode, "timeout", StringComparison.OrdinalIgnoreCase);
        return new VideoToolResult(
            ExitCode: -1,
            StdOut: string.Empty,
            StdErr: (op.ErrorCode ?? "op_failed") + ": " + (op.Error?.Message ?? "the worker returned no detail"),
            TimedOut: timedOut);
    }

    private static int Seconds(TimeSpan timeout)
        => (int)Math.Clamp(Math.Ceiling(timeout.TotalSeconds), 0, int.MaxValue);

    private static string? ReadString(JsonNode? node, string name)
        => node?[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static int ReadInt(JsonNode? node, string name, int fallback)
        => node?[name] is JsonValue value && value.TryGetValue(out int number) ? number : fallback;

    private static long ReadLong(JsonNode? node, string name, long fallback)
        => node?[name] is JsonValue value && value.TryGetValue(out long number) ? number : fallback;

    private static bool ReadBool(JsonNode? node, string name)
        => node?[name] is JsonValue value && value.TryGetValue(out bool flag) && flag;

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "media session {SessionId}: removing the local write buffer {Path} failed", _sessionId, path);
        }
    }
}

/// <summary>
/// A remote media session could not carry out the operation at all: a refused argv, a session that was
/// gone before the op ran, a publish that landed nowhere. Distinct from the domain failures a caller
/// branches on (a missing file, an ffmpeg exit code) and from the deployment facts of
/// <see cref="ComputeFabricUnavailableException"/>.
/// </summary>
public sealed class MediaWorkspaceException : InvalidOperationException
{
    public MediaWorkspaceException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    /// <summary>A stable, log-friendly token for why.</summary>
    public string Code { get; }
}

/// <summary>
/// The platform rejected a runner's published object (D12). Carries the verifier's own code and
/// <see cref="FailureClass"/> so the caller can fail the step with the right class - a
/// <see cref="FailureClass.Runner"/> failure is NOT refunded.
/// </summary>
public sealed class RunnerOutputRejectedException : InvalidOperationException
{
    public RunnerOutputRejectedException(string code, FailureClass failureClass, string message)
        : base(code + ": " + message)
    {
        Code = code;
        FailureClass = failureClass;
    }

    /// <summary>
    /// What a step shows the customer for a rejected runner output. The verifier's detail (ffprobe text, byte
    /// prefix, sizes, keys) is in <see cref="Exception.Message"/> and goes to the log only.
    /// </summary>
    public const string CustomerMessage =
        "The result could not be checked after it was made. Nothing was charged; run it again.";

    /// <summary>One of the RUNNER_OUTPUT_* codes.</summary>
    public string Code { get; }

    /// <summary>Who bears the cost.</summary>
    public FailureClass FailureClass { get; }
}