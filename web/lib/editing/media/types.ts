/**
 * TypeScript mirror of the committed media-pool contract.
 *
 * Source of truth: `media-pool.schema.json` **beside this file** — which is itself, and unusually,
 * the frontend's own transcription of T-101/T-102 rather than a generated backend artifact. See
 * that file's `$comment` for exactly what was read from backend code (the `MediaDerivativeKind`
 * enum, the `VideoAnalysisMedia` record) and what was inferred from repo convention (enum casing).
 *
 * This module is hand-written, so `__tests__/schema-drift.test.ts` asserts every closed union below
 * against the schema's `enum`s in both directions — never edit one side without the other.
 *
 * Mirroring rules (the same ones `web/lib/editing/types.ts` follows):
 *   - Names are camelCase, because the C# enums go through
 *     `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`, so every closed set is a
 *     lowerCamel string-literal union rather than a TS `enum`.
 *   - C# `long`/`double`/`int` → `number`, `Guid` → `string`, `T?` → `T | null`.
 *   - A field the schema lists as nullable is `T | null` here, never optional: the API sends the
 *     key with an explicit null, and a component that reads `undefined` would render it.
 */

// ─────────────────────────────────────────────────────────────────────────────
// The mediaRef token
// ─────────────────────────────────────────────────────────────────────────────

/**
 * A media source is addressed by an **opaque token**, not a GUID: `file:<guid>` (a `ProjectFile`)
 * or `step_result:<guid>` (a `WorkflowStepResult` whose render is registered nowhere else).
 *
 * It is a plain `string` on purpose. Giving it a branded type would invite a helper that narrows
 * it back to a GUID, and the whole point of the token is that a GUID cannot tell a project file
 * apart from a step-result render. **Nothing in this feature may parse a GUID out of it, branch on
 * which prefix it carries, or assume `file:`.**
 */
export type MediaRef = string;

/** The two prefixes a mediaRef may carry. Exported for the contract test, never for branching. */
export const MEDIA_REF_PREFIXES = ['file', 'step_result'] as const;

/**
 * Shape check for an untrusted mediaRef (a drag payload, a URL segment).
 *
 * It deliberately accepts BOTH prefixes and never reports which one matched: a caller that can ask
 * "is this a file: token?" is one step away from branching on it, which the contract forbids. The
 * pattern is anchored on the whole token and requires a 36-character GUID body.
 */
export function isMediaRef(value: unknown): value is MediaRef {
  return typeof value === 'string' && /^(?:file|step_result):[0-9a-fA-F-]{36}$/.test(value);
}

// ─────────────────────────────────────────────────────────────────────────────
// Enums — camelCase string unions, exactly as the schema spells them.
// ─────────────────────────────────────────────────────────────────────────────

/** The media kind of a pool item, derived from its MIME type. */
export type MediaKind = 'video' | 'audio' | 'image';

/** The owning project-file category of a pool item. */
export type MediaCategory = 'userFiles' | 'agentFiles' | 'outputFiles';

/**
 * What a derivative row was generated for — the **eight** members the landed
 * `ReelBolt.Shared/Data/Models/MediaDerivative.cs` declares.
 *
 * The set is six and not three because `MediaController.ListDerivatives` filters by SOURCE and
 * **not by kind**, so a response can legitimately carry `waveformPeaks`, `filmstrip` or
 * `keyframeIndex` or `stills` rows (`stills` is on-demand full-size frames the assistant asks for; the editor never
 * shows them). `waveform` is retained by the model as a superseded signpost — nothing ever
 * wrote it, and `waveformPeaks` is what the editor draws a waveform from.
 */
export type MediaDerivativeKind =
  | 'proxy'
  | 'waveform'
  | 'waveformPeaks'
  | 'thumbnail'
  | 'filmstrip'
  | 'keyframeIndex'
  | 'stills'
  | 'stabilizedProxy';

/**
 * The three closed sets, written down exactly once each.
 *
 * `__tests__/schema-drift.test.ts` proves these arrays equal the schema's `enum`s in both
 * directions AND that each is assignable to the union above, so a member added to the schema without
 * being added here (or vice versa) fails the suite rather than drifting quietly.
 *
 * `MEDIA_KINDS` and `MEDIA_CATEGORIES` were confirmed against the landed DTOs; `DERIVATIVE_KINDS`
 * was WIDENED from three to six when the landed enum turned out to have six members.
 */
export const MEDIA_KINDS = ['video', 'audio', 'image'] as const;
export const MEDIA_CATEGORIES = ['userFiles', 'agentFiles', 'outputFiles'] as const;
export const DERIVATIVE_KINDS = [
  'proxy',
  'waveform',
  'waveformPeaks',
  'thumbnail',
  'filmstrip',
  'keyframeIndex',
  'stills',
  'stabilizedProxy',
] as const;

