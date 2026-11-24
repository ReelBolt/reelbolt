// @vitest-environment jsdom
/**
 * The Deliver workspace pieces, rendered: the queue shows each render's state in words, stops an
 * active render and downloads a finished one through the outputs URL (never a storage key); the
 * settings form posts the chosen preset and the marked range; the silence reader reads an artifact
 * defensively.
 */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { RenderQueue } from '../RenderQueue';
import { RenderSettings, presetShortSide } from '../RenderSettings';
import { countCaptions } from '../DeliverWorkspace';
import { readSilenceSpans } from '../../panels/RemoveSilenceDialog';
import type { EditTimelineRenderSummary, RenderEditTimelineRequest } from '@/lib/editing/sync/api';

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

afterEach(() => cleanup());

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);

const render1: EditTimelineRenderSummary = {
  executionId: 'exec-1',
  workflowId: 'wf-1',
  timelineId: 'tl',
  status: 'Running',
  startedAt: '2026-10-10T10:00:00Z',
  completedAt: null,
  revision: null,
  preset: 'Hd1080p16x9',
  outputFileName: 'cut.mp4',
  errorMessage: null,
  stepResultId: null,
  hasOutput: false,
  regions: null,
};
const render2: EditTimelineRenderSummary = {
  ...render1,
  executionId: 'exec-2',
  status: 'Passed',
  completedAt: '2026-10-10T10:05:00Z',
  revision: 7,
  preset: 'Vertical9x16',
  outputFileName: 'reel.mp4',
  stepResultId: 'sr-2',
  hasOutput: true,
  regions: { total: 4, cached: 3, list: [] },
};
const render3: EditTimelineRenderSummary = {
  ...render1,
  executionId: 'exec-3',
  status: 'Failed',
  errorMessage: 'ASSET_UNRESOLVED: Asset projectFile 1234 does not resolve',
};

describe('RenderQueue', () => {
  it('says so when there is nothing yet', () => {
    wrap(<RenderQueue renders={[]} progress={null} outputUrl={() => ''} onCancel={() => {}} loading={false} />);
    expect(screen.getByTestId('render-queue-empty')).toBeTruthy();
  });

  it('shows status, revision and cache per row, stops the active one and downloads the finished one', () => {
    const onCancel = vi.fn();
    wrap(
      <RenderQueue
        renders={[render1, render2, render3]}
        progress={{ executionId: 'exec-1', stage: 'Rendering region 2/4', percent: 35 }}
        outputUrl={(id, name) => `/api/v1/projects/p/outputs/${id}/download?saveAs=${name}`}
        onCancel={onCancel}
        onRetry={() => {}}
        onRemove={() => {}}
        loading={false}
      />,
    );
    const statuses = screen.getAllByTestId('render-status').map((el) => el.textContent);
    expect(statuses).toEqual(['Running', 'Done', 'Failed']);
    expect(screen.getAllByTestId('render-status').map((el) => el.querySelector('[data-state]')?.getAttribute('data-state'))).toEqual(['running', 'done', 'failed']);
    expect(screen.getByText('Vertical for phones', { exact: false })).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/\brev \d|cache \d/);
    expect(screen.getByTestId('render-progress-stage').textContent).toBe('Rendering region 2/4');
    expect(screen.getByTestId('render-error').textContent).not.toMatch(/ASSET_UNRESOLVED|1234/);
    expect(screen.getByTestId('render-error').textContent?.length).toBeGreaterThan(10);

    fireEvent.click(screen.getByTestId('render-cancel'));
    expect(onCancel).toHaveBeenCalledWith(render1);
    expect(screen.getByTestId('render-retry').textContent).toBe('Try again');
    expect(screen.getByTestId('render-remove').textContent).toBe('Remove');

    const download = screen.getByTestId('render-download') as HTMLAnchorElement;
    expect(download.getAttribute('href')).toBe('/api/v1/projects/p/outputs/sr-2/download?saveAs=reel.mp4');
    // WS-171: the link saves a file (the endpoint answers inline for video/mp4, which would navigate away).
    expect(download.getAttribute('download')).toBe('reel.mp4');
    // No row ever renders a bucket path.
    expect(document.body.textContent).not.toMatch(/projects\/.*\/outputFiles/);
  });
});

