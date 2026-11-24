using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Shared.Archives;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// An organization's own brand/design-system libraries (SPEC 16.1, P4-04), the org-scoped analogue
/// of a project's <see cref="DesignSystemsController"/>. Routed under the SINGULAR
/// <c>/api/v1/org/design-libraries</c>: nginx sends the plural <c>/api/v1/orgs/…</c> to the Go API,
/// so the two never collide.
///
/// <para>
/// Every action starts from the FILTERED <c>design_system_libraries</c> set and never from a
/// caller-supplied <c>design_systems</c> id: a library that does not exist, or belongs to another
/// organization, is <c>404 LIBRARY_NOT_FOUND</c>, never <c>403</c>. Inside the owning organization,
/// Billing is refused on every action and Member is read-only; Owner and Admin may write. The role is
/// re-read from <see cref="ICurrentUser.OrgRole"/> on every call and a platform admin whose active
/// role is Member is refused like any other Member.
/// </para>
///
/// <para>
/// The document container is the <c>design_systems</c> row with <c>project_id NULL</c> and
/// <c>kind = 'design'</c>; creating a library creates that row plus the library row in one
/// transaction, and deleting a library nulls every project's <c>parent_library_id</c> before removing
/// the container (and, with it, the versions). Version work runs through the shared
/// <see cref="IDesignSystemService"/> over a <see cref="DesignSystemScope"/>, so the validators, the
/// digest, the version numbering, the quota and the approve/reject/restore state machine are the
/// project path's, not a second copy.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/org/design-libraries")]
[Authorize]
public class OrgDesignLibrariesController : ControllerBase
{
    /// <summary>SPEC 19: the stable code for a foreign or nonexistent library.</summary>
    internal const string LibraryNotFoundCode = "LIBRARY_NOT_FOUND";

    /// <summary>The default for <c>DesignSystem:Library:MaxPerOrganization</c>.</summary>
    private const int DefaultMaxLibrariesPerOrganization = 50;

    /// <summary>SPEC 16.1: <c>design_system_libraries.name varchar(200)</c>.</summary>
    private const int MaxNameLength = 200;

    /// <summary>Keeps a download filename free of path separators and header-breaking characters.</summary>
    private static readonly Regex SafeFileNameChars = new("[^A-Za-z0-9._-]", RegexOptions.Compiled);

    /// <summary>The CSP an SVG leaves under: inert, sandboxed, no subresources.</summary>
    internal const string SvgContentSecurityPolicy = "default-src 'none'; sandbox";

    /// <summary>The longest stored asset name, matching <c>assets.&lt;role&gt;.name</c>.</summary>
    private const int MaxAssetNameLength = 200;

    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IDesignSystemService _designSystems;
    private readonly ILogger<OrgDesignLibrariesController> _logger;
    private readonly IConfiguration? _configuration;
    private readonly ILibraryAssetStorage? _libraryAssets;
    private readonly IPlanGateService? _planGate;

