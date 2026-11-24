/**
 * QA probes (T-104), each pinned to the server's behaviour at 7e53c803c:
 * - the plan's resolution cap is checked on the output SHORT side (VideoEnhanceService.PlanAsync + VideoEnhanceSizing),
 *   so a portrait output 1440x2560 under a 1440 cap is allowed by the server;
 * - one failed status poll must not end the client's tracking of a run the server keeps running (and holding credits for).
 */
import { describe, expect, it, vi } from 'vitest';
import { waitForEnhance } from '../enhance-api';
import { exceedsPlanHeight, type EnhanceEstimate, type EnhanceJob } from '../types';

const EST: EnhanceEstimate = { billingEnabled: true, hosted: false, credits: 0, seconds: 10, balanceCredits: 10, affordable: true, unavailableReason: null, consented: true, maxOutputHeight: 1440, outputWidth: 1440, outputHeight: 2560, outputFps: 30, estimatedBytes: 1, estimatedProcessingSeconds: 1 };

describe('QA probe: plan cap uses the output short side like the server', () => {
  it('a portrait 1440x2560 result under a 1440 cap is not refused on the client', () => {
    expect(exceedsPlanHeight(EST)).toBe(false);
  });
  it('a landscape 2560x1440 result under a 1440 cap is not refused either (control)', () => {
    expect(exceedsPlanHeight({ ...EST, outputWidth: 2560, outputHeight: 1440 })).toBe(false);
  });
});

describe('QA probe: a transient poll error does not drop the run', () => {
  it('keeps polling after one failed GET and returns the ready job', async () => {
    const ready: EnhanceJob = { executionId: 'e1', state: 'ready', op: 'Upscale', engine: 'Hosted', outputFileId: 'o', sourceMediaRef: 'file:x' };
    const fetchJob = vi.fn().mockRejectedValueOnce(Object.assign(new Error('Bad gateway'), { status: 502 })).mockResolvedValue(ready);
    await expect(waitForEnhance('p', 'file:x', 'e1', { intervalMs: 1, fetchJob })).resolves.toEqual(ready);
  });
});
