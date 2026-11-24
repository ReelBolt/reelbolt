/**
 * The 2D overlay pass — titles, captions, generator solids and insert placeholders rasterised onto
 * an offscreen 2D canvas that the GL compositor draws last. Everything here is `approximate` by the
 * parity contract (`parity.ts`): the browser's text engine is not ffmpeg's drawtext.
 *
 * Pure in the sense that matters: it takes a 2D context and the items on screen, and draws. The
 * tests run it against a recording fake context.
 */

import type { ClipTransform, Item } from '../types';
import { drawBars } from './bars-pattern';
import { evaluateKeyframed } from './scheduler';
import { TEXT_LINE_HEIGHT, titleTemplateLayout } from './title-template';

export interface OverlayItem {
  item: Item;
  /** The layer's draw order index (0 = bottom). */
  layerIndex: number;
  /** The timeline frame, for a title whose transform is keyframed (the raster then depends on the frame). */
  frame?: number;
}

/** True when a title's transform carries keyframes (its raster changes from frame to frame). */
export function hasKeyedTransform(transform: ClipTransform | null | undefined): boolean {
  if (!transform) return false;
  return Object.values(transform).some((c) => !!c && Array.isArray((c as { keys?: unknown[] | null }).keys) && ((c as { keys: unknown[] }).keys.length > 0));
}

interface Ctx2D {
  canvas: { width: number; height: number };
  clearRect(x: number, y: number, w: number, h: number): void;
  /** Optional: a title with a free transform is drawn under a transform; a context without these draws it untransformed. */
  save?(): void;
  restore?(): void;
  translate?(x: number, y: number): void;
  rotate?(radians: number): void;
  scale?(x: number, y: number): void;
  fillRect(x: number, y: number, w: number, h: number): void;
  strokeRect(x: number, y: number, w: number, h: number): void;
  fillText(text: string, x: number, y: number, maxWidth?: number): void;
  /** Optional: the title templates' outline/border; a context without it draws the text only. */
  strokeText?(text: string, x: number, y: number, maxWidth?: number): void;
  measureText?(text: string): { width: number };
  lineJoin?: CanvasLineJoin;
  fillStyle: string | CanvasGradient | CanvasPattern;
  strokeStyle: string | CanvasGradient | CanvasPattern;
  lineWidth: number;
  font: string;
  textAlign: CanvasTextAlign;
  textBaseline: CanvasTextBaseline;
  globalAlpha: number;
}

const SAFE_COLOR = /^#[0-9a-fA-F]{6}$|^#[0-9a-fA-F]{3}$/;

function safeColor(value: unknown, fallback: string): string {
  return typeof value === 'string' && SAFE_COLOR.test(value) ? value : fallback;
}

