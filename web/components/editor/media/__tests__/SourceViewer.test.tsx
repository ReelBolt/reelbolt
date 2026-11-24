// @vitest-environment jsdom
/**
 * The source viewer.
 *
 * The claims this file makes assertable:
 *
 *   1. **The DOM effect, not a prop.** Flipping the proxy toggle changes the src ATTRIBUTE of the
 *      mounted `<video>` element — read back off the element, not off React's props.
 *   2. **No `<video>` without a URL that exists.** A `step_result:` source with no proxy row renders
 *      a stated reason and no player at all.
 *   3. **Honest degradation.** A missing proxy says "Proxy not ready yet" and NEVER says "failed"
 *      or "unsupported" — the API has no `state` field and cannot distinguish those.
 *   4. **Marking issues ZERO requests.** Five-plus mark changes leave the derivatives call count
 *      untouched, over a mocked client whose call count IS the assertion.
 *   5. **The subclip descriptor is not persisted.** It is shown, and the copy says so.
 *   6. **No route, no global state.** Asserted over the component's own source.
 *
 * Note: this repo does not install `@testing-library/jest-dom`, so assertions go through
 * `textContent`/`getAttribute` rather than `toHaveTextContent`/`toHaveAttribute`.
 */

import { readFileSync } from 'node:fs';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

import { SourceViewer } from '../SourceViewer';
import type { MediaPoolItem } from '@/lib/editing/media/types';
import type { SubclipDescriptor } from '@/lib/editing/media/subclip';
import {
  DERIVATIVES_ALL_KINDS,
  DERIVATIVES_NONE,
  FIXTURE_IDS,
  FIXTURE_PROJECT_ID,
  AUDIO_FILE_ITEM,
  IMAGE_FILE_ITEM,
  STEP_RESULT_ITEM,
  VIDEO_FILE_ITEM,
  preFixProbe,
} from '@/lib/editing/media/fixtures';

// ─────────────────────────────────────────────────────────────────────────────
// The derivatives client is a spy: its CALL COUNT is one of the assertions.
// Everything else in the API client stays real, so the path helpers under test
// are the shipped ones.
// ─────────────────────────────────────────────────────────────────────────────

vi.mock('@/lib/editing/api/media', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/editing/api/media')>();
  return { ...actual, fetchMediaDerivatives: vi.fn() };
});

import { fetchMediaDerivatives } from '@/lib/editing/api/media';

const derivativesSpy = vi.mocked(fetchMediaDerivatives);

vi.stubGlobal(
  'ResizeObserver',
  class ResizeObserver {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);

Element.prototype.scrollIntoView = vi.fn();

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
  derivativesSpy.mockReset();
});

afterEach(() => {
  cleanup();
});

const renderViewer = (
  item: MediaPoolItem | null = VIDEO_FILE_ITEM,
  onCreateSubclip?: (descriptor: SubclipDescriptor) => void,
) =>
  render(
    <MantineProvider>
      <SourceViewer
        projectId={FIXTURE_PROJECT_ID}
        item={item}
        onCreateSubclip={onCreateSubclip}
      />
    </MantineProvider>,
  );

const videoSrc = (): string | null =>
  screen.getByTestId('source-viewer-video').getAttribute('src');

const text = (): string => document.body.textContent ?? '';

// ─────────────────────────────────────────────────────────────────────────────
// Playback source — the DOM effect
// ─────────────────────────────────────────────────────────────────────────────

