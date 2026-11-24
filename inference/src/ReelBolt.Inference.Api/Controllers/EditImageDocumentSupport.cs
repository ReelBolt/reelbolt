using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// What the image-document controllers share: the row-to-DTO mapping, the layer-stack arithmetic, the
/// storage-key shapes and the validation of the editor's JSON payloads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing in here authorizes anything, on purpose.</b> <c>TenantBoundaryInventoryTests</c> counts the
/// access-service calls in each controller's source and requires exactly one per action, so a helper that
/// performed the check for several actions would be a guard that vanishes the moment a new action is added
/// beside them. Every action of <see cref="EditImageDocumentsController"/>,
/// <see cref="EditImageOperationsController"/> and <see cref="EditImageAssetsController"/> therefore
/// performs its own check BEFORE calling anything here; the helpers only ever receive an already
/// authorized <c>projectId</c>, and every query still constrains the parent (a document in another
/// project is a 404).
/// </para>
/// </remarks>
internal static class EditImageDocumentSupport
{
    // ------------------------------------------------------------------ error vocabulary

    /// <summary>The target is not an image, or not in this project.</summary>
    public const string UnsupportedSourceCode = "IMAGE_SOURCE_UNSUPPORTED";

    /// <summary>Nothing can segment: no provider row, or one that cannot be resolved.</summary>
    public const string NoSegmentationProviderCode = "SEGMENTATION_PROVIDER_UNAVAILABLE";

    /// <summary>The vendor refused or failed a segmentation. Distinct from a 500 because the fix is a provider change.</summary>
    public const string SegmentationFailedCode = "SEGMENTATION_FAILED";

    /// <summary>A stale <c>baseRevision</c>, or a write that lost a race. Nothing is written.</summary>
    public const string RevisionConflictCode = "REVISION_CONFLICT";

    public const string OperationTimeoutCode = "OPERATION_TIMEOUT";
    public const string OperationUnsupportedCode = "OPERATION_UNSUPPORTED";
    public const string MaskRequiredCode = "MASK_REQUIRED";
    public const string LayerHasNoBitmapCode = "LAYER_HAS_NO_BITMAP";

    /// <summary>The 409 sentence when no Segmentation row exists, in the user's words plus what an admin adds.</summary>
    public const string NoSegmentationProviderMessage =
        "No AI service is set up for cutting out subjects yet. An administrator can add one on the AI providers " +
        "page (capability 'Segmentation'; fal.ai serves it).";

    /// <summary>
    /// A vendor's refusal or failure, said plainly first: what did not happen, then the vendor's own words
    /// (kept, because they are what tells an administrator which setting to change), bounded so a vendor's
    /// echo of a whole request body never reaches the screen.
    /// </summary>
    public static string VendorFailureMessage(string whatFailed, string? vendorSays)
    {
        string detail = ScrubUrls(vendorSays).Trim();
        if (detail.Length > 400) detail = detail[..400] + "...";
        return detail.Length == 0
            ? $"The AI service could not {whatFailed}. Nothing was added to your picture."
            : $"The AI service could not {whatFailed}. Nothing was added to your picture. It said: {detail}";
    }

    /// <summary>
    /// The HTTP status of a vendor's refusal or failure, and of a vendor that did not answer within the
    /// operation bound: <c>424 Failed Dependency</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT 502 or 504. Cloudflare, in front of every cloud deployment, replaces the body of an
    /// origin 502 or 504 with its own HTML error page, so the plain-language reason this API writes never
    /// reached the editor (or the logs: the owner saw only an opaque "Bad Gateway"). 424 says what happened
    /// — the request failed because something it depended on failed — and its JSON body passes through. The
    /// UI branches on the <c>code</c>, never on the status.
    /// </remarks>
    public const int VendorFailureStatus = StatusCodes.Status424FailedDependency;

