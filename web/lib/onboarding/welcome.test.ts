import { describe, expect, it } from 'vitest';
import type { OnboardingMirror, OnboardingProfile, OnboardingServerState } from './state';
import {
  WELCOME_PATH,
  answeredWelcome,
  firstProjectHref,
  isWelcomeCandidate,
  projectFilesHref,
  shotListHref,
  starterTemplateKeyFor,
  welcomeDecision,
  type WelcomeDecisionInput,
} from './welcome';

const blank: OnboardingProfile = { persona: null, material: null, suggestedPersona: null, pickedPlan: null, answeredAt: null, skippedAt: null };

const server = (
  profile: OnboardingProfile | null = blank,
  tour: OnboardingServerState['tour'] = { status: 'pending', version: null, updatedAt: null },
): OnboardingServerState => ({ tour, checklist: { dismissed: false, items: [] }, profile });

/** A brand-new account on the dashboard: flow on, state loaded, nothing answered, no project. */
const fresh = (over: Partial<WelcomeDecisionInput> = {}): WelcomeDecisionInput => ({
  enabled: true,
  settled: true,
  server: server(),
  mirror: {},
  projects: 0,
  teams: 0,
  ...over,
});

describe('welcomeDecision (the one redirect rule)', () => {
  it('sends a brand-new account with no project to the welcome questions', () => {
    expect(welcomeDecision(fresh())).toBe('redirect');
  });

  it('never redirects with the flag off, whatever else is true', () => {
    expect(welcomeDecision(fresh({ enabled: false }))).toBe('stay');
    expect(welcomeDecision(fresh({ enabled: false, settled: false, projects: 'loading', teams: 'loading' }))).toBe('stay');
  });

  it('waits (no redirect, no tour) until the onboarding state and the projects are known', () => {
    expect(welcomeDecision(fresh({ settled: false, server: null }))).toBe('wait');
    expect(welcomeDecision(fresh({ projects: 'loading' }))).toBe('wait');
    expect(welcomeDecision(fresh({ teams: 'loading' }))).toBe('wait');
  });

  // An account set up from a team invitation opens in its own Personal workspace, which is empty.
  // Counting projects alone sent that person through "what are you here to make" and into a
  // personal first project, when they came to join a team.
  it('never redirects a member of a team, even though their Personal workspace has no project', () => {
    expect(welcomeDecision(fresh({ teams: 1 }))).toBe('stay');
    expect(welcomeDecision(fresh({ teams: 3, projects: 0 }))).toBe('stay');
    // Known to be in a team: no need to wait for the project list.
    expect(welcomeDecision(fresh({ teams: 1, projects: 'loading' }))).toBe('stay');
    // And a workspace list that failed to load is never guessed at.
    expect(welcomeDecision(fresh({ teams: 'error' }))).toBe('stay');
  });

  it('never redirects a workspace that already has projects (an invited member, an existing account)', () => {
    expect(welcomeDecision(fresh({ projects: 1 }))).toBe('stay');
    expect(welcomeDecision(fresh({ projects: 12 }))).toBe('stay');
  });

  it('never redirects on a guess: no API answer, an API without the profile, or a failed project list', () => {
    expect(welcomeDecision(fresh({ server: null }))).toBe('stay');
    expect(welcomeDecision(fresh({ server: server(null) }))).toBe('stay');
    const olderApi: OnboardingServerState = { tour: { status: 'pending', version: null, updatedAt: null }, checklist: { dismissed: false, items: [] } };
    expect(welcomeDecision(fresh({ server: olderApi }))).toBe('stay');
    expect(welcomeDecision(fresh({ projects: 'error' }))).toBe('stay');
  });

  it('never redirects someone who answered or skipped the questions', () => {
    expect(welcomeDecision(fresh({ server: server({ ...blank, persona: 'creator', material: 'footage', answeredAt: '2026-11-11T10:00:00Z' }) }))).toBe('stay');
    expect(welcomeDecision(fresh({ server: server({ ...blank, skippedAt: '2026-11-11T10:00:00Z' }) }))).toBe('stay');
  });

  it('keeps a suggested persona a candidate: a suggestion is not an answer', () => {
    expect(welcomeDecision(fresh({ server: server({ ...blank, suggestedPersona: 'educator' }) }))).toBe('redirect');
  });

  it('never loops: once this browser sent the person there, it does not send them again', () => {
    for (const welcome of ['offered', 'answered', 'skipped'] as const) {
      expect(welcomeDecision(fresh({ mirror: { welcome } }))).toBe('stay');
    }
  });

  it('never redirects someone whose tour is not pending (they are not new)', () => {
    expect(welcomeDecision(fresh({ server: server(blank, { status: 'completed', version: 1, updatedAt: null }) }))).toBe('stay');
    expect(welcomeDecision(fresh({ server: server(blank, { status: 'skipped', version: 1, updatedAt: null }) }))).toBe('stay');
    const mirror: OnboardingMirror = { tour: { status: 'skipped', version: 1, at: Date.now() } };
    expect(welcomeDecision(fresh({ mirror }))).toBe('stay');
  });

  it('does not make a browser that already knows the person wait for the API', () => {
    const known: OnboardingMirror = { tour: { status: 'completed', version: 1, at: 1 } };
    expect(welcomeDecision(fresh({ settled: false, server: null, mirror: known, projects: 'loading' }))).toBe('stay');
    expect(welcomeDecision(fresh({ settled: false, server: null, mirror: { welcome: 'offered' }, projects: 'loading' }))).toBe('stay');
  });
});

