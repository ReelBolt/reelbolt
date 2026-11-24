import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { NextIntlClientProvider } from 'next-intl';
import en from '../../messages/en/postText.json';
import { ApiError } from '@/lib/api/client';
import { PostTextButton } from './PostTextDialog';

const api = vi.hoisted(() => ({ estimate: vi.fn(), write: vi.fn(), start: vi.fn(), save: vi.fn() }));
vi.mock('@/lib/api/post-text', async (orig) => {
  const real = await orig<typeof import('@/lib/api/post-text')>();
  return { ...real, estimatePostText: api.estimate, writePostText: api.write, startTranscript: api.start, savePostText: api.save };
});
vi.mock('@/lib/hooks/use-post-texts', () => ({ useSavedPostTexts: () => ({ data: { items: [] }, mutate: vi.fn() }) }));

const estimate = {
  billingEnabled: true, credits: 1, balance: 50, affordable: true, included: false, model: 'm', language: 'en',
  platforms: { version: 1, lastChecked: '2026-11-10', items: [] },
  lineItems: [{ kind: 'text', credits: 1, state: 'included', note: null, heldSeparately: false }],
  disclosure: 'Frames go to the provider.', video: null, totalCredits: 1,
};

function view() {
  return render(
    <NextIntlClientProvider locale="en" messages={{ postText: en }}>
      <MantineProvider><PostTextButton projectId="p" outputId="o" /></MantineProvider>
    </NextIntlClientProvider>,
  );
}

describe('PostTextDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.matchMedia = ((query: string) => ({
      matches: false, media: query, onchange: null, addEventListener: vi.fn(), removeEventListener: vi.fn(),
      addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
    })) as unknown as typeof window.matchMedia;
    Object.defineProperty(document, 'fonts', { configurable: true, value: { addEventListener: vi.fn(), removeEventListener: vi.fn() } });
    globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} };
 api.estimate.mockResolvedValue(estimate); });

  it('shows a priced estimate and the disclosure when reading the video', async () => {
    view();
    fireEvent.click(screen.getByTestId('post-text-open'));
    expect(await screen.findByText('About 1 credits')).toBeTruthy();
    fireEvent.click(screen.getByTestId('post-text-read-video'));
    expect(await screen.findByTestId('post-text-disclosure')).toBeTruthy();
    expect(api.estimate).toHaveBeenLastCalledWith('p', 'o', expect.objectContaining({ readVideo: true, hashtagSource: 'model' }));
  });

  it('shows the disclosure when web hashtags are on, without reading the video', async () => {
    api.estimate.mockResolvedValue({ ...estimate, disclosure: 'Project name goes to the web search provider.' });
    view();
    fireEvent.click(screen.getByTestId('post-text-open'));
    expect((await screen.findByTestId('post-text-disclosure')).textContent).toContain('web search provider');
  });

  it('renders cards with the server basis label verbatim and cited hosts', async () => {
    api.write.mockResolvedValue({
      outputId: 'o', language: 'en', tone: null, creditsUsed: 1,
      drafts: [{ platform: 'x', label: 'X', title: null, body: 'Hello', hashtags: ['a'], text: 'Hello #a', characters: 8, limit: 280, titleLimit: null, trimmed: false }],
      hashtagBasis: { kind: 'web', source: 'web', label: 'found in cited web search results', sources: [{ title: 'Page', host: 'example.com', url: 'https://example.com/a' }, { title: 'Bad', host: 'evil.test', url: 'javascript:alert(1)' }] },
    });
    view();
    fireEvent.click(screen.getByTestId('post-text-open'));
    await screen.findByText('About 1 credits');
    fireEvent.click(screen.getByTestId('post-text-write'));
    expect(await screen.findByTestId('post-text-card-x')).toBeTruthy();
    expect(screen.getByTestId('post-text-basis').textContent).toContain('found in cited web search results');
    expect(screen.getByText('Page (example.com)').closest('a')).not.toBeNull();
    expect(screen.getByText('evil.test').closest('a')).toBeNull();
    await waitFor(() => expect(screen.getByTestId('post-text-count-x').textContent).toContain('280'));
  });
});

const result = {
  outputId: 'o', language: 'en', tone: null, creditsUsed: null,
  drafts: [{ platform: 'x', label: 'X', title: null, body: 'Hello', hashtags: ['a'], text: 'Hello #a', characters: 8, limit: 280, titleLimit: null, trimmed: false }],
  hashtagBasis: { kind: 'model', source: 'model', label: 'suggested, not measured trends' },
};

