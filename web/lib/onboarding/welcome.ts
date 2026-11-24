/**
 * The welcome flow's rules, in one pure place (docs/design/new-user-funnel.md sections 3 and 4):
 * who is sent to `/welcome`, when the first-run tour steps aside, which recipe a first project
 * starts with and where the person lands afterwards. No React, no storage, no network.
 */
import {
  effectiveTour,
  type Material,
  type OnboardingMirror,
  type OnboardingProfile,
  type OnboardingServerState,
} from './state';

/** The welcome flow's route (inside the dashboard's `/app` base path). */
export const WELCOME_PATH = '/welcome';

/**
 * `wait`: not known yet, so nothing may start (neither the redirect nor the tour).
 * `redirect`: send this person to `/welcome` now.
 * `stay`: the welcome flow does not apply; the dashboard carries on as before.
 */
export type WelcomeDecision = 'wait' | 'redirect' | 'stay';

/** What is known about the active workspace's projects: still loading, failed, or how many. */
export type ProjectsKnowledge = 'loading' | 'error' | number;

/** How many Team workspaces the person belongs to: still loading, failed, or the count. */
export type TeamsKnowledge = 'loading' | 'error' | number;

export interface WelcomeDecisionInput {
  /** `WELCOME_FLOW_ENABLED`, as the server resolved it. */
  enabled: boolean;
  /** The onboarding state has loaded (or failed to). */
  settled: boolean;
  server: OnboardingServerState | null;
  mirror: OnboardingMirror;
  projects: ProjectsKnowledge;
  /**
   * The Team workspaces the person is a member of (their own Personal workspace is not one).
   * Someone who belongs to a team was invited into it or built it: either way they did not arrive
   * as a lone new user, whatever their Personal workspace holds.
   */
  teams: TeamsKnowledge;
}

/**
 * Is this person a candidate for the welcome questions, before their projects are counted?
 *
 * Only when all of these hold: the flow is switched on; the API answered **with a profile** (no
 * answer, or an API without the profile, is never guessed at); the questions were neither answered
 * nor skipped; this browser has not already sent them there once; and the first-run tour is still
 * pending (someone who finished or skipped the tour is not new).
 */
export function isWelcomeCandidate({ enabled, settled, server, mirror }: Omit<WelcomeDecisionInput, 'projects' | 'teams'>): boolean {
  if (!enabled || !settled) return false;
  const profile = server?.profile;
  if (!server || !profile) return false;
  if (profile.answeredAt || profile.skippedAt) return false;
  if (mirror.welcome) return false;
  return effectiveTour(server, mirror).status === 'pending';
}

/**
 * The one redirect rule. A candidate (see {@link isWelcomeCandidate}) whose active workspace has
 * **no project at all** and who belongs to **no team** goes to `/welcome`; anyone else stays.
 *
 * - A workspace that already has projects (an existing account) never redirects.
 * - A member of any Team workspace never redirects. An account set up from a team invitation opens
 *   in the Personal workspace every account gets, which is empty: counting projects alone would
 *   send that person through "what are you here to make" and into a personal first project, when
 *   they came to join a team.
 * - A project list or a workspace list that failed to load never redirects (no guessing).
 *
 * While the onboarding state or either list is still loading for someone who could be a candidate,
 * the answer is `wait`.
 */
export function welcomeDecision(input: WelcomeDecisionInput): WelcomeDecision {
  if (!input.enabled) return 'stay';
  // This browser already knows the person is not new (the questions were offered here, or the tour
  // was finished or skipped here): no need to wait for the API to say so.
  if (input.mirror.welcome || (input.mirror.tour && input.mirror.tour.status !== 'pending')) return 'stay';
  if (!input.settled) return 'wait';
  if (!isWelcomeCandidate(input)) return 'stay';
  if (input.projects === 'error' || input.teams === 'error') return 'stay';
  if (typeof input.projects === 'number' && input.projects > 0) return 'stay';
  if (typeof input.teams === 'number' && input.teams > 0) return 'stay';
  if (input.projects === 'loading' || input.teams === 'loading') return 'wait';
  return 'redirect';
}

/**
 * Someone who answered the welcome questions is on the fast path to a first video: the tour does
 * not start by itself for them (it stays in the Help and account menus and as a quiet card on the
 * dashboard). The browser's own memory of the answer counts too, so a failed save changes nothing.
 */
export function answeredWelcome(server: OnboardingServerState | null, mirror: OnboardingMirror): boolean {
  return Boolean(server?.profile?.answeredAt) || mirror.welcome === 'answered';
}

/**
 * The starter recipe for a material, as the contract fixes it. The API's answer to the profile save
 * is what is normally passed on; this is the same mapping for the moment that answer is missing.
 */
export function starterTemplateKeyFor(material: Material): string {
  return material === 'code' ? 'quick-win-promo' : 'edit-my-video';
}

/** What a project can start from in the plain "New project" form: clips, or an app's code. */
export type StartFrom = Exclude<Material, 'idea'>;

/**
 * What the plain "New project" form starts a project from before the person chooses. Their own
 * answer to "what do you have" when there is one (an idea is filmed, so it starts from clips);
 * else the audience they named or arrived as (a software team starts from code); else clips.
 *
 * Clips is the default for someone nothing is known about: a project used to get the
 * promo-from-code app whatever the person had, which is the wrong first app for everyone who is
 * not a software team. The form shows the choice, so one click changes it.
 */
export function defaultStartFrom(profile: Pick<OnboardingProfile, 'persona' | 'material' | 'suggestedPersona'> | null | undefined): StartFrom {
  if (profile?.material) return profile.material === 'code' ? 'code' : 'footage';
  return (profile?.persona ?? profile?.suggestedPersona) === 'software' ? 'code' : 'footage';
}

/** The project's Files tab: the drop zone. */
export function projectFilesHref(projectId: string): string {
  return `/projects/${encodeURIComponent(projectId)}?tab=files`;
}

/**
 * Where a person adds what they start from. Clips (and an idea, once filmed) go to the Files tab's
 * drop zone. Code goes to the Repository tab when this server can connect a repository, and to the
 * Files tab otherwise, where the code is uploaded as a zip.
 */
export function firstProjectHref(material: Material, projectId: string, options: { repositoryAvailable: boolean }): string {
  if (material === 'code' && options.repositoryAvailable) return `/projects/${encodeURIComponent(projectId)}?tab=repository`;
  return projectFilesHref(projectId);
}

/** The persona shot list for a project that starts from an idea; reachable again from the dashboard. */
export function shotListHref(projectId: string): string {
  return `${WELCOME_PATH}?step=shots&project=${encodeURIComponent(projectId)}`;
}
