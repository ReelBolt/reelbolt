/** @vitest-environment node */
/**
 * Unit tests for the pure bin-tree arithmetic.
 *
 * The two things worth testing here are the two the UI must never get wrong: a drop that would
 * create a cycle (refused BEFORE any request is issued) and the tree itself (a cycle in the input
 * must not become an infinite render loop).
 */

import { describe, expect, it } from 'vitest';

import {
  BIN_DRAG_MIME,
  CLIP_DRAG_MIME,
  MAX_BIN_NESTING_DEPTH,
  applyDropOptimistically,
  buildBinTree,
  descendantIdsOf,
  readDragPayload,
  resolveBinDrop,
  writeDragPayload,
} from '../bins';
import type { DragDataTransfer } from '../bins';
import { BINS, FIXTURE_IDS } from '../fixtures';
import type { MediaBin } from '../types';

/** The fixture bin with this id, or a loud failure — a missing fixture must not read as a pass. */
function binNamed(id: string): MediaBin {
  const found = BINS.find((bin) => bin.id === id);
  if (!found) throw new Error(`fixture bin ${id} is missing`);
  return found;
}

const rushes = binNamed(FIXTURE_IDS.rushesBin);
const interviews = binNamed(FIXTURE_IDS.interviewsBin);
const bRoll = binNamed(FIXTURE_IDS.bRollBin);
const music = binNamed(FIXTURE_IDS.musicBin);

const CLIP = { kind: 'clip', mediaRef: `file:${FIXTURE_IDS.videoFile}` } as const;
const VIDEO_REF = `file:${FIXTURE_IDS.videoFile}`;

/** A minimal in-memory DataTransfer, since jsdom's is not available in a node environment. */
function fakeDataTransfer(entries: Record<string, string> = {}): DragDataTransfer {
  const store = new Map(Object.entries(entries));
  return {
    setData: (format, data) => void store.set(format, data),
    getData: (format) => store.get(format) ?? '',
  };
}

const chain = (length: number): MediaBin[] =>
  Array.from({ length }, (_, index) => ({
    id: `bin-${index}`,
    name: `Bin ${index}`,
    parentBinId: index === 0 ? null : `bin-${index - 1}`,
    sortOrder: 0,
    itemCount: 0,
  }));

describe('buildBinTree', () => {
  it('is exercised against the fixture tree the other tests assume', () => {
    // A guard on the fixture itself: the nesting, the parent links and the counts the tests below
    // reason about are asserted here, so a fixture edit cannot quietly invalidate them.
    expect(rushes.parentBinId).toBeNull();
    expect(interviews.parentBinId).toBe(rushes.id);
    expect(bRoll.parentBinId).toBe(interviews.id);
    expect(music.parentBinId).toBeNull();
  });

  it('nests children under their parent and assigns depth by level', () => {
    const tree = buildBinTree(BINS);

    expect(tree.map((node) => node.name)).toEqual(['Rushes', 'Music']);
    expect(tree[0].depth).toBe(0);
    expect(tree[0].children.map((node) => node.name)).toEqual(['Interviews']);
    expect(tree[0].children[0].depth).toBe(1);
    expect(tree[0].children[0].children.map((node) => node.name)).toEqual(['B-roll']);
    expect(tree[0].children[0].children[0].depth).toBe(2);
    expect(tree[1].depth).toBe(0);
  });

  it('orders siblings by sortOrder, then name, then id', () => {
    const tree = buildBinTree([
      { id: 'c', name: 'C', parentBinId: null, sortOrder: 2, itemCount: 0 },
      { id: 'b', name: 'B', parentBinId: null, sortOrder: 1, itemCount: 0 },
      { id: 'a', name: 'A', parentBinId: null, sortOrder: 1, itemCount: 0 },
    ]);

    expect(tree.map((node) => node.name)).toEqual(['A', 'B', 'C']);
  });

  it('promotes an orphan to the top level rather than dropping it', () => {
    const tree = buildBinTree([
      { id: 'child', name: 'Orphan', parentBinId: 'missing', sortOrder: 0, itemCount: 0 },
    ]);

    expect(tree).toHaveLength(1);
    expect(tree[0].name).toBe('Orphan');
    expect(tree[0].depth).toBe(0);
  });

  it('promotes a self-parented bin to the top level', () => {
    const tree = buildBinTree([
      { id: 'self', name: 'Self', parentBinId: 'self', sortOrder: 0, itemCount: 0 },
    ]);

    expect(tree).toHaveLength(1);
    expect(tree[0].depth).toBe(0);
  });

  it('resolves a cycle in the input instead of looping forever', () => {
    // A→B→A: whichever of the two is walked first breaks the cycle, and both stay visible.
    const tree = buildBinTree([
      { id: 'a', name: 'A', parentBinId: 'b', sortOrder: 0, itemCount: 0 },
      { id: 'b', name: 'B', parentBinId: 'a', sortOrder: 0, itemCount: 0 },
    ]);

    const flatten = (nodes: ReturnType<typeof buildBinTree>): string[] =>
      nodes.flatMap((node) => [node.id, ...flatten(node.children)]);

    expect(flatten(tree).sort()).toEqual(['a', 'b']);
  });

  it('returns an empty tree for an empty list', () => {
    expect(buildBinTree([])).toEqual([]);
  });

  it('reports every descendant of a bin, excluding the bin itself', () => {
    expect([...descendantIdsOf(BINS, rushes.id)].sort()).toEqual([interviews.id, bRoll.id].sort());
    expect([...descendantIdsOf(BINS, bRoll.id)]).toEqual([]);
  });
});

