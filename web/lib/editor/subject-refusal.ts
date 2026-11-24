/**
 * Why the server refused a picture operation that was to show the project's avatars or objects
 * (docs/design/avatar-objects.md "Avatars and objects in the picture editor"), sorted into the few states the
 * editor words itself. Pure: it reads the error's status and body only.
 */

export type SubjectRefusalKind = 'consent' | 'noPictures' | 'notUsable' | 'unsupported' | 'plan';

export interface SubjectRefusal {
  kind: SubjectRefusalKind;
  /** `notUsable`: the one rule's reason (`consent_revoked`, `reference_missing`, …), when the body names it. */
  reason?: string;
  /** `unsupported`: the models that can be shown avatars and objects, when the body lists them. */
  models?: string[];
  /** `plan`: the part of the plan that is missing. */
  feature?: 'avatars' | 'avatar_objects';
}

const text = (value: unknown): string | undefined => (typeof value === 'string' && value.trim() ? value.trim() : undefined);

/**
 * The refusal, or null when the error is not about avatars or objects (the editor then handles it as it always
 * did). `sentSubjects` says whether the request carried `subjectIds`: only then is `OPERATION_UNSUPPORTED` about
 * them (without, it means the tool has no AI service at all) and only then is a plan refusal read here.
 */
export function describeSubjectRefusal(cause: unknown, sentSubjects: boolean): SubjectRefusal | null {
  const e = cause as { status?: unknown; body?: unknown } | null;
  const body = typeof e?.body === 'object' && e.body !== null ? (e.body as Record<string, unknown>) : null;
  if (!body) return null;
  const code = text(body.code) ?? text(body.error) ?? '';
  if (code === 'CONSENT_REQUIRED') return { kind: 'consent' };
  if (code === 'AVATAR_CONSENT_NO_PICTURES') return { kind: 'noPictures' };
  if (code === 'AVATAR_NOT_USABLE') {
    const reason = text(body.reason);
    return { kind: 'notUsable', ...(reason ? { reason } : {}) };
  }
  if (!sentSubjects) return null;
  if (code === 'OPERATION_UNSUPPORTED') {
    const models = Array.isArray(body.models) ? body.models.filter((m): m is string => typeof m === 'string' && m.trim().length > 0) : [];
    return { kind: 'unsupported', ...(models.length > 0 ? { models } : {}) };
  }
  if (code === 'PLAN_NOT_ENTITLED') {
    const feature = body.feature === 'avatars' || body.feature === 'avatar_objects' ? body.feature : undefined;
    return { kind: 'plan', ...(feature ? { feature } : {}) };
  }
  return null;
}
