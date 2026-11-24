/**
 * The thumbnail editor's in-browser document: what the canvas draws, the history stack edits and the
 * sync layer diffs against the server. It is the wire DTO (`lib/types/image-document.ts`) with every
 * optional field resolved to a value, so no consumer below this file ever has to ask "is `visible`
 * present?".
 *
 * Pure: no React, no DOM.
 */

import {
  parseLayerKind,
  type BlendMode,
  type EditImageDocument,
  type EditImageLayer,
  type EditImageLayerKind,
  type EditImageLayerRole,
  type LayerCrop,
  type LayerProvenance,
  type TextContent,
} from '@/lib/types/image-document';
import { aiMarkOf, type LayerAiMark } from './ai-label';

/** Where a layer sits. `x`/`y` are canvas pixels of its top-left corner (before rotation). */
export interface EditorTransform {
  x: number;
  y: number;
  /** Uniform. */
  scale: number;
  /** Degrees clockwise about the layer's centre. */
  rotation: number;
  flipX: boolean;
  flipY: boolean;
}

export interface EditorLayer {
  id: string;
  kind: EditImageLayerKind;
  role: EditImageLayerRole;
  label: string;
  /** Ascending = further forward. The array order in {@link EditorDocState} is the source of truth. */
  zOrder: number;
  visible: boolean;
  locked: boolean;
  /** 0..1 */
  opacity: number;
  blendMode: BlendMode;
  transform: EditorTransform;
  /** The bitmap's pixel size as the server knows it; null for a text layer. */
  width: number | null;
  height: number | null;
  imageUrl: string;
  matteUrl: string | null;
  /** Vector content; non-null exactly when `kind === 'text'`. */
  content: TextContent | null;
  segmentationPrompt: string | null;
  /**
   * What made a `generated` layer (an AI operation, a designed graphic). Read-only here: never
   * edited or sent back. Optional so a layer built by hand (tests, the history) need not state it.
   */
  provenance?: LayerProvenance | null;
  /**
   * Non-destructive crop of a picture layer (fractions of its bitmap); null/absent = uncropped.
   * `transform` places the kept part's top-left corner, and the layer's drawn size is the kept part.
   */
  crop?: LayerCrop | null;
  /**
   * A picture made with the project's avatars or objects: who it shows and whether an export must carry the
   * "AI-generated" label. Read-only here (from the server's layer content): never edited or sent back.
   */
  aiMark?: LayerAiMark | null;
}

export interface EditorDocState {
  canvasWidth: number;
  canvasHeight: number;
  /** A CSS colour, or null for a transparent canvas. */
  backgroundColor: string | null;
  /** Ascending z-order: index 0 is the back. */
  layers: EditorLayer[];
}

/** What a canvas size falls back to when neither the document nor a source layer states one. */
export const FALLBACK_CANVAS = { width: 1280, height: 720 } as const;

/**
 * The smallest and largest canvas edge the editor will set — the server's own limits for
 * `canvasWidth`/`canvasHeight`. The shapes and sizes the Canvas tab offers are in `canvas-size.ts`.
 */
export const CANVAS_MIN = 16;
export const CANVAS_MAX = 8192;

export function clampCanvasEdge(value: number): number {
  if (!Number.isFinite(value)) return FALLBACK_CANVAS.width;
  return Math.min(CANVAS_MAX, Math.max(CANVAS_MIN, Math.round(value)));
}

/**
 * Fills the optional parts of stored text content (`stroke`, `shadow`, `maxWidth` are omitted on the
 * wire when absent) so the rest of the editor reads `null`, never `undefined`.
 */
export function normalizeTextContent(raw: TextContent): TextContent {
  return {
    ...raw,
    stroke: raw.stroke ?? null,
    shadow: raw.shadow ?? null,
    maxWidth: raw.maxWidth ?? null,
    uppercase: raw.uppercase ?? false,
    letterSpacing: raw.letterSpacing ?? 0,
    lineHeight: raw.lineHeight ?? 1.1,
    align: raw.align ?? 'left',
  };
}

