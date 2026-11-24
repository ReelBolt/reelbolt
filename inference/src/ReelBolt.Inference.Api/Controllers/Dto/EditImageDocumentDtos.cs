using System.Text.Json;

namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// A layer's placement (docs/design/thumbnail-editor.md section 2).
/// </summary>
/// <param name="X">Canvas pixels from the canvas's left edge to the layer's top-left corner.</param>
/// <param name="Y">Canvas pixels from the canvas's top edge to the layer's top-left corner.</param>
/// <param name="Scale">Uniform multiplier of the layer's own pixel size; 1 draws it at natural size.</param>
/// <param name="Rotation">Degrees clockwise about the layer's centre.</param>
/// <param name="FlipX">Mirror horizontally about the layer's centre.</param>
/// <param name="FlipY">Mirror vertically about the layer's centre.</param>
public record EditImageLayerTransformDto(
    double X = 0,
    double Y = 0,
    double Scale = 1,
    double Rotation = 0,
    bool FlipX = false,
    bool FlipY = false);

/// <summary>
/// A non-destructive crop of a picture layer: the kept window as fractions (0 to 1) of the layer's own
/// bitmap, measured from its top-left. The full window (0, 0, 1, 1) is "uncropped".
/// </summary>
public record EditImageLayerCropDto(double X = 0, double Y = 0, double W = 1, double H = 1);

/// <summary>
/// One layer, as the editor sees it.
/// </summary>
/// <param name="Kind">
/// <c>Source</c>, <c>Cutout</c>, <c>Generated</c>, <c>Text</c> or <c>Image</c>. A <c>Text</c> layer has
/// no bitmap: its image and matte routes answer 404 and <paramref name="Content"/> carries the text.
/// </param>
/// <param name="ImageUrl">
/// Where the layer's own pixels are served from, resolved by the server rather than assembled by the
/// client: <c>{project}/image-documents/{document}/layers/{layer}/image</c>. A cut-out is an RGBA PNG,
/// so this is what makes a subject a subject rather than a rectangle of photograph. Present for a
/// <c>Text</c> layer too (a stable URL shape), but that route is 404.
/// </param>
/// <param name="MatteUrl">The binary matte, or <c>null</c> when the vendor returned none separately.</param>
/// <param name="HasMatte">True when the layer has a mask (any bitmap kind): <paramref name="MatteUrl"/> serves it as a grayscale PNG, 255 = visible.</param>
/// <param name="SegmentationModel">Which endpoint produced the cut-out, e.g. <c>fal-ai/evf-sam</c>.</param>
/// <param name="SegmentationPrompt">The subject prompt that produced it, e.g. <c>the person</c>.</param>
/// <param name="Visible">Hidden layers are kept and still listed.</param>
/// <param name="Locked">The canvas refuses to move or scale a locked layer (a UI rule, not enforced here).</param>
/// <param name="BlendMode">One of <c>normal</c>, <c>multiply</c>, <c>screen</c>, <c>overlay</c>, <c>darken</c>,
/// <c>lighten</c>, <c>soft-light</c>, <c>hard-light</c>, <c>difference</c>.</param>
/// <param name="Content">
/// The vector content of a <c>Text</c> layer (a <see cref="TextContentDto"/> as JSON). For a bitmap layer made with
/// avatars or objects (<c>subjectIds</c>): <c>{ subjects: [{ avatarId, name, kind }], aiGenerated: true, disclosure }</c>,
/// where <c>disclosure</c> is <c>Visible</c> (the editor adds an "AI-generated" label at export) or
/// <c>MetadataOnly</c>: the person's own label when a person is shown, else <c>MetadataOnly</c>. A subject stays listed
/// after its avatar is deleted, under the name it had when the picture was made. <c>null</c> for every other layer.
/// </param>
/// <param name="Provenance">For a <c>Generated</c> layer: <c>{ operation, providerKind, model, prompt, vendorRequestId, parentLayerId }</c>; otherwise <c>null</c>.</param>
public record EditImageLayerDto(
    Guid Id,
    int ZOrder,
    string Role,
    string Kind,
    string Label,
    Guid? SourceProjectFileId,
    string ImageUrl,
    string? MatteUrl,
    string MimeType,
    int? Width,
    int? Height,
    double Opacity,
    EditImageLayerTransformDto Transform,
    string? SegmentationModel,
    string? SegmentationPrompt,
    DateTime CreatedAt,
    bool Visible = true,
    bool Locked = false,
    string BlendMode = "normal",
    JsonElement? Content = null,
    JsonElement? Provenance = null,
    bool HasMatte = false,
    EditImageLayerCropDto? Crop = null);