// ─────────────────────────────────────────────────────────────────────────────
// Pool
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Probed media characteristics, projected from the newest `VideoAnalyze` artifact.
 *
 * `fps` is an exact rational pair, never a pre-divided double: 30000/1001 stays 30000/1001 so
 * frame arithmetic does not drift. Every field is nullable on its own — a partially probed source
 * is possible, and a component must render a stated placeholder rather than `NaN`.
 *
 * ## Two levels of absence, and both are stated
 *
 * `probe === null` means **no artifact mentioned this source at all**. A `null` FIELD inside a
 * present probe means **an artifact mentioned the source but never probed this particular field**,
 * which is the normal reading of an artifact persisted before `videoCodec`/`audioCodec`/
 * `container`/`audioChannels`/`audioSampleRateHz`/`bitrateBps` existed. The six appended fields are
 * therefore nullable and are **null, never 0 and never an empty string** — a 0 channel count or a
 * blank codec name would state something ffprobe never said. See `format.ts#NOT_PROBED`.
 *
 * `container` is ffprobe's `format_name` **verbatim**: a sniffed MP4 reports the comma-separated
 * probe list `"mov,mp4,m4a,3gp,3g2,mj2"`. Nothing here normalizes it, and no component may render
 * it as if it were one clean container name — `format.ts#formatContainer` is where that is made
 * readable without pretending.
 */
export interface MediaProbe {
  durationSec: number | null;
  fpsNum: number | null;
  fpsDen: number | null;
  width: number | null;
  height: number | null;
  /** ffprobe's `codec_name` for the first video stream, or null when unprobed. */
  videoCodec: string | null;
  /** ffprobe's `codec_name` for the first audio stream, or null when unprobed. */
  audioCodec: string | null;
  /** ffprobe's `format_name` VERBATIM — a probe list, not necessarily one container. */
  container: string | null;
  /** Channel count of the first audio stream, or null when unprobed. */
  audioChannels: number | null;
  /** Sample rate of the first audio stream in Hz, or null when unprobed. */
  audioSampleRateHz: number | null;
  /** Container bitrate in bits per second, or null when unprobed. */
  bitrateBps: number | null;
  /**
   * Whether the source has an audio stream (C# `MediaProbeResponse.HasAudio`): set when the server
   * knows (the stored ingest probe always does), null or absent when it does not (an analysis
   * artifact that predates codec probing, or a server without the field).
   */
  hasAudio?: boolean | null;
  /** R10.7: ffprobe's `color_transfer` (e.g. `arib-std-b67`); absent or null on a probe stored before it existed. */
  colorTransfer?: string | null;
  /** R10.7: ffprobe's `color_primaries` (e.g. `bt2020`). */
  colorPrimaries?: string | null;
  /** R10.7: `'hlg'` or `'pq'` for HDR footage, null/absent for standard colours (the render tone-maps HDR to SDR before the grade). */
  hdr?: 'hlg' | 'pq' | null;
  /** log-footage-color P2: ffprobe `color_space` (the matrix), `color_range`, `pix_fmt`, bit depth; absent/null when unknown. */
  colorSpace?: string | null;
  colorRange?: string | null;
  pixFmt?: string | null;
  bitDepth?: number | null;
  /** Maker/model tags (sanitised, max 64 chars); absent/null when the file carries none. */
  make?: string | null;
  model?: string | null;
  /** Server-computed camera-profile hint; omitted when nothing was detected. Only `high` is auto-applied by a client. */
  cameraProfileHint?: CameraProfileHint;
}

/** `profile` is a camera-profile id (e.g. `appleLog`); `reason` is a stable machine word, not display text. */
export interface CameraProfileHint {
  profile: string | null;
  confidence: 'high' | 'medium';
  reason: string;
}

/**
 * One bin holding one source — a membership **pin**, not the membership itself.
 *
 * Both ids are needed and neither is derivable from the other: `binId` says which folder the source
 * sits in, and `itemId` is what a removal (`DELETE …/media/bins/{binId}/items/{itemId}`) addresses.
 * The same source legitimately appears in several bins at once, which is why `MediaPoolItem.bins`
 * is a list and why the `count` a bin shows is the number of pins pointing at it.
 */
export interface MediaBinMembership {
  binId: string;
  itemId: string;
}