describe('readDragPayload / writeDragPayload', () => {
  it('round-trips a clip payload', () => {
    const dataTransfer = fakeDataTransfer();
    writeDragPayload(dataTransfer, CLIP);
    expect(readDragPayload(dataTransfer)).toEqual(CLIP);
    expect(dataTransfer.getData(CLIP_DRAG_MIME)).toBe(VIDEO_REF);
  });

  it('round-trips a bin payload', () => {
    const dataTransfer = fakeDataTransfer();
    writeDragPayload(dataTransfer, { kind: 'bin', binId: FIXTURE_IDS.rushesBin });
    expect(readDragPayload(dataTransfer)).toEqual({ kind: 'bin', binId: FIXTURE_IDS.rushesBin });
    expect(dataTransfer.getData(BIN_DRAG_MIME)).toBe(FIXTURE_IDS.rushesBin);
  });

  it('refuses a malformed mediaRef instead of passing it through', () => {
    // A bare GUID is the exact mistake the opaque token exists to make impossible.
    const bare = fakeDataTransfer({ [CLIP_DRAG_MIME]: FIXTURE_IDS.videoFile });
    expect(readDragPayload(bare)).toBeNull();

    const wrongPrefix = fakeDataTransfer({
      [CLIP_DRAG_MIME]: `folder:${FIXTURE_IDS.videoFile}`,
    });
    expect(readDragPayload(wrongPrefix)).toBeNull();
  });

  it('accepts a step_result payload without branching on the prefix', () => {
    const dataTransfer = fakeDataTransfer({
      [CLIP_DRAG_MIME]: `step_result:${FIXTURE_IDS.stepResultRender}`,
    });
    expect(readDragPayload(dataTransfer)).toEqual({
      kind: 'clip',
      mediaRef: `step_result:${FIXTURE_IDS.stepResultRender}`,
    });
  });

  it('returns null for nothing, empty data, or no data transfer at all', () => {
    expect(readDragPayload(null)).toBeNull();
    expect(readDragPayload(undefined)).toBeNull();
    expect(readDragPayload(fakeDataTransfer())).toBeNull();
  });
});

