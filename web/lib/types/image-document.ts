import type { T } from '@/i18n/translator';
import { enT, labelOf } from '@/lib/i18n-default';
import type { ProjectFile } from './project';
import type { PictureEstimate } from '@/lib/editor/credit-estimate';

/**
 * The image editor's *layer document*: one picture, decomposed into ordered layers that each carry
 * their own pixels, a role and a transform. Mirrors the Inference API DTOs
 * (`ImageDocumentDtos.cs`) exactly — camelCase, like every other ReelBolt JSON surface.
 *
 * <p><b>Why this exists next to `ProjectFile`.</b> A project file is an uploaded asset; a layer is
 * a piece of one. A cut-out layer's pixels are not the source image at all: the segmentation
 * provider (fal.ai) was asked for "the person" and answered with an RGBA image of just that, which
 * is what lets the editor move, scale and recolour a subject without dragging its background along.
 * The source layer keeps the original image, so an edit is destructive to nothing.</p>
 */

/** What a layer is for: a background plate, a cut-out subject, or an overlay brought in on top. */
export type EditImageLayerRole = 'background' | 'subject' | 'overlay';

/**
 * `source` is the picture the document was created from; `cutout` is a subject the segmentation
 * provider cut out of it (or of another layer), with alpha.
 *
 * The thumbnail editor adds three (the server enum is append-only and persisted by member name, so
 * these mirror `EditImageLayerKind` in order): `generated` is a vendor output (a fill, an expansion,
 * an upscale, a new background, an instruction edit, a text-to-image result); `text` is vector text
 * with NO bitmap (its `imageUrl` answers 404 — see {@link TextContent}); `image` is a project image
 * placed as a layer, such as a logo.
 *
 * <p>The server serialises the member name as it is written in C# (`Cutout`), and this union is
 * lower-cased, so every layer crosses {@link parseLayerKind} on the way in
 * (`lib/api/image-documents.ts`) — that is the one place the casing is reconciled.</p>
 */
export type EditImageLayerKind = 'source' | 'cutout' | 'generated' | 'text' | 'image';

/** Every {@link EditImageLayerKind}, in server-enum order. Pinned by a drift test. */
export const EDIT_IMAGE_LAYER_KINDS = ['source', 'cutout', 'generated', 'text', 'image'] as const;

/**
 * Reads a kind off the wire in either casing. An unknown value is read as `image` (a bitmap layer
 * with no special behaviour) rather than thrown on: a newer server must not blank the editor.
 */
export function parseLayerKind(raw: string | null | undefined): EditImageLayerKind {
  const lower = (raw ?? '').trim().toLowerCase();
  return (EDIT_IMAGE_LAYER_KINDS as readonly string[]).includes(lower)
    ? (lower as EditImageLayerKind)
    : 'image';
}

/**
 * The blend modes a layer may carry, each the name of a `<canvas>` `globalCompositeOperation` except
 * `normal` (which is `source-over`). Validated at the API boundary; mirrored here and pinned by a
 * drift test.
 */
export const BLEND_MODES = [
  'normal',
  'multiply',
  'screen',
  'overlay',
  'darken',
  'lighten',
  'soft-light',
  'hard-light',
  'difference',
] as const;
export type BlendMode = (typeof BLEND_MODES)[number];

/** The `operation` values of `POST …/operations` (section 5 of the thumbnail editor contract). */
export const EDIT_IMAGE_OPERATIONS = [
  'erase',
  'fill',
  'expand',
  'replaceBackground',
  'relight',
  'upscale',
  'instruct',
  'generate',
  'removeBackground',
] as const;
export type EditImageOperation = (typeof EDIT_IMAGE_OPERATIONS)[number];

/** Text alignment inside a text layer's box. */
export type TextAlign = 'left' | 'center' | 'right';

/**
 * The vector content of a `text` layer (`content_json`). Rendered by the browser only; the server
 * validates shape and bounds (font size 8–600, stroke width 0–64, text ≤ 2 000 chars) and stores
 * it verbatim. `stroke`, `shadow` and `maxWidth` are `null` when absent.
 */
export interface TextContent {
  text: string;
  fontFamily: string;
  fontWeight: number;
  fontSize: number;
  lineHeight: number;
  letterSpacing: number;
  align: TextAlign;
  color: string;
  stroke: { color: string; width: number } | null;
  shadow: { color: string; blur: number; offsetX: number; offsetY: number } | null;
  maxWidth: number | null;
  uppercase: boolean;
}

