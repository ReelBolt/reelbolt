/**
 * Onboarding state: shapes, the closed vocabularies shared with the API (`/api/v1/onboarding`), and
 * the per-user localStorage mirror that remembers a tour's end instantly and when the API answers
 * 404 or is unreachable. Nothing here throws: a blocked storage simply remembers nothing.
 */
import { PERSONAS, type Persona } from '../attribution/persona';
import { CHECKLIST_ITEM_GUIDES } from './guide-links';

/** Bump to offer the tour again to everyone who finished or skipped an older one. */
export const TOUR_VERSION = 1;

export const ONBOARDING_URL = '/api/v1/onboarding';
export const ONBOARDING_EVENTS_URL = '/api/v1/onboarding/events';
export const ONBOARDING_PROFILE_URL = '/api/v1/onboarding/profile';

export type TourStatus = 'pending' | 'completed' | 'skipped';

/** Items in the order the card shows them (the API's order). */
export const CHECKLIST_KEYS = [
  'create_project',
  'upload_file',
  'run_video',
  'open_result',
  'try_assistant',
  'open_editor',
] as const satisfies readonly (keyof typeof CHECKLIST_ITEM_GUIDES)[];

export type ChecklistKey = (typeof CHECKLIST_KEYS)[number];

/** The two items only the browser can report. */
export type ChecklistMark = 'open_result' | 'open_editor';

export type OnboardingEventName =
  | 'tour_started'
  | 'tour_completed'
  | 'tour_skipped'
  | 'checklist_dismissed'
  | 'checklist_item_done';

/** What a person starts their first video from (docs/design/new-user-funnel.md section 4). Closed. */
export const MATERIALS = ['footage', 'code', 'idea'] as const;
export type Material = (typeof MATERIALS)[number];

/**
 * The two welcome answers as the API stores them. `suggestedPersona` comes from the landing page
 * the person signed up from; `answeredAt` / `skippedAt` say which way the welcome questions ended.
 */
export interface OnboardingProfile {
  persona: Persona | null;
  material: Material | null;
  suggestedPersona: Persona | null;
  /** The plan the person picked before signing up (server copy, any browser); null when none. */
  pickedPlan: string | null;
  answeredAt: string | null;
  skippedAt: string | null;
}

export interface OnboardingServerState {
  tour: { status: TourStatus; version: number | null; updatedAt: string | null };
  checklist: { dismissed: boolean; items: { key: ChecklistKey; done: boolean }[] };
  /**
   * `null` (or absent) when the API did not send one: an API older than the welcome flow. The
   * welcome flow never acts on a missing profile: no profile, no redirect.
   */
  profile?: OnboardingProfile | null;
}

/** `PUT /api/v1/onboarding/profile`: the two answers, or the skip. */
export type OnboardingProfileAnswer = { persona: Persona; material: Material } | { skipped: true };

/** What `PUT /api/v1/onboarding/profile` answers. */
export interface OnboardingProfileResult {
  profile: OnboardingProfile;
  /** The recipe the first project starts with; passed on to `POST /api/v1/projects`. */
  starterTemplateKey: string | null;
}

export interface OnboardingPatch {
  tour?: { status: TourStatus; version: number };
  checklistDismissed?: boolean;
  marks?: ChecklistMark[];
}

export function isChecklistKey(value: unknown): value is ChecklistKey {
  return typeof value === 'string' && (CHECKLIST_KEYS as readonly string[]).includes(value);
}

function isTourStatus(value: unknown): value is TourStatus {
  return value === 'pending' || value === 'completed' || value === 'skipped';
}

/** Nothing suggested, nothing answered, nothing skipped. */
const EMPTY_PROFILE: OnboardingProfile = { persona: null, material: null, suggestedPersona: null, pickedPlan: null, answeredAt: null, skippedAt: null };

export function isPersona(value: unknown): value is Persona {
  return typeof value === 'string' && (PERSONAS as readonly string[]).includes(value);
}