/**
 * One source in the project media pool.
 *
 * `probe` is required but nullable: `null` means the source was never analysed, which is a normal
 * state (the API never probes on read and never invents a duration, fps or resolution).
 *
 * `bins` and `filmstripUrl` are **always present** — the landed `MediaPoolItemResponse` appends
 * both, and each exists so a follow-up the panel needs is addressable from the payload it already
 * holds: without `bins` a removal could not name the pin it means (an `itemId` is visible nowhere
 * else the pool client reads), and without `filmstripUrl` every client would have to rebuild this
 * feature's path grammar out of a `mediaRef` it is not supposed to interpret.
 *
 * `bins` is `[]` — **empty, never null** — when the source is in no bin. "In no bin" is a normal
 * state a panel renders, not a missing value.
 */
export interface MediaPoolItem {
  mediaRef: MediaRef;
  kind: MediaKind;
  fileName: string;
  mimeType: string;
  sizeBytes: number;
  category: MediaCategory;
  probe: MediaProbe | null;
  bins: MediaBinMembership[];
  filmstripUrl: string;
}

// ─────────────────────────────────────────────────────────────────────────────
// Derivatives
// ─────────────────────────────────────────────────────────────────────────────

/**
 * One derivative of one source.
 *
 * Rows are a **flat list**, not a kind-keyed map, and there is deliberately **no `state` field**:
 * the only thing observable about a derivative is whether a row exists. 'Not generated yet',
 * 'failed' and 'unsupported' are indistinguishable here, so no UI built on this may claim any of
 * them.
 */
export interface MediaDerivativeRow {
  id: string;
  kind: MediaDerivativeKind;
  mimeType: string | null;
  sizeBytes: number;
  durationSec: number | null;
  width: number | null;
  height: number | null;
  contentUrl: string;
  /** Camera profile id of a converted proxy (conversion baked in); null/absent for every other row, including the plain proxy. */
  cameraProfile?: string | null;
}

/**
 * The proxy row a viewer should play. A source can carry more than one proxy row: a portrait source
 * whose proxy was built before proxies targeted the SHORT side keeps its old 540-tall row next to the
 * rebuilt 540-wide one, and the listing is oldest first. The largest picture wins (ties: the later
 * row), so the sharper rebuild is used as soon as it exists.
 */
export function pickProxyRow<T extends Pick<MediaDerivativeRow, 'kind' | 'width' | 'height'> & { cameraProfile?: string | null }>(
  rows: readonly (T | null | undefined)[] | null | undefined,
  cameraProfile?: string | null,
): T | null {
  if (!Array.isArray(rows)) return null;
  const wanted = cameraProfile || null;
  let best: T | null = null;
  let bestArea = -1;
  let converted: T | null = null;
  let convertedArea = -1;
  for (const row of rows) {
    if (!row || row.kind !== 'proxy') continue;
    const area = (row.width ?? 0) * (row.height ?? 0);
    if (row.cameraProfile) {
      // A converted proxy is only ever the answer to a request for exactly that profile.
      if (wanted !== null && row.cameraProfile === wanted && area >= convertedArea) {
        converted = row;
        convertedArea = area;
      }
      continue;
    }
    if (area >= bestArea) {
      best = row;
      bestArea = area;
    }
  }
  // With the converted row ready, play it (the caller leaves its input stage off: `row.cameraProfile === wanted`);
  // otherwise the plain row, and the caller runs the GL input transform.
  return converted ?? best;
}

// ─────────────────────────────────────────────────────────────────────────────
// Bins
// ─────────────────────────────────────────────────────────────────────────────

/**
 * One media bin.
 *
 * `itemCount` is the server's own count of DIRECT members — not the subtree total. Membership is
 * no longer unreadable: `GET …/media/bins/{binId}/items` lists a bin's pins ([`MediaBinItemDetail`])
 * and `MediaPoolItem.bins` carries every pin of every source in the pool. A UI that wants a count
 * for **the sources it can actually show** should derive it from the pool (see
 * `bins.ts#countsByBinId`), because the listing route also returns DANGLING pins whose source is
 * gone and which therefore appear in no pool item at all.
 */
export interface MediaBin {
  id: string;
  name: string;
  parentBinId: string | null;
  sortOrder: number;
  itemCount: number;
  /** P6: the saved filter of a smart bin, or `null`/absent for an ordinary (pinned) bin. */
  smartFilter?: MediaSmartFilter | null;
  /** P6: true for a smart bin — its items are computed from `smartFilter`, never pinned. */
  isSmart?: boolean;
}

// ─────────────────────────────────────────────────────────────────────────────
// P6: smart bins and annotations — mirrors `ReelBolt.Shared.Editing.MediaSmartFilter` and
// `MediaAnnotationResponse`/`UpsertMediaAnnotationRequest`.
// ─────────────────────────────────────────────────────────────────────────────

