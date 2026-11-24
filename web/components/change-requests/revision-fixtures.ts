import type { CompositionRevision, CompositionVersion, RevisionHistory } from '@/lib/types/revisions';

/** Test fixtures for the revision components (not a test file itself). */
export function version(over: Partial<CompositionVersion> = {}): CompositionVersion {
  return {
    id: 'v1', versionNumber: 1, parentVersionId: null, parentVersionNumber: null,
    createdAt: '2026-10-04T14:00:00Z', instruction: null, source: 'RenderCapture',
    executionId: 'ex1', workflowId: 'wf', renderStepResultId: 'sr1', hasRender: true,
    compositionId: 'Main', durationInFrames: 900, fps: 30, width: 1920, height: 1080, sceneCount: 3,
    ...over,
  };
}

export function revision(over: Partial<CompositionRevision> = {}): CompositionRevision {
  return {
    id: 'rev1', executionId: 'ex-rev1', workflowId: 'wf-rev', status: 'Running',
    instruction: 'Make the outro shorter', targets: null, mode: 'Render',
    baseVersionId: 'v1', baseVersionNumber: 1, resultVersionId: null, resultVersionNumber: null,
    createdAt: '2026-10-05T09:00:00Z', completedAt: null, errorMessage: null,
    ...over,
  };
}

export function history(over: Partial<RevisionHistory> = {}): RevisionHistory {
  return {
    workspaceId: 'ws1', availability: 'Snapshot', canRevise: true, unavailableReason: null,
    currentVersionId: 'v1',
    scenes: [
      { name: 'Intro', fromFrame: 0, durationInFrames: 120 },
      { name: 'Stats', fromFrame: 120, durationInFrames: 135 },
    ],
    versions: [version()],
    revisions: [],
    estimate: { billingEnabled: true, lowCredits: 4, highCredits: 9, requiredCredits: 9, balanceCredits: 100, affordable: true },
    ...over,
  };
}
