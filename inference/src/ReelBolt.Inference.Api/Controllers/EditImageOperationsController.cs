using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Graphics;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Storage;
using static ReelBolt.Inference.Api.Controllers.EditImageDocumentSupport;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The thumbnail editor's AI operations (docs/design/thumbnail-editor.md section 5): one synchronous
/// <c>POST .../operations</c> that dispatches to the image-generation/editing arm
/// (<see cref="IImageClient"/>) or the segmentation arm (<see cref="IImageAnalysisClient"/>) and stores
/// each output as a NEW immutable layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second controller.</b> The document controller passed the ~900 line mark the contract names
/// as the point to split operations out. It shares the route prefix and the helper class
/// (<see cref="EditImageDocumentSupport"/>) with its siblings and nothing else; this class performs its
/// own <see cref="IProjectAccessService"/> check and is in the reviewed set of project-route controllers.
/// </para>
/// <para>
/// <b>The server never rasterises.</b> An operation takes an existing layer's bitmap (already in object
/// storage), mints a short-lived presigned GET for it, optionally downloads the mask the browser
/// uploaded, hands both to the vendor client and stores what comes back. Nothing is edited in place: a
/// layer is immutable once written, which is also what makes the layer-bytes <c>immutable</c> cache
/// header correct.
/// </para>
/// <para>
/// <b>The vendor wait is bounded at 90 seconds</b> (<see cref="DefaultOperationTimeout"/>) because
/// Cloudflare's edge gives up at 100: past it the answer is <c>424 OPERATION_TIMEOUT</c>, not a
/// connection the edge drops. <b>A vendor failure is <c>424</c>, never 502/504</b>
/// (<see cref="EditImageDocumentSupport.VendorFailureStatus"/>): Cloudflare replaces the body of an origin
/// 502/504 with its own page, which hid every reason from the user; the reason is also logged. <b>The document is read twice</b>: once untracked before the call, to
/// find the source layer, and once fresh after it, to insert the output. A user dragging a layer during
/// a 60 second inpaint would otherwise turn a perfectly good, already-paid vendor result into a
/// concurrency failure, and the output is an addition that does not depend on what changed meanwhile.
/// </para>
/// <para>
/// <b>Which provider.</b> Each tool resolves its OWN capability (<see cref="CapabilityFor"/>): erase
/// <c>ImageErase</c>, fill <c>ImageFill</c>, expand <c>ImageExpand</c>, replace background
/// <c>ImageBackgroundReplace</c>, relight <c>ImageRelight</c>, upscale <c>ImageUpscale</c>, the
/// instruction edit <c>ImageEdit</c>, generate <c>ImageGeneration</c>, remove background
/// <c>Segmentation</c>. A job whose own capability has no row falls back to the <c>ImageEdit</c> default
/// — the row every tool used before the split — but only when that row's kind serves the job
/// (<see cref="ImageJobCapabilities.KindServes"/>), so a tool is never sent to a vendor that cannot do it;
/// otherwise the answer is <c>409</c> naming the capability to add.
/// </para>
/// <para>
/// <b>Which model.</b> An explicit <c>model</c> wins. Otherwise a <c>FalAi</c> row sends its own model when
/// that is a catalogued endpoint FOR THIS JOB and an EMPTY model otherwise, which the client reads as the
/// catalogue's default endpoint for the job (<see cref="ImageJobCapabilities.FalModelFor"/>); every other
/// kind sends the row's <c>ModelName</c>. A model that cannot do the operation surfaces as the client's
/// <see cref="NotSupportedException"/>, which is <c>409 OPERATION_UNSUPPORTED</c> — raised before
/// anything is sent, so it costs nothing.
/// </para>
/// <para>
/// <b>Expand is planned, not forwarded.</b> The user asks for a shape (<c>params.aspect</c>, "9:16") or for
/// pixels per side of the layer's own picture; <see cref="ExpandPlanner"/> turns that into the endpoint's own
/// geometry within its documented limits, sending the ORIGINAL whenever they allow it and otherwise answering
/// <c>422 SOURCE_TOO_LARGE</c> with the size of the copy the browser should send instead (the size the layer
/// appears in the design). The result is placed from the plan and the size the vendor delivered, so the
/// original's pixels stay where they were.
/// </para>
/// <para>
/// <b>Transparency survives an edit.</b> Most models answer an RGBA cut-out with an opaque picture, so a
/// pixel edit of a layer with transparent areas (erase, fill, instruct, relight, upscale) gets the
/// source's alpha carried over to the result (<see cref="PngAlpha"/>); fill also keeps what was painted.
/// Expand and replace background are exempt, because filling the background is what they are for.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/image-documents")]
[Authorize]
public partial class EditImageOperationsController : ControllerBase
{
    /// <summary>How long the vendor may take before the answer is <c>504 OPERATION_TIMEOUT</c>.</summary>
    public static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How many reference pictures one edit may carry. Not a vendor limit that is known — the schemas say
    /// "an array" without a count — but a bound the deployment sets: every entry is another picture the
    /// vendor is billed to read, and an unbounded list is an unbounded bill.
    /// </summary>
    public const int MaxReferencePictures = 4;

    internal const string ImageEditProviderUnavailableCode = "IMAGE_EDIT_PROVIDER_UNAVAILABLE";
    internal const string ImageGenerationProviderUnavailableCode = "IMAGE_GENERATION_PROVIDER_UNAVAILABLE";

    /// <summary>The background-removal endpoint, whose <c>Matting</c> variant is tuned for soft alpha edges such as hair.</summary>
    internal const string BackgroundRemovalModel = "fal-ai/birefnet/v2";

    internal const string BackgroundRemovalVariant = "Matting";

    private enum Op
    {
        Erase,
        Fill,
        Expand,
        ReplaceBackground,
        Relight,
        Upscale,
        Instruct,
        Generate,
        RemoveBackground
    }

    private static readonly string[] OperationNames =
        ["erase", "fill", "expand", "replaceBackground", "relight", "upscale", "instruct", "generate", "removeBackground"];

    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly IFileStorageService _storage;
    /// <summary>T-004: generation-prompt gate; null in tests that do not exercise moderation.</summary>
    private readonly IContentModerationGate? _moderation;
    private readonly IModerationMediaSampler? _sampler;
    private readonly IInferenceProviderResolver _providerResolver;
    private readonly IImageClientFactory _imageClients;
    private readonly IImageAnalysisClientFactory _analysisClients;
    private readonly IPresignedUrlService? _presignedUrls;
    private readonly TimeSpan _operationTimeout;
    private readonly ILogger<EditImageOperationsController> _logger;
    private readonly INonWorkflowChargeService? _charges;
    private readonly ReelBolt.Shared.Auth.ICurrentUser? _currentUser;

    private readonly ReelBolt.Inference.Api.Services.Moderation.PublishModeration? _publishModeration;

    public EditImageOperationsController(
        InferenceApiDbContext db,
        IProjectAccessService projectAccess,
        IFileStorageService storage,
        IInferenceProviderResolver providerResolver,
        IImageClientFactory imageClients,
        IImageAnalysisClientFactory analysisClients,
        IPresignedUrlService? presignedUrls = null,
        TimeSpan? operationTimeout = null,
        ILogger<EditImageOperationsController>? logger = null,
        INonWorkflowChargeService? charges = null,
        IContentModerationGate? moderation = null,
        IModerationMediaSampler? sampler = null,
        ReelBolt.Shared.Auth.ICurrentUser? currentUser = null,
        ReelBolt.Inference.Api.Services.Moderation.PublishModeration? publishModeration = null,
        ReelBolt.Inference.Api.Services.Avatars.IProjectSubjectReader? subjectReader = null,
        ReelBolt.Inference.Api.Services.Avatars.ISubjectPictureScaler? subjectScaler = null,
        ReelBolt.Inference.Api.Services.Avatars.IMediaEgressCallPermission? egressPermission = null,
        ReelBolt.Shared.Tenancy.IOrganizationEntitlements? entitlements = null)
    {
        _subjectReader = subjectReader;
        _subjectScaler = subjectScaler;
        _egressPermission = egressPermission ?? new ReelBolt.Inference.Api.Services.Avatars.MediaEgressCallPermission(db);
        _entitlements = entitlements;
        _publishModeration = publishModeration;
        _currentUser = currentUser;
        _charges = charges;
        _moderation = moderation;
        _sampler = sampler;
        _db = db;
        _projectAccess = projectAccess;
        _storage = storage;
        _providerResolver = providerResolver;
        _imageClients = imageClients;
        _analysisClients = analysisClients;
        _presignedUrls = presignedUrls;
        _operationTimeout = operationTimeout ?? DefaultOperationTimeout;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EditImageOperationsController>.Instance;
    }

    /// <summary>An expand that was planned: the plan, the original's pixel width, and the copy sent instead (if any).</summary>
    private sealed record ExpandWork(ExpandPlan Plan, int SourceWidth, string? CopyKey);

    /// <summary>
    /// One image an operation produced, plus its separate matte when there is one. <paramref name="IsCutout"/>
    /// marks an output the browser must draw through its matte (a background removal, or an edit whose
    /// transparency could only be kept as a matte).
    /// </summary>
    private sealed record Output(GeneratedImage Image, GeneratedImage? Matte, bool IsCutout = false);

    /// <summary>What a vendor call returned, normalised across the two client arms.</summary>
    private sealed record VendorResult(IReadOnlyList<Output> Outputs, string? VendorRequestId, string? ResolvedModel);

    /// <summary>The largest source or matte object read back to carry its transparency over (bytes).</summary>
    internal const long MaxAlphaSourceBytes = 96L * 1024 * 1024;

    /// <summary>The unit a capability is billed in (the first one it meters; images for the editor's jobs).</summary>
    private static UsageKind BilledUnitOf(InferenceProviderCapability capability)
        => ProviderCapabilityUsage.ByCapability.TryGetValue(capability, out UsageKind[]? units) && units.Length > 0
            ? units[0]
            : UsageKind.ImageEditImages;

