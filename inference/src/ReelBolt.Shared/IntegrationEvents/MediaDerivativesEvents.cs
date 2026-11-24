using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.IntegrationEvents;

/// <summary>
/// A media source addressed by <b>kind + id</b> — the pair that actually identifies a row, since a
/// bare GUID cannot tell a <c>ProjectFile</c> from a <c>WorkflowStepResult</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not a parsed/branded string.</b> The wire-facing form of a source reference is
/// the opaque <c>mediaRef</c> token (<c>file:&lt;guid&gt;</c> / <c>step_result:&lt;guid&gt;</c>)
/// that the media endpoints route on; inside the process the two halves are carried typed. Producer
/// and consumer therefore never disagree about what the token's prefix meant.
/// </para>
/// <para>
/// <b>Why this exists on top of a project-file id.</b> A render produced with
/// <c>RegisterProjectFile: false</c> has no <c>ProjectFile</c> row at all — only a
/// <c>WorkflowStepResult.OutputStorageKey</c> — so a reference shape built around a non-nullable
/// <c>SourceProjectFileId</c> cannot express it. <see cref="MediaSourceKind"/> is declared once, in
/// <see cref="ReelBolt.Shared.Data.Models.MediaSourceKind"/>, and referenced here rather than
/// redeclared: a second enum of the same name in another namespace makes every file that imports
/// both namespaces fail to compile on an ambiguous reference.
/// </para>
/// </remarks>
public sealed record MediaSourceRef(MediaSourceKind Kind, Guid SourceId);

/// <summary>
/// Published by the Inference API when a media source needs its derivative set (proxy, waveform,
/// thumbnail, filmstrip, keyframe index) produced — the request side of the <c>media_derivatives</c>
/// index whose writer lives in the WorkflowEngine.
/// </summary>
/// <remarks>
/// <para>
/// Three sites publish this, all of them in the Inference API and all of them <b>after</b> the row
/// they refer to is committed:
/// <list type="bullet">
/// <item><description>a <c>video/*</c> or <c>audio/*</c> upload — <c>RequestedBy: "upload"</c></description></item>
/// <item><description>a render being registered as a project output — <c>RequestedBy: "render-output"</c> (out of scope for the P2 wave that introduces this event)</description></item>
/// <item><description>the editor's explicit first-open call — <c>RequestedBy: "editor-open"</c></description></item>
/// </list>
/// </para>
/// <para>
/// <b>This message is a hint, never a source of truth.</b> Nothing about producing a derivative is
/// durable here: no DB row is written by the publisher, a lost delivery simply means the derivative
/// is produced when the next request arrives, and the consumer must be idempotent because two
/// requests for one unchanged source are indistinguishable from one.
/// </para>
/// <para>
/// <b><see cref="SourceEtag"/> may legitimately be empty.</b> It is read with a best-effort S3 HEAD,
/// and a failed HEAD must never fail an upload (the file is already stored; only a cache-identity
/// read went wrong). A consumer that cannot key a cache on an empty etag must skip or re-derive
/// rather than reject — an event whose only defect is missing metadata is not a poison message.
/// </para>
/// <para>
/// <b><see cref="StillTimesSec"/> (appended, optional)</b> only matters when <see cref="Kinds"/> names
/// <see cref="MediaDerivativeKind.Stills"/>: null or empty asks for the evenly spaced set, otherwise the
/// stills are taken at those times (normalized by <see cref="MediaDerivativeStillTimes.NormalizeToMs"/>,
/// at most twelve, clamped to the source's duration by the writer). The normalized times are part of
/// the derivative's cache identity, so the same ask never extracts twice.
/// </para>
/// <para>
/// <b><see cref="ProxyVariant"/> (appended, optional)</b>: 0 asks for the plain proxy only (as before); <c>1..N</c> also
/// asks for the converted proxy of <c>CameraProfiles.All[ProxyVariant - 1]</c> (camera log footage with the conversion
/// baked in). It only matters when <see cref="Kinds"/> names <see cref="MediaDerivativeKind.Proxy"/>, and an HDR
/// source never gets one.
/// </para>
/// <para>
/// <b><see cref="AnalyzeForWorkflows"/> (appended, optional)</b>: true also asks the run to keep, for a video source,
/// what the silence, shot, frame grid and event passes of a VideoAnalyze step with default settings find, so a
/// later workflow does not decode the video again for them. It is extra work after the derivatives and never
/// changes them; false (the default) is the run as it was. The engine can ignore it
/// (<c>VideoEditing:SourcePassCache:WarmOnUpload</c>).
/// </para>
/// </remarks>
public record MediaDerivativesRequested(
    Guid ProjectId, MediaSourceRef SourceRef, Guid? SourceProjectFileId,
    string SourceStorageKey, string SourceEtag, string MimeType,
    IReadOnlyList<MediaDerivativeKind> Kinds, string? RequestedBy, DateTime RequestedAt,
    IReadOnlyList<double>? StillTimesSec = null,
    int ProxyVariant = 0,
    bool AnalyzeForWorkflows = false);

/// <summary>
/// The one normalization of requested still times, shared by the publisher (which matches an existing
/// set against an ask) and the writer (which keys the cache on it), so the two can never disagree.
/// </summary>
public static class MediaDerivativeStillTimes
{
    /// <summary>The most stills one set holds.</summary>
    public const int MaxStills = 12;

