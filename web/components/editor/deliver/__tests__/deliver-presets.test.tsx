// @vitest-environment jsdom
/**
 * Wave 5: presets are the spine of the settings column. The list sends the preset by NAME, says what the plan does
 * with each one before any click, keeps a person's own presets on the device, and the Render button names what it
 * makes and says why when it is off.
 */

import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { RenderEditTimelineRequest } from '@/lib/editing/sync/api';
import { LiveChordsProvider } from '../../prefs/live-chords';
import { RenderSettings, type RenderSettingsProps } from '../RenderSettings';
import { DEFAULT_FORM_SETTINGS, MY_PRESETS_KEY, type RenderFormSettings } from '../render-presets';

vi.mock('@/lib/editing/use-device-class', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/use-device-class')>()),
  useDeviceClass: () => 'desktop',
}));
vi.mock('../../presets/PresetPicker', () => ({ PresetPicker: () => null }));
vi.mock('next/link', () => ({ default: ({ children, href, ...rest }: { children: React.ReactNode; href: string }) => <a href={href} {...rest}>{children}</a> }));

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;

beforeEach(() => window.localStorage.clear());
afterEach(() => cleanup());

const base: RenderSettingsProps = { inOut: { inFrame: null, outFrame: null }, revision: 4, readOnly: false, busy: false, onRender: () => {} };

function mount(props: Partial<RenderSettingsProps> = {}) {
  const onRender = vi.fn<(request: RenderEditTimelineRequest, settings: RenderFormSettings) => void>();
  const utils = render(
    <MantineProvider>
      <RenderSettings {...base} onRender={onRender} {...props} />
    </MantineProvider>,
  );
  return { onRender, ...utils };
}

const row = (preset: string) => screen.getByTestId(`render-preset-${preset}`);
const start = () => screen.getByTestId('render-start') as HTMLButtonElement;

describe('the preset list', () => {
  it('lists every render preset with what it makes, one of them chosen', () => {
    mount({ timelineSize: { width: 1920, height: 1080 } });
    const list = screen.getByRole('listbox', { name: 'What to render' });
    expect(within(list).getAllByRole('option').map((o) => o.getAttribute('data-testid'))).toEqual([
      'render-preset-Hd1080p16x9', 'render-preset-Uhd4k16x9', 'render-preset-Vertical9x16', 'render-preset-Square1x1', 'render-preset-Portrait4x5', 'render-preset-Master', 'render-preset-AudioOnly',
    ]);
    expect(row('Hd1080p16x9').getAttribute('aria-selected')).toBe('true');
    expect(row('Hd1080p16x9').textContent).toContain('1920 × 1080 · 16:9');
    expect(row('Vertical9x16').textContent).toContain('1080 × 1920 · 9:16');
    expect(row('Master').textContent).toContain('1920 × 1080');
    expect(row('AudioOnly').textContent).toContain('No picture');
    // Every row says what it is for.
    for (const option of within(list).getAllByRole('option')) expect(option.textContent?.length).toBeGreaterThan(30);
  });

  it('sends the chosen preset by its name in the render request', () => {
    const { onRender } = mount();
    for (const preset of ['Vertical9x16', 'Square1x1', 'Portrait4x5', 'Master', 'AudioOnly', 'Uhd4k16x9', 'Hd1080p16x9'] as const) {
      fireEvent.click(row(preset));
      expect(row(preset).getAttribute('aria-selected')).toBe('true');
      fireEvent.click(start());
      const [request, settings] = onRender.mock.calls[onRender.mock.calls.length - 1];
      expect(request.preset).toBe(preset);
      expect(typeof request.preset).toBe('string');
      expect(settings.preset).toBe(preset);
    }
  });

  it('moves the choice with the arrow keys, Home and End, skipping what the plan refuses', () => {
    mount({ maxOutputHeight: 1080 });
    fireEvent.keyDown(row('Hd1080p16x9'), { key: 'ArrowDown' });
    // 4K is refused on this plan, so the next choice is the vertical film.
    expect(row('Vertical9x16').getAttribute('aria-selected')).toBe('true');
    fireEvent.keyDown(row('Vertical9x16'), { key: 'End' });
    expect(row('AudioOnly').getAttribute('aria-selected')).toBe('true');
    fireEvent.keyDown(row('AudioOnly'), { key: 'Home' });
    expect(row('Hd1080p16x9').getAttribute('aria-selected')).toBe('true');
    expect(row('Hd1080p16x9').getAttribute('tabindex')).toBe('0');
    expect(row('Square1x1').getAttribute('tabindex')).toBe('-1');
  });

  it('names the Render button after what it makes', () => {
    mount();
    expect(start().textContent).toBe('Render 1080p film');
    fireEvent.click(row('Vertical9x16'));
    expect(start().textContent).toBe('Render vertical film');
    fireEvent.click(row('AudioOnly'));
    expect(start().textContent).toBe('Render audio file');
  });
});

