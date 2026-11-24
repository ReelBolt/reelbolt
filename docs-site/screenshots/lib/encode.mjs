// PNG buffer -> WebP at the highest quality that stays under the size budget. Deterministic:
// same pixels in, same bytes out.
import { loadSharp } from './env.mjs';

export const MAX_BYTES = 120 * 1024;

export async function encode(png) {
  const sharp = loadSharp();
  let last;
  for (const quality of [88, 82, 76, 70, 64, 58, 52, 46]) {
    last = await sharp(png).webp({ quality, effort: 6, smartSubsample: true }).toBuffer();
    if (last.length <= MAX_BYTES) return { buffer: last, quality };
  }
  return { buffer: last, quality: 46, tooBig: true };
}

/** A pixel counts as changed when any channel moves by more than this (anti-aliasing and encoder noise stay below it). */
export const CHANGED_LEVELS = 40;

/**
 * Whether the new image shows what the old one shows: no pixel differs by more than CHANGED_LEVELS. Browsers
 * and the lossy encoder move single pixels by a level or two from run to run, and the lossy encoder turns that
 * into different bytes; a real change (a word, a colour, a layout shift) moves pixels by far more.
 */
export async function looksUnchanged(oldWebp, newWebp) {
  const sharp = loadSharp();
  const [a, b] = await Promise.all([oldWebp, newWebp].map((buf) => sharp(buf).removeAlpha().raw().toBuffer({ resolveWithObject: true })));
  if (a.info.width !== b.info.width || a.info.height !== b.info.height) return false;
  for (let i = 0; i < a.data.length; i++) if (Math.abs(a.data[i] - b.data[i]) > CHANGED_LEVELS) return false;
  return true;
}
