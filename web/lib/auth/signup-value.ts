// Which copy the signup page's value panel shows (components/auth/SignupValuePanel.tsx).
//
// The panel speaks to the audience the visitor's link named (`?for=<persona>`,
// docs/design/new-user-funnel.md section 2). Each audience has an outcome line and a qualitative
// before/after; a link with no persona, an unknown one, or `other` gets the general story.
//
// The strings are catalogue keys (absolute, `auth.value.…`), resolved with the root translator.
// They are written out one by one, not built from the persona token, so the unused-key test sees
// every one of them. The wording itself follows the messaging brief: no invented numbers, no
// customers, no quotes. A "before" line is a description of a situation, never a testimonial.

import type { Persona } from '@/lib/attribution/persona';

/** A persona with its own copy, or `general`. */
export type ValueAudience = Exclude<Persona, 'other'> | 'general';

export interface ValueCopyKeys {
  outcome: string;
  before: string;
  after: string;
}

export const VALUE_COPY: Readonly<Record<ValueAudience, ValueCopyKeys>> = {
  general: {
    outcome: 'auth.value.general.outcome',
    before: 'auth.value.general.before',
    after: 'auth.value.general.after',
  },
  creator: {
    outcome: 'auth.value.creator.outcome',
    before: 'auth.value.creator.before',
    after: 'auth.value.creator.after',
  },
  business: {
    outcome: 'auth.value.business.outcome',
    before: 'auth.value.business.before',
    after: 'auth.value.business.after',
  },
  educator: {
    outcome: 'auth.value.educator.outcome',
    before: 'auth.value.educator.before',
    after: 'auth.value.educator.after',
  },
  nonprofit: {
    outcome: 'auth.value.nonprofit.outcome',
    before: 'auth.value.nonprofit.before',
    after: 'auth.value.nonprofit.after',
  },
  software: {
    outcome: 'auth.value.software.outcome',
    before: 'auth.value.software.before',
    after: 'auth.value.software.after',
  },
  agency: {
    outcome: 'auth.value.agency.outcome',
    before: 'auth.value.agency.before',
    after: 'auth.value.agency.after',
  },
};

/** The audience whose copy the panel shows for a (possibly absent) persona. */
export function valueAudience(persona: Persona | undefined): ValueAudience {
  return persona && persona !== 'other' ? persona : 'general';
}