describe('resolveBinDrop', () => {
  it('adds a clip to the bin it is dropped on', () => {
    expect(resolveBinDrop(CLIP, FIXTURE_IDS.rushesBin, BINS)).toEqual({
      kind: 'addItem',
      binId: FIXTURE_IDS.rushesBin,
      mediaRef: VIDEO_REF,
    });
  });

  it('adds a clip to a nested bin', () => {
    expect(resolveBinDrop(CLIP, FIXTURE_IDS.bRollBin, BINS)).toEqual({
      kind: 'addItem',
      binId: FIXTURE_IDS.bRollBin,
      mediaRef: VIDEO_REF,
    });
  });

  it('turns a clip dropped on the root into a removal of every pin it holds', () => {
    const pins = [
      { binId: FIXTURE_IDS.rushesBin, itemId: FIXTURE_IDS.videoPin },
      { binId: FIXTURE_IDS.musicBin, itemId: FIXTURE_IDS.audioPinA },
    ];
    const plan = resolveBinDrop(CLIP, null, BINS, pins);

    expect(plan).toEqual({ kind: 'removeItems', mediaRef: VIDEO_REF, items: pins });
  });

  it('refuses a root drop for a clip the pool cannot name a pin for', () => {
    const plan = resolveBinDrop(CLIP, null, BINS);

    expect(plan.kind).toBe('none');
    expect(plan.kind === 'none' && plan.reason).toContain('not in any bin');
  });

  it('refuses to add a clip the pool already shows as pinned in that bin', () => {
    const plan = resolveBinDrop(CLIP, FIXTURE_IDS.rushesBin, BINS, [
      { binId: FIXTURE_IDS.rushesBin, itemId: FIXTURE_IDS.videoPin },
    ]);

    expect(plan.kind).toBe('none');
    expect(plan.kind === 'none' && plan.reason).toContain('already in this bin');
  });

  it('ignores a malformed pin rather than removing against a guessed id', () => {
    const plan = resolveBinDrop(CLIP, null, BINS, [
      { binId: FIXTURE_IDS.rushesBin, itemId: '' },
    ]);

    expect(plan.kind).toBe('none');
    expect(plan.kind === 'none' && plan.reason).toContain('not in any bin');
  });

  it('refuses dropping a bin on itself', () => {
    const plan = resolveBinDrop({ kind: 'bin', binId: FIXTURE_IDS.rushesBin }, FIXTURE_IDS.rushesBin, BINS);

    expect(plan.kind).toBe('none');
    expect(plan.kind === 'none' && plan.reason).toContain('inside itself');
  });

  it('refuses a move that would create a cycle, issuing no call', () => {
    // Rushes moved inside its own grandchild.
    const plan = resolveBinDrop({ kind: 'bin', binId: FIXTURE_IDS.rushesBin }, FIXTURE_IDS.bRollBin, BINS);

    expect(plan.kind).toBe('none');
    expect(plan.kind === 'none' && plan.reason).toContain('own sub-bin');
  });

  it('refuses a move into the bin that already holds it', () => {
    const plan = resolveBinDrop(
      { kind: 'bin', binId: FIXTURE_IDS.interviewsBin },
      FIXTURE_IDS.rushesBin,
      BINS,
    );

    expect(plan.kind).toBe('none');
    expect(plan.kind === 'none' && plan.reason).toContain('already in that bin');
  });

  it('reparents a nested bin to the root', () => {
    expect(resolveBinDrop({ kind: 'bin', binId: FIXTURE_IDS.interviewsBin }, null, BINS)).toEqual({
      kind: 'moveBin',
      binId: FIXTURE_IDS.interviewsBin,
      parentBinId: null,
    });
  });

  it('refuses reparenting a bin that is already at the top level', () => {
    const plan = resolveBinDrop({ kind: 'bin', binId: FIXTURE_IDS.rushesBin }, null, BINS);
    expect(plan.kind).toBe('none');
    expect(plan.kind === 'none' && plan.reason).toContain('already at the top level');
  });

  it('moves a bin into a sibling bin', () => {
    expect(
      resolveBinDrop({ kind: 'bin', binId: FIXTURE_IDS.interviewsBin }, FIXTURE_IDS.musicBin, BINS),
    ).toEqual({
      kind: 'moveBin',
      binId: FIXTURE_IDS.interviewsBin,
      parentBinId: FIXTURE_IDS.musicBin,
    });
  });

  it('refuses any drop naming a bin that is not in the list', () => {
    expect(resolveBinDrop(CLIP, 'ghost', BINS).kind).toBe('none');
    expect(resolveBinDrop({ kind: 'bin', binId: 'ghost' }, FIXTURE_IDS.musicBin, BINS).kind).toBe(
      'none',
    );
  });

  it('refuses an empty drop', () => {
    expect(resolveBinDrop(null, FIXTURE_IDS.musicBin, BINS).kind).toBe('none');
  });

  it('refuses a move that would push nesting past the server bound', () => {
    // A chain at the maximum depth, then a second deep chain to hang under its deepest bin.
    const target = chain(MAX_BIN_NESTING_DEPTH);
    const mover: MediaBin = {
      id: 'mover',
      name: 'Mover',
      parentBinId: null,
      sortOrder: 9,
      itemCount: 0,
    };
    const child: MediaBin = {
      id: 'mover-child',
      name: 'Mover child',
      parentBinId: 'mover',
      sortOrder: 0,
      itemCount: 0,
    };
    const bins = [...target, mover, child];
    const deepest = `bin-${MAX_BIN_NESTING_DEPTH - 1}`;

    const plan = resolveBinDrop({ kind: 'bin', binId: 'mover' }, deepest, bins);
    expect(plan.kind).toBe('none');
    expect(plan.kind === 'none' && plan.reason).toContain('deeper');
  });
});

describe('applyDropOptimistically', () => {
  it('bumps the target bin count by one for an add', () => {
    const next = applyDropOptimistically(BINS, {
      kind: 'addItem',
      binId: FIXTURE_IDS.rushesBin,
      mediaRef: VIDEO_REF,
    });

    expect(next.find((bin) => bin.id === FIXTURE_IDS.rushesBin)?.itemCount).toBe(
      rushes.itemCount + 1,
    );
    // Nothing else moves.
    expect(next.find((bin) => bin.id === FIXTURE_IDS.musicBin)?.itemCount).toBe(music.itemCount);
  });

  it('reparents the moved bin and touches nothing else', () => {
    const next = applyDropOptimistically(BINS, {
      kind: 'moveBin',
      binId: FIXTURE_IDS.interviewsBin,
      parentBinId: null,
    });

    expect(next.find((bin) => bin.id === interviews.id)?.parentBinId).toBeNull();
    expect(next).toHaveLength(BINS.length);
  });

  it('leaves the list untouched for a refusal', () => {
    expect(applyDropOptimistically(BINS, { kind: 'none', reason: 'no' })).toEqual(BINS);
  });

  it('does not mutate the input list', () => {
    const before = JSON.stringify(BINS);
    applyDropOptimistically(BINS, { kind: 'addItem', binId: FIXTURE_IDS.rushesBin, mediaRef: VIDEO_REF });
    expect(JSON.stringify(BINS)).toBe(before);
  });
});
