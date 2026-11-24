// @vitest-environment jsdom
/**
 * The metadata panel.
 *
 * What this file exists to pin:
 *
 *   1. **All eight rows are always rendered**, and each is either a REAL value read from the probe
 *      or an explicit unknown. Nothing disappears, nothing shows a fabricated zero, and no
 *      stand-in ("unknown codec") is ever presented as data.
 *   2. **The three states are distinguished**: no probe at all ("Not analysed"), a probe whose
 *      appended fields are all null ("Not probed" — the pre-fix artifact shape), and a probe that
 *      carries values (the real ones, rendered from the fields the landed DTO appended).
 *   3. **The container is not over-simplified.** ffprobe's `format_name` for a sniffed MP4 is the
 *      comma-separated probe list `mov,mp4,m4a,3gp,3g2,mj2`; the row must stay readable AND keep
 *      the raw list visible rather than claiming one clean container name.
 *   4. **Nothing bad reaches the DOM** — no `NaN`, no `undefined`, no `null`, no empty value — over
 *      the all-null probe AND the all-populated one, on the DOM and on the pure `metadataRows`.
 *
 * Note: this repo does not install `@testing-library/jest-dom`, so assertions go through
 * `textContent` rather than `toHaveTextContent`.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

import { MetadataPanel, metadataRows } from '../MetadataPanel';
import {
  AUDIO_FILE_ITEM,
  IMAGE_FILE_ITEM,
  STEP_RESULT_ITEM,
  VIDEO_FILE_ITEM,
  preFixProbe,
} from '@/lib/editing/media/fixtures';
import { NOT_ANALYSED, NOT_PROBED } from '@/lib/editing/media/format';
import type { MediaProbe } from '@/lib/editing/media/types';

/** The populated probe fixture, narrowed once so a test can patch exactly one field of it. */
const VIDEO_PROBE = VIDEO_FILE_ITEM.probe as MediaProbe;
const probeWith = (patch: Partial<MediaProbe>): MediaProbe => ({ ...VIDEO_PROBE, ...patch });

