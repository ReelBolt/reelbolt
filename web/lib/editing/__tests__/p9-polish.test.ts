/** @vitest-environment node */
/**
 * P9 polish — the pure modules: the keymap (physical-key chords, overrides, unbinding, conflicts,
 * stale-override sanitising), the mastering presets (clamped into the render's ranges), the HSL
 * qualifier / power window value rules in the engine (and that the preview never folds a secondary
 * whole-frame), the NCC feature tracker on a synthetic moving texture (and its window offsets), and
 * review-comment threading.
 */

import { describe, expect, it } from 'vitest';
import fc from 'fast-check';
import { apply, EditEngineError, isGradeQualifier, isGradeWindow } from '../engine';
import { buildKeymap, conflicts, effectiveChords, eventToChord, keyOf, sanitizeOverrides, KEY_ACTIONS } from '../keymap';
import { clampMastering, MASTERING_PRESETS } from '../presets';
import { previewGradeOf, isSecondary, isIdentityPreviewGrade } from '../color/preview-grade';
import { classifyItem } from '../preview';
import { trackFeature, trackToWindowOffsets, type GrayFrame } from '../color/feature-tracker';
import { threadComments, type ReviewComment } from '../api/editor-p9';
import { doc, media, track, TRACKS } from './fixtures';
import type { GradeStack, MediaClip } from '../types';

const ev = (code: string, key: string, mods: Partial<Record<'ctrlKey' | 'metaKey' | 'shiftKey' | 'altKey', boolean>> = {}) => ({ code, key, ctrlKey: false, metaKey: false, shiftKey: false, altKey: false, ...mods });

describe('keymap', () => {
  it('spells chords from the physical key, so Shift never changes which key it is', () => {
    expect(eventToChord(ev('KeyB', 'b'))).toBe('B');
    expect(eventToChord(ev('Comma', '<', { shiftKey: true }))).toBe('Shift+,');
    expect(eventToChord(ev('KeyZ', 'z', { metaKey: true, shiftKey: true }))).toBe('Mod+Shift+Z');
    expect(eventToChord(ev('Space', ' '))).toBe('Space');
    expect(eventToChord(ev('', 'j'))).toBe('J');
    expect(keyOf({ code: 'Digit3', key: '#' })).toBe('3');
  });

  it('applies overrides, unbinds with an empty chord, and reports conflicts', () => {
    expect(buildKeymap({}).get('B')).toBe('toolBlade');
    const remapped = buildKeymap({ toolBlade: 'C', deleteSelection: '' });
    expect(remapped.get('C')).toBe('toolBlade');
    expect(remapped.get('B')).toBeUndefined();
    expect(remapped.get('Delete')).toBeUndefined();
    // D7: Delete ripples (closes the gap), Shift+Delete lifts (leaves it).
    expect(buildKeymap({}).get('Delete')).toBe('deleteSelection');
    expect(buildKeymap({}).get('Shift+Delete')).toBe('liftSelection');
    expect(effectiveChords({ redo: 'Mod+R' }).redo).toEqual(['Mod+R']);
    expect(conflicts({})).toEqual([]);
    expect(conflicts({ toolBlade: 'M' })).toEqual([{ chord: 'M', actions: ['addMarker', 'toolBlade'] }].map((c) => ({ ...c, actions: c.actions.sort((a, b) => KEY_ACTIONS.findIndex((x) => x.id === a) - KEY_ACTIONS.findIndex((x) => x.id === b)) })));
    expect(sanitizeOverrides({ toolBlade: 'C', nonsense: 'X', undo: 7, redo: 'x'.repeat(40) })).toEqual({ toolBlade: 'C' });
  });

  it('every default chord is unique across the catalogue', () => {
    const all = KEY_ACTIONS.flatMap((a) => a.defaults);
    expect(new Set(all).size).toBe(all.length);
  });
});

describe('mastering presets', () => {
  it('ships social / podcast / broadcast and clamps any payload into the render ranges', () => {
    expect(MASTERING_PRESETS.map((p) => p.payload.targetLufs)).toEqual([-14, -16, -23]);
    expect(clampMastering({ targetLufs: 0, truePeakDbtp: 3, loudnessRange: 99 })).toEqual({ targetLufs: -5, truePeakDbtp: 0, loudnessRange: 30 });
  });
});