describe('RenderSettings', () => {
  it('posts the chosen preset, the marked range (on by default once both marks are set), and the pinned revision', () => {
    const onRender = vi.fn<(request: RenderEditTimelineRequest) => void>();
    wrap(<RenderSettings inOut={{ inFrame: 10, outFrame: 40 }} revision={5} readOnly={false} busy={false} onRender={onRender} />);

    // W0.6 (P4-OPS-12): the stored marks drive the Deliver range.
    expect((screen.getByTestId('render-range') as HTMLInputElement).checked).toBe(true);
    fireEvent.change(screen.getByTestId('render-file-name'), { target: { value: 'teaser.mp4' } });
    fireEvent.click(screen.getByTestId('render-start'));

    expect(onRender).toHaveBeenCalledTimes(1);
    const request = onRender.mock.calls[0][0];
    expect(request.preset).toBe('Hd1080p16x9');
    expect(request.revision).toBe(5);
    expect(request.range).toEqual({ inFrame: 10, outFrame: 40 });
    expect(request.outputFileName).toBe('teaser.mp4');
    expect(request.loudness).toBeNull();
    expect(request.encode).toBeNull();
  });

  it('refuses a file name outside the allowlist and has no marked range to offer', () => {
    const onRender = vi.fn();
    wrap(<RenderSettings inOut={{ inFrame: null, outFrame: null }} revision={2} readOnly={false} busy={false} onRender={onRender} />);
    fireEvent.change(screen.getByTestId('render-file-name'), { target: { value: '../escape.mp4' } });
    expect((screen.getByTestId('render-start') as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByTestId('render-range') as HTMLInputElement).disabled).toBe(true);
  });

  it('WS-160: a range request switches the marked-range switch on even when it started off', () => {
    const props = { revision: 1, readOnly: false, busy: false, onRender: vi.fn() };
    const { rerender } = wrap(<RenderSettings inOut={{ inFrame: null, outFrame: null }} {...props} />);
    expect((screen.getByTestId('render-range') as HTMLInputElement).checked).toBe(false);
    // The marks arrive (what "Export only <clip>" sets) and the request counter is bumped.
    rerender(<MantineProvider><RenderSettings inOut={{ inFrame: 30, outFrame: 90 }} rangeRequest={1} {...props} /></MantineProvider>);
    expect((screen.getByTestId('render-range') as HTMLInputElement).checked).toBe(true);
    fireEvent.click(screen.getByTestId('render-start'));
    expect(props.onRender.mock.calls[0][0].range).toEqual({ inFrame: 30, outFrame: 90 });
  });

  const isDisabled = (name: RegExp) => {
    const option = screen.getByRole('option', { name });
    return option.hasAttribute('data-combobox-disabled') || option.getAttribute('aria-disabled') === 'true';
  };

  it('Trial (720p): 1080p stays enabled with an "exports at 720p" note and can render; only 4K is refused', () => {
    const onRender = vi.fn<(request: RenderEditTimelineRequest) => void>();
    wrap(
      <RenderSettings inOut={{ inFrame: null, outFrame: null }} revision={1} readOnly={false} busy={false} onRender={onRender} maxOutputHeight={720} masterShortSide={2160} />,
    );
    // The default HD 1080p preset is clamped by the engine, not refused.
    const note = screen.getByTestId('render-preset-clamp');
    expect(note.textContent).toContain('Exports at 720p on your plan.');
    expect(screen.getByRole('link', { name: 'Upgrade for higher resolution' }).getAttribute('href')).toBe('/settings/billing');
    expect(screen.queryByText(/Your plan does not render this size/)).toBeNull();
    expect(screen.getByTestId('render-preset-cap').textContent).toContain('Your plan renders up to 720p. Upgrade to render at 1080p or 4K.');

    fireEvent.click(screen.getByTestId('render-preset'));
    expect(isDisabled(/^4K landscape/)).toBe(true);
    expect(isDisabled(/^Full HD landscape/)).toBe(false);
    expect(isDisabled(/^Master/)).toBe(false);
    expect(isDisabled(/^Audio only/)).toBe(false);

    const start = screen.getByTestId('render-start') as HTMLButtonElement;
    expect(start.disabled).toBe(false);
    fireEvent.click(start);
    expect(onRender).toHaveBeenCalledTimes(1);
    expect(onRender.mock.calls[0][0].preset).toBe('Hd1080p16x9');
  });

  it('Creator (1080p): Master above the cap is enabled with the note; 4K is refused with the 4K hint', () => {
    wrap(
      <RenderSettings inOut={{ inFrame: null, outFrame: null }} revision={1} readOnly={false} busy={false} onRender={vi.fn()} maxOutputHeight={1080} masterShortSide={2160} />,
    );
    // HD 1080p fits a 1080p plan: no note.
    expect(screen.queryByTestId('render-preset-clamp')).toBeNull();
    expect(screen.getByTestId('render-preset-cap').textContent).toContain('Your plan renders up to 1080p. Upgrade to render in 4K.');
    expect(screen.getByRole('link', { name: 'See plans' }).getAttribute('href')).toBe('/settings/billing');
    fireEvent.click(screen.getByTestId('render-preset'));
    expect(isDisabled(/^4K landscape/)).toBe(true);
    expect(isDisabled(/^Master/)).toBe(false);
    fireEvent.click(screen.getByRole('option', { name: /^Master/ }));
    expect(screen.getByTestId('render-preset-clamp').textContent).toContain('Exports at 1080p on your plan.');
    expect((screen.getByTestId('render-start') as HTMLButtonElement).disabled).toBe(false);
  });

  it('Pro (2160p): 4K is enabled and shows no note or hint', () => {
    wrap(
      <RenderSettings inOut={{ inFrame: null, outFrame: null }} revision={1} readOnly={false} busy={false} onRender={vi.fn()} maxOutputHeight={2160} masterShortSide={1080} />,
    );
    fireEvent.click(screen.getByTestId('render-preset'));
    expect(isDisabled(/^4K landscape/)).toBe(false);
    fireEvent.click(screen.getByRole('option', { name: /^4K landscape/ }));
    expect(screen.queryByTestId('render-preset-clamp')).toBeNull();
    expect(screen.queryByTestId('render-preset-cap')).toBeNull();
    expect((screen.getByTestId('render-start') as HTMLButtonElement).disabled).toBe(false);
  });

  it('offers every preset when there is no cap (unlimited plan or billing off)', () => {
    wrap(<RenderSettings inOut={{ inFrame: null, outFrame: null }} revision={1} readOnly={false} busy={false} onRender={vi.fn()} maxOutputHeight={null} masterShortSide={2160} />);
    expect(screen.queryByTestId('render-preset-cap')).toBeNull();
    expect(screen.queryByTestId('render-preset-clamp')).toBeNull();
    fireEvent.click(screen.getByTestId('render-preset'));
    expect(isDisabled(/^4K landscape/)).toBe(false);
    fireEvent.click(screen.getByRole('option', { name: /^Master/ }));
    expect(screen.queryByTestId('render-preset-clamp')).toBeNull();
    expect(presetShortSide('Uhd4k16x9', null)).toBe(2160);
    expect(presetShortSide('Master', 1440)).toBe(1440);
    expect(presetShortSide('AudioOnly', null)).toBeNull();
  });
});

