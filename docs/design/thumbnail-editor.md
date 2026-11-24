# The thumbnail editor: contract for the layered image editor (design, 2026-10-10)

**Status:** contract agreed; implementation in flight across three work packages (backend fal arm,
backend document operations, web canvas editor). Read this before touching
`EditImageDocumentsController`, `web/components/editor/**` or `FalAiAnalysisClient`.

**Owner ask, verbatim:** "the best ai assisted thumbnail editor ever, on the level of photoshop on
steroids" — a layered 2D editor on top of the existing image document (`edit_image_documents` /
`edit_image_layers`, see [`editor.md`](../editor.md)), where every expensive pixel operation is a
hosted model call (fal.ai first; the existing BlackForestLabs / MiniMax / OpenAI arms where they
already serve the job) and everything else happens in the browser on a `<canvas>`.

The binding constraints from `BRIEF-image-subject-detection.md` still hold: no GPU we host, 2.5D not
3D, vendors disclosed, **never invent an endpoint, field, enum or price**, and every (kind,
capability, usage kind) priced in the same change. `docs/fal-ai-models.md` is the vendor-fact sheet
(every row carries the URL it was read from); nothing in this document overrides it.

---

## 1. Architecture in one picture

    browser  <canvas> compositor (layers, transforms, text, brush masks, export PNG)
       |  JSON + presigned PUT (masks, exports -> R2 directly, never through nginx)
       v
    Inference API  EditImageDocumentsController (+ EditImageOperations)
       |- IImageAnalysisClient   Segmentation (SAM 3.1 / birefnet) -> cut-out layers
       |- IImageClient           one capability per tool: ImageErase / ImageFill / ImageExpand /
       |                         ImageBackgroundReplace / ImageRelight / ImageUpscale / ImageEdit /
       |                         ImageGeneration (fal.ai, BFL, MiniMax, OpenAI)
       |- IPresignedUrlService   source/mask/layer URLs handed to the vendor; PUT tickets for the browser
       \- InferenceApiDbContext  documents, layers, exports

**The server never rasterises.** There is no raster library in either .NET service and none is added:
compositing, text rendering, brush masks and the flattened export are all done by the browser. Every
server-side operation takes an existing layer's bitmap (already in object storage) plus, optionally, a
mask the browser uploaded, hands presigned URLs to the vendor, and stores the vendor's output as a
**new layer**. Nothing is ever edited in place; a layer is immutable once written (that is what makes
the `immutable` cache header on layer bytes correct).

**Uploads go to R2 directly.** nginx caps `/api/v1/*` bodies (1–2 MB), and the cloud object store is
Cloudflare R2 reached through presigned PUTs (the one upload path proven to work there). A mask or an
an export is therefore: `POST …/assets/upload-url` → browser `PUT`s the PNG to storage → the API is told
the key. Base64 bodies are not used for pixels.

---

## 2. Data model changes (one migration, `AddThumbnailEditor`)

`EditImageDocument` gains:

| Column | Type | Meaning |
|---|---|---|
| `canvas_width`, `canvas_height` | int, nullable | The composition size. NULL = the source image's size. The UI offers eight shapes at 720p–4K (short side) or an exact size; only the two numbers are stored. |
| `background_color` | text, nullable | CSS colour painted under the lowest layer (NULL = transparent). |

`EditImageLayer` gains:

| Column | Type | Meaning |
|---|---|---|
| `visible` | bool, default true | Hidden layers are kept and still exported as hidden. |
| `locked` | bool, default false | The canvas refuses to move/scale a locked layer. |
| `blend_mode` | text, default `normal` | One of the `<canvas>` `globalCompositeOperation` names the UI exposes: `normal` (`source-over`), `multiply`, `screen`, `overlay`, `darken`, `lighten`, `soft-light`, `hard-light`, `difference`. Validated at the API boundary. |
| `content_json` | jsonb, nullable | The vector content of a non-bitmap layer (today: text). See §4. |
| `provenance_json` | jsonb, nullable | For a `Generated` layer: `{ operation, providerKind, model, prompt, vendorRequestId, parentLayerId }`. The `SegmentationModel`/`SegmentationPrompt` columns stay for `Cutout` layers. |

