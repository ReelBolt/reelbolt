/**
 * Mirrors `DesignSystemDtos.cs` (Inference API) — SPEC §9/§22, P2-10/11/16.
 * Every `Guid` travels as a `string`, every enum as its NAME string (so appending a member never
 * changes the wire shape) and every `DateTime` as an ISO `string`. The server remains the authority;
 * these types only describe what the read-only tab renders.
 */

export type DesignSystemKind = 'design' | 'product_map';

/**
 * SPEC §16.3 `GET /design-system/editor-defaults`: the approved design member's text-layer
 * defaults, already mapped onto the editor's own thirteen fonts. A null `fontFamily` means the
 * brand's face is not one of them (the editor keeps its own default); a null `color` means the
 * member carries no `color.text`. `warnings` carries `font_substituted:requested->used` codes.
 */
export interface EditorDefaults {
  fontFamily: string | null;
  fontWeight: number;
  color: string | null;
  strokeColor: string;
  warnings: string[];
}

export type DesignVersionState = 'Draft' | 'Approved' | 'Superseded' | 'Rejected';

/** SPEC §9: `State` is the workflow position; `Origin` is how the version was produced. */
export type DesignVersionOrigin =
  | 'Import'
  | 'StaticExtract'
  | 'LlmExtract'
  | 'Manual'
  | 'Merge'
  | 'Restore';

export type DesignSourceStatus = 'Current' | 'Changed' | 'Missing';

/** The token types the design document defines (SPEC §4). */
export type DesignTokenType =
  | 'color'
  | 'dimension'
  | 'fontFamily'
  | 'fontWeight'
  | 'duration'
  | 'cubicBezier'
  | 'number'
  | 'shadow'
  | 'typography';

export type ConfidenceBand = 'high' | 'medium' | 'low';

/** `DesignSystemCurrentVersionResponse` — the approved version a system points at, no document. */
export interface DesignSystemCurrentVersion {
  id: string;
  versionNumber: number;
  digest: string;
  approvedAt: string | null;
}

/** `DesignSystemParentLibraryResponse` — Phase 4; always null in Phase 2. */
export interface DesignSystemParentLibrary {
  id: string;
  name: string;
}

/** `DesignVersionSummaryResponse` — a version without its document, for lists and the draft slot. */
export interface DesignVersionSummary {
  id: string;
  designSystemId: string;
  versionNumber: number;
  state: DesignVersionState;
  origin: DesignVersionOrigin;
  documentDigest: string;
  baseVersionId: string | null;
  workflowExecutionId: string | null;
  createdByUserId: string;
  approvedByUserId: string | null;
  approvedAt: string | null;
  createdAt: string;
}

/** `DesignSystemSummaryResponse` — one system summary row. */
export interface DesignSystemSummary {
  id: string;
  kind: DesignSystemKind;
  name: string;
  currentVersion: DesignSystemCurrentVersion | null;
  latestDraft: DesignVersionSummary | null;
  pendingProposals: number;
  parentLibrary: DesignSystemParentLibrary | null;
}

/**
 * SPEC §16.1/P4 `LibrarySummaryResponse` — one organization brand library plus its document
 * container's summary. The library is the addressable, tenant-filtered identity; the container is a
 * `design_systems` row with no project.
 */
export interface DesignLibrarySummary {
  id: string;
  organizationId: string;
  name: string;
  designSystemId: string;
  designSystem: DesignSystemSummary;
}

/** `DesignSourceResponse` — one source a version was read from. */
export interface DesignSource {
  id: string;
  sourceKind: string;
  projectFileId: string | null;
  path: string;
  importer: string;
  contentSha256: string;
  sourceRevision: string | null;
  sizeBytes: number;
  status: DesignSourceStatus;
  lastCheckedAt: string;
  lastChangedAt: string | null;
}

/** `DesignVersionResponse` — the full version: the summary fields plus document, coverage, sources. */
export interface DesignVersion extends DesignVersionSummary {
  document: DesignDocument;
  coverage: DesignDocument | null;
  sources: DesignSource[];
}

