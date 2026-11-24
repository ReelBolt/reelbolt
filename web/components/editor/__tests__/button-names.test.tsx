// @vitest-environment jsdom
/**
 * VF7 / T-029: every button the editor draws has an accessible name. The panels are rendered for real (so a computed
 * name, such as one built from a track or clip name, is checked as well as a literal one) and the sources are scanned
 * so that an icon button added later cannot ship nameless either.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { cleanup, render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { WorkspaceBar } from '../shell/WorkspaceBar';
import { HistoryPanel } from '../panels/HistoryPanel';
import { InspectorPanel } from '../panels/InspectorPanel';
import { MarkersPanel } from '../panels/MarkersPanel';
import { KeyframeLane } from '../panels/KeyframeLane';
import { SubtitlesPanel } from '../panels/SubtitlesPanel';
import { StructurePanel } from '../panels/StructurePanel';
import { MixerPanel } from '../audio/MixerPanel';
import { BusesPanel } from '../audio/BusesPanel';
import { GradeStackEditor } from '../color/GradeStackEditor';
import { TransportBar } from '../viewer/TransportBar';
import { LayoutMenu } from '../prefs/LayoutMenu';
import { createEditorStore } from '@/lib/editing/store';
import { standardDoc } from '@/lib/editing/__tests__/fixtures';
import { newNode, withNode } from '@/lib/editing/color/grade';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
};

afterEach(cleanup);

/** The name a screen reader would read for a button: aria-label, aria-labelledby, its text, an image alt or a title. */
function accessibleName(el: Element): string {
  const label = el.getAttribute('aria-label')?.trim();
  if (label) return label;
  const by = el.getAttribute('aria-labelledby');
  if (by) {
    const text = by.split(/\s+/).map((id) => el.ownerDocument.getElementById(id)?.textContent ?? '').join(' ').trim();
    if (text) return text;
  }
  const own = (el.textContent ?? '').trim();
  if (own) return own;
  const alt = el.querySelector('img[alt]')?.getAttribute('alt')?.trim();
  if (alt) return alt;
  return el.getAttribute('title')?.trim() ?? '';
}

function nameless(container: HTMLElement): string[] {
  return Array.from(container.querySelectorAll('button, [role="button"]'))
    // Mantine's number-field steppers are pointer-only (tabindex -1): the field itself is named and takes the arrow keys.
    .filter((b) => !b.classList.contains('mantine-NumberInput-control'))
    .filter((b) => accessibleName(b) === '')
    .map((b) => b.outerHTML.slice(0, 160));
}

const doc = standardDoc();
const noop = () => {};
const rate = { num: 30, den: 1 };
const store = createEditorStore({ timelineId: 't', projectId: 'p', doc, revision: 1 });

