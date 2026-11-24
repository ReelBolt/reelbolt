using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// The media wire contract's enum encoding: <b>camelCase strings, integers rejected</b>.
/// </summary>
/// <remarks>
/// <para>
/// The media-pool contract spells its closed sets as lowercase/camelCase string literals
/// (<c>video|audio|image</c>, <c>userFiles|agentFiles|outputFiles</c>, <c>proxy|waveform|…</c>), so
/// anything that writes them — MVC model binding on the way in, the JSON response on the way out —
/// has to write that form and no other. System.Text.Json's own default is the ordinal integer, which
/// would silently turn <c>"kind":"video"</c> into <c>"kind":0</c> and break every client that reads
/// the field by name.
/// </para>
/// <para>
/// <b>A second converter class exists rather than reusing <c>EditTimelineEnumJsonConverter</c></b>,
/// whose own doc scopes it to the five edit-v2 contract enums. The encoding rule is identical; the
/// contract is not. (Note the same trap that doc records: an options-level
/// <see cref="JsonStringEnumConverter"/> would take precedence over these attributes and write
/// PascalCase, so no serializer options object in this feature carries one.)
/// </para>
/// </remarks>
public sealed class MediaWireEnumJsonConverter : JsonStringEnumConverter
{
    /// <summary>
    /// Configures the encoding: camelCase names, integer values rejected.
    /// </summary>
    public MediaWireEnumJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// The media kind of a pool item, derived from its MIME type. Mirrors the media-pool contract's
/// <c>MediaKind</c> closed set.
/// </summary>
[JsonConverter(typeof(MediaWireEnumJsonConverter))]
public enum MediaKind
{
    Video,
    Audio,
    Image,
}

/// <summary>
/// The owning project-file category of a pool item — the <b>same</b> three values
/// <c>ProjectFile.Category</c> already carries, not a second media taxonomy.
/// </summary>
[JsonConverter(typeof(MediaWireEnumJsonConverter))]
public enum MediaCategory
{
    UserFiles,
    AgentFiles,
    OutputFiles,
}

/// <summary>
/// Probed media characteristics, projected verbatim from the probe the derivative run stored for the
/// source (<c>MediaSourceProbe</c>) or, failing that, from one <c>VideoAnalyze</c> artifact's
/// <c>Sources[]</c> entry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Featureless on purpose.</b> Fps stays an exact rational pair rather than a pre-divided double,
/// so frame arithmetic downstream accumulates no drift. A source that was never analysed at all has
/// no probe (<c>MediaPoolItemResponse.Probe</c> is null), and this endpoint never probes, computes or
/// invents a duration, fps, resolution, codec, container, channel count, sample rate or bitrate of
/// its own.
/// </para>
/// <para>
/// <b>Two levels of absence, and both are stated rather than defaulted.</b> "No artifact mentioned
/// this source" is <c>Probe == null</c>; "an artifact mentioned it but never probed this particular
/// field" is that ONE field being <c>null</c> here — which is the normal reading of an artifact
/// persisted before <c>VideoCodec</c>/<c>AudioCodec</c>/<c>Container</c>/<c>AudioChannels</c>/
/// <c>AudioSampleRateHz</c>/<c>BitrateBps</c> existed. <b>Never 0, never an empty string:</b> a 0
/// channel count, a 0 bit/s rate or a blank codec name would state something ffprobe never said,
/// while null says exactly what is true, which is that it is not known. The five original fields
/// keep their names, order and meaning — the six below are strictly appended.
/// </para>
/// </remarks>
public sealed record MediaProbeResponse(
    double DurationSec,
    int FpsNum,
    int FpsDen,
    int Width,
    int Height,
    /// <summary>ffprobe's <c>codec_name</c> for the first video stream, or null when unprobed.</summary>
    string? VideoCodec = null,
    /// <summary>ffprobe's <c>codec_name</c> for the first audio stream, or null when unprobed (which is also the honest reading of a video with no audio stream: nothing was reported).</summary>
    string? AudioCodec = null,
    /// <summary>ffprobe's <c>format_name</c> VERBATIM — a sniffed container reports a comma-separated probe list (e.g. <c>"mov,mp4,m4a,3gp,3g2,mj2"</c>) and normalizing it here would invent a canonical name.</summary>
    string? Container = null,
    /// <summary>Channel count of the first audio stream, or null when unprobed.</summary>
    int? AudioChannels = null,
    /// <summary>Sample rate of the first audio stream in Hz, or null when unprobed.</summary>
    int? AudioSampleRateHz = null,
    /// <summary>Container bitrate in bits per second, or null when unprobed.</summary>
    long? BitrateBps = null,
    /// <summary>
    /// Whether the source has an audio stream: true/false when a probe that looked for codecs said
    /// so (the stored ingest probe always does), null on an analysis artifact that predates codec
    /// probing.
    /// </summary>
    bool? HasAudio = null,
    /// <summary>R10.7: ffprobe's <c>color_transfer</c> (e.g. <c>arib-std-b67</c>), or null when untagged or the probe predates it.</summary>
    string? ColorTransfer = null,
    /// <summary>R10.7: ffprobe's <c>color_primaries</c> (e.g. <c>bt2020</c>), or null.</summary>
    string? ColorPrimaries = null,
    /// <summary>R10.7: <c>"hlg"</c> or <c>"pq"</c> for HDR footage, else null.</summary>
    string? Hdr = null,
    string? ColorSpace = null,
    string? ColorRange = null,
    string? PixFmt = null,
    int? BitDepth = null,
    string? Make = null,
    string? Model = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    CameraProfileHintResponse? CameraProfileHint = null);

/// <summary>
/// Server-computed camera-profile hint (log-footage-color P2). Omitted from the probe when nothing was detected.
/// <c>Confidence</c> is "high" (the client may auto-apply) or "medium" (a suggestion only); <c>Reason</c> is a stable machine word.
/// </summary>
public sealed record CameraProfileHintResponse(string? Profile, string Confidence, string Reason);

/// <summary>
/// One source in the project media pool: a <c>project_files</c> row with a video/audio/image MIME
/// type, or an unregistered render found only in <c>workflow_step_results.output_storage_key</c>.
/// </summary>
/// <remarks>
/// <para>
/// Carries no storage key, no bucket name and no object URL — the source is addressed only by
/// <see cref="MediaRef"/>, and its bytes are reachable only through the project's own API routes.
/// </para>
/// <para>
/// <b><see cref="Bins"/> and <see cref="FilmstripUrl"/> exist so every follow-up a media panel needs
/// is addressable from the one payload it already has.</b> Without the membership list a drag-out
/// could not name the pin it means (the <c>itemId</c> is visible nowhere else the pool client
/// reads), and without the URL every client would have to reconstruct this feature's path grammar
/// from a <c>mediaRef</c> it is not supposed to interpret.
/// </para>
/// </remarks>
public sealed record MediaPoolItemResponse(
    string MediaRef,
    MediaKind Kind,
    string FileName,
    string MimeType,
    long SizeBytes,
    MediaCategory Category,
    MediaProbeResponse? Probe,
    /// <summary>
    /// Every non-deleted bin of THIS project that holds this source, ordered by <c>binId</c> then
    /// <c>itemId</c>. <b>Always present, and empty rather than null when the source is unbinned</b> —
    /// "in no bin" is a normal state a panel renders, not a missing value.
    /// </summary>
    IReadOnlyList<MediaBinMembershipResponse> Bins,
    /// <summary>
    /// This source's filmstrip-index route, as an API path string — never a storage key, a bucket or
    /// an object URL.
    /// </summary>
    string FilmstripUrl);

/// <summary>
/// One bin membership of a pool source: the pin's own id plus the bin holding it.
/// </summary>
/// <remarks>
/// Both ids are needed and neither is derivable from the other: <c>binId</c> says which folder, and
/// <c>itemId</c> is what a removal (<c>DELETE .../media/bins/{binId}/items/{itemId}</c>) addresses.
/// The same source can legitimately appear in several bins at once, so this is a list and not a
/// single-valued field.
/// </remarks>
public sealed record MediaBinMembershipResponse(Guid BinId, Guid ItemId);

/// <summary>
/// The filmstrip index route's envelope.
/// </summary>
/// <remarks>
/// <b>An envelope rather than a bare array, deliberately:</b> the frontend's
/// <c>resolveFilmstripThumb(index, timeSec)</c> is already written against
/// <c>filmstrip.densities[].sheets[]</c>, so returning that exact nesting means a fetched body drops
/// straight into it. The envelope also leaves room for a sibling key (a source's index state, say)
/// without a breaking reshape of the array a client is already destructuring.
/// </remarks>
public sealed record MediaFilmstripResponse(MediaFilmstripDocument Filmstrip);

/// <summary>
/// Every density of one source's filmstrip index, <c>densitySec</c> ascending.
/// </summary>
/// <remarks>
/// <b>An empty list is a normal answer, not an error.</b> "This source has no filmstrip index yet"
/// is a state the editor shows as "nothing to scrub with", and it is reported honestly as zero
/// densities rather than as a fabricated one — the same discipline as a null probe field.
/// </remarks>
public sealed record MediaFilmstripDocument(IReadOnlyList<MediaFilmstripDensityResponse> Densities);

/// <summary>
/// One filmstrip density: the sprite-sheet geometry and the sheets it is tiled from, read VERBATIM
/// from the sidecar document the media writer produced.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is recomputed.</b> <c>sheetCount</c> comes from the document, not from the sheet
/// list's length, and the geometry comes from the document rather than from an assumed 10x10 tile:
/// the writer MEASURES what ffmpeg actually decoded, because <c>duration / densitySec</c> and the
/// real decoded frame count disagree and the rounded-up difference is exactly the off-by-one that
/// makes hover-scrub land on the wrong thumbnail. A server that re-derived any of these would be
/// reintroducing the guesswork the writer went to the trouble of avoiding.
/// </para>
/// <para>
/// <b><see cref="DensitySec"/> is not the same as the thumbnail stride of a partial last sheet.</b>
/// It is the source-seconds step between consecutive thumbnails of the flat sequence, which is what
/// a client multiplies by a thumbnail index to get a source time.
/// </para>
/// </remarks>
public sealed record MediaFilmstripDensityResponse(
    double DensitySec,
    int ThumbWidth,
    int ThumbHeight,
    int Cols,
    int Rows,
    int SheetCount,
    /// <summary>The sheets in the order the sidecar lists them — which IS the flat thumbnail order, since each sheet's <c>firstThumbIndex</c> is the running sum of the preceding sheets' counts.</summary>
    IReadOnlyList<MediaFilmstripSheetResponse> Sheets);

/// <summary>
/// One sprite sheet of one density: where its thumbnails start in the flat sequence, how many it
/// carries, and the API path that streams its bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="ContentUrl"/> addresses the sheet through the row that OWNS the density plus the
/// sheet's own index, and is always present.</b> A filmstrip density is ONE <c>media_derivatives</c>
/// row — the writer inserts one row per (source, kind, variant), whose <c>StorageKey</c> names the
/// primary sprite sheet — while every sheet of that density is uploaded into one shared directory and
/// has no row of its own. So there is no row to resolve a secondary sheet against, and a rule that
/// matched a sheet's leaf to a row would find nothing for sheets 1..N-1 and silently break
/// hover-scrub past the first sheet. The path is
/// <c>/api/v1/projects/{projectId}/media/derivatives/{derivativeId}/sheet/{i}/content</c>, where
/// <c>{i}</c> is this sheet's 0-based index in the density's own sidecar array — the same array this
/// response is built from, in the same order.
/// </para>
/// <para>
/// <b>Whether the bytes exist yet is the sheet route's answer, not this document's.</b> The index
/// describes what the writer measured; a sheet whose object has not been uploaded (or was removed)
/// answers <c>404</c> from its own URL, which is a per-sheet degradation rather than a hole in the
/// index. Dropping such a sheet here would renumber the flat thumbnail sequence the client indexes
/// into, and inventing a different URL would hand it a path this service does not serve.
/// </para>
/// </remarks>
public sealed record MediaFilmstripSheetResponse(
    int FirstThumbIndex,
    int ThumbCount,
    double FirstTimeSec,
    string ContentUrl);

/// <summary>
/// One derivative of one source. A flat list of these is the whole read surface: the only thing a
/// client can observe about a derivative is whether its row exists, so there is deliberately no
/// <c>state</c> field — "not generated yet", "failed" and "unsupported" are indistinguishable here
/// and no UI built on this may claim any of them.
/// </summary>
/// <remarks>
/// <see cref="ContentUrl"/> is an <b>API path</b> on this same controller, never a storage key, a
/// bucket or a presigned object URL. Bytes stream from it by ROW ID (not by kind), because one
/// source can legitimately hold several rows of the same kind.
/// </remarks>
public sealed record MediaDerivativeResponse(
    Guid Id,
    [property: JsonConverter(typeof(MediaWireEnumJsonConverter))] MediaDerivativeKind Kind,
    string? MimeType,
    long SizeBytes,
    double? DurationSec,
    int? Width,
    int? Height,
    string ContentUrl,
    // Additive: the camera profile id a converted proxy was made with (derived from the row's spec, never a storage
    // key); null for every other row, including the plain proxy.
    string? CameraProfile = null);

/// <summary>
/// Body of <c>POST /api/v1/projects/{projectId}/media/derivatives/request</c> — the editor's
/// explicit "I just opened this clip, build what it needs" call.
/// </summary>
/// <remarks>
/// <para>
/// Every field except <see cref="MediaRef"/> is optional so the common call is a one-field body:
/// an omitted <see cref="Kinds"/> means <see cref="MediaDerivativeRequests.DefaultKinds"/>, and an
/// omitted <see cref="RequestedBy"/> means <c>"editor-open"</c>.
/// </para>
/// <para>
/// <b><see cref="Kinds"/> is a list of NAMES, not of <see cref="MediaDerivativeKind"/> values, and
/// that is a deliberate boundary decision.</b> The event this request produces carries
/// <c>IReadOnlyList&lt;MediaDerivativeKind&gt;</c> — a closed, typed array, never strings — but the
/// HTTP body cannot reach that encoding: <see cref="JsonConverterAttribute"/> on a collection
/// property replaces the converter for the COLLECTION, not for its elements, and
/// <see cref="MediaDerivativeKind"/> lives in <c>ReelBolt.Shared</c> where its type-level
/// converter cannot be declared without also changing how EF Core and the WorkflowEngine treat it.
/// So the boundary takes names and <c>MediaController</c> closes them onto the enum — once, before
/// anything is published — rejecting any name the enum does not have. A caller cannot smuggle an
/// open string through into the event.
/// </para>
/// </remarks>
public sealed record RequestMediaDerivativesRequest(
    string MediaRef,
    IReadOnlyList<string>? Kinds = null,
    string? RequestedBy = null,
    // Optional, a member of the closed camera-profile set (CameraProfiles.Ids): also asks for the converted proxy.
    string? CameraProfile = null);

/// <summary>
/// The vocabulary shared by the two <c>MediaDerivativesRequested</c> publishers in this service, so
/// an upload and an editor-open cannot ask for different things by accident.
/// </summary>
public static class MediaDerivativeRequests
{
    /// <summary>
    /// The kinds a request asks for when it names none: <b>the complete set the derivative run
    /// produces</b> — proxy, filmstrip, waveform peaks and keyframe index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not every enum member: <see cref="MediaDerivativeKind.Thumbnail"/> rows come only from the
    /// ThumbnailGenerate workflow step and <see cref="MediaDerivativeKind.Waveform"/> is a superseded
    /// signpost nothing writes, so asking for them on every upload and editor-open advertised two
    /// derivatives that never arrive (RD-15). <c>MediaDerivativeDefaultKindsTests</c> pins this list
    /// to the engine's own slug catalogue, so a kind the run learns to produce fails the suite until
    /// it is added here. The consumer still decides per source what is worth producing (a silent
    /// video gets no peaks); a caller may name any kind explicitly.
    /// </para>
    /// <para>
    /// <b>The one exception is <see cref="MediaDerivativeKind.Stills"/></b> (T-101): full-size stills
    /// are extracted on demand, when someone asks to look at an output, never for every upload.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<MediaDerivativeKind> DefaultKinds =
        Enum.GetValues<MediaDerivativeKind>()
            .Where(k => k is not (MediaDerivativeKind.Stills
                or MediaDerivativeKind.StabilizedProxy
                or MediaDerivativeKind.Thumbnail
                or MediaDerivativeKind.Waveform))
            .ToArray();

    /// <summary>A <c>video/*</c> or <c>audio/*</c> upload just committed a <c>project_files</c> row.</summary>
    public const string RequestedByUpload = "upload";

    /// <summary>A render was registered as a project output. (No publisher in this wave.)</summary>
    public const string RequestedByRenderOutput = "render-output";

    /// <summary>The editor opened the clip for the first time and asked for its derivative set.</summary>
    public const string RequestedByEditorOpen = "editor-open";

    /// <summary>
    /// An operator ran <c>POST /api/v1/media/admin/backfill</c>: the explicit repair of every video
    /// source that has no proxy. See <c>MediaDerivativeCoverageService</c>.
    /// </summary>
    public const string RequestedByBackfill = "backfill";

    /// <summary>
    /// The periodic <c>MediaDerivativeBackfillService</c> sweep found a video source with no proxy.
    /// The sweeper, not the upload hint, is the durable contract: a lost publish is repaired here on
    /// the next tick because the candidates are re-derived from the source rows, not from a message.
    /// </summary>
    public const string RequestedBySweep = "sweep";
}