/** `DesignSystemsSummaryResponse` — both kinds' summaries. */
export interface DesignSystemsSummary {
  design: DesignSystemSummary | null;
  productMap: DesignSystemSummary | null;
}

/**
 * A design document is a loose tree of groups and tokens. A TOKEN is an object carrying `$value`;
 * a GROUP is an object without it. The web keeps the raw shape and reads it through the helpers
 * below rather than re-modelling the JSON Schema (SPEC §4).
 */
export type DesignDocument = Record<string, unknown>;

/** The roles the design document stores a brand asset under; `preview` is the archive thumbnail. */
export type DesignAssetRole = 'logo' | 'icon' | 'preview';

/** One alternative file of a brand asset (a light or dark mark, for example). */
export interface DesignAssetVariant {
  name: string;
  /** `project-file:<guid>` naming a stored file in this project. */
  fileRef: string;
}

/** One `assets.<role>` object: the base file, its stored base name and its alternatives. */
export interface DesignAsset {
  /** `project-file:<guid>` naming the base file in this project. */
  fileRef: string;
  /** The stored file's base name, shown as text beside the mark. */
  name?: string;
  variants?: DesignAssetVariant[];
}

/** The design document's `assets` block (SPEC §4.2). */
export type DesignAssets = Partial<Record<DesignAssetRole, DesignAsset>>;

/** The design document's `fonts` block; `files` maps a family to a stored `project-file:<guid>`. */
export interface DesignFonts {
  licence?: Record<string, string>;
  resolved?: Record<string, string>;
  files?: Record<string, string>;
}

/** One token and the `com.reelbolt` metadata the document may carry beside its value. */
export interface DesignToken {
  $type?: DesignTokenType;
  $value: unknown;
  $description?: string;
  $extensions?: {
    'com.reelbolt'?: {
      prov?: DesignTokenProvenance;
      conf?: number;
      locked?: boolean;
      orig?: string | null;
    };
  };
}

/** Where a token came from (SPEC §4/§11.3). `sourceId` is a design-source id or null. */
export interface DesignTokenProvenance {
  method: 'static' | 'imported' | 'llm' | 'manual';
  importer: string | null;
  sourceId: string | null;
  loc: string | null;
}

/** The product-map document (SPEC §4.3), typed for the few fields the overview reads. */
export interface ProductMapFramework {
  name: string;
  version?: string;
  language?: string;
  styling?: string[];
  packageManager?: string;
  projectType?: string;
}

export interface ProductMapDirectory {
  path: string;
  purpose: string;
}

export interface ProductMapEntryPoint {
  path: string;
  role: string;
}

export interface ProductMapRoute {
  path: string;
  file: string;
  kind: string;
  method?: string;
  title?: string;
  requiresAuth: boolean;
  source: string;
}

export interface ProductMapComponent {
  name: string;
  path: string;
  responsibility: string;
  keyProps: string[];
  core: boolean;
  source: string;
}

export interface ProductMapDependency {
  name: string;
  version: string;
}

export interface ProductMapDependencies {
  runtime: ProductMapDependency[];
  dev: ProductMapDependency[];
  ui: string[];
}

export interface ProductMapProvenance {
  method: string;
  sourceIds: string[];
}

/**
 * One preview card the product map captured from the imported markup (SPEC §4.3). It is DATA, not
 * a page: `text` is the plain text the importer extracted and must be rendered as text, never as
 * markup. `assets` holds the relative image basenames the card referenced.
 */
export interface ProductMapPreviewCard {
  name: string;
  group?: string;
  subtitle?: string;
  viewport?: string;
  path?: string;
  /** Plain text extracted from the card body; a `<script>` in the source never reaches it. */
  text?: string;
  /** Relative image basenames the card referenced (never absolute URLs). */
  assets: string[];
  /** The card needs JavaScript to render, so only its extracted text is trustworthy. */
  runtimeRendered: boolean;
}