    /// <summary>
    /// 422: the layer's picture is larger than the AI service takes, so the browser must send a copy of the
    /// size the body names (<c>width</c>, <c>height</c>) and run the operation again with its <c>sourceKey</c>.
    /// Nothing was sent to a vendor and nothing was spent.
    /// </summary>
    public const string SourceTooLargeCode = "SOURCE_TOO_LARGE";

    /// <summary>The largest copy of a layer the browser may upload for an operation (purpose <c>source</c>): 48 MiB.</summary>
    public const long MaxSourceCopyBytes = 48L * 1024 * 1024;

    private static readonly Regex UrlQuery = new(@"(https?://[^\s""'<>?]+)\?[^\s""'<>]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Drops the query string of every URL in a vendor message. Vendors echo the request in a validation
    /// error, and the request carries presigned GETs whose query IS the credential; the path is kept, because
    /// it is what tells somebody which input was meant.
    /// </summary>
    public static string ScrubUrls(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : UrlQuery.Replace(text, "$1?…");

    /// <summary>A bounded, URL-scrubbed copy of a vendor exception for the server log.</summary>
    public static string VendorLogDetail(Exception ex)
    {
        string text = ex.GetType().Name + ": " + ScrubUrls(ex.Message);
        return text.Length > 1000 ? text[..1000] + "..." : text;
    }

    /// <summary>A subject cut-out with no layer named, on a document whose original image is gone.</summary>
    public const string NoSourceImageCode = "NO_SOURCE_IMAGE";

    /// <summary>404 code for a layer whose stored object no longer exists (served as a placeholder by the editor).</summary>
    public const string LayerImageMissingCode = "LAYER_IMAGE_MISSING";
    public const string ExportKeyInvalidCode = "EXPORT_KEY_INVALID";

    /// <summary>The vendor refused or failed an operation (the operations twin of <see cref="SegmentationFailedCode"/>).</summary>
    public const string OperationFailedCode = "OPERATION_FAILED";

    /// <summary>The operation's own request was malformed or refused by the client's schema check.</summary>
    public const string OperationInvalidCode = "OPERATION_INVALID";

    // ------------------------------------------------------------------ limits

    /// <summary>The largest brush mask the editor may upload: 12 MiB (a mask is a black-and-white PNG).</summary>
    public const long MaxEditorUploadBytes = 12L * 1024 * 1024;

    /// <summary>
    /// The largest export the editor may upload: 200 MiB. Larger than a mask because an export can be a
    /// 4K picture or a LAYERED file (Photoshop, OpenRaster) carrying one full bitmap per layer. It goes
    /// to storage by presigned PUT, never through nginx, and the plan's storage quota is still checked
    /// against the real size before anything is recorded.
    /// </summary>
    public const long MaxExportUploadBytes = 200L * 1024 * 1024;

    /// <summary>
    /// The file types an export may be, by content type, with the extension its storage key and file
    /// name carry. The browser renders every one of them (the server never rasterises); the two layered
    /// ones keep the editor's layers for Photoshop (<c>.psd</c>) and for GIMP and Krita (<c>.ora</c>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ExportFormats =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/png"] = ".png",
            ["image/jpeg"] = ".jpg",
            ["image/webp"] = ".webp",
            ["image/avif"] = ".avif",
            ["image/vnd.adobe.photoshop"] = ".psd",
            ["image/openraster"] = ".ora"
        };

    /// <summary>
    /// The layered file types: images, but not ones a browser can draw, so they can never be a layer
    /// document's source or be placed as a layer (see <see cref="IsImage"/>).
    /// </summary>
    public static readonly IReadOnlySet<string> LayeredImageTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/vnd.adobe.photoshop", "image/openraster" };

    /// <summary>The canonical content type for an export extension (<c>.jpeg</c> reads as <c>.jpg</c>), or null.</summary>
    public static string? ExportContentTypeForExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        string ext = extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : extension;
        foreach ((string type, string known) in ExportFormats)
        {
            if (known.Equals(ext, StringComparison.OrdinalIgnoreCase)) return type;
        }

        return null;
    }

