/** @vitest-environment node */
/**
 * R04.1: per-source readiness (preparing with a percent, failed, offline, ready), the viewer's reason line,
 * and "Try again" / "Prepare all" asking the pipeline once per source.
 */

import { describe, expect, it, vi } from 'vitest';
import { PREPARING_PREVIEW } from '../honesty';
import { pictureSourceRefs, prepareSources, readinessRows, readinessWords, ReadinessStore, sourceReadiness, summarizeReadiness, viewerReason } from '../readiness';
import type { ProxyStatus } from '../proxy-urls';
import { assetRef, doc, media, track } from '../../__tests__/fixtures';
import type { EditTimeline, MediaClip } from '../../types';

const REF = `file:${assetRef(1).projectFileId}`;
const timeline = (): EditTimeline => doc([track('v1', 'video', [media('c1', 0, 60, 0, { name: 'Interview' })]), track('a1', 'audio', [media('a1', 0, 60)])]);
const pending: ProxyStatus = { state: 'pending' };
const original: ProxyStatus = { state: 'original', url: '/o' };
const proxy: ProxyStatus = { state: 'proxy', url: '/p' };
const missingC1 = [{ itemId: 'c1', trackId: 'v1', cause: 'noUrl' as const }];

describe('sourceReadiness', () => {
  it('a proxy is ready, even after an old failure', () => {
    expect(sourceReadiness(proxy, { stage: 'Failed', percent: null }, 'x')).toEqual({ state: 'ready', original: false });
  });
  it('reports the pipeline percent while preparing', () => {
    expect(sourceReadiness(pending, { stage: 'Proxy', percent: 42 }, undefined)).toEqual({ state: 'preparing', percent: 42 });
    expect(sourceReadiness(original, { stage: 'Proxy', percent: 7 }, undefined)).toEqual({ state: 'preparing', percent: 7 });
  });
  it('a pending lookup with no progress is preparing without a number', () => {
    expect(sourceReadiness(pending, undefined, undefined)).toEqual({ state: 'preparing', percent: null });
  });
  it('a failed run or a failed request is failed, with a plain reason', () => {
    expect(sourceReadiness(original, { stage: 'Failed', percent: null }, undefined)).toMatchObject({ state: 'failed', reason: expect.stringMatching(/could not be converted/) });
    expect(sourceReadiness(pending, undefined, 'we could not reach the server')).toEqual({ state: 'failed', reason: 'we could not reach the server' });
  });
  it('no proxy and no original is offline', () => {
    expect(sourceReadiness({ state: 'unavailable', reason: 'x' }, undefined, undefined).state).toBe('offline');
  });
  it('an original with no pipeline news plays from the original', () => {
    expect(sourceReadiness(original, undefined, undefined)).toEqual({ state: 'ready', original: true });
    expect(sourceReadiness(original, { stage: 'Skipped', percent: null }, undefined)).toEqual({ state: 'ready', original: true });
  });
});

describe('ReadinessStore', () => {
  it('folds progress events, ignores unattributed ones and notifies only on change', () => {
    const store = new ReadinessStore();
    const listener = vi.fn();
    store.subscribe(listener);
    store.apply({ mediaRef: null, stage: 'Proxy', percentComplete: 3 });
    expect(listener).not.toHaveBeenCalled();
    store.apply({ mediaRef: REF, stage: 'Proxy', percentComplete: 42 });
    store.apply({ mediaRef: REF, stage: 'Proxy', percentComplete: 42 });
    expect(listener).toHaveBeenCalledTimes(1);
    expect(store.progressOf(REF)).toEqual({ stage: 'Proxy', percent: 42 });
  });
  it('a fresh run clears a request failure; restart forgets a failed run', () => {
    const store = new ReadinessStore();
    store.fail(REF, 'down');
    store.apply({ mediaRef: REF, stage: 'Queued', percentComplete: null });
    expect(store.errorOf(REF)).toBeUndefined();
    store.apply({ mediaRef: REF, stage: 'Failed', percentComplete: null });
    store.restart(REF);
    expect(store.progressOf(REF)).toEqual({ stage: 'Queued', percent: null });
  });
});

