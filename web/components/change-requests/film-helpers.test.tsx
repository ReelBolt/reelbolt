// @vitest-environment jsdom
import { describe, expect, it } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useT } from '@/i18n';
import { filmPlan, hasRevisionSurface, replayStepLines, sceneTimeRange, versionOptionLabel } from './film-helpers';
import { history, revision, version } from './revision-fixtures';
import { hasActiveRevision, REVISION_POLL_MS } from '@/lib/hooks/use-revisions';

describe('film helpers', () => {
  it('formats a scene range in seconds, and nothing without a frame rate', () => {
    expect(sceneTimeRange({ name: 'a', fromFrame: 30, durationInFrames: 45 }, 30)).toBe('1–2.5 s');
    expect(sceneTimeRange({ name: 'a', fromFrame: 30, durationInFrames: 45 }, null)).toBeNull();
  });

  it('labels a version with its instruction, truncated', () => {
    const t = renderHook(() => useT('revisions.panel')).result.current;
    expect(versionOptionLabel(version(), t)).toBe('Version 1 — Original');
    expect(versionOptionLabel(version({ versionNumber: 3, instruction: 'x'.repeat(100) }), t)).toMatch(/^Version 3 — x{59}…$/);
  });

  it('lists the steps that run again for the chosen cut', () => {
    const t = renderHook(() => useT('revisions.panel')).result.current;
    const replay = {
      available: true, reason: null, hasEditDecisions: true, keepCutEstimate: null, reEditEstimate: null,
      steps: [
        { stepOrder: 5, label: 'Analyze', stepType: 'A', keepCut: 'Run' as const, reEdit: 'Run' as const },
        { stepOrder: 6, label: 'Edit', stepType: 'B', keepCut: 'KeepIfCompatible' as const, reEdit: 'Run' as const },
        { stepOrder: 7, label: 'Skipped', stepType: 'C', keepCut: 'Keep' as const, reEdit: 'Keep' as const },
      ],
    };
    expect(replayStepLines(replay as never, 'KeepCut', t)).toEqual(['Analyze', 'Edit (kept as it is, unless the scenes changed)']);
    expect(replayStepLines(replay as never, 'ReEdit', t)).toEqual(['Analyze', 'Edit']);
  });

  it('picks the price that follows the cut, else the history estimate', () => {
    const est = (n: number) => ({ billingEnabled: true, lowCredits: n, highCredits: n, requiredCredits: n, balanceCredits: 100, affordable: true });
    const h = history({ replay: { available: true, reason: null, hasEditDecisions: true, steps: [], keepCutEstimate: est(5), reEditEstimate: est(9) } });
    expect(filmPlan(h, 'KeepCut').estimate?.lowCredits).toBe(5);
    expect(filmPlan(h, 'ReEdit').estimate?.lowCredits).toBe(9);
    expect(filmPlan(h, 'ReEdit').chooseCut).toBe(true);
    expect(filmPlan(history(), 'KeepCut')).toMatchObject({ replay: null, chooseCut: false });
  });

  it('shows a recoverable run with no versions yet', () => {
    expect(hasRevisionSurface(history({ availability: 'Recoverable', versions: [] }))).toBe(true);
    expect(hasRevisionSurface(history({ availability: 'Unavailable', versions: [] }))).toBe(false);
    expect(hasRevisionSurface(history({ availability: 'Unavailable', canRevise: false }))).toBe(true);
    expect(hasRevisionSurface(undefined)).toBe(false);
  });

  it('polls only while a change request is waiting or working', () => {
    expect(REVISION_POLL_MS).toBe(5000);
    expect(hasActiveRevision(history())).toBe(false);
    expect(hasActiveRevision(history({ revisions: [revision({ status: 'Queued' })] }))).toBe(true);
    expect(hasActiveRevision(history({ revisions: [revision({ status: 'Failed' })] }))).toBe(false);
    expect(hasActiveRevision(undefined)).toBe(false);
  });
});
