// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { RevisionHistory as RevisionHistoryData } from '@/lib/types/revisions';
import type { StepChangeRequest } from '@/lib/types/change-requests';
import type { ActivityJob } from '@/lib/activity/types';
import { getOutputVideoUrl } from '@/lib/api/outputs';
import { ChangeHistory, historyParts } from './ChangeHistory';
import { changeRequest } from './change-request-fixtures';
import { history, revision, version } from './revision-fixtures';

let rows: ActivityJob[] = [];
vi.mock('@/lib/activity/ActivityProvider', () => ({
  useJobFor: (p: (j: ActivityJob) => boolean) => rows.find(p),
  useActivity: () => ({ cancel: vi.fn(async () => undefined) }),
}));
vi.mock('next/link', () => ({ default: ({ children, href }: { children: React.ReactNode; href: string }) => <a href={href}>{children}</a> }));

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false, media: query, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(() => {
  cleanup();
  rows = [];
});

// v1 (original, this page's run) → v2 (a finished change) → a change still working on v2.
const chain = (): RevisionHistoryData => history({
  versions: [
    version(),
    version({ id: 'v2', versionNumber: 2, parentVersionId: 'v1', parentVersionNumber: 1, createdAt: '2026-10-04T15:00:00Z', instruction: 'Bigger logo', executionId: 'ex-rev0', workflowId: 'wf-rev', renderStepResultId: 'sr2' }),
  ],
  revisions: [
    revision({ id: 'rev2', executionId: 'ex-rev2', status: 'Running', instruction: 'Make the outro shorter', baseVersionId: 'v2', baseVersionNumber: 2, createdAt: '2026-10-05T09:00:00Z' }),
    revision({ id: 'rev1', executionId: 'ex-rev0', status: 'Passed', instruction: 'Bigger logo', resultVersionId: 'v2', resultVersionNumber: 2, createdAt: '2026-10-04T14:30:00Z' }),
  ],
});

function renderHistory(data: RevisionHistoryData | null = chain(), onChangeVersion = vi.fn(), executionId = 'ex1', requests: StepChangeRequest[] = []) {
  render(
    <MantineProvider>
      <ChangeHistory projectId="p1" executionId={executionId} history={data} requests={requests} onChangeVersion={onChangeVersion} />
    </MantineProvider>,
  );
  return { onChangeVersion };
}

const job = (over: Partial<ActivityJob>): ActivityJob => ({
  id: 'run:r', type: 'revision', state: 'running', projectId: 'p1', projectName: 'P', title: 'Revision', executionId: 'ex-rev2',
  workflowId: 'wf-rev', subject: {}, step: null, progress: 0.4, credits: { heldCredits: null, chargedCredits: null }, failure: null,
  startedByYou: true, cancellable: false, createdAt: '2026-10-05T09:00:00Z', startedAt: null, endedAt: null, origin: 'server', ...over,
});

