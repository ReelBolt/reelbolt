import { EditorStore } from '@/lib/editing/store';
import { RULER_HEIGHT, MARKER_LANE_HEIGHT } from '../timeline/paint';

export const IS_MAC = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform ?? '');

/**
 * T-018: where the clip menu opens for the keyboard: at the start of the first selected clip on the timeline canvas
 * (clamped inside it), or null when nothing is selected or the canvas is not on screen.
 */
export function selectedClipMenuAnchor(store: EditorStore): { x: number; y: number } | null {
  const { doc, ui } = store.getState();
  const canvas = typeof document === 'undefined' ? null : document.querySelector<HTMLElement>('[data-testid="timeline-canvas"]');
  if (!canvas || ui.selection.length === 0) return null;
  const rect = canvas.getBoundingClientRect();
  let top = RULER_HEIGHT + MARKER_LANE_HEIGHT;
  for (const track of doc.tracks) {
    const item = track.items.find((i) => i.id === ui.selection[0]);
    const height = Math.max(24, Math.round(track.height) || 64);
    if (item) {
      const x = rect.left + (item.recordIn - ui.scrollFrame) * ui.pixelsPerFrame + 12;
      return { x: Math.min(Math.max(x, rect.left + 4), rect.right - 4), y: Math.min(rect.top + top + Math.min(height / 2, 24), rect.bottom - 4) };
    }
    top += height;
  }
  return null;
}
