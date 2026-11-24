import { telemetryEnabled } from '../telemetry/config';
import { readTelemetryConsent } from '../telemetry/consent';
import { sendTelemetry } from '../telemetry/client';
import { apiFetch } from './client';
import {
  ONBOARDING_EVENTS_URL,
  ONBOARDING_PROFILE_URL,
  ONBOARDING_URL,
  parseProfileResult,
  parseServerState,
  type ChecklistKey,
  type OnboardingEventName,
  type OnboardingPatch,
  type OnboardingProfileAnswer,
  type OnboardingProfileResult,
  type OnboardingServerState,
} from '../onboarding/state';
import type { ProjectFile } from '../types/project';

/**
 * GET `/api/v1/onboarding`. Onboarding is a nicety: a 404 (API not deployed yet), an offline
 * browser or a malformed body all read as "no server answer" (`null`) and never throw.
 */
export async function getOnboarding(): Promise<OnboardingServerState | null> {
  try {
    const res = await fetch(ONBOARDING_URL, { headers: { 'Content-Type': 'application/json' } });
    if (!res.ok) return null;
    return parseServerState(await res.json());
  } catch {
    return null;
  }
}

/** PUT `/api/v1/onboarding`; answers the stored state, or `null` when it could not be saved. */
export async function putOnboarding(patch: OnboardingPatch): Promise<OnboardingServerState | null> {
  try {
    const res = await fetch(ONBOARDING_URL, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(patch),
    });
    if (!res.ok) return null;
    return parseServerState(await res.json());
  } catch {
    return null;
  }
}

/** DELETE `/api/v1/onboarding/picked-plan`; best effort, answers whether it was cleared. */
export async function deletePickedPlan(): Promise<boolean> {
  try {
    const res = await fetch(`${ONBOARDING_URL}/picked-plan`, { method: 'DELETE' });
    return res.ok;
  } catch {
    return false;
  }
}

/**
 * POST `/api/v1/onboarding/events`, only with the person's telemetry consent and only when
 * telemetry is on. Best effort: nothing about it is ever surfaced.
 */
export async function sendOnboardingEvent(event: OnboardingEventName, item?: ChecklistKey): Promise<void> {
  if (!telemetryEnabled() || readTelemetryConsent() !== 'granted') return;
  await sendTelemetry(ONBOARDING_EVENTS_URL, item ? { event, item } : { event });
}

/**
 * PUT `/api/v1/onboarding/profile`: the two welcome answers (`{persona, material}`) or the skip
 * (`{skipped: true}`). Answers the stored profile and the starter recipe for the first project, or
 * `null` when it could not be saved. Like the rest of onboarding it never throws: a first project
 * must not wait on it. The server writes the `onboarding.profile_*` event; the browser sends none.
 */
export async function putOnboardingProfile(answer: OnboardingProfileAnswer): Promise<OnboardingProfileResult | null> {
  try {
    const res = await fetch(ONBOARDING_PROFILE_URL, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(answer),
    });
    if (!res.ok) return null;
    return parseProfileResult(await res.json());
  } catch {
    return null;
  }
}

/**
 * Imports one sample clip into a project with the ordinary import-from-link API
 * (`POST /api/v1/projects/{id}/files/import-url`). Unlike the calls above this one throws: the
 * caller shows why a clip could not be added (`ApiError.code` is `EMAIL_NOT_VERIFIED` for an
 * address that is not confirmed yet, or one of the `IMPORT_*` codes).
 */
export function importSampleClip(projectId: string, url: string): Promise<ProjectFile> {
  return apiFetch<ProjectFile>(`/api/v1/projects/${encodeURIComponent(projectId)}/files/import-url`, {
    method: 'POST',
    body: JSON.stringify({ url }),
  });
}
