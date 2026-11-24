// @vitest-environment jsdom
/**
 * Magic blur panel: price states, consent flow (cancel writes nothing, confirm = PUT then run), a launch is
 * registered with the editor (the workspace's watcher applies the result), failure / not-found / limit / plan / credit states, the
 * existing-effect rows, and the phone (coarse pointer) sizing.
 */

import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import type { MediaClip, RegionClipEffect } from '@/lib/editing/types';
import type { ObjectEstimate, ObjectRequest } from '@/lib/editing/api/objects';
import { getResultsStore, resetResultsStores, setActiveTimeline } from '@/lib/editing/background/results-store';
import { ObjectEffectsSection } from '../ObjectEffectsSection';

const api = vi.hoisted(() => ({
  estimateObject: vi.fn(),
  startObject: vi.fn(),
  grantObjectConsent: vi.fn(),
  withdrawObjectConsent: vi.fn(),
  list: { data: undefined as unknown },
  coarse: { value: false },
}));

vi.mock('@/lib/editing/api/objects', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/api/objects')>()),
  estimateObject: api.estimateObject,
  startObject: api.startObject,
  grantObjectConsent: api.grantObjectConsent,
  withdrawObjectConsent: api.withdrawObjectConsent,
  useObjectList: () => api.list,
  useObjectConsent: () => ({ data: undefined }),
}));
vi.mock('../../thumbnail/useCoarsePointer', () => ({ useCoarsePointer: () => api.coarse.value }));

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

const estimate = (over: Partial<ObjectEstimate> = {}): ObjectEstimate => ({ billingEnabled: true, credits: 3.2, frames: 100, seconds: 10, balanceCredits: 50, affordable: true, cached: false, consented: true, unavailableReason: null, ...over });
const finished = (over: Partial<ObjectRequest> = {}): ObjectRequest => ({
  requestId: 'r1', executionId: 'e1', status: 'succeeded', prompt: 'license plate', matchScope: null, effect: 'blur', strength: 0.7, invert: false, featherPx: 0,
  analysisStepResultId: 'sr1', trackIds: ['g1', 'g2'], coverageEndSec: null, failureCode: null, message: null, createdAt: '2026-01-01T00:00:00Z', ...over,
});
const regionEffect = (id: string, over: Partial<RegionClipEffect> = {}): RegionClipEffect => ({ effect: 'blur', strength: 0.5, enabled: true, mask: { kind: 'segmentation', analysisStepResultId: id, trackIds: ['g1'], invert: false, featherPx: 0 }, ...over });
const clipOf = (over: Partial<MediaClip> = {}): MediaClip =>
  ({ id: 'c1', type: 'mediaClip', recordIn: 0, recordOut: 100, sourceIn: 0, assetRef: { kind: 'projectFile', projectFileId: 'f1' }, ...over }) as unknown as MediaClip;

function setup(clip: MediaClip = clipOf(), readOnly = false) {
  const onSetProperty = vi.fn();
  const view = (c: MediaClip) => (
    <MantineProvider>
      <ObjectEffectsSection clip={c} projectId="p1" readOnly={readOnly} onSetProperty={onSetProperty} />
    </MantineProvider>
  );
  const utils = render(view(clip));
  return { onSetProperty, rerender: (c: MediaClip) => utils.rerender(view(c)) };
}
const type = (text: string) => fireEvent.change(screen.getByTestId('object-prompt'), { target: { value: text } });

beforeEach(() => {
  api.estimateObject.mockReset().mockResolvedValue(estimate());
  api.startObject.mockReset().mockResolvedValue({ requestId: 'r1', executionId: 'e1', status: 'queued' });
  api.grantObjectConsent.mockReset().mockResolvedValue({ consented: true, consentedAt: 'x' });
  api.withdrawObjectConsent.mockReset().mockResolvedValue({ consented: false, consentedAt: null });
  api.list.data = undefined;
  window.localStorage.clear();
  resetResultsStores();
  setActiveTimeline('t1');
  api.coarse.value = false;
});
afterEach(cleanup);

