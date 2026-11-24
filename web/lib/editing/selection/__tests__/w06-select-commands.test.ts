/** @vitest-environment node */
/**
 * W0.6 (P3-SEL-03, eng D17): select forward / backward from the playhead and select all on a track
 * are reachable — one command function behind both the Select menu and the keymap.
 */
import { describe, expect, it } from 'vitest';
import { runSelectCommand } from '../selection';
import { buildKeymap, KEY_ACTIONS } from '../../keymap';
import { standardDoc, TRACKS } from '../../__tests__/fixtures';

const d = standardDoc();

describe('select commands', () => {
  it('forward takes every item that ends after the playhead (a clip under it included), on every track', () => {
    const ids = runSelectCommand(d, { playheadFrame: 100, selection: [], targetedTrackIds: [] }, 'forward');
    expect(ids).toEqual(expect.arrayContaining(['c2', 'c3']));
    expect(ids).not.toContain('c1');
  });

  it('backward takes every item that starts before the playhead', () => {
    const ids = runSelectCommand(d, { playheadFrame: 100, selection: [], targetedTrackIds: [] }, 'backward');
    expect(ids).toEqual(expect.arrayContaining(['c1', 'c2']));
    expect(ids).not.toContain('c3');
  });

  it('all on track uses the selected item\'s track, else the first targeted track', () => {
    expect(runSelectCommand(d, { playheadFrame: 0, selection: ['c2'], targetedTrackIds: [] }, 'track')).toEqual(['c1', 'c2', 'c3']);
    expect(runSelectCommand(d, { playheadFrame: 0, selection: [], targetedTrackIds: [TRACKS.v1] }, 'track')).toEqual(['c1', 'c2', 'c3']);
    expect(runSelectCommand(d, { playheadFrame: 0, selection: [], targetedTrackIds: [] }, 'track')).toEqual([]);
  });

  it('all and none', () => {
    expect(runSelectCommand(d, { playheadFrame: 0, selection: [], targetedTrackIds: [] }, 'all')).toHaveLength(6);
    expect(runSelectCommand(d, { playheadFrame: 0, selection: ['c1'], targetedTrackIds: [] }, 'none')).toEqual([]);
  });

  it('the keymap has actions for forward, backward and all on track', () => {
    const map = buildKeymap({});
    expect(map.get('A')).toBe('selectForward');
    expect(map.get('Shift+A')).toBe('selectBackward');
    expect(map.get('Alt+A')).toBe('selectAllOnTrack');
    expect(KEY_ACTIONS.map((a) => a.id)).toEqual(expect.arrayContaining(['selectForward', 'selectBackward', 'selectAllOnTrack']));
  });
});
