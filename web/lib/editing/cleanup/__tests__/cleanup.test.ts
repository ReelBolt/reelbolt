/** @vitest-environment node */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apply, type EditOp } from '../../engine';
import { assetRef, doc, media, track } from '../../__tests__/fixtures';
import type { EditTimeline } from '../../types';
import { ApiError } from '../../../api/client';
import { locate } from '../../link/groups';
import { CLEANUP_CONSENT_PREFIX, userScopeFromEmail } from '../../sync/user-scope';

const apiFetch = vi.fn();
vi.mock('../../../api/client', async (orig) => ({ ...(await orig<typeof import('../../../api/client')>()), apiFetch: (...a: unknown[]) => apiFetch(...a) }));

import { CleanupFailedError, describeCleanupRefusal, estimateCleanup, getCleanupJob, mediaRefOf, startCleanup, waitForCleanup } from '../cleanup-api';
import { hasCleanupConsent, rememberCleanupConsent } from '../consent';
import { cleanupSubject, planApply, planRevert } from '../plan';
import { NOISE_CHOICES, isAiTarget, targetOf } from '../types';

const run = (d: EditTimeline, ops: EditOp[]): EditTimeline => ops.reduce((acc, op) => apply(acc, op).doc, d);
const CLEAN = 'aaaaaaaa-0000-4000-8000-000000000009';

beforeEach(() => apiFetch.mockReset());

describe('choices', () => {
  it('offers exactly Hum, Wind, Hiss, Echo (free) and Music (AI) in the noise picker', () => {
    expect(NOISE_CHOICES).toEqual(['hum', 'wind', 'hiss', 'echo', 'music']);
    expect(NOISE_CHOICES.map((c) => isAiTarget(targetOf(c)))).toEqual([false, false, false, false, true]);
    expect(isAiTarget(targetOf('basic'))).toBe(false);
    expect(isAiTarget(targetOf('studio'))).toBe(true);
  });
});

describe('api wrappers', () => {
  it('builds the media ref from an asset ref', () => {
    expect(mediaRefOf({ kind: 'projectFile', projectFileId: 'g1' })).toBe('file:g1');
    expect(mediaRefOf({ kind: 'stepResult', stepResultId: 's1' })).toBe('step_result:s1');
    expect(mediaRefOf({ kind: 'projectFile' })).toBeNull();
  });
  it('posts start and estimate and reads the job at the contract paths', async () => {
    apiFetch.mockResolvedValue({});
    await startCleanup('p1', 'file:g1', { target: 'Hum', allowSourceMediaEgress: true });
    await estimateCleanup('p1', 'file:g1', { target: 'Hum' });
    await getCleanupJob('p1', 'file:g1', 'e1');
    // The colon in `file:<guid>` is encoded on the wire; ASP.NET decodes the route segment back to `file:g1`.
    expect(apiFetch.mock.calls[0][0]).toBe('/api/v1/projects/p1/media/file%3Ag1/cleanup');
    expect(JSON.parse(apiFetch.mock.calls[0][1].body)).toEqual({ target: 'Hum', allowSourceMediaEgress: true });
    expect(apiFetch.mock.calls[1][0]).toBe('/api/v1/projects/p1/media/file%3Ag1/cleanup/estimate');
    expect(apiFetch.mock.calls[2][0]).toBe('/api/v1/projects/p1/media/file%3Ag1/cleanup/e1');
  });
  it('percent-encodes every id in the path', async () => {
    apiFetch.mockResolvedValue({});
    await startCleanup('p 1/x', 'step_result:s 1', { target: 'Hum' });
    await getCleanupJob('p 1/x', 'file:g/1', 'e 1/2');
    expect(apiFetch.mock.calls[0][0]).toBe('/api/v1/projects/p%201%2Fx/media/step_result%3As%201/cleanup');
    expect(apiFetch.mock.calls[1][0]).toBe('/api/v1/projects/p%201%2Fx/media/file%3Ag%2F1/cleanup/e%201%2F2');
  });
  it('polls until completed and throws a CleanupFailedError on failure', async () => {
    const fetchJob = vi.fn().mockResolvedValueOnce({ state: 'running' }).mockResolvedValueOnce({ state: 'ready', outputFileId: 'o' });
    await expect(waitForCleanup('p', 'file:g', 'e', { intervalMs: 1, fetchJob })).resolves.toMatchObject({ outputFileId: 'o' });
    const failing = vi.fn().mockResolvedValue({ state: 'failed', failureCode: 'x' });
    await expect(waitForCleanup('p', 'file:g', 'e', { intervalMs: 1, fetchJob: failing })).rejects.toBeInstanceOf(CleanupFailedError);
  });
  it('sorts refusals', () => {
    const err = (status: number, body: unknown) => Object.assign(new ApiError('x', status), { body });
    expect(describeCleanupRefusal(err(402, { code: 'INSUFFICIENT_CREDITS', missing: 2.04 }))).toEqual({ kind: 'credits', missing: 2.1 });
    expect(describeCleanupRefusal(err(402, { code: 'PLAN_NOT_ENTITLED' }))).toEqual({ kind: 'plan' });
    expect(describeCleanupRefusal(err(400, { code: 'EGRESS_REFUSED' }))).toEqual({ kind: 'egress' });
    expect(describeCleanupRefusal(err(429, { code: 'RUN_RATE_LIMITED' }))).toEqual({ kind: 'busy' });
    expect(describeCleanupRefusal(err(413, { code: 'SOURCE_TOO_LONG' }))).toEqual({ kind: 'tooLong' });
    expect(describeCleanupRefusal(err(409, { code: 'CLEANUP_UNAVAILABLE' }))).toEqual({ kind: 'unavailable' });
    expect(describeCleanupRefusal(err(404, {}))).toEqual({ kind: 'notFound' });
    expect(describeCleanupRefusal(new Error('boom'))).toEqual({ kind: 'other' });
  });
});

