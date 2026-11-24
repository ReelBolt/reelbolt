// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import { SWRConfig } from 'swr';
import { userKeyFromEmail } from '@/lib/telemetry/identity';
import { TELEMETRY_CONSENT_STORAGE_KEY } from '@/lib/telemetry/consent';
import { mergeChecklist, useOnboarding } from './use-onboarding';
import { TOUR_VERSION, type OnboardingServerState } from '@/lib/onboarding/state';

vi.mock('./use-auth', () => ({ useAuth: () => ({ user: { email: 'ada@example.com' } }) }));

type Call = { url: string; method: string; body: unknown };
let calls: Call[];
let onboardingStatus: number;
let onboardingBody: unknown;
let profileStatus: number;
let profileBody: unknown;

function installFetch() {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, method: init?.method ?? 'GET', body: init?.body ? JSON.parse(String(init.body)) : undefined });
      if (url === '/api/v1/onboarding') {
        return new Response(JSON.stringify(onboardingBody), { status: onboardingStatus });
      }
      if (url === '/api/v1/onboarding/profile') {
        return new Response(JSON.stringify(profileBody), { status: profileStatus });
      }
      return new Response(null, { status: 204 });
    }),
  );
}

const wrapper = ({ children }: { children: React.ReactNode }) => (
  <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>{children}</SWRConfig>
);

const serverState = (over: Partial<OnboardingServerState['tour']> = {}): OnboardingServerState => ({
  tour: { status: 'pending', version: null, updatedAt: null, ...over },
  checklist: { dismissed: false, items: [{ key: 'create_project', done: true }] },
});

beforeEach(() => {
  window.localStorage.clear();
  calls = [];
  onboardingStatus = 200;
  onboardingBody = serverState();
  profileStatus = 200;
  profileBody = {};
  vi.stubEnv('NEXT_PUBLIC_TELEMETRY_ENABLED', 'true');
  installFetch();
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
});

