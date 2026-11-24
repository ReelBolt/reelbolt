// @vitest-environment jsdom
/**
 * RD-5 (UI half): a finished render row says, in plain words, what the render left out and what it
 * warned about, with a disclosure listing each sentence. A skipped item is prefixed with its name
 * from the current document when its id resolves, else its kind in plain words, else nothing. Older
 * renders without the fields (absent, null or empty) show nothing extra. No id, op name or JSON.
 */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { RenderQueue } from '../RenderQueue';
import { describeRenderNotes, itemNamesOf } from '../render-notes';
import type { EditTimelineRenderSummary } from '@/lib/editing/sync/api';

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

const passed: EditTimelineRenderSummary = {
  executionId: 'exec-9',
  workflowId: 'wf-9',
  timelineId: 'tl',
  status: 'Passed',
  startedAt: '2026-10-10T10:00:00Z',
  completedAt: '2026-10-10T10:05:00Z',
  revision: 3,
  preset: 'Hd1080p16x9',
  outputFileName: 'cut.mp4',
  errorMessage: null,
  stepResultId: 'sr-9',
  hasOutput: true,
  regions: null,
};

const ITEM_ID = '0c6f2a8e-1111-4bbb-9ccc-123456789abc';
const withNotes: EditTimelineRenderSummary = {
  ...passed,
  skippedItems: [
    { itemId: ITEM_ID, type: 'InsertClip', message: 'A screen insert is not part of rendered videos yet, so it was left out.' },
    { itemId: 'gone-item', type: 'Transition', message: 'A transition was left out because it does not sit between two enabled clips.' },
    { itemId: null, type: null, message: 'An item of a kind rendered videos do not support yet was left out.' },
  ],
  warnings: ['The audio was louder than the target and was turned down.'],
};

const names = new Map([[ITEM_ID, 'Interview A']]);

const queue = (renders: EditTimelineRenderSummary[], itemNames?: ReadonlyMap<string, string>) =>
  wrap(<RenderQueue renders={renders} progress={null} outputUrl={(id) => `/out/${id}`} onCancel={() => {}} loading={false} itemNames={itemNames} />);

describe('render notes in the render queue', () => {
  it('summarises what a finished render left out and its notes, and lists each sentence on demand', () => {
    queue([withNotes], names);
    const summary = screen.getByTestId('render-notes-summary');
    expect(summary.textContent).toContain('3 items were left out of this render');
    expect(summary.textContent).toContain('1 note');

    const toggle = screen.getByTestId('render-notes-toggle');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');

    const lines = screen.getAllByTestId('render-notes-line').map((el) => el.textContent);
    expect(lines).toEqual([
      'Interview A: A screen insert is not part of rendered videos yet, so it was left out.',
      'Transition: A transition was left out because it does not sit between two enabled clips.',
      'An item of a kind rendered videos do not support yet was left out.',
      'The audio was louder than the target and was turned down.',
    ]);

    const text = screen.getByTestId('render-row-exec-9').textContent ?? '';
    expect(text).not.toContain(ITEM_ID);
    expect(text).not.toContain('gone-item');
    expect(text).not.toContain('InsertClip');
    expect(text).not.toMatch(/[{}[\]]/);
  });

  it('uses singular wording and shows only the parts that are present', () => {
    queue([{ ...passed, skippedItems: [{ itemId: null, type: 'Caption', message: 'One caption was empty.' }], warnings: [] }]);
    expect(screen.getByTestId('render-notes-summary').textContent).toBe('1 item was left out of this render');
    cleanup();
    queue([{ ...passed, skippedItems: null, warnings: ['First.', 'Second.'] }]);
    expect(screen.getByTestId('render-notes-summary').textContent).toBe('2 notes');
  });

  it('renders nothing extra for an older render without the fields, or with null or empty ones', () => {
    queue([passed, { ...passed, executionId: 'e2', skippedItems: null, warnings: null }, { ...passed, executionId: 'e3', skippedItems: [], warnings: [] }]);
    expect(screen.queryByTestId('render-notes-summary')).toBeNull();
    expect(screen.queryByTestId('render-notes-toggle')).toBeNull();
  });

  it('says nothing while a render is still queued or running', () => {
    queue([{ ...withNotes, status: 'Running', completedAt: null }]);
    expect(screen.queryByTestId('render-notes-summary')).toBeNull();
  });

  it('uses readable text and a fingertip-sized disclosure', () => {
    queue([withNotes], names);
    const toggle = screen.getByTestId('render-notes-toggle');
    // Mantine's xs is 12px; nothing in the notes uses the 10px row caption size.
    expect(toggle.getAttribute('data-size')).toBe('xs');
    expect(screen.getByTestId('render-notes-summary').getAttribute('data-size')).toBe('xs');
    expect(toggle.className).toMatch(/notesToggle/);
  });
});