    [HttpPost("{documentId:guid}/operations")]
    public async Task<ActionResult<EditImageOperationResponse>> RunOperation(
        Guid projectId, Guid documentId, [FromBody] EditImageOperationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        // ---- 1. The request, validated without touching a provider or a byte of storage -------------

        Op? parsed = ParseOperation(request.Operation);
        if (parsed is not { } op)
        {
            return BadRequest(new { message = $"operation must be one of: {string.Join(", ", OperationNames)}." });
        }

        string operationName = OperationNames[(int)op];

        EditImageDocument? document = await LoadDocumentAsync(_db, projectId, documentId, ct, tracked: false);
        if (document is null) return NotFound();

        string? requestedRole = null;
        if (request.Role is not null)
        {
            requestedRole = EditImageLayerRoles.Normalise(request.Role);
            if (requestedRole is null)
                return BadRequest(new { message = $"role must be one of: {string.Join(", ", EditImageLayerRoles.All)}" });
        }

        SanitizedPrompt prompt = GeneratedPromptSanitizer.Sanitize(request.Prompt, GeneratedPromptSanitizer.DefaultMaxPromptLength);
        if (prompt.ExceedsMaxLength)
            return BadRequest(new { message = $"prompt must be at most {prompt.MaxLength} characters." });

        SanitizedPrompt negative = GeneratedPromptSanitizer.Sanitize(request.NegativePrompt, GeneratedPromptSanitizer.DefaultMaxPromptLength);
        if (negative.ExceedsMaxLength)
            return BadRequest(new { message = $"negativePrompt must be at most {negative.MaxLength} characters." });

        if (PromptRequired(op) && prompt.IsEmpty)
            return BadRequest(new { message = $"{operationName} needs a prompt." });

        if (op == Op.Relight && prompt.IsEmpty && request.Params is not { Count: > 0 })
            return BadRequest(new { message = "relight needs a prompt and/or params." });

        string? paramError = ValidateParams(op, request.Params);
        if (paramError is not null)
            return BadRequest(new { code = OperationInvalidCode, message = paramError });

        // Avatars and objects can be shown by the two tools that take a description of the result, and by no other.
        bool withSubjects = request.SubjectIds is { Count: > 0 };
        if (withSubjects && !TakesSubjects(op))
        {
            return BadRequest(new
            {
                code = SubjectsNotSupportedCode,
                message = $"{operationName} cannot show an avatar or object. Only generate and instruct take subjectIds."
            });
        }

        // A new picture with subjects is made by the instruction-edit model, from the subjects' own pictures.
        Op routed = RoutedAs(op, withSubjects);

        EditImageLayer? source = null;
        if (op != Op.Generate)
        {
            if (request.LayerId is not { } layerId)
                return BadRequest(new { message = $"{operationName} needs a layerId." });

            source = document.Layers.FirstOrDefault(l => l.Id == layerId);
            if (source is null) return NotFound();

            // A source photo that was quarantined makes the picture's private copy unusable too.
            if (await EditImageDocumentSupport.IsLayerSourceQuarantinedAsync(_db, projectId, source, ct))
                return NotFound();
            if (await Services.Outputs.OutputGate.IsPictureLayerHeldAsync(_db, projectId, source, ct))
                return Services.Outputs.OutputGate.Unavailable();

            if (source.Kind == EditImageLayerKind.Text || string.IsNullOrWhiteSpace(source.StorageKey))
            {
                return Conflict(new
                {
                    code = LayerHasNoBitmapCode,
                    message = $"'{source.Label}' is a text layer with no pixels, so {operationName} cannot act on it."
                });
            }
        }

        // An upscale is priced as x2 of the largest canvas preset, so a larger source would bill below what its vendor
        // charges. Refused here, before a provider is chosen, a hold is taken or a byte is sent. The source's size is the
        // larger of its recorded and header sizes, and one that cannot be established is refused (fail closed); the
        // assistant path applies the same check through PictureSizes.UpscaleRefusalAsync.
        if (op == Op.Upscale && source is not null
            && await PictureSizes.UpscaleRefusalAsync(_storage, projectId, source, ct) is { } upscaleRefusal)
        {
            return BadRequest(new { code = OperationInvalidCode, message = upscaleRefusal });
        }

        string? maskKey = null;
        if (op is Op.Erase or Op.Fill)
        {
            if (!IsIssuedEditorKey(request.MaskKey, projectId, documentId, "mask"))
            {
                return BadRequest(new
                {
                    code = MaskRequiredCode,
                    message = $"{operationName} needs a maskKey returned by assets/upload-url with purpose 'mask' for this document."
                });
            }

            maskKey = request.MaskKey;
        }

        // ---- 2. The provider --------------------------------------------------------------------------

        (ResolvedImageProvider? imageProvider, ResolvedImageAnalysisProvider? analysisProvider, ActionResult? missing) =
            await ResolveProviderAsync(routed, request.ProviderId, ct);
        if (missing is not null) return missing;

        InferenceProviderKind kind = imageProvider?.Kind ?? analysisProvider!.Kind;
        string rowModel = imageProvider?.ModelName ?? analysisProvider!.ModelName;
        InferenceProviderCapability servedAs = imageProvider?.Capability ?? analysisProvider!.Capability;
        string model = ChooseModel(request.Model, routed, kind, rowModel);

        // T-026 (finding 6): on a Platform row a caller-chosen model must be a configured, priced capability of this
        // job; any other would be priced at the account's dearest row, which can be below what its vendor charges. It is
        // refused here, before any hold or vendor call. A BYO row is the workspace's own vendor bill: unchanged.
        if (!string.IsNullOrWhiteSpace(request.Model) && _charges is { Options.Enabled: true }
            && (imageProvider?.IsPlatformManaged ?? analysisProvider!.IsPlatformManaged)
            && !await IsPricedPlatformModelAsync(_db, kind, servedAs, model, ct))
        {
            return BadRequest(UnpricedRequestedModelBody(model));
        }

        // T-110 (L2): a default the tool substitutes for the row's own model (removeBackground's fal-ai/birefnet/v2,
        // which the catalog leaves unpriced) follows the branch's rule for an unpriced platform model: on a Platform row
        // with billing on it is refused in plain words before any hold or vendor call, never billed at the dearest row.
        // The row's own model was already checked by the resolver; billing off and BYO rows are unchanged.
        if (string.IsNullOrWhiteSpace(request.Model) && _charges is { Options.Enabled: true }
            && !string.Equals(model, rowModel, StringComparison.OrdinalIgnoreCase)
            && (imageProvider?.IsPlatformManaged ?? analysisProvider!.IsPlatformManaged)
            && !await IsPricedPlatformModelAsync(_db, kind, servedAs, model, ct))
        {
            return ModelUnpricedResults.Conflict(new ModelUnpricedException(servedAs, model));
        }

        // ---- 2a. Avatars and objects (subjectIds) ------------------------------------------------------
        // Resolved, gated and read before anything is presigned, held or sent: see PrepareSubjectsAsync for the order.
        SubjectWork? subjects = null;
        if (withSubjects)
        {
            (subjects, ActionResult? subjectRefusal) = await PrepareSubjectsAsync(op, request, access.Project!, kind, model, ct);
            if (subjectRefusal is not null) return subjectRefusal;
        }

        // ---- 2b. Expand: plan the request within the endpoint's documented limits ---------------------

        ExpandWork? expand = null;
        if (op == Op.Expand)
        {
            if (!TryReadExpandTarget(request.Params, out ExpandTarget? target, out string? targetProblem))
                return BadRequest(new { code = OperationInvalidCode, message = targetProblem });

            FalImageEndpoint? endpoint = kind == InferenceProviderKind.FalAi ? ExpandEndpointFor(model) : null;
            if (endpoint is not null)
            {
                (int Width, int Height)? size = source!.Width is > 0 && source.Height is > 0
                    ? (source.Width.Value, source.Height.Value)
                    : await ReadImageSizeAsync(projectId, source.StorageKey, ct);
                if (size is not { } original)
                {
                    return UnprocessableEntity(new
                    {
                        code = OperationInvalidCode,
                        message = $"The size of '{source.Label}' could not be read, so it cannot be expanded."
                    });
                }

                (int Width, int Height)? copy = null;
                string? copyKey = null;
                if (!string.IsNullOrWhiteSpace(request.SourceKey))
                {
                    if (!IsIssuedEditorKey(request.SourceKey, projectId, documentId, "source"))
                    {
                        return BadRequest(new
                        {
                            code = OperationInvalidCode,
                            message = "sourceKey must be one returned by assets/upload-url with purpose 'source' for this document."
                        });
                    }

                    copy = await ReadImageSizeAsync(projectId, request.SourceKey, ct);
                    if (copy is null)
                    {
                        return BadRequest(new
                        {
                            code = OperationInvalidCode,
                            message = "No readable picture was uploaded at that sourceKey. Run the expand again."
                        });
                    }

                    copyKey = request.SourceKey;
                }

                double shownAt = Math.Abs(ReadTransform(source.TransformJson ?? "{}").Scale);
                ExpandPlanOutcome planned = ExpandPlanner.Plan(
                    original.Width, original.Height, target!, ExpandLimits.For(endpoint), shownAt, copy);
                if (planned.Plan is not { } plan)
                    return UnprocessableEntity(new { code = OperationInvalidCode, message = planned.Refusal });

                if (plan.Source == ExpandSource.Original)
                {
                    copyKey = null; // the original fits: it is always preferred to a copy
                }
                else if (copyKey is null)
                {
                    // Nothing is sent and nothing is spent: the browser renders a copy of exactly this size,
                    // uploads it (purpose 'source') and runs the expand again with its key.
                    return UnprocessableEntity(new
                    {
                        code = SourceTooLargeCode,
                        message = plan.Notice,
                        width = plan.SendWidth,
                        height = plan.SendHeight,
                        source = plan.Source == ExpandSource.DisplayedSize ? "displayed" : "smaller"
                    });
                }

                expand = new ExpandWork(plan, original.Width, copyKey);
            }
            else if (target is ExpandTarget.Shape)
            {
                return Conflict(new
                {
                    code = OperationUnsupportedCode,
                    message = "The AI service set up for this tool cannot expand a picture to a shape. Use pixels per side instead."
                });
            }
        }

        // ---- 3. Source URL and mask bytes -------------------------------------------------------------
        // The source is sent to a vendor (as a link or as bytes). A picture that is still an output's bytes goes only
        // with a current verdict; any other key passes at once.
        if (source is not null && !string.IsNullOrWhiteSpace(source.StorageKey)
            && _publishModeration is not null
            && await _publishModeration.GateStoredCopyAsync(projectId, source.StorageKey, ct) is { } sourceRefused)
            return sourceRefused.ToRefusal(this);


        string? sourceUrl = null;
        if (source is not null)
        {
            if (_presignedUrls is null)
            {
                return StatusCode(StatusCodes.Status501NotImplemented, new
                {
                    code = op == Op.RemoveBackground ? NoSegmentationProviderCode : ImageEditProviderUnavailableCode,
                    message = "This deployment cannot hand a hosted model a link to your image, so " +
                              $"{operationName} is unavailable here."
                });
            }

            sourceUrl = await _presignedUrls.CreateGetAsync(
                projectId, expand?.CopyKey ?? source.StorageKey, EditImageDocumentsController.SourceUrlTtl);
        }

        // A second picture the edit is made against. It must be a file of THIS project: a project file
        // id is the only thing the client can name, and resolving it here is what stops a foreign id
        // from becoming a URL the vendor fetches.
        List<string>? referenceUrls = null;
        List<string>? referenceKeys = null;
        if (op == Op.Instruct && request.ReferenceProjectFileIds is { Count: > 0 } referenceFileIds)
        {
            if (referenceFileIds.Count > MaxReferencePictures)
            {
                return BadRequest(new
                {
                    code = OperationInvalidCode,
                    message = $"One edit can work from at most {MaxReferencePictures} reference pictures."
                });
            }

            if (_presignedUrls is null)
            {
                return StatusCode(StatusCodes.Status501NotImplemented, new
                {
                    code = ImageEditProviderUnavailableCode,
                    message = "This deployment cannot hand a hosted model a link to your picture, so a " +
                              "reference picture cannot be used here."
                });
            }

            referenceUrls = [];
            referenceKeys = [];
            foreach (Guid referenceFileId in referenceFileIds)
            {
                ProjectFile? reference = await _db.ProjectFiles
                    .FirstOrDefaultAsync(f => f.Id == referenceFileId && f.ProjectId == projectId
                                              && f.CsamStatus != CsamScanStatus.Quarantined, ct);

                // A quarantined picture is not a usable reference and its object must never be
                // presigned to a vendor: answer exactly like a missing file.
                if (reference is null || reference.ModerationStatus == FileModerationStatus.Quarantined)
                {
                    return BadRequest(new
                    {
                        code = OperationInvalidCode,
                        message = "A reference picture is not a file of this project."
                    });
                }

                if (await Services.Outputs.OutputGate.IsFileHeldAsync(_db, reference, ct))
                    return Services.Outputs.OutputGate.Unavailable();
                if (!reference.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new
                    {
                        code = OperationInvalidCode,
                        message = $"\"{reference.OriginalFileName}\" is not a picture, so it cannot be used as one."
                    });
                }

                if (string.IsNullOrWhiteSpace(reference.StorageKey))
                {
                    return BadRequest(new
                    {
                        code = OperationInvalidCode,
                        message = $"\"{reference.OriginalFileName}\" has no stored picture to send."
                    });
                }

                // A reference picture is presigned to a vendor: the same gate as the source, in plain text.
                if (_publishModeration is not null
                    && await _publishModeration.GateStoredCopyAsync(projectId, reference.StorageKey, ct) is { } referenceRefused)
                    return referenceRefused.ToRefusal(this);
                referenceUrls.Add(await _presignedUrls.CreateGetAsync(
                    projectId, reference.StorageKey, EditImageDocumentsController.SourceUrlTtl));
                referenceKeys.Add(reference.StorageKey);
            }
        }