/// <summary>
/// The whole document: the source image plus its ordered layers.
/// </summary>
/// <param name="Revision">
/// Bumped by every layer change. A client that wants to be sure it is not overwriting someone else's
/// reorder sends it back on a PATCH as <c>baseRevision</c> and gets a <c>409</c> when it is stale.
/// </param>
/// <param name="SourceProjectFileId">
/// The project file the picture was made from, or <c>null</c> once that file was deleted (the picture then
/// shows its own copy, decision D10).
/// </param>
/// <param name="CanvasWidth">Composition width in pixels, or <c>null</c> for the source image's size.</param>
/// <param name="CanvasHeight">Composition height in pixels, or <c>null</c> for the source image's size.</param>
/// <param name="BackgroundColor">CSS colour painted under the lowest layer, or <c>null</c> for transparent.</param>
public record EditImageDocumentDto(
    Guid Id,
    Guid ProjectId,
    string? Name,
    Guid? SourceProjectFileId,
    int Revision,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<EditImageLayerDto> Layers,
    int? CanvasWidth = null,
    int? CanvasHeight = null,
    string? BackgroundColor = null);

/// <summary>Summary row for a list, without the layers.</summary>
public record EditImageDocumentSummaryDto(
    Guid Id,
    string? Name,
    Guid? SourceProjectFileId,
    int Revision,
    int LayerCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// Creates a document from one of the project's images. The source file becomes the backdrop layer,
/// so a new document is immediately renderable rather than an empty frame.
/// </summary>
/// <param name="SourceProjectFileId">Must be an <c>image/*</c> file in THIS project.</param>
/// <param name="Width">
/// Optional: the photo's width as the browser measured it (after EXIF orientation). Used only when the
/// server cannot read the size from the image header itself; send it WITH <paramref name="Height"/>, both
/// positive, or the request is a <c>400</c>.
/// </param>
/// <param name="Height">See <paramref name="Width"/>.</param>
/// <remarks>
/// Omit <paramref name="SourceProjectFileId"/> and send <paramref name="Width"/> + <paramref name="Height"/>
/// (each 16 to 4096) to create a BLANK picture: no layers, transparent unless <paramref name="BackgroundColor"/>
/// (a CSS colour) is given, named "New picture" unless <paramref name="Name"/> is given.
/// </remarks>
/// <param name="BackgroundColor">Blank pictures only: a CSS colour painted under the layers.</param>
public record CreateEditImageDocumentRequest(Guid? SourceProjectFileId = null, string? Name = null, int? Width = null, int? Height = null, string? BackgroundColor = null);

/// <summary>
/// Changes a document's own fields. Every field is optional and an absent one is left alone.
/// </summary>
/// <param name="Name">The new name. An empty string clears it (the UI then shows the source file's name).</param>
/// <param name="CanvasWidth">
/// Composition width, 16 to 8192. Send it WITH <paramref name="CanvasHeight"/>: one without the other is
/// a <c>400</c>. <c>0</c> together with <c>0</c> clears both back to "the source image's size".
/// </param>
/// <param name="CanvasHeight">See <paramref name="CanvasWidth"/>.</param>
/// <param name="BackgroundColor">
/// A CSS colour (<c>#112233</c>, <c>rgba(0,0,0,.5)</c>, <c>transparent</c>). An empty string clears it.
/// Anything with characters a colour cannot contain is a <c>400</c>.
/// </param>
/// <param name="BaseRevision">The revision the client last saw; stale means <c>409 REVISION_CONFLICT</c> and no write.</param>
public record UpdateEditImageDocumentRequest(
    string? Name = null,
    int? CanvasWidth = null,
    int? CanvasHeight = null,
    string? BackgroundColor = null,
    int? BaseRevision = null);

/// <summary>An include (1) or exclude (0) click on the picture being cut from, in its own pixels.</summary>
public record PointPromptDto(int X, int Y, int Label);

/// <summary>A drag-a-box prompt on the picture being cut from, in its own pixels.</summary>
public record BoxPromptDto(int XMin, int YMin, int XMax, int YMax);

/// <summary>
/// Cuts one or more subjects out of a picture layer (or, with no <c>layerId</c>, the document's source
/// image) and adds each as a layer.
/// </summary>
/// <param name="LayerId">
/// The picture layer to cut from — any layer with pixels: an uploaded image, the original, an AI result,
/// an earlier cut-out. Point and box prompts are in THAT layer's pixels, and each new cut-out takes the
/// layer's position and size on the canvas and sits directly above it. <c>null</c> keeps the original
/// behaviour: the document's source project file, new layers on top.
/// </param>
/// <param name="Prompt">
/// What to cut out, in words — <c>"the person"</c>, <c>"the red car"</c>. Required by the text-prompted
/// segmenter and refused by the others rather than silently ignored.
/// </param>
/// <param name="NegativePrompt">What to exclude, where the endpoint documents one.</param>
/// <param name="Model">
/// The fal endpoint id. <c>null</c> uses the provider row's own model. Only ids whose schema has been
/// read are accepted; an unknown one is a <c>400</c>, never a guess.
/// </param>
/// <param name="Variant">
/// A sub-model on the same endpoint, where it documents one — notably <c>"Matting"</c> on
/// <c>fal-ai/birefnet/v2</c>, which is tuned for soft alpha edges such as hair.
/// </param>
/// <param name="Role">The role the new layer takes. Defaults to <c>subject</c>.</param>
/// <param name="ProviderId">An explicit segmentation provider row, overriding the default.</param>
/// <param name="SemanticParts">Ask for semantic-level segmentation, where documented.</param>
/// <param name="PointPrompts">Click prompts in the cut-from picture's PIXELS, at most 64. Absent keeps the old behaviour.</param>
/// <param name="BoxPrompts">Box prompts in the cut-from picture's PIXELS, at most 16.</param>
/// <param name="ReturnMultipleMasks">Ask the model for every mask it can produce; one layer is created per mask returned.</param>
/// <param name="MaxMasks">Upper bound on the layers created, 1 to 16. Defaults to 8 when multiple masks are requested, else 1.</param>
public record SegmentSubjectRequest(
    string? Prompt = null,
    string? NegativePrompt = null,
    string? Model = null,
    string? Variant = null,
    string? Role = null,
    Guid? ProviderId = null,
    bool? SemanticParts = null,
    IReadOnlyList<PointPromptDto>? PointPrompts = null,
    IReadOnlyList<BoxPromptDto>? BoxPrompts = null,
    bool? ReturnMultipleMasks = null,
    int? MaxMasks = null,
    Guid? LayerId = null);

/// <summary>
/// Changes a layer in place. Every field is optional; an absent one is left alone, which is why this
/// is not a PUT.
/// </summary>
/// <param name="ZOrder">
/// Moves the layer to this position, 1-based, and renumbers the rest. Expressed as a POSITION rather
/// than a raw number so a client never has to know the current numbering to move something — ask for
/// position 1 and the layer ends up at the back whatever the other layers are doing.
/// </param>
/// <param name="BaseRevision">
/// The revision the client last saw. When supplied and stale the answer is <c>409</c> and nothing is
/// written, so two editors of one document collide instead of silently overwriting each other.
/// </param>
/// <param name="Visible">Show or hide the layer.</param>
/// <param name="Locked">Lock or unlock the layer.</param>
/// <param name="BlendMode">One of the blend modes listed on <see cref="EditImageLayerDto"/>; anything else is a <c>400</c>.</param>
/// <param name="Label">The new layer name (1 to 300 characters).</param>
/// <param name="Content">Replacement content. <c>Text</c> layers only; any other layer is a <c>400</c>.</param>
/// <param name="Crop">
/// The kept window as 0..1 fractions of the layer's bitmap. Not for <c>Text</c> layers (<c>400</c>). The
/// window must lie inside the bitmap and be at least 1% wide and tall. Sending the full window
/// (0, 0, 1, 1) removes the crop; the layer's pixels are never changed, so it can be widened again.
/// </param>
public record UpdateEditImageLayerRequest(
    string? Role = null,
    int? ZOrder = null,
    double? Opacity = null,
    EditImageLayerTransformDto? Transform = null,
    int? BaseRevision = null,
    bool? Visible = null,
    bool? Locked = null,
    string? BlendMode = null,
    string? Label = null,
    TextContentDto? Content = null,
    EditImageLayerCropDto? Crop = null);

// ---------------------------------------------------------------------------------------------------
// Text layers (docs/design/thumbnail-editor.md section 4)
// ---------------------------------------------------------------------------------------------------

/// <summary>An outline around text glyphs.</summary>
public record TextStrokeDto(string Color = "#000000", double Width = 0);

/// <summary>A drop shadow behind text glyphs.</summary>
public record TextShadowDto(string Color = "rgba(0,0,0,0.6)", double Blur = 0, double OffsetX = 0, double OffsetY = 0);

/// <summary>
/// The vector content of a text layer. Rendered by the browser only; the API validates its shape and
/// bounds (text up to 2000 characters, font size 8 to 600, stroke width 0 to 64) and stores it
/// verbatim. Omitted fields take the defaults declared here.
/// </summary>
public record TextContentDto(
    string? Text = null,
    string FontFamily = "Chakra Petch",
    int FontWeight = 700,
    double FontSize = 96,
    double LineHeight = 1.05,
    double LetterSpacing = 0,
    string Align = "left",
    string Color = "#ffffff",
    TextStrokeDto? Stroke = null,
    TextShadowDto? Shadow = null,
    double? MaxWidth = null,
    bool Uppercase = false);

/// <summary>Adds a text layer on top of the stack.</summary>
/// <param name="Content">Required.</param>
/// <param name="Transform">Defaults to the identity transform (0, 0, scale 1).</param>
/// <param name="Role">Defaults to <c>overlay</c>.</param>
/// <param name="Label">Defaults to the first line of the text.</param>
public record CreateTextLayerRequest(
    TextContentDto? Content = null,
    EditImageLayerTransformDto? Transform = null,
    string? Role = null,
    string? Label = null);

/// <summary>Places one of the project's images as a layer on top of the stack.</summary>
/// <param name="ProjectFileId">An <c>image/*</c> file in THIS project.</param>
/// <param name="Role">Defaults to <c>overlay</c>.</param>
/// <param name="Label">Defaults to the file name.</param>
public record CreateImageLayerFromFileRequest(Guid ProjectFileId, string? Role = null, string? Label = null);

// ---------------------------------------------------------------------------------------------------
// Uploads and exports
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// Asks for a presigned PUT for a mask or an export.
/// </summary>
/// <param name="Purpose"><c>mask</c> or <c>export</c>.</param>
/// <param name="ContentType">
/// <c>image/png</c> for a mask; for an export one of <c>image/png</c>, <c>image/jpeg</c>, <c>image/webp</c>,
/// <c>image/avif</c>, <c>image/vnd.adobe.photoshop</c> (layered PSD) or <c>image/openraster</c> (layered ORA).
/// The key's extension follows it.
/// </param>
/// <param name="ContentLength">
/// The size the browser is about to send: 1 byte to 12 MiB for a mask, 1 byte to 200 MiB for an export.
/// </param>
public record EditImageUploadUrlRequest(string? Purpose = null, string? ContentType = null, long ContentLength = 0);

/// <summary>
/// A presigned PUT. <paramref name="Headers"/> must be sent on the PUT exactly as given (they are part of
/// the signature). <paramref name="Url"/> is a bearer capability and is never logged.
/// </summary>
/// <param name="Key">The server-chosen object key, to be echoed back as <c>maskKey</c> or the export <c>key</c>.</param>
/// <param name="ExpiresAt">When the URL stops working (UTC).</param>
public record EditImageUploadUrlResponse(
    string Key,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    DateTime ExpiresAt);

/// <summary>Registers a picture the browser rendered and uploaded as an export of the document.</summary>
/// <param name="Key">The key an <c>export</c> upload-url call returned.</param>
/// <param name="Width">Rendered width in pixels, 1 to 16384.</param>
/// <param name="Height">Rendered height in pixels, 1 to 16384.</param>
/// <param name="FileName">
/// The project file's name; the extension of the uploaded type (<c>.png</c>, <c>.jpg</c>, <c>.webp</c>,
/// <c>.avif</c>, <c>.psd</c>, <c>.ora</c>) is appended when it does not already end with it.
/// </param>
/// <param name="SetAsProjectThumbnail">
/// A no-op today: the project model has no thumbnail field, so nothing can be set. The response says so
/// through <see cref="EditImageExportResponse.ProjectThumbnailApplied"/> being <c>false</c>.
/// </param>
public record CreateEditImageExportRequest(
    string? Key = null,
    int Width = 0,
    int Height = 0,
    string? FileName = null,
    bool? SetAsProjectThumbnail = null);

/// <summary>One export of a document.</summary>
/// <param name="FileUrl">Where the exported file downloads from, or <c>null</c> when its project file was deleted.</param>
public record EditImageExportDto(
    Guid Id,
    Guid DocumentId,
    Guid? ProjectFileId,
    string? FileUrl,
    int Width,
    int Height,
    int Revision,
    DateTime CreatedAt);

/// <summary>The export row and the project file that was registered for it.</summary>
public record EditImageExportResponse(
    EditImageExportDto Export,
    ProjectFileResponse ProjectFile,
    bool ProjectThumbnailApplied);

// ---------------------------------------------------------------------------------------------------
// Operations (docs/design/thumbnail-editor.md section 5)
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// One AI operation on the document.
/// </summary>
/// <param name="Operation">
/// <c>erase</c>, <c>fill</c>, <c>expand</c>, <c>replaceBackground</c>, <c>relight</c>, <c>upscale</c>,
/// <c>instruct</c>, <c>generate</c> or <c>removeBackground</c> (case-insensitive).
/// </param>
/// <param name="LayerId">The bitmap layer to act on. Required for everything except <c>generate</c>.</param>
/// <param name="MaskKey">
/// An uploaded <c>mask</c> key (white = the region to change). Required by <c>erase</c> and <c>fill</c>.
/// </param>
/// <param name="Prompt">What to do or draw. Required by <c>fill</c>, <c>replaceBackground</c>, <c>instruct</c>, <c>generate</c>.</param>
/// <param name="NegativePrompt">What to avoid, where the endpoint documents one.</param>
/// <param name="Model">Overrides the endpoint/model; absent lets the provider row (or the vendor client) choose.</param>
/// <param name="ProviderId">An explicit provider row, overriding the default for the capability.</param>
/// <param name="Params">
/// Operation-specific fields, forwarded as the chosen endpoint's schema names them. <c>expand</c> also reads two
/// editor shapes of its own, never forwarded as they are: <c>top</c>/<c>right</c>/<c>bottom</c>/<c>left</c>
/// (pixels of the LAYER'S picture to add per side) or <c>aspect</c> (<c>"9:16"</c>: grow, centred, to that
/// shape), which the server turns into the endpoint's own geometry within its documented limits.
/// </param>
/// <param name="Role">The new layer's role; defaults per operation.</param>
/// <param name="Label">The new layer's name; defaults to a description of the operation.</param>
/// <param name="SourceKey">
/// An uploaded <c>source</c> key: a smaller copy of the layer, sent INSTEAD of the original when an earlier try
/// answered <c>422 SOURCE_TOO_LARGE</c> with the size to make it. Ignored when the original fits.
/// </param>
public record EditImageOperationRequest(
    string? Operation = null,
    Guid? LayerId = null,
    string? MaskKey = null,
    string? Prompt = null,
    string? NegativePrompt = null,
    string? Model = null,
    Guid? ProviderId = null,
    Dictionary<string, JsonElement>? Params = null,
    string? Role = null,
    string? Label = null,
    string? SourceKey = null,
    /// <summary>
    /// FURTHER pictures of this project the edit is made against, for <c>instruct</c> only, in the
    /// caller's order: "replace the face in this picture with the face from the second one". Project file
    /// ids, resolved and presigned server-side like the source; at most four, and the model set up for the
    /// tool refuses them (409) when its schema documents no second picture. Ignored by every other
    /// operation.
    /// </summary>
    List<Guid>? ReferenceProjectFileIds = null,
    /// <summary>
    /// Avatars and objects of this project to show (docs/design/avatar-objects.md), for <c>generate</c> and
    /// <c>instruct</c> only: 1 to 3 avatar ids, at most one of them a person. <c>generate</c> then makes a new picture
    /// that shows them in the scene of the prompt; <c>instruct</c> keeps the layer as the picture being edited and adds
    /// their pictures as references. Every other operation refuses the field (<c>400 subjects_not_supported</c>).
    /// </summary>
    List<Guid>? SubjectIds = null,
    /// <summary>
    /// With <c>subjectIds</c> only: allows sending the subjects' pictures to the picture service for THIS call, when
    /// the project's own permission (<c>PUT .../avatars/media-consent</c>) is not recorded. It counts only for a
    /// signed-in person using the app; any other caller is answered <c>409 CONSENT_REQUIRED</c>. It records nothing.
    /// </summary>
    bool? AllowMediaEgress = null);

/// <summary>
/// What the AI service set up for one editor tool can take: <c>GET .../image-documents/operations/{operation}/capabilities</c>.
/// The editor shows only the controls the service honours (no prompt field for a service that takes none).
/// </summary>
/// <param name="Operation">The tool.</param>
/// <param name="Provider">The provider row and model the tool would run on right now.</param>
/// <param name="TakesPrompt"><c>false</c> when the model documents no prompt; <c>null</c> when not established.</param>
/// <param name="MaxPromptLength">The model's documented prompt cap in characters.</param>
/// <param name="Expand">For <c>expand</c> on a catalogued endpoint: its geometry and limits.</param>
/// <param name="Estimate">About what one run costs (from the document's canvas or the requested size when the tool is size-priced).</param>
/// <param name="CanConfigure">The caller is an organization Owner/Admin or a platform admin, so can set up the AI service themselves.</param>
/// <param name="Subjects">For <c>generate</c> and <c>instruct</c>: whether avatars and objects can be shown, and by which models. <c>null</c> for every other tool.</param>
public record EditImageOperationCapabilitiesResponse(
    string Operation,
    EditImageOperationProviderDto Provider,
    bool? TakesPrompt,
    int MaxPromptLength,
    EditImageExpandCapabilitiesDto? Expand,
    ImageGraphicEstimateDto? Estimate = null,
    bool CanConfigure = false,
    EditImageSubjectCapabilitiesDto? Subjects = null);

/// <summary>
/// Whether a tool can show avatars and objects right now (<c>subjectIds</c>), and its limits.
/// </summary>
/// <param name="Supported">
/// The picture-edit model the tool would use takes several pictures at once. <c>false</c> when no picture-edit
/// service is set up, or its model takes one picture only.
/// </param>
/// <param name="MaxSubjects">The most avatars and objects in one picture.</param>
/// <param name="MaxPeople">The most of them that may be a person.</param>
/// <param name="Models">The models of the resolved picture-edit service that take several pictures (empty when there is none).</param>
/// <param name="Provider">The picture-edit service and model a run with subjects would use, or <c>null</c> when none is set up.</param>
public record EditImageSubjectCapabilitiesDto(
    bool Supported,
    int MaxSubjects,
    int MaxPeople,
    IReadOnlyList<string> Models,
    EditImageOperationProviderDto? Provider = null);

/// <summary>An expand endpoint's geometry and documented limits.</summary>
/// <param name="Geometry"><c>margins</c> (pixels per side) or <c>canvas</c> (the finished canvas).</param>
/// <param name="MaxMarginPx">The per-side cap of a margins endpoint, in pixels of the picture SENT.</param>
/// <param name="MaxOutputPixels">The largest finished picture, in pixels.</param>
/// <param name="MaxInputPixels">The largest picture the endpoint takes as it is; a larger one is sent as a copy.</param>
/// <param name="Basis">Where the limits were read.</param>
public record EditImageExpandCapabilitiesDto(
    string Geometry,
    int? MaxMarginPx,
    long? MaxOutputPixels,
    long? MaxInputPixels,
    string? Basis);

/// <summary>Which provider row and model served the operation.</summary>
/// <param name="Capability">
/// The capability the serving row was resolved as: the job's own (<c>ImageUpscale</c> for an upscale), or
/// <c>ImageEdit</c> when the job fell back to the instruction-edit row because it has none of its own.
/// </param>
public record EditImageOperationProviderDto(string Kind, string Model, string? Capability = null);

/// <summary>What was consumed: <c>images</c> produced.</summary>
public record EditImageOperationUsageDto(string Unit, int Quantity);

/// <summary>The layers an operation produced (already persisted, in the document) and who made them.</summary>
/// <param name="Revision">The document's revision after the layers were added, so the client can keep sending a fresh <c>baseRevision</c>.</param>
/// <param name="Notice">
/// A plain-language note about the result the user should know, or <c>null</c>. Today: the AI returned a
/// differently shaped picture, so a cut-out's transparent background could not be carried over.
/// </param>
/// <param name="ChargedCredits">The credits this call settled at ("used N credits"); <c>null</c> when nothing was charged.</param>
public record EditImageOperationResponse(
    IReadOnlyList<EditImageLayerDto> Layers,
    string Operation,
    EditImageOperationProviderDto Provider,
    EditImageOperationUsageDto? Usage,
    int Revision = 0,
    string? Notice = null,
    double? ChargedCredits = null,
    ImageNeedsReviewDto? NeedsReview = null,
    /// <summary>
    /// With <c>subjectIds</c>: how many of the subjects' pictures were NOT sent (an avatar has more pictures than one
    /// call takes). <c>null</c> for a call without subjects.
    /// </summary>
    int? SubjectPicturesSkipped = null);

/// <summary>
/// A paid picture result that was kept but not shown because the layer it was made for no longer exists.
/// Wire shape <c>needsReview: { reason: "layerGone", layerId }</c>: <c>LayerId</c> is the NEW (hidden) layer. The client
/// offers "Show it" (layer PATCH visible=true with baseRevision) or "Discard" (layer DELETE).
/// </summary>
public sealed record ImageNeedsReviewDto(string Reason, Guid LayerId)
{
    /// <summary>The only reason today: the target layer was deleted while the job ran.</summary>
    public const string LayerGone = "layerGone";

    public static ImageNeedsReviewDto ForLayerGone(Guid layerId) => new(LayerGone, layerId);
}