describe('the plan cap is said before the click', () => {
  it('marks 4K as not in the plan, with the reason and a way to the plans, and does not let it be chosen', () => {
    const { onRender } = mount({ maxOutputHeight: 1080 });
    const fourK = row('Uhd4k16x9');
    expect(fourK.getAttribute('aria-disabled')).toBe('true');
    expect(fourK.getAttribute('data-availability')).toBe('refused');
    expect(screen.getByTestId('render-preset-Uhd4k16x9-refused').textContent).toBe('Not in your plan');
    expect(screen.getByTestId('render-preset-cap').textContent).toContain('Your plan renders up to 1080p. Upgrade to render in 4K.');
    fireEvent.click(fourK);
    expect(fourK.getAttribute('aria-selected')).toBe('false');
    fireEvent.click(start());
    expect(onRender.mock.calls[0][0].preset).toBe('Hd1080p16x9');
  });

  it('says a scaled-down preset comes out at the plan size, on the row, in the statement and on the button', () => {
    mount({ maxOutputHeight: 720 });
    expect(screen.getByTestId('render-preset-Hd1080p16x9-clamped').textContent).toBe('Comes out at 720p on your plan');
    expect(row('Hd1080p16x9').getAttribute('data-availability')).toBe('clamped');
    expect(screen.getByTestId('render-summary-format').textContent).toBe('Full HD landscape · 1920 × 1080, exported at 720p on your plan');
    expect(start().textContent).toBe('Render 720p film');
    expect(start().disabled).toBe(false);
  });

  it('turns Render off with the reason when a refused format is on the form', () => {
    mount({ maxOutputHeight: 1080, prefill: { token: 1, settings: { preset: 'Uhd4k16x9', ...DEFAULT_FORM_SETTINGS }, useRange: false, fileName: '' } });
    expect(row('Uhd4k16x9').getAttribute('aria-selected')).toBe('true');
    expect(start().disabled).toBe(true);
    const reason = screen.getByTestId('render-disabled-reason');
    expect(reason.getAttribute('data-reason')).toBe('plan');
    expect(reason.textContent).toContain('Your plan does not render this size.');
    expect(within(reason).getByRole('link', { name: 'See plans' }).getAttribute('href')).toBe('/settings/billing#plans');
  });
});

