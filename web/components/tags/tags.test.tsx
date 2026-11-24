// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { TagActivityResponse, TagDetailActivityResponse } from '@/lib/types/project-organization';
import { TagActivityView } from './TagActivityView';
import { TagDetailView } from './TagDetailView';
import TagActivityLinkDefault, { TagActivityLink } from './TagActivityLink';

vi.mock('next/link', () => ({ default: ({ children, href, ...r }: { children: React.ReactNode; href: string }) => <a href={href} {...r}>{children}</a> }));
const activity = vi.fn();
const detail = vi.fn();
vi.mock('@/lib/hooks/use-tag-activity', () => ({ useTagActivity: () => activity(), useTagDetailActivity: () => detail() }));

const base = (o: Partial<TagActivityResponse> = {}): TagActivityResponse => ({
  windowStart: '', windowEnd: '', billingEnabled: true,
  workspace: { projects: 3, runs: 10, credits: 12 },
  tags: [{ tagId: 't1', name: 'Acme', color: 'blue', projects: 2, runs: 6, passed: 5, failed: 1, credits: 7 }],
  untagged: { projects: 1, runs: 4, credits: 5 }, ...o,
});
const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, onchange: null, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(cleanup);

describe('TagActivityView', () => {
  it('shows rows, untagged and workspace total', () => {
    activity.mockReturnValue({ data: base() });
    wrap(<TagActivityView />);
    expect(screen.getByText('Acme')).toBeTruthy();
    expect(screen.getByText('Untagged')).toBeTruthy();
    expect(screen.getByText('Workspace total')).toBeTruthy();
    expect(screen.getByRole('columnheader', { name: 'Credits' })).toBeTruthy();
  });
  it('shows the note only when a project is counted twice', () => {
    activity.mockReturnValue({ data: base() });
    const { unmount } = wrap(<TagActivityView />);
    expect(screen.queryByTestId('tags-double-count-note')).toBeNull();
    unmount();
    activity.mockReturnValue({ data: base({ tags: [{ tagId: 't1', name: 'A', color: 'red', projects: 2, runs: 1, passed: 1, failed: 0, credits: 1 }, { tagId: 't2', name: 'B', color: 'red', projects: 2, runs: 1, passed: 1, failed: 0, credits: 1 }] }) });
    wrap(<TagActivityView />);
    expect(screen.getByTestId('tags-double-count-note')).toBeTruthy();
  });
  it('hides credits when billing is off', () => {
    activity.mockReturnValue({ data: base({ billingEnabled: false }) });
    wrap(<TagActivityView />);
    expect(screen.queryByRole('columnheader', { name: 'Credits' })).toBeNull();
  });
  it('shows the empty state with a link to projects', () => {
    activity.mockReturnValue({ data: base({ tags: [] }) });
    wrap(<TagActivityView />);
    expect(screen.getByText('Add tags to your projects to see activity per customer')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Go to projects' }).getAttribute('href')).toBe('/projects');
  });
});

describe('TagDetailView', () => {
  it('shows projects and run links', () => {
    const d: TagDetailActivityResponse = {
      tag: { id: 't1', name: 'Acme', color: 'blue' }, windowStart: '', windowEnd: '', billingEnabled: true,
      totals: { projects: 1, runs: 1, passed: 1, failed: 0, credits: 2 },
      projects: [{ projectId: 'p1', name: 'Spring', runs: 1, credits: 2 }],
      recentRuns: [{ executionId: 'e1', projectId: 'p1', workflowId: 'w1', projectName: 'Spring', workflowName: 'Promo', status: 'Passed', startedAt: null, completedAt: null, credits: 2 }],
    };
    detail.mockReturnValue({ data: d });
    wrap(<TagDetailView tagId="t1" />);
    expect(screen.getByRole('link', { name: 'Spring' }).getAttribute('href')).toBe('/projects/p1');
    expect(screen.getByText('Promo')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Open run' })).toBeTruthy();
  });
});

describe('href encoding', () => {
  it('encodes server ids in hrefs', () => {
    activity.mockReturnValue({ data: base({ tags: [{ tagId: 'a/b', name: 'Odd', color: 'red', projects: 1, runs: 1, passed: 1, failed: 0, credits: 1 }] }) });
    wrap(<TagActivityView />);
    expect(screen.getByRole('link', { name: 'Odd' }).getAttribute('href')).toBe('/tags/a%2Fb');
  });
  it('keeps the activity link at 44px', () => {
    wrap(<TagActivityLink />);
    expect(screen.getByRole('link', { name: 'Activity by tag' }).getAttribute('style')).toContain('min-height: 44px');
  });
});

describe('TagActivityLink', () => {
  it('links to /tags with both exports', () => {
    expect(TagActivityLinkDefault).toBe(TagActivityLink);
    wrap(<TagActivityLink />);
    expect(screen.getByRole('link', { name: 'Activity by tag' }).getAttribute('href')).toBe('/tags');
  });
});
