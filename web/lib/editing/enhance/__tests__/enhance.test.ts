/** @vitest-environment node */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apply, type EditOp } from '../../engine';
import { assetRef, doc, media, track } from '../../__tests__/fixtures';
import type { EditTimeline } from '../../types';
import { locate } from '../../link/groups';
import en from '@/messages/en/editor-enhance.json';
import it_ from '@/messages/it/editor-enhance.json';

const apiFetch = vi.fn();
vi.mock('../../../api/client', async (orig) => ({ ...(await orig<typeof import('../../../api/client')>()), apiFetch: (...a: unknown[]) => apiFetch(...a) }));

import { cancelEnhance, deleteEnhanceConsent, describeEnhanceRefusal, describeEstimateUnavailable, describeEstimateUnavailableReason, EnhanceFailedError, estimateEnhance, getEnhanceConsent, getEnhanceJob, putEnhanceConsent, startEnhance, waitForEnhance } from '../enhance-api';
import { clipsUsingSource, enhanceSubject, planApply, planApplyToClip, planRevert } from '../plan';
import { VIDEO_ENHANCE_SCHEMA } from '../../../schemas/workflow-step-configs';
import { extendMarginsEmpty, extendMargins, extendSidesOn, extendAmountOf, selectionOf, DEFAULT_SELECTION, failureKey, progressPercent, multiplierGate, requestOf, upscaleGate, withEngine, exceedsPlanHeight, type EnhanceEstimate } from '../types';

const run = (d: EditTimeline, ops: EditOp[]): EditTimeline => ops.reduce((acc, op) => apply(acc, op).doc, d);
const OUT = 'aaaaaaaa-0000-4000-8000-000000000009';
const SRC = 'file:00000000-0000-4000-8000-000000000001';

beforeEach(() => apiFetch.mockReset());

describe('request building', () => {
  it('maps every choice and option to the contract body', () => {
    expect(requestOf({ ...DEFAULT_SELECTION, choice: 'upscale', upscale: 'x4' })).toEqual({ op: 'Upscale', engine: 'Local', upscaleFactor: 4 });
    expect(requestOf({ ...DEFAULT_SELECTION, choice: 'upscale', upscale: 'h1080', engine: 'Hosted' })).toEqual({ op: 'Upscale', engine: 'Hosted', targetHeight: 1080 });
    expect(requestOf({ ...DEFAULT_SELECTION, choice: 'upscale', upscale: 'h2160' })).toEqual({ op: 'Upscale', engine: 'Local', targetHeight: 2160 });
    expect(requestOf({ ...DEFAULT_SELECTION, choice: 'interpolate', multiplier: 8 })).toEqual({ op: 'Interpolate', engine: 'Local', frameMultiplier: 8 });
    expect(requestOf({ ...DEFAULT_SELECTION, choice: 'denoise', strength: 'Strong', keepGrain: false })).toEqual({ op: 'Denoise', engine: 'Local', denoiseStrength: 'Strong', keepGrain: false });
    expect(requestOf({ ...DEFAULT_SELECTION, choice: 'motionBlur', shutter: 'light' })).toEqual({ op: 'MotionBlur', engine: 'Local', shutterAngle: 90 });
    expect(requestOf(DEFAULT_SELECTION.choice === 'upscale' ? { ...DEFAULT_SELECTION, choice: 'motionBlur' } : DEFAULT_SELECTION).shutterAngle).toBe(180);
  });
  it('noise and motion blur are always Fast, even if AI was picked before', () => {
    expect(requestOf({ ...DEFAULT_SELECTION, choice: 'denoise', engine: 'Hosted' }).engine).toBe('Local');
    expect(requestOf({ ...DEFAULT_SELECTION, choice: 'motionBlur', engine: 'Hosted' }).engine).toBe('Local');
  });
  it('AI slow motion stops at 4x', () => {
    expect(withEngine({ ...DEFAULT_SELECTION, multiplier: 8 }, 'Hosted').multiplier).toBe(4);
    expect(withEngine({ ...DEFAULT_SELECTION, multiplier: 2 }, 'Hosted').multiplier).toBe(2);
    expect(multiplierGate(8, 'Hosted')).toBe('engine');
    expect(multiplierGate(8, 'Local')).toBeNull();
    expect(requestOf({ ...DEFAULT_SELECTION, choice: 'interpolate', engine: 'Hosted', multiplier: 8 }).frameMultiplier).toBe(4);
  });
});

