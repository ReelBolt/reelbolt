// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { WorkflowExecution } from '@/lib/types/workflow';

vi.mock('next/link', () => ({ default: ({ href, children }: { href: string; children: React.ReactNode }) => <a href={href}>{children}</a> }));
const outputsState = vi.hoisted(() => ({ value: { data: undefined as unknown, error: undefined as unknown, mutate: (() => undefined) as () => unknown } }));
vi.mock('@/lib/hooks/use-outputs', () => ({ useOutputVideos: () => outputsState.value }));
vi.mock('@/components/shell/VideoEditorFlag', () => ({ useVideoEditorEnabled: () => false }));
vi.mock('@/components/outputs/CheckedVideo', () => ({ CheckedVideo: () => null, CheckedOutputMedia: () => null }));
vi.mock('@/lib/outputs/output-check', () => ({ useDownloadGuard: () => () => undefined }));

import { ApiError } from '@/lib/api/client';
import { RunResult } from './RunResult';

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
});
beforeEach(() => {
  outputsState.value = { data: undefined, error: undefined, mutate: () => undefined };
});
afterEach(cleanup);

const failed = (code: string, charged: number | null): WorkflowExecution =>
  ({ id: 'e1', status: 'Failed', stepResults: [{ id: 'r', workflowStepId: 's', status: 'Failed', failureCode: code, creditsCharged: charged }] }) as never;

function mount(execution: WorkflowExecution, handlers: { onChooseAnotherClip?: (slot: 'video' | 'insertContent') => void } = {}) {
  const onRetry = vi.fn();
  const onAskAssistant = vi.fn();
  render(
      <MantineProvider>
        <RunResult projectId="p" workflowId="w" resultKind="video" execution={execution} lines={[]} needs={[]} busy={false} retryError={null}
          onRetry={onRetry} onResume={vi.fn()} onChangeOne={vi.fn()} onAskAssistant={onAskAssistant} {...handlers} />
      </MantineProvider>
  );
  return { onRetry, onAskAssistant };
}

describe('RunResult insert next step', () => {
  it('offers another clip, try again and the assistant, and says nothing was charged when it was 0', () => {
    const choose = vi.fn();
    const { onRetry, onAskAssistant } = mount(failed('INSERT_NOT_APPLIED', 0), { onChooseAnotherClip: choose });
    const block = screen.getByTestId('insert-next-step');
    expect(screen.getByTestId('failed-banner').textContent).toContain('large, steady');
    expect(block.textContent).toContain('You were not charged');
    fireEvent.click(screen.getByRole('button', { name: 'Choose another clip' }));
    expect(choose).toHaveBeenCalledTimes(1);
    expect(choose).toHaveBeenCalledWith('video');
    expect(screen.getByRole('link', { name: 'What clips work' }).getAttribute('href')).toBe('/docs/recipes/tracked-screen-insert#which-clip-works');
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(onRetry).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getAllByRole('button', { name: 'Ask the assistant' })[0]!);
    expect(onAskAssistant).toHaveBeenCalled();
  });
  it('sends an unusable screen picture to the screen-content slot, not the footage', () => {
    const choose = vi.fn();
    mount(failed('INSERT_CONTENT_UNUSABLE', 0), { onChooseAnotherClip: choose });
    fireEvent.click(screen.getByRole('button', { name: 'Choose another clip' }));
    expect(choose).toHaveBeenCalledWith('insertContent');
  });
  it('does not claim no charge when credits were charged or unknown', () => {
    mount(failed('INSERT_NOT_APPLIED', 2), { onChooseAnotherClip: vi.fn() });
    expect(screen.getByTestId('insert-next-step').textContent).not.toContain('not charged');
  });
  it('has no block for other failures and no other-clip button for our own fault', () => {
    mount(failed('VOICE_NOT_FOUND', 0), { onChooseAnotherClip: vi.fn() });
    expect(screen.queryByTestId('insert-next-step')).toBeNull();
    cleanup();
    mount(failed('INSERT_FILTER_UNAVAILABLE', 0), { onChooseAnotherClip: vi.fn() });
    expect(screen.getByTestId('insert-next-step')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Choose another clip' })).toBeNull();
  });
});

const passed = { id: 'e1', status: 'Passed', stepResults: [] } as unknown as WorkflowExecution;
const output = { stepResultId: 'sr1', workflowExecutionId: 'e1', fileName: 'out.mp4', isFinal: true, isIntermediate: false, producedAt: '2026-01-01T00:00:00Z' };

describe('RunResult states', () => {
  it('ready: heading and body agree and the video is offered', () => {
    outputsState.value = { data: [output], error: undefined, mutate: () => undefined };
    mount(passed);
    expect(screen.getByRole('heading', { level: 3 }).textContent).toBe('Your video is ready');
    expect(screen.getByRole('link', { name: /download/i })).toBeTruthy();
  });
  it('preparing: never says ready while the body says being prepared', () => {
    mount(passed);
    expect(screen.getByRole('heading', { level: 3 }).textContent).toBe('The video is being prepared.');
    expect(screen.queryByText('Your video is ready')).toBeNull();
    expect(screen.getByRole('link', { name: 'Open the run' }).getAttribute('href')).toBe('/projects/p/workflows/w/executions/e1');
  });
  it('failed: shows the banner with Try again', () => {
    const { onRetry } = mount(failed('VOICE_NOT_FOUND', 0));
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });
  it('stopped: says so and does not claim a video', () => {
    mount({ id: 'e1', status: 'Cancelled', stepResults: [] } as unknown as WorkflowExecution);
    expect(screen.queryByText('Your video is ready')).toBeNull();
    expect(screen.getByTestId('run-result').textContent).toContain('stopped');
  });
  it('outputs-error: offers Retry that re-reads the list and a way to the run, without raw text', () => {
    const mutate = vi.fn();
    outputsState.value = { data: undefined, error: new ApiError('boom raw internal', 500), mutate };
    mount(passed);
    const card = screen.getByTestId('failure-card');
    expect(card.textContent).toContain('Something went wrong on our side');
    expect(screen.getByRole('heading', { level: 3 }).textContent).not.toContain('ready');
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(mutate).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('link', { name: 'Open the run' })).toBeTruthy();
  });
  it('outputs-error 403 and 404: no Retry', () => {
    outputsState.value = { data: undefined, error: new ApiError('x', 403), mutate: () => undefined };
    mount(passed);
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
    cleanup();
    outputsState.value = { data: undefined, error: new ApiError('x', 404), mutate: () => undefined };
    mount(passed);
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
  });
  it('gives up after about a minute and says the video file was not found', () => {
    vi.useFakeTimers();
    try {
      mount(passed);
      act(() => { vi.advanceTimersByTime(61000); });
      expect(screen.getByRole('heading', { level: 3 }).textContent).toContain('could not find');
      expect(screen.getByRole('link', { name: 'Open the run' })).toBeTruthy();
    } finally {
      vi.useRealTimers();
    }
  });
});