describe('useOnboarding', () => {
  it('offers the tour to a pending account once the server has answered', async () => {
    const { result } = renderHook(() => useOnboarding(), { wrapper });
    expect(result.current.offerTour).toBe(false);
    await waitFor(() => expect(result.current.settled).toBe(true));
    expect(result.current.offerTour).toBe(true);
  });

  it('remembers a skip locally and saves it to the server', async () => {
    const { result } = renderHook(() => useOnboarding(), { wrapper });
    await waitFor(() => expect(result.current.settled).toBe(true));
    onboardingBody = serverState({ status: 'skipped', version: TOUR_VERSION });
    act(() => result.current.finishTour('skipped'));
    expect(result.current.offerTour).toBe(false);
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true));
    expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ tour: { status: 'skipped', version: TOUR_VERSION } });
  });

  it('keeps remembering when the API answers 404', async () => {
    onboardingStatus = 404;
    onboardingBody = {};
    const first = renderHook(() => useOnboarding(), { wrapper });
    await waitFor(() => expect(first.result.current.settled).toBe(true));
    expect(first.result.current.server).toBeNull();
    expect(first.result.current.offerTour).toBe(true);
    act(() => first.result.current.finishTour('completed'));
    first.unmount();

    const second = renderHook(() => useOnboarding(), { wrapper });
    await waitFor(() => expect(second.result.current.settled).toBe(true));
    expect(second.result.current.offerTour).toBe(false);
  });

  it('survives a network failure without throwing', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw new TypeError('offline'); }));
    const { result } = renderHook(() => useOnboarding(), { wrapper });
    await waitFor(() => expect(result.current.settled).toBe(true));
    expect(() => act(() => result.current.dismissChecklist())).not.toThrow();
    expect(result.current.checklistDismissed).toBe(true);
  });

  it('re-offers the tour when the saved version is older', async () => {
    onboardingBody = serverState({ status: 'completed', version: TOUR_VERSION - 1 });
    const { result } = renderHook(() => useOnboarding(), { wrapper });
    await waitFor(() => expect(result.current.settled).toBe(true));
    expect(result.current.offerTour).toBe(true);
  });

  describe('telemetry', () => {
    const events = () => calls.filter((c) => c.url === '/api/v1/onboarding/events');

    it('sends nothing without granted consent', async () => {
      const { result } = renderHook(() => useOnboarding(), { wrapper });
      await waitFor(() => expect(result.current.settled).toBe(true));
      act(() => result.current.announceTourStarted());
      act(() => result.current.finishTour('completed'));
      expect(events()).toHaveLength(0);
      window.localStorage.setItem(TELEMETRY_CONSENT_STORAGE_KEY, 'denied');
      act(() => result.current.dismissChecklist());
      expect(events()).toHaveLength(0);
    });

    it('sends the closed events when consent is granted', async () => {
      window.localStorage.setItem(TELEMETRY_CONSENT_STORAGE_KEY, 'granted');
      const { result } = renderHook(() => useOnboarding(), { wrapper });
      await waitFor(() => expect(result.current.settled).toBe(true));
      act(() => result.current.announceTourStarted());
      act(() => result.current.finishTour('skipped'));
      act(() => result.current.dismissChecklist());
      act(() => result.current.noteDone(['create_project']));
      act(() => result.current.noteDone(['create_project']));
      await waitFor(() => expect(events()).toHaveLength(4));
      expect(events().map((c) => c.body)).toEqual([
        { event: 'tour_started' },
        { event: 'tour_skipped' },
        { event: 'checklist_dismissed' },
        { event: 'checklist_item_done', item: 'create_project' },
      ]);
    });

    it('sends nothing when telemetry is off, even with consent', async () => {
      vi.stubEnv('NEXT_PUBLIC_TELEMETRY_ENABLED', 'false');
      window.localStorage.setItem(TELEMETRY_CONSENT_STORAGE_KEY, 'granted');
      const { result } = renderHook(() => useOnboarding(), { wrapper });
      await waitFor(() => expect(result.current.settled).toBe(true));
      act(() => result.current.finishTour('completed'));
      expect(events()).toHaveLength(0);
    });
  });

  it('reconciles a local skip up to the server only for the instance that asks', async () => {
    window.localStorage.setItem(
      'rb-onboarding:v1:' + userKeyFromEmail('ada@example.com'),
      JSON.stringify({ tour: { status: 'skipped', version: TOUR_VERSION, at: Date.now() } }),
    );
    const plain = renderHook(() => useOnboarding(), { wrapper });
    await waitFor(() => expect(plain.result.current.settled).toBe(true));
    expect(calls.filter((c) => c.method === 'PUT')).toHaveLength(0);
    plain.unmount();
    const owner = renderHook(() => useOnboarding({ reconcile: true }), { wrapper });
    await waitFor(() => expect(calls.filter((c) => c.method === 'PUT')).toHaveLength(1));
    expect(owner.result.current.settled).toBe(true);
  });

  it('marks open_editor once and only once', async () => {
    const { result } = renderHook(() => useOnboarding(), { wrapper });
    await waitFor(() => expect(result.current.settled).toBe(true));
    act(() => result.current.mark('open_editor'));
    act(() => result.current.mark('open_editor'));
    await waitFor(() => expect(calls.filter((c) => c.method === 'PUT')).toHaveLength(1));
    expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ marks: ['open_editor'] });
  });
});

describe('mergeChecklist', () => {
  it('keeps the API order, ORs server and sticky ticks, and uses the fallback only without a server answer', () => {
    const items = mergeChecklist(serverState(), { done: ['open_editor'] }, { run_video: true });
    expect(items.map((i) => i.key)).toEqual(['create_project', 'upload_file', 'run_video', 'open_result', 'try_assistant', 'open_editor']);
    expect(items.filter((i) => i.done).map((i) => i.key)).toEqual(['create_project', 'open_editor']);
    // Only with no server answer does the fallback stand in.
    const offline = mergeChecklist(null, { done: ['open_editor'] }, { run_video: true });
    expect(offline.filter((i) => i.done).map((i) => i.key)).toEqual(['run_video', 'open_editor']);
  });
});

