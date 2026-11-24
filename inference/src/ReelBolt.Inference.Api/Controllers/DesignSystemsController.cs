using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Archives;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using ReelBolt.Shared.DesignSystems.Merge;
using ReelBolt.Shared.DesignSystems.Views;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The Phase-2 route subset of SPEC 9 for one project's design systems
/// (<c>/api/v1/projects/{projectId}/design-system</c>): summaries, version lists and reads, manual
/// Draft creation, the approve/reject/restore state machine and the current version's sources.
///
/// <para>
/// Every action resolves the project through <see cref="IProjectAccessService"/> first — View for
/// reads, Edit for writes — so a missing or foreign project is a 404 and a same-organization low
/// role is a 403. The service then answers 404 for a version id that belongs to a different project,
/// because its queries always join through <c>design_systems.project_id</c>.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/design-system")]
[Authorize]
public class DesignSystemsController : ControllerBase
{
    /// <summary>SPEC 16.4 figma-variables: the only file keys the one-shot import accepts.</summary>
    private const int MaxFigmaAccessTokenLength = 512;

    private static readonly Regex FigmaFileKeyPattern = new(@"^[A-Za-z0-9]{10,64}\z", RegexOptions.Compiled);

    /// <summary>
    /// The production Figma client, created on first use. The host always registers no per-request
    /// client, so a one-shot import owns one long-lived <see cref="HttpClient"/> rather than one per
    /// request; the tests inject their own over a fake handler.
    /// </summary>
    private static readonly Lazy<FigmaVariablesClient> DefaultFigmaClient = new(static () => new FigmaVariablesClient());

    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IDesignSystemService _designSystems;
    private readonly IDesignSystemIngestService _ingestService;
    private readonly ILogger<DesignSystemsController> _logger;
    private readonly IConfiguration? _configuration;
    private readonly WorkflowTemplateProvisioningService? _templates;
    private readonly IWorkflowExecutionService? _executions;
    private readonly ICreditAccountProvisioner? _trialCredits;
    private readonly IWorkflowCostEstimator? _costEstimator;
    private readonly IDesignBuildEstimateGate _designBuildGate;
    private readonly DesignDriftQueue? _driftQueue;
    private readonly FigmaVariablesClient? _figmaClient;
    private readonly IPlanGateService? _planGate;
    private readonly ImportWorkerClient? _importWorker;
    private readonly IFileStorageService? _fileStorage;
    private readonly IProjectFileAddedEffects? _fileAddedEffects;

    /// <summary>SPEC 16.4 brand-pdf: the multipart body cap, in bytes (20 MiB).</summary>
    private const long PdfMaxBytes = 20L * 1024 * 1024;

    /// <summary>SPEC 16.5 extract-zip: the multipart body cap, in bytes (32 MiB).</summary>
    private const long ZipMaxBytes = 32L * 1024 * 1024;

    /// <summary>
    /// The reader refusal reasons that refuse the whole archive. The benign classifier reasons
    /// (<c>DROPPED_HTML</c>/<c>DROPPED_SCRIPT</c>/<c>DROPPED_BINARY</c>) and the
    /// per-entry shapes that are simply ignored (a duplicate path, a special file) never do.
    /// </summary>
    private static readonly HashSet<string> RejectingZipRefusals = new(StringComparer.Ordinal)
    {
        "ARCHIVE_TOO_LARGE",
        "ARCHIVE_INVALID",
        "TOO_MANY_ENTRIES",
        "TOTAL_TOO_LARGE",
        "BOMB_SUSPECTED",
        "PATH_TRAVERSAL",
        "PATH_INVALID",
        "PATH_TOO_DEEP",
        "NESTED_ARCHIVE",
        "RATIO_EXCEEDED",
        "ENTRY_TOO_LARGE",
    };

