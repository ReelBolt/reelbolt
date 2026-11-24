// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor } from '@testing-library/react';
import { SWRConfig } from 'swr';
import OnboardingTour from './OnboardingTour';
import { GettingStartedCard } from './GettingStartedCard';
import { renderWithI18n } from '@/test/i18n';
import { guideHrefFor } from '@/lib/onboarding/guide-links';
import { onboardingCopy } from '@/lib/onboarding/copy';
import { TOUR_STEPS } from './tour-steps';
import en from '../../messages/en/onboarding.json';
import type { DashboardOverview } from '@/lib/types/dashboard';
import type { Project } from '@/lib/types/project';

vi.mock('next/link', () => ({
  default: ({ children, href, className }: { children: React.ReactNode; href: string; className?: string }) => (
    <a href={href} className={className}>{children}</a>
  ),
}));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ user: { email: 'ada@example.com' } }) }));

beforeEach(() => {
  window.localStorage.clear();
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  Element.prototype.scrollIntoView = vi.fn();
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      const body = url.includes('onboarding')
        ? { tour: { status: 'completed', version: 1, updatedAt: null }, checklist: { dismissed: false, items: [] } }
        : { firstVideoGrant: { remaining: 1 } };
      return new Response(JSON.stringify(body), { status: 200, headers: { 'content-type': 'application/json' } });
    }),
  );
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const project = { id: 'p1', name: 'Promo' } as Project;
const overview = {
  projects: [], activeExecutions: [], needsAttention: [], recentOutputs: [], recentExecutions: [],
  totals: { projects: 1, sourceFiles: 0, renders: 0, passedLast7Days: 0, failedLast7Days: 0 },
} as DashboardOverview;

describe('onboarding catalogue', () => {
  it('English catalogue equals the English reference copy', () => {
    const c = onboardingCopy;
    expect(en.tour.steps.welcome.title).toBe(c.tour.steps.welcome.title);
    for (const s of TOUR_STEPS) {
      const key = s.id === 'new-project' ? 'newProject' : s.id;
      expect((en.tour.steps[key] as { title: string; body: string }).title).toBe(s.title);
      expect((en.tour.steps[key] as { title: string; body: string }).body).toBe(s.body);
    }
    expect(en.tour.steps.appsOff.body).toBe(c.tour.appsOff.body);
    expect(en.tour.next).toBe(c.tour.next);
    expect(en.checklist.items).toEqual(c.checklist.items);
    expect(en.checklist.assistantPrompt).toBe(c.checklist.assistantPrompt);
    expect(en.checklist.howLabel).toBe('How: {label} (opens in a new tab)');
    expect(en.menu.takeTheTour).toBe(c.menu.takeTheTour);
  });

  it('builds locale-aware guide links', () => {
    expect(guideHrefFor('editor-basics', 'en')).toBe('/docs/basics/editor-basics');
    expect(guideHrefFor('editor-basics', 'it')).toBe('/docs/it/basics/editor-basics');
    expect(guideHrefFor('index', 'it')).toBe('/docs/it/basics/');
  });
});

describe.each(['it', 'en-XA'])('onboarding in %s', (locale) => {
  it('the tour keeps an accessible name on every control', () => {
    renderWithI18n(<OnboardingTour onClose={vi.fn()} />, { locale });
    const dialog = screen.getByRole('dialog');
    expect(dialog.getAttribute('aria-labelledby')).toBeTruthy();
    expect(screen.getByRole('heading').textContent?.trim()).not.toBe('');
    for (const el of [...screen.getAllByRole('button'), ...screen.getAllByRole('link')]) {
      expect((el.getAttribute('aria-label') ?? el.textContent ?? '').trim()).not.toBe('');
      expect(el.textContent).not.toMatch(/⟦/);
    }
    const link = screen.getByRole('link') as HTMLAnchorElement;
    if (locale === 'it') expect(link.getAttribute('href')).toMatch(/^\/docs\/it\/basics\//);
    expect(dialog.textContent).not.toMatch(/Step \d of \d|Welcome to ReelBolt/);
  });

  it('the checklist keeps an accessible name on every control', async () => {
    renderWithI18n(
      <SWRConfig value={{ provider: () => new Map() }}>
        <GettingStartedCard projects={[project]} overview={overview} onAskAssistant={vi.fn()} />
      </SWRConfig>,
      { locale },
    );
    await waitFor(() => expect(screen.getByTestId('getting-started')).toBeTruthy());
    const card = screen.getByTestId('getting-started');
    expect(card.getAttribute('aria-label')).toBeTruthy();
    const controls = [...screen.getAllByRole('button'), ...screen.getAllByRole('link')];
    expect(controls.length).toBeGreaterThan(6);
    for (const el of controls) {
      expect((el.getAttribute('aria-label') ?? el.textContent ?? '').trim()).not.toBe('');
      expect(card.textContent).not.toMatch(/⟦/);
    }
  });
});