vi.stubGlobal(
  'ResizeObserver',
  class ResizeObserver {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);

function installMatchMedia() {
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
}

beforeEach(() => {
  installMatchMedia();
});

afterEach(() => {
  cleanup();
});

const renderPanel = (item: Parameters<typeof MetadataPanel>[0]['item']) =>
  render(
    <MantineProvider>
      <MetadataPanel item={item} />
    </MantineProvider>,
  );

const ROW_KEYS = [
  'codec',
  'container',
  'frameRate',
  'resolution',
  'duration',
  'channels',
  'sampleRate',
  'bitrate',
] as const;

const valueOf = (key: string): string =>
  screen.getByTestId(`metadata-value-${key}`).textContent ?? '';

const sourceOf = (key: string): string | null =>
  screen.getByTestId(`metadata-value-${key}`).getAttribute('data-source');

const text = (): string => document.body.textContent ?? '';

/** An audio source whose probe was never analysed at all. */
const NEVER_ANALYSED_ITEM = IMAGE_FILE_ITEM;

/** A video whose probe exists but carries no value for any of the six appended fields. */
const PRE_FIX_ARTIFACT = {
  ...VIDEO_FILE_ITEM,
  probe: preFixProbe({
    durationSec: 12.5,
    fpsNum: 30000,
    fpsDen: 1001,
    width: 1920,
    height: 1080,
  }),
};

// ─────────────────────────────────────────────────────────────────────────────
// The populated probe — every row is now a real value
// ─────────────────────────────────────────────────────────────────────────────

describe('MetadataPanel — a fully populated probe', () => {
  it('renders every row the acceptance criterion names', () => {
    renderPanel(VIDEO_FILE_ITEM);

    for (const key of ROW_KEYS) {
      expect(screen.getByTestId(`metadata-row-${key}`)).toBeTruthy();
    }
    expect(screen.queryByTestId('metadata-not-analysed')).toBeNull();
    expect(screen.queryByTestId('metadata-pre-probe-artifact')).toBeNull();
  });

  it('shows the four rows that previously had no field behind them, from the probe', () => {
    renderPanel(VIDEO_FILE_ITEM);

    // Video and audio codecs, both read from the probe's own fields.
    expect(valueOf('codec')).toBe('h264 video · aac audio');
    expect(valueOf('channels')).toBe('2 channels');
    expect(valueOf('sampleRate')).toBe('48 kHz');
    expect(valueOf('bitrate')).toBe('30791683 bps (30.79 Mbps)');

    for (const key of ['codec', 'channels', 'sampleRate', 'bitrate'] as const) {
      expect(sourceOf(key)).toBe('probe');
    }
    // The old placeholder must be gone from the rendered panel entirely.
    expect(text()).not.toContain('Not carried by the media API');
  });

  it('renders ffprobe format_name readably WITHOUT pretending it is one container', () => {
    renderPanel(VIDEO_FILE_ITEM);

    const container = valueOf('container');
    // Readable family first...
    expect(container.startsWith('MP4 (')).toBe(true);
    // ...and the raw probe list, verbatim, still visible.
    expect(container).toContain('mov, mp4, m4a, 3gp, 3g2, mj2');
    expect(container).not.toBe('mp4');
    expect(sourceOf('container')).toBe('probe');
  });

  it('shows the frame rate as a reduced rational AND its decimal, from the probe', () => {
    renderPanel(VIDEO_FILE_ITEM);

    expect(valueOf('frameRate')).toBe('30000/1001 (29.97 fps)');
    expect(valueOf('resolution')).toBe('1920 × 1080');
    expect(valueOf('duration')).toBe('12.5 s');
  });

  it('renders a single-token container as its readable label', () => {
    renderPanel({
      ...VIDEO_FILE_ITEM,
      mimeType: 'audio/mpeg',
      probe: probeWith({ container: 'mp3' }),
    });

    expect(valueOf('container')).toBe('MP3');
  });

  it('keeps an unrecognised multi-token probe list readable without claiming a family', () => {
    renderPanel({
      ...VIDEO_FILE_ITEM,
      probe: probeWith({ container: 'foo,bar' }),
    });

    expect(valueOf('container')).toBe('foo, bar (probe list)');
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// The two levels of absence
// ─────────────────────────────────────────────────────────────────────────────

describe('MetadataPanel — the two absences are distinguished', () => {
  it('says "Not analysed" when there is no probe at all, and never "Not probed"', () => {
    renderPanel(NEVER_ANALYSED_ITEM);

    expect(screen.getByTestId('metadata-not-analysed').textContent).toContain(NOT_ANALYSED);
    // R05.8: a row with no value is not shown, and the gap note names what is missing.
    for (const key of ['frameRate', 'resolution', 'duration', 'codec'] as const) {
      expect(screen.queryByTestId(`metadata-row-${key}`)).toBeNull();
    }
    expect(screen.getByTestId('metadata-gap-note').textContent).toContain('Frame rate');
  });

  it('says "Not probed" — never a zero — for a probe whose appended fields are absent', () => {
    renderPanel(PRE_FIX_ARTIFACT);

    expect(screen.queryByTestId('metadata-not-analysed')).toBeNull();
    expect(screen.getByTestId('metadata-pre-probe-artifact')).toBeTruthy();

    // The five original fields are still real values...
    expect(valueOf('frameRate')).toBe('30000/1001 (29.97 fps)');
    expect(valueOf('duration')).toBe('12.5 s');
    // ...and the six appended ones are stated as unknown, not as 0 channels / 0 kbps / a codec.
    for (const key of ['codec', 'channels', 'sampleRate', 'bitrate'] as const) {
      expect(screen.queryByTestId(`metadata-row-${key}`)).toBeNull();
    }

    expect(text()).not.toContain('0 channels');
    expect(text()).not.toContain('0 bps');
    expect(text()).not.toContain('0 kHz');
    expect(text()).not.toContain('unknown codec');
  });

  it('falls back to the declared MIME type for the container, and says that is what it is', () => {
    renderPanel(PRE_FIX_ARTIFACT);

    expect(valueOf('container')).toBe('mp4 (declared MIME type)');
    expect(sourceOf('container')).toBe('mimeType');
  });

  it('states "Not probed" rather than an empty cell when the container is whitespace', () => {
    renderPanel({ ...VIDEO_FILE_ITEM, probe: probeWith({ container: '   ' }) });

    // A blank probe field is unknown, so the declared MIME type is used and labelled as such.
    expect(valueOf('container')).toBe('mp4 (declared MIME type)');
  });

  it('renders a stated empty state for no item at all', () => {
    renderPanel(null);
    expect(screen.getByTestId('metadata-panel-empty').textContent).toContain('Select a clip');
  });

  it('treats a pre-fix AUDIO fixture as fully unknown on the appended fields', () => {
    // AUDIO_FILE_ITEM's probe exists with every field null, including the six appended ones.
    renderPanel(AUDIO_FILE_ITEM);

    expect(screen.getByTestId('metadata-pre-probe-artifact')).toBeTruthy();
    expect(screen.queryByTestId('metadata-row-codec')).toBeNull();
    expect(valueOf('container')).toBe('wav (declared MIME type)');
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Nothing bad reaches the DOM
// ─────────────────────────────────────────────────────────────────────────────

describe('MetadataPanel — the DOM is never poisoned', () => {
  it('renders no NaN, undefined, null or empty value for any fixture', () => {
    for (const item of [
      VIDEO_FILE_ITEM,
      AUDIO_FILE_ITEM,
      IMAGE_FILE_ITEM,
      STEP_RESULT_ITEM,
      PRE_FIX_ARTIFACT,
    ]) {
      cleanup();
      renderPanel(item);

      for (const key of ROW_KEYS) {
        if (!screen.queryByTestId(`metadata-value-${key}`)) continue; // hidden: no value (R05.8)
        const value = valueOf(key);
        expect(value.trim().length).toBeGreaterThan(0);
        expect(value).not.toMatch(/NaN|undefined/);
        expect(value).not.toBe('null');
      }

      const rendered = text();
      expect(rendered).not.toContain('NaN');
      expect(rendered).not.toContain('undefined');
      expect(rendered).not.toContain('null');
      expect(rendered).not.toContain('[object Object]');
    }
  });

  it('produces the same guarantees from the pure row builder', () => {
    const inputs = [
      VIDEO_FILE_ITEM,
      AUDIO_FILE_ITEM,
      IMAGE_FILE_ITEM,
      STEP_RESULT_ITEM,
      PRE_FIX_ARTIFACT,
      null,
    ];

    for (const item of inputs) {
      const rows = metadataRows(item);
      expect(rows.map((row) => row.key)).toEqual([...ROW_KEYS]);

      for (const row of rows) {
        expect(typeof row.value).toBe('string');
        expect(row.value.trim().length).toBeGreaterThan(0);
        expect(row.value).not.toMatch(/NaN|undefined/);
        expect(row.label.trim().length).toBeGreaterThan(0);
      }
    }
  });

  it('never renders a fabricated zero for a probe field that is null', () => {
    // The whole point of the nullable appended fields: null means "not probed", and a 0 would state
    // a measurement nobody took. This asserts it on the VALUES, not only on the rendered text.
    const rows = metadataRows(PRE_FIX_ARTIFACT);
    const numericRows = rows.filter((row) =>
      ['channels', 'sampleRate', 'bitrate'].includes(row.key),
    );

    expect(numericRows).toHaveLength(3);
    for (const row of numericRows) {
      expect(row.value).toBe(NOT_PROBED);
      expect(row.value).not.toMatch(/\b0\b/);
    }
  });
});
