/** Subject clips: the request the form builds, the refusals the dialog words, the polling, and the starting shape. */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '@/lib/api/client';
import {
  SUBJECT_CLIP_PROMPT_MAX,
  SubjectClipFailedError,
  buildSubjectClipRequest,
  cancelSubjectClip,
  describeSubjectClipRefusal,
  estimateSubjectClip,
  getSubjectClip,
  nearestSubjectClipRatio,
  resolutionsForModel,
  startSubjectClip,
  subjectClipBlocker,
  waitForSubjectClip,
  type SubjectClipForm,
  type SubjectClipStatus,
} from '../subject-clip-api';

const FORM: SubjectClipForm = { subjectIds: ['a1', 'o1'], prompt: '  She opens the box  ', capabilityId: null, durationSeconds: 6, ratio: '16:9', resolution: '768P', takes: 1, speechFileId: null };
const refusal = (status: number, body: Record<string, unknown>, errors: string[] = []) => new ApiError(String(body.message ?? 'refused'), status, errors, body);
const status = (over: Partial<SubjectClipStatus> = {}): SubjectClipStatus => ({ executionId: 'e1', state: 'running', outputFileIds: [], ...over });

afterEach(() => vi.unstubAllGlobals());

describe('the request body', () => {
  it('sends the subjects as avatar ids in the order chosen, the trimmed prompt and the plain settings', () => {
    expect(buildSubjectClipRequest(FORM, false)).toEqual({
      subjects: [{ avatarId: 'a1' }, { avatarId: 'o1' }],
      prompt: 'She opens the box',
      durationSeconds: 6,
      ratio: '16:9',
      resolution: '768P',
      takes: 1,
    });
  });

  it('leaves optional fields out instead of sending them empty', () => {
    const body = buildSubjectClipRequest(FORM, false);
    expect('capabilityId' in body).toBe(false);
    expect('speechFileId' in body).toBe(false);
    expect('subjectMode' in body).toBe(false);
  });

  it('sends the model when one is picked, and the speech file only when one person alone is chosen', () => {
    const picked = { ...FORM, capabilityId: 'cap-1', speechFileId: 'audio-1' };
    expect(buildSubjectClipRequest(picked, true)).toMatchObject({ capabilityId: 'cap-1', speechFileId: 'audio-1' });
    expect('speechFileId' in buildSubjectClipRequest(picked, false)).toBe(false);
  });

  it('keeps the length and the takes inside what the server takes', () => {
    expect(buildSubjectClipRequest({ ...FORM, durationSeconds: 99, takes: 7 }, false)).toMatchObject({ durationSeconds: 15, takes: 2 });
    expect(buildSubjectClipRequest({ ...FORM, durationSeconds: 1, takes: 0 }, false)).toMatchObject({ durationSeconds: 4, takes: 1 });
    expect(buildSubjectClipRequest({ ...FORM, durationSeconds: Number.NaN, takes: Number.NaN }, false)).toMatchObject({ durationSeconds: 6, takes: 1 });
  });

  it('says what is missing before anything is sent', () => {
    expect(subjectClipBlocker({ subjectIds: [], prompt: 'x' })).toBe('noSubject');
    expect(subjectClipBlocker({ subjectIds: ['a'], prompt: '   ' })).toBe('noPrompt');
    expect(subjectClipBlocker({ subjectIds: ['a'], prompt: 'x'.repeat(SUBJECT_CLIP_PROMPT_MAX + 1) })).toBe('promptTooLong');
    expect(subjectClipBlocker({ subjectIds: ['a'], prompt: 'x'.repeat(SUBJECT_CLIP_PROMPT_MAX) })).toBeNull();
  });
});

describe('the routes', () => {
  it('posts the estimate and the start, reads a run and stops one under the project', async () => {
    const calls: { url: string; method: string; body: unknown }[] = [];
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, method: init?.method ?? 'GET', body: init?.body ? JSON.parse(String(init.body)) : undefined });
      return { ok: true, status: 200, json: async () => (url.endsWith('/e 1') ? { executionId: 'e 1', state: 'ready' } : { executionId: 'e1', state: 'queued' }) } as Response;
    }));
    const body = buildSubjectClipRequest(FORM, false);
    await estimateSubjectClip('p 1', body);
    await startSubjectClip('p 1', body);
    const read = await getSubjectClip('p 1', 'e 1');
    await cancelSubjectClip('p 1', 'e 1');
    expect(calls.map((c) => `${c.method} ${c.url}`)).toEqual([
      'POST /api/v1/projects/p%201/subject-clips/estimate',
      'POST /api/v1/projects/p%201/subject-clips',
      'GET /api/v1/projects/p%201/subject-clips/e%201',
      'POST /api/v1/projects/p%201/subject-clips/e%201/cancel',
    ]);
    expect(calls[0].body).toEqual(body);
    expect(calls[1].body).toEqual(body);
    // a server that leaves the list out answers an empty one
    expect(read.outputFileIds).toEqual([]);
  });

  it('treats "already finished" on cancel as an answer, and passes any other failure on', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: false, status: 409, statusText: 'Conflict', headers: new Headers(), json: async () => ({ code: 'RUN_ALREADY_FINISHED' }) }) as unknown as Response));
    expect(await cancelSubjectClip('p', 'e')).toBeNull();
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: false, status: 500, statusText: 'Server Error', headers: new Headers(), json: async () => ({}) }) as unknown as Response));
    await expect(cancelSubjectClip('p', 'e')).rejects.toBeInstanceOf(ApiError);
  });
});