describe('why Render is off', () => {
  it('a read-only timeline', () => {
    mount({ readOnly: true });
    expect(start().disabled).toBe(true);
    expect(screen.getByTestId('render-disabled-reason').getAttribute('data-reason')).toBe('readOnly');
  });

  it('nothing on the timeline, with the way out and its live chord', () => {
    const onOpenEdit = vi.fn();
    render(
      <MantineProvider>
        <LiveChordsProvider chords={{ pageEdit: ['Shift+3'], markIn: ['I'], markOut: ['O'] }}>
          <RenderSettings {...base} timelineEmpty onOpenEdit={onOpenEdit} />
        </LiveChordsProvider>
      </MantineProvider>,
    );
    expect(start().disabled).toBe(true);
    expect(screen.getByTestId('render-disabled-reason').textContent).toContain('There is nothing on the timeline yet.');
    const go = screen.getByTestId('render-open-edit');
    expect(go.textContent).toBe('Go to Edit (Shift+3)');
    expect(go.getAttribute('aria-keyshortcuts')).toBe('Shift+3');
    fireEvent.click(go);
    expect(onOpenEdit).toHaveBeenCalledTimes(1);
    // The range switch names the chords a person actually has for the marks.
    expect(screen.getByText(/I for in, O for out/)).toBeTruthy();
  });

  it('a file name that cannot be used, and a refused estimate', () => {
    const { unmount } = mount();
    fireEvent.change(screen.getByTestId('render-file-name'), { target: { value: '../escape.mp4' } });
    expect(start().disabled).toBe(true);
    expect(screen.getByTestId('render-disabled-reason').getAttribute('data-reason')).toBe('fileName');
    unmount();
    mount({ cost: { kind: 'refused', message: 'You need 4 more credits to render this.', link: 'credits' } });
    expect(start().disabled).toBe(true);
    expect(screen.getByTestId('render-disabled-reason').getAttribute('data-reason')).toBe('cost');
  });

  it('not enough credits keeps the existing sentence and the way to add credits', () => {
    mount({ needMoreLine: 'You need 10 more credits' });
    expect(start().disabled).toBe(true);
    expect(screen.getByTestId('render-need-credits').textContent).toContain('You need 10 more credits');
    expect(screen.getByTestId('render-add-credits').getAttribute('href')).toBe('/settings/billing');
  });

  it('is on, with no reason shown, when nothing stands in the way', () => {
    mount();
    expect(start().disabled).toBe(false);
    expect(screen.queryByTestId('render-disabled-reason')).toBeNull();
  });
});

describe('the statement says what will be rendered', () => {
  it('names the range in timecode: the whole timeline, then the marked range', () => {
    const { rerender } = mount({ timelineSize: { width: 1920, height: 1080, rate: { num: 30, den: 1 } }, programFrames: 2510 });
    expect(screen.getByTestId('render-summary-range').textContent).toBe('Whole timeline, 00:00:00:00 to 00:01:23:20');
    rerender(
      <MantineProvider>
        <RenderSettings {...base} timelineSize={{ width: 1920, height: 1080, rate: { num: 30, den: 1 } }} programFrames={2510} inOut={{ inFrame: 40, outFrame: 120 }} rangeRequest={1} />
      </MantineProvider>,
    );
    expect(screen.getByTestId('render-summary-range').textContent).toBe('Marked range, 00:00:01:10 to 00:00:04:00');
  });

  it('names the captions choice when the timeline has captions, and never for an audio file', () => {
    mount({ captionsSummary: 'Burned into the picture' });
    expect(screen.getByTestId('render-summary-captions').textContent).toBe('Burned into the picture');
    fireEvent.click(row('AudioOnly'));
    expect(screen.queryByTestId('render-summary-captions')).toBeNull();
    expect(screen.getByTestId('render-summary-format').textContent).toBe('Audio only');
  });

  it('keeps the secondary settings folded under the preset, and the preset list never folded', () => {
    mount();
    expect(screen.getByTestId('render-preset')).toBeTruthy();
    expect(screen.getByTestId('render-quality')).toBeTruthy();
    expect(screen.getByTestId('render-range')).toBeTruthy();
    expect(screen.getByTestId('render-size-toggle').getAttribute('aria-expanded')).toBe('false');
    expect(screen.getByTestId('render-advanced-toggle').getAttribute('aria-expanded')).toBe('false');
    // One header language: every block of the column is a panel with a heading.
    expect(screen.getAllByRole('heading', { level: 2 }).map((h) => h.textContent)).toEqual(['Presets', 'Options', 'Before you render']);
  });
});