/** The closed colour-label words the server accepts (`MediaAnnotation.ColorLabels`). */
export const MEDIA_COLOR_LABELS = ['red', 'orange', 'yellow', 'green', 'blue', 'violet', 'grey'] as const;
export type MediaColorLabel = (typeof MEDIA_COLOR_LABELS)[number];

/**
 * A saved metadata filter. Every clause is optional and the clauses AND together; an empty filter
 * matches every pool source. Keywords are matched case-insensitively and ALL listed keywords must
 * be present on the source's annotation.
 */
export interface MediaSmartFilter {
  keywords?: string[] | null;
  kinds?: MediaKind[] | null;
  colorLabel?: MediaColorLabel | null;
  favourite?: boolean | null;
  flag?: boolean | null;
  nameContains?: string | null;
}

/** One source's annotation, keyed by its opaque `mediaRef`. */
export interface MediaAnnotation {
  mediaRef: MediaRef;
  keywords: string[];
  note: string | null;
  colorLabel: MediaColorLabel | null;
  favourite: boolean;
  flag: boolean;
  updatedAt: string;
}

/**
 * Body of `PUT …/media/annotations/{mediaRef}`. An omitted field keeps its stored value;
 * `keywords: []` clears the keywords, `note: ''` clears the note, and `colorLabel: null` clears the
 * label ONLY when the key is sent (the server reads it as a raw JSON element, like a bin's parent).
 */
export interface UpsertMediaAnnotationRequest {
  keywords?: string[];
  note?: string;
  colorLabel?: MediaColorLabel | null;
  favourite?: boolean;
  flag?: boolean;
}

/** A bin plus its resolved place in the tree, as built by `bins.ts#buildBinTree`. */
export interface BinNode extends MediaBin {
  /** Immediate children, in the server's order. */
  children: BinNode[];
  /** 0 for a top-level bin. Used for indentation only. */
  depth: number;
}

/** Body of `POST .../media/bins`. */
export interface CreateBinRequest {
  name: string;
  parentBinId?: string | null;
  /** P6: supply a filter to create a SMART bin; omit it for an ordinary bin. */
  smartFilter?: MediaSmartFilter | null;
}

/**
 * Body of `PATCH .../media/bins/{binId}`. At least one field must be present.
 *
 * **The two no-op-looking shapes are different requests** (the landed `PatchMediaBinRequest` reads
 * `parentBinId` as a raw JSON element precisely to keep them apart): an ABSENT `parentBinId` means
 * "rename, keep the current parent", while an explicit `null` means "move this bin to the root".
 * Never send the key unless you mean to move.
 */
export interface UpdateBinRequest {
  name?: string;
  parentBinId?: string | null;
  /** P6: replace the smart filter; an explicit `null` turns the bin back into an ordinary bin. Never send the key otherwise. */
  smartFilter?: MediaSmartFilter | null;
}

/** Body of `POST .../media/bins/{binId}/items`. */
export interface AddBinItemRequest {
  mediaRef: MediaRef;
}

/** Body of `PATCH .../media/bins/{binId}/items/{itemId}` — the field is `binId`. */
export interface MoveBinItemRequest {
  binId: string;
}

/**
 * One bin item, as returned by the add and move routes (the landed `MediaBinItemResponse`).
 *
 * This is NOT a way to read membership — use [`MediaBinItemDetail`] from
 * `GET …/media/bins/{binId}/items` for that. It exists so the two mutating calls can be typed
 * honestly, and so the `itemId` a removal needs is available the moment a pin is created.
 */
export interface MediaBinItem {
  id: string;
  binId: string;
  mediaRef: MediaRef;
  /**
   * `"ProjectFile"` or `"StepResult"` — the string form of the shared `MediaSourceKind`. A plain
   * string on the wire (the DTO does not run it through the enum converter), so it is NOT modelled
   * as a closed union here: closing it would invent a constraint the wire does not carry.
   */
  sourceKind: string;
  sortOrder: number;
  createdAt: string;
}

/**
 * One bin item as `GET …/media/bins/{binId}/items` returns it — [`MediaBinItem`]'s six fields, in
 * its order and with its meaning, plus the four fields of the source the pin names.
 *
 * The four resolution fields are **all null for a DANGLING pin** — its source row was deleted,
 * belongs to another project, or (for a step result) no longer sits under this project's
 * `outputFiles/` prefix. The row is still returned with its id and its normalized `mediaRef`,
 * because a pin the client cannot see is a pin the client cannot remove. Each field is
 * independently nullable for the same reason: a render records no object size, and a file uploaded
 * without a MIME type has no kind. **Nothing is defaulted, emptied or guessed** in place of a value
 * the server does not have — so a component renders a stated "source unavailable", never a 0-byte
 * file or a blank name.
 *
 * There is deliberately no `probe` here: probe metadata is produced by the pool and joined by
 * `mediaRef`, and a second producer of the same value could disagree with it.
 */
