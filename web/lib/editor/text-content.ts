/**
 * Text layer content: its defaults, the bounds the server enforces, and the fonts the editor offers.
 *
 * The bounds are the contract's (docs/design/thumbnail-editor.md section 4: font size 8–600, stroke
 * width 0–64, text at most 2 000 characters). The server is the authority and answers 400 past
 * them; checking here first means the user is told in plain words before a round trip, and the
 * sliders cannot be dragged into a request that would be refused.
 *
 * Pure: no DOM. Loading a font into the page is `lib/editor/fonts.ts`.
 */

import type { TextAlign, TextContent } from '@/lib/types/image-document';
import type { EditorDefaults } from '@/lib/types/design-system';

export const TEXT_LIMITS = {
  maxLength: 2000,
  fontSize: { min: 8, max: 600 },
  strokeWidth: { min: 0, max: 64 },
  fontWeight: { min: 100, max: 900 },
  lineHeight: { min: 0.5, max: 3 },
  letterSpacing: { min: -50, max: 200 },
  shadowBlur: { min: 0, max: 200 },
  shadowOffset: { min: -200, max: 200 },
  maxWidth: { min: 20, max: 8000 },
  colorLength: 64,
} as const;

export const TEXT_ALIGNS: readonly TextAlign[] = ['left', 'center', 'right'];

/** A font the editor offers: a Google Fonts family and the weights it ships. */
export interface EditorFont {
  family: string;
  weights: readonly number[];
  /** One line saying what it is for, shown in the picker. */
  note: string;
}

/**
 * The curated list. Chakra Petch and JetBrains Mono are the brand's own (docs/site-design-system.md);
 * the rest are the display and text faces thumbnails are usually set in. Every family here is on
 * Google Fonts, which is where {@link googleFontsHref} points.
 */
export const EDITOR_FONTS: readonly EditorFont[] = [
  { family: 'Chakra Petch', weights: [400, 500, 600, 700], note: 'ReelBolt display face' },
  { family: 'JetBrains Mono', weights: [400, 500, 700, 800], note: 'ReelBolt monospace' },
  { family: 'Anton', weights: [400], note: 'Tall, condensed headline' },
  { family: 'Bebas Neue', weights: [400], note: 'Condensed capitals' },
  { family: 'Oswald', weights: [400, 500, 600, 700], note: 'Strong condensed sans' },
  { family: 'Montserrat', weights: [400, 600, 700, 800, 900], note: 'Clean geometric sans' },
  { family: 'Poppins', weights: [400, 500, 600, 700, 800], note: 'Friendly geometric sans' },
  { family: 'Inter', weights: [400, 500, 600, 700, 800], note: 'Neutral interface sans' },
  { family: 'Roboto Condensed', weights: [400, 700], note: 'Compact and legible' },
  { family: 'Archivo Black', weights: [400], note: 'Heavy grotesque' },
  { family: 'Playfair Display', weights: [400, 700, 900], note: 'High-contrast serif' },
  { family: 'Permanent Marker', weights: [400], note: 'Hand-drawn marker' },
  { family: 'Bangers', weights: [400], note: 'Comic-book headline' },
];

export const DEFAULT_FONT = EDITOR_FONTS[0];

export function findFont(family: string): EditorFont | null {
  return EDITOR_FONTS.find((f) => f.family === family) ?? null;
}

/** The weight in `font` closest to `wanted` (so switching family never leaves an unavailable weight). */
export function nearestWeight(font: EditorFont, wanted: number): number {
  return font.weights.reduce((best, w) => (Math.abs(w - wanted) < Math.abs(best - wanted) ? w : best), font.weights[0]);
}

/**
 * The Google Fonts CSS URL for one family at the weights it ships. Only families from
 * {@link EDITOR_FONTS} are ever requested, so the URL is built from a fixed list, never from user
 * text.
 */
export function googleFontsHref(font: EditorFont): string {
  const family = font.family.trim().replace(/ /g, '+');
  const weights = [...font.weights].sort((a, b) => a - b).join(';');
  return `https://fonts.googleapis.com/css2?family=${family}:wght@${weights}&display=swap`;
}