async function openAndWrite() {
  view();
  fireEvent.click(screen.getByTestId('post-text-open'));
  await screen.findByText('About 1 credits');
  fireEvent.click(screen.getByTestId('post-text-write'));
}

describe('PostTextDialog write flow', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.matchMedia = ((query: string) => ({
      matches: false, media: query, onchange: null, addEventListener: vi.fn(), removeEventListener: vi.fn(),
      addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
    })) as unknown as typeof window.matchMedia;
    Object.defineProperty(document, 'fonts', { configurable: true, value: { addEventListener: vi.fn(), removeEventListener: vi.fn() } });
    globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} };
    api.estimate.mockResolvedValue(estimate);
  });
  afterEach(() => { vi.useRealTimers(); });

  it('starts the transcript once, waits, then writes again on a 202 preparing', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const start = { method: 'POST', path: '/api/v1/projects/p/media/file:1/analysis', kinds: ['transcript'] };
    api.write
      .mockResolvedValueOnce({ status: 'preparing', retryAfterSeconds: 3, video: { startTranscript: start } })
      .mockResolvedValueOnce(result);
    api.start.mockResolvedValue({});
    await openAndWrite();
    await waitFor(() => expect(api.start).toHaveBeenCalledWith(start.path, start.kinds));
    expect(await screen.findByText(en.preparing)).toBeTruthy();
    await vi.advanceTimersByTimeAsync(4000);
    expect(await screen.findByTestId('post-text-card-x')).toBeTruthy();
    expect(api.write).toHaveBeenCalledTimes(2);
    expect(api.start).toHaveBeenCalledTimes(1);
  });

  it('says what to do on 402 and shows the server sentence on 409', async () => {
    api.write.mockRejectedValueOnce(new ApiError('nope', 402, [], { code: 'INSUFFICIENT_CREDITS' }));
    await openAndWrite();
    expect((await screen.findByTestId('post-text-error')).textContent).toContain(en.creditsRefused);
    api.write.mockRejectedValueOnce(new ApiError('This video is held for review.', 409, [], { code: 'QUARANTINED' }));
    fireEvent.click(screen.getByTestId('post-text-write'));
    await waitFor(() => expect(screen.getByTestId('post-text-error').textContent).toContain('This video is held for review.'));
  });

  it('shows the Italian label for a known server note and the raw text for an unknown one', async () => {
    api.estimate.mockResolvedValue({ ...estimate, lineItems: [
      { kind: 'webSearch', credits: null, state: 'skipped', note: "Web search is not set up, so the writer's own tags are used.", heldSeparately: false },
      { kind: 'frames', credits: null, state: 'skipped', note: 'Something new.', heldSeparately: false },
    ] });
    view();
    fireEvent.click(screen.getByTestId('post-text-open'));
    const box = await screen.findByTestId('post-text-estimate');
    await waitFor(() => expect(box.textContent).toContain(en.lineKind.webSearch));
    expect(box.textContent).toContain(en.note.noWebSearch);
    expect(box.textContent).toContain('Something new.');
  });

  it('copies, edits, saves and regenerates a card', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
    api.write.mockResolvedValue(result);
    api.save.mockResolvedValue({});
    await openAndWrite();
    const card = await screen.findByTestId('post-text-card-x');
    const inCard = (name: string): HTMLButtonElement => Array.from(card.querySelectorAll('button')).find((b) => b.textContent?.includes(name)) as HTMLButtonElement;
    fireEvent.click(inCard(en.card.copy));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith('Hello\n\n#a'));
    fireEvent.click(inCard(en.card.edit));
    fireEvent.change(card.querySelector('textarea')!, { target: { value: 'Changed' } });
    fireEvent.click(inCard(en.card.save));
    await waitFor(() => expect(api.save).toHaveBeenCalledWith('p', 'o', 'x', expect.objectContaining({ body: 'Changed', hashtags: ['a'], edited: true, language: 'en' })));
    fireEvent.click(inCard(en.card.regenerate));
    await waitFor(() => expect(api.write).toHaveBeenCalledTimes(2));
    expect(api.write).toHaveBeenLastCalledWith('p', 'o', expect.objectContaining({ platforms: ['x'] }));
  });
});
