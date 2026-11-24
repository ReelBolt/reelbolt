// @vitest-environment jsdom
import { beforeEach, describe, expect, it } from 'vitest';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';
import type { EditOp } from '../../engine';
import type { CompoundClip, EditTimeline } from '../../types';
import { anchorForClips } from '../anchor';
import { entriesAffectedBy } from '../edit-guard';
import { getResultsStore, resetResultsStores, type BackgroundEntry } from '../results-store';

const compound: CompoundClip = { id: 'cmp', type: 'compoundClip', trackId: TRACKS.v1, recordIn: 0, recordOut: 60, sequenceId: 'seq' } as unknown as CompoundClip;
const withCompound = (): EditTimeline => ({ ...doc([track(TRACKS.v1, 'video', [compound])]), sequences: [{ id: 'seq', name: 'S', tracks: [track('00000000-0000-4000-8000-0000000000b1', 'video', [media('inner', 0, 60, 0)])] }] });
const entry = (d: EditTimeline): BackgroundEntry => ({ id: 'r1', tool: 'objectEffect', requestId: 'r1', anchor: anchorForClips(d, ['inner'], { startSec: 0, endSec: null }), intent: {}, state: 'running', createdAt: '2026-01-01T00:00:00Z' });

beforeEach(() => {
  window.localStorage.clear();
  resetResultsStores();
});

describe('guard and mark cover clips nested in a compound', () => {
  it('lifting the compound, or replacing/removing its sequence, asks first', () => {
    const d = withCompound();
    const e = entry(d);
    const none = new Set<string>();
    expect(entriesAffectedBy([{ type: 'lift', itemId: 'cmp' } as EditOp], d, [e], none)).toHaveLength(1);
    expect(entriesAffectedBy([{ type: 'setSequence', sequenceId: 'seq', sequence: undefined } as EditOp], d, [e], none)).toHaveLength(1);
  });
  it('the compound shows busy when a nested clip is', () => {
    const d = withCompound();
    const results = getResultsStore('t');
    results.launch(entry(d));
    expect([...results.busyClipIds(d)].sort()).toEqual(['cmp', 'inner']);
  });
});