    public DesignSystemsController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IProjectAccessService projectAccess,
        IDesignSystemService designSystems,
        IDesignSystemIngestService ingestService,
        ILogger<DesignSystemsController> logger,
        IConfiguration? configuration = null,
        // Optional so every existing construction site (the tenancy matrix, the controller tests)
        // keeps compiling; the app registers all four. Null means "nothing to run and nothing to
        // charge", which the builds route answers as a 500 rather than silently doing nothing.
        WorkflowTemplateProvisioningService? templates = null,
        IWorkflowExecutionService? executions = null,
        ICreditAccountProvisioner? trialCredits = null,
        IWorkflowCostEstimator? costEstimator = null,
        DesignDriftQueue? driftQueue = null,
        FigmaVariablesClient? figmaClient = null,
        IPlanGateService? planGate = null,
        ImportWorkerClient? importWorker = null,
        IDesignBuildEstimateGate? designBuildGate = null,
        IFileStorageService? fileStorage = null,
        IProjectFileAddedEffects? fileAddedEffects = null)
    {
        _designBuildGate = designBuildGate ?? new DesignBuildEstimateGate(db, costEstimator, configuration);
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _designSystems = designSystems;
        _ingestService = ingestService;
        _logger = logger;
        _configuration = configuration;
        _templates = templates;
        _executions = executions;
        _trialCredits = trialCredits;
        _costEstimator = costEstimator;
        _driftQueue = driftQueue;
        _figmaClient = figmaClient;
        _planGate = planGate;
        _importWorker = importWorker;
        _fileStorage = fileStorage;
        _fileAddedEffects = fileAddedEffects;
    }

    [HttpGet]
    public async Task<ActionResult<DesignSystemsSummaryResponse>> GetSummary(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        return Ok(await _designSystems.GetSummaryAsync(projectId, ct));
    }

    [HttpGet("versions")]
    public async Task<ActionResult<IReadOnlyList<DesignVersionSummaryResponse>>> ListVersions(
        Guid projectId, [FromQuery] string? kind, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        string resolved = ResolveKind(kind);
        if (!DesignSystemKinds.IsValid(resolved)) return InvalidKind();

        return Ok(await _designSystems.ListVersionsAsync(projectId, resolved, ct));
    }

    [HttpGet("versions/{versionId:guid}")]
    public async Task<ActionResult<DesignVersionResponse>> GetVersion(Guid projectId, Guid versionId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        DesignVersionResponse? version = await _designSystems.GetVersionAsync(projectId, versionId, ct);
        return version is null ? NotFound() : Ok(version);
    }

    [HttpPost("versions")]
    public async Task<ActionResult<DesignVersionResponse>> CreateVersion(
        Guid projectId, CreateDesignVersionRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (!DesignSystemKinds.IsValid(request.Kind)) return InvalidKind();

        DesignSystemWriteResult result = await _designSystems.CreateDraftAsync(
            projectId,
            _currentUser.UserId,
            request.Kind,
            request.Document ?? new JsonObject(),
            DesignVersionOrigin.Manual,
            workflowExecutionId: null,
            coverageJson: null,
            sources: [],
            baseVersionId: null,
            ct);

        if (result.Status == DesignSystemWriteStatus.Ok && result.Version is { } created)
        {
            _logger.LogInformation(
                "design_system.create {DesignSystemId} {VersionId} {UserId}",
                created.DesignSystemId, created.Id, _currentUser.UserId);
            return StatusCode(StatusCodes.Status201Created, created);
        }

        return MapWriteResult(result);
    }

    [HttpPost("versions/{versionId:guid}/approve")]
    public async Task<ActionResult<DesignVersionResponse>> Approve(Guid projectId, Guid versionId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        DesignSystemWriteResult result = await _designSystems.ApproveAsync(projectId, versionId, _currentUser.UserId, ct);
        if (result.Status == DesignSystemWriteStatus.Ok && result.Version is { } approved)
        {
            _logger.LogInformation(
                "design_system.approve {DesignSystemId} {VersionId} {UserId}",
                approved.DesignSystemId, approved.Id, _currentUser.UserId);
        }

        return MapWriteResult(result);
    }

    [HttpPost("versions/{versionId:guid}/reject")]
    public async Task<ActionResult<DesignVersionResponse>> Reject(
        Guid projectId, Guid versionId, ApproveRejectRequest? request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        DesignSystemWriteResult result = await _designSystems.RejectAsync(
            projectId, versionId, _currentUser.UserId, request?.Note, ct);
        if (result.Status == DesignSystemWriteStatus.Ok && result.Version is { } rejected)
        {
            _logger.LogInformation(
                "design_system.reject {DesignSystemId} {VersionId} {UserId}",
                rejected.DesignSystemId, rejected.Id, _currentUser.UserId);
        }

        return MapWriteResult(result);
    }

    [HttpPost("versions/{versionId:guid}/restore")]
    public async Task<ActionResult<DesignVersionResponse>> Restore(Guid projectId, Guid versionId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        DesignSystemWriteResult result = await _designSystems.RestoreAsync(projectId, versionId, _currentUser.UserId, ct);
        if (result.Status == DesignSystemWriteStatus.Ok && result.Version is { } restored)
        {
            _logger.LogInformation(
                "design_system.restore {DesignSystemId} {VersionId} {UserId}",
                restored.DesignSystemId, restored.Id, _currentUser.UserId);
            return StatusCode(StatusCodes.Status201Created, restored);
        }

        return MapWriteResult(result);
    }

    /// <summary>
    /// SPEC 9 <c>GET /proposals</c>: the current system's change proposals, newest first, optionally
    /// filtered by <c>status</c>. <c>kind</c> defaults to <c>design</c>, like the other routes.
    /// </summary>
    [HttpGet("proposals")]
    public async Task<ActionResult<IReadOnlyList<ProposalResponse>>> ListProposals(
        Guid projectId, [FromQuery] string? status, [FromQuery] string? kind, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        string resolved = ResolveKind(kind);
        if (!DesignSystemKinds.IsValid(resolved)) return InvalidKind();

        DesignProposalStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse(status, ignoreCase: true, out DesignProposalStatus candidate)
                || !Enum.IsDefined(candidate))
            {
                return BadRequest(new { code = "INVALID_STATUS", message = "invalid proposal status" });
            }

            parsedStatus = candidate;
        }

        return Ok(await _designSystems.ListProposalsAsync(projectId, resolved, parsedStatus, ct));
    }

    /// <summary>
    /// SPEC 9/13.4 <c>POST /proposals/{id}/approve</c>: approves the proposed Draft through the same
    /// state machine as <c>/versions/{id}/approve</c> and marks the proposal Approved in the same save.
    /// A proposal that is no longer Pending is a 409, as is a Draft whose base moved on.
    /// </summary>
    [HttpPost("proposals/{proposalId:guid}/approve")]
    public async Task<ActionResult<ProposalResponse>> ApproveProposal(
        Guid projectId, Guid proposalId, ApproveRejectRequest? request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        DesignSystemProposalResult result = await _designSystems.ApproveProposalAsync(
            projectId, proposalId, _currentUser.UserId, request?.Note, ct);

        if (result.Status == DesignSystemProposalDecisionStatus.Ok && result.Proposal is { } proposal)
        {
            _logger.LogInformation(
                "design_system.proposal.approve {DesignSystemId} {ProposalId} {VersionId} {UserId}",
                proposal.DesignSystemId, proposal.Id, proposal.ProposedVersionId, _currentUser.UserId);
        }

        return MapProposalResult(result);
    }

    /// <summary>
    /// SPEC 9/13.4 <c>POST /proposals/{id}/reject</c>: marks the proposal Rejected and its still-Draft
    /// proposed version Rejected in the same save.
    /// </summary>
    [HttpPost("proposals/{proposalId:guid}/reject")]
    public async Task<ActionResult<ProposalResponse>> RejectProposal(
        Guid projectId, Guid proposalId, ApproveRejectRequest? request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        DesignSystemProposalResult result = await _designSystems.RejectProposalAsync(
            projectId, proposalId, _currentUser.UserId, request?.Note, ct);

        if (result.Status == DesignSystemProposalDecisionStatus.Ok && result.Proposal is { } proposal)
        {
            _logger.LogInformation(
                "design_system.proposal.reject {DesignSystemId} {ProposalId} {UserId}",
                proposal.DesignSystemId, proposal.Id, _currentUser.UserId);
        }

        return MapProposalResult(result);
    }

    /// <summary>
    /// SPEC 9 <c>PATCH /versions/{id}/locks</c>: locks/unlocks token paths on a Draft and re-digests
    /// it. This is the ONLY route that may mutate a Draft's document.
    /// </summary>
    [HttpPatch("versions/{versionId:guid}/locks")]
    public async Task<ActionResult<DesignVersionResponse>> UpdateLocks(
        Guid projectId, Guid versionId, LockRequest? request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        DesignSystemLockResult result = await _designSystems.ApplyLocksAsync(
            projectId, versionId, request?.Lock, request?.Unlock, _currentUser.UserId, ct);

        if (result.Status == DesignSystemLockStatus.Ok && result.Version is { } version)
        {
            _logger.LogInformation(
                "design_system.locks {DesignSystemId} {VersionId} {UserId}",
                version.DesignSystemId, version.Id, _currentUser.UserId);
            return Ok(version);
        }

        return result.Status switch
        {
            DesignSystemLockStatus.NotFound => NotFound(),
            DesignSystemLockStatus.NotDraft =>
                Conflict(new { code = "NOT_DRAFT", message = "the version is not a Draft" }),
            DesignSystemLockStatus.TooManyPaths => BadRequest(new
            {
                code = "TOO_MANY_PATHS",
                message = "at most 200 lock/unlock paths may be sent per call",
            }),
            DesignSystemLockStatus.UnknownPath => BadRequest(new
            {
                code = "UNKNOWN_PATH",
                message = "every listed path must name an existing token",
                path = result.Path,
            }),
            DesignSystemLockStatus.InvalidDocument => BadRequest(new
            {
                code = "INVALID_DOCUMENT",
                message = "the stored document is not a JSON object",
            }),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    /// <summary>
    /// SPEC 16.1 <c>PUT /library-link</c>: sets (or clears, with a null/absent <c>libraryId</c>) the
    /// organization library the project's design system descends from. Edit on the project is the
    /// first gate — a missing or foreign project is a 404 and a same-org low role a 403 — and the
    /// caller must additionally be able to read libraries, so Billing is refused. The library is
    /// resolved against the project's own organization, so a library of another org is a 404, never a
    /// 403. Setting a link on a project with no design system yet creates the empty project container
    /// so the link has a home; clearing one that does not exist is a no-op.
    /// </summary>
    [HttpPut("library-link")]
    public async Task<IActionResult> SetLibraryLink(
        Guid projectId, [FromBody] LibraryLinkRequest? request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        // "Caller must be able to read libraries (not Billing)": no active organization is a 404 and
        // Billing a 403, before any read or write. The project check above already proved same-org.
        if (OrgLibraryGate.Read(this, _currentUser, out Guid orgId) is { } gateDenied) return gateDenied;

        Project project = access.Project!;
        if (project.OrganizationId != orgId) return NotFound();

        DesignSystem? system = await _db.DesignSystems
            .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Kind == DesignSystemKinds.Design, ct);

        if (request?.LibraryId is not { } libraryId)
        {
            if (system is not null)
            {
                system.ParentLibraryId = null;
                system.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "design_system.library_link.clear {ProjectId} {UserId}", projectId, _currentUser.UserId);
            }

            return NoContent();
        }

        DesignSystemLibrary? library = await _db.DesignSystemLibraries
            .FirstOrDefaultAsync(l => l.Id == libraryId && l.OrganizationId == orgId, ct);
        if (library is null)
        {
            return NotFound(new
            {
                code = OrgDesignLibrariesController.LibraryNotFoundCode,
                message = "design-system library not found",
            });
        }

        if (system is null)
        {
            DateTime now = DateTime.UtcNow;
            system = new DesignSystem
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Kind = DesignSystemKinds.Design,
                Name = project.Name,
                CreatedByUserId = _currentUser.UserId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.DesignSystems.Add(system);
        }

        system.ParentLibraryId = libraryId;
        system.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "design_system.library_link.set {ProjectId} {LibraryId} {UserId}",
            projectId, libraryId, _currentUser.UserId);

        return NoContent();
    }

    /// <summary>
    /// SPEC 9 <c>POST /check</c>: enqueues a manual drift check now and answers 202 with what the
    /// queue did (queued/coalesced/debounced/dropped). The check itself never runs in the request.
    /// </summary>
    [HttpPost("check")]
    public async Task<IActionResult> Check(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (_driftQueue is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { code = "DRIFT_UNAVAILABLE" });

        // Manual bypasses the debounce window, not the organization cap or the queue's capacity.
        DesignDriftEnqueueResult enqueued = access.Project is { } owning
            ? _driftQueue.Enqueue(projectId, DesignDriftTrigger.Manual, changedPaths: null, owning.OrganizationId)
            : _driftQueue.Enqueue(projectId, DesignDriftTrigger.Manual, changedPaths: null);
        _logger.LogInformation(
            "design_system.drift.manual {ProjectId} {UserId} {Enqueued}",
            projectId, _currentUser.UserId, enqueued);

        return Accepted(new { status = enqueued.ToString().ToLowerInvariant() });
    }

    /// <summary>
    /// SPEC 9 <c>GET /versions/{id}/diff?against={otherId}</c>: the arbitrary two-version diff. The
    /// addressed version is the newer side, <c>against</c> the older. A missing/empty <c>against</c>
    /// is a 400, a foreign or missing id a 404, and two different systems a 400
    /// <c>DIFF_KIND_MISMATCH</c>. The body is the differ output without <c>driftScore</c>.
    /// </summary>
    [HttpGet("versions/{versionId:guid}/diff")]
    public async Task<IActionResult> GetVersionDiff(
        Guid projectId, Guid versionId, [FromQuery] Guid against, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        if (against == Guid.Empty)
            return BadRequest(new { message = "against is required" });

        DesignVersionDiffResult result = await _designSystems.GetDiffAsync(projectId, versionId, against, ct);
        return result.Status switch
        {
            DesignVersionDiffStatus.Ok => Ok(result.Diff),
            DesignVersionDiffStatus.KindMismatch => BadRequest(new
            {
                code = "DIFF_KIND_MISMATCH",
                message = "versions belong to different systems",
            }),
            _ => NotFound(),
        };
    }

    [HttpGet("sources")]
    public async Task<ActionResult<IReadOnlyList<DesignSourceResponse>>> ListSources(
        Guid projectId, [FromQuery] string? kind, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        string resolved = ResolveKind(kind);
        if (!DesignSystemKinds.IsValid(resolved)) return InvalidKind();

        return Ok(await _designSystems.ListSourcesAsync(projectId, resolved, ct));
    }

    /// <summary>
    /// SPEC 16.2 <c>GET export?kind=&amp;format=</c>: the project's effective current document as a
    /// download. <c>kind</c> defaults to <c>design</c>; the design member is the linked library's
    /// approved document with the project's own layered on it — the same inheritance the pin freezes
    /// in <see cref="DesignSystemPinService"/> and the run loader resolves in
    /// <c>PinnedDocumentLoader</c> — while the product map is its own approved document.
    ///
    /// <para>
    /// Design formats are <c>dtcg</c>, <c>css</c>, <c>css-tailwind</c>, <c>designmd</c> and
    /// <c>json</c> (empty = <c>json</c>); the product map exports as <c>json</c> only. Every answer
    /// is an attachment with <c>nosniff</c>. An unsupported format is a 400 before any read of the
    /// document, and a member with no approved document a 404.
    /// </para>
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(
        Guid projectId, [FromQuery] string? kind, [FromQuery] string? format, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        string resolvedKind = ResolveKind(kind);
        if (!DesignSystemKinds.IsValid(resolvedKind)) return InvalidKind();

        bool productMap = string.Equals(resolvedKind, DesignSystemKinds.ProductMap, StringComparison.Ordinal);
        string resolvedFormat = string.IsNullOrEmpty(format) ? "json" : format;
        if (!IsExportFormatSupported(resolvedFormat, productMap))
        {
            return BadRequest(new
            {
                code = "UNSUPPORTED_FORMAT",
                message = productMap
                    ? "the product map exports as format=json only"
                    : "format must be one of dtcg, css, css-tailwind, designmd or json",
            });
        }

        JsonObject? document = productMap
            ? await LoadProductMapDocumentAsync(projectId, ct)
            : await LoadEffectiveDesignDocumentAsync(projectId, ct);

        if (document is null)
        {
            return NotFound(new
            {
                code = "NO_APPROVED_VERSION",
                message = "there is no approved document of this kind to export",
            });
        }

        (string body, string contentType, string fileName) = RenderExport(document, resolvedFormat, productMap);

        SafeContentPolicy.ApplySecurityHeaders(Response);
        Response.Headers["Content-Disposition"] = SafeContentPolicy.BuildContentDisposition(false, fileName);
        return File(Encoding.UTF8.GetBytes(body), contentType, fileName);
    }

    /// <summary>True when <paramref name="format"/> names one of the exporters the route serves.</summary>
    private static bool IsExportFormatSupported(string format, bool productMap)
    {
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            return true;

        if (productMap)
            return false;

        return format.ToLowerInvariant() is "dtcg" or "css" or "css-tailwind" or "designmd";
    }

    /// <summary>Runs the pure exporter for one already-validated format.</summary>
    private static (string Body, string ContentType, string FileName) RenderExport(
        JsonObject document, string format, bool productMap)
    {
        switch (format.ToLowerInvariant())
        {
            case "dtcg":
                return (DesignExporter.ToDtcg(document).ToJsonString(), "application/json", "design-system.dtcg.json");

            case "css":
                return (DesignExporter.ToCss(document, tailwindV4: false), "text/css", "design-system.css");

            case "css-tailwind":
                return (DesignExporter.ToCss(document, tailwindV4: true), "text/css", "design-system.tailwind.css");

            case "designmd":
                return (DesignExporter.ToDesignMd(document), "text/markdown", "DESIGN.md");

            default:
                return (
                    DesignExporter.ToJson(document).ToJsonString(),
                    "application/json",
                    productMap ? "product-map.json" : "design-system.json");
        }
    }

    /// <summary>
    /// The design member's effective document: the project's own approved version, the linked
    /// library's approved version, or the library's document with the project's layered on it. Null
    /// when neither exists or the effective document cannot be parsed.
    /// </summary>
    private async Task<JsonObject?> LoadEffectiveDesignDocumentAsync(Guid projectId, CancellationToken ct)
    {
        DesignSystem? system = await _db.DesignSystems
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Kind == DesignSystemKinds.Design, ct);
        if (system is null)
            return null;

        DesignSystemVersion? own = await CurrentApprovedAsync(system, ct);
        LinkedLibraryDocument? library = await LoadLinkedLibraryAsync(system, ct);

        JsonObject? ownDocument = own is null ? null : ParseObject(own.DocumentJson);
        JsonObject? libraryDocument = library is null ? null : ParseObject(library.DocumentJson);

        if (libraryDocument is null)
            return ownDocument;

        return ownDocument is null ? libraryDocument : DesignDocumentMerger.Inherit(libraryDocument, ownDocument);
    }

    /// <summary>The product map's own current approved document, or null.</summary>
    private async Task<JsonObject?> LoadProductMapDocumentAsync(Guid projectId, CancellationToken ct)
    {
        DesignSystem? system = await _db.DesignSystems
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Kind == DesignSystemKinds.ProductMap, ct);
        if (system is null)
            return null;

        DesignSystemVersion? version = await CurrentApprovedAsync(system, ct);
        return version is null ? null : ParseObject(version.DocumentJson);
    }

    /// <summary>The system's current version only while it is Approved, else null.</summary>
    private async Task<DesignSystemVersion?> CurrentApprovedAsync(DesignSystem system, CancellationToken ct)
    {
        if (system.CurrentVersionId is not { } versionId)
            return null;

        return await _db.DesignSystemVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                v => v.Id == versionId && v.DesignSystemId == system.Id && v.State == DesignVersionState.Approved,
                ct);
    }

    /// <summary>
    /// The linked library's own approved version, resolved from the library row's document
    /// container. Null when the system names no library or the library has no approved version.
    /// </summary>
    private async Task<LinkedLibraryDocument?> LoadLinkedLibraryAsync(DesignSystem system, CancellationToken ct)
    {
        if (system.ParentLibraryId is not { } libraryId)
            return null;

        var row = await (
            from l in _db.DesignSystemLibraries
            join c in _db.DesignSystems on l.DesignSystemId equals c.Id
            join v in _db.DesignSystemVersions on c.CurrentVersionId equals (Guid?)v.Id
            where l.Id == libraryId && v.State == DesignVersionState.Approved
            select new { v.Id, v.DocumentJson })
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        return row is null ? null : new LinkedLibraryDocument(row.Id, row.DocumentJson);
    }

    /// <summary>One linked library's approved version, as the export needs it.</summary>
    private sealed record LinkedLibraryDocument(Guid VersionId, string DocumentJson);

    private static string ResolveKind(string? kind) =>
        string.IsNullOrEmpty(kind) ? DesignSystemKinds.Design : kind;

    /// <summary>
    /// SPEC 11.4 <c>GET preview</c>: the exact block an agent would be shown for the current
    /// approved version, or an empty block when there is nothing to show. <c>kind</c> defaults to
    /// <c>design</c>; <c>role</c> is an <see cref="AgentType"/> name, case-insensitive.
    /// </summary>
    [HttpGet("preview")]
    public async Task<ActionResult<PreviewResponse>> Preview(
        Guid projectId, [FromQuery] string? kind, [FromQuery] string? role, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        string resolvedKind = ResolveKind(kind);
        if (!DesignSystemKinds.IsValid(resolvedKind))
            return BadRequest(new { code = "INVALID_KIND" });

        if (!TryResolveAgentType(role, out AgentType agentType))
            return BadRequest(new { code = "INVALID_ROLE" });

        DesignSystemVersion? version = await _designSystems.GetCurrentApprovedAsync(projectId, resolvedKind, ct);
        if (version is null)
            return Ok(new PreviewResponse(string.Empty, 0));

        (DesignView? designView, ProductView? productView) = DesignSystemRoleViews.ViewsFor(agentType);

        string? block;
        if (resolvedKind == DesignSystemKinds.Design)
        {
            if (designView is not { } view || ParseObject(version.DocumentJson) is not { } document)
                return Ok(new PreviewResponse(string.Empty, 0));

            block = DesignSystemPromptView.RenderDesign(document, view, version.VersionNumber, DesignMaxChars());
        }
        else
        {
            if (productView is not { } view)
                return Ok(new PreviewResponse(string.Empty, 0));

            ProductMapDocument? map = ProductMapJson.Parse(version.DocumentJson);
            if (map is null)
                return Ok(new PreviewResponse(string.Empty, 0));

            block = DesignSystemPromptView.RenderProduct(map, view, version.VersionNumber, ProductMaxChars());
        }

        return Ok(new PreviewResponse(block ?? string.Empty, block?.Length ?? 0));
    }

    /// <summary>
    /// SPEC 16.3 <c>GET editor-defaults</c>: the current approved design member's heading font and
    /// text colour mapped onto what the thumbnail editor offers. A project with no usable design
    /// member is <c>204</c>, so the editor keeps exactly its own defaults; a member is unusable when
    /// there is no design system, no approved current version, or a document that will not parse.
    /// </summary>
    [HttpGet("editor-defaults")]
    public async Task<ActionResult<EditorDefaultsResponse>> GetEditorDefaults(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        EditorDefaultsResponse? defaults = await EditImageDocumentSupport.LoadEditorDefaultsAsync(_db, projectId, ct);
        return defaults is null ? NoContent() : Ok(defaults);
    }

    private int DesignMaxChars() =>
        _configuration?.GetValue<int?>("DesignSystem:Ambient:DesignMaxChars") ?? 2400;

    private int ProductMaxChars() =>
        _configuration?.GetValue<int?>("DesignSystem:Ambient:ProductMaxChars") ?? 3200;

    /// <summary>
    /// Resolves <paramref name="role"/> by an exact (case-insensitive) <see cref="AgentType"/> name,
    /// so a numeric string never parses into an undefined member.
    /// </summary>
    private static bool TryResolveAgentType(string? role, out AgentType agentType)
    {
        agentType = default;
        if (string.IsNullOrWhiteSpace(role))
            return false;

        foreach (string name in Enum.GetNames<AgentType>())
        {
            if (string.Equals(name, role, StringComparison.OrdinalIgnoreCase))
            {
                agentType = Enum.Parse<AgentType>(name);
                return true;
            }
        }

        return false;
    }

    private static JsonObject? ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// SPEC 9 <c>POST /import</c>: runs the static importers over the listed project files (max 50)
    /// and answers the Draft versions plus every diagnostic.
    /// </summary>
    [HttpPost("import")]
    public async Task<ActionResult<ImportResponse>> Import(
        Guid projectId, ImportRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        IReadOnlyList<string> kinds = request.Kinds is { Count: > 0 } ? request.Kinds : [DesignSystemKinds.Design];
        foreach (string kind in kinds)
        {
            if (!DesignSystemKinds.IsValid(kind)) return InvalidKind();
        }

        DesignSystemImportResult result = await _designSystems.ImportAsync(
            projectId, _currentUser.UserId, kinds, request.ProjectFileIds ?? [], ct);

        IReadOnlyList<ImportDiagnosticDto> diagnostics = (result.Diagnostics ?? [])
            .Select(d => new ImportDiagnosticDto(d.Severity.ToString(), d.Code, d.Path, d.Message))
            .ToList();

        switch (result.Status)
        {
            case DesignSystemImportStatus.Ok:
                // DesignSystemService returns only the created versions, not the DesignImportResult the
                // readers produced, so there is no document to count. A report built from a null design
                // would claim every axis imported 0 with support None; emit none rather than a false one.
                return Ok(new ImportResponse(result.Versions ?? [], diagnostics));

            case DesignSystemImportStatus.NotFound:
                return NotFound();

            case DesignSystemImportStatus.NoFiles:
                return BadRequest(new { message = "no project files listed" });

            case DesignSystemImportStatus.TooManyFiles:
                return BadRequest(new { message = "at most 50 project files may be imported at once" });

            case DesignSystemImportStatus.QuotaExceeded when result.Quota is { } quota:
                return quota.ToResult();

            case DesignSystemImportStatus.NothingRecognized:
                return BadRequest(new { message = "no importable file was recognized", diagnostics });

            case DesignSystemImportStatus.ValidationFailed:
                return BadRequest(new
                {
                    message = "Invalid design-system document",
                    errors = (result.Violations ?? [])
                        .Select(v => new { code = v.Code, path = v.Path, message = v.Message }),
                });

            default:
                return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// SPEC 16.4 figma-variables <c>POST /import/figma</c>: a one-shot bridge that fetches a Figma
    /// file's variables with a caller-supplied Personal Access Token, runs the pure
    /// <see cref="FigmaVariablesImporter"/> over the response and stores the Draft as an Import
    /// version. The token serves this single call only and is never persisted, logged or echoed.
    /// </summary>
    [HttpPost("import/figma")]
    public async Task<ActionResult<FigmaImportResponse>> ImportFigma(
        Guid projectId, FigmaImportRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (!(_configuration?.GetValue<bool?>("Import:Figma:Enabled") ?? false))
            return NotFound(new { code = "feature_disabled", message = "the Figma variables import is disabled" });

        if (request is null
            || string.IsNullOrEmpty(request.FileKey)
            || !FigmaFileKeyPattern.IsMatch(request.FileKey))
        {
            return BadRequest(new
            {
                code = "INVALID_FILE_KEY",
                message = "fileKey must be 10 to 64 letters and digits",
            });
        }

        if (string.IsNullOrEmpty(request.AccessToken) || request.AccessToken.Length > MaxFigmaAccessTokenLength)
        {
            return BadRequest(new
            {
                code = "INVALID_ACCESS_TOKEN",
                message = $"an access token of at most {MaxFigmaAccessTokenLength} characters is required",
            });
        }

        // The token is handed to the client on this line and exists nowhere else: no field, no log,
        // no draft, no response body.
        FigmaVariablesClient client = _figmaClient ?? DefaultFigmaClient.Value;
        FigmaFetchResult fetch = await client.FetchVariablesAsync(request.FileKey, request.AccessToken, ct);

        switch (fetch.Status)
        {
            case FigmaFetchStatus.Forbidden:
                return StatusCode(StatusCodes.Status424FailedDependency, new
                {
                    code = "FIGMA_NOT_AVAILABLE",
                    message = "Figma refused access to this file's variables. Local variables require an Enterprise plan and a token allowed to read them.",
                });

            case FigmaFetchStatus.Redirected:
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    code = "FIGMA_REDIRECT_REFUSED",
                    message = "Figma answered with a redirect, which is never followed.",
                });

            case FigmaFetchStatus.TooLarge:
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new
                {
                    code = "RESPONSE_TOO_LARGE",
                    message = "the Figma response exceeded the 2 MiB cap.",
                });

            case FigmaFetchStatus.NotFound:
                return NotFound(new
                {
                    code = "FIGMA_FILE_NOT_FOUND",
                    message = "Figma knows no such file for this token.",
                });

            case FigmaFetchStatus.Ok:
                break;

            default:
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    code = "FIGMA_IMPORT_FAILED",
                    message = "the Figma request did not complete.",
                });
        }

        if (fetch.Body is null)
            return StatusCode(StatusCodes.Status502BadGateway, new { code = "FIGMA_IMPORT_FAILED" });

        var importer = new FigmaVariablesImporter();
        byte[] bytes = Encoding.UTF8.GetBytes(fetch.Body);
        DesignImportResult imported = importer.Import(
        [
            new ImportFile($"figma/{request.FileKey}.json", bytes, string.Empty, null),
        ]);

        if (imported.Design is null)
        {
            return BadRequest(new
            {
                code = "NO_TOKENS_RECOGNIZED",
                message = "the Figma response carried no importable variables",
                diagnostics = imported.Diagnostics
                    .Select(d => new ImportDiagnosticDto(d.Severity.ToString(), d.Code, d.Path, d.Message)),
            });
        }

        DesignSystemWriteResult write = await _designSystems.CreateDraftAsync(
            projectId,
            _currentUser.UserId,
            DesignSystemKinds.Design,
            imported.Design,
            DesignVersionOrigin.Import,
            workflowExecutionId: null,
            coverageJson: null,
            sources: imported.Sources,
            baseVersionId: null,
            ct);

        if (write.Status != DesignSystemWriteStatus.Ok || write.Version is not { } version)
            return MapWriteResult(write);

        await DesignSystemService.PersistImportSourcesAsync(_db, version, imported.Sources, ct);

        _logger.LogInformation(
            "design_system.import.figma {ProjectId} {VersionId} {UserId}",
            projectId, version.Id, _currentUser.UserId);

        IReadOnlyList<ImportDiagnosticDto> diagnostics = imported.Diagnostics
            .Concat(await _designSystems.AssessImportAsync(projectId, imported.Design, ct))
            .Select(d => new ImportDiagnosticDto(d.Severity.ToString(), d.Code, d.Path, d.Message))
            .ToList();

        return StatusCode(StatusCodes.Status201Created, new FigmaImportResponse(
            version, diagnostics, ToReport(DesignImportReportBuilder.Build(importer.Id, null, imported))));
    }

    /// <summary>
    /// SPEC 16.4 brand-pdf <c>POST /import/pdf</c>: a multipart PDF (at most 20 MiB) is sniffed for
    /// the <c>%PDF-</c> magic, measured against the plan's storage quota, and forwarded to the
    /// <c>import-worker</c>'s <c>/v1/pdf-text</c> — the only place the bytes are parsed. The pure
    /// <see cref="BrandPdfImporter"/> then reads the extracted text into a low-confidence
    /// (<c>0.4</c>, the "low" band) <see cref="DesignVersionOrigin.Import"/> Draft.
    ///
    /// <para>
    /// An unreachable worker is <c>503 import_worker_unavailable</c>; an over-cap upload is
    /// <c>413</c>; a body with no PDF magic is <c>415</c>. The worker's bearer token is applied to
    /// that one call and appears in no log, no draft and no response.
    /// </para>
    /// </summary>
    [HttpPost("import/pdf")]
    [RequestSizeLimit(PdfMaxBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = PdfMaxBytes)]
    public async Task<ActionResult<PdfImportResponse>> ImportPdf(
        Guid projectId, [FromForm] IFormFile? file, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        // SEC-7 kill switch: Import:Pdf:Enabled (default true) turns the whole route off without a deploy.
        if (!(_configuration?.GetValue<bool?>("Import:Pdf:Enabled") ?? true))
            return NotFound(new { code = "feature_disabled", message = "the brand PDF import is disabled" });

        if (file is null || file.Length == 0)
            return BadRequest(new { code = "NO_FILE", message = "a PDF file is required" });

        if (file.Length > PdfMaxBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new
            {
                code = "PDF_TOO_LARGE",
                message = $"the PDF exceeds the {PdfMaxBytes}-byte cap.",
            });
        }

        byte[] bytes;
        await using (Stream stream = file.OpenReadStream())
        {
            bytes = await ReadBoundedAsync(stream, PdfMaxBytes, ct);
        }

        if (bytes.LongLength > PdfMaxBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new
            {
                code = "PDF_TOO_LARGE",
                message = $"the PDF exceeds the {PdfMaxBytes}-byte cap.",
            });
        }

        if (!HasPdfMagic(bytes))
            return StatusCode(StatusCodes.Status415UnsupportedMediaType, new { code = "NOT_A_PDF" });

        Project? project = access.Project;
        if (project is null)
            return NotFound();

        if (_planGate is not null)
        {
            PlanGateViolation? violation = await _planGate.CheckUploadAsync(project.OrganizationId, bytes.LongLength, ct);
            if (violation is not null)
                return violation.ToResult();
        }

        if (_importWorker is null || !_importWorker.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { code = "import_worker_unavailable" });

        using var pdf = new MemoryStream(bytes, writable: false);
        ImportWorkerPdfTextResult worker = await _importWorker.ExtractPdfTextAsync(pdf, ct);
        switch (worker.Status)
        {
            case ImportWorkerStatus.Ok:
                break;

            case ImportWorkerStatus.Unavailable:
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    code = "import_worker_unavailable",
                    message = "the import worker is unavailable.",
                });

            case ImportWorkerStatus.Busy:
                return WorkerBusy(worker.RetryAfterSeconds);

            case ImportWorkerStatus.TooLarge:
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new { code = "PDF_TOO_LARGE" });

            case ImportWorkerStatus.NotPdf:
                return StatusCode(StatusCodes.Status415UnsupportedMediaType, new { code = "NOT_A_PDF" });

            case ImportWorkerStatus.Unreadable:
                return StatusCode(StatusCodes.Status422UnprocessableEntity, new { code = "PDF_UNREADABLE" });

            default:
                return StatusCode(StatusCodes.Status502BadGateway, new { code = "IMPORT_WORKER_FAILED" });
        }

        var importer = new BrandPdfImporter();
        string path = string.IsNullOrWhiteSpace(file.FileName) ? "brand.pdf" : Path.GetFileName(file.FileName);
        DesignImportResult imported = importer.ImportText(worker.Text, path);

        IReadOnlyList<ImportDiagnosticDto> diagnostics = imported.Diagnostics
            .Select(d => new ImportDiagnosticDto(d.Severity.ToString(), d.Code, d.Path, d.Message))
            .ToList();

        if (imported.Design is null)
        {
            return BadRequest(new
            {
                code = "IMPORT_NOTHING_RECOGNIZED",
                message = "the PDF text carried no colour or font candidate",
                diagnostics,
            });
        }

        DesignSystemWriteResult write = await _designSystems.CreateDraftAsync(
            projectId,
            _currentUser.UserId,
            DesignSystemKinds.Design,
            imported.Design,
            DesignVersionOrigin.Import,
            workflowExecutionId: null,
            coverageJson: null,
            sources: imported.Sources,
            baseVersionId: null,
            ct);

        if (write.Status != DesignSystemWriteStatus.Ok || write.Version is not { } version)
            return MapWriteResult(write);

        await DesignSystemService.PersistImportSourcesAsync(_db, version, imported.Sources, ct);

        _logger.LogInformation(
            "design_system.import.pdf {ProjectId} {VersionId} {UserId}",
            projectId, version.Id, _currentUser.UserId);

        diagnostics = diagnostics
            .Concat((await _designSystems.AssessImportAsync(projectId, imported.Design, ct))
                .Select(d => new ImportDiagnosticDto(d.Severity.ToString(), d.Code, d.Path, d.Message)))
            .ToList();

        return StatusCode(StatusCodes.Status201Created, new PdfImportResponse(
            version, diagnostics, ToReport(DesignImportReportBuilder.Build(importer.Id, null, imported))));
    }

    /// <summary>
    /// SPEC 16.4 bundle <c>POST /import-zip</c>: a multipart zip (at most 32 MiB, magic
    /// <c>PK\x03\x04</c>) is measured against the plan's storage quota and forwarded to the
    /// <c>import-worker</c>'s <c>/v1/extract-zip</c> — the only place the archive is parsed. The
    /// inlined text entries are built into <see cref="ImportFile"/>s, <see cref="BundleImporter"/>
    /// runs every registered importer over them, and the merged document becomes an
    /// <see cref="DesignVersionOrigin.Import"/> Draft with one provenance source per file and the
    /// vendor label read from the archive's layout.
    ///
    /// <para>
    /// A hostile archive (path traversal, bomb, too many entries) is <c>400 IMPORT_REJECTED</c> with
    /// the reader's reasons, an archive that only holds images or binary is <c>400
    /// IMPORT_NOTHING_RECOGNIZED</c>, an unreachable worker is <c>503 import_worker_unavailable</c>.
    /// The zip and its entries are never stored as project files and no code entry is ever read.
    /// </para>
    /// </summary>
    [HttpPost("import-zip")]
    [RequestSizeLimit(ZipMaxBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = ZipMaxBytes)]
    public async Task<ActionResult<ZipImportResponse>> ImportZip(
        Guid projectId, [FromForm] IFormFile? file, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (file is null || file.Length == 0)
            return BadRequest(new { code = "NO_FILE", message = "a zip file is required" });

        if (file.Length > ZipMaxBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new
            {
                code = "ZIP_TOO_LARGE",
                message = $"the archive exceeds the {ZipMaxBytes}-byte cap.",
            });
        }

        byte[] bytes;
        await using (Stream stream = file.OpenReadStream())
        {
            bytes = await ReadBoundedAsync(stream, ZipMaxBytes, ct);
        }

        if (bytes.LongLength > ZipMaxBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new
            {
                code = "ZIP_TOO_LARGE",
                message = $"the archive exceeds the {ZipMaxBytes}-byte cap.",
            });
        }

        if (!HasZipMagic(bytes))
            return StatusCode(StatusCodes.Status415UnsupportedMediaType, new { code = "NOT_A_ZIP" });

        Project? project = access.Project;
        if (project is null)
            return NotFound();

        if (_planGate is not null)
        {
            PlanGateViolation? violation = await _planGate.CheckUploadAsync(project.OrganizationId, bytes.LongLength, ct);
            if (violation is not null)
                return violation.ToResult();
        }

        if (_importWorker is null || !_importWorker.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { code = "import_worker_unavailable" });

        using var zip = new MemoryStream(bytes, writable: false);
        ImportWorkerZipResult worker = await _importWorker.ExtractZipAsync(zip, ct);
        switch (worker.Status)
        {
            case ImportWorkerStatus.Ok:
                break;

            case ImportWorkerStatus.Unavailable:
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    code = "import_worker_unavailable",
                    message = "the import worker is unavailable.",
                });

            case ImportWorkerStatus.TooLarge:
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new { code = "ZIP_TOO_LARGE" });

            case ImportWorkerStatus.Busy:
                return WorkerBusy(worker.RetryAfterSeconds);

            case ImportWorkerStatus.BadResponse:
                // Counts and a fixed violation code only: never a path, digest or text from the worker.
                _logger.LogWarning(
                    "design_system.import.zip.bad_worker_response {ProjectId} {Violation} {Entries}",
                    projectId, worker.Violation ?? "unknown", worker.ObservedEntries);
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    code = "IMPORT_WORKER_BAD_RESPONSE",
                    message = "the import worker returned an answer that failed validation.",
                });

            case ImportWorkerStatus.NotZip:
                return BadRequest(new { code = "NOT_A_ZIP" });

            default:
                return StatusCode(StatusCodes.Status502BadGateway, new { code = "IMPORT_WORKER_FAILED" });
        }

        ZipBundle bundle = worker.Bundle
            ?? throw new InvalidOperationException("a successful extract-zip answer carried no bundle");

        // Only the reader's hostile shapes refuse the archive; benign classifier drops are ignored.
        if (bundle.Aborted || bundle.Dropped.Any(drop => RejectingZipRefusals.Contains(drop.Reason)))
        {
            return BadRequest(new
            {
                code = "IMPORT_REJECTED",
                message = bundle.Aborted
                    ? "the archive was refused as a whole"
                    : "the archive contained entries that were refused",
                abortReason = bundle.AbortReason,
                reasons = bundle.Dropped.Select(drop => new { path = drop.Path, reason = drop.Reason }),
            });
        }

        var files = new List<ImportFile>();
        var capDiagnostics = new List<ImportDiagnostic>();
        foreach (ZipEntry entry in bundle.Files)
        {
            if (entry.Text is null || entry.Kind is not ("json" or "css" or "text" or "svg" or "markup"))
                continue;

            long cap = entry.Kind switch
            {
                "json" => ImportLimits.JsonMaxBytes,
                "css" => ImportLimits.CssMaxBytes,
                "svg" => ImportLimits.SvgMaxBytes,
                "markup" => ImportLimits.MarkupMaxBytes,
                _ => ImportLimits.MarkdownMaxBytes,
            };

            byte[] content = Encoding.UTF8.GetBytes(entry.Text);
            if (content.LongLength > cap)
            {
                capDiagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Warn,
                    "FILE_TOO_LARGE",
                    entry.Path,
                    $"the entry exceeds the {cap}-byte importer cap"));
                continue;
            }

            files.Add(new ImportFile(entry.Path, content, entry.Sha256, null));
        }

        // The worker's bounded binary channel: PNG/JPEG/WebP images under `assets/` (or the exact `.thumbnail`
        // basename) and font files under `fonts`/`assets`. Every entry is labelled by its OWN magic bytes, never
        // by the name it arrived under; a font is re-sniffed here so a non-font can never become an asset.
        var binaryAssets = new List<ImportedAsset>();
        foreach (ZipEntry entry in bundle.Files)
        {
            if (entry.Bytes is not { } content)
                continue;

            string? mimeType;
            if (entry.Kind == "image")
            {
                mimeType = MagicSniffer.Sniff(content) switch
                {
                    MagicKind.Png => "image/png",
                    MagicKind.Jpeg => "image/jpeg",
                    MagicKind.WebP => "image/webp",
                    _ => null,
                };
            }
            else if (entry.Kind == "font")
            {
                mimeType = MagicSniffer.Sniff(content) switch
                {
                    MagicKind.Woff => "font/woff",
                    MagicKind.Woff2 => "font/woff2",
                    MagicKind.TrueType => "font/ttf",
                    MagicKind.OpenType => "font/otf",
                    _ => null,
                };
            }
            else
            {
                continue;
            }

            if (mimeType is null)
                continue;

            binaryAssets.Add(new ImportedAsset(
                Role: entry.Kind == "font" ? "font" : string.Empty,
                Variant: null,
                Name: SafeContentPolicy.SanitizeFileName(Path.GetFileName(entry.Path)),
                Path: entry.Path,
                MimeType: mimeType,
                Sha256: entry.Sha256,
                Content: content));
        }

        BundleImportResult imported = new BundleImporter().ImportBundle(files, binaryAssets);

        IReadOnlyList<SkippedFileGroup> skipped = SummariseSkipped(bundle.Dropped);

        // The response's skipped list is bounded to 20 reason groups, but the report must not inherit
        // that bound: it counts every distinct skip the worker named.
        IReadOnlyList<ImportSkippedGroup> reportSkipped = SummariseSkipped(bundle.Dropped, maxReasons: int.MaxValue)
            .Select(group => new ImportSkippedGroup(group.Reason, group.Count))
            .ToList();

        IEnumerable<ImportDiagnostic> skippedDiagnostics = skipped.Select(group => new ImportDiagnostic(
            ImportDiagnosticSeverity.Info,
            "FILES_SKIPPED",
            null,
            $"{group.Count} file(s) were skipped ({group.Reason})"));

        var diagnostics = capDiagnostics
            .Concat(skippedDiagnostics)
            .Concat(imported.Import.Diagnostics ?? [])
            .Select(d => new ImportDiagnosticDto(d.Severity.ToString(), d.Code, d.Path, d.Message))
            .ToList();

        // Route-level facts (an asset not stored or quarantined, a reference not stored, a product map
        // not created) are drops too. Keep the typed copy so the report counts the same diagnostics
        // the response carries, and both agree.
        var routeDiagnostics = new List<ImportDiagnostic>(capDiagnostics);

        void AddRouteDiagnostic(ImportDiagnostic diagnostic)
        {
            routeDiagnostics.Add(diagnostic);
            diagnostics.Add(new ImportDiagnosticDto(
                diagnostic.Severity.ToString(), diagnostic.Code, diagnostic.Path, diagnostic.Message));
        }

        if (imported.Import.Design is null)
        {
            return BadRequest(new
            {
                code = "IMPORT_NOTHING_RECOGNIZED",
                message = "no registered importer recognized any entry of the archive",
                vendor = imported.Vendor,
                diagnostics,
                skipped,
            });
        }

        // A brand asset is a project file of THIS project or it does not exist: upload under the
        // project's own prefix, insert the row exactly as the upload routes do, then let the shared
        // post-add path (CSAM hash matching first) run before the document references it.
        // The importer is the one authority on how many assets an archive may carry: it bounds each
        // raster/font channel and each role, so the route stores exactly what the reader selected.
        var keptAssets = new List<(ImportedAsset Asset, Guid FileId)>();
        if (imported.Assets.Count > 0 && (_fileStorage is null || _fileAddedEffects is null))
        {
            AddRouteDiagnostic(new ImportDiagnostic(
                ImportDiagnosticSeverity.Warn,
                "ASSETS_NOT_UPLOADED",
                string.Empty,
                "assets were read from the archive but storage is not available; none was stored")
            { Axis = DesignImportAxis.Assets });
        }
        else if (imported.Assets.Count > 0)
        {
            // The archive was checked compressed; the uncompressed bytes it will store get their own
            // quota check, exactly as reference documents do, so the ratio cannot overrun the plan.
            long assetBytes = imported.Assets.Sum(asset => (long)asset.Content.Length);
            if (_planGate is not null)
            {
                PlanGateViolation? violation = await _planGate.CheckUploadAsync(
                    project.OrganizationId, assetBytes, ct);
                if (violation is not null)
                    return violation.ToResult();
            }

            foreach (ImportedAsset asset in imported.Assets)
            {
                Guid fileId = Guid.NewGuid();
                var metadata = new Dictionary<string, string>
                {
                    ["project-id"] = projectId.ToString(),
                    ["owner-id"] = project.OwnerId.ToString(),
                    ["org-id"] = project.OrganizationId.ToString(),
                    ["uploaded-by"] = _currentUser.UserId.ToString(),
                    ["project-file-id"] = fileId.ToString(),
                };

                StoredFileObject stored = await _fileStorage!.UploadAsync(
                    projectId,
                    fileId,
                    new MemoryStream(asset.Content.ToArray()),
                    asset.Name,
                    asset.MimeType,
                    metadata,
                    ct,
                    category: "userFiles",
                    originalPath: $"design-system/assets/{asset.Name}");

                var row = new ProjectFile
                {
                    Id = fileId,
                    ProjectId = projectId,
                    OriginalFileName = stored.OriginalFileName,
                    OriginalPath = stored.OriginalPath,
                    DirectoryPath = stored.DirectoryPath,
                    Category = "userFiles",
                    StorageKey = stored.StorageKey,
                    StorageFileName = stored.StorageFileName,
                    StorageBucket = stored.BucketName,
                    StoragePrefix = stored.StoragePrefix,
                    StorageMetadataJson = stored.StorageMetadataJson,
                    MimeType = asset.MimeType,
                    SizeBytes = asset.Content.Length,
                    // A logo is an image to serve, not text to index: Done/NotIndexed keeps it out of
                    // the summarizer/embedding queue (and its metered Vision call) by design.
                    SummaryStatus = SummaryStatus.Done,
                    IndexingStatus = FileIndexingStatus.NotIndexed,
                    UploadedAt = DateTime.UtcNow,
                };

                _db.ProjectFiles.Add(row);
                await _db.SaveChangesAsync(ct);
                await _fileAddedEffects!.RunAsync(row, _currentUser.UserId, ct);

                if (row.CsamStatus == CsamScanStatus.Quarantined)
                {
                    AddRouteDiagnostic(new ImportDiagnostic(
                        ImportDiagnosticSeverity.Warn,
                        "ASSET_QUARANTINED",
                        asset.Name,
                        "the stored asset was quarantined and is not referenced by the document")
                    { Axis = DesignImportAxis.Assets });
                    continue;
                }

                keptAssets.Add((asset, fileId));
            }
        }

        if (keptAssets.Count > 0)
        {
            var assetsNode = new JsonObject();
            foreach (string role in new[] { "logo", "icon", "preview" })
            {
                List<(ImportedAsset Asset, Guid FileId)> roleAssets = keptAssets
                    .Where(kept => string.Equals(kept.Asset.Role, role, StringComparison.Ordinal))
                    .OrderBy(kept => kept.Asset.Subject switch { "product" or null => 0, "unknown" => 1, _ => 2 })
                    .ThenBy(kept => string.Equals(kept.Asset.Variant, "default", StringComparison.Ordinal) ? 0 : 1)
                    .ThenBy(kept => kept.Asset.Name, StringComparer.Ordinal)
                    .ToList();
                if (roleAssets.Count == 0)
                    continue;

                var variants = new JsonArray();
                foreach ((ImportedAsset asset, Guid fileId) in roleAssets.Skip(1).Take(7))
                {
                    var variantNode = new JsonObject
                    {
                        ["name"] = asset.Variant ?? "variant",
                        ["fileRef"] = $"project-file:{fileId}",
                    };
                    AddBrandMetadata(variantNode, asset);
                    variants.Add(variantNode);
                }

                var slotNode = new JsonObject
                {
                    ["fileRef"] = $"project-file:{roleAssets[0].FileId}",
                    ["name"] = roleAssets[0].Asset.Name,
                    ["variants"] = variants,
                };
                AddBrandMetadata(slotNode, roleAssets[0].Asset);
                assetsNode[role] = slotNode;
            }

            if (assetsNode.Count > 0)
                imported.Import.Design["assets"] = assetsNode;
        }

        // Font files are project files too, but they are not brand assets: a face belongs in
        // `fonts.files[<family>]`, never in the logo/icon/preview slots. Only a file that was stored
        // (and survived CSAM/effects) is referenced.
        if (keptAssets.Count > 0)
        {
            var fontFiles = new JsonObject();
            foreach ((ImportedAsset asset, Guid fileId) in keptAssets
                         .Where(static kept =>
                             string.Equals(kept.Asset.Role, "font", StringComparison.Ordinal))
                         .OrderBy(static kept => kept.Asset.Variant, StringComparer.Ordinal))
            {
                string family = string.IsNullOrWhiteSpace(asset.Variant) ? "font" : asset.Variant!;
                fontFiles[family] = $"project-file:{fileId}";
            }

            if (fontFiles.Count > 0)
            {
                if (imported.Import.Design["fonts"] is JsonObject fonts)
                {
                    fonts["files"] = fontFiles;
                }
                else
                {
                    imported.Import.Design["fonts"] = new JsonObject { ["files"] = fontFiles };
                }
            }
        }

        // Prose, guidelines and UI-kit notes are project files too: inert text the rest of the product
        // can search and summarize. Their total runs through the same upload quota as the archive.
        var keptReferences = new List<ImportedReferenceSummary>();
        if (imported.ReferenceDocuments.Count > 0)
        {
            long referenceBytes = imported.ReferenceDocuments.Sum(
                reference => (long)Encoding.UTF8.GetByteCount(reference.Text));

            if (_planGate is not null)
            {
                PlanGateViolation? violation = await _planGate.CheckUploadAsync(
                    project.OrganizationId, referenceBytes, ct);
                if (violation is not null)
                    return violation.ToResult();
            }

            if (_fileStorage is null || _fileAddedEffects is null)
            {
                AddRouteDiagnostic(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Warn,
                    "REFERENCES_NOT_UPLOADED",
                    string.Empty,
                    "reference documents were read from the archive but storage is not available; none was stored")
                { Axis = DesignImportAxis.ReferenceDocuments });
            }
            else
            {
                foreach (ImportedReferenceDocument reference in imported.ReferenceDocuments)
                {
                    byte[] content = Encoding.UTF8.GetBytes(reference.Text);
                    Guid fileId = Guid.NewGuid();
                    var metadata = new Dictionary<string, string>
                    {
                        ["project-id"] = projectId.ToString(),
                        ["owner-id"] = project.OwnerId.ToString(),
                        ["org-id"] = project.OrganizationId.ToString(),
                        ["uploaded-by"] = _currentUser.UserId.ToString(),
                        ["project-file-id"] = fileId.ToString(),
                    };

                    StoredFileObject stored = await _fileStorage.UploadAsync(
                        projectId,
                        fileId,
                        new MemoryStream(content),
                        Path.GetFileName(reference.Name),
                        reference.MimeType,
                        metadata,
                        ct,
                        category: "userFiles",
                        originalPath: $"design-system/references/{reference.Name}");

                    var row = new ProjectFile
                    {
                        Id = fileId,
                        ProjectId = projectId,
                        OriginalFileName = stored.OriginalFileName,
                        OriginalPath = stored.OriginalPath,
                        DirectoryPath = stored.DirectoryPath,
                        Category = "userFiles",
                        StorageKey = stored.StorageKey,
                        StorageFileName = stored.StorageFileName,
                        StorageBucket = stored.BucketName,
                        StoragePrefix = stored.StoragePrefix,
                        StorageMetadataJson = stored.StorageMetadataJson,
                        MimeType = reference.MimeType,
                        SizeBytes = content.Length,
                        // Reference prose is text to index and summarize, unlike a brand image.
                        SummaryStatus = SummaryStatus.Pending,
                        IndexingStatus = FileIndexingStatus.Pending,
                        UploadedAt = DateTime.UtcNow,
                    };

                    _db.ProjectFiles.Add(row);
                    await _db.SaveChangesAsync(ct);
                    await _fileAddedEffects.RunAsync(row, _currentUser.UserId, ct);

                    if (row.CsamStatus == CsamScanStatus.Quarantined)
                    {
                        AddRouteDiagnostic(new ImportDiagnostic(
                            ImportDiagnosticSeverity.Warn,
                            "ASSET_QUARANTINED",
                            reference.Name,
                            "the stored reference was quarantined and is not returned")
                        { Axis = DesignImportAxis.ReferenceDocuments });
                        continue;
                    }

                    keptReferences.Add(new ImportedReferenceSummary(
                        reference.Name, reference.Path, fileId));
                }
            }
        }

        DesignSystemWriteResult write = await _designSystems.CreateDraftAsync(
            projectId,
            _currentUser.UserId,
            DesignSystemKinds.Design,
            imported.Import.Design,
            DesignVersionOrigin.Import,
            workflowExecutionId: null,
            coverageJson: null,
            sources: imported.Import.Sources,
            baseVersionId: null,
            ct);

        if (write.Status != DesignSystemWriteStatus.Ok || write.Version is not { } version)
            return MapWriteResult(write);

        await DesignSystemService.PersistImportSourcesAsync(_db, version, imported.Import.Sources, ct);

        // Nothing found, or less than the approved design has: said in the result, kept out of the
        // report's drop counts (it is a property of the whole import, not a dropped item).
        foreach (ImportDiagnostic warning in await _designSystems.AssessImportAsync(projectId, imported.Import.Design, ct))
        {
            diagnostics.Add(new ImportDiagnosticDto(
                warning.Severity.ToString(), warning.Code, warning.Path, warning.Message));
        }

        // A Claude Design export carries its component inventory: it becomes the product-map Draft.
        // A product-map that does not validate is a warning, not a lost design import.
        DesignVersionResponse? productMapVersion = null;
        if (imported.Import.ProductMap is { } productMap)
        {
            DesignSystemWriteResult productMapWrite = await _designSystems.CreateDraftAsync(
                projectId,
                _currentUser.UserId,
                DesignSystemKinds.ProductMap,
                ProductMapJson.ToNode(productMap),
                DesignVersionOrigin.Import,
                workflowExecutionId: null,
                coverageJson: null,
                sources: imported.Import.Sources,
                baseVersionId: null,
                ct);

            if (productMapWrite.Status == DesignSystemWriteStatus.Ok && productMapWrite.Version is { } mapVersion)
            {
                productMapVersion = mapVersion;
                await DesignSystemService.PersistImportSourcesAsync(
                    _db, mapVersion, imported.Import.Sources, ct);
            }
            else
            {
                DesignViolation? first = productMapWrite.Violations?.FirstOrDefault();
                AddRouteDiagnostic(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Warn,
                    "PRODUCT_MAP_NOT_CREATED",
                    first?.Path ?? string.Empty,
                    first is null
                        ? $"the product-map Draft was not created ({productMapWrite.Status})"
                        : $"the product-map Draft was not created ({productMapWrite.Status}): {first.Code}")
                { Axis = DesignImportAxis.Components });
            }
        }

        _logger.LogInformation(
            "design_system.import.zip {ProjectId} {VersionId} {UserId} {Vendor}",
            projectId, version.Id, _currentUser.UserId, imported.Vendor ?? "unknown");

        // The report is built after the final diagnostics exist: the reader's, the cap facts, and the
        // route-level drops (not stored, quarantined, product map not created). It also receives the
        // FULL skipped list, not the response's bounded 20-group summary.
        DesignImportResult reportResult = imported.Import with
        {
            Diagnostics = [.. routeDiagnostics, .. imported.Import.Diagnostics ?? []],
        };
        DesignImportReport report = DesignImportReportBuilder.Build(
            imported.Vendor ?? BundleImporter.ImporterId,
            imported.Vendor,
            reportResult,
            assets: imported.Assets,
            references: imported.ReferenceDocuments,
            skipped: reportSkipped);

        return StatusCode(StatusCodes.Status201Created, new ZipImportResponse(
            version,
            diagnostics,
            imported.Import.Sources,
            imported.Vendor,
            skipped,
            keptAssets.Count > 0
                ? keptAssets
                    .Select(kept => new ImportedAssetSummary(
                        kept.Asset.Role, kept.Asset.Variant, kept.Asset.Name, kept.FileId))
                    .ToList()
                : null,
            productMapVersion,
            keptReferences.Count > 0 ? keptReferences : null,
            ToReport(report)));
    }

    /// <summary>At most this many distinct skip reasons are reported.</summary>
    private const int MaxSkippedReasons = 20;

    /// <summary>
    /// Groups the worker's non-rejecting refusals by reason: <c>{reason, count, examples}</c> with at most
    /// three sanitised example names per reason and at most <paramref name="maxReasons"/> reasons (the
    /// response bound; the report passes <see cref="int.MaxValue"/> so no drop is summarised away).
    /// </summary>
    private static IReadOnlyList<SkippedFileGroup> SummariseSkipped(
        IReadOnlyList<ZipEntryRefusal> dropped, int maxReasons = MaxSkippedReasons) =>
        dropped
            .GroupBy(drop => drop.Reason, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(maxReasons)
            .Select(group => new SkippedFileGroup(
                group.Key,
                group.Count(),
                group.Select(drop => SanitiseFileName(drop.Path)).Distinct(StringComparer.Ordinal).Take(3).ToList()))
            .ToList();

    /// <summary>Printable ASCII only (each other character, however many UTF-16 units, becomes one <c>?</c>), at most 80 characters.</summary>
    private static string SanitiseFileName(string path)
    {
        var builder = new StringBuilder(Math.Min(path.Length, 80));
        int characters = 0;
        foreach (System.Text.Rune rune in path.EnumerateRunes())
        {
            if (characters++ == 80)
                break;
            builder.Append(rune.Value is >= ' ' and <= '~' ? (char)rune.Value : '?');
        }

        return builder.ToString();
    }

    /// <summary>503 for a worker that refused rather than queued; its Retry-After is passed through.</summary>
    private ObjectResult WorkerBusy(int retryAfterSeconds)
    {
        if (HttpContext is not null)
            Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new
        {
            code = "import_worker_unavailable",
            message = "the import worker is busy; retry shortly.",
        });
    }

    /// <summary>True when <paramref name="bytes"/> starts with the local-file zip magic <c>PK\x03\x04</c>.</summary>
    private static bool HasZipMagic(byte[] bytes) =>
        bytes.Length >= 4
        && bytes[0] == 0x50
        && bytes[1] == 0x4B
        && bytes[2] == 0x03
        && bytes[3] == 0x04;

    /// <summary>
    /// The byte twin of the importers' bounded reads: pulls at most <paramref name="maxBytes"/> + 1 so
    /// truncation is still reportable without ever buffering more than the cap.
    /// </summary>
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        const int chunkSize = 81920;
        byte[] chunk = new byte[chunkSize];
        using var buffered = new MemoryStream();
        long total = 0;

        while (total <= maxBytes)
        {
            int want = (int)Math.Min(chunkSize, maxBytes + 1 - total);
            int read = await stream.ReadAsync(chunk.AsMemory(0, want), ct);
            if (read <= 0)
                break;

            buffered.Write(chunk, 0, read);
            total += read;
        }

        return buffered.ToArray();
    }

    /// <summary>True when <c>%PDF-</c> appears in the first KiB, exactly as the worker checks it.</summary>
    private static bool HasPdfMagic(byte[] bytes)
    {
        int limit = Math.Min(bytes.Length, 1024);
        return limit > 0 && bytes.AsSpan(0, limit).IndexOf("%PDF-"u8) >= 0;
    }

    /// <summary>
    /// SPEC 9 <c>POST /ingest</c>: reads the Compose step's artifact and creates the Drafts and
    /// sources. Idempotent per execution: a second call answers the same version ids.
    /// </summary>
    [HttpPost("ingest")]
    public async Task<ActionResult<IngestResponse>> Ingest(
        Guid projectId, IngestRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        DesignSystemIngestResult result = await _ingestService.IngestAsync(
            projectId, _currentUser.UserId, request.ExecutionId, request.StepOrder, ct);

        switch (result.Status)
        {
            case DesignSystemIngestStatus.Ok:
                return Ok(new IngestResponse(result.VersionIds ?? [], result.Versions ?? []));

            case DesignSystemIngestStatus.NotFound:
            case DesignSystemIngestStatus.ArtifactNotFound:
                return NotFound();

            case DesignSystemIngestStatus.ArtifactTooLarge:
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new
                {
                    code = "ARTIFACT_TOO_LARGE",
                    message = "the artifact exceeds the 2 MiB cap",
                });

            case DesignSystemIngestStatus.InvalidArtifactKey:
                return BadRequest(new
                {
                    code = "INVALID_ARTIFACT_KEY",
                    message = "the artifact key is not under the execution prefix",
                });

            case DesignSystemIngestStatus.InvalidDocument:
                return BadRequest(new
                {
                    code = "INVALID_DOCUMENT",
                    message = "the artifact is not a valid design-system build document",
                });

            default:
                return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// SPEC 9.4 <c>POST /builds</c>: provision the <c>product-profile-from-code</c> workflow for the
    /// project, price the charged LLM-fallback steps, enforce the cap and the caller's acceptance,
    /// then queue the run. The static parse is free; only the fallback is estimated.
    ///
    /// <para>
    /// The order of the gates is the SPEC's: email verification, Edit on the project, the estimate
    /// cap, the caller's acceptance, then B7's affordability preflight (<c>402</c>) and finally
    /// <see cref="IWorkflowExecutionService.ExecuteAsync"/>. The preflight reuses
    /// <see cref="Services.Billing.WorkflowExecutionPreflight"/> — the same shared check
    /// <c>WorkflowsController</c> uses — so the two execute paths can never price a run differently,
    /// and the email-verification/trial-grant gates are replicated exactly as <c>Execute</c> runs them.
    /// </para>
    /// </summary>
    [HttpPost("builds")]
    public async Task<ActionResult<BuildResponse>> Builds(
        Guid projectId, BuildRequest? request, CancellationToken ct)
    {
        // C5: the same email-verification 403 every execute entry point answers first.
        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;

        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (_templates is null || _executions is null)
        {
            // No provisioning/execution service is wired in this host. Answer honestly rather than
            // pretending a build started; the app always registers both.
            return StatusCode(StatusCodes.Status500InternalServerError, new { code = "BUILD_UNAVAILABLE" });
        }

        string templateKey = string.IsNullOrWhiteSpace(request?.TemplateKey)
            ? WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey
            : request!.TemplateKey!;
        if (!string.Equals(templateKey, WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey, StringComparison.Ordinal))
        {
            return BadRequest(new
            {
                code = "INVALID_TEMPLATE",
                message = "this route only builds the product-profile-from-code template",
            });
        }

        WorkflowDefinition workflow;
        try
        {
            workflow = await _templates.CreateFromTemplateAsync(projectId, templateKey, skipIfExists: true, ct)
                ?? throw new InvalidOperationException("the template could not be provisioned");
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { code = "TEMPLATE_UNAVAILABLE", message = ex.Message });
        }

        // C6: lazy trial-credit provisioning (no-op unless Billing:Enabled; never throws). It must
        // run before the estimate so a first-ever build is priced against the credits just granted.
        if (_trialCredits is not null)
            await _trialCredits.EnsureTrialGrantAsync(_currentUser.UserId, _currentUser.OrganizationId, ct);

        // the one shared cap and accept-the-estimate gate (also run by the generic Execute and
        // Retry routes and by the assistant's ExecuteWorkflow). An existing same-named workflow whose
        // build shape was edited away would be skipped by the gate, so this route refuses it instead
        // of starting it ungated.
        if (!_designBuildGate.IsBuildTemplate(workflow))
        {
            return Conflict(new
            {
                code = "TEMPLATE_MODIFIED",
                message = "The project's profile build workflow was edited and is no longer the build template. Restore or delete it, then try again.",
            });
        }

        DesignBuildGateResult gate = await _designBuildGate.CheckAsync(
            _currentUser.OrganizationId, workflow, request?.AcceptEstimateCredits, ct);
        if (gate.Refused)
            return gate.ToActionResult();

        double low = gate.Low;
        double high = gate.High;

        // B7: the shared execute preflight. Advisory — it refuses only a run the balance plainly
        // cannot start; the engine's per-step gate still enforces every credit.
        WorkflowExecutionPreflightResult preflight = await new WorkflowExecutionPreflight(_db, _costEstimator)
            .CheckAsync(_currentUser.OrganizationId, projectId, workflow.Id, ct);
        if (preflight.WorkflowNotFound) return NotFound();
        if (!preflight.Allowed)
        {
            return StatusCode(402, new
            {
                code = "INSUFFICIENT_CREDITS",
                message = CreditShortfallMessage.Build(preflight),
                required = preflight.Required,
                balance = preflight.Balance,
                missing = CreditShortfallMessage.MissingCredits(preflight),
                low = preflight.Low,
                high = preflight.High,
                hasHistory = preflight.HasHistory,
            });
        }

        // A restore of this project is rewriting it: no run starts until it is done.
        if (await RestoreRunGate.IsHeldAsync(_db, projectId, ct)) return RestoreRunGate.Refusal();

        try
        {
            WorkflowExecutionResponse execution = await _executions.ExecuteAsync(
                projectId,
                workflow.Id,
                _currentUser.UserId,
                "Build the brand and product profile for this project.",
                ExecutionInitiator.Human,
                ct);

            _logger.LogInformation(
                "design_system.build {ProjectId} {WorkflowId} {ExecutionId} {UserId}",
                projectId, workflow.Id, execution.Id, _currentUser.UserId);

            return StatusCode(StatusCodes.Status202Accepted, new BuildResponse(
                execution.Id,
                workflow.Id,
                new BuildEstimateResponse(low, high)));
        }
        catch (WorkflowNotFoundException)
        {
            return NotFound();
        }
        catch (PlanLimitException limit)
        {
            return limit.Violation.ToResult();
        }
    }

    /// <summary>The proposal-decision-to-HTTP mapping (SPEC 9/13.4): both decided and stale are 409.</summary>
    private ActionResult<ProposalResponse> MapProposalResult(DesignSystemProposalResult result)
    {
        switch (result.Status)
        {
            case DesignSystemProposalDecisionStatus.Ok when result.Proposal is { } proposal:
                return Ok(proposal);

            case DesignSystemProposalDecisionStatus.NotFound:
                return NotFound();

            case DesignSystemProposalDecisionStatus.NotPending:
                return Conflict(new { code = "PROPOSAL_DECIDED", message = "the proposal was already decided" });

            case DesignSystemProposalDecisionStatus.StaleBase:
                return Conflict(new
                {
                    code = "STALE_BASE",
                    message = "the system's current version changed since the proposal",
                });

            case DesignSystemProposalDecisionStatus.VersionNotDraft:
                return Conflict(new { code = "NOT_DRAFT", message = "the proposed version is not a Draft" });

            default:
                return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    private ActionResult InvalidKind() => BadRequest(new { message = "invalid kind" });

    /// <summary>
    /// Publishes the internal report with enum names as strings, matching the T-008 capability
    /// matrix. The internal record keeps its enums; no serializer setting is relied on.
    /// </summary>
    private static ImportReportResponse ToReport(DesignImportReport report) =>
        new(
            report.ImporterId,
            report.Vendor,
            report.Axes
                .Select(axis => new ImportAxisReportResponse(
                    axis.Axis.ToString(),
                    axis.Support.ToString(),
                    axis.Imported,
                    axis.Dropped,
                    axis.Reasons
                        .Select(reason => new ImportDropGroupResponse(
                            reason.Code, reason.Severity.ToString(), reason.Count, reason.Message))
                        .ToList()))
                .ToList());

    /// <summary>
    /// The one write-result-to-HTTP mapping (SPEC 9): NotFound, the two 409 codes, and the 400
    /// violation body shared with import and ingest.
    /// </summary>
    private ActionResult MapWriteResult(DesignSystemWriteResult result)
    {
        switch (result.Status)
        {
            case DesignSystemWriteStatus.Ok when result.Version is { } version:
                return Ok(version);

            case DesignSystemWriteStatus.NotFound:
                return NotFound();

            case DesignSystemWriteStatus.NotDraft:
                return Conflict(new { code = "NOT_DRAFT", message = "the version is not a Draft" });

            case DesignSystemWriteStatus.StaleBase:
                return Conflict(new
                {
                    code = "STALE_BASE",
                    message = "the version was reviewed against a version that is no longer current",
                });

            case DesignSystemWriteStatus.ValidationFailed:
                return BadRequest(new
                {
                    message = "Invalid design-system document",
                    errors = (result.Violations ?? [])
                        .Select(v => new { code = v.Code, path = v.Path, message = v.Message }),
                });

            default:
                return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Writes the importer's classification and measurement onto an assets slot or variant: the sanitized
    /// file name, subject, form, tone, readableOn and luminance. Values come only from the importer, never
    /// from the request body.
    /// </summary>
    private static void AddBrandMetadata(JsonObject node, ImportedAsset asset)
    {
        node["file"] = asset.Name;
        if (asset.Subject is not null) node["subject"] = asset.Subject;
        if (asset.Form is not null) node["form"] = asset.Form;
        if (asset.Tone is not null) node["tone"] = asset.Tone;
        if (asset.ReadableOn is not null)
        {
            var readable = new JsonArray();
            foreach (string item in asset.ReadableOn) readable.Add(item);
            node["readableOn"] = readable;
        }

        if (asset.Luminance is { } luminance) node["luminance"] = Math.Round(luminance, 3);
    }

}

/// <summary>
/// The one-shot Figma import body. The access token is consumed by the request that carries it and
/// is never persisted anywhere.
/// </summary>
public sealed record FigmaImportRequest(string FileKey, string AccessToken);

/// <summary>The Import Draft created from a Figma variables response, plus every importer diagnostic.</summary>
public sealed record FigmaImportResponse(
    DesignVersionResponse Version,
    IReadOnlyList<ImportDiagnosticDto> Diagnostics,
    ImportReportResponse? Report = null);

/// <summary>The low-confidence Import Draft created from a brand PDF's extracted text, plus diagnostics.</summary>
public sealed record PdfImportResponse(
    DesignVersionResponse Version,
    IReadOnlyList<ImportDiagnosticDto> Diagnostics,
    ImportReportResponse? Report = null);

/// <summary>
/// The Import Draft created from an extracted archive's allowed text entries, every importer
/// diagnostic, one provenance source per file the bundle read, and the vendor label read from the
/// archive's layout (null when none).
/// </summary>
public sealed record ZipImportResponse(
    DesignVersionResponse Version,
    IReadOnlyList<ImportDiagnosticDto> Diagnostics,
    IReadOnlyList<ImportSourceRef> Sources,
    string? Vendor,
    IReadOnlyList<SkippedFileGroup> Skipped,
    IReadOnlyList<ImportedAssetSummary>? Assets = null,
    DesignVersionResponse? ProductMapVersion = null,
    IReadOnlyList<ImportedReferenceSummary>? References = null,
    ImportReportResponse? Report = null);

/// <summary>
/// One binary asset the zip route stored as a project file: its design-document role and variant,
/// its basename, and the project-file id the document's <c>assets.*.fileRef</c> names.
/// </summary>
public sealed record ImportedAssetSummary(string Role, string? Variant, string Name, Guid ProjectFileId);

/// <summary>
/// One reference document the zip route stored as a project file: the stable name the reader gave
/// it, the archive path it came from, and the project-file id of its stored inert text.
/// </summary>
public sealed record ImportedReferenceSummary(string Name, string Path, Guid ProjectFileId);

/// <summary>
/// Files the import-worker left out of an archive for one reason (e.g. <c>DROPPED_SCRIPT</c>,
/// <c>DROPPED_HTML</c>, <c>DOCUMENT_TOO_LARGE</c>): the count and up to three
/// sanitised example names. Serialised as <c>skipped: [{reason, count, examples}]</c>.
/// </summary>
public sealed record SkippedFileGroup(string Reason, int Count, IReadOnlyList<string> Examples);

/// <summary>
/// SPEC 16.1 <c>PUT /library-link</c> body: the organization library to descend from, or null/absent
/// to clear the link.
/// </summary>
public sealed record LibraryLinkRequest(Guid? LibraryId = null);