describe('HSL qualifiers and power windows', () => {
  const node = (extra: object) => ({ id: 'n1', primaries: { saturation: 1.4 }, ...extra });

  it('the engine accepts in-range secondaries and refuses hostile ones (same rules as the validator)', () => {
    expect(isGradeQualifier({ hueCenter: 25, hueWidth: 20, satMin: 0.1, satMax: 0.9, softness: 0.1 })).toBe(true);
    expect(isGradeQualifier({ hueCenter: 360 })).toBe(false);
    expect(isGradeQualifier({ hueWidth: 0 })).toBe(false);
    expect(isGradeQualifier({ satMin: 0.9, satMax: 0.1 })).toBe(false);
    expect(isGradeQualifier({ sparkle: 1 })).toBe(false);
    expect(isGradeWindow({ shape: { kind: 'ellipse', x: 0.5, y: 0.5, w: 0.3, h: 0.3 }, offsetX: { value: 0, keys: [{ frame: 0, value: 0.1, interp: 'linear' }, { frame: 10, value: -0.2, interp: 'linear' }] } })).toBe(true);
    expect(isGradeWindow({ shape: { kind: 'star' } })).toBe(false);
    expect(isGradeWindow({ shape: {}, offsetY: { value: 2 } })).toBe(false);

    const d = doc([track(TRACKS.v1, 'video', [media('c', 0, 30)])]);
    const good: GradeStack = { enabled: true, nodes: [node({ qualifier: { hueCenter: 25, hueWidth: 20 } })] };
    expect(apply(d, { type: 'setProperty', itemId: 'c', key: 'grade', value: good }).doc.tracks[0].items[0]).toMatchObject({ grade: good });
    expect(() => apply(d, { type: 'setProperty', itemId: 'c', key: 'grade', value: { nodes: [node({ qualifier: { hueWidth: 400 } })] } as GradeStack })).toThrow(EditEngineError);
  });

  it('the preview never folds a secondary whole-frame (the grade pass applies it, so the parity is faithful)', () => {
    const qualified: GradeStack = { enabled: true, nodes: [node({ qualifier: { hueCenter: 25, hueWidth: 20 } })] };
    const selectAll: GradeStack = { enabled: true, nodes: [node({ qualifier: { hueWidth: 180 } })] };
    expect(isSecondary(qualified.nodes![0])).toBe(true);
    expect(isSecondary(selectAll.nodes![0])).toBe(false);
    expect(isIdentityPreviewGrade(previewGradeOf([qualified]))).toBe(true);
    expect(isIdentityPreviewGrade(previewGradeOf([selectAll]))).toBe(false);
    const clip = { ...media('c', 0, 30), grade: qualified } as MediaClip;
    expect(classifyItem(clip).parity).toBe('faithful');
  });
});

describe('feature tracker', () => {
  /** A textured background with a bright textured square moving (dx, dy) per frame. */
  function movingFrames(count: number, dx: number, dy: number, size = 96): GrayFrame[] {
    const frames: GrayFrame[] = [];
    for (let f = 0; f < count; f++) {
      const data = new Uint8ClampedArray(size * size);
      for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) data[y * size + x] = ((x * 7 + y * 13) % 31) + 20;
      const ox = 20 + f * dx;
      const oy = 30 + f * dy;
      for (let y = 0; y < 16; y++) for (let x = 0; x < 16; x++) data[(oy + y) * size + ox + x] = 120 + ((x * 11 + y * 5) % 9) * 14;
      frames.push({ width: size, height: size, data });
    }
    return frames;
  }

  it('follows a moving feature exactly and turns the track into thinned window offsets', () => {
    const points = trackFeature(movingFrames(12, 3, 1), { x: 20, y: 30, w: 16, h: 16 }, { searchRadius: 6 });
    expect(points.map((p) => [p.x, p.y])).toEqual(Array.from({ length: 12 }, (_, f) => [20 + 3 * f, 30 + f]));
    expect(points.every((p) => !p.lost && p.score > 0.99)).toBe(true);
    const { offsetX, offsetY, lostFrames } = trackToWindowOffsets(points, 96, 96, 100);
    expect(lostFrames).toBe(0);
    // A constant velocity is a straight line: two keys.
    expect(offsetX.keys).toEqual([{ frame: 100, value: 0, interp: 'linear' }, { frame: 111, value: Math.round((33 / 96) * 10000) / 10000, interp: 'linear' }]);
    expect(offsetY.keys?.at(-1)?.value).toBeCloseTo(11 / 96, 3);
  });

  it('holds and reports a frame where the feature vanished, never inventing a position', () => {
    const frames = movingFrames(6, 2, 0);
    frames[3] = { ...frames[3], data: new Uint8ClampedArray(frames[3].data.length).fill(60) };
    const points = trackFeature(frames, { x: 20, y: 30, w: 16, h: 16 }, { searchRadius: 6 });
    expect(points[3].lost).toBe(true);
    expect([points[3].x, points[3].y]).toEqual([points[2].x, points[2].y]);
    expect(points[4].lost).toBe(false);
    expect(trackToWindowOffsets(points, 96, 96, 0).lostFrames).toBe(1);
  });

  it('property: offsets are always inside the validator range', () => {
    fc.assert(
      fc.property(fc.integer({ min: -4, max: 4 }), fc.integer({ min: -2, max: 2 }), (dx, dy) => {
        const points = trackFeature(movingFrames(6, dx, dy), { x: 20, y: 30, w: 16, h: 16 }, { searchRadius: 6 });
        const { offsetX, offsetY } = trackToWindowOffsets(points, 96, 96, 0);
        for (const k of [...(offsetX.keys ?? []), ...(offsetY.keys ?? [])]) expect(Math.abs(k.value)).toBeLessThanOrEqual(1);
      }),
      { numRuns: 20 },
    );
  });
});

describe('review comments', () => {
  it('threads roots by frame with replies oldest first', () => {
    const c = (id: string, frame: number, parentId: string | null, at: string): ReviewComment => ({ id, timelineId: 't', parentId, authorUserId: 'u', authorName: 'a', frame, durationFrames: null, body: id, resolved: false, createdAt: at, updatedAt: at });
    const threads = threadComments([c('r2', 50, null, '2'), c('x', 50, 'r1', '4'), c('r1', 10, null, '1'), c('y', 10, 'r1', '3')]);
    expect(threads.map((t) => [t.root.id, t.replies.map((r) => r.id)])).toEqual([['r1', ['y', 'x']], ['r2', []]]);
  });
});
