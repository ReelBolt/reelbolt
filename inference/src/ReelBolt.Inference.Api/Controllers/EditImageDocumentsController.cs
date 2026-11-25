using System.Text.Json;
using Amazon.S3;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Graphics;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Observability;
using ReelBolt.Shared.Storage;
using static ReelBolt.Inference.Api.Controllers.EditImageDocumentSupport;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The image editor's document API: a source image decomposed into ordered layers, each of which may
/// be a SUBJECT CUT OUT of that image, a vendor output, a vector text layer or a placed project image.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what makes "select a subject and make a layer out of it" real.</b> A layer carries its
/// own pixels — an RGBA cut-out with an alpha matte for a subject, or the whole image for the
/// backdrop — plus a role, a z-order, an opacity and a transform. The reveal the editor animates reads
/// exactly those fields, so a composite shows a person on transparency moving against their
/// background rather than two rectangles stacked.
/// </para>
/// <para>
/// <b>Three controllers share one route prefix.</b> This one owns documents, layers, subject detection
/// and the layer pixels; <see cref="EditImageOperationsController"/> owns the synchronous AI operations
/// (docs/design/thumbnail-editor.md section 5) and <see cref="EditImageAssetsController"/> owns the
/// presigned uploads and exports. They are split by concern and by size, not by trust: each is in the
/// reviewed set of project-route controllers and every action in each of them performs its own access
/// check. The full contract is docs/design/thumbnail-editor.md.
/// </para>
/// <para>
/// <b>Why this is not <c>EditTimelinesController</c>.</b> A timeline is a TIME-based document with a
/// pinned schema version and a hand-written TypeScript mirror; this is a LAYER document. Overloading
/// one with the other would have put two incompatible shapes behind one version number. See
/// <see cref="EditImageDocument"/>.
/// </para>
/// <para>
/// <b>Ownership is the outer gate, project scoping is the inner one.</b> Every endpoint resolves the
/// project through <see cref="IProjectAccessService"/>, so a project that does not exist, is in
/// another organization or is invisible to the caller's role is <c>404</c>/<c>403</c> before anything
/// else happens. Every <c>{documentId}</c> and <c>{layerId}</c> lookup then ALSO constrains the
/// parent, so a document in another project, soft-deleted or nonexistent is <c>404</c> in all three
/// cases and the body never distinguishes them.
/// </para>
/// <para>
/// <b>Segmentation leaves the host, deliberately and visibly.</b> The cut-out is produced by a hosted
/// vendor, which means the source image is sent to it. The transport is a short-lived presigned GET
/// the vendor fetches itself — capped by <see cref="SourceUrlTtl"/> — rather than a public URL, and
/// the vendor's data-handling position is disclosed in <c>site/lib/provider-data-handling.ts</c>.
/// Resolving no provider is a clean <c>409</c> the UI can explain, never a failed request.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/image-documents")]
[Authorize]
public class EditImageDocumentsController : ControllerBase
{
    /// <summary>
    /// How long the vendor's link to the source image stays valid.
    /// </summary>
    /// <remarks>
    /// Long enough for a queue wait plus a fetch, short enough that a URL leaked into a vendor's logs
    /// is worthless almost immediately. A queued job can wait a while, so this is minutes rather than
    /// seconds; <see cref="IPresignedUrlService.CreateGetAsync"/> caps it at 15 minutes regardless.
    /// </remarks>
    public static readonly TimeSpan SourceUrlTtl = TimeSpan.FromMinutes(15);

    /// <summary>The largest side a browser-measured photo size on create may state.</summary>
    public const int MaxClientMeasuredSide = 65535;

    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly IFileStorageService _storage;
    private readonly ReelBolt.Inference.Api.Services.Moderation.PublishModeration? _publishModeration;
    private readonly IPresignedUrlService? _presignedUrls;
    private readonly IInferenceProviderResolver _providerResolver;
    private readonly IImageAnalysisClientFactory _analysisClients;
    private readonly ILogger<EditImageDocumentsController> _logger;
    private readonly INonWorkflowChargeService? _charges;
    private readonly IPlanGateService? _planGate;
    private readonly IConfiguration? _configuration;
    /// <summary>The vendor segmentation wait is bounded like operations (nginx/Cloudflare cut at 60-100 s).</summary>
    public static readonly TimeSpan DefaultSegmentationTimeout = TimeSpan.FromSeconds(90);
    private readonly TimeSpan _segmentationTimeout;
    private readonly ReelBolt.Inference.Api.Services.Uploads.OrganizationStorageLock _storageLock;

    public EditImageDocumentsController(
        InferenceApiDbContext db,
        IProjectAccessService projectAccess,
        IFileStorageService storage,
        IInferenceProviderResolver providerResolver,
        IImageAnalysisClientFactory analysisClients,
        IPresignedUrlService? presignedUrls = null,
        ILogger<EditImageDocumentsController>? logger = null,
        INonWorkflowChargeService? charges = null,
        IPlanGateService? planGate = null,
        ReelBolt.Inference.Api.Services.Uploads.OrganizationStorageLock? storageLock = null,
        IConfiguration? configuration = null,
        ReelBolt.Inference.Api.Services.Moderation.PublishModeration? publishModeration = null,
        TimeSpan? segmentationTimeout = null)
    {
        _segmentationTimeout = segmentationTimeout ?? DefaultSegmentationTimeout;
        _publishModeration = publishModeration;
        _configuration = configuration;
        _planGate = planGate;
        _storageLock = storageLock ?? new ReelBolt.Inference.Api.Services.Uploads.OrganizationStorageLock();
        _charges = charges;
        _db = db;
        _projectAccess = projectAccess;
        _storage = storage;
        _providerResolver = providerResolver;
        _analysisClients = analysisClients;
        _presignedUrls = presignedUrls;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EditImageDocumentsController>.Instance;
    }

    // ------------------------------------------------------------------ documents

    /// <summary>Every document in the project, newest first, without their layers.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<EditImageDocumentSummaryDto>>> List(
        Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        List<EditImageDocumentSummaryDto> summaries = await _db.EditImageDocuments
            .Where(d => d.ProjectId == projectId)
            .OrderByDescending(d => d.UpdatedAt)
            .ThenBy(d => d.Id)
            .Select(d => new EditImageDocumentSummaryDto(
                d.Id, d.Name, d.SourceProjectFileId, d.Revision, d.Layers.Count, d.CreatedAt, d.UpdatedAt))
            .ToListAsync(ct);