describe('consent', () => {
  const store = () => {
    const map = new Map<string, string>();
    return {
      map,
      storage: {
        getItem: (k: string) => map.get(k) ?? null,
        setItem: (k: string, v: string) => void map.set(k, v),
      },
    };
  };
  const SCOPE = { user: 'a@x', org: 'o1', project: 'p1' };

  it('is remembered per user, organisation and project', () => {
    const { storage } = store();
    expect(hasCleanupConsent(SCOPE, storage)).toBe(false);
    rememberCleanupConsent(SCOPE, storage);
    expect(hasCleanupConsent(SCOPE, storage)).toBe(true);

    expect(hasCleanupConsent({ ...SCOPE, project: 'p2' }, storage)).toBe(false);
    expect(hasCleanupConsent({ ...SCOPE, org: 'o2' }, storage)).toBe(false);
    expect(hasCleanupConsent({ ...SCOPE, user: 'b@x' }, storage)).toBe(false);
  });

  it('never reads or stores without a resolvable user, org or project (no shared anonymous key)', () => {
    const { map, storage } = store();
    rememberCleanupConsent({ user: null, org: 'o1', project: 'p1' }, storage);
    rememberCleanupConsent({ user: 'a@x', org: null, project: 'p1' }, storage);
    rememberCleanupConsent({ user: 'a@x', org: 'o1', project: null }, storage);
    rememberCleanupConsent({ user: 'a@x', org: '  ', project: 'p1' }, storage);
    expect(map.size).toBe(0);

    // Even a pre-existing "anonymous" record is not read without a user.
    storage.setItem(`${CLEANUP_CONSENT_PREFIX}v1:["anonymous","o1","p1"]`, JSON.stringify({ version: 1, user: 'anonymous', org: 'o1', project: 'p1' }));
    expect(hasCleanupConsent({ user: null, org: 'o1', project: 'p1' }, storage)).toBe(false);
  });

  it('stores the opaque user scope, never the raw email', () => {
    const { map, storage } = store();
    rememberCleanupConsent(SCOPE, storage);
    const [key, raw] = [...map.entries()][0];
    expect(key).not.toContain('a@x');
    expect(raw).not.toContain('a@x');
    expect((JSON.parse(raw) as Record<string, unknown>).user).toBe(userScopeFromEmail('a@x'));
  });

  it('ignores a consent record written by the old raw-email code', () => {
    const { storage } = store();
    storage.setItem(`${CLEANUP_CONSENT_PREFIX}v1:${JSON.stringify(['a@x', 'o1', 'p1'])}`, JSON.stringify({ version: 1, user: 'a@x', org: 'o1', project: 'p1' }));
    expect(hasCleanupConsent(SCOPE, storage)).toBe(false);
  });

  it('ignores a hand-set record of the wrong shape or for another scope', () => {
    const { map, storage } = store();
    rememberCleanupConsent(SCOPE, storage);
    const [key, raw] = [...map.entries()][0];
    const good = JSON.parse(raw) as Record<string, unknown>;
    expect(good).toEqual({ version: 1, user: userScopeFromEmail('a@x'), org: 'o1', project: 'p1' });

    map.set(key, JSON.stringify({ ...good, version: 2 }));
    expect(hasCleanupConsent(SCOPE, storage)).toBe(false);
    map.set(key, JSON.stringify({ ...good, project: 'p2' }));
    expect(hasCleanupConsent(SCOPE, storage)).toBe(false);
    map.set(key, JSON.stringify({ ...good, org: 'o2' }));
    expect(hasCleanupConsent(SCOPE, storage)).toBe(false);
    map.set(key, JSON.stringify({ ...good, user: 'b@x' }));
    expect(hasCleanupConsent(SCOPE, storage)).toBe(false);
    map.set(key, JSON.stringify({ version: 1, user: 'a@x' }));
    expect(hasCleanupConsent(SCOPE, storage)).toBe(false);
    map.set(key, '1');
    expect(hasCleanupConsent(SCOPE, storage)).toBe(false);
    map.set(key, '{not json');
    expect(hasCleanupConsent(SCOPE, storage)).toBe(false);
    map.set(key, JSON.stringify(good));
    expect(hasCleanupConsent(SCOPE, storage)).toBe(true);
  });
});