`EditImageLayerKind` (append-only, persisted by name) gains, in this order: `Generated` (a vendor
output: fill, expansion, upscale, background, instruction edit, text-to-image), `Text` (vector text,
no bitmap: `StorageKey` is empty, the image endpoint answers 404), `Image` (a project image placed as
a layer, e.g. a logo).

`TransformJson` gains `flipX` and `flipY` (bool, default false) next to `x`, `y`, `scale`, `rotation`.
Units: `x`/`y` are canvas pixels of the layer's top-left corner; `scale` is uniform; `rotation` is
degrees clockwise about the layer's centre.

**Added later (editor remediation W0.8, decision D10, migration `PictureCopyOnDelete`).**
`edit_image_documents.source_project_file_id` is nullable with `ON DELETE SET NULL` (it was a cascade:
deleting the photo deleted every picture made from it). Before a project file is deleted, every picture with
a layer on its storage key gets its own copy under `projects/{p}/agentFiles/image-editor/{documentId}/`,
every such layer is repointed, and `edit_image_layers.copy_size_bytes` (bigint, nullable) records the copy's
size so it counts toward storage and the plan quota. A user's delete (Files, assistant) is refused before any
copy with `409 PICTURE_COPIES_OVER_QUOTA` when the copies' net growth does not fit the plan's storage; a sync
or output removal cannot be refused and copies regardless. `GET
…/files/{fileId}/picture-usage` → `{ pictures }` feeds the delete dialog. A new picture opens at its photo's
own size (header-measured, long side capped at 4096, source layer scaled to fit). A layer whose object is
gone is `404 LAYER_IMAGE_MISSING`; an empty mask or export upload is `400 UPLOAD_EMPTY`. The `409
NO_SOURCE_IMAGE` stays for a cut-out without a layer on a legacy picture that has no copy. Details:
`docs/editor.md` "The thumbnail editor's document API".

A new table **`edit_image_exports`**: `Id`, `DocumentId` (cascade), `ProjectFileId` (SET NULL), `Width`,
`Height`, `Revision` (the document revision exported), `CreatedAt`. An export is a project file the
browser rendered and uploaded; the row is what makes "exports of this document" listable.

---

## 3. API surface (all under `/api/v1/projects/{projectId}/image-documents`, camelCase)

Existing routes keep their shape (see `editor.md`). Additions:

| Method | Path | Body → result |
|---|---|---|
| PATCH | `/{documentId}` | `{ name?, canvasWidth?, canvasHeight?, backgroundColor?, baseRevision? }` → document (409 `REVISION_CONFLICT` on mismatch, nothing written) |
| POST | `/{documentId}/assets/upload-url` | `{ purpose: "mask" \| "export", contentType, contentLength }` → `{ key, url, headers, expiresAt }` — a presigned PUT under `projects/{p}/agentFiles/image-editor/{documentId}/{purpose}-{guid}{ext}`. A mask is `image/png`, max 12 MiB; an export is `image/png`, `image/jpeg`, `image/webp`, `image/avif`, `image/vnd.adobe.photoshop` or `image/openraster`, max 200 MiB, and the key's extension follows the type. Both get a `storage_retention_marks` row with a 24 h horizon; an `export` key is promoted by the export route. |
| POST | `/{documentId}/layers/text` | `{ content: TextContent, transform?, role?, label? }` → the `Text` layer |
| POST | `/{documentId}/layers/from-file` | `{ projectFileId, role?, label? }` → an `Image` layer (409 `IMAGE_SOURCE_UNSUPPORTED` unless the file is an image) |
| POST | `/{documentId}/subjects` | **extended**: `{ prompt?, pointPrompts?: [{x,y,label}], boxPrompts?: [{xMin,yMin,xMax,yMax}], returnMultipleMasks?, maxMasks?, model?, variant?, role?, providerId? }` → `EditImageLayerDto[]` (one `Cutout` layer per returned mask; one when the model returns one). Point/box coordinates are **source-image pixels**; the controller passes them to the vendor as the vendor's schema states (SAM 3.1 declares integers with no stated space — the client passes pixels through verbatim and the live-verification step measures it). |
| POST | `/{documentId}/operations` | `OperationRequest` (§5) → `{ layers: EditImageLayerDto[], operation, provider: { kind, model }, usage?: { unit, quantity } }`. Synchronous; the controller caps the vendor wait at 90 s and answers 424 `OPERATION_TIMEOUT` past it (Cloudflare's 100 s edge limit; 424, not 504, because Cloudflare replaces a 502/504 body). A vendor failure is 424 `OPERATION_FAILED`; an expand whose original is over the endpoint's limits is 422 `SOURCE_TOO_LARGE` (see `docs/editor.md` "Expanding to a shape"). |
| POST | `/{documentId}/exports` | `{ key (from an export upload-url), width, height, fileName, setAsProjectThumbnail? }` → `{ export: EditImageExportDto, projectFile: ProjectFileDto }` — registers the uploaded file as a `project_files` row (category `userFiles`, directory `thumbnails/`, the type the key was issued for), writes the export row. |
| GET | `/{documentId}/exports` | `EditImageExportDto[]` |
| PATCH | `/{documentId}/layers/{layerId}` | **extended** with `visible?`, `locked?`, `blendMode?`, `label?`, `content?` (Text layers only) |

Error vocabulary is unchanged and extended only with `OPERATION_TIMEOUT`, `OPERATION_UNSUPPORTED`
(the resolved provider kind cannot perform that operation), `MASK_REQUIRED`, `LAYER_HAS_NO_BITMAP`
(an operation that needs pixels was pointed at a `Text` layer), `EXPORT_KEY_INVALID`.

Every action consults `IProjectAccessService` exactly once (the tenancy inventory test counts
occurrences in the controller source — do not share a helper). A document in another project is 404.

---

## 4. Text layer content

```json
{
  "text": "COME PERMETTERSI\nUNA M2 A 23 ANNI",
  "fontFamily": "Chakra Petch",
  "fontWeight": 700,
  "fontSize": 96,
  "lineHeight": 1.05,
  "letterSpacing": 0,
  "align": "left",
  "color": "#ffffff",
  "stroke": { "color": "#000000", "width": 6 },
  "shadow": { "color": "rgba(0,0,0,0.6)", "blur": 18, "offsetX": 0, "offsetY": 8 },
  "maxWidth": 900,
  "uppercase": false
}
```

Rendered by the browser only (`<canvas>` `fillText`/`strokeText`, fonts loaded from Google Fonts on
demand from a curated list that includes the brand's Chakra Petch and JetBrains Mono). The API
validates shape and bounds (font size 8–600, stroke width 0–64, text ≤ 2 000 chars) and stores it
verbatim. No server-side text rasterisation exists or is planned.

---

## 5. Operations

`OperationRequest`: `{ operation, layerId?, maskKey?, prompt?, negativePrompt?, model?, providerId?, params?: {...}, role?, label? }`.

| `operation` | Needs | Vendor arm | Produces |
|---|---|---|---|
| `erase` | `layerId` (bitmap), `maskKey` (white = remove) | `IImageClient.InpaintAsync` with an empty/"background" prompt — BFL `flux-pro-1.0-fill` (polarity documented: black preserved, white inpainted) when a BFL `ImageEdit` row resolves, else fal `fal-ai/lama` | one `Generated` layer, same transform as the source layer, inserted directly above it |
| `fill` | `layerId`, `maskKey`, `prompt` | `InpaintAsync` (BFL fill, fal fill-capable endpoint per the fact sheet) | one `Generated` layer |
| `expand` | `layerId`, `params.{top,right,bottom,left}` px or `params.{width,height}` | an outpainting endpoint from the fact sheet | one `Generated` layer sized to the new bounds |
| `replaceBackground` | `layerId`, `prompt` | background-replace endpoint from the fact sheet | one `Generated` layer |
| `relight` | `layerId`, `prompt` and/or `params` per the endpoint schema | relighting endpoint from the fact sheet | one `Generated` layer |
| `upscale` | `layerId`, `params.factor` (2 or 4 where the schema allows) | upscale endpoint from the fact sheet | one `Generated` layer, `Width`/`Height` from the output |
| `instruct` | `layerId`, `prompt` | `IImageClient.EditAsync` (BFL Kontext, fal instruction-edit endpoints, OpenAI edits, MiniMax edit) | one `Generated` layer |
| `generate` | `prompt`, `params.{width,height}` or aspect | `IImageClient.GenerateAsync` (any `ImageGeneration` row) | one `Generated` layer placed as a backdrop (zOrder 1, role `background`) |
| `removeBackground` | `layerId` | `IImageAnalysisClient` birefnet (`Matting`) | one `Cutout` layer with a matte |

Resolution (revised 2026-10-10, see `docs/editor.md` "Thumbnail editor operations"): each tool resolves
its OWN capability — `ImageErase`, `ImageFill`, `ImageExpand`, `ImageBackgroundReplace`, `ImageRelight`,
`ImageUpscale`, `ImageEdit` (instruct), `ImageGeneration` (generate), `Segmentation` (subjects and
removeBackground) — an explicit `providerId`, else that capability's single default row. A job with no row of
its own falls back to the `ImageEdit` default only when that row's kind serves the job; else 409
`*_PROVIDER_UNAVAILABLE` with the capability named. On a fal.ai row the **endpoint** is the row's own model
when it is a catalogued endpoint for the job, else the catalogue default for the job (overridable by
`model`), so a pre-split `FalAi` `ImageEdit` row still serves every tool. A kind that cannot do the operation
is 409 `OPERATION_UNSUPPORTED` before anything is sent. The original single-`ImageEdit` design sent one
row's model to every tool, which is why it was split.

Metering: every operation records usage through the existing metering decorators
(`ImageEditImages` / `ImageGenImages` / segmentation units), and every fal endpoint the catalogue
names has a **cited** model-keyed rate card (`fal.ai/models/{id}` price line) — the coverage tests
enforce it.

---

## 6. The browser editor

Route: `/app/editor/thumbnails/[documentId]` (project-scoped through the document), reached from the
Editor page's Pictures view (`/app/editor?view=pictures`, in the same card as the videos) and from a
project's Files tab on any image. A layered export (PSD, ORA) is not an editable image: the server
refuses it as a source or a placed layer.

- **Canvas**: an infinite, pannable and zoomable workspace with the document's frame in it; layers
  past the frame stay visible beside it, dimmed, and only exports crop to it. Drawn by a pure compositor (`web/lib/editor/compose.ts`,
  no React) from `layers[]` + loaded bitmaps: for each visible layer in z-order apply transform
  (translate, rotate about centre, scale, flips), `globalAlpha`, `globalCompositeOperation`; `Cutout`
  with a matte draws the bitmap then `destination-in` the matte; `Text` layers draw text per §4.
- **Tools**: select/move/scale/rotate with handles; brush mask (paints into an offscreen mask canvas,
  exported as a white-on-black PNG at source-layer resolution for `erase`/`fill`); text tool; subject
  tool (click = point prompt, drag = box prompt, or type a name) calling `/subjects`; AI panel with
  one compact "Choose an AI tool" entry opening a picker (full-screen on a phone) of every operation of
  §5 and the subject cut-out, each with an icon and a one-sentence description, then only the chosen
  tool's controls, showing the resolved provider/model after a run; layers panel (drag reorder → `PATCH zOrder`,
  visibility, lock, opacity, blend mode, rename, delete); canvas shape (16:9, 9:16, 1:1, 4:5, 4:3, 3:2, 2:3, 21:9) and size (720p–4K by the short side, or
  exact pixels); export as PNG, JPEG, WebP, AVIF or a layered PSD / ORA (render → upload-url → PUT →
  `/exports`, or a download); a project image larger than the canvas is fitted inside it on insert.
- **Undo/redo**: client-side command stack over the document state; server writes are replayed from
  it (optimistic, with `baseRevision`, 409 reloads).
- **AI effect**: while an operation runs, the affected region of the canvas is blurred and shimmers in
  place; when the result layer has loaded it resolves from blur to sharp (about 800 ms, a still blur and
  an instant result under `prefers-reduced-motion`). The separate "reveal preview" of the first slice
  was removed.
- No new npm dependency for the canvas. The one dependency the editor has is for **export** only:
  `ag-psd` (MIT) writes the layered Photoshop file, loaded on demand; OpenRaster is a ZIP the editor
  writes itself.

---

## 6a. Added later: "Design a graphic" and the assistant in the editor (2026-10-10)

**Design a graphic.** One AI tool is not a vendor call: an agent (`AgentType.GraphicDesigner`) DESIGNS a still graphic in
React — a Remotion `<Still>` on a transparent frame, in the same sandbox and toolchain as the video Author, with the
taste/minimalist/brutalist skills — and the platform renders it to a PNG, verifies and crops it, and adds it as a
`Generated` layer. It is long-running (agent loop + render), so it is NOT an `operations` call with the 90-second bound:
`POST …/graphics` queues an execution of the project's hidden "Picture graphics" workflow (`StepType.DesignGraphic`),
which brings credits (preflight 402, reserve/settle/refund by failure class), leases, cancellation, SSE progress and history.
The graphic's source is recorded as a composition workspace version (the revision library), so "Change this graphic"
restores it and the designer edits rather than redraws. The server still never rasterises: the PNG comes out of the
sandbox's headless browser; the API only crops it (`PngAlpha.Crop`, row-streamed) after verifying it. Full reference:
[`../editor.md`](../editor.md) "Designed graphics".

**The assistant in the editor.** The platform assistant has 14 picture tools (describe, list tools, and Propose/Apply for
text layers, layer edits, deletes, the canvas, a paid AI tool and a designed graphic). Every apply goes through the same
controller actions this contract defines, so the rules here bind the assistant exactly as they bind the browser. `erase`
and `fill` need a mask: the assistant names a rectangle (fractions of the layer) and the server draws that rectangle as the
mask at the layer's size. Applied changes stream to the open editor (`data-imageDocumentChanged`), which reloads and
highlights the changed layers. See [`../platform-assistant-mcp.md`](../platform-assistant-mcp.md) "Picture editor tools".

## 7. What is deliberately out of scope in this slice

Server-side rendering of text or composites; non-destructive adjustment layers (curves, levels);
vector shapes beyond text; collaborative editing; the depth-based 2.5D parallax renderer (row I4 stays
as it is); any vendor we could not establish from its own schema (listed in `fal-ai-models.md`).

## 8. Verification that needs a key

No fal.ai credential exists in any deployment or secret store today, so every fal call is tested
against a mocked transport. Before the feature is used in staging an admin must create a `FalAi`
provider row (`Segmentation`, then **Set up all picture tools** for one row per picture capability) with a fal key; the live-verification checklist in
`fal-ai-models.md` §"to measure" (mask polarity for lama, SAM point/box coordinate space) is run
then, and the findings are written back into the fact sheet.