    public OrgDesignLibrariesController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IDesignSystemService designSystems,
        ILogger<OrgDesignLibrariesController> logger,
        IConfiguration? configuration = null,
        ILibraryAssetStorage? libraryAssets = null,
        IPlanGateService? planGate = null)
    {
        _configuration = configuration;
        _db = db;
        _currentUser = currentUser;
        _designSystems = designSystems;
        _logger = logger;
        _libraryAssets = libraryAssets;
        _planGate = planGate;
    }

    /// <summary>SPEC 16.1 <c>GET</c>: every library of the active organization, by name.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LibrarySummaryResponse>>> List(CancellationToken ct)
    {
        if (OrgLibraryGate.Read(this, _currentUser, out Guid orgId) is { } denied) return denied;

        List<DesignSystemLibrary> libraries = await _db.DesignSystemLibraries
            .Where(l => l.OrganizationId == orgId)
            .OrderBy(l => l.Name)
            .ToListAsync(ct);

        var summaries = new List<LibrarySummaryResponse>(libraries.Count);
        foreach (DesignSystemLibrary library in libraries)
        {
            DesignSystemSummaryResponse? summary = await _designSystems.GetSummaryAsync(
                DesignSystemScope.Library(library.DesignSystemId, orgId), ct);
            if (summary is not null)
                summaries.Add(ToSummary(library, summary));
        }

        return Ok(summaries);
    }

    /// <summary>
    /// SPEC 16.1 <c>POST</c>: creates the container row and the library row in one transaction and,
    /// when a document is present, its first Draft. An invalid document is a <c>400</c> and writes
    /// nothing at all.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<LibrarySummaryResponse>> Create(
        [FromBody] CreateLibraryRequest request, CancellationToken ct)
    {
        if (OrgLibraryGate.Write(this, _currentUser, out Guid orgId) is { } denied) return denied;

        string name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            return BadRequest(new { message = "name is required" });
        if (name.Length > MaxNameLength)
            return BadRequest(new { message = $"name must be at most {MaxNameLength} characters" });

        if (await _db.DesignSystemLibraries.AnyAsync(l => l.OrganizationId == orgId && l.Name == name, ct))
            return NameTaken();

        int cap = _configuration?.GetValue<int?>("DesignSystem:Library:MaxPerOrganization") ?? DefaultMaxLibrariesPerOrganization;
        if (await _db.DesignSystemLibraries.CountAsync(l => l.OrganizationId == orgId, ct) >= cap)
        {
            return Conflict(new
            {
                code = "library_limit_reached",
                message = $"an organization can have at most {cap} design-system libraries",
            });
        }

        // Validate before the first write: a refused body leaves no container and no library row. The
        // library's context has no project files, so every file id is foreign, exactly as the shared
        // service will see it.
        if (request.Document is { } document)
        {
            DesignValidationResult validation = DesignDocumentValidator.Validate(
                document, new DesignValidationContext(_ => false, DesignSystemService.BundledFontNames));
            if (!validation.IsValid)
                return BadRequest(new { message = "Invalid design-system document", errors = Errors(validation) });
        }

        DateTime now = DateTime.UtcNow;
        var container = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = null,
            Kind = DesignSystemKinds.Design,
            Name = name,
            CreatedByUserId = _currentUser.UserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var library = new DesignSystemLibrary
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Name = name,
            DesignSystemId = container.Id,
            CreatedByUserId = _currentUser.UserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using IDbContextTransaction transaction = await _db.Database.BeginTransactionAsync(ct);

        _db.DesignSystems.Add(container);
        _db.DesignSystemLibraries.Add(library);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A racing request took the name between the pre-check and the insert: the (organization_id, name)
            // unique index decided. The transaction disposes uncommitted, so nothing is left behind.
            await transaction.RollbackAsync(ct);
            return NameTaken();
        }

        if (request.Document is { } firstDocument)
        {
            DesignSystemWriteResult write = await _designSystems.CreateDraftAsync(
                DesignSystemScope.Library(container.Id, orgId),
                _currentUser.UserId,
                firstDocument,
                DesignVersionOrigin.Manual,
                workflowExecutionId: null,
                coverageJson: null,
                baseVersionId: null,
                ct);

            if (write.Status != DesignSystemWriteStatus.Ok)
            {
                await transaction.RollbackAsync(ct);
                return MapWrite(write);
            }
        }

        await transaction.CommitAsync(ct);

        _logger.LogInformation(
            "design_system.library.create {LibraryId} {DesignSystemId} {OrganizationId} {UserId}",
            library.Id, container.Id, orgId, _currentUser.UserId);

        DesignSystemSummaryResponse? created = await _designSystems.GetSummaryAsync(
            DesignSystemScope.Library(container.Id, orgId), ct);
        return created is null
            ? LibraryNotFound()
            : StatusCode(StatusCodes.Status201Created, ToSummary(library, created));
    }

    /// <summary>SPEC 16.1 <c>GET {libraryId}</c>: the library and its container summary.</summary>
    [HttpGet("{libraryId:guid}")]
    public async Task<ActionResult<LibrarySummaryResponse>> Get(Guid libraryId, CancellationToken ct)
    {
        if (OrgLibraryGate.Read(this, _currentUser, out Guid orgId) is { } denied) return denied;

        DesignSystemLibrary? library = await ResolveLibraryAsync(libraryId, orgId, ct);
        if (library is null) return LibraryNotFound();

        DesignSystemSummaryResponse? summary = await _designSystems.GetSummaryAsync(
            DesignSystemScope.Library(library.DesignSystemId, orgId), ct);
        return summary is null ? LibraryNotFound() : Ok(ToSummary(library, summary));
    }

    /// <summary>SPEC 16.1 <c>PATCH {libraryId}</c>: renames the library (the container name follows).</summary>
    [HttpPatch("{libraryId:guid}")]
    public async Task<ActionResult<LibrarySummaryResponse>> Rename(
        Guid libraryId, [FromBody] RenameLibraryRequest request, CancellationToken ct)
    {
        if (OrgLibraryGate.Write(this, _currentUser, out Guid orgId) is { } denied) return denied;

        DesignSystemLibrary? library = await ResolveLibraryAsync(libraryId, orgId, ct);
        if (library is null) return LibraryNotFound();

        string name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            return BadRequest(new { message = "name is required" });
        if (name.Length > MaxNameLength)
            return BadRequest(new { message = $"name must be at most {MaxNameLength} characters" });

        if (await _db.DesignSystemLibraries.AnyAsync(
                l => l.OrganizationId == orgId && l.Name == name && l.Id != library.Id, ct))
        {
            return NameTaken();
        }

        DateTime now = DateTime.UtcNow;
        library.Name = name;
        library.UpdatedAt = now;

        DesignSystem? container = await _db.DesignSystems.FirstOrDefaultAsync(s => s.Id == library.DesignSystemId, ct);
        if (container is not null)
        {
            container.Name = name;
            container.UpdatedAt = now;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost the race for the new name to a concurrent create/rename; the unique index is the arbiter.
            return NameTaken();
        }

        _logger.LogInformation(
            "design_system.library.rename {LibraryId} {UserId}", library.Id, _currentUser.UserId);

        DesignSystemSummaryResponse? summary = await _designSystems.GetSummaryAsync(
            DesignSystemScope.Library(library.DesignSystemId, orgId), ct);
        return summary is null ? LibraryNotFound() : Ok(ToSummary(library, summary));
    }

    /// <summary>
    /// SPEC 16.1 <c>DELETE {libraryId}?confirm=true</c>: in one transaction nulls every project's
    /// <c>parent_library_id</c> that points at this library, then removes the container row — whose
    /// FKs cascade the library row and its versions. The link is nulled explicitly rather than left to
    /// the database's <c>ON DELETE SET NULL</c>, which Sqlite test contexts may not enforce.
    /// </summary>
    [HttpDelete("{libraryId:guid}")]
    public async Task<IActionResult> Delete(
        Guid libraryId, [FromQuery] bool confirm, CancellationToken ct)
    {
        if (OrgLibraryGate.Write(this, _currentUser, out Guid orgId) is { } denied) return denied;

        if (!confirm)
            return BadRequest(new { message = "confirm=true is required to delete a library" });

        DesignSystemLibrary? library = await ResolveLibraryAsync(libraryId, orgId, ct);
        if (library is null) return LibraryNotFound();

        await using IDbContextTransaction transaction = await _db.Database.BeginTransactionAsync(ct);

        List<DesignSystem> linked = await _db.DesignSystems
            .Where(s => s.ParentLibraryId == libraryId)
            .ToListAsync(ct);
        foreach (DesignSystem row in linked)
            row.ParentLibraryId = null;
        if (linked.Count > 0)
            await _db.SaveChangesAsync(ct);

        DesignSystem? container = await _db.DesignSystems
            .FirstOrDefaultAsync(s => s.Id == library.DesignSystemId, ct);
        if (container is not null)
            _db.DesignSystems.Remove(container);

        // Sweep the asset objects before the rows go: the FK cascade removes the rows but the object
        // store has no foreign key, so an un-swept library would leave inert bytes behind.
        List<DesignSystemLibraryAsset> assets = await _db.DesignSystemLibraryAssets
            .Where(a => a.LibraryId == library.Id)
            .ToListAsync(ct);
        if (_libraryAssets is not null)
        {
            foreach (DesignSystemLibraryAsset asset in assets)
                await _libraryAssets.DeleteAsync(asset.StorageKey, ct);
        }

        if (assets.Count > 0)
            _db.DesignSystemLibraryAssets.RemoveRange(assets);
        _db.DesignSystemLibraries.Remove(library);

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        _logger.LogInformation(
            "design_system.library.delete {LibraryId} {DesignSystemId} {UserId}",
            library.Id, library.DesignSystemId, _currentUser.UserId);

        return NoContent();
    }

    /// <summary>SPEC 16.1 <c>GET {libraryId}/versions</c>: the container's versions, newest first.</summary>
    [HttpGet("{libraryId:guid}/versions")]
    public async Task<ActionResult<IReadOnlyList<DesignVersionSummaryResponse>>> ListVersions(
        Guid libraryId, CancellationToken ct)
    {
        if (OrgLibraryGate.Read(this, _currentUser, out Guid orgId) is { } denied) return denied;

        DesignSystemLibrary? library = await ResolveLibraryAsync(libraryId, orgId, ct);
        if (library is null) return LibraryNotFound();

        return Ok(await _designSystems.ListVersionsAsync(
            DesignSystemScope.Library(library.DesignSystemId, orgId), ct));
    }

    /// <summary>
    /// SPEC 16.1 <c>GET {libraryId}/versions/{versionId}</c>: the full version, but only when it
    /// belongs to THIS library's container; a version of another container is a 404.
    /// </summary>
    [HttpGet("{libraryId:guid}/versions/{versionId:guid}")]
    public async Task<ActionResult<DesignVersionResponse>> GetVersion(
        Guid libraryId, Guid versionId, CancellationToken ct)
    {
        if (OrgLibraryGate.Read(this, _currentUser, out Guid orgId) is { } denied) return denied;

        DesignSystemLibrary? library = await ResolveLibraryAsync(libraryId, orgId, ct);
        if (library is null) return LibraryNotFound();

        DesignVersionResponse? version = await _designSystems.GetVersionAsync(
            DesignSystemScope.Library(library.DesignSystemId, orgId), versionId, ct);
        return version is null ? NotFound() : Ok(version);
    }

    /// <summary>
    /// SPEC 16.1 <c>POST {libraryId}/versions</c>: a manual Draft through the shared service. The body's
    /// <c>kind</c> is ignored — a library is always <c>design</c>.
    /// </summary>
    [HttpPost("{libraryId:guid}/versions")]
    public async Task<ActionResult<DesignVersionResponse>> CreateVersion(
        Guid libraryId, [FromBody] CreateDesignVersionRequest request, CancellationToken ct)
    {
        if (OrgLibraryGate.Write(this, _currentUser, out Guid orgId) is { } denied) return denied;

        DesignSystemLibrary? library = await ResolveLibraryAsync(libraryId, orgId, ct);
        if (library is null) return LibraryNotFound();

        DesignSystemWriteResult result = await _designSystems.CreateDraftAsync(
            DesignSystemScope.Library(library.DesignSystemId, orgId),
            _currentUser.UserId,
            request.Document ?? new JsonObject(),
            DesignVersionOrigin.Manual,
            workflowExecutionId: null,
            coverageJson: null,
            baseVersionId: null,
            ct);

        if (result.Status == DesignSystemWriteStatus.Ok && result.Version is { } created)
        {
            _logger.LogInformation(
                "design_system.create {DesignSystemId} {VersionId} {UserId}",
                created.DesignSystemId, created.Id, _currentUser.UserId);
            return StatusCode(StatusCodes.Status201Created, created);
        }

        return MapWrite(result);
    }

    /// <summary>SPEC 9/16.1 <c>POST {libraryId}/versions/{versionId}/approve</c>.</summary>
    [HttpPost("{libraryId:guid}/versions/{versionId:guid}/approve")]
    public async Task<ActionResult<DesignVersionResponse>> Approve(
        Guid libraryId, Guid versionId, CancellationToken ct)
    {
        if (OrgLibraryGate.Write(this, _currentUser, out Guid orgId) is { } denied) return denied;

        DesignSystemLibrary? library = await ResolveLibraryAsync(libraryId, orgId, ct);
        if (library is null) return LibraryNotFound();

        DesignSystemWriteResult result = await _designSystems.ApproveAsync(
            DesignSystemScope.Library(library.DesignSystemId, orgId), versionId, _currentUser.UserId, ct);

        if (result.Status == DesignSystemWriteStatus.Ok && result.Version is { } approved)
        {
            _logger.LogInformation(
                "design_system.approve {DesignSystemId} {VersionId} {UserId}",
                approved.DesignSystemId, approved.Id, _currentUser.UserId);
        }

        return MapWrite(result);
    }

    /// <summary>SPEC 9/16.1 <c>POST {libraryId}/versions/{versionId}/reject</c>.</summary>
    [HttpPost("{libraryId:guid}/versions/{versionId:guid}/reject")]
    public async Task<ActionResult<DesignVersionResponse>> Reject(
        Guid libraryId, Guid versionId, [FromBody] ApproveRejectRequest? request, CancellationToken ct)
    {
        if (OrgLibraryGate.Write(this, _currentUser, out Guid orgId) is { } denied) return denied;

        DesignSystemLibrary? library = await ResolveLibraryAsync(libraryId, orgId, ct);
        if (library is null) return LibraryNotFound();

        DesignSystemWriteResult result = await _designSystems.RejectAsync(
            DesignSystemScope.Library(library.DesignSystemId, orgId), versionId, _currentUser.UserId, request?.Note, ct);

        if (result.Status == DesignSystemWriteStatus.Ok && result.Version is { } rejected)
        {
            _logger.LogInformation(
                "design_system.reject {DesignSystemId} {VersionId} {UserId}",
                rejected.DesignSystemId, rejected.Id, _currentUser.UserId);
        }

        return MapWrite(result);
    }

    /// <summary>SPEC 9/16.1 <c>POST {libraryId}/versions/{versionId}/restore</c>: a NEW Draft, never a mutation.</summary>
    [HttpPost("{libraryId:guid}/versions/{versionId:guid}/restore")]
    public async Task<ActionResult<DesignVersionResponse>> Restore(
        Guid libraryId, Guid versionId, CancellationToken ct)
    {
        if (OrgLibraryGate.Write(this, _currentUser, out Guid orgId) is { } denied) return denied;

        DesignSystemLibrary? library = await ResolveLibraryAsync(libraryId, orgId, ct);
        if (library is null) return LibraryNotFound();

        DesignSystemWriteResult result = await _designSystems.RestoreAsync(
            DesignSystemScope.Library(library.DesignSystemId, orgId), versionId, _currentUser.UserId, ct);

        if (result.Status == DesignSystemWriteStatus.Ok && result.Version is { } restored)
        {
            _logger.LogInformation(
                "design_system.restore {DesignSystemId} {VersionId} {UserId}",
                restored.DesignSystemId, restored.Id, _currentUser.UserId);
            return Ok(restored);
        }

        return MapWrite(result);
    }

    /// <summary>
    /// SPEC 16.1 <c>GET {libraryId}/export?format=json</c>: the current Approved version's canonical
    /// document as a download. P4-07/P4-08 add the other formats and the project route; here only the
    /// gating and the <c>json</c> body exist.
    /// </summary>
    [HttpGet("{libraryId:guid}/export")]
    public async Task<IActionResult> Export(
        Guid libraryId, [FromQuery] string? format, CancellationToken ct)
    {
        if (OrgLibraryGate.Read(this, _currentUser, out Guid orgId) is { } denied) return denied;

        DesignSystemLibrary? library = await ResolveLibraryAsync(libraryId, orgId, ct);
        if (library is null) return LibraryNotFound();

        if (!string.IsNullOrEmpty(format) && !string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { code = "UNSUPPORTED_FORMAT", message = "only format=json is supported" });

        DesignSystemVersion? approved = await _designSystems.GetCurrentApprovedAsync(
            DesignSystemScope.Library(library.DesignSystemId, orgId), ct);
        if (approved is null)
            return NotFound(new { code = "NO_APPROVED_VERSION", message = "the library has no approved version" });

        string fileName = $"{SafeFileName(library.Name)}.json";
        SafeContentPolicy.ApplySecurityHeaders(Response);
        Response.Headers["Content-Disposition"] = SafeContentPolicy.BuildContentDisposition(false, fileName);
        return File(Encoding.UTF8.GetBytes(approved.DocumentJson), "application/json", fileName);
    }

    /// <summary>
    /// T-010 <c>POST {libraryId}/assets</c>: upload one inert library asset. Owner/Admin only. The
    /// library is resolved against the caller's active organization first (foreign/missing is 404);
    /// the bytes are then classified by MAGIC, never by the declared type or the name, and only an SVG
    /// the sanitizer rebuilds or one of the four font containers is stored. An SVG is stored sanitized,
    /// the digest is computed server-side, and the name is sanitized and bounded.
    /// </summary>
    [HttpPost("{libraryId:guid}/assets")]
    [RequestSizeLimit(LibraryAssetLimits.FontMaxBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = LibraryAssetLimits.FontMaxBytes)]
    public async Task<ActionResult<LibraryAssetUploadResponse>> Upload(
        Guid libraryId, [FromForm] IFormFile? file, CancellationToken ct)
    {
        if (OrgLibraryGate.Write(this, _currentUser, out Guid orgId) is { } denied) return denied;

        DesignSystemLibrary? library = await ResolveLibraryAsync(libraryId, orgId, ct);
        if (library is null) return LibraryNotFound();

        if (_libraryAssets is null)
            return AssetFailure(StatusCodes.Status503ServiceUnavailable, "LIBRARY_ASSET_STORAGE_UNAVAILABLE");

        if (file is null || file.Length == 0)
            return AssetFailure(StatusCodes.Status400BadRequest, "LIBRARY_ASSET_MISSING");

        if (file.Length > LibraryAssetLimits.FontMaxBytes)
            return AssetFailure(StatusCodes.Status413PayloadTooLarge, "LIBRARY_ASSET_TOO_LARGE");

        int count = await _db.DesignSystemLibraryAssets.CountAsync(a => a.LibraryId == library.Id, ct);
        if (count >= LibraryAssetLimits.MaxAssets)
            return AssetFailure(StatusCodes.Status409Conflict, "LIBRARY_ASSET_LIMIT");

        byte[] raw;
        await using (Stream stream = file.OpenReadStream())
            raw = await ReadBoundedAsync(stream, LibraryAssetLimits.FontMaxBytes, ct);

        (byte[] stored, string mimeType)? prepared = PrepareAsset(raw);
        if (prepared is not { } assetBytes)
        {
            if (MagicSniffer.Sniff(raw) == MagicKind.Svg)
            {
                return raw.LongLength > LibraryAssetLimits.SvgMaxBytes
                    ? AssetFailure(StatusCodes.Status413PayloadTooLarge, "LIBRARY_ASSET_TOO_LARGE")
                    : AssetFailure(StatusCodes.Status422UnprocessableEntity, "LIBRARY_ASSET_UNSAFE");
            }

            return AssetFailure(StatusCodes.Status415UnsupportedMediaType, "LIBRARY_ASSET_TYPE");
        }

        // The stored object key must stay one segment below the asset: take the basename, then sanitize it.
        string? name = DesignText.Sanitize(SingleKeySegment(file.FileName), MaxAssetNameLength);
        if (name is null)
            return AssetFailure(StatusCodes.Status400BadRequest, "LIBRARY_ASSET_NAME");

        if (await _db.DesignSystemLibraryAssets.AnyAsync(
                a => a.LibraryId == library.Id && a.Name == name, ct))
        {
            return AssetFailure(StatusCodes.Status409Conflict, "LIBRARY_ASSET_NAME_TAKEN");
        }

        // Asset bytes are storage the organization owns: the same plan quota as project files applies.
        if (_planGate is not null)
        {
            PlanGateViolation? violation = await _planGate.CheckUploadAsync(orgId, assetBytes.stored.LongLength, ct);
            if (violation is not null)
                return violation.ToResult();
        }

        Guid assetId = Guid.NewGuid();
        string storageKey;
        await using (var content = new MemoryStream(assetBytes.stored, writable: false))
        {
            storageKey = await _libraryAssets.UploadAsync(
                orgId, library.Id, assetId, content, name, assetBytes.mimeType, ct);
        }

        var row = new DesignSystemLibraryAsset
        {
            Id = assetId,
            LibraryId = library.Id,
            Name = name,
            MimeType = assetBytes.mimeType,
            SizeBytes = assetBytes.stored.LongLength,
            Sha256 = Sha256Hex(assetBytes.stored),
            StorageKey = storageKey,
            CreatedByUserId = _currentUser.UserId,
            CreatedAt = DateTime.UtcNow,
        };

        _db.DesignSystemLibraryAssets.Add(row);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A racing upload took the name; the unique index is the arbiter. The object is orphaned
            // and the retention sweep removes it.
            await _libraryAssets.DeleteAsync(storageKey, ct);
            return AssetFailure(StatusCodes.Status409Conflict, "LIBRARY_ASSET_NAME_TAKEN");
        }

        _logger.LogInformation(
            "design_system.library.asset.upload {LibraryId} {AssetId} {OrganizationId} {UserId}",
            library.Id, row.Id, orgId, _currentUser.UserId);

        return StatusCode(
            StatusCodes.Status201Created,
            new LibraryAssetUploadResponse(StatusCodes.Status201Created, ToAssetResponse(row), null));
    }

    /// <summary>T-010 <c>GET {libraryId}/assets</c>: every asset of the library, by name. Any member may read.</summary>
    [HttpGet("{libraryId:guid}/assets")]
    public async Task<ActionResult<IReadOnlyList<LibraryAssetResponse>>> ListAssets(
        Guid libraryId, CancellationToken ct)
    {
        if (OrgLibraryGate.Read(this, _currentUser, out Guid orgId) is { } denied) return denied;

        DesignSystemLibrary? library = await ResolveLibraryAsync(libraryId, orgId, ct);
        if (library is null) return LibraryNotFound();

        List<DesignSystemLibraryAsset> assets = await _db.DesignSystemLibraryAssets
            .AsNoTracking()
            .Where(a => a.LibraryId == library.Id)
            .OrderBy(a => a.Name)
            .ToListAsync(ct);

        return Ok(assets.Select(ToAssetResponse).ToList());
    }

    /// <summary>
    /// T-010 <c>DELETE {libraryId}/assets/{assetId}</c>: Owner/Admin. The asset is looked up INSIDE the
    /// resolved library, so a foreign or missing id is a 404; its object is removed with the row.
    /// </summary>
    [HttpDelete("{libraryId:guid}/assets/{assetId:guid}")]
    public async Task<IActionResult> DeleteAsset(Guid libraryId, Guid assetId, CancellationToken ct)
    {
        if (OrgLibraryGate.Write(this, _currentUser, out Guid orgId) is { } denied) return denied;

        DesignSystemLibrary? library = await ResolveLibraryAsync(libraryId, orgId, ct);
        if (library is null) return LibraryNotFound();

        DesignSystemLibraryAsset? asset = await _db.DesignSystemLibraryAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.LibraryId == library.Id, ct);
        if (asset is null) return NotFound();

        if (_libraryAssets is not null)
            await _libraryAssets.DeleteAsync(asset.StorageKey, ct);

        _db.DesignSystemLibraryAssets.Remove(asset);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "design_system.library.asset.delete {LibraryId} {AssetId} {UserId}",
            library.Id, assetId, _currentUser.UserId);

        return NoContent();
    }

    /// <summary>
    /// T-010 <c>GET {libraryId}/assets/{assetId}/content</c>: any member. An SVG is rebuilt by the
    /// sanitizer AGAIN on read and leaves inert under a sandbox CSP; a font leaves as an opaque
    /// <c>application/octet-stream</c> attachment. A stored type outside those is a 404.
    /// </summary>
    [HttpGet("{libraryId:guid}/assets/{assetId:guid}/content")]
    public async Task<IActionResult> GetAssetContent(Guid libraryId, Guid assetId, CancellationToken ct)
    {
        if (OrgLibraryGate.Read(this, _currentUser, out Guid orgId) is { } denied) return denied;

        DesignSystemLibrary? library = await ResolveLibraryAsync(libraryId, orgId, ct);
        if (library is null) return LibraryNotFound();

        DesignSystemLibraryAsset? asset = await _db.DesignSystemLibraryAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == assetId && a.LibraryId == library.Id, ct);
        if (asset is null || _libraryAssets is null) return NotFound();

        // Defence in depth: the key is server-built, but a corrupted row must never open another
        // library's (or another organization's) object.
        if (!asset.StorageKey.StartsWith(LibraryAssetKeyPrefix(orgId, library.Id), StringComparison.Ordinal))
            return NotFound();

        await using Stream? stored = await _libraryAssets.OpenReadAsync(asset.StorageKey, ct);
        if (stored is null) return NotFound();

        byte[] bytes = await ReadBoundedAsync(stored, LibraryAssetLimits.FontMaxBytes, ct);
        if (bytes.LongLength > LibraryAssetLimits.FontMaxBytes) return NotFound();

        if (string.Equals(asset.MimeType, "image/svg+xml", StringComparison.Ordinal))
        {
            SvgSanitizeResult sanitized = SvgSanitizer.Sanitize(bytes);
            if (!sanitized.Ok || sanitized.Sanitized is null) return NotFound();

            SafeContentPolicy.ApplySecurityHeaders(Response);
            Response.Headers["Content-Security-Policy"] = SvgContentSecurityPolicy;
            Response.Headers["Content-Disposition"] =
                SafeContentPolicy.BuildContentDisposition(inline: true, asset.Name);
            return File(Encoding.UTF8.GetBytes(sanitized.Sanitized), "image/svg+xml; charset=utf-8");
        }

        if (asset.MimeType is "font/woff" or "font/woff2" or "font/ttf" or "font/otf")
        {
            // The stored font MIME is a hint; the shared policy decides what leaves the origin. A font
            // is not on the inline allowlist, so this is application/octet-stream as an attachment.
            ServedContent served = SafeContentPolicy.Decide(asset.MimeType, asset.Name);
            SafeContentPolicy.ApplySecurityHeaders(Response);
            Response.Headers["Content-Disposition"] =
                SafeContentPolicy.BuildContentDisposition(inline: false, asset.Name);
            return File(bytes, served.ContentType);
        }

        return NotFound();
    }

    /// <summary>
    /// Classifies the uploaded bytes by MAGIC only. An SVG is rebuilt by the sanitizer, so the stored
    /// bytes are the rebuilt document: disallowed elements and attributes (a <c>&lt;script&gt;</c>, a
    /// C2PA <c>&lt;metadata&gt;</c> block) are dropped while the visual paths survive, exactly as on
    /// the importer path. It refuses only when the sanitizer cannot produce a document at all. Null
    /// means "refused": the caller maps it to 422 for an SVG and 415 for anything else.
    /// </summary>
    private static (byte[] Stored, string MimeType)? PrepareAsset(byte[] raw)
    {
        switch (MagicSniffer.Sniff(raw))
        {
            case MagicKind.Svg:
                if (raw.LongLength > LibraryAssetLimits.SvgMaxBytes)
                    return null;

                SvgSanitizeResult result = SvgSanitizer.Sanitize(raw);
                if (!result.Ok || result.Sanitized is null)
                    return null;

                return (Encoding.UTF8.GetBytes(result.Sanitized), "image/svg+xml");

            case MagicKind.Woff when raw.LongLength <= LibraryAssetLimits.FontMaxBytes:
                return (raw, "font/woff");
            case MagicKind.Woff2 when raw.LongLength <= LibraryAssetLimits.FontMaxBytes:
                return (raw, "font/woff2");
            case MagicKind.TrueType when raw.LongLength <= LibraryAssetLimits.FontMaxBytes:
                return (raw, "font/ttf");
            case MagicKind.OpenType when raw.LongLength <= LibraryAssetLimits.FontMaxBytes:
                return (raw, "font/otf");

            default:
                return null;
        }
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>The only key prefix a library's asset may live under.</summary>
    private static string LibraryAssetKeyPrefix(Guid organizationId, Guid libraryId) =>
        $"design-libraries/{organizationId}/{libraryId}/assets/";

    /// <summary>The last path segment of an uploaded name, so the stored key stays a single segment.</summary>
    private static string SingleKeySegment(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return string.Empty;

        int separator = fileName.LastIndexOfAny(['/', '\\']);
        return separator >= 0 ? fileName[(separator + 1)..] : fileName;
    }

    private static LibraryAssetResponse ToAssetResponse(DesignSystemLibraryAsset asset) =>
        new(asset.Id, asset.Name, asset.MimeType, asset.SizeBytes, asset.Sha256,
            asset.Role, asset.Variant, asset.CreatedAt);

    private ActionResult<LibraryAssetUploadResponse> AssetFailure(int status, string code) =>
        StatusCode(status, new LibraryAssetUploadResponse(status, null, code));

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

    /// <summary>The library row, scoped to the caller's active organization — never a caller-chosen design_systems id.</summary>
    private Task<DesignSystemLibrary?> ResolveLibraryAsync(Guid libraryId, Guid orgId, CancellationToken ct) =>
        _db.DesignSystemLibraries.FirstOrDefaultAsync(l => l.Id == libraryId && l.OrganizationId == orgId, ct);

    private static LibrarySummaryResponse ToSummary(DesignSystemLibrary library, DesignSystemSummaryResponse summary) =>
        new(library.Id, library.OrganizationId, library.Name, library.DesignSystemId, summary);

    /// <summary>
    /// True for a unique-index violation (Postgres 23505, or SQLite's UNIQUE failure in tests). Every other
    /// unique index on these inserts is on a freshly generated id, so a violation here is the name.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation }
        || (ex.InnerException?.GetType().Name == "SqliteException"
            && ex.InnerException.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase));

    private ConflictObjectResult NameTaken() => Conflict(new
    {
        code = "library_name_taken",
        message = "another design-system library in this organization already has that name",
    });

    private NotFoundObjectResult LibraryNotFound() =>
        NotFound(new { code = LibraryNotFoundCode, message = "design-system library not found" });

    private static IEnumerable<object> Errors(DesignValidationResult validation) =>
        validation.Violations.Select(v => new { code = v.Code, path = v.Path, message = v.Message });

    /// <summary>
    /// The same write-result mapping as <see cref="DesignSystemsController"/>, so the two surfaces
    /// never answer a version-state problem differently.
    /// </summary>
    private ActionResult MapWrite(DesignSystemWriteResult result) => result.Status switch
    {
        DesignSystemWriteStatus.Ok when result.Version is { } version => Ok(version),

        DesignSystemWriteStatus.NotFound => LibraryNotFound(),

        DesignSystemWriteStatus.NotDraft =>
            Conflict(new { code = "NOT_DRAFT", message = "the version is not a Draft" }),

        DesignSystemWriteStatus.StaleBase =>
            Conflict(new
            {
                code = "STALE_BASE",
                message = "the version was reviewed against a version that is no longer current",
            }),

        DesignSystemWriteStatus.ValidationFailed => BadRequest(new
        {
            message = "Invalid design-system document",
            errors = (result.Violations ?? [])
                .Select(v => new { code = v.Code, path = v.Path, message = v.Message }),
        }),

        _ => StatusCode(StatusCodes.Status500InternalServerError),
    };

    private static string SafeFileName(string name)
    {
        string sanitized = SafeFileNameChars.Replace(name, "_").Trim('_', '.');
        return sanitized.Length == 0 ? "design-library" : sanitized;
    }
}