/**
 * The project's design-system defaults this function reads: the heading font and text colour the
 * approved brand asks for. Structurally a subset of `EditorDefaults`, so a full response can be
 * passed straight in.
 */
export type TextLayerDefaults = Pick<EditorDefaults, 'fontFamily' | 'fontWeight' | 'color' | 'strokeColor'>;

/**
 * What {@link defaultTextContent} accepts: the caller's own overrides AND/OR the project's design
 * defaults. A design default is applied only where the caller gave nothing; with no design system
 * the result is byte-identical to the legacy defaults (Chakra Petch 700 96 `#ffffff`).
 */
export interface TextContentInput extends Omit<Partial<TextContent>, 'fontFamily' | 'fontWeight' | 'color'> {
  fontFamily?: string | null;
  fontWeight?: number | null;
  color?: string | null;
  strokeColor?: string | null;
  warnings?: readonly string[];
}

/** The keys `defaultTextContent` resolves itself, so they never ride the overrides spread. */
const DEFAULTS_KEYS = ['fontFamily', 'fontWeight', 'color', 'strokeColor', 'warnings'] as const;

export function defaultTextContent(input: TextContentInput | null = {}): TextContent {
  const given = input ?? {};
  const overrides: Record<string, unknown> = { ...given };
  for (const key of DEFAULTS_KEYS) delete overrides[key];

  return {
    text: 'Your title',
    fontFamily: given.fontFamily ?? DEFAULT_FONT.family,
    fontWeight: given.fontWeight ?? 700,
    fontSize: 96,
    lineHeight: 1.05,
    letterSpacing: 0,
    align: 'left',
    color: given.color ?? '#ffffff',
    stroke: { color: given.strokeColor ?? '#000000', width: 6 },
    shadow: { color: 'rgba(0,0,0,0.6)', blur: 18, offsetX: 0, offsetY: 8 },
    maxWidth: null,
    uppercase: false,
    ...(overrides as unknown as Partial<TextContent>),
  };
}

const finite = (n: unknown): n is number => typeof n === 'number' && Number.isFinite(n);

function outside(n: number, range: { min: number; max: number }): boolean {
  return n < range.min || n > range.max;
}

/**
 * Every reason the server would refuse this content, in plain words. Empty means it will be
 * accepted. The checks are the contract's three bounds plus the shape rules a number input can
 * break (NaN, a non-colour, an unknown alignment).
 */
