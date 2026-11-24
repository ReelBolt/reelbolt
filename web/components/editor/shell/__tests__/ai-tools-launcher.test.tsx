import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ReactNode } from 'react';
import type { ActivityJob } from '@/lib/activity/types';
import type { EditorAiToolContext } from '@/lib/editing/ai-tools';

const act = { allJobs: [] as ActivityJob[], jobs: [] as ActivityJob[], cancel: vi.fn(async () => undefined) };
vi.mock('@/lib/activity/ActivityProvider', () => ({ useActivity: () => act, useJobsWhere: (p: (j: ActivityJob) => boolean) => act.allJobs.filter(p) }));

import { AiToolsLauncher } from '../AiToolsLauncher';
import { listEditorAiTools } from '@/lib/editing/ai-tools';

Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (q: string) => ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false, onchange: null }),
});
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

const ctx = (over: Partial<EditorAiToolContext> = {}): EditorAiToolContext => ({
  selectedClipId: null, selectedClipKind: null, hasRange: false,
  openPanel: vi.fn(), openWorkspace: vi.fn(), openRemoveSilence: vi.fn(), openCutToBeat: vi.fn(), openInspectorSection: vi.fn(), openAssist: vi.fn(), openDeliver: vi.fn(), ...over,
});
const job = (over: Partial<ActivityJob> = {}): ActivityJob => ({
  id: 'j1', type: 'objectAnalysis', state: 'running', projectId: 'p1', projectName: 'P', title: null, executionId: null, workflowId: null,
  subject: {}, step: null, progress: null, credits: { heldCredits: null, chargedCredits: null }, failure: null, startedByYou: true,
  cancellable: false, createdAt: '2026-01-01T00:00:00Z', startedAt: null, endedAt: null, origin: 'server', ...over,
});
const wrap = (ui: ReactNode) => render(<MantineProvider env="test">{ui}</MantineProvider>);
const openList = (c: EditorAiToolContext = ctx()) => {
  wrap(<AiToolsLauncher context={c} projectId="p1" />);
  fireEvent.click(screen.getByRole('button', { name: 'AI tools' }));
};

afterEach(() => {
  cleanup();
  act.allJobs = [];
});

describe('AiToolsLauncher', () => {
  it('has a labelled button and lists every built-in with a cost tag', () => {
    openList();
    const rows = listEditorAiTools();
    for (const def of rows) expect(screen.getByTestId(`ai-tool-${def.id}`)).toBeTruthy();
    expect(screen.getAllByText('Free').length).toBe(rows.filter((r) => r.cost === 'free').length);
    expect(screen.getAllByText('Uses credits').length).toBe(rows.filter((r) => r.cost === 'credits').length);
  });
  it('shows the hint, not a call, when a video clip is needed and none is selected', () => {
    const c = ctx();
    openList(c);
    expect(screen.getByTestId('ai-tool-hint-magicBlur').textContent).toBe('Select a video clip first');
    fireEvent.click(screen.getByText('Magic blur'));
    expect(c.openInspectorSection).not.toHaveBeenCalled();
  });
  it('calls the right callback when a tool is chosen', () => {
    const c = ctx({ selectedClipId: 'c1', selectedClipKind: 'video' });
    openList(c);
    fireEvent.click(screen.getByText('Magic blur'));
    expect(c.openInspectorSection).toHaveBeenCalledWith('object-effects-section');
  });
  it('filters by search and moves with the arrow keys', () => {
    openList();
    fireEvent.change(screen.getByTestId('ai-tools-search'), { target: { value: 'silence' } });
    expect(screen.queryByText('Magic blur')).toBeNull();
    expect(screen.getByText('Remove silence')).toBeTruthy();
    fireEvent.keyDown(screen.getByTestId('ai-tools-search'), { key: 'ArrowDown' });
    expect(document.activeElement?.hasAttribute('data-ai-tool-row')).toBe(true);
  });
  it('shows a running chip for an active job of that type in this project only', () => {
    act.allJobs = [job(), job({ id: 'j2', type: 'mediaAnalysis', projectId: 'other' })];
    openList();
    expect(screen.getByTestId('ai-tool-running-magicBlur')).toBeTruthy();
    expect(screen.queryByTestId('ai-tool-running-analyseFootage')).toBeNull();
  });
  it('renders a bottom drawer on phone', () => {
    wrap(<AiToolsLauncher phone opened onClose={() => undefined} context={ctx()} projectId="p1" />);
    expect(screen.getByText('Remove silence')).toBeTruthy();
    expect(screen.queryByTestId('ai-tools-button')).toBeNull();
  });
});