/// <summary>The library rename body (SPEC 16.1). Controller-local, like the project DTOs.</summary>
public sealed record RenameLibraryRequest(string Name);

/// <summary>One library-owned asset without its bytes (T-010).</summary>
public sealed record LibraryAssetResponse(
    Guid Id, string Name, string MimeType, long SizeBytes, string Sha256,
    string? Role, string? Variant, DateTime CreatedAt);

/// <summary>
/// The upload outcome. The HTTP status is authoritative and <see cref="Status"/> repeats it for
/// callers that read the body; <see cref="Code"/> is one of the stable <c>LIBRARY_ASSET_*</c> codes
/// on refusal and null on success.
/// </summary>
public sealed record LibraryAssetUploadResponse(int Status, LibraryAssetResponse? Asset, string? Code);

/// <summary>
/// The org library authority check, mirroring <see cref="OrgAdminGate"/> but split by action because
/// reads and writes differ (SPEC 16.1): Billing is refused everywhere; Member may read; only Owner and
/// Admin may write. Every call re-reads <see cref="ICurrentUser.OrgRole"/> — never memoized — and
/// never consults <see cref="ICurrentUser.IsPlatformAdmin"/>, so a platform admin whose active role
/// is Member is refused like any other Member.
/// </summary>
internal static class OrgLibraryGate
{
    /// <summary>Null when the caller may read; otherwise the refusal. No org/role is 404, Billing 403.</summary>
    public static ActionResult? Read(ControllerBase controller, ICurrentUser user, out Guid orgId)
    {
        orgId = Guid.Empty;
        if (user.OrganizationId is not { } activeOrgId || user.OrgRole is null)
            return controller.NotFound();

        if (user.OrgRole is OrganizationRole.Billing)
            return controller.Forbid();

        orgId = activeOrgId;
        return null;
    }

    /// <summary>Null when the caller may write (Owner/Admin); otherwise the refusal. Billing and Member 403.</summary>
    public static ActionResult? Write(ControllerBase controller, ICurrentUser user, out Guid orgId)
    {
        orgId = Guid.Empty;
        if (user.OrganizationId is not { } activeOrgId || user.OrgRole is null)
            return controller.NotFound();

        if (!OrgAuthority.IsOrganizationAdmin(user.OrgRole))
            return controller.Forbid();

        orgId = activeOrgId;
        return null;
    }
}
