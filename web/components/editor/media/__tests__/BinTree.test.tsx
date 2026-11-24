// @vitest-environment jsdom
/* eslint @typescript-eslint/no-unused-vars: ["warn", { "argsIgnorePattern": "^_" }] */
/**
 * The bin tree: the real membership listing, the counts derived from it, and every write the
 * contract reaches through drag-and-drop.
 *
 * The membership gap this file used to pin as a LIMITATION is now closed, so the assertions moved
 * with it:
 *
 *   - counts are DERIVED from the pool's own pin lists (`countsByBinId`), not from the server's
 *     `itemCount` and not from an optimistic increment;
 *   - selecting a bin loads `GET .../bins/{binId}/items` and renders what it really holds, including
 *     a DANGLING pin whose source is gone;
 *   - every listed pin can be REMOVED, through `DELETE .../bins/{binId}/items/{itemId}` — the write
 *     that was unreachable while no listing supplied an `itemId`;
 *   - dropping a clip on the root removes it from every bin it is in;
 *   - a drop that would create a cycle still issues **no call at all** (decided client-side);
 *   - a failed write **rolls the optimistic change back**.
 *
 * Note: this repo does not install `@testing-library/jest-dom`, so assertions go through
 * `textContent`/`getAttribute` helpers rather than `toHaveTextContent`/`toHaveAttribute`.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, cleanup, fireEvent, waitFor, act } from '@testing-library/react';
import { useState } from 'react';
import { MantineProvider } from '@mantine/core';

import { BinTree } from '../BinTree';
import { BIN_DRAG_MIME, CLIP_DRAG_MIME } from '@/lib/editing/media/bins';
import type { DragDataTransfer } from '@/lib/editing/media/bins';
import {
  BINS,
  BIN_ITEMS_EMPTY,
  BIN_ITEMS_MUSIC,
  BIN_ITEMS_RUSHES,
  FIXTURE_IDS,
} from '@/lib/editing/media/fixtures';
import type { MediaBin, MediaBinMembership } from '@/lib/editing/media/types';

const { rushesBin, interviewsBin, bRollBin, musicBin } = FIXTURE_IDS;
const PROJECT_ID = '00000000-0000-4000-8000-000000000009';
const VIDEO_REF = `file:${FIXTURE_IDS.videoFile}`;

// ─────────────────────────────────────────────────────────────────────────────
// The API client is mocked: every assertion here is about WHICH call is issued.
// ─────────────────────────────────────────────────────────────────────────────

const api = vi.hoisted(() => ({
  createBin: vi.fn(),
  updateBin: vi.fn(),
  deleteBin: vi.fn(),
  addBinItem: vi.fn(),
  fetchBinItems: vi.fn(),
  removeBinItem: vi.fn(),
}));

const toasts = vi.hoisted(() => ({ show: vi.fn(), hide: vi.fn() }));
vi.mock('@mantine/notifications', () => ({ notifications: toasts }));

vi.mock('@/lib/editing/api/media', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/editing/api/media')>();
  return { ...actual, ...api };
});

// Mantine's Tooltip/ActionIcon need the observer polyfill under jsdom.
vi.stubGlobal(
  'ResizeObserver',
  class ResizeObserver {
    constructor(_callback: ResizeObserverCallback) {}
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);

Element.prototype.scrollIntoView = vi.fn();

/** A minimal DataTransfer stand-in; jsdom does not implement drag payloads. */
function fakeDataTransfer(initial: Record<string, string> = {}): DragDataTransfer {
  const store = new Map(Object.entries(initial));
  return {
    setData: (format, data) => void store.set(format, data),
    getData: (format) => store.get(format) ?? '',
  };
}

/**
 * The counts the parent derives from the pool's own pin lists. `VIDEO_FILE_ITEM` is pinned in
 * Rushes, `AUDIO_FILE_ITEM` in Music and in Rushes — so Rushes has 2 and Music has 1, which is
 * deliberately NOT what `BINS[].itemCount` says (12 and 2). A test that still saw 12 would be
 * reading the placeholder this file exists to replace.
 */
