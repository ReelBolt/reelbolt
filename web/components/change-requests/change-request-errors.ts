import { ApiError } from '@/lib/api/client';
import { describeRevisionError, type RevisionRefusal } from '@/components/change-requests/revision-errors';
import type { CrT } from './change-request-copy';

/** The first `errors[0].message` of a 400 body (`{ errors: [{ path, message }] }`), when it has one. */
function firstServerMessage(body: unknown): string | null {
  if (typeof body !== 'object' || body === null) return null;
  const errors = (body as Record<string, unknown>).errors;
  if (!Array.isArray(errors) || errors.length === 0) return null;
  const first: unknown = errors[0];
  if (typeof first === 'string' && first.trim()) return first;
  if (typeof first === 'object' && first !== null) {
    const m = (first as Record<string, unknown>).message;
    if (typeof m === 'string' && m.trim()) return m;
  }
  return null;
}

/** Refusal alert for a change request, reusing the revision wording and adding the step-specific 409s. */
export function describeChangeRequestError(error: unknown, t: CrT): RevisionRefusal {
  if (error instanceof ApiError) {
    const code = error.code;
    if (error.status === 409 && code === 'STEP_NOT_COMPLETED') {
      return { title: t('refusals.stepNotCompleted.title'), message: error.message || t('refusals.stepNotCompleted.message'), color: 'orange', topUp: false };
    }
    if (error.status === 409 && code === 'USE_COMPOSITION_REVISION') {
      return { title: t('refusals.useComposition.title'), message: error.message || t('refusals.useComposition.message'), color: 'orange', topUp: false };
    }
    if (code === 'PLAN_NOT_ENTITLED') {
      return { title: t('refusals.notEntitled.title'), message: error.message || t('refusals.notEntitled.message'), color: 'yellow', topUp: false };
    }
    if (error.status === 400) {
      const first = firstServerMessage(error.body) ?? error.errors[0];
      if (first) return { title: t('refusals.checkRequest'), message: first, color: 'orange', topUp: false };
    }
  }
  return describeRevisionError(error);
}
