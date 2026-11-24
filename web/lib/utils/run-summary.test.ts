import { describe, expect, it } from 'vitest';
import type { WorkflowStepResult } from '../types/workflow';
import {
  collectRunWarnings, estimateRemainingMs, formatFrameSize, formatTimeLeft, formatVideoLength, readWarnings, renderDownloadName,
  renderStage, renderTitle, runName, runOrdinal, warningTitle,
} from './run-summary';

const r = (stepId: string, status: WorkflowStepResult['status'], durationMs: number, executedAt = '2026-10-04T12:00:00Z', extra: Partial<WorkflowStepResult> = {}): WorkflowStepResult => ({
  id: `${stepId}-${status}`, workflowStepId: stepId, output: '', tokensUsed: 0, durationMs, executedAt, status, ...extra,
});

describe('warnings (contract C1)', () => {
  it('reads, deduplicates and tolerates junk', () => {
    const w = JSON.stringify({ warnings: [{ code: 'narration_dropped', message: 'Two lines were dropped.' }, { code: 'x' }, 'nope'] });
    expect(readWarnings(w)).toEqual([{ code: 'narration_dropped', message: 'Two lines were dropped.' }]);
    expect(readWarnings('not json warnings')).toEqual([]);
    expect(readWarnings('[1,2]')).toEqual([]);
    expect(readWarnings(null)).toEqual([]);
    expect(collectRunWarnings([{ outputJson: w }, { outputJson: w }, { outputJson: null }])).toHaveLength(1);
  });

  it('has a headline for each known code and a generic one otherwise', () => {
    expect(warningTitle('narration_mostly_dropped')).toBe('Most of the narration is missing');
    expect(warningTitle('target_length_missed')).toMatch(/length/);
    expect(warningTitle('something_new')).toBe('Something needs your attention');
  });
});

describe('run naming', () => {
  it('numbers runs by start time and formats a human name', () => {
    const runs = [{ id: 'b', startedAt: '2026-10-04T12:00:00Z' }, { id: 'a', startedAt: '2026-10-03T12:00:00Z' }, { id: 'q', startedAt: null }];
    expect(runOrdinal('a', runs)).toBe(1);
    expect(runOrdinal('b', runs)).toBe(2);
    expect(runOrdinal('q', runs)).toBe(3);
    expect(runOrdinal('zzz', runs)).toBeNull();
    expect(runName(3, '2026-10-04T14:02:00')).toBe('Run 3 · 4 Oct, 14:02');
    expect(runName(null, null)).toBe('Run');
  });
});

describe('time left', () => {
  const steps = [{ id: 's1' }, { id: 's2' }, { id: 's3' }, { id: 's4' }];
  const now = new Date('2026-10-04T12:10:00Z').getTime();

  it('averages real (non-cached) step durations over the steps still to go', () => {
    const exec = {
      status: 'Running' as const,
      stepResults: [
        r('s1', 'Completed', 60_000), r('s2', 'Completed', 0, undefined, { fromCache: true }),
        r('s3', 'Running', 0, '2026-10-04T12:09:30Z'),
      ],
    };
    // avg 60 s; s3 has 30 s left, s4 another 60 s.
    expect(estimateRemainingMs(steps, exec, now)).toBe(90_000);
  });

  it('is unknown without a finished step, and absent once the run is over', () => {
    expect(estimateRemainingMs(steps, { status: 'Running', stepResults: [r('s1', 'Running', 0)] }, now)).toBeNull();
    expect(estimateRemainingMs(steps, { status: 'Passed', stepResults: [r('s1', 'Completed', 1000)] }, now)).toBeNull();
  });

  it('formats the estimate in plain words', () => {
    expect(formatTimeLeft(20_000)).toBe('Less than a minute left');
    expect(formatTimeLeft(12 * 60_000)).toBe('About 12 min left');
    expect(formatTimeLeft(65 * 60_000)).toBe('About 1 h 5 min left');
    expect(formatTimeLeft(120 * 60_000)).toBe('About 2 h left');
  });
});

describe('render labels', () => {
  it('describes length and frame shape', () => {
    expect(formatVideoLength(61.2)).toBe('1:01');
    expect(formatVideoLength(3725)).toBe('1:02:05');
    expect(formatVideoLength(0)).toBeNull();
    expect(formatFrameSize(1080, 1920)).toBe('Vertical 9:16 · 1080×1920');
    expect(formatFrameSize(1920, 1080)).toBe('Landscape 16:9 · 1920×1080');
    expect(formatFrameSize(1080, 1080)).toBe('Square 1:1 · 1080×1080');
    expect(formatFrameSize(null, 1080)).toBeNull();
  });

  it('titles renders after their workflow and run, never a GUID when context exists', () => {
    const o = { fileName: 'acf96fd3.mp4', workflowName: 'Teaser: 20s', runNumber: 4, executionStatus: 'Passed', isFinal: false };
    expect(renderTitle(o)).toBe('Teaser: 20s, Run 4');
    expect(renderStage(o)).toBe('draft');
    expect(renderDownloadName(o, 'draft')).toBe('Teaser 20s, Run 4 (draft).mp4');
    expect(renderStage({ ...o, isFinal: true })).toBe('final');
    expect(renderStage({ ...o, executionStatus: 'Running' })).toBe('inProgress');
    expect(renderTitle({ fileName: 'edited.mp4' })).toBe('edited.mp4');
    expect(renderStage({ fileName: 'edited.mp4' })).toBeNull();
  });
});