describe('readSilenceSpans', () => {
  it('reads camelCase and PascalCase artifacts and skips malformed entries', () => {
    expect(readSilenceSpans({ silenceSpans: [{ id: 'g1', startSec: 1, endSec: 2 }, { id: 'bad', startSec: 3, endSec: 3 }, 'junk', { StartSec: 4, EndSec: 5 }] }))
      .toEqual([{ id: 'g1', startSec: 1, endSec: 2, sourceIndex: 0 }, { id: 'g3', startSec: 4, endSec: 5, sourceIndex: 0 }]);
    expect(readSilenceSpans(null)).toEqual([]);
    expect(readSilenceSpans({ silenceSpans: 'nope' })).toEqual([]);
  });
});

describe('countCaptions (WS-169)', () => {
  const doc = (items: Array<{ type: string; text?: string; enabled?: boolean }>) => ({ tracks: [{ items }] });
  it('counts enabled captions with text, as the SRT/WebVTT writers do', () => {
    expect(countCaptions(doc([]))).toBe(0);
    expect(countCaptions(doc([{ type: 'mediaClip' }, { type: 'caption', text: '   ' }, { type: 'caption', text: 'hi', enabled: false }]))).toBe(0);
    expect(countCaptions(doc([{ type: 'caption', text: 'hello' }, { type: 'caption', text: 'world', enabled: true }]))).toBe(2);
  });
});