export interface ProductMapDocument {
  schema?: string;
  name: string;
  framework: ProductMapFramework;
  architecture?: string;
  routingStrategy?: string;
  directories?: ProductMapDirectory[];
  entryPoints?: ProductMapEntryPoint[];
  routes?: ProductMapRoute[];
  components?: ProductMapComponent[];
  dependencies?: ProductMapDependencies;
  patterns?: string[];
  provenance?: Record<string, ProductMapProvenance>;
  /** The preview cards the importer read; absent when the archive had none. */
  previewCards?: ProductMapPreviewCard[];
}

/**
 * SPEC §9/§13.4: a change proposal against one design system. `diff` is the stored `diff_json`
 * (§13.3) and carries `driftScore`; the arbitrary two-version diff route omits it.
 */
export type DesignProposalStatus = 'Pending' | 'Approved' | 'Rejected' | 'Superseded' | 'Expired';

export type DesignProposalTrigger = 'SourceChanged' | 'Manual' | 'Scheduled' | 'Import';

/** Where a changed token came from, as the stored `prov` block records it. */
export interface DesignDiffProv {
  method: 'imported' | 'static' | 'llm' | 'manual';
  importer: string | null;
  sourceId: string | null;
  loc: string | null;
}

/** One token path a diff gained, lost or changed. */
export interface DesignDiffChange {
  path: string;
  before?: unknown;
  after?: unknown;
  locked?: boolean;
  conflict?: boolean;
  prov?: DesignDiffProv;
}

/** SPEC §13.3 `diff_json`, with or without `driftScore`; product listings only on a product diff. */
export interface DesignDiff {
  counts: { added: number; removed: number; changed: number };
  driftScore?: number;
  added: DesignDiffChange[];
  removed: DesignDiffChange[];
  changed: DesignDiffChange[];
  sections?: { voice: string; assets: string; fonts: string; modes: string };
  product?: {
    routes: { added: string[]; removed: string[] };
    components: { added: string[]; removed: string[] };
    dependencies: { added: string[]; removed: string[]; changed: { name: string; before?: string; after?: string }[] };
  };
}

/** `ProposalResponse` — one change proposal with its stored diff. */
export interface DesignProposal {
  id: string;
  designSystemId: string;
  baseVersionId: string;
  proposedVersionId: string;
  trigger: DesignProposalTrigger;
  driftScore: number;
  diff: DesignDiff;
  status: DesignProposalStatus;
  proposedDigest: string;
  decidedByUserId: string | null;
  decidedAt: string | null;
  decisionNote: string | null;
  createdAt: string;
}

/** SPEC §9 `PATCH /versions/{id}/locks` body. */
export interface LockRequest {
  lock: string[];
  unlock: string[];
}

// --- Build, import and the execution pin (P2-22; SPEC §3.5/§9/§17) ----------------------------

/**
 * One kind's pin on a workflow execution: the exact approved version the run was started from.
 * `libraryVersionId` is reserved for the Phase 4 shared library and is always null in Phase 2.
 */
export interface DesignSystemPinMember {
  versionId: string;
  versionNumber: number;
  digest: string;
  libraryVersionId: string | null;
}

/** `workflow_executions.design_system_pin_json` — either member may be absent; the whole field is null. */
export interface DesignSystemPin {
  v: number;
  design?: DesignSystemPinMember;
  productMap?: DesignSystemPinMember;
}

/** The estimate a build returns before it starts, in credits. */
export interface BuildEstimate {
  low: number;
  high: number;
}

/** `POST …/design-system/builds` request. `templateKey` defaults to `product-profile-from-code`. */
export interface BuildRequest {
  templateKey?: 'product-profile-from-code';
  acceptEstimateCredits?: number;
}

/** `POST …/design-system/builds` `202` body. */
export interface BuildResponse {
  executionId: string;
  workflowId: string;
  estimate: BuildEstimate;
}

