// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, renderHook, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { useT } from '@/i18n';
import type { RunAppInput } from '@/lib/types/workflow';
import { RunAppInputsList } from './RunAppInputsList';
import { runInputWords, runInputsSummary } from './run-app-inputs';

afterEach(cleanup);
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (query: string) => ({
    matches: false, media: query, onchange: null, addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {}, dispatchEvent: () => false,
  }),
});

const apps = () => renderHook(() => useT()).result.current;

const FIELDS: RunAppInput[] = [
  { id: 'liteMode', label: 'Lite mode (cheaper)', type: 'toggle', value: true, valueText: 'yes' },
  { id: 'whatTheySay', label: 'What they say', type: 'longText', value: 'Welcome to Caffè Lume!', valueText: 'Welcome to Caffè Lume!' },
  { id: 'shape', label: 'Shape', type: 'shape', value: '9:16', valueText: '9:16' },
  { id: 'length', label: 'Length', type: 'duration', value: 10, valueText: '10' },
  { id: 'look', label: 'Caption look', type: 'choice', value: 'Karaoke', valueText: 'Word by word', valueLabelKey: 'no.such.key' },
  { id: 'model', label: 'Video model', type: 'model', valueText: 'Kling 3', modelName: 'Kling 3' },
  { id: 'gone', label: 'Lip-sync model', type: 'model', valueText: 'a model of this workspace' },
];

describe('a past run\'s app fields in words (docs/design/app-inputs.md 10.4)', () => {
  it('words each type and never shows a model id', () => {
    const t = apps();
    expect(FIELDS.map((f) => runInputWords(t, f).value)).toEqual([
      'On', 'Welcome to Caffè Lume!', 'Vertical (9:16)', '10s', 'Word by word', 'Kling 3', 'A model of this workspace',
    ]);
    expect(runInputWords(t, { id: 'x', label: 'Ending', type: 'toggle', value: false, valueText: 'no', offLabel: 'Cut at the end' }).value)
      .toBe('Cut at the end');
  });

  it('summarises the first two fields for the history row', () => {
    expect(runInputsSummary(apps(), FIELDS)).toBe('Lite mode (cheaper) · “Welcome to Caffè Lume!”');
    expect(runInputsSummary(apps(), [FIELDS[0]!, FIELDS[0]!].map((f) => ({ ...f, value: false })))).toBe('Lite mode (cheaper): Off · Lite mode (cheaper): Off');
    expect(runInputsSummary(apps(), null)).toBeNull();
    expect(runInputsSummary(apps(), [])).toBeNull();
  });

  it('lists every field with its label on the run detail', () => {
    render(<MantineProvider><RunAppInputsList inputs={FIELDS} /></MantineProvider>);
    const list = screen.getByTestId('run-app-inputs');
    expect(list.querySelectorAll('dt')).toHaveLength(FIELDS.length);
    expect(list.querySelector('dt')?.textContent).toBe('Lite mode (cheaper)');
    expect(list.textContent).toContain('Fields of this run');
    expect(list.textContent).not.toMatch(/[0-9a-f]{8}-[0-9a-f]{4}-/);
  });
});
