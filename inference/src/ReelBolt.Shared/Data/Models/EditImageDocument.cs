namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// What a layer <i>is</i> in the composite, which decides whether it may be edited as a subject.
/// Persisted by member name, so the enum is append-only.
/// </summary>
public enum EditImageLayerKind
{
    /// <summary>
    /// The whole source image. Every document starts with exactly one of these, underneath
    /// everything else: a composite with no backdrop is a floating subject on transparency, which is
    /// almost never what someone editing a photograph wants, and having the row means "put the
    /// original back" is a z-order change rather than a re-upload.
    /// </summary>
    Source,

    /// <summary>
    /// A subject CUT OUT of a source image, with an alpha matte. This is the whole point of the
    /// table: the pixels here are a person or an object on transparency, not a rectangle of
    /// photograph, so moving, scaling and re-colouring it does not drag the background along.
    /// </summary>
    Cutout,

    /// <summary>
    /// A vendor OUTPUT that is neither the source nor a segmented subject: an erase or fill result,
    /// an expansion, an upscale, a replaced background, a relit picture, an instruction edit or a
    /// text-to-image backdrop (docs/design/thumbnail-editor.md section 5). Carries its own bitmap and
    /// a <see cref="EditImageLayer.ProvenanceJson"/> naming the operation, provider, model and parent.
    /// Appended after <see cref="Cutout"/>: the enum is persisted by name, so this is append-only.
    /// </summary>
    Generated,

    /// <summary>
    /// Vector text. <b>Has no bitmap</b>: <see cref="EditImageLayer.StorageKey"/> is empty, the layer
    /// image and matte routes answer 404, and the browser rasterises
    /// <see cref="EditImageLayer.ContentJson"/> onto the canvas. No server-side text rendering exists.
    /// </summary>
    Text,

    /// <summary>
    /// A project image placed as a layer (a logo, a sticker). Its <see cref="EditImageLayer.StorageKey"/>
    /// IS the user's own project file, exactly like a <see cref="Source"/> layer, so it is never marked
    /// for retention.
    /// </summary>
    Image,
}

/// <summary>
/// Where a layer's pixels sit and how they are drawn.
/// </summary>
/// <remarks>
/// A separate type rather than scalars read directly, because the editor serialises this as one JSON
/// object on the wire and a shape that round-trips is easier to keep stable than a handful of scalars
/// that a client has to reassemble. <b>Units (docs/design/thumbnail-editor.md section 2):</b>
/// <see cref="X"/> and <see cref="Y"/> are CANVAS PIXELS of the layer's top-left corner,
/// <see cref="Scale"/> is a uniform multiplier of the layer's own pixel size, and
/// <see cref="Rotation"/> is degrees clockwise about the layer's centre. Flips mirror about the centre.
/// </remarks>
public sealed class EditImageLayerTransform
{
    /// <summary>Canvas pixels from the canvas's left edge to the layer's top-left corner.</summary>
    public double X { get; set; }

    /// <summary>Canvas pixels from the canvas's top edge to the layer's top-left corner.</summary>
    public double Y { get; set; }

    /// <summary>Uniform scale multiplier; 1 draws the layer at its own pixel size.</summary>
    public double Scale { get; set; } = 1;

    /// <summary>Rotation in degrees, clockwise, about the layer's centre.</summary>
    public double Rotation { get; set; }

    /// <summary>Mirror horizontally about the layer's centre.</summary>
    public bool FlipX { get; set; }

    /// <summary>Mirror vertically about the layer's centre.</summary>
    public bool FlipY { get; set; }
}