        // The subjects' pictures, as data URIs (no link a vendor could fetch later). A new picture has no layer: the
        // first picture of the first subject is the picture the model starts from and the rest follow it. An edit keeps
        // the layer first, then the loose reference pictures, then the subjects' pictures.
        if (subjects is not null)
        {
            IEnumerable<SubjectPictureSent> asReferences = subjects.Pictures;
            if (op == Op.Generate)
            {
                sourceUrl = subjects.Pictures[0].DataUri;
                asReferences = subjects.Pictures.Skip(1);
            }

            referenceUrls ??= [];
            referenceUrls.AddRange(asReferences.Select(p => p.DataUri));
            if (referenceUrls.Count == 0) referenceUrls = null;
        }

        byte[]? maskBytes = null;
        if (maskKey is not null)
        {
            StoredObjectMetadata? maskMeta = await _storage.GetMetadataAsync(projectId, maskKey, ct);
            if (maskMeta is null || maskMeta.SizeBytes < 1)
            {
                return BadRequest(new
                {
                    code = MaskRequiredCode,
                    message = "No mask was uploaded at that key. PUT the PNG to the upload-url first."
                });
            }

            if (maskMeta.SizeBytes > MaxEditorUploadBytes)
            {
                return BadRequest(new
                {
                    code = MaskRequiredCode,
                    message = $"The mask is {maskMeta.SizeBytes} bytes; the limit is {MaxEditorUploadBytes}."
                });
            }

            await using Stream maskStream = await _storage.DownloadAsync(projectId, maskKey, ct);
            using MemoryStream buffer = new();
            await maskStream.CopyToAsync(buffer, ct);
            maskBytes = buffer.ToArray();
        }

        // fal.ai takes a mask only as a URL it fetches itself, so the stored mask also gets the same
        // short-lived presigned GET the source does. Other kinds ignore it and keep using the bytes.
        string? maskUrl = maskKey is not null && _presignedUrls is not null
            ? await _presignedUrls.CreateGetAsync(projectId, maskKey, EditImageDocumentsController.SourceUrlTtl)
            : null;

        // ---- 3a-bis. Content moderation ------------------------------------------------------
        // The prompt (and the source picture, downscaled to a derived JPEG) is screened BEFORE a hold
        // is taken or a vendor is called. A Block refuses with no spend; Unavailable/Review proceed
        // because generation is a fail-open surface.
        if (_moderation is not null)
        {
            List<ModerationImageInput> moderationImages = [];
            if (source is not null && !string.IsNullOrWhiteSpace(source.StorageKey) && _sampler is not null)
            {
                try
                {
                    await using Stream sourceStream = await _storage.DownloadAsync(projectId, source.StorageKey, ct);
                    ModerationMediaSample sample = await _sampler.SampleAsync(sourceStream, "image/png", ct);
                    moderationImages.AddRange(sample.Images.Select(bytes => new ModerationImageInput("image/jpeg", bytes)));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not sample the source image for moderation; screening the prompt only");
                }
            }

            // Reference pictures are additional model input: screen their derived frames too.
            if (referenceKeys is { Count: > 0 } && _sampler is not null)
            {
                foreach (string key in referenceKeys)
                {
                    try
                    {
                        await using Stream referenceStream = await _storage.DownloadAsync(projectId, key, ct);
                        ModerationMediaSample sample = await _sampler.SampleAsync(referenceStream, "image/png", ct);
                        moderationImages.AddRange(
                            sample.Images.Select(bytes => new ModerationImageInput("image/jpeg", bytes)));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not sample a reference picture for moderation; skipping that picture");
                    }
                }
            }

            // The subjects' pictures are model input too: the copies that are sent are sampled like a reference.
            if (subjects is not null && _sampler is not null)
            {
                foreach (SubjectPictureSent picture in subjects.Pictures)
                {
                    try
                    {
                        using MemoryStream subjectStream = new(picture.Bytes, writable: false);
                        ModerationMediaSample sample = await _sampler.SampleAsync(subjectStream, picture.ContentType, ct);
                        moderationImages.AddRange(
                            sample.Images.Select(bytes => new ModerationImageInput("image/jpeg", bytes)));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not sample a subject picture for moderation; skipping that picture");
                    }
                }
            }

            // A subject's name and description are customer text the model is given, so they are screened with the prompt.
            IEnumerable<string?> subjectWords = subjects is null
                ? []
                : subjects.Read.Subjects.SelectMany(s => new[] { s.Name, s.Description });
            string moderationText = string.Join('\n',
                new[] { prompt.Text, negative.Text }.Concat(subjectWords).Where(t => !string.IsNullOrWhiteSpace(t)));

            ModerationDecision decision = await _moderation.ModerateAsync(
                new ModerationInput(moderationText, moderationImages, ModerationContentKind.Text),
                new ModerationAttribution(
                    access.Project!.OrganizationId, projectId, CallerId(HttpContext), null, null,
                    ModerationSurface.GenerationPrompt, ModerationContentKind.Text),
                ct);

            if (decision.Action == ModerationAction.Block)
            {
                return BadRequest(new
                {
                    code = "CONTENT_BLOCKED",
                    message = string.IsNullOrWhiteSpace(decision.UserMessage)
                        ? "This prompt may violate the content policy."
                        : decision.UserMessage
                });
            }

            if (!decision.Available)
            {
                _logger.LogWarning(
                    "Content moderation was unavailable for a picture operation; proceeding because generation fails open");
            }
        }

        // ---- 3b. Credits: picture AI ops are charged per call (decision 9) ----------------------------

        // One unit of what the op is billed in, priced on the capability that will serve it, is held before
        // anything is sent; the call then settles at what metering actually recorded, or releases on failure.
        ChargeHold? hold = null;
        long? meteredMillis = null;

        // T-013: a per-megapixel job is held at the size it is EXPECTED to deliver (each side rounded up to the vendors'
        // 32-pixel grid, plus every reference picture's own size), priced by the same variant rule the meter bills with,
        // so a 10 MP fill holds the 10 MP charge. An expected size that cannot be computed holds at the endpoint's
        // documented maximum, and with none at the variant-less (largest) row. The hold is only the preflight: a call
        // whose output was measured and priced settles at what it metered (see the settle below), and only an
        // unreadable qwen reference picture is refused here (422), because no price can be formed for it.
        string billedModel = model.Length == 0 ? rowModel : model;
        bool sizePriced = imageProvider is not null && OutputSizePricing.PricesByOutputMegapixels(
            kind, billedModel, routed != Op.Generate, referenceUrls?.Count ?? 0);
        bool billed = _charges is { Options.Enabled: true } && access.Project?.OrganizationId is not null
            && imageProvider is { IsPlatformManaged: true };
        List<long>? referencePixels = null;
        string? holdVariant = null;
        if (sizePriced && referenceKeys is { Count: > 0 })
        {
            referencePixels = [];
            foreach (string key in referenceKeys)
            {
                if (await ReadImageSizeAsync(projectId, key, ct) is not { } size)
                {
                    if (billed)
                    {
                        return UnprocessableEntity(new
                        {
                            code = OperationInvalidCode,
                            message = "The size of a reference picture could not be read, and this tool is priced by the size " +
                                      "of every picture it works from, so it cannot be run. Use a PNG, JPEG or WebP picture."
                        });
                    }

                    referencePixels = null;
                    break;
                }

                referencePixels.Add((long)size.Width * size.Height);
            }
        }

        // A subject picture sent as a reference is priced like any other reference picture: by the size of the copy
        // that is sent (the first picture of a new picture is its source, not a reference).
        if (sizePriced && subjects is not null && (referenceKeys is not { Count: > 0 } || referencePixels is not null))
        {
            foreach (SubjectPictureSent picture in subjects.Pictures.Skip(op == Op.Generate ? 1 : 0))
            {
                if (picture.Pixels is not { } sentPixels)
                {
                    if (billed)
                    {
                        return UnprocessableEntity(new
                        {
                            code = OperationInvalidCode,
                            message = "The size of an avatar picture could not be read, and this tool is priced by the size " +
                                      "of every picture it works from, so it cannot be run."
                        });
                    }

                    referencePixels = null;
                    break;
                }

                (referencePixels ??= []).Add(sentPixels);
            }
        }

        long? expectedPixels = null;
        if (sizePriced && billed)
        {
            // Rework 2: with no expected size and no documented maximum the hold is the variant-less (largest) row
            // rather than a refusal; the settlement below never goes under it.
            expectedPixels = await ExpectedOutputPixelsAsync(op, request, projectId, source, expand, ct)
                             ?? DocumentedMaxOutputPixels(op, kind, billedModel, request);
            if (expectedPixels is { } pixels)
            {
                // An endpoint that bills its input (outpaint) holds max(input, output) MP, the rule the meter bills by.
                holdVariant = OutputSizePricing.BillsInputMegapixels(kind, billedModel)
                    ? OutputSizePricing.InputAndOutputVariant(pixels,
                        expand?.Plan.Geometry is { ImageWidth: > 0, ImageHeight: > 0 } g
                            ? OutputSizePricing.GridPixels(g.ImageWidth, g.ImageHeight)
                            : pixels)
                    : OutputSizePricing.MegapixelVariant(pixels, referencePixels);
            }
        }