    public const int MaxCanvasSide = 8192;
    public const int MinCanvasSide = 16;
    public const int MaxExportSide = 16384;

    public const int MaxPointPrompts = 64;
    public const int MaxBoxPrompts = 16;
    public const int MaxSubjectMasks = 16;
    public const int DefaultMultiMasks = 8;

    public const int MaxTextLength = 2000;

    /// <summary>The camelCase JSON the API speaks; used for every jsonb column this feature writes.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Same as <see cref="Json"/> but writes nulls, for payloads whose KEY SET is part of the contract
    /// (a layer's provenance is always <c>{ operation, providerKind, model, prompt, vendorRequestId,
    /// parentLayerId }</c>, with <c>null</c> where a value does not apply).
    /// </summary>
    public static readonly JsonSerializerOptions JsonWithNulls = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------ keys

    /// <summary>
    /// The object-key prefix of everything the editor uploads for one document:
    /// <c>projects/{projectId}/agentFiles/image-editor/{documentId}/</c>.
    /// </summary>
    public static string EditorPrefix(Guid projectId, Guid documentId) =>
        $"{ProjectFilePath.BuildStoragePrefix(projectId, "agentFiles")}image-editor/{documentId}/";

    /// <summary>
    /// Whether <paramref name="key"/> is exactly <c>{prefix}{purpose}-{guid}{extension}</c> for this
    /// document — the only shape <c>assets/upload-url</c> ever issues. A <c>mask</c> is always
    /// <c>.png</c>; an <c>export</c> carries the extension of one of the <see cref="ExportFormats"/>; a
    /// <c>source</c> (a smaller copy of a layer for an AI service that does not take the original) is
    /// <c>.png</c> or <c>.jpg</c>. A pure string test: no separators beyond the prefix's own, so it cannot name another project's,
    /// another document's or a parent directory's object.
    /// </summary>
    public static bool IsIssuedEditorKey(string? key, Guid projectId, Guid documentId, string purpose)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        string prefix = $"{EditorPrefix(projectId, documentId)}{purpose}-";
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) return false;

        Match match = IssuedTail.Match(key[prefix.Length..]);
        if (!match.Success) return false;

        string extension = match.Groups["ext"].Value;
        return purpose switch
        {
            "export" => ExportFormats.Values.Contains(extension, StringComparer.Ordinal),
            "source" => extension is ".png" or ".jpg",
            _ => extension == ".png"
        };
    }

    /// <summary>
    /// The content type an issued export key was issued for, read back from its extension (null when the
    /// key carries none of the <see cref="ExportFormats"/>). Only meaningful after
    /// <see cref="IsIssuedEditorKey"/> accepted the key.
    /// </summary>
    public static string? ExportContentTypeOfKey(string key) => ExportContentTypeForExtension(Path.GetExtension(key));

    private static readonly Regex IssuedTail = new(
        @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(?<ext>\.[a-z]{3,4})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string ExtensionFor(string? contentType) => contentType?.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".png"
    };

    /// <summary>
    /// Whether a project file can be drawn by the editor: an <c>image/*</c> type that is not one of the
    /// <see cref="LayeredImageTypes"/>. A Photoshop or OpenRaster file is an image to a person but not to
    /// a browser or a vendor, so it is refused as a document source and as a placed layer rather than
    /// becoming a layer that never loads.
    /// </summary>
    public static bool IsImage(string? mimeType) =>
        !string.IsNullOrWhiteSpace(mimeType)
        && mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        && !LayeredImageTypes.Contains(mimeType.Split(';')[0].Trim());

    // ------------------------------------------------------------------ loading

    /// <summary>
    /// The document with its layers, constrained to the project. <paramref name="tracked"/> false is for
    /// the read-only phase before a long vendor call, so the entity the write phase mutates is a FRESH
    /// load and a concurrent edit during the vendor wait does not turn into a spurious concurrency failure.
    /// </summary>
    /// <summary>
    /// True when a layer came from a project file that is now quarantined. A layer's pixels are a
    /// private copy, so the layer's own key can outlive a successful move: the source link is what
    /// makes the copy evidence too. One helper for every path that could presign or serve a layer.
    /// </summary>
    internal static async Task<bool> IsLayerSourceQuarantinedAsync(
        InferenceApiDbContext db, Guid projectId, EditImageLayer? layer, CancellationToken ct)
    {
        if (layer?.SourceProjectFileId is not { } sourceFileId) return false;
        return await db.ProjectFiles.AnyAsync(
            f => f.Id == sourceFileId && f.ProjectId == projectId
                 && f.CsamStatus == CsamScanStatus.Quarantined, ct);
    }

    public static async Task<EditImageDocument?> LoadDocumentAsync(
        InferenceApiDbContext db, Guid projectId, Guid documentId, CancellationToken ct, bool tracked = true)
    {
        IQueryable<EditImageDocument> query = db.EditImageDocuments.Include(d => d.Layers);
        if (!tracked) query = query.AsNoTracking();

        return await query.FirstOrDefaultAsync(d => d.Id == documentId && d.ProjectId == projectId, ct);
    }

    // ------------------------------------------------------------------ design-system defaults

    /// <summary>The font a text layer uses when no brand default applies (the legacy shape).</summary>
    public const string LegacyFontFamily = "Chakra Petch";

    /// <summary>The text colour a text layer uses when no brand default applies.</summary>
    public const string LegacyTextColor = "#ffffff";

    /// <summary>The outline colour a text layer uses when no brand default applies.</summary>
    public const string LegacyStrokeColor = "#000000";

    /// <summary>The weight every editor default carries, brand or not (SPEC 16.3).</summary>
    public const int EditorDefaultFontWeight = 700;

    /// <summary>
    /// SPEC 16.3: the editor defaults the project's current approved design system yields, or null
    /// when there is no usable design member (no system, no approved current version, or a document
    /// that will not parse) — the caller then answers 204 and keeps today's defaults. Never throws.
    /// </summary>
    public static async Task<EditorDefaultsResponse?> LoadEditorDefaultsAsync(
        InferenceApiDbContext db, Guid projectId, CancellationToken ct)
    {
        DesignSystem? system = await db.DesignSystems
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Kind == DesignSystemKinds.Design, ct);
        if (system?.CurrentVersionId is not { } currentVersionId)
            return null;

        DesignSystemVersion? version = await db.DesignSystemVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                v => v.Id == currentVersionId
                    && v.DesignSystemId == system.Id
                    && v.State == DesignVersionState.Approved,
                ct);
        if (version is null)
            return null;

        JsonObject? design = ParseObject(version.DocumentJson);
        if (design is null)
            return null;

        IReadOnlyList<string> families = BrandFontMatcher.RequestedFamilies(design);
        string? font = BrandFontMatcher.NearestEditorFont(families, BrandFontMatcher.EditorFontFamilies);

        var warnings = new List<string>();
        if (font is not null)
        {
            string requested = families.FirstOrDefault(f => f.Length > 0) ?? string.Empty;
            if (requested.Length > 0 && !string.Equals(requested, font, StringComparison.OrdinalIgnoreCase))
                warnings.Add($"font_substituted:{requested}->{font}");
        }

        return new EditorDefaultsResponse(font, EditorDefaultFontWeight, ReadColorText(design), LegacyStrokeColor, warnings);
    }

    /// <summary>
    /// Fills the design member's font, colour and outline colour into a text payload, but only for
    /// the fields the caller left at the legacy default. The wire DTO cannot tell an omitted field
    /// from one sent with its default value, so "still the legacy default" is what "omitted" means
    /// here; an explicit non-default value always wins. This is what lets the <c>layers/text</c>
    /// route supply the brand face even when the browser had not fetched the defaults yet.
    /// </summary>
    public static TextContentDto WithEditorDefaults(TextContentDto content, EditorDefaultsResponse defaults)
    {
        string family = IsLegacy(content.FontFamily, LegacyFontFamily) && !string.IsNullOrWhiteSpace(defaults.FontFamily)
            ? defaults.FontFamily!
            : content.FontFamily;

        string color = IsLegacy(content.Color, LegacyTextColor) && !string.IsNullOrWhiteSpace(defaults.Color)
            ? defaults.Color!
            : content.Color;

        TextStrokeDto? stroke = content.Stroke;
        if (stroke is not null
            && IsLegacy(stroke.Color, LegacyStrokeColor)
            && !string.IsNullOrWhiteSpace(defaults.StrokeColor))
        {
            stroke = stroke with { Color = defaults.StrokeColor };
        }

        return content with { FontFamily = family, Color = color, Stroke = stroke };
    }

    private static bool IsLegacy(string? value, string legacy) =>
        string.Equals(value?.Trim(), legacy, StringComparison.OrdinalIgnoreCase);

    /// <summary>The design member's <c>color.text</c> as an opaque <c>#rrggbb</c> (canonical <c>{hex}</c> object, or a legacy bare string), or null when absent.</summary>
    private static string? ReadColorText(JsonObject design)
    {
        const string path = "color.text";
        if (DesignDocument.FindToken(design, path) is null)
            return null;
        if (!DesignDocument.ResolveAlias(design, path, out JsonNode? value))
            return null;

        // Canonical colour tokens are {"hex": "...", "colorSpace": "srgb"}; a bare string is tolerated for legacy documents.
        string? raw = value switch
        {
            JsonObject color => color["hex"] is JsonValue hexValue && hexValue.TryGetValue(out string? hexText) ? hexText : null,
            JsonValue json when json.TryGetValue(out string? text) => text,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Trim();
        return raw.Length == 9 && raw[0] == '#' ? raw[..7] : raw;
    }

    private static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ layer stack

    public static int NextZOrder(EditImageDocument document) =>
        document.Layers.Count == 0 ? 1 : document.Layers.Max(l => l.ZOrder) + 1;

    /// <summary>
    /// Adds <paramref name="layer"/> to the stack and renumbers it 1..n. With <paramref name="aboveLayerId"/>
    /// naming a layer still in the stack it lands directly above it; with <paramref name="atBack"/> it
    /// becomes position 1; otherwise it goes on top.
    /// </summary>
    public static void InsertLayer(
        InferenceApiDbContext db, EditImageDocument document, EditImageLayer layer, Guid? aboveLayerId = null, bool atBack = false)
    {
        List<EditImageLayer> ordered = [.. document.Layers.OrderBy(l => l.ZOrder).ThenBy(l => l.Id)];

        int index = ordered.Count;
        if (atBack)
        {
            index = 0;
        }
        else if (aboveLayerId is { } above)
        {
            int at = ordered.FindIndex(l => l.Id == above);
            if (at >= 0) index = at + 1;
        }

        ordered.Insert(index, layer);
        for (int i = 0; i < ordered.Count; i++) ordered[i].ZOrder = i + 1;

        // The new entity is added through the DbSet, not through the navigation: the document was loaded
        // with its layers, and EF's fix-up only needs the DocumentId, which the caller already set.
        db.EditImageLayers.Add(layer);
    }

    // ------------------------------------------------------------------ retention

    /// <summary>
    /// Marks the objects of deleted layers for the retention sweep (see <c>StorageRetentionService</c>).
    /// </summary>
    /// <remarks>
    /// <b>Cut-outs and generated layers, never a <c>Source</c>/<c>Image</c> layer.</b> Those two point at
    /// the user's own project file, and marking it would delete an original the customer still owns.
    /// Cut-outs and vendor outputs are the objects the editor itself created and nothing else names — no
    /// <c>project_files</c> row exists to expire them — so the party that wrote them has to name them when
    /// they go, or they sit in the bucket forever. The grace period is a safety net for a mistake, not a
    /// licence to be careless about which objects are marked.
    /// <para>
    /// <b>A picture's private copy of a deleted photo is the editor's own object too</b> (decision D10,
    /// <see cref="EditImageLayer.CopySizeBytes"/>), so it is marked as well, but only when no layer in
    /// <paramref name="survivors"/> still shows it: one copy is shared by every layer of the picture that used
    /// the photo.
    /// </para>
    /// </remarks>
    public static void MarkLayerObjects(
        InferenceApiDbContext db, Project project, IEnumerable<EditImageLayer> layers, DateTime now,
        IEnumerable<EditImageLayer>? survivors = null)
    {
        HashSet<string> stillShown = survivors is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : survivors.Select(l => l.StorageKey).Where(k => !string.IsNullOrEmpty(k)).ToHashSet(StringComparer.Ordinal);
        HashSet<string> marked = new(StringComparer.Ordinal);

        foreach (EditImageLayer layer in layers)
        {
            bool ownCopy = layer.CopySizeBytes is not null && !stillShown.Contains(layer.StorageKey);
            bool ownImage = layer.Kind is EditImageLayerKind.Cutout or EditImageLayerKind.Generated || ownCopy;
            // A matte is always the editor's own object (a vendor cut-out matte or a user mask), whatever the kind.
            foreach (string? key in ownImage ? new[] { layer.StorageKey, layer.MatteStorageKey } : new[] { layer.MatteStorageKey })
            {
                if (string.IsNullOrWhiteSpace(key) || !marked.Add(key)) continue;
                MarkObject(db, project, key, now);
            }
        }
    }

    /// <summary>Marks one editor-owned object for the retention sweep.</summary>
    public static void MarkObject(InferenceApiDbContext db, Project project, string key, DateTime now) =>
        db.StorageRetentionMarks.Add(new StorageRetentionMark
        {
            Id = Guid.NewGuid(),
            OrganizationId = project.OrganizationId,
            ProjectId = project.Id,
            StorageKey = key,
            Category = "agentFiles",
            MarkedAt = now
        });

    // ------------------------------------------------------------------ mapping

    public static EditImageDocumentDto ToDto(EditImageDocument document, Guid projectId) => new(
        document.Id,
        document.ProjectId,
        document.Name,
        document.SourceProjectFileId,
        document.Revision,
        document.CreatedAt,
        document.UpdatedAt,
        [.. document.Layers.OrderBy(l => l.ZOrder).ThenBy(l => l.Id).Select(l => ToDto(l, document.Id, projectId))],
        document.CanvasWidth,
        document.CanvasHeight,
        document.BackgroundColor);

    public static EditImageLayerDto ToDto(EditImageLayer layer, Guid documentId, Guid projectId)
    {
        string baseUrl = $"/api/v1/projects/{projectId}/image-documents/{documentId}/layers/{layer.Id}";

        return new EditImageLayerDto(
            layer.Id,
            layer.ZOrder,
            layer.Role,
            layer.Kind.ToString(),
            layer.Label,
            layer.SourceProjectFileId,
            $"{baseUrl}/image",
            string.IsNullOrWhiteSpace(layer.MatteStorageKey) ? null : $"{baseUrl}/matte",
            layer.MimeType,
            layer.Width,
            layer.Height,
            layer.Opacity,
            ReadTransform(layer.TransformJson),
            layer.SegmentationModel,
            layer.SegmentationPrompt,
            layer.CreatedAt,
            layer.Visible,
            layer.Locked,
            string.IsNullOrWhiteSpace(layer.BlendMode) ? EditImageBlendModes.Normal : layer.BlendMode,
            ParseJson(layer.ContentJson),
            ParseJson(layer.ProvenanceJson),
            !string.IsNullOrWhiteSpace(layer.MatteStorageKey),
            ReadCrop(layer.CropJson));
    }

    /// <summary>The stored crop, or <c>null</c> when the layer is uncropped (or the blob is unreadable).</summary>
    public static EditImageLayerCropDto? ReadCrop(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            EditImageLayerCropDto? crop = JsonSerializer.Deserialize<EditImageLayerCropDto>(json, Json);
            return crop is not null && ValidateCrop(crop) is null ? crop : null;
        }
        catch (JsonException)
        {
            // A corrupt blob reads as "uncropped" rather than failing the whole document.
            return null;
        }
    }

    /// <summary>True for the full window, which is stored as no crop at all.</summary>
    public static bool IsIdentityCrop(EditImageLayerCropDto c) => c.X == 0 && c.Y == 0 && c.W == 1 && c.H == 1;

    /// <summary>A crop window inside the bitmap, at least 1% of each side; <c>null</c> when valid, else the reason.</summary>
    public static string? ValidateCrop(EditImageLayerCropDto c)
    {
        if (!double.IsFinite(c.X) || !double.IsFinite(c.Y) || !double.IsFinite(c.W) || !double.IsFinite(c.H))
            return "crop values must be finite numbers.";
        const double eps = 1e-6;
        if (c.X < 0 || c.Y < 0 || c.W < 0.01 || c.H < 0.01 || c.X + c.W > 1 + eps || c.Y + c.H > 1 + eps)
            return "crop must lie inside the picture (x, y from 0; w, h at least 0.01; x + w and y + h at most 1).";
        return null;
    }

    public static EditImageLayerTransformDto ReadTransform(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<EditImageLayerTransformDto>(json, Json) ?? new EditImageLayerTransformDto();
        }
        catch (JsonException)
        {
            // A transform blob that will not parse is a corrupt row, not a reason to 500 a whole
            // document: the identity transform is what the column's default means anyway.
            return new EditImageLayerTransformDto();
        }
    }

    public static string WriteTransform(EditImageLayerTransformDto transform) => JsonSerializer.Serialize(transform, Json);

    /// <summary>
    /// The calling user's id for usage attribution, or <c>null</c> when there is no request (a unit test) or
    /// no authenticated user. Read from the request's services rather than injected, so attribution is not a
    /// constructor dependency of every image-document controller.
    /// </summary>
    public static Guid? CallerId(Microsoft.AspNetCore.Http.HttpContext? context)
    {
        try
        {
            return context?.RequestServices?.GetService(typeof(ReelBolt.Shared.Auth.ICurrentUser)) is ReelBolt.Shared.Auth.ICurrentUser user
                   && user.UserId != Guid.Empty
                ? user.UserId
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A jsonb column as a standalone element, or <c>null</c> when empty or corrupt.</summary>
    public static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(json);
            return parsed.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ validation

    /// <summary>
    /// A finite transform with a positive scale; <c>null</c> when valid, else the reason. NaN and infinity
    /// cannot arrive over JSON, but the check is what keeps the stored blob something a canvas can draw.
    /// </summary>
    public static string? ValidateTransform(EditImageLayerTransformDto t)
    {
        if (!double.IsFinite(t.X) || !double.IsFinite(t.Y) || !double.IsFinite(t.Scale) || !double.IsFinite(t.Rotation))
            return "transform values must be finite numbers.";
        if (t.Scale <= 0 || t.Scale > 1000)
            return "transform.scale must be greater than 0 and at most 1000 (mirror with flipX/flipY, not a negative scale).";

        return null;
    }

    private static readonly Regex CssColour = new(
        @"^[#A-Za-z0-9(),.%\s/+-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsCssColour(string? value) => value is not null && CssColour.IsMatch(value);

    private static readonly Regex FontFamily = new(
        @"^[A-Za-z0-9 _.\-]{1,100}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] Alignments = ["left", "center", "right"];

    /// <summary>
    /// Validates the text payload against the bounds in docs/design/thumbnail-editor.md section 4 and
    /// returns it normalised (control characters stripped, alignment lower-cased, strings trimmed).
    /// <c>error</c> is non-null on any violation and <c>content</c> is then <c>null</c>.
    /// </summary>
    public static (TextContentDto? Content, string? Error) NormaliseText(TextContentDto? content)
    {
        if (content is null) return (null, "content is required.");
        if (content.Text is null) return (null, "content.text is required.");

        string text = GeneratedPromptSanitizer.StripControlCharacters(content.Text);
        if (text.Length > MaxTextLength) return (null, $"content.text must be at most {MaxTextLength} characters.");

        string family = (content.FontFamily ?? string.Empty).Trim();
        if (!FontFamily.IsMatch(family)) return (null, "content.fontFamily must be 1 to 100 letters, digits, spaces, dots, dashes or underscores.");

        if (content.FontWeight is < 100 or > 900) return (null, "content.fontWeight must be between 100 and 900.");
        if (!double.IsFinite(content.FontSize) || content.FontSize is < 8 or > 600) return (null, "content.fontSize must be between 8 and 600.");
        if (!double.IsFinite(content.LineHeight) || content.LineHeight is < 0.5 or > 3) return (null, "content.lineHeight must be between 0.5 and 3.");
        if (!double.IsFinite(content.LetterSpacing) || content.LetterSpacing is < -50 or > 200) return (null, "content.letterSpacing must be between -50 and 200.");

        string? align = Alignments.FirstOrDefault(a => string.Equals(a, content.Align, StringComparison.OrdinalIgnoreCase));
        if (align is null) return (null, "content.align must be left, center or right.");

        string colour = (content.Color ?? string.Empty).Trim();
        if (!IsCssColour(colour)) return (null, "content.color must be a CSS colour of at most 64 characters.");

        TextStrokeDto? stroke = null;
        if (content.Stroke is { } s)
        {
            string strokeColour = (s.Color ?? string.Empty).Trim();
            if (!IsCssColour(strokeColour)) return (null, "content.stroke.color must be a CSS colour of at most 64 characters.");
            if (!double.IsFinite(s.Width) || s.Width is < 0 or > 64) return (null, "content.stroke.width must be between 0 and 64.");
            stroke = new TextStrokeDto(strokeColour, s.Width);
        }

        TextShadowDto? shadow = null;
        if (content.Shadow is { } sh)
        {
            string shadowColour = (sh.Color ?? string.Empty).Trim();
            if (!IsCssColour(shadowColour)) return (null, "content.shadow.color must be a CSS colour of at most 64 characters.");
            if (!double.IsFinite(sh.Blur) || sh.Blur is < 0 or > 200) return (null, "content.shadow.blur must be between 0 and 200.");
            if (!double.IsFinite(sh.OffsetX) || Math.Abs(sh.OffsetX) > 1000
                || !double.IsFinite(sh.OffsetY) || Math.Abs(sh.OffsetY) > 1000)
                return (null, "content.shadow.offsetX and offsetY must be between -1000 and 1000.");
            shadow = new TextShadowDto(shadowColour, sh.Blur, sh.OffsetX, sh.OffsetY);
        }

        if (content.MaxWidth is { } maxWidth && (!double.IsFinite(maxWidth) || maxWidth is < 1 or > 20000))
            return (null, "content.maxWidth must be between 1 and 20000.");

        return (new TextContentDto(
            text, family, content.FontWeight, content.FontSize, content.LineHeight, content.LetterSpacing,
            align, colour, stroke, shadow, content.MaxWidth, content.Uppercase), null);
    }

    /// <summary>The layer label for a text layer: its first non-empty line, capped, else "Text".</summary>
    public static string TextLabel(string text)
    {
        string first = text.Split('\n', '\r').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "Text";
        return first.Length > 40 ? first[..40] : first;
    }
}