/** `IngestResponse` — `POST …/ingest`: the Draft versions created (or already present). */
export interface IngestResponse {
  versionIds: string[];
  versions: DesignVersionSummary[];
}

// --- Extended import and diagnostics (P3-14; SPEC §9/§17) --------------------------------------

/** How serious one importer diagnostic is; the API sends the enum NAME string (append-only). */
export type ImportDiagnosticSeverity = 'Info' | 'Warn' | 'Error';

/** `ImportDiagnosticDto` — one importer note: how serious, its code, the token path and a sentence. */
export interface ImportDiagnostic {
  severity: ImportDiagnosticSeverity;
  code: string;
  path: string;
  message: string;
}

/** `ImportResponse` — `POST …/import`: the Draft versions created and everything noticed. */
export interface ImportResponse {
  versions: DesignVersionSummary[];
  diagnostics: ImportDiagnostic[];
  /** What each axis of the profile carried and dropped; absent on a worker from before T-009. */
  report?: ImportReport | null;
}

/**
 * `FigmaImportRequest` — `POST …/import/figma`: the Figma file key and a one-shot Personal Access
 * Token. The token serves that single call and is never persisted, logged or echoed.
 */
export interface FigmaImportRequest {
  fileKey: string;
  accessToken: string;
}

/** `FigmaImportResponse` (`201`) — the one Draft version created plus every diagnostic. */
export interface FigmaImportResponse {
  version: DesignVersionSummary;
  diagnostics: ImportDiagnostic[];
  report?: ImportReport | null;
}

/** `PdfImportResponse` (`201`) — the one Draft version read from a Brand PDF plus every diagnostic. */
export interface PdfImportResponse {
  version: DesignVersionSummary;
  diagnostics: ImportDiagnostic[];
  report?: ImportReport | null;
}

/**
 * `ImportSourceRef` — one archive entry the bundle importer read: its in-archive path, the importer
 * that understood it, the entry's SHA-256, and the project-file id (always null for a zip).
 */
export interface ImportSourceRef {
  path: string;
  importer: string;
  sha256: string;
  projectFileId: string | null;
}

/**
 * `ImportedReferenceSummary` — one reference document the zip route stored as a project file: the
 * stable name the reader gave it, the archive path it came from and the stored file's id.
 */
export interface ImportReferenceSummary {
  name: string;
  path: string;
  projectFileId: string;
}

/**
 * `ZipImportResponse` (`201`) — `POST …/import-zip`: the one Draft version read from a
 * `.zip`/`.sketch`/`.penpot` export, every diagnostic, the entries the bundle read and the vendor
 * label it recognized from the archive's layout (null when none).
 */
export interface ZipImportResponse {
  version: DesignVersionSummary;
  diagnostics: ImportDiagnostic[];
  sources: ImportSourceRef[];
  vendor: string | null;
  /** Files left out of the read, grouped by worker reason code (also on the 400 rejection body). */
  skipped?: SkippedFilesGroup[];
  /** Binary assets the route stored as project files (Claude Design exports); absent when none. */
  assets?: { role: string; variant: string | null; name: string; projectFileId: string }[];
  /** Reference documents the route stored as project files; absent when none. */
  references?: ImportReferenceSummary[];
  /** The product-map Draft created from a component inventory; null/absent when the archive had none. */
  productMapVersion?: DesignVersionSummary | null;
  report?: ImportReport | null;
}

/**
 * `ImportDropGroupResponse` — one reason code on an axis: how serious, how many entries it removed
 * and one sanitized sentence. `code` stays internal vocabulary; the panel adds plain language.
 */
export interface ImportDropGroup {
  code: string;
  severity: ImportDiagnosticSeverity;
  count: number;
  message: string;
}

/**
 * `ImportAxisReportResponse` — one row of the import report: how much of the axis came in, how much
 * was dropped, and the grouped reason codes. `axis` is the enum NAME string (e.g. `Assets`).
 */
export interface ImportAxisReport {
  axis: string;
  support: ImportCapabilitySupport;
  imported: number;
  dropped: number;
  reasons: ImportDropGroup[];
}

