import { describe, expect, it } from 'vitest';
import type { EditTimelineRenderSummary } from '@/lib/editing/sync/api';
import {
  aspectLabel, buildRenderRequest, creditsMissing, fitWarning, formatEstimate, formatLength, formatName, latestFinished,
  missingSentence, qualityEncode, renderStatusLine, saveFailureSentence, statusWord,
} from '../deliver-model';
import { visibleRenders } from '../DeliverWorkspace';
import { describeRenderRefusal } from '../render-refusal';
import { ApiError } from '@/lib/api/client';

const base: EditTimelineRenderSummary = {
  executionId: 'e', workflowId: 'w', timelineId: 't', status: 'Passed', startedAt: null, completedAt: '2026-10-10T10:00:00Z', revision: 4,
  preset: 'Hd1080p16x9', outputFileName: 'a.mp4', errorMessage: null, stepResultId: 's', hasOutput: true, regions: null,
};

describe('format names', () => {
  it('names every preset in plain words', () => {
    expect(formatName('Hd1080p16x9')).toBe('Full HD landscape');
    expect(formatName('Uhd4k16x9')).toBe('4K landscape');
    expect(formatName('Vertical9x16')).toBe('Vertical for phones');
    expect(formatName('Square1x1')).toBe('Square');
    expect(formatName('Portrait4x5')).toBe('Portrait 4:5');
    expect(formatName('Master')).toBe('Master (large, for editing elsewhere)');
    expect(formatName('AudioOnly')).toBe('Audio only');
  });
  it('warns when the shape will crop or letterbox, not when it fits', () => {
    expect(fitWarning({ width: 1920, height: 1080 }, 'Hd1080p16x9')).toBeNull();
    expect(fitWarning({ width: 3840, height: 2160 }, 'Uhd4k16x9')).toBeNull();
    expect(fitWarning({ width: 1920, height: 1080 }, 'Vertical9x16')).toMatch(/crop or letterbox your picture.*16:9/);
    expect(fitWarning({ width: 1920, height: 1080 }, 'Master')).toBeNull();
    expect(fitWarning({ width: 1920, height: 1080 }, 'AudioOnly')).toBeNull();
    expect(fitWarning(null, 'Square1x1')).toBeNull();
    expect(aspectLabel(1080, 1920)).toBe('9:16');
  });
});

describe('quality steps', () => {
  it('maps Draft/Good/Best onto the encoder fields; Good sends nothing', () => {
    expect(qualityEncode('good')).toBeNull();
    expect(qualityEncode('draft')).toMatchObject({ speed: 'Fast', crf: 28 });
    expect(qualityEncode('best')).toMatchObject({ speed: 'Slow', crf: 16 });
  });
  it('builds the render request: encode follows quality, advanced overrides it, audio-only never encodes video', () => {
    const form = { revision: 3, preset: 'Hd1080p16x9' as const, range: null, fileName: ' ', quality: 'best' as const, advanced: null, loudness: null };
    expect(buildRenderRequest(form)).toMatchObject({ revision: 3, outputFileName: null, encode: { codec: 'H264', speed: 'Slow' } });
    expect(buildRenderRequest({ ...form, advanced: { codec: 'H265', crf: 20, speed: 'Medium', audioKbps: 128 } }).encode).toMatchObject({ codec: 'H265', audioBitrateKbps: 128 });
    expect(buildRenderRequest({ ...form, preset: 'AudioOnly' }).encode).toBeNull();
    expect(buildRenderRequest({ ...form, quality: 'good' }).encode).toBeNull();
  });
});

describe('status line (R06.9)', () => {
  it('says the render is the latest edit, or how many changes were made since', () => {
    expect(renderStatusLine(7, 7)).toBe('Last render: your latest edit');
    expect(renderStatusLine(null, 7)).toBe('Last render: your latest edit');
    expect(renderStatusLine(4, 5)).toBe('Last render: before your latest edits (1 change since)');
    expect(renderStatusLine(4, 9)).toBe('Last render: before your latest edits (5 changes since)');
  });
});