describe('describeRenderNotes', () => {
  it('drops blank messages, ignores unknown kinds and tolerates a non-array', () => {
    const notes = describeRenderNotes(
      {
        ...passed,
        skippedItems: [
          { itemId: null, type: 'someInternalOp', message: 'Left out for a reason.' },
          { itemId: null, type: null, message: '   ' },
        ],
        warnings: ['', 'Kept.'],
      },
      new Map(),
    );
    expect(notes?.skipped).toEqual(['Left out for a reason.']);
    expect(notes?.warnings).toEqual(['Kept.']);
    expect(describeRenderNotes({ ...passed, warnings: 'oops' as unknown as string[] }, new Map())).toBeNull();
  });
});

/**
 * C-328-1: the engine's real step output (TimelineRenderStepExecutor) sends each skip in
 * `skippedItems` with the C# type name (`InsertClip`, `Transition`, ...) AND copies every distinct
 * skip message into `warnings` (counted as "(n items)" when it repeats). Deliver must show each
 * sentence once, with the kind prefix, and count no copied warning as a note.
 */
describe('describeRenderNotes on the engine output shape', () => {
  const INSERT = 'A screen insert is not part of rendered videos yet, so it was left out.';
  const TRANSITION = 'A transition was left out because it does not sit between two enabled clips.';
  const SOUND_CUT = 'A sound transition was played as a cut because the next clip has no extra media before its start to blend with.';
  const AUDIO_ITEM = 'An item on an audio track that is not a sound clip was left out.';
  const engineRender: EditTimelineRenderSummary = {
    ...passed,
    skippedItems: [
      { itemId: 'i1', type: 'InsertClip', message: INSERT },
      { itemId: 'i2', type: 'InsertClip', message: INSERT },
      { itemId: 't1', type: 'Transition', message: TRANSITION },
      { itemId: 't2', type: 'Transition', message: SOUND_CUT },
      { itemId: 'a1', type: 'TitleClip', message: AUDIO_ITEM },
    ],
    warnings: [`${INSERT} (2 items)`, TRANSITION, SOUND_CUT, AUDIO_ITEM],
  };

  it('prefixes each skipped sentence with the plain word for the engine type name', () => {
    const notes = describeRenderNotes(engineRender, new Map());
    expect(notes?.skipped).toEqual([`Insert: ${INSERT}`, `Insert: ${INSERT}`, `Transition: ${TRANSITION}`, `Transition: ${SOUND_CUT}`, `Title: ${AUDIO_ITEM}`]);
  });

  it('drops a warning that repeats a skipped sentence, counted or not, and keeps any other warning', () => {
    expect(describeRenderNotes(engineRender, new Map())).toEqual({
      skipped: expect.any(Array),
      warnings: [],
      summary: '5 items were left out of this render',
    });
    const extra = describeRenderNotes({ ...engineRender, warnings: [...(engineRender.warnings ?? []), 'The audio was turned down.'] }, new Map());
    expect(extra?.warnings).toEqual(['The audio was turned down.']);
    expect(extra?.summary).toBe('5 items were left out of this render · 1 note');
  });

  it('shows each sentence once in the queue disclosure', () => {
    queue([engineRender]);
    fireEvent.click(screen.getByTestId('render-notes-toggle'));
    const lines = screen.getAllByTestId('render-notes-line').map((el) => el.textContent ?? '');
    expect(lines).toHaveLength(5);
    expect(lines.filter((l) => l.includes(TRANSITION))).toHaveLength(1);
    expect(lines.some((l) => l.includes('(2 items)'))).toBe(false);
  });
});

describe('itemNamesOf', () => {
  it('maps item ids to names across the tracks and the nested sequences, skipping blank names', () => {
    const map = itemNamesOf({
      tracks: [{ items: [{ id: 'a', name: 'Opening' }, { id: 'b', name: '  ' }] }],
      sequences: [{ tracks: [{ items: [{ id: 'c', name: 'Nested shot' }] }] }],
    });
    expect(map.get('a')).toBe('Opening');
    expect(map.has('b')).toBe(false);
    expect(map.get('c')).toBe('Nested shot');
  });
});