/** Draws every overlay item for one frame. Returns how many items were drawn. */
export function rasterizeOverlays(ctx: Ctx2D, items: readonly OverlayItem[]): number {
  const { width, height } = ctx.canvas;
  ctx.clearRect(0, 0, width, height);
  let drawn = 0;

  for (const { item, frame } of items) {
    const enabled = (item as { enabled?: boolean }).enabled;
    if (enabled === false) continue;
    // T-014: a title's free transform moves/scales/rotates the whole layer about the canvas centre (as the render's
    // canvas-size layer does), then offsets it; its opacity multiplies the text's alpha.
    const tf = item.type === 'titleClip' && item.transform ? item.transform : null;
    let titleOpacity = 1;
    if (tf && ctx.save && ctx.translate && ctx.rotate && ctx.scale) {
      const f = frame ?? item.recordIn;
      const sx = evaluateKeyframed(tf.scale ?? null, f, 1);
      const sy = tf.scaleY ? evaluateKeyframed(tf.scaleY, f, 1) : sx;
      ctx.save();
      ctx.translate(width / 2 + evaluateKeyframed(tf.positionX ?? null, f, 0) * width, height / 2 + evaluateKeyframed(tf.positionY ?? null, f, 0) * height);
      ctx.rotate((evaluateKeyframed(tf.rotation ?? null, f, 0) * Math.PI) / 180);
      ctx.scale(sx > 0 ? sx : 1, sy > 0 ? sy : 1);
      ctx.translate(-width / 2, -height / 2);
      titleOpacity = Math.min(1, Math.max(0, evaluateKeyframed(tf.opacity ?? null, f, 1)));
    }
    const transformed = !!(tf && ctx.save && ctx.translate && ctx.rotate && ctx.scale);
    switch (item.type) {
      case 'titleClip': {
        const fontSize = typeof item.fontSize === 'number' && item.fontSize > 0 ? item.fontSize : 48;
        const scaled = (fontSize / 1080) * height;
        const fontPx = Math.max(8, Math.round(scaled));
        ctx.font = `${fontPx}px ${sanitizeFontFamily(item.fontFamily)}`;
        // S6: the template decides where the text sits and what is drawn behind and around it, as the render does.
        const layout = titleTemplateLayout(item.template, width, height, fontPx, item.position);
        const maxWidth = width * 0.9;
        ctx.textAlign = layout.align;
        ctx.textBaseline = 'top';
        if (layout.box) {
          const measured = ctx.measureText ? ctx.measureText(item.text).width : fontPx * 0.55 * item.text.length;
          const textW = Math.min(measured, maxWidth);
          const left = layout.align === 'center' ? layout.x - textW / 2 : layout.align === 'right' ? layout.x - textW : layout.x;
          ctx.globalAlpha = layout.box.alpha * titleOpacity;
          ctx.fillStyle = layout.box.color;
          ctx.fillRect(left - layout.box.pad, layout.y - layout.box.pad, textW + 2 * layout.box.pad, fontPx * TEXT_LINE_HEIGHT + 2 * layout.box.pad);
        }
        if (layout.border && ctx.strokeText) {
          ctx.globalAlpha = layout.border.alpha * titleOpacity;
          ctx.strokeStyle = layout.border.color;
          ctx.lineWidth = layout.border.width * 2;
          ctx.lineJoin = 'round';
          ctx.strokeText(item.text, layout.x, layout.y, maxWidth);
        }
        ctx.globalAlpha = titleOpacity;
        ctx.fillStyle = safeColor(item.color, '#FFFFFF');
        ctx.fillText(item.text, layout.x, layout.y, maxWidth);
        drawn += 1;
        break;
      }
      case 'caption': {
        const scaled = (36 / 1080) * height;
        ctx.font = `${Math.max(8, Math.round(scaled))}px sans-serif`;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'bottom';
        ctx.globalAlpha = 0.65;
        ctx.fillStyle = '#000000';
        const boxH = scaled * 1.5;
        ctx.fillRect(width * 0.1, height * 0.92 - boxH, width * 0.8, boxH);
        ctx.globalAlpha = 1;
        ctx.fillStyle = '#FFFFFF';
        ctx.fillText(item.text, width / 2, height * 0.92 - scaled * 0.25, width * 0.78);
        drawn += 1;
        break;
      }
      case 'generatorClip': {
        const config = (item.generatorConfig ?? {}) as Record<string, unknown>;
        ctx.globalAlpha = 1;
        if (item.generatorType === 'bars') {
          drawBars(ctx);
          drawn += 1;
          break;
        }
        ctx.fillStyle = safeColor(config.color, item.generatorType === 'bars' ? '#808080' : '#000000');
        ctx.fillRect(0, 0, width, height);
        drawn += 1;
        break;
      }
      case 'insertClip': {
        // The quad's bounding box (base value only) as an outlined region — the corner pin and
        // matte are render-only, and the outline says so visually rather than faking a warp.
        const quad = item.quad?.value;
        if (!quad) break;
        const xs = [quad.topLeft.x, quad.topRight.x, quad.bottomRight.x, quad.bottomLeft.x];
        const ys = [quad.topLeft.y, quad.topRight.y, quad.bottomRight.y, quad.bottomLeft.y];
        const normalized = xs.every((v) => v >= 0 && v <= 1) && ys.every((v) => v >= 0 && v <= 1);
        const sx = normalized ? width : 1;
        const sy = normalized ? height : 1;
        const minX = Math.min(...xs) * sx;
        const maxX = Math.max(...xs) * sx;
        const minY = Math.min(...ys) * sy;
        const maxY = Math.max(...ys) * sy;
        ctx.globalAlpha = 0.35;
        ctx.fillStyle = '#4C6EF5';
        ctx.fillRect(minX, minY, Math.max(1, maxX - minX), Math.max(1, maxY - minY));
        ctx.globalAlpha = 1;
        ctx.strokeStyle = '#4C6EF5';
        ctx.lineWidth = 2;
        ctx.strokeRect(minX, minY, Math.max(1, maxX - minX), Math.max(1, maxY - minY));
        drawn += 1;
        break;
      }
      default:
        break;
    }
    if (transformed) ctx.restore?.();
  }
  ctx.globalAlpha = 1;
  return drawn;
}

/** A font family is a CSS token list; anything with quotes/semicolons/parens is replaced. */
export function sanitizeFontFamily(value: unknown): string {
  if (typeof value !== 'string') return 'sans-serif';
  const trimmed = value.trim();
  if (trimmed.length === 0 || trimmed.length > 64 || /[;(){}"'\\<>]/.test(trimmed)) return 'sans-serif';
  return trimmed;
}