describe('price line', () => {
  it('shows About N credits', async () => {
    setup();
    type('license plate');
    expect((await screen.findByTestId('object-price')).textContent).toBe('About 4 credits');
    expect(api.estimateObject).toHaveBeenCalledWith('p1', 'file:f1', 'license plate');
    await waitFor(() => expect((screen.getByTestId('object-run') as HTMLButtonElement).disabled).toBe(false));
  });
  it('says Up to N credits when the length is unknown', async () => {
    api.estimateObject.mockResolvedValue(estimate({ upperBound: true }));
    setup();
    type('face');
    expect((await screen.findByTestId('object-price')).textContent).toBe('Up to 4 credits');
  });
  it('says free when the object was already found', async () => {
    api.estimateObject.mockResolvedValue(estimate({ cached: true }));
    setup();
    type('face');
    expect((await screen.findByTestId('object-price')).textContent).toBe('Already found in this video: free');
  });
  it('shows no price when billing is off', async () => {
    api.estimateObject.mockResolvedValue(estimate({ billingEnabled: false }));
    setup();
    type('face');
    await waitFor(() => expect((screen.getByTestId('object-run') as HTMLButtonElement).disabled).toBe(false));
    expect(screen.queryByTestId('object-price')).toBeNull();
  });
  it('disables Run and says why when the credits are not enough', async () => {
    api.estimateObject.mockResolvedValue(estimate({ affordable: false }));
    setup();
    type('face');
    expect((await screen.findByTestId('object-blocked')).textContent).toContain('not have enough credits');
    expect((screen.getByTestId('object-run') as HTMLButtonElement).disabled).toBe(true);
  });
  it('shows the plan message when the plan does not include it', async () => {
    api.estimateObject.mockRejectedValue(new ApiError('Upgrade to use this.', 402, [], { code: 'PLAN_NOT_ENTITLED', feature: 'segmentation' }));
    setup();
    type('face');
    expect((await screen.findByTestId('object-blocked')).textContent).toBe('Upgrade to use this.');
    expect((screen.getByTestId('object-run') as HTMLButtonElement).disabled).toBe(true);
  });
  it('refuses a source that is too long', async () => {
    api.estimateObject.mockRejectedValue(new ApiError('Videos over 10 minutes are not supported.', 422, [], { code: 'OBJECT_SOURCE_TOO_LONG' }));
    setup();
    type('face');
    expect((await screen.findByTestId('object-blocked')).textContent).toContain('too long');
  });
  it('quick picks fill the field', () => {
    setup();
    fireEvent.click(screen.getByTestId('object-chip-chipFace'));
    expect((screen.getByTestId('object-prompt') as HTMLInputElement).value).toBe('face');
  });
});

