using System.Text.Json.Nodes;

namespace ReelBolt.WorkflowEngine.Services.Compute;

/// <summary>
/// D11: the media session op names, in one place. They are the second group of
/// <c>docs/runner-protocol.md</c> §"Media session ops and argv validation" — valid in a
/// <c>video.compile</c> or <c>video.analyze.extract</c> session, and refused with
/// <c>unsupported_op</c> in a Remotion workspace session (and vice versa:
/// <c>pkg/agent/media</c> refuses every workspace op).
/// <para>
/// The Go side is the authority: <c>sandbox/pkg/protocol/constants.go</c>'s <c>MediaOps()</c>, pinned
/// on both sides by <c>MediaSessionOpContractTests</c>. The spellings are lowercase and are an OPEN
/// SET in the protocol ("an unrecognised value MUST be handled as an unknown case, not as a parse
/// error"), which is why they are constants here rather than an enum: a worker that grows an op this
/// build does not know is a worker this build never calls, not a failure.
/// </para>
/// </summary>
public static class MediaSessionOps
{
    /// <summary>Object storage → the session directory, from a presigned GET. <c>{url, path, maxBytes, sha256?}</c> → <c>{bytes, sha256}</c>.</summary>
    public const string Stage = "stage";

    /// <summary>One ffmpeg invocation. <c>{argv, timeoutSec}</c> → <c>{exitCode, stderrTail, durationMs}</c>.</summary>
    public const string Ffmpeg = "ffmpeg";

    /// <summary>One ffprobe invocation. <c>{argv, timeoutSec}</c> → <c>{exitCode, stdout, stderrTail}</c>.</summary>
    public const string Ffprobe = "ffprobe";

    /// <summary>Read a session file inline. <c>{path, maxBytes}</c> → <c>{content, encoding, bytes}</c>.</summary>
    public const string Read = "read";

    /// <summary>The session directory → object storage, to a presigned PUT. <c>{path, putUrl, maxBytes, contentType}</c> → <c>{bytes, sha256}</c>.</summary>
    public const string Publish = "publish";

    /// <summary>Does a session path exist, and how big is it? <c>{path}</c> → <c>{exists, bytes}</c>.</summary>
    public const string Stat = "stat";

    /// <summary>Destroy the session directory and end the session.</summary>
    public const string Close = "close";

    /// <summary>Every op of a media session, for tests and drift guards.</summary>
    public static IReadOnlyList<string> All { get; } = [Stage, Ffmpeg, Ffprobe, Read, Publish, Stat, Close];

    /// <summary>
    /// The protocol's own inline read cap ("<c>maxBytes</c> (at most 4 MiB)"). A larger read is not
    /// refused by the engine, it takes the other arm — publish, then fetch the object — which is
    /// exactly what the same table says: "larger content is read by <c>publish</c> then fetched from
    /// object storage by the engine".
    /// </summary>
    public const int MaxInlineReadBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The default ceiling of a read with no caller cap, and so the most the engine buffers in memory for one
    /// whole-file read. Sized to the largest legitimate whole-file read: a decoded 16 kHz mono 16-bit WAV
    /// (32,000 bytes/s), so 256 MiB is about 2.2 hours of audio; everything else read whole (keyframe JPEGs,
    /// extract document, scene sidecar and manifest, analysis artifact) is a few MiB at most. A caller that
    /// legitimately needs more passes an explicit <c>maxBytes</c>.
    /// </summary>
    public const int MaxEngineReadBytes = 256 * 1024 * 1024;

    /// <summary>
    /// The largest RAW size the engine will ask an inline <c>read</c> to carry. Binary content comes
    /// back base64 (x4/3) inside a JSON envelope that must fit one 4 MiB gateway frame, so a raw
    /// 4 MiB cap is not safe: a 3.86 MB WAV is ~5.15 MB on the wire, the socket closes 1009 and the
    /// op stalls until the gateway times out. 3 MiB raw is exactly 4 MiB of base64, so the cap
    /// keeps 256 KiB of headroom below that (2.75 MiB raw is ~3.67 MiB base64 plus envelope). Anything
    /// larger goes publish-then-fetch over object storage.
    /// </summary>
    public const int SafeInlineReadBytes = 3 * 1024 * 1024 - 256 * 1024;
}

/// <summary>One op's error, as the worker reported it (<c>op.response.error</c>).</summary>
/// <param name="Code">One of the protocol's op-level codes (<c>invalid_path</c>, <c>argv_not_allowed</c>, <c>file_too_large</c>, …).</param>
/// <param name="Message">The worker's own detail, passed through for the log and the step error.</param>
/// <param name="Retryable">The worker's hint. The engine decides — a hint, never an instruction.</param>
public sealed record RunnerSessionOpError(string? Code, string? Message, bool Retryable);

