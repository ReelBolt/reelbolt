// @vitest-environment jsdom
/** T-012: the public guest review page: single failure message, credentials omitted, text-only rendering, decisions. */

import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { GuestReviewPage } from '../GuestReviewPage';
import type { GuestReview } from '@/lib/editing/review/guest-links';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;

Object.defineProperty(document, 'fonts', { value: { addEventListener: () => {}, removeEventListener: () => {} }, configurable: true });

const TOKEN = 'T'.repeat(43);

function review(over: Partial<GuestReview> = {}): GuestReview {
  return {
    title: 'Client cut',
    expiresAt: '2030-01-01T00:00:00Z',
    allowComments: true,
    renderCreatedAt: '2029-12-01T00:00:00Z',
    stale: false,
    durationSec: 10,
    comments: [],
    decision: null,
    ...over,
  };
}

const json = (status: number, body: unknown): Response => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

let fetchMock: ReturnType<typeof vi.fn>;

function mount() {
  return render(
    <MantineProvider>
      <GuestReviewPage token={TOKEN} />
    </MantineProvider>,
  );
}

beforeEach(() => {
  window.localStorage.clear();
  fetchMock = vi.fn();
  vi.stubGlobal('fetch', fetchMock);
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('GuestReviewPage', () => {
  it('shows a loader, then the film and title, fetching without credentials', async () => {
    fetchMock.mockResolvedValue(json(200, review()));
    mount();
    expect(screen.getByTestId('guest-loading')).toBeTruthy();
    expect((await screen.findByTestId('guest-title')).textContent).toBe('Client cut');
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe(`/api/v1/review-links/${TOKEN}`);
    expect(init.credentials).toBe('omit');
    expect(init.referrerPolicy).toBe('no-referrer');
    expect(screen.getByTestId('guest-video').getAttribute('src')).toBe(`/api/v1/review-links/${TOKEN}/media`);
    expect(screen.getByTestId('guest-expiry').textContent).toMatch(/This link works until/);
    expect(document.body.textContent).not.toMatch(/Early preview/);
  });

  it.each([404, 410, 403])('says the link is gone for a %s, with no retry', async (status) => {
    fetchMock.mockResolvedValue(json(status, {}));
    mount();
    expect((await screen.findByTestId('guest-failure')).textContent).toBe('This link has expired or was turned off.');
    expect(screen.queryByRole('button', { name: 'Try again' })).toBeNull();
  });

  it('does not say the link is gone when our server fails, and offers Try again', async () => {
    fetchMock.mockResolvedValue(json(500, {}));
    mount();
    expect((await screen.findByTestId('guest-failure')).textContent).toMatch(/Something went wrong on our side/);
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
  });

  it('says it could not reach ReelBolt when the network fails, and Try again reloads', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'));
    mount();
    expect((await screen.findByTestId('guest-failure')).textContent).toMatch(/Could not reach ReelBolt/);
    expect(document.body.textContent).not.toMatch(/expired or was turned off/);
    fetchMock.mockResolvedValue(json(200, review()));
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect((await screen.findByTestId('guest-title')).textContent).toBeTruthy();
  });

  it('says to try again in a minute on 429', async () => {
    fetchMock.mockResolvedValue(json(429, { code: 'RATE_LIMITED' }));
    mount();
    expect((await screen.findByTestId('guest-failure')).textContent).toBe('Too many requests. Try again in a minute.');
  });

  it('renders guest text as text, never as markup', async () => {
    const evil = '<script>window.__pwned = 1</script><img src=x onerror="window.__pwned = 2">';
    fetchMock.mockResolvedValue(
      json(200, review({ title: evil, comments: [{ id: 'c1', parentId: null, authorName: evil, guest: true, body: evil, frame: 30, resolved: false, createdAt: '2029-12-02T00:00:00Z' }] })),
    );
    const { container } = mount();
    await screen.findByTestId('guest-thread-c1');
    expect(container.querySelector('script')).toBeNull();
    expect(container.querySelector('img')).toBeNull();
    expect(screen.getByTestId('guest-thread-c1').textContent).toContain(evil);
    expect(screen.getByTestId('guest-thread-c1').textContent).toContain('(guest)');
    expect((window as unknown as { __pwned?: number }).__pwned).toBeUndefined();
  });

  it('draws one marker per thread, dims resolved ones, and lists threads by time', async () => {
    fetchMock.mockResolvedValue(
      json(
        200,
        review({
          comments: [
            { id: 'late', parentId: null, authorName: 'Ana', guest: true, body: 'later', frame: 200, resolved: false, createdAt: '2029-12-02T00:00:00Z' },
            { id: 'early', parentId: null, authorName: 'Bo', guest: false, body: 'earlier', frame: 30, resolved: true, createdAt: '2029-12-03T00:00:00Z' },
            { id: 'rep', parentId: 'early', authorName: 'Ana', guest: true, body: 'a reply', frame: 30, resolved: false, createdAt: '2029-12-04T00:00:00Z' },
          ],
        }),
      ),
    );
    mount();
    await screen.findByTestId('guest-thread-early');
    expect(screen.getByTestId('guest-marker-early')).toBeTruthy();
    expect(screen.getByTestId('guest-marker-late')).toBeTruthy();
    expect(screen.queryByTestId('guest-marker-rep')).toBeNull();
    const ids = Array.from(screen.getByTestId('guest-threads').querySelectorAll('[data-testid^="guest-thread-"]')).map((n) => n.getAttribute('data-testid'));
    expect(ids).toEqual(['guest-thread-early', 'guest-thread-late']);
    expect(screen.getByTestId('guest-thread-early').style.opacity).toBe('0.6');
    expect(screen.getByTestId('guest-thread-early').textContent).toContain('a reply');
    expect(screen.getByTestId('guest-thread-early').textContent).toContain('Bo, resolved');
  });

  it('shows the Newer edits exist banner only when stale', async () => {
    fetchMock.mockResolvedValue(json(200, review({ stale: true })));
    mount();
    expect(await screen.findByTestId('guest-stale-banner')).toBeTruthy();
  });

  it('posts a comment with the remembered name, omitting credentials, then reloads', async () => {
    fetchMock.mockResolvedValueOnce(json(200, review()));
    mount();
    await screen.findByTestId('guest-title');
    expect((screen.getByTestId('guest-comment-send') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(screen.getByTestId('guest-name'), { target: { value: ' Ana ' } });
    expect(window.localStorage.getItem('reelbolt:review:guest-name')).toBe('Ana');
    fireEvent.change(screen.getByTestId('guest-comment-body'), { target: { value: 'Logo is small' } });
    fetchMock.mockResolvedValueOnce(json(201, { id: 'n1' })).mockResolvedValueOnce(json(200, review()));
    fireEvent.click(screen.getByTestId('guest-comment-send'));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3));
    const [url, init] = fetchMock.mock.calls[1] as [string, RequestInit];
    expect(url).toBe(`/api/v1/review-links/${TOKEN}/comments`);
    expect(init.method).toBe('POST');
    expect(init.credentials).toBe('omit');
    expect(JSON.parse(init.body as string)).toMatchObject({ name: 'Ana', body: 'Logo is small', frame: 0 });
  });

  it('restores the remembered name', async () => {
    window.localStorage.setItem('reelbolt:review:guest-name', 'Ana');
    fetchMock.mockResolvedValue(json(200, review()));
    mount();
    await screen.findByTestId('guest-title');
    await waitFor(() => expect((screen.getByTestId('guest-name') as HTMLInputElement).value).toBe('Ana'));
  });

  it('requires a note for Request changes unless the guest already commented', async () => {
    window.localStorage.setItem('reelbolt:review:guest-name', 'Ana');
    fetchMock.mockResolvedValueOnce(json(200, review()));
    mount();
    await screen.findByTestId('guest-title');
    await waitFor(() => expect((screen.getByTestId('guest-name') as HTMLInputElement).value).toBe('Ana'));
    fireEvent.click(screen.getByTestId('guest-request-changes'));
    const send = screen.getByTestId('guest-changes-send') as HTMLButtonElement;
    expect(send.disabled).toBe(true);
    fireEvent.change(screen.getByTestId('guest-changes-note'), { target: { value: 'Cut the intro' } });
    expect(send.disabled).toBe(false);
    fetchMock.mockResolvedValueOnce(json(201, {})).mockResolvedValueOnce(json(200, review()));
    fireEvent.click(send);
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3));
    const [url, init] = fetchMock.mock.calls[1] as [string, RequestInit];
    expect(url).toBe(`/api/v1/review-links/${TOKEN}/decision`);
    expect(JSON.parse(init.body as string)).toEqual({ decision: 'changesRequested', name: 'Ana', note: 'Cut the intro' });
  });

  it('lets a guest who already commented request changes without a note', async () => {
    window.localStorage.setItem('reelbolt:review:guest-name', 'Ana');
    fetchMock.mockResolvedValueOnce(json(200, review({ comments: [{ id: 'c', parentId: null, authorName: 'Ana', guest: true, body: 'x', frame: 1, resolved: false, createdAt: '2029-12-02T00:00:00Z' }] })));
    mount();
    await screen.findByTestId('guest-thread-c');
    await waitFor(() => expect((screen.getByTestId('guest-name') as HTMLInputElement).value).toBe('Ana'));
    fireEvent.click(screen.getByTestId('guest-request-changes'));
    expect((screen.getByTestId('guest-changes-send') as HTMLButtonElement).disabled).toBe(false);
  });

  it('posts Approve', async () => {
    window.localStorage.setItem('reelbolt:review:guest-name', 'Ana');
    fetchMock.mockResolvedValueOnce(json(200, review()));
    mount();
    await screen.findByTestId('guest-title');
    await waitFor(() => expect((screen.getByTestId('guest-approve') as HTMLButtonElement).disabled).toBe(false));
    fetchMock.mockResolvedValueOnce(json(201, {})).mockResolvedValueOnce(json(200, review({ decision: { decision: 'approved', guestName: 'Ana', at: '2029-12-05T00:00:00Z' } })));
    fireEvent.click(screen.getByTestId('guest-approve'));
    expect((await screen.findByTestId('guest-decision-banner')).textContent).toBe('Approved by Ana');
  });

  it('turns a rate limit on a write into the one-minute sentence without leaving the page', async () => {
    window.localStorage.setItem('reelbolt:review:guest-name', 'Ana');
    fetchMock.mockResolvedValueOnce(json(200, review()));
    mount();
    await screen.findByTestId('guest-title');
    await waitFor(() => expect((screen.getByTestId('guest-name') as HTMLInputElement).value).toBe('Ana'));
    fireEvent.change(screen.getByTestId('guest-comment-body'), { target: { value: 'hi' } });
    fetchMock.mockResolvedValueOnce(json(429, {}));
    fireEvent.click(screen.getByTestId('guest-comment-send'));
    expect((await screen.findByTestId('guest-problem')).textContent).toBe('Too many requests. Try again in a minute.');
    expect(screen.getByTestId('guest-title')).toBeTruthy();
  });

  it('hides the comment box when comments are off', async () => {
    fetchMock.mockResolvedValue(json(200, review({ allowComments: false })));
    mount();
    await screen.findByTestId('guest-title');
    expect(screen.queryByTestId('guest-comment-body')).toBeNull();
    expect(screen.getByTestId('guest-approve')).toBeTruthy();
  });
});
