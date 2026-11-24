using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Amazon.S3;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing.Color;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The editor's read surface over a project's media: the media pool, one source's derivative rows,
/// a derivative's bytes, and the explicit request that asks for a source's derivative set.
/// </summary>
/// <remarks>
/// <para>
/// <b>The project is resolved through <c>IProjectAccessService</c> first, and below it every
/// failure is <c>404</c>.</b> The service applies docs/design/tenancy.md: a project in another
/// organization, or none at all, is <c>404</c> — so a stranger probing project ids cannot
/// distinguish "no such project" from "a real project, not yours" and then keep probing mediaRefs
/// and derivative ids against it — while a caller inside the project's own organization whose
/// role may not act is <c>403</c>, which tells a member nothing they did not already know. Reads
/// need <c>View</c>; requesting derivatives (it enqueues work) needs <c>Edit</c>. Below that gate
/// a mediaRef or derivativeId that is not in this project, a soft-deleted row, or a storage key
/// outside the project prefix all answer the SAME empty <c>404</c>, and no body ever says which
/// case applied.
/// </para>
/// <para>
/// <b>No response from this controller ever carries a storage key, a bucket name or an object
/// URL.</b> A source is addressed only by its opaque <c>mediaRef</c> token and a derivative's bytes
/// only by this API's own <c>/content</c> path — see <see cref="MediaPoolItemResponse"/> and
/// <see cref="MediaDerivativeResponse"/>. Every storage key that is used internally is first checked
/// against the project's own <c>projects/{projectId}/</c> prefix, so no key from another project can
/// be reached even if a row's key were wrong.
/// </para>
/// <para>
/// <b>This controller never probes and never invents.</b> Duration, fps and resolution come only
/// from a <c>VideoAnalyze</c> artifact another step already produced; a source that was never
/// analysed reports <c>null</c> rather than a computed or defaulted value.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/media")]
[Authorize]
public class MediaController : ControllerBase
{
    /// <summary>
    /// Ceiling on the pool's size. Applied to each source table's own query and again to the union,
    /// so the answer is always the newest <see cref="PoolCap"/> sources of the project rather than
    /// the newest of each kind.
    /// </summary>
    public const int PoolCap = 500;

    /// <summary>
    /// The route a <c>file:&lt;guid&gt;</c> mediaRef is addressed by, written exactly as an ASP.NET
    /// route template requires it to be escaped — which is why it does not look like the regex it
    /// denotes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two templates rather than one alternation, because the framework cannot express the
    /// alternation.</b> The obvious form,
    /// <c>{fileId:regex(^(file|step_result):[0-9a-fA-F-]{{36}}$)}</c>, does not build: a route
    /// template's constraint argument ends at the FIRST <c>)</c>, so the group's closing paren
    /// terminates <c>regex(</c> early and the rest of the pattern is parsed as a second, unrelated
    /// policy — the route table then carries two unresolvable policies instead of one regex
    /// constraint. (Verified against the framework, not assumed: the parse yields
    /// <c>regex(^(file|step_result)</c> plus <c>[0-9a-fA-F-]{36}$)</c>.) One template per prefix
    /// expresses the same language with no nested group, and each is still a real
    /// <c>RegexRouteConstraint</c> whose <c>Match</c> behaves as the single template would have.
    /// </para>
    /// <para>
    /// <b>The escaping is load-bearing.</b> <c>[</c>/<c>]</c> inside a route template is the
    /// token-replacement syntax (<c>[action]</c>, <c>[controller]</c>), so the character class's
    /// brackets must be written <c>[[</c> and <c>]]</c> — otherwise MVC reads
    /// <c>[0-9a-fA-F-]</c> as a token named <c>0-9a-fA-F-</c> and refuses to build the route table
    /// at all. The quantifier's braces are doubled for their own reason: <c>{36}</c> unescaped is
    /// read as a route parameter. The regex the framework ends up compiling is
    /// <c>^file:[0-9a-fA-F-]{36}$</c> (and the <c>step_result:</c> equivalent).
    /// </para>
    /// <para>
    /// Without a constraint, <c>{fileId}</c> would also match <c>media/bins</c>, so the media-bin
    /// routes the frontend calls would be parsed as a derivative lookup for a source literally named
    /// <c>"bins"</c>. The constraint is therefore part of the contract, not a nicety.
    /// </para>
    /// </remarks>
    public const string FileMediaRefRouteTemplate = @"{fileId:regex(^file:[[0-9a-fA-F-]]{{36}}$)}/derivatives";

    /// <summary>
    /// The route a <c>step_result:&lt;guid&gt;</c> mediaRef is addressed by — the
    /// <c>step_result</c> half of <see cref="FileMediaRefRouteTemplate"/>'s rationale.
    /// </summary>
    public const string StepResultMediaRefRouteTemplate = @"{fileId:regex(^step_result:[[0-9a-fA-F-]]{{36}}$)}/derivatives";

    /// <summary>
    /// The filmstrip-index route of a <c>file:&lt;guid&gt;</c> mediaRef — the same regex constraint,
    /// for the same reason, as <see cref="FileMediaRefRouteTemplate"/>; only the trailing segment
    /// differs.
    /// </summary>
    /// <remarks>
    /// A separate constant per prefix rather than a shared prefix fragment, exactly as the two
    /// derivatives templates are separate: a route template's constraint argument ends at the FIRST
    /// <c>)</c>, so the alternation the two prefixes really are cannot be written as one template.
    /// The constraint is what keeps <c>media/bins</c> from being parsed as a source literally named
    /// <c>"bins"</c> — see <see cref="FileMediaRefRouteTemplate"/>.
    /// </remarks>
    public const string FileMediaRefFilmstripRouteTemplate = @"{fileId:regex(^file:[[0-9a-fA-F-]]{{36}}$)}/filmstrip";

    /// <summary>
    /// The filmstrip-index route of a <c>step_result:&lt;guid&gt;</c> mediaRef — the
    /// <c>step_result</c> half of <see cref="FileMediaRefFilmstripRouteTemplate"/>'s rationale.
    /// </summary>
    public const string StepResultMediaRefFilmstripRouteTemplate = @"{fileId:regex(^step_result:[[0-9a-fA-F-]]{{36}}$)}/filmstrip";

    /// <summary>P6: the annotation route of a <c>file:&lt;guid&gt;</c> mediaRef — same constraint grammar as the derivatives routes.</summary>
    public const string FileMediaRefAnnotationRouteTemplate = @"annotations/{fileId:regex(^file:[[0-9a-fA-F-]]{{36}}$)}";

    /// <summary>P6: the <c>step_result</c> half of <see cref="FileMediaRefAnnotationRouteTemplate"/>.</summary>
    public const string StepResultMediaRefAnnotationRouteTemplate = @"annotations/{fileId:regex(^step_result:[[0-9a-fA-F-]]{{36}}$)}";