describe('rows and summary', () => {
  it('lists each picture source once, never the sound tracks', () => {
    const store = new ReadinessStore();
    const d = doc([track('v1', 'video', [media('c1', 0, 30), media('c2', 30, 60)]), track('a1', 'audio', [media('a1', 0, 60)])]);
    const rows = readinessRows(d, store, () => pending);
    expect(rows).toHaveLength(1);
    expect(rows[0]).toMatchObject({ itemId: 'c1', mediaRef: REF });
    expect(pictureSourceRefs(d)).toEqual([REF]);
  });
  it('counts states and averages the percent', () => {
    const store = new ReadinessStore();
    store.apply({ mediaRef: REF, stage: 'Proxy', percentComplete: 40 });
    const rows = readinessRows(timeline(), store, () => pending);
    expect(summarizeReadiness(rows)).toEqual({ total: 1, ready: 0, preparing: 1, failed: 0, offline: 0, percent: 40 });
    expect(readinessWords(rows[0].readiness)).toBe('Preparing preview 40%');
  });
});

describe('viewerReason', () => {
  it('says "Preparing preview 42%" with the percent and no retry', () => {
    const store = new ReadinessStore();
    store.apply({ mediaRef: REF, stage: 'Proxy', percentComplete: 42 });
    expect(viewerReason(timeline(), missingC1, () => pending, store)).toEqual({ kind: 'preparing', text: 'Preparing preview 42%', percent: 42, retryRef: null });
  });
  it('says "Preparing preview…" when no number is known', () => {
    expect(viewerReason(timeline(), missingC1, () => pending, new ReadinessStore())).toMatchObject({ kind: 'preparing', text: PREPARING_PREVIEW, percent: null });
  });
  it('a failure names the reason and offers Try again for that source', () => {
    const store = new ReadinessStore();
    store.apply({ mediaRef: REF, stage: 'Failed', percentComplete: null });
    const reason = viewerReason(timeline(), missingC1, () => original, store)!;
    expect(reason.kind).toBe('failed');
    expect(reason.text).toMatch(/^Preview failed: the file could not be converted/);
    expect(reason.retryRef).toBe(REF);
  });
  it('a clip the viewer cannot open is a failure with Try again', () => {
    const reason = viewerReason(timeline(), [{ itemId: 'c1', trackId: 'v1', cause: 'openFailed' }], () => proxy, new ReadinessStore())!;
    expect(reason).toMatchObject({ kind: 'failed', retryRef: REF });
    expect(reason.text).toMatch(/format may not be supported/);
  });
  it('an offline source says so', () => {
    const reason = viewerReason(timeline(), missingC1, () => ({ state: 'unavailable', reason: 'gone' }), new ReadinessStore())!;
    expect(reason.kind).toBe('offline');
    expect(reason.text).toMatch(/^Source offline:/);
  });
  it('is null when every layer was drawn, and never leaks ids', () => {
    expect(viewerReason(timeline(), [], () => proxy, new ReadinessStore())).toBeNull();
    const text = viewerReason(timeline(), missingC1, () => pending, new ReadinessStore())!.text;
    expect(text).not.toMatch(/file:|[0-9a-f]{8}-[0-9a-f]{4}/);
  });
});

describe('prepareSources', () => {
  it('asks once per source, in order, then refreshes the lookups', async () => {
    const store = new ReadinessStore();
    const request = vi.fn().mockResolvedValue(undefined);
    const refresh = vi.fn();
    await prepareSources(store, { request, refresh }, ['file:a', 'file:b']);
    expect(request.mock.calls.map((c) => c[0])).toEqual(['file:a', 'file:b']);
    expect(refresh).toHaveBeenCalledWith(['file:a', 'file:b']);
    expect(store.progressOf('file:a')?.stage).toBe('Queued');
  });
  it('a failed request is that source\'s failure; the others still go', async () => {
    const store = new ReadinessStore();
    const request = vi.fn().mockRejectedValueOnce(new Error('boom')).mockResolvedValue(undefined);
    await prepareSources(store, { request, refresh: () => undefined }, ['file:a', 'file:b']);
    expect(store.errorOf('file:a')).toMatch(/could not reach the server/);
    expect(store.errorOf('file:b')).toBeUndefined();
    expect(request).toHaveBeenCalledTimes(2);
    const clip = media('x', 0, 1) as MediaClip;
    expect(store.readinessOf({ ...clip, assetRef: { kind: 'projectFile', projectFileId: 'a' } }, () => pending).state).toBe('failed');
  });
});