describe('my presets', () => {
  it('saves the settings on screen under a name, on this device, and applies them back', () => {
    const { onRender } = mount();
    expect(screen.getByTestId('my-presets-empty')).toBeTruthy();
    fireEvent.click(row('Square1x1'));
    fireEvent.click(within(screen.getByTestId('render-quality')).getByText('Best'));
    fireEvent.click(screen.getByTestId('my-preset-save'));
    expect((screen.getByTestId('my-preset-save-confirm') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(screen.getByTestId('my-preset-name'), { target: { value: '  Client square ' } });
    fireEvent.click(screen.getByTestId('my-preset-save-confirm'));

    const stored = JSON.parse(window.localStorage.getItem(MY_PRESETS_KEY) ?? '[]') as { id: string; name: string; settings: RenderFormSettings }[];
    expect(stored).toHaveLength(1);
    expect(stored[0].name).toBe('Client square');
    expect(stored[0].settings).toMatchObject({ preset: 'Square1x1', quality: 'best' });
    expect(screen.getByTestId('my-preset-summary').textContent).toBe('In use · Square · Best');

    // Change away, then apply: the form is back on the saved settings.
    fireEvent.click(row('Hd1080p16x9'));
    expect(screen.getByTestId('my-preset-summary').textContent).toBe('Square · Best');
    fireEvent.click(screen.getByTestId('my-preset-apply'));
    expect(row('Square1x1').getAttribute('aria-selected')).toBe('true');
    fireEvent.click(start());
    const request = onRender.mock.calls[0][0];
    expect(request.preset).toBe('Square1x1');
    expect(request.encode).toMatchObject({ codec: 'H264', crf: 16, speed: 'Slow' });
  });

  it('renames and deletes, asking before a delete, and refuses a name already taken', () => {
    window.localStorage.setItem(MY_PRESETS_KEY, JSON.stringify([
      { id: 'a', name: 'Social', settings: { preset: 'Vertical9x16', ...DEFAULT_FORM_SETTINGS } },
      { id: 'b', name: 'Archive', settings: { preset: 'Master', ...DEFAULT_FORM_SETTINGS } },
    ]));
    mount();
    const social = screen.getByTestId('my-preset-a');
    fireEvent.click(within(social).getByTestId('my-preset-rename'));
    fireEvent.change(screen.getByTestId('my-preset-rename-input'), { target: { value: 'archive' } });
    expect((screen.getByTestId('my-preset-rename-save') as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText('You already have a preset with this name.')).toBeTruthy();
    fireEvent.change(screen.getByTestId('my-preset-rename-input'), { target: { value: 'Social tall' } });
    fireEvent.click(screen.getByTestId('my-preset-rename-save'));
    expect(within(screen.getByTestId('my-preset-a')).getByText('Social tall')).toBeTruthy();

    fireEvent.click(within(screen.getByTestId('my-preset-b')).getByTestId('my-preset-delete'));
    expect(screen.getByTestId('my-preset-delete-b').textContent).toContain('Delete the preset “Archive”?');
    fireEvent.click(screen.getByTestId('my-preset-delete-confirm'));
    expect(screen.queryByTestId('my-preset-b')).toBeNull();
    expect((JSON.parse(window.localStorage.getItem(MY_PRESETS_KEY) ?? '[]') as { name: string }[]).map((p) => p.name)).toEqual(['Social tall']);
  });

  it('every preset control has a name of its own', () => {
    window.localStorage.setItem(MY_PRESETS_KEY, JSON.stringify([{ id: 'a', name: 'Social', settings: { preset: 'Vertical9x16', ...DEFAULT_FORM_SETTINGS } }]));
    mount();
    expect(screen.getByRole('button', { name: 'Apply the preset Social' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Rename the preset Social' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Delete the preset Social' })).toBeTruthy();
  });

  it('says so in a sentence when the browser will not keep anything, and the page still renders', () => {
    const getItem = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied');
    });
    try {
      const { onRender } = mount();
      expect(screen.getByTestId('my-presets-unavailable').textContent).toContain('does not let the page keep presets');
      expect(screen.queryByTestId('my-preset-save')).toBeNull();
      fireEvent.click(start());
      expect(onRender).toHaveBeenCalledTimes(1);
    } finally {
      getItem.mockRestore();
    }
  });
});
