'use client';

import { createContext, useContext } from 'react';
import type { StepData } from '@/components/workflows/WorkflowStepList';
import type { AppBindableField, AppManifest, AppMediaEgress } from '@/lib/types/workflow';

/**
 * The app page as the builder drafts it (docs/design/app-inputs.md 13.1, 24). Provided by the workflow page only while
 * the web apps switch is on: without a provider every builder surface
 * (the App inputs panel's editor, the step drawer's "App fields", the condition's field picker) renders as before.
 */
export interface AppBuilderValue {
  /** The draft manifest (the effective one until the author changes something). */
  manifest: AppManifest;
  setManifest: (next: AppManifest) => void;
  /** The draft steps, so a removed field is unbound where it was used. */
  steps: StepData[];
  setSteps: (next: StepData[]) => void;
  /** Settings an app field may fill, per step type. */
  bindableFields: Record<string, AppBindableField[]>;
  /** Whether the stored manifest is the author's (else it is derived from the steps). */
  hasExplicitManifest: boolean;
  /** The app's own permission to send media, when a step sends something: listed locked, never part of the draft. */
  mediaEgress?: AppMediaEgress | null;
}

export const AppBuilderContext = createContext<AppBuilderValue | null>(null);

/** The app-page draft, or null when app inputs are off (the builder is then exactly as before). */
export function useAppBuilder(): AppBuilderValue | null {
  return useContext(AppBuilderContext);
}
