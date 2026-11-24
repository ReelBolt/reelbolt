/** @vitest-environment node */
/**
 * T-012: titles, captions and generators sit at their own track's place in the stack, as the render composites them.
 */
import { describe, expect, it } from 'vitest';
import { activeLayersAt } from '../scheduler';
import { overlayRunsOf } from '../overlay-stack';
import { caption, doc, media, title, track } from '../../__tests__/fixtures';

describe('overlayRunsOf', () => {
  it('a title on V1 under a video on V2 is a run BELOW the video, not above everything', () => {
    const d = doc([track('v1', 'video', [title('t1', 0, 100)]), track('v2', 'video', [media('m2', 0, 100)])]);
    const layers = activeLayersAt(d, 10);
    expect(layers.map((l) => l.item.id)).toEqual(['t1', 'm2']);
    const runs = overlayRunsOf(layers);
    expect([...runs.keys()].map((l) => l.item.id)).toEqual(['t1']);
    // The run's first layer is index 0: it draws before (under) the media clip at index 1.
    expect([...runs.keys()][0].layerIndex).toBeLessThan(layers[1].layerIndex);
  });

  it('consecutive overlay tracks share one run; a media track between them splits the runs', () => {
    const d = doc([
      track('v1', 'video', [title('t1', 0, 100)]),
      track('v2', 'video', [caption('c1', 0, 100)]),
      track('v3', 'video', [media('m3', 0, 100)]),
      track('v4', 'video', [title('t4', 0, 100)]),
    ]);
    const runs = [...overlayRunsOf(activeLayersAt(d, 10)).values()];
    expect(runs.map((r) => r.items.map((i) => i.item.id))).toEqual([['t1', 'c1'], ['t4']]);
    expect(runs.map((r) => r.slot)).toEqual([0, 1]);
  });

  it('the signature changes with the content and is stable for the same content', () => {
    const a = overlayRunsOf(activeLayersAt(doc([track('v1', 'video', [title('t1', 0, 100, 'Hello')])]), 10));
    const b = overlayRunsOf(activeLayersAt(doc([track('v1', 'video', [title('t1', 0, 100, 'Hello')])]), 20));
    const c = overlayRunsOf(activeLayersAt(doc([track('v1', 'video', [title('t1', 0, 100, 'Bye')])]), 10));
    expect([...a.values()][0].signature).toBe([...b.values()][0].signature);
    expect([...a.values()][0].signature).not.toBe([...c.values()][0].signature);
  });

  it('a frame with only media has no overlay runs', () => {
    expect(overlayRunsOf(activeLayersAt(doc([track('v1', 'video', [media('m', 0, 50)])]), 10)).size).toBe(0);
  });
});