describe('option gating by plan height', () => {
  it('disables sizes above the plan cap and keeps the rest', () => {
    expect(upscaleGate('h2160', 1080)).toBe('plan');
    expect(upscaleGate('h1080', 1080)).toBeNull();
    expect(upscaleGate('x4', 1080, 720)).toBe('plan');
    expect(upscaleGate('x2', 1080, 480)).toBeNull();
    expect(upscaleGate('x4', 1080)).toBeNull();
    expect(upscaleGate('h2160', null)).toBeNull();
  });
  it('flags an estimate that exceeds the plan cap', () => {
    const e = { maxOutputHeight: 1080, outputHeight: 2160 } as EnhanceEstimate;
    expect(exceedsPlanHeight(e)).toBe(true);
    expect(exceedsPlanHeight({ ...e, outputHeight: 1080 })).toBe(false);
  });
});

describe('api wrappers', () => {
  it('posts start and estimate and reads the job at the contract paths', async () => {
    apiFetch.mockResolvedValue({});
    await startEnhance('p1', 'file:g1', { op: 'Upscale', engine: 'Local', upscaleFactor: 2 });
    await estimateEnhance('p1', 'file:g1', { op: 'Denoise', engine: 'Local' });
    await getEnhanceJob('p1', 'file:g1', 'e1');
    await cancelEnhance('p1', 'file:g1', 'e1');
    expect(apiFetch.mock.calls[0][0]).toBe('/api/v1/projects/p1/media/file%3Ag1/enhance');
    expect(apiFetch.mock.calls[0][1].method).toBe('POST');
    expect(JSON.parse(apiFetch.mock.calls[0][1].body)).toEqual({ op: 'Upscale', engine: 'Local', upscaleFactor: 2 });
    expect(apiFetch.mock.calls[1][0]).toBe('/api/v1/projects/p1/media/file%3Ag1/enhance/estimate');
    expect(apiFetch.mock.calls[2][0]).toBe('/api/v1/projects/p1/media/file%3Ag1/enhance/e1');
    expect(apiFetch.mock.calls[3][0]).toBe('/api/v1/projects/p1/media/file%3Ag1/enhance/e1/cancel');
    expect(apiFetch.mock.calls[3][1].method).toBe('POST');
  });
  it('reads, records and withdraws the project consent on the server', async () => {
    apiFetch.mockResolvedValue({ consented: true, consentedAt: 'x' });
    await getEnhanceConsent('p1');
    await putEnhanceConsent('p1');
    await deleteEnhanceConsent('p1');
    expect(apiFetch.mock.calls.map((c) => [c[0], c[1]?.method])).toEqual([
      ['/api/v1/projects/p1/video-enhance/consent', undefined],
      ['/api/v1/projects/p1/video-enhance/consent', 'PUT'],
      ['/api/v1/projects/p1/video-enhance/consent', 'DELETE'],
    ]);
  });
  it('polls until ready, reports progress, and throws EnhanceFailedError on failure', async () => {
    const onProgress = vi.fn();
    const fetchJob = vi.fn().mockResolvedValueOnce({ state: 'running', progress: 40 }).mockResolvedValueOnce({ state: 'ready', outputFileId: 'o' });
    await expect(waitForEnhance('p', 'file:g', 'e', { intervalMs: 1, fetchJob, onProgress })).resolves.toMatchObject({ outputFileId: 'o' });
    expect(onProgress).toHaveBeenCalledWith(expect.objectContaining({ progress: 40 }));
    const failing = vi.fn().mockResolvedValue({ state: 'failed', failureCode: 'ENHANCE_DURATION_MISMATCH' });
    await expect(waitForEnhance('p', 'file:g', 'e', { intervalMs: 1, fetchJob: failing })).rejects.toBeInstanceOf(EnhanceFailedError);
  });
  it('aborts a poll', async () => {
    const c = new AbortController();
    c.abort();
    await expect(waitForEnhance('p', 'file:g', 'e', { signal: c.signal, fetchJob: vi.fn() })).rejects.toMatchObject({ name: 'AbortError' });
  });
});