const panels: [string, () => React.ReactNode][] = [
  ['WorkspaceBar', () => <WorkspaceBar active="edit" onChange={noop} />],
  ['HistoryPanel', () => <HistoryPanel history={store.getState().history} cursor={0} onUndo={noop} onRedo={noop} onJumpTo={noop} />],
  ['InspectorPanel (a clip)', () => <InspectorPanel doc={doc} selection={['c1']} rate={rate} readOnly={false} onSetProperty={noop} />],
  ['InspectorPanel (nothing selected)', () => <InspectorPanel doc={doc} selection={[]} rate={rate} readOnly={false} onSetProperty={noop} />],
  [
    'MarkersPanel',
    () => (
      <MarkersPanel
        rows={[{ id: 'm1', name: 'Hook', frame: 45, color: null, derived: false } as unknown as Parameters<typeof MarkersPanel>[0]['rows'][number]]}
        rate={rate}
        playheadFrame={0}
        onSeek={noop}
        onAdd={noop}
        onRename={noop}
        onRemove={noop}
        onSelectItem={noop}
      />
    ),
  ],
  ['MixerPanel', () => <MixerPanel doc={doc} readOnly={false} playheadFrame={0} flags={(t) => ({ enabled: t.enabled, mute: t.mute, solo: t.solo, locked: t.locked })} getMeters={() => null} lastRenderLoudness={null} onOp={noop} onToggle={noop} />],
  ['BusesPanel', () => <BusesPanel doc={doc} readOnly={false} playheadFrame={0} playbackSpeed={0} newId={() => 'n'} onOp={noop} onApply={noop} />],
  ['StructurePanel', () => <StructurePanel doc={doc} readOnly={false} selection={['c1']} playheadFrame={0} playbackSpeed={0} newId={() => 'n'} onOp={noop} onApply={noop} onSelect={noop} />],
  ['GradeStackEditor', () => <GradeStackEditor projectId="p1" stack={withNode(null, newNode('n1'))} readOnly={false} onChange={noop} newId={() => 'n'} lutFiles={[]} subject="Clip" />],
  ['SubtitlesPanel', () => <SubtitlesPanel doc={doc} projectId="p" timelineId="t" seedStepResultId="seed" readOnly={false} playheadFrame={0} newItemId={() => 'x'} newTrackId={() => 's'} onApply={noop} onSetProperty={noop} onSeek={noop} />],
  ['TransportBar', () => <TransportBar playheadFrame={300} rate={rate} endFrame={1000} speed={0} loop={false} inFrame={null} outFrame={null} onSeek={noop} onSpeed={noop} onStep={noop} onPrevEdit={noop} onNextEdit={noop} onPrevMarker={noop} onNextMarker={noop} onMarkIn={noop} onMarkOut={noop} onToggleLoop={noop} onPlayInToOut={noop} />],
  ['LayoutMenu', () => <LayoutMenu workspace="edit" layouts={[]} current={{} as never} onApply={noop} onReset={noop} onSaveLayouts={noop} />],
  ['KeyframeLane', () => <KeyframeLane target={{ itemId: 'c1' }} param="opacity" channel={{ value: 1, keys: [{ frame: 10, value: 0.5, interp: 'linear' }] } as never} startFrame={0} endFrame={60} playheadFrame={20} readOnly={false} onOp={noop} />],
];

describe('every rendered editor button has an accessible name', () => {
  it.each(panels)('%s', (_name, make) => {
    const { container } = render(<MantineProvider>{make()}</MantineProvider>);
    expect(container.querySelectorAll('button, [role="button"]').length).toBeGreaterThanOrEqual(0);
    expect(nameless(container)).toEqual([]);
  });

  it('flags a seeded nameless icon button (the check can fail)', () => {
    const { container } = render(<button type="button"><svg aria-hidden="true" /></button>);
    expect(nameless(container)).toHaveLength(1);
  });
});

function walk(dir: string, out: string[] = []): string[] {
  for (const n of readdirSync(dir)) {
    const p = join(dir, n);
    if (statSync(p).isDirectory()) {
      if (n !== '__tests__' && n !== 'node_modules') walk(p, out);
    } else if (/\.tsx$/.test(n)) out.push(p);
  }
  return out;
}

describe('editor sources: icon buttons are named and go through EditorIcon', () => {
  const root = join(__dirname, '..');
  const files = walk(root);
  it('no raw <ActionIcon> without an aria-label outside the EditorIcon wrapper', () => {
    const bad: string[] = [];
    for (const f of files) {
      if (f.endsWith(join('ui', 'EditorIcon.tsx'))) continue;
      const s = readFileSync(f, 'utf8');
      for (const m of s.matchAll(/<ActionIcon\b[\s\S]*?>/g)) if (!/aria-label=/.test(m[0])) bad.push(`${f}: ${m[0].slice(0, 80)}`);
    }
    expect(bad).toEqual([]);
  });
  it('every <EditorIcon ... label> button carries a non-empty label', () => {
    const bad: string[] = [];
    for (const f of files) {
      const s = readFileSync(f, 'utf8');
      for (const m of s.matchAll(/<EditorIcon\b[^>]*?\blabel=(?:""|\{""\}|\{''\})/g)) bad.push(`${f}: ${m[0].slice(0, 80)}`);
    }
    expect(bad).toEqual([]);
  });
});
