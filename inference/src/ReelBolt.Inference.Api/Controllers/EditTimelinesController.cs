using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.EditTimelines;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Editing.Interchange;
using ReelBolt.Shared.Editing.Proposals;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ValidationResult = ReelBolt.Shared.Editing.ValidationResult;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The v2 edit-timeline API. The four P1 endpoints — list, create (blank or seeded), get, patch —
/// are the write-path contract and are byte-for-byte unchanged since P1; P3 added three read-mostly
/// companions beside them: <c>GET …/{id}/history</c>, <c>GET …/{id}/revisions/{rev}</c> and
/// <c>POST …/{id}/duplicate</c>.
///
/// <para>
/// <b>Restore is not an endpoint.</b> A client restores a revision by fetching it through
/// <c>revisions/{rev}</c> and submitting it as an ordinary <c>PATCH</c>, which produces revision
/// N+1 and keeps the op log append-only; the server never rewrites history and never replays ops.
/// Everything else the editing feature needs later (render, export, import, proposals) is a new
/// ticket, not a variation of this one.
/// </para>
/// <para>
/// <b>Ownership is the outer gate, project scoping is the inner one.</b> Every endpoint loads the
/// project and returns <c>404</c> when it does not exist and <c>403</c> when it exists but belongs
/// to somebody else — the same order <c>TimelinesController</c> uses. Every <c>{id}</c> lookup then
/// ALSO constrains <c>ProjectId == projectId</c> and inherits the soft-delete query filter, so a
/// timeline that is in another project, soft-deleted or nonexistent is <b>404</b> in all three
/// cases and the body never distinguishes them.
/// </para>
/// <para>
/// <b>PATCH is optimistic-concurrency protected twice, on purpose.</b> The explicit
/// <c>Revision != BaseRevision</c> check catches the ordinary stale writer before any work happens;
/// the catch behind it handles the writer that raced between that check and the commit — surfacing
/// either as a <see cref="DbUpdateConcurrencyException"/> or as a unique violation (Postgres 23505)
/// on the op/snapshot <c>(timeline_id, revision)</c> index. All answer <c>409</c> with the same
/// body, and none writes.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/edit-timelines")]
[Authorize]
public class EditTimelinesController : ControllerBase
{
    /// <summary>
    /// Ceiling on the serialized size of a PATCH's <c>document</c>, in bytes (8 MiB). A timeline
    /// document is a bounded JSON structure — the validator caps it at 64 tracks × 5000 items — so
    /// anything past this ceiling is a client defect or an attack, not an edit. Exceeding it is
    /// rejected with <c>413 Payload Too Large</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a POST-parse check and is defence in depth, not the pre-parse control.</b> By the
    /// time this constant is compared, the body has already been received, bound and deserialized
    /// by MVC: the check only stops an oversized document from being <i>persisted</i>, it cannot
    /// stop one from being <i>read</i>. The real pre-parse bound is
    /// <see cref="MaxRequestBodyBytes"/>, applied through <see cref="RequestSizeLimitAttribute"/>
    /// on <c>PatchEditTimeline</c>, which Kestrel enforces while the body is still arriving and
    /// answers <c>413</c> before any binding happens. Both paths answer <c>413</c>; this one is the
    /// narrower, cheaper guard for a body that fits under the transport ceiling yet still
    /// serializes past the document's own budget.
    /// </para>
    /// <para>
    /// Documented and public so the security audit can confirm the bound exists and so the
    /// tests can assert the exact boundary rather than a copy of it.
    /// </para>
    /// </remarks>
    public const int MaxDocumentBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Ceiling on the serialized size of a PATCH's caller-supplied <c>ops</c> blob, in bytes
    /// (1 MiB). The op log is the change closure for ONE revision over a document already capped at
    /// 64 tracks × 5000 items, so 1 MiB is roughly 100× the largest realistic batch while keeping
    /// the durable <c>edit_timeline_ops.ops_json</c> row small. The ops are stored verbatim and
    /// never applied server-side, so an unbounded blob would be pure transfer and storage cost.
    /// Exceeding it is rejected with <c>413 Payload Too Large</c>, and no row is written: no
    /// <c>edit_timeline_ops</c> insert, no document/revision change.
    /// </summary>
    /// <remarks>
    /// Bounds the specific column the body would land in, so it is a POST-parse check like
    /// <see cref="MaxDocumentBytes"/>, not the pre-parse control — that is
    /// <see cref="MaxRequestBodyBytes"/>.
    /// </remarks>
    public const int MaxOpsBytes = 1024 * 1024;

    /// <summary>
    /// Ceiling on a PATCH's entire request body, in bytes: <see cref="MaxDocumentBytes"/> +
    /// <see cref="MaxOpsBytes"/> plus 64 KiB of JSON-envelope headroom (property names, escaping
    /// and the <c>baseRevision</c>/<c>ops</c>/<c>document</c> wrappers).
    /// </summary>
    /// <remarks>
    /// <b>This is the real pre-parse control.</b> <see cref="RequestSizeLimitAttribute"/> on
    /// <c>PatchEditTimeline</c> sets
    /// <c>IHttpMaxRequestBodySizeFeature.MaxRequestBodySize</c> to this value, so Kestrel stops
    /// reading an over-limit body while it is still arriving and answers <c>413 Payload Too
    /// Large</c> — before MVC binds, allocates or deserializes anything. The two per-part ceilings
    /// above are what make this a bound on what the server will actually accept.
    /// </remarks>
    public const int MaxRequestBodyBytes = MaxDocumentBytes + MaxOpsBytes + (64 * 1024);

    /// <summary>
    /// How often a full document snapshot is retained alongside the append-only op log: every
    /// 20th revision, so <c>revision % 20 == 0</c> on the revision a PATCH produces.
    /// </summary>
    public const int SnapshotIntervalRevisions = EditTimelineCommit.SnapshotIntervalRevisions;

    /// <summary>
    /// Sample rate of a blank timeline's audio track. 48 kHz, matching the seeder's own constant —
    /// v1 manifests carry no sample rate and every audio path in the pipeline is 48 kHz.
    /// </summary>
    private const int BlankSampleRate = 48000;

    /// <summary>
    /// The track label a blank timeline's video track is addressed by. Deliberately the seeder's own
    /// <c>video</c> label, so a blank timeline and a seeded one address their video track by the
    /// same deterministic id and a client can rely on the id across the two.
    /// </summary>
    private const string BlankVideoTrackLabel = "V1";

    /// <summary>
    /// The track label a blank timeline's audio track is addressed by. <c>A1</c> is free: the seeder
    /// only ever emits <c>A2</c> (sfx) and <c>A3</c> (music), so the blank audio track can never
    /// collide with a seeded one.
    /// </summary>
    private const string BlankAudioTrackLabel = "A1";

    /// <summary>
    /// The one JSON configuration used to read and write a timeline document, in both directions, so
    /// a stored document always round-trips. Web defaults only (camelCase, case-insensitive) — the
    /// enum encoding is <b>not</b> configured here, it is owned by the enum types themselves
    /// (<c>EditTimelineEnumJsonConverter</c>, applied on the declarations in
    /// <c>ReelBolt.Shared.Data.Models.Enums</c>). One place defines the encoding.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately no converter list.</b> An options-level converter takes precedence over a
    /// type-level <see cref="System.Text.Json.Serialization.JsonConverterAttribute"/>, so a
    /// <see cref="JsonStringEnumConverter"/> here — which has no naming policy and therefore writes
    /// PascalCase — would silently override the attribute and keep storing <c>"kind":"Video"</c>
    /// instead of the schema's <c>"kind":"video"</c>. Adding the new converter to this list instead
    /// would work, but would leave two places defining one encoding, free to drift apart.
    /// </remarks>
    private static readonly JsonSerializerOptions DocumentJsonOptions = EditTimelineJson.Options;

    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IFileStorageService _storage;
    /// <summary>T-004: publish/egress gate; null in tests that do not exercise publication.</summary>
    private readonly PublishModeration? _publishModeration;
    private readonly IWorkflowExecutionService _executions;
    private readonly INonWorkflowChargeService? _charges;
    /// <summary>R06.7: answers whether billing is on; null (tests) reads as off.</summary>
    private readonly IWorkflowCostEstimator? _costEstimator;
    private readonly IAgentChatClientProvider? _chatClients;
    private readonly IOrganizationEntitlements? _entitlements;
    private readonly ILogger<EditTimelinesController> _logger;
    private readonly EditingRetentionOptions _retention;
    private readonly TimeProvider _clock;
    /// <summary>R11.1: the per user+project hourly proposal cap; null (tests) means no cap.</summary>
    private readonly ReelBolt.Inference.Api.Services.Editing.EditProposalRateLimiter? _proposalLimiter;
    /// <summary>R12.2: the hand-off package caps (Editing:HandOff:*) and the per-user concurrent stream limiter.</summary>
    private readonly HandOffOptions _handOff;
    private readonly HandOffLimiter _handOffLimiter;
    private readonly int _maxTranscriptChars;

    private readonly TimelinePreviewOptions _preview;