describe('isWelcomeCandidate', () => {
  it('is the same rule before the projects are counted', () => {
    expect(isWelcomeCandidate(fresh())).toBe(true);
    expect(isWelcomeCandidate(fresh({ enabled: false }))).toBe(false);
    expect(isWelcomeCandidate(fresh({ settled: false }))).toBe(false);
    expect(isWelcomeCandidate(fresh({ server: server(null) }))).toBe(false);
    expect(isWelcomeCandidate(fresh({ mirror: { welcome: 'offered' } }))).toBe(false);
  });
});

describe('answeredWelcome (the tour suppression rule)', () => {
  it('is true once the API says the questions were answered', () => {
    expect(answeredWelcome(server({ ...blank, answeredAt: '2026-11-11T10:00:00Z' }), {})).toBe(true);
  });

  it('is true when this browser saw the answer, even if the save failed', () => {
    expect(answeredWelcome(null, { welcome: 'answered' })).toBe(true);
    expect(answeredWelcome(server(), { welcome: 'answered' })).toBe(true);
  });

  it('is false for a skip, an offer, a suggestion, or no profile at all (the tour opens as before)', () => {
    expect(answeredWelcome(server({ ...blank, skippedAt: '2026-11-11T10:00:00Z' }), {})).toBe(false);
    expect(answeredWelcome(server({ ...blank, suggestedPersona: 'creator' }), {})).toBe(false);
    expect(answeredWelcome(server(), { welcome: 'skipped' })).toBe(false);
    expect(answeredWelcome(server(), { welcome: 'offered' })).toBe(false);
    expect(answeredWelcome(server(null), {})).toBe(false);
    expect(answeredWelcome(null, {})).toBe(false);
  });
});

describe('the first project', () => {
  it('maps a material to the starter recipe the contract fixes', () => {
    expect(starterTemplateKeyFor('footage')).toBe('edit-my-video');
    expect(starterTemplateKeyFor('idea')).toBe('animation-from-idea');
    expect(starterTemplateKeyFor('code')).toBe('quick-win-promo');
  });

  it('sends clips and a filmed idea to the Files tab drop zone', () => {
    expect(firstProjectHref('footage', 'p1', { repositoryAvailable: true })).toBe('/projects/p1?tab=files');
    expect(firstProjectHref('idea', 'p1', { repositoryAvailable: true })).toBe('/projects/p1?tab=files');
    expect(projectFilesHref('p1')).toBe('/projects/p1?tab=files');
  });

  it('sends code to the Repository tab when one can be connected, else to the Files tab (zip upload)', () => {
    expect(firstProjectHref('code', 'p1', { repositoryAvailable: true })).toBe('/projects/p1?tab=repository');
    expect(firstProjectHref('code', 'p1', { repositoryAvailable: false })).toBe('/projects/p1?tab=files');
  });

  it('links the shot list of a project inside the welcome route', () => {
    expect(WELCOME_PATH).toBe('/welcome');
    expect(shotListHref('2f0d5b7c-1a34-4c2e-9f10-8b6a1d2e3f40')).toBe('/welcome?step=shots&project=2f0d5b7c-1a34-4c2e-9f10-8b6a1d2e3f40');
  });
});