    /// <summary>
    /// Finite times only, negatives as 0, rounded to a tenth of a second, as whole milliseconds,
    /// deduplicated, ascending, the first <see cref="MaxStills"/>. Null or nothing usable → empty
    /// (the evenly spaced set).
    /// </summary>
    public static int[] NormalizeToMs(IReadOnlyList<double>? timesSec)
    {
        if (timesSec is null || timesSec.Count == 0) return [];

        return timesSec
            .Where(double.IsFinite)
            .Select(t => Math.Max(0, t))
            .Where(t => t <= int.MaxValue / 1000.0)
            .Select(t => (int)Math.Round(t * 10, MidpointRounding.AwayFromZero) * 100)
            .Distinct()
            .Order()
            .Take(MaxStills)
            .ToArray();
    }
}

/// <summary>
/// Ephemeral, supersedable progress for one media-derivative production run. Structural twin of
/// <see cref="WorkflowStepProgress"/> with the same discipline — never persisted, never
/// authoritative — and <b>deliberately NOT a reuse of it</b>: a derivative has no execution id, step
/// id or step-result id, and publishing those as empty or invented Guids would render a phantom step
/// card in the workflow monitor, because every consumer of that event routes by those three ids.
/// </summary>
/// <remarks>
/// <para>
/// <b>Published by the WorkflowEngine</b>, through
/// <c>IMediaDerivativeProgressSink</c> (<c>Services/Video/MediaDerivativeProgressSink.cs</c>), which
/// is the only seam a derivative run reports through. One message per stage transition of one run;
/// a later message for the same source supersedes an earlier one, and a lost delivery costs nothing
/// but a missing UI frame.
/// </para>
/// <para>
/// <b>Publication must never cost a derivative, an upload or a step result.</b> This is the same
/// class of hardening T-107 applied to <see cref="MediaDerivativesRequested"/>'s publish site: a
/// progress event is a courtesy, so a broker outage while publishing it must not fail the work it
/// describes. The publisher therefore publishes fire-and-forget — never awaiting the publish in a
/// path whose own success is already durable — and <c>IMediaDerivativeProgressSink.Report</c> is
/// contractually not allowed to throw.
/// </para>
/// <para>
/// <b>This message is not a record.</b> Nothing here is written to <c>media_derivatives</c>; the row
/// the run commits remains the only durable statement that a derivative exists, and a
/// <c>Done</c>/<c>Skipped</c>/<c>Failed</c> stage here is a UI hint, not a completion signal. A
/// consumer must treat <c>Stage</c> as a display string from a closed vocabulary, never as state it
/// can act on.
/// </para>
/// <para>
/// <b>Carries a storage key, so it must not be forwarded to a browser verbatim.</b>
/// <see cref="SourceStorageKey"/> is the half of the cache identity that says which object the run
/// is for; it is internal bus traffic (exactly as on <see cref="MediaDerivativesRequested"/>), and
/// any client-facing relay is responsible for projecting it away — no response may carry a storage
/// key. The Go API's SSE relay does precisely that; see <c>api/services/rabbitmq_service.go</c>.
/// </para>
/// </remarks>
public record MediaDerivativeProgress
{
    /// <summary>Owning project. The only id a client can always scope this event by.</summary>
    public Guid ProjectId { get; init; }

    /// <summary>
    /// The <c>ProjectFile</c> the run is for, or null when the source is an unregistered render (a
    /// <c>WorkflowStepResult</c>), exactly as on <see cref="MediaDerivativesRequested.SourceProjectFileId"/>
    /// and on the committed row.
    /// </summary>
    public Guid? SourceProjectFileId { get; init; }

    /// <summary>
    /// The source object's storage key. <b>Never relayed to a client</b> — see the remarks above.
    /// </summary>
    public string SourceStorageKey { get; init; } = string.Empty;

    /// <summary>
    /// The derivative's canonical cache key, or <b>null</b> for a run-level transition that is not
    /// yet attributable to one derivative — a MIME-type skip, a stale message, or a run that failed
    /// before any variant was planned. Null is meaningful and is not the same as "empty": an empty
    /// string would claim a derivative identity that does not exist.
    /// </summary>
    public string? CacheKey { get; init; }

    /// <summary>
    /// Where the run is, as a NAME from the closed vocabulary
    /// <c>Queued | Probing | Proxy | Filmstrip | Peaks | KeyframeIndex | Done | Skipped | Failed</c>.
    /// A string rather than an enum because the producing vocabulary
    /// (<c>MediaDerivativeProgressStage</c>) is engine-owned and the producer's own enum
    /// (<c>MediaDerivativeStage</c>) has no <c>Failed</c> member — the eight shared names are
    /// identical on purpose, so the producer's mapping is a rename-free pass-through.
    /// </summary>
    public string Stage { get; init; } = string.Empty;

    /// <summary>
    /// 0-100 when the producer reports a real percentage within the derivative (a proxy encode does;
    /// a cache hit does not); null otherwise, and the UI shows the stage alone.
    /// </summary>
    public int? PercentComplete { get; init; }

    /// <summary>
    /// Free-form diagnostics, never UI copy: why a derivative was skipped, what failed. Null on the
    /// ordinary path.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>When the transition was observed. Set by the producer, not by the transport.</summary>
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}