    /// <summary>
    /// The tokenizer an opaque <c>mediaRef</c> is parsed with, covering <b>both</b> prefixes — the
    /// language <see cref="FileMediaRefRouteTemplate"/>/<see cref="StepResultMediaRefRouteTemplate"/>
    /// express for the path, written once more here for the action body.
    /// </summary>
    /// <remarks>
    /// <b>Two expressions rather than one shared constant, deliberately.</b> The route templates must
    /// carry route-template escaping (<c>[[</c>, <c>{{36}}</c>) and this one must not, and a body- or
    /// query-supplied <c>mediaRef</c> is not covered by a route constraint at all — so this is the
    /// only guard the <c>POST .../derivatives/request</c> body has. That the two describe the same
    /// language is asserted by <c>MediaControllerTests</c> — the constraints through the framework's
    /// own route parser, the tokenizer through the action itself — rather than left to drift.
    /// </remarks>
    private static readonly Regex MediaRefPattern = new(
        @"^(?<kind>file|step_result):(?<id>[0-9a-fA-F-]{36})$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    /// <summary>
    /// The one JSON configuration used to read a <c>VideoAnalyze</c> artifact.
    /// </summary>
    /// <remarks>
    /// Must match the writer's (<c>VideoAnalyzeStepExecutor.ArtifactJsonOptions</c>): web defaults
    /// for camelCase property names, plus a <see cref="JsonStringEnumConverter"/> for the two
    /// provenance enums — without it, a string-valued enum member makes deserialization throw and
    /// every source would read as unprobed.
    /// </remarks>
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// The one JSON configuration used to read a filmstrip index sidecar.
    /// </summary>
    /// <remarks>
    /// Web defaults, and deliberately no enum converter: the sidecar is a numeric-and-strings
    /// document (<c>FilmstripIndex.Build</c>) with no enum-valued member, so a converter here would
    /// only widen what a hand-edited sidecar could smuggle in. Kept separate from
    /// <see cref="ArtifactJsonOptions"/> rather than shared, so a change to one document's reading
    /// cannot silently change the other's.
    /// </remarks>
    private static readonly JsonSerializerOptions FilmstripIndexJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly IFileStorageService _storage;
    private readonly IAmazonS3 _s3;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<MediaController> _logger;
    /// <summary>T-004: a step-result derivative's source is gated as a publication before it is served.</summary>
    private readonly PublishModeration? _publishModeration;
    /// <summary>The project's "analyse videos at upload" choice, asked for the editor's own derivative request.</summary>
    private readonly ReelBolt.Inference.Api.Services.Uploads.IUploadAnalysisSetting? _uploadAnalysis;

    public MediaController(
        InferenceApiDbContext db,
        IProjectAccessService projectAccess,
        IFileStorageService storage,
        IAmazonS3 s3,
        IPublishEndpoint publishEndpoint,
        ILogger<MediaController> logger,
        PublishModeration? publishModeration = null,
        ReelBolt.Inference.Api.Services.Uploads.IUploadAnalysisSetting? uploadAnalysis = null)
    {
        _uploadAnalysis = uploadAnalysis;
        _db = db;
        _projectAccess = projectAccess;
        _storage = storage;
        _s3 = s3;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
        _publishModeration = publishModeration;
    }

    /// <summary>
    /// The project media pool: this project's <c>video/*</c>/<c>audio/*</c>/<c>image/*</c> project
    /// files, plus the renders that exist only as a step result's <c>output_storage_key</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Newest first, capped at <see cref="PoolCap"/>. A render that HAS been registered as a project
    /// file is not listed twice: it is already the <c>file:</c> item, and the same bytes under two
    /// references would make the panel show one clip as two.
    /// </para>
    /// <para>
    /// Probe metadata is the probe the derivative run stored for the source
    /// (<c>media_derivatives.source_probe_json</c>, written when the upload's derivatives are made —
    /// see <see cref="StoredMediaProbes"/>). A source no derivative run has probed falls back to the
    /// newest <c>VideoAnalyze</c> artifact's <c>Sources[]</c> entry for its storage key — one
    /// artifact, the newest, per the P2-B1 decision, not a walk back through older ones. A source
    /// neither mentions stays unprobed.
    /// </para>
    /// <para>
    /// <b>Bin membership and the filmstrip URL ride along on the same payload.</b> Both are
    /// answered by one bounded query each for the whole pool rather than one per item — see
    /// <see cref="LoadBinMembershipsAsync"/> — because a per-item query is exactly the N+1 that
    /// makes a 500-item pool slow.
    /// </para>
    /// </remarks>
    [HttpGet]
    public async Task<ActionResult<List<MediaPoolItemResponse>>> ListMedia(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        List<MediaSourceDescriptor> sources = await ListPoolSourcesAsync(projectId, ct);

        List<MediaPoolItemResponse> items = new(sources.Count);

        if (sources.Count == 0)
        {
            return Ok(items);
        }

        Dictionary<string, MediaSourceProbe> stored = await StoredMediaProbes.LoadAsync(
            _db, projectId, sources.Select(s => s.StorageKey).ToList(), ct);
        Dictionary<string, VideoAnalysisMedia> probes = await LoadNewestAnalysisProbesAsync(projectId, ct);
        Dictionary<MediaSourceKey, List<MediaBinMembershipResponse>> memberships =
            await LoadBinMembershipsAsync(projectId, sources, ct);

        foreach (MediaSourceDescriptor source in sources)
        {
            items.Add(new MediaPoolItemResponse(
                source.MediaRef,
                source.Kind,
                source.FileName,
                source.MimeType,
                source.SizeBytes,
                source.Category,
                ProbeFor(source.StorageKey),
                memberships.TryGetValue(source.Key, out List<MediaBinMembershipResponse>? pins) ? pins : [],
                FilmstripPath(projectId, source.MediaRef)));
        }

        // The probe the derivative run stored for this file wins (probe at ingest, R05.1): it is the
        // file's own measurement. The newest VideoAnalyze artifact is the fallback for a source no
        // derivative run has probed yet.
        MediaProbeResponse? ProbeFor(string storageKey) =>
            stored.TryGetValue(storageKey, out MediaSourceProbe? own)
                ? StoredMediaProbes.ToResponse(own)
                : ArtifactProbeFor(storageKey);

        MediaProbeResponse? ArtifactProbeFor(string storageKey) =>
            probes.TryGetValue(storageKey, out VideoAnalysisMedia? media) && media is not null
                ? new MediaProbeResponse(
                    media.DurationSec,
                    media.FpsNum,
                    media.FpsDen,
                    media.Width,
                    media.Height,
                    // The six T-105 fields, mapped one-for-one. Each is null on an artifact that
                    // predates them — which is the only honest reading, since the analysis that
                    // produced it never probed them. A blank string is folded to null rather than
                    // passed through: "" is not a codec name or a container name, it is the same
                    // "not known" the contract states as null, and the contract says this field is
                    // never an empty string.
                    BlankToNull(media.VideoCodec),
                    BlankToNull(media.AudioCodec),
                    BlankToNull(media.Container),
                    media.AudioChannels,
                    media.AudioSampleRateHz,
                    media.BitrateBps,
                    // Known only when the analysis probed codecs at all: a named audio codec is
                    // sound, a named video codec with no audio codec is a silent file, and an
                    // artifact that named neither predates codec probing and says nothing.
                    !string.IsNullOrWhiteSpace(media.AudioCodec)
                        ? true
                        : !string.IsNullOrWhiteSpace(media.VideoCodec) ? false : null)
                : null;

        return Ok(items);
    }

    /// <summary>
    /// Every density of one source's filmstrip index, with each sheet's bytes URL resolved
    /// server-side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The sidecar is passed through, not re-derived.</b> <c>densitySec</c>, the tile geometry and
    /// each sheet's <c>firstThumbIndex</c>/<c>thumbCount</c>/<c>firstTimeSec</c> are copied from the
    /// document <c>FilmstripIndex.Build</c> wrote. Recomputing them from the row set or from the
    /// source's duration would be exactly the guesswork the writer refuses to do (the <c>fps</c>
    /// filter's decimation and the container's declared duration disagree, and the rounded-up
    /// difference is the off-by-one that makes hover-scrub land on the wrong thumbnail).
    /// </para>
    /// <para>
    /// <b>The row-side resolution is the one thing this endpoint adds.</b> Storage keys never leave
    /// this service, so a client handed the raw sidecar would hold a <c>storageLeaf</c> it cannot turn
    /// into bytes. Each sheet therefore gets the URL of the row that OWNS its density plus its own
    /// index in the sidecar's array — never a leaf-to-row match, because a density is one row (the
    /// primary sheet) and its other sheets have no row at all. See
    /// <see cref="MediaFilmstripSheetResponse"/> and <see cref="GetFilmstripSheetContent"/>.
    /// </para>
    /// <para>
    /// <b>Two honest absences, neither an error.</b> A source with no index at all is <c>200</c> with
    /// <c>densities: []</c>, and every sheet of a density that DOES exist is listed with its URL —
    /// whether that sheet's bytes have been uploaded yet is the sheet route's answer (a per-sheet
    /// <c>404</c>), never a hole in the index. A sidecar that cannot be fetched or parsed is logged
    /// and its DENSITY is omitted, leaving the rest of the answer intact — one unreadable sidecar must
    /// not turn the whole scrub strip into a failure.
    /// </para>
    /// </remarks>
    [HttpGet(FileMediaRefFilmstripRouteTemplate)]
    [HttpGet(StepResultMediaRefFilmstripRouteTemplate)]
    public async Task<ActionResult<MediaFilmstripResponse>> GetFilmstrip(
        Guid projectId,
        string fileId,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        if (!TryParseMediaRef(fileId, out MediaSourceRef sourceRef))
        {
            return NotFound();
        }

        // Resolved through the same single source-resolution path the derivative routes use, so
        // "foreign", "deleted", "not media" and "not here" are one answer everywhere.
        if (await IsMediaSourceHeldAsync(projectId, sourceRef, ct)) return OutputGate.Unavailable();

        if (await FindMediaSourceAsync(projectId, sourceRef, ct) is null)
        {
            return NotFound();
        }

        IQueryable<MediaDerivative> query = FilmstripRowsOf(projectId, sourceRef);

        List<MediaDerivative> rows = await query
            .OrderBy(d => d.CreatedAt)
            .ThenBy(d => d.Id)
            .ToListAsync(ct);

        List<MediaFilmstripDensityResponse> densities =
            await ResolveFilmstripDensitiesAsync(projectId, rows, ct);

        return Ok(new MediaFilmstripResponse(new MediaFilmstripDocument(densities)));
    }

    /// <summary>
    /// Every non-deleted derivative row of one source, as a flat list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>{fileId}</c> segment is the source's OPAQUE <c>mediaRef</c> token, not a bare GUID, so
    /// the source's kind travels with the reference.
    /// </para>
    /// <para>
    /// An empty list is a normal answer meaning "no derivative has been generated yet". There is no
    /// <c>state</c> field: this endpoint cannot distinguish "not yet" from "failed" from
    /// "unsupported", so it claims none of them.
    /// </para>
    /// </remarks>
    [HttpGet(FileMediaRefRouteTemplate)]
    [HttpGet(StepResultMediaRefRouteTemplate)]
    public async Task<ActionResult<List<MediaDerivativeResponse>>> ListDerivatives(
        Guid projectId,
        string fileId,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        // The route constraint guarantees the token's SHAPE, but not that its body is a parseable
        // GUID (36 dashes would pass the constraint) and not that it exists in this project.
        if (!TryParseMediaRef(fileId, out MediaSourceRef sourceRef))
        {
            return NotFound();
        }

        if (await IsMediaSourceHeldAsync(projectId, sourceRef, ct)) return OutputGate.Unavailable();

        if (await FindMediaSourceAsync(projectId, sourceRef, ct) is null)
        {
            return NotFound();
        }

        IQueryable<MediaDerivative> query = _db.MediaDerivatives.Where(d => d.ProjectId == projectId);

        query = sourceRef.Kind == MediaSourceKind.ProjectFile
            ? query.Where(d => d.SourceKind == MediaSourceKind.ProjectFile &&
                               d.SourceProjectFileId == sourceRef.SourceId)
            : query.Where(d => d.SourceKind == MediaSourceKind.StepResult &&
                               d.SourceStepResultId == sourceRef.SourceId);

        // Materialized as entities and mapped afterwards, deliberately: the content URL is built by
        // a client-side helper, and keeping it out of the LINQ projection means the query itself
        // stays trivially translatable on any provider.
        List<MediaDerivative> derivatives = await query
            .OrderBy(d => d.CreatedAt)
            .ThenBy(d => d.Id)
            .ToListAsync(ct);

        List<MediaDerivativeResponse> rows = derivatives
            .Select(d => new MediaDerivativeResponse(
                d.Id,
                d.Kind,
                d.MimeType,
                d.SizeBytes,
                d.DurationSec,
                d.Width,
                d.Height,
                DerivativeContentPath(projectId, d.Id),
                CameraProfileOf(d)))
            .ToList();

        return Ok(rows);
    }

    /// <summary>
    /// Streams one derivative's bytes.
    /// </summary>
    /// <remarks>
    /// Addressing is by ROW id, never by kind: one source can legitimately hold several rows of the
    /// same kind (different params), so a kind-keyed route could not say which. Ownership is
    /// re-checked here rather than trusted from the caller's earlier list request, and the row's
    /// storage key is checked against this project's own prefix before the object store is touched.
    /// </remarks>
    [HttpGet("derivatives/{derivativeId:guid}/content")]
    public async Task<IActionResult> GetDerivativeContent(Guid projectId, Guid derivativeId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        MediaDerivative? derivative = await _db.MediaDerivatives
            .FirstOrDefaultAsync(d => d.Id == derivativeId && d.ProjectId == projectId, ct);

        if (derivative is null) return NotFound();

        if (!await IsDerivativeSourceVisibleAsync(derivative, projectId, ct)) return NotFound();
        if (await OutputGate.IsDerivativeHeldAsync(_db, derivative, ct)) return OutputGate.Unavailable();
        if (await DerivativeSourceRefusalAsync(derivative, projectId, ct) is { } sourceRefusal) return sourceRefusal;

        string expectedPrefix = $"projects/{projectId}/";
        if (string.IsNullOrWhiteSpace(derivative.StorageKey) ||
            !derivative.StorageKey.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            // 404 rather than the sibling controllers' 403/Forbid: a key outside this project is
            // indistinguishable from a row that does not exist, and saying "forbidden" would confirm
            // the row is real.
            return NotFound();
        }

        string fileName = Path.GetFileName(derivative.StorageKey);
        string contentType = string.IsNullOrWhiteSpace(derivative.MimeType)
            ? InferMediaMimeType(fileName) ?? "application/octet-stream"
            : derivative.MimeType;

        // Ranges are pushed down to the object store (RangedObjectStreamer): the proxy and the editor
        // both scrub, and File(stream, enableRangeProcessing: true) silently ignores Range on S3's
        // non-seekable stream — answering bytes=0-99 with 200 and the whole object, which leaves a
        // <video> with no seekable range and makes WebKit refuse to play it (api D4).
        //
        // The streamer is also where the response is hardened: it decides inline vs attachment
        // through SafeContentPolicy's MIME allowlist, sets the anti-sniffing/containment headers and
        // builds the RFC 6266 disposition (which sanitises the file name itself), so this route keeps
        // both the Range pushdown and the policy that replaced the old hand-rolled header.
        try
        {
            return await RangedObjectStreamer.StreamAsync(
                this, _s3, BucketOf(derivative), derivative.StorageKey, contentType, fileName, ct);
        }
        catch (AmazonS3Exception ex) when (IsMissingObjectError(ex))
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Streams sprite sheet <paramref name="index"/> of a filmstrip density.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A density is ONE row, and this is what makes its other sheets reachable.</b> The writer
    /// inserts one <c>media_derivatives</c> row per (source, kind, variant) — whose
    /// <see cref="MediaDerivative.StorageKey"/> names the primary sprite sheet — and uploads EVERY
    /// artifact of that density into one shared directory. Sheets 1..N-1 therefore have no row of
    /// their own, and a leaf-to-row lookup would find nothing for them. So the sheet is addressed by
    /// the row that OWNS the density plus its index in that density's own sidecar array, and its
    /// object key is derived as <b>the directory of the row's own key plus the sidecar's
    /// <c>storageLeaf</c> for that index</b> — which is exactly the layout the writer uploaded into.
    /// </para>
    /// <para>
    /// <b>Every failure is <c>404</c>.</b> An unknown or foreign derivative, a row that is not a
    /// filmstrip, a missing or out-of-prefix index pointer, an out-of-range index, a sidecar that
    /// cannot be read, and an object that is not in the store all answer the same empty not-found —
    /// never <c>403</c> (which would confirm the row exists) and never <c>500</c> (a density whose
    /// sheets have not been uploaded yet is a normal state, not a server fault). This is what lets a
    /// partially materialized density degrade SHEET BY SHEET rather than failing the index that
    /// describes it.
    /// </para>
    /// <para>
    /// <b>The derived key is checked before the store is touched.</b> The row's own key must sit
    /// under this project's <c>projects/{projectId}/</c> prefix, the leaf must be a bare file name
    /// (no <c>/</c>, no <c>\</c>, no <c>..</c>), and the derived key is checked against the same
    /// prefix again. A sidecar is data the object store handed back, so a hostile or corrupt leaf
    /// must not be able to climb out of the directory its row owns — defence in depth on top of the
    /// fact that a path segment containing <c>/</c> could not be a file name in the first place.
    /// </para>
    /// </remarks>
    [HttpGet("derivatives/{derivativeId:guid}/sheet/{index:int}/content")]
    public async Task<IActionResult> GetFilmstripSheetContent(
        Guid projectId,
        Guid derivativeId,
        int index,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        MediaDerivative? derivative = await _db.MediaDerivatives
            .FirstOrDefaultAsync(d => d.Id == derivativeId && d.ProjectId == projectId, ct);

        if (derivative is null) return NotFound();

        if (!await IsDerivativeSourceVisibleAsync(derivative, projectId, ct)) return NotFound();
        if (await OutputGate.IsDerivativeHeldAsync(_db, derivative, ct)) return OutputGate.Unavailable();
        if (await DerivativeSourceRefusalAsync(derivative, projectId, ct) is { } sourceRefusal) return sourceRefusal;

        // Only a filmstrip row owns a density and an index. A proxy or a peaks row that happened to
        // carry an index pointer is not a sprite-sheet owner, and answering 404 keeps that from
        // becoming a second, accidental way to read an arbitrary object.
        if (derivative.Kind != MediaDerivativeKind.Filmstrip) return NotFound();

        string expectedPrefix = $"projects/{projectId}/";

        if (string.IsNullOrWhiteSpace(derivative.StorageKey) ||
            !derivative.StorageKey.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(derivative.IndexStorageKey) ||
            !derivative.IndexStorageKey.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            return NotFound();
        }

        FilmstripSidecar? sidecar = await TryReadFilmstripSidecarAsync(projectId, derivative.IndexStorageKey, ct);
        if (sidecar is null) return NotFound();

        IReadOnlyList<FilmstripSidecarSheet> sheets = sidecar.Sheets ?? [];

        // A negative index, or one past the end, is simply not a sheet this density has.
        if (index < 0 || index >= sheets.Count) return NotFound();

        if (!TryDeriveSheetKey(derivative.StorageKey, sheets[index].StorageLeaf, expectedPrefix, out string? sheetKey))
        {
            return NotFound();
        }

        string leaf = Path.GetFileName(sheetKey);

        // Inferred from the LEAF, not from the row: the row's MimeType describes the primary sheet,
        // and a density whose sheets are not all the same type would otherwise be served as one.
        string contentType = InferMediaMimeType(leaf) ?? "application/octet-stream";

        // Ranged for the same reason the sibling content route is: a client fetching one sprite
        // sheet should not have to take the whole object in one piece. As there, the streamer also
        // applies SafeContentPolicy, so the sheet keeps the same hardening as the primary content.
        try
        {
            return await RangedObjectStreamer.StreamAsync(
                this, _s3, BucketOf(derivative), sheetKey, contentType, leaf, ct);
        }
        catch (AmazonS3Exception ex) when (IsMissingObjectError(ex))
        {
            // The row and the index both describe this sheet, but its object was never uploaded (or
            // has been removed). That is this sheet's own absence and nothing else's.
            return NotFound();
        }
    }

    /// <summary>
    /// A derivative of a quarantined project file is not readable: the source row is the authority,
    /// and an already-built proxy/filmstrip must not outlive the block that hid the original.
    /// </summary>
    private async Task<IActionResult?> DerivativeSourceRefusalAsync(
        MediaDerivative derivative, Guid projectId, CancellationToken ct)
    {
        // Quarantined, blocked and missing sources stay hidden (404). A source that is only pending (or
        // in an outage) answers the same 409 code as download, so the client can ask again.
        IActionResult? Map(PublishModerationOutcome? outcome) =>
            outcome is null ? null
            : outcome.Code == "CONTENT_BLOCKED" ? NotFound()
            : outcome.ToRefusal(this);

        if (derivative.SourceProjectFileId is { } sourceFileId)
        {
            if (await _db.ProjectFiles.AnyAsync(
                    f => f.Id == sourceFileId
                         && f.ProjectId == projectId
                         && f.ModerationStatus == FileModerationStatus.Quarantined,
                    ct))
            {
                return NotFound();
            }

            // The source file may share the key of a step output: then its stored verdict decides too.
            if (_publishModeration is null) return null;
            string? sourceKey = await _db.ProjectFiles.AsNoTracking()
                .Where(f => f.Id == sourceFileId && f.ProjectId == projectId)
                .Select(f => f.StorageKey)
                .FirstOrDefaultAsync(ct);
            return sourceKey is null ? null : Map(await _publishModeration.GateStoredFileAsync(projectId, sourceKey, ct));
        }

        // A step-result source has no ProjectFile row to carry ModerationStatus. Its render is a
        // publication, so gate it through the same publish service (the 24-hour content-hash cache
        // makes a repeat cheap). A missing/out-of-scope source is hidden rather than served.
        if (derivative.SourceStepResultId is not { } stepResultId || _publishModeration is null)
            return null;

        string outputPrefix = $"projects/{projectId}/outputFiles/";
        string? storageKey = await _db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.Id == stepResultId
                        && r.WorkflowExecution.ProjectId == projectId
                        && r.OutputStorageKey != null
                        && r.OutputStorageKey.StartsWith(outputPrefix))
            .Select(r => r.OutputStorageKey)
            .FirstOrDefaultAsync(ct);

        if (storageKey is null) return NotFound();

        Guid organizationId = await _db.Projects
            .Where(p => p.Id == projectId)
            .Select(p => p.OrganizationId)
            .FirstOrDefaultAsync(ct);

        PublishModerationOutcome outcome = await _publishModeration.EvaluateObjectAsync(
            projectId,
            storageKey,
            InferMediaMimeType(Path.GetFileName(storageKey)) ?? "application/octet-stream",
            new ModerationAttribution(
                organizationId, projectId, UserId: null, ExecutionId: null, StepResultId: stepResultId,
                ModerationSurface.Publish, ModerationContentKind.Text),
            ct);

        return outcome.Allowed ? null : Map(outcome);
    }

    /// <summary>
    /// The bucket a derivative's objects live in: the one its writer recorded, else this service's
    /// own (the same fallback <c>ProjectFileMediaController</c> applies to a file row).
    /// </summary>
    private string BucketOf(MediaDerivative derivative) =>
        string.IsNullOrWhiteSpace(derivative.StorageBucket) ? _storage.BucketName : derivative.StorageBucket;

    /// <summary>
    /// The editor's explicit, lazy first-open call: ask for one owned source's derivative set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This writes nothing.</b> No <c>media_derivatives</c> row, no bin row, no request log — the
    /// only durable effect is the published <see cref="MediaDerivativesRequested"/>, and exactly one
    /// of those per accepted call. "Lazily on first open" is THIS call and never a side effect of a
    /// GET: a list request that quietly triggered ffmpeg work would make a page load spend CPU, and
    /// a retried GET would spend it twice.
    /// </para>
    /// <para>
    /// A failed ETag HEAD does not fail the request: the event's <c>SourceEtag</c> is then empty,
    /// which the consumer must read as "no content identity available" rather than as a poison
    /// message.
    /// </para>
    /// </remarks>
    /// <summary>
    /// P6: every annotation in the project, keyed by mediaRef. Sources that were never annotated
    /// have no row and are simply absent — the pool is the list of sources, this is the list of notes.
    /// </summary>
    [HttpGet("annotations")]
    public async Task<ActionResult<List<MediaAnnotationResponse>>> ListAnnotations(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        List<MediaAnnotation> rows = await _db.MediaAnnotations
            .Where(a => a.ProjectId == projectId)
            .OrderByDescending(a => a.UpdatedAt)
            .ToListAsync(ct);

        return Ok(rows.Select(ToAnnotationResponse).ToList());
    }

    /// <summary>
    /// P6: writes (creating or updating) one source's annotation. The source must resolve inside
    /// this project (<c>404</c> otherwise — the same answer as every other route on this controller);
    /// keywords are normalised (trimmed, lower-cased, distinct, at most 32 of at most 64 characters),
    /// the note is capped at 2000 characters and the colour label must be one of the closed words.
    /// </summary>
    [HttpPut(FileMediaRefAnnotationRouteTemplate)]
    [HttpPut(StepResultMediaRefAnnotationRouteTemplate)]
    public async Task<ActionResult<MediaAnnotationResponse>> UpsertAnnotation(
        Guid projectId,
        string fileId,
        [FromBody] UpsertMediaAnnotationRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (!TryParseMediaRef(fileId, out MediaSourceRef sourceRef))
            return BadRequest(new { message = "mediaRef must be 'file:<guid>' or 'step_result:<guid>'." });

        if (await IsMediaSourceHeldAsync(projectId, sourceRef, ct)) return OutputGate.Unavailable();

        if (await FindMediaSourceAsync(projectId, sourceRef, ct) is null)
            return NotFound();

        if (request.Note is { Length: > 2000 })
            return BadRequest(new { message = "note is at most 2000 characters" });

        string? colorLabel = null;
        bool setColor = false;
        if (request.HasColorLabel)
        {
            setColor = true;
            if (request.ColorLabel.ValueKind == JsonValueKind.String)
            {
                colorLabel = request.ColorLabel.GetString();
                if (colorLabel is not null && !ReelBolt.Shared.Editing.MediaSmartFilter.ColorLabels.Contains(colorLabel))
                    return BadRequest(new { message = $"colorLabel must be one of {string.Join(", ", ReelBolt.Shared.Editing.MediaSmartFilter.ColorLabels)} or null" });
            }
            else if (request.ColorLabel.ValueKind != JsonValueKind.Null)
            {
                return BadRequest(new { message = "colorLabel must be a label word or null" });
            }
        }

        MediaAnnotation? row = sourceRef.Kind == MediaSourceKind.ProjectFile
            ? await _db.MediaAnnotations.FirstOrDefaultAsync(a => a.ProjectId == projectId && a.SourceKind == MediaSourceKind.ProjectFile && a.SourceProjectFileId == sourceRef.SourceId, ct)
            : await _db.MediaAnnotations.FirstOrDefaultAsync(a => a.ProjectId == projectId && a.SourceKind == MediaSourceKind.StepResult && a.SourceStepResultId == sourceRef.SourceId, ct);

        bool created = row is null;
        row ??= new MediaAnnotation
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            SourceKind = sourceRef.Kind,
            SourceProjectFileId = sourceRef.Kind == MediaSourceKind.ProjectFile ? sourceRef.SourceId : null,
            SourceStepResultId = sourceRef.Kind == MediaSourceKind.StepResult ? sourceRef.SourceId : null
        };

        if (request.Keywords is { } keywords)
            row.KeywordsJson = JsonSerializer.Serialize(ReelBolt.Shared.Editing.MediaSmartFilter.NormalizeKeywords(keywords));
        if (request.Note is not null)
            row.Note = request.Note.Length == 0 ? null : request.Note.Trim();
        if (setColor)
            row.ColorLabel = colorLabel;
        if (request.Favourite is { } favourite)
            row.Favourite = favourite;
        if (request.Flag is { } flag)
            row.Flag = flag;
        row.UpdatedAt = DateTime.UtcNow;

        if (created) _db.MediaAnnotations.Add(row);
        await _db.SaveChangesAsync(ct);

        return created
            ? StatusCode(StatusCodes.Status201Created, ToAnnotationResponse(row))
            : Ok(ToAnnotationResponse(row));
    }