describe('refusals', () => {
  const err = (status: number, body: Record<string, unknown>) => ({ status, body });
  it('sorts each refusal into the kind the UI words', () => {
    expect(describeEnhanceRefusal(err(402, { code: 'INSUFFICIENT_CREDITS', missing: 2.04 }))).toEqual({ kind: 'credits', missing: 2.1 });
    expect(describeEnhanceRefusal(err(402, { code: 'PLAN_NOT_ENTITLED', feature: 'video_enhance' }))).toEqual({ kind: 'plan', feature: 'video_enhance' });
    expect(describeEnhanceRefusal(err(402, { code: 'PLAN_NOT_ENTITLED', feature: 'resolution' }))).toEqual({ kind: 'plan', feature: 'resolution' });
    expect(describeEnhanceRefusal(err(409, { code: 'CONSENT_REQUIRED' }))).toEqual({ kind: 'consent' });
    expect(describeEnhanceRefusal(err(409, { code: 'ENHANCE_UNAVAILABLE' }))).toEqual({ kind: 'unavailable' });
    expect(describeEnhanceRefusal(err(413, { code: 'SOURCE_TOO_LONG', maxSeconds: 600 }))).toEqual({ kind: 'tooLong', maxSeconds: 600 });
    expect(describeEnhanceRefusal(err(429, { code: 'RUN_RATE_LIMITED' }))).toEqual({ kind: 'busy' });
    expect(describeEnhanceRefusal(err(400, { code: 'NOT_VIDEO' }))).toEqual({ kind: 'notVideo' });
    expect(describeEnhanceRefusal(err(403, { code: 'EMAIL_NOT_VERIFIED' }))).toEqual({ kind: 'email' });
    expect(describeEnhanceRefusal(err(400, { code: 'ENHANCE_REQUEST_INVALID' }))).toEqual({ kind: 'invalid' });
    expect(describeEnhanceRefusal(err(422, { code: 'ENHANCE_SOURCE_NOT_VIDEO' }))).toEqual({ kind: 'notVideo' });
    expect(describeEnhanceRefusal(err(409, { code: 'SOURCE_NOT_MEASURED' }))).toEqual({ kind: 'notMeasured' });
    expect(describeEnhanceRefusal(err(404, {}))).toEqual({ kind: 'notFound' });
    expect(describeEnhanceRefusal(new Error('x'))).toEqual({ kind: 'other' });
  });
  it('sorts an estimate by its code, and ignores the sentence when a code is present', () => {
    expect(describeEstimateUnavailable('PLAN_NOT_ENTITLED', 'anything')).toEqual({ kind: 'plan', feature: 'video_enhance' });
    expect(describeEstimateUnavailable('RESOLUTION_ABOVE_PLAN', 'too long')).toEqual({ kind: 'plan', feature: 'resolution' });
    expect(describeEstimateUnavailable('SOURCE_TOO_LONG', 'plan')).toEqual({ kind: 'tooLong' });
    expect(describeEstimateUnavailable('SOURCE_TOO_SHORT', 'plan')).toEqual({ kind: 'tooShort' });
    expect(failureKey('ENHANCE_SOURCE_TOO_SHORT')).toBe('tooShort');
    expect(describeEstimateUnavailable('SOURCE_NOT_MEASURED', null)).toEqual({ kind: 'notMeasured' });
    expect(describeEstimateUnavailable('ENHANCE_UNAVAILABLE', 'plan')).toEqual({ kind: 'unavailable' });
    expect(describeEstimateUnavailable('CONSENT_REQUIRED', null)).toEqual({ kind: 'consent' });
    expect(describeEstimateUnavailable(null, 'Your plan does not include this.').kind).toBe('plan');
    expect(describeEstimateUnavailable(undefined, undefined)).toEqual({ kind: 'other' });
  });
  it('sorts an estimate sentence', () => {
    expect(describeEstimateUnavailableReason('Your plan does not include this.').kind).toBe('plan');
    expect(describeEstimateUnavailableReason('This video is too long.').kind).toBe('tooLong');
    expect(describeEstimateUnavailableReason('Something else').kind).toBe('other');
  });
});

const two = (): EditTimeline =>
  doc([track('V1', 'video', [media('c1', 0, 100, 10), media('c2', 100, 200, 0)]), track('A1', 'audio', [media('a1', 0, 100, 10)])]);