describe('useOnboarding: the welcome answers', () => {
  const blank = { persona: null, material: null, suggestedPersona: 'creator', pickedPlan: null, answeredAt: null, skippedAt: null };
  const profilePuts = () => calls.filter((c) => c.url === '/api/v1/onboarding/profile');

  it('exposes the profile the API sends, and none when it sends none', async () => {
    onboardingBody = { ...serverState(), profile: blank };
    const { result } = renderHook(() => useOnboarding(), { wrapper });
    await waitFor(() => expect(result.current.settled).toBe(true));
    expect(result.current.profile).toEqual(blank);
    expect(result.current.answeredWelcome).toBe(false);
  });

  it('saves the two answers, adopts the stored profile and hands back the recipe the API named', async () => {
    onboardingBody = { ...serverState(), profile: blank };
    const stored = { ...blank, persona: 'educator', material: 'idea', answeredAt: '2026-11-11T10:00:00Z' };
    profileBody = { profile: stored, starterTemplateKey: 'video-derush-edit' };
    const { result } = renderHook(() => useOnboarding(), { wrapper });
    await waitFor(() => expect(result.current.settled).toBe(true));
    let answer: Awaited<ReturnType<typeof result.current.answerWelcome>> | undefined;
    await act(async () => {
      answer = await result.current.answerWelcome('educator', 'idea');
    });
    expect(profilePuts()).toEqual([{ url: '/api/v1/onboarding/profile', method: 'PUT', body: { persona: 'educator', material: 'idea' } }]);
    expect(answer).toEqual({ starterTemplateKey: 'video-derush-edit', saved: true });
    expect(result.current.profile).toEqual(stored);
    expect(result.current.answeredWelcome).toBe(true);
  });

  it('never blocks on a failed save: this browser remembers the answer and the contract names the recipe', async () => {
    onboardingBody = { ...serverState(), profile: blank };
    profileStatus = 500;
    const { result } = renderHook(() => useOnboarding(), { wrapper });
    await waitFor(() => expect(result.current.settled).toBe(true));
    let answer: Awaited<ReturnType<typeof result.current.answerWelcome>> | undefined;
    await act(async () => {
      answer = await result.current.answerWelcome('software', 'code');
    });
    expect(answer).toEqual({ starterTemplateKey: 'quick-win-promo', saved: false });
    expect(result.current.answeredWelcome).toBe(true);
    expect(result.current.mirror.welcome).toBe('answered');
  });

  it('records a skip once, and remembers it before the request returns', async () => {
    onboardingBody = { ...serverState(), profile: blank };
    profileBody = { profile: { ...blank, skippedAt: '2026-11-11T10:00:00Z' }, starterTemplateKey: null };
    const { result } = renderHook(() => useOnboarding(), { wrapper });
    await waitFor(() => expect(result.current.settled).toBe(true));
    let pending: Promise<void> | undefined;
    act(() => {
      pending = result.current.skipWelcome();
    });
    expect(result.current.mirror.welcome).toBe('skipped');
    await act(async () => {
      await pending;
    });
    expect(profilePuts().map((c) => c.body)).toEqual([{ skipped: true }]);
    expect(result.current.profile?.skippedAt).toBe('2026-11-11T10:00:00Z');
    expect(result.current.answeredWelcome).toBe(false);
  });

  it('does not record a skip for someone who already answered', async () => {
    onboardingBody = { ...serverState(), profile: { ...blank, persona: 'creator', material: 'footage', answeredAt: '2026-11-11T10:00:00Z' } };
    const { result } = renderHook(() => useOnboarding(), { wrapper });
    await waitFor(() => expect(result.current.settled).toBe(true));
    await act(async () => {
      await result.current.skipWelcome();
    });
    expect(profilePuts()).toEqual([]);
    expect(result.current.mirror.welcome).toBeUndefined();
  });

  it('marks the welcome questions as offered without overwriting what happened after', async () => {
    const { result } = renderHook(() => useOnboarding(), { wrapper });
    await waitFor(() => expect(result.current.settled).toBe(true));
    act(() => result.current.markWelcomeOffered());
    expect(result.current.mirror.welcome).toBe('offered');
    await act(async () => {
      await result.current.skipWelcome();
    });
    act(() => result.current.markWelcomeOffered());
    expect(result.current.mirror.welcome).toBe('skipped');
  });

  it('keeps the known profile when a tour save answers without one', async () => {
    const answered = { ...blank, persona: 'creator', material: 'footage', answeredAt: '2026-11-11T10:00:00Z' };
    onboardingBody = { ...serverState(), profile: answered };
    const { result } = renderHook(() => useOnboarding(), { wrapper });
    await waitFor(() => expect(result.current.settled).toBe(true));
    onboardingBody = serverState({ status: 'skipped', version: TOUR_VERSION });
    act(() => result.current.finishTour('skipped'));
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT' && c.url === '/api/v1/onboarding')).toBe(true));
    await waitFor(() => expect(result.current.server?.tour.status).toBe('skipped'));
    expect(result.current.profile).toEqual(answered);
  });
});