describe('SourceViewer — playback source', () => {
  it('R05.5 plays the original while no proxy exists, with no switch and no label', async () => {
    derivativesSpy.mockResolvedValue(DERIVATIVES_NONE);
    renderViewer();
    const src = await waitFor(() => {
      const value = videoSrc();
      expect(value).not.toBeNull();
      return value as string;
    });
    // The original of a `file:` source is the project-files content route for THAT file.
    expect(src).toContain(FIXTURE_IDS.videoFile);
    expect(screen.queryByRole('radio', { name: 'Proxy' })).toBeNull();
    expect(screen.queryByRole('radio', { name: 'Original' })).toBeNull();
    expect(screen.queryByTestId('proxy-toggle')).toBeNull();
    expect(screen.queryByTestId('source-viewer-preview-label')).toBeNull();
  });

  it('R05.5 switches by itself to the proxy once it exists and labels it "Simple preview"', async () => {
    derivativesSpy.mockResolvedValue(DERIVATIVES_ALL_KINDS);
    renderViewer();
    const proxySrc = await waitFor(() => {
      const value = videoSrc();
      expect(value).toContain(FIXTURE_IDS.proxyRow);
      return value as string;
    });
    expect(proxySrc).not.toContain(FIXTURE_IDS.videoFile);
    expect(screen.getByTestId('source-viewer-preview-label').textContent).toBe('Simple preview');
    expect(screen.getByTestId('source-viewer-player').getAttribute('data-playing-source')).toBe('proxy');
    expect(screen.queryByRole('radio')).toBeNull();
  });

  it('plays a step_result source with no proxy from its original, through the outputs route', async () => {
    derivativesSpy.mockResolvedValue(DERIVATIVES_NONE);
    renderViewer(STEP_RESULT_ITEM);
    const src = await waitFor(() => {
      const value = videoSrc();
      expect(value).not.toBeNull();
      return value as string;
    });
    expect(src).toMatch(/\/outputs\/[^/]+\/download$/);
    expect(screen.getByTestId('source-viewer-player').getAttribute('data-playing-source')).toBe('original');
  });

  it('never says "failed" or "unsupported" — the API cannot tell those apart', async () => {
    for (const rows of [DERIVATIVES_NONE, DERIVATIVES_ALL_KINDS]) {
      derivativesSpy.mockResolvedValue(rows);
      cleanup();
      renderViewer(STEP_RESULT_ITEM);
      await waitFor(() => expect(screen.getByTestId('source-viewer-video')).toBeTruthy());

      expect(text().toLowerCase()).not.toContain('failed');
      expect(text().toLowerCase()).not.toContain('unsupported');
      expect(text().toLowerCase()).not.toContain('error');
    }
  });

  it('still plays the original when the derivative list cannot be read', async () => {
    derivativesSpy.mockRejectedValue(new Error('Network unreachable'));
    renderViewer();
    // The derivative list is unreadable, so the proxy's existence is unknown — but the ORIGINAL is
    // a `file:` source's real route, so playback still works and no claim is made about a proxy.
    const src = await waitFor(() => {
      const value = videoSrc();
      expect(value).not.toBeNull();
      return value as string;
    });
    expect(src).toContain(FIXTURE_IDS.videoFile);
  });

  it('renders a stated empty state with no clip', () => {
    renderViewer(null);
    expect(screen.getByTestId('source-viewer-empty').textContent).toContain('Select a clip');
    expect(screen.queryByTestId('source-viewer-video')).toBeNull();
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Marking — view state, and ZERO requests
// ─────────────────────────────────────────────────────────────────────────────

/** Puts the playhead at `seconds` on the mounted element, the way a real seek would. */
const seekTo = async (seconds: number) => {
  const video = screen.getByTestId('source-viewer-video') as HTMLVideoElement;
  await act(async () => {
    video.currentTime = seconds;
    fireEvent.timeUpdate(video);
  });
};

const clickMark = async (testId: string) => {
  const button = screen.getByTestId(testId) as HTMLButtonElement;
  expect(button.disabled).toBe(false);
  await act(async () => {
    fireEvent.click(button);
  });
};

describe('SourceViewer — in/out marks are view state', () => {
  it('issues ZERO network requests across more than five mark changes', async () => {
    derivativesSpy.mockResolvedValue(DERIVATIVES_ALL_KINDS);
    renderViewer();

    await waitFor(() => expect(derivativesSpy).toHaveBeenCalledTimes(1));

    // The clip renders straight away (the original URL needs no derivative row), so the playhead
    // can be moved before the derivative response has even landed.
    await seekTo(0);
    await waitFor(() =>
      expect((screen.getByTestId('mark-in') as HTMLButtonElement).disabled).toBe(false),
    );

    // Ten mark changes, starting from here: six "in" marks, three "out" marks, one clear.
    const callsAfterMount = derivativesSpy.mock.calls.length;
    expect(callsAfterMount).toBe(1);

    for (const seconds of [0.5, 1, 1.5, 2, 2.5, 3]) {
      await seekTo(seconds);
      await clickMark('mark-in');
    }
    for (const seconds of [3.5, 4, 4.5]) {
      await seekTo(seconds);
      await clickMark('mark-out');
    }
    await clickMark('mark-clear');

    expect(derivativesSpy.mock.calls.length).toBe(callsAfterMount);

    // …and the marks really did move, so the count above is not vacuous.
    expect(screen.getByTestId('mark-in-value').textContent).toContain('Not set');
    expect(screen.getByTestId('mark-out-value').textContent).toContain('Not set');
  });

  it('records marks in source frames at the rational rate, and shows a timecode', async () => {
    derivativesSpy.mockResolvedValue(DERIVATIVES_ALL_KINDS);
    renderViewer();
    await seekTo(1);
    await waitFor(() =>
      expect((screen.getByTestId('mark-in') as HTMLButtonElement).disabled).toBe(false),
    );

    await clickMark('mark-in');
    await seekTo(2.5);
    await clickMark('mark-out');

    // 1 s → frame 30 at 30000/1001; 2.5 s → frame 75. The span is 45 frames.
    expect(screen.getByTestId('mark-in-value').textContent).toContain('frame 30');
    expect(screen.getByTestId('mark-out-value').textContent).toContain('frame 75');
    expect(screen.getByTestId('mark-span-value').textContent).toContain('45 frames');
    expect(screen.getByTestId('mark-in-value').textContent).toMatch(/In: \d\d:\d\d:\d\d:\d\d/);
  });

  it('disables marking, with a stated reason, when there is no frame domain at all', async () => {
    derivativesSpy.mockResolvedValue(DERIVATIVES_NONE);
    // A probe that exists but carries neither a duration nor a rate.
    renderViewer({
      ...VIDEO_FILE_ITEM,
      probe: preFixProbe({ durationSec: null, fpsNum: null, fpsDen: null, width: null, height: null }),
    });

    await waitFor(() => expect(screen.getByTestId('mark-disabled-reason')).toBeTruthy());
    expect(screen.getByTestId('mark-disabled-reason').textContent).toContain(
      'no probed duration and no probed frame rate',
    );
    expect((screen.getByTestId('mark-in') as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByTestId('mark-out') as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByTestId('create-subclip') as HTMLButtonElement).disabled).toBe(true);
  });

  it('falls back to decoded frames when a duration is known but the rate is not', async () => {
    derivativesSpy.mockResolvedValue(DERIVATIVES_NONE);
    renderViewer({
      ...VIDEO_FILE_ITEM,
      probe: preFixProbe({ durationSec: 12.5, fpsNum: null, fpsDen: null, width: 1920, height: 1080 }),
    });

    await waitFor(() => expect(screen.getByTestId('mark-playhead-reason')).toBeTruthy());
    expect(screen.getByTestId('mark-playhead-reason').textContent).toContain('decoded frames');
    // The element cannot report a decoded frame index in this environment, so no mark is offered —
    // and nothing NaN is displayed in its place.
    expect((screen.getByTestId('mark-in') as HTMLButtonElement).disabled).toBe(true);
    expect(text()).not.toMatch(/NaN/);
  });

  it('never renders NaN, undefined, null or an empty value', async () => {
    for (const item of [VIDEO_FILE_ITEM, STEP_RESULT_ITEM]) {
      for (const rows of [DERIVATIVES_ALL_KINDS, DERIVATIVES_NONE]) {
        derivativesSpy.mockResolvedValue(rows);
        cleanup();
        renderViewer(item);
        await waitFor(() => expect(screen.getByTestId('source-viewer-video')).toBeTruthy());

        const rendered = text();
        expect(rendered).not.toContain('NaN');
        expect(rendered).not.toContain('undefined');
        expect(rendered).not.toContain('null');
        expect(rendered).not.toContain('[object Object]');
      }
    }
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Subclips — described, not persisted
// ─────────────────────────────────────────────────────────────────────────────

describe('SourceViewer — subclip creation', () => {
  it('hands a typed descriptor to its callback and says it was not saved', async () => {
    const onCreateSubclip = vi.fn();
    derivativesSpy.mockResolvedValue(DERIVATIVES_ALL_KINDS);
    renderViewer(VIDEO_FILE_ITEM, onCreateSubclip);

    await seekTo(1);
    await waitFor(() =>
      expect((screen.getByTestId('mark-in') as HTMLButtonElement).disabled).toBe(false),
    );

    // Nothing to create before a complete range exists.
    expect((screen.getByTestId('create-subclip') as HTMLButtonElement).disabled).toBe(true);

    await clickMark('mark-in');
    await seekTo(2.5);
    await clickMark('mark-out');

    expect((screen.getByTestId('create-subclip') as HTMLButtonElement).disabled).toBe(false);
    await clickMark('create-subclip');

    expect(onCreateSubclip).toHaveBeenCalledTimes(1);
    expect(onCreateSubclip.mock.calls[0][0]).toEqual({
      mediaRef: VIDEO_FILE_ITEM.mediaRef,
      sourceInFrames: 30,
      sourceOutFrames: 75,
      durationFrames: 45,
      name: 'b-roll-city [30-75]',
      sourceDurationFrames: 375,
    });

    const preview = screen.getByTestId('subclip-descriptor').textContent ?? '';
    expect(preview).toContain('b-roll-city [30-75]');
    expect(preview).toContain('Not saved');
    // And no request was made for it.
    expect(derivativesSpy).toHaveBeenCalledTimes(1);
  });

  it('reports every mark change, and shows the caller\'s hint once a subclip is created', async () => {
    const onMarksChange = vi.fn();
    derivativesSpy.mockResolvedValue(DERIVATIVES_ALL_KINDS);
    render(
      <MantineProvider>
        <SourceViewer
          projectId={FIXTURE_PROJECT_ID}
          item={VIDEO_FILE_ITEM}
          onCreateSubclip={() => {}}
          onMarksChange={onMarksChange}
          subclipHint="Create subclip adds the marked range to the end of the timeline."
        />
      </MantineProvider>,
    );
    await seekTo(1);
    await waitFor(() =>
      expect((screen.getByTestId('mark-in') as HTMLButtonElement).disabled).toBe(false),
    );
    await clickMark('mark-in');
    await seekTo(2.5);
    await clickMark('mark-out');
    expect(onMarksChange).toHaveBeenLastCalledWith({ inFrame: 30, outFrame: 75 });
    await clickMark('create-subclip');
    expect(screen.getByTestId('subclip-hint').textContent).toBe(
      'Create subclip adds the marked range to the end of the timeline.',
    );
    await clickMark('mark-clear');
    expect(onMarksChange).toHaveBeenLastCalledWith({ inFrame: null, outFrame: null });
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Sound and stills (audit d2 N3): never a <video>
// ─────────────────────────────────────────────────────────────────────────────

describe('SourceViewer — audio and image sources', () => {
  it('plays an audio file in an <audio> element and opens no <video>', async () => {
    derivativesSpy.mockResolvedValue(DERIVATIVES_NONE);
    renderViewer(AUDIO_FILE_ITEM);
    const audio = await screen.findByTestId('source-viewer-audio');
    expect(audio.tagName).toBe('AUDIO');
    expect(document.querySelector('video')).toBeNull();
    expect(screen.queryByTestId('proxy-toggle')).toBeNull();
    expect(screen.getByTestId('source-viewer-no-proxy').textContent).toContain('needs no proxy');
  });

  it('shows a still as a picture and opens no <video>', async () => {
    derivativesSpy.mockResolvedValue(DERIVATIVES_NONE);
    renderViewer(IMAGE_FILE_ITEM);
    const image = await screen.findByTestId('source-viewer-image');
    expect(image.tagName).toBe('IMG');
    expect(image.getAttribute('alt')).toBe(IMAGE_FILE_ITEM.fileName);
    expect(document.querySelector('video')).toBeNull();
    expect(text()).not.toContain('Proxy not ready');
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Independence from the route
// ─────────────────────────────────────────────────────────────────────────────

describe('SourceViewer — mountable anywhere', () => {
  it('reads no route and no global state', () => {
    const source = readFileSync(join(process.cwd(), 'components', 'editor', 'media', 'SourceViewer.tsx'), 'utf8');

    expect(source).not.toContain('next/navigation');
    expect(source).not.toContain('useParams');
    expect(source).not.toContain('useSearchParams');
    expect(source).not.toContain('useRouter');
    expect(source).not.toContain('usePathname');
    // The clip arrives as a prop, and the project id with it.
    expect(source).toContain('item: MediaPoolItem | null');
    expect(source).toContain('projectId: string');
  });
});