describe('ChangeHistory: versions', () => {
  it('lists versions newest first, a finished change once (as its version), and a running change with its status', () => {
    renderHistory();
    const cards = screen.getAllByTestId(/^(revision-version|revision-request|change-request-row)$/);
    expect(cards.map((c) => c.getAttribute('data-testid'))).toEqual(['revision-version', 'revision-version', 'revision-request']);
    expect(within(cards[0]).getByText('Version 2')).toBeTruthy();
    expect(within(cards[0]).getByText('Bigger logo')).toBeTruthy();
    expect(within(cards[1]).getByText('Version 1')).toBeTruthy();
    expect(within(cards[1]).getByText('Original video')).toBeTruthy();
    expect(within(cards[2]).getByText('Working')).toBeTruthy();
    expect(within(cards[2]).getByText('Make the outro shorter')).toBeTruthy();
    expect(within(cards[2]).getByText('Starting from version 2')).toBeTruthy();
    expect(within(cards[2]).getByRole('link', { name: /Follow progress/ }).getAttribute('href')).toBe('/projects/p1/workflows/wf-rev/executions/ex-rev2');
  });

  it('highlights the version this run made and links to the others', () => {
    renderHistory();
    const [v2, v1] = screen.getAllByTestId('revision-version');
    expect(v1.getAttribute('aria-current')).toBe('true');
    expect(within(v1).getByText('Made by this run')).toBeTruthy();
    expect(within(v1).queryByRole('link')).toBeNull();
    expect(v2.getAttribute('aria-current')).toBeNull();
    expect(within(v2).getByRole('link', { name: /See the run/ }).getAttribute('href')).toBe('/projects/p1/workflows/wf-rev/executions/ex-rev0');
  });

  it('shows why a change failed', () => {
    renderHistory(history({ revisions: [revision({ status: 'Failed', errorMessage: 'We could not render the new version.' })] }));
    const card = screen.getByTestId('revision-request');
    expect(within(card).getByText('Failed')).toBeTruthy();
    expect(within(card).getByText('We could not render the new version.')).toBeTruthy();
  });

  it('plays a version in a player with its render', async () => {
    renderHistory();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Play Version 2' }));
    });
    const video = (await screen.findByTestId('revision-video')) as HTMLVideoElement;
    expect(video.getAttribute('src')).toBe(getOutputVideoUrl('p1', 'sr2'));
    expect(video.getAttribute('src')).toBe('/api/v1/projects/p1/outputs/sr2/download');
    expect(video.controls).toBe(true);
  });

  it('plays a version\'s finished video first and keeps the animation as the second choice', async () => {
    renderHistory(history({
      versions: [version({
        id: 'v2', versionNumber: 2, renderStepResultId: 'sr-film', cutPolicy: 'KeepCut', cutKept: true, finalVideoExpected: true, runStatus: 'Passed',
        deliverables: [{ stepResultId: 'sr-cut', stepOrder: 7, label: 'Compile the final cut', isPrimary: true }],
      })],
    }));
    expect(screen.getByText('Same cut kept')).toBeTruthy();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Play Version 2' }));
    });
    expect((await screen.findByTestId('revision-video')).getAttribute('src')).toBe(getOutputVideoUrl('p1', 'sr-cut'));
    cleanup();

    renderHistory(history({
      versions: [version({
        id: 'v2', versionNumber: 2, renderStepResultId: 'sr-film', finalVideoExpected: true, runStatus: 'Passed',
        deliverables: [{ stepResultId: 'sr-cut', stepOrder: 7, label: null, isPrimary: true }],
      })],
    }));
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Play the animation of Version 2' }));
    });
    expect((await screen.findByTestId('revision-video')).getAttribute('src')).toBe(getOutputVideoUrl('p1', 'sr-film'));
  });

  it('says when the finished video is still being made or was not made', () => {
    renderHistory(history({ versions: [version({ finalVideoExpected: true, runStatus: 'Running', deliverables: [] })] }));
    expect(screen.getByTestId('revision-final-note').textContent).toBe('The finished video is still being put together.');
    cleanup();
    renderHistory(history({ versions: [version({ finalVideoExpected: true, runStatus: 'Failed', deliverables: [], cutPolicy: 'KeepCut', cutKept: false })] }));
    expect(screen.getByTestId('revision-final-note').textContent).toMatch(/was not made/);
    expect(screen.getByText('Edit made again')).toBeTruthy();
  });

  it('"Change this version" hands the version to the request panel', () => {
    const { onChangeVersion } = renderHistory();
    fireEvent.click(screen.getByRole('button', { name: 'Change Version 2' }));
    expect(onChangeVersion).toHaveBeenCalledWith('v2');
  });

  it('offers no "Change this version" while changes cannot be requested', () => {
    renderHistory(history({ canRevise: false, versions: chain().versions }));
    expect(screen.queryByRole('button', { name: /^Change Version/ })).toBeNull();
    expect(screen.getAllByRole('button', { name: /^Play Version/ })).toHaveLength(2);
  });

  it('orders versions by date, then by version number', () => {
    const { versions } = historyParts(history({
      versions: [version({ id: 'a', versionNumber: 1, createdAt: '' }), version({ id: 'b', versionNumber: 2, createdAt: '' })],
    }), [], 'ex1');
    expect(versions.map((v) => v.id)).toEqual(['b', 'a']);
  });
});