describe('refusals', () => {
  it('sorts each refusal the server can give', () => {
    expect(describeSubjectClipRefusal(refusal(409, { code: 'CONSENT_REQUIRED' }))).toEqual({ kind: 'consent' });
    expect(describeSubjectClipRefusal(refusal(402, { code: 'PLAN_NOT_ENTITLED', feature: 'video_generation' }))).toEqual({ kind: 'plan', feature: 'video_generation' });
    expect(describeSubjectClipRefusal(refusal(402, { code: 'PLAN_NOT_ENTITLED', feature: 'avatars' }))).toEqual({ kind: 'plan', feature: 'avatars' });
    expect(describeSubjectClipRefusal(refusal(402, { code: 'PLAN_NOT_ENTITLED', feature: 'avatar_objects' }))).toEqual({ kind: 'plan', feature: 'avatar_objects' });
    expect(describeSubjectClipRefusal(refusal(402, { code: 'PLAN_NOT_ENTITLED', feature: 'something_new' }))).toEqual({ kind: 'plan' });
    expect(describeSubjectClipRefusal(refusal(402, { code: 'INSUFFICIENT_CREDITS', message: 'You need 12 more credits.', missing: 12 }))).toEqual({ kind: 'credits', message: 'You need 12 more credits.' });
    expect(describeSubjectClipRefusal(refusal(403, { code: 'EMAIL_NOT_VERIFIED' }))).toEqual({ kind: 'email' });
    expect(describeSubjectClipRefusal(refusal(409, { code: 'AVATAR_GENERATION_DISABLED', message: 'Clips that show a person are not available.' })))
      .toEqual({ kind: 'personOff' });
    expect(describeSubjectClipRefusal(refusal(409, { code: 'AVATAR_NOT_USABLE', reason: 'consent_revoked' }))).toEqual({ kind: 'avatar', reason: 'consent_revoked' });
    expect(describeSubjectClipRefusal(refusal(409, { code: 'AVATAR_NOT_USABLE' }))).toEqual({ kind: 'avatar' });
    expect(describeSubjectClipRefusal(refusal(404, {}))).toEqual({ kind: 'off' });
    expect(describeSubjectClipRefusal(refusal(500, {}))).toEqual({ kind: 'other' });
    expect(describeSubjectClipRefusal(new Error('network'))).toEqual({ kind: 'other' });
  });

  it('names the model of the same family that can show pictures, however the body carries it', () => {
    expect(describeSubjectClipRefusal(refusal(409, { code: 'AVATAR_NOT_SUPPORTED_BY_MODEL', sibling: 'MiniMax-H3' }))).toEqual({ kind: 'model', sibling: 'MiniMax-H3' });
    expect(describeSubjectClipRefusal(refusal(409, { code: 'AVATAR_NOT_SUPPORTED_BY_MODEL', sibling: { model: 'kling/i2v', name: 'Kling' } }))).toEqual({ kind: 'model', sibling: 'kling/i2v' });
    expect(describeSubjectClipRefusal(refusal(409, { code: 'AVATAR_NOT_SUPPORTED_BY_MODEL' }))).toEqual({ kind: 'model' });
  });

  it('reads the run rate limit by code or by status, with the wait when the server gave one', () => {
    expect(describeSubjectClipRefusal(refusal(429, { code: 'RUN_RATE_LIMITED' }))).toEqual({ kind: 'busy' });
    const limited = refusal(429, {});
    limited.retryAfterSeconds = 90;
    expect(describeSubjectClipRefusal(limited)).toEqual({ kind: 'busy', retryAfterSeconds: 90 });
  });

  it('keeps the sentence and the list of a validation refusal', () => {
    expect(describeSubjectClipRefusal(refusal(400, { message: 'Fix these.', errors: ['The length must be 4 to 15 seconds.'] }, ['The length must be 4 to 15 seconds.']))).toEqual({
      kind: 'invalid',
      message: 'Fix these.',
      errors: ['The length must be 4 to 15 seconds.'],
    });
  });
});