const DERIVED_COUNTS: Record<string, number> = {
  [rushesBin]: 2,
  [musicBin]: 1,
};

/** The pins the parent can resolve for a dragged clip — what a root drop removes. */
const DRAGGED_PINS: MediaBinMembership[] = [
  { binId: rushesBin, itemId: FIXTURE_IDS.videoPin },
  { binId: musicBin, itemId: FIXTURE_IDS.audioPinA },
];

/**
 * A controlled harness: the tree is stateless about bins, exactly as in production, so an
 * optimistic update is observable by re-rendering with the list the component reported. It also
 * records the membership callbacks, so a pin's id can be asserted without reaching into the tree.
 */
function Harness({
  initial = BINS,
  selected = null,
  counts = DERIVED_COUNTS,
  pins = [],
  onMembershipRemoved,
  onMembershipAdded,
}: {
  initial?: MediaBin[];
  selected?: string | null;
  counts?: Record<string, number>;
  pins?: MediaBinMembership[];
  onMembershipRemoved?: (mediaRef: string, itemId: string) => void;
  onMembershipAdded?: (mediaRef: string, membership: MediaBinMembership) => void;
}) {
  const [bins, setBins] = useState<MediaBin[]>(initial);
  const [selectedBinId, setSelectedBinId] = useState<string | null>(selected);

  return (
    <BinTree
      projectId={PROJECT_ID}
      bins={bins}
      onBinsChange={setBins}
      selectedBinId={selectedBinId}
      onSelect={setSelectedBinId}
      countsByBinId={counts}
      pinsFor={() => pins}
      onMembershipAdded={onMembershipAdded}
      onMembershipRemoved={onMembershipRemoved}
    />
  );
}

const renderTree = (initial?: MediaBin[]) =>
  render(
    <MantineProvider>
      <Harness initial={initial} />
    </MantineProvider>,
  );

const dropTarget = (binId: string) => screen.getByTestId(`bin-drop-target-${binId}`);

/** Trimmed text of a node, or an empty string — never `undefined`. */
const textOf = (node: Element | null): string => (node?.textContent ?? '').trim();

/** The indentation React put on a bin row, which is how depth is expressed. */
const indentOf = (binId: string): string =>
  screen.getByTestId(`bin-name-${binId}`).closest('div')?.style.marginLeft ?? '';

const countOf = (binId: string) => (screen.getByTestId(`bin-count-${binId}`).textContent ?? '').trim();

const message = () => (screen.queryByTestId('bin-message')?.textContent ?? '').trim();

/** Drops a dragged payload onto a target. */
async function drop(payload: Record<string, string>, target: HTMLElement) {
  await act(async () => {
    fireEvent.drop(target, { dataTransfer: fakeDataTransfer(payload) });
  });
}