/// <summary>
/// One op's outcome, as <c>POST /internal/v1/sessions/{id}/ops</c> returns it. A FAILED op is a
/// SUCCESSFUL round trip (HTTP 200 with <c>ok: false</c>), which is why this type carries
/// <see cref="Ok"/> rather than the transport throwing: "only the transport failing is an HTTP
/// error".
/// </summary>
/// <param name="OpId">The op's id, minted by the gateway when the engine did not supply one. Repeating it is what makes a retry after a <c>504</c> safe.</param>
/// <param name="Result">The op's payload. Its shape is the op's own (see <see cref="MediaSessionOps"/>).</param>
/// <param name="DurationMs">How long the op took, per the gateway.</param>
public sealed record RunnerSessionOpResult(
    Guid SessionId,
    Guid JobId,
    string OpId,
    bool Ok,
    JsonNode? Result,
    RunnerSessionOpError? Error,
    long DurationMs)
{
    /// <summary>The worker's error code, when the op failed.</summary>
    public string? ErrorCode => Error?.Code;

    /// <summary>
    /// The op's payload, or a <see cref="RunnerSessionOpException"/> when the op failed. Callers that
    /// map a failed op onto a domain failure (an ffmpeg exit code, a missing file) should read
    /// <see cref="Ok"/>/<see cref="Error"/> instead; this is for the callers where a failed op is
    /// simply not a possible answer.
    /// </summary>
    public JsonNode RequireResult()
        => Ok && Result is not null
            ? Result
            : throw new RunnerSessionOpException(
                Error?.Code ?? "op_failed",
                "the session op failed: " + (Error?.Message ?? "the worker returned no detail"),
                sessionLost: false,
                retryable: Error?.Retryable ?? false);
}

/// <summary>
/// A session op could not be run. The <see cref="Code"/> is either one of the gateway's four
/// session-level refusals (<c>session_not_found</c>, <c>session_lost</c>, <c>op_in_flight</c>,
/// <c>op_cancelled</c>, <c>op_timeout</c>, <c>session_owner_unreachable</c>) or the worker's own
/// op-level code, so a caller can act on the difference the protocol draws between them:
/// <list type="bullet">
///   <item><see cref="SessionLost"/> — the session is gone. Do NOT retry the op; open a new session or fail the step.</item>
///   <item><see cref="Retryable"/> — a retry with the SAME <c>opId</c> is safe, because the worker answers a repeated id from its own 256-entry cache.</item>
/// </list>
/// <para>
/// A gateway that is not deployed at all is deliberately NOT this exception: 503 and a transport
/// failure are <see cref="ComputeFabricUnavailableException"/>, the deployment fact the selector and
/// the workspace both degrade on.
/// </para>
/// </summary>
public sealed class RunnerSessionOpException : InvalidOperationException
{
    public RunnerSessionOpException(
        string code,
        string message,
        bool sessionLost,
        bool retryable,
        int? statusCode = null,
        string? reason = null)
        : base(message)
    {
        Code = code;
        SessionLost = sessionLost;
        Retryable = retryable;
        StatusCode = statusCode;
        Reason = reason;
    }

    /// <summary>The gateway's or the worker's own error code.</summary>
    public string Code { get; }

    /// <summary>True when the session cannot serve the op and must be re-opened (or the step failed).</summary>
    public bool SessionLost { get; }

    /// <summary>True when repeating the op under the same <c>opId</c> is safe.</summary>
    public bool Retryable { get; }

    /// <summary>The HTTP status that carried the refusal, when there was one.</summary>
    public int? StatusCode { get; }

    /// <summary>
    /// The worker named a stage that the object store answered 404 to (the object is not there). The op
    /// error carries no structured status, only the worker's text ("the object store answered 404", written
    /// in sandbox/pkg/agent/transfer.go and sourcecache.go), so this is the ONE place that reads it; a test
    /// pins the matcher against the Go source so a rewording there fails here.
    /// </summary>
    public bool IsObjectStoreNotFound =>
        string.Equals(Code, "stage_failed", StringComparison.Ordinal)
        && Message.Contains(ObjectStoreNotFoundText, StringComparison.Ordinal);

    /// <summary>The worker's wording for a 404 from the object store (its format string is "the object store answered %d").</summary>
    public const string ObjectStoreNotFoundText = "the object store answered 404";

    /// <summary>The gateway's <c>reason</c> for a lost session (<c>lease_lost</c>, <c>runner_offline</c>, …).</summary>
    public string? Reason { get; }
}