describe('apply / revert plans', () => {
  it('finds the picture clip for a project file and refuses an audio-track clip', () => {
    expect(enhanceSubject(two(), 'c1')?.mediaRef).toBe(SRC);
    expect(enhanceSubject(two(), 'a1')).toBeNull();
    expect(enhanceSubject(two(), 'nope')).toBeNull();
  });
  it('also accepts a render (step result) source', () => {
    const d = doc([track('V1', 'video', [media('c1', 0, 100, 0, { assetRef: { kind: 'stepResult', stepResultId: 's1' } })])]);
    expect(enhanceSubject(d, 'c1')?.mediaRef).toBe('step_result:s1');
  });
  it('switches every clip still on the source in ONE batch and keeps durations and trims', () => {
    const d = two();
    const plan = planApply(d, SRC, OUT)!;
    expect(plan.ops).toHaveLength(3);
    const after = run(d, plan.ops);
    for (const id of ['c1', 'c2', 'a1']) {
      const c = locate(after, id)!.item as { assetRef: unknown; recordIn: number; recordOut: number; sourceIn: number };
      expect(c.assetRef).toEqual({ kind: 'projectFile', projectFileId: OUT });
      const was = locate(d, id)!.item as { recordIn: number; recordOut: number; sourceIn: number };
      expect([c.recordIn, c.recordOut, c.sourceIn]).toEqual([was.recordIn, was.recordOut, was.sourceIn]);
    }
  });
  it('leaves a clip that was switched to another source alone', () => {
    const d = doc([track('V1', 'video', [media('c1', 0, 100, 0), media('c2', 100, 200, 0, { assetRef: assetRef(2) })])]);
    const plan = planApply(d, SRC, OUT)!;
    expect(plan.ops).toHaveLength(1);
    expect(clipsUsingSource(d, SRC).map((c) => c.id)).toEqual(['c1']);
  });
  it('returns null when the clip was deleted or no clip uses the source', () => {
    const d = doc([track('V1', 'video', [media('c2', 0, 100, 0, { assetRef: assetRef(2) })])]);
    expect(planApply(d, SRC, OUT)).toBeNull();
    expect(planApplyToClip(d, 'gone', OUT)).toBeNull();
  });
  it('"Apply to this clip" switches only that clip, even when it moved to another source', () => {
    const d = doc([track('V1', 'video', [media('c2', 0, 100, 0, { assetRef: assetRef(2) })])]);
    const plan = planApplyToClip(d, 'c2', OUT)!;
    expect(plan.ops).toHaveLength(1);
  });
  it('is a no-op when everything already plays the enhanced file', () => {
    const d = two();
    const applied = run(d, planApply(d, SRC, OUT)!.ops);
    expect(planApply(applied, SRC, OUT)).toBeNull();
  });
  it('"Use original" restores the exact previous refs and skips clips changed since', () => {
    const d = two();
    const plan = planApply(d, SRC, OUT)!;
    const applied = run(d, plan.ops);
    const back = planRevert(applied, plan.record)!;
    expect(run(applied, back.ops)).toEqual(d);
    const moved = run(applied, [{ type: 'setProperty', itemId: 'c1', key: 'assetRef', value: assetRef(3) } as EditOp]);
    expect(planRevert(moved, plan.record)!.ops).toHaveLength(2);
  });
});

describe('progress scale', () => {
  it('reads the server percent: 0 queued, null running (unknown), never 100 while in progress', () => {
    expect(progressPercent(0)).toBe(0);
    expect(progressPercent(40)).toBe(40);
    expect(progressPercent(null)).toBeNull();
    expect(progressPercent(undefined)).toBeNull();
    expect(progressPercent(100)).toBe(99);
    expect(progressPercent(250)).toBe(99);
    expect(progressPercent(-5)).toBe(0);
  });
});

describe('cancel', () => {
  it('treats 409 ENHANCE_ALREADY_FINISHED as done and rethrows anything else', async () => {
    const { ApiError } = await import('../../../api/client');
    apiFetch.mockRejectedValueOnce(new ApiError('done', 409, [], { code: 'ENHANCE_ALREADY_FINISHED' }));
    await expect(cancelEnhance('p', 'file:g', 'e')).resolves.toBeNull();
    apiFetch.mockRejectedValueOnce(new ApiError('boom', 500));
    await expect(cancelEnhance('p', 'file:g', 'e')).rejects.toBeTruthy();
  });
});