export interface MediaBinItemDetail extends MediaBinItem {
  kind: MediaKind | null;
  fileName: string | null;
  mimeType: string | null;
  sizeBytes: number | null;
}

// ─────────────────────────────────────────────────────────────────────────────
// Filmstrip index — the landed wire shape
// ─────────────────────────────────────────────────────────────────────────────

/**
 * One sprite sheet of one density, as `GET …/media/{mediaRef}/filmstrip` returns it.
 *
 * Field names are read from the landed response DTO
 * (`ReelBolt.Inference.Api/Controllers/Dto/MediaDtos.cs#MediaFilmstripSheetResponse`) and the
 * sidecar the media writer produces, not invented: `firstThumbIndex`, `thumbCount`,
 * `firstTimeSec` and `contentUrl`.
 *
 * `firstThumbIndex`/`thumbCount` are MEASURED decoded frame counts, never `duration / densitySec` —
 * that division is what makes hover-scrub land on the wrong cell, so nothing here recomputes it.
 *
 * `contentUrl` is an **API path** (never a storage key, a bucket or an object URL) and is always
 * present. It addresses the sheet through the derivative row that OWNS the density plus this
 * sheet's own 0-based index in `sheets[]`, because a filmstrip density is ONE `media_derivatives`
 * row while sheets 1..N-1 have no row of their own. Whether a sheet's bytes have been uploaded yet
 * is the sheet route's answer (a per-sheet `404`), never a hole in this index — so a component must
 * treat a broken sheet image as a per-sheet degradation, not as "no filmstrip".
 */
export interface FilmstripSheet {
  /** Index of this sheet's first thumbnail in the whole filmstrip, 0-based. */
  firstThumbIndex: number;
  /** Thumbnails this sheet actually carries; the last sheet is usually partial. */
  thumbCount: number;
  /** Source time of thumbnail `firstThumbIndex`. */
  firstTimeSec: number;
  /** The API content path for this sheet's bytes. Always present; never a storage key. */
  contentUrl: string;
}

/**
 * One filmstrip density: the sheet geometry plus the sheets of that single density.
 *
 * `sheetCount` is the number the SIDECAR recorded, not `sheets.length` — the server passes both
 * through verbatim, and nothing here re-derives either.
 */
export interface FilmstripDensity {
  /** Seconds between two consecutive thumbnails of the flat sequence. */
  densitySec: number;
  thumbWidth: number;
  thumbHeight: number;
  cols: number;
  rows: number;
  /** Sheets the sidecar says this density has. Read, never recomputed from `sheets.length`. */
  sheetCount: number;
  sheets: FilmstripSheet[];
}

/**
 * A source's filmstrip index, keyed by density, `densitySec` ascending.
 *
 * **`densities: []` is a normal answer, not an error**: it means this source has no filmstrip index
 * yet, and the strip says so rather than guessing a density. It is the same discipline as a null
 * probe field.
 */
export interface FilmstripIndex {
  densities: FilmstripDensity[];
}

/** The envelope `GET …/media/{mediaRef}/filmstrip` returns. */
export interface MediaFilmstripResponse {
  filmstrip: FilmstripIndex;
}

/** Body of `POST …/media/derivatives/request` — the explicit "build what this clip needs" call. */
export interface RequestMediaDerivativesRequest {
  mediaRef: MediaRef;
  /**
   * Kinds by NAME (`"proxy"`, `"waveformPeaks"`, …). Omitted means the server's own default, which
   * is **every member of the closed enum** — the consumer, not the producer, decides what is worth
   * producing, because this API has no ffmpeg and cannot know which kinds are cheap for a source.
   */
  kinds?: string[];
  /**
   * Why the request was made. Omitted means `"editor-open"`; the server also defines `"upload"` and
   * `"render-output"`. Never sent as an empty string.
   */
  requestedBy?: string;
  /** With `kinds: ["proxy"]`: build the proxy converted through this camera profile (a variant of the plain proxy). */
  cameraProfile?: string;
}

/** The `202 Accepted` body of a derivative request: what was asked for, and under which reason. */
export interface RequestMediaDerivativesResponse {
  mediaRef: MediaRef;
  kinds: string[];
  requestedBy: string;
}
