// @vitest-environment jsdom
/**
 * The "Early preview" label (product decisions D2+ and D12): one shared label on every editor shell —
 * the timeline editor's header on desktop/tablet and on phone, and the phone review page — that
 * opens a plain sentence when tapped or clicked, and never says "beta". The hub and the picture
 * editor pin it in their own route tests.
 */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.mock('next/link', () => ({
  default: ({ children, href, ...rest }: { children: React.ReactNode; href: string }) => <a href={href} {...rest}>{children}</a>,
}));
vi.mock('swr', () => ({
  default: () => ({ data: { name: 'Launch cut', revision: 3, document: { settings: { rate: { num: 30, den: 1 } }, tracks: [] } }, error: undefined }),
}));
vi.mock('@/lib/hooks/use-edit-timeline-renders', () => ({ useEditTimelineRenders: () => ({ data: [] }) }));
vi.mock('@/lib/hooks/use-editor-p9', () => ({ useReviewComments: () => ({ data: [], mutate: () => undefined, isLoading: false, error: undefined }) }));
vi.mock('@/components/editor/review/ReviewPanel', () => ({ ReviewPanel: () => <div data-testid="review-panel" /> }));

import { EarlyPreviewLabel, EARLY_PREVIEW_SENTENCE } from '../shell/EarlyPreviewLabel';
import { WorkspaceHeader } from '../shell/WorkspaceHeader';
import { MobileReview } from '../review/MobileReview';

global.matchMedia = ((query: string) => ({
  matches: false,
  media: query,
  onchange: null,
  addListener: () => {},
  removeListener: () => {},
  addEventListener: () => {},
  removeEventListener: () => {},
  dispatchEvent: () => true,
})) as unknown as typeof matchMedia;
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
};

const wrap = (node: React.ReactNode) => render(<MantineProvider>{node}</MantineProvider>);
afterEach(cleanup);

const header = (compact: boolean) =>
  wrap(
    <WorkspaceHeader
      name="Launch cut"
      revision={3}
      backHref="/editor?project=p"
      reviewHref="/editor/p/review/t"
      workspace="edit"
      onWorkspace={() => {}}
      onShortcuts={() => {}}
      onExport={() => {}}
      compact={compact}
    />,
  );

describe('EarlyPreviewLabel', () => {
  it('is a button named "Early preview" that opens the plain sentence on a tap', async () => {
    wrap(<EarlyPreviewLabel />);
    const button = screen.getByRole('button', { name: 'Early preview' });
    expect(screen.queryByText(EARLY_PREVIEW_SENTENCE)).toBeNull();
    fireEvent.click(button);
    expect(await screen.findByText(EARLY_PREVIEW_SENTENCE)).toBeTruthy();
    expect(EARLY_PREVIEW_SENTENCE).toBe('The editor is still being finished. Your work is saved, but some tools may change.');
    expect(document.body.textContent?.toLowerCase()).not.toMatch(/\bbeta\b|hover/);
  });
});

describe('the label on every editor shell', () => {
  it('desktop and tablet: the timeline header shows it beside the timeline name', () => {
    header(false);
    const start = screen.getByText('Launch cut').parentElement as HTMLElement;
    expect(within(start).getByRole('button', { name: 'Early preview' })).toBeTruthy();
    expect(document.body.textContent?.toLowerCase()).not.toMatch(/\bbeta\b/);
  });

  it('phone: it sits in the title area, so the header keeps its back and More controls', () => {
    header(true);
    const start = screen.getByText('Launch cut').parentElement as HTMLElement;
    expect(within(start).getByRole('button', { name: 'Early preview' })).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Back to all timelines' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'More' })).toBeTruthy();
  });

  it('review page: the phone review shows it beside the timeline name', () => {
    wrap(<MobileReview projectId="p" timelineId="t" />);
    expect(screen.getByRole('button', { name: 'Early preview' })).toBeTruthy();
    expect(document.body.textContent?.toLowerCase()).not.toMatch(/\bbeta\b/);
  });
});