describe('BinTree', () => {
  beforeEach(() => {
    global.matchMedia = (query: string): MediaQueryList =>
      ({
        matches: false,
        media: query,
        onchange: null,
        addListener: () => {},
        removeListener: () => {},
        addEventListener: () => {},
        removeEventListener: () => {},
        dispatchEvent: () => true,
      }) as MediaQueryList;

    api.createBin.mockReset();
    api.updateBin.mockReset();
    api.deleteBin.mockReset();
    api.addBinItem.mockReset();
    api.fetchBinItems.mockReset();
    api.removeBinItem.mockReset();
    api.updateBin.mockResolvedValue({} as MediaBin);
    api.deleteBin.mockResolvedValue(undefined);
    api.addBinItem.mockResolvedValue({ id: 'item-1' });
    api.removeBinItem.mockResolvedValue(undefined);
    api.fetchBinItems.mockImplementation((_projectId: string, binId: string) =>
      Promise.resolve(
        binId === rushesBin
          ? BIN_ITEMS_RUSHES
          : binId === musicBin
            ? BIN_ITEMS_MUSIC
            : BIN_ITEMS_EMPTY,
      ),
    );
  });

  afterEach(() => cleanup());

  // ───────────────────────────────────────────────────────────────────────────
  // Rendering
  // ───────────────────────────────────────────────────────────────────────────

  it('renders the nested tree, expanded, in the server order', () => {
    renderTree();

    const names = screen
      .getAllByTestId('bin-node')
      .map((node) => (node.getAttribute('data-bin-id') ?? ''));
    expect(names).toEqual([rushesBin, interviewsBin, bRollBin, musicBin]);

    // Each row names its bin and states its depth through indentation.
    expect(textOf(screen.getByTestId(`bin-name-${rushesBin}`))).toBe('Rushes');
    expect(textOf(screen.getByTestId(`bin-name-${bRollBin}`))).toBe('B-roll');
    expect(indentOf(rushesBin)).toBe('0px');
    expect(indentOf(interviewsBin)).toBe('14px');
    expect(indentOf(bRollBin)).toBe('28px');
  });

  it('renders counts DERIVED from the pool pins, not from the server itemCount', () => {
    renderTree();

    // The fixtures' MediaBin.itemCount says 12 and 2. The derived counts say 2 and 1, and the
    // derived number is the one a user can verify against the pool in front of them.
    expect(countOf(rushesBin)).toBe('2 items');
    expect(countOf(musicBin)).toBe('1 item');
    expect(countOf(bRollBin)).toBe('0 items');
    // A bin with no pins at all is not in the map, and still renders as zero rather than blank.
    expect(countOf(interviewsBin)).toBe('0 items');

    // Singular, for the one-item case.
    expect(countOf(musicBin)).not.toContain('1 items');
  });

  it('states what the count means in its own copy, and no longer claims membership is unreadable', () => {
    renderTree();

    const copy = screen.getByTestId('bin-tree').textContent ?? '';
    expect(copy).toContain('number of sources in the pool pinned to that bin');
    expect(copy).toContain('list what it holds');
    expect(copy).not.toContain('no bin membership listing');
    expect(copy).not.toContain('not which ones');
  });

  it('collapses and expands a bin with children', () => {
    renderTree();
    expect(screen.getAllByTestId('bin-node')).toHaveLength(4);

    fireEvent.click(screen.getByRole('button', { name: 'Collapse Rushes' }));
    expect(screen.getAllByTestId('bin-node')).toHaveLength(2);

    fireEvent.click(screen.getByRole('button', { name: 'Expand Rushes' }));
    expect(screen.getAllByTestId('bin-node')).toHaveLength(4);
  });

  // ───────────────────────────────────────────────────────────────────────────
  // Adding a clip: the one membership write that is actually reachable
  // ───────────────────────────────────────────────────────────────────────────

  it('adds a dropped clip to a bin and reports the created pin to the parent', async () => {
    const added: { mediaRef: string; membership: MediaBinMembership }[] = [];
    render(
      <MantineProvider>
        <Harness
          onMembershipAdded={(mediaRef, membership) => added.push({ mediaRef, membership })}
        />
      </MantineProvider>,
    );

    await drop({ [CLIP_DRAG_MIME]: VIDEO_REF }, dropTarget(bRollBin));

    expect(api.addBinItem).toHaveBeenCalledTimes(1);
    expect(api.addBinItem).toHaveBeenCalledWith(PROJECT_ID, bRollBin, VIDEO_REF);
    // The pin's real id comes back from the server, which is what makes a later removal possible.
    expect(added).toEqual([
      { mediaRef: VIDEO_REF, membership: { binId: bRollBin, itemId: 'item-1' } },
    ]);
    expect(message()).toBe('');
  });

  it('refuses to re-add a clip the pool says is already in that bin, issuing no call', async () => {
    render(
      <MantineProvider>
        <Harness pins={[{ binId: rushesBin, itemId: FIXTURE_IDS.videoPin }]} />
      </MantineProvider>,
    );

    await drop({ [CLIP_DRAG_MIME]: VIDEO_REF }, dropTarget(rushesBin));

    expect(api.addBinItem).not.toHaveBeenCalled();
    expect(message()).toContain('already in this bin');
  });

  it('adds a dropped clip to a nested bin', async () => {
    renderTree();

    await drop({ [CLIP_DRAG_MIME]: VIDEO_REF }, dropTarget(bRollBin));

    expect(api.addBinItem).toHaveBeenCalledWith(PROJECT_ID, bRollBin, VIDEO_REF);
  });

  it('accepts a step_result mediaRef without branching on its prefix', async () => {
    const stepRef = `step_result:${FIXTURE_IDS.stepResultRender}`;
    renderTree();

    await drop({ [CLIP_DRAG_MIME]: stepRef }, dropTarget(rushesBin));

    expect(api.addBinItem).toHaveBeenCalledWith(PROJECT_ID, rushesBin, stepRef);
  });

  it('refuses a malformed drag payload without issuing a call', async () => {
    renderTree();

    // A bare GUID is not a mediaRef.
    await drop({ [CLIP_DRAG_MIME]: FIXTURE_IDS.videoFile }, dropTarget(rushesBin));

    expect(api.addBinItem).not.toHaveBeenCalled();
    expect(message()).toContain('Nothing to drop');
  });

  it('reports a failed write and issues no membership callback', async () => {
    const added: unknown[] = [];
    api.addBinItem.mockRejectedValue(new Error('Bin is full'));
    render(
      <MantineProvider>
        <Harness onMembershipAdded={(...args) => added.push(args)} />
      </MantineProvider>,
    );

    await drop({ [CLIP_DRAG_MIME]: VIDEO_REF }, dropTarget(rushesBin));

    await waitFor(() => expect(message()).toContain('Bin is full'));
    expect(added).toEqual([]);
  });

  // ───────────────────────────────────────────────────────────────────────────
  // Refusals — decided, and asserted, before any request is issued
  // ───────────────────────────────────────────────────────────────────────────

  it('removes a clip from every bin when it is dropped on the root', async () => {
    const removed: { mediaRef: string; itemId: string }[] = [];
    render(
      <MantineProvider>
        <Harness
          pins={DRAGGED_PINS}
          onMembershipRemoved={(mediaRef, itemId) => removed.push({ mediaRef, itemId })}
        />
      </MantineProvider>,
    );

    await drop({ [CLIP_DRAG_MIME]: VIDEO_REF }, screen.getByTestId('bin-root-drop-target'));

    expect(api.removeBinItem).toHaveBeenCalledTimes(2);
    expect(api.removeBinItem).toHaveBeenCalledWith(PROJECT_ID, rushesBin, FIXTURE_IDS.videoPin);
    expect(api.removeBinItem).toHaveBeenCalledWith(PROJECT_ID, musicBin, FIXTURE_IDS.audioPinA);
    expect(removed).toEqual([
      { mediaRef: VIDEO_REF, itemId: FIXTURE_IDS.videoPin },
      { mediaRef: VIDEO_REF, itemId: FIXTURE_IDS.audioPinA },
    ]);
    expect(message()).toBe('');
  });

  it('refuses a root drop when the pool cannot name any pin, issuing no call', async () => {
    render(
      <MantineProvider>
        <Harness pins={[]} />
      </MantineProvider>,
    );

    await drop({ [CLIP_DRAG_MIME]: VIDEO_REF }, screen.getByTestId('bin-root-drop-target'));

    expect(api.removeBinItem).not.toHaveBeenCalled();
    expect(message()).toContain('not in any bin');
  });

  it('refuses dropping a bin on itself, issuing no call', async () => {
    renderTree();

    await drop({ [BIN_DRAG_MIME]: rushesBin }, dropTarget(rushesBin));

    expect(api.updateBin).not.toHaveBeenCalled();
    expect(message()).toContain('inside itself');
  });

  it('refuses a move that would create a cycle, issuing no call', async () => {
    renderTree();

    // Rushes dropped inside its own grandchild.
    await drop({ [BIN_DRAG_MIME]: rushesBin }, dropTarget(bRollBin));

    expect(api.updateBin).not.toHaveBeenCalled();
    expect(message()).toContain('own sub-bin');
  });

  it('refuses moving a bin into the bin that already holds it', async () => {
    renderTree();

    await drop({ [BIN_DRAG_MIME]: interviewsBin }, dropTarget(rushesBin));

    expect(api.updateBin).not.toHaveBeenCalled();
    expect(message()).toContain('already in that bin');
  });

  // ───────────────────────────────────────────────────────────────────────────
  // Reparenting: PATCH with parentBinId is reachable
  // ───────────────────────────────────────────────────────────────────────────

  it('reparents a nested bin to the root through the write endpoint', async () => {
    renderTree();

    await drop({ [BIN_DRAG_MIME]: interviewsBin }, screen.getByTestId('bin-root-drop-target'));

    expect(api.updateBin).toHaveBeenCalledWith(PROJECT_ID, interviewsBin, { parentBinId: null });

    // It is now a top-level bin: no indentation, and still rendered.
    await waitFor(() => expect(indentOf(interviewsBin)).toBe('0px'));
    expect(screen.getByTestId(`bin-drop-target-${interviewsBin}`)).toBeTruthy();
    // Its own child follows it, still one level down.
    expect(indentOf(bRollBin)).toBe('14px');
  });

  it('moves a bin into another bin, optimistically', async () => {
    renderTree();

    await drop({ [BIN_DRAG_MIME]: interviewsBin }, dropTarget(musicBin));

    expect(api.updateBin).toHaveBeenCalledWith(PROJECT_ID, interviewsBin, {
      parentBinId: musicBin,
    });
  });

  it('rolls a reparent back when the write fails', async () => {
    api.updateBin.mockRejectedValue(new Error('Depth limit'));
    renderTree();

    await drop({ [BIN_DRAG_MIME]: interviewsBin }, screen.getByTestId('bin-root-drop-target'));

    await waitFor(() => expect(message()).toContain('Depth limit'));
    // Interviews is still under Rushes: collapsing Rushes must hide it again.
    fireEvent.click(screen.getByRole('button', { name: 'Collapse Rushes' }));
    expect(screen.queryByTestId(`bin-drop-target-${interviewsBin}`)).toBeNull();
  });

  // ───────────────────────────────────────────────────────────────────────────
  // Create / rename / delete
  // ───────────────────────────────────────────────────────────────────────────

  it('creates a bin and shows it with an empty count', async () => {
    const created: MediaBin = {
      id: 'new-bin',
      name: 'Selects',
      parentBinId: null,
      sortOrder: 9,
      itemCount: 0,
    };
    api.createBin.mockResolvedValue(created);
    renderTree();

    fireEvent.change(screen.getByLabelText('New bin name'), { target: { value: 'Selects' } });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /Add bin/ }));
    });

    expect(api.createBin).toHaveBeenCalledWith(PROJECT_ID, { name: 'Selects' });
    await waitFor(() => expect(countOf('new-bin')).toBe('0 items'));
  });

  it('refuses an empty bin name without issuing a call', async () => {
    renderTree();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /Add bin/ }));
    });

    expect(api.createBin).not.toHaveBeenCalled();
    expect(message()).toContain('name is required');
  });

  it('renames a bin through the write endpoint', async () => {
    renderTree();

    fireEvent.click(screen.getByRole('button', { name: 'Actions for Music' }));
    fireEvent.click(await screen.findByTestId(`bin-action-rename-${musicBin}`));
    fireEvent.change(screen.getByLabelText('New name for Music'), { target: { value: 'Score' } });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    });

    expect(api.updateBin).toHaveBeenCalledWith(PROJECT_ID, musicBin, { name: 'Score' });
    await waitFor(() => expect(screen.getByText('Score')).toBeDefined());
  });

  it('R05.3 "New sub-bin" from the bin menu creates the next bin inside it', async () => {
    api.createBin.mockResolvedValue({ id: 'new-1', name: 'Cutaways', parentBinId: musicBin, sortOrder: 9, itemCount: 0 } satisfies MediaBin);
    renderTree();
    fireEvent.click(screen.getByRole('button', { name: 'Actions for Music' }));
    fireEvent.click(await screen.findByTestId(`bin-action-subbin-${musicBin}`));
    expect(screen.getByTestId('new-bin-parent').textContent).toContain('Music');
    fireEvent.change(screen.getByLabelText('New bin name'), { target: { value: 'Cutaways' } });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /Add bin/ }));
    });
    expect(api.createBin).toHaveBeenCalledWith(PROJECT_ID, { name: 'Cutaways', parentBinId: musicBin });
  });

  it('R05.3 "Move to" in the bin menu reparents without a drag, and never lists a bin\'s own subtree', async () => {
    renderTree();
    fireEvent.click(screen.getByRole('button', { name: 'Actions for Rushes' }));
    expect(await screen.findByTestId(`bin-action-move-${rushesBin}-${musicBin}`)).toBeTruthy();
    expect(screen.queryByTestId(`bin-action-move-${rushesBin}-${interviewsBin}`)).toBeNull();
    await act(async () => {
      fireEvent.click(screen.getByTestId(`bin-action-move-${rushesBin}-${musicBin}`));
    });
    expect(api.updateBin).toHaveBeenCalledWith(PROJECT_ID, rushesBin, { parentBinId: musicBin });
  });

  it('deletes a bin together with its sub-bins', async () => {
    renderTree();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Actions for Rushes' }));
    });
    await act(async () => {
      fireEvent.click(await screen.findByTestId(`bin-action-delete-${rushesBin}`));
    });
    await act(async () => {
      fireEvent.click(await screen.findByTestId('bin-delete-confirm'));
    });

    expect(api.deleteBin).toHaveBeenCalledWith(PROJECT_ID, rushesBin);
    await waitFor(() => expect(screen.queryByTestId(`bin-drop-target-${rushesBin}`)).toBeNull());
    // The subtree went with it.
    expect(screen.queryByTestId(`bin-drop-target-${interviewsBin}`)).toBeNull();
    expect(screen.queryByTestId(`bin-drop-target-${bRollBin}`)).toBeNull();
    // An unrelated top-level bin survives.
    expect(screen.getByTestId(`bin-drop-target-${musicBin}`)).toBeTruthy();
  });

  it('R05.3 shows "Bin deleted" with Undo, and Undo re-creates the bin, its sub-bin and its pins', async () => {
    toasts.show.mockReset();
    let nextId = 0;
    api.createBin.mockImplementation((_p: string, request: { name: string; parentBinId?: string | null }) =>
      Promise.resolve({ id: `new-${(nextId += 1)}`, name: request.name, parentBinId: request.parentBinId ?? null, sortOrder: 0, itemCount: 0 }),
    );
    renderTree();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Actions for Rushes' }));
    });
    await act(async () => {
      fireEvent.click(await screen.findByTestId(`bin-action-delete-${rushesBin}`));
    });
    await act(async () => {
      fireEvent.click(await screen.findByTestId('bin-delete-confirm'));
    });
    await waitFor(() => expect(toasts.show).toHaveBeenCalledTimes(1));
    expect(api.deleteBin).toHaveBeenCalledWith(PROJECT_ID, rushesBin);
    expect(screen.queryByTestId(`bin-drop-target-${rushesBin}`)).toBeNull();

    const toast = render(<MantineProvider>{toasts.show.mock.calls[0][0].message}</MantineProvider>);
    expect(toast.container.textContent).toContain('Bin deleted.');
    await act(async () => {
      fireEvent.click(toast.getByTestId('bin-undo-delete'));
    });

    await waitFor(() => expect(api.createBin.mock.calls.length).toBeGreaterThanOrEqual(3));
    // The parent is re-created before its children, and the children hang off the NEW parent id.
    expect(api.createBin.mock.calls[0][1].name).toBe('Rushes');
    expect(api.createBin.mock.calls[1][1].parentBinId).toBe('new-1');
    expect(api.addBinItem).toHaveBeenCalledWith(PROJECT_ID, 'new-1', BIN_ITEMS_RUSHES[0].mediaRef);
    await waitFor(() => expect(screen.getByTestId(`bin-drop-target-new-1`)).toBeTruthy());
  });

  it('R04.3 asks before deleting, names the bin and its contents, and Cancel keeps it', async () => {
    renderTree();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Actions for Rushes' }));
    });
    await act(async () => {
      fireEvent.click(await screen.findByTestId(`bin-action-delete-${rushesBin}`));
    });
    const text = (await screen.findByTestId('bin-delete-text')).textContent ?? '';
    expect(text).toContain('Rushes');
    expect(text).toMatch(/\d+ sub-bins?/);
    expect(text).toMatch(/\d+ clips?/);
    expect(text).toContain('Clips stay in your project; only the grouping goes.');
    expect(api.deleteBin).not.toHaveBeenCalled();

    await act(async () => {
      fireEvent.click(screen.getByTestId('bin-delete-cancel'));
    });
    expect(api.deleteBin).not.toHaveBeenCalled();
    expect(screen.getByTestId(`bin-drop-target-${rushesBin}`)).toBeTruthy();
  });

  it('rolls a delete back when the write fails', async () => {
    api.deleteBin.mockRejectedValue(new Error('Bin is locked'));
    renderTree();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Actions for Rushes' }));
    });
    await act(async () => {
      fireEvent.click(await screen.findByTestId(`bin-action-delete-${rushesBin}`));
    });
    await act(async () => {
      fireEvent.click(await screen.findByTestId('bin-delete-confirm'));
    });

    await waitFor(() => expect(message()).toContain('Bin is locked'));
    expect(screen.getByTestId(`bin-drop-target-${rushesBin}`)).toBeTruthy();
    expect(screen.getByTestId(`bin-drop-target-${interviewsBin}`)).toBeTruthy();
  });

  it('renders an empty bin list without failing', () => {
    renderTree([]);

    expect(screen.queryAllByTestId('bin-node')).toHaveLength(0);
    expect(screen.getByTestId('bin-root-drop-target')).toBeTruthy();
  });

  it('drops a bin it cannot find in the list without issuing a call', async () => {
    renderTree();

    await drop({ [BIN_DRAG_MIME]: 'ghost-bin' }, dropTarget(musicBin));

    expect(api.updateBin).not.toHaveBeenCalled();
    expect(message()).toContain('no longer exists');
  });

  // ───────────────────────────────────────────────────────────────────────────
  // The real membership listing, and removal
  // ───────────────────────────────────────────────────────────────────────────

  it('lists what the selected bin really holds, including a dangling pin', async () => {
    render(
      <MantineProvider>
        <Harness selected={rushesBin} />
      </MantineProvider>,
    );

    expect(api.fetchBinItems).toHaveBeenCalledWith(PROJECT_ID, rushesBin);

    await waitFor(() => expect(screen.getByTestId('bin-items-list')).toBeTruthy());

    const names = screen
      .getAllByTestId(/^bin-item-name-/, { exact: false })
      .map((node) => (node.textContent ?? '').trim());
    expect(names).toContain('b-roll-city.mp4');
    expect(names).toContain('interview-room-tone.wav');

    // The dangling pin is SHOWN, with its mediaRef as the only identifier the server still knows,
    // and is labelled — never silently dropped, which would strand it forever.
    const dangling = screen.getByTestId(`bin-item-${FIXTURE_IDS.danglingPin}`);
    const danglingText = dangling.textContent ?? '';
    expect(danglingText).toContain(FIXTURE_IDS.imageFile);
    expect(screen.getByTestId(`bin-item-dangling-${FIXTURE_IDS.danglingPin}`)).toBeTruthy();
  });

  it('re-reads the listing when the selected bin changes', async () => {
    render(
      <MantineProvider>
        <Harness selected={rushesBin} />
      </MantineProvider>,
    );
    await waitFor(() => expect(screen.getByTestId('bin-items-list')).toBeTruthy());

    await act(async () => {
      fireEvent.click(screen.getByTestId(`bin-name-${musicBin}`));
    });

    expect(api.fetchBinItems).toHaveBeenCalledWith(PROJECT_ID, musicBin);
    await waitFor(() =>
      expect(screen.getByTestId(`bin-item-${FIXTURE_IDS.audioPinA}`)).toBeTruthy(),
    );
    expect(screen.queryByTestId(`bin-item-${FIXTURE_IDS.danglingPin}`)).toBeNull();
  });

  it('states an empty bin rather than rendering nothing', async () => {
    render(
      <MantineProvider>
        <Harness selected={bRollBin} />
      </MantineProvider>,
    );

    await waitFor(() =>
      expect(screen.getByTestId('bin-items-empty').textContent).toContain('holds nothing'),
    );
  });

  it('reports a listing that could not be read, without claiming the bin is empty', async () => {
    api.fetchBinItems.mockRejectedValue(new Error('Listing exploded'));
    render(
      <MantineProvider>
        <Harness selected={rushesBin} />
      </MantineProvider>,
    );

    await waitFor(() =>
      expect(screen.getByTestId('bin-items-error').textContent).toContain('Listing exploded'),
    );
    expect(screen.queryByTestId('bin-items-empty')).toBeNull();
  });

  it('removes a pin through DELETE, and tells the parent which id went', async () => {
    const removed: { mediaRef: string; itemId: string }[] = [];
    render(
      <MantineProvider>
        <Harness
          selected={rushesBin}
          onMembershipRemoved={(mediaRef, itemId) => removed.push({ mediaRef, itemId })}
        />
      </MantineProvider>,
    );
    await waitFor(() => expect(screen.getByTestId('bin-items-list')).toBeTruthy());

    await act(async () => {
      fireEvent.click(screen.getByTestId(`bin-item-remove-${FIXTURE_IDS.videoPin}`));
    });

    expect(api.removeBinItem).toHaveBeenCalledWith(PROJECT_ID, rushesBin, FIXTURE_IDS.videoPin);
    expect(removed).toEqual([
      { mediaRef: `file:${FIXTURE_IDS.videoFile}`, itemId: FIXTURE_IDS.videoPin },
    ]);
    // Optimistically gone from the list, so the row cannot be pressed twice.
    await waitFor(() => expect(screen.queryByTestId(`bin-item-${FIXTURE_IDS.videoPin}`)).toBeNull());
  });

  it('removes a DANGLING pin too — the entry a user most needs to clean up', async () => {
    render(
      <MantineProvider>
        <Harness selected={rushesBin} />
      </MantineProvider>,
    );
    await waitFor(() => expect(screen.getByTestId('bin-items-list')).toBeTruthy());

    await act(async () => {
      fireEvent.click(screen.getByTestId(`bin-item-remove-${FIXTURE_IDS.danglingPin}`));
    });

    expect(api.removeBinItem).toHaveBeenCalledWith(PROJECT_ID, rushesBin, FIXTURE_IDS.danglingPin);
  });

  it('puts a removed row back when the removal fails', async () => {
    api.removeBinItem.mockRejectedValue(new Error('Pin is locked'));
    render(
      <MantineProvider>
        <Harness selected={rushesBin} />
      </MantineProvider>,
    );
    await waitFor(() => expect(screen.getByTestId('bin-items-list')).toBeTruthy());

    await act(async () => {
      fireEvent.click(screen.getByTestId(`bin-item-remove-${FIXTURE_IDS.videoPin}`));
    });

    await waitFor(() => expect(message()).toContain('Pin is locked'));
    expect(screen.getByTestId(`bin-item-${FIXTURE_IDS.videoPin}`)).toBeTruthy();
  });
});
