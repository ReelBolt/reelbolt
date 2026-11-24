/** @vitest-environment node */
/** T-008c: the bars pattern (measured on real ffmpeg smptehdbars) and where adjustment clips sit in the stack. */
import { describe, expect, it } from 'vitest';
import { barsRects } from '../bars-pattern';
import { activeLayersAt } from '../scheduler';
import { overlayRunsOf } from '../overlay-stack';
import { classifyItem } from '../parity';
import { doc, media, title, track } from '../../__tests__/fixtures';
import type { AdjustmentClip, GeneratorClip } from '../../types';

const colorAt = (w: number, h: number, x: number, y: number) => [...barsRects(w, h)].reverse().find((r) => x >= r.x && x < r.x + r.w && y >= r.y && y < r.y + r.h)?.color;

describe('barsRects', () => {
  it('matches the measured 1920x1080 frame', () => {
    expect(colorAt(1920, 1080, 100, 100)).toEqual([102, 102, 102]);
    expect(colorAt(1920, 1080, 300, 100)).toEqual([190, 190, 190]);
    expect(colorAt(1920, 1080, 500, 100)).toEqual([188, 201, 6]);
    expect(colorAt(1920, 1080, 1600, 100)).toEqual([0, 0, 182]);
    expect(colorAt(1920, 1080, 100, 650)).toEqual([21, 255, 252]);
    expect(colorAt(1920, 1080, 800, 650)).toEqual([190, 190, 190]);
    expect(colorAt(1920, 1080, 100, 750)).toEqual([251, 255, 10]);
    expect(colorAt(1920, 1080, 460, 750)).toEqual([0, 0, 0]);
    expect(colorAt(1920, 1080, 1620, 750)).toEqual([255, 255, 255]);
    expect(colorAt(1920, 1080, 700, 900)).toEqual([255, 255, 255]);
    expect(colorAt(1920, 1080, 1300, 900)).toEqual([4, 4, 4]);
  });

  it('the ramp rises one level at a time and scales to another canvas', () => {
    const ramp = barsRects(1920, 1080).filter((r) => r.y === 720 && r.x >= 530 && r.x < 1586);
    expect(ramp.length).toBe(255);
    expect(ramp[0].color).toEqual([1, 1, 1]);
    expect(ramp[254].color).toEqual([255, 255, 255]);
    const small = barsRects(480, 270);
    expect(small.every((r) => r.x >= 0 && r.y >= 0 && r.x + r.w <= 480 && r.y + r.h <= 270)).toBe(true);
    expect(colorAt(480, 270, 25, 25)).toEqual([102, 102, 102]);
  });
});

describe('adjustment clips in the stack', () => {
  const adj: AdjustmentClip = { id: 'adj', type: 'adjustmentClip', name: 'adj', recordIn: 0, recordOut: 100 };

  it('is a layer at its own track position and splits overlay runs', () => {
    const d = doc([track('v1', 'video', [media('m', 0, 100)]), track('v2', 'video', [adj]), track('v3', 'video', [title('t', 0, 100)])]);
    const layers = activeLayersAt(d, 10);
    expect(layers.map((l) => l.item.id)).toEqual(['m', 'adj', 't']);
    expect([...overlayRunsOf(layers).keys()].map((l) => l.item.id)).toEqual(['t']);
  });

  it('is faithful when its effects preview, render-required when one does not', () => {
    expect(classifyItem(adj).parity).toBe('faithful');
    expect(classifyItem({ ...adj, effects: [{ type: 'sparkle' as never }] }).parity).toBe('renderRequired');
  });

  it('the bars generator is faithful, test pattern stays approximate', () => {
    const gen = (generatorType: string) => ({ id: 'g', type: 'generatorClip', name: 'g', recordIn: 0, recordOut: 10, generatorType, generatorConfig: {} }) as unknown as GeneratorClip;
    expect(classifyItem(gen('bars')).parity).toBe('faithful');
    expect(classifyItem(gen('testsrc')).parity).toBe('approximate');
  });
});