/**
 * `ImportReportResponse` — the axis-tagged answer to "what did this import give me, and what did it
 * drop?". `importerId` is the stable importer id; `vendor` is the label read from the archive.
 */
export interface ImportReport {
  importerId: string;
  vendor: string | null;
  axes: ImportAxisReport[];
}

/** One reason files were skipped: the worker code, how many, and up to 3 printable-ASCII names. */
export interface SkippedFilesGroup {
  reason: string;
  count: number;
  examples: string[];
}

// --- Importer capability matrix (SPEC §17; GET /design-system/importers) -----------------------

/** How much of one axis an importer reads end to end: all of it, part of it, or none. */
export type ImportCapabilitySupport = 'Full' | 'Partial' | 'None';

/** One importer × one axis: the support level plus a one-sentence explanation. */
export interface ImportCapabilityAxis {
  axis: string;
  support: ImportCapabilitySupport;
  note: string;
}

/** One importer's row: its stable id, the label a customer sees, and one entry per axis. */
export interface ImportCapabilityImporter {
  id: string;
  label: string;
  axes: ImportCapabilityAxis[];
}

/** `GET /api/v1/design-system/importers` — the static capability table, with no project context. */
export interface ImportCapabilitiesResponse {
  schema: string;
  importers: ImportCapabilityImporter[];
}

/** An object is a token when it carries a `$value`; otherwise it is a group. */
export function isToken(value: unknown): value is DesignToken {
  return typeof value === 'object' && value !== null && '$value' in (value as Record<string, unknown>);
}

/** A group is a (non-array, non-token) object; an empty object is a group with no leaves. */
export function isGroup(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) && !isToken(value);
}

/** Walk a dot path through plain nested objects. Returns `undefined` when any segment is missing. */
export function readPath(doc: DesignDocument, dotPath: string): unknown {
  let current: unknown = doc;
  for (const segment of dotPath.split('.').filter(Boolean)) {
    if (typeof current !== 'object' || current === null) return undefined;
    current = (current as Record<string, unknown>)[segment];
  }
  return current;
}

/** The token at a dot path, or null when the path names a group, a plain value or nothing. */
export function getToken(doc: DesignDocument, dotPath: string): DesignToken | null {
  const value = readPath(doc, dotPath);
  return isToken(value) ? value : null;
}

/** The raw `$value` of the token at a dot path. */
export function getTokenValue(doc: DesignDocument, dotPath: string): unknown {
  return getToken(doc, dotPath)?.$value ?? null;
}

/** For a `color` token, its lowercase `$value.hex`; null for any other token or shape. */
export function readColorHex(doc: DesignDocument, dotPath: string): string | null {
  const value = getTokenValue(doc, dotPath);
  if (typeof value !== 'object' || value === null) return null;
  const hex = (value as Record<string, unknown>).hex;
  return typeof hex === 'string' ? hex : null;
}

/** For a `fontFamily` token, the first family name in its `$value` array. */
export function readFontFamily(doc: DesignDocument, dotPath: string): string | null {
  const value = getTokenValue(doc, dotPath);
  if (Array.isArray(value)) {
    const first = value.find((entry) => typeof entry === 'string');
    return typeof first === 'string' ? first : null;
  }
  return typeof value === 'string' ? value : null;
}

/** For a `number`/`fontWeight` token, its numeric `$value`. */
export function readNumber(doc: DesignDocument, dotPath: string): number | null {
  const value = getTokenValue(doc, dotPath);
  return typeof value === 'number' ? value : null;
}

/** A plain (non-token) string at a dot path — the voice summary, for example. */
export function readText(doc: DesignDocument, dotPath: string): string | null {
  const value = readPath(doc, dotPath);
  return typeof value === 'string' ? value : null;
}

/** A plain (non-token) array of strings at a dot path — the voice tone list, for example. */
export function readStringList(doc: DesignDocument, dotPath: string): string[] {
  const value = readPath(doc, dotPath);
  if (!Array.isArray(value)) return [];
  return value.filter((entry): entry is string => typeof entry === 'string');
}

