// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ProjectHistoryItemDiff } from '@/lib/types/project-history';
import { ItemDiffBody } from './ItemDiffView';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }),
  });
});
beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(cleanup);

const diff = (mode: string, overrides: Partial<ProjectHistoryItemDiff> = {}): ProjectHistoryItemDiff => ({
  kind: 'File',
  key: 'f1',
  name: 'intro.mp4',
  mode,
  hunks: null,
  fromSize: 10,
  toSize: 20,
  workflow: null,
  timeline: null,
  designSystem: null,
  pronunciation: null,
  project: null,
  ...overrides,
});

const renderBody = (mode: string, overrides: Partial<ProjectHistoryItemDiff> = {}) =>
  render(<MantineProvider><ItemDiffBody diff={diff(mode, overrides)} /></MantineProvider>);

describe('item diff body', () => {
  it('renders text hunks line by line', () => {
    renderBody('text', {
      hunks: [{ fromStart: 1, fromCount: 0, toStart: 1, toCount: 1, lines: [{ op: '+', text: 'hello world' }] }],
    });
    expect(screen.getByText('hello world')).toBeTruthy();
    expect(screen.getByTestId('hunk-view')).toBeTruthy();
  });

  it('says how a binary file changed in size', () => {
    renderBody('binary');
    expect(screen.getByText(/binary file/)).toBeTruthy();
  });

  it('says a file is too large to compare line by line', () => {
    renderBody('tooLarge');
    expect(screen.getByText(/too large to compare line by line/)).toBeTruthy();
  });

  it('says plainly that held content cannot be shown instead of showing nothing', () => {
    renderBody('unavailable');
    expect(screen.getByText('This content cannot be shown.')).toBeTruthy();
  });

  it('summarises workflow steps added and removed', () => {
    renderBody('workflow', {
      workflow: {
        added: [{ order: 1, type: 'Generate', agentName: 'Narrator' }],
        removed: [{ order: 2, type: 'Render', agentName: null }],
        changed: [],
      },
    });
    expect(screen.getByText(/Step 1: Generate \(Narrator\)/)).toBeTruthy();
    expect(screen.getByText(/Step 2: Render/)).toBeTruthy();
  });

  it('summarises the timeline revisions and counts', () => {
    renderBody('timeline', {
      timeline: {
        fromRevision: 1,
        toRevision: 2,
        fromSummary: { tracks: 1, clips: 2, durationFrames: 100 },
        toSummary: { tracks: 2, clips: 3, durationFrames: 200 },
      },
    });
    expect(screen.getByText(/Revision 1 to 2/)).toBeTruthy();
  });

  it('lists pronunciation words added, removed and changed', () => {
    renderBody('pronunciation', {
      pronunciation: { added: ['Novara'], removed: ['ReelBolt'], changed: ['AI'] },
    });
    expect(screen.getByText('Added words: Novara')).toBeTruthy();
    expect(screen.getByText('Removed words: ReelBolt')).toBeTruthy();
    expect(screen.getByText('Changed words: AI')).toBeTruthy();
  });

  it('shows project name and description before/after', () => {
    renderBody('project', {
      project: {
        name: { from: 'Old name', to: 'New name' },
        description: { from: 'Old', to: 'New' },
      },
    });
    expect(screen.getByText('Name: Old name to New name')).toBeTruthy();
    expect(screen.getByText('Description: Old to New')).toBeTruthy();
  });

  it('shows the design system diff through VersionDiff', () => {
    renderBody('designSystem', {
      designSystem: { counts: { added: 0, removed: 0, changed: 0 }, added: [], removed: [], changed: [] },
    });
    expect(screen.getByText('Design system changes')).toBeTruthy();
    expect(screen.getByTestId('version-diff')).toBeTruthy();
  });

  it('falls back to a plain "no differences" sentence for an unknown mode', () => {
    renderBody('mystery');
    expect(screen.getByText('No differences to show.')).toBeTruthy();
  });
});