/// <summary>
/// The persisted document row for a project's IMAGE edit: a source image decomposed into ordered
/// layers (table <c>edit_image_documents</c>, Inference-API-owned).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately NOT <see cref="EditTimelineEntity"/>.</b> <c>docs/thumbnails-and-image-editing.md</c>
/// section P3 is explicit that the v2 timeline is a <i>time</i>-based document with a pinned
/// <c>schemaVersion: 2</c> and a hand-written, drift-tested TypeScript mirror, and that an image
/// editor needs a <i>layer</i> document instead — ordered layers with a transform, an opacity and a
/// z-order. Overloading one with the other would have put two incompatible shapes behind one
/// version number, and every existing timeline client would have had to learn to ignore columns it
/// does not understand.
/// </para>
/// <para>
/// <b>Why the layers are rows rather than a JSON blob.</b> The timeline keeps its document as jsonb
/// because nothing queries inside it. Here the API has to answer "which layers are on this document,
/// in what order" for every reveal, list and reorder, and it has to be able to delete one layer
/// without rewriting the whole document under a concurrency token. Rows make a reorder an update of
/// one integer per moved layer rather than a full document write, which is the difference between a
/// drag being cheap and a drag being a lost update.
/// </para>
/// </remarks>
public class EditImageDocument
{
    public Guid Id { get; set; }

    /// <summary>The owning project. Cascade-deleted with the project.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>User-facing name. Optional; the API renders the source file's name when it is null.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// The project file this document was built from — the backdrop layer's image and the image a cut-out
    /// with no layer named is segmented out of. <c>null</c> once that file is deleted: the FK is
    /// <c>SET NULL</c> (decision D10), and before the delete every layer that showed the file was given the
    /// picture's own copy, so the picture keeps working. A picture created before D10 whose file was then
    /// removed outside the copy path has no copy: its cut-out without a layer is the documented
    /// <c>409 NO_SOURCE_IMAGE</c>.
    /// </summary>
    public Guid? SourceProjectFileId { get; set; }

    /// <summary>
    /// Composition width in pixels. <c>null</c> means "the source image's size", which the client
    /// reads off the source itself (nothing server-side measures an upload's pixels). The editor's
    /// presets are YouTube 1280x720, Square 1080x1080 and Portrait 1080x1920.
    /// </summary>
    public int? CanvasWidth { get; set; }

    /// <summary>Composition height in pixels; <c>null</c> together with <see cref="CanvasWidth"/>.</summary>
    public int? CanvasHeight { get; set; }

    /// <summary>
    /// CSS colour painted under the lowest layer. <c>null</c> is transparent. Validated for shape at the
    /// API boundary and otherwise stored verbatim; only the browser ever interprets it.
    /// </summary>
    public string? BackgroundColor { get; set; }

    /// <summary>Monotonic document revision, starting at 1. Bumped by any layer change.</summary>
    public int Revision { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete marker; non-null rows are hidden by the query filter.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>The layers, in no particular order — <see cref="EditImageLayer.ZOrder"/> decides.</summary>
    public List<EditImageLayer> Layers { get; set; } = [];
}

/// <summary>
/// One layer of an <see cref="EditImageDocument"/> (table <c>edit_image_layers</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A cut-out layer carries its own storage pointer rather than a <c>media_derivatives</c> row.</b>
/// That table is an index of derivations OF a project file — a proxy, a waveform, a thumbnail — and
/// it has no column for a transform, an opacity or a z-order, because a derivative is a fact about
/// the file while a layer is an EDITABLE element of a document. Registering one inside the other
/// would mean two rows that must agree about the same object and one of them unable to express what
/// the other holds. The layer row IS the index for a layer.
/// </para>
/// </remarks>
public class EditImageLayer
{
    public Guid Id { get; set; }

    /// <summary>The document this layer belongs to. Cascade-deleted with it.</summary>
    public Guid DocumentId { get; set; }

    /// <summary>
    /// Painting order: LOWER composites FIRST, i.e. furthest back. Chosen so "the backdrop is 1" reads
    /// the way a designer expects, and so appending a new layer is a max+1 rather than a shift of
    /// everything else.
    /// </summary>
    public int ZOrder { get; set; }

    /// <summary>
    /// The layer's role in the composite. This is not decoration: it selects the reveal ramp the
    /// editor animates with (a backdrop and a subject resolve on different curves and at different
    /// speeds), so it is the one piece of layer metadata the UI reads for motion as well as layout.
    /// Persisted as a string, so an unrecognised value is a visible error rather than a silent zero.
    /// </summary>
    public string Role { get; set; } = EditImageLayerRoles.Subject;

    /// <summary>Whole image, or a subject cut out of one. See <see cref="EditImageLayerKind"/>.</summary>
    public EditImageLayerKind Kind { get; set; } = EditImageLayerKind.Source;