describe('consent', () => {
  it('cancel writes nothing', async () => {
    api.estimateObject.mockResolvedValue(estimate({ consented: false }));
    setup();
    type('face');
    await waitFor(() => expect((screen.getByTestId('object-run') as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByTestId('object-run'));
    expect(await screen.findByTestId('object-consent-dialog')).toBeTruthy();
    expect((screen.getByTestId('object-consent-confirm') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByTestId('object-consent-cancel'));
    expect(api.grantObjectConsent).not.toHaveBeenCalled();
    expect(api.startObject).not.toHaveBeenCalled();
  });
  it('confirm = PUT consent, then run', async () => {
    api.estimateObject.mockResolvedValue(estimate({ consented: false }));
    setup();
    type('face');
    await waitFor(() => expect((screen.getByTestId('object-run') as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByTestId('object-run'));
    fireEvent.click(await screen.findByTestId('object-consent-check'));
    fireEvent.click(screen.getByTestId('object-consent-confirm'));
    await waitFor(() => expect(api.startObject).toHaveBeenCalledTimes(1));
    expect(api.grantObjectConsent).toHaveBeenCalledWith('p1');
    expect(api.grantObjectConsent.mock.invocationCallOrder[0]).toBeLessThan(api.startObject.mock.invocationCallOrder[0]);
    expect(api.startObject).toHaveBeenCalledWith('p1', 'file:f1', { prompt: 'face', effect: 'blur', strength: 0.7, invert: false, featherPx: 0 });
  });
  it('offers Withdraw when consented', async () => {
    setup();
    type('face');
    fireEvent.click(await screen.findByTestId('object-withdraw'));
    await waitFor(() => expect(api.withdrawObjectConsent).toHaveBeenCalledWith('p1'));
    await waitFor(() => expect(screen.queryByTestId('object-withdraw')).toBeNull());
  });
});

describe('run and register', () => {
  async function run() {
    const s = setup();
    type('license plate');
    await waitFor(() => expect((screen.getByTestId('object-run') as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByTestId('object-run'));
    await waitFor(() => expect(api.startObject).toHaveBeenCalled());
    await screen.findByTestId('object-progress');
    return s;
  }
  const entries = () => getResultsStore('t1').getEntries();
  it('registers the launch with the editor, anchored to the clip, and writes nothing itself', async () => {
    const { onSetProperty } = await run();
    expect(entries()).toHaveLength(1);
    expect(entries()[0]).toMatchObject({ tool: 'objectEffect', requestId: 'r1', executionId: 'e1', state: 'running', intent: { prompt: 'license plate', effect: 'blur', strength: 0.7, invert: false, featherPx: 0 } });
    expect(entries()[0].anchor).toMatchObject({ assetKey: 'file:f1', clipIds: ['c1'], coverage: { startSec: 0, endSec: null } });
    expect(screen.getByTestId('object-progress').textContent).toContain('license plate');
    expect(onSetProperty).not.toHaveBeenCalled();
  });
  it('records the launch response workflowId so the busy-edit modal can offer "Stop the job"', async () => {
    api.startObject.mockResolvedValue({ requestId: 'r1', executionId: 'e1', status: 'queued', workflowId: 'w1' });
    await run();
    expect(entries()[0].intent).toMatchObject({ workflowId: 'w1' });
  });
  it('keeps the run when the section unmounts: the entry stays running in the editor', async () => {
    await run();
    cleanup();
    expect(entries()[0].state).toBe('running');
  });
  it('Stop following discards the entry; Run comes back', async () => {
    await run();
    fireEvent.click(screen.getByTestId('object-stop'));
    await waitFor(() => expect(screen.queryByTestId('object-progress')).toBeNull());
    expect(entries()[0].state).toBe('discarded');
  });
  it('shows poll stalled and retry clears the flag', async () => {
    await run();
    act(() => getResultsStore('t1').update('r1', { stalled: true }));
    expect(await screen.findByTestId('object-poll-stalled')).toBeTruthy();
    fireEvent.click(screen.getByTestId('object-poll-retry'));
    await waitFor(() => expect(entries()[0].stalled).toBe(false));
    expect(screen.queryByTestId('object-poll-stalled')).toBeNull();
  });
  it('says a finished result waits for the user\'s OK', async () => {
    await run();
    act(() => getResultsStore('t1').update('r1', { state: 'needsReview', review: { applicable: [], reasons: ['targetGone'], lostCount: 1 } }));
    expect((await screen.findByTestId('object-needs-ok')).textContent).toContain('needs your OK');
  });
  it('words OBJECT_NOT_FOUND with the object', async () => {
    await run();
    act(() => getResultsStore('t1').update('r1', { state: 'failed', failure: { code: 'OBJECT_NOT_FOUND', message: null } }));
    expect((await screen.findByTestId('object-failed')).textContent).toBe('We could not find license plate in this video. Try other words.');
  });
  it('maps known failure codes to its own words, not the server text', async () => {
    await run();
    act(() => getResultsStore('t1').update('r1', { state: 'failed', failure: { code: 'RESULT_UNAVAILABLE', message: 'server english' } }));
    expect((await screen.findByTestId('object-failed')).textContent).toContain('no longer available');
  });
  it('shows the API message on another failure', async () => {
    await run();
    act(() => getResultsStore('t1').update('r1', { state: 'failed', failure: { code: 'EGRESS_REFUSED', message: 'This video cannot be searched.' } }));
    expect((await screen.findByTestId('object-failed')).textContent).toBe('This video cannot be searched.');
  });
});

describe('stale price and consent copy', () => {
  it('disables Run while the price belongs to older words', async () => {
    setup();
    type('face');
    await waitFor(() => expect((screen.getByTestId('object-run') as HTMLButtonElement).disabled).toBe(false));
    type('faces');
    expect((screen.getByTestId('object-run') as HTMLButtonElement).disabled).toBe(true);
    await waitFor(() => expect((screen.getByTestId('object-run') as HTMLButtonElement).disabled).toBe(false));
  });
  it('says the consent covers the project', async () => {
    api.estimateObject.mockResolvedValue(estimate({ consented: false }));
    setup();
    type('face');
    await waitFor(() => expect((screen.getByTestId('object-run') as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByTestId('object-run'));
    expect((await screen.findByTestId('object-consent-dialog')).textContent).toContain('videos in this project');
  });
});

describe('limit, unavailable source, read-only', () => {
  it('at 4 effects says to remove one first and cannot run', async () => {
    setup(clipOf({ regionEffects: [regionEffect('a'), regionEffect('b'), regionEffect('c'), regionEffect('d')] }));
    type('face');
    expect((await screen.findByTestId('object-limit')).textContent).toContain('Remove one first');
    expect((screen.getByTestId('object-run') as HTMLButtonElement).disabled).toBe(true);
  });
  it('explains an unavailable source', () => {
    setup(clipOf({ assetRef: { kind: 'url' } as unknown as MediaClip['assetRef'] }));
    expect(screen.getByTestId('object-effects-unavailable').textContent).toContain('project file');
    expect(screen.queryByTestId('object-run')).toBeNull();
  });
  it('read-only disables Run and the prompt', () => {
    setup(clipOf(), true);
    expect((screen.getByTestId('object-run') as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByTestId('object-prompt') as HTMLInputElement).disabled).toBe(true);
  });
});

describe('existing effects', () => {
  it('names the object from the list, falls back to Object N, toggles and removes (one write each)', () => {
    api.list.data = [finished({ analysisStepResultId: 'a', prompt: 'logo' })];
    const effects = [regionEffect('a'), regionEffect('zz', { effect: 'pixelate' })];
    const { onSetProperty } = setup(clipOf({ regionEffects: effects }));
    expect(screen.getByTestId('object-row-0').textContent).toContain('Blur on logo');
    expect(screen.getByTestId('object-row-1').textContent).toContain('Pixelate on Object 2');
    fireEvent.click(screen.getByTestId('object-row-toggle-0'));
    expect(onSetProperty).toHaveBeenLastCalledWith('c1', 'regionEffects', [{ ...effects[0], enabled: false }, effects[1]], 'Toggle object effect');
    fireEvent.click(screen.getByTestId('object-row-remove-1'));
    expect(onSetProperty).toHaveBeenLastCalledWith('c1', 'regionEffects', [effects[0]], 'Remove object effect');
    expect(onSetProperty).toHaveBeenCalledTimes(2);
  });
  it('removing the last effect clears the field', () => {
    const { onSetProperty } = setup(clipOf({ regionEffects: [regionEffect('a')] }));
    fireEvent.click(screen.getByTestId('object-row-remove-0'));
    expect(onSetProperty).toHaveBeenCalledWith('c1', 'regionEffects', null, 'Remove object effect');
  });
});

describe('phone and a11y', () => {
  it('uses 44 px targets on a coarse pointer', () => {
    api.coarse.value = true;
    setup(clipOf({ regionEffects: [regionEffect('a')] }));
    expect(screen.getByTestId('object-run').style.minHeight).toBe('44px');
    expect(screen.getByTestId('object-row-remove-0').style.minHeight).toBe('44px');
  });
  it('labels its controls and names the roles', () => {
    setup(clipOf({ regionEffects: [regionEffect('a')] }));
    expect(screen.getByRole('textbox', { name: 'What should it find?' })).toBeTruthy();
    expect(screen.getByRole('group', { name: 'Quick picks' })).toBeTruthy();
    expect(screen.getByRole('switch', { name: 'Apply to everything except the object' })).toBeTruthy();
    expect(screen.getByRole('list', { name: 'Object effects on this clip' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Remove Blur on Object 1' })).toBeTruthy();
    expect(screen.getByTestId('object-preview-hint').textContent).toBe('The preview follows the object with a box until its outline is ready. The finished video uses the exact outline.');
  });
});
