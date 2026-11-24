/**
 * The ONE place the in-app tour and checklist learn where the user guides live.
 *
 * The slugs are the contract with the docs lane (`/docs/basics/<slug>`, docs/user-guide/basics/).
 * `/docs/...` sits outside the app's `/app` base path, so these are plain absolute hrefs, opened in
 * a new tab by the caller (a tour must not be lost by reading a guide).
 */
export const GUIDE_BASE_PATH = '/docs/basics';

/** Every page of "The basics" the docs publish. A guide that is not in this list does not exist. */
export const GUIDE_SLUGS = [
  'index',
  'sign-up-and-first-look',
  'create-a-project-and-upload',
  'pick-an-app-and-run',
  'follow-progress-and-share',
  'ask-for-changes',
  'editor-basics',
  'assistant-basics',
  'pictures-and-thumbnails',
  'plan-credits-and-billing',
  'connect-a-runner',
] as const;

export type GuideSlug = (typeof GUIDE_SLUGS)[number];

/** `/docs/basics/<slug>`; the index page lives at the folder itself. */
export function guideHref(slug: GuideSlug): string {
  return guideHrefFor(slug, 'en');
}

/**
 * The guide URL in the active language: `/docs/basics/<slug>` for English, `/docs/<locale>/basics/<slug>`
 * for the rest. A page the docs have not translated falls back to English on the docs side.
 */
export function guideHrefFor(slug: GuideSlug, locale: string): string {
  const base = locale === 'en' ? GUIDE_BASE_PATH : `/docs/${locale}/basics`;
  return slug === 'index' ? `${base}/` : `${base}/${slug}`;
}

/** The whole documentation in the active language: `/docs/` for English, `/docs/<locale>/` for the rest. */
export function docsHomeHref(locale: string): string {
  return locale === 'en' ? '/docs/' : `/docs/${locale}/`;
}

/**
 * Any user-guide page in the active language (`path` without leading slash, e.g. `api/webhooks`).
 * Always a plain `<a href>`: `/docs` is outside the dashboard's `/app` base path, so a Next `Link`
 * would send the visitor to `/app/docs/...`.
 */
export function docsPageHref(path: string, locale: string): string {
  const clean = path.replace(/^\/+/, '');
  return `${docsHomeHref(locale)}${clean}`;
}

/** Tour step id -> the guide that teaches it. */
export const TOUR_STEP_GUIDES = {
  welcome: 'sign-up-and-first-look',
  'new-project': 'create-a-project-and-upload',
  apps: 'pick-an-app-and-run',
  progress: 'follow-progress-and-share',
  assistant: 'assistant-basics',
  editor: 'editor-basics',
  pictures: 'pictures-and-thumbnails',
  credits: 'plan-credits-and-billing',
} as const satisfies Record<string, GuideSlug>;

export type TourStepId = keyof typeof TOUR_STEP_GUIDES;

/** Checklist item key -> the guide behind its small "How" link. */
export const CHECKLIST_ITEM_GUIDES = {
  create_project: 'create-a-project-and-upload',
  upload_file: 'create-a-project-and-upload',
  run_video: 'pick-an-app-and-run',
  open_result: 'follow-progress-and-share',
  try_assistant: 'assistant-basics',
  open_editor: 'editor-basics',
} as const satisfies Record<string, GuideSlug>;
