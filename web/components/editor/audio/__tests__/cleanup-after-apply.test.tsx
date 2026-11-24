// @vitest-environment jsdom
/** Regression: the panel after Apply re-renders with the updated doc; plan refusal on the estimate path. */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { apply, type EditOp } from '@/lib/editing/engine';
import { doc, media, track } from '@/lib/editing/__tests__/fixtures';
import type { EditTimeline } from '@/lib/editing/types';
import type { CleanupEstimate, CleanupJob } from '@/lib/editing/cleanup/types';
import { CleanupPanel } from '../CleanupPanel';
import type { CleanupJobApi } from '../useCleanupJob';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };
Element.prototype.scrollIntoView = vi.fn();
window.HTMLMediaElement.prototype.pause = vi.fn();
window.HTMLMediaElement.prototype.play = vi.fn().mockResolvedValue(undefined);
beforeEach(() => window.localStorage.clear());
afterEach(cleanup);

const CLEAN = 'aaaaaaaa-0000-4000-8000-000000000009';
const FREE: CleanupEstimate = { billingEnabled: true, hosted: false, credits: 0, affordable: true, cached: false };
const api = (estimate: CleanupEstimate = FREE): CleanupJobApi => ({
  estimate: vi.fn().mockResolvedValue(estimate),
  start: vi.fn().mockResolvedValue({ executionId: 'e1', target: 'Hum', hosted: false }),
  wait: vi.fn().mockResolvedValue({ executionId: 'e1', state: 'ready', target: 'Hum', applied: true, outputFileId: CLEAN } as CleanupJob),
});

/** Mounts the panel with a live doc: onApply runs the ops and re-renders, as the editor store does. */
function mountLive(initial: EditTimeline, selection: string[] | (() => string[]), a: CleanupJobApi) {
  let current = initial;
  const applied: EditOp[][] = [];
  let rerender = () => {};
  const ui = () => (
    <MantineProvider>
      <CleanupPanel doc={current} selection={typeof selection === 'function' ? selection() : selection} projectId="p1" readOnly={false} onApply={onApply} api={a} userId="u@x" />
    </MantineProvider>
  );
  const onApply = (ops: readonly EditOp[]) => {
    applied.push([...ops]);
    current = ops.reduce((acc, op) => apply(acc, op).doc, current);
    rerender();
  };
  const view = render(ui());
  rerender = () => view.rerender(ui());
  return { applied, rerender };
}

const linked = () => doc([track('V1', 'video', [media('v', 0, 100, 0, { linkId: 'g' })]), track('A1', 'audio', [media('a', 0, 100, 0, { linkId: 'g' })])]);

describe('QA probe: after Apply', () => {
  it('linked pair, picture clip selected: "Use original" is still offered after Apply', async () => {
    mountLive(linked(), ['v'], api());
    fireEvent.click(screen.getByTestId('cleanup-choice-hum'));
    fireEvent.click(await screen.findByTestId('cleanup-run'));
    fireEvent.click(await screen.findByTestId('cleanup-apply'));
    expect(screen.queryByTestId('cleanup-use-original')).not.toBeNull();
  });

  it('audio clip selected: the "before" side of the audition still plays the original after Apply', async () => {
    mountLive(doc([track('A1', 'audio', [media('a', 0, 100)])]), ['a'], api());
    fireEvent.click(screen.getByTestId('cleanup-choice-hum'));
    fireEvent.click(await screen.findByTestId('cleanup-run'));
    fireEvent.click(await screen.findByTestId('cleanup-apply'));
    const before = screen.getByTestId('cleanup-audio-before').getAttribute('src');
    expect(before).not.toContain(CLEAN);
  });

  it('a selection change mid-run does not hide the result: Apply still targets the job\'s own clip', async () => {
    let selection = ['a'];
    let release!: (job: CleanupJob) => void;
    const pending = new Promise<CleanupJob>((resolve) => { release = resolve; });
    const a: CleanupJobApi = {
      estimate: vi.fn().mockResolvedValue(FREE),
      start: vi.fn().mockResolvedValue({ executionId: 'e1', target: 'Hum', hosted: false }),
      wait: vi.fn().mockReturnValue(pending),
    };
    const panel = mountLive(doc([track('A1', 'audio', [media('a', 0, 100)]), track('A2', 'audio', [media('b', 0, 100)])]), () => selection, a);
    fireEvent.click(screen.getByTestId('cleanup-choice-hum'));
    fireEvent.click(await screen.findByTestId('cleanup-run'));
    // Another clip is selected while the charged job is still running.
    selection = ['b'];
    panel.rerender();
    release({ executionId: 'e1', state: 'ready', target: 'Hum', applied: true, outputFileId: CLEAN });
    fireEvent.click(await screen.findByTestId('cleanup-apply'));
    expect(panel.applied[0][0]).toEqual({ type: 'setProperty', itemId: 'a', key: 'assetRef', value: { kind: 'projectFile', projectFileId: CLEAN } });
  });
});

describe('QA probe: plan refusal as the real estimate route sends it', () => {
  it('billing on, plan without AI cleanup: estimate is 200 with unavailableReason, panel should show the plan message', async () => {
    const notEntitled: CleanupEstimate = { billingEnabled: true, hosted: true, credits: null, seconds: 30, balanceCredits: null, affordable: false, cached: false, unavailableReason: 'The Free plan does not include AI audio cleanup' };
    mountLive(doc([track('A1', 'audio', [media('a', 0, 100)])]), ['a'], api(notEntitled));
    fireEvent.click(screen.getByTestId('cleanup-choice-studio'));
    const alert = await screen.findByTestId('cleanup-refusal');
    expect(alert.getAttribute('data-kind')).toBe('plan');
  });
});
