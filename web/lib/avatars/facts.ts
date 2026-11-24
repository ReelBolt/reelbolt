import type { AvatarIdentityFacts, SubjectFidelityStatus, UpdateAvatarRequest } from '../types/avatars';

/** The fields of an object's identity facts, in the order they are shown. */
export const FACT_FIELDS = ['colour', 'makeModel', 'marks', 'scale'] as const;
export type FactField = (typeof FACT_FIELDS)[number];

/** The server keeps at most this many characters of each fact. */
export const FACT_MAX = 120;

export type FactDraft = Record<FactField, string>;

/** The form's starting text: an unknown fact is an empty box. */
export function draftOf(facts: AvatarIdentityFacts | null | undefined): FactDraft {
  return {
    colour: facts?.colour ?? '', makeModel: facts?.makeModel ?? '', marks: facts?.marks ?? '', scale: facts?.scale ?? '',
  };
}

/** Only the fields the owner changed, as the `identityFacts` of a PATCH (an emptied box clears the fact); null when nothing changed. */
export function factsPatch(
  draft: FactDraft, facts: AvatarIdentityFacts | null | undefined,
): NonNullable<UpdateAvatarRequest['identityFacts']> | null {
  const before = draftOf(facts);
  const out: NonNullable<UpdateAvatarRequest['identityFacts']> = {};
  let changed = false;
  for (const field of FACT_FIELDS) {
    const next = draft[field].trim();
    if (next !== before[field].trim()) { out[field] = next; changed = true; }
  }
  return changed ? out : null;
}

/** Whether any fact is known. */
export function hasFacts(facts: AvatarIdentityFacts | null | undefined): boolean {
  return FACT_FIELDS.some((f) => Boolean(facts?.[f]));
}

/** The take-check verdicts that are worth telling the owner about; the others say nothing. */
export function fidelityBadge(status: SubjectFidelityStatus | string | null | undefined): 'mismatch' | 'unavailable' | null {
  return status === 'mismatch' || status === 'unavailable' ? status : null;
}

/** The verdict of a step result's output JSON (`subjectFidelity.status`), null when absent or unreadable. */
export function readSubjectFidelity(outputJson: string | null | undefined): string | null {
  if (!outputJson || !outputJson.includes('subjectFidelity')) return null;
  try {
    const parsed: unknown = JSON.parse(outputJson);
    if (typeof parsed !== 'object' || parsed === null) return null;
    const node = (parsed as { subjectFidelity?: unknown }).subjectFidelity;
    if (typeof node !== 'object' || node === null) return null;
    const status = (node as { status?: unknown }).status;
    return typeof status === 'string' ? status : null;
  } catch {
    return null;
  }
}
