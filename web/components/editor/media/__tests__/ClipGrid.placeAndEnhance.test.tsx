// @vitest-environment jsdom
/**
 * Merge pin (zip-upload-unpack into video-enhance/ui): the tile menu keeps BOTH sides — the place choices typed as
 * `PlaceChoice` (the four placements plus "Place over") and video-enhance's "Enhance..." — and each calls its own handler.
 */
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, cleanup, fireEvent, act } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ClipGrid } from '../ClipGrid';
import { POOL } from '@/lib/editing/media/fixtures';
import type { MediaPoolItem } from '@/lib/editing/media/types';
import type { PlaceChoice } from '../../place/placeChoices';

beforeEach(() => {
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const video = POOL.find((item) => item.kind === 'video')!;
const audio = POOL.find((item) => item.kind === 'audio')!;

function mount(onPlace: (item: MediaPoolItem, mode: PlaceChoice) => void, onEnhance: (item: MediaPoolItem) => void) {
  return render(
    <MantineProvider>
      <ClipGrid
        items={[video, audio]}
        viewMode="grid"
        selectedMediaRef={null}
        subjectMediaRef={null}
        onSelect={() => {}}
        onHover={() => {}}
        onFocusTile={() => {}}
        onPlace={onPlace}
        onEnhance={onEnhance}
      />
    </MantineProvider>,
  );
}

const openMenu = async (index: number) => {
  await act(async () => {
    fireEvent.click(screen.getAllByTestId('media-tile-menu')[index]);
  });
};

describe('ClipGrid tile menu: place choices and Enhance side by side', () => {
  it('offers the four placements, Place over and Enhance... on a video tile, each calling its own handler', async () => {
    const onPlace = vi.fn();
    const onEnhance = vi.fn();
    mount(onPlace, onEnhance);

    await openMenu(0);
    for (const mode of ['insert', 'overwrite', 'append', 'placeOnTop', 'placeOver'])
      expect(await screen.findByTestId(`tile-place-${mode}`)).toBeTruthy();
    expect(screen.getByTestId('tile-enhance')).toBeTruthy();

    await act(async () => {
      fireEvent.click(screen.getByTestId('tile-place-placeOver'));
    });
    expect(onPlace).toHaveBeenCalledWith(video, 'placeOver');
    expect(onEnhance).not.toHaveBeenCalled();

    await openMenu(0);
    await act(async () => {
      fireEvent.click(await screen.findByTestId('tile-enhance'));
    });
    expect(onEnhance).toHaveBeenCalledWith(video);
  });

  it('keeps the place choices on an audio tile but offers no Enhance...', async () => {
    mount(vi.fn(), vi.fn());

    await openMenu(1);
    expect(await screen.findByTestId('tile-place-placeOver')).toBeTruthy();
    expect(screen.queryByTestId('tile-enhance')).toBeNull();
  });
});
