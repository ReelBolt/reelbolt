// @vitest-environment jsdom
/**
 * A refused render start reads as plain words in the Deliver panel itself (audit d4 N8, P4-DELIVER-03):
 * the server's own `{ message, errors }` sentence for a 400 it composed, the plan's sentence for a 429,
 * and never a JSON converter string, a JSON path or a raw body.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import { describeRenderRefusal } from '../render-refusal';
import { DeliverWorkspace } from '../DeliverWorkspace';

const renderEditTimeline = vi.fn();
vi.mock('@/lib/editing/sync/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/sync/api')>()),
  renderEditTimeline: (...args: unknown[]) => renderEditTimeline(...args),
}));
vi.mock('@/lib/hooks/use-execution-stream', () => ({ useExecutionStream: () => ({ events: [] }) }));
vi.mock('@/lib/hooks/use-billing', () => ({ useOutputHeightCap: () => null }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.mock('../../presets/PresetPicker', () => ({ PresetPicker: () => null }));

global.matchMedia = (query: string): MediaQueryList =>
  ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => true,
  }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
global.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver;

beforeEach(() => renderEditTimeline.mockReset());
afterEach(() => cleanup());

const converterProblem = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
  title: 'One or more validation errors occurred.',
  status: 400,
  errors: { '$.preset': ['The JSON value could not be converted to System.Nullable`1[ReelBolt.Shared.Workflows.TimelineRenderPreset]. Path: $.preset | LineNumber: 0 | BytePositionInLine: 30.'] },
  traceId: '00-abc-def-00',
};
const validation = {
  message: 'The render request is invalid.',
  errors: ["outputFileName may only contain letters, digits, '.', '_', '-' and spaces, must start with a letter or digit, and may not contain '..'."],
};
const busy = {
  code: 'PLAN_LIMIT_CONCURRENCY',
  feature: 'concurrent_executions',
  message: 'The trial plan allows 1 execution(s) at once and 1 are running. Wait for one to finish or upgrade your plan.',
  limit: 1,
  current: 1,
};

describe('plan refusal (A28)', () => {
  it('names the feature and asks for the plans link', () => {
    const r = describeRenderRefusal(new ApiError('', 402, [], { code: 'PLAN_NOT_ENTITLED', feature: 'resolution' }));
    expect(r.message).toContain('resolution');
    expect(r.planLink).toBe(true);
  });
});

describe('describeRenderRefusal', () => {
  it('turns a binding ProblemDetails into a plain sentence, never the converter text', () => {
    const refusal = describeRenderRefusal(new ApiError('ignored', 400, [], converterProblem));
    const text = [refusal.message, ...refusal.details].join(' ');
    expect(refusal.message).toBe('The render settings could not be read. Reload the page and try again.');
    expect(text).not.toMatch(/JSON|\$\.|System\.|converted/);
  });

  it("keeps the server's own 400 sentence and names the field in words", () => {
    const refusal = describeRenderRefusal(new ApiError(validation.message, 400, validation.errors, validation));
    expect(refusal.message).toBe('The render request is invalid.');
    expect(refusal.details).toEqual([
      "The file name may only contain letters, digits, '.', '_', '-' and spaces, must start with a letter or digit, and may not contain '..'.",
    ]);
  });

  it("shows the plan's 429 sentence", () => {
    const refusal = describeRenderRefusal(new ApiError(busy.message, 429, [], busy));
    expect(refusal.message).toBe(busy.message);
    expect(refusal.details).toEqual([]);
  });

  it('says something useful for a 429 without a body, a 500 and a network failure', () => {
    expect(describeRenderRefusal(new ApiError('Too Many Requests', 429)).message).toMatch(/Wait for one to finish/);
    expect(describeRenderRefusal(new ApiError('', 500, [], '')).message).toMatch(/could not start/);
    expect(describeRenderRefusal(new TypeError('Failed to fetch')).message).toMatch(/connection/);
  });
});

describe('DeliverWorkspace refused render', () => {
  const mount = () => {
    render(
      <MantineProvider>
        <DeliverWorkspace
          projectId="p"
          timelineId="t"
          revision={3}
          dirty={false}
          readOnly={false}
          inOut={{ inFrame: null, outFrame: null }}
          renders={[]}
          rendersLoading={false}
          refreshRenders={() => {}}
        />
      </MantineProvider>,
    );
  };

  it('shows a forced 400 in plain words in the panel', async () => {
    renderEditTimeline.mockRejectedValueOnce(new ApiError('ignored', 400, [], converterProblem));
    mount();
    fireEvent.click(screen.getByTestId('render-start'));
    const alert = await screen.findByTestId('render-refusal');
    expect(alert.textContent).toContain('The render settings could not be read.');
    expect(alert.textContent).not.toMatch(/JSON|\$\.preset|\{/);
  });

  it('shows a forced 429 with the plan sentence, and clears it on the next successful start', async () => {
    renderEditTimeline.mockRejectedValueOnce(new ApiError(busy.message, 429, [], busy));
    mount();
    fireEvent.click(screen.getByTestId('render-start'));
    expect((await screen.findByTestId('render-refusal')).textContent).toContain('Wait for one to finish or upgrade your plan.');

    renderEditTimeline.mockResolvedValueOnce({
      executionId: 'e', workflowId: 'w', timelineId: 't', revision: 3, preset: 'Hd1080p16x9', outputFileName: 'doc.mp4',
    });
    fireEvent.click(screen.getByTestId('render-start'));
    await waitFor(() => expect(screen.queryByTestId('render-refusal')).toBeNull());
  });
});