        return Ok(summaries);
    }

    /// <summary>
    /// Creates a document from one of the project's images, with that image as its backdrop layer.
    /// </summary>
    /// <remarks>
    /// The backdrop is created eagerly rather than left to the client: a document whose only layer is
    /// a cut-out composites a subject on nothing, and every caller would have to remember to add the
    /// source before the result looked like an edit.
    /// </remarks>
    [HttpPost]
    public async Task<ActionResult<EditImageDocumentDto>> Create(
        Guid projectId, [FromBody] CreateEditImageDocumentRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (request.SourceProjectFileId is null)
        {
            if (request.Width is null || request.Height is null) return NotFound();
            const int maxBlank = PictureSizes.MaxNewCanvasLongSide;
            if (request.Width is < MinCanvasSide or > maxBlank || request.Height is < MinCanvasSide or > maxBlank)
            {
                return BadRequest(new
                {
                    message = $"A new picture's width and height must each be between {MinCanvasSide} and {maxBlank}."
                });
            }
            string? blankBackground = request.BackgroundColor?.Trim();
            if (!string.IsNullOrEmpty(blankBackground) && !IsCssColour(blankBackground))
                return BadRequest(new { message = "backgroundColor must be a CSS colour of at most 64 characters." });
            DateTime created = DateTime.UtcNow;
            EditImageDocument blank = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                SourceProjectFileId = null,
                Name = string.IsNullOrWhiteSpace(request.Name) ? "New picture" : request.Name.Trim(),
                CanvasWidth = request.Width,
                CanvasHeight = request.Height,
                BackgroundColor = string.IsNullOrEmpty(blankBackground) ? null : blankBackground,
                Revision = 1,
                CreatedAt = created,
                UpdatedAt = created
            };
            _db.EditImageDocuments.Add(blank);
            await _db.SaveChangesAsync(ct);
            return Ok(ToDto(blank, projectId));
        }
        if ((request.Width is null) != (request.Height is null)
            || request.Width is < 1 or > MaxClientMeasuredSide
            || request.Height is < 1 or > MaxClientMeasuredSide)
        {
            return BadRequest(new
            {
                message = $"The photo's width and height go together, as whole numbers from 1 to {MaxClientMeasuredSide}."
            });
        }

        ProjectFile? source = await _db.ProjectFiles
            .FirstOrDefaultAsync(f => f.Id == request.SourceProjectFileId.Value && f.ProjectId == projectId
                                      && f.CsamStatus != CsamScanStatus.Quarantined, ct);

        if (source is null) return NotFound();
        if (await Services.Outputs.OutputGate.IsFileHeldAsync(_db, source, ct)) return Services.Outputs.OutputGate.Unavailable();

        if (!IsImage(source.MimeType))
        {
            return Conflict(new
            {
                code = UnsupportedSourceCode,
                message = $"'{source.OriginalFileName}' is {source.MimeType}, not an image, so it cannot be a layer document's source."
            });
        }
        // The picture serves the source's bytes through its own routes: an output shared with this file needs a current verdict.
        if (_publishModeration is not null
            && await _publishModeration.GateStoredCopyAsync(projectId, source.StorageKey, ct) is { } sourceRefused)
            return sourceRefused.ToRefusal(this);

        // The picture opens at the photo's own size (W0.8 T1): measured from the stored header (EXIF
        // orientation applied, nothing decoded), else the size the browser measured, else no size at all
        // (null canvas, "the source image's size"). A photo whose long side exceeds 4096 opens scaled down
        // with its aspect kept, and the backdrop layer is scaled to fill that canvas exactly.
        (int Width, int Height)? photo =
            await PictureSizes.MeasureDisplaySizeAsync(_storage, projectId, source.StorageKey, ct)
            ?? (request.Width is { } cw && request.Height is { } chh ? (cw, chh) : null);
        (int CanvasWidth, int CanvasHeight, double Scale)? fit =
            photo is { } p ? PictureSizes.FitNewCanvas(p.Width, p.Height) : null;

        DateTime now = DateTime.UtcNow;
        EditImageDocument document = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            SourceProjectFileId = source.Id,
            Name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(),
            CanvasWidth = fit?.CanvasWidth,
            CanvasHeight = fit?.CanvasHeight,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        document.Layers.Add(new EditImageLayer
        {
            Id = Guid.NewGuid(),
            DocumentId = document.Id,
            ZOrder = 1,
            Role = EditImageLayerRoles.Background,
            Kind = EditImageLayerKind.Source,
            Label = source.OriginalFileName,
            SourceProjectFileId = source.Id,
            StorageKey = source.StorageKey,
            StorageBucket = _storage.BucketName,
            MimeType = source.MimeType,
            // The photo's measured size (header, else the browser's measurement). When neither is known
            // the layer stays without one and the client reads it off the image: a guessed pair would be
            // worse than null, because the editor positions layers in canvas pixels.
            Width = photo?.Width,
            Height = photo?.Height,
            Opacity = 1,
            TransformJson = WriteTransform(new EditImageLayerTransformDto(Scale: fit?.Scale ?? 1)),
            CreatedAt = now
        });

        _db.EditImageDocuments.Add(document);
        await _db.SaveChangesAsync(ct);

        return Ok(ToDto(document, projectId));
    }

    /// <summary>One document, with its layers in composite order.</summary>
    [HttpGet("{documentId:guid}")]
    public async Task<ActionResult<EditImageDocumentDto>> Get(
        Guid projectId, Guid documentId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        EditImageDocument? document = await LoadDocumentAsync(_db, projectId, documentId, ct);
        if (document is null) return NotFound();

        return Ok(ToDto(document, projectId));
    }

    /// <summary>
    /// Renames the document or changes its canvas size and background colour.
    /// </summary>
    /// <remarks>
    /// An absent field is left alone, so "clear" needs an explicit spelling (the JSON binder cannot tell
    /// an omitted property from a <c>null</c> one): an empty <c>name</c> or <c>backgroundColor</c> clears
    /// it, and <c>canvasWidth</c>/<c>canvasHeight</c> of <c>0</c>/<c>0</c> restore "the source image's
    /// size". The two canvas sides travel together — a width without a height has no meaning.
    /// </remarks>
    [HttpPatch("{documentId:guid}")]
    public async Task<ActionResult<EditImageDocumentDto>> UpdateDocument(
        Guid projectId, Guid documentId, [FromBody] UpdateEditImageDocumentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        EditImageDocument? document = await LoadDocumentAsync(_db, projectId, documentId, ct);
        if (document is null) return NotFound();

        // Checked BEFORE anything is read from the body further, so a stale writer returns 409 and
        // changes nothing.
        if (request.BaseRevision is { } baseRevision && baseRevision != document.Revision)
            return RevisionConflict(document.Revision, baseRevision);

        if (request.Name is { } name && name.Trim().Length > 200)
            return BadRequest(new { message = "name must be at most 200 characters." });

        bool setCanvas = false;
        bool clearCanvas = false;
        if (request.CanvasWidth is not null || request.CanvasHeight is not null)
        {
            if (request.CanvasWidth is not { } width || request.CanvasHeight is not { } height)
                return BadRequest(new { message = "canvasWidth and canvasHeight must be sent together." });

            if (width == 0 && height == 0)
            {
                clearCanvas = true;
            }
            else if (width is < MinCanvasSide or > MaxCanvasSide || height is < MinCanvasSide or > MaxCanvasSide)
            {
                return BadRequest(new
                {
                    message = $"canvasWidth and canvasHeight must each be between {MinCanvasSide} and {MaxCanvasSide} (or both 0 to clear)."
                });
            }
            else
            {
                setCanvas = true;
            }
        }

        string? background = request.BackgroundColor?.Trim();
        if (!string.IsNullOrEmpty(background) && !IsCssColour(background))
            return BadRequest(new { message = "backgroundColor must be a CSS colour of at most 64 characters." });

        if (request.Name is { } newName) document.Name = string.IsNullOrWhiteSpace(newName) ? null : newName.Trim();
        if (setCanvas)
        {
            document.CanvasWidth = request.CanvasWidth;
            document.CanvasHeight = request.CanvasHeight;
        }
        else if (clearCanvas)
        {
            document.CanvasWidth = null;
            document.CanvasHeight = null;
        }

        if (background is not null) document.BackgroundColor = background.Length == 0 ? null : background;

        document.Revision += 1;
        document.UpdatedAt = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Lost a race with another writer between the revision check and the save: the same
            // answer as a stale baseRevision, and nothing was written.
            return RevisionConflict(document.Revision, request.BaseRevision);
        }

        return Ok(ToDto(document, projectId));
    }
    /// <summary>
    /// Duplicates a picture: "Copy of &lt;name&gt;", the same canvas, background and layers in z-order with new
    /// ids. Every layer image (and matte) is COPIED to a new object under the new document's editor prefix,
    /// so deleting either picture never breaks the other; the copies count toward the plan's storage
    /// (409 <c>PICTURE_COPIES_OVER_QUOTA</c> when they do not fit). Exports are not copied. 201 with the
    /// same body as <c>GET {id}</c>; a foreign or missing picture is 404.
    /// </summary>
    [HttpPost("{documentId:guid}/duplicate")]
    public async Task<ActionResult<EditImageDocumentDto>> Duplicate(
        Guid projectId, Guid documentId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        EditImageDocument? source = await LoadDocumentAsync(_db, projectId, documentId, ct, tracked: false);
        if (source is null) return NotFound();

        List<EditImageLayer> sourceLayers = [.. source.Layers.Where(l => l.DeletedAt is null).OrderBy(l => l.ZOrder).ThenBy(l => l.Id)];
        // A copy would carry the bytes under a new, later stamp: a picture with an unserved layer is not copied.
        foreach (EditImageLayer heldLayer in sourceLayers)
            if (await Services.Outputs.OutputGate.IsPictureLayerHeldAsync(_db, projectId, heldLayer, ct)) return Services.Outputs.OutputGate.Unavailable();
        List<Guid> originFiles = [.. sourceLayers.Where(l => l.SourceProjectFileId is not null).Select(l => l.SourceProjectFileId!.Value)];
        if (source.SourceProjectFileId is { } docFile) originFiles.Add(docFile);
        if (originFiles.Count > 0 && await _db.ProjectFiles.AnyAsync(
                f => originFiles.Contains(f.Id) && f.ProjectId == projectId
                     && (f.CsamStatus == CsamScanStatus.Quarantined || f.ModerationStatus == FileModerationStatus.Quarantined), ct))
        {
            return NotFound();
        }

        Guid newId = Guid.NewGuid();
        string prefix = EditorPrefix(projectId, newId);

        // Sizes first (reads only), so a refusal copies nothing.
        Dictionary<string, long?> sizes = new(StringComparer.Ordinal);
        foreach (string key in sourceLayers.Where(l => l.Kind != EditImageLayerKind.Text)
                     .SelectMany(l => new[] { l.StorageKey, l.MatteStorageKey })
                     .Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k!).Distinct(StringComparer.Ordinal))
        {
            if (_publishModeration is not null
                && await _publishModeration.GateStoredCopyAsync(projectId, key, ct) is { } blocked)
                return blocked.ToRefusal(this);
            sizes[key] = (await _storage.GetMetadataAsync(projectId, key, ct))?.SizeBytes;
        }

        Guid? organizationId = access.Project?.OrganizationId;
        await using ReelBolt.Inference.Api.Services.Uploads.OrganizationStorageGuard guard = organizationId is { } org
            ? await _storageLock.AcquireAsync(_db, org, ct)
            : default!;
        long growth = sizes.Values.Sum(v => v ?? 0);
        if (organizationId is { } orgId && _planGate is not null && growth > 0
            && await _planGate.CheckUploadAsync(orgId, growth, ct) is not null)
        {
            int pictures = 1;
            return Conflict(new PictureCopiesOverQuota(pictures, false).ToDuplicateBody());
        }

        string baseName = string.IsNullOrWhiteSpace(source.Name) ? "picture" : source.Name.Trim();
        const string copyPrefix = "Copy of ";
        string name = copyPrefix + (baseName.Length > 200 - copyPrefix.Length ? baseName[..(200 - copyPrefix.Length)] : baseName);
        DateTime now = DateTime.UtcNow;
        EditImageDocument copy = new()
        {
            Id = newId,
            ProjectId = projectId,
            SourceProjectFileId = source.SourceProjectFileId,
            Name = name,
            CanvasWidth = source.CanvasWidth,
            CanvasHeight = source.CanvasHeight,
            BackgroundColor = source.BackgroundColor,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        List<string> made = [];
        try
        {
            Dictionary<string, string> copied = new(StringComparer.Ordinal);
            async Task<string> CopyOnceAsync(string key)
            {
                if (copied.TryGetValue(key, out string? existing)) return existing;
                if (sizes[key] is null) return key; // already missing from the store: nothing to share
                string ext = Path.GetExtension(key).ToLowerInvariant();
                if (ext.Length is <= 1 or > 6 || !ext[1..].All(char.IsAsciiLetterOrDigit)) ext = string.Empty;
                string newKey = $"{prefix}copy-{Guid.NewGuid()}{ext}";
                await _storage.CopyAsync(projectId, key, newKey, ct);
                made.Add(newKey);
                copied[key] = newKey;
                return newKey;
            }

            foreach (EditImageLayer l in sourceLayers)
            {
                bool hasImage = l.Kind != EditImageLayerKind.Text && !string.IsNullOrWhiteSpace(l.StorageKey);
                string storageKey = hasImage ? await CopyOnceAsync(l.StorageKey) : l.StorageKey;
                bool ownCopy = hasImage && !string.Equals(storageKey, l.StorageKey, StringComparison.Ordinal);
                // The matte travels only with a copied image; every object created counts toward the quota.
                string? matteKey = null;
                long? copyBytes = null;
                if (ownCopy)
                {
                    copyBytes = sizes[l.StorageKey] ?? 0;
                    if (!string.IsNullOrWhiteSpace(l.MatteStorageKey))
                    {
                        matteKey = await CopyOnceAsync(l.MatteStorageKey);
                        if (!string.Equals(matteKey, l.MatteStorageKey, StringComparison.Ordinal)) copyBytes += sizes[l.MatteStorageKey] ?? 0;
                        else matteKey = null;
                    }
                }
                else if (!hasImage) matteKey = l.MatteStorageKey;
                copy.Layers.Add(new EditImageLayer
                {
                    Id = Guid.NewGuid(),
                    DocumentId = newId,
                    ZOrder = l.ZOrder,
                    Role = l.Role,
                    Kind = l.Kind,
                    Label = l.Label,
                    SourceProjectFileId = l.SourceProjectFileId,
                    StorageKey = storageKey,
                    StorageBucket = ownCopy ? _storage.BucketName : l.StorageBucket,
                    CopySizeBytes = copyBytes,
                    MatteStorageKey = matteKey,
                    MimeType = l.MimeType,
                    Width = l.Width,
                    Height = l.Height,
                    Opacity = l.Opacity,
                    TransformJson = l.TransformJson,
                    SegmentationModel = l.SegmentationModel,
                    SegmentationPrompt = l.SegmentationPrompt,
                    Visible = l.Visible,
                    Locked = l.Locked,
                    BlendMode = l.BlendMode,
                    ContentJson = l.ContentJson,
                    ProvenanceJson = l.ProvenanceJson,
                    CropJson = l.CropJson,
                    CreatedAt = now
                });
            }

            _db.EditImageDocuments.Add(copy);
            await _db.SaveChangesAsync(ct);
            // The guard owns the transaction the rows were written in: without this it rolls them back.
            if (organizationId is not null) await guard.CommitAsync(ct);
        }
        catch
        {
            if (made.Count > 0)
            {
                try { await _storage.DeleteManyAsync(projectId, made, CancellationToken.None); }
                catch (Exception cleanup) { _logger.LogWarning(cleanup, "Could not remove {Count} picture copies after a failed duplicate", made.Count); }
            }

            throw;
        }

        return StatusCode(StatusCodes.Status201Created, ToDto(copy, projectId));
    }


    /// <summary>Soft-deletes a document and every layer on it.</summary>
    [HttpDelete("{documentId:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid documentId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        EditImageDocument? document = await LoadDocumentAsync(_db, projectId, documentId, ct);
        if (document is null) return NotFound();

        DateTime now = DateTime.UtcNow;
        document.DeletedAt = now;
        // The layers go with it in the same write. They are soft-deleted too rather than hard-deleted
        // so a mistaken delete is recoverable by an operator, and the query filter hides both.
        foreach (EditImageLayer layer in document.Layers) layer.DeletedAt = now;

        MarkLayerObjects(_db, access.Project!, document.Layers, now);

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ------------------------------------------------------------------ subjects

    /// <summary>
    /// Cuts one or more subjects out of a picture layer — or, with no <c>layerId</c>, out of the document's
    /// source image — and adds each as a layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole point of the feature: the response layers' pixels are subjects on transparency, so the
    /// editor can move, scale and re-colour them without dragging the background along.
    /// </para>
    /// <para>
    /// <b>Any picture layer can be cut from.</b> Originally only the document's source file could, so a
    /// picture made by the AI (or uploaded as a second layer) could not be cut out at all, and a document
    /// whose original layer had been deleted could not cut anything. With <c>layerId</c> the vendor is handed
    /// that layer's own bitmap (the same presigned GET every operation uses), the prompts are in that
    /// layer's pixels, and each cut-out takes the layer's transform — scaled when the vendor answered at a
    /// different size — and sits directly above it, so it lands exactly over what it was cut from.
    /// </para>
    /// <para>
    /// <b>The answer is an ARRAY</b> (docs/design/thumbnail-editor.md section 3): a model asked for
    /// several masks yields one <c>Cutout</c> layer per mask, and a model that returns one yields an array
    /// of one. Point and box prompts are in SOURCE-IMAGE PIXELS and are passed to the client verbatim;
    /// the SAM endpoints' schemas state integers and no coordinate space, which the live-verification
    /// step measures.
    /// </para>
    /// </remarks>
    [HttpPost("{documentId:guid}/subjects")]
    public async Task<ActionResult<IReadOnlyList<EditImageLayerDto>>> SegmentSubject(
        Guid projectId, Guid documentId, [FromBody] SegmentSubjectRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        EditImageDocument? document = await LoadDocumentAsync(_db, projectId, documentId, ct);
        if (document is null) return NotFound();

        if (_presignedUrls is null)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new
            {
                code = NoSegmentationProviderCode,
                message = "This deployment cannot hand a hosted segmenter a link to your image, so " +
                          "subject detection is unavailable here."
            });
        }

        EditImageLayer? parent = null;
        string sourceKey;
        Guid? sourceFileId;
        if (request.LayerId is { } layerId)
        {
            parent = document.Layers.FirstOrDefault(l => l.Id == layerId);
            if (parent is null) return NotFound();

            if (parent.Kind == EditImageLayerKind.Text || string.IsNullOrWhiteSpace(parent.StorageKey))
            {
                return Conflict(new
                {
                    code = LayerHasNoBitmapCode,
                    message = $"'{parent.Label}' is a text layer, so there is no picture to cut a subject out of. " +
                              "Select a picture layer and try again."
                });
            }

            if (await EditImageDocumentSupport.IsLayerSourceQuarantinedAsync(_db, projectId, parent, ct))
                return NotFound();
            if (await Services.Outputs.OutputGate.IsPictureLayerHeldAsync(_db, projectId, parent, ct))
                return Services.Outputs.OutputGate.Unavailable();
            sourceKey = parent.StorageKey;
            sourceFileId = parent.SourceProjectFileId;
        }
        else
        {
            ProjectFile? sourceFile = document.SourceProjectFileId is { } fileId
                ? await _db.ProjectFiles.FirstOrDefaultAsync(
                    f => f.Id == fileId && f.ProjectId == projectId
                         && f.ModerationStatus != FileModerationStatus.Quarantined, ct)
                : null;
            // The picture's source photo is evidence: refuse even though the picture kept a copy.
            if (sourceFile?.CsamStatus == CsamScanStatus.Quarantined) return NotFound();
            // The source photo was deleted and the picture kept its own copy (D10): cut from that copy.
            EditImageLayer? copiedSource = sourceFile is null
                ? document.Layers
                    .Where(l => l.Kind == EditImageLayerKind.Source && l.CopySizeBytes != null && !string.IsNullOrWhiteSpace(l.StorageKey))
                    .OrderBy(l => l.ZOrder)
                    .FirstOrDefault()
                : null;
            if (sourceFile is null && copiedSource is not null)
            {
                if (await EditImageDocumentSupport.IsLayerSourceQuarantinedAsync(_db, projectId, copiedSource, ct))
                    return NotFound();
                if (await Services.Outputs.OutputGate.IsPictureLayerHeldAsync(_db, projectId, copiedSource, ct))
                    return Services.Outputs.OutputGate.Unavailable();
                sourceKey = copiedSource.StorageKey;
                sourceFileId = null;
            }
            else if (sourceFile is null)
            {
                return Conflict(new
                {
                    code = NoSourceImageCode,
                    message = "This picture has no original image to cut a subject out of. Select a picture layer " +
                              "and try again."
                });
            }
            else
            {
                sourceKey = sourceFile.StorageKey;
                sourceFileId = sourceFile.Id;
            }
        }

        // A quarantined source is not presigned to a segmenter (the same rule every read path
        // applies); the picture keeps its own copy only when the source was gone, not when it was
        // blocked.
        if (sourceFileId is { } quarantinedSource &&
            await _db.ProjectFiles.AnyAsync(
                f => f.Id == quarantinedSource && f.ProjectId == projectId
                     && f.ModerationStatus == FileModerationStatus.Quarantined, ct))
        {
            return NotFound();
        }

        if (await Services.Outputs.OutputGate.IsKeyHeldAsync(_db, projectId, sourceKey, ct))
            return Services.Outputs.OutputGate.Unavailable();
        string? role = request.Role is null ? EditImageLayerRoles.Subject : EditImageLayerRoles.Normalise(request.Role);
        if (role is null)
        {
            return BadRequest(new
            {
                message = $"role must be one of: {string.Join(", ", EditImageLayerRoles.All)}"
            });
        }

        string? promptError = ValidateSubjectPrompts(request);
        if (promptError is not null) return BadRequest(new { message = promptError });

        ResolvedImageAnalysisProvider? provider;
        try
        {
            provider = await _providerResolver.ResolveSegmentationAsync(request.ProviderId, ct);
        }
        catch (ModelUnpricedException unpriced)
        {
            // An unpriced Platform segmenter is refused in plain words (409), before any hold.
            return ModelUnpricedResults.Conflict(unpriced);
        }

        if (provider is null)
        {
            return Conflict(new
            {
                code = NoSegmentationProviderCode,
                message = NoSegmentationProviderMessage
            });
        }

        // The vendor fetches the image itself, so it gets a short-lived presigned GET rather than a
        // public URL. Nothing about our storage policy changes to make this work.
        // The segmenter fetches the picture: a source that is still an output's bytes goes only with a current verdict.
        if (_publishModeration is not null
            && await _publishModeration.GateStoredCopyAsync(projectId, sourceKey, ct) is { } segmentRefused)
            return segmentRefused.ToRefusal(this);
        string sourceUrl = await _presignedUrls.CreateGetAsync(projectId, sourceKey, SourceUrlTtl);

        bool multiple = request.ReturnMultipleMasks == true;
        int wanted = multiple ? request.MaxMasks ?? DefaultMultiMasks : 1;

        List<PointPrompt>? points = request.PointPrompts is { Count: > 0 }
            ? [.. request.PointPrompts.Select(p => new PointPrompt(p.X, p.Y, p.Label))]
            : null;
        List<BoxPrompt>? boxes = request.BoxPrompts is { Count: > 0 }
            ? [.. request.BoxPrompts.Select(b => new BoxPrompt(b.XMin, b.YMin, b.XMax, b.YMax))]
            : null;

        SegmentationRequest segmentationRequest = new(
            Model: string.IsNullOrWhiteSpace(request.Model) ? provider.ModelName : request.Model!,
            SourceImageUrl: sourceUrl,
            Prompt: request.Prompt,
            NegativePrompt: request.NegativePrompt,
            SemanticParts: request.SemanticParts,
            Variant: request.Variant,
            PointPrompts: points,
            BoxPrompts: boxes,
            ReturnMultipleMasks: request.ReturnMultipleMasks,
            MaxMasks: request.MaxMasks);

        // T-110 (L2): RunOperation's priced-model rule. On a Platform segmenter (billing on) a caller-chosen model must be
        // a configured, priced Segmentation capability; any other would be priced at the account's dearest row, which can
        // be below its vendor cost, so it is refused before any hold or vendor call. BYO and billing off are unchanged;
        // the default (the row's own model) was already checked by the resolver.
        if (!string.IsNullOrWhiteSpace(request.Model) && _charges is { Options.Enabled: true } && provider.IsPlatformManaged
            && !await EditImageOperationsController.IsPricedPlatformModelAsync(_db, provider.Kind, provider.Capability, request.Model!, ct))
        {
            return BadRequest(EditImageOperationsController.UnpricedRequestedModelBody(request.Model!.Trim()));
        }
        // Credits: a subject cut-out is charged per call (decision 9). One priced output image is held before the
        // segmenter is called, then settled at what was metered, or released when the call fails.
        ChargeHold? hold = null;
        long? meteredMillis = null;
        if (_charges is not null && access.Project?.OrganizationId is { } chargedOrg)
        {
            long estimate = await _charges.EstimateOneUnitAsync(new PriceRequest(
                provider.ProviderId,
                provider.AccountId,
                provider.Kind,
                provider.Capability,
                segmentationRequest.Model,
                UsageKind.SegmentationImages,
                null,
                provider.IsPlatformManaged ? UsageBillingMode.Platform : UsageBillingMode.Byo,
                1m), ct);
            hold = await _charges.BeginHoldAsync(
                chargedOrg, UsageSource.Editor, estimate, Guid.NewGuid(), new CreditLedgerContext(projectId, Note: "subjects"), ct);
            if (!hold.Allowed) return NonWorkflowChargeResults.Refusal(hold, "subject cut-out");
        }

        SegmentationResult result;
        using CancellationTokenSource segTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        segTimeout.CancelAfter(_segmentationTimeout);
        try
        {
            // Attributed to the project's organization and the user (source Editor), not left Unattributed.
            using IDisposable usageScope = UsageScope.Begin(
                access.Project?.OrganizationId, projectId, null, null, CallerId(HttpContext), UsageSource.Editor);
            IImageAnalysisClient client = _analysisClients.Get(provider);
            result = await client.SegmentAsync(segmentationRequest, segTimeout.Token);
            // platform usage no price row prices was recorded at 0 credits, so it settles at least at the hold.
            meteredMillis = UsageScope.Current?.SawUnpricedPlatformUsage == true
                ? Math.Max(UsageScope.RunningCreditsMillis, hold?.RequiredMillis ?? 0)
                : UsageScope.RunningCreditsMillis;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Subject cut-out on {Kind}/{Model} timed out after {Seconds}s (project {ProjectId}): {Detail}",
                provider.Kind, segmentationRequest.Model.ForLog(), (int)_segmentationTimeout.TotalSeconds, projectId, VendorLogDetail(ex));
            return StatusCode(VendorFailureStatus, new
            {
                code = OperationTimeoutCode,
                message = $"The provider did not finish cutting out the subject within {(int)_segmentationTimeout.TotalSeconds} seconds. " +
                          "Nothing was added to the document; try again, or choose a faster provider."
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A vendor refusal, a bad model id or a request the endpoint's schema does not allow is a
            // CONFIGURATION answer, not a server fault: 424 with the reason (never 502, whose body
            // Cloudflare replaces with its own page), so the UI can say which control to change instead of
            // "something went wrong". The reason is logged too.
            _logger.LogWarning("Subject cut-out on {Kind}/{Model} failed at the vendor (project {ProjectId}): {Detail}",
                provider.Kind, segmentationRequest.Model.ForLog(), projectId, VendorLogDetail(ex));
            return StatusCode(VendorFailureStatus, new
            {
                code = SegmentationFailedCode,
                message = VendorFailureMessage("cut out the subject", ex.Message)
            });
        }
        finally
        {
            await NonWorkflowChargeResults.CloseHoldAsync(_charges, hold, meteredMillis, _logger);
        }

        List<SegmentedMask> masks = [.. result.AllMasks.Take(wanted)];
        string? prompt = string.IsNullOrWhiteSpace(request.Prompt) ? null : request.Prompt!.Trim();

        DateTime now = DateTime.UtcNow;
        List<EditImageLayer> layers = [];
        List<string> uploaded = [];

        try
        {
            for (int i = 0; i < masks.Count; i++)
            {
                SegmentedMask mask = masks[i];
                Guid cutoutId = Guid.NewGuid();

                StoredFileObject stored;
                using (MemoryStream cutoutStream = new(mask.Cutout.Bytes, writable: false))
                {
                    stored = await _storage.UploadAsync(
                        projectId,
                        cutoutId,
                        cutoutStream,
                        $"subject-{cutoutId:N}.png",
                        mask.Cutout.ContentType,
                        new Dictionary<string, string>
                        {
                            ["reelbolt-kind"] = "subject-cutout",
                            ["reelbolt-model"] = segmentationRequest.Model
                        },
                        ct,
                        category: "agentFiles");
                }

                uploaded.Add(stored.StorageKey);

                string? matteKey = null;
                if (mask.Matte is { } matte)
                {
                    Guid matteId = Guid.NewGuid();
                    using MemoryStream matteStream = new(matte.Bytes, writable: false);
                    StoredFileObject storedMatte = await _storage.UploadAsync(
                        projectId,
                        matteId,
                        matteStream,
                        $"subject-{matteId:N}-matte.png",
                        matte.ContentType,
                        new Dictionary<string, string> { ["reelbolt-kind"] = "subject-matte" },
                        ct,
                        category: "agentFiles");

                    matteKey = storedMatte.StorageKey;
                    uploaded.Add(matteKey);
                }

                string label = masks.Count > 1
                    ? $"{prompt ?? "Subject"} {i + 1}"
                    : prompt ?? $"Subject ({segmentationRequest.Model})";

                EditImageLayer layer = new()
                {
                    Id = Guid.NewGuid(),
                    DocumentId = document.Id,
                    ZOrder = NextZOrder(document) + layers.Count,
                    Role = role,
                    Kind = EditImageLayerKind.Cutout,
                    Label = label.Length > 300 ? label[..300] : label,
                    SourceProjectFileId = sourceFileId,
                    StorageKey = stored.StorageKey,
                    StorageBucket = stored.BucketName,
                    MatteStorageKey = matteKey,
                    MimeType = mask.Cutout.ContentType,
                    Width = mask.Cutout.Width,
                    Height = mask.Cutout.Height,
                    Opacity = 1,
                    TransformJson = WriteTransform(CutoutTransform(parent, mask.Cutout)),
                    SegmentationModel = segmentationRequest.Model,
                    SegmentationPrompt = prompt,
                    CreatedAt = now
                };

                layers.Add(layer);
            }
        }
        catch (Exception) when (uploaded.Count > 0)
        {
            // A half-written set must not leave objects nothing names. Best effort: a failed delete is
            // no worse than the failure that brought us here.
            foreach (string key in uploaded)
            {
                try { await _storage.DeleteAsync(projectId, key, CancellationToken.None); }
                catch (Exception) { /* nothing more to do */ }
            }

            throw;
        }

        if (parent is null)
        {
            _db.EditImageLayers.AddRange(layers);
        }
        else
        {
            // Directly above the layer it was cut from, in the order the vendor returned the masks.
            Guid above = parent.Id;
            foreach (EditImageLayer layer in layers)
            {
                InsertLayer(_db, document, layer, aboveLayerId: above);
                above = layer.Id;
            }
        }

        document.Revision += 1;
        document.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        // "Used N credits": the body stays the bare layer list, so the settled amount rides in a header.
        if (hold is { ReservationId: not null } && meteredMillis is { } settledMillis)
            Response.Headers["X-Charged-Credits"] = (settledMillis / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        return Ok(layers.Select(l => ToDto(l, document.Id, projectId)).ToList());
    }

    /// <summary>
    /// Where a cut-out goes: over the layer it was cut from, with that layer's transform, the scale corrected
    /// when the vendor answered at a different pixel width so the subject stays the size it was drawn at.
    /// Cut from the document's source (no parent): the identity transform, as before.
    /// </summary>
    internal static EditImageLayerTransformDto CutoutTransform(EditImageLayer? parent, GeneratedImage cutout)
    {
        if (parent is null) return new EditImageLayerTransformDto();

        EditImageLayerTransformDto t = ReadTransform(parent.TransformJson);
        return parent.Width is { } before && cutout.Width is { } after && before > 0 && after > 0 && before != after
            ? t with { Scale = t.Scale * before / after }
            : t;
    }

    // ------------------------------------------------------------------ non-bitmap and placed layers

    /// <summary>Adds a vector text layer on top of the stack. It has no bitmap.</summary>
    [HttpPost("{documentId:guid}/layers/text")]
    public async Task<ActionResult<EditImageLayerDto>> CreateTextLayer(
        Guid projectId, Guid documentId, [FromBody] CreateTextLayerRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        EditImageDocument? document = await LoadDocumentAsync(_db, projectId, documentId, ct);
        if (document is null) return NotFound();

        // SPEC 16.3: the project's brand defaults fill only the fields the caller left at the
        // legacy default, so an explicit font or colour always wins.
        TextContentDto? raw = request.Content;
        if (raw is not null && await LoadEditorDefaultsAsync(_db, projectId, ct) is { } editorDefaults)
            raw = WithEditorDefaults(raw, editorDefaults);

        (TextContentDto? content, string? contentError) = NormaliseText(raw);
        if (content is null) return BadRequest(new { message = contentError });

        string? role = request.Role is null ? EditImageLayerRoles.Overlay : EditImageLayerRoles.Normalise(request.Role);
        if (role is null)
            return BadRequest(new { message = $"role must be one of: {string.Join(", ", EditImageLayerRoles.All)}" });

        EditImageLayerTransformDto transform = request.Transform ?? new EditImageLayerTransformDto();
        if (ValidateTransform(transform) is { } transformError) return BadRequest(new { message = transformError });

        string label = string.IsNullOrWhiteSpace(request.Label) ? TextLabel(content.Text!) : request.Label.Trim();
        if (label.Length > 300) label = label[..300];

        DateTime now = DateTime.UtcNow;
        EditImageLayer layer = new()
        {
            Id = Guid.NewGuid(),
            DocumentId = document.Id,
            ZOrder = NextZOrder(document),
            Role = role,
            Kind = EditImageLayerKind.Text,
            Label = label,
            // A text layer has no bitmap: no key, no bucket, no size. The image route answers 404.
            StorageKey = string.Empty,
            StorageBucket = string.Empty,
            MimeType = "text/plain",
            Opacity = 1,
            TransformJson = WriteTransform(transform),
            ContentJson = JsonSerializer.Serialize(content, Json),
            CreatedAt = now
        };

        _db.EditImageLayers.Add(layer);
        document.Revision += 1;
        document.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        return Ok(ToDto(layer, document.Id, projectId));
    }

    /// <summary>Places one of the project's images (a logo, a sticker) as a layer on top of the stack.</summary>
    [HttpPost("{documentId:guid}/layers/from-file")]
    public async Task<ActionResult<EditImageLayerDto>> CreateImageLayerFromFile(
        Guid projectId, Guid documentId, [FromBody] CreateImageLayerFromFileRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        EditImageDocument? document = await LoadDocumentAsync(_db, projectId, documentId, ct);
        if (document is null) return NotFound();

        ProjectFile? file = await _db.ProjectFiles
            .FirstOrDefaultAsync(f => f.Id == request.ProjectFileId && f.ProjectId == projectId
                                      && f.CsamStatus != CsamScanStatus.Quarantined, ct);
        if (file is null) return NotFound();
        if (await Services.Outputs.OutputGate.IsFileHeldAsync(_db, file, ct)) return Services.Outputs.OutputGate.Unavailable();

        if (!IsImage(file.MimeType))
        {
            return Conflict(new
            {
                code = UnsupportedSourceCode,
                message = $"'{file.OriginalFileName}' is {file.MimeType}, not an image, so it cannot be placed as a layer."
            });
        }
        // A layer shows the file's bytes through its own route: an output shared with this file must have a current verdict.
        if (_publishModeration is not null
            && await _publishModeration.GateStoredCopyAsync(projectId, file.StorageKey, ct) is { } refused)
            return refused.ToRefusal(this);

        string? role = request.Role is null ? EditImageLayerRoles.Overlay : EditImageLayerRoles.Normalise(request.Role);
        if (role is null)
            return BadRequest(new { message = $"role must be one of: {string.Join(", ", EditImageLayerRoles.All)}" });

        string label = string.IsNullOrWhiteSpace(request.Label) ? file.OriginalFileName : request.Label.Trim();
        if (label.Length > 300) label = label[..300];

        DateTime now = DateTime.UtcNow;
        EditImageLayer layer = new()
        {
            Id = Guid.NewGuid(),
            DocumentId = document.Id,
            ZOrder = NextZOrder(document),
            Role = role,
            Kind = EditImageLayerKind.Image,
            Label = label,
            // Like a Source layer, the pixels ARE the user's project file: no copy is made and, on
            // delete, nothing is marked for retention (it is the customer's own object).
            SourceProjectFileId = file.Id,
            StorageKey = file.StorageKey,
            StorageBucket = string.IsNullOrWhiteSpace(file.StorageBucket) ? _storage.BucketName : file.StorageBucket,
            MimeType = file.MimeType,
            Opacity = 1,
            TransformJson = WriteTransform(new EditImageLayerTransformDto()),
            CreatedAt = now
        };

        _db.EditImageLayers.Add(layer);
        document.Revision += 1;
        document.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        return Ok(ToDto(layer, document.Id, projectId));
    }

    // ------------------------------------------------------------------ layers

    /// <summary>
    /// Changes a layer's role, position, opacity, transform, visibility, lock, blend mode, name or
    /// (text layers only) content.
    /// </summary>
    [HttpPatch("{documentId:guid}/layers/{layerId:guid}")]
    public async Task<ActionResult<EditImageLayerDto>> UpdateLayer(
        Guid projectId,
        Guid documentId,
        Guid layerId,
        [FromBody] UpdateEditImageLayerRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        EditImageDocument? document = await LoadDocumentAsync(_db, projectId, documentId, ct);
        if (document is null) return NotFound();

        EditImageLayer? layer = document.Layers.FirstOrDefault(l => l.Id == layerId);
        if (layer is null) return NotFound();

        // Checked BEFORE anything is written, so a stale writer returns 409 and changes nothing.
        if (request.BaseRevision is { } baseRevision && baseRevision != document.Revision)
            return RevisionConflict(document.Revision, baseRevision);

        // Every field is validated before any is applied, so a request with one bad field changes
        // nothing rather than half of itself.
        string? role = null;
        if (request.Role is { } requestedRole)
        {
            role = EditImageLayerRoles.Normalise(requestedRole);
            if (role is null)
                return BadRequest(new { message = $"role must be one of: {string.Join(", ", EditImageLayerRoles.All)}" });
        }

        string? blendMode = null;
        if (request.BlendMode is { } requestedBlend)
        {
            blendMode = EditImageBlendModes.Normalise(requestedBlend);
            if (blendMode is null)
                return BadRequest(new { message = $"blendMode must be one of: {string.Join(", ", EditImageBlendModes.All)}" });
        }

        if (request.Transform is { } transform && ValidateTransform(transform) is { } transformError)
            return BadRequest(new { message = transformError });

        string? cropJson = null;
        bool clearCrop = false;
        if (request.Crop is { } requestedCrop)
        {
            if (layer.Kind == EditImageLayerKind.Text)
                return BadRequest(new { message = "crop can only be set on a picture layer, not a Text layer." });
            if (ValidateCrop(requestedCrop) is { } cropError)
                return BadRequest(new { message = cropError });
            if (IsIdentityCrop(requestedCrop)) clearCrop = true;
            else cropJson = JsonSerializer.Serialize(requestedCrop, Json);
        }
        string? label = null;
        if (request.Label is { } requestedLabel)
        {
            label = requestedLabel.Trim();
            if (label.Length is 0 or > 300)
                return BadRequest(new { message = "label must be between 1 and 300 characters." });
        }

        string? contentJson = null;
        if (request.Content is not null)
        {
            if (layer.Kind != EditImageLayerKind.Text)
                return BadRequest(new { message = "content can only be set on a Text layer." });

            (TextContentDto? content, string? contentError) = NormaliseText(request.Content);
            if (content is null) return BadRequest(new { message = contentError });
            contentJson = JsonSerializer.Serialize(content, Json);
        }

        if (role is not null) layer.Role = role;
        if (blendMode is not null) layer.BlendMode = blendMode;
        if (label is not null) layer.Label = label;
        if (contentJson is not null) layer.ContentJson = contentJson;
        if (cropJson is not null) layer.CropJson = cropJson;
        else if (clearCrop) layer.CropJson = null;
        if (request.Visible is { } visible) layer.Visible = visible;
        if (request.Locked is { } locked) layer.Locked = locked;

        if (request.Opacity is { } opacity)
        {
            // Clamped rather than rejected: an opacity outside [0,1] is a slider rounding artefact, and
            // refusing the whole edit over 1.0000001 would be a worse answer than pinning it.
            layer.Opacity = Math.Clamp(opacity, 0d, 1d);
        }

        if (request.Transform is { } newTransform)
        {
            layer.TransformJson = WriteTransform(newTransform);
        }

        if (request.ZOrder is { } zOrder)
        {
            List<EditImageLayer> ordered = [.. document.Layers.OrderBy(l => l.ZOrder).ThenBy(l => l.Id)];
            ordered.Remove(layer);

            // A 1-based POSITION, clamped into range, not a raw ordinal: a client asking for position 1
            // means "put it at the back", whatever the other layers are doing, and never has to know
            // the current numbering to say so.
            int index = Math.Clamp(zOrder - 1, 0, ordered.Count);
            ordered.Insert(index, layer);

            for (int i = 0; i < ordered.Count; i++) ordered[i].ZOrder = i + 1;
        }

        document.Revision += 1;
        document.UpdatedAt = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RevisionConflict(document.Revision, request.BaseRevision);
        }

        return Ok(ToDto(layer, document.Id, projectId));
    }

    /// <summary>
    /// Soft-deletes one layer and closes the gap in the z-order.
    /// </summary>
    /// <remarks>
    /// The renumbering is not cosmetic: leaving a hole would make "move to position 3" mean something
    /// different after a delete than before, and a client caching the order would disagree with the
    /// server about a document nobody edited.
    /// </remarks>
    [HttpDelete("{documentId:guid}/layers/{layerId:guid}")]
    public async Task<IActionResult> DeleteLayer(
        Guid projectId, Guid documentId, Guid layerId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        EditImageDocument? document = await LoadDocumentAsync(_db, projectId, documentId, ct);
        if (document is null) return NotFound();

        EditImageLayer? layer = document.Layers.FirstOrDefault(l => l.Id == layerId);
        if (layer is null) return NotFound();

        DateTime now = DateTime.UtcNow;
        layer.DeletedAt = now;

        List<EditImageLayer> remaining = [.. document.Layers.Where(l => l.Id != layerId).OrderBy(l => l.ZOrder)];
        for (int i = 0; i < remaining.Count; i++) remaining[i].ZOrder = i + 1;

        MarkLayerObjects(_db, access.Project!, [layer], now, survivors: remaining);

        document.Revision += 1;
        document.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    // ------------------------------------------------------------------ layer pixels

    /// <summary>Serves a layer's own pixels. For a cut-out that is an RGBA PNG. A <c>Text</c> layer is 404.</summary>
    [HttpGet("{documentId:guid}/layers/{layerId:guid}/image")]
    public async Task<IActionResult> GetLayerImage(
        Guid projectId, Guid documentId, Guid layerId, CancellationToken ct)
    {
        // The access check lives in the ACTION rather than in the shared helper below, because
        // TenantBoundaryInventoryTests asserts that every action of a {projectId} controller consults
        // IProjectAccessService — and it is right to: a helper that checks for two actions is a guard
        // that disappears the moment a third action is added beside it and calls the same helper.
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        return await ServeLayerObjectAsync(projectId, documentId, layerId, matte: false, ct);
    }

    /// <summary>
    /// Replaces a bitmap layer's mask (matte). Body = PNG bytes (<c>image/png</c>), grayscale or RGBA. Visible amount:
    /// the alpha channel when any pixel is not fully opaque, else the luminance. The server stores an 8-bit grayscale
    /// PNG (255 = fully visible, 0 = hidden). The PNG must be exactly the layer's pixel size.
    /// </summary>
    [HttpPut("{documentId:guid}/layers/{layerId:guid}/matte")]
    public async Task<IActionResult> PutLayerMatte(
        Guid projectId, Guid documentId, Guid layerId, [FromQuery] int? baseRevision, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        EditImageDocument? document = await LoadDocumentAsync(_db, projectId, documentId, ct);
        if (document is null) return NotFound();
        EditImageLayer? layer = document.Layers.FirstOrDefault(l => l.Id == layerId && l.DeletedAt is null);
        if (layer is null || layer.Kind == EditImageLayerKind.Text) return NotFound();
        if (await IsLayerSourceQuarantinedAsync(_db, projectId, layer, ct)) return NotFound();
        if (await Services.Outputs.OutputGate.IsPictureLayerHeldAsync(_db, projectId, layer, ct)) return Services.Outputs.OutputGate.Unavailable();
        if (baseRevision is { } expected && expected != document.Revision)
            return RevisionConflict(document.Revision, expected);
        long maxBytes = _configuration?.GetValue("PictureEditor:MaxMatteBytes", DefaultMaxMatteBytes) ?? DefaultMaxMatteBytes;
        if (maxBytes <= 0) maxBytes = DefaultMaxMatteBytes;
        if (Request.ContentLength is { } declared && declared > maxBytes) return MatteTooLarge(maxBytes);
        byte[] raw;
        using (MemoryStream buffer = new())
        {
            byte[] chunk = new byte[64 * 1024];
            int read;
            while ((read = await Request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > maxBytes) return MatteTooLarge(maxBytes);
                buffer.Write(chunk, 0, read);
            }
            raw = buffer.ToArray();
        }
        // Magic bytes and the declared size come from the header, BEFORE any decode: a tiny file claiming a
        // huge raster is refused here, never inflated.
        if (raw.Length < 8 || !raw.AsSpan(0, 8).SequenceEqual(PngSignature)
            || !ImageHeader.TryReadSize(raw, out int width, out int height))
            return BadRequest(new { code = "MATTE_NOT_PNG", message = "The mask must be a PNG image." });
        if (width < 1 || height < 1 || width > MaxMatteSide || height > MaxMatteSide || (long)width * height > (long)MaxMatteSide * MaxMatteSide)
            return BadRequest(new { code = "MATTE_TOO_LARGE_PIXELS", message = $"The mask can be at most {MaxMatteSide} x {MaxMatteSide} pixels." });
        (int Width, int Height)? expectedSize = await PictureSizes.LayerAsync(_storage, projectId, layer, ct);
        if (expectedSize is not { } want || want.Width != width || want.Height != height)
            return BadRequest(new
            {
                code = "MATTE_SIZE_MISMATCH",
                message = expectedSize is { } w
                    ? $"The mask is {width} x {height} pixels but this layer is {w.Width} x {w.Height}."
                    : "This layer's size is not known, so a mask cannot be checked against it."
            });
        byte[]? normalised = NormaliseMatte(raw, width, height);
        if (normalised is null)
            return BadRequest(new { code = "MATTE_NOT_PNG", message = "The mask could not be read as a PNG image." });
        // The per-organisation storage lock is taken BEFORE the quota check and held through SaveChanges
        // (as Duplicate does), so two concurrent mask writes cannot both pass the check.
        await using ReelBolt.Inference.Api.Services.Uploads.OrganizationStorageGuard storageGuard = access.Project is { } lockedProject
            ? await _storageLock.AcquireAsync(_db, lockedProject.OrganizationId, ct)
            : default!;
        if (access.Project is { } project && _planGate is not null)
        {
            PlanGateViolation? violation = await _planGate.CheckUploadAsync(project.OrganizationId, normalised.LongLength, ct);
            if (violation is not null) return violation.ToResult();
        }
        Guid matteId = Guid.NewGuid();
        string? previous = layer.MatteStorageKey;
        string newKey;
        using (MemoryStream stream = new(normalised, writable: false))
        {
            StoredFileObject stored = await _storage.UploadAsync(
                projectId, matteId, stream, $"matte-{matteId:N}.png", "image/png",
                new Dictionary<string, string> { ["reelbolt-kind"] = "layer-mask" }, ct,
                category: "agentFiles", originalPath: $"image-editor/{documentId}/matte-{matteId:N}.png");
            newKey = stored.StorageKey;
        }
        DateTime now = DateTime.UtcNow;
        layer.MatteStorageKey = newKey;
        if (!string.IsNullOrWhiteSpace(previous)) await MarkReleasedMatteAsync(access.Project!, layer.Id, previous, now, ct);
        document.Revision += 1;
        document.UpdatedAt = now;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await TryDeleteQuietlyAsync(projectId, newKey);
            return RevisionConflict(document.Revision, baseRevision);
        }
        catch
        {
            await TryDeleteQuietlyAsync(projectId, newKey);
            throw;
        }
        // The storage guard owns the transaction the mask row was written in: without this the write rolls back on dispose.
        if (access.Project is not null) await storageGuard.CommitAsync(ct);
        Response.Headers["X-Image-Document-Revision"] = document.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Ok(ToDto(layer, document.Id, projectId));
    }

    /// <summary>Removes a bitmap layer's mask: the object is marked for the retention sweep and the key cleared. 204 (also when there was none).</summary>
    [HttpDelete("{documentId:guid}/layers/{layerId:guid}/matte")]
    public async Task<IActionResult> DeleteLayerMatte(
        Guid projectId, Guid documentId, Guid layerId, [FromQuery] int? baseRevision, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        EditImageDocument? document = await LoadDocumentAsync(_db, projectId, documentId, ct);
        if (document is null) return NotFound();
        EditImageLayer? layer = document.Layers.FirstOrDefault(l => l.Id == layerId && l.DeletedAt is null);
        if (layer is null || layer.Kind == EditImageLayerKind.Text) return NotFound();
        if (baseRevision is { } expected && expected != document.Revision)
            return RevisionConflict(document.Revision, expected);
        if (string.IsNullOrWhiteSpace(layer.MatteStorageKey)) return NoContent();
        DateTime now = DateTime.UtcNow;
        await MarkReleasedMatteAsync(access.Project!, layer.Id, layer.MatteStorageKey, now, ct);
        layer.MatteStorageKey = null;
        document.Revision += 1;
        document.UpdatedAt = now;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RevisionConflict(document.Revision, baseRevision);
        }
        Response.Headers["X-Image-Document-Revision"] = document.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return NoContent();
    }

    /// <summary>
    /// Marks a released matte for the retention sweep unless the key is already marked or another live layer still
    /// shows it (Duplicate can share one copy); a second mark would hit the unique index.
    /// </summary>
    private async Task MarkReleasedMatteAsync(Project project, Guid releasingLayerId, string key, DateTime now, CancellationToken ct)
    {
        if (_db.StorageRetentionMarks.Local.Any(m => m.StorageKey == key && m.ProjectId == project.Id)) return;
        if (await _db.StorageRetentionMarks.AnyAsync(m => m.StorageKey == key, ct)) return;
        if (await _db.EditImageLayers.AnyAsync(l => l.Id != releasingLayerId && l.DeletedAt == null && l.MatteStorageKey == key, ct)) return;
        MarkObject(_db, project, key, now);
    }

    /// <summary>The default cap on a mask upload (<c>PictureEditor:MaxMatteBytes</c>).</summary>
    public const long DefaultMaxMatteBytes = 8L * 1024 * 1024;

    /// <summary>The longest side (and the square of it, in pixels) a mask may declare.</summary>
    public const int MaxMatteSide = 4096;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private ObjectResult MatteTooLarge(long maxBytes) => StatusCode(StatusCodes.Status413PayloadTooLarge, new
    {
        code = "MATTE_TOO_LARGE",
        message = $"The mask is larger than the {maxBytes / (1024 * 1024)} MB limit."
    });

    private static string MatteETag(string key) =>
        "\"" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16] + "\"";

    private async Task TryDeleteQuietlyAsync(Guid projectId, string key)
    {
        try { await _storage.DeleteAsync(projectId, key, CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not remove a mask object after a failed save"); }
    }

    /// <summary>Decodes the PNG and re-encodes the visible amount as an 8-bit grayscale PNG; null when it will not decode.</summary>
    private static byte[]? NormaliseMatte(byte[] png, int width, int height)
    {
        SkiaSharp.SKBitmap? decoded;
        try
        {
            decoded = SkiaSharp.SKBitmap.Decode(
                png, new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            return null;
        }
        using SkiaSharp.SKBitmap? owned = decoded;
        if (decoded is null || decoded.Width != width || decoded.Height != height) return null;
        ReadOnlySpan<byte> px = decoded.GetPixelSpan();
        if (px.Length < (long)width * height * 4) return null;
        bool useAlpha = false;
        for (int i = 3; i < px.Length; i += 4)
        {
            if (px[i] != 255) { useAlpha = true; break; }
        }
        byte[] gray = new byte[width * height];
        for (int i = 0, o = 0; o < gray.Length; i += 4, o++)
        {
            gray[o] = useAlpha ? px[i + 3] : (byte)((px[i] * 299 + px[i + 1] * 587 + px[i + 2] * 114) / 1000);
        }
        using SkiaSharp.SKBitmap output = new(new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Gray8, SkiaSharp.SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(gray, 0, output.GetPixels(), gray.Length);
        using SkiaSharp.SKImage image = SkiaSharp.SKImage.FromBitmap(output);
        using SkiaSharp.SKData data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Serves a layer's binary matte, or 404 when the vendor returned none (and for a <c>Text</c> layer).</summary>
    [HttpGet("{documentId:guid}/layers/{layerId:guid}/matte")]
    public async Task<IActionResult> GetLayerMatte(
        Guid projectId, Guid documentId, Guid layerId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        return await ServeLayerObjectAsync(projectId, documentId, layerId, matte: true, ct);
    }

    // ------------------------------------------------------------------ internals

    private ConflictObjectResult RevisionConflict(int currentRevision, int? baseRevision) => Conflict(new
    {
        code = RevisionConflictCode,
        message = baseRevision is { } b
            ? $"This document is at revision {currentRevision}, not {b}. Reload and reapply the change."
            : $"This document changed while the request was being applied (now at revision {currentRevision}). Reload and reapply the change.",
        revision = currentRevision
    });

    /// <summary>The first problem with the subject prompts, or <c>null</c> when they are usable.</summary>
    private static string? ValidateSubjectPrompts(SegmentSubjectRequest request)
    {
        if (request.PointPrompts is { } points)
        {
            if (points.Count > MaxPointPrompts) return $"pointPrompts is limited to {MaxPointPrompts} points.";
            foreach (PointPromptDto p in points)
            {
                if (p.X < 0 || p.Y < 0) return "pointPrompts coordinates must not be negative (source-image pixels).";
                if (p.Label is not (0 or 1)) return "pointPrompts label must be 1 (include) or 0 (exclude).";
            }
        }

        if (request.BoxPrompts is { } boxes)
        {
            if (boxes.Count > MaxBoxPrompts) return $"boxPrompts is limited to {MaxBoxPrompts} boxes.";
            foreach (BoxPromptDto b in boxes)
            {
                if (b.XMin < 0 || b.YMin < 0) return "boxPrompts coordinates must not be negative (source-image pixels).";
                if (b.XMax <= b.XMin || b.YMax <= b.YMin) return "boxPrompts need xMax > xMin and yMax > yMin.";
            }
        }

        if (request.MaxMasks is { } max && (max < 1 || max > MaxSubjectMasks))
            return $"maxMasks must be between 1 and {MaxSubjectMasks}.";

        return null;
    }

    /// <summary>
    /// Streams one of a layer's two objects. Deliberately does NOT do the access check: its callers are
    /// actions, and each of them performs its own.
    /// </summary>
    private async Task<IActionResult> ServeLayerObjectAsync(
        Guid projectId, Guid documentId, Guid layerId, bool matte, CancellationToken ct)
    {
        EditImageLayer? layer = await _db.EditImageLayers
            .Where(l => l.Id == layerId && l.DocumentId == documentId)
            .Join(_db.EditImageDocuments.Where(d => d.Id == documentId && d.ProjectId == projectId),
                l => l.DocumentId, d => d.Id, (l, _) => l)
            .FirstOrDefaultAsync(ct);

        // A Text layer has no bitmap, whatever its row says: the browser renders it from content.
        if (layer is null || layer.Kind == EditImageLayerKind.Text) return NotFound();

        // A picture whose source photo was quarantined is absent, including the private copy the
        // picture made before the match: quarantine reaches every path that can serve the bytes.
        if (await EditImageDocumentSupport.IsLayerSourceQuarantinedAsync(_db, projectId, layer, ct))
            return NotFound();
        if (await Services.Outputs.OutputGate.IsPictureLayerHeldAsync(_db, projectId, layer, ct))
            return Services.Outputs.OutputGate.Unavailable();

        string? key = matte ? layer.MatteStorageKey : layer.StorageKey;
        if (string.IsNullOrWhiteSpace(key)) return NotFound();
        // A layer that still points at an output key is served only while that output has a current verdict.
        if (_publishModeration is not null
            && await _publishModeration.GateStoredFileAsync(projectId, key, ct) is { } layerRefused)
            return layerRefused.ToRefusal(this);
        if (matte && Request.Headers.IfNoneMatch.ToString() == MatteETag(key))
        {
            Response.Headers.ETag = MatteETag(key);
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Stream stream;
        try
        {
            stream = await _storage.DownloadAsync(projectId, key, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound
                                           || string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.Ordinal))
        {
            // The object is gone (a source photo deleted before copy-on-delete existed, a swept object):
            // a 404 the editor renders as a placeholder, never a 500 that blocks the whole picture.
            _logger.LogWarning("Layer {LayerId} of image document {DocumentId} points at a missing object", layerId, documentId);
            return NotFound(new
            {
                code = LayerImageMissingCode,
                message = "This layer's picture is no longer stored. Delete the layer or replace its picture."
            });
        }

        // A layer's bytes leave the origin under the same policy as any other customer bytes. A layer has
        // no file name, so the MIME alone decides (SafeContentPolicy.DecideByMime): an image type stays
        // inline and the editor renders it, anything else becomes an attachment.
        ServedContent served = SafeContentPolicy.DecideByMime(matte ? "image/png" : layer.MimeType);
        SafeContentPolicy.ApplySecurityHeaders(Response);
        if (!served.Inline)
        {
            Response.Headers["Content-Disposition"] = SafeContentPolicy.BuildContentDisposition(false, "layer");
        }

        // A layer is immutable once written — a re-segment or an operation creates a new layer rather
        // than overwriting one — so the bytes behind a stable id never change and this is safe to cache hard.
        if (matte)
        {
            // A mask can be REPLACED (PUT .../matte), so the stable URL is revalidated, not cached forever.
            Response.Headers.CacheControl = "private, no-cache";
            Response.Headers.ETag = MatteETag(key);
        }
        else
        {
            Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        }
        return File(stream, served.ContentType);
    }
}