export function isMaterial(value: unknown): value is Material {
  return typeof value === 'string' && (MATERIALS as readonly string[]).includes(value);
}

/**
 * Reads a `profile` object defensively. Not an object is `null` (the API sent none); an unknown
 * token inside it is dropped to `null` rather than trusted.
 */
export function parseProfile(body: unknown): OnboardingProfile | null {
  if (typeof body !== 'object' || body === null) return null;
  const p = body as Record<string, unknown>;
  const stamp = (v: unknown) => (typeof v === 'string' && v ? v : null);
  return {
    persona: isPersona(p.persona) ? p.persona : null,
    material: isMaterial(p.material) ? p.material : null,
    suggestedPersona: isPersona(p.suggestedPersona) ? p.suggestedPersona : null,
    pickedPlan: typeof p.pickedPlan === 'string' && p.pickedPlan ? p.pickedPlan : null,
    answeredAt: stamp(p.answeredAt),
    skippedAt: stamp(p.skippedAt),
  };
}

/** Reads the body of `PUT /api/v1/onboarding/profile`; `null` when it carries no profile. */
export function parseProfileResult(body: unknown): OnboardingProfileResult | null {
  if (typeof body !== 'object' || body === null) return null;
  const b = body as { profile?: unknown; starterTemplateKey?: unknown };
  const profile = parseProfile(b.profile);
  if (!profile) return null;
  return {
    profile,
    starterTemplateKey: typeof b.starterTemplateKey === 'string' && b.starterTemplateKey ? b.starterTemplateKey : null,
  };
}

/** Reads an API body defensively; anything unexpected is `null` (as if the API had not answered). */
export function parseServerState(body: unknown): OnboardingServerState | null {
  if (typeof body !== 'object' || body === null) return null;
  const { tour, checklist, profile } = body as { tour?: unknown; checklist?: unknown; profile?: unknown };
  if (typeof tour !== 'object' || tour === null) return null;
  const t = tour as Record<string, unknown>;
  if (!isTourStatus(t.status)) return null;
  const c = (typeof checklist === 'object' && checklist !== null ? checklist : {}) as Record<string, unknown>;
  const items = Array.isArray(c.items)
    ? c.items.flatMap((item): { key: ChecklistKey; done: boolean }[] => {
        const i = item as { key?: unknown; done?: unknown } | null;
        return i && isChecklistKey(i.key) ? [{ key: i.key, done: i.done === true }] : [];
      })
    : [];
  return {
    tour: {
      status: t.status,
      version: typeof t.version === 'number' ? t.version : null,
      updatedAt: typeof t.updatedAt === 'string' ? t.updatedAt : null,
    },
    checklist: { dismissed: c.dismissed === true, items },
    // An explicit `profile: null` comes from an API that knows the welcome profile and has nothing
    // stored for this person: an empty profile. A missing key is an API without it: no profile.
    profile: profile === null ? EMPTY_PROFILE : parseProfile(profile),
  };
}

// ---------------------------------------------------------------------------------------------
// The per-user mirror
// ---------------------------------------------------------------------------------------------

export interface OnboardingMirror {
  tour?: { status: TourStatus; version: number; at: number };
  checklistDismissed?: boolean;
  /** Items the browser has seen done (sticky), including the two marks. */
  done?: ChecklistKey[];
  /** Items whose `checklist_item_done` event was already sent. */
  evented?: ChecklistKey[];
  /**
   * How far the welcome questions got in this browser: `offered` (sent to them once, so never sent
   * again), `answered` or `skipped`. Remembered here so a failed save cannot send anyone back.
   */
  welcome?: WelcomeMark;
  /**
   * The first project the welcome flow created from this browser, and the recipe it started with.
   * A reload of the flow, or a second pass through the questions, opens this project again rather
   * than making another "My first video".
   */
  welcomeProject?: { id: string; starter: string };
}

