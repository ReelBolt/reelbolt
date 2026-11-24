import { ApiError } from '@/lib/api/client';
import { isNetworkFailure } from '@/lib/api/network-error';
import type { DashboardOverview } from '@/lib/types/dashboard';
import type { Project } from '@/lib/types/project';

/** The recipe the guided first film sets up (the highlight edit app). */
export const FIRST_FILM_RECIPE_KEY = 'video-derush-edit';
/** The project the guided path creates, and reuses the next time. */
export const FIRST_FILM_PROJECT_NAME = 'My first film';
/** Query parameter on the app page carrying the sample clip's file id (the initial footage choice). */
export const FIRST_FILM_SAMPLE_PARAM = 'sample';
export const FIRST_FILM_DISMISS_KEY = 'rb.firstFilm.dismissed';

/**
 * The "Make your first film" offer is for someone with no run yet: shown only once the dashboard has
 * loaded and neither a finished nor a running execution exists, and not after it was dismissed.
 */
export function shouldOfferFirstFilm(overview: DashboardOverview | undefined, dismissed: boolean): boolean {
  if (!overview || dismissed) return false;
  return overview.recentExecutions.length + overview.activeExecutions.length === 0;
}

export interface FirstFilmDeps {
  createProject: (data: { name: string; description: string }) => Promise<{ id: string }>;
  addSample: (projectId: string) => Promise<{ id: string }>;
  applyTemplate: (projectId: string, key: string, skipIfExists: boolean) => Promise<{ id: string }>;
}

/**
 * Creates (or reuses) the first-film project, adds the sample clip, makes sure the footage app exists
 * and returns the app's page with the sample preselected. Nothing runs: the person presses Run there. The project's only clip is
 * the sample, which the footage slot picks up on its own.
 */
export async function startFirstFilm(
  deps: FirstFilmDeps,
  opts: {
    projects?: Pick<Project, 'id' | 'name'>[];
    projectId?: string;
    description?: string;
    /** Holds the project this flow created, so a retry after a later step failed reuses it. */
    created?: { id?: string };
  } = {},
): Promise<string> {
  const createAndRemember = async () => {
    const id = (await deps.createProject({ name: FIRST_FILM_PROJECT_NAME, description: opts.description ?? '' })).id;
    if (opts.created) opts.created.id = id;
    return id;
  };
  const projectId =
    opts.projectId
    ?? opts.created?.id
    ?? opts.projects?.find((p) => p.name === FIRST_FILM_PROJECT_NAME)?.id
    ?? (await createAndRemember());
  const sample = await deps.addSample(projectId);
  const workflow = await deps.applyTemplate(projectId, FIRST_FILM_RECIPE_KEY, true);
  // view=app: a person who last chose Advanced still lands on the app page; sample: only this clip is chosen.
  return `/projects/${projectId}/workflows/${workflow.id}?view=app&${FIRST_FILM_SAMPLE_PARAM}=${encodeURIComponent(sample.id)}`;
}

/**
 * The footage slot's initial choice when the page was opened from the first-film path: just the sample
 * clip, if it is among the project's files; otherwise null (the slot picks as it always does).
 */
export function sampleFootageChoice(sampleId: string | null | undefined, files: { id: string }[] | undefined): string[] | null {
  return sampleId && files?.some((f) => f.id === sampleId) ? [sampleId] : null;
}

export type FirstFilmErrorKind = 'unverified' | 'tooLarge' | 'network' | 'generic';

/** Which sentence a failed first-film start gets (the raw API message is never shown). */
export function firstFilmErrorKind(err: unknown): FirstFilmErrorKind {
  if (err instanceof ApiError) {
    if (err.code === 'EMAIL_NOT_VERIFIED') return 'unverified';
    if (err.status === 413) return 'tooLarge';
  }
  return isNetworkFailure(err) ? 'network' : 'generic';
}
