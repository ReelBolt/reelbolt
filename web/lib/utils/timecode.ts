/**
 * Timecode and frame math utilities for the ReelBolt Studio Timeline.
 */

/**
 * Formats seconds into standard SMPTE timecode string: HH:MM:SS:FF
 */
export function formatTimecode(seconds: number, fps: number = 30): string {
  if (isNaN(seconds) || seconds < 0) seconds = 0;
  const safeFps = fps > 0 ? fps : 30;
  const totalFrames = Math.floor(seconds * safeFps);
  const frames = totalFrames % Math.round(safeFps);
  const totalSecs = Math.floor(seconds);
  const s = totalSecs % 60;
  const m = Math.floor(totalSecs / 60) % 60;
  const h = Math.floor(totalSecs / 3600);

  const pad = (n: number, z = 2) => String(n).padStart(z, '0');
  return `${pad(h)}:${pad(m)}:${pad(s)}:${pad(frames)}`;
}

/**
 * Formats seconds into MM:SS.FF format for concise header displays.
 */
export function formatTimecodeShort(seconds: number, fps: number = 30): string {
  if (isNaN(seconds) || seconds < 0) seconds = 0;
  const safeFps = fps > 0 ? fps : 30;
  const totalFrames = Math.floor(seconds * safeFps);
  const frames = totalFrames % Math.round(safeFps);
  const totalSecs = Math.floor(seconds);
  const s = totalSecs % 60;
  const m = Math.floor(totalSecs / 60);

  const pad = (n: number, z = 2) => String(n).padStart(z, '0');
  return `${pad(m)}:${pad(s)}.${pad(frames)}`;
}

/**
 * Formats seconds as readable MM:SS or S.s
 */
export function formatDurationSec(seconds: number): string {
  if (isNaN(seconds) || seconds < 0) return '0.00s';
  if (seconds < 60) {
    return `${seconds.toFixed(2)}s`;
  }
  const m = Math.floor(seconds / 60);
  const s = (seconds % 60).toFixed(1);
  return `${m}m ${s}s`;
}