    public EditTimelinesController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IProjectAccessService projectAccess,
        IFileStorageService storage,
        IWorkflowExecutionService executions,
        INonWorkflowChargeService? charges = null,
        IAgentChatClientProvider? chatClients = null,
        IOrganizationEntitlements? entitlements = null,
        ILogger<EditTimelinesController>? logger = null,
        PublishModeration? publishModeration = null,
        Microsoft.Extensions.Options.IOptions<EditingRetentionOptions>? retention = null,
        TimeProvider? clock = null,
        IWorkflowCostEstimator? costEstimator = null,
        ReelBolt.Inference.Api.Services.Editing.EditProposalRateLimiter? proposalLimiter = null,
        Microsoft.Extensions.Options.IOptions<HandOffOptions>? handOffOptions = null,
        HandOffLimiter? handOffLimiter = null,
        Microsoft.Extensions.Options.IOptions<TimelinePreviewOptions>? previewOptions = null,
        Microsoft.Extensions.Options.IOptions<ReelBolt.Inference.Api.Services.Editing.EditProposalGroundingOptions>? proposalGrounding = null)
    {
        _preview = previewOptions?.Value ?? new TimelinePreviewOptions();
        _maxTranscriptChars = Math.Max(0, proposalGrounding?.Value.MaxTranscriptChars ?? ProposalFactText.DefaultMaxTranscriptChars);
        _handOff = handOffOptions?.Value ?? new HandOffOptions();
        _handOffLimiter = handOffLimiter ?? new HandOffLimiter();
        _proposalLimiter = proposalLimiter;
        _retention = retention?.Value ?? new EditingRetentionOptions();
        _clock = clock ?? TimeProvider.System;
        _costEstimator = costEstimator;
        _charges = charges;
        _chatClients = chatClients;
        _publishModeration = publishModeration;
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _storage = storage;
        _executions = executions;
        // The render endpoint's plan gate (an explicit size above the plan's output cap → 402).
        // Optional so the tests that construct this controller directly keep compiling; the app
        // registers it. Absent means no gate, exactly like an unlimited plan.
        _entitlements = entitlements;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EditTimelinesController>.Instance;
    }

    /// <summary>
    /// Lists this project's edit timelines, newest-updated-first, as lightweight summaries.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<EditTimelineSummaryResponse>>> ListEditTimelines(
        Guid projectId,
        [FromQuery] string? state,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        if (string.Equals(state, "deleted", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(await ListDeletedAsync(projectId, ct));
        }

        // The soft-delete query filter on EditTimelineEntity removes deleted rows here, so no
        // explicit DeletedAt predicate is needed — or wanted, since a second one could drift.
        List<EditTimelineSummaryResponse> summaries = await _db.EditTimelines
            .Where(t => t.ProjectId == projectId)
            .OrderByDescending(t => t.UpdatedAt)
            .ThenBy(t => t.Id)
            .Select(t => new EditTimelineSummaryResponse(
                t.Id,
                t.Name,
                t.Revision,
                t.SeedStepResultId,
                t.CreatedAt,
                t.UpdatedAt,
                null,
                null))
            .ToListAsync(ct);

        return Ok(summaries);
    }

    /// <summary>
    /// The trash: this project's soft-deleted timelines that are still inside the retention window,
    /// most recently deleted first, with when each was deleted and when the purge job will remove it.
    /// </summary>
    private async Task<List<EditTimelineSummaryResponse>> ListDeletedAsync(Guid projectId, CancellationToken ct)
    {
        TimeSpan window = _retention.TrashWindow;
        DateTime cutoff = _clock.GetUtcNow().UtcDateTime - window;

        // IgnoreQueryFilters lifts the soft-delete filter only; the project predicate is the scope.
        List<EditTimelineEntity> rows = await _db.EditTimelines
            .IgnoreQueryFilters()
            .Where(t => t.ProjectId == projectId && t.DeletedAt != null && t.DeletedAt > cutoff)
            .OrderByDescending(t => t.DeletedAt)
            .ThenBy(t => t.Id)
            .ToListAsync(ct);

        return rows
            .Select(t => new EditTimelineSummaryResponse(
                t.Id,
                t.Name,
                t.Revision,
                t.SeedStepResultId,
                t.CreatedAt,
                t.UpdatedAt,
                t.DeletedAt,
                t.DeletedAt!.Value + window))
            .ToList();
    }

    /// <summary>
    /// Restores a timeline from the trash: the same row comes back (name, document, revision,
    /// comments), nothing is copied. Edit permission. 404 when it is not deleted, was purged or is
    /// past the retention window, or belongs to another project or tenant.
    /// </summary>
    [HttpPost("{id:guid}/restore")]
    public async Task<ActionResult<EditTimelineSummaryResponse>> RestoreEditTimeline(
        Guid projectId,
        Guid id,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        DateTime cutoff = now - _retention.TrashWindow;

        EditTimelineEntity? row = await _db.EditTimelines
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == id && t.ProjectId == projectId && t.DeletedAt != null && t.DeletedAt > cutoff, ct);
        if (row is null) return NotFound();

        row.DeletedAt = null;
        row.UpdatedAt = now;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A racing restore or purge got there first: nothing left to restore here.
            _db.ChangeTracker.Clear();
            return NotFound();
        }

        return Ok(new EditTimelineSummaryResponse(
            row.Id, row.Name, row.Revision, row.SeedStepResultId, row.CreatedAt, row.UpdatedAt, null, null));
    }

    /// <summary>Customer-facing wording of the seed-failure 500; the redacted violation rules are returned with it.</summary>
    public const string SeedFailedMessage = "This edit could not be opened in the editor.";

    /// <summary>
    /// The 500's message when both server-built documents still fail validation: the seed failed in
    /// plain words, then the FIRST rule it broke. A violation message is <c>&lt;rule&gt;: &lt;values&gt;</c>,
    /// so the clause before the first colon names the rule.
    /// </summary>
    internal static string SeedFailedMessageWithRule(IReadOnlyList<EditTimelineViolation> violations)
    {
        if (violations.Count == 0)
        {
            return SeedFailedMessage;
        }

        string first = violations[0].Message;
        int separator = first.IndexOf(':');
        string rule = separator > 0 ? first[..separator] : first;
        return $"{SeedFailedMessage} The workflow's timeline breaks an editing rule: {rule}.";
    }

    /// <summary>
    /// Log form of a validation message: a seeded document's clip names come from the compile manifest,
    /// where they can be an AI caption of the customer's footage, and the validator interpolates them as
    /// double-quoted runs (e.g. <c>The clip "…" runs past the end of its source: …</c>). The span from the
    /// first quote to the last is replaced so the log keeps the structural path, the rule and the numbers,
    /// never the label. The
    /// validator's own message is untouched: it is returned to the caller on the normal validation path.
    /// </summary>
    public static string RedactQuotedLabelsForLog(string message)
    {
        // First-to-last, not quote pairing: a label may itself contain quotes (captions routinely do),
        // which would desynchronise a pair-by-pair scan and leave fragments behind. Redacting the whole
        // span is robust to any number of embedded quotes and to more than one quoted name.
        int first = message.IndexOf('"');
        int last = message.LastIndexOf('"');
        if (first < 0 || last <= first) return message;
        return string.Concat(message.AsSpan(0, first), "\"<clip>\"", message.AsSpan(last + 1));
    }

    /// <summary>
    /// Creates a blank timeline, or one seeded from a workflow step result's timeline manifest.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<EditTimelineResponse>> CreateEditTimeline(
        Guid projectId,
        [FromBody] CreateEditTimelineRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { message = "name must not be null or whitespace-only" });
        }

        string name = request.Name.Trim();
        if (HasControlCharacter(name))
        {
            return BadRequest(new { message = NameHasControlCharacterMessage });
        }

        if (name.Length > MaxNameLength)
        {
            return BadRequest(new { message = NameTooLongMessage });
        }

        Guid timelineId = Guid.NewGuid();
        EditTimeline document;
        IReadOnlyList<UnplacedItem> unplaced = [];

        if (request.SeedStepResultId is { } seedStepResultId)
        {
            WorkflowStepResult? stepResult = await _db.WorkflowStepResults
                .Include(r => r.WorkflowExecution)
                .FirstOrDefaultAsync(r => r.Id == seedStepResultId, ct);

            // A step result in another project is indistinguishable from one that does not exist.
            if (stepResult is null || stepResult.WorkflowExecution.ProjectId != projectId)
            {
                return NotFound();
            }

            if (await OutputGate.IsResultHeldAsync(_db, stepResult.Id, ct)) return OutputGate.Unavailable();

            if (string.IsNullOrWhiteSpace(stepResult.TimelineStorageKey))
            {
                return BadRequest(new
                {
                    message = $"step result {seedStepResultId} carries no timeline manifest " +
                              "(timelineStorageKey is null), so it cannot seed a timeline"
                });
            }

            string manifestKey = stepResult.TimelineStorageKey;
            TimelineManifest manifest;
            try
            {
                await using Stream stream = await _storage.DownloadAsync(projectId, manifestKey, ct);
                manifest = await JsonSerializer.DeserializeAsync<TimelineManifest>(stream, DocumentJsonOptions, ct)
                    ?? throw new InvalidOperationException(
                        $"timeline manifest '{manifestKey}' deserialized to null");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The caller supplied a valid step result id and the server could not fetch or read
                // what that step result points at: a failed dependency, not a client error. Not 502:
                // Cloudflare replaces a 502/504 body. The storage key and the exception stay in the
                // log; a response never carries a storage key (security MI-3).
                _logger.LogWarning(
                    ex,
                    "Could not read the timeline manifest {ManifestKey} for step result {StepResultId} in project {ProjectId}",
                    manifestKey, seedStepResultId, projectId);
                return StatusCode(StatusCodes.Status424FailedDependency, new
                {
                    message = "The timeline saved by that workflow step could not be read, so a timeline " +
                              "cannot be started from it. Nothing was created."
                });
            }

            SeedAssetResolver seedResolver = await SeedAssetResolver.LoadAsync(_db, projectId, ct);

            // Camera audio: a cut whose source the derivative run probed as having sound gets it as
            // a companion clip. The stored ingest probe wins; a source it never reached falls back to
            // the VideoAnalyze artifact's audio facts (QA C-323-1); neither -> no camera audio.
            List<string> sourceKeys = (manifest.Sources ?? []).Select(s => s.StorageKey).OfType<string>().ToList();
            Dictionary<string, MediaSourceProbe> probes = await StoredMediaProbes.LoadAsync(
                _db,
                projectId,
                sourceKeys,
                ct);
            Dictionary<string, bool> analysed = sourceKeys.All(probes.ContainsKey)
                ? []
                : await AnalysisAudioFacts.LoadAsync(
                    _db, _storage, _logger, projectId, stepResult.WorkflowExecutionId, ct);
            // T-102: the run's compile chain decomposed onto the ORIGINAL sources (cuts replayed, canvas fill and
            // region effects as editable layers). Null when it adds nothing over the plain seeding below.
            SeededTimeline? lineageSeeded = await LineageSeedResolver.TrySeedAsync(
                _db, _storage, _logger, projectId, stepResult, manifest, timelineId, seedResolver, ct);
            if (lineageSeeded is null && await PlainSeedCensorGuard.PlainSeedWouldRevealAsync(_db, projectId, stepResult, ct))
            {
                return RefuseUncensoredSeed(seedStepResultId, projectId);
            }

            SeededTimeline seededTimeline = lineageSeeded
                ?? EditTimelineSeeder.SeedWithReport(
                    manifest,
                    timelineId,
                    seedResolver.ResolveKey,
                    key => probes.TryGetValue(key, out MediaSourceProbe? probe)
                        ? probe.HasAudio
                        : analysed.TryGetValue(key, out bool hasAudio) && hasAudio,
                    seedResolver.ResolveIdentity,
                    StoredMediaProbes.InputProfileResolver(probes, seedResolver.ResolveKey));
            unplaced = seededTimeline.Unplaced;
            if (unplaced.Count > 0)
            {
                _logger.LogWarning(
                    "Seeding edit timeline from step result {StepResultId} in project {ProjectId} left {Count} item(s) unplaced ({Reasons})",
                    seedStepResultId, projectId, unplaced.Count,
                    string.Join(", ", unplaced.GroupBy(u => u.Reason).Select(g => $"{g.Key}={g.Count()}")));
            }

            document = seededTimeline.Document with
            {
                // The seeder takes ProjectId (and the name) from the manifest, which is v1 data.
                // The row is being created in THIS project by THIS caller, so the route's project id
                // and the caller's name win: a manifest cannot plant a document that claims to
                // belong somewhere else.
                ProjectId = projectId,
                Name = name
            };

            ValidationResult seeded = EditTimelineValidator.Validate(
                document, await BuildValidationContextAsync(projectId, document, ct));

            if (!seeded.IsValid)
            {
                _logger.LogWarning(
                    "Seeded edit timeline for step result {StepResultId} failed validation: {Violations}",
                    seedStepResultId,
                    string.Join("; ", seeded.Violations.Select(v => $"{v.Path}: {RedactQuotedLabelsForLog(v.Message)}")));
                // The lineage document is richer but riskier; the plain seed is the proven path, so try it before refusing,
                // unless the plain seed would show picture the film censors.
                if (await PlainSeedCensorGuard.PlainSeedWouldRevealAsync(_db, projectId, stepResult, ct))
                {
                    return RefuseUncensoredSeed(seedStepResultId, projectId);
                }

                SeededTimeline plainSeeded = EditTimelineSeeder.SeedWithReport(
                    manifest,
                    timelineId,
                    seedResolver.ResolveKey,
                    key => probes.TryGetValue(key, out MediaSourceProbe? probe)
                        ? probe.HasAudio
                        : analysed.TryGetValue(key, out bool hasAudio) && hasAudio,
                    seedResolver.ResolveIdentity,
                    StoredMediaProbes.InputProfileResolver(probes, seedResolver.ResolveKey));
                EditTimeline plainDocument = plainSeeded.Document with { ProjectId = projectId, Name = name };
                ValidationResult plainResult = EditTimelineValidator.Validate(
                    plainDocument, await BuildValidationContextAsync(projectId, plainDocument, ct));
                if (plainResult.IsValid)
                {
                    document = plainDocument;
                    unplaced = plainSeeded.Unplaced;
                    seeded = plainResult;
                }
                else
                {
                    _logger.LogWarning(
                        "Plain seed for step result {StepResultId} also failed validation: {Violations}",
                        seedStepResultId,
                        string.Join("; ", plainResult.Violations.Select(v => $"{v.Path}: {RedactQuotedLabelsForLog(v.Message)}")));
                    // The plain seed is the last attempt, so ITS violations are the ones the 500 reports.
                    seeded = plainResult;
                }
            }

            if (!seeded.IsValid)
            {
                // T-016's self-check proves the seeder's own output is valid, so a violation here is
                // a server-side defect — the caller did nothing wrong. The redacted rules are handed
                // back so the run page can say what broke instead of "an error on our side".
                IReadOnlyList<EditTimelineViolation> violations = seeded.Violations
                    .Select(violation => new EditTimelineViolation(
                        violation.Path, RedactQuotedLabelsForLog(violation.Message)))
                    .ToList();
                return StatusCode(StatusCodes.Status500InternalServerError, new EditTimelineSeedInvalidResponse(
                    SeedFailedMessageWithRule(violations),
                    violations));
            }
        }
        else
        {
            document = CreateBlankDocument(projectId, timelineId, name, request.Settings);
            if (request.Settings is not null && ChosenSettingsProblem(request.Settings) is { } problem)
            {
                return BadRequest(new { message = "The chosen format is not supported: " + problem });
            }
        }

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        EditTimelineEntity entity = new()
        {
            Id = timelineId,
            ProjectId = projectId,
            Name = name,
            DocumentJson = SerializeDocument(document),
            Revision = 1,
            SeedStepResultId = request.SeedStepResultId,
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = null
        };

        _db.EditTimelines.Add(entity);

        // Revision 1 is snapshotted at birth (P3). The PATCH path snapshots every 20th revision
        // only, so without this row the ORIGINAL document of a timeline would be unrecoverable the
        // moment the first PATCH landed — the op log holds the client's ops, and the server never
        // replays them. This is additive: PatchEditTimeline's observable step order is untouched.
        _db.EditTimelineSnapshots.Add(NewSnapshot(timelineId, 1, entity.DocumentJson, EditTimelineSnapshotCause.Milestone, now));

        await _db.SaveChangesAsync(ct);

        return StatusCode(StatusCodes.Status201Created, ToResponse(entity, document) with
        {
            UnplacedItems = unplaced.Select(u => new UnplacedTimelineItemResponse(u.Track, u.ItemId, u.Label, u.Reason)).ToList()
        });
    }

    /// <summary>
    /// Returns one timeline's current document and revision.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EditTimelineResponse>> GetEditTimeline(
        Guid projectId,
        Guid id,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        EditTimelineEntity? timeline = await FindTimelineAsync(projectId, id, ct);
        if (timeline is null) return NotFound();

        if (!TryDeserializeDocument(timeline, out EditTimeline document))
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = $"timeline {id} stores a document that cannot be parsed"
            });
        }

        // R02.5: the Edit check PATCH makes, asked rather than enforced, so a viewer-role member's
        // editor can open read-only instead of failing on its first save.
        bool canEdit = (await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct)).Failure is null;
        return Ok(ToResponse(timeline, document) with { CanEdit = canEdit });
    }

    /// <summary>
    /// Applies one revision to a timeline: replaces the document, advances the revision, appends the
    /// caller's ops verbatim, and snapshots every <see cref="SnapshotIntervalRevisions"/>th revision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order below is the contract, not an implementation detail:
    /// <list type="number">
    /// <item><description>project/timeline lookup, both 404</description></item>
    /// <item><description><see cref="MaxRequestBodyBytes"/> guard, 413 — <b>pre-parse</b>, enforced by Kestrel through <see cref="RequestSizeLimitAttribute"/> on this action while the body is still arriving</description></item>
    /// <item><description><see cref="MaxDocumentBytes"/> (document) and <see cref="MaxOpsBytes"/> (ops) guards, 413 — post-parse defence in depth, each bounding the column it would be written to</description></item>
    /// <item><description>a missing (bound as 0) or non-positive <c>baseRevision</c>, 400 at <c>baseRevision</c>, no write</description></item>
    /// <item><description>stale <c>baseRevision</c>, 409 with <c>currentRevision</c>, no write</description></item>
    /// <item><description>a null <c>document</c>, 400, no write</description></item>
    /// <item><description>header <c>id</c>/<c>projectId</c> not this row's, plus <see cref="EditTimelineValidator"/>, one 400 with every violation, no write</description></item>
    /// <item><description>one transaction: document + revision + op row (+ snapshot), commit</description></item>
    /// <item><description><see cref="DbUpdateConcurrencyException"/> or a unique violation (Postgres 23505 on the op/snapshot <c>(timeline_id, revision)</c> index) from the racing writer, the same 409 — never a 500</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Before every step above, the body has to bind — and every contract enum binds only as a
    /// camelCase string.</b> <see cref="AspectPolicy"/>, <see cref="TrackKind"/>,
    /// <see cref="InterpKind"/>, <see cref="ProvenanceOrigin"/> and <see cref="AssetRefKind"/> carry
    /// <c>EditTimelineEnumJsonConverter</c>, which rejects integers, so a body sending
    /// <c>"aspectPolicy": 0</c> instead of <c>"aspectPolicy": "fit"</c> fails model binding and
    /// <c>[ApiController]</c> answers its ordinary <c>400</c> problem response — <b>not</b> a
    /// <c>500</c>, and not a silent accept of an ambiguous number. The action is never entered, so
    /// nothing is written. The wire format is the schema: an integer that has to be resolved against
    /// a C# declaration is not part of the contract.
    /// </para>
    /// </remarks>
    [HttpPatch("{id:guid}")]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    public async Task<ActionResult<EditTimelineResponse>> PatchEditTimeline(
        Guid projectId,
        Guid id,
        [FromBody] PatchEditTimelineRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        EditTimelineEntity? timeline = await FindTimelineAsync(projectId, id, ct);
        if (timeline is null) return NotFound();

        string? documentJson = request.Document is null ? null : SerializeDocument(request.Document);

        // Serialized exactly ONCE, right here, so the bytes that were measured are the bytes that
        // get persisted: re-serializing at the write would be a second, unmeasured blob free to
        // differ from the one this guard approved.
        string opsJson = SerializeOps(request.Ops);

        if (documentJson is not null && Encoding.UTF8.GetByteCount(documentJson) > MaxDocumentBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new
            {
                message = $"the serialized document exceeds the {MaxDocumentBytes}-byte limit",
                maxDocumentBytes = MaxDocumentBytes
            });
        }

        if (Encoding.UTF8.GetByteCount(opsJson) > MaxOpsBytes)
        {
            // Same 413 shape as the document guard and BEFORE the stale-revision check, so a caller
            // who is both oversized and stale learns about the size first. Nothing has been touched
            // yet: no entity modified, no op row added, no snapshot written.
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new
            {
                message = $"the serialized ops exceed the {MaxOpsBytes}-byte limit",
                maxOpsBytes = MaxOpsBytes
            });
        }

        if (request.BaseRevision < 1)
        {
            // A body without baseRevision binds it to 0, and revisions start at 1: there is no
            // revision 0 to rebase onto, so this is a malformed request, not a conflict.
            return BadRequest(new[]
            {
                new EditTimelineViolation("baseRevision", "baseRevision is required: the revision this edit was made from (1 or more)")
            });
        }

        if (timeline.Revision != request.BaseRevision)
        {
            // Nothing has been touched yet: no entity is modified, no row is added. The 409 is the
            // whole response body's worth of state.
            return Conflict(new EditTimelineConflictResponse(timeline.Revision));
        }

        if (request.Document is not { } document)
        {
            return BadRequest(new[]
            {
                new EditTimelineViolation("document", "document must not be null")
            });
        }

        EditTimelineValidationContext context = await BuildValidationContextAsync(projectId, document, ct);
        ValidationResult validation = EditTimelineValidator.Validate(document, context);

        // The document header must name the row it is saved into (api D7). Create, duplicate and
        // import force these; PATCH checks them, and reports them with the validator's violations
        // so one 400 lists everything to fix.
        List<EditTimelineViolation> violations = [];
        if (document.Id != timeline.Id)
        {
            violations.Add(new EditTimelineViolation("id", $"id must be this timeline's id {timeline.Id}, found {document.Id}"));
        }

        if (document.ProjectId != projectId)
        {
            violations.Add(new EditTimelineViolation("projectId", $"projectId must be this timeline's project {projectId}, found {document.ProjectId}"));
        }

        violations.AddRange(validation.Violations.Select(v => new EditTimelineViolation(v.Path, v.Message)));

        if (violations.Count > 0)
        {
            // A violated document is never persisted — not partially, not "with the bad bit fixed".
            return BadRequest(violations);
        }

        EditCommitResult commit = await EditTimelineCommit.CommitAsync(
            _db,
            timeline,
            request.BaseRevision,
            documentJson!,
            opsJson,
            HasRestoreMarker(request.Ops),
            _clock.GetUtcNow().UtcDateTime,
            _currentUser.UserId == Guid.Empty ? null : _currentUser.UserId,
            _retention.AutoCheckpointWindow,
            ct);

        // A writer that raced this one (concurrency token or the (timeline_id, revision) unique index) wrote nothing: the caller
        // rebases onto the revision it left. A racing DELETE leaves no current revision to rebase onto: the timeline is gone, 404.
        if (commit.Status == EditCommitStatus.Gone) return NotFound();
        if (commit.Status == EditCommitStatus.Conflict) return Conflict(new EditTimelineConflictResponse(commit.Revision));

        return Ok(ToResponse(timeline, document));
    }

    /// <summary>
    /// True when <paramref name="ex"/> is a unique-index violation: Postgres SQLSTATE 23505, or the
    /// SQLite equivalent the transaction-honouring test harnesses use.
    /// </summary>
    // ─────────────────────────────────────────────────────────────────────────────
    // Version history (D14): named versions and automatic checkpoints over edit_timeline_snapshots
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists the restorable versions of a timeline, newest revision first: every stored snapshot that
    /// has not expired. This is the same set <c>revisions/{rev}</c>, <c>history</c> and render accept,
    /// so each listed version restores through the ordinary restore PATCH. View permission.
    /// </summary>
    [HttpGet("{id:guid}/versions")]
    public async Task<ActionResult<EditTimelineVersionsResponse>> ListVersions(Guid projectId, Guid id, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        if (!await TimelineExistsAsync(projectId, id, ct)) return NotFound();

        var rows = await LiveSnapshots(id)
            .OrderByDescending(s => s.Revision)
            .Select(s => new { s.Revision, s.Name, s.Cause, s.CreatedAt, s.ExpiresAt, s.CreatedByUserId })
            .ToListAsync(ct);

        Guid[] userIds = rows.Where(r => r.CreatedByUserId != null).Select(r => r.CreatedByUserId!.Value).Distinct().ToArray();
        Dictionary<Guid, string> names = userIds.Length == 0
            ? []
            : await _db.ApplicationUsers
                .Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.DisplayName })
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        Dictionary<Guid, string> serviceNames = await LoadServiceAccountNamesAsync(userIds, ct);

        return Ok(new EditTimelineVersionsResponse(rows
            .Select(r => new EditTimelineVersionResponse(
                r.Revision,
                r.Name,
                r.Cause,
                r.CreatedAt,
                r.ExpiresAt,
                r.CreatedByUserId is { } uid && names.TryGetValue(uid, out string? display) && !string.IsNullOrWhiteSpace(display) ? display : null,
                r.CreatedByUserId is { } kid && names.TryGetValue(kid, out string? named) && !string.IsNullOrWhiteSpace(named)
                    ? (serviceNames.ContainsKey(kid) ? "apiKey" : "person")
                    : null))
            .ToList()));
    }

    /// <summary>
    /// Saves the CURRENT revision as a version. <c>cause</c> <c>named</c> (the default) needs a name and
    /// is kept for good; <c>beforeProposal</c> is an automatic checkpoint (name optional) that expires
    /// like any other. When the current revision already has a snapshot, that one is reused (and
    /// named, for <c>named</c>) rather than duplicated. Edit permission; 400 on a bad name or cause.
    /// </summary>
    [HttpPost("{id:guid}/versions")]
    public async Task<ActionResult<EditTimelineVersionResponse>> CreateVersion(
        Guid projectId,
        Guid id,
        [FromBody] CreateEditTimelineVersionRequest? request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        string cause = string.IsNullOrWhiteSpace(request?.Cause) ? EditTimelineSnapshotCause.Named : request!.Cause!.Trim();
        if (cause is not (EditTimelineSnapshotCause.Named or EditTimelineSnapshotCause.BeforeProposal))
        {
            return BadRequest(new[]
            {
                new EditTimelineViolation("cause", "cause must be 'named' or 'beforeProposal'")
            });
        }

        if (ValidateVersionName(request?.Name, required: cause == EditTimelineSnapshotCause.Named, out string? name) is { } violation)
        {
            return BadRequest(new[] { violation });
        }

        // Only a 'named' version carries a name (and is kept for good, inside the cap); a checkpoint taken
        // before an AI suggestion is an ordinary expiring one, so a name sent with it is ignored.
        if (cause != EditTimelineSnapshotCause.Named) name = null;

        EditTimelineEntity? timeline = await FindTimelineAsync(projectId, id, ct);
        if (timeline is null) return NotFound();

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        EditTimelineSnapshot? snapshot = await LiveSnapshots(id).FirstOrDefaultAsync(s => s.Revision == timeline.Revision, ct);
        if (cause == EditTimelineSnapshotCause.Named && snapshot?.Name is null && await NamedVersionCapReachedAsync(id, ct))
        {
            return BadRequest(new { message = NamedVersionCapMessage });
        }

        if (snapshot is null)
        {
            // An expired-but-unpruned row for this revision would collide on the unique index: drop it.
            EditTimelineSnapshot? stale = await _db.EditTimelineSnapshots
                .FirstOrDefaultAsync(s => s.TimelineId == id && s.Revision == timeline.Revision, ct);
            if (stale is not null) _db.EditTimelineSnapshots.Remove(stale);

            snapshot = NewSnapshot(id, timeline.Revision, timeline.DocumentJson, cause, now, name);
            _db.EditTimelineSnapshots.Add(snapshot);
        }
        else if (cause == EditTimelineSnapshotCause.Named)
        {
            ApplyName(snapshot, name!);
        }

        try
        {
            if (cause != EditTimelineSnapshotCause.Named)
            {
                await TimelineSnapshotRetention.EnforceCapAsync(_db, id, 1, ct);
            }

            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || IsUniqueViolation(ex))
        {
            // A racing writer took this revision's snapshot first: the caller retries and reuses it.
            _db.ChangeTracker.Clear();
            int? currentRevision = await ReadCurrentRevisionAsync(projectId, id, ct);
            if (currentRevision is null) return NotFound();
            return Conflict(new EditTimelineConflictResponse(currentRevision.Value));
        }

        return StatusCode(StatusCodes.Status201Created, await ToVersionResponseAsync(snapshot, ct));
    }

    /// <summary>Names (or renames) a version. A named version is kept for good. Edit permission; 404 for an unknown or expired version.</summary>
    [HttpPut("{id:guid}/versions/{revision:int}/name")]
    public async Task<ActionResult<EditTimelineVersionResponse>> NameVersion(
        Guid projectId,
        Guid id,
        int revision,
        [FromBody] NameEditTimelineVersionRequest? request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (ValidateVersionName(request?.Name, required: true, out string? name) is { } violation)
        {
            return BadRequest(new[] { violation });
        }

        if (!await TimelineExistsAsync(projectId, id, ct)) return NotFound();
        EditTimelineSnapshot? snapshot = await LiveSnapshots(id).FirstOrDefaultAsync(s => s.Revision == revision, ct);
        if (snapshot is null) return NotFound();

        if (snapshot.Name is null && await NamedVersionCapReachedAsync(id, ct))
        {
            return BadRequest(new { message = NamedVersionCapMessage });
        }

        ApplyName(snapshot, name!);
        await _db.SaveChangesAsync(ct);
        return Ok(await ToVersionResponseAsync(snapshot, ct));
    }

    /// <summary>
    /// Removes a version's name: it becomes an automatic checkpoint again and expires
    /// <c>Editing:AutoCheckpointDays</c> from now (revision 1 never expires). Unnamed already: unchanged.
    /// Edit permission; 404 for an unknown or expired version.
    /// </summary>
    [HttpDelete("{id:guid}/versions/{revision:int}/name")]
    public async Task<ActionResult<EditTimelineVersionResponse>> UnnameVersion(
        Guid projectId,
        Guid id,
        int revision,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (!await TimelineExistsAsync(projectId, id, ct)) return NotFound();
        EditTimelineSnapshot? snapshot = await LiveSnapshots(id).FirstOrDefaultAsync(s => s.Revision == revision, ct);
        if (snapshot is null) return NotFound();

        if (snapshot.Cause == EditTimelineSnapshotCause.Named || snapshot.Name is not null)
        {
            snapshot.Name = null;
            snapshot.Cause = EditTimelineSnapshotCause.Auto;
            snapshot.ExpiresAt = TimelineSnapshotRetention.ExpiryFor(
                EditTimelineSnapshotCause.Auto, snapshot.Revision, _clock.GetUtcNow().UtcDateTime, _retention.AutoCheckpointWindow);
            await _db.SaveChangesAsync(ct);
        }

        return Ok(await ToVersionResponseAsync(snapshot, ct));
    }

    /// <summary>The most named versions one timeline keeps (the 200-checkpoint cap only covers the automatic ones).</summary>
    public const int MaxNamedVersionsPerTimeline = 100;

    public const string NamedVersionCapMessage = "This timeline already has 100 named versions. Remove a name to add another.";

    private async Task<bool> NamedVersionCapReachedAsync(Guid timelineId, CancellationToken ct) =>
        await _db.EditTimelineSnapshots.CountAsync(s => s.TimelineId == timelineId && s.Name != null, ct) >= MaxNamedVersionsPerTimeline;

    private static void ApplyName(EditTimelineSnapshot snapshot, string name)
    {
        snapshot.Name = name;
        snapshot.Cause = EditTimelineSnapshotCause.Named;
        snapshot.ExpiresAt = null;
    }

    /// <summary>Trims and checks a version name: 1..80 characters, no control characters. Null result = valid.</summary>
    private static EditTimelineViolation? ValidateVersionName(string? raw, bool required, out string? name)
    {
        string trimmed = raw?.Trim() ?? string.Empty;
        name = trimmed.Length == 0 ? null : trimmed;
        if (name is null)
        {
            return required ? new EditTimelineViolation("name", "Give the version a name (1 to 80 characters).") : null;
        }

        if (name.Length > MaxVersionNameLength)
        {
            return new EditTimelineViolation("name", $"The name can be at most {MaxVersionNameLength} characters long.");
        }

        return HasControlCharacter(name) ? new EditTimelineViolation("name", NameHasControlCharacterMessage) : null;
    }

    private async Task<EditTimelineVersionResponse> ToVersionResponseAsync(EditTimelineSnapshot s, CancellationToken ct)
    {
        string? createdBy = null;
        string? actorKind = null;
        if (s.CreatedByUserId is { } uid)
        {
            var author = await _db.ApplicationUsers.Where(u => u.Id == uid)
                .Select(u => new { u.DisplayName, u.IsServiceAccount }).FirstOrDefaultAsync(ct);
            createdBy = string.IsNullOrWhiteSpace(author?.DisplayName) ? null : author!.DisplayName;
            if (createdBy is not null)
                actorKind = author!.IsServiceAccount ? "apiKey" : "person";
        }

        return new EditTimelineVersionResponse(s.Revision, s.Name, s.Cause, s.CreatedAt, s.ExpiresAt, createdBy, actorKind);
    }

    /// <summary>
    /// Pins the revision a render starts on (cause beforeRender): when it is the live revision and
    /// has no snapshot yet, one is stored first, so the render's document survives the user's next
    /// edit and the version stays restorable. A revision that already has a snapshot is left alone.
    /// </summary>
    private async Task EnsureRenderSnapshotAsync(EditTimelineEntity timeline, int revision, DateTime now, CancellationToken ct)
    {
        if (revision != timeline.Revision) return;
        if (await _db.EditTimelineSnapshots.AnyAsync(s => s.TimelineId == timeline.Id && s.Revision == revision, ct)) return;

        EditTimelineSnapshot snapshot = NewSnapshot(timeline.Id, revision, timeline.DocumentJson, EditTimelineSnapshotCause.BeforeRender, now);
        _db.EditTimelineSnapshots.Add(snapshot);
        try
        {
            await TimelineSnapshotRetention.EnforceCapAsync(_db, timeline.Id, 1, ct);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Someone snapshotted this revision in the meantime: that row is the pin.
            _db.Entry(snapshot).State = EntityState.Detached;
        }
    }

    /// <summary>A PATCH arriving this long after the previous revision snapshots that revision (cause idle).</summary>
    public static readonly TimeSpan IdleSnapshotGap = EditTimelineCommit.IdleSnapshotGap;

    /// <summary>Longest version name, in characters, after trimming.</summary>
    public const int MaxVersionNameLength = 80;

    /// <summary>True when the PATCH's ops carry the client's <c>{type:'restore'}</c> marker.</summary>
    private static bool HasRestoreMarker(JsonElement? ops)
    {
        if (ops is not { ValueKind: JsonValueKind.Array } array) return false;
        foreach (JsonElement op in array.EnumerateArray())
        {
            if (op.ValueKind == JsonValueKind.Object
                && op.TryGetProperty("type", out JsonElement type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "restore")
            {
                return true;
            }
        }

        return false;
    }

    private EditTimelineSnapshot NewSnapshot(Guid timelineId, int revision, string documentJson, string cause, DateTime now, string? name = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            TimelineId = timelineId,
            Revision = revision,
            DocumentJson = documentJson,
            CreatedAt = now,
            Name = name,
            Cause = cause,
            CreatedByUserId = _currentUser.UserId == Guid.Empty ? null : _currentUser.UserId,
            ExpiresAt = TimelineSnapshotRetention.ExpiryFor(cause, revision, now, _retention.AutoCheckpointWindow)
        };

    /// <summary>
    /// This timeline's restorable snapshots: those not yet past their expiry. History, revisions,
    /// versions and render all read this one set, so every listed version restores.
    /// </summary>
    private IQueryable<EditTimelineSnapshot> LiveSnapshots(Guid timelineId)
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        return _db.EditTimelineSnapshots.Where(s => s.TimelineId == timelineId && (s.ExpiresAt == null || s.ExpiresAt > now));
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
        || (ex.InnerException?.GetType().Name == "SqliteException"
            && ex.InnerException.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase));

    /// <summary>Longest timeline name, in characters after trimming (the <c>name</c> column's limit).</summary>
    public const int MaxNameLength = 200;

    /// <summary>The <c>400</c> message for a timeline name longer than <see cref="MaxNameLength"/>.</summary>
    public const string NameTooLongMessage = "The name can be at most 200 characters long.";

    /// <summary>Appended to the source's name when a duplicate is not given one.</summary>
    private const string CopySuffix = " (copy)";

    /// <summary>The <c>400</c> message for a timeline name carrying a control character.</summary>
    public const string NameHasControlCharacterMessage =
        "The name cannot contain control characters such as line breaks, tabs or invisible codes.";

    /// <summary>
    /// True when <paramref name="name"/> holds any Unicode control character (category Cc: C0,
    /// DEL and C1, NUL included). Postgres refuses NUL in text (SQLSTATE 22021, a 500) and C0
    /// characters break the FCPXML export, so every timeline name is checked on the way in.
    /// </summary>
    private static bool HasControlCharacter(string name) => name.Any(char.IsControl);

    /// <summary>
    /// W0.9: deletes a timeline — soft, through the same <c>DeletedAt</c> the query filter already
    /// hides, so it vanishes from list, get, history, renders and every other <c>{id}</c> action
    /// (all <c>404</c>), while its op log, snapshots and comments stay in place. The hidden
    /// <c>Render:</c> workflows of its renders are untouched: they are bound to the project and go
    /// with it under the existing cascade, and nothing lists them without the timeline.
    /// </summary>
    /// <remarks>Edit permission. Unknown, another project's, another tenant's or already deleted: <c>404</c>.</remarks>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteEditTimeline(Guid projectId, Guid id, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        // The Revision concurrency token guards this UPDATE too: a PATCH or rename that committed
        // between the read and the write makes it match no row. The delete does not depend on the
        // content, so it re-reads and deletes the fresh row (the delete wins); a timeline that a
        // racing delete already removed is 404. Never an unhandled concurrency exception.
        for (int attempt = 0; attempt < DeleteAttempts; attempt++)
        {
            EditTimelineEntity? timeline = await FindTimelineAsync(projectId, id, ct);
            if (timeline is null) return NotFound();

            // The delete also advances the Revision concurrency token, so a PATCH that read the row
            // before the delete committed matches no row afterwards and answers 404 instead of
            // reporting a 200 for a write onto a deleted timeline. No op row: nothing reads the
            // history of a deleted timeline.
            DateTime now = _clock.GetUtcNow().UtcDateTime;
            timeline.DeletedAt = now;
            timeline.UpdatedAt = now;
            timeline.Revision += 1;

            try
            {
                await _db.SaveChangesAsync(ct);
                return NoContent();
            }
            catch (DbUpdateConcurrencyException)
            {
                _db.ChangeTracker.Clear();
            }
        }

        int? currentRevision = await ReadCurrentRevisionAsync(projectId, id, ct);
        if (currentRevision is null) return NotFound();
        return Conflict(new EditTimelineConflictResponse(currentRevision.Value));
    }

    /// <summary>The first try plus one retry against the re-read row.</summary>
    private const int DeleteAttempts = 2;

    /// <summary>
    /// W0.9: renames a timeline. The name is row metadata: the row's <c>name</c> changes and the
    /// stored document's <c>name</c> mirrors it, but the revision does NOT advance and no op row is
    /// written, so an open editor's next PATCH from the same base still applies. The response is the
    /// same shape as <c>GET</c>, so a client takes the new name (and the mirrored document) from it.
    /// </summary>
    /// <remarks>
    /// Edit permission. The name is trimmed and must then be 1..<see cref="MaxNameLength"/>
    /// characters; otherwise <c>400</c> at <c>name</c>. Unknown, another project's, another tenant's
    /// or deleted: <c>404</c>.
    /// </remarks>
    [HttpPut("{id:guid}/name")]
    public async Task<ActionResult<EditTimelineResponse>> RenameEditTimeline(
        Guid projectId,
        Guid id,
        [FromBody] RenameEditTimelineRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        string name = request?.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            return BadRequest(new[]
            {
                new EditTimelineViolation("name", $"name must be 1 to {MaxNameLength} characters after trimming, found {name.Length}")
            });
        }

        if (HasControlCharacter(name))
        {
            return BadRequest(new[] { new EditTimelineViolation("name", NameHasControlCharacterMessage) });
        }

        // The Revision concurrency token guards this UPDATE too: a PATCH that committed between
        // the read and the write makes it match no row. The rename does not depend on the
        // document's content, so it re-reads and re-applies onto the new revision instead of
        // failing; a timeline that keeps moving under it three times answers 409.
        for (int attempt = 0; attempt < RenameAttempts; attempt++)
        {
            EditTimelineEntity? timeline = await FindTimelineAsync(projectId, id, ct);
            if (timeline is null) return NotFound();

            if (!TryDeserializeDocument(timeline, out EditTimeline document))
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = $"timeline {id} stores a document that cannot be parsed"
                });
            }

            EditTimeline renamed = document with { Name = name };
            timeline.Name = name;
            timeline.DocumentJson = SerializeDocument(renamed);
            timeline.UpdatedAt = DateTime.UtcNow;

            try
            {
                await _db.SaveChangesAsync(ct);
                return Ok(ToResponse(timeline, renamed));
            }
            catch (DbUpdateConcurrencyException)
            {
                _db.ChangeTracker.Clear();
            }
        }

        // A delete that landed under the last try leaves no revision to report: 404, as PATCH.
        int? currentRevision = await ReadCurrentRevisionAsync(projectId, id, ct);
        if (currentRevision is null) return NotFound();
        return Conflict(new EditTimelineConflictResponse(currentRevision.Value));
    }

    private const int RenameAttempts = 3;

    // ─────────────────────────────────────────────────────────────────────────────
    // P3 — history, revisions, duplicate
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Default page size of <see cref="GetHistory"/>.</summary>
    public const int DefaultHistoryLimit = 50;

    /// <summary>Ceiling on <see cref="GetHistory"/>'s page size; a larger request is clamped, not rejected.</summary>
    public const int MaxHistoryLimit = 200;

    /// <summary>
    /// The op log of one timeline, newest revision first, with the set of revisions a stored document
    /// exists for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This action deliberately does <b>not</b> go through <see cref="FindTimelineAsync"/>: that
    /// materializes <c>document_json</c>, which for a history listing is pure read amplification. It
    /// projects the row's existence, project membership and revision instead, with the same
    /// soft-delete filter and the same <c>404</c> for the three indistinguishable cases.
    /// </para>
    /// <para>
    /// <c>ops</c> is returned <b>verbatim</b>. The server stores it opaque and hands it back opaque;
    /// describing an op ("blade V1 at 00:00:12:05") is the client's job because only the client's
    /// engine knows the op catalogue.
    /// </para>
    /// </remarks>
    [HttpGet("{id:guid}/history")]
    public async Task<ActionResult<EditTimelineHistoryResponse>> GetHistory(
        Guid projectId,
        Guid id,
        [FromQuery] int? limit,
        [FromQuery] int? before,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        var head = await _db.EditTimelines
            .Where(t => t.Id == id && t.ProjectId == projectId)
            .Select(t => new { t.Id, t.Revision })
            .FirstOrDefaultAsync(ct);
        if (head is null) return NotFound();

        int pageSize = Math.Clamp(limit ?? DefaultHistoryLimit, 1, MaxHistoryLimit);

        IQueryable<EditTimelineOp> query = _db.EditTimelineOps.Where(o => o.TimelineId == id);
        if (before is { } beforeRevision)
        {
            query = query.Where(o => o.Revision < beforeRevision);
        }

        List<EditTimelineOp> ops = await query
            .OrderByDescending(o => o.Revision)
            .Take(pageSize)
            .ToListAsync(ct);

        List<int> snapshotRevisions = await LiveSnapshots(id)
            .Select(s => s.Revision)
            .Distinct()
            .OrderBy(r => r)
            .ToListAsync(ct);

        HashSet<int> snapshotSet = [.. snapshotRevisions];

        // The current revision is always restorable (it IS the live row) even when no snapshot
        // row exists for it; a snapshot for the current revision is not listed twice.
        List<int> restorable = snapshotRevisions.Contains(head.Revision)
            ? snapshotRevisions
            : [.. snapshotRevisions, head.Revision];

        List<EditTimelineHistoryEntryResponse> entries = ops
            .Select(o => new EditTimelineHistoryEntryResponse(
                o.Revision,
                o.CreatedAt,
                ParseOpsOrEmptyArray(o.OpsJson),
                snapshotSet.Contains(o.Revision) || o.Revision == head.Revision))
            .ToList();

        return Ok(new EditTimelineHistoryResponse(head.Id, head.Revision, entries, restorable));
    }

    /// <summary>
    /// The document as it stood at one revision — from the live row when <paramref name="rev"/> is
    /// the current revision, otherwise from an <c>edit_timeline_snapshots</c> row.
    /// </summary>
    /// <remarks>
    /// A revision the op log knows but no stored document answers for is a <c>404</c> carrying
    /// <see cref="EditTimelineRevisionUnavailableResponse"/> with the restorable set, because the
    /// server never replays ops and will not pretend to. A revision above the current one, or below
    /// 1, is a plain <c>404</c>.
    /// </remarks>
    [HttpGet("{id:guid}/revisions/{rev:int}")]
    public async Task<ActionResult<EditTimelineRevisionResponse>> GetRevision(
        Guid projectId,
        Guid id,
        int rev,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        EditTimelineEntity? timeline = await FindTimelineAsync(projectId, id, ct);
        if (timeline is null) return NotFound();

        if (rev < 1 || rev > timeline.Revision) return NotFound();

        if (rev == timeline.Revision)
        {
            if (!TryDeserializeDocument(timeline, out EditTimeline current))
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = $"timeline {id} stores a document that cannot be parsed"
                });
            }

            return Ok(new EditTimelineRevisionResponse(timeline.Id, rev, "current", current));
        }

        EditTimelineSnapshot? snapshot = await LiveSnapshots(id)
            .Where(s => s.Revision == rev)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (snapshot is null)
        {
            List<int> restorable = await LiveSnapshots(id)
                .Select(s => s.Revision)
                .Distinct()
                .OrderBy(r => r)
                .ToListAsync(ct);
            if (!restorable.Contains(timeline.Revision)) restorable.Add(timeline.Revision);

            return NotFound(new EditTimelineRevisionUnavailableResponse(
                $"revision {rev} of timeline {id} has no stored document: the server never replays ops, " +
                "so only snapshotted revisions and the current one can be restored",
                rev,
                restorable));
        }

        EditTimeline? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<EditTimeline>(snapshot.DocumentJson, DocumentJsonOptions);
        }
        catch (JsonException)
        {
            parsed = null;
        }

        if (parsed is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = $"snapshot {rev} of timeline {id} stores a document that cannot be parsed"
            });
        }

        return Ok(new EditTimelineRevisionResponse(timeline.Id, rev, "snapshot", parsed));
    }

    /// <summary>
    /// Copies a timeline's CURRENT document into a new timeline at revision 1 ("timeline versions").
    /// The copy carries the source's <c>seedStepResultId</c> (its provenance is the same compile
    /// result), a fresh id inside the document, and its own revision-1 snapshot.
    /// </summary>
    [HttpPost("{id:guid}/duplicate")]
    public async Task<ActionResult<EditTimelineResponse>> DuplicateEditTimeline(
        Guid projectId,
        Guid id,
        [FromBody] DuplicateEditTimelineRequest? request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        EditTimelineEntity? source = await FindTimelineAsync(projectId, id, ct);
        if (source is null) return NotFound();

        if (!TryDeserializeDocument(source, out EditTimeline sourceDocument))
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = $"timeline {id} stores a document that cannot be parsed"
            });
        }

        // The default name shortens the source's so " (copy)" still fits the column.
        string name = string.IsNullOrWhiteSpace(request?.Name)
            ? $"{source.Name[..Math.Min(source.Name.Length, MaxNameLength - CopySuffix.Length)]}{CopySuffix}"
            : request!.Name!.Trim();
        if (HasControlCharacter(name))
        {
            return BadRequest(new { message = NameHasControlCharacterMessage });
        }

        if (name.Length > MaxNameLength)
        {
            return BadRequest(new { message = NameTooLongMessage });
        }

        Guid newId = Guid.NewGuid();
        EditTimeline document = sourceDocument with { Id = newId, Name = name, ProjectId = projectId };

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        EditTimelineEntity entity = new()
        {
            Id = newId,
            ProjectId = projectId,
            Name = name,
            DocumentJson = SerializeDocument(document),
            Revision = 1,
            SeedStepResultId = source.SeedStepResultId,
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = null
        };

        _db.EditTimelines.Add(entity);
        _db.EditTimelineSnapshots.Add(NewSnapshot(newId, 1, entity.DocumentJson, EditTimelineSnapshotCause.Duplicate, now));
        await _db.SaveChangesAsync(ct);

        return StatusCode(StatusCodes.Status201Created, ToResponse(entity, document));
    }

    /// <summary>
    /// Renders this timeline to a delivery preset through the ordinary workflow engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Creates a <b>system-owned, single-step</b> <see cref="WorkflowDefinition"/>
    /// (<c>Kind = WorkflowDefinitionKind.TimelineRender</c>, hidden from the project's workflow list, bound to the built-in
    /// <c>VideoTransform</c> placeholder agent exactly like every deterministic step type) whose one
    /// <see cref="StepType.TimelineRender"/> step carries the request as its config, then queues it
    /// through <see cref="IWorkflowExecutionService"/> — so <c>WorkflowExecutionRunner</c>,
    /// <c>MaxConcurrency</c>, cancellation provenance, <c>step.progress</c> SSE, the outputs
    /// surfaces and the step cache are all reused unchanged. Nothing renders in this request.
    /// </para>
    /// <para>
    /// The revision is PINNED here: a request without one records the timeline's current revision
    /// into the step config, so what the queue eventually renders is what the user was looking at
    /// when they pressed Render, not whatever the document becomes while the job waits.
    /// </para>
    /// <para>
    /// The config is validated with <see cref="TimelineRenderStepConfig.Validate"/> before anything
    /// is written — every number a human authored here is range-checked at the door, never in argv.
    /// A <c>404</c> for a foreign/missing timeline, <c>400</c> with every violation otherwise,
    /// <c>402</c> <c>PLAN_NOT_ENTITLED</c> (feature <c>resolution</c>) for an explicit size above the
    /// plan's output cap (the <c>Uhd4k16x9</c> preset or an <c>encode</c> width/height), <c>202</c>
    /// with the execution and workflow ids on success. Shape presets and <c>Master</c> are never
    /// refused for size: the engine clamps them to the plan.
    /// </para>
    /// </remarks>
    [HttpPost("{id:guid}/render")]
    public async Task<ActionResult<RenderEditTimelineResponse>> RenderEditTimeline(
        Guid projectId,
        Guid id,
        [FromBody] RenderEditTimelineRequest? request,
        CancellationToken ct)
    {
        if (request?.OutputFileName is { } name && IsAudioJobFile(name))
        {
            return BadRequest(new
            {
                message = "That file name is reserved for loudness, audition and preview jobs; please choose another.",
                errors = new[] { "outputFileName: reserved prefix" }
            });
        }

        if (request?.Preset == TimelineRenderPreset.Preview || request?.Preview is not null)
        {
            return BadRequest(new
            {
                message = "Previews are started from the preview route; please use that instead.",
                errors = new[] { "preset: reserved" }
            });
        }

        return await QueueRenderAsync(projectId, id, request, ProjectPermission.Edit, ct);
    }

    internal const string LoudnessJobPrefix = "loudness-measure-";
    internal const string AuditionJobPrefix = "audition-";

    internal const string PreviewJobPrefix = "preview-";
    internal const string PreviewJobKind = "preview";

    private static bool IsAudioJobFile(string name) =>
        name.StartsWith(LoudnessJobPrefix, StringComparison.Ordinal) || name.StartsWith(AuditionJobPrefix, StringComparison.Ordinal)
        || name.StartsWith(PreviewJobPrefix, StringComparison.Ordinal);

    /// <summary>R08.4: measure the timeline's mixed loudness by running the real AudioOnly mix; nothing is registered as a project file.</summary>
    [HttpPost("{id:guid}/loudness")]
    public async Task<ActionResult<RenderEditTimelineResponse>> MeasureLoudness(
        Guid projectId, Guid id, [FromBody] AudioJobRequest? request, CancellationToken ct) =>
        await QueueRenderAsync(projectId, id, ToAudioJob(request, null, LoudnessJobPrefix), ProjectPermission.Edit, ct, "loudness");

    /// <summary>R08.4: what a loudness measurement would cost (the AudioOnly render estimate).</summary>
    [HttpPost("{id:guid}/loudness/estimate")]
    public async Task<ActionResult<EditTimelineRenderEstimateResponse>> EstimateLoudness(
        Guid projectId, Guid id, [FromBody] AudioJobRequest? request, CancellationToken ct) =>
        await EstimateRender(projectId, id, ToAudioJob(request, null, LoudnessJobPrefix), ct);

    /// <summary>R08.6: render just a range of the mix as short-lived playable audio.</summary>
    [HttpPost("{id:guid}/audition")]
    public async Task<ActionResult<RenderEditTimelineResponse>> AuditionRange(
        Guid projectId, Guid id, [FromBody] AudioJobRequest? request, CancellationToken ct)
    {
        if (request?.Range is null)
            return BadRequest(new { message = "The audition request is invalid.", errors = new[] { "range: required" } });
        return await QueueRenderAsync(projectId, id, ToAudioJob(request, request.Range, AuditionJobPrefix), ProjectPermission.Edit, ct, "audition");
    }

    // ---- R10.4 "Preview 3 s" -------------------------------------------------------------------------------

    /// <summary>The most a preview window may be, and the longest it can be asked for, in seconds.</summary>
    internal const double PreviewMaxSeconds = 3;
    internal const double PreviewMinSeconds = 1;

    /// <summary>
    /// R10.4: render three seconds around a frame through the real render path, without a delivery render. The
    /// server builds the whole config (preset <c>Preview</c>, the current revision, a range clamped to the timeline,
    /// no loudness, enabled captions burned in, no project file); the kind is server-set and nothing in the body can
    /// choose it. Same gates as a render: <c>402 INSUFFICIENT_CREDITS</c>, the plan's concurrency <c>429</c>, and a
    /// per-user-per-project hourly cap (<c>429 PREVIEW_RATE_LIMITED</c>). <c>202</c> carries the job id
    /// (<c>executionId</c>) to poll at <c>render/preview/{jobId}</c>.
    /// </summary>
    [HttpPost("{id:guid}/render/preview")]
    public async Task<ActionResult<RenderEditTimelineResponse>> RenderPreview(
        Guid projectId, Guid id, [FromBody] PreviewRenderRequest? request, CancellationToken ct)
    {
        if (request is null)
            return BadRequest(new { message = "The preview request is invalid.", errors = new[] { "atFrame: required" } });

        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        EditTimelineEntity? timeline = await FindTimelineAsync(projectId, id, ct);
        if (timeline is null) return NotFound();

        (RenderEditTimelineRequest? built, ActionResult? invalid) = BuildPreviewRequest(timeline, request);
        if (invalid is not null) return invalid;

        // No preview before a verdict: a source clip that is an output without a current Allow or Review
        // verdict (pending, changed, blocked, outage) refuses the preview.
        if (_publishModeration is not null && await FirstRefusedSourceAsync(projectId, timeline.DocumentJson, ct) is { } refused)
            return refused.ToRefusal(this);

        return await QueueRenderAsync(projectId, id, built, ProjectPermission.Edit, ct, PreviewJobKind);
    }

    private async Task<PublishModerationOutcome?> FirstRefusedSourceAsync(
        Guid projectId, string? documentJson, CancellationToken ct)
    {
        // A document that cannot be read cannot be checked: refuse (fail closed).
        if (documentJson is null) return PublishModerationOutcome.Unavailable();
        EditTimeline? document;
        try { document = JsonSerializer.Deserialize<EditTimeline>(documentJson, DocumentJsonOptions); }
        catch (JsonException) { return PublishModerationOutcome.Unavailable(); }
        if (document is null) return PublishModerationOutcome.Unavailable();

        HandOffPlan plan = await HandOffPlanner.PlanAsync(_db, _storage, projectId, document, _handOff.CapFiles, ct);
        foreach (HandOffSource source in plan.Sources.Where(s => s.Resolvable && s.StorageKey is not null))
        {
            PublishModerationOutcome? outcome = await _publishModeration!.GateStoredCopyAsync(projectId, source.StorageKey!, ct);
            if (outcome is not null) return outcome;
        }

        return null;
    }

    /// <summary>
    /// The server-built render request of a preview: Preview preset at the current revision over the clamped window.
    /// The window starts at <c>atFrame</c>; one that would run past the end slides back so it ends at the timeline's end.
    /// </summary>
    private (RenderEditTimelineRequest? Request, ActionResult? Invalid) BuildPreviewRequest(
        EditTimelineEntity timeline, PreviewRenderRequest window)
    {
        List<string> errors = [];
        double seconds = window.Seconds ?? PreviewMaxSeconds;
        if (double.IsNaN(seconds) || seconds < PreviewMinSeconds || seconds > PreviewMaxSeconds)
            errors.Add($"seconds: must be between {PreviewMinSeconds:0} and {PreviewMaxSeconds:0}");
        if (window.AtFrame < 0)
            errors.Add("atFrame: must be 0 or more");

        EditTimeline? document = null;
        try { document = JsonSerializer.Deserialize<EditTimeline>(timeline.DocumentJson, DocumentJsonOptions); }
        catch (JsonException) { /* reported below */ }
        if (document?.Settings is null)
            errors.Add("timeline: unreadable");

        if (errors.Count > 0)
            return (null, BadRequest(new { message = "The preview request is invalid.", errors }));

        double fps = document!.Settings.Rate is { Den: > 0 } rate && rate.Num > 0 ? (double)rate.Num / rate.Den : 30;
        long total = document.Tracks.SelectMany(t => t.Items).Select(i => i.RecordOut).DefaultIfEmpty(0).Max();
        if (total <= 0)
        {
            return (null, BadRequest(new
            {
                message = "There is nothing to preview yet. Add a clip first.",
                errors = new[] { "timeline: empty" }
            }));
        }

        long length = Math.Max(1, (long)Math.Round(seconds * fps));
        long inFrame = Math.Clamp(window.AtFrame, 0, Math.Max(0, total - length));
        long outFrame = Math.Min(total, inFrame + length);

        return (new RenderEditTimelineRequest(
            Revision: timeline.Revision,
            Range: new TimelineRenderRange(inFrame, outFrame),
            Preset: TimelineRenderPreset.Preview,
            OutputFileName: $"{PreviewJobPrefix}{Guid.NewGuid():N}"[..(PreviewJobPrefix.Length + 8)] + ".mp4",
            RegisterProjectFile: false,
            Loudness: null,
            Captions: TimelineRenderCaptions.BurnIn), null);
    }

    /// <summary>The 429 for a user who has started their share of previews in this project in the last hour, else null.</summary>
    private async Task<ActionResult?> CheckPreviewRateAsync(Guid projectId, CancellationToken ct)
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        DateTime since = now.AddHours(-1);
        Guid userId = _currentUser.UserId;
        List<Guid> previewWorkflows = await _db.WorkflowDefinitions
            .Where(w => w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.TimelineRender
                && w.Name.StartsWith(TimelinePreviewSweeper.WorkflowNamePrefix))
            .Select(w => w.Id)
            .ToListAsync(ct);
        if (previewWorkflows.Count == 0) return null;

        List<DateTime> recent = (await _db.WorkflowExecutions.AsNoTracking()
                .Where(e => e.ProjectId == projectId && e.InitiatedByUserId == userId
                    && previewWorkflows.Contains(e.WorkflowDefinitionId))
                .Select(e => e.QueuedAt)
                .ToListAsync(ct))
            .Where(q => q is not null && q >= since)
            .Select(q => q!.Value)
            .ToList();
        int limit = _preview.Limit;
        if (recent.Count < limit) return null;

        int retryAfter = Math.Max(1, (int)Math.Ceiling((recent.Min().AddHours(1) - now).TotalSeconds));
        return StatusCode(StatusCodes.Status429TooManyRequests, new
        {
            code = "PREVIEW_RATE_LIMITED",
            message = $"Preview limit reached: {recent.Count} of {limit} previews in this project in the last hour. Try again in {RunRateRefusal.Wait(retryAfter)}.",
            used = recent.Count,
            limit,
            windowMinutes = 60,
            retryAfterSeconds = retryAfter
        });
    }

    /// <summary>
    /// R10.4: poll a preview job. <c>status</c> is the execution status name (<c>Queued</c>, <c>Running</c>,
    /// <c>Passed</c>, <c>Failed</c>, ...); <c>ready</c> is true once the file can be played from <c>content</c>.
    /// A job of another timeline or project, or a render that is not a preview, is a 404.
    /// </summary>
    [HttpGet("{id:guid}/render/preview/{jobId:guid}")]
    public async Task<ActionResult<PreviewJobResponse>> GetPreviewJob(Guid projectId, Guid id, Guid jobId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        (EditTimelineRenderResponse? job, string? storageKey, ActionResult? failure) = await FindPreviewAsync(projectId, id, jobId, ct);
        if (failure is not null) return failure;

        bool passed = string.Equals(job!.Status, nameof(ExecutionStatus.Passed), StringComparison.Ordinal);
        bool fileThere = passed && storageKey is not null
            && await _storage.GetMetadataAsync(projectId, storageKey, ct) is not null;
        string? error = job.ErrorMessage;
        if (passed && !fileThere) error ??= "This preview has expired. Make a new one.";
        return Ok(new PreviewJobResponse(job.ExecutionId, job.Status, error, job.DurationSec, fileThere));
    }

    /// <summary>
    /// R10.4: the finished preview as <c>video/mp4</c> with HTTP Range, <c>Cache-Control: private, no-store</c>.
    /// No storage key or URL is ever returned; the bytes stream through this route.
    /// </summary>
    [HttpGet("{id:guid}/render/preview/{jobId:guid}/content")]
    public async Task<IActionResult> GetPreviewContent(Guid projectId, Guid id, Guid jobId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        (EditTimelineRenderResponse? job, string? storageKey, ActionResult? failure) = await FindPreviewAsync(projectId, id, jobId, ct);
        if (failure is not null) return failure;
        if (storageKey is null || !string.Equals(job!.Status, nameof(ExecutionStatus.Passed), StringComparison.Ordinal))
            return NotFound();
        // A preview whose output is not served answers exactly like one that is missing.
        if (await OutputGate.IsKeyHeldAsync(_db, projectId, storageKey, ct)) return NotFound();
        // A preview rendered before a source was cleared (or after it was blocked) is not served either. The clips
        // checked are the ones the preview was rendered from (its pinned revision), not whatever the timeline holds
        // now; a timeline that is gone, or a revision that cannot be read back, refuses.
        if (_publishModeration is not null)
        {
            string? pinned = await PinnedDocumentJsonAsync(projectId, id, job!.Revision, ct);
            if (await FirstRefusedSourceAsync(projectId, pinned, ct) is { } previewRefused)
                return previewRefused.ToRefusal(this);
        }
        if (await _storage.GetMetadataAsync(projectId, storageKey, ct) is not { } metadata || metadata.SizeBytes > MaxPreviewBytes)
            return NotFound();

        // A preview is three seconds of 540p: small enough to hold in memory so Range works on any store.
        MemoryStream buffer = new();
        try
        {
            await using Stream source = await _storage.DownloadAsync(projectId, storageKey, ct);
            await source.CopyToAsync(buffer, ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            await buffer.DisposeAsync();
            return NotFound();
        }

        buffer.Position = 0;
        // The container is ours (the Preview preset is always mp4), but the type still goes through the one serving policy.
        ServedContent served = SafeContentPolicy.DecideByMime("video/mp4");
        SafeContentPolicy.ApplySecurityHeaders(Response);
        Response.Headers["Content-Disposition"] = SafeContentPolicy.BuildContentDisposition(served.Inline, "preview.mp4");
        Response.Headers.CacheControl = "private, no-store";
        return File(buffer, served.ContentType, enableRangeProcessing: true);
    }

    private const long MaxPreviewBytes = 64L * 1024 * 1024;

    /// <summary>The timeline document at <paramref name="revision"/>: the live one when it still is that revision, else its snapshot. Null when neither exists.</summary>
    private async Task<string?> PinnedDocumentJsonAsync(Guid projectId, Guid id, int? revision, CancellationToken ct)
    {
        EditTimelineEntity? timeline = await FindTimelineAsync(projectId, id, ct);
        if (timeline is null || revision is null) return null;
        if (timeline.Revision == revision) return timeline.DocumentJson;

        return (await LiveSnapshots(id).Where(s => s.Revision == revision).Select(s => s.DocumentJson).FirstOrDefaultAsync(ct));
    }

    private async Task<(EditTimelineRenderResponse? Job, string? StorageKey, ActionResult? Failure)> FindPreviewAsync(
        Guid projectId, Guid id, Guid jobId, CancellationToken ct)
    {
        EditTimelineEntity? timeline = await FindTimelineAsync(projectId, id, ct);
        if (timeline is null) return (null, null, NotFound());

        List<EditTimelineRenderResponse> jobs = await LoadRendersAsync(projectId, id, c => c.AudioJobKind == PreviewJobKind, ct);
        EditTimelineRenderResponse? job = jobs.FirstOrDefault(j => j.ExecutionId == jobId);
        if (job is null) return (null, null, NotFound());

        string? key = job.StepResultId is { } resultId
            ? await _db.WorkflowStepResults.AsNoTracking().Where(r => r.Id == resultId).Select(r => r.OutputStorageKey).FirstOrDefaultAsync(ct)
            : null;
        return (job, string.IsNullOrEmpty(key) ? null : key, null);
    }

    private static RenderEditTimelineRequest ToAudioJob(AudioJobRequest? request, TimelineRenderRange? range, string prefix) =>
        new(Revision: request?.Revision,
            Range: range,
            Preset: TimelineRenderPreset.AudioOnly,
            OutputFileName: $"{prefix}{Guid.NewGuid():N}"[..(prefix.Length + 8)] + ".m4a",
            RegisterProjectFile: false);

    /// <summary>R08.4/R08.6: poll a loudness or audition job.</summary>
    [HttpGet("{id:guid}/audio-jobs/{executionId:guid}")]
    public async Task<ActionResult<AudioJobResponse>> GetAudioJob(Guid projectId, Guid id, Guid executionId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        EditTimelineEntity? timeline = await FindTimelineAsync(projectId, id, ct);
        if (timeline is null) return NotFound();

        List<EditTimelineRenderResponse> jobs = await LoadRendersAsync(projectId, id, c => c.AudioJobKind is not null and not PreviewJobKind, ct);
        EditTimelineRenderResponse? job = jobs.FirstOrDefault(j => j.ExecutionId == executionId);
        if (job is null) return NotFound();
        return Ok(new AudioJobResponse(
            job.ExecutionId,
            job.OutputFileName.StartsWith(AuditionJobPrefix, StringComparison.Ordinal) ? "audition" : "loudness",
            job.Status, job.Revision, job.ErrorMessage, job.Loudness, job.DurationSec, job.StepResultId));
    }

    private async Task<ActionResult<RenderEditTimelineResponse>> QueueRenderAsync(
        Guid projectId,
        Guid id,
        RenderEditTimelineRequest? request,
        ProjectPermission permission,
        CancellationToken ct,
        string? audioJobKind = null)
    {
        // A render is a run like any other: an unverified address cannot start one.
        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;

        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, permission, ct);
        if (access.Failure is { } denied) return denied;

        // A restore of this project is rewriting it: render, loudness, audition and preview starts wait.
        if (await RestoreRunGate.IsHeldAsync(_db, projectId, ct)) return RestoreRunGate.Refusal();

        EditTimelineEntity? timeline = await FindTimelineAsync(projectId, id, ct);
        if (timeline is null) return NotFound();

        (TimelineRenderStepConfig config, int revision, ActionResult? invalid) = await PrepareRenderAsync(timeline, request, ct, audioJobKind);
        if (invalid is not null) return invalid;

        // Plan gate (before any write): only an EXPLICIT size above the plan is refused — the 4K
        // preset or an encode width/height. Shape presets and Master are accepted and clamped to
        // the plan by the engine (TimelineRenderStepExecutor), so a capped plan can still export.
        if (await CheckRenderPlanAsync(config, access.Project, ct) is { } violation)
            return violation.ToResult();

        // R06.7: then the credits, also before anything persists (the order every run start shares:
        // plan entitlement 402, credit 402; the plan's concurrency 429 comes from the queueing below).
        if (BillingOn && await PriceRenderAsync(timeline, config, revision, access.Project, ct) is { } price
            && price.BalanceMillis < price.RequiredMillis)
        {
            return StatusCode(StatusCodes.Status402PaymentRequired, new
            {
                code = "INSUFFICIENT_CREDITS",
                message = CreditShortfallMessage.Build(price.RequiredMillis, null, price.BalanceMillis),
                missing = CreditShortfallMessage.MissingCredits(price.RequiredMillis, price.BalanceMillis)
            });
        }

        if (audioJobKind == PreviewJobKind && await CheckPreviewRateAsync(projectId, ct) is { } limited)
            return limited;

        AgentDefinition? placeholder = await _db.AgentDefinitions
            .Where(a => a.AgentType == AgentType.VideoTransform)
            .OrderByDescending(a => a.IsBuiltIn)
            .FirstOrDefaultAsync(ct);
        if (placeholder is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "The VideoTransform placeholder agent is not seeded, so no deterministic step can be created."
            });
        }

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        await EnsureRenderSnapshotAsync(timeline, revision, now, ct);

        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = audioJobKind == PreviewJobKind
                ? $"{TimelinePreviewSweeper.WorkflowNamePrefix}{timeline.Name}"
                : $"Render: {timeline.Name}",
            Kind = WorkflowDefinitionKind.TimelineRender,
            RequiresUserInput = false,
            CreatedAt = now,
            UpdatedAt = now
        };
        workflow.Steps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            AgentDefinitionId = placeholder.Id,
            StepOrder = 1,
            Label = $"Render {config.Preset}",
            StepType = StepType.TimelineRender,
            TimelineRenderConfigJson = JsonSerializer.Serialize(config, RenderConfigJsonOptions),
            CacheMode = StepCacheMode.Never
        });

        // One transaction for the hidden workflow and its execution: any failure before the commit
        // (the plan's concurrency cap → 429, a database error, anything else) rolls both back, so a
        // refused or failed start never leaves an orphan render workflow. QueueAsync commits through
        // beforePublish — after the execution row is saved, before the bus message — so the engine
        // only ever sees committed rows. It is queued under its own hidden kind: the public
        // ExecuteAsync looks workflows up as Standard only (so no execute route can start a render
        // workflow) and would report this one missing.
        WorkflowExecutionResponse execution;
        await using (IDbContextTransaction transaction = await _db.Database.BeginTransactionAsync(ct))
        {
            bool committed = false;
            try
            {
                _db.WorkflowDefinitions.Add(workflow);
                await _db.SaveChangesAsync(ct);

                execution = await _executions.QueueAsync(
                    projectId, workflow.Id, _currentUser.UserId, userRequest: null, ExecutionInitiator.Human, ct,
                    regenerateVideoClips: false, runChoices: null, WorkflowDefinitionKind.TimelineRender, executionId: null,
                    beforePublish: async commitCt =>
                    {
                        await transaction.CommitAsync(commitCt);
                        committed = true;
                    });
                if (!committed)
                {
                    // An execution service that never reached its publish point still queued the row.
                    await transaction.CommitAsync(ct);
                    committed = true;
                }
            }
            catch (Exception ex) when (!committed)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                _db.ChangeTracker.Clear();
                if (ex is PlanLimitException limit) return limit.Violation.ToResult();
                throw;
            }
        }

        return StatusCode(StatusCodes.Status202Accepted, new RenderEditTimelineResponse(
            ExecutionId: execution.Id,
            WorkflowId: workflow.Id,
            TimelineId: id,
            Revision: revision,
            Preset: config.Preset.ToString(),
            OutputFileName: config.OutputFileName));
    }

    /// <summary>
    /// The render request's pinned revision and validated config, shared by <c>render</c> and
    /// <c>render/estimate</c> so both refuse exactly the same requests (a 400 listing every violation).
    /// </summary>
    private async Task<(TimelineRenderStepConfig Config, int Revision, ActionResult? Invalid)> PrepareRenderAsync(
        EditTimelineEntity timeline,
        RenderEditTimelineRequest? request,
        CancellationToken ct,
        string? audioJobKind = null)
    {
        Guid id = timeline.Id;
        request ??= new RenderEditTimelineRequest();
        int revision = request.Revision ?? timeline.Revision;
        if (revision != timeline.Revision)
        {
            bool restorable = await LiveSnapshots(timeline.Id)
                .AnyAsync(s => s.Revision == revision, ct);
            if (!restorable)
            {
                return (null!, 0, BadRequest(new
                {
                    message = $"Revision {revision} has no stored document; restorable revisions are the snapshots and the current revision {timeline.Revision}.",
                    errors = new[] { "revision: not restorable" }
                }));
            }
        }

        TimelineRenderStepConfig config = new(
            EditTimelineId: id,
            Revision: revision,
            Range: request.Range,
            Preset: request.Preset ?? TimelineRenderPreset.Hd1080p16x9,
            Encode: request.Encode,
            OutputFileName: string.IsNullOrWhiteSpace(request.OutputFileName) ? DefaultRenderFileName(timeline.Name) : request.OutputFileName!,
            RegisterProjectFile: request.RegisterProjectFile ?? true,
            Loudness: request.Loudness,
            AudioJobKind: audioJobKind,
            Captions: request.Captions);

        List<string> errors = config.Validate();
        if (errors.Count > 0)
        {
            return (null!, 0, BadRequest(new { message = "The render request is invalid.", errors }));
        }

        return (config, revision, null);
    }

    /// <summary>
    /// R06.7: what a render of this timeline would cost and how long it would take, before anything
    /// is queued. Same body, same validation (400 lists every violation) and same permission as
    /// <c>render</c>; nothing is persisted.
    /// </summary>
    /// <remarks>
    /// Billing off (or no estimator) answers <c>{billingEnabled:false, affordable:true}</c>. Credits are
    /// the compute-metering price of the render: the output's resolution tier
    /// (<see cref="ComputeMetering.TierFor"/>, 720p/1080p/uhd) times the output length in seconds, priced
    /// through the same charge service the other estimates use. <c>estimateSeconds</c> is a heuristic
    /// (<see cref="EstimateRenderSeconds"/>), an estimate and not a promise. A size the plan refuses
    /// answers <c>affordable:false</c> with the plan's own sentence as <c>unavailableReason</c>.
    /// </remarks>
    [HttpPost("{id:guid}/render/estimate")]
    public async Task<ActionResult<EditTimelineRenderEstimateResponse>> EstimateRender(
        Guid projectId,
        Guid id,
        [FromBody] RenderEditTimelineRequest? request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        EditTimelineEntity? timeline = await FindTimelineAsync(projectId, id, ct);
        if (timeline is null) return NotFound();

        if (request?.Preview is { } previewWindow)
        {
            (RenderEditTimelineRequest? built, ActionResult? badWindow) = BuildPreviewRequest(timeline, previewWindow);
            if (badWindow is not null) return badWindow;
            request = built;

            // The estimate is the first thing a preview click asks: it refuses what the preview itself would refuse,
            // so the page can say "still being checked" instead of offering a price for a render that will not run.
            if (_publishModeration is not null && await FirstRefusedSourceAsync(projectId, timeline.DocumentJson, ct) is { } estimateRefused)
                return estimateRefused.ToRefusal(this);
        }
        else if (request?.Preset == TimelineRenderPreset.Preview)
        {
            return BadRequest(new { message = "Previews are priced with a preview window.", errors = new[] { "preset: reserved" } });
        }

        (TimelineRenderStepConfig config, int revision, ActionResult? invalid) = await PrepareRenderAsync(
            timeline, request, ct, request?.Preview is not null || request?.Preset == TimelineRenderPreset.Preview ? PreviewJobKind : null);
        if (invalid is not null) return invalid;

        (long? sizeBytes, bool? sizeApprox) = await EstimateSizeAsync(timeline, config, revision, ct);

        if (!BillingOn)
            return Ok(new EditTimelineRenderEstimateResponse(false, null, null, null, null, null, true, null, sizeBytes, sizeApprox));

        if (await CheckRenderPlanAsync(config, access.Project, ct) is { } notEntitled)
        {
            return Ok(new EditTimelineRenderEstimateResponse(
                true, null, null, null, null, null, false, notEntitled.Message, sizeBytes, sizeApprox));
        }

        RenderPrice? price = await PriceRenderAsync(timeline, config, revision, access.Project, ct);
        if (price is null)
        {
            return Ok(new EditTimelineRenderEstimateResponse(
                true, null, null, null, null, null, true,
                "The cost of this render could not be worked out right now. It will still be checked when you start it.",
                sizeBytes, sizeApprox));
        }

        double credits = price.RequiredMillis / 1000.0;
        return Ok(new EditTimelineRenderEstimateResponse(
            BillingEnabled: true,
            Credits: credits,
            CreditsLow: credits,
            CreditsHigh: credits,
            EstimateSeconds: EstimateRenderSeconds(price.Seconds, price.Tier),
            BalanceCredits: price.BalanceMillis / 1000.0,
            Affordable: price.BalanceMillis >= price.RequiredMillis,
            UnavailableReason: null,
            EstimatedSizeBytes: sizeBytes,
            SizeIsApproximate: sizeApprox));
    }

    /// <summary>
    /// A rough delivery file size (<see cref="RenderSizeEstimator"/>), independent of billing. Null when the
    /// document cannot be read or has no length; the estimate then simply omits it.
    /// </summary>
    private async Task<(long? Bytes, bool? Approximate)> EstimateSizeAsync(
        EditTimelineEntity timeline, TimelineRenderStepConfig config, int revision, CancellationToken ct)
    {
        try
        {
            string? json = revision == timeline.Revision
                ? timeline.DocumentJson
                : await LiveSnapshots(timeline.Id).Where(s => s.Revision == revision).Select(s => s.DocumentJson).FirstOrDefaultAsync(ct);
            if (json is null) return (null, null);

            EditTimeline? document = JsonSerializer.Deserialize<EditTimeline>(json, DocumentJsonOptions);
            if (document?.Settings is null) return (null, null);

            double fps = document.Settings.Rate is { Den: > 0 } rate && rate.Num > 0 ? (double)rate.Num / rate.Den : 30;
            long frames = config.Range is { } range
                ? range.OutFrame - range.InFrame
                : document.Tracks.SelectMany(t => t.Items).Select(i => i.RecordOut).DefaultIfEmpty(0).Max();
            bool audioOnly = config.Preset == TimelineRenderPreset.AudioOnly;
            (int width, int height) = audioOnly ? (0, 0) : TimelineRenderSizing.Dimensions(config, document.Settings.Width, document.Settings.Height);
            TimelineRenderEncodeSettings encode = config.Encode ?? new TimelineRenderEncodeSettings();

            return RenderSizeEstimator.Estimate(
                Math.Max(0, frames) / fps, width, height, fps, audioOnly, encode.Crf, encode.Codec, encode.Speed,
                encode.AudioBitrateKbps, encode.RateControl, encode.VideoBitrateKbps) is { } estimate
                ? (estimate.Bytes, estimate.Approximate)
                : (null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not estimate the file size of timeline {TimelineId}", timeline.Id);
            return (null, null);
        }
    }

    private bool BillingOn => _costEstimator is { BillingEnabled: true } && _charges is not null;

    /// <summary>Plan gate: an EXPLICIT size above the plan's cap (the 4K preset or an encode size), else null.</summary>
    private async Task<PlanGateViolation?> CheckRenderPlanAsync(TimelineRenderStepConfig config, Project? project, CancellationToken ct)
    {
        if (_entitlements is null || project?.OrganizationId is not { } organizationId
            || TimelineRenderSizing.ExplicitShortSide(config) is not { } requestedShortSide)
            return null;

        OrganizationEntitlementSet plan = await _entitlements.GetAsync(organizationId, ct);
        return PlanGate.CheckResolution(requestedShortSide, plan);
    }

    /// <summary>The priced render: its length, its resolution tier, what it needs and what the workspace has.</summary>
    private sealed record RenderPrice(double Seconds, string Tier, long RequiredMillis, long BalanceMillis);

    /// <summary>
    /// The compute-metering price of the render at its resolution tier and length, and the workspace's
    /// available balance. Null when it cannot be worked out (no organization, an unreadable document, a
    /// pricing failure): the caller then fails open and the engine's own gate still decides.
    /// </summary>
    private async Task<RenderPrice?> PriceRenderAsync(
        EditTimelineEntity timeline, TimelineRenderStepConfig config, int revision, Project? project, CancellationToken ct)
    {
        if (_charges is null || project?.OrganizationId is not { } organizationId) return null;

        try
        {
            string? json = revision == timeline.Revision
                ? timeline.DocumentJson
                : await LiveSnapshots(timeline.Id)
                    .Where(s => s.Revision == revision)
                    .Select(s => s.DocumentJson)
                    .FirstOrDefaultAsync(ct);
            if (json is null) return null;

            EditTimeline? document = JsonSerializer.Deserialize<EditTimeline>(json, DocumentJsonOptions);
            if (document?.Settings is null) return null;

            double fps = document.Settings.Rate is { Den: > 0 } rate && rate.Num > 0 ? (double)rate.Num / rate.Den : 30;
            long frames = config.Range is { } range
                ? range.OutFrame - range.InFrame
                : document.Tracks.SelectMany(t => t.Items).Select(i => i.RecordOut).DefaultIfEmpty(0).Max();
            double seconds = Math.Max(0, frames) / fps;

            (int width, int height) = config.Preset == TimelineRenderPreset.AudioOnly
                ? (0, 0)
                : TimelineRenderSizing.Dimensions(config, document.Settings.Width, document.Settings.Height);
            string tier = ComputeMetering.TierFor(width, height);

            long millis = seconds <= 0
                ? 0
                : await _charges.EstimateAsync(new PriceRequest(
                    WellKnownModelCatalog.ComputeCapabilityId,
                    WellKnownModelCatalog.ComputeProviderId,
                    ComputeMetering.ProviderKind,
                    InferenceProviderCapability.Compute,
                    WellKnownModelCatalog.ComputeModelCode,
                    UsageKind.EncodeOutputSeconds,
                    tier,
                    UsageBillingMode.Platform,
                    (decimal)Math.Round(seconds, 3)), ct);

            long available = (await new CreditLedger(_db).GetBalanceAsync(organizationId, ct)).AvailableMillis;
            return new RenderPrice(seconds, tier, millis, available);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not price a render of timeline {TimelineId}; failing open", timeline.Id);
            return null;
        }
    }

    /// <summary>
    /// A documented heuristic, labelled an estimate everywhere it is shown: a fixed start-up of 10 s
    /// plus the output length times a factor per resolution tier (720p 0.5x, 1080p 1x, 4K 3x of real time),
    /// rounded up to 5 s. Region caching can make a re-render far quicker.
    /// </summary>
    internal static double EstimateRenderSeconds(double outputSeconds, string tier)
    {
        double factor = tier switch
        {
            ComputeMetering.Tier720 => 0.5,
            ComputeMetering.Tier1080 => 1.0,
            _ => 3.0
        };
        return Math.Ceiling((10 + (Math.Max(0, outputSeconds) * factor)) / 5) * 5;
    }

    /// <summary>
    /// The renders of this timeline — every execution of a <c>TimelineRender</c> workflow whose step
    /// names this timeline — newest first, capped at <see cref="MaxRendersListed"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Render workflows are hidden from the workflow list (<c>Kind == TimelineRender</c>), so this is
    /// the one place the editor's Deliver workspace sees its queue. The match is on the step config's
    /// <c>editTimelineId</c>, read back through the same options that wrote it; a config that does
    /// not parse simply does not match.
    /// </para>
    /// <para>
    /// Nothing here carries a storage key: a finished render is addressed by its step result id,
    /// which the outputs endpoints resolve, and the region list carries frames and a cached flag only.
    /// </para>
    /// </remarks>
    [HttpGet("{id:guid}/renders")]
    public async Task<ActionResult<List<EditTimelineRenderResponse>>> ListRenders(
        Guid projectId,
        Guid id,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        EditTimelineEntity? timeline = await FindTimelineAsync(projectId, id, ct);
        if (timeline is null) return NotFound();

        return Ok(await LoadRendersAsync(projectId, id, c => c.AudioJobKind is null, ct));
    }

    private async Task<List<EditTimelineRenderResponse>> LoadRendersAsync(
        Guid projectId, Guid id, Func<TimelineRenderStepConfig, bool> include, CancellationToken ct)
    {
        List<WorkflowDefinition> renderWorkflows = await _db.WorkflowDefinitions
            .Include(w => w.Steps)
            .Where(w => w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.TimelineRender)
            .ToListAsync(ct);

        Dictionary<Guid, (WorkflowStep Step, TimelineRenderStepConfig Config)> byWorkflow = [];
        foreach (WorkflowDefinition workflow in renderWorkflows)
        {
            WorkflowStep? step = workflow.Steps.FirstOrDefault(s => s.StepType == StepType.TimelineRender);
            TimelineRenderStepConfig? config = ParseRenderConfig(step?.TimelineRenderConfigJson);
            if (step is null || config is null || config.EditTimelineId != id || !include(config)) continue;
            byWorkflow[workflow.Id] = (step, config);
        }

        if (byWorkflow.Count == 0) return [];

        List<Guid> workflowIds = byWorkflow.Keys.ToList();
        List<WorkflowExecution> executions = await _db.WorkflowExecutions
            .Include(e => e.StepResults)
            .Where(e => e.ProjectId == projectId && workflowIds.Contains(e.WorkflowDefinitionId))
            .ToListAsync(ct);

        // R06: the project file each finished render registered (its id and size), one bounded query
        // for the whole list; a render that registered none (or whose file is gone) just has nulls.
        List<string> outputKeys = executions
            .SelectMany(e => e.StepResults)
            .SelectMany(r => new[] { r.OutputStorageKey }.Concat(ReadCaptionKeys(r.OutputJson).Select(k => (string?)k.Key)))
            .OfType<string>()
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Dictionary<string, (Guid Id, long SizeBytes, string FileName)> filesByKey = outputKeys.Count == 0
            ? []
            : (await _db.ProjectFiles
                .AsNoTracking()
                .Where(f => f.ProjectId == projectId && outputKeys.Contains(f.StorageKey))
                .Select(f => new { f.Id, f.StorageKey, f.SizeBytes, FileName = f.OriginalFileName })
                .ToListAsync(ct))
                .GroupBy(f => f.StorageKey, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (g.First().Id, g.First().SizeBytes, g.First().FileName), StringComparer.Ordinal);

        // A render whose output is not served lists without its result, so it shows no output, file or caption.
        HashSet<Guid> unavailable = await OutputGate.HeldResultIdsAsync(
            _db, projectId, executions.SelectMany(e => e.StepResults).Select(r => r.Id).ToList(), ct);

        List<EditTimelineRenderResponse> renders = executions
            .OrderByDescending(e => e.StartedAt ?? DateTime.MaxValue)
            .ThenByDescending(e => e.Id)
            .Take(MaxRendersListed)
            .Select(e =>
            {
                (WorkflowStep step, TimelineRenderStepConfig config) = byWorkflow[e.WorkflowDefinitionId];
                WorkflowStepResult? result = e.StepResults
                    .Where(r => r.WorkflowStepId == step.Id && r.Status == StepStatus.Completed && r.SupersededAt == null)
                    .OrderByDescending(r => r.CompletedAt ?? r.ExecutedAt)
                    .FirstOrDefault();
                if (result is not null && unavailable.Contains(result.Id)) result = null;
                (int? revision, EditTimelineRenderRegionsResponse? regions) = ReadRenderOutput(result?.OutputJson);
                EditTimelineRenderLoudnessResponse? loudness = ReadRenderLoudness(result?.OutputJson);
                (IReadOnlyList<EditTimelineRenderSkippedItemResponse> skippedItems, IReadOnlyList<string> warnings) = ReadRenderDiagnostics(result?.OutputJson);
                return new EditTimelineRenderResponse(
                    ExecutionId: e.Id,
                    WorkflowId: e.WorkflowDefinitionId,
                    TimelineId: id,
                    Status: e.Status.ToString(),
                    StartedAt: e.StartedAt,
                    CompletedAt: e.CompletedAt,
                    Revision: revision ?? config.Revision,
                    Preset: config.Preset.ToString(),
                    OutputFileName: config.OutputFileName,
                    ErrorMessage: e.ErrorMessage,
                    StepResultId: result?.Id,
                    HasOutput: !string.IsNullOrEmpty(result?.OutputStorageKey),
                    Regions: regions,
                    Loudness: loudness,
                    SkippedItems: skippedItems,
                    Warnings: warnings,
                    DurationSec: ReadRenderDurationSec(result?.OutputJson),
                    SizeBytes: result?.OutputStorageKey is { } sizedKey && filesByKey.TryGetValue(sizedKey, out (Guid Id, long SizeBytes, string FileName) sized) ? sized.SizeBytes : null,
                    OutputFileId: result?.OutputStorageKey is { } fileKey && filesByKey.TryGetValue(fileKey, out (Guid Id, long SizeBytes, string FileName) file) ? file.Id : null,
                    CreatedAt: e.QueuedAt ?? e.StartedAt,
                    CaptionFiles: ReadCaptionKeys(result?.OutputJson)
                        .Where(k => filesByKey.ContainsKey(k.Key))
                        .Select(k => new EditTimelineCaptionFileResponse(k.Format, filesByKey[k.Key].FileName, filesByKey[k.Key].SizeBytes, filesByKey[k.Key].Id))
                        .ToList());
            })
            .ToList();

        return renders;
    }

    /// <summary>R09.7: the sidecar storage keys a render's output JSON names (server-side only, never returned).</summary>
    private static IEnumerable<(string Format, string Key)> ReadCaptionKeys(string? outputJson)
    {
        if (string.IsNullOrEmpty(outputJson)) yield break;
        System.Text.Json.Nodes.JsonNode? node;
        try { node = System.Text.Json.Nodes.JsonNode.Parse(outputJson)?["captionSidecars"]; }
        catch (JsonException) { yield break; }
        if (node?["srtStorageKey"]?.GetValue<string>() is { Length: > 0 } srt) yield return ("srt", srt);
        if (node?["vttStorageKey"]?.GetValue<string>() is { Length: > 0 } vtt) yield return ("vtt", vtt);
    }

    /// <summary>The most renders one listing returns; the queue is a recent-history view, not an archive.</summary>
    internal const int MaxRendersListed = 50;

    /// <summary>P6: the interchange formats <see cref="ExportEditTimeline"/> writes.</summary>
    internal static readonly IReadOnlySet<string> ExportFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "otio", "edl", "srt", "vtt", "fcpxml" };

    /// <summary>P6: ceiling on an imported OTIO body (the document ceiling plus envelope headroom).</summary>
    public const int MaxImportBodyBytes = MaxDocumentBytes + (64 * 1024);

    /// <summary>
    /// P6: the timeline as an interchange file — <c>otio</c> (OpenTimelineIO JSON, lossless for
    /// ReelBolt through its metadata block), <c>edl</c> (CMX3600, first video track), <c>srt</c> or
    /// <c>vtt</c> (the captions). Served as a download; nothing in any format is a storage key.
    /// </summary>
    [HttpGet("{id:guid}/export")]
    public async Task<IActionResult> ExportEditTimeline(
        Guid projectId, Guid id, [FromQuery] string? format, CancellationToken ct,
        [FromQuery] bool includeAudio = true, [FromQuery] bool roles = true)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        string chosen = (format ?? "otio").Trim().ToLowerInvariant();
        if (!ExportFormats.Contains(chosen))
            return BadRequest(new { message = $"format must be one of {string.Join(", ", ExportFormats)}" });

        EditTimelineEntity? timeline = await FindTimelineAsync(projectId, id, ct);
        if (timeline is null) return NotFound();
        if (!TryDeserializeDocument(timeline, out EditTimeline document))
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = $"timeline {id} stores a document that cannot be parsed" });

        (string body, string mediaType, string extension) = chosen switch
        {
            "edl" => (TimelineInterchange.WriteEdl(document), "text/plain; charset=utf-8", "edl"),
            "srt" => (TimelineInterchange.WriteSrt(document), "application/x-subrip; charset=utf-8", "srt"),
            "vtt" => (TimelineInterchange.WriteVtt(document), "text/vtt; charset=utf-8", "vtt"),
            "fcpxml" => (FcpxmlWriter.Write(document, new FcpxmlOptions(includeAudio, roles)), "application/xml; charset=utf-8", "fcpxml"),
            _ => (TimelineInterchange.WriteOtio(document), "application/json; charset=utf-8", "otio")
        };

        string fileName = DefaultRenderFileName(timeline.Name);
        fileName = fileName[..^4] + "." + extension; // DefaultRenderFileName always ends in ".mp4"

        // T-004: an export is publication. Block/unavailable refuses it (fail closed); Review allows.
        if (_publishModeration is not null)
        {
            PublishModerationOutcome outcome = await _publishModeration.EvaluateTextAsync(
                body,
                new ModerationAttribution(
                    access.Project!.OrganizationId, projectId, _currentUser.UserId, null, null,
                    ModerationSurface.Publish, ModerationContentKind.Text),
                ct);
            if (!outcome.Allowed)
            {
                return outcome.Code == "CONTENT_BLOCKED"
                    ? StatusCode(StatusCodes.Status422UnprocessableEntity, new { message = outcome.Message, code = outcome.Code, @final = outcome.Final })
                    : StatusCode(StatusCodes.Status409Conflict, new { message = outcome.Message, code = outcome.Code, @final = outcome.Final });
            }
        }

        // DS-4: server-generated bytes, but still a download of customer-derived text: nosniff/CSP/CORP and an
        // attachment disposition built by the policy, never inline.
        SafeContentPolicy.ApplySecurityHeaders(Response);
        Response.Headers["Content-Disposition"] = SafeContentPolicy.BuildContentDisposition(false, fileName);
        return File(Encoding.UTF8.GetBytes(body), mediaType, fileName);
    }

    /// <summary>
    /// T-020: what an export carries and drops, machine-readable, so the UI words its scope text from the server's truth.
    /// <c>GET …/export/scope?format=otio|edl|srt|vtt|fcpxml</c> returns <c>{format, carries[], dropped[], mediaMode}</c>.
    /// </summary>
    [HttpGet("{id:guid}/export/scope")]
    public async Task<IActionResult> GetExportScope(Guid projectId, Guid id, [FromQuery] string? format, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        string chosen = (format ?? "otio").Trim().ToLowerInvariant();
        if (!ExportFormats.Contains(chosen))
            return BadRequest(new { message = $"format must be one of {string.Join(", ", ExportFormats)}" });

        EditTimelineEntity? timeline = await FindTimelineAsync(projectId, id, ct);
        if (timeline is null) return NotFound();

        // R12.2: what a hand-off package would carry (counts from the document, sizes from storage records).
        HandOffScopeResponse handOff = new(false, 0, 0, false, _handOff.CapBytes);
        if (HandOffFormats.Contains(chosen) && TryDeserializeDocument(timeline, out EditTimeline document))
        {
            HandOffPlan plan = await HandOffPlanner.PlanAsync(_db, _storage, projectId, document, _handOff.CapFiles, ct);
            handOff = new HandOffScopeResponse(true, plan.MediaFiles, plan.MediaBytes, ExceedsCap(plan), _handOff.CapBytes);
        }

        // T-011: a camera log profile is applied at render time, not written into an interchange file;
        // every format but OTIO (which carries the lossless ReelBolt document) therefore drops it.
        InterchangeScopeResponse scope = InterchangeScope.For(chosen);
        if (!string.Equals(chosen, "otio", StringComparison.OrdinalIgnoreCase))
            scope = scope with { Dropped = [.. scope.Dropped, CameraProfileDroppedReason] };
        return Ok(scope with { HandOff = handOff });
    }

    /// <summary>T-011: the plain reason a camera profile is not carried by an interchange export.</summary>
    internal const string CameraProfileDroppedReason = "Camera profile conversion is not carried; apply it before importing";

    /// <summary>R12.2: the formats a hand-off package can carry (the ones with media references).</summary>
    internal static readonly IReadOnlySet<string> HandOffFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "otio", "edl", "fcpxml" };

    private bool ExceedsCap(HandOffPlan plan) =>
        plan.MediaFiles > 0 && (_handOff.CapBytes == 0 || plan.MediaFiles > _handOff.CapFiles || plan.MediaBytes > _handOff.CapBytes);

    /// <summary>
    /// R12.2: the hand-off package for a pro editor: <c>GET …/{id}/handoff?format=otio|edl|fcpxml&amp;media=zip|manifest&amp;track=&amp;includeAudio=&amp;roles=</c>.
    /// Streams a ZIP: the timeline file (media referenced as <c>media/&lt;real file name&gt;</c>), the media folder, README.txt and
    /// manifest.json (last). <c>track</c> (EDL only) is the id of the video track to write instead of the first. Over the
    /// size or file cap, <c>media=zip</c> answers <c>409 HANDOFF_TOO_LARGE</c> (the UI then offers <c>media=manifest</c>);
    /// at the per-user stream limit, <c>429 HANDOFF_BUSY</c>. A storage object that is missing or unreadable is listed in the
    /// manifest and the zip still completes. Free of credits. Gated by the same publish moderation as an export.
    /// </summary>
    [HttpGet("{id:guid}/handoff")]
    public async Task<IActionResult> HandOffEditTimeline(
        Guid projectId, Guid id, [FromQuery] string? format, [FromQuery] string? media, [FromQuery] string? track, CancellationToken ct,
        [FromQuery] bool includeAudio = true, [FromQuery] bool roles = true)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        string chosen = (format ?? "otio").Trim().ToLowerInvariant();
        if (!HandOffFormats.Contains(chosen))
            return BadRequest(new { message = $"format must be one of {string.Join(", ", HandOffFormats)}" });
        string mediaMode = (media ?? "zip").Trim().ToLowerInvariant();
        if (mediaMode is not ("zip" or "manifest"))
            return BadRequest(new { message = "media must be zip or manifest" });

        EditTimelineEntity? timeline = await FindTimelineAsync(projectId, id, ct);
        if (timeline is null) return NotFound();
        if (!TryDeserializeDocument(timeline, out EditTimeline document))
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = $"timeline {id} stores a document that cannot be parsed" });

        EditTimeline forEdl = document;
        if (!string.IsNullOrWhiteSpace(track))
        {
            Track? chosenTrack = Guid.TryParse(track, out Guid trackId) ? document.Tracks.FirstOrDefault(t => t.Id == trackId && t.Kind == TrackKind.Video) : null;
            if (chosenTrack is null) return BadRequest(new { message = "track must be the id of a video track of this timeline" });
            forEdl = document with { Tracks = [chosenTrack, .. document.Tracks.Where(t => t.Id != chosenTrack.Id)] };
        }

        IDisposable? lease = _handOffLimiter.TryEnter(_currentUser.UserId);
        if (lease is null)
            return StatusCode(StatusCodes.Status429TooManyRequests, new { code = "HANDOFF_BUSY", message = "You already have hand-off packages being prepared. Wait for one to finish, then try again." });

        bool handedOver = false;
        try
        {
            HandOffPlan plan = await HandOffPlanner.PlanAsync(_db, _storage, projectId, document, _handOff.CapFiles, ct);

            bool includeMedia = mediaMode == "zip";
            string? notIncludedReason = includeMedia ? null : "You chose a package without the media files.";
            if (includeMedia && ExceedsCap(plan))
            {
                return Conflict(new HandOffTooLargeResponse(
                    "HANDOFF_TOO_LARGE",
                    _handOff.CapBytes == 0
                        ? "Hand-off packages with media are turned off here. Download the package without media instead."
                        : "The media is too big to put in one download. Download the package without media, then copy the files over yourself.",
                    plan.MediaBytes,
                    _handOff.CapBytes));
            }

            HandOffMediaNames names = plan.Names();
            string body = chosen switch
            {
                "edl" => TimelineInterchange.WriteEdl(forEdl, names),
                "fcpxml" => FcpxmlWriter.Write(document, new FcpxmlOptions(includeAudio, roles, names)),
                _ => TimelineInterchange.WriteOtio(document, names)
            };

            // An export is publication (T-004): the interchange text, and the media bytes when they are included. Fail closed.
            if (_publishModeration is not null)
            {
                ModerationAttribution attribution = new(
                    access.Project!.OrganizationId, projectId, _currentUser.UserId, null, null,
                    ModerationSurface.Publish, ModerationContentKind.Text);
                PublishModerationOutcome outcome = await _publishModeration.EvaluateTextAsync(body, attribution, ct);
                if (outcome.Allowed && includeMedia)
                {
                    // Every clip is read (and every pending one offered) before refusing, so one click starts
                    // them all instead of one clip per attempt.
                    foreach (HandOffSource source in plan.Sources.Where(s => s.Resolvable))
                    {
                        PublishModerationOutcome clip = await _publishModeration.EvaluateObjectAsync(
                            projectId, source.StorageKey!, source.MimeType,
                            attribution with { StepResultId = source.Reference.StepResultId }, ct);
                        PublishModerationOutcome named = !clip.Allowed && clip.Final && clip.Code != "CONTENT_BLOCKED"
                            ? PublishModerationOutcome.UnavailableFinalClip(source.DisplayName, clip.TooLong)
                            : clip;
                        if (!named.Allowed && (outcome.Allowed || named.Code == "CONTENT_BLOCKED")) outcome = named;
                    }
                }

                if (!outcome.Allowed)
                {
                    return outcome.Code == "CONTENT_BLOCKED"
                        ? StatusCode(StatusCodes.Status422UnprocessableEntity, new { message = outcome.Message, code = outcome.Code, @final = outcome.Final })
                        : StatusCode(StatusCodes.Status409Conflict, new { message = outcome.Message, code = outcome.Code, @final = outcome.Final });
                }
            }

            string slug = DefaultRenderFileName(timeline.Name);
            slug = System.Text.RegularExpressions.Regex.Replace(slug[..^4].ToLowerInvariant().Replace(' ', '-'), "-{2,}", "-").Trim('-', '.');
            if (slug.Length == 0) slug = "timeline";
            HandOffPackageInput input = new(
                projectId, string.IsNullOrWhiteSpace(timeline.Name) ? "Timeline" : timeline.Name.Trim(), chosen,
                slug + "." + chosen, body, plan.Sources, includeMedia, notIncludedReason, _clock.GetUtcNow().UtcDateTime);

            handedOver = true;
            return new HandOffStreamResult(
                slug + "-hand-off.zip",
                (output, token) => HandOffPackage.WriteAsync(output, input, _storage, _logger, token),
                lease);
        }
        finally
        {
            if (!handedOver) lease.Dispose();
        }
    }

    /// <summary>
    /// P6: creates a timeline from an OpenTimelineIO JSON document. A file ReelBolt exported restores
    /// its full document (every asset still has to resolve in THIS project); any other OTIO imports as
    /// cuts — one <c>MediaClip</c> per <c>Clip.1</c> whose <c>target_url</c> is a reelbolt URL or
    /// matches a project file's name, gaps as gaps, transitions as dissolves. A clip that resolves to
    /// nothing refuses the whole import (<c>400</c> with the unresolved URLs) rather than guessing.
    /// The result is validated like every other write and born with its revision-1 snapshot.
    /// </summary>
    [HttpPost("import")]
    [RequestSizeLimit(MaxImportBodyBytes)]
    public async Task<ActionResult<EditTimelineResponse>> ImportEditTimeline(Guid projectId, [FromBody] ImportEditTimelineRequest request, CancellationToken ct, [FromQuery] bool strict = false)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (request.Otio.ValueKind != JsonValueKind.Object)
            return BadRequest(new EditTimelineImportRefusedResponse("otio must be the OTIO timeline as a JSON object", ["otio is not a JSON object"], []));

        Guid timelineId = Guid.NewGuid();
        // A name the caller typed is refused like on create; one read from a third-party file
        // falls back to the default rather than failing the whole import.
        string name = string.IsNullOrWhiteSpace(request.Name)
            ? (request.Otio.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()) && !HasControlCharacter(n.GetString()!.Trim()) && n.GetString()!.Trim().Length <= MaxNameLength ? n.GetString()!.Trim() : "Imported timeline")
            : request.Name.Trim();
        if (HasControlCharacter(name))
        {
            return BadRequest(new { message = NameHasControlCharacterMessage });
        }

        if (name.Length > MaxNameLength)
        {
            return BadRequest(new { message = NameTooLongMessage });
        }

        SeedAssetResolver seedResolver = await SeedAssetResolver.LoadAsync(_db, projectId, ct);
        var fileRows = await _db.ProjectFiles.AsNoTracking().Where(f => f.ProjectId == projectId
                && f.CsamStatus != CsamScanStatus.Quarantined
                && f.ModerationStatus != FileModerationStatus.Quarantined
                && !f.StorageKey.Contains("/quarantine/"))
            .Select(f => new { f.Id, f.OriginalFileName, f.StorageKey, f.SizeBytes }).ToListAsync(ct);
        HashSet<Guid> ownedFiles = fileRows.Select(f => f.Id).ToHashSet();
        HashSet<Guid> ownedStepResults = (await _db.WorkflowStepResults.Where(r => r.WorkflowExecution.ProjectId == projectId).Select(r => r.Id).ToListAsync(ct)).ToHashSet();
        // R12.3: durations come from the stored ingest probes (never computed here); a file never probed has none.
        Dictionary<string, MediaSourceProbe> probes = await StoredMediaProbes.LoadAsync(_db, projectId, fileRows.Select(f => f.StorageKey).ToList(), ct);
        ImportMediaMatcher matcher = new(
            fileRows.Select(f => new MediaCandidate(f.Id, f.OriginalFileName, probes.TryGetValue(f.StorageKey, out MediaSourceProbe? probe) && probe.DurationSec > 0 ? probe.DurationSec : null, f.SizeBytes)),
            owned => owned.Kind == AssetRefKind.ProjectFile ? ownedFiles.Contains(owned.ProjectFileId ?? Guid.Empty) : ownedStepResults.Contains(owned.StepResultId ?? Guid.Empty),
            seedResolver.ResolveKey);

        TimelineInterchange.OtioImportResult imported = TimelineInterchange.ReadOtio(request.Otio, projectId, timelineId, name, matcher.Resolve, allowOffline: !strict);
        if (!imported.Succeeded || imported.Document is null)
        {
            return BadRequest(new EditTimelineImportRefusedResponse(
                imported.UnresolvedUrls.Count > 0 ? "some media in the file does not exist in this project" : "the OTIO file could not be read as a timeline",
                imported.Errors,
                imported.UnresolvedUrls));
        }

        EditTimeline document = imported.Document;
        ValidationResult validation = EditTimelineValidator.Validate(document, await BuildValidationContextAsync(projectId, document, ct));
        if (!validation.IsValid)
        {
            return BadRequest(validation.Violations.Select(v => new EditTimelineViolation(v.Path, v.Message)).ToList());
        }

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        EditTimelineEntity entity = new()
        {
            Id = timelineId,
            ProjectId = projectId,
            Name = name,
            DocumentJson = SerializeDocument(document),
            Revision = 1,
            SeedStepResultId = null,
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = null
        };
        _db.EditTimelines.Add(entity);
        _db.EditTimelineSnapshots.Add(NewSnapshot(timelineId, 1, entity.DocumentJson, EditTimelineSnapshotCause.Import, now));
        await _db.SaveChangesAsync(ct);

        EditTimelineResponse created = ToResponse(entity, document) with
        {
            Offline = imported.OfflineClips.Select(o => new EditTimelineOfflineClip(o.ItemId, o.Name, o.Reason, o.Message)).ToList(),
            Matched = imported.MatchedMedia.Select(m => new EditTimelineMatchedMedia(m.Name, m.FileId, m.How, m.StepResultId)).ToList(),
            Ambiguous = imported.AmbiguousMedia.Select(a => new EditTimelineAmbiguousMedia(a.Name, a.Candidates.Select(c => new EditTimelineMediaCandidate(c.FileId, c.Name, c.DurationSec, c.SizeBytes)).ToList())).ToList()
        };
        return StatusCode(StatusCodes.Status201Created, created);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // P8 — AI-assist proposals
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Audio project files offered to the SFX agent, at most this many (aliases x1..xN).</summary>
    public const int MaxOfferedAudioFiles = 24;

    /// <summary>
    /// Millicredits a proposal is expected to cost at most: <c>ceil((brief + view characters) / 4)</c> input tokens plus
    /// <see cref="AgentEditProposalModel.MaxOutputTokens"/> output tokens, priced on the chat model the agent resolves to; 0 when that cannot be resolved or priced (the floor applies);
    /// null when it resolves to a chat provider the workspace pays for itself (BYO or Runner), which costs no credits
    /// and so takes no hold, as editor ops already do through <c>EstimateOneUnitAsync</c>.
    /// </summary>
    private async Task<long?> EstimateProposalAsync(ResolvedInferenceProvider? provider, string viewText, string? brief, CancellationToken ct)
    {
        if (_charges is null || provider is null) return 0;
        if (!provider.IsPlatformManaged) return null;
        long chars = (long)viewText.Length + (brief?.Length ?? 0);
        decimal inputTokens = Math.Max(1, (chars + 3) / 4);
        PriceRequest Request(UsageKind unit, decimal quantity) => new(
            provider.ProviderId,
            provider.AccountId,
            provider.Kind,
            provider.Capability,
            provider.ModelName,
            unit,
            null,
            UsageBillingMode.Platform,
            quantity,
            PromptTokens: (long)inputTokens);

        // The input, plus the most the model may write (T-032 low a: its MaxOutputTokens at the output price), so the
        // hold covers the call's worst case rather than its input alone.
        long input = await _charges.EstimateAsync(Request(UsageKind.LlmInputTokens, inputTokens), ct);
        long output = await _charges.EstimateAsync(Request(UsageKind.LlmOutputTokens, AgentEditProposalModel.MaxOutputTokens), ct);
        return input + output;
    }

    /// <summary>
    /// T-026 (finding 5): the chat provider the proposal's agent resolves to, resolved ONCE: the hold is sized on it and
    /// the model is called on it. Null when it cannot be resolved (the hold then takes its floor). An unpriced Platform
    /// model is not "cannot be resolved": <see cref="ModelUnpricedException"/> propagates and the proposal is refused (409).
    /// </summary>
    private async Task<ResolvedInferenceProvider?> ResolveProposalProviderAsync(string agent, CancellationToken ct)
    {
        if (_chatClients is null) return null;
        try
        {
            return await _chatClients.ResolveProviderAsync(EditProposalAgents.AgentTypeOf(agent), null, ct);
        }
        catch (Exception ex) when (ex is not ModelUnpricedException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Validates a proposal/estimate body and builds the model's view; the failure is the ready 400/404/500 answer.</summary>
    private async Task<(ActionResult? Failure, string Agent, EditTimeline? Document, EditProposalView? View, double Fps)> PrepareProposalAsync(
        Guid projectId, Guid id, EditProposalRequest request, CancellationToken ct)
    {
        string agent = (request.Agent ?? string.Empty).Trim().ToLowerInvariant();
        if (!EditProposalAgents.IsKnown(agent))
            return (BadRequest(new { message = $"agent must be one of {string.Join(", ", EditProposalAgents.All)}" }), agent, null, null, 0);

        if (request.Range is { } r && (r.InFrame < 0 || r.OutFrame <= r.InFrame))
            return (BadRequest(new { message = "range must satisfy 0 <= inFrame < outFrame" }), agent, null, null, 0);

        if (request.Brief is { Length: > MaxProposalBriefChars })
            return (BadRequest(new { message = $"The brief can be at most {MaxProposalBriefChars} characters." }), agent, null, null, 0);

        EditTimelineEntity? timeline = await FindTimelineAsync(projectId, id, ct);
        if (timeline is null) return (NotFound(), agent, null, null, 0);
        if (!TryDeserializeDocument(timeline, out EditTimeline document))
            return (StatusCode(StatusCodes.Status500InternalServerError, new { message = $"timeline {id} stores a document that cannot be parsed" }), agent, null, null, 0);

        List<(Guid Id, string Name)> offeredAudio = [];
        if (agent == EditProposalAgents.Sfx)
        {
            offeredAudio = (await _db.ProjectFiles
                .Where(f => f.ProjectId == projectId && f.MimeType.StartsWith("audio/"))
                .OrderBy(f => f.OriginalFileName)
                .Take(MaxOfferedAudioFiles)
                .Select(f => new { f.Id, f.OriginalFileName })
                .ToListAsync(ct))
                .Select(f => (f.Id, f.OriginalFileName))
                .ToList();
        }

        double fps = document.Settings.Rate.Den > 0 ? document.Settings.Rate.Num / (double)document.Settings.Rate.Den : 30;
        // R11.2: what stored analysis says about the footage in the document. Read-only; never starts an analysis.
        List<MediaClip> mediaClips = document.Tracks.SelectMany(t => t.Items).OfType<MediaClip>().ToList();
        List<Guid> fileIds = mediaClips.Select(c => c.AssetRef.ProjectFileId).OfType<Guid>().Distinct().ToList();
        // A clip whose asset is a workflow render output is grounded by the analysis keyed step_result:<id>.
        List<Guid> stepResultIds = mediaClips.Where(c => c.AssetRef.ProjectFileId is null && c.AssetRef.Kind == AssetRefKind.StepResult)
            .Select(c => c.AssetRef.StepResultId).OfType<Guid>().Distinct().ToList();
        ReelBolt.Inference.Api.Services.Editing.ProposalFactsLoader.LoadResult loaded =
            await ReelBolt.Inference.Api.Services.Editing.ProposalFactsLoader.LoadWithStatusAsync(_db, _storage, _logger, projectId, fileIds, stepResultIds, ct);
        EditProposalView built = EditProposalView.Build(document, request.Range, offeredAudio, fps, loaded.Facts, _maxTranscriptChars);
        if (loaded.ReadFailed)
        {
            // Proposing still goes on, but the answer says the stored analysis could not be read instead of looking merely un-analysed.
            ProposalGrounding g = built.Grounding ?? ProposalGrounding.None(0);
            built = built with { Grounding = g with { Note = "The stored analysis of this footage could not be read, so this proposal does not use it." } };
        }

        return (null, agent, document, built, fps);
    }

    /// <summary>
    /// R11.2 (W4-D4): Grade and Review are offered only when grounded. Null when the agent may run; otherwise the plain reason.
    /// Grade needs shots with colour or brightness words; Review needs a transcript and/or shots. Re-cut, Graphics and SFX always run.
    /// </summary>
    internal static string? NeedsAnalysisReason(string agent, ProposalGrounding? g)
    {
        if (g is null || g.ClipsTotal == 0) return agent is EditProposalAgents.Grade or EditProposalAgents.Review ? "There are no clips in this range yet." : null;
        return agent switch
        {
            EditProposalAgents.Grade when !g.HasColourFacts => "Analyse this footage first so the colourist can see its colours and light.",
            EditProposalAgents.Review when !g.HasTranscriptOrShotFacts => "Analyse this footage first so the reviewer has the words and shots to go on.",
            _ => null
        };
    }

    /// <summary>The plain seed would show picture the film censors, so nothing is created.</summary>
    private ObjectResult RefuseUncensoredSeed(Guid seedStepResultId, Guid projectId)
    {
        _logger.LogWarning(
            "Refusing to seed an edit timeline from step result {StepResultId} in project {ProjectId}: it applies region effects the plain seed cannot carry",
            seedStepResultId, projectId);
        return StatusCode(StatusCodes.Status409Conflict, new
        {
            message = PlainSeedCensorGuard.Message,
            code = PlainSeedCensorGuard.Code
        });
    }

    private ObjectResult NeedsAnalysis(string reason) => StatusCode(StatusCodes.Status409Conflict, new
    {
        code = "PROPOSAL_NEEDS_ANALYSIS",
        message = reason + " You were not charged."
    });

    /// <summary>The most media refs a needs-analysis answer lists.</summary>
    private const int MaxNeedsAnalysisRefs = 8;

    private static EditProposalNeedsAnalysisResponse ToNeedsAnalysis(AnalysisGap gap) => new(
        gap.Kinds, gap.MissingFileIds.Take(MaxNeedsAnalysisRefs).Select(gap.RefOf).ToList());

    /// <summary>
    /// A Re-cut whose request names speech, fillers, silence or lines to keep is only as good as the stored analysis it cuts by.
    /// Null when the request needs none, nothing is analysable in range, or at least one clip already has it.
    /// </summary>
    private static AnalysisGap? RecutGap(string agent, EditProposalView view, string? brief)
    {
        if (agent != EditProposalAgents.Recut) return null;
        (RecutOpClass? op, RecutTarget? target) = RecutRequestIntent.Classify(brief);
        // Only a content cut needs the stored analysis; a request that merely mentions speech ("make the dialogue louder") does not.
        if (target is null || op != RecutOpClass.Cut) return null;
        return view.AnalysisGapFor(target.Value);
    }

    private ObjectResult NeedsAnalysis(AnalysisGap gap) => StatusCode(StatusCodes.Status409Conflict, new
    {
        code = "PROPOSAL_NEEDS_ANALYSIS",
        message = (gap.Kinds.Contains("silence")
            ? "Analyse the quiet parts of this footage first so the cut can find them."
            : "Analyse what is said in this footage first so the cut can find the words.") + " You were not charged.",
        analysis = ToNeedsAnalysis(gap)
    });

    private static EditProposalGroundingResponse? ToGroundingResponse(ProposalGrounding? g) =>
        g is null ? null : new EditProposalGroundingResponse(g.Level, g.ClipsWithFacts, g.ClipsTotal, g.Note);

    /// <summary>
    /// R11.2: which proposal agents can run on a range, so the UI gates without guessing. No model call, no charge, nothing
    /// written. View permission; a foreign tenant's timeline is a 404 like everywhere else.
    /// </summary>
    [HttpGet("{id:guid}/proposals/availability")]
    public async Task<ActionResult<EditProposalAvailabilityResponse>> ProposalAvailability(
        Guid projectId, Guid id, [FromQuery] long? startFrame, [FromQuery] long? endFrame, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        EditRange? range = startFrame is null && endFrame is null ? null : new EditRange(startFrame ?? 0, endFrame ?? long.MaxValue / 4);
        (ActionResult? invalid, _, EditTimeline? document, EditProposalView? view, _) =
            await PrepareProposalAsync(projectId, id, new EditProposalRequest(EditProposalAgents.Recut, range, null), ct);
        if (invalid is not null || document is null || view is null) return invalid ?? NotFound();

        ProposalGrounding grounding = view.Grounding ?? ProposalGrounding.None(0);
        EditProposalAgentAvailability Agent(string agent) => NeedsAnalysisReason(agent, grounding) is { } why
            ? new EditProposalAgentAvailability(false, null, why)
            : new EditProposalAgentAvailability(true, null, null);

        bool hasSounds = await _db.ProjectFiles.AnyAsync(f => f.ProjectId == projectId && f.MimeType.StartsWith("audio/"), ct);
        EditProposalAgentAvailability sfx = hasSounds ? new(true, null, null) : new(false, null, "Add a sound file to this project first.");
        return Ok(new EditProposalAvailabilityResponse(
            new EditProposalAgentsAvailability(
                new EditProposalAgentAvailability(true, ToGroundingResponse(grounding), null),
                Agent(EditProposalAgents.Grade),
                Agent(EditProposalAgents.Review),
                new EditProposalAgentAvailability(true, null, null),
                sfx),
            grounding.ClipsWithFacts < grounding.ClipsTotal,
            view.AnalysisGapFor(RecutTarget.Speech) is { MissingFileIds.Count: > 0 } missingTranscript ? ToNeedsAnalysis(missingTranscript) : null));
    }

    /// <summary>R11.1: what the model is sent, as counts and one plain sentence; never ids, names or media.</summary>
    internal static EditProposalDisclosure DescribeProposalContext(EditProposalView view, string? brief, double fps)
    {
        int items = view.ItemAliases.Count;
        double seconds = Math.Round(Math.Max(0, view.OutFrame - view.InFrame) / Math.Max(1, fps), 1);
        int briefChars = brief?.Trim().Length ?? 0;
        int sounds = view.FileAliases.Count;
        string clips = items == 1 ? "1 item" : $"{items} items";
        string sentence = $"Sent to the AI: a text outline of {clips} covering {seconds:0.#} seconds of your timeline (names and times only)"
            + (sounds > 0 ? $", the names of {sounds} sound {(sounds == 1 ? "file" : "files")}" : string.Empty)
            + (briefChars > 0 ? ", and your note" : string.Empty);
        if (view.BeatClips is { Count: > 0 }) sentence += ", a few words about the beat of your music";
        if (view.MomentRefs.Count > 0) sentence += ", a few words about moments found in your picture";
        ProposalGrounding? g = view.Grounding;
        if (g is not null && g.Level != ProposalGrounding.NamesOnly)
        {
            sentence += ", plus short text excerpts of what is said and descriptions of the shots from your analysis. No video or audio leaves your project.";
            return new EditProposalDisclosure(items, seconds, sounds, briefChars, Math.Round(g.TranscriptSeconds, 1), sentence);
        }

        sentence += ". No video, audio or transcript leaves your project.";
        return new EditProposalDisclosure(items, seconds, sounds, briefChars, 0, sentence);
    }

    private static string? ProposalModelLabel(ResolvedInferenceProvider? provider) =>
        provider is null ? null : string.IsNullOrWhiteSpace(provider.Name) ? provider.ModelName : $"{provider.ModelName} ({provider.Name})";

    /// <summary>
    /// R11.1: what a proposal would cost before it is asked for, in the render-estimate shape. Nothing is persisted, no model
    /// is called and the hourly cap is not touched; the balance is a plain read (no billing lock).
    /// </summary>
    [HttpPost("{id:guid}/proposals/estimate")]
    public async Task<ActionResult<EditProposalEstimateResponse>> EstimateProposal(
        Guid projectId, Guid id, [FromBody] EditProposalRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        (ActionResult? invalid, string agent, EditTimeline? document, EditProposalView? view, double fps) =
            await PrepareProposalAsync(projectId, id, request, ct);
        if (invalid is not null || view is null) return invalid ?? NotFound();
        if (NeedsAnalysisReason(agent, view.Grounding) is { } needs) return NeedsAnalysis(needs);
        if (RecutGap(agent, view, request.Brief) is { NoneCovered: true } recutGap) return NeedsAnalysis(recutGap);

        EditProposalDisclosure disclosure = DescribeProposalContext(view, request.Brief, fps);
        Guid userId = _currentUser.UserId;
        EditProposalRateState rate = _proposalLimiter?.Peek(userId, projectId) ?? new EditProposalRateState(0, 0, 0);

        ResolvedInferenceProvider? provider;
        long? estimated = null;
        try
        {
            provider = await ResolveProposalProviderAsync(agent, ct);
            if (_charges is not null && access.Project?.OrganizationId is not null)
                estimated = await EstimateProposalAsync(provider, view.Text, request.Brief, ct);
        }
        catch (ModelUnpricedException unpriced)
        {
            return ModelUnpricedResults.Conflict(unpriced);
        }

        string? label = ProposalModelLabel(provider);
        EditProposalEstimateResponse Answer(bool billing, double? credits, double? balance, bool affordable, bool included, string? reason) => new(
            billing, credits, credits, credits, null, balance, affordable, reason, included, label, disclosure, rate.Used, rate.Limit, rate.Full ? rate.RetryAfterSeconds : 0,
            ToGroundingResponse(view.Grounding), view.Grounding is { } gr && gr.ClipsWithFacts < gr.ClipsTotal);

        if (!BillingOn || access.Project?.OrganizationId is not { } org)
            return Ok(Answer(false, null, null, true, false, null));
        if (estimated is null)
            return Ok(Answer(true, 0, null, true, true, null));

        long floorMillis = _charges!.Options.NonWorkflow.TimelineProposalHoldCredits * 1000;
        long required = Math.Max(floorMillis, estimated.Value);
        long? available = await _charges.GetAvailableMillisAsync(org, ct);
        return Ok(Answer(true, required / 1000.0, available is null ? null : available / 1000.0, available is null || available >= required, false, null));
    }

    /// <summary>The longest brief a proposal accepts; it is sent to the model and sizes the credit hold.</summary>
    public const int MaxProposalBriefChars = 2000;

    /// <summary>
    /// P8: runs one of the five assist agents over a range of the timeline and returns an op batch the
    /// client applies through <c>applyProposal</c> — nothing is written here. Edit permission, since a
    /// proposal reads the whole document and names the editor's clips. <c>400</c> for an unknown agent,
    /// <c>404</c> for a timeline this project does not hold, <c>503 no_chat_provider</c> when no chat
    /// provider resolves for that agent. The model sees aliases, never ids; the compiler resolves them
    /// and rejects what does not resolve.
    /// </summary>
    [HttpPost("{id:guid}/proposals")]
    public async Task<ActionResult<EditProposalResponse>> ProposeEdit(
        Guid projectId,
        Guid id,
        [FromBody] EditProposalRequest request,
        [FromServices] IEditProposalModel model,
        CancellationToken ct,
        [FromQuery] bool background = false,
        [FromServices] EditProposalJobStore? jobs = null)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        (ActionResult? invalid, string agent, EditTimeline? document, EditProposalView? view, double fps) =
            await PrepareProposalAsync(projectId, id, request, ct);
        if (invalid is not null || document is null || view is null) return invalid ?? NotFound();

        // R11.2 (W4-D4): Grade and Review run only on grounded footage. Before the hourly cap, the hold and the model:
        // a refusal here charges nothing, counts nothing and writes nothing.
        if (NeedsAnalysisReason(agent, view.Grounding) is { } needsAnalysis) return NeedsAnalysis(needsAnalysis);
        if (RecutGap(agent, view, request.Brief) is { NoneCovered: true } recutGap) return NeedsAnalysis(recutGap);

        if (_proposalLimiter?.Peek(_currentUser.UserId, projectId) is { Full: true } capped) return ProposalRateLimited(capped);

        // Credits: a proposal is charged per call (decision 9). The hold is sized from the request (about a token per
        // four characters of the brief and the serialized view, plus the call's 2000 output tokens, priced on the
        // agent's resolved chat model), never
        // below Billing:NonWorkflow:TimelineProposalHoldCredits; it is taken before the model runs and settled at
        // what the call metered, capped at the hold plus its overdraft headroom. A BYO or Runner chat provider costs
        // no credits, so it takes no hold at all.
        // T-026 (finding 5): the chat provider is resolved once, and that one resolution both sizes the hold and is the
        // row the model is called on, so a BYO estimate can never meet a Platform call.
        ChargeHold? hold = null;
        ResolvedInferenceProvider? chatProvider;
        long? estimated;
        Guid requestId = Guid.NewGuid();
        long floorMillis = (_charges?.Options.NonWorkflow.TimelineProposalHoldCredits ?? 0) * 1000;
        try
        {
            chatProvider = await ResolveProposalProviderAsync(agent, ct);
            estimated = _charges is not null && access.Project?.OrganizationId is not null
                ? await EstimateProposalAsync(chatProvider, view.Text, request.Brief, ct)
                : null;
        }
        catch (ModelUnpricedException unpriced)
        {
            return ModelUnpricedResults.Conflict(unpriced);
        }

        // Background only: the running slot is reserved atomically here, before the hold, and given back on any refusal below.
        bool reserved = false;
        if (background && jobs is not null)
        {
            if (!jobs.TryReserve(_currentUser.UserId))
            {
                Response.Headers.RetryAfter = "10";
                return StatusCode(StatusCodes.Status429TooManyRequests, new
                {
                    code = "PROPOSALS_BUSY",
                    message = "Suggestions are busy right now. Wait for one to finish, then try again.",
                    retryAfter = 10
                });
            }

            reserved = true;
        }

        if (_charges is not null && access.Project?.OrganizationId is { } chargedOrg && estimated is { } estimateMillis)
        {
            try
            {
                hold = await _charges.BeginHoldAsync(
                    chargedOrg, UsageSource.TimelineAssist, Math.Max(floorMillis, estimateMillis),
                    requestId, new CreditLedgerContext(projectId, Note: agent), ct);
            }
            catch
            {
                if (reserved) jobs!.CancelReservation(_currentUser.UserId);
                throw;
            }

            if (!hold.Allowed)
            {
                if (reserved) jobs!.CancelReservation(_currentUser.UserId);
                return NonWorkflowChargeResults.Refusal(hold, "suggestion");
            }
        }

        // Counted only now (after the 402 gate, before the model call), so a refused request never uses up a slot.
        if (_proposalLimiter is not null && !_proposalLimiter.TryAcquire(_currentUser.UserId, projectId, out EditProposalRateState taken))
        {
            await NonWorkflowChargeResults.CloseHoldAsync(
                _charges, hold, null, HttpContext?.RequestServices?.GetService<ILogger<EditTimelinesController>>());
            if (reserved) jobs!.CancelReservation(_currentUser.UserId);
            return ProposalRateLimited(taken);
        }

        Guid callerId = _currentUser.UserId;
        Guid? chargedOrgId = access.Project?.OrganizationId;
        ILogger? logger = HttpContext?.RequestServices?.GetService<ILogger<EditTimelinesController>>();
        double timeoutSeconds = (jobs?.Options ?? new EditProposalOptions()).TimeoutSeconds;

        if (background && jobs is not null)
        {
            // The 202 answers now; the model call, settle and compile continue on a background task that must never touch
            // HttpContext or the request-scoped services (their scope ends with this request). The charge service is
            // resolved again from a scope of its own; the hold taken above is closed through it.
            IServiceScopeFactory? scopes = HttpContext?.RequestServices?.GetService<IServiceScopeFactory>();
            INonWorkflowChargeService? requestCharges = _charges;
            Guid jobId = jobs.Start(callerId, projectId, id, async jobCt =>
            {
                (ObjectResult result, Func<bool, Task>? close) = await RunProposalAsync(
                    requestCharges, scopes, logger, model, agent, document, view, request, fps, chatProvider, hold, requestId, floorMillis,
                    chargedOrgId, projectId, callerId, jobCt, jobs.Options.TimeoutSeconds, deferClose: true);
                return (new EditProposalOutcome(result.StatusCode ?? StatusCodes.Status200OK, result.Value!), close);
            });
            string location = Url?.Action(nameof(GetProposalJob), new { projectId, id, jobId }) ?? $"proposals/jobs/{jobId}";
            Response.Headers.Location = location;
            return StatusCode(StatusCodes.Status202Accepted, new { jobId, state = "running" });
        }

        return (await RunProposalAsync(
            _charges, null, logger, model, agent, document, view, request, fps, chatProvider, hold, requestId, floorMillis,
            chargedOrgId, projectId, callerId, ct, jobs?.Options.SyncTimeoutSeconds ?? Math.Min(timeoutSeconds, new EditProposalOptions().SyncTimeoutSeconds), deferClose: false)).Result;
    }

    /// <summary>
    /// GET: the state of a background proposal job. <c>404</c> for an unknown, expired or someone else's job (one answer);
    /// <c>202</c> while it runs; once finished, exactly the status and body the synchronous route gives for that outcome.
    /// </summary>
    [HttpGet("{id:guid}/proposals/jobs/{jobId:guid}")]
    public async Task<IActionResult> GetProposalJob(
        Guid projectId, Guid id, Guid jobId, [FromServices] EditProposalJobStore? jobs, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        if (jobs is null) return NotFound();

        // Charge on delivery: PollAsync settles the pending hold (once) before the final answer is returned.
        EditProposalPoll poll = await jobs.PollAsync(jobId, _currentUser.UserId, projectId, id);
        switch (poll.State)
        {
            case EditProposalPollState.Running:
                return StatusCode(StatusCodes.Status202Accepted, new { jobId, state = "running" });
            case EditProposalPollState.Expired:
                return StatusCode(StatusCodes.Status410Gone, new
                {
                    code = "PROPOSAL_EXPIRED",
                    message = "This suggestion expired before it was collected. You were not charged; please ask again."
                });
            case EditProposalPollState.Ready:
                EditProposalOutcome outcome = poll.Outcome!;
                return outcome.Status == StatusCodes.Status200OK ? Ok(outcome.Body) : StatusCode(outcome.Status, outcome.Body);
            default:
                return NotFound();
        }
    }

    /// <summary>
    /// The model call, settle and compile shared by the synchronous route and the background job. Touches no HttpContext:
    /// <paramref name="charges"/> is whatever the caller can still use. A timeout (<paramref name="timeoutSeconds"/>) releases
    /// the hold and answers 424 PROPOSAL_TIMEOUT; cancelling <paramref name="outer"/> (client gone, abandonment, shutdown)
    /// releases it too and rethrows. A proposal the user never receives is never charged.
    /// </summary>
    private async Task<(ObjectResult Result, Func<bool, Task>? Close)> RunProposalAsync(
        INonWorkflowChargeService? charges, IServiceScopeFactory? scopes, ILogger? logger, IEditProposalModel model, string agent, EditTimeline document,
        EditProposalView view, EditProposalRequest request, double fps, ResolvedInferenceProvider? chatProvider, ChargeHold? hold,
        Guid requestId, long floorMillis, Guid? org, Guid projectId, Guid userId, CancellationToken outer, double timeoutSeconds, bool deferClose)
    {
        using CancellationTokenSource callCts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        callCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.001, timeoutSeconds)));
        CancellationToken ct = callCts.Token;

        // Metering scope: the proposal's model call belongs to the project's organization, the project and the
        // caller. Opened here, not in the model, which is a singleton with no request to read the caller from.
        EditProposalOutput? output = null;
        bool timedOut = false;
        long? meteredMillis = null;
        long? unheldMillis = null;
        bool deferred = false;

        // Settles (deliver) or releases the hold. With a scope factory (background) it resolves its own charge service, since
        // the request's is gone by the time the answer is read.
        async Task Close(bool deliver)
        {
            using IServiceScope? scope = scopes?.CreateScope();
            INonWorkflowChargeService? c = scope is null ? charges : scope.ServiceProvider.GetService<INonWorkflowChargeService>();
            await NonWorkflowChargeResults.CloseHoldAsync(c, hold, deliver ? meteredMillis : null, logger);
            if (deliver && c is not null && org is { } unheldOrg && unheldMillis is { } unheld)
            {
                await c.ChargeUnheldAsync(
                    unheldOrg, UsageSource.TimelineAssist, unheld, requestId, new CreditLedgerContext(projectId, Note: agent), CancellationToken.None);
            }
        }

        try
        {
            using (UsageScope.Begin(org, projectId, null, null, userId, UsageSource.TimelineAssist))
            {
                try
                {
                    output = await model.ProposeAsync(agent, view.Text, request.Brief, chatProvider, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested && !outer.IsCancellationRequested)
                {
                    timedOut = true;
                }
                catch (ModelUnpricedException unpriced)
                {
                    // The agent's model is an unpriced Platform model: no call was made, so the hold is released.
                    return (ModelUnpricedResults.Conflict(unpriced), null);
                }

                // No chat provider means no call was made: the hold is released, not settled. A call that
                // metered platform usage no price row prices settles at least at its hold, never at the understated sum.
                if (output is not null && !timedOut)
                {
                    bool unpricedUsage = UsageScope.Current?.SawUnpricedPlatformUsage == true;
                    meteredMillis = unpricedUsage
                        ? Math.Max(UsageScope.RunningCreditsMillis, Math.Max(hold?.RequiredMillis ?? 0, floorMillis))
                        : UsageScope.RunningCreditsMillis;

                    // R11.1: a failed answer is charged for what it consumed (an unparseable answer used tokens); only a call
                    // that metered nothing and saw no unpriced usage (a provider error before any tokens) is released (refunded).
                    if (output.Failed && meteredMillis <= 0 && !unpricedUsage) meteredMillis = null;

                    // T-026 (finding 5): no hold was taken (a BYO estimate) yet the call metered Platform usage: it is
                    // charged after the fact under this request's id, never left free. T-040: so is a hold that reserved
                    // nothing (NO_PLATFORM_COST: no floor and no provider resolved), which would otherwise settle at 0;
                    // only billing off is exempt.
                    bool reservedNothing = hold is null
                                           || hold.ReservationId is null && hold.Code != NonWorkflowChargeCodes.BillingDisabled;
                    if (reservedNothing && (meteredMillis > 0 || unpricedUsage)) unheldMillis = meteredMillis;
                    deferred = deferClose;
                }
            }
        }
        finally
        {
            // Background: a call that produced an answer keeps its hold open until the answer is read (charge on delivery);
            // everything else (timeout, no provider, unpriced, cancel, throw) closes now, released or settled as before.
            if (!deferred) await Close(true);
        }

        if (timedOut)
        {
            return (StatusCode(StatusCodes.Status424FailedDependency, new
            {
                code = "PROPOSAL_TIMEOUT",
                message = "The AI took too long to answer. You were not charged; please try again."
            }), null);
        }

        if (output is null)
        {
            return (StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "no_chat_provider",
                message = $"No chat provider is configured for the {agent} agent. Add one under Admin → Inference providers."
            }), null);
        }

        if (output.Failed)
        {
            bool charged = meteredMillis is > 0;
            return (StatusCode(StatusCodes.Status424FailedDependency, new
            {
                code = "PROPOSAL_FAILED",
                creditsUsed = charged ? meteredMillis!.Value / 1000.0 : 0,
                message = charged
                    ? $"The AI did not give a usable answer this time. About {meteredMillis!.Value / 1000.0:0.##} credits were used; please try again."
                    : "The AI did not answer this time. You were not charged; please try again."
            }), deferred ? Close : null);
        }

        EditProposal proposal;
        bool isRecut = agent == EditProposalAgents.Recut;
        (RecutOpClass? askedOp, RecutTarget? askedTarget) = isRecut ? RecutRequestIntent.Classify(request.Brief) : (null, null);
        try
        {
            proposal = EditProposalCompiler.Compile(document, agent, output, view, fps, Guid.NewGuid, isRecut ? (askedOp, askedTarget) : null, RecutRequestIntent.IsUnscoped(request.Brief, askedTarget), request.Brief);
        }
        catch
        {
            // Nothing reaches the user: the deferred hold is released, never settled.
            if (deferred) await Close(false);
            throw;
        }

        return (Ok(new EditProposalResponse(
            proposal.Id,
            proposal.Agent,
            proposal.Summary,
            proposal.Groups.Select(g => new EditProposalGroupResponse(
                g.Label,
                JsonSerializer.SerializeToElement(g.Ops, DocumentJsonOptions),
                g.TouchedItemIds,
                g.TouchesUserOwned,
                g.Reason,
                g.Cuts is { Count: > 0 } ? g.Cuts.Select(c => new EditProposalCutResponse(c.ItemId, c.StartSec, c.EndSec)).ToList() : null)).ToList(),
            proposal.Rejected,
            ProposalModelLabel(chatProvider),
            DescribeProposalContext(view, request.Brief, fps),
            ToGroundingResponse(view.Grounding),
            view.Grounding is { } gp && gp.ClipsWithFacts < gp.ClipsTotal,
            proposal.Did is { } did ? new EditProposalDidResponse(did.Cut, did.CutSeconds, did.Remove, did.Trim, did.Mute, did.Other, (did.Cuts ?? []).Select(c => new EditProposalDidCutResponse(c.Kind, c.Spans, c.Seconds)).ToList()) : null,
            new EditProposalAskedResponse(askedOp is { } o ? RecutRequestIntent.Word(o) : null, askedTarget is { } t ? RecutRequestIntent.Word(t) : null),
            proposal.SwapsRefused,
            RecutGap(agent, view, request.Brief) is { MissingFileIds.Count: > 0 } partial ? ToNeedsAnalysis(partial) : null)), deferred ? Close : null);
    }

    private ObjectResult ProposalRateLimited(EditProposalRateState state)
    {
        Response.Headers.RetryAfter = state.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return StatusCode(StatusCodes.Status429TooManyRequests, new
        {
            code = "PROPOSAL_RATE_LIMITED",
            message = state.Message,
            used = state.Used,
            limit = state.Limit,
            windowMinutes = 60,
            retryAfter = state.RetryAfterSeconds,
            retryAfterSeconds = state.RetryAfterSeconds
        });
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // P9 — timecoded review comments
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// P9: the review comments on one timeline, oldest first, replies included (each names its root in
    /// <c>parentId</c>). View permission: a reviewer who can watch the cut can read and write comments.
    /// </summary>
    [HttpGet("{id:guid}/comments")]
    public async Task<ActionResult<IReadOnlyList<EditTimelineCommentResponse>>> ListComments(Guid projectId, Guid id, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        if (!await TimelineExistsAsync(projectId, id, ct)) return NotFound();

        List<EditTimelineComment> rows = await _db.EditTimelineComments
            .Where(c => c.TimelineId == id && c.ProjectId == projectId)
            .OrderBy(c => c.Frame).ThenBy(c => c.CreatedAt)
            .ToListAsync(ct);
        Dictionary<Guid, string> serviceAuthors = await LoadServiceAccountNamesAsync(rows.Select(r => r.AuthorUserId).Distinct().ToArray(), ct);
        return Ok(rows.Select(r => ToCommentResponse(r, serviceAuthors)).ToList());
    }

    /// <summary>
    /// P9: adds a comment at a timeline frame (optionally spanning <c>durationFrames</c>), or a reply to
    /// a root comment of the same timeline. <c>400</c> with every problem; <c>404</c> for a timeline (or
    /// parent) this project does not hold.
    /// </summary>
    [HttpPost("{id:guid}/comments")]
    public async Task<ActionResult<EditTimelineCommentResponse>> CreateComment(Guid projectId, Guid id, [FromBody] CreateEditTimelineCommentRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        if (!await TimelineExistsAsync(projectId, id, ct)) return NotFound();

        IReadOnlyList<string> errors = EditTimelineCommentRules.Validate(request.Body, request.Frame, request.DurationFrames);
        if (errors.Count > 0) return BadRequest(new { message = "The comment is not valid.", errors });

        long frame = request.Frame;
        if (request.ParentId is { } parentId)
        {
            EditTimelineComment? parent = await _db.EditTimelineComments.FirstOrDefaultAsync(c => c.Id == parentId && c.TimelineId == id && c.ProjectId == projectId, ct);
            if (parent is null) return NotFound(new { message = "The comment being replied to does not exist on this timeline." });
            if (parent.ParentId is not null) return BadRequest(new { message = "Replies are one level deep: reply to the thread's first comment." });
            frame = parent.Frame;
        }

        if (await _db.EditTimelineComments.CountAsync(c => c.TimelineId == id, ct) >= EditTimelineCommentRules.MaxCommentsPerTimeline)
            return BadRequest(new { message = $"A timeline may carry at most {EditTimelineCommentRules.MaxCommentsPerTimeline} comments." });

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        EditTimelineComment row = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            TimelineId = id,
            ParentId = request.ParentId,
            AuthorUserId = _currentUser.UserId,
            AuthorName = string.IsNullOrWhiteSpace(_currentUser.Email) ? "Reviewer" : _currentUser.Email.Length > 200 ? _currentUser.Email[..200] : _currentUser.Email,
            Frame = frame,
            DurationFrames = request.ParentId is null ? request.DurationFrames : null,
            Body = request.Body!.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.EditTimelineComments.Add(row);
        await _db.SaveChangesAsync(ct);
        return StatusCode(StatusCodes.Status201Created, ToCommentResponse(row, await LoadServiceAccountNamesAsync([row.AuthorUserId], ct)));
    }

    /// <summary>
    /// P9: edits a comment's body (author only) and/or resolves/reopens it (author, or anyone with Edit
    /// permission). Another reviewer's text is never rewritten: <c>403</c>.
    /// </summary>
    [HttpPatch("{id:guid}/comments/{commentId:guid}")]
    public async Task<ActionResult<EditTimelineCommentResponse>> UpdateComment(Guid projectId, Guid id, Guid commentId, [FromBody] UpdateEditTimelineCommentRequest request, CancellationToken ct)
    {
        // The comment is located first (scoped by project AND timeline, so a foreign id is simply
        // absent) because the permission the ONE access check asks for depends on who wrote it: the
        // author needs View, anyone else Edit. A caller with no access gets 404 either way, and so does
        // a comment whose timeline has been deleted: the parent is resolved through the same
        // soft-delete-filtered lookup as list and create.
        if (!await TimelineExistsAsync(projectId, id, ct)) return NotFound();
        EditTimelineComment? row = await _db.EditTimelineComments.FirstOrDefaultAsync(c => c.Id == commentId && c.TimelineId == id && c.ProjectId == projectId, ct);
        if (row is null) return NotFound();
        bool isAuthor = row.AuthorUserId == _currentUser.UserId;
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, isAuthor ? ProjectPermission.View : ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (request.Body is not null)
        {
            if (!isAuthor) return StatusCode(StatusCodes.Status403Forbidden, new { message = "Only the author can edit a comment." });
            IReadOnlyList<string> errors = EditTimelineCommentRules.Validate(request.Body, row.Frame, row.DurationFrames);
            if (errors.Count > 0) return BadRequest(new { message = "The comment is not valid.", errors });
            row.Body = request.Body.Trim();
        }

        if (request.Resolved is { } resolved)
        {
            // A non-author reached here through the Edit check above.
            row.Resolved = resolved;
        }

        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(ToCommentResponse(row, await LoadServiceAccountNamesAsync([row.AuthorUserId], ct)));
    }

    /// <summary>
    /// P9: removes a comment (soft delete) — the author, or anyone with Edit permission. Removing a
    /// thread's first comment promotes its oldest reply (W0.6, api D9).
    /// </summary>
    [HttpDelete("{id:guid}/comments/{commentId:guid}")]
    public async Task<IActionResult> DeleteComment(Guid projectId, Guid id, Guid commentId, CancellationToken ct)
    {
        // As in UpdateComment: the author needs View, anyone else Edit — one access check either way;
        // a deleted timeline's comments are 404.
        if (!await TimelineExistsAsync(projectId, id, ct)) return NotFound();
        EditTimelineComment? row = await _db.EditTimelineComments.FirstOrDefaultAsync(c => c.Id == commentId && c.TimelineId == id && c.ProjectId == projectId, ct);
        if (row is null) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, row.AuthorUserId == _currentUser.UserId ? ProjectPermission.View : ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        row.DeletedAt = now;
        if (row.ParentId is null)
        {
            // A thread survives its first comment (api D9): the oldest reply takes the root's place —
            // its frame, span and resolved state — and the other replies hang off it. Nobody else's
            // text is removed, and no reply is left pointing at a hidden root.
            List<EditTimelineComment> replies = await _db.EditTimelineComments
                .Where(c => c.ParentId == row.Id && c.TimelineId == id && c.ProjectId == projectId)
                .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
                .ToListAsync(ct);
            if (replies.Count > 0)
            {
                EditTimelineComment heir = replies[0];
                heir.ParentId = null;
                heir.Frame = row.Frame;
                heir.DurationFrames = row.DurationFrames;
                heir.Resolved = row.Resolved;
                heir.UpdatedAt = now;
                foreach (EditTimelineComment reply in replies.Skip(1))
                {
                    reply.ParentId = heir.Id;
                    reply.UpdatedAt = now;
                }
            }
        }
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Whether this project holds a live timeline <paramref name="id"/>: the soft-delete query filter
    /// applies, so every comment route answers 404 once its timeline is deleted.
    /// </summary>
    private Task<bool> TimelineExistsAsync(Guid projectId, Guid id, CancellationToken ct) =>
        _db.EditTimelines.AnyAsync(t => t.Id == id && t.ProjectId == projectId, ct);

    /// <summary>The display names (= API key names) of those of <paramref name="userIds"/> that are service accounts.</summary>
    private async Task<Dictionary<Guid, string>> LoadServiceAccountNamesAsync(Guid[] userIds, CancellationToken ct) =>
        userIds.Length == 0
            ? []
            : await _db.ApplicationUsers.AsNoTracking()
                .Where(u => userIds.Contains(u.Id) && u.IsServiceAccount)
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

    private static EditTimelineCommentResponse ToCommentResponse(EditTimelineComment c, IReadOnlyDictionary<Guid, string> serviceAuthors) =>
        serviceAuthors.TryGetValue(c.AuthorUserId, out string? keyName)
            ? new(c.Id, c.TimelineId, c.ParentId, c.AuthorUserId, keyName, c.Frame, c.DurationFrames, c.Body, c.Resolved, c.CreatedAt, c.UpdatedAt, c.GuestLinkId is not null, c.GuestName, "apiKey")
            : new(c.Id, c.TimelineId, c.ParentId, c.AuthorUserId, c.AuthorName, c.Frame, c.DurationFrames, c.Body, c.Resolved, c.CreatedAt, c.UpdatedAt, c.GuestLinkId is not null, c.GuestName);

    // ─────────────────────────────────────────────────────────────────────────────
    // P9 — project presets (transition / effect / mastering)
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>P9: the project's presets, optionally of one kind, by name.</summary>
    [HttpGet("presets")]
    public async Task<ActionResult<IReadOnlyList<EditorPresetResponse>>> ListPresets(Guid projectId, [FromQuery] string? kind, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        IQueryable<EditorPreset> query = _db.EditorPresets.Where(p => p.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(p => p.Kind == kind);
        List<EditorPreset> rows = await query.OrderBy(p => p.Kind).ThenBy(p => p.Name).ToListAsync(ct);
        return Ok(rows.Select(ToPresetResponse).ToList());
    }

    /// <summary>P9: saves a preset. The payload is checked against the timeline's own catalogues and ranges (<c>400</c> with every problem).</summary>
    [HttpPost("presets")]
    public async Task<ActionResult<EditorPresetResponse>> CreatePreset(Guid projectId, [FromBody] SaveEditorPresetRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        IReadOnlyList<string> errors = EditorPresetValidator.Validate(request.Kind, request.Name, request.Payload);
        if (errors.Count > 0) return BadRequest(new { message = "The preset is not valid.", errors });
        if (await _db.EditorPresets.CountAsync(p => p.ProjectId == projectId && p.Kind == request.Kind, ct) >= EditorPresetValidator.MaxPresetsPerKind)
            return BadRequest(new { message = $"A project may keep at most {EditorPresetValidator.MaxPresetsPerKind} presets of a kind." });
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        EditorPreset row = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Kind = request.Kind!,
            Name = request.Name!.Trim(),
            PayloadJson = request.Payload.GetRawText(),
            CreatedByUserId = _currentUser.UserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.EditorPresets.Add(row);
        await _db.SaveChangesAsync(ct);
        return StatusCode(StatusCodes.Status201Created, ToPresetResponse(row));
    }

    /// <summary>P9: renames or re-saves a preset; its kind cannot change.</summary>
    [HttpPut("presets/{presetId:guid}")]
    public async Task<ActionResult<EditorPresetResponse>> UpdatePreset(Guid projectId, Guid presetId, [FromBody] SaveEditorPresetRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        EditorPreset? row = await _db.EditorPresets.FirstOrDefaultAsync(p => p.Id == presetId && p.ProjectId == projectId, ct);
        if (row is null) return NotFound();
        if (request.Kind is not null && request.Kind != row.Kind) return BadRequest(new { message = "A preset's kind cannot change." });
        IReadOnlyList<string> errors = EditorPresetValidator.Validate(row.Kind, request.Name, request.Payload);
        if (errors.Count > 0) return BadRequest(new { message = "The preset is not valid.", errors });
        row.Name = request.Name!.Trim();
        row.PayloadJson = request.Payload.GetRawText();
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(ToPresetResponse(row));
    }

    /// <summary>P9: deletes a preset. Documents that used it keep their values — a preset is copied in, never referenced.</summary>
    [HttpDelete("presets/{presetId:guid}")]
    public async Task<IActionResult> DeletePreset(Guid projectId, Guid presetId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        EditorPreset? row = await _db.EditorPresets.FirstOrDefaultAsync(p => p.Id == presetId && p.ProjectId == projectId, ct);
        if (row is null) return NotFound();
        _db.EditorPresets.Remove(row);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static EditorPresetResponse ToPresetResponse(EditorPreset p) =>
        new(p.Id, p.Kind, p.Name, JsonDocument.Parse(p.PayloadJson).RootElement.Clone(), p.CreatedByUserId, p.CreatedAt, p.UpdatedAt);

    private static TimelineRenderStepConfig? ParseRenderConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<TimelineRenderStepConfig>(json, RenderConfigJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The pinned revision and the region picture out of a <c>TimelineRender</c> step's output JSON.
    /// Anything malformed degrades to nulls: the queue must list a run whose output we cannot read.
    /// </summary>
    /// <summary>The <c>loudness</c> block of a render's output JSON, or null when the render did not measure (tolerant of any shape).</summary>
    internal static EditTimelineRenderLoudnessResponse? ReadRenderLoudness(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(outputJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("loudness", out JsonElement l) || l.ValueKind != JsonValueKind.Object) return null;
            double Read(string name) => l.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double v) ? v : 0;
            bool normalised = l.TryGetProperty("normalised", out JsonElement n) && n.ValueKind == JsonValueKind.True;
            return new EditTimelineRenderLoudnessResponse(Read("integratedLufs"), Read("truePeakDbtp"), Read("loudnessRange"), normalised, ReadQualityChecks(l));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private const int MaxQualityChecks = 20;
    private const int MaxQualityDetailLength = 300;

    private static readonly HashSet<string> QualityCheckKinds = new(StringComparer.Ordinal)
    {
        "low_contrast", "frozen_frames", "loudness_off_target", "loudness_range_wide", "true_peak_high", "clipping", "long_silence", "no_sound"
    };

    /// <summary>The <c>checks</c> array of a loudness node: bounded count and length, closed kinds (mirrors the engine's QualityKinds), anything else dropped.</summary>
    internal static List<EditTimelineRenderQualityCheck> ReadQualityChecks(JsonElement loudness)
    {
        List<EditTimelineRenderQualityCheck> list = [];
        if (!loudness.TryGetProperty("checks", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (JsonElement c in arr.EnumerateArray())
        {
            if (list.Count >= MaxQualityChecks) break;
            if (c.ValueKind != JsonValueKind.Object) continue;
            string? kind = Str(c, "kind");
            string? severity = Str(c, "severity");
            if (kind is null || !QualityCheckKinds.Contains(kind)) continue;
            if (severity is not ("high" or "medium" or "low")) continue;
            string detail = Str(c, "detail") ?? string.Empty;
            if (detail.Length > MaxQualityDetailLength) detail = detail[..MaxQualityDetailLength];
            list.Add(new EditTimelineRenderQualityCheck(kind, severity, Num(c, "startSec"), Num(c, "endSec"), Num(c, "measured"), Num(c, "limit"), detail));
        }

        return list;

        static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        static double? Num(JsonElement o, string name) =>
            o.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double v) && double.IsFinite(v) ? v : null;
    }

    /// <summary>R06: the rendered length in seconds from a render's output JSON, or null when absent or unreadable.</summary>
    internal static double? ReadRenderDurationSec(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(outputJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("durationSec", out JsonElement d)
                && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out double v) && double.IsFinite(v) && v >= 0
                ? v
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The most skipped items, and the most warnings, one render lists.</summary>
    internal const int MaxRenderDiagnostics = 50;

    /// <summary>The longest skipped-item message or warning one render lists; longer text is cut.</summary>
    internal const int MaxRenderDiagnosticLength = 300;

    /// <summary>
    /// The <c>skippedItems</c> (item id, type, plain message — never the reason code) and plain
    /// <c>warnings</c> of a render's output JSON. Anything absent or malformed degrades to empty lists;
    /// an entry without readable text is dropped; both lists and every sentence are bounded.
    /// </summary>
    internal static (IReadOnlyList<EditTimelineRenderSkippedItemResponse> SkippedItems, IReadOnlyList<string> Warnings) ReadRenderDiagnostics(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson)) return ([], []);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(outputJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ([], []);

            static string? Bounded(JsonElement e, int max)
            {
                if (e.ValueKind != JsonValueKind.String) return null;
                string? value = e.GetString()?.Trim();
                if (string.IsNullOrEmpty(value)) return null;
                return value.Length <= max ? value : value[..max].TrimEnd();
            }

            static string? Text(JsonElement owner, string name, int max) =>
                owner.TryGetProperty(name, out JsonElement e) ? Bounded(e, max) : null;

            List<EditTimelineRenderSkippedItemResponse> skipped = [];
            if (root.TryGetProperty("skippedItems", out JsonElement skippedEl) && skippedEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in skippedEl.EnumerateArray())
                {
                    if (skipped.Count >= MaxRenderDiagnostics) break;
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string? message = Text(item, "message", MaxRenderDiagnosticLength);
                    if (message is null) continue;
                    Guid? itemId = Text(item, "itemId", 64) is { } raw && Guid.TryParse(raw, out Guid g) ? g : null;
                    skipped.Add(new EditTimelineRenderSkippedItemResponse(itemId, Text(item, "type", 32), message));
                }
            }

            List<string> warnings = [];
            if (root.TryGetProperty("warnings", out JsonElement warningsEl) && warningsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement warning in warningsEl.EnumerateArray())
                {
                    if (warnings.Count >= MaxRenderDiagnostics) break;
                    if (Bounded(warning, MaxRenderDiagnosticLength) is { } text) warnings.Add(text);
                }
            }

            return (skipped, warnings);
        }
        catch (JsonException)
        {
            return ([], []);
        }
    }

    internal static (int? Revision, EditTimelineRenderRegionsResponse? Regions) ReadRenderOutput(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson)) return (null, null);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(outputJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);

            int? revision = root.TryGetProperty("revision", out JsonElement rev) && rev.ValueKind == JsonValueKind.Number && rev.TryGetInt32(out int r) ? r : null;

            EditTimelineRenderRegionsResponse? regions = null;
            if (root.TryGetProperty("regions", out JsonElement regionsEl) && regionsEl.ValueKind == JsonValueKind.Object)
            {
                List<EditTimelineRenderRegionResponse> list = [];
                if (regionsEl.TryGetProperty("list", out JsonElement listEl) && listEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement region in listEl.EnumerateArray())
                    {
                        if (region.ValueKind != JsonValueKind.Object) continue;
                        if (!region.TryGetProperty("startFrame", out JsonElement s) || s.ValueKind != JsonValueKind.Number || !s.TryGetInt64(out long start)) continue;
                        if (!region.TryGetProperty("endFrame", out JsonElement en) || en.ValueKind != JsonValueKind.Number || !en.TryGetInt64(out long end)) continue;
                        bool cached = region.TryGetProperty("cached", out JsonElement c) && c.ValueKind == JsonValueKind.True;
                        list.Add(new EditTimelineRenderRegionResponse(start, end, cached));
                    }
                }

                int total = regionsEl.TryGetProperty("total", out JsonElement t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out int tv) ? tv : list.Count;
                int cachedCount = regionsEl.TryGetProperty("cached", out JsonElement cc) && cc.ValueKind == JsonValueKind.Number && cc.TryGetInt32(out int cv) ? cv : list.Count(l => l.Cached);
                regions = new EditTimelineRenderRegionsResponse(total, cachedCount, list);
            }

            return (revision, regions);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static readonly JsonSerializerOptions RenderConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// A safe default delivery name from the timeline's name, slugified to the step config's allowlist
    /// (ASCII letters, digits, '.', '_', '-' and spaces, starting with a letter or digit, no "..").
    /// Accents are folded ("Café" → "Cafe"), any dash becomes '-', everything else outside the
    /// allowlist is dropped; a name with nothing left (blank, or e.g. all CJK) falls back to
    /// "timeline-render". The server never answers 400 for a name the user did not type.
    /// </summary>
    internal static string DefaultRenderFileName(string timelineName)
    {
        StringBuilder sb = new();
        foreach (char c in timelineName.Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ' ') sb.Append(c);
            else if (c is '/' or '\\' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.DashPunctuation) sb.Append('-');
            else if (char.IsWhiteSpace(c)) sb.Append(' ');
        }

        string name = sb.ToString();
        while (name.Contains("..", StringComparison.Ordinal)) name = name.Replace("..", ".", StringComparison.Ordinal);
        while (name.Contains("  ", StringComparison.Ordinal)) name = name.Replace("  ", " ", StringComparison.Ordinal);
        name = name.TrimStart('.', '_', '-', ' ');
        if (name.Length > 100) name = name[..100];
        name = name.TrimEnd('.', ' '); // "cut." + ".mp4" would hold ".."
        if (name.Length == 0) name = "timeline-render";
        return name + ".mp4";
    }

    /// <summary>
    /// A stored <c>ops_json</c> as a <see cref="JsonElement"/>. A row holding something that is not
    /// JSON (impossible through the PATCH path, which only stores what it parsed) degrades to an
    /// empty array rather than failing the whole history page.
    /// </summary>
    private static JsonElement ParseOpsOrEmptyArray(string opsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(opsJson) ? "[]" : opsJson);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using JsonDocument empty = JsonDocument.Parse("[]");
            return empty.RootElement.Clone();
        }
    }

    /// <summary>
    /// Loads a timeline by id, scoped to the project and to non-deleted rows. Every <c>{id}</c>
    /// lookup that needs the DOCUMENT goes through here, so "another project", "soft-deleted" and
    /// "nonexistent" cannot drift apart into three different answers. <see cref="GetHistory"/> is
    /// the one deliberate exception: it projects the row instead, to avoid materializing
    /// <c>document_json</c> for a listing, and applies the identical predicate.
    /// </summary>
    private Task<EditTimelineEntity?> FindTimelineAsync(Guid projectId, Guid id, CancellationToken ct) =>
        _db.EditTimelines.FirstOrDefaultAsync(t => t.Id == id && t.ProjectId == projectId, ct);

    /// <summary>
    /// The revision a racing writer left behind, for the <c>409</c> body of the
    /// <see cref="DbUpdateConcurrencyException"/> path. Read with <c>AsNoTracking</c> precisely
    /// because the tracked entity still carries this request's rejected edit.
    /// </summary>
    private async Task<int?> ReadCurrentRevisionAsync(Guid projectId, Guid id, CancellationToken ct)
    {
        int? revision = await _db.EditTimelines
            .AsNoTracking()
            .Where(t => t.Id == id && t.ProjectId == projectId)
            .Select(t => (int?)t.Revision)
            .FirstOrDefaultAsync(ct);

        return revision;
    }

    /// <summary>R01.3: a plain reason the user-chosen blank-timeline settings cannot be used, or null when they can.</summary>
    private static string? ChosenSettingsProblem(EditTimelineSettings s)
    {
        if (s.Width is < 16 or > 8192 || s.Height is < 16 or > 8192) return "the picture size must be between 16 and 8192 pixels.";
        if (s.Rate is null || s.Rate.Num <= 0 || s.Rate.Den <= 0 || (double)s.Rate.Num / s.Rate.Den > 120) return "the frame rate must be above 0 and at most 120.";
        if (s.PixelAspect is null || s.PixelAspect.Num <= 0 || s.PixelAspect.Den <= 0) return "the pixel shape must be positive.";
        if (s.StartTimecodeFrames < 0) return "the start timecode cannot be negative.";
        return null;
    }

    /// <summary>
    /// Builds the blank document the plan names as this phase's placeholder: 1920×1080 at 30 fps,
    /// 48 kHz, square pixels, <see cref="AspectPolicy.Fit"/>, timecode starting at frame 0, one
    /// empty <c>Video</c> track and one empty <c>Audio</c> track.
    /// </summary>
    /// <remarks>
    /// Track ids come from <see cref="EditTimelineSeeder.TrackId"/>, the seeder's own deterministic
    /// label → id function, so a blank timeline and a seeded one address the video track by the same
    /// id and a client can hold onto it across both.
    /// </remarks>
    private static EditTimeline CreateBlankDocument(Guid projectId, Guid timelineId, string name, EditTimelineSettings? chosen = null)
    {
        EditTimelineSettings settings = chosen ?? new(
            Width: 1920,
            Height: 1080,
            Rate: new Rational(30, 1),
            SampleRate: BlankSampleRate,
            PixelAspect: new Rational(1, 1),
            AspectPolicy: AspectPolicy.Fit,
            StartTimecodeFrames: 0);

        Track video = new(
            Id: EditTimelineSeeder.TrackId(BlankVideoTrackLabel),
            Kind: TrackKind.Video,
            Name: "Video",
            Color: null,
            Height: 100,
            Locked: false,
            Enabled: true,
            Mute: false,
            Solo: false,
            BusId: null,
            Items: Array.Empty<Item>());

        Track audio = new(
            Id: EditTimelineSeeder.TrackId(BlankAudioTrackLabel),
            Kind: TrackKind.Audio,
            Name: "Audio",
            Color: null,
            Height: 100,
            Locked: false,
            Enabled: true,
            Mute: false,
            Solo: false,
            BusId: null,
            Items: Array.Empty<Item>());

        return new EditTimeline(
            id: timelineId,
            projectId: projectId,
            name: name,
            settings: settings,
            tracks: [video, audio],
            markers: Array.Empty<Marker>());
    }

    /// <summary>
    /// Builds the caller-supplied lookups <see cref="EditTimelineValidator"/> needs from the
    /// database: the two ownership maps, and the deliberately empty duration map.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both ownership queries run WITHOUT a project filter, and that is the whole point.</b> The
    /// question being answered is "who really owns this id", not "does this project own it": an id
    /// belonging to another project must resolve to that OTHER project so the validator can reject
    /// it. Adding <c>Where(f =&gt; f.ProjectId == projectId)</c> here would silently turn every
    /// foreign reference into "unknown" — still rejected, but for the wrong reason, and the maps
    /// would then be blind to the very leak they exist to catch. An id that resolves to no row is
    /// simply absent from the map, which the validator treats as a violation too.
    /// </para>
    /// <para>
    /// <b><see cref="EditTimelineValidationContext.AssetDurationFrames"/> (R01.6)</b> is filled from
    /// the ingest probe (<c>media_derivatives.source_probe_json</c>) or the VideoAnalyze artifact by
    /// <see cref="SourceDurationResolver"/>. A source with no known duration is left out, and the
    /// validator reads a missing entry as "not checked" (fail open), never as a violation.
    /// </para>
    /// </remarks>
    private Task<EditTimelineValidationContext> BuildValidationContextAsync(
        Guid projectId,
        EditTimeline document,
        CancellationToken ct) =>
        (_contextBuilder ??= new EditTimelineValidationContextBuilder(_db, _storage, _logger)).BuildAsync(projectId, document, ct);

    private EditTimelineValidationContextBuilder? _contextBuilder;

    /// <summary>
    /// The request's <c>ops</c>, serialized verbatim for <c>edit_timeline_ops.ops_json</c>. The
    /// server never interprets them; an absent or <c>undefined</c> value stores an empty array.
    /// </summary>
    /// <remarks>
    /// <c>PatchEditTimeline</c> calls this ONCE and keeps the string: it measures the returned bytes
    /// against <see cref="MaxOpsBytes"/> and then persists that same instance, so the guard and the
    /// write cannot disagree about what was sent.
    /// </remarks>
    private static string SerializeOps(JsonElement? ops) =>
        ops is { } element && element.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null
            ? element.GetRawText()
            : "[]";

    private static string SerializeDocument(EditTimeline document) =>
        JsonSerializer.Serialize(document, DocumentJsonOptions);

    /// <summary>
    /// Reads a stored document, reporting a corrupt one as a failed parse rather than throwing: an
    /// unparseable <c>document_json</c> is data corruption, and the caller's <c>500</c> has to name
    /// the row so an operator can find it.
    /// </summary>
    private static bool TryDeserializeDocument(EditTimelineEntity timeline, out EditTimeline document)
    {
        document = null!;

        try
        {
            EditTimeline? parsed = JsonSerializer.Deserialize<EditTimeline>(timeline.DocumentJson, DocumentJsonOptions);
            if (parsed is null)
            {
                return false;
            }

            document = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static EditTimelineResponse ToResponse(EditTimelineEntity timeline, EditTimeline document) =>
        new(
            timeline.Id,
            timeline.ProjectId,
            timeline.Name,
            timeline.Revision,
            document,
            timeline.SeedStepResultId,
            timeline.CreatedAt,
            timeline.UpdatedAt);
}

/// <summary>
/// Body of <c>PUT /api/v1/projects/{projectId}/edit-timelines/{id}/name</c>: the new name, trimmed
/// server-side and then 1..200 characters.
/// </summary>
public sealed record RenameEditTimelineRequest(string? Name);
