/** Maps music-file seconds onto the record frames of a clip showing that music, and back. */

import type { MediaClip } from '../types';
import type { GridLine } from './beat-grid';

export interface RecordBeat {
  frame: number;
  bar: boolean;
  barNumber: number;
}

const rateOf = (clip: MediaClip): number => (typeof clip.playbackRate === 'number' && clip.playbackRate > 0 ? clip.playbackRate : 1);
const sourceInOf = (clip: MediaClip): number => (typeof clip.sourceIn === 'number' ? clip.sourceIn : 0);
const roundHalfUp = (x: number): number => Math.floor(x + 0.5);

/** One source second to its record frame (unclipped); null for a hold. */
export function sourceSecToRecordFrame(clip: MediaClip, sec: number, fps: number): number | null {
  if (clip.hold === true) return null;
  const rate = rateOf(clip);
  const sourceIn = sourceInOf(clip);
  const src = sec * fps;
  if (clip.reverse === true) {
    const sourceEnd = sourceIn + (clip.recordOut - clip.recordIn) * rate;
    return clip.recordIn + roundHalfUp((sourceEnd - src) / rate);
  }
  return clip.recordIn + roundHalfUp((src - sourceIn) / rate);
}

/** Inverse: the source second shown at a record frame. */
export function recordFrameToSourceSec(clip: MediaClip, frame: number, fps: number): number {
  const rate = rateOf(clip);
  const sourceIn = sourceInOf(clip);
  const offset = (frame - clip.recordIn) * rate;
  if (clip.reverse === true) {
    const sourceEnd = sourceIn + (clip.recordOut - clip.recordIn) * rate;
    return (sourceEnd - offset) / fps;
  }
  return (sourceIn + offset) / fps;
}

/** Grid lines to record frames inside `[recordIn, recordOut)`, ascending and de-duplicated. */
export function beatSecondsToRecordFrames(clip: MediaClip, lines: readonly GridLine[], fps: number): RecordBeat[] | { refused: 'hold' } {
  if (clip.hold === true) return { refused: 'hold' };
  const mapped: RecordBeat[] = [];
  for (const line of lines) {
    const frame = sourceSecToRecordFrame(clip, line.sec, fps);
    if (frame === null || frame < clip.recordIn || frame >= clip.recordOut) continue;
    mapped.push({ frame, bar: line.bar, barNumber: line.barNumber });
  }
  mapped.sort((a, b) => a.frame - b.frame);
  const out: RecordBeat[] = [];
  for (const m of mapped) {
    const last = out[out.length - 1];
    if (last && last.frame === m.frame) {
      if (m.bar && !last.bar) out[out.length - 1] = m;
    } else out.push(m);
  }
  return out;
}