describe('ChangeHistory: changes', () => {
  it('lists requests with counts, cost and a link', () => {
    renderHistory(null, vi.fn(), 'ex1', [changeRequest()]);
    const row = screen.getByTestId('change-request-row');
    expect(row.textContent).toContain('Slower first line');
    expect(row.textContent).toContain('2 redone, 5 reused');
    expect(row.textContent).toContain('1.50 credits');
    expect(row.querySelector('a')?.getAttribute('href')).toBe('/projects/p1/workflows/wf2/executions/ex2');
  });
  it("opens 'changed from' under the source run's own workflow", () => {
    renderHistory(null, vi.fn(), 'ex2', [changeRequest({ sourceWorkflowDefinitionId: 'wfSrc' })]);
    expect(screen.getByTestId('change-request-lineage').querySelector('a')?.getAttribute('href')).toBe('/projects/p1/workflows/wfSrc/executions/ex1');
  });
  it("shows 'changed from' on the request's own run, and nothing when empty", () => {
    renderHistory(null, vi.fn(), 'ex2', [changeRequest()]);
    expect(screen.getByTestId('change-request-lineage').textContent).toContain('the original run');
    cleanup();
    renderHistory(null, vi.fn(), 'ex1', []);
    expect(screen.queryByTestId('change-history')).toBeNull();
  });
  it('names an app-input change by its fields, before and after, not by the steps it re-made', () => {
    const r = changeRequest({
      targets: [
        { stepOrder: 1, stepLabel: 'Write the script', stepType: 'Agent', itemIds: [], instruction: '', kind: 'inputs' },
        { stepOrder: 3, stepLabel: 'Make the clip', stepType: 'VideoGenerate', itemIds: [], instruction: '', kind: 'inputs' },
      ],
      inputChanges: [{ inputId: 'audience', label: 'Who it is for', type: 'shortText', before: 'Baristas', after: 'Students', value: 'Students' }],
    });
    renderHistory(null, vi.fn(), 'ex1', [r]);
    expect(screen.getByTestId('change-request-input-changes').textContent).toBe('Who it is for: Baristas → Students');
    expect(screen.getByTestId('change-request-row').textContent).not.toContain('Write the script');
  });
  it('never shows step types or raw item ids in a title', () => {
    const r = changeRequest({ instruction: '', stepType: 'Voiceover', itemIds: ['vo1'], targets: [{ stepOrder: 3, stepLabel: 'Narration', stepType: 'Voiceover', itemIds: ['vo1'], instruction: '', kind: 'step' }] });
    renderHistory(null, vi.fn(), 'ex1', [r]);
    const text = screen.getByTestId('change-request-row').textContent ?? '';
    expect(text).toContain('Narration (1 line)');
    expect(text).not.toMatch(/Voiceover|vo1/);
  });
  it('names every target of a multi request and keeps single rows as before', () => {
    const multi = changeRequest({
      id: 'cr2', executionId: 'ex5', targets: [
        { stepOrder: 3, stepLabel: 'Narration', stepType: 'Voiceover', itemIds: ['vo1', 'vo2'], instruction: '', kind: 'step' },
        { stepOrder: 7, stepLabel: 'Video', stepType: 'Composition', itemIds: [], instruction: 'Bigger title', kind: 'film' },
      ],
    });
    renderHistory(null, vi.fn(), 'ex1', [multi, changeRequest()]);
    const rows = screen.getAllByTestId('change-request-row');
    expect(rows[0].textContent).toContain('Narration (2 lines) + The video');
    expect(rows[1].textContent).toContain('Slower first line');
  });
  it('lists a combined run once: its video change row is dropped when the request row is there', () => {
    const data = history({
      versions: [version()],
      revisions: [revision({ id: 'rev7', executionId: 'ex-both', status: 'Running', instruction: 'Bigger title', createdAt: '2026-10-26T09:00:00Z' })],
    });
    const combined = changeRequest({ id: 'cr7', executionId: 'ex-both', workflowDefinitionId: 'wf-both', status: 'Running', createdAt: '2026-10-26T09:00:00Z', instruction: 'Bigger title' });
    renderHistory(data, vi.fn(), 'ex1', [combined]);
    expect(screen.getAllByTestId('change-request-row')).toHaveLength(1);
    expect(screen.queryByTestId('revision-request')).toBeNull();
    // Without the request row the video change shows on its own.
    cleanup();
    renderHistory(data, vi.fn(), 'ex1', []);
    expect(screen.getAllByTestId('revision-request')).toHaveLength(1);
  });
  it('mixes video changes and requests newest first, versions in their own list', () => {
    const data = history({
      versions: [version()],
      revisions: [revision({ id: 'rev8', executionId: 'ex-v', status: 'Failed', createdAt: '2026-10-05T09:00:00Z' })],
    });
    const parts = historyParts(data, [changeRequest({ createdAt: '2026-10-25T10:00:00Z', executionId: 'ex2' })], 'ex1');
    expect(parts.changes.map((c) => c.kind)).toEqual(['request', 'revision']);
    expect(parts.versions).toHaveLength(1);
    expect(parts.own).toBeNull();
  });
  it('labels its lists', () => {
    renderHistory(chain(), vi.fn(), 'ex1', [changeRequest()]);
    expect(screen.getByRole('list', { name: 'Versions' })).toBeTruthy();
    expect(screen.getByRole('list', { name: 'Changes to this run' })).toBeTruthy();
  });
});

describe('ChangeHistory activity chip', () => {
  it('shows the live status chip on a video change that is still running', () => {
    rows = [job({})];
    renderHistory();
    expect(within(screen.getByTestId('revision-request')).getByTestId('inline-job-status')).toBeTruthy();
  });
  it('shows the live chip on a change request that is working', () => {
    rows = [job({ executionId: 'ex2', type: 'changeRequest' })];
    renderHistory(null, vi.fn(), 'ex1', [changeRequest({ status: 'Running' })]);
    expect(within(screen.getByTestId('change-request-row')).getByTestId('inline-job-status')).toBeTruthy();
  });
  it('shows no chip when the activity model has no row for the run', () => {
    renderHistory();
    expect(screen.queryByTestId('inline-job-status')).toBeNull();
  });
});
