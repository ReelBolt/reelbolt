// @vitest-environment jsdom
/* eslint @typescript-eslint/no-unused-vars: ["warn", { "argsIgnorePattern": "^_" }] */
/**
 * Derivative production — the one write in the media workspace, and where it may be triggered from.
 *
 * This file exists because the trigger rule is a CONTRACT decision, not a style preference: the
 * ruling is that `POST …/media/derivatives/request` is called on explicit user action — opening or
 * selecting an asset in the detail pane — and **never on hover, never on list render**. The claims
 * asserted here are:
 *
 *   1. **Mounting the pane requests once.** Opening a clip is the sanctioned explicit action.
 *   2. **Re-rendering the same open clip requests NOTHING more.** A mark change, a state update or
 *      any other re-render must not queue a second round of encoding work.
 *   3. **Opening a DIFFERENT clip requests for that clip**, exactly once.
 *   4. **The button is a real affordance** and re-requests on demand.
 *   5. **An accepted request is never reported as a generated derivative** — a `202` means the work
 *      was published, and the copy says so.
 *   6. **A failure is reported as a failed REQUEST**, never as "derivatives unavailable", because
 *      the contract cannot distinguish those and claiming one would invent a fact.
 *
 * Note: this repo does not install `@testing-library/jest-dom`, so assertions go through
 * `textContent`/`getAttribute` helpers rather than `toHaveTextContent`.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { useState } from 'react';

import { PrepareDerivatives, REQUESTED_BY_EDITOR_OPEN } from '../PrepareDerivatives';
import {
  AUDIO_FILE_ITEM,
  DERIVATIVE_REQUEST_ACCEPTED,
  IMAGE_FILE_ITEM,
  VIDEO_FILE_ITEM,
} from '@/lib/editing/media/fixtures';
import type { MediaPoolItem } from '@/lib/editing/media/types';

// ─────────────────────────────────────────────────────────────────────────────
// The request client is a spy: its CALL COUNT is the assertion.
// ─────────────────────────────────────────────────────────────────────────────

vi.mock('@/lib/editing/api/media', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/editing/api/media')>();
  return { ...actual, requestMediaDerivatives: vi.fn() };
});

import { requestMediaDerivatives } from '@/lib/editing/api/media';

const requestSpy = vi.mocked(requestMediaDerivatives);

const PROJECT_ID = '00000000-0000-4000-8000-000000000009';

vi.stubGlobal(
  'ResizeObserver',
  class ResizeObserver {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);

Element.prototype.scrollIntoView = vi.fn();

beforeEach(() => {
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

  requestSpy.mockReset();
  requestSpy.mockResolvedValue(DERIVATIVE_REQUEST_ACCEPTED);
});

afterEach(() => cleanup());

const renderPane = (item: MediaPoolItem | null, onCheckAgain?: () => void) =>
  render(
    <MantineProvider>
      <PrepareDerivatives projectId={PROJECT_ID} item={item} onCheckAgain={onCheckAgain} />
    </MantineProvider>,
  );

const textOf = (node: Element | null): string => (node?.textContent ?? '').trim();

describe('PrepareDerivatives — the explicit-open trigger', () => {
  it('requests once when the clip is opened, naming the sanctioned reason', async () => {
    renderPane(VIDEO_FILE_ITEM);

    await waitFor(() => expect(requestSpy).toHaveBeenCalledTimes(1));
    expect(requestSpy).toHaveBeenCalledWith(PROJECT_ID, {
      mediaRef: VIDEO_FILE_ITEM.mediaRef,
      requestedBy: REQUESTED_BY_EDITOR_OPEN,
    });
    // No `kinds` is sent on the default call: the server then asks for the whole closed enum, and
    // deciding what is worth producing is the consumer's job.
    expect(requestSpy.mock.calls[0][1]).not.toHaveProperty('kinds');
  });

  it('issues NOTHING more when the same open clip re-renders', async () => {
    function Harness() {
      const [tick, setTick] = useState(0);
      return (
        <>
          <button type="button" data-testid="rerender" onClick={() => setTick(tick + 1)}>
            rerender
          </button>
          <PrepareDerivatives projectId={PROJECT_ID} item={VIDEO_FILE_ITEM} />
        </>
      );
    }

    render(
      <MantineProvider>
        <Harness />
      </MantineProvider>,
    );
    await waitFor(() => expect(requestSpy).toHaveBeenCalledTimes(1));

    for (let i = 0; i < 5; i += 1) {
      await act(async () => {
        fireEvent.click(screen.getByTestId('rerender'));
      });
    }

    // Five re-renders, still one request. This is the rule the ruling exists to enforce.
    expect(requestSpy).toHaveBeenCalledTimes(1);
  });

  it('requests for a DIFFERENT clip when the selection changes', async () => {
    const items: MediaPoolItem[] = [VIDEO_FILE_ITEM, AUDIO_FILE_ITEM];
    const { rerender } = render(
      <MantineProvider>
        <PrepareDerivatives projectId={PROJECT_ID} item={items[0]} />
      </MantineProvider>,
    );
    await waitFor(() => expect(requestSpy).toHaveBeenCalledTimes(1));

    rerender(
      <MantineProvider>
        <PrepareDerivatives projectId={PROJECT_ID} item={items[1]} />
      </MantineProvider>,
    );

    await waitFor(() => expect(requestSpy).toHaveBeenCalledTimes(2));
    expect(requestSpy).toHaveBeenLastCalledWith(PROJECT_ID, {
      mediaRef: AUDIO_FILE_ITEM.mediaRef,
      requestedBy: REQUESTED_BY_EDITOR_OPEN,
    });
  });

  it('requests nothing at all when no clip is open', async () => {
    renderPane(null);

    // Nothing is rendered, and nothing is requested: an empty selection is not an open asset.
    expect(screen.queryByTestId('prepare-derivatives')).toBeNull();
    await act(async () => {
      await Promise.resolve();
    });
    expect(requestSpy).not.toHaveBeenCalled();
  });

  it('requests for a step_result source too, without interpreting its token', async () => {
    const stepResult = { ...VIDEO_FILE_ITEM, mediaRef: 'step_result:00000000-0000-4000-8000-000000000102' };
    renderPane(stepResult);

    await waitFor(() => expect(requestSpy).toHaveBeenCalledTimes(1));
    expect(requestSpy.mock.calls[0][1].mediaRef).toBe(stepResult.mediaRef);
  });
});

describe('PrepareDerivatives — the button affordance', () => {
  it('re-requests on an explicit press', async () => {
    renderPane(VIDEO_FILE_ITEM);
    await waitFor(() => expect(requestSpy).toHaveBeenCalledTimes(1));

    await act(async () => {
      fireEvent.click(screen.getByTestId('prepare-derivatives-button'));
    });

    expect(requestSpy).toHaveBeenCalledTimes(2);
  });

  it('offers a way to re-read the derivative list, because a request is not a generation', async () => {
    const onCheckAgain = vi.fn();
    renderPane(VIDEO_FILE_ITEM, onCheckAgain);
    await waitFor(() => expect(requestSpy).toHaveBeenCalledTimes(1));

    await act(async () => {
      fireEvent.click(screen.getByTestId('prepare-derivatives-check'));
    });

    expect(onCheckAgain).toHaveBeenCalledTimes(1);
  });

  it('states plainly that a request is not a generation', async () => {
    renderPane(VIDEO_FILE_ITEM);

    const accepted = await screen.findByTestId('prepare-derivatives-accepted');
    const copy = textOf(accepted);
    expect(copy).toContain('No derivative exists until');
    // The number of kinds is what was ASKED FOR, read off the response — never a count of rows.
    expect(copy).toContain('6 derivative kinds');
    expect(copy).toContain('editor-open');
  });
});

describe('PrepareDerivatives — honest failure', () => {
  it('reports a failed REQUEST, and never claims the derivatives are unavailable', async () => {
    requestSpy.mockRejectedValue(new Error('Gateway timeout'));
    renderPane(VIDEO_FILE_ITEM);

    const failed = await screen.findByTestId('prepare-derivatives-failed');
    const copy = textOf(failed);
    expect(copy).toContain('could not be sent');
    expect(copy).toContain('Gateway timeout');
    // The contract cannot distinguish "not generated" from "failed" from "unsupported", so none of
    // those may be claimed — only the thing that actually failed.
    expect(copy).not.toMatch(/derivatives are unavailable|generation failed|unsupported/i);
  });

  it('keeps the button usable after a failure', async () => {
    requestSpy.mockRejectedValueOnce(new Error('Gateway timeout'));
    renderPane(VIDEO_FILE_ITEM);
    await screen.findByTestId('prepare-derivatives-failed');

    await act(async () => {
      fireEvent.click(screen.getByTestId('prepare-derivatives-button'));
    });

    await waitFor(() => expect(screen.getByTestId('prepare-derivatives-accepted')).toBeTruthy());
  });

  it('does not request for an image source any differently than for a video', async () => {
    // The panel does not decide what is worth producing — the consumer does. Nothing here branches
    // on kind, and this pins that so a future allowlist cannot creep in silently.
    renderPane(IMAGE_FILE_ITEM);

    await waitFor(() => expect(requestSpy).toHaveBeenCalledTimes(1));
    expect(requestSpy.mock.calls[0][1]).not.toHaveProperty('kinds');
  });
});