describe('waiting for a run', () => {
  it('polls until ready and reports progress on the way', async () => {
    const answers = [status({ state: 'queued' }), status({ state: 'running' }), status({ state: 'ready', outputFileIds: ['f1', 'f2'] })];
    const fetchStatus = vi.fn(async () => answers.shift()!);
    const seen: string[] = [];
    const done = await waitForSubjectClip('p', 'e1', { intervalMs: 1, fetchStatus, onProgress: (s) => seen.push(s.state) });
    expect(done.outputFileIds).toEqual(['f1', 'f2']);
    expect(seen).toEqual(['queued', 'running']);
    expect(fetchStatus).toHaveBeenCalledTimes(3);
  });

  it('ends with the server\'s own sentence when the run failed or was cancelled', async () => {
    const failed = waitForSubjectClip('p', 'e1', { intervalMs: 1, fetchStatus: async () => status({ state: 'failed', message: 'The model refused the prompt.', failureCode: 'CONTENT_BLOCKED' }) });
    await expect(failed).rejects.toBeInstanceOf(SubjectClipFailedError);
    await expect(failed).rejects.toMatchObject({ message: 'The model refused the prompt.', status: { failureCode: 'CONTENT_BLOCKED' } });
    await expect(waitForSubjectClip('p', 'e1', { intervalMs: 1, fetchStatus: async () => status({ state: 'cancelled' }) })).rejects.toMatchObject({ status: { state: 'cancelled' } });
  });

  it('keeps going after a failed read, waiting longer each time up to the cap, and stops at a 404', async () => {
    const waits: number[] = [];
    const realTimeout = globalThis.setTimeout;
    vi.stubGlobal('setTimeout', ((fn: () => void, ms?: number) => {
      waits.push(ms ?? 0);
      return realTimeout(fn, 0);
    }) as typeof setTimeout);
    let n = 0;
    const fetchStatus = vi.fn(async () => {
      n += 1;
      if (n <= 4) throw new Error('offline');
      return status({ state: 'ready', outputFileIds: ['f1'] });
    });
    const done = await waitForSubjectClip('p', 'e1', { intervalMs: 2000, maxBackoffMs: 15_000, fetchStatus });
    expect(done.state).toBe('ready');
    expect(waits).toEqual([4000, 8000, 15_000, 15_000]);
    vi.unstubAllGlobals();
    const gone = new ApiError('gone', 404, [], {});
    await expect(waitForSubjectClip('p', 'e1', { intervalMs: 1, fetchStatus: async () => { throw gone; } })).rejects.toBe(gone);
  });

  it('stops when told to, and gives up after the timeout', async () => {
    const controller = new AbortController();
    const waiting = waitForSubjectClip('p', 'e1', { intervalMs: 1, signal: controller.signal, fetchStatus: async () => status() });
    controller.abort();
    await expect(waiting).rejects.toMatchObject({ name: 'AbortError' });
    await expect(waitForSubjectClip('p', 'e1', { intervalMs: 1, timeoutMs: -1, fetchStatus: async () => status() })).rejects.toMatchObject({ status: { state: 'failed', failureCode: 'timeout' } });
  });
});

describe('the starting shape and the sizes', () => {
  it('starts on the offered shape nearest to the timeline', () => {
    expect(nearestSubjectClipRatio({ width: 1920, height: 1080 })).toBe('16:9');
    expect(nearestSubjectClipRatio({ width: 1080, height: 1920 })).toBe('9:16');
    expect(nearestSubjectClipRatio({ width: 1080, height: 1080 })).toBe('1:1');
    expect(nearestSubjectClipRatio({ width: 1080, height: 1350 })).toBe('4:5');
    expect(nearestSubjectClipRatio({ width: 2560, height: 1080 })).toBe('16:9');
    expect(nearestSubjectClipRatio({ width: 1000, height: 1100 })).toBe('1:1');
    expect(nearestSubjectClipRatio(null)).toBe('16:9');
    expect(nearestSubjectClipRatio({ width: 0, height: 0 })).toBe('16:9');
  });

  it('offers the sizes the chosen model takes, and every size when the model is not known', () => {
    expect(resolutionsForModel('MiniMax-H3')).toEqual(['768P', '2K']);
    expect(resolutionsForModel('MiniMax-H3-Max')).toEqual(['768P', '480P']);
    expect(resolutionsForModel('wan2.2-ti2v-5b-i2v')).toEqual(['480P', '720P']);
    expect(resolutionsForModel(null)).toEqual(['768P', '480P', '720P', '2K']);
    expect(resolutionsForModel('kling-video/v3.0/std/image-to-video')[0]).toBe('768P');
  });
});
