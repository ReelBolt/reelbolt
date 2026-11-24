/**
 * "Preview 3 s" (R10.4) logic: when it is offered, the price in plain words, the polling delay and the
 * state machine (idle, starting, rendering, ready, failed, refused for credits, limited by the hourly cap).
 * Pure: the store and the components wrap it.
 */

import type { EditTimelineRenderEstimate } from '../sync/api';
import { formatEstimate, creditsMissing, missingSentence } from '../../../components/editor/deliver/deliver-model';
import type { ParityClass } from './parity';

export const PREVIEW_SECONDS = 3;

/** Offered only where the preview cannot show the real result: render-required, or an approximate feature. */
export function offersPreview3s(parity: ParityClass | null | undefined): boolean {
  return parity === 'renderRequired' || parity === 'approximate';
}

export type PriceState =
  | { kind: 'unknown' }
  | { kind: 'loading' }
  | { kind: 'ready'; label: string; affordable: boolean }
  | { kind: 'refused'; reason: string };

/** "Included" when billing is off, "About 2 credits" otherwise, and why not when the balance is short. */
export function priceFromEstimate(estimate: EditTimelineRenderEstimate): PriceState {
  if (!estimate.billingEnabled) return { kind: 'ready', label: 'Included', affordable: true };
  if (!estimate.affordable) {
    const missing = missingSentence(creditsMissing(estimate));
    return { kind: 'refused', reason: missing ? `${missing} for this preview.` : (estimate.unavailableReason ?? 'This preview is not available right now.') };
  }
  if ((estimate.credits ?? 0) <= 0) return { kind: 'ready', label: 'Included', affordable: true };
  const label = formatEstimate({ ...estimate, estimateSeconds: null });
  return { kind: 'ready', label: label ?? 'Included', affordable: true };
}

/** Button text: "Preview 3 s" plus the price when known. */
export function buttonLabel(price: PriceState): string {
  if (price.kind === 'ready') return `Preview 3 s (${price.label.charAt(0).toLowerCase()}${price.label.slice(1)})`;
  return 'Preview 3 s';
}

/** About every 1.5 s, easing to 4 s. */
export function pollDelayMs(attempt: number): number {
  return Math.min(4000, Math.round(1500 * Math.pow(1.25, Math.max(0, attempt))));
}

export type PreviewState =
  | { phase: 'idle' }
  | { phase: 'starting' }
  | { phase: 'rendering'; jobId: string; statusWord: string }
  | { phase: 'ready'; jobId: string }
  | { phase: 'failed'; message: string }
  | { phase: 'refused'; message: string }
  | { phase: 'limited'; message: string };

export type PreviewEvent =
  | { type: 'start' }
  | { type: 'started'; jobId: string }
  | { type: 'status'; status: string; ready: boolean; errorMessage?: string | null }
  | { type: 'error'; status: number | null; code: string | null; message: string }
  | { type: 'close' };

const TERMINAL_BAD = new Set(['Failed', 'Cancelled', 'Canceled', 'TimedOut', 'Skipped']);

export function statusWordFor(status: string): string {
  return status === 'Queued' ? 'Waiting to start' : 'Rendering your preview';
}

export function previewReducer(state: PreviewState, event: PreviewEvent): PreviewState {
  switch (event.type) {
    case 'start':
      return state.phase === 'starting' || state.phase === 'rendering' ? state : { phase: 'starting' };
    case 'started':
      return { phase: 'rendering', jobId: event.jobId, statusWord: statusWordFor('Queued') };
    case 'status': {
      if (state.phase !== 'rendering') return state;
      if (event.ready) return { phase: 'ready', jobId: state.jobId };
      if (TERMINAL_BAD.has(event.status) || event.status === 'Passed') {
        return { phase: 'failed', message: event.errorMessage || 'The preview did not finish. Try again.' };
      }
      return { ...state, statusWord: statusWordFor(event.status) };
    }
    case 'error':
      if (event.status === 402 || event.code === 'INSUFFICIENT_CREDITS') return { phase: 'refused', message: event.message };
      if (event.status === 429 && event.code === 'PREVIEW_RATE_LIMITED') return { phase: 'limited', message: event.message };
      if (event.status === 429) return { phase: 'limited', message: event.message };
      return { phase: 'failed', message: event.message };
    case 'close':
      return { phase: 'idle' };
  }
}
