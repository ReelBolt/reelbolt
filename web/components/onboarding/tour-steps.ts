import { msg, type Msg } from '@/i18n/msg';
import { onboardingCopy, type TourStepCopy } from '@/lib/onboarding/copy';
import { TOUR_STEP_GUIDES, type GuideSlug, type TourStepId } from '@/lib/onboarding/guide-links';

/**
 * `title`/`body` are the English reference text (tests assert them); the UI shows `text`, the same
 * words from the `onboarding` catalogue in the active language.
 */
export interface TourStep extends TourStepCopy {
  id: TourStepId;
  text: { title: Msg; body: Msg };
  /** Value of the `data-tour` attribute on the real element to point at; absent = a centred card. */
  anchor?: string;
  guide: GuideSlug;
}

/**
 * The eight stops of the first-run tour. Plain language only: no internal product or engine names,
 * and the editor is always called "Early preview". A step whose anchor is missing or off screen is
 * shown as a centred card, so the text has to read well without the highlight.
 */
export const TOUR_STEPS: readonly TourStep[] = [
  {
    id: 'welcome',
    ...onboardingCopy.tour.steps['welcome'],
    text: { title: msg('onboarding.tour.steps.welcome.title'), body: msg('onboarding.tour.steps.welcome.body') },
    anchor: 'nav',
    guide: TOUR_STEP_GUIDES.welcome,
  },
  {
    id: 'new-project',
    ...onboardingCopy.tour.steps['new-project'],
    text: { title: msg('onboarding.tour.steps.newProject.title'), body: msg('onboarding.tour.steps.newProject.body') },
    anchor: 'new-project',
    guide: TOUR_STEP_GUIDES['new-project'],
  },
  {
    id: 'apps',
    ...onboardingCopy.tour.steps['apps'],
    text: { title: msg('onboarding.tour.steps.apps.title'), body: msg('onboarding.tour.steps.apps.body') },
    anchor: 'nav-projects',
    guide: TOUR_STEP_GUIDES.apps,
  },
  {
    id: 'progress',
    ...onboardingCopy.tour.steps['progress'],
    text: { title: msg('onboarding.tour.steps.progress.title'), body: msg('onboarding.tour.steps.progress.body') },
    guide: TOUR_STEP_GUIDES.progress,
  },
  {
    id: 'assistant',
    ...onboardingCopy.tour.steps['assistant'],
    text: { title: msg('onboarding.tour.steps.assistant.title'), body: msg('onboarding.tour.steps.assistant.body') },
    anchor: 'assistant',
    guide: TOUR_STEP_GUIDES.assistant,
  },
  {
    id: 'editor',
    ...onboardingCopy.tour.steps['editor'],
    text: { title: msg('onboarding.tour.steps.editor.title'), body: msg('onboarding.tour.steps.editor.body') },
    anchor: 'nav-editor',
    guide: TOUR_STEP_GUIDES.editor,
  },
  {
    id: 'pictures',
    ...onboardingCopy.tour.steps['pictures'],
    text: { title: msg('onboarding.tour.steps.pictures.title'), body: msg('onboarding.tour.steps.pictures.body') },
    anchor: 'nav-editor',
    guide: TOUR_STEP_GUIDES.pictures,
  },
  {
    id: 'credits',
    ...onboardingCopy.tour.steps['credits'],
    text: { title: msg('onboarding.tour.steps.credits.title'), body: msg('onboarding.tour.steps.credits.body') },
    anchor: 'account-menu',
    guide: TOUR_STEP_GUIDES.credits,
  },
];

export interface TourFeatures {
  editorEnabled: boolean;
  appsEnabled: boolean;
}

/** The steps this deployment can honestly show: no editor, no editor or pictures step; no Apps tab, "Workflows" wording. */
export function buildTourSteps({ editorEnabled, appsEnabled }: TourFeatures): TourStep[] {
  return TOUR_STEPS.filter((s) => editorEnabled || (s.id !== 'editor' && s.id !== 'pictures')).map((s) =>
    s.id === 'apps' && !appsEnabled ? {
          ...s,
          ...onboardingCopy.tour.appsOff,
          text: { title: msg('onboarding.tour.steps.appsOff.title'), body: msg('onboarding.tour.steps.appsOff.body') },
        } : s,
  );
}