/** The `com.reelbolt` extension block of the token at a dot path, when present. */
export function reelboltExtension(
  doc: DesignDocument,
  dotPath: string,
): NonNullable<NonNullable<DesignToken['$extensions']>['com.reelbolt']> | null {
  return getToken(doc, dotPath)?.$extensions?.['com.reelbolt'] ?? null;
}

/** The recorded confidence of the token at a dot path, or null when it carries none. */
export function tokenConf(doc: DesignDocument, dotPath: string): number | null {
  const conf = reelboltExtension(doc, dotPath)?.conf;
  return typeof conf === 'number' ? conf : null;
}

/** The recorded provenance of the token at a dot path, or null when it carries none. */
export function tokenProv(doc: DesignDocument, dotPath: string): DesignTokenProvenance | null {
  return reelboltExtension(doc, dotPath)?.prov ?? null;
}

/** SPEC §11.3: `>= 0.85` high, `>= 0.6` medium, else low; no band when `conf` is absent. */
export function confidenceBand(conf: number | null | undefined): ConfidenceBand | null {
  if (typeof conf !== 'number') return null;
  if (conf >= 0.85) return 'high';
  if (conf >= 0.6) return 'medium';
  return 'low';
}

/** Every token leaf in the document, with its full dot path — groups are not returned. */
export function listTokens(doc: DesignDocument): { path: string; token: DesignToken }[] {
  const tokens: { path: string; token: DesignToken }[] = [];
  const walk = (node: unknown, path: string) => {
    if (typeof node !== 'object' || node === null || Array.isArray(node)) return;
    if (isToken(node)) {
      if (path) tokens.push({ path, token: node });
      return;
    }
    for (const [key, child] of Object.entries(node)) {
      if (key.startsWith('$')) continue;
      walk(child, path ? `${path}.${key}` : key);
    }
  };
  walk(doc, '');
  return tokens;
}

/** The product-map document, when the given design document really is one. */
export function readProductMap(document: DesignDocument | null | undefined): ProductMapDocument | null {
  if (!document || typeof document !== 'object') return null;
  if (document.schema !== 'reelbolt.product-map/1') return null;
  return document as unknown as ProductMapDocument;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

const DESIGN_ASSET_ROLES: readonly DesignAssetRole[] = ['logo', 'icon', 'preview'];

/** The design document's `assets` block, narrowed to the roles the overview renders. */
export function readDesignAssets(document: DesignDocument | null | undefined): DesignAssets {
  const raw = document && isPlainObject(document) ? document.assets : undefined;
  if (!isPlainObject(raw)) return {};

  const assets: DesignAssets = {};
  for (const role of DESIGN_ASSET_ROLES) {
    const entry = raw[role];
    if (!isPlainObject(entry) || typeof entry.fileRef !== 'string') continue;

    const variants: DesignAssetVariant[] = Array.isArray(entry.variants)
      ? entry.variants.filter(isPlainObject).flatMap((variant) =>
          typeof variant.name === 'string' && typeof variant.fileRef === 'string'
            ? [{ name: variant.name, fileRef: variant.fileRef }]
            : [],
        )
      : [];

    assets[role] = {
      fileRef: entry.fileRef,
      ...(typeof entry.name === 'string' ? { name: entry.name } : {}),
      ...(variants.length > 0 ? { variants } : {}),
    };
  }
  return assets;
}

/** The `fonts.files` map (family → stored file reference); empty when absent. */
export function readFontFiles(document: DesignDocument | null | undefined): Record<string, string> {
  const raw = document && isPlainObject(document) ? document.fonts : undefined;
  if (!isPlainObject(raw) || !isPlainObject(raw.files)) return {};

  const files: Record<string, string> = {};
  for (const [family, value] of Object.entries(raw.files)) {
    if (typeof value === 'string') files[family] = value;
  }
  return files;
}