describe('step config schema', () => {
  const props = (VIDEO_ENHANCE_SCHEMA.properties ?? {}) as Record<string, { type?: unknown; minimum?: number; maximum?: number; exclusiveMinimum?: number }>;
  it('carries the optional source hints the route writes, nullable, never required', () => {
    for (const k of ['sourceWidth', 'sourceHeight', 'sourceFps']) {
      expect(props[k]).toBeTruthy();
      expect(props[k].type).toContain('null');
      expect(VIDEO_ENHANCE_SCHEMA.required).not.toContain(k);
    }
    expect([props.sourceWidth.minimum, props.sourceWidth.maximum]).toEqual([1, 16384]);
    expect([props.sourceHeight.minimum, props.sourceHeight.maximum]).toEqual([1, 16384]);
    expect([props.sourceFps.exclusiveMinimum, props.sourceFps.maximum]).toEqual([0, 240]);
    expect(VIDEO_ENHANCE_SCHEMA.additionalProperties).toBe(false);
  });
});

describe('T-022 fixes', () => {
  it('failureKey names every code the server can send, and only unknown ones are generic', () => {
    const codes = ['ENHANCE_SOURCE_TOO_LONG', 'ENHANCE_SOURCE_TOO_SHORT', 'ENHANCE_SOURCE_NO_VIDEO', 'ENHANCE_OUTPUT_TOO_LARGE', 'ENHANCE_FPS_TOO_HIGH', 'ENHANCE_NOT_LARGER', 'ENHANCE_INVALID_REQUEST', 'ENHANCE_REQUEST_INVALID', 'ENHANCE_PROBE_FAILED', 'ENHANCE_FFMPEG_FAILED', 'ENHANCE_PICTURE_INVALID', 'ENHANCE_DURATION_MISMATCH', 'ENHANCE_EGRESS_UNAVAILABLE', 'ENHANCE_UPLOAD_FAILED', 'ENHANCE_VENDOR_QUOTA', 'ENHANCE_VENDOR_RETRYABLE', 'ENHANCE_VENDOR_FAILED', 'ENHANCE_VENDOR_REFUSED', 'ENHANCE_PROVIDER', 'ENHANCE_PROVIDER_CONFIG', 'ENHANCE_SOURCE_CHANGED', 'ENHANCE_STILL_RUNNING', 'OUTPUT_UNAVAILABLE', 'timeout'];
    for (const c of codes) expect(failureKey(c), c).not.toBe('failed');
    expect(failureKey('SOMETHING_NEW')).toBe('failed');
    expect(failureKey(null)).toBe('failed');
  });
  it('retries transient poll errors with backoff and ends only on a 404', async () => {
    const ready = { executionId: 'e', state: 'ready', op: 'Upscale', engine: 'Local', outputFileId: 'o', sourceMediaRef: 'file:x' };
    const flaky = vi.fn().mockRejectedValueOnce({ status: 502 }).mockRejectedValueOnce({ status: 500 }).mockResolvedValue(ready);
    await expect(waitForEnhance('p', 'file:x', 'e', { intervalMs: 1, maxBackoffMs: 2, fetchJob: flaky })).resolves.toEqual(ready);
    expect(flaky).toHaveBeenCalledTimes(3);
    const gone = vi.fn().mockRejectedValue({ status: 404 });
    await expect(waitForEnhance('p', 'file:x', 'e', { intervalMs: 1, fetchJob: gone })).rejects.toMatchObject({ status: 404 });
    expect(gone).toHaveBeenCalledTimes(1);
  });
  it('checks the plan cap on the short side of the output', () => {
    const e = { maxOutputHeight: 1080, outputWidth: 1080, outputHeight: 1920 } as EnhanceEstimate;
    expect(exceedsPlanHeight(e)).toBe(false);
    expect(exceedsPlanHeight({ ...e, outputWidth: 1440, outputHeight: 2560 })).toBe(true);
    expect(exceedsPlanHeight({ ...e, outputWidth: null, outputHeight: 1920 })).toBe(true);
  });
});