        if (_charges is not null && access.Project?.OrganizationId is { } chargedOrg)
        {
            long estimate = await _charges.EstimateOneUnitAsync(new PriceRequest(
                imageProvider is not null ? imageProvider.ProviderId : analysisProvider!.ProviderId,
                imageProvider is not null ? imageProvider.AccountId : analysisProvider!.AccountId,
                kind,
                servedAs,
                model.Length == 0 ? rowModel : model,
                BilledUnitOf(servedAs),
                holdVariant,
                (imageProvider?.IsPlatformManaged ?? analysisProvider!.IsPlatformManaged) ? UsageBillingMode.Platform : UsageBillingMode.Byo,
                1m), ct);
            hold = await _charges.BeginHoldAsync(
                chargedOrg, UsageSource.Editor, estimate, Guid.NewGuid(), new CreditLedgerContext(projectId, Note: operationName), ct);
            if (!hold.Allowed) return NonWorkflowChargeResults.Refusal(hold, "picture edit");
        }

        // ---- 4. The vendor call, bounded ---------------------------------------------------------------

        VendorResult vendor;
        using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        using (UsageScope.Begin(access.Project?.OrganizationId, projectId, null, null, CallerId(HttpContext), UsageSource.Editor))
        {
            timeout.CancelAfter(_operationTimeout);

            try
            {
                vendor = await CallVendorAsync(
                    op, request, prompt.Text, negative.Text, model, sourceUrl, maskBytes, maskUrl, referenceUrls, referencePixels,
                    expectedPixels, document, imageProvider, analysisProvider, expand?.Plan.Geometry, timeout.Token, subjects);
                // platform usage no price row prices was recorded at 0 credits, so it settles at least at the hold.
                meteredMillis = UsageScope.Current?.SawUnpricedPlatformUsage == true
                    ? Math.Max(UsageScope.RunningCreditsMillis, hold?.RequiredMillis ?? 0)
                    : UsageScope.RunningCreditsMillis;

                // T-013 (F1, F3, rework 4): once the vendor answered, a billed call is never released for free, and a
                // size-priced job settles uncapped at what it metered (clamped by the meter to what the endpoint can
                // plausibly deliver). The user keeps the picture they paid for.
                if (hold is { ReservationId: not null } && meteredMillis is { } metered)
                {
                    // The hold is a conservative estimate, never the charge when metering worked: a measured, priced
                    // call settles at what it metered (uncapped when size-priced). Only a metering failure after the
                    // vendor answered - nothing metered (a swallowed meter error) or, size-priced, a picture whose
                    // size could not be measured - is floored at the hold.
                    bool meteringFailed = metered <= 0
                        || (sizePriced && OutputSizePricing.MeasuredPixels(vendor.Outputs.Select(o => o.Image)) is null);
                    if (meteringFailed)
                        meteredMillis = Math.Max(metered, hold.RequiredMillis);

                    if (meteredMillis != metered)
                    {
                        _logger.LogWarning(
                            "Image editor {Operation} on {Kind}/{Model} metered {Metered} millicredits against a {Hold} hold " +
                            "(project {ProjectId}); settled at the hold",
                            operationName, kind, billedModel, metered, hold.RequiredMillis, projectId);
                    }
                    else if (sizePriced && _charges is not null && metered > SettleCeilingMillis(hold.RequiredMillis, _charges.Options))
                    {
                        _logger.LogWarning(
                            "Image editor {Operation} on {Kind}/{Model} metered {Metered} millicredits against a {Hold} hold " +
                            "(project {ProjectId}); settled uncapped",
                            operationName, kind, billedModel, metered, hold.RequiredMillis, projectId);
                    }
                }
            }
            catch (NotSupportedException ex)
            {
                // The client says its vendor documents no such operation. Thrown before anything is
                // sent, so nothing was spent; the message names the vendor basis.
                return Conflict(new
                {
                    code = OperationUnsupportedCode,
                    message = "The AI service set up for this tool cannot " + Describe(op) + ". " + ex.Message
                });
            }
            catch (ArgumentException ex)
            {
                // The client checked the request against its endpoint's schema and refused it (an
                // unknown param, a model it has not read). Also before anything was sent.
                return BadRequest(new { code = OperationInvalidCode, message = ex.Message });
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException && !ct.IsCancellationRequested)
            {
                // Our 90 second bound, or the vendor client's own HTTP timeout: either way the caller
                // did not cancel, the vendor did not answer in time. 424, not 504: see VendorFailureStatus.
                _logger.LogWarning(
                    "Image editor {Operation} on {Kind}/{Model} timed out after {Seconds}s (project {ProjectId}): {Detail}",
                    operationName, kind, model.Length == 0 ? rowModel : model, (int)_operationTimeout.TotalSeconds, projectId,
                    VendorLogDetail(ex));
                return StatusCode(VendorFailureStatus, new
                {
                    code = OperationTimeoutCode,
                    message = $"The provider did not finish {operationName} within {(int)_operationTimeout.TotalSeconds} seconds. " +
                              "Nothing was added to the document; try again, or choose a faster provider."
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A vendor refusal or failure is a CONFIGURATION answer, not a server fault: 424 with the
                // reason (never 502, whose body Cloudflare replaces), so the UI can say which control to
                // change - and the reason is logged, because it is the only record of what the vendor said.
                _logger.LogWarning(
                    "Image editor {Operation} on {Kind}/{Model} failed at the vendor (project {ProjectId}): {Detail}",
                    operationName, kind, model.Length == 0 ? rowModel : model, projectId, VendorLogDetail(ex));
                return StatusCode(VendorFailureStatus, new
                {
                    code = op == Op.RemoveBackground ? SegmentationFailedCode : OperationFailedCode,
                    message = VendorFailureMessage(Describe(op), ex.Message)
                });
            }
            finally
            {
                // Settled at what the call metered once the vendor answered; released on any failure.
                await NonWorkflowChargeResults.CloseHoldAsync(_charges, hold, meteredMillis, _logger, uncapped: sizePriced);
            }
        }

        if (vendor.Outputs.Count == 0)
        {
            _logger.LogWarning("Image editor {Operation} on {Kind}/{Model} answered without a picture (project {ProjectId})",
                operationName, kind, model.Length == 0 ? rowModel : model, projectId);
            return StatusCode(VendorFailureStatus, new
            {
                code = op == Op.RemoveBackground ? SegmentationFailedCode : OperationFailedCode,
                message = VendorFailureMessage(Describe(op), "it answered without a picture.")
            });
        }

        // ---- 5. Keep the source's transparency -------------------------------------------------------

        string? notice = expand?.Plan.Notice;
        if (source is not null && KeepsTransparency(op))
        {
            AlphaPlane? alpha = await ReadSourceAlphaAsync(projectId, source, ct);
            if (alpha is not null)
            {
                // A fill paints INTO the painted area, which on a cut-out is often transparent (a hat above
                // a head): what was painted is kept as well as what was already there.
                try
                {
                    if (op == Op.Fill && maskBytes is not null && PngAlpha.DecodeMask(maskBytes) is { } painted)
                    {
                        alpha = alpha.Max(painted);
                    }

                    (Output kept, notice) = KeepTransparency(vendor.Outputs[0], alpha);
                    vendor = vendor with { Outputs = [kept, .. vendor.Outputs.Skip(1)] };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The result is already paid for: store it as it came rather than lose it.
                    _ = ex;
                    notice = "The transparent background of the original could not be carried over to this result. " +
                             "Hide or delete the parts you do not want.";
                }
            }
        }

        // ---- 6. Store the outputs as new layers --------------------------------------------------------

        // A fresh, tracked load: see the class remarks. The output is an addition, so it does not
        // depend on what the user changed during the vendor wait.
        EditImageDocument? current = await LoadDocumentAsync(_db, projectId, documentId, ct);
        if (current is null) return NotFound();

        // The FRESH row, so a move, resize or crop made during the vendor wait is followed. If the target was deleted
        // meanwhile, the request-time row is only used to name and place the output, which is then kept HIDDEN.
        EditImageLayer? freshParent = source is null ? null : current.Layers.FirstOrDefault(l => l.Id == source.Id);
        bool layerGone = source is not null && freshParent is null;
        EditImageLayer? parent = freshParent ?? source;
        Guid? aboveId = freshParent?.Id;
        bool atBackWhenGone = false;
        if (layerGone)
        {
            EditImageLayer? below = current.Layers.Where(l => l.ZOrder <= source!.ZOrder).OrderBy(l => l.ZOrder).ThenBy(l => l.Id).LastOrDefault();
            aboveId = below?.Id;
            atBackWhenGone = below is null && current.Layers.Count > 0 && source!.ZOrder < current.Layers.Min(l => l.ZOrder);
        }

        string providerKind = kind.ToString();
        string? promptForProvenance = prompt.IsEmpty ? null : prompt.Text;

        DateTime now = DateTime.UtcNow;
        List<EditImageLayer> layers = [];
        List<string> uploaded = [];

        try
        {
            foreach (Output output in vendor.Outputs.Take(1))
            {
                Guid layerId = Guid.NewGuid();
                string contentType = string.IsNullOrWhiteSpace(output.Image.ContentType) ? "image/png" : output.Image.ContentType;

                StoredFileObject stored;
                using (MemoryStream stream = new(output.Image.Bytes, writable: false))
                {
                    stored = await _storage.UploadAsync(
                        projectId,
                        layerId,
                        stream,
                        $"{operationName}-{layerId:N}{ExtensionFor(contentType)}",
                        contentType,
                        new Dictionary<string, string>
                        {
                            ["reelbolt-kind"] = $"image-editor-{operationName}",
                            ["reelbolt-model"] = vendor.ResolvedModel ?? (model.Length == 0 ? rowModel : model)
                        },
                        ct,
                        category: "agentFiles");
                }

                uploaded.Add(stored.StorageKey);

                string? matteKey = null;
                if (output.Matte is { } matte)
                {
                    Guid matteId = Guid.NewGuid();
                    using MemoryStream matteStream = new(matte.Bytes, writable: false);
                    StoredFileObject storedMatte = await _storage.UploadAsync(
                        projectId,
                        matteId,
                        matteStream,
                        $"{operationName}-{matteId:N}-matte.png",
                        matte.ContentType,
                        new Dictionary<string, string> { ["reelbolt-kind"] = "image-editor-matte" },
                        ct,
                        category: "agentFiles");

                    matteKey = storedMatte.StorageKey;
                    uploaded.Add(matteKey);
                }

                string effectiveModel = vendor.ResolvedModel ?? (model.Length == 0 ? rowModel : model);
                string label = string.IsNullOrWhiteSpace(request.Label)
                    ? op == Op.Generate && subjects is not null
                        ? "Generated: " + string.Join(", ", subjects.Read.Subjects.Select(s => s.Name))
                        : DefaultLabel(op, parent)
                    : request.Label.Trim();

                EditImageLayer layer = new()
                {
                    Id = layerId,
                    DocumentId = current.Id,
                    Role = requestedRole ?? DefaultRole(op, parent),
                    Kind = output.IsCutout ? EditImageLayerKind.Cutout : EditImageLayerKind.Generated,
                    Label = label.Length > 300 ? label[..300] : label,
                    SourceProjectFileId = parent?.SourceProjectFileId,
                    StorageKey = stored.StorageKey,
                    StorageBucket = stored.BucketName,
                    MatteStorageKey = matteKey,
                    MimeType = contentType,
                    Width = output.Image.Width,
                    Height = output.Image.Height,
                    // The output replaces the layer it was made from visually, so it inherits how that
                    // layer was being drawn; the original stays underneath for the user to hide.
                    Opacity = parent?.Opacity ?? 1,
                    BlendMode = parent?.BlendMode ?? EditImageBlendModes.Normal,
                    TransformJson = WriteTransform(OutputTransform(op, parent, output.Image, request.Params, current, expand)),
                    SegmentationModel = op == Op.RemoveBackground ? effectiveModel : null,
                    // Who the layer shows, that an AI made it and how that is disclosed: the layer's content, which the
                    // editor reads (and a duplicate copies). Null for every layer made without subjects.
                    ContentJson = subjects?.Facts.ToContentJson(),
                    ProvenanceJson = subjects is not null
                        ? SubjectProvenanceJson(operationName, providerKind, servedAs, effectiveModel, promptForProvenance,
                            vendor.VendorRequestId, parent?.Id)
                        : JsonSerializer.Serialize(new
                        {
                            operation = operationName,
                            providerKind,
                            capability = servedAs.ToString(),
                            model = effectiveModel,
                            prompt = promptForProvenance,
                            vendorRequestId = vendor.VendorRequestId,
                            parentLayerId = parent?.Id
                        }, JsonWithNulls),
                    Visible = !layerGone,
                    CreatedAt = now
                };

                // directly above the layer it was made from; a generated backdrop goes to the very back
                InsertLayer(_db, current, layer, aboveLayerId: aboveId, atBack: op == Op.Generate || atBackWhenGone);
                layers.Add(layer);
            }

            current.Revision += 1;
            current.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await DiscardAsync(projectId, uploaded);
            return Conflict(new
            {
                code = RevisionConflictCode,
                message = "The document changed while the operation was being stored. Reload and run it again.",
                revision = current.Revision
            });
        }
        catch (Exception)
        {
            // Never leave objects nothing names.
            await DiscardAsync(projectId, uploaded);
            throw;
        }

        return Ok(new EditImageOperationResponse(
            [.. layers.Select(l => ToDto(l, current.Id, projectId))],
            operationName,
            new EditImageOperationProviderDto(
                providerKind, vendor.ResolvedModel ?? (model.Length == 0 ? rowModel : model), servedAs.ToString()),
            new EditImageOperationUsageDto("images", layers.Count),
            current.Revision,
            notice,
            hold is { ReservationId: not null } && meteredMillis is { } settledMillis ? settledMillis / 1000.0 : null,
            layerGone && layers.Count > 0 ? ImageNeedsReviewDto.ForLayerGone(layers[0].Id) : null,
            subjects?.Skipped));
    }

    /// <summary>
    /// What the AI service set up for <paramref name="operation"/> can take right now, so the editor shows only
    /// the controls it honours: whether it documents a prompt, and for <c>expand</c> its geometry (pixels per
    /// side or a finished canvas) and documented limits. Resolves the provider exactly as running the tool
    /// would (its own capability, then the ImageEdit fallback), and answers the same <c>409</c> when none is
    /// set up. Read from the client's catalogue (<see cref="IImageClient.Describe"/>): nothing is sent to a
    /// vendor.
    /// </summary>
    [HttpGet("operations/{operation}/capabilities")]
    public async Task<ActionResult<EditImageOperationCapabilitiesResponse>> GetOperationCapabilities(
        Guid projectId, string operation, [FromQuery] Guid? providerId,
        [FromQuery] Guid? documentId, [FromQuery] int? width, [FromQuery] int? height, CancellationToken ct,
        [FromQuery] bool? subjects = null)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (ParseOperation(operation) is not { } requested)
        {
            return BadRequest(new { message = $"operation must be one of: {string.Join(", ", OperationNames)}." });
        }

        // `subjects=true` asks what a run WITH avatars or objects would use and cost. For generate that is the
        // instruction-edit model (a new picture with subjects is made from the subjects' pictures), so the provider,
        // the prompt limits and the estimate below are that route's; the answer still names the tool that was asked.
        Op op = RoutedAs(requested, subjects == true && TakesSubjects(requested));

        (ResolvedImageProvider? imageProvider, ResolvedImageAnalysisProvider? analysisProvider, ActionResult? missing) =
            await ResolveProviderAsync(op, providerId, ct);
        if (missing is not null) return missing;

        // What the tool can do with avatars and objects: read from the picture-edit service a run with subjects would
        // use (the one just resolved for an edit; the default one for a plain "create a picture").
        EditImageSubjectCapabilitiesDto? subjectCapabilities = !TakesSubjects(requested)
            ? null
            : op == Op.Instruct && imageProvider is not null
                ? SubjectCapabilities(imageProvider)
                : await SubjectCapabilitiesAsync(ct);

        if (analysisProvider is not null)
        {
            string segmentation = ChooseModel(null, op, analysisProvider.Kind, analysisProvider.ModelName);
            return Ok(new EditImageOperationCapabilitiesResponse(
                OperationNames[(int)op],
                new EditImageOperationProviderDto(analysisProvider.Kind.ToString(), segmentation, analysisProvider.Capability.ToString()),
                TakesPrompt: false,
                MaxPromptLength: 0,
                Expand: null,
                Estimate: await EstimateAsync(access, op, analysisProvider.ProviderId, analysisProvider.AccountId, analysisProvider.Kind,
                    analysisProvider.Capability, segmentation, analysisProvider.IsPlatformManaged, documentId, width, height, ct),
                CanConfigure: CanConfigure()));
        }

        ResolvedImageProvider provider = imageProvider!;
        string model = ChooseModel(null, op, provider.Kind, provider.ModelName);

        // A fal.ai row sends an empty model for "the catalogue's default for this job"; describe THAT endpoint.
        string described = model.Length == 0 && provider.Kind == InferenceProviderKind.FalAi
            ? ImageJobCapabilities.FalDefaultFor(CapabilityFor(op))?.Id ?? model
            : model;

        ImageModelCapabilities caps;
        try
        {
            caps = _imageClients.Get(provider).Describe(described);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return Conflict(new
            {
                code = OperationUnsupportedCode,
                message = "The AI service set up for this tool cannot " + Describe(op) + ". " + ex.Message
            });
        }

        return Ok(new EditImageOperationCapabilitiesResponse(
            OperationNames[(int)requested],
            new EditImageOperationProviderDto(provider.Kind.ToString(), described, provider.Capability.ToString()),
            caps.TakesPrompt,
            caps.MaxPromptLength,
            op == Op.Expand && caps.Expand is { } e
                ? new EditImageExpandCapabilitiesDto(e.Geometry, e.MaxMarginPx, e.MaxOutputPixels, e.MaxInputPixels, e.Basis)
                : null,
            await EstimateAsync(access, op, provider.ProviderId, provider.AccountId, provider.Kind, provider.Capability,
                model.Length == 0 ? provider.ModelName : model, provider.IsPlatformManaged, documentId, width, height, ct),
            CanConfigure(),
            subjectCapabilities));
    }