export function layerFromDto(layer: EditImageLayer): EditorLayer {
  const kind = parseLayerKind(layer.kind as string);
  const t = layer.transform;
  return {
    id: layer.id,
    kind,
    role: layer.role,
    label: layer.label,
    zOrder: layer.zOrder,
    visible: layer.visible ?? true,
    locked: layer.locked ?? false,
    opacity: layer.opacity,
    blendMode: layer.blendMode ?? 'normal',
    transform: {
      x: t.x,
      y: t.y,
      scale: t.scale,
      rotation: t.rotation,
      flipX: t.flipX ?? false,
      flipY: t.flipY ?? false,
    },
    width: layer.width,
    height: layer.height,
    imageUrl: layer.imageUrl,
    matteUrl: layer.matteUrl,
    content: kind === 'text' && layer.content ? normalizeTextContent(layer.content as TextContent) : null,
    segmentationPrompt: layer.segmentationPrompt,
    provenance: layer.provenance ?? null,
    crop: kind === 'text' ? null : normalizeCrop(layer.crop),
    aiMark: kind === 'text' ? null : aiMarkOf(layer.content),
  };
}

/** A usable crop window, or null for none/identity/garbage (a stored document must keep loading). */
export function normalizeCrop(crop: LayerCrop | null | undefined): LayerCrop | null {
  if (!crop) return null;
  const { x, y, w, h } = crop;
  if (![x, y, w, h].every(Number.isFinite)) return null;
  const eps = 1e-6;
  if (x < 0 || y < 0 || w < 0.01 || h < 0.01 || x + w > 1 + eps || y + h > 1 + eps) return null;
  if (x === 0 && y === 0 && w === 1 && h === 1) return null;
  return { x, y, w: Math.min(w, 1 - x), h: Math.min(h, 1 - y) };
}

/** The first `source` layer (the picture the document was opened on), if it still exists. */
export function findSourceLayer(layers: readonly EditorLayer[]): EditorLayer | null {
  return layers.find((l) => l.kind === 'source') ?? null;
}

/**
 * The composition size: the document's own when it states one, otherwise the source image's, and
 * only then a 16:9 fallback (a document whose source layer was deleted and which never chose a size).
 */
export function resolveCanvasSize(
  doc: Pick<EditImageDocument, 'canvasWidth' | 'canvasHeight'>,
  layers: readonly EditorLayer[],
): { width: number; height: number } {
  if (doc.canvasWidth && doc.canvasHeight) {
    return { width: doc.canvasWidth, height: doc.canvasHeight };
  }
  const source = findSourceLayer(layers);
  if (source?.width && source.height) return { width: source.width, height: source.height };
  return { width: FALLBACK_CANVAS.width, height: FALLBACK_CANVAS.height };
}

/** Renumbers `zOrder` 1..n by array position, so the array order and the numbers can never disagree. */
export function renumber(layers: readonly EditorLayer[]): EditorLayer[] {
  return layers.map((l, i) => (l.zOrder === i + 1 ? l : { ...l, zOrder: i + 1 }));
}

export function docFromDto(doc: EditImageDocument): EditorDocState {
  const layers = doc.layers
    .map(layerFromDto)
    .map((l, i) => ({ l, i }))
    .sort((a, b) => a.l.zOrder - b.l.zOrder || a.i - b.i)
    .map(({ l }) => l);
  const size = resolveCanvasSize(doc, layers);
  return {
    canvasWidth: size.width,
    canvasHeight: size.height,
    backgroundColor: doc.backgroundColor ?? null,
    layers,
  };
}

export function getLayer(state: EditorDocState, id: string | null): EditorLayer | null {
  if (!id) return null;
  return state.layers.find((l) => l.id === id) ?? null;
}

/** Whether a layer has pixels on the server (a `text` layer does not; its image route is a 404). */
export function hasBitmap(layer: Pick<EditorLayer, 'kind'>): boolean {
  return layer.kind !== 'text';
}

/** Appends layers the server just created (already numbered by the server) and re-sorts by z. */
export function mergeServerLayers(
  state: EditorDocState,
  incoming: readonly EditImageLayer[],
): EditorDocState {
  const known = new Set(state.layers.map((l) => l.id));
  const fresh = incoming.filter((l) => !known.has(l.id)).map(layerFromDto);
  if (fresh.length === 0) return state;
  return { ...state, layers: [...state.layers, ...fresh] };
}

export function removeLayerFromState(state: EditorDocState, id: string): EditorDocState {
  return { ...state, layers: renumber(state.layers.filter((l) => l.id !== id)) };
}