    private static MediaAnnotationResponse ToAnnotationResponse(MediaAnnotation row)
    {
        Guid sourceId = row.SourceKind == MediaSourceKind.ProjectFile ? row.SourceProjectFileId ?? Guid.Empty : row.SourceStepResultId ?? Guid.Empty;
        string mediaRef = row.SourceKind == MediaSourceKind.ProjectFile ? $"file:{sourceId:D}" : $"step_result:{sourceId:D}";
        return new MediaAnnotationResponse(mediaRef, MediaBinsController.ReadKeywords(row.KeywordsJson), row.Note, row.ColorLabel, row.Favourite, row.Flag, row.UpdatedAt);
    }

    [HttpPost("derivatives/request")]
    public async Task<IActionResult> RequestDerivatives(
        Guid projectId,
        [FromBody] RequestMediaDerivativesRequest? request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (request is null || string.IsNullOrWhiteSpace(request.MediaRef))
        {
            return BadRequest(new { message = "mediaRef is required." });
        }

        // A malformed token is a client error (400), not a missing resource: the route constraint
        // cannot help here, since this token arrives in the body rather than as a path segment.
        if (!TryParseMediaRef(request.MediaRef, out MediaSourceRef sourceRef))
        {
            return BadRequest(new { message = "mediaRef must be 'file:<guid>' or 'step_result:<guid>'." });
        }

        if (await IsMediaSourceHeldAsync(projectId, sourceRef, ct)) return OutputGate.Unavailable();

        MediaSourceDescriptor? source = await FindMediaSourceAsync(projectId, sourceRef, ct);
        if (source is null) return NotFound();

        if (!TryParseKinds(request.Kinds, out IReadOnlyList<MediaDerivativeKind> kinds, out string? kindError))
        {
            return BadRequest(new { message = kindError });
        }

        int proxyVariant = 0;
        if (!string.IsNullOrWhiteSpace(request.CameraProfile))
        {
            if (!CameraProfiles.IsKnown(request.CameraProfile))
            {
                return BadRequest(new
                {
                    message = $"cameraProfile must be one of: {string.Join(", ", CameraProfiles.Ids)}.",
                    code = "camera_profile_unknown"
                });
            }

            if (source.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) != true)
            {
                return BadRequest(new
                {
                    message = "A camera profile only applies to video sources.",
                    code = "camera_profile_not_applicable_source"
                });
            }

            // HDR (HLG/PQ) keeps its own tone-map; the stored probe knows. A source never probed is allowed through,
            // and the engine refuses it at production time if it turns out to be HDR.
            Dictionary<string, MediaSourceProbe> probes = await StoredMediaProbes.LoadAsync(
                _db, projectId, [source.StorageKey], ct);
            if (probes.TryGetValue(source.StorageKey, out MediaSourceProbe? stored)
                && (!string.IsNullOrEmpty(stored.Hdr) || !string.IsNullOrEmpty(HdrKindOf(stored.ColorTransfer))))
            {
                return BadRequest(new
                {
                    message = "HDR footage is tone-mapped on its own; a camera profile does not apply.",
                    code = "camera_profile_not_applicable_hdr"
                });
            }

            proxyVariant = CameraProfileVariants.VariantOf(request.CameraProfile);
        }

