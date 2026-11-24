// @vitest-environment jsdom
import { beforeEach, describe, expect, it } from 'vitest';
import type { ProjectOverview } from '@/lib/types/dashboard';
import { dismissRunNudge, hasFilesAndNoRun, isRunNudgeDismissed, nudgeProject } from './run-nudge';

const row = (over: Partial<ProjectOverview>): ProjectOverview => ({
  projectId: 'p', sourceFileCount: 1, videoCount: 1, audioCount: 0, imageCount: 0, totalBytes: 1, workflowCount: 1,
  renderCount: 0, activeExecutionCount: 0, failedExecutionCount: 0, lastActivityAt: '2026-01-01T00:00:00Z', latestOutput: null, ...over,
});

beforeEach(() => window.localStorage.clear());

describe('run nudge rules', () => {
  it('is for projects with files and no run of any kind', () => {
    expect(hasFilesAndNoRun(row({}))).toBe(true);
    expect(hasFilesAndNoRun(row({ sourceFileCount: 0 }))).toBe(false);
    expect(hasFilesAndNoRun(row({ renderCount: 1 }))).toBe(false);
    expect(hasFilesAndNoRun(row({ activeExecutionCount: 1 }))).toBe(false);
    expect(hasFilesAndNoRun(row({ failedExecutionCount: 1 }))).toBe(false);
  });

  it('picks the most recently active candidate and skips dismissed ones', () => {
    const overview = { projects: [row({ projectId: 'old' }), row({ projectId: 'new', lastActivityAt: '2026-02-01T00:00:00Z' }), row({ projectId: 'ran', renderCount: 2 })] };
    expect(nudgeProject(overview, () => false)?.projectId).toBe('new');
    expect(nudgeProject(overview, (id) => id === 'new')?.projectId).toBe('old');
    expect(nudgeProject(undefined, () => false)).toBeNull();
  });

  it('remembers a dismissal per person and project', () => {
    dismissRunNudge('u1', 'p1');
    expect(isRunNudgeDismissed('u1', 'p1')).toBe(true);
    expect(isRunNudgeDismissed('u1', 'p2')).toBe(false);
    expect(isRunNudgeDismissed('u2', 'p1')).toBe(false);
  });
});
