// @vitest-environment jsdom
/**
 * Wave 5: the queue is the spine of the result column. Rows say what they are (format, length, size, when), carry
 * progress and Stop while they run, a plain reason when they fail, their caption files when they made any, and
 * "Render again" only for renders whose settings are actually known.
 */

import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import type { EditTimelineRenderSummary, RenderEditTimelineRequest } from '@/lib/editing/sync/api';
import { RenderQueue } from '../RenderQueue';
import { DeliverWorkspace, type DeliverWorkspaceProps } from '../DeliverWorkspace';
import { SENT_RENDERS_KEY } from '../render-presets';

const sentRequests: RenderEditTimelineRequest[] = [];
vi.mock('@/lib/editing/sync/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/sync/api')>()),
  estimateEditTimelineRender: async () => ({ billingEnabled: false, affordable: true }),
  renderEditTimeline: async (_p: string, _t: string, request: RenderEditTimelineRequest) => {
    sentRequests.push(request);
    return { executionId: `exec-new-${sentRequests.length}`, workflowId: 'wf', timelineId: 't', revision: 3, preset: request.preset ?? 'Hd1080p16x9', outputFileName: 'new.mp4' };
  },
  getExportScope: async () => ({ format: 'otio', carries: ['Clips'], dropped: [], mediaMode: 'none' }),
}));
vi.mock('@/lib/editing/review/guest-links', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/review/guest-links')>()),
  listReviewLinks: async () => [],
}));
vi.mock('@/lib/editing/use-device-class', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/use-device-class')>()),
  useDeviceClass: () => 'desktop',
}));
vi.mock('@/lib/hooks/use-execution-stream', () => ({ useExecutionStream: () => ({ events: [] }) }));
vi.mock('@/lib/hooks/use-billing', () => ({ useOutputHeightCap: () => null }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.mock('../../presets/PresetPicker', () => ({ PresetPicker: () => null }));
vi.mock('next/link', () => ({ default: ({ children, href, ...rest }: { children: React.ReactNode; href: string }) => <a href={href} {...rest}>{children}</a> }));

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;

beforeEach(() => {
  sentRequests.length = 0;
  window.localStorage.clear();
});
afterEach(() => cleanup());

const row = (over: Partial<EditTimelineRenderSummary>): EditTimelineRenderSummary => ({
  executionId: 'e1', workflowId: 'w', timelineId: 't', status: 'Passed', startedAt: '2026-10-10T10:00:00Z', completedAt: '2026-10-10T10:05:00Z', revision: 3, preset: 'Vertical9x16',
  outputFileName: 'reel.mp4', errorMessage: null, stepResultId: 'sr-1', hasOutput: true, regions: null, durationSec: 83, sizeBytes: 12_500_000, outputFileId: 'f1', ...over,
});

const wrap = (node: React.ReactNode) => render(<MantineProvider>{node}</MantineProvider>);

describe('RenderQueue rows', () => {
  it('a finished row says its format, length and size, and saves the film and its caption files', () => {
    wrap(
      <RenderQueue
        renders={[row({ captionFiles: [{ format: 'srt', fileName: 'reel.srt', sizeBytes: 900, fileId: 'c1' }, { format: 'vtt', fileName: 'reel.vtt', sizeBytes: 950, fileId: 'c2' }] })]}
        progress={null}
        outputUrl={(id, name) => `/out/${id}?saveAs=${name}`}
        captionFileUrl={(id) => `/files/${id}/download`}
        onCancel={() => {}}
        loading={false}
      />,
    );
    const facts = screen.getByTestId('render-facts').textContent ?? '';
    expect(facts).toContain('Vertical for phones');
    expect(facts).toContain('1:23');
    expect(facts).toContain('11.9 MB');
    expect(facts).toContain('finished');
    expect(screen.getByTestId('render-download').getAttribute('href')).toBe('/out/sr-1?saveAs=reel.mp4');
    expect(screen.getByTestId('render-caption-srt').getAttribute('href')).toBe('/files/c1/download');
    expect(screen.getByTestId('render-caption-vtt').getAttribute('download')).toBe('reel.vtt');
    // Never a storage key, a bucket path or an id in words a person reads.
    expect(document.body.textContent).not.toMatch(/outputFiles|sr-1|c1\b/);
  });

  it('does not list the caption files of the film shown in the card above a second time', () => {
    wrap(
      <RenderQueue renders={[row({ captionFiles: [{ format: 'srt', fileName: 'reel.srt', sizeBytes: 900, fileId: 'c1' }] })]} progress={null} outputUrl={() => '/o'} captionFileUrl={() => '/c'} featuredExecutionId="e1" onCancel={() => {}} loading={false} />,
    );
    expect(screen.queryByTestId('render-caption-files')).toBeNull();
  });

  it('a running row shows the stage it was sent and can be stopped; nothing else is offered on it', () => {
    const onCancel = vi.fn();
    const running = row({ executionId: 'run', status: 'Running', completedAt: null, hasOutput: false, stepResultId: null, durationSec: null, sizeBytes: null });
    wrap(<RenderQueue renders={[running]} progress={{ executionId: 'run', stage: 'Rendering region 2/4', percent: 35 }} outputUrl={() => ''} onCancel={onCancel} onRenderAgain={() => {}} canRenderAgain={() => true} onRetry={() => {}} loading={false} />);
    expect(screen.getByTestId('render-progress-stage').textContent).toBe('Rendering region 2/4');
    expect(screen.getByTestId('render-facts').textContent).toContain('started');
    fireEvent.click(screen.getByTestId('render-cancel'));
    expect(onCancel).toHaveBeenCalledWith(running);
    expect(screen.queryByTestId('render-again')).toBeNull();
    expect(screen.queryByTestId('render-download')).toBeNull();
    expect(screen.queryByTestId('render-retry')).toBeNull();
  });

  it('a failed row gives a plain reason and what to do, never a code', () => {
    wrap(<RenderQueue renders={[row({ executionId: 'bad', status: 'Failed', hasOutput: false, stepResultId: null, errorMessage: 'ASSET_UNRESOLVED: Asset projectFile 7c1f2b3a does not resolve' })]} progress={null} outputUrl={() => ''} onCancel={() => {}} onRetry={() => {}} onRemove={() => {}} loading={false} />);
    const error = screen.getByTestId('render-error').textContent ?? '';
    expect(error).not.toMatch(/ASSET_UNRESOLVED|7c1f2b3a/);
    expect(error).toContain('try again');
    expect(screen.getByTestId('render-retry').textContent).toBe('Try again');
    expect(screen.getByTestId('render-remove').textContent).toBe('Remove');
  });

  it('a stopped row says nothing was saved', () => {
    wrap(<RenderQueue renders={[row({ executionId: 'stopped', status: 'Cancelled', hasOutput: false, stepResultId: null })]} progress={null} outputUrl={() => ''} onCancel={() => {}} loading={false} />);
    expect(screen.getByTestId('render-stopped').textContent).toContain('Nothing was saved');
  });

  it('offers "Render again" only on rows whose settings are known', () => {
    const onRenderAgain = vi.fn();
    const known = row({ executionId: 'known' });
    const unknown = row({ executionId: 'unknown', outputFileName: 'other.mp4' });
    wrap(<RenderQueue renders={[known, unknown]} progress={null} outputUrl={() => '/o'} onCancel={() => {}} onRenderAgain={onRenderAgain} canRenderAgain={(r) => r.executionId === 'known'} loading={false} />);
    const again = within(screen.getByTestId('render-row-known')).getByTestId('render-again');
    expect(again.textContent).toBe('Render again');
    expect(within(screen.getByTestId('render-row-unknown')).queryByTestId('render-again')).toBeNull();
    fireEvent.click(again);
    expect(onRenderAgain).toHaveBeenCalledWith(known);
  });

  it('the empty state is one sentence and the first action; loading says so', () => {
    const onFirstAction = vi.fn();
    const { rerender } = wrap(<RenderQueue renders={[]} progress={null} outputUrl={() => ''} onCancel={() => {}} onFirstAction={onFirstAction} loading={false} />);
    expect(screen.getByTestId('render-queue-empty').textContent).toContain('Nothing has been rendered from this timeline yet.');
    fireEvent.click(screen.getByTestId('render-queue-first-action'));
    expect(onFirstAction).toHaveBeenCalledTimes(1);
    rerender(<MantineProvider><RenderQueue renders={[]} progress={null} outputUrl={() => ''} onCancel={() => {}} loading /></MantineProvider>);
    expect(screen.getByTestId('render-queue-loading').textContent).toContain('Loading your renders');
  });
});

const props: DeliverWorkspaceProps = {
  projectId: 'p', timelineId: 't', revision: 3, dirty: false, readOnly: false, inOut: { inFrame: null, outFrame: null }, renders: [], rendersLoading: false, refreshRenders: () => {},
  timelineSize: { width: 1920, height: 1080, rate: { num: 30, den: 1 } },
  doc: { tracks: [{ items: [{ id: 'i1', name: 'Clip', type: 'mediaClip', recordOut: 900 }] }] } as unknown as DeliverWorkspaceProps['doc'],
};

const page = (over: Partial<DeliverWorkspaceProps> = {}) => (
  <MantineProvider>
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <DeliverWorkspace {...props} {...over} />
    </SWRConfig>
  </MantineProvider>
);

describe('the result column on the Deliver page', () => {
  it('puts the film first, then the queue, then sharing, export and hand-off, each under a panel header', () => {
    render(page({ renders: [row({})] }));
    const result = screen.getByTestId('deliver-result');
    const order = ['deliver-film-section', 'deliver-queue-section', 'deliver-review-section', 'deliver-export-section', 'deliver-handoff-section'].map((id) => within(result).getByTestId(id));
    for (let i = 1; i < order.length; i++) expect(order[i - 1].compareDocumentPosition(order[i]) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(within(result).getAllByRole('heading', { level: 2 }).map((h) => h.textContent)).toEqual(['Latest film', 'Render queue', 'Share for review', 'Export for other apps', 'Hand-off package']);
    expect(within(order[0]).getByTestId('film-card')).toBeTruthy();
    expect(within(order[1]).getByTestId('render-row-e1')).toBeTruthy();
    // The queue lives in the result column, not under the settings.
    expect(within(screen.getByTestId('deliver-controls')).queryByTestId('render-queue')).toBeNull();
    // One export panel and one hand-off card: nothing is drawn twice.
    expect(screen.getAllByTestId('export-panel')).toHaveLength(1);
    expect(screen.getAllByTestId('handoff-card')).toHaveLength(1);
  });

  it('with nothing rendered yet: a plain empty film state and a queue whose first action leads to the presets', () => {
    render(page());
    expect(screen.getByTestId('film-empty')).toBeTruthy();
    fireEvent.click(screen.getByTestId('render-queue-first-action'));
    expect(document.activeElement).toBe(screen.getByTestId('render-preset-Hd1080p16x9'));
  });

  it('remembers what a render was started with, and "Render again" puts exactly that back on the form', async () => {
    const onSetInOut = vi.fn();
    const { rerender } = render(page({ onSetInOut, inOut: { inFrame: 30, outFrame: 90 } }));
    // Set the form up: square, best quality, a file name, the marked range (on by default with both marks).
    fireEvent.click(screen.getByTestId('render-preset-Square1x1'));
    fireEvent.click(within(screen.getByTestId('render-quality')).getByText('Best'));
    fireEvent.change(screen.getByTestId('render-file-name'), { target: { value: 'teaser.mp4' } });
    fireEvent.click(screen.getByTestId('render-start'));
    await waitFor(() => expect(sentRequests).toHaveLength(1));
    expect(sentRequests[0]).toMatchObject({ preset: 'Square1x1', range: { inFrame: 30, outFrame: 90 }, outputFileName: 'teaser.mp4' });
    await waitFor(() => expect(window.localStorage.getItem(SENT_RENDERS_KEY)).toContain('exec-new-1'));

    // The queue now holds that render and one this device knows nothing about.
    const mine = row({ executionId: 'exec-new-1', preset: 'Square1x1', outputFileName: 'teaser.mp4' });
    const other = row({ executionId: 'elsewhere', preset: 'Vertical9x16', outputFileName: 'reel.mp4', completedAt: '2026-10-09T10:05:00Z' });
    rerender(page({ onSetInOut, inOut: { inFrame: null, outFrame: null }, renders: [mine, other] }));
    expect(within(screen.getByTestId('render-row-elsewhere')).queryByTestId('render-again')).toBeNull();

    // Move the form away, then ask for the same render again.
    fireEvent.click(screen.getByTestId('render-preset-Hd1080p16x9'));
    fireEvent.change(screen.getByTestId('render-file-name'), { target: { value: '' } });
    fireEvent.click(within(screen.getByTestId('render-row-exec-new-1')).getByTestId('render-again'));

    expect(screen.getByTestId('render-preset-Square1x1').getAttribute('aria-selected')).toBe('true');
    expect((screen.getByTestId('render-file-name') as HTMLInputElement).value).toBe('teaser.mp4');
    expect(onSetInOut).toHaveBeenCalledWith({ inFrame: 30, outFrame: 90 });
    expect(screen.getByTestId('render-prefilled').textContent).toContain('The settings of teaser.mp4 are loaded.');
    // Nothing started: the person reads the statement, then presses Render.
    expect(sentRequests).toHaveLength(1);

    rerender(page({ onSetInOut, inOut: { inFrame: 30, outFrame: 90 }, renders: [mine, other] }));
    fireEvent.click(screen.getByTestId('render-start'));
    await waitFor(() => expect(sentRequests).toHaveLength(2));
    expect(sentRequests[1]).toMatchObject({ preset: 'Square1x1', range: { inFrame: 30, outFrame: 90 }, outputFileName: 'teaser.mp4' });
    expect(sentRequests[1].encode).toMatchObject({ crf: 16, speed: 'Slow' });
  });

  it('a read-only timeline offers no retry, no render again and no film delete, and still lists everything', () => {
    window.localStorage.setItem(SENT_RENDERS_KEY, JSON.stringify([['e1', { settings: { preset: 'Vertical9x16', quality: 'good', custom: false, codec: 'H264', crf: 18, speed: 'Medium', rateControl: 'Quality', videoKbps: 8000, audioKbps: 192, normalise: false, targetLufs: -16, truePeak: -1.5, loudnessRange: 11 }, range: null, fileName: '', captions: null }]]));
    render(page({ readOnly: true, renders: [row({}), row({ executionId: 'bad', status: 'Failed', hasOutput: false, stepResultId: null, errorMessage: 'boom' })] }));
    expect(screen.getByTestId('film-card')).toBeTruthy();
    expect(screen.queryByTestId('film-delete')).toBeNull();
    expect(screen.queryByTestId('render-again')).toBeNull();
    expect(screen.queryByTestId('render-retry')).toBeNull();
    expect(screen.getByTestId('render-download')).toBeTruthy();
    expect(screen.getByTestId('share-read-only')).toBeTruthy();
    expect(screen.queryByTestId('share-open-form')).toBeNull();
  });
});