describe('credit estimate (R06.7)', () => {
  it('formats credits and minutes; billing off or no figure says nothing, never 0 credits', () => {
    expect(formatEstimate({ billingEnabled: true, credits: 12, estimateSeconds: 190, affordable: true })).toBe('About 12 credits, about 3 min');
    expect(formatEstimate({ billingEnabled: true, credits: 1, estimateSeconds: 20, affordable: true })).toBe('About 1 credit, under 1 min');
    expect(formatEstimate({ billingEnabled: true, credits: 2.5, affordable: true })).toBe('About 2.5 credits');
    expect(formatEstimate({ billingEnabled: false, affordable: true })).toBeNull();
    expect(formatEstimate({ billingEnabled: false, credits: 0, affordable: true })).toBeNull();
    expect(formatEstimate({ billingEnabled: true, affordable: false, unavailableReason: 'x' })).toBeNull();
    expect(formatEstimate(null)).toBeNull();
  });
  it('says how many more credits are needed when it is not affordable', () => {
    const e = { billingEnabled: true, credits: 12, balanceCredits: 9, affordable: false };
    expect(creditsMissing(e)).toBe(3);
    expect(missingSentence(creditsMissing(e))).toBe('You need 3 more credits');
    expect(missingSentence(1)).toBe('You need 1 more credit');
    expect(creditsMissing({ ...e, affordable: true })).toBeNull();
    expect(creditsMissing({ billingEnabled: false, affordable: false })).toBeNull();
  });
  it('explains a 402 from the render in the same words and offers credits', () => {
    const refusal = describeRenderRefusal(new ApiError('x', 402, [], { code: 'INSUFFICIENT_CREDITS', message: 'raw', missing: 2.04 }));
    expect(refusal.message).toBe('You need 2.1 more credits to render this.');
    expect(refusal.missingCredits).toBe(2.1);
  });
});

describe('queue helpers', () => {
  it('uses plain status words', () => {
    expect(['Queued', 'Running', 'Passed', 'Failed', 'Cancelled'].map(statusWord)).toEqual(['Waiting', 'Rendering', 'Finished', 'Did not finish', 'Stopped']);
  });
  it('picks the newest finished film with a file', () => {
    const older = { ...base, executionId: 'o', completedAt: '2026-10-09T10:00:00Z' };
    const noFile = { ...base, executionId: 'n', hasOutput: false, completedAt: '2026-10-11T10:00:00Z' };
    expect(latestFinished([older, base, noFile])?.executionId).toBe('e');
    expect(latestFinished([noFile])).toBeNull();
    expect(latestFinished(undefined)).toBeNull();
  });
  it('hides a deleted film and a removed failure', () => {
    const gone = { ...base, executionId: 'g', hasOutput: false };
    const failed = { ...base, executionId: 'f', status: 'Failed', hasOutput: false };
    expect(visibleRenders([base, gone, failed], new Set(['f'])).map((r) => r.executionId)).toEqual(['e']);
  });
  it('formats the length', () => {
    expect(formatLength(7.2)).toBe('0:07');
    expect(formatLength(83)).toBe('1:23');
    expect(formatLength(null)).toBeNull();
  });
  it('turns a failed save into a plain sentence', () => {
    expect(saveFailureSentence({ status: 'conflict', message: null })).toMatch(/changed somewhere else/);
    expect(saveFailureSentence({ status: 'offline', message: null })).toMatch(/offline/);
    expect(saveFailureSentence({ status: 'rejected', message: null, reasons: ['Clips cannot overlap'] })).toBe('Clips cannot overlap.');
    expect(saveFailureSentence({ status: 'error', message: null })).toBe('Your latest edits could not be saved.');
  });
});

describe('rate control request', () => {
  const form = { revision: 1, preset: 'Hd1080p16x9' as const, range: null, fileName: '', quality: 'good' as const, advanced: null, loudness: null };
  it('sends no encode for the plain Quality step', () => {
    expect(buildRenderRequest({ ...form, rate: { control: 'Quality', videoKbps: 8000 } }).encode).toBeNull();
  });
  it('sends rateControl and the bitrate for Average', () => {
    const encode = buildRenderRequest({ ...form, rate: { control: 'Average', videoKbps: 5000 }, audioKbps: 128 }).encode;
    expect(encode).toMatchObject({ rateControl: 'Average', videoBitrateKbps: 5000, audioBitrateKbps: 128, crf: 18 });
  });
  it('sends no rate for audio only', () => {
    expect(buildRenderRequest({ ...form, preset: 'AudioOnly', rate: { control: 'Capped', videoKbps: 5000 } }).encode).toBeNull();
  });
});