        string requestedBy = string.IsNullOrWhiteSpace(request.RequestedBy)
            ? MediaDerivativeRequests.RequestedByEditorOpen
            : TrimRequestedBy(request.RequestedBy);

        // No derivative is made from a file before its CSAM verdict (docs/csam.md). The answer is the same 202:
        // nothing was produced yet either way, and CsamScanSweeper requests the default set once it has a verdict.
        if (!await ReelBolt.Inference.Api.Services.Csam.CsamScanGate.AllowsDerivativesAsync(
                _db, projectId, sourceRef.Kind == MediaSourceKind.ProjectFile ? sourceRef.SourceId : null, ct))
        {
            return Accepted(new
            {
                mediaRef = source.MediaRef,
                kinds = kinds.Select(WireName).ToList(),
                requestedBy
            });
        }

        string? sourceEtag = await _storage.GetObjectETagAsync(projectId, source.StorageKey, ct);

        // This request is also how a video whose upload request was lost, or that came in through a synced
        // folder, gets its derivatives: the project's analysis switch is asked for it like for an upload.
        bool analyze = sourceRef.Kind == MediaSourceKind.ProjectFile
            && await ReelBolt.Inference.Api.Services.Uploads.UploadDerivativeRequest.AnalyzeForWorkflowsAsync(
                _uploadAnalysis, projectId, sourceRef.SourceId, source.MimeType, _logger, ct);

