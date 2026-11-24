// @vitest-environment jsdom
/**
 * T-008: one header language and honest empty states. Every docked surface is an EditorPanel header,
 * every page's empty state says what the page is for (the ONE catalogue description), and no bare
 * `Text fw={600}` remains as a surface title.
 */

import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { MantineProvider } from '@mantine/core';
import en from '@/messages/en/editor.json';
import { PageEmptyState } from '../ui/PageEmptyState';
import { InspectorPanel } from '../panels/InspectorPanel';
import { TimelineSettingsPanel } from '../panels/TimelineSettingsPanel';
import { ComparePanel } from '../panels/ComparePanel';
import { MetadataPanel } from '../media/MetadataPanel';
import { SourceViewer } from '../media/SourceViewer';
import { RenderQueue } from '../deliver/RenderQueue';
import { createEditorStore } from '@/lib/editing/store';
import { standardDoc } from '@/lib/editing/__tests__/fixtures';
import { VIDEO_FILE_ITEM } from '@/lib/editing/media/fixtures';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;
globalThis.fetch = vi.fn(() => new Promise<Response>(() => {})) as unknown as typeof fetch;
Element.prototype.scrollIntoView = vi.fn();
afterEach(cleanup);

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);
const description = (page: string) => (en.shell.workspaceBar.names as Record<string, { description: string }>)[page].description;
const PAGES = ['media', 'cut', 'edit', 'compose', 'color', 'audio', 'deliver'] as const;

describe('page empty states (T-008)', () => {
  it('every page renders the one catalogue description as its empty state', () => {
    for (const page of PAGES) {
      const { unmount } = wrap(<PageEmptyState page={page} testId={`empty-${page}`} />);
      const state = screen.getByTestId(`empty-${page}`);
      expect(state.textContent, page).toContain(description(page));
      unmount();
    }
  });

  it('each page host uses PageEmptyState for its page id', () => {
    const root = process.cwd();
    const hosts: [string, string][] = [
      ['components/editor/hub/MediaView.tsx', 'media'],
      ['components/editor/cut/CutWorkspace.tsx', 'cut'],
      ['components/editor/workspace/view/useViewerZoneView.tsx', 'edit'],
      ['components/editor/compose/ComposeList.tsx', 'compose'],
      ['components/editor/color/ColorWorkspace.tsx', 'color'],
      ['components/editor/audio/MixerPanel.tsx', 'audio'],
      ['components/editor/deliver/RenderQueue.tsx', 'deliver'],
    ];
    for (const [file, page] of hosts) {
      const source = readFileSync(join(root, file), 'utf8');
      expect(source, file).toMatch(new RegExp(`PageEmptyState[\\s\\S]{0,60}page="${page}"`));
    }
  });

  it('the Inspector and the render queue show their page/panel headers', () => {
    wrap(<InspectorPanel doc={standardDoc()} selection={[]} rate={{ num: 30, den: 1 }} readOnly={false} onSetProperty={vi.fn()} />);
    expect(screen.getByTestId('inspector-panel').textContent).toContain('Inspector');
    expect(screen.getByTestId('inspector-empty')).toBeTruthy();
    cleanup();
    wrap(<RenderQueue renders={[]} progress={null} outputUrl={() => ''} onCancel={vi.fn()} loading={false} />);
    expect(screen.getByTestId('render-queue-empty').textContent).toContain(description('deliver'));
  });
});

describe('one header language (T-008)', () => {
  it('the converted surfaces render an EditorPanel and no bare surface title', () => {
    const root = process.cwd();
    const files = [
      'components/editor/panels/InspectorPanel.tsx',
      'components/editor/panels/TimelineSettingsPanel.tsx',
      'components/editor/panels/RevisionHistoryPanel.tsx',
      'components/editor/panels/ComparePanel.tsx',
      'components/editor/media/MediaPool.tsx',
      'components/editor/media/SourceViewer.tsx',
      'components/editor/media/MetadataPanel.tsx',
    ];
    for (const file of files) {
      const source = readFileSync(join(root, file), 'utf8');
      expect(source, file).toMatch(/EditorPanel/);
      expect(source.replace(/<Text size="xs" fw=\{600\}>/g, ''), file).not.toContain('<Text size="sm" fw={600}>');
    }
  });

  it('settings, compare, metadata and the source viewer show their own surface titles', () => {
    wrap(<TimelineSettingsPanel settings={standardDoc().settings} hasClips={false} readOnly onChange={vi.fn()} />);
    expect(screen.getByTestId('timeline-settings-panel').textContent).toContain('Timeline settings');
    cleanup();
    wrap(<ComparePanel doc={standardDoc()} projectId="p" timelineId="t" onSeek={vi.fn()} />);
    expect(screen.getByTestId('compare-panel').textContent).toContain('Compare');
    cleanup();
    wrap(<MetadataPanel item={VIDEO_FILE_ITEM} />);
    expect(screen.getByTestId('metadata-panel').textContent).toContain(VIDEO_FILE_ITEM.fileName);
    cleanup();
    wrap(<SourceViewer projectId="p" item={null} />);
    expect(screen.getByTestId('source-viewer').textContent).toContain('Source');
    expect(screen.getByTestId('source-viewer-empty')).toBeTruthy();
  });

  it('an empty timeline shows the Edit page description with one real action', () => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: { ...standardDoc(), tracks: [] }, revision: 1 });
    expect(store.getState().doc.tracks.length).toBe(0);
    // The overlay itself is rendered by EditorWorkspace; the shared sentence and action are asserted here.
    wrap(<PageEmptyState page="edit" action={{ label: 'Open Media', onClick: vi.fn(), testId: 'edit-empty-open-media' }} testId="edit-empty" />);
    expect(screen.getByTestId('edit-empty').textContent).toContain(description('edit'));
    expect(screen.getByTestId('edit-empty-open-media').textContent).toBe('Open Media');
  });
});
