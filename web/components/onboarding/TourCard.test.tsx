// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { TOUR_VERSION } from '@/lib/onboarding/state';
import { OnboardingContext, type OnboardingContextValue } from './onboarding-context';
import { TourCard } from './TourCard';

vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ user: { email: 'ada@example.com' } }) }));

let body: unknown;
let calls: { url: string; method: string; body?: unknown }[];
const startTour = vi.fn();

const blank = { persona: null, material: null, suggestedPersona: null, answeredAt: null, skippedAt: null };
const answered = { ...blank, persona: 'creator', material: 'footage', answeredAt: '2026-11-11T10:00:00Z' };
const state = (tour: string, profile: unknown) => ({
  tour: { status: tour, version: tour === 'pending' ? null : TOUR_VERSION, updatedAt: null },
  checklist: { dismissed: false, items: [] },
  ...(profile ? { profile } : {}),
});

beforeEach(() => {
  window.localStorage.clear();
  window.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null,
    addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
  calls = [];
  startTour.mockClear();
  body = state('pending', answered);
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, method: init?.method ?? 'GET', body: init?.body ? JSON.parse(String(init.body)) : undefined });
      if (url === '/api/v1/onboarding' && init?.method === 'PUT') return new Response(JSON.stringify(state('skipped', answered)), { status: 200 });
      if (url === '/api/v1/onboarding') return new Response(JSON.stringify(body), { status: 200 });
      return new Response(null, { status: 204 });
    }),
  );
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const context: OnboardingContextValue = {
  startTour, editorEnabled: true, appsEnabled: true, welcomeEnabled: true, welcome: 'stay', beginWelcome() {}, cardUi: null, setCardUi: null,
};
const mount = () =>
  render(
    <MantineProvider>
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <OnboardingContext.Provider value={context}>
          <TourCard />
        </OnboardingContext.Provider>
      </SWRConfig>
    </MantineProvider>,
  );
const loaded = () => waitFor(() => expect(calls.some((c) => c.url === '/api/v1/onboarding')).toBe(true));

describe('TourCard', () => {
  it('offers the tour quietly to someone who answered the welcome questions and has not taken it', async () => {
    mount();
    const card = await screen.findByTestId('tour-card');
    expect(card.textContent).toContain('New here? Take a look around');
    const take = screen.getByRole('button', { name: 'Take the tour' });
    expect(take.style.minHeight).toBe('44px');
    fireEvent.click(take);
    expect(startTour).toHaveBeenCalledTimes(1);
  });

  it('goes away for good on "Not now", recorded as a skipped tour', async () => {
    mount();
    fireEvent.click(await screen.findByRole('button', { name: 'Not now' }));
    expect(screen.queryByTestId('tour-card')).toBeNull();
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ tour: { status: 'skipped', version: TOUR_VERSION } }));
  });

  it.each([
    ['who skipped the questions (the tour opens for them by itself)', state('pending', { ...blank, skippedAt: '2026-11-11T10:00:00Z' })],
    ['who has no profile (an existing account: the tour opens by itself)', state('pending', null)],
    ['who answered and already finished the tour', state('completed', answered)],
    ['who answered and already skipped the tour', state('skipped', answered)],
  ])('renders nothing for someone %s', async (_who, serverBody) => {
    body = serverBody;
    mount();
    await loaded();
    await new Promise((r) => setTimeout(r, 30));
    expect(screen.queryByTestId('tour-card')).toBeNull();
  });
});