    /// <summary>A human label, e.g. the file name or the prompt that produced the cut-out.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// The project file the pixels came from, or <c>null</c> when the layer is not derived from one
    /// document file. For a <see cref="EditImageLayerKind.Cutout"/> this is the image that was
    /// segmented, which is what makes "segment this subject again with a better prompt" answerable.
    /// </summary>
    public Guid? SourceProjectFileId { get; set; }

    /// <summary>S3/MinIO object key of the layer's own pixels. For a cut-out, an RGBA PNG.</summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>
    /// Set when <see cref="StorageKey"/> is the picture's PRIVATE COPY of a project photo that was deleted
    /// (decision D10, copy-on-delete): the copy's size in bytes, else <c>null</c>. The copy lives under
    /// <c>projects/{p}/agentFiles/image-editor/{documentId}/</c>, is shared by every layer of the document
    /// that used the photo, counts toward the workspace's storage once per (document, key), and is marked
    /// for the retention sweep when the last layer using it is deleted.
    /// </summary>
    public long? CopySizeBytes { get; set; }

    /// <summary>S3/MinIO bucket holding the object.</summary>
    public string StorageBucket { get; set; } = string.Empty;

    /// <summary>
    /// S3/MinIO object key of the binary matte, when the vendor returned one separately. <c>null</c>
    /// is normal: only some segmentation endpoints document a second output field, and a layer whose
    /// matte lives inside its own alpha channel does not need one.
    /// </summary>
    public string? MatteStorageKey { get; set; }

    /// <summary>MIME type of the stored object.</summary>
    public string MimeType { get; set; } = "image/png";

    /// <summary>Pixel width of the layer's own image, when known.</summary>
    public int? Width { get; set; }

    /// <summary>Pixel height of the layer's own image, when known.</summary>
    public int? Height { get; set; }

    /// <summary>0 to 1. Multiplied into the composite; 1 means fully opaque.</summary>
    public double Opacity { get; set; } = 1;

    /// <summary>
    /// The layer's placement, stored as jsonb. Kept as one column rather than four scalars because it
    /// is one value conceptually and travels as one object on the wire; the API never queries inside
    /// it, and a reorder or an opacity change must not have to rewrite it.
    /// </summary>
    public string TransformJson { get; set; } = "{}";

    /// <summary>
    /// Non-destructive crop of the layer's own pixels as <c>{"x","y","w","h"}</c> fractions (0 to 1) of
    /// the bitmap, stored as jsonb. <c>null</c> (the default, and every row that predates the column) means
    /// "uncropped". The stored bitmap is never changed, so the crop can be widened again. When set, the
    /// layer's <see cref="TransformJson"/> places the top-left corner of the KEPT part.
    /// </summary>
    public string? CropJson { get; set; }

    /// <summary>
    /// The endpoint id that produced the cut-out, e.g. <c>fal-ai/evf-sam</c>. Recorded because a
    /// cut-out is not reproducible from the layer alone — the model, the prompt and the vendor's
    /// current weights all matter — so "which model made this" has to survive the request that made
    /// it. Null for a source layer.
    /// </summary>
    public string? SegmentationModel { get; set; }

    /// <summary>The subject prompt that produced the cut-out, e.g. <c>the person</c>. Null for a source layer.</summary>
    public string? SegmentationPrompt { get; set; }

    /// <summary>
    /// Hidden layers are kept, still listed and still exported (as hidden). Defaults to <c>true</c>.
    /// </summary>
    public bool Visible { get; set; } = true;

    /// <summary>
    /// The canvas refuses to move or scale a locked layer. Advisory for the browser only: the API still
    /// lets a PATCH change a locked layer's transform, so a script is not blocked by a UI affordance.
    /// </summary>
    public bool Locked { get; set; }

    /// <summary>One of <see cref="EditImageBlendModes.All"/>; validated at the API boundary.</summary>
    public string BlendMode { get; set; } = EditImageBlendModes.Normal;