    /// <summary>The caller can set up the AI service: an organization Owner/Admin or a platform admin.</summary>
    private bool CanConfigure() => _currentUser is { } u
        && (u.IsPlatformAdmin || u.OrgRole is ReelBolt.Shared.Tenancy.OrganizationRole.Owner or ReelBolt.Shared.Tenancy.OrganizationRole.Admin);

    /// <summary>
    /// About what one run of <paramref name="op"/> would hold: the price of one unit on the serving row, a size-priced
    /// tool at the document's canvas (or the requested size). Billing off answers <c>billingEnabled: false</c>.
    /// </summary>
    private async Task<ImageGraphicEstimateDto> EstimateAsync(
        ProjectAccessResult access, Op op, Guid? providerId, Guid? accountId, InferenceProviderKind kind,
        InferenceProviderCapability servedAs, string billedModel, bool platformManaged,
        Guid? documentId, int? width, int? height, CancellationToken ct)
    {
        if (_charges is not { Options.Enabled: true } || access.Project?.OrganizationId is not { } org)
            return ImageGraphicEstimateDto.From(false, 0, null);
        string? variant = null;
        if (platformManaged && OutputSizePricing.PricesByOutputMegapixels(kind, billedModel, op != Op.Generate, 0))
        {
            long? pixels = width is > 0 && height is > 0 ? (long)width.Value * height.Value : null;
            if (pixels is null && documentId is { } docId
                && await _db.EditImageDocuments.AsNoTracking()
                    .Where(d => d.Id == docId && d.ProjectId == access.Project.Id)
                    .Select(d => new { d.CanvasWidth, d.CanvasHeight }).FirstOrDefaultAsync(ct) is { CanvasWidth: > 0, CanvasHeight: > 0 } canvas)
            {
                pixels = (long)canvas.CanvasWidth!.Value * canvas.CanvasHeight!.Value;
            }

            if (pixels is { } px) variant = OutputSizePricing.MegapixelVariant(px);
        }

        long millis = await _charges.EstimateOneUnitAsync(new PriceRequest(
            providerId, accountId, kind, servedAs, billedModel, BilledUnitOf(servedAs), variant,
            platformManaged ? UsageBillingMode.Platform : UsageBillingMode.Byo, 1m), ct);
        return ImageGraphicEstimateDto.From(true, millis, await _charges.GetAvailableMillisAsync(org, ct));
    }