export function validateTextContent(c: TextContent): string[] {
  const problems: string[] = [];
  if (c.text.trim().length === 0) problems.push('Type some text.');
  if (c.text.length > TEXT_LIMITS.maxLength) {
    problems.push(`Text can be at most ${TEXT_LIMITS.maxLength} characters (it is ${c.text.length}).`);
  }
  if (!c.fontFamily.trim()) problems.push('Choose a font.');
  if (!finite(c.fontSize) || outside(c.fontSize, TEXT_LIMITS.fontSize)) {
    problems.push(`Font size must be between ${TEXT_LIMITS.fontSize.min} and ${TEXT_LIMITS.fontSize.max}.`);
  }
  if (!finite(c.fontWeight) || outside(c.fontWeight, TEXT_LIMITS.fontWeight)) {
    problems.push(`Weight must be between ${TEXT_LIMITS.fontWeight.min} and ${TEXT_LIMITS.fontWeight.max}.`);
  }
  if (!finite(c.lineHeight) || outside(c.lineHeight, TEXT_LIMITS.lineHeight)) {
    problems.push(`Line height must be between ${TEXT_LIMITS.lineHeight.min} and ${TEXT_LIMITS.lineHeight.max}.`);
  }
  if (!finite(c.letterSpacing) || outside(c.letterSpacing, TEXT_LIMITS.letterSpacing)) {
    problems.push(
      `Letter spacing must be between ${TEXT_LIMITS.letterSpacing.min} and ${TEXT_LIMITS.letterSpacing.max}.`,
    );
  }
  if (!TEXT_ALIGNS.includes(c.align)) problems.push('Alignment must be left, centre or right.');
  if (!c.color.trim()) problems.push('Choose a text colour.');
  const colours = [c.color, c.stroke?.color, c.shadow?.color];
  if (colours.some((v) => (v?.length ?? 0) > TEXT_LIMITS.colorLength)) problems.push('A colour value is too long.');
  if (c.stroke) {
    if (!finite(c.stroke.width) || outside(c.stroke.width, TEXT_LIMITS.strokeWidth)) {
      problems.push(
        `Outline width must be between ${TEXT_LIMITS.strokeWidth.min} and ${TEXT_LIMITS.strokeWidth.max}.`,
      );
    }
    if (!c.stroke.color.trim()) problems.push('Choose an outline colour.');
  }
  if (c.shadow) {
    if (!finite(c.shadow.blur) || outside(c.shadow.blur, TEXT_LIMITS.shadowBlur)) {
      problems.push(`Shadow blur must be between ${TEXT_LIMITS.shadowBlur.min} and ${TEXT_LIMITS.shadowBlur.max}.`);
    }
    if (
      !finite(c.shadow.offsetX) ||
      !finite(c.shadow.offsetY) ||
      outside(c.shadow.offsetX, TEXT_LIMITS.shadowOffset) ||
      outside(c.shadow.offsetY, TEXT_LIMITS.shadowOffset)
    ) {
      problems.push(
        `Shadow offset must be between ${TEXT_LIMITS.shadowOffset.min} and ${TEXT_LIMITS.shadowOffset.max}.`,
      );
    }
    if (!c.shadow.color.trim()) problems.push('Choose a shadow colour.');
  }
  if (c.maxWidth !== null && (!finite(c.maxWidth) || outside(c.maxWidth, TEXT_LIMITS.maxWidth))) {
    problems.push(`Wrap width must be between ${TEXT_LIMITS.maxWidth.min} and ${TEXT_LIMITS.maxWidth.max}.`);
  }
  return problems;
}

/** Pulls every numeric field into its bound, for a control that must always produce valid content. */
export function clampTextContent(c: TextContent): TextContent {
  const clamp = (n: number, r: { min: number; max: number }) =>
    finite(n) ? Math.min(r.max, Math.max(r.min, n)) : r.min;
  return {
    ...c,
    text: c.text.slice(0, TEXT_LIMITS.maxLength),
    fontSize: clamp(c.fontSize, TEXT_LIMITS.fontSize),
    fontWeight: clamp(c.fontWeight, TEXT_LIMITS.fontWeight),
    lineHeight: clamp(c.lineHeight, TEXT_LIMITS.lineHeight),
    letterSpacing: clamp(c.letterSpacing, TEXT_LIMITS.letterSpacing),
    stroke: c.stroke ? { ...c.stroke, width: clamp(c.stroke.width, TEXT_LIMITS.strokeWidth) } : null,
    shadow: c.shadow
      ? {
          ...c.shadow,
          blur: clamp(c.shadow.blur, TEXT_LIMITS.shadowBlur),
          offsetX: clamp(c.shadow.offsetX, TEXT_LIMITS.shadowOffset),
          offsetY: clamp(c.shadow.offsetY, TEXT_LIMITS.shadowOffset),
        }
      : null,
    maxWidth: c.maxWidth === null ? null : clamp(c.maxWidth, TEXT_LIMITS.maxWidth),
  };
}

/**
 * The wire form of text content: absent `stroke`/`shadow`/`maxWidth` are omitted rather than sent
 * as `null`, so the stored JSON matches the contract's example exactly.
 */
export function toWireContent(c: TextContent): Record<string, unknown> {
  const { stroke, shadow, maxWidth, ...rest } = c;
  return {
    ...rest,
    ...(stroke && stroke.width > 0 ? { stroke } : {}),
    ...(shadow ? { shadow } : {}),
    ...(maxWidth !== null ? { maxWidth } : {}),
  };
}