    /// <summary>
    /// The vector content of a non-bitmap layer, stored as jsonb. A
    /// <see cref="EditImageLayerKind.Text"/> layer keeps its text here (docs/design/thumbnail-editor.md section 4);
    /// the API validates its shape and bounds and stores it verbatim. A bitmap layer made with avatars or objects
    /// keeps what it shows here instead (<c>{ subjects, aiGenerated, disclosure }</c>, written by the operations route,
    /// never by a client; docs/design/avatar-objects.md). <c>null</c> for every other bitmap layer.
    /// </summary>
    public string? ContentJson { get; set; }

    /// <summary>
    /// For a <see cref="EditImageLayerKind.Generated"/> layer, as jsonb:
    /// <c>{ operation, providerKind, model, prompt, vendorRequestId, parentLayerId }</c>. Recorded
    /// because a vendor output is not reproducible from the layer alone. The
    /// <see cref="SegmentationModel"/>/<see cref="SegmentationPrompt"/> columns stay the record for a
    /// <see cref="EditImageLayerKind.Cutout"/>.
    /// </summary>
    public string? ProvenanceJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete marker; non-null rows are hidden by the query filter.</summary>
    public DateTime? DeletedAt { get; set; }
}

/// <summary>
/// The three role words a layer's <see cref="EditImageLayer.Role"/> may take, in one place.
/// </summary>
/// <remarks>
/// Source-generated rather than a C# enum because the value is persisted as a string AND is part of
/// the JSON contract the web app already speaks; a single string constant set is the one form both
/// can share without a converter on either side. An unrecognised value is rejected at the API
/// boundary, never stored.
/// </remarks>
public static class EditImageLayerRoles
{
    /// <summary>The backdrop a subject is cut out of.</summary>
    public const string Background = "background";

    /// <summary>A subject standing in front of the backdrop.</summary>
    public const string Subject = "subject";

    /// <summary>A graphic laid over the top — a title, a logo, a sticker.</summary>
    public const string Overlay = "overlay";

    /// <summary>Every valid role. Not a <c>HashSet</c>: the set is three items and order is stable.</summary>
    public static readonly IReadOnlyList<string> All = [Background, Subject, Overlay];

    /// <summary>Whether <paramref name="role"/> is one of <see cref="All"/>, case-insensitively.</summary>
    public static bool IsValid(string? role) =>
        role is not null && All.Contains(role, StringComparer.OrdinalIgnoreCase);

    /// <summary>The canonical spelling of <paramref name="role"/>, or <c>null</c> when invalid.</summary>
    public static string? Normalise(string? role) =>
        role is null ? null : All.FirstOrDefault(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The blend modes a layer may take, in one place: the names the editor's UI exposes, each of which is
/// a <c>&lt;canvas&gt;</c> <c>globalCompositeOperation</c> (<c>normal</c> is <c>source-over</c>).
/// </summary>
/// <remarks>
/// Persisted as a lower-case string and validated at the API boundary, never stored unrecognised —
/// the same convention as <see cref="EditImageLayerRoles"/>.
/// </remarks>
public static class EditImageBlendModes
{
    public const string Normal = "normal";

    /// <summary>Every valid blend mode, in the order the UI lists them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Normal, "multiply", "screen", "overlay", "darken", "lighten", "soft-light", "hard-light", "difference"
    ];

    /// <summary>The canonical spelling of <paramref name="mode"/>, or <c>null</c> when invalid.</summary>
    public static string? Normalise(string? mode) =>
        mode is null ? null : All.FirstOrDefault(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One flattened render of a document (table <c>edit_image_exports</c>): a PNG the browser composed
/// and uploaded, registered as a <c>project_files</c> row. The row is what makes "the exports of this
/// document" listable and records which document revision was rendered.
/// </summary>
public class EditImageExport
{
    public Guid Id { get; set; }

    /// <summary>The document that was exported. Cascade-deleted with it.</summary>
    public Guid DocumentId { get; set; }

    /// <summary>
    /// The project file holding the PNG. <c>SET NULL</c> when the file is deleted: the history of
    /// having exported survives the user tidying their files, and the list simply shows no file.
    /// </summary>
    public Guid? ProjectFileId { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>The document revision that was rendered.</summary>
    public int Revision { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
