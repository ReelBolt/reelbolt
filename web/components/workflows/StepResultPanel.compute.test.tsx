// @vitest-environment jsdom
/**
 * E8-UI: the executed-by badge on a step. Every case is driven from a step-result fixture of the
 * shape the API returns (workflow_step_results.compute_target + the persisted FromRunner tag),
 * and the two rules the badge must not break are asserted directly: an unknown target renders
 * NOTHING rather than a wrong label, and Local is never called cloud or "your machine".
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

import { StepResultPanel } from './StepResultPanel';
import { VideoEditorFlagProvider } from '@/components/shell/VideoEditorFlag';
import type { WorkflowStepResult } from '@/lib/types/workflow';

beforeEach(() => {
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null,
    addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {},
    dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

function result(overrides: Partial<WorkflowStepResult> = {}): WorkflowStepResult {
  return {
    id: '11111111-1111-1111-1111-111111111111',
    workflowStepId: '22222222-2222-2222-2222-222222222222',
    output: 'Done.',
    tokensUsed: 0,
    durationMs: 1500,
    executedAt: '2026-10-08T10:00:00Z',
    status: 'Completed',
    ...overrides,
  };
}

function renderPanel(stepResult: WorkflowStepResult, runnerNames?: Record<string, string>) {
  render(
    <MantineProvider>
      <StepResultPanel
        projectId="33333333-3333-3333-3333-333333333333"
        stepResult={stepResult}
        stepEvents={[]}
        liveTokenMetrics={{ inputTokens: 0, outputTokens: 0 }}
        artifactJson={null}
        artifactError={null}
        artifactLoading={false}
        runnerNames={runnerNames}
      />
    </MantineProvider>,
  );
}

describe('StepResultPanel executed-by badge', () => {
  it('names the machine the step ran on when the workspace list resolved it', () => {
    renderPanel(
      result({ computeTarget: 'OrgRunner', fromRunner: true, runnerId: 'aaaaaaaa-1111', runnerJobId: 'bbbbbbbb-2222' }),
      { 'aaaaaaaa-1111': 'Studio workstation' },
    );
    expect(screen.getByText('Ran on Studio workstation')).toBeTruthy();
  });

  it('still says a runner ran it when the device is no longer listed', () => {
    renderPanel(result({ computeTarget: 'OrgRunner', fromRunner: true, runnerId: 'cccccccc-3333' }));
    expect(screen.getByText('Ran on your machine')).toBeTruthy();
  });

  it('distinguishes the cloud pool from the customer\'s own machine', () => {
    renderPanel(result({ computeTarget: 'CloudPool' }));
    expect(screen.getByText('Ran in ReelBolt cloud')).toBeTruthy();
    expect(screen.queryByText('Ran on your machine')).toBeNull();
  });

  it('says the engine that handled the run did the step itself, and calls it neither', () => {
    renderPanel(result({ computeTarget: 'Local' }));
    expect(screen.getByText('Ran on this server')).toBeTruthy();
    expect(screen.queryByText('Ran in ReelBolt cloud')).toBeNull();
    expect(screen.queryByText('Ran on your machine')).toBeNull();
  });

  it('shows no badge at all when the run does not say where the step ran', () => {
    renderPanel(result());
    expect(screen.queryByText(/^Ran /)).toBeNull();
  });

  it('shows no badge for a target this build does not know', () => {
    renderPanel(result({ computeTarget: 'Quantum' }));
    expect(screen.queryByText(/^Ran /)).toBeNull();
    // ...and the FromRunner tag alone is not enough to claim a machine: it is absent here.
    expect(screen.queryByText('Ran on your machine')).toBeNull();
  });

  it('trusts the persisted FromRunner tag when the target is missing from the payload', () => {
    renderPanel(result({ fromRunner: true, runnerId: 'dddddddd-4444' }));
    expect(screen.getByText('Ran on your machine')).toBeTruthy();
  });
});

describe('StepResultPanel charge and failure code', () => {
  it('says what the step was charged, in credits', () => {
    renderPanel(result({ creditsCharged: 12.5 }));
    expect(screen.getByTestId('step-credits-charged').textContent).toBe('12.5 credits');
  });

  it('says "No charge" for a step that was recorded as free, and shows nothing when no charge was recorded', () => {
    renderPanel(result({ creditsCharged: 0 }));
    expect(screen.getByTestId('step-credits-charged').textContent).toBe('No charge');
    cleanup();
    renderPanel(result({ creditsCharged: null }));
    expect(screen.queryByTestId('step-credits-charged')).toBeNull();
    cleanup();
    renderPanel(result());
    expect(screen.queryByTestId('step-credits-charged')).toBeNull();
  });

  it('names the failure code and its class on a failed step', () => {
    renderPanel(result({ status: 'Failed', failureCode: 'CONFIG_INVALID', failureClass: 'UserInput' }));
    const line = screen.getByTestId('step-failure-code');
    expect(line.textContent).toContain('CONFIG_INVALID');
    expect(line.textContent).toContain('(UserInput)');
  });

  it('shows no failure line for a step that did not fail', () => {
    renderPanel(result());
    expect(screen.queryByTestId('step-failure-code')).toBeNull();
  });
});

/**
 * T-014: a render's "Open in Editor" is an entry point into the video editor. With the flag off it
 * must become the coming-soon label, not a button that promises an editor and lands on a
 * coming-soon page — the same defect one click later. Client render, so the flag is the shell's
 * context value, never an environment read.
 */
describe('StepResultPanel video editor flag', () => {
  const renderWithFlag = (enabled: boolean) =>
    render(
      <MantineProvider>
        <VideoEditorFlagProvider enabled={enabled}>
          <StepResultPanel
            projectId="33333333-3333-3333-3333-333333333333"
            stepResult={result({ outputStorageKey: 'renders/launch.mp4' })}
            stepEvents={[]}
            liveTokenMetrics={{ inputTokens: 0, outputTokens: 0 }}
            artifactJson={null}
            artifactError={null}
            artifactLoading={false}
          />
        </VideoEditorFlagProvider>
      </MantineProvider>,
    );

  it('offers "Open in Editor" while the flag is on', () => {
    renderWithFlag(true);
    expect(screen.getByRole('link', { name: 'Open in Editor' }).getAttribute('href')).toContain('/editor/');
    expect(screen.queryByText('Video editor · Coming soon')).toBeNull();
  });

  it('shows the coming-soon label, and no editor link, while the flag is off', () => {
    renderWithFlag(false);
    expect(screen.getByText('Video editor · Coming soon')).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'Open in Editor' })).toBeNull();
    expect(document.querySelector('a[href*="/editor/"]')).toBeNull();
  });
});

describe('StepResultPanel output media kind', () => {
  it('shows a png output as an image and keeps the download button', () => {
    renderPanel(result({ outputStorageKey: 'projects/p/step-3-image1.png' }));
    const media = screen.getByTestId('step-output-media');
    expect(media.tagName).toBe('IMG');
    expect(document.querySelector('video')).toBeNull();
    expect(document.querySelector('a[href*="/download"]')).not.toBeNull();
  });
});