        await _publishEndpoint.Publish(new MediaDerivativesRequested(
            projectId,
            sourceRef,
            sourceRef.Kind == MediaSourceKind.ProjectFile ? sourceRef.SourceId : null,
            source.StorageKey,
            sourceEtag ?? string.Empty,
            source.MimeType,
            kinds,
            requestedBy,
            DateTime.UtcNow,
            ProxyVariant: proxyVariant,
            AnalyzeForWorkflows: analyze), ct);

        return Accepted(new
        {
            mediaRef = source.MediaRef,
            kinds = kinds.Select(WireName).ToList(),
            requestedBy
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Pool assembly
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds the pool's source descriptors: project files with a media MIME type, union the
    /// unregistered renders, newest first, capped at <see cref="PoolCap"/>.
    /// </summary>
    private async Task<List<MediaSourceDescriptor>> ListPoolSourcesAsync(Guid projectId, CancellationToken ct)
    {
        List<ProjectFile> files = await _db.ProjectFiles
            .Where(f => f.ProjectId == projectId
                        && f.CsamStatus != CsamScanStatus.Quarantined
                        && f.ModerationStatus != FileModerationStatus.Quarantined)
            .Where(f => f.MimeType != null && f.MimeType != string.Empty)
            .Where(f => f.MimeType.StartsWith("video/") ||
                        f.MimeType.StartsWith("audio/") ||
                        f.MimeType.StartsWith("image/"))
            .OrderByDescending(f => f.UploadedAt)
            .Take(PoolCap)
            .ToListAsync(ct);

        List<MediaSourceDescriptor> sources = new(files.Count);
        sources.AddRange(files.Select(ToDescriptor));

        // A render exists only as a step result's output key. Trailing slash makes the prefix a true
        // path-segment prefix, so "outputFiles-tmp/" can never be swept in (the same guard
        // OutputsController uses).
        string outputPrefix = $"projects/{projectId}/outputFiles/";

        List<RenderRow> renders = await _db.WorkflowStepResults
            .Where(r => r.WorkflowExecution.ProjectId == projectId &&
                        r.OutputStorageKey != null &&
                        r.OutputStorageKey.StartsWith(outputPrefix))
            .OrderByDescending(r => r.CompletedAt ?? r.ExecutedAt)
            .Take(PoolCap)
            .Select(r => new RenderRow(r.Id, r.OutputStorageKey!, r.CompletedAt ?? r.ExecutedAt))
            .ToListAsync(ct);

        if (renders.Count > 0)
        {
            // Only the renders' own keys are asked about, so this stays a bounded IN clause rather
            // than a full project-file scan.
            List<string> renderKeys = renders
                .Select(r => r.StorageKey)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            HashSet<string> registeredKeys = (await _db.ProjectFiles
                    .Where(f => f.ProjectId == projectId && renderKeys.Contains(f.StorageKey))
                    .Select(f => f.StorageKey)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);

            foreach (RenderRow render in renders)
            {
                // Registered renders are already the file: item above; listing them again would show
                // one clip as two.
                if (registeredKeys.Contains(render.StorageKey))
                {
                    continue;
                }

                string fileName = Path.GetFileName(render.StorageKey);
                string? mimeType = InferMediaMimeType(fileName);

                // An object under outputFiles that is not a playable/displayable media file (an EDL
                // or a manifest written by mistake, a sidecar) is not a pool source: `kind` has to
                // be one of video/audio/image, and inventing a fourth would break the contract.
                if (mimeType is null)
                {
                    continue;
                }

                sources.Add(new MediaSourceDescriptor(
                    $"step_result:{render.Id:D}",
                    new MediaSourceKey(MediaSourceKind.StepResult, render.Id),
                    KindFromMimeType(mimeType),
                    fileName,
                    mimeType,
                    // A step result carries no object size and this endpoint does not HEAD each
                    // render to find one — that would be exactly the per-item storage probe the pool
                    // is specified never to do. 0 is the media-pool contract's stated "not known".
                    SizeBytes: 0,
                    MediaCategory.OutputFiles,
                    render.StorageKey,
                    render.SortAt));
            }
        }

        sources = await WithoutHeldAsync(projectId, sources, files, ct);

        return sources
            .OrderByDescending(s => s.SortAt)
            .ThenBy(s => s.MediaRef, StringComparer.Ordinal)
            .Take(PoolCap)
            .ToList();
    }

    /// <summary>
    /// Reads the newest <c>VideoAnalyze</c> artifact this project produced and indexes its
    /// <c>Sources[]</c> by storage key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Newest artifact", one of them — per the P2-B1 decision, not a walk back through earlier
    /// artifacts. The consequence is stated rather than hidden: when a later execution analyzed a
    /// different source, a source that only an earlier artifact probed reports unprobed here. That
    /// is a missing reading, never a wrong one.
    /// </para>
    /// <para>
    /// A storage failure, a missing object or a corrupt artifact degrades to "nothing is probed" and
    /// a warning log. The pool is a listing; a listing that 500s because one old artifact cannot be
    /// parsed would be worse than a listing with no probe data.
    /// </para>
    /// </remarks>
    private async Task<Dictionary<string, VideoAnalysisMedia>> LoadNewestAnalysisProbesAsync(
        Guid projectId,
        CancellationToken ct)
    {
        Dictionary<string, VideoAnalysisMedia> byStorageKey = new(StringComparer.Ordinal);

        string artifactPrefix = $"projects/{projectId}/agentFiles/video-analysis/";

        // Only a VideoAnalyze step's artifact has sources[]: a compile step's EDL shares the prefix
        // and is written later (T-107), so "newest under the prefix" alone would pick it.
        string? artifactKey = await _db.WorkflowStepResults
            .Where(r => r.WorkflowExecution.ProjectId == projectId &&
                        r.WorkflowStep.StepType == StepType.VideoAnalyze &&
                        r.ArtifactStorageKey != null &&
                        r.ArtifactStorageKey.StartsWith(artifactPrefix))
            .OrderByDescending(r => r.CompletedAt ?? r.ExecutedAt)
            .Select(r => r.ArtifactStorageKey)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrEmpty(artifactKey))
        {
            return byStorageKey;
        }

        try
        {
            await using Stream stream = await _storage.DownloadAsync(projectId, artifactKey, ct);

            VideoAnalysisArtifact? artifact =
                await JsonSerializer.DeserializeAsync<VideoAnalysisArtifact>(stream, ArtifactJsonOptions, ct);

            IReadOnlyList<VideoAnalysisSourceInfo> sources = artifact?.Sources ?? [];

            foreach (VideoAnalysisSourceInfo source in sources)
            {
                if (!string.IsNullOrEmpty(source.StorageKey))
                {
                    // First wins: duplicate keys within one artifact would be a writer bug, and the
                    // earliest (source-index order) reading is the one the rest of the artifact
                    // agrees with.
                    byStorageKey.TryAdd(source.StorageKey, source.Media);
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Could not read the video-analysis artifact {ArtifactStorageKey} for project {ProjectId}; " +
                "serving the media pool with no probe metadata",
                artifactKey,
                projectId);
        }

        return byStorageKey;
    }

    // ---------------------------------------------------------------------------------------------
    // Bin membership
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Every live bin of this project that holds one of the pool's own sources, keyed by that source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two bounded queries for the whole pool, never one per item.</b> The project's live bin ids
    /// are read first (the soft-delete query filter on <see cref="MediaBin"/> is what makes "live"
    /// true, so no explicit <c>DeletedAt</c> predicate is written here and none can drift), then the
    /// pins inside exactly those bins whose source is one of the pool's own ids. A bin of another
    /// project is therefore not merely unresolvable but unreachable — the id set never contains it.
    /// </para>
    /// <para>
    /// Membership is driven by the <c>media_bin_items</c> columns, not by re-deriving a source from
    /// the pool descriptor, so a pin whose two source columns disagree with its <c>SourceKind</c> is
    /// simply not returned rather than being attributed to the wrong media.
    /// </para>
    /// </remarks>
    private async Task<Dictionary<MediaSourceKey, List<MediaBinMembershipResponse>>> LoadBinMembershipsAsync(
        Guid projectId,
        IReadOnlyList<MediaSourceDescriptor> sources,
        CancellationToken ct)
    {
        Dictionary<MediaSourceKey, List<MediaBinMembershipResponse>> memberships = [];

        if (sources.Count == 0)
        {
            return memberships;
        }

        List<Guid> liveBinIds = await _db.MediaBins
            .Where(b => b.ProjectId == projectId)
            .Select(b => b.Id)
            .ToListAsync(ct);

        if (liveBinIds.Count == 0)
        {
            return memberships;
        }

        List<Guid> fileSourceIds = sources
            .Where(s => s.Key.Kind == MediaSourceKind.ProjectFile)
            .Select(s => s.Key.SourceId)
            .Distinct()
            .ToList();

        List<Guid> stepResultSourceIds = sources
            .Where(s => s.Key.Kind == MediaSourceKind.StepResult)
            .Select(s => s.Key.SourceId)
            .Distinct()
            .ToList();

        List<MediaBinItem> items = await _db.MediaBinItems
            .Where(i => liveBinIds.Contains(i.BinId))
            .Where(i =>
                (i.SourceKind == MediaSourceKind.ProjectFile &&
                 i.SourceProjectFileId != null &&
                 fileSourceIds.Contains(i.SourceProjectFileId.Value)) ||
                (i.SourceKind == MediaSourceKind.StepResult &&
                 i.SourceStepResultId != null &&
                 stepResultSourceIds.Contains(i.SourceStepResultId.Value)))
            .ToListAsync(ct);

        foreach (MediaBinItem item in items)
        {
            MediaSourceKey key = MediaSourceKey.Of(item.SourceKind, item.SourceKind == MediaSourceKind.ProjectFile
                ? item.SourceProjectFileId
                : item.SourceStepResultId);

            if (!memberships.TryGetValue(key, out List<MediaBinMembershipResponse>? pins))
            {
                memberships[key] = pins = [];
            }

            pins.Add(new MediaBinMembershipResponse(item.BinId, item.Id));
        }

        // Ordered by binId then itemId, in the runtime's own Guid order. The order is applied in
        // memory rather than translated into SQL so it is the SAME order on every provider — a
        // client re-rendering the list must not see it reshuffle because the backing store changed.
        foreach (MediaSourceKey key in memberships.Keys.ToList())
        {
            memberships[key] = memberships[key]
                .OrderBy(pin => pin.BinId)
                .ThenBy(pin => pin.ItemId)
                .ToList();
        }

        return memberships;
    }

    // ---------------------------------------------------------------------------------------------
    // Filmstrip index
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// One source's non-deleted <c>Filmstrip</c> derivative rows, scoped to THIS project and to that
    /// source's own kind and id.
    /// </summary>
    /// <remarks>
    /// The kind filter is not decoration: without it a proxy or a peaks row whose key happened to end
    /// in the same leaf could be matched as a sheet, and the client would be handed a video URL where
    /// it expects a sprite sheet.
    /// </remarks>
    private IQueryable<MediaDerivative> FilmstripRowsOf(Guid projectId, MediaSourceRef sourceRef)
    {
        IQueryable<MediaDerivative> query = _db.MediaDerivatives
            .Where(d => d.ProjectId == projectId && d.Kind == MediaDerivativeKind.Filmstrip);

        return sourceRef.Kind == MediaSourceKind.ProjectFile
            ? query.Where(d => d.SourceKind == MediaSourceKind.ProjectFile &&
                               d.SourceProjectFileId == sourceRef.SourceId)
            : query.Where(d => d.SourceKind == MediaSourceKind.StepResult &&
                               d.SourceStepResultId == sourceRef.SourceId);
    }

    /// <summary>
    /// Resolves one source's filmstrip rows into densities, <c>densitySec</c> ascending.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One row per density, and the row's index pointer is the density's identity.</b> The writer
    /// inserts one <c>media_derivatives</c> row per (source, kind, variant) — the primary sprite sheet
    /// of that density — and sets <c>IndexStorageKey</c> on it. So each distinct non-null index
    /// pointer IS one density, and the row that carries it is the row every sheet of that density is
    /// addressed through (see <see cref="GetFilmstripSheetContent"/>).
    /// </para>
    /// <para>
    /// <b>The rows are walked in a deterministic (createdAt, id) order and the FIRST one wins a
    /// pointer collision</b>, so a duplicate row naming one index cannot make the same density appear
    /// twice or make the ownership of its sheets alternate between requests.
    /// </para>
    /// <para>
    /// Every key is re-checked against this project's own <c>projects/{projectId}/</c> prefix before
    /// the object store is touched — the same guard the derivative-content route applies — so a row
    /// whose key was written outside the project cannot be fetched at all.
    /// </para>
    /// </remarks>
    private async Task<List<MediaFilmstripDensityResponse>> ResolveFilmstripDensitiesAsync(
        Guid projectId,
        IReadOnlyList<MediaDerivative> filmstripRows,
        CancellationToken ct)
    {
        string projectPrefix = $"projects/{projectId}/";

        List<MediaDerivative> densityRows = [];
        HashSet<string> seenIndexKeys = new(StringComparer.Ordinal);

        foreach (MediaDerivative row in filmstripRows)
        {
            if (string.IsNullOrWhiteSpace(row.IndexStorageKey) || !seenIndexKeys.Add(row.IndexStorageKey))
            {
                continue;
            }

            densityRows.Add(row);
        }

        List<MediaFilmstripDensityResponse> densities = new(densityRows.Count);

        foreach (MediaDerivative row in densityRows)
        {
            string indexKey = row.IndexStorageKey!;

            if (!indexKey.StartsWith(projectPrefix, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Filmstrip index {IndexStorageKey} for project {ProjectId} is outside the project's own prefix; " +
                    "its density is omitted",
                    indexKey,
                    projectId);
                continue;
            }

            FilmstripSidecar? sidecar = await TryReadFilmstripSidecarAsync(projectId, indexKey, ct);

            if (sidecar is null)
            {
                // A density whose index cannot be read is omitted, and the request still answers 200
                // with whatever else resolved: one unreadable sidecar must not cost the client the
                // densities that ARE readable. TryReadFilmstripSidecarAsync already logged why.
                continue;
            }

            densities.Add(ToDensityResponse(sidecar, projectId, row.Id));
        }

        return densities
            .OrderBy(density => density.DensitySec)
            .ToList();
    }

    /// <summary>
    /// Reads and parses one filmstrip index sidecar, or returns <c>null</c> (with a warning) when it
    /// cannot be read at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both callers degrade, neither throws.</b> The index route omits the density whose sidecar
    /// this returned null for; the sheet route answers <c>404</c>. A missing object, a transport
    /// failure and malformed JSON are one outcome here on purpose: this service cannot act on the
    /// difference, and a caller that branched on it would be inventing a distinction the store never
    /// promised.
    /// </para>
    /// <para>
    /// The <c>IsCancellationRequested</c> clause is the one non-swallowing case: an aborted request
    /// must surface as a cancellation, never as a quietly successful read of nothing.
    /// </para>
    /// </remarks>
    private async Task<FilmstripSidecar?> TryReadFilmstripSidecarAsync(
        Guid projectId,
        string indexStorageKey,
        CancellationToken ct)
    {
        try
        {
            await using Stream stream = await _storage.DownloadAsync(projectId, indexStorageKey, ct);

            FilmstripSidecar? sidecar = await JsonSerializer.DeserializeAsync<FilmstripSidecar>(
                stream,
                FilmstripIndexJsonOptions,
                ct);

            if (sidecar is null)
            {
                _logger.LogWarning(
                    "The filmstrip index {IndexStorageKey} for project {ProjectId} deserialized to nothing",
                    indexStorageKey,
                    projectId);
            }

            return sidecar;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Could not read the filmstrip index {IndexStorageKey} for project {ProjectId}",
                indexStorageKey,
                projectId);

            return null;
        }
    }

    /// <summary>
    /// Maps one parsed sidecar to a wire density, giving each sheet the URL of the row that owns the
    /// density plus that sheet's own index.
    /// </summary>
    /// <remarks>
    /// The sidecar's own fields are copied through unchanged. <c>sheetCount</c> in particular is the
    /// DOCUMENT's value rather than the sheet list's length: a disagreement between the two is a fact
    /// about the writer that a client debugging a scrub strip needs to see, not one this endpoint
    /// should silently paper over. The sheet's index is its position in THIS list, which is the same
    /// list <see cref="GetFilmstripSheetContent"/> indexes into — so the URL a client is handed and
    /// the leaf the route reads back always describe the same sheet.
    /// </remarks>
    private static MediaFilmstripDensityResponse ToDensityResponse(
        FilmstripSidecar sidecar,
        Guid projectId,
        Guid derivativeId)
    {
        IReadOnlyList<FilmstripSidecarSheet> sheets = sidecar.Sheets ?? [];

        List<MediaFilmstripSheetResponse> mapped = new(sheets.Count);

        for (int i = 0; i < sheets.Count; i++)
        {
            FilmstripSidecarSheet sheet = sheets[i];

            mapped.Add(new MediaFilmstripSheetResponse(
                sheet.FirstThumbIndex,
                sheet.ThumbCount,
                sheet.FirstTimeSec,
                FilmstripSheetContentPath(projectId, derivativeId, i)));
        }

        return new MediaFilmstripDensityResponse(
            sidecar.DensitySec,
            sidecar.ThumbWidth,
            sidecar.ThumbHeight,
            sidecar.Cols,
            sidecar.Rows,
            sidecar.SheetCount,
            mapped);
    }

    /// <summary>
    /// Derives a sheet's object key as the directory of its owning row's key plus the sidecar's leaf,
    /// or returns false when the leaf cannot be used.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The directory is the layout the writer uploaded into.</b> Every artifact of one density goes
    /// up as <c>{ScratchSubdirectory}/{sourceHash}/{artifact.FileName}</c>, so all of a density's
    /// sheets are siblings of the row's own key and a sheet's key is exactly that sibling.
    /// </para>
    /// <para>
    /// <b>A leaf must be a bare file name.</b> <c>/</c>, <c>\</c> and <c>..</c> are each rejected, and
    /// the key it produces is checked against the project prefix once more: the sidecar is an object
    /// the store handed back, so a corrupt or hostile <c>storageLeaf</c> must not be able to name an
    /// object outside the directory its own row owns. This is defence in depth — a leaf with a
    /// separator could not be a file name the writer ever produced — but it is the difference between
    /// trusting a document and validating it.
    /// </para>
    /// </remarks>
    private static bool TryDeriveSheetKey(
        string rowStorageKey,
        string? storageLeaf,
        string expectedPrefix,
        out string sheetKey)
    {
        sheetKey = string.Empty;

        if (string.IsNullOrWhiteSpace(storageLeaf) ||
            storageLeaf.Contains('/') ||
            storageLeaf.Contains('\\') ||
            storageLeaf.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        string? directory = Path.GetDirectoryName(rowStorageKey);

        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        sheetKey = $"{directory}/{storageLeaf}";

        // The derived key can only ever start with the row's own directory, which was already checked
        // against the prefix — but the check is written against the RESULT rather than argued from
        // that, so a future change to how the key is composed cannot quietly widen what is readable.
        return sheetKey.StartsWith(expectedPrefix, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Source resolution
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Resolves a <c>mediaRef</c> to a description of the media it names, or <c>null</c> when it is
    /// not a media source of this project.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the single place a reference becomes a storage key, so "another project", "deleted"
    /// and "not media at all" cannot drift into three different answers on different endpoints.
    /// </para>
    /// <para>
    /// Every lookup is project-scoped at the database, and the step-result case additionally
    /// re-checks the key's own prefix, exactly as the pool does.
    /// </para>
    /// </remarks>
    /// <summary>
    /// A derivative of a quarantined project file is absent, even if its row survived a failed purge.
    /// Step-result derivatives have no project file source and are always visible.
    /// </summary>
    private async Task<bool> IsDerivativeSourceVisibleAsync(
        MediaDerivative derivative, Guid projectId, CancellationToken ct)
    {
        if (derivative.SourceKind == MediaSourceKind.ProjectFile)
        {
            if (derivative.SourceProjectFileId is not { } fileId) return true;
            ProjectFile? source = await _db.ProjectFiles
                .FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId, ct);
            return source is not null && source.CsamStatus != CsamScanStatus.Quarantined;
        }

        // A render can be registered as a project file AND addressed as a step result. The derivative
        // row names the same bytes in SourceStorageKey, so a quarantined project file (or a move that
        // completed, leaving the key under quarantine/) hides every step-result derivative too.
        if (derivative.SourceKind == MediaSourceKind.StepResult)
        {
            if (derivative.SourceStorageKey.Contains("/quarantine/", StringComparison.Ordinal))
                return false;
            return !await _db.ProjectFiles.AnyAsync(
                f => f.ProjectId == projectId
                     && (f.StorageKey == derivative.SourceStorageKey
                         || f.CsamQuarantineKey == derivative.SourceStorageKey)
                     && (f.CsamStatus == CsamScanStatus.Quarantined || f.CsamQuarantineKey != null), ct);
        }

        return true;
    }

    /// <summary>True when the referenced source's bytes are not served.</summary>
    private async Task<bool> IsMediaSourceHeldAsync(Guid projectId, MediaSourceRef sourceRef, CancellationToken ct)
    {
        if (sourceRef.Kind == MediaSourceKind.StepResult)
        {
            bool inProject = await _db.WorkflowStepResults.AsNoTracking()
                .AnyAsync(r => r.Id == sourceRef.SourceId && r.WorkflowExecution.ProjectId == projectId, ct);
            return inProject && await OutputGate.IsResultHeldAsync(_db, sourceRef.SourceId, ct);
        }

        ProjectFile? file = await _db.ProjectFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == sourceRef.SourceId && f.ProjectId == projectId, ct);
        return file is not null && await OutputGate.IsFileHeldAsync(_db, file, ct);
    }

    /// <summary>The pool without the sources whose bytes are not served (one marker read).</summary>
    private async Task<List<MediaSourceDescriptor>> WithoutHeldAsync(
        Guid projectId, List<MediaSourceDescriptor> sources, IReadOnlyCollection<ProjectFile> files, CancellationToken ct)
    {
        HashSet<Guid> heldFiles = await OutputGate.HeldFileIdsAsync(_db, projectId, files, ct);
        HashSet<Guid> heldResults = await OutputGate.HeldResultIdsAsync(
            _db, projectId,
            sources.Where(s => s.Key.Kind == MediaSourceKind.StepResult).Select(s => s.Key.SourceId).ToList(), ct);
        if (heldFiles.Count == 0 && heldResults.Count == 0) return sources;
        return sources.Where(s => !(s.Key.Kind == MediaSourceKind.StepResult
            ? heldResults.Contains(s.Key.SourceId)
            : heldFiles.Contains(s.Key.SourceId))).ToList();
    }

    private async Task<MediaSourceDescriptor?> FindMediaSourceAsync(Guid projectId,
        MediaSourceRef sourceRef,
        CancellationToken ct)
    {
        if (sourceRef.Kind == MediaSourceKind.ProjectFile)
        {
            ProjectFile? file = await _db.ProjectFiles
                .FirstOrDefaultAsync(f => f.Id == sourceRef.SourceId
                                          && f.ProjectId == projectId
                                          && f.ModerationStatus != FileModerationStatus.Quarantined, ct);

            // A quarantined source is absent everywhere: list, derivatives, annotations and reads.
            return file is null || file.CsamStatus == CsamScanStatus.Quarantined || !IsMediaMimeType(file.MimeType)
                ? null
                : ToDescriptor(file);
        }

        string outputPrefix = $"projects/{projectId}/outputFiles/";

        WorkflowStepResult? render = await _db.WorkflowStepResults
            .FirstOrDefaultAsync(r => r.Id == sourceRef.SourceId &&
                                      r.WorkflowExecution.ProjectId == projectId &&
                                      r.OutputStorageKey != null &&
                                      r.OutputStorageKey.StartsWith(outputPrefix), ct);

        if (render?.OutputStorageKey is not { } storageKey)
        {
            return null;
        }

        string fileName = Path.GetFileName(storageKey);
        string? mimeType = InferMediaMimeType(fileName);

        return mimeType is null
            ? null
            : new MediaSourceDescriptor(
                $"step_result:{render.Id:D}",
                new MediaSourceKey(MediaSourceKind.StepResult, render.Id),
                KindFromMimeType(mimeType),
                fileName,
                mimeType,
                SizeBytes: 0,
                MediaCategory.OutputFiles,
                storageKey,
                render.CompletedAt ?? render.ExecutedAt);
    }

    private static MediaSourceDescriptor ToDescriptor(ProjectFile file) =>
        new(
            $"file:{file.Id:D}",
            new MediaSourceKey(MediaSourceKind.ProjectFile, file.Id),
            KindFromMimeType(file.MimeType),
            string.IsNullOrWhiteSpace(file.OriginalFileName)
                ? Path.GetFileName(file.StorageKey)
                : file.OriginalFileName,
            file.MimeType,
            file.SizeBytes,
            CategoryFromColumn(file.Category),
            file.StorageKey,
            file.UploadedAt);

    // ---------------------------------------------------------------------------------------------
    // Small helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Parses an opaque <c>mediaRef</c> token into its typed halves. Accepts both prefixes and never
    /// reports which one matched — a caller that can ask "is this a file: token?" is one step away
    /// from branching on it, which the token exists to prevent.
    /// </summary>
    private static bool TryParseMediaRef(string? mediaRef, out MediaSourceRef sourceRef)
    {
        sourceRef = null!;

        if (string.IsNullOrWhiteSpace(mediaRef))
        {
            return false;
        }

        Match match = MediaRefPattern.Match(mediaRef);
        if (!match.Success || !Guid.TryParse(match.Groups["id"].Value, out Guid id))
        {
            return false;
        }

        MediaSourceKind kind = match.Groups["kind"].Value == "file"
            ? MediaSourceKind.ProjectFile
            : MediaSourceKind.StepResult;

        sourceRef = new MediaSourceRef(kind, id);
        return true;
    }

    /// <summary>
    /// Closes a caller-supplied kind-name list onto <see cref="MediaDerivativeKind"/>, or falls back
    /// to <see cref="MediaDerivativeRequests.DefaultKinds"/> when none were named.
    /// </summary>
    /// <remarks>
    /// This is where the wire's names become the event's closed, typed array — an unknown name is a
    /// <c>400</c> naming the offending value, so nothing open-ended can reach
    /// <see cref="MediaDerivativesRequested.Kinds"/>. Lookups go through a name table built from the
    /// enum itself rather than <c>Enum.TryParse</c>, which would also accept an ordinal string like
    /// <c>"0"</c> and quietly resolve it against this C# declaration. Duplicates are collapsed,
    /// order preserved.
    /// </remarks>
    private static bool TryParseKinds(
        IReadOnlyList<string>? names,
        out IReadOnlyList<MediaDerivativeKind> kinds,
        out string? error)
    {
        error = null;

        if (names is null || names.Count == 0)
        {
            kinds = MediaDerivativeRequests.DefaultKinds;
            return true;
        }

        List<MediaDerivativeKind> parsed = [];

        foreach (string name in names)
        {
            if (name is null || !DerivativeKindByWireName.TryGetValue(name, out MediaDerivativeKind kind))
            {
                kinds = [];
                error = $"'{name}' is not a media derivative kind; expected one of " +
                        $"{string.Join(", ", DerivativeKindByWireName.Keys)}.";
                return false;
            }

            if (!parsed.Contains(kind))
            {
                parsed.Add(kind);
            }
        }

        kinds = parsed;
        return true;
    }

    /// <summary>
    /// The wire name (camelCase, the encoding <see cref="MediaWireEnumJsonConverter"/> writes) of
    /// each <see cref="MediaDerivativeKind"/>, and the only way this controller turns a caller's
    /// string back into the enum.
    /// </summary>
    private static readonly Dictionary<string, MediaDerivativeKind> DerivativeKindByWireName =
        Enum.GetValues<MediaDerivativeKind>().ToDictionary(
            WireName,
            kind => kind,
            StringComparer.OrdinalIgnoreCase);

    private static string WireName(MediaDerivativeKind kind) =>
        JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());

    private static string? HdrKindOf(string? colorTransfer) => colorTransfer?.Trim().ToLowerInvariant() switch
    {
        "arib-std-b67" => "hlg",
        "smpte2084" => "pq",
        _ => null
    };

    /// <summary>
    /// The camera profile a converted proxy row was made with, read from its spec JSON and checked against the closed
    /// catalogue; null for any other row. The spec is the row's own identity, so this never touches a storage key.
    /// </summary>
    private static string? CameraProfileOf(MediaDerivative row)
    {
        if (row.Kind != MediaDerivativeKind.Proxy || string.IsNullOrEmpty(row.ParamsJson)) return null;
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(row.ParamsJson);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("cameraProfile", out System.Text.Json.JsonElement id)
                   && id.ValueKind == System.Text.Json.JsonValueKind.String
                   && CameraProfiles.TryGet(id.GetString(), out CameraProfileDefinition? profile)
                ? profile.Id
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string DerivativeContentPath(Guid projectId, Guid derivativeId) =>
        $"/api/v1/projects/{projectId:D}/media/derivatives/{derivativeId:D}/content";

    /// <summary>
    /// One sprite sheet's route: the row that owns the density, plus the sheet's index in that
    /// density's own sidecar array.
    /// </summary>
    /// <remarks>
    /// Built by the same helper shape as <see cref="DerivativeContentPath"/>, so neither path is
    /// assembled by hand at a call site and the two cannot drift apart.
    /// </remarks>
    private static string FilmstripSheetContentPath(Guid projectId, Guid derivativeId, int sheetIndex) =>
        $"/api/v1/projects/{projectId:D}/media/derivatives/{derivativeId:D}/sheet/{sheetIndex}/content";

    /// <summary>
    /// A source's filmstrip-index route, built from the source's own opaque token.
    /// </summary>
    /// <remarks>
    /// The <c>mediaRef</c> is interpolated VERBATIM, exactly as a client would have to send it back:
    /// it is the only spelling the route constraint admits (<c>file:&lt;guid&gt;</c> /
    /// <c>step_result:&lt;guid&gt;</c>), and the token this controller minted is already in that
    /// canonical lower-case form. Re-deriving it here from an id would be a second place the token
    /// grammar lives.
    /// </remarks>
    private static string FilmstripPath(Guid projectId, string mediaRef) =>
        $"/api/v1/projects/{projectId:D}/media/{mediaRef}/filmstrip";

    /// <summary>
    /// Folds a blank string to <c>null</c>, leaving a real value untouched.
    /// </summary>
    /// <remarks>
    /// The media contract states "not known" as <c>null</c> and promises a codec, a container or a
    /// sample-rate reading is never an empty string. An empty or whitespace-only value in an artifact
    /// carries no more information than an absent one — a codec named <c>""</c> does not exist — so it
    /// is reported as the absence it is, rather than passed through as a value the client would have
    /// to special-case.
    /// </remarks>
    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool IsMediaMimeType(string? mimeType) =>
        !string.IsNullOrEmpty(mimeType) &&
        (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
         mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
         mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The pool/derivative kind of a MIME type. Callers only ever pass a
    /// <see cref="IsMediaMimeType"/> value, so the three-way split is total.
    /// </summary>
    private static MediaKind KindFromMimeType(string mimeType)
    {
        if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return MediaKind.Video;
        if (mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return MediaKind.Audio;
        return MediaKind.Image;
    }

    /// <summary>
    /// Maps a <c>ProjectFile.Category</c> column value to the contract enum. The column is validated
    /// to one of the three at every write site; anything else (a value written by an older build or
    /// by hand) falls back to <c>userFiles</c>, which is the column's own default rather than an
    /// invented fourth category.
    /// </summary>
    private static MediaCategory CategoryFromColumn(string? category) => category switch
    {
        "agentFiles" => MediaCategory.AgentFiles,
        "outputFiles" => MediaCategory.OutputFiles,
        _ => MediaCategory.UserFiles,
    };

    /// <summary>
    /// The MIME type of a stored object, from its extension.
    /// </summary>
    /// <remarks>
    /// A step-result render has no MIME column, so its extension is the only thing there is; this
    /// table deliberately mirrors <c>OutputsController.InferContentType</c> and
    /// <c>ProjectFilesController.MediaExtensionMimeTypes</c> (extended with the image types the pool
    /// accepts). Returns null for anything that is not video/audio/image, which is how a non-media
    /// object under <c>outputFiles/</c> is kept out of the pool.
    /// </remarks>
    private static string? InferMediaMimeType(string fileName) =>
        MimeTypeByExtension.TryGetValue(Path.GetExtension(fileName), out string? mimeType) ? mimeType : null;

    private static readonly Dictionary<string, string> MimeTypeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
        [".mkv"] = "video/x-matroska",
        [".avi"] = "video/x-msvideo",
        [".gif"] = "image/gif",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".m4a"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".flac"] = "audio/flac",
        [".ogg"] = "audio/ogg",
    };

    private static bool IsMissingObjectError(AmazonS3Exception ex) =>
        ex.StatusCode == System.Net.HttpStatusCode.NotFound ||
        string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.Ordinal) ||
        string.Equals(ex.ErrorCode, "NotFound", StringComparison.Ordinal);

    /// <summary>
    /// A free-text <c>RequestedBy</c> is carried onto the event, trimmed and length-capped. The
    /// three values this service itself writes live on <see cref="MediaDerivativeRequests"/>; the
    /// event's own shape is <c>string?</c> so a future producer is not forced through a release of
    /// this service to say something new.
    /// </summary>
    private static string TrimRequestedBy(string requestedBy)
    {
        string trimmed = requestedBy.Trim();
        return trimmed.Length <= 64 ? trimmed : trimmed[..64];
    }

    // ---------------------------------------------------------------------------------------------
    // Local shapes
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// One pool/derivative source, resolved to everything the endpoints need — including the storage
    /// key, which never leaves this controller.
    /// </summary>
    /// <remarks>
    /// <see cref="Key"/> carries the source's typed identity alongside the opaque
    /// <see cref="MediaRef"/> that goes on the wire. The two are not redundant: the token is what a
    /// client round-trips, and this is what a bin-membership or derivative query compares columns
    /// against — parsing the token back into halves at every use site would be a second
    /// implementation of the token grammar.
    /// </remarks>
    private sealed record MediaSourceDescriptor(
        string MediaRef,
        MediaSourceKey Key,
        MediaKind Kind,
        string FileName,
        string MimeType,
        long SizeBytes,
        MediaCategory Category,
        string StorageKey,
        DateTime SortAt);

    /// <summary>
    /// A media source's typed identity, as a value usable as a dictionary key.
    /// </summary>
    /// <remarks>
    /// The kind is part of the key, not decoration: a <c>ProjectFile</c> and a <c>WorkflowStepResult</c>
    /// can carry the same Guid — the ids come from different tables — and folding them together would
    /// hand one source the other's bins.
    /// </remarks>
    private readonly record struct MediaSourceKey(MediaSourceKind Kind, Guid SourceId)
    {
        /// <summary>
        /// The key of a bin item's own reference, taking the id from the column its kind names — and
        /// <see cref="Guid.Empty"/> when that column is null, which can never match a real pool source
        /// because a pool descriptor's id is never empty.
        /// </summary>
        public static MediaSourceKey Of(MediaSourceKind kind, Guid? sourceId) =>
            new(kind, sourceId ?? Guid.Empty);
    }

    /// <summary>A step result that carries an <c>output_storage_key</c>, projected for the pool.</summary>
    private sealed record RenderRow(Guid Id, string StorageKey, DateTime SortAt);

    // ---------------------------------------------------------------------------------------------
    // The filmstrip sidecar document
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The JSON <b>index sidecar</b> the media writer produced, in <c>FilmstripIndex.Build</c>'s own
    /// shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A private local shape rather than a reference to the writer's own type.</b> The writer's
    /// <c>FilmstripIndex</c> lives in the WorkflowEngine project, which this service does not (and
    /// must not) reference — and even if it could, deserializing into the builder's <i>input</i>
    /// records would tie this reader to the writer's construction path rather than to the document's
    /// published shape.
    /// </para>
    /// <para>
    /// Every member is optional, so a sidecar written by an older or newer builder — one with a field
    /// this reader does not know, or without one it does — still parses, with the unknown member
    /// ignored and the absent one defaulted. Only truly malformed JSON throws, and that is handled by
    /// the caller.
    /// </para>
    /// </remarks>
    private sealed record FilmstripSidecar(
        double DensitySec = 0,
        int ThumbWidth = 0,
        int ThumbHeight = 0,
        int Cols = 0,
        int Rows = 0,
        int SheetCount = 0,
        IReadOnlyList<FilmstripSidecarSheet>? Sheets = null);

    /// <summary>One sheet as the sidecar describes it: its object leaf and its place in the flat thumbnail sequence.</summary>
    /// <remarks>
    /// <see cref="StorageLeaf"/> is a base name, never a key — the sidecar is written by the producer
    /// that knows its own prefix and deliberately names only the leaf, which is exactly what makes it
    /// resolvable by file name against the row set without this reader being told any prefix.
    /// </remarks>
    private sealed record FilmstripSidecarSheet(
        string? StorageLeaf = null,
        int FirstThumbIndex = 0,
        int ThumbCount = 0,
        double FirstTimeSec = 0);
}
