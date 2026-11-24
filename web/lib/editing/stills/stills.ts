/**
 * Stills and per-clip export (P6).
 *
 * A still is the program viewer's canvas at the playhead, saved as PNG in the browser. It is
 * **preview quality** — the compositor's proxy-resolution frame, not the renderer's output — and
 * its file name says so, because a reference still that silently differed from the render would
 * mislead a colour session. A per-clip export is the ordinary render with the range set to the
 * selected clip's span: there is no second render path.
 */

import { framesToTimecode } from '../time';
import type { EditTimeline, Item, Rational } from '../types';

/** `My cut` + frame 1234 at 30 fps → `my-cut-00-00-41-04-preview.png`. */
export function stillFileName(docName: string, frame: number, rate: Rational): string {
  const slug = docName.trim().toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '-').replace(/^-+|-+$/g, '') || 'timeline';
  const tc = framesToTimecode(frame, rate).replace(/[:;]/g, '-');
  return `${slug}-${tc}-preview.png`;
}

/**
 * S12: the canvas that holds the picture on screen. The live (WebGL) session draws to the program canvas; the
 * degraded single-clip session shows a `<video>` instead and never draws the canvas, so saving the canvas
 * would write a blank PNG. When the canvas is hidden and its sibling video is showing a frame, that frame is
 * copied onto a fresh canvas; null when there is no picture to take (nothing decoded yet, or the canvas is blank
 * because no session drew to it).
 */
export function programPictureCanvas(canvas: HTMLCanvasElement | null): HTMLCanvasElement | null {
  if (!canvas) return null;
  const hidden = canvas.style.display === 'none';
  if (!hidden) return canvas;
  const video = canvas.parentElement?.querySelector('video') ?? null;
  if (!video || video.style.display === 'none' || video.videoWidth === 0 || video.videoHeight === 0 || video.readyState < 2) return null;
  const copy = document.createElement('canvas');
  copy.width = video.videoWidth;
  copy.height = video.videoHeight;
  const ctx = copy.getContext('2d');
  if (!ctx) return null;
  ctx.drawImage(video, 0, 0, copy.width, copy.height);
  return copy;
}

/** The canvas as a PNG blob, or null when the canvas cannot encode (tainted, zero-sized, no 2D/GL). */
export function canvasToPngBlob(canvas: HTMLCanvasElement): Promise<Blob | null> {
  return new Promise((resolve) => {
    if (canvas.width === 0 || canvas.height === 0) {
      resolve(null);
      return;
    }
    try {
      canvas.toBlob((blob) => resolve(blob), 'image/png');
    } catch {
      resolve(null);
    }
  });
}

/** Saves a blob through a transient anchor; the only DOM this module touches. */
export function downloadBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  a.rel = 'noopener';
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** The render range covering exactly one clip. */
export function clipExportRange(item: Pick<Item, 'recordIn' | 'recordOut'>): { inFrame: number; outFrame: number } {
  return { inFrame: item.recordIn, outFrame: item.recordOut };
}

/** `My cut` + clip `Interview` → `my-cut-interview.mp4`. */
export function clipExportFileName(doc: Pick<EditTimeline, 'name'>, item: Pick<Item, 'name' | 'id'>): string {
  const slug = (s: string) => s.trim().toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '-').replace(/^-+|-+$/g, '');
  const base = slug(doc.name) || 'timeline';
  const clip = slug(item.name) || item.id.slice(0, 8);
  return `${base}-${clip}.mp4`;
}

/** The single selected item of a selection, or null when none or several are selected. */
export function singleSelectedItem(doc: EditTimeline, selection: readonly string[]): Item | null {
  if (selection.length !== 1) return null;
  for (const track of doc.tracks) {
    const item = track.items.find((i) => i.id === selection[0]);
    if (item) return item;
  }
  return null;
}
