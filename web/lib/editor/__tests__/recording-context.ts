/**
 * A minimal recording 2D context for the editor's tests. jsdom ships no canvas, and the compositor
 * is specified by the SEQUENCE of calls it makes (translate, rotate, scale, then drawImage, with the
 * alpha and blend operation in force at that moment), which is exactly what this records.
 *
 * It implements only the members `compose.ts` and `mask.ts` use; anything else is a TypeError, so a
 * new context call in production code fails a test loudly instead of being silently ignored.
 */

import type { Bitmap } from '../compose';

export interface RecordedCall {
  op: string;
  args: unknown[];
  /** `globalAlpha` / `globalCompositeOperation` / `shadowBlur` in force when the call was made. */
  alpha: number;
  composite: string;
  shadowBlur: number;
  fillStyle: unknown;
  strokeStyle: unknown;
  lineWidth: number;
  font: string;
}

const STATE_KEYS = [
  'globalAlpha',
  'globalCompositeOperation',
  'fillStyle',
  'strokeStyle',
  'lineWidth',
  'font',
  'textAlign',
  'textBaseline',
  'lineJoin',
  'lineCap',
  'shadowColor',
  'shadowBlur',
  'shadowOffsetX',
  'shadowOffsetY',
  'letterSpacing',
] as const;

export class RecordingContext {
  calls: RecordedCall[] = [];
  globalAlpha = 1;
  globalCompositeOperation = 'source-over';
  fillStyle: unknown = '#000000';
  strokeStyle: unknown = '#000000';
  lineWidth = 1;
  font = '10px sans-serif';
  textAlign = 'start';
  textBaseline = 'alphabetic';
  lineJoin = 'miter';
  lineCap = 'butt';
  shadowColor = 'rgba(0,0,0,0)';
  shadowBlur = 0;
  shadowOffsetX = 0;
  shadowOffsetY = 0;
  letterSpacing = '0px';
  private stack: Record<string, unknown>[] = [];

  /** Width = 10 px per character, so wrapping is predictable in assertions. */
  measureText(text: string) {
    return { width: text.length * 10 };
  }

  private record(op: string, args: unknown[]) {
    this.calls.push({
      op,
      args,
      alpha: this.globalAlpha,
      composite: this.globalCompositeOperation,
      shadowBlur: this.shadowBlur,
      fillStyle: this.fillStyle,
      strokeStyle: this.strokeStyle,
      lineWidth: this.lineWidth,
      font: this.font,
    });
  }

  save() {
    const snapshot: Record<string, unknown> = {};
    for (const key of STATE_KEYS) snapshot[key] = this[key];
    this.stack.push(snapshot);
    this.record('save', []);
  }

  restore() {
    const snapshot = this.stack.pop();
    if (snapshot) Object.assign(this, snapshot);
    this.record('restore', []);
  }

  translate(...a: unknown[]) { this.record('translate', a); }
  rotate(...a: unknown[]) { this.record('rotate', a); }
  scale(...a: unknown[]) { this.record('scale', a); }
  clearRect(...a: unknown[]) { this.record('clearRect', a); }
  fillRect(...a: unknown[]) { this.record('fillRect', a); }
  drawImage(...a: unknown[]) { this.record('drawImage', a); }
  fillText(...a: unknown[]) { this.record('fillText', a); }
  strokeText(...a: unknown[]) { this.record('strokeText', a); }
  beginPath(...a: unknown[]) { this.record('beginPath', a); }
  moveTo(...a: unknown[]) { this.record('moveTo', a); }
  lineTo(...a: unknown[]) { this.record('lineTo', a); }
  arc(...a: unknown[]) { this.record('arc', a); }
  fill(...a: unknown[]) { this.record('fill', a); }
  stroke(...a: unknown[]) { this.record('stroke', a); }
  // The workspace painter's extra calls (lib/editor/workspace.ts).
  setTransform(...a: unknown[]) { this.record('setTransform', a); }
  rect(...a: unknown[]) { this.record('rect', a); }
  clip(...a: unknown[]) { this.record('clip', a); }
  createPattern(...a: unknown[]) {
    this.record('createPattern', a);
    return { pattern: true };
  }
  createLinearGradient(...a: unknown[]) {
    this.record('createLinearGradient', a);
    return { addColorStop: () => {} };
  }

  ops(): string[] {
    return this.calls.map((c) => c.op);
  }

  of(op: string): RecordedCall[] {
    return this.calls.filter((c) => c.op === op);
  }

  as(): CanvasRenderingContext2D {
    return this as unknown as CanvasRenderingContext2D;
  }
}

/** A stand-in decoded image: the compositor only reads `width` and `height` and hands it to drawImage. */
export function fakeBitmap(width: number, height: number, name = 'bitmap'): Bitmap {
  return { width, height, name } as unknown as Bitmap;
}
