// @vitest-environment jsdom
/**
 * Follow subject section: visibility per clip kind, the draw-box flow posts the right body, a finished follow writes
 * ONE undoable transform, a changed clip gets the re-run prompt instead of a write, and a refusal shows its message.
 */
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import { createEditorStore, undoStep, type EditorStore } from '@/lib/editing/store';
import { standardDoc } from '@/lib/editing/__tests__/fixtures';
import type { MediaClip } from '@/lib/editing/types';
import type { FollowResult } from '@/lib/editing/api/follow';
import { getFollowHost, registerFollowStore } from '@/lib/editing/follow/follow-host';
import { FOLLOW_RUN_PREFIX, userScopeFromEmail } from '@/lib/editing/sync/user-scope';
import { getUserFromCookie } from '@/lib/hooks/use-auth';
import { FollowSection } from '../FollowSection';

const api = vi.hoisted(() => ({
  startSubjectTrack: vi.fn(),
  requestFollow: vi.fn(),
  job: { data: undefined as unknown },
}));
vi.mock('@/lib/editing/api/follow', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/api/follow')>()),
  startSubjectTrack: api.startSubjectTrack,
  requestFollow: api.requestFollow,
  useSubjectTrack: () => ({ data: api.job.data, error: undefined, stalled: false, retry: () => {} }),
}));
vi.mock('@/lib/editing/api/objects', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/api/objects')>()),
  useObjectList: () => ({ data: [] }),
}));
vi.mock('../../thumbnail/useCoarsePointer', () => ({ useCoarsePointer: () => false }));

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;

const result = (over: Partial<FollowResult> = {}): FollowResult => ({
  revision: 1,
  itemId: 'c1',
  keys: [
    { sourceSec: 0, positionX: 0.1, positionY: 0, scale: 1.5 },
    { sourceSec: 1, positionX: 0.2, positionY: 0.1, scale: 2 },
  ],
  assumed: { canvasWidth: 1920, canvasHeight: 1080, sourceWidth: 1920, sourceHeight: 1080 },
  zoomCap: 2,
  zoomCapLimited: false,
  lostSpans: [],
  clampCount: 0,
  warnings: [],
  ...over,
});

const runKey = `${FOLLOW_RUN_PREFIX}${userScopeFromEmail(getUserFromCookie()?.email)}:p1:c1`;
let store: EditorStore;
let unregister: () => void;
const clipOf = (id: string): MediaClip => store.getState().doc.tracks.flatMap((t) => t.items).find((i) => i.id === id) as MediaClip;
const mount = (id = 'c1', projectId: string | null = 'p1', probe: React.ComponentProps<typeof FollowSection>['probe'] = null) =>
  render(
    <MantineProvider>
      <FollowSection clip={clipOf(id)} projectId={projectId} readOnly={false} allowDraw probe={probe} />
    </MantineProvider>,
  );
const ready = () => {
  api.job.data = { executionId: 'e1', status: 'succeeded', failureCode: null, message: null, analysisStepResultId: 'sr1', track: { trackId: 'r1', startSec: 3, endSec: 41, boxes: [] } };
  window.sessionStorage.setItem(runKey, 'e1');
};

beforeEach(() => {
  (globalThis as { ResizeObserver?: unknown }).ResizeObserver ??= class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
  window.sessionStorage.clear();
  api.startSubjectTrack.mockReset().mockResolvedValue({ executionId: 'e9', status: 'queued' });
  api.requestFollow.mockReset().mockResolvedValue(result());
  api.job.data = undefined;
  store = createEditorStore({ timelineId: 'tl-1', projectId: 'p1', doc: standardDoc(), revision: 4 });
  unregister = registerFollowStore(store);
});
afterEach(() => {
  cleanup();
  unregister();
});

describe('visibility', () => {
  it('shows for a video clip', () => {
    mount('c1');
    expect(screen.getByTestId('follow-section')).toBeTruthy();
  });
  it.each([
    ['a sound clip', 'a1', 'p1', null],
    ['a title', 't1', 'p1', null],
    ['a still picture', 'c1', 'p1', { container: 'png_pipe' }],
    ['no project', 'c1', null, null],
  ])('is hidden for %s', (_n, id, project, probe) => {
    const view = mount(id, project, probe as never);
    expect(view.container.querySelector('[data-testid="follow-section"]')).toBeNull();
  });
});

describe('draw a box', () => {
  it('posts the source second at the playhead and the box', async () => {
    store.setUi({ playheadFrame: 30 });
    mount('c1');
    fireEvent.click(screen.getByTestId('follow-draw'));
    expect(getFollowHost().armed?.clipId).toBe('c1');
    await act(async () => getFollowHost().armed!.onSeed!({ mediaRef: 'file:f1', atSec: 1, point: { x: 0.25, y: 0.4 }, box: { x: 0.2, y: 0.3, w: 0.1, h: 0.2 } }, 30));
    expect(api.startSubjectTrack).toHaveBeenCalledWith('p1', expect.stringMatching(/^file:/), { atSec: 1, box: { x: 0.2, y: 0.3, w: 0.1, h: 0.2 } });
    expect(window.sessionStorage.getItem(runKey)).toBe('e9');
  });
});