    /// <summary>
    /// The provider a tool runs on: its own capability's row, else (for the six split-out jobs) the ImageEdit
    /// default when that row's kind serves the job. <c>Missing</c> is the 409 to answer when there is none.
    /// </summary>
    private async Task<(ResolvedImageProvider? Image, ResolvedImageAnalysisProvider? Analysis, ActionResult? Missing)> ResolveProviderAsync(
        Op op, Guid? providerId, CancellationToken ct)
    {
        if (op == Op.RemoveBackground)
        {
            ResolvedImageAnalysisProvider? analysis;
            try
            {
                analysis = await _providerResolver.ResolveSegmentationAsync(providerId, ct);
            }
            catch (ModelUnpricedException unpriced)
            {
                return (null, null, ModelUnpricedResults.Conflict(unpriced));
            }

            return analysis is null
                ? (null, null, Conflict(new
                {
                    code = NoSegmentationProviderCode,
                    reason = "NO_PROVIDER",
                    canConfigure = CanConfigure(),
                    message = NoSegmentationProviderMessage
                }))
                : (null, analysis, null);
        }

        // An unpriced Platform model (ModelUnpricedException) is treated as absent: the ImageEdit fallback below still
        // applies, and only when nothing serves the job is the answer 409 MODEL_UNPRICED rather than "not set up".
        InferenceProviderCapability capability = CapabilityFor(op);
        ModelUnpricedException? unpricedJob = null;
        ResolvedImageProvider? image;
        try
        {
            image = await _providerResolver.ResolveImageJobAsync(capability, providerId, ct);
        }
        catch (ModelUnpricedException unpriced)
        {
            image = null;
            unpricedJob = unpriced;
        }

        // The one compatibility fallback: before each job had its own capability every tool resolved
        // ImageEdit, so a deployment that only has that row keeps working - but only for a job the
        // row's KIND can do. A MiniMax ImageEdit row, which documents no mask and no transform, is not
        // handed an upscale to fail; the user is told which capability is missing instead.
        if (image is null && ImageJobCapabilities.FallsBackToImageEdit(capability))
        {
            ResolvedImageProvider? legacy;
            try
            {
                legacy = await _providerResolver.ResolveImageJobAsync(InferenceProviderCapability.ImageEdit, providerId, ct);
            }
            catch (ModelUnpricedException unpriced)
            {
                legacy = null;
                unpricedJob ??= unpriced;
            }

            if (legacy is not null && ImageJobCapabilities.KindServes(legacy.Kind, capability))
            {
                image = legacy;
            }
        }

        if (image is null && unpricedJob is not null)
        {
            return (null, null, ModelUnpricedResults.Conflict(unpricedJob));
        }

        return image is null
            ? (null, null, Conflict(new
            {
                code = op == Op.Generate ? ImageGenerationProviderUnavailableCode : ImageEditProviderUnavailableCode,
                reason = "NO_PROVIDER",
                canConfigure = CanConfigure(),
                capability = capability.ToString(),
                message = NoProviderMessage(op, capability)
            }))
            : (image, null, null);
    }

    // ------------------------------------------------------------------ expand

    /// <summary>The editor's own expand keys: per-side margins of the layer's picture, or a target shape.</summary>
    private static readonly string[] ExpandEditorKeys = ["top", "right", "bottom", "left", "aspect"];

    /// <summary>
    /// The catalogued fal.ai expand endpoint <paramref name="model"/> names (empty = the catalogue's default),
    /// or <c>null</c> when it names none - the client then refuses it with its own sentence.
    /// </summary>
    private static FalImageEndpoint? ExpandEndpointFor(string model)
    {
        FalImageEndpoint? endpoint = model.Length == 0
            ? FalAiImageClient.Catalog.DefaultFor(FalImageJob.Expand)
            : FalAiImageClient.Catalog.Find(model);
        return endpoint is { Job: FalImageJob.Expand, ExpandBy: not FalExpandGeometry.None } ? endpoint : null;
    }

    /// <summary>
    /// Reads what an expand asks for: <c>params.aspect</c> ("9:16") or <c>params.top/right/bottom/left</c> (whole
    /// pixels of the layer's picture, 0 to 100000). One or the other, never both.
    /// </summary>
    private static bool TryReadExpandTarget(
        IReadOnlyDictionary<string, JsonElement>? parameters, out ExpandTarget? target, out string? problem)
    {
        target = null;
        problem = null;

        string? aspect = parameters is not null && TryGet(parameters, "aspect", out JsonElement a) ? a.ValueKind == JsonValueKind.String ? a.GetString() : "" : null;
        int[] margins = new int[4];
        bool anyMargin = false;
        string[] sides = ["left", "top", "right", "bottom"];
        for (int i = 0; i < sides.Length; i++)
        {
            if (parameters is null || !TryGet(parameters, sides[i], out JsonElement v)) continue;
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int n) || n is < 0 or > 100000)
            {
                problem = $"params.{sides[i]} must be a whole number of pixels from 0 to 100000.";
                return false;
            }

