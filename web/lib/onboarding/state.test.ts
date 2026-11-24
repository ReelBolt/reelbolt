// @vitest-environment jsdom
import { beforeEach, describe, expect, it } from 'vitest';
import {
  TOUR_VERSION,
  effectiveTour,
  parseMirror,
  parseProfile,
  parseProfileResult,
  parseServerState,
  readMirrorRaw,
  shouldOfferTour,
  updateMirror,
  type OnboardingServerState,
} from './state';

const server = (status: 'pending' | 'completed' | 'skipped', version: number | null, updatedAt: string | null = null): OnboardingServerState => ({
  tour: { status, version, updatedAt },
  checklist: { dismissed: false, items: [] },
});

beforeEach(() => window.localStorage.clear());

describe('parseServerState', () => {
  it('reads the contract shape and drops unknown items', () => {
    const parsed = parseServerState({
      tour: { status: 'completed', version: 1, updatedAt: '2026-11-01T00:00:00Z' },
      checklist: { dismissed: true, items: [{ key: 'create_project', done: true }, { key: 'nope', done: true }] },
    });
    expect(parsed?.tour.status).toBe('completed');
    expect(parsed?.checklist.dismissed).toBe(true);
    expect(parsed?.checklist.items).toEqual([{ key: 'create_project', done: true }]);
  });

  it('treats anything malformed as no answer', () => {
    expect(parseServerState(null)).toBeNull();
    expect(parseServerState({})).toBeNull();
    expect(parseServerState({ tour: { status: 'weird' } })).toBeNull();
  });
});

describe('the welcome profile', () => {
  const tour = { status: 'pending', version: null, updatedAt: null };

  it('reads the profile the contract adds to GET /api/v1/onboarding', () => {
    const parsed = parseServerState({
      tour,
      checklist: { dismissed: false, items: [] },
      profile: { persona: 'creator', material: 'footage', suggestedPersona: 'creator', pickedPlan: null, answeredAt: '2026-11-11T10:00:00Z', skippedAt: null },
    });
    expect(parsed?.profile).toEqual({
      persona: 'creator', material: 'footage', suggestedPersona: 'creator', pickedPlan: null, answeredAt: '2026-11-11T10:00:00Z', skippedAt: null,
    });
  });

  it('reads an all-null profile as a profile (nothing answered yet), not as no profile', () => {
    const parsed = parseServerState({
      tour,
      profile: { persona: null, material: null, suggestedPersona: null, pickedPlan: null, answeredAt: null, skippedAt: null },
    });
    expect(parsed?.profile).toEqual({ persona: null, material: null, suggestedPersona: null, pickedPlan: null, answeredAt: null, skippedAt: null });
  });

  it('reads an API without the profile as no profile', () => {
    expect(parseServerState({ tour })?.profile).toBeNull();
    expect(parseServerState({ tour, profile: 'creator' })?.profile).toBeNull();
  });

  it('reads an explicit null profile as an empty one: the API knows the profile and has nothing stored', () => {
    expect(parseServerState({ tour, profile: null })?.profile).toEqual({
      persona: null, material: null, suggestedPersona: null, pickedPlan: null, answeredAt: null, skippedAt: null,
    });
  });

  it('drops tokens outside the closed sets instead of trusting them', () => {
    expect(parseProfile({ persona: 'astronaut', material: 'photos', suggestedPersona: 'CREATOR', answeredAt: 7, skippedAt: '' })).toEqual({
      persona: null, material: null, suggestedPersona: null, pickedPlan: null, answeredAt: null, skippedAt: null,
    });
  });

  it('reads the answer of PUT /api/v1/onboarding/profile', () => {
    expect(
      parseProfileResult({
        profile: { persona: 'business', material: 'idea', suggestedPersona: null, pickedPlan: null, answeredAt: '2026-11-11T10:00:00Z', skippedAt: null },
        starterTemplateKey: 'video-derush-edit',
      }),
    ).toEqual({
      profile: { persona: 'business', material: 'idea', suggestedPersona: null, pickedPlan: null, answeredAt: '2026-11-11T10:00:00Z', skippedAt: null },
      starterTemplateKey: 'video-derush-edit',
    });
    expect(parseProfileResult({ profile: { skippedAt: '2026-11-11T10:00:00Z' } })?.starterTemplateKey).toBeNull();
    expect(parseProfileResult({ starterTemplateKey: 'video-derush-edit' })).toBeNull();
    expect(parseProfileResult(null)).toBeNull();
  });

  it('remembers in the mirror how far the welcome questions got, and nothing unknown', () => {
    updateMirror('u1', (m) => ({ ...m, welcome: 'offered' }));
    expect(parseMirror(readMirrorRaw('u1')).welcome).toBe('offered');
    expect(parseMirror(JSON.stringify({ welcome: 'maybe' })).welcome).toBeUndefined();
  });
});

describe('tour decision', () => {
  it('offers a pending tour and not a finished one', () => {
    expect(shouldOfferTour(server('pending', null), {})).toBe(true);
    expect(shouldOfferTour(server('completed', TOUR_VERSION), {})).toBe(false);
    expect(shouldOfferTour(server('skipped', TOUR_VERSION), {})).toBe(false);
  });

  it('offers it again when the tour version is bumped', () => {
    expect(shouldOfferTour(server('completed', TOUR_VERSION - 1), {})).toBe(true);
  });

  it('lets the local mirror win until the server has something newer', () => {
    const mirror = { tour: { status: 'skipped' as const, version: TOUR_VERSION, at: 2000 } };
    expect(effectiveTour(server('pending', null), mirror).status).toBe('skipped');
    expect(effectiveTour(null, mirror).status).toBe('skipped');
    expect(effectiveTour(server('pending', TOUR_VERSION, new Date(5000).toISOString()), mirror).status).toBe('pending');
  });
});

describe('mirror', () => {
  it('round-trips per user and ignores corrupt storage', () => {
    updateMirror('u1', (m) => ({ ...m, checklistDismissed: true, done: ['open_editor'] }));
    expect(parseMirror(readMirrorRaw('u1'))).toMatchObject({ checklistDismissed: true, done: ['open_editor'] });
    expect(parseMirror(readMirrorRaw('u2'))).toEqual({});
    expect(parseMirror('{not json')).toEqual({});
  });
});