describe('follow', () => {
  it('writes one undoable transform with linear keyframes', async () => {
    ready();
    mount('c1');
    expect(window.sessionStorage.getItem(runKey)).toBe('e1');
    fireEvent.click(screen.getByTestId('follow-run'));
    await waitFor(() => expect(clipOf('c1').transform?.positionX?.keys?.length).toBe(2));
    expect(window.sessionStorage.getItem(runKey)).toBeNull();
    expect(api.requestFollow).toHaveBeenCalledWith('p1', 'tl-1', expect.objectContaining({ itemId: 'c1', revision: 4, track: { analysisStepResultId: 'sr1', trackId: 'r1' }, tightness: 'Medium', smoothness: 'Normal', onLost: 'Hold', allowSoftZoom: false }));
    expect(clipOf('c1').transform?.scale?.keys?.map((k) => k.interp)).toEqual(['linear', 'linear']);
    undoStep(store);
    expect(clipOf('c1').transform?.positionX?.keys ?? null).toBeNull();
  });

  it('keeps the saved run id after a succeeded run until Follow has applied, and drops it after a failed one', () => {
    ready();
    const view = mount('c1');
    expect(window.sessionStorage.getItem(runKey)).toBe('e1');
    view.unmount();
    api.job.data = { executionId: 'e1', status: 'failed', failureCode: 'X', message: 'No luck', analysisStepResultId: null, track: null };
    mount('c1');
    expect(window.sessionStorage.getItem(runKey)).toBeNull();
  });

  it('says why when the zoom was limited and where the subject was lost', async () => {
    ready();
    api.requestFollow.mockResolvedValue(result({ zoomCapLimited: true, lostSpans: [{ fromSec: 0.2, toSec: 0.8 }] }));
    mount('c1');
    fireEvent.click(screen.getByTestId('follow-run'));
    await screen.findByTestId('follow-zoom-note');
    expect(screen.getByTestId('follow-lost-note').textContent).toContain('0:00');
  });

  it('does not apply a result for a clip that changed, and offers to run again', async () => {
    ready();
    api.requestFollow.mockImplementation(async () => {
      store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'crop', value: { left: 0.1 } });
      return result();
    });
    mount('c1');
    fireEvent.click(screen.getByTestId('follow-run'));
    await screen.findByTestId('follow-stale');
    expect(clipOf('c1').transform?.positionX?.keys ?? null).toBeNull();
    api.requestFollow.mockResolvedValue(result());
    act(() => store.markSaved(5));
    fireEvent.click(screen.getByTestId('follow-rerun'));
    await waitFor(() => expect(api.requestFollow).toHaveBeenCalledTimes(2));
  });

  it('shows the server refusal verbatim and writes nothing', async () => {
    ready();
    api.requestFollow.mockRejectedValue(new ApiError('That clip is played at a changing speed.', 409, []));
    mount('c1');
    fireEvent.click(screen.getByTestId('follow-run'));
    expect((await screen.findByTestId('follow-error')).textContent).toBe('That clip is played at a changing speed.');
    expect(clipOf('c1').transform?.positionX?.keys ?? null).toBeNull();
  });

  it('waits for the editor to save before asking, and sends the saved revision', async () => {
    ready();
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'stabilize', value: true });
    mount('c1');
    fireEvent.click(screen.getByTestId('follow-run'));
    await act(async () => {});
    expect(api.requestFollow).not.toHaveBeenCalled();
    act(() => store.markSaved(7));
    await waitFor(() => expect(api.requestFollow).toHaveBeenCalledWith('p1', 'tl-1', expect.objectContaining({ revision: 7 })));
  });

  it('refuses with a plain message when the editor cannot save (frozen)', async () => {
    ready();
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'stabilize', value: true });
    store.setReadOnly(true);
    mount('c1');
    fireEvent.click(screen.getByTestId('follow-run'));
    await screen.findByTestId('follow-error');
    expect(api.requestFollow).not.toHaveBeenCalled();
  });

  it('Remove follow clears only position and scale, in one undo step', async () => {
    ready();
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'transform', value: { rotation: { value: 9 }, positionX: { value: 0, keys: [{ frame: 0, value: 0, interp: 'linear' }, { frame: 5, value: 1, interp: 'linear' }] }, scale: { value: 2 } } });
    store.markSaved(5);
    const view = mount('c1');
    view.rerender(<MantineProvider><FollowSection clip={clipOf('c1')} projectId="p1" readOnly={false} allowDraw /></MantineProvider>);
    fireEvent.click(screen.getByTestId('follow-remove'));
    expect(clipOf('c1').transform).toEqual({ rotation: { value: 9 } });
    undoStep(store);
    expect(clipOf('c1').transform?.positionX?.keys?.length).toBe(2);
  });
});