describe('apply and revert plans', () => {
  const linked = () => doc([track('V1', 'video', [media('v', 0, 100, 0, { linkId: 'g' })]), track('A1', 'audio', [media('a', 0, 100, 0, { linkId: 'g' })])]);

  it('picks the audio member of a linked pair (audio output) and swaps only it, in one valid program', () => {
    const d = linked();
    const subject = cleanupSubject(d, ['v'])!;
    expect(subject.clip.id).toBe('a');
    expect(subject.outputKind).toBe('Audio');
    expect(subject.linked).toBe(true);
    const plan = planApply(d, subject, CLEAN)!;
    expect(plan.ops.every((o) => o.type === 'setProperty')).toBe(true);
    const after = run(d, plan.ops);
    expect((locate(after, 'a')!.item as { assetRef: unknown }).assetRef).toEqual({ kind: 'projectFile', projectFileId: CLEAN });
    expect((locate(after, 'v')!.item as { assetRef: unknown }).assetRef).toEqual(assetRef(1));
    const back = planRevert(after, plan.record)!;
    expect(run(after, back.ops)).toEqual(d);
  });

  it('an unlinked picture clip asks for the film with clean audio and switches its own source', () => {
    const d = doc([track('V1', 'video', [media('v', 0, 100)])]);
    const subject = cleanupSubject(d, ['v'])!;
    expect(subject.outputKind).toBe('VideoWithCleanAudio');
    const plan = planApply(d, subject, CLEAN)!;
    expect(plan.ops).toHaveLength(1);
    expect(run(d, plan.ops)).not.toEqual(d);
  });

  it('an audio clip alone asks for audio and reverts exactly', () => {
    const d = doc([track('A1', 'audio', [media('a', 0, 100)])]);
    const subject = cleanupSubject(d, ['a'])!;
    expect(subject.outputKind).toBe('Audio');
    const plan = planApply(d, subject, CLEAN)!;
    const after = run(d, plan.ops);
    expect(run(after, planRevert(after, plan.record)!.ops)).toEqual(d);
  });

  it('is null with no usable selection, and a revert skips a clip that changed since', () => {
    const d = doc([track('A1', 'audio', [media('a', 0, 100)])]);
    expect(cleanupSubject(d, [])).toBeNull();
    const plan = planApply(d, cleanupSubject(d, ['a'])!, CLEAN)!;
    expect(planRevert(d, plan.record)).toBeNull();
  });
});
