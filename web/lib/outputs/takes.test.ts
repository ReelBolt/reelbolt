import { describe, it, expect } from 'vitest';
import type { OutputVideo } from '../api/outputs';
import { earlierTakeStorageKeys, groupTakes, takeKey } from './takes';

function render(id: string, at: string, extra: Partial<OutputVideo> = {}): OutputVideo {
  return {
    stepResultId: id, workflowExecutionId: `e-${id}`, workflowStepId: 'compile', storageKey: `k-${id}`,
    fileName: `${id}.mp4`, producedAt: at, workflowDefinitionId: 'wf', takeGroupKey: 'wf:3:VideoCompile', ...extra,
  };
}

describe('takes', () => {
  it('groups renders of one step into one video, latest take first, videos newest first', () => {
    const groups = groupTakes([
      render('old', '2026-10-05T09:00:00Z'),
      render('promo', '2026-10-05T10:30:00Z', { takeGroupKey: 'wf:2:Agent', workflowStepId: 'author' }),
      render('new', '2026-10-05T11:00:00Z'),
      render('mid', '2026-10-05T10:00:00Z', { isIntermediate: true }),
    ]);
    expect(groups.map((g) => g.latest.stepResultId)).toEqual(['new', 'promo']);
    expect(groups[0].earlier.map((o) => o.stepResultId)).toEqual(['mid', 'old']);
    expect(groups[1].earlier).toEqual([]);
  });

  it('falls back to workflow and step when the server sends no group key', () => {
    expect(takeKey(render('a', '2026-10-05T09:00:00Z', { takeGroupKey: null }))).toBe('wf:compile');
  });

  it('hides earlier takes from Files but never a video the latest take still plays from', () => {
    const keys = earlierTakeStorageKeys([
      render('new', '2026-10-05T11:00:00Z', { storageKey: 'shared' }),
      render('cached', '2026-10-05T10:00:00Z', { storageKey: 'shared' }),
      render('old', '2026-10-05T09:00:00Z'),
    ]);
    expect([...keys]).toEqual(['k-old']);
  });
});
