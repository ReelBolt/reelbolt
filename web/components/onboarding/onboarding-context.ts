import { createContext, useContext } from 'react';
import type { WelcomeDecision } from '@/lib/onboarding/welcome';

export interface CardUi {
  /** `null` = follow the screen size (collapsed on a phone, open elsewhere). */
  expanded: boolean | null;
  hidden: boolean;
}

export interface OnboardingContextValue {
  /** Opens the tour now ("Take the tour"). Does not touch the saved status until it ends. */
  startTour: () => void;
  /** The shell's feature flags: a gated feature leaves the tour and the checklist. */
  editorEnabled: boolean;
  appsEnabled: boolean;
  /** `WELCOME_FLOW_ENABLED`, as the server resolved it: whether `/welcome` exists to be sent to. */
  welcomeEnabled: boolean;
  /**
   * Whether the person on the dashboard should be at the welcome questions instead (`redirect`),
   * is not known yet (`wait`), or simply stays (`stay`). The dashboard acts on it; see
   * `welcomeDecision` in `lib/onboarding/welcome.ts` for the rule.
   */
  welcome: WelcomeDecision;
  /** Called by the dashboard right before it leaves for `/welcome`: remembered, so it happens once. */
  beginWelcome: () => void;
  /** The card's own display state, kept above the card so moving it in the page keeps it. */
  cardUi: CardUi | null;
  setCardUi: ((patch: Partial<CardUi>) => void) | null;
}

export const OnboardingContext = createContext<OnboardingContextValue>({
  startTour: () => {},
  editorEnabled: true,
  appsEnabled: true,
  welcomeEnabled: true,
  welcome: 'stay',
  beginWelcome: () => {},
  cardUi: null,
  setCardUi: null,
});

export function useOnboardingContext(): OnboardingContextValue {
  return useContext(OnboardingContext);
}
