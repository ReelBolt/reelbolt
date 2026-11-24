/**
 * The "AI-generated" label of a picture made with the project's avatars (docs/design/avatar-objects.md "Avatars
 * and objects in the picture editor"): what a layer's content says about it, whether an export must carry the
 * label, and where the label goes. Pure: no React, no DOM. The drawing itself is `drawAiLabel` in `canvas-dom.ts`.
 */

/** Who a picture layer shows and how it must be marked, read from the layer's content. */
export interface LayerAiMark {
  /** The people and objects it was made with, as the server recorded them. */
  subjects: { avatarId: string | null; name: string; kind: 'Person' | 'Object' }[];
  aiGenerated: boolean;
  /** `Visible`: an exported picture that shows this layer carries the label. */
  disclosure: 'Visible' | 'MetadataOnly';
}

const isRecord = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value);

/**
 * What a picture layer's content records about avatars, or null when it records nothing (every layer of an older
 * server, every layer made without them, and every text layer). Tolerant on purpose: an unknown disclosure word
 * is read as `MetadataOnly` only when the content does not say `Visible`, and a subject without a name is skipped.
 */
export function aiMarkOf(content: unknown): LayerAiMark | null {
  if (!isRecord(content)) return null;
  // Text content is a different shape altogether.
  if (typeof content.text === 'string' && typeof content.fontFamily === 'string') return null;
  const subjects: LayerAiMark['subjects'] = [];
  if (Array.isArray(content.subjects)) {
    for (const raw of content.subjects) {
      if (!isRecord(raw) || typeof raw.name !== 'string' || !raw.name.trim()) continue;
      subjects.push({
        avatarId: typeof raw.avatarId === 'string' && raw.avatarId ? raw.avatarId : null,
        name: raw.name.trim(),
        kind: typeof raw.kind === 'string' && raw.kind.toLowerCase() === 'object' ? 'Object' : 'Person',
      });
    }
  }
  const aiGenerated = content.aiGenerated === true;
  const visible = typeof content.disclosure === 'string' && content.disclosure.toLowerCase() === 'visible';
  if (subjects.length === 0 && !aiGenerated && !visible) return null;
  return { subjects, aiGenerated: aiGenerated || subjects.length > 0, disclosure: visible ? 'Visible' : 'MetadataOnly' };
}

/** The names a layer shows, in the order recorded. */
export function aiMarkNames(mark: LayerAiMark | null | undefined): string[] {
  return (mark?.subjects ?? []).map((s) => s.name);
}

/**
 * True when the exported picture must carry the "AI-generated" label: a layer that is VISIBLE asks for it
 * (`disclosure: 'Visible'`). A hidden layer is not in the flat picture, so it asks for nothing.
 */
export function needsAiLabel(layers: readonly { visible: boolean; aiMark?: LayerAiMark | null }[]): boolean {
  return layers.some((layer) => layer.visible && layer.aiMark?.disclosure === 'Visible');
}

/** The label's type size and spacing, in pixels of the exported picture. */
export interface AiLabelStyle {
  fontSize: number;
  paddingX: number;
  paddingY: number;
  /** Distance from the picture's right and bottom edges. */
  margin: number;
  radius: number;
}

/** The smallest type the label is drawn in, unless the picture itself is too small to hold it. */
export const AI_LABEL_MIN_FONT_PX = 12;
/** The type size at the reference width. */
const REFERENCE_FONT_PX = 14;
const REFERENCE_WIDTH_PX = 1280;

/**
 * The label's size for a picture: in proportion to its width (14 px on a 1280 px wide picture), never under 12 px.
 * A very wide strip is measured by twice its height instead, so a banner does not get giant type.
 */
export function aiLabelStyle(canvasWidth: number, canvasHeight: number): AiLabelStyle {
  const basis = Math.max(1, Math.min(canvasWidth, canvasHeight * 2));
  const fontSize = Math.max(AI_LABEL_MIN_FONT_PX, Math.round((basis * REFERENCE_FONT_PX) / REFERENCE_WIDTH_PX));
  return {
    fontSize,
    paddingX: Math.round(fontSize * 0.6),
    paddingY: Math.round(fontSize * 0.35),
    margin: Math.round(fontSize * 0.9),
    radius: Math.round(fontSize * 0.4),
  };
}

/** Where the label's box and text go. `scale` is under 1 only when the picture is too small for the label at full size. */
export interface AiLabelLayout {
  /** The dark box: top-left corner and size. */
  x: number;
  y: number;
  width: number;
  height: number;
  radius: number;
  fontSize: number;
  /** The text's left edge and its vertical middle. */
  textX: number;
  textY: number;
  scale: number;
}

/**
 * The label in the bottom-right corner of a `canvasWidth` x `canvasHeight` picture, for a text that measures
 * `textWidthAtStyle` pixels at `style.fontSize`. The box always lies inside the picture: on one too small to hold
 * it at full size, everything is scaled down together.
 */
export function aiLabelLayout(canvasWidth: number, canvasHeight: number, textWidthAtStyle: number, style: AiLabelStyle = aiLabelStyle(canvasWidth, canvasHeight)): AiLabelLayout {
  const fullWidth = textWidthAtStyle + style.paddingX * 2;
  const fullHeight = style.fontSize + style.paddingY * 2;
  const room = Math.min((canvasWidth - style.margin * 2) / fullWidth, (canvasHeight - style.margin * 2) / fullHeight);
  // No room even for the margins: drop them and fit the box to the picture itself.
  const bare = Math.min(canvasWidth / fullWidth, canvasHeight / fullHeight);
  const scale = room >= 1 ? 1 : room > 0 ? room : Math.min(1, bare);
  const margin = room > 0 ? style.margin * Math.min(1, scale) : 0;
  const width = fullWidth * scale;
  const height = fullHeight * scale;
  const x = Math.max(0, canvasWidth - margin - width);
  const y = Math.max(0, canvasHeight - margin - height);
  return {
    x,
    y,
    width,
    height,
    radius: style.radius * scale,
    fontSize: style.fontSize * scale,
    textX: x + style.paddingX * scale,
    textY: y + height / 2,
    scale,
  };
}
