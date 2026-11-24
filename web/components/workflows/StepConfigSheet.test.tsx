// @vitest-environment jsdom
'use client';

import { describe, it, expect, vi, beforeAll, beforeEach, afterEach } from 'vitest';
import { render, screen, cleanup } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { StepConfigSheet } from './StepConfigSheet';
import type { StepData } from './WorkflowStepList';
import type { ProjectFile } from '@/lib/types/project';

// `useProjectFiles` is deliberately the ONLY link in the chain that is mocked. Everything
// between StepConfigSheet's own `projectId` prop and this call is real code:
//
//   StepConfigSheet(projectId) -> StepConfigBody -> VideoAnalyzeStepConfig -> VideoSourceRefPicker
//
// so if the prop is dropped anywhere along that chain, the spy is called with '' instead of the
// project id — which is exactly the Gap B regression: `useProjectFiles('')` makes the SWR key
// null (see use-files.ts) and the picker silently never fetches a file list.
const { useProjectFilesSpy } = vi.hoisted(() => ({ useProjectFilesSpy: vi.fn() }));

vi.mock('@/lib/hooks/use-files', () => ({
  useProjectFiles: useProjectFilesSpy,
}));

vi.mock('@/lib/hooks/use-media', () => ({
  useMediaPool: () => ({ data: [] }),
}));

vi.mock('@/lib/hooks/use-agents', () => ({
  useAgents: () => ({ data: [], isLoading: false }),
}));

const PROJECT_ID = 'project-abc123';

const VIDEO_FILE: ProjectFile = {
  id: 'file-1',
  projectId: PROJECT_ID,
  originalFileName: 'holiday-clip.mp4',
  category: 'userFiles',
  mimeType: 'video/mp4',
  sizeBytes: 1024,
  storageKey: 'projects/project-abc123/userFiles/holiday-clip.mp4',
  agentSummary: null,
  summaryStatus: 'Done',
  indexingStatus: 'Indexed',
  uploadedAt: '2026-10-02T00:00:00Z',
};

/** A VideoAnalyze step whose source is a project file, so the picker renders its file Select. */
function createVideoAnalyzeStep(): StepData {
  return {
    id: 'step-1',
    label: 'Analyze source',
    agentDefinitionId: '',
    stepType: 'VideoAnalyze',
    conditionExpression: null,
    loopSourceExpression: null,
    loopTargetStepOrder: null,
    maxIterations: 3,
    minScore: null,
    inputMappingJson: null,
    agentInputContextMode: null,
    cacheMode: null,
    selectedPriorStepOrders: [],
    trueBranchStepOrder: null,
    falseBranchStepOrder: null,
    parallelAgentIds: [],
    extractConfig: null,
    videoAnalyzeConfig: {
      version: 1,
      source: { kind: 'ProjectFile', projectFileId: null, stepOrder: null },
    } as StepData['videoAnalyzeConfig'],
    videoCompileConfig: null,
    thumbnailGenerateConfig: null,
    videoGenerateConfig: null,
    voiceoverConfig: null,
  };
}

function renderSheet(projectId?: string) {
  return render(
    <MantineProvider>
      <StepConfigSheet
        step={createVideoAnalyzeStep()}
        opened
        onClose={vi.fn()}
        onChange={vi.fn()}
        projectId={projectId}
      />
    </MantineProvider>,
  );
}

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  });

  // A `function`, not an arrow: vitest 4+ refuses `new` on an arrow-function mock.
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
});

beforeEach(() => {
  useProjectFilesSpy.mockReset();
  // The real hook returns `{ data }`; return a file only for a real id, mirroring how
  // `useProjectFiles('')` builds a null SWR key and therefore never resolves anything.
  useProjectFilesSpy.mockImplementation((projectId: string) => ({
    data: projectId ? [VIDEO_FILE] : [],
  }));
});

afterEach(cleanup);

describe('StepConfigSheet projectId wiring to the VideoAnalyze file picker', () => {
  it('gives the VideoAnalyze source picker the real project id, so it can list project files', () => {
    renderSheet(PROJECT_ID);

    // The wiring assertion: the id must arrive at the hook, not be dropped to '' on the way down.
    expect(useProjectFilesSpy).toHaveBeenCalledWith(PROJECT_ID);
    expect(useProjectFilesSpy).not.toHaveBeenCalledWith('');

    // The user-visible consequence: with a real id the picker offers the project's video files
    // instead of falling back to the empty state.
    const chooseButton = screen.getByRole('button', { name: 'Choose' }) as HTMLButtonElement;
    expect(chooseButton.disabled).toBe(false);
    expect(screen.queryByText('This project has no matching files')).toBeNull();
  });

  it('falls back to the empty state when the sheet is given no projectId at all', () => {
    renderSheet(undefined);

    // Documents the fallback (and keeps the test above from passing vacuously): with no id the
    // hook legitimately receives '' and the picker shows its empty state.
    expect(useProjectFilesSpy).toHaveBeenCalledWith('');
    expect(screen.getByText('This project has no matching files')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Choose' }) as HTMLButtonElement).disabled).toBe(true);
  });
});