/**
 * For a `generated` layer: what made it. An AI operation fills the first six; a designed graphic
 * (`operation: 'designGraphic'`) carries the design run's facts instead — which job and execution
 * made it, and the workspace version "Change this graphic" revises — so every field is optional
 * except `operation`. Read with care: the server may add fields, and may omit any of these.
 */
export interface LayerProvenance {
  operation: string;
  providerKind?: string | null;
  model?: string | null;
  /** What was asked for: the prompt of an operation, the description of a designed graphic. */
  prompt?: string | null;
  vendorRequestId?: string | null;
  parentLayerId?: string | null;
  /** A designed graphic: the job ({@link ImageGraphicJob}) that made it. */
  graphicId?: string | null;
  executionId?: string | null;
  workspaceId?: string | null;
  /** A designed graphic: the design version a revision starts from. Present = revisable. */
  workspaceVersionId?: string | null;
  versionNumber?: number | null;
  /** A designed graphic: the designer's plain-language summary. */
  summary?: string | null;
  frameWidth?: number | null;
  frameHeight?: number | null;
  cropX?: number | null;
  cropY?: number | null;
}

/**
 * What the content of a picture layer records when it was made with the project's avatars or objects
 * (docs/design/avatar-objects.md): who it shows, that an AI made it, and whether the exported picture must say so
 * (`Visible`: the person's avatar asks for a visible label; `MetadataOnly`: no label on the picture).
 */
export interface LayerSubjectContent {
  subjects?: { avatarId?: string | null; name: string; kind?: string | null }[] | null;
  aiGenerated?: boolean | null;
  disclosure?: 'Visible' | 'MetadataOnly' | (string & {}) | null;
}

/** Where the layer sits in the composite. Applied by the editor, never baked into the pixels. */
export interface EditImageLayerTransform {
  /** Canvas pixels of the layer's top-left corner. */
  x: number;
  y: number;
  /** Uniform. */
  scale: number;
  /** Degrees clockwise about the layer's centre. */
  rotation: number;
  /** Mirror about the layer's own centre; absent reads as false. */
  flipX?: boolean;
  flipY?: boolean;
}

/**
 * A non-destructive crop of a picture layer: the kept window as 0..1 fractions of the layer's own
 * bitmap from its top-left. Absent or the full window (0, 0, 1, 1) means uncropped; the stored
 * pixels never change, so a crop can be widened again.
 */
