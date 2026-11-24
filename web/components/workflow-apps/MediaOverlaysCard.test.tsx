// @vitest-environment jsdom
/**
 * The picture-overlay run card: the applied overlays and the quiet "Not shown" list, read from the
 * compile's existing output node (design §8.3). No ids, storage keys or pixel boxes reach the
 * customer surface.
 */
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { beforeAll, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { WorkflowStepResult } from '@/lib/types/workflow';
import { findSmallFonts } from '@/components/editor/__tests__/no-small-fonts';
import { MediaOverlaysCard, findMediaOverlays } from './MediaOverlaysCard';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  });
  // A `function`, not an arrow: vitest 4+ refuses `new` on an arrow-function mock.
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
});

const withOverlays = (mediaOverlays: unknown): Pick<WorkflowStepResult, 'stepType' | 'outputJson' | 'output' | 'status'> => ({
  stepType: 'VideoCompile',
  status: 'Completed',
  output: '',
  outputJson: JSON.stringify({ video: {}, mediaOverlays }),
});

const NODE = {
  enabled: true,
  applied: true,
  appliedCount: 2,
  dropped: [{ mediaId: 'm3', windowId: 'o9', reason: 'window_cut_away' }],
  offered: [{ mediaId: 'm0', name: 'testimonial.png' }],
  unusedOffered: [{ mediaId: 'm1', name: 'logo.png' }],
  items: [
    {
      media: 'testimonial.png', form: 'Card', startSec: 1.2, endSec: 6.4, opacityPercent: 100,
      containsBars: false, endedEarly: false, cardAudio: 'Keep',
    },
    {
      media: 'demo.mp4', form: 'PictureInPicture', anchor: 'BottomRight', size: 'Medium',
      box: { x: 1128, y: 590, w: 637, h: 360 }, startSec: 7.0, endSec: 13.0,
      opacityPercent: 80, border: 'Light', endedEarly: true, audio: 'Mixed', audioLevel: 'Balanced',
    },
  ],
};

function mount(node: unknown) {
  return render(
    <MantineProvider>
      <MediaOverlaysCard stepResults={[withOverlays(node)]} />
    </MantineProvider>,
  );
}

describe('MediaOverlaysCard', () => {
  it('shows one row per applied overlay, in plain words, and the quiet Not shown list', () => {
    mount(NODE);
    const card = screen.getByRole('region', { name: 'Pictures over the film' });

    const applied = within(card).getByRole('list', { name: 'What was shown' });
    const rows = within(applied).getAllByRole('listitem');
    expect(rows).toHaveLength(2);
    expect(rows[0]?.textContent).toContain('testimonial.png');
    expect(rows[0]?.textContent).toContain('Full screen');
    expect(rows[0]?.textContent).toContain('0:01–0:06');
    expect(rows[0]?.textContent).toContain('sound kept');
    expect(rows[1]?.textContent).toContain('demo.mp4');
    expect(rows[1]?.textContent).toContain('Corner (bottom right)');
    expect(rows[1]?.textContent).toContain('80% opacity');
    expect(rows[1]?.textContent).toContain('Light border');
    expect(rows[1]?.textContent).toContain('sound at Balanced');

    const notShown = within(card).getByRole('list', { name: 'Not shown' });
    expect(notShown.textContent).toContain('logo.png: not chosen for this edit');
    expect(notShown.textContent).toContain('A picture: the moment was cut out');
  });

  it('never shows ids, storage keys or pixel boxes on the customer surface', () => {
    mount(NODE);
    const text = screen.getByRole('region', { name: 'Pictures over the film' }).textContent ?? '';
    for (const leak of ['m0', 'm1', 'm3', 'o9', '1128', '637', 'storageKey']) {
      expect(text).not.toContain(leak);
    }
  });

  it('shows nothing extra for a run with no overlay report or no overlays', () => {
    const { unmount } = render(
      <MantineProvider>
        <MediaOverlaysCard stepResults={[]} />
      </MantineProvider>,
    );
    expect(screen.queryByTestId('media-overlays')).toBeNull();
    unmount();

    mount({ enabled: true, applied: true, appliedCount: 0, items: [], dropped: [], unusedOffered: [] });
    expect(screen.queryByTestId('media-overlays')).toBeNull();
  });

  it('is a labelled section with a heading, and its lists have names', () => {
    mount(NODE);
    const card = screen.getByRole('region', { name: 'Pictures over the film' });
    expect(within(card).getByRole('heading', { name: 'Pictures over the film' })).toBeTruthy();
    expect(within(card).getByRole('list', { name: 'What was shown' })).toBeTruthy();
    expect(within(card).getByRole('list', { name: 'Not shown' })).toBeTruthy();
  });

  it('keeps every text at or above 12 px', () => {
    const source = readFileSync(join(process.cwd(), 'components', 'workflow-apps', 'MediaOverlaysCard.tsx'), 'utf8');
    expect(findSmallFonts(source)).toEqual([]);
  });
});

describe('findMediaOverlays', () => {
  it('takes the last completed compile report and ignores failed or unparseable steps', () => {
    const results = [
      withOverlays({ items: [{ media: 'old.png', form: 'Card' }] }),
      { stepType: 'VideoCompile', status: 'Failed' as const, output: '', outputJson: JSON.stringify({ mediaOverlays: { items: [] } }) },
      { stepType: 'VideoCompile', status: 'Completed' as const, output: 'not json', outputJson: 'not json' },
      withOverlays(NODE),
    ];
    expect(findMediaOverlays(results)?.items).toHaveLength(2);
    expect(findMediaOverlays([])).toBeNull();
  });

  it('ignores a mediaOverlays object on a non-compile step', () => {
    const forged = {
      stepType: 'Agent' as const,
      status: 'Completed' as const,
      output: '',
      outputJson: JSON.stringify({ mediaOverlays: { items: [{ media: 'forged.png' }] } })
    };

    expect(findMediaOverlays([forged])).toBeNull();
  });
});
