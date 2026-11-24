/**
 * The title templates' layout in the preview — the browser's mirror of the render's
 * `GradeFilterBuilder.TitleTemplate` (drawtext position, box and border). Pure: it returns where the text goes
 * and what is drawn behind it, in canvas pixels; `overlay-raster.ts` paints it.
 *
 * Mirrors, for a canvas of height H and a text of height t (drawtext's `text_h`):
 *   lowerThird  x = W/12, y = H - t - H/8, box black@0.55, boxborderw = max(8, H/54)
 *   boxed       centred,                   box black@0.6,  boxborderw = max(8, H/54)
 *   outline     centred, 4 px black border (at 1080 lines)
 *   card        centred,                   box black@0.8,  boxborderw = 3 x max(8, H/54)
 *   plain/null  centred, 2 px black@0.6 border
 * The font metrics stay the browser's (the item remains `approximate` in `parity.ts`), but the position,
 * the box and the border are the template's.
 */

export type TitleTemplateName = 'plain' | 'lowerThird' | 'boxed' | 'outline' | 'card';

export interface TitleTemplateLayout {
  /** Left-aligned at `x` (lower third) or centred on `x`. */
  align: 'left' | 'center' | 'right';
  /** Anchor x of the text, canvas pixels. */
  x: number;
  /** Top of the text box, canvas pixels (the caller draws with a "top" baseline). */
  y: number;
  /** A filled rectangle behind the text, padded by `pad` on every side. */
  box: { color: string; alpha: number; pad: number } | null;
  /** A stroke around the glyphs, `width` canvas pixels wide on each side. */
  border: { color: string; alpha: number; width: number } | null;
}

/** `text_h` of a drawtext line for a font size: the line box is a little taller than the em. */
export const TEXT_LINE_HEIGHT = 1.2;

/** Templates whose own layout wins over `position` in the render (`RegionFilterGraphBuilder`). */
export const POSITION_IGNORING_TEMPLATES: ReadonlySet<string> = new Set(['lowerThird', 'card']);

const POSITIONS = new Set(['topLeft', 'topCenter', 'topRight', 'middleLeft', 'center', 'middleRight', 'bottomLeft', 'bottomCenter', 'bottomRight']);

/** Mirror of `CaptionBurnIn.Placement`: anchor x/y of a 3x3 position (margins 8% of height, 14% on a vertical frame). */
export function positionAnchor(position: string, canvasWidth: number, canvasHeight: number, textH: number): { align: 'left' | 'center' | 'right'; x: number; y: number } {
  const margin = canvasWidth < canvasHeight ? canvasHeight * 0.14 : canvasHeight / 12;
  const col = position.endsWith('Left') ? 'l' : position.endsWith('Right') ? 'r' : 'c';
  const row = position.startsWith('top') ? 't' : position.startsWith('bottom') ? 'b' : 'm';
  return {
    align: col === 'l' ? 'left' : col === 'r' ? 'right' : 'center',
    x: col === 'l' ? canvasWidth / 12 : col === 'r' ? canvasWidth - canvasWidth / 12 : canvasWidth / 2,
    y: row === 't' ? margin : row === 'b' ? canvasHeight - textH - margin : (canvasHeight - textH) / 2,
  };
}

export function titleTemplateLayout(template: string | null | undefined, canvasWidth: number, canvasHeight: number, fontPx: number, position?: string | null): TitleTemplateLayout {
  const base = templateLayout(template, canvasWidth, canvasHeight, fontPx);
  if (!position || !POSITIONS.has(position) || (template && POSITION_IGNORING_TEMPLATES.has(template))) return base;
  return { ...base, ...positionAnchor(position, canvasWidth, canvasHeight, fontPx * TEXT_LINE_HEIGHT) };
}

function templateLayout(template: string | null | undefined, canvasWidth: number, canvasHeight: number, fontPx: number): TitleTemplateLayout {
  const textH = fontPx * TEXT_LINE_HEIGHT;
  const pad = Math.max(8 * (canvasHeight / 1080), canvasHeight / 54);
  const hairline = (px: number) => px * (canvasHeight / 1080);
  const centredY = (canvasHeight - textH) / 2;
  switch (template) {
    case 'lowerThird':
      return { align: 'left', x: canvasWidth / 12, y: canvasHeight - textH - canvasHeight / 8, box: { color: '#000000', alpha: 0.55, pad }, border: null };
    case 'boxed':
      return { align: 'center', x: canvasWidth / 2, y: centredY, box: { color: '#000000', alpha: 0.6, pad }, border: null };
    case 'outline':
      return { align: 'center', x: canvasWidth / 2, y: centredY, box: null, border: { color: '#000000', alpha: 1, width: hairline(4) } };
    case 'card':
      return { align: 'center', x: canvasWidth / 2, y: centredY, box: { color: '#000000', alpha: 0.8, pad: pad * 3 }, border: null };
    default:
      return { align: 'center', x: canvasWidth / 2, y: centredY, box: null, border: { color: '#000000', alpha: 0.6, width: hairline(2) } };
  }
}