            margins[i] = n;
            anyMargin |= n > 0;
        }

        if (aspect is not null)
        {
            if (anyMargin)
            {
                problem = "Give either a shape (params.aspect) or pixels per side (params.top/right/bottom/left), not both.";
                return false;
            }

            target = ExpandTarget.ParseShape(aspect);
            if (target is null)
            {
                problem = "params.aspect must be a shape such as \"9:16\" (width:height, between 1:8 and 8:1).";
                return false;
            }

            return true;
        }

        if (!anyMargin)
        {
            problem = "Choose a shape to expand to (such as 9:16), or how far to expand on at least one side.";
            return false;
        }

        target = new ExpandTarget.Margins(margins[0], margins[1], margins[2], margins[3]);
        return true;
    }

    /// <summary>
    /// The size a stored picture is SHOWN at (EXIF orientation applied), read from its first bytes (a JPEG up to its frame header, at most 16 MiB), or
    /// <c>null</c> when it cannot be read. Used for a layer whose row states no size (an uploaded photo) and for
    /// the copy the browser uploads.
    /// </summary>
    private async Task<(int Width, int Height)?> ReadImageSizeAsync(Guid projectId, string key, CancellationToken ct)
    {
        try
        {
            // T-013 (F2): a JPEG whose frame header sits past the usual prefix is read on, bounded, to its frame.
            await using Stream stream = await _storage.DownloadAsync(projectId, key, ct);
            return await ImageHeader.ReadDisplaySizeAsync(stream, ImageHeader.MaxJpegScanBytes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Could not read the size of {Key} in project {ProjectId}: {Message}", key, projectId, ex.Message);
            return null;
        }
    }

    // ------------------------------------------------------------------ vendor dispatch

    private async Task<VendorResult> CallVendorAsync(
        Op op,
        EditImageOperationRequest request,
        string prompt,
        string negativePrompt,
        string model,
        string? sourceUrl,
        byte[]? maskBytes,
        string? maskUrl,
        List<string>? referenceUrls,
        List<long>? referencePixels,
        long? expectedPixels,
        EditImageDocument document,
        ResolvedImageProvider? imageProvider,
        ResolvedImageAnalysisProvider? analysisProvider,
        ImageExpandGeometry? expandGeometry,
        CancellationToken ct,
        SubjectWork? subjects = null)
    {
        if (op == Op.RemoveBackground)
        {
            IImageAnalysisClient analysis = _analysisClients.Get(analysisProvider!);
            SegmentationResult segmented = await analysis.SegmentAsync(
                new SegmentationRequest(
                    Model: model,
                    SourceImageUrl: sourceUrl!,
                    Variant: ParamString(request.Params, "variant") ?? BackgroundRemovalVariant,
                    MatteToo: true),
                ct);

            return new VendorResult(
                [new Output(segmented.Cutout, segmented.Matte, IsCutout: true)],
                segmented.VendorRequestId,
                segmented.ResolvedModel);
        }

        IImageClient client = _imageClients.Get(imageProvider!);
        int? seed = ParamInt(request.Params, "seed");
        string? aspect = ParamString(request.Params, "aspect") ?? ParamString(request.Params, "aspectRatio");
        string? negative = negativePrompt.Length == 0 ? null : negativePrompt;

        GeneratedImageSet set;
        switch (op)
        {
            case Op.Erase:
            case Op.Fill:
                // White = the region to change (ImageInpaintRequest's documented polarity, which is
                // exactly what the browser's brush mask is exported as). Erase carries no instruction
                // of its own; "background" is the neutral prompt that asks the model to continue
                // what surrounds the hole, and a vendor that requires a non-empty prompt gets one.
                set = await client.InpaintAsync(
                    new ImageInpaintRequest(
                        model,
                        op == Op.Erase && prompt.Length == 0 ? "background" : prompt,
                        sourceUrl!,
                        maskBytes!,
                        "image/png",
                        seed,
                        MaskUrl: maskUrl)
                    {
                        ExpectedOutputPixels = expectedPixels
                    },
                    ct);
                break;

            case Op.Instruct:
                // With subjects the layer stays picture 1 and the loose references follow it, so the subjects' pictures
                // start after them; one fixed sentence says which of them show which subject.
                set = await client.EditAsync(
                    new ImageEditRequest(
                        model,
                        subjects is null
                            ? prompt
                            : SubjectPictureInstruction.ForInstruct(
                                subjects.Slots(2 + ((referenceUrls?.Count ?? 0) - subjects.Pictures.Count)), prompt),
                        sourceUrl!, aspect, seed, ReferenceImageUrls: referenceUrls)
                    {
                        ReferenceImagePixels = referencePixels,
                        ExpectedOutputPixels = expectedPixels,
                        NoVendorRetention = subjects is { HasPerson: true }
                    },
                    ct);
                break;

            case Op.Generate when subjects is not null:
                // A NEW picture that shows the subjects: the instruction-edit model, its image array the subjects' own
                // pictures (the first is the picture it starts from), the customer's prompt inside the fixed frame.
                set = await client.EditAsync(
                    new ImageEditRequest(
                        model,
                        SubjectPictureInstruction.ForGenerate(subjects.Slots(1), prompt),
                        sourceUrl!,
                        subjects.GenerateAspect,
                        seed,
                        ReferenceImageUrls: referenceUrls)
                    {
                        ReferenceImagePixels = referencePixels,
                        ExpectedOutputPixels = expectedPixels,
                        NoVendorRetention = subjects.HasPerson
                    },
                    ct);
                break;

            case Op.Generate:
                set = await client.GenerateAsync(
                    new ReelBolt.Shared.Inference.ImageGenerationRequest(
                        model,
                        prompt,
                        aspect,
                        ParamInt(request.Params, "width"),
                        ParamInt(request.Params, "height"),
                        Count: 1,
                        ReferenceImageUrl: null,
                        Seed: seed)
                    {
                        ExpectedOutputPixels = expectedPixels
                    },
                    ct);
                break;

            default:
                {
                    // expand / replaceBackground / relight / upscale. The client forwards only the fields
                    // its endpoint documents and refuses the rest, so params go through untouched except
                    // for the seed, which has its own typed slot.
                    // A planned expand carries its geometry typed; the editor's own expand keys (margins per side
                    // of the layer's picture, or a shape) were turned into it and are not the endpoint's fields.
                    Dictionary<string, JsonElement>? forwarded = request.Params is null
                        ? null
                        : request.Params
                            .Where(p => !string.Equals(p.Key, "seed", StringComparison.OrdinalIgnoreCase))
                            .Where(p => expandGeometry is null || !ExpandEditorKeys.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
                            .ToDictionary(p => p.Key, p => p.Value);

                    set = await client.TransformAsync(
                        new ImageTransformRequest(
                            op switch
                            {
                                Op.Expand => ImageOperation.Expand,
                                Op.ReplaceBackground => ImageOperation.ReplaceBackground,
                                Op.Relight => ImageOperation.Relight,
                                _ => ImageOperation.Upscale
                            },
                            model,
                            sourceUrl!,
                            prompt.Length == 0 ? null : prompt,
                            negative,
                            forwarded is { Count: 0 } ? null : forwarded,
                            seed,
                            expandGeometry)
                        {
                            ExpectedOutputPixels = expectedPixels
                        },
                        ct);
                    break;
                }
        }

        return new VendorResult([.. set.Images.Select(i => new Output(i, null))], set.VendorRequestId, set.ResolvedModel);
    }

    // ------------------------------------------------------------------ rules

    private static Op? ParseOperation(string? name)
    {
        for (int i = 0; i < OperationNames.Length; i++)
        {
            if (string.Equals(OperationNames[i], name?.Trim(), StringComparison.OrdinalIgnoreCase)) return (Op)i;
        }

        return null;
    }

    private static bool PromptRequired(Op op) => op is Op.Fill or Op.ReplaceBackground or Op.Instruct or Op.Generate;

    /// <summary>
    /// An explicit model wins. Otherwise a <c>FalAi</c> row sends an EMPTY model (the client applies its
    /// per-operation default endpoint) and every other kind sends the row's model; background removal
    /// defaults to birefnet on a FalAi row because that is the endpoint whose Matting variant the
    /// contract names, whatever segmentation model the row itself was set up with.
    /// </summary>
    /// <summary>
    /// The most a hold of <paramref name="holdMillis"/> settles at: the hold plus the overdraft headroom it was admitted
    /// with (<c>NonWorkflowChargeService.SettleAsync</c> caps a settlement there).
    /// </summary>
    internal static long SettleCeilingMillis(long holdMillis, NonWorkflowChargeOptions options) =>
        holdMillis + Math.Max(0, Math.Min(holdMillis * options.OverdraftTolerancePercent / 100, options.OverdraftFloorCredits * 1000));

    /// <summary>
    /// The pixels a per-megapixel job is expected to deliver, from what the editor knows before submit, each side rounded
    /// UP to the vendors' 32-pixel grid (<see cref="OutputSizePricing.GridPixels"/>), or null when it cannot be computed:
    /// generate, the requested size; expand, the planned canvas; upscale, the source scaled by the factor (or to the
    /// target resolution, read as the SHORT side so it never under-sizes); relight with a preset <c>image_size</c>, at
    /// least that preset's 1024 x 1024; every other job (erase, fill, instruct), the source's size.
    /// </summary>
    private async Task<long?> ExpectedOutputPixelsAsync(
        Op op, EditImageOperationRequest request, Guid projectId, EditImageLayer? source, ExpandWork? expand, CancellationToken ct)
    {
        switch (op)
        {
            case Op.Generate:
                return ParamInt(request.Params, "width") is int w and > 0 && ParamInt(request.Params, "height") is int h and > 0
                    ? OutputSizePricing.GridPixels(w, h)
                    : null;
            case Op.Expand:
                return expand?.Plan.Geometry is { } g && g.CanvasWidth > 0 && g.CanvasHeight > 0
                    ? OutputSizePricing.GridPixels(g.CanvasWidth, g.CanvasHeight)
                    : null;
        }

        if (source is null || await SourceSizeAsync(projectId, source, ct) is not { } size) return null;

        switch (op)
        {
            case Op.Upscale:
                if (string.Equals(ParamString(request.Params, "upscale_mode"), "target", StringComparison.OrdinalIgnoreCase))
                {
                    // Rework 6: a target below the source's short side never sizes the hold below the source.
                    double scale = Math.Max(1d, (double)UpscaleTargetShortSide(request) / Math.Min(size.Width, size.Height));
                    return OutputSizePricing.GridPixels(size.Width * scale, size.Height * scale);
                }

                double factor = UpscaleFactor(request);
                return factor > 0 ? OutputSizePricing.GridPixels(size.Width * factor, size.Height * factor) : null;
            case Op.Relight when ParamString(request.Params, "image_size") is not null:
                // fal's image_size presets are at most 1024 x 1024; the larger of that and the source never under-sizes.
                return Math.Max(OutputSizePricing.GridPixels(size.Width, size.Height), 1024L * 1024);
            default:
                return OutputSizePricing.GridPixels(size.Width, size.Height);
        }
    }

    /// <summary>
    /// T-013 (F2): the most a size-priced job can deliver by its vendor's documentation, for a job whose expected size is
    /// unknown: the endpoint's maximum output, else (upscale) its maximum input scaled by the factor; null when neither is
    /// documented (the job is then held at the variant-less, largest, row).
    /// </summary>
    private static long? DocumentedMaxOutputPixels(Op op, InferenceProviderKind kind, string model, EditImageOperationRequest request)
    {
        if (OutputSizePricing.MaxOutputPixels(kind, model) is { } output and > 0) return output;
        if (op == Op.Upscale && OutputSizePricing.MaxInputPixels(kind, model) is { } input and > 0)
        {
            double factor = UpscaleFactor(request);
            return factor > 0 ? (long)Math.Ceiling(input * factor * factor) : null;
        }

        return null;
    }

    /// <summary>The upscale factor the request asks for (<c>upscale_factor</c> or the editor's <c>factor</c>), else the vendor default 2.</summary>
    private static double UpscaleFactor(EditImageOperationRequest request) =>
        ParamNumber(request.Params, "upscale_factor") ?? ParamNumber(request.Params, "factor") ?? 2;

    /// <summary>The target short side of a target-mode upscale (<c>720p</c> ... <c>2160p</c>); an unreadable value is the largest, 2160.</summary>
    private static int UpscaleTargetShortSide(EditImageOperationRequest request)
    {
        string resolution = ParamString(request.Params, "target_resolution") ?? "1080p";
        return int.TryParse(resolution.TrimEnd('p', 'P'), System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out int target) && target is > 0 and <= 2160
            ? target
            : 2160;
    }

    /// <summary>
    /// The source's pixel size as its stored picture's header states it (the larger of that and its recorded size), or
    /// null when the header cannot be read: a recorded size alone is not trusted to price a job (F2).
    /// </summary>
    private async Task<(int Width, int Height)?> SourceSizeAsync(Guid projectId, EditImageLayer source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source.StorageKey)) return null;
        if (await ReadImageSizeAsync(projectId, source.StorageKey, ct) is not { } header) return null;
        return source.Width is > 0 && source.Height is > 0 && (long)source.Width.Value * source.Height.Value > (long)header.Width * header.Height
            ? (source.Width.Value, source.Height.Value)
            : header;
    }

    private static string ChooseModel(string? requested, Op op, InferenceProviderKind kind, string rowModel)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim();
        if (kind != InferenceProviderKind.FalAi) return rowModel;
        if (op == Op.RemoveBackground) return BackgroundRemovalModel;

        return ImageJobCapabilities.FalModelFor(ImageJobCapabilities.JobFor(CapabilityFor(op))!.Value, rowModel);
    }

    /// <summary>The 400 body for a caller-chosen Platform model outside the priced catalog (T-026 finding 6, T-110).</summary>
    internal static object UnpricedRequestedModelBody(string model) => new
    {
        code = OperationInvalidCode,
        message = $"The model \"{model}\" is not one of the priced picture models set up for this tool, so it cannot be used here. "
                  + "Leave the model out to use the one set up for it, or ask an administrator to add and price that model."
    };

    /// <summary>
    /// Whether <paramref name="modelCode"/> is an enabled platform capability of <paramref name="capability"/> on a
    /// <paramref name="kind"/> account with a price in force for every unit it meters
    /// (<see cref="ModelCapabilityView.IsFullyPriced"/>), so the call prices on its own row, never the dearest fallback.
    /// </summary>
    /// <remarks>T-110 (L2): shared with the subject cut-out (<c>EditImageDocumentsController.SegmentSubject</c>), one rule for both.</remarks>
    internal static async Task<bool> IsPricedPlatformModelAsync(
        InferenceApiDbContext db, InferenceProviderKind kind, InferenceProviderCapability capability, string modelCode, CancellationToken ct)
    {
        DateTime at = DateTime.UtcNow;
        string wanted = modelCode.Trim();
        var rows = await db.ModelCapabilities
            .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .AsNoTracking()
            .Where(c => c.OrganizationId == null && c.Capability == capability && c.Model.Provider.Kind == kind
                        && c.IsEnabled && c.Model.IsEnabled && c.Model.Provider.IsEnabled)
            .Select(c => new
            {
                c.Model.ModelCode,
                Units = c.Prices
                    .Where(p => p.EffectiveFrom <= at && (p.EffectiveTo == null || p.EffectiveTo > at))
                    .Select(p => p.Unit)
                    .ToList()
            })
            .ToListAsync(ct);
        return rows.Any(r => string.Equals(r.ModelCode?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)
                             && ModelCapabilityView.IsFullyPriced(capability, r.Units));
    }

    /// <summary>What a tool does, as the end of "The AI service could not ...".</summary>
    private static string Describe(Op op) => op switch
    {
        Op.Erase => "remove the painted area",
        Op.Fill => "fill in the painted area",
        Op.Expand => "expand the picture",
        Op.ReplaceBackground => "replace the background",
        Op.Relight => "relight the picture",
        Op.Upscale => "upscale the picture",
        Op.Instruct => "make that edit",
        Op.Generate => "create the picture",
        _ => "remove the background"
    };

    /// <summary>The capability each tool resolves (see the class remarks).</summary>
    private static InferenceProviderCapability CapabilityFor(Op op) => op switch
    {
        Op.Erase => InferenceProviderCapability.ImageErase,
        Op.Fill => InferenceProviderCapability.ImageFill,
        Op.Expand => InferenceProviderCapability.ImageExpand,
        Op.ReplaceBackground => InferenceProviderCapability.ImageBackgroundReplace,
        Op.Relight => InferenceProviderCapability.ImageRelight,
        Op.Upscale => InferenceProviderCapability.ImageUpscale,
        Op.Instruct => InferenceProviderCapability.ImageEdit,
        Op.Generate => InferenceProviderCapability.ImageGeneration,
        _ => InferenceProviderCapability.Segmentation
    };

    /// <summary>
    /// The 409 sentence when no row serves a tool: what is missing in the user's words, and the capability
    /// an administrator adds on the AI providers page.
    /// </summary>
    private static string NoProviderMessage(Op op, InferenceProviderCapability capability)
    {
        string what = op switch
        {
            Op.Erase => "removing objects",
            Op.Fill => "filling in a painted area",
            Op.Expand => "expanding pictures",
            Op.ReplaceBackground => "replacing backgrounds",
            Op.Relight => "relighting pictures",
            Op.Upscale => "upscaling pictures",
            Op.Instruct => "editing pictures from a description",
            _ => "creating pictures"
        };

        return $"No AI service is set up for {what} yet. An administrator can add one on the AI providers page " +
               $"(capability '{capability}'; fal.ai serves it).";
    }

    /// <summary>
    /// The tools that change a picture's pixels in place and so must not lose its transparency. Expand and
    /// replace background are not here: filling in the background is what they are for. Generate has no
    /// source, and background removal makes transparency rather than keeping it.
    /// </summary>
    private static bool KeepsTransparency(Op op) => op is Op.Erase or Op.Fill or Op.Instruct or Op.Relight or Op.Upscale;

    /// <summary>
    /// The alpha a layer is SEEN with: its bitmap's own alpha, times its matte's when it is a cut-out with
    /// one (the browser draws a cut-out <c>destination-in</c> its matte). <c>null</c> when the layer is
    /// fully opaque, not a PNG, too large to read back, or unreadable — every one of which means "nothing to
    /// keep", never a failed operation: the vendor's result is already paid for.
    /// </summary>
    private async Task<AlphaPlane?> ReadSourceAlphaAsync(Guid projectId, EditImageLayer source, CancellationToken ct)
    {
        // Only a PNG can carry alpha here (a JPEG original cannot, and is not downloaded for nothing).
        bool mayHaveAlpha = string.IsNullOrWhiteSpace(source.MimeType)
            || source.MimeType.Contains("png", StringComparison.OrdinalIgnoreCase);
        AlphaPlane? alpha = mayHaveAlpha ? await ReadAlphaAsync(projectId, source.StorageKey, ct) : null;

        if (source.Kind == EditImageLayerKind.Cutout && !string.IsNullOrWhiteSpace(source.MatteStorageKey)
            && await ReadAlphaAsync(projectId, source.MatteStorageKey, ct) is { } matte && matte.HasTransparency)
        {
            alpha = alpha is null ? matte : alpha.Multiply(matte);
        }

        return alpha is { HasTransparency: true } ? alpha : null;
    }

    private async Task<AlphaPlane?> ReadAlphaAsync(Guid projectId, string key, CancellationToken ct)
    {
        try
        {
            StoredObjectMetadata? meta = await _storage.GetMetadataAsync(projectId, key, ct);
            if (meta is null || meta.SizeBytes < 8 || meta.SizeBytes > MaxAlphaSourceBytes) return null;

            await using Stream stream = await _storage.DownloadAsync(projectId, key, ct);
            using MemoryStream buffer = new();
            await stream.CopyToAsync(buffer, ct);
            byte[] bytes = buffer.ToArray();
            return PngAlpha.IsPng(bytes) ? PngAlpha.DecodeAlpha(bytes) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Carries <paramref name="alpha"/> over to <paramref name="output"/>: baked into the result's own alpha
    /// channel when the result is a PNG this can re-encode, otherwise stored as a separate matte that the
    /// browser draws the layer through. A result whose shape differs from the source's (a model that
    /// cropped or re-framed) cannot be lined up with the source's alpha, and is kept as it came back, with a
    /// notice saying so in plain words.
    /// </summary>
    private static (Output Output, string? Notice) KeepTransparency(Output output, AlphaPlane alpha)
    {
        GeneratedImage image = output.Image;
        int? width = image.Width;
        int? height = image.Height;
        if ((width is null || height is null) && PngAlpha.IsPng(image.Bytes) && image.Bytes.Length >= 24)
        {
            width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(image.Bytes.AsSpan(16, 4));
            height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(image.Bytes.AsSpan(20, 4));
        }

        if (width is not > 0 || height is not > 0)
        {
            return (output, null);
        }

        // The vendor's header is not trusted: past the reader's own caps nothing is resized or encoded.
        if (!PngAlpha.WithinCaps(width.Value, height.Value))
        {
            return (output, "The AI returned a picture too large to carry the transparent background over to. " +
                            "Hide or delete the parts you do not want.");
        }

        double sourceAspect = (double)alpha.Width / alpha.Height;
        double outputAspect = (double)width.Value / height.Value;
        if (Math.Abs(outputAspect - sourceAspect) / sourceAspect > 0.02)
        {
            return (output, "The AI returned a picture with a different shape, so the transparent background of the " +
                            "original could not be carried over. Hide or delete the parts you do not want.");
        }

        if (PngAlpha.IsPng(image.Bytes) && PngAlpha.ApplyAlpha(image.Bytes, alpha) is { } baked)
        {
            GeneratedImage withAlpha = new(baked, "image/png", image.RevisedPrompt, image.VendorUrl)
            {
                Width = width,
                Height = height
            };
            return (output with { Image = withAlpha }, null);
        }

        // Not a PNG (a JPEG or WebP answer) or not one this can re-encode: keep the transparency as a matte
        // the browser applies. The layer becomes a cut-out, which is how the compositor knows to use it.
        // At the RESULT's size: the browser stretches a matte over its layer anyway, and encoding one at a
        // 24-megapixel cut-out's own size would take seconds for no visible gain.
        AlphaPlane sized = alpha.Resize(width.Value, height.Value);
        GeneratedImage matte = new(PngAlpha.EncodeMatte(sized), "image/png") { Width = sized.Width, Height = sized.Height };
        return (output with { Matte = matte, IsCutout = true }, null);
    }


    /// <summary>
    /// For the operations whose request has typed fields only, an unrecognised param is refused rather
    /// than dropped (the house rule for vendor requests); the transform operations forward everything
    /// to a client that applies the same rule against its endpoint's schema.
    /// </summary>
    private static string? ValidateParams(Op op, Dictionary<string, JsonElement>? parameters)
    {
        if (parameters is null || parameters.Count == 0) return null;

        string[]? allowed = op switch
        {
            Op.Erase or Op.Fill => ["seed"],
            Op.Instruct => ["seed", "aspect", "aspectRatio"],
            Op.Generate => ["seed", "aspect", "aspectRatio", "width", "height"],
            Op.RemoveBackground => ["variant"],
            _ => null
        };

        if (allowed is not null)
        {
            string? unknown = parameters.Keys.FirstOrDefault(k => !allowed.Contains(k, StringComparer.OrdinalIgnoreCase));
            if (unknown is not null)
                return $"params.{unknown} is not used by {OperationNames[(int)op]}; accepted: {string.Join(", ", allowed)}.";
        }

        foreach (string numeric in new[] { "seed", "width", "height" })
        {
            if (parameters.TryGetValue(numeric, out JsonElement value)
                && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int n) || (numeric != "seed" && n is < 1 or > MaxExportSide)))
            {
                return numeric == "seed"
                    ? "params.seed must be an integer."
                    : $"params.{numeric} must be an integer between 1 and {MaxExportSide}.";
            }
        }

        return null;
    }

    private static string DefaultRole(Op op, EditImageLayer? parent) => op switch
    {
        Op.Generate => EditImageLayerRoles.Background,
        Op.RemoveBackground => EditImageLayerRoles.Subject,
        _ => parent?.Role ?? EditImageLayerRoles.Overlay
    };

    private static string DefaultLabel(Op op, EditImageLayer? parent)
    {
        string name = parent?.Label ?? "image";
        return op switch
        {
            Op.Erase => $"Erased: {name}",
            Op.Fill => $"Filled: {name}",
            Op.Expand => $"Expanded: {name}",
            Op.ReplaceBackground => $"New background: {name}",
            Op.Relight => $"Relit: {name}",
            Op.Upscale => $"Upscaled: {name}",
            Op.Instruct => $"Edited: {name}",
            Op.Generate => "Generated background",
            _ => $"Cut-out: {name}"
        };
    }

    /// <summary>
    /// Where an output sits (docs/design/thumbnail-editor.md section 5): the same transform as the layer
    /// it was made from, corrected so the picture does not visibly jump.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><b>Same size as the source</b> (erase, fill, relight, replace background,
    /// instruct, remove background): the source's transform, unchanged.</description></item>
    /// <item><description><b>A different pixel size</b> (an upscale, or a vendor that resized): the scale
    /// is multiplied by <c>sourceWidth / outputWidth</c>, so the layer keeps the width it was drawn at.
    /// When the source's width is unknown (a <c>Source</c> layer has none) an upscale falls back to
    /// dividing by <c>params.factor</c>.</description></item>
    /// <item><description><b>Expand</b>: the new bitmap grows up and to the left by
    /// <c>params.top</c>/<c>params.left</c> source pixels, so the top-left corner moves by
    /// <c>-left * scale</c>, <c>-top * scale</c> and the original pixels stay where they were. Applied
    /// only for an unrotated, unflipped layer; otherwise the transform is kept as is.</description></item>
    /// <item><description><b>Generate</b>: a backdrop. With a canvas size and an output size known it is
    /// scaled to COVER the canvas and centred; otherwise the identity transform.</description></item>
    /// </list>
    /// </remarks>
    private static EditImageLayerTransformDto OutputTransform(
        Op op,
        EditImageLayer? parent,
        GeneratedImage output,
        Dictionary<string, JsonElement>? parameters,
        EditImageDocument document,
        ExpandWork? expand = null)
    {
        if (op == Op.Generate)
        {
            if (document.CanvasWidth is { } cw && document.CanvasHeight is { } ch
                && output.Width is { } w && output.Height is { } h && w > 0 && h > 0)
            {
                double cover = Math.Max((double)cw / w, (double)ch / h);
                return new EditImageLayerTransformDto(
                    X: (cw - (w * cover)) / 2,
                    Y: (ch - (h * cover)) / 2,
                    Scale: cover);
            }

            return new EditImageLayerTransformDto();
        }

        EditImageLayerTransformDto t = ReadTransform(parent?.TransformJson ?? "{}");

        if (op == Op.Expand && expand is not null)
        {
            ImageExpandGeometry g = expand.Plan.Geometry;
            (double x, double y, double scale) = ExpandPlanner.PlaceResult(
                t.X, t.Y, t.Scale, expand.SourceWidth, g, output.Width ?? g.CanvasWidth, output.Height ?? g.CanvasHeight);

            // Rotated or mirrored, the corner the result grows from is not the top-left one: the size is still
            // right, the position is left for the user, as before.
            return t.Rotation == 0 && !t.FlipX && !t.FlipY ? t with { X = x, Y = y, Scale = scale } : t with { Scale = scale };
        }

        if (op == Op.Expand)
        {
            if (t.Rotation == 0 && !t.FlipX && !t.FlipY)
            {
                double left = ParamNumber(parameters, "left") ?? 0;
                double top = ParamNumber(parameters, "top") ?? 0;
                return t with { X = t.X - (left * t.Scale), Y = t.Y - (top * t.Scale) };
            }

            return t;
        }

        if (parent?.Width is { } before && output.Width is { } after && before > 0 && after > 0 && before != after)
            return t with { Scale = t.Scale * before / after };

        if (op == Op.Upscale && ParamNumber(parameters, "factor") is { } factor && factor > 0)
            return t with { Scale = t.Scale / factor };

        return t;
    }

    private static int? ParamInt(IReadOnlyDictionary<string, JsonElement>? parameters, string name) =>
        parameters is not null
        && TryGet(parameters, name, out JsonElement v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out int n)
            ? n
            : null;

    private static double? ParamNumber(IReadOnlyDictionary<string, JsonElement>? parameters, string name) =>
        parameters is not null
        && TryGet(parameters, name, out JsonElement v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetDouble(out double d)
            ? d
            : null;

    private static string? ParamString(IReadOnlyDictionary<string, JsonElement>? parameters, string name) =>
        parameters is not null
        && TryGet(parameters, name, out JsonElement v)
        && v.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static bool TryGet(IReadOnlyDictionary<string, JsonElement> parameters, string name, out JsonElement value)
    {
        foreach (KeyValuePair<string, JsonElement> pair in parameters)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private async Task DiscardAsync(Guid projectId, IEnumerable<string> keys)
    {
        foreach (string key in keys)
        {
            try { await _storage.DeleteAsync(projectId, key, CancellationToken.None); }
            catch (Exception) { /* nothing more to do; the failure that brought us here is the answer */ }
        }
    }
}
