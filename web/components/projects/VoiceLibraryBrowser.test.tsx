// @vitest-environment jsdom
/**
 * "Browse voices" in the Voices tab. The library API is stubbed with a recorded-shape fixture (the
 * Fish `GET /model` listing is an assumed shape, see docs/voiceover.md): no live vendor call.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import type { VoiceLibraryPage } from '@/lib/types/voices';

const H = vi.hoisted(() => ({
  libraryCalls: [] as unknown[][],
  library: { data: undefined, error: undefined, isLoading: false } as {
    data: unknown; error: unknown; isLoading: boolean;
  },
  createLibraryVoice: vi.fn(),
  notify: vi.fn(),
}));

vi.mock('@/lib/hooks/use-voices', () => ({
  useVoiceLibrary: (...args: unknown[]) => { H.libraryCalls.push(args); return H.library; },
}));
vi.mock('@/lib/api/voices', () => ({ createLibraryVoice: (...a: unknown[]) => H.createLibraryVoice(...a) }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: (...a: unknown[]) => H.notify(...a) } }));

import { VoiceLibraryBrowser } from './VoiceLibraryBrowser';

const PAGE: VoiceLibraryPage = {
  total: 50,
  page: 1,
  pageSize: 24,
  items: [
    { voiceId: 'v-1', name: 'Energetic Male', languages: ['en', 'zh'], tags: ['male', 'energetic'], previewAvailable: true },
    { voiceId: 'v-2', name: 'Calm Narrator', languages: ['en'], tags: [], previewAvailable: false },
  ],
};

function renderBrowser(registered: string[] = []) {
  const onAdded = vi.fn();
  render(
    <MantineProvider>
      <VoiceLibraryBrowser projectId="p1" opened onClose={() => {}} onAdded={onAdded} registeredIds={new Set(registered)} />
    </MantineProvider>,
  );
  return { onAdded };
}

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
  H.libraryCalls = [];
  H.library = { data: PAGE, error: undefined, isLoading: false };
  H.createLibraryVoice.mockReset().mockResolvedValue({});
  H.notify.mockReset();
});
afterEach(cleanup);

describe('VoiceLibraryBrowser', () => {
  it('lists the provider voices with their languages and tags', () => {
    renderBrowser();
    expect(screen.getByText('Energetic Male')).toBeTruthy();
    expect(screen.getByText('Calm Narrator')).toBeTruthy();
    expect(screen.getByText('zh')).toBeTruthy();
    expect(screen.getByText('energetic')).toBeTruthy();
  });

  it('adds a voice through the register-library-voice call and refreshes the tab', async () => {
    const { onAdded } = renderBrowser();
    fireEvent.click(screen.getByRole('button', { name: 'Add Energetic Male' }));
    await waitFor(() => expect(H.createLibraryVoice).toHaveBeenCalledWith('p1', {
      name: 'Energetic Male', remoteVoiceId: 'v-1', description: 'male, energetic',
    }));
    await waitFor(() => expect(onAdded).toHaveBeenCalled());
    expect(screen.getByRole('button', { name: 'Added Energetic Male' }).hasAttribute('disabled')).toBe(true);
  });

  it('shows a voice the project already has as Added and does not offer it again', () => {
    renderBrowser(['v-2']);
    expect(screen.getByRole('button', { name: 'Added Calm Narrator' }).hasAttribute('disabled')).toBe(true);
    expect(screen.getByRole('button', { name: 'Add Energetic Male' }).hasAttribute('disabled')).toBe(false);
  });

  it('searches (debounced) and pages through the library', async () => {
    renderBrowser();
    fireEvent.change(screen.getByLabelText('Search'), { target: { value: 'calm' } });
    await waitFor(() => expect(H.libraryCalls.some((c) => c[2] === 'calm' && c[4] === 1)).toBe(true));

    fireEvent.click(screen.getByRole('button', { name: '2' }));
    await waitFor(() => expect(H.libraryCalls.some((c) => c[4] === 2)).toBe(true));
  });

  it('explains a provider that has no library instead of showing an empty table', () => {
    H.library = { data: undefined, error: new ApiError('voice_library_not_supported', 404, [], { error: 'voice_library_not_supported' }), isLoading: false };
    renderBrowser();
    expect(screen.getByText(/no voice library to browse/)).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('says so when a search matches nothing', () => {
    H.library = { data: { ...PAGE, items: [], total: 0 }, error: undefined, isLoading: false };
    renderBrowser();
    expect(screen.getByText('No voices match this search.')).toBeTruthy();
  });

  it('never offers a page above the 200-page cap the API enforces', () => {
    H.library = { data: { ...PAGE, total: 24 * 417 }, error: undefined, isLoading: false };
    renderBrowser();
    expect(screen.getByRole('button', { name: '200' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: '417' })).toBeNull();
  });

  it('keeps the pagination usable when a page request is refused', async () => {
    H.library = { data: PAGE, error: new ApiError('page must be between 1 and 200.', 400, [], { error: 'page must be between 1 and 200.' }), isLoading: false };
    renderBrowser();
    expect(screen.getByText(/page must be between 1 and 200/)).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: '2' }));
    await waitFor(() => expect(H.libraryCalls.some((c) => c[4] === 2)).toBe(true));
  });
});
