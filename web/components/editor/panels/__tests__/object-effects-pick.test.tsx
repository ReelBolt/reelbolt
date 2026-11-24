// @vitest-environment jsdom
/** Objects section: "Pick on the picture" names the object with a seed; price, consent and failures as for words. */
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import type { MediaClip } from '@/lib/editing/types';
import type { ObjectEstimate } from '@/lib/editing/api/objects';
import { createEditorStore } from '@/lib/editing/store';
import { standardDoc } from '@/lib/editing/__tests__/fixtures';
import { disarmFollowBox, getFollowHost, registerFollowStore } from '@/lib/editing/follow/follow-host';
import { resetResultsStores, setActiveTimeline } from '@/lib/editing/background/results-store';
import { ObjectEffectsSection } from '../ObjectEffectsSection';

const api = vi.hoisted(() => ({ estimateObject: vi.fn(), startObject: vi.fn(), grantObjectConsent: vi.fn(), withdrawObjectConsent: vi.fn(), consented: { value: true } }));
vi.mock('@/lib/editing/api/objects', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/api/objects')>()),
  estimateObject: api.estimateObject,
  startObject: api.startObject,
  grantObjectConsent: api.grantObjectConsent,
  withdrawObjectConsent: api.withdrawObjectConsent,
  useObjectList: () => ({ data: undefined }),
  useObjectConsent: () => ({ data: { consented: api.consented.value, consentedAt: null } }),
}));
vi.mock('../../thumbnail/useCoarsePointer', () => ({ useCoarsePointer: () => false }));
global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
};

const estimate = (over: Partial<ObjectEstimate> = {}): ObjectEstimate => ({ billingEnabled: true, credits: 3.2, frames: 100, seconds: 10, balanceCredits: 50, affordable: true, cached: false, consented: true, unavailableReason: null, ...over });
const clip = { id: 'c1', type: 'mediaClip', recordIn: 0, recordOut: 100, sourceIn: 0, assetRef: { kind: 'projectFile', projectFileId: 'f1' } } as unknown as MediaClip;
const seed = { mediaRef: 'file:f1', atSec: 2, point: { x: 0.4, y: 0.6 }, box: { x: 0.3, y: 0.5, w: 0.2, h: 0.2 } };
const body = { atSec: 2, point: { x: 0.4, y: 0.6 }, box: { x: 0.3, y: 0.5, w: 0.2, h: 0.2 } };
let unregister: () => void;

const mount = () =>
  render(
    <MantineProvider>
      <ObjectEffectsSection clip={clip} projectId="p1" readOnly={false} onSetProperty={vi.fn()} />
    </MantineProvider>,
  );
const pick = async () => {
  fireEvent.click(screen.getByTestId('object-pick'));
  expect(getFollowHost().armed?.clipId).toBe('c1');
  const arm = getFollowHost().armed!;
  // The layer disarms before it hands the seed back.
  await act(async () => {
    disarmFollowBox();
    arm.onSeed!(seed, 60);
  });
};

beforeEach(() => {
  api.estimateObject.mockReset().mockResolvedValue(estimate());
  api.startObject.mockReset().mockResolvedValue({ requestId: 'r1', executionId: 'e1', status: 'queued' });
  api.consented.value = true;
  window.localStorage.clear();
  resetResultsStores();
  setActiveTimeline('t1');
  unregister = registerFollowStore(createEditorStore({ timelineId: 'tl-1', projectId: 'p1', doc: standardDoc(), revision: 1 }));
});
afterEach(() => {
  disarmFollowBox();
  unregister();
  cleanup();
});

describe('pick on the picture', () => {
  it('is offered first, with "Find by name" still there', () => {
    mount();
    expect(screen.getByTestId('object-pick').textContent).toBe('Pick on the picture');
    expect(screen.getByText('Find by name')).toBeTruthy();
  });

  it('prices the seed, then runs it without words', async () => {
    mount();
    await pick();
    expect((await screen.findByTestId('object-price')).textContent).toBe('About 4 credits');
    expect(api.estimateObject).toHaveBeenCalledWith('p1', 'file:f1', { seed: body });
    await waitFor(() => expect((screen.getByTestId('object-run') as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByTestId('object-run'));
    await waitFor(() => expect(api.startObject).toHaveBeenCalled());
    const sent = api.startObject.mock.calls[0][2];
    expect(sent.seed).toEqual(body);
    expect(sent.prompt).toBeUndefined();
  });

  it('asks for consent first when the project has not agreed', async () => {
    api.consented.value = false;
    api.estimateObject.mockResolvedValue(estimate({ consented: false }));
    mount();
    await pick();
    await waitFor(() => expect((screen.getByTestId('object-run') as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByTestId('object-run'));
    expect(api.startObject).not.toHaveBeenCalled();
    expect(screen.getByRole('dialog')).toBeTruthy();
  });

  it('typing words drops the pick', async () => {
    mount();
    await pick();
    expect(screen.getByTestId('object-picked')).toBeTruthy();
    fireEvent.change(screen.getByTestId('object-prompt'), { target: { value: 'face' } });
    expect(screen.queryByTestId('object-picked')).toBeNull();
  });

  it('OBJECT_NOT_FOUND_AT_POINT says so and offers Pick again', async () => {
    api.startObject.mockRejectedValue(new ApiError('x', 422, [], { code: 'OBJECT_NOT_FOUND_AT_POINT' }));
    mount();
    await pick();
    await waitFor(() => expect((screen.getByTestId('object-run') as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByTestId('object-run'));
    expect((await screen.findByTestId('object-start-error')).textContent).toBe('We could not find an object where you tapped. Tap its centre, or draw a box.');
    fireEvent.click(screen.getByTestId('object-pick-again'));
    expect(getFollowHost().armed?.clipId).toBe('c1');
  });
});
