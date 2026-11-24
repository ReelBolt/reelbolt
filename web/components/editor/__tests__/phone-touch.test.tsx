// @vitest-environment jsdom
/**
 * Phone shell pieces (remediation W0.7): the conflict banner as a one-line bar, "More > Export",
 * and the one "needs a larger screen" state.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ReadOnlyBanner } from '../sync/ReadOnlyBanner';
import { WorkspaceHeader } from '../shell/WorkspaceHeader';
import { LargerScreenOnly, NeedsLargerScreen } from '../shell/LargerScreen';
import { resetTestViewport, setTestViewport } from '@/lib/editing/__tests__/viewport';
import type { SaveState } from '@/lib/editing/sync';
import { BusesPanel } from '../audio/BusesPanel';
import { StructurePanel } from '../panels/StructurePanel';
import { GradeStackEditor } from '../color/GradeStackEditor';
import { SecondaryFields } from '../color/SecondaryFields';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { standardDoc } from '@/lib/editing/__tests__/fixtures';
import { newNode, withNode } from '@/lib/editing/color/grade';

(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
};

let matching = new Set<string>();
beforeEach(() => {
  resetTestViewport();
  matching = new Set();
  global.matchMedia = (query: string): MediaQueryList =>
    ({
      matches: matching.has(query),
      media: query,
      onchange: null,
      addListener: () => {},
      removeListener: () => {},
      addEventListener: () => {},
      removeEventListener: () => {},
      dispatchEvent: () => true,
    }) as MediaQueryList;
});
beforeEach(() => window.localStorage.setItem('rb.editor.advancedAudio', '1'));
afterEach(cleanup);

const wrap = (node: React.ReactNode) => render(<MantineProvider>{node}</MantineProvider>);

const conflict: SaveState = { status: 'conflict', revision: 4, lastSavedAt: 1, conflictRevision: 6, violations: [], message: null, attempt: 0 };

describe('ReadOnlyBanner as a bar', () => {
  it('collapses the conflict to one line with Reload, keeping the same test ids', () => {
    const onReload = vi.fn();
    wrap(<ReadOnlyBanner save={conflict} onReload={onReload} variant="bar" />);
    const bar = screen.getByTestId('read-only-banner');
    expect(bar.getAttribute('data-variant')).toBe('bar');
    expect(bar.getAttribute('role')).toBe('alert');
    expect(bar.textContent).toContain('Changed elsewhere. Your edits are kept, not saved.');
    // The long explanation (with revision numbers) is the desktop banner's, not the bar's.
    expect(bar.textContent).not.toContain('revision');
    fireEvent.click(screen.getByTestId('read-only-reload'));
    expect(onReload).toHaveBeenCalledTimes(1);
  });

  it('keeps the full banner by default', () => {
    wrap(<ReadOnlyBanner save={conflict} onReload={() => {}} />);
    expect(screen.getByTestId('read-only-banner').textContent).toContain('changed somewhere else');
  });
});

describe('WorkspaceHeader on a phone', () => {
  it('"More > Export" calls onExport', async () => {
    const onExport = vi.fn();
    wrap(
      <WorkspaceHeader name="Cut" revision={3} backHref="/b" reviewHref="/r" workspace="edit" onWorkspace={() => {}} onShortcuts={() => {}} onExport={onExport} compact />,
    );
    await act(async () => fireEvent.click(screen.getByTestId('header-more')));
    fireEvent.click(await screen.findByTestId('more-export'));
    expect(onExport).toHaveBeenCalledTimes(1);
  });
});

describe('the "needs a larger screen" state', () => {
  it('is one plain sentence naming the tool', () => {
    wrap(<NeedsLargerScreen feature="curves" what="Editing curves" />);
    const note = screen.getByTestId('needs-larger-screen');
    expect(note.getAttribute('data-feature')).toBe('curves');
    expect(note.textContent).toBe('Editing curves needs a larger screen. Open this timeline on a tablet or computer to use it.');
  });

  it('replaces its children on a phone, and only there', async () => {
    setTestViewport('phone');
    wrap(
      <LargerScreenOnly feature="curves" what="Editing curves">
        <button data-testid="curve-editor">curves</button>
      </LargerScreenOnly>,
    );
    await act(async () => {});
    expect(screen.queryByTestId('curve-editor')).toBeNull();
    expect(screen.getByTestId('needs-larger-screen')).toBeTruthy();
  });

  it('renders the tool itself on a larger screen', async () => {
    wrap(
      <LargerScreenOnly feature="curves" what="Editing curves">
        <button data-testid="curve-editor">curves</button>
      </LargerScreenOnly>,
    );
    await act(async () => {});
    expect(screen.getByTestId('curve-editor')).toBeTruthy();
    expect(screen.queryByTestId('needs-larger-screen')).toBeNull();
  });
});

describe('tools cut on a phone show the state instead of a broken control', () => {
  const doc = standardDoc();
  const noop = () => {};

  it.each([
    ['buses-automation', 'automation-mode-', () => <BusesPanel doc={doc} readOnly={false} playheadFrame={0} playbackSpeed={0} newId={() => 'n'} onOp={noop} onApply={noop} />],
    ['compound', 'compound-create', () => <StructurePanel doc={doc} readOnly={false} selection={['c1']} playheadFrame={0} playbackSpeed={0} newId={() => 'n'} onOp={noop} onApply={noop} onSelect={noop} />],
    ['curves', 'curve-', () => <GradeStackEditor projectId="p1" stack={withNode(null, newNode('n1'))} readOnly={false} onChange={noop} newId={() => 'n'} lutFiles={[]} subject="Clip" />],
    ['window-tracking', 'grade-window-track', () => <SecondaryFields node={{ ...newNode('n1'), window: { shape: { kind: 'ellipse', x: 0.5, y: 0.5, w: 0.3, h: 0.3 } } }} readOnly={false} onChange={noop} onTrackWindow={async () => null} />],
  ] as const)('%s', async (feature, controlPrefix, make) => {
    setTestViewport('phone');
    const { container } = wrap(make());
    // The same tool renders on a larger screen (so the assertion below is about the phone, not about a missing control).
    setTestViewport('desktop');
    const larger = wrap(make());
    await act(async () => {});
    expect(larger.container.querySelector(`[data-testid^="${controlPrefix}"]`)).not.toBeNull();
    larger.unmount();
    setTestViewport('phone');
    await act(async () => {});
    const notes = screen.getAllByTestId('needs-larger-screen').map((n) => n.getAttribute('data-feature'));
    expect(notes).toContain(feature);
    expect(container.querySelector(`[data-testid^="${controlPrefix}"]`)).toBeNull();
  });
});

describe('the timeline toolbar never widens the page on a phone', () => {
  it('scrolls inside itself instead of overflowing (an overflow made mobile Chrome widen the layout viewport past the screen)', () => {
    const css = readFileSync(join(__dirname, '../shell/workspace.module.css'), 'utf8');
    const rule = /\.toolbar\s*\{([^}]*)\}/.exec(css)?.[1] ?? '';
    expect(rule).toMatch(/overflow-x:\s*auto/);
    expect(rule).toMatch(/min-width:\s*0/);
  });
});