describe('path segments are encoded', () => {
  it('encodes the project id and the media ref in every enhance URL', async () => {
    apiFetch.mockResolvedValue({});
    await startEnhance('p/1?x', 'file:a/b', { op: 'Denoise', engine: 'Local' });
    await getEnhanceJob('p/1?x', 'file:a/b', 'e/1');
    await cancelEnhance('p/1?x', 'file:a/b', 'e/1');
    await getEnhanceConsent('p/1?x');
    expect(apiFetch.mock.calls.map((c) => c[0])).toEqual([
      '/api/v1/projects/p%2F1%3Fx/media/file%3Aa%2Fb/enhance',
      '/api/v1/projects/p%2F1%3Fx/media/file%3Aa%2Fb/enhance/e%2F1',
      '/api/v1/projects/p%2F1%3Fx/media/file%3Aa%2Fb/enhance/e%2F1/cancel',
      '/api/v1/projects/p%2F1%3Fx/video-enhance/consent',
    ]);
  });
});

describe('extend request', () => {
  it('sends a shape and a trimmed description, hosted only', () => {
    expect(requestOf({ ...DEFAULT_SELECTION, choice: 'extend', extendMode: 'aspect', extendAspect: '1:1', prompt: '  a beach  ' })).toEqual({ op: 'Extend', engine: 'Hosted', targetAspect: '1:1', prompt: 'a beach' });
  });
  it('sends the chosen sides at ONE clamped amount (Wan has one expand ratio) and no prompt when blank', () => {
    // Uneven stored margins collapse to the largest amount on the sides that grow; a negative or missing side is off.
    expect(requestOf({ ...DEFAULT_SELECTION, choice: 'extend', extendMode: 'sides', marginLeft: 0.9, marginRight: 0.1, marginTop: 0, marginBottom: -1 }))
      .toEqual({ op: 'Extend', engine: 'Hosted', marginLeft: 0.5, marginRight: 0.5, marginTop: 0, marginBottom: 0 });
    expect(extendMargins(['left', 'top'], 0.3)).toEqual({ marginLeft: 0.3, marginRight: 0, marginTop: 0.3, marginBottom: 0 });
    expect(extendSidesOn({ marginLeft: 0.2, marginRight: 0, marginTop: 0.2, marginBottom: null })).toEqual(['left', 'top']);
    expect(extendAmountOf({ marginLeft: 0, marginRight: 0, marginTop: 0, marginBottom: 0 })).toBe(0.2);
    expect(extendAmountOf({ marginLeft: 0.1, marginRight: 0.3 })).toBe(0.3);
  });
  it('defaults to a shape every ordinary source can reach, and only an Extend request restores the sides mode', () => {
    expect(DEFAULT_SELECTION.extendAspect).toBe('1:1');
    expect(selectionOf({ op: 'Upscale', engine: 'Local', upscaleFactor: 2 }).extendMode).toBe('aspect');
    expect(selectionOf({ op: 'Extend', engine: 'Hosted', marginLeft: 0.2 }).extendMode).toBe('sides');
    expect(selectionOf({ op: 'Extend', engine: 'Hosted', targetAspect: '4:5' }).extendMode).toBe('aspect');
  });
  it('an unknown submit outcome has its own honest sentence in both languages, with no vendor name', () => {
    expect(failureKey('ENHANCE_SUBMIT_OUTCOME_UNKNOWN')).toBe('submitUnknown');
    expect(failureKey('SUBMIT_OUTCOME_UNKNOWN')).toBe('submitUnknown');
    for (const m of [en, it_]) {
      const text = (m as { failure: Record<string, string> }).failure.submitUnknown;
      expect(text).toBeTruthy();
      expect(text.toLowerCase()).not.toMatch(/fal|wan|luma|vendor/);
    }
  });
  it('maps a blocked prompt and an impossible extension to their own plain sentences', () => {
    expect(failureKey('CONTENT_BLOCKED')).toBe('contentBlocked');
    expect(failureKey('CONFIG_INVALID')).toBe('configInvalid');
  });
  it('flags empty sides', () => {
    expect(extendMarginsEmpty({ marginLeft: 0, marginRight: 0, marginTop: 0, marginBottom: 0 })).toBe(true);
    expect(extendMarginsEmpty({ marginLeft: 0.1, marginRight: 0, marginTop: 0, marginBottom: 0 })).toBe(false);
  });
});