export interface LayerCrop {
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface EditImageLayer {
  id: string;
  zOrder: number;
  role: EditImageLayerRole;
  kind: EditImageLayerKind;
  label: string;
  /** The project file the pixels were cut from; null for a layer with no single source. */
  sourceProjectFileId: string | null;
  /** Ready to use in an `<img src>`: the layer's own pixels (RGBA cut-out for a cutout layer). */
  imageUrl: string;
  /** The binary matte, when the vendor returned one; null otherwise. */
  matteUrl: string | null;
  /** True exactly when `matteUrl` is set: the layer has a mask (every bitmap kind can). */
  hasMatte?: boolean;
  mimeType: string;
  width: number | null;
  height: number | null;
  opacity: number;
  transform: EditImageLayerTransform;
  /** Which model produced the cut-out, e.g. 'fal-ai/evf-sam'. Null for a source layer. */
  segmentationModel: string | null;
  /** The subject prompt that produced the cut-out, e.g. 'the person'. Null for a source layer. */
  segmentationPrompt: string | null;
  createdAt: string;
  /** Hidden layers are kept and still exported (as hidden). Absent reads as true. */
  visible?: boolean;
  /** The canvas refuses to move or scale a locked layer. Absent reads as false. */
  locked?: boolean;
  /** Absent reads as `normal`. */
  blendMode?: BlendMode;
  /**
   * The vector content of a `text` layer; null for a bitmap layer, except a picture made with the project's
   * avatars or objects, whose content records who it shows ({@link LayerSubjectContent}). Read a bitmap layer's
   * content with `aiMarkOf` (`lib/editor/ai-label.ts`), never by casting.
   */
  content?: TextContent | LayerSubjectContent | null;
  /** For a `generated` layer. */
  provenance?: LayerProvenance | null;
  /** Picture layers only. Absent or null reads as uncropped. When set, `transform` places the kept part's top-left. */
  crop?: LayerCrop | null;
}

/**
 * One row of `GET …/image-documents`, which answers WITHOUT layers: the list is a picker, and
 * fetching every layer of every document to draw one dropdown is work nothing asked for. The layers
 * come from {@link EditImageDocument} — the per-document read.
 */
export interface EditImageDocumentSummary {
  id: string;
  name: string | null;
  /** The project file the picture was made from, or null once that file was deleted (the picture keeps its own copy). */
  sourceProjectFileId: string | null;
  revision: number;
  /** How many layers the document has, so a picker can say so without loading them. */
  layerCount: number;
  createdAt: string;
  updatedAt: string;
}

export interface EditImageDocument {
  id: string;
  projectId: string;
  name: string | null;
  /** The project file the picture was made from, or null once that file was deleted (the picture keeps its own copy). */
  sourceProjectFileId: string | null;
  /** Bumped by every write under this document, so a caller can tell a stale read from a fresh one. */
  revision: number;
  createdAt: string;
  updatedAt: string;
  layers: EditImageLayer[];
  /** The composition size in pixels; null = the source image's own size. */
  canvasWidth?: number | null;
  canvasHeight?: number | null;
  /** CSS colour painted under the lowest layer; null = transparent. */
  backgroundColor?: string | null;
}

/** `POST …/image-documents` — open a new document on one project image. */
export interface CreateImageDocumentRequest {
  /** Omit it (with `width` and `height`) to start a blank picture. */
  sourceProjectFileId?: string;
  name?: string;
  /** Blank picture only: the colour behind the layers; absent means transparent. */
  backgroundColor?: string;
  /**
   * The photo's own pixel size as this browser decoded it (send both or neither, 1 to 65535). The
   * server measures the file itself and uses these only when it cannot read the header; the canvas
   * then opens at the photo's shape.
   */
  width?: number;
  height?: number;
}

/**
 * `POST …/image-documents/{id}/subjects` — ask the vendor to cut a subject out. Returns the NEW layer.
 *
 * <p>The field set is the backend's `SegmentSubjectRequest` as it is actually implemented (read from
 * `Controllers/Dto/EditImageDocumentDtos.cs`), not the shape the work package brief listed: the brief
 * carried `maskOnly`, which the implemented DTO does not declare, and omitted `variant`,
 * `role` and `providerId`, which it does. Nothing here sends `maskOnly`.</p>
 */
export interface DetectSubjectRequest {
  /** What to cut out, in prose: 'the person', 'the dog'. Required by the text-prompted segmenter. */
  prompt?: string;
  negativePrompt?: string;
  /** A fal endpoint id; omitted leaves the provider row's own model. An unknown id is a 400. */
  model?: string;
  /** A sub-model on the same endpoint where it documents one, e.g. 'Matting' on fal-ai/birefnet/v2. */
  variant?: string;
  /** The role the new layer takes. Omitted leaves the server default, 'subject'. */
  role?: EditImageLayerRole;
  /** An explicit segmentation provider row, overriding the workspace's default. */
  providerId?: string;
  /** Ask for semantic-level segmentation, where the endpoint documents it. */
  semanticParts?: boolean;
}

/** `PATCH …/image-documents/{id}/layers/{layerId}` — every field optional; absent leaves it unchanged. */
export interface UpdateImageLayerRequest {
  role?: EditImageLayerRole;
  /**
   * Where to MOVE the layer, 1-based — the server renumbers the rest. Expressed as a position rather
   * than a raw number so a caller never has to read the current numbering to reorder anything.
   */
  zOrder?: number;
  opacity?: number;
  transform?: Partial<EditImageLayerTransform>;
  visible?: boolean;
  locked?: boolean;
  blendMode?: BlendMode;
  label?: string;
  /** Text layers only. */
  content?: TextContent;
  /** Picture layers only. The full window (0, 0, 1, 1) removes the crop. */
  crop?: LayerCrop;
  /**
   * The revision the caller last saw. A stale one is a 409 and nothing is written, so two editors of
   * one document collide instead of silently overwriting each other.
   */
  baseRevision?: number;
}

/**
 * The fal.ai endpoints the segmentation capability can be pointed at. Real endpoint ids from the
 * vendor's own catalogue (fal.ai/models), not inventions: a wrong id is a 404 at run time and
 * nothing here would have caught it.
 *
 * `sam-3-1/image` is the default: the newest SAM, it takes a text prompt, clicks and boxes, can return
 * several candidate masks, and is billed per request. `evf-sam` takes the text prompt ("the person") and is the default because it is the one that
 * does what the control says; `sam2/image` is prompt-driven segmentation too; `birefnet/v2` cuts a
 * salient subject out with no prompt at all.
 */
export const SEGMENTATION_MODELS = [
  'fal-ai/sam-3-1/image',
  'fal-ai/evf-sam',
  'fal-ai/sam2/image',
  'fal-ai/birefnet/v2',
] as const;

/** The fal.ai endpoints the depth capability can be pointed at (real ids, as above). */
export const DEPTH_MODELS = ['fal-ai/imageutils/depth', 'fal-ai/imageutils/marigold-depth'] as const;

/** What the editor's model picker offers: the id it posts, and a plain-language name for it. */
export interface SegmentationModelOption {
  value: string;
  label: string;
}

/** The {@link SEGMENTATION_MODELS} ids with the labels the panel shows. */
export const SEGMENTATION_MODEL_OPTIONS: SegmentationModelOption[] = [
  { value: 'fal-ai/sam-3-1/image', label: enT('libLabels.segmentationModels.falAiSam31Image') },
  { value: 'fal-ai/evf-sam', label: enT('libLabels.segmentationModels.falAiEvfSam') },
  { value: 'fal-ai/sam2/image', label: enT('libLabels.segmentationModels.falAiSam2Image') },
  { value: 'fal-ai/birefnet/v2', label: enT('libLabels.segmentationModels.falAiBirefnetV2') },
];

/** The model's label in the active language (an id with no entry shows as it is). */
export function segmentationModelLabel(t: T, value: string): string {
  return labelOf(t, 'segmentationModels', value);
}

/** The model a brand-new detect control starts on: SAM 3.1, the newest segmenter. */
export const DEFAULT_SEGMENTATION_MODEL = SEGMENTATION_MODELS[0];

// ─────────────────────────────────────────────────────────────────────────────────────────────
// The thumbnail editor (docs/design/thumbnail-editor.md). Everything below is the contract's JSON
// shape, camelCase. Where the contract leaves a response implicit the choice is made HERE and in
// lib/api/image-documents.ts, so aligning with the server later is a change to those two files.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/** `PATCH …/image-documents/{id}` — every field optional; absent leaves it unchanged. */
export interface UpdateImageDocumentRequest {
  name?: string;
  canvasWidth?: number;
  canvasHeight?: number;
  /** A CSS colour, or `null` to clear back to transparent. */
  backgroundColor?: string | null;
  /** A stale one is a 409 `REVISION_CONFLICT` and nothing is written. */
  baseRevision?: number;
}

/**
 * Why the editor asks for a presigned PUT: a brush `mask`, an `export`, or a `source` - a smaller copy of
 * a layer for an AI service whose documented limits its original is over (a PNG or JPEG of at most 48 MiB;
 * the operation answers `422 SOURCE_TOO_LARGE` with the size to make it).
 */
export type AssetUploadPurpose = 'mask' | 'export' | 'source';

/**
 * The content types an upload may declare: a mask is always `image/png`; an export is any of
 * these (the server's `EditImageDocumentSupport.ExportFormats`, pinned by the drift test). The two
 * layered ones keep the editor's layers: Photoshop and OpenRaster.
 */
export const EXPORT_CONTENT_TYPES = [
  'image/png',
  'image/jpeg',
  'image/webp',
  'image/avif',
  'image/vnd.adobe.photoshop',
  'image/openraster',
] as const;
export type ExportContentType = (typeof EXPORT_CONTENT_TYPES)[number];

/** `POST …/assets/upload-url` body. A mask is 1 byte to 12 MiB; an export 1 byte to 200 MiB. */
export interface AssetUploadRequest {
  purpose: AssetUploadPurpose;
  contentType: ExportContentType;
  contentLength: number;
}

/**
 * `POST …/assets/upload-url` result: a presigned PUT. `url` is a bearer capability, held in memory
 * for one upload and never logged; `headers` MUST be sent on the PUT unchanged (the signature covers
 * them). The contract names `expiresAt` without a type; an ISO string is assumed.
 */
export interface AssetUploadTicket {
  key: string;
  url: string;
  headers: Record<string, string>;
  expiresAt: string;
}

/** `POST …/layers/text` body. */
export interface CreateTextLayerRequest {
  content: TextContent;
  transform?: Partial<EditImageLayerTransform>;
  role?: EditImageLayerRole;
  label?: string;
}

/** `POST …/layers/from-file` body. */
export interface CreateLayerFromFileRequest {
  projectFileId: string;
  role?: EditImageLayerRole;
  label?: string;
}

/** A point prompt in the cut-from picture's pixels; `label` 1 = include this spot, 0 = exclude it. */
export interface SubjectPointPrompt {
  x: number;
  y: number;
  label: 0 | 1;
}

/** A box prompt in the cut-from picture's pixels. */
export interface SubjectBoxPrompt {
  xMin: number;
  yMin: number;
  xMax: number;
  yMax: number;
}

/**
 * `POST …/subjects`, the extended form: text, point and box prompts, and several masks back. The
 * answer is one `cutout` layer per returned mask. Coordinates are passed to the vendor verbatim.
 */
export interface SegmentSubjectsRequest extends DetectSubjectRequest {
  /**
   * The picture layer to cut from (an uploaded image, the original, an AI result, an earlier cut-out).
   * Point and box prompts are then in THAT layer's pixels, and each cut-out lands over it with its
   * position and size. Absent: the document's original image, as before.
   */
  layerId?: string;
  pointPrompts?: SubjectPointPrompt[];
  boxPrompts?: SubjectBoxPrompt[];
  returnMultipleMasks?: boolean;
  maxMasks?: number;
}

/** `POST …/operations` body (section 5). */
export interface ImageOperationRequest {
  operation: EditImageOperation;
  layerId?: string;
  /** A key from an `assets/upload-url` ticket with purpose `mask` (white = the region to act on). */
  maskKey?: string;
  /**
   * A key from an `assets/upload-url` ticket with purpose `source`: the copy of the layer the server asked
   * for with `422 SOURCE_TOO_LARGE`, sent instead of the original. Ignored when the original fits.
   */
  sourceKey?: string;
  /**
   * `instruct` only: the project FILE ids (`ProjectFile.id`) of pictures to edit against — "replace
   * the face with this person", "put the badge from the second picture on the jacket". The server
   * sends them to the model AFTER the picture being edited and IN THIS ORDER, so the order the user
   * chose them in is the order they are referred to by.
   *
   * <p>At most 4 — `MAX_REFERENCE_PICTURES` in `lib/editor/ai-operations.ts` is that one number. The
   * server refuses a fifth, a non-image and a file of another project with `400 { code, message }`.
   * Empty or absent means no reference pictures, and the key is then omitted from the body entirely.
   * No other operation may send this field.</p>
   */
  referenceProjectFileIds?: string[];
  /**
   * `generate` and `instruct` only: the ids of the project's avatars and objects to show (1..3, at most one of
   * them a person; docs/design/avatar-objects.md "Avatars and objects in the picture editor"). The server takes
   * their own reference pictures; nothing else about them is sent from here. Empty means none, and the key is
   * then left out of the body entirely. No other operation may send this field.
   */
  subjectIds?: string[];
  prompt?: string;
  negativePrompt?: string;
  model?: string;
  providerId?: string;
  params?: Record<string, number | string | boolean>;
  role?: EditImageLayerRole;
  label?: string;
}

/** `POST …/operations` result: the new layers, what served them, the billed quantity and the new document revision. */
export interface ImageOperationResult {
  layers: EditImageLayer[];
  operation: EditImageOperation;
  /**
   * What served it. `capability` is the capability the row was resolved as: the tool's own
   * (`ImageUpscale` for an upscale) or `ImageEdit` when the tool fell back to the editing row.
   */
  provider: { kind: string; model: string; capability?: string | null };
  usage?: { unit: string; quantity: number };
  /** What this run cost, in credits; absent or 0 when billing is off. */
  chargedCredits?: number | null;
  /** The document revision after the write. */
  revision?: number;
  /**
   * Something about the result the user should know, in plain words, or null. Today: the AI returned a
   * differently shaped picture, so a cut-out's transparent background could not be carried over.
   */
  notice?: string | null;
  /**
   * Set when the layer this was made for was deleted while it ran: the output is kept but HIDDEN, and the
   * user is asked what to do with it. Absent for a normal result.
   */
  needsReview?: NeedsReview | null;
}

/** Why a finished result waits for the user's OK (additive on an operation answer and on a graphic read). */
export interface NeedsReview {
  reason: 'layerGone';
  /** The layer the result was made for, now deleted. */
  layerId: string;
}

/**
 * `GET …/image-documents/operations/{operation}/capabilities`: what the AI service set up for a tool can take,
 * read from its catalogue (nothing is sent to a vendor). The panel shows only the controls it honours.
 */
export interface ImageOperationCapabilities {
  operation: EditImageOperation;
  provider: { kind: string; model: string; capability?: string | null };
  /** `false`: the model documents no prompt, so no prompt field is shown. `null`: not established. */
  takesPrompt: boolean | null;
  maxPromptLength: number;
  /** What one run costs and whether the balance covers it. Absent on an older server. */
  estimate?: PictureEstimate | null;
  /** True when the caller may set up the workspace's AI services (members are told to ask an admin). */
  canConfigure?: boolean;
  /**
   * `generate` and `instruct`: whether the model set up for the tool can be shown the project's avatars and
   * objects, how many at once, and (when it cannot) the models that can. Absent on an older server, which is read
   * as "it can" (see `subjectSupport` in `lib/editor/ai-operations.ts`).
   */
  subjects?: { supported: boolean; maxSubjects?: number | null; models?: string[] | null } | null;
  /** For `expand` on a catalogued endpoint: how it takes the new size, and its documented limits. */
  expand: {
    /** `margins`: pixels per side, the picture kept at the size sent. `canvas`: the finished canvas. */
    geometry: 'margins' | 'canvas';
    /** The per-side cap of a margins service, in pixels of the picture sent. */
    maxMarginPx: number | null;
    maxOutputPixels: number | null;
    maxInputPixels: number | null;
    basis: string | null;
  } | null;
}

/** The body of `422 SOURCE_TOO_LARGE`: the size of the copy to send instead of the original. */
export interface SourceTooLargeBody {
  code: 'SOURCE_TOO_LARGE';
  message: string;
  width: number;
  height: number;
  /** `displayed`: the size the layer appears in the design; `smaller`: even that was too large. */
  source: 'displayed' | 'smaller';
}

/** `POST …/exports` body. `key` comes from an `export` upload ticket. */
export interface CreateImageExportRequest {
  key: string;
  width: number;
  height: number;
  fileName: string;
}

export interface EditImageExport {
  id: string;
  documentId: string;
  projectFileId: string | null;
  /** Where the exported file is served from. */
  fileUrl?: string | null;
  width: number;
  height: number;
  /** The document revision that was exported. */
  revision: number;
  createdAt: string;
}

/** `POST …/exports` result. */
export interface CreateImageExportResult {
  export: EditImageExport;
  projectFile: ProjectFile;
  /** Always false for now: the server does not yet apply an export as the project thumbnail. */
  projectThumbnailApplied?: boolean;
}

/**
 * The error codes the thumbnail editor branches on (`body.code` of a non-2xx answer). Provider
 * codes are matched by suffix: the contract writes them as `*_PROVIDER_UNAVAILABLE`.
 */
export const IMAGE_EDITOR_ERROR_CODES = {
  revisionConflict: 'REVISION_CONFLICT',
  operationTimeout: 'OPERATION_TIMEOUT',
  operationUnsupported: 'OPERATION_UNSUPPORTED',
  maskRequired: 'MASK_REQUIRED',
  layerHasNoBitmap: 'LAYER_HAS_NO_BITMAP',
  exportKeyInvalid: 'EXPORT_KEY_INVALID',
  uploadObjectMissing: 'UPLOAD_OBJECT_MISSING',
  directUploadDisabled: 'DIRECT_UPLOAD_DISABLED',
  operationInvalid: 'OPERATION_INVALID',
  operationFailed: 'OPERATION_FAILED',
  sourceTooLarge: 'SOURCE_TOO_LARGE',
  imageSourceUnsupported: 'IMAGE_SOURCE_UNSUPPORTED',
  providerUnavailableSuffix: '_PROVIDER_UNAVAILABLE',
} as const;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// "Design a graphic": an AI designer draws one graphic (a title, a badge, a price tag, a shape) as a
// new transparent layer. It runs for minutes as a hidden workflow execution, so the route answers a
// JOB at once and the editor polls `GET …/graphics` (which also settles finished jobs server-side:
// a `Passed` job's `layer` is the layer it inserted).
// ─────────────────────────────────────────────────────────────────────────────────────────────

/** Where in the frame the graphic goes. */
export const GRAPHIC_PLACEMENTS = [
  'center',
  'top',
  'bottom',
  'left',
  'right',
  'topLeft',
  'topRight',
  'bottomLeft',
  'bottomRight',
] as const;
export type GraphicPlacement = (typeof GRAPHIC_PLACEMENTS)[number];

export const GRAPHIC_JOB_STATUSES = ['Queued', 'Running', 'Passed', 'Failed', 'Cancelled'] as const;
export type GraphicJobStatus = (typeof GRAPHIC_JOB_STATUSES)[number];

/** A rectangle in canvas (document) pixels. */
export interface GraphicTarget {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface ImageGraphicJob {
  id: string;
  documentId: string;
  executionId: string;
  workflowId: string;
  status: GraphicJobStatus;
  /** What the user asked for — or, when revising, the change they asked for. */
  description: string;
  /** The frame the designer works in, in pixels. */
  frameWidth: number;
  frameHeight: number;
  placement: GraphicPlacement;
  /** Where on the canvas the graphic is expected to land (the "working" effect is drawn there). */
  target: GraphicTarget;
  /** Set for "Change this graphic": the layer being revised. */
  revisesLayerId: string | null;
  /** The inserted layer's id, once the job finished and was settled. */
  layerId: string | null;
  /** The inserted layer itself, once settled. */
  layer: EditImageLayer | null;
  /** The designer's plain-language summary of what it made. */
  summary: string | null;
  /** Why it failed, in plain words. */
  error: string | null;
  createdAt: string;
  completedAt: string | null;
  /** What the run cost in credits, once settled; absent or 0 when billing is off. */
  chargedCredits?: number | null;
  /** Set when the layer it was made for was deleted meanwhile: the layer is kept hidden until the user decides. */
  needsReview?: NeedsReview | null;
}

/** The cost of one design run, in credits. */
export interface ImageGraphicEstimate {
  billingEnabled: boolean;
  lowCredits: number;
  highCredits: number;
  requiredCredits: number;
  balanceCredits: number;
  affordable: boolean;
}

/** `GET …/graphics`. */
export interface ImageGraphicsResponse {
  /** Newest first. */
  graphics: ImageGraphicJob[];
  /** Null when billing is off or the cost is not known. */
  estimate: ImageGraphicEstimate | null;
  /** False when this installation cannot design graphics; `unavailableReason` says why. */
  available: boolean;
  unavailableReason: string | null;
  /** A code for why it is unavailable (`NO_PROVIDER`, `DISABLED`, ...), or null. */
  unavailableCode?: string | null;
  /** True when the caller may set up the workspace's AI services. */
  canConfigure?: boolean;
}

/** `POST …/graphics` body. */
export interface CreateImageGraphicRequest {
  /** 1..2000 characters: the graphic, or (with `revisesLayerId`) the change to make. */
  description: string;
  placement?: GraphicPlacement;
  /** The frame to design in, 64..4096 each; omitted = the whole canvas. */
  width?: number;
  height?: number;
  /** The canvas size the editor shows; the server uses it only when the document stores none. */
  canvasWidth?: number;
  canvasHeight?: number;
  /** "Change this graphic": the designed layer to revise. */
  revisesLayerId?: string;
}

/** The error codes `POST …/graphics` answers with (`body.code`). */
export const IMAGE_GRAPHIC_ERROR_CODES = {
  insufficientCredits: 'INSUFFICIENT_CREDITS',
  notRevisable: 'GRAPHIC_NOT_REVISABLE',
  unavailable: 'GRAPHIC_UNAVAILABLE',
  rateLimited: 'GRAPHIC_RATE_LIMITED',
  agentUnavailable: 'GRAPHIC_AGENT_UNAVAILABLE',
} as const;
