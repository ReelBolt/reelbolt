import type { SheetDetent } from '../phone/BottomSheet';

/** The phone stage is pinned at about 45 dvh (R13.6). */
export const PHONE_STAGE_VH = 0.45;
/** Never squeezed below this, so the picture stays something you can see and aim at. */
export const PHONE_STAGE_FLOOR_PX = 160;
const GAP_PX = 8;

/** The heights `BottomSheet` gives its detents, in CSS pixels for a window `viewportHeight` tall. */
export function sheetHeightPx(detent: SheetDetent, viewportHeight: number): number {
  return detent === 'peek' ? 120 : Math.round(viewportHeight * (detent === 'half' ? 0.45 : 0.9));
}

/**
 * How tall the stage is on a portrait phone: 45 dvh, but when a sheet is open it shrinks so it ends above the sheet
 * and stays visible and reactive. `stageTop` is where the stage starts in the window.
 */
export function phoneStageHeight(viewportHeight: number, stageTop: number, detent: SheetDetent | null): number {
  const pinned = Math.round(viewportHeight * PHONE_STAGE_VH);
  if (detent === null) return pinned;
  const room = viewportHeight - stageTop - sheetHeightPx(detent, viewportHeight) - GAP_PX;
  return Math.max(PHONE_STAGE_FLOOR_PX, Math.min(pinned, Math.floor(room)));
}

/** The four panels the dock opens, in dock order. */
export const PICTURE_DOCK = [
  { id: 'layers', label: 'Layers' },
  { id: 'text', label: 'Text' },
  { id: 'ai', label: 'AI' },
  { id: 'canvas', label: 'Canvas' },
] as const;

export type PicturePanelId = (typeof PICTURE_DOCK)[number]['id'];