export type WelcomeMark = 'offered' | 'answered' | 'skipped';

function isWelcomeMark(value: unknown): value is WelcomeMark {
  return value === 'offered' || value === 'answered' || value === 'skipped';
}

const KEY_PREFIX = 'rb-onboarding:v1:';
const listeners = new Set<() => void>();

export function mirrorStorageKey(userKey: string): string {
  return `${KEY_PREFIX}${userKey}`;
}

/** The raw stored string (stable between calls, as `useSyncExternalStore` needs), or `''`. */
export function readMirrorRaw(userKey: string): string {
  if (typeof window === 'undefined' || !userKey) return '';
  try {
    return window.localStorage.getItem(mirrorStorageKey(userKey)) ?? '';
  } catch {
    return '';
  }
}

export function parseMirror(raw: string): OnboardingMirror {
  if (!raw) return {};
  try {
    const v = JSON.parse(raw) as Record<string, unknown>;
    const out: OnboardingMirror = {};
    const t = v.tour as { status?: unknown; version?: unknown; at?: unknown } | undefined;
    if (t && isTourStatus(t.status) && typeof t.version === 'number') {
      out.tour = { status: t.status, version: t.version, at: typeof t.at === 'number' ? t.at : 0 };
    }
    if (v.checklistDismissed === true) out.checklistDismissed = true;
    if (Array.isArray(v.done)) out.done = v.done.filter(isChecklistKey);
    if (Array.isArray(v.evented)) out.evented = v.evented.filter(isChecklistKey);
    if (isWelcomeMark(v.welcome)) out.welcome = v.welcome;
    const wp = v.welcomeProject as { id?: unknown; starter?: unknown } | undefined;
    if (wp && typeof wp.id === 'string' && wp.id && typeof wp.starter === 'string') out.welcomeProject = { id: wp.id, starter: wp.starter };
    return out;
  } catch {
    return {};
  }
}

export function updateMirror(userKey: string, change: (current: OnboardingMirror) => OnboardingMirror): void {
  if (typeof window === 'undefined' || !userKey) return;
  try {
    const next = change(parseMirror(readMirrorRaw(userKey)));
    window.localStorage.setItem(mirrorStorageKey(userKey), JSON.stringify(next));
  } catch {
    // Storage blocked or full: the server copy (when there is one) still remembers.
  }
  listeners.forEach((l) => l());
}

/**
 * Tells this browser that the welcome questions do not apply to this person, before they first
 * reach the dashboard: someone who has just set up an account from a team invitation came to join
 * that team, not to start a personal first project. Uses the mirror's own "already offered" mark,
 * which is what stops the redirect; an existing mark is left as it is.
 */
export function markWelcomeNotApplicable(userKey: string): void {
  updateMirror(userKey, (m) => (m.welcome ? m : { ...m, welcome: 'offered' }));
}

export function subscribeMirror(callback: () => void): () => void {
  if (typeof window === 'undefined') return () => {};
  listeners.add(callback);
  window.addEventListener('storage', callback);
  return () => {
    listeners.delete(callback);
    window.removeEventListener('storage', callback);
  };
}

/**
 * Should the tour be offered on its own? Pending always; finished or skipped only when it was an
 * older tour version. The mirror wins over the server unless the server's copy is newer.
 */
export function shouldOfferTour(server: OnboardingServerState | null, mirror: OnboardingMirror): boolean {
  const status = effectiveTour(server, mirror);
  return status.status === 'pending' || (status.version ?? 0) < TOUR_VERSION;
}

export function effectiveTour(
  server: OnboardingServerState | null,
  mirror: OnboardingMirror,
): { status: TourStatus; version: number | null } {
  const m = mirror.tour;
  const s = server?.tour;
  if (m && !(s && s.updatedAt && Date.parse(s.updatedAt) > m.at)) return { status: m.status, version: m.version };
  if (s) return { status: s.status, version: s.version };
  return { status: 'pending', version: null };
}
