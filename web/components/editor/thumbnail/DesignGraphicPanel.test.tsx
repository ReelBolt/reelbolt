// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { SWRConfig } from 'swr';
import { ActivityProvider, useActivity, type ActivityContextValue } from '@/lib/activity/ActivityProvider';
import { FakeEventSource, signInAs } from '@/lib/activity/__tests__/helpers';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import type { ImageGraphicJob, ImageGraphicsResponse } from '@/lib/types/image-document';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

// Mantine's <Transition> (used by the SegmentedControl thumb and other animated parts)
// schedules its completion setTimeout through a requestAnimationFrame chain (use-transition).
// In jsdom those frame callbacks fire AFTER cleanup() has unmounted the component, so the
// trailing setTimeout's setStatus (React dispatchSetState) runs with the test window already
// torn down and leaks an unhandled "window is not defined". Keep the animation chain inert in
// tests: the frame callback never runs, so the completion timer is never scheduled. The panel
// behaviour asserted here does not depend on the transition animating.
vi.stubGlobal('requestAnimationFrame', () => 0);
vi.stubGlobal('cancelAnimationFrame', () => {});

const stream = vi.hoisted(() => ({ events: [] as { type: string; payload: Record<string, unknown> }[] }));
vi.mock('next/navigation', () => ({ usePathname: () => '/editor/thumbnails/d', useRouter: () => ({ push: vi.fn() }) }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.mock('@/lib/hooks/use-execution-stream', () => ({
  useExecutionStream: () => ({ events: stream.events, connectionState: 'connected', lastError: null, lastEventAt: null, metrics: {} }),
}));

import { DesignGraphicPanel, type DesignGraphicPanelProps } from './DesignGraphicPanel';

const job = (over: Partial<ImageGraphicJob> & { id: string }): ImageGraphicJob => ({
  documentId: 'd',
  executionId: `e-${over.id}`,
  workflowId: 'w',
  status: 'Running',
  description: 'A sale badge',
  frameWidth: 400,
  frameHeight: 200,
  placement: 'topRight',
  target: { x: 800, y: 40, width: 400, height: 200 },
  revisesLayerId: null,
  layerId: null,
  layer: null,
  summary: null,
  error: null,
  createdAt: '',
  completedAt: null,
  ...over,
});

const data = (over: Partial<ImageGraphicsResponse> = {}): ImageGraphicsResponse => ({
  graphics: [],
  estimate: { billingEnabled: true, lowCredits: 12, highCredits: 30, requiredCredits: 30, balanceCredits: 500, affordable: true },
  available: true,
  unavailableReason: null,
  ...over,
});

const mount = (props: Partial<Omit<DesignGraphicPanelProps, 'onDesign'>> & { onDesign?: Mock } = {}) => {
  const onDesign: Mock = props.onDesign ?? vi.fn().mockResolvedValue(undefined);
  render(
    <MantineProvider>
      <DesignGraphicPanel
        data={data()}
        canvas={{ width: 1280, height: 720 }}
        revisable={null}
        {...props}
        onDesign={onDesign}
      />
    </MantineProvider>,
  );
  return { onDesign };
};

const designButton = () => screen.getByRole('button', { name: /design it/i }) as HTMLButtonElement;

beforeEach(() => {
  stream.events = [];
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

describe('DesignGraphicPanel', () => {
  it('says what it costs and that it takes a few minutes', () => {
    mount();
    expect(screen.getByTestId('design-graphic-cost').textContent).toBe('About 12–30 credits.');
    expect(screen.getByTestId('design-graphic-needs').textContent).toBe('Needs 30 credits available to start; it can cost more.');
    expect(screen.getByText(/takes a few minutes\. You can keep editing meanwhile/)).toBeTruthy();
  });

  it('says when billing is off', () => {
    mount({ data: data({ estimate: { billingEnabled: false, lowCredits: 0, highCredits: 0, requiredCredits: 0, balanceCredits: 0, affordable: true } }) });
    expect(screen.getByTestId('design-graphic-cost').textContent).toBe('Billing is off on this installation.');
    expect(screen.queryByTestId('design-graphic-needs')).toBeNull();
  });

  it('posts the description, the place, the canvas and — only when chosen — the size', async () => {
    const { onDesign } = mount();
    expect(designButton().disabled).toBe(true);
    fireEvent.change(screen.getByLabelText('Describe the graphic'), { target: { value: 'A bold price tag' } });
    fireEvent.change(screen.getByLabelText('Where it goes'), { target: { value: 'bottomLeft' } });
    expect(designButton().disabled).toBe(false);
    fireEvent.click(designButton());
    await waitFor(() => expect(onDesign).toHaveBeenCalledTimes(1));
    expect(onDesign).toHaveBeenCalledWith({
      description: 'A bold price tag',
      placement: 'bottomLeft',
      canvasWidth: 1280,
      canvasHeight: 720,
    });
    expect(await screen.findByText(/Your graphic is on its way/)).toBeTruthy();
    expect((screen.getByLabelText('Describe the graphic') as HTMLTextAreaElement).value).toBe('');

    fireEvent.click(screen.getByText('Custom'));
    fireEvent.change(screen.getByLabelText('Width'), { target: { value: '500' } });
    fireEvent.change(screen.getByLabelText('Height'), { target: { value: '300' } });
    fireEvent.change(screen.getByLabelText('Describe the graphic'), { target: { value: 'An arrow' } });
    fireEvent.click(designButton());
    await waitFor(() => expect(onDesign).toHaveBeenCalledTimes(2));
    expect(onDesign.mock.calls[1][0]).toEqual({
      description: 'An arrow',
      placement: 'bottomLeft',
      width: 500,
      height: 300,
      canvasWidth: 1280,
      canvasHeight: 720,
    });
  });

  it('is disabled, and says why, when the balance is too low', () => {
    mount({ data: data({ estimate: { billingEnabled: true, lowCredits: 12, highCredits: 30, requiredCredits: 30, balanceCredits: 4, affordable: false } }) });
    fireEvent.change(screen.getByLabelText('Describe the graphic'), { target: { value: 'A badge' } });
    expect(designButton().disabled).toBe(true);
    expect(screen.getByTestId('design-graphic-cost').textContent).toMatch(/You have 4 credits.*Add credits/);
    expect(screen.getByTestId('design-graphic-needs').textContent).toBe('Needs 30 credits available to start; it can cost more.');
  });

  it('rounds what a design needs up to the next whole credit', () => {
    mount({ data: data({ estimate: { billingEnabled: true, lowCredits: 8, highCredits: 12, requiredCredits: 12.4, balanceCredits: 500, affordable: true } }) });
    expect(screen.getByTestId('design-graphic-needs').textContent).toBe('Needs 13 credits available to start; it can cost more.');
  });

  it('is disabled with the server\'s reason when this installation cannot design', () => {
    mount({ data: data({ available: false, unavailableReason: 'No graphic designer is set up here.' }) });
    expect(screen.getByText('No graphic designer is set up here.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /design it/i })).toBeNull();
    expect((screen.getByLabelText('Describe the graphic') as HTMLTextAreaElement).disabled).toBe(true);
  });

  it('shows a refusal in the server\'s own words', async () => {
    const onDesign = vi.fn().mockRejectedValue(
      new ApiError('You can start another design in a minute.', 429, [], { code: 'GRAPHIC_RATE_LIMITED', retryAfterSeconds: 60 }),
    );
    mount({ onDesign });
    fireEvent.change(screen.getByLabelText('Describe the graphic'), { target: { value: 'A badge' } });
    fireEvent.click(designButton());
    expect(await screen.findByText('You can start another design in a minute.')).toBeTruthy();
  });

  it('changes the selected designed graphic, showing what it was made from', async () => {
    const { onDesign } = mount({
      revisable: { layerId: 'l9', prompt: 'A red sale badge', graphicId: 'g1', workspaceVersionId: 'v1' },
      revisableLabel: 'Sale badge',
    });
    expect(screen.getByText('Change this graphic')).toBeTruthy();
    expect(screen.getByText(/A red sale badge/)).toBeTruthy();
    fireEvent.change(screen.getByLabelText('What should change?'), { target: { value: 'Make it blue' } });
    fireEvent.click(screen.getByRole('button', { name: /^Change it/ }));
    await waitFor(() => expect(onDesign).toHaveBeenCalledTimes(1));
    expect(onDesign).toHaveBeenCalledWith({ description: 'Make it blue', canvasWidth: 1280, canvasHeight: 720, revisesLayerId: 'l9' });

    // It can be switched back to designing a new one.
    fireEvent.click(screen.getByRole('button', { name: 'Design a new graphic instead' }));
    expect(screen.getByLabelText('Describe the graphic')).toBeTruthy();
    expect(screen.getByRole('button', { name: /change the selected graphic instead/i })).toBeTruthy();
  });

  it('lists this picture\'s designs with what each is doing', () => {
    stream.events = [{ type: 'step.progress', payload: { stage: 'Rendering the picture' } }];
    mount({
      data: data({
        graphics: [
          job({ id: 'run', status: 'Running' }),
          job({ id: 'fail', status: 'Failed', error: 'The designer could not render it.' }),
          job({ id: 'done', status: 'Passed', summary: 'A red badge, tilted 8 degrees.' }),
        ],
      }),
    });
    expect(screen.getByTestId('graphic-job-run').textContent).toMatch(/Designing.*Rendering the picture…/);
    expect(screen.getByTestId('graphic-job-fail').textContent).toMatch(/Did not work.*The designer could not render it\./);
    expect(screen.getByTestId('graphic-job-done').textContent).toMatch(/Done.*A red badge, tilted 8 degrees\./);
  });
});

describe('DesignGraphicPanel in the activity tray', () => {
  it('shows the in-place status chip for a running design that the tray tracks', async () => {
    FakeEventSource.instances = [];
    vi.stubGlobal('EventSource', FakeEventSource);
    signInAs();
    let ctx: ActivityContextValue | undefined;
    function Probe() {
      ctx = useActivity();
      return null;
    }
    render(
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <MantineProvider>
          <ActivityProvider>
            <Probe />
            <DesignGraphicPanel data={data({ graphics: [job({ id: 'run', status: 'Running' })] })} canvas={{ width: 1280, height: 720 }} revisable={null} onDesign={vi.fn()} />
          </ActivityProvider>
        </MantineProvider>
      </SWRConfig>,
    );
    expect(screen.queryByTestId('inline-job-status')).toBeNull();
    act(() => {
      void ctx?.trackJob({ type: 'graphic', title: 'A sale badge', executionId: 'e-run' }, new Promise<void>(() => undefined));
    });
    expect(await screen.findByTestId('inline-job-status')).toBeTruthy();
  });
});
