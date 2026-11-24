/**
 * Help for the one box a first-time person stalls on: "who is it for and what should it show".
 * Three example briefs per audience and recipe family, in that audience's own terms, and a one-line
 * prompt above them. The examples are things a person might type, never claims about a result; a
 * click puts one in the box, where it is edited like anything typed.
 *
 * The audience is the closed persona set (docs/design/new-user-funnel.md section 4). Someone whose
 * audience is not known gets the neutral examples (`other`).
 */
import { PERSONAS, type Persona } from '@/lib/attribution/persona';
import type { OnboardingProfile } from '@/lib/onboarding/state';
import { msg, type Msg } from '@/i18n/msg';

/** The recipe families the examples are written for: an edit of the person's clips, or a promo of their app. */
export type BriefFamily = 'footage' | 'promo';

export const EXAMPLES_PER_FAMILY = 3;

/** The family an app's category belongs to; `null` for an app the examples were not written for. */
export function briefFamilyOf(category: string | null | undefined): BriefFamily | null {
  return category === 'footage' || category === 'promo' ? category : null;
}

/**
 * Who the examples speak to: the audience the person chose in the welcome questions, else the one
 * their landing page suggested, else nobody in particular.
 */
export function briefPersona(profile: Pick<OnboardingProfile, 'persona' | 'suggestedPersona'> | null | undefined): Persona {
  const token = profile?.persona ?? profile?.suggestedPersona ?? null;
  return token && (PERSONAS as readonly string[]).includes(token) ? token : 'other';
}

/** The one-line prompt above the box, in the audience's terms. */
export function briefPrompt(family: BriefFamily, persona: Persona): Msg {
  return family === 'promo' ? msg('apps.run.briefHelp.promptPromo', { persona }) : msg('apps.run.briefHelp.promptFootage', { persona });
}

/** The three example briefs for a family and an audience. */
export function briefExamples(family: BriefFamily, persona: Persona): Msg[] {
  return family === 'promo'
    ? [
        msg('apps.run.briefHelp.promo1', { persona }),
        msg('apps.run.briefHelp.promo2', { persona }),
        msg('apps.run.briefHelp.promo3', { persona }),
      ]
    : [
        msg('apps.run.briefHelp.footage1', { persona }),
        msg('apps.run.briefHelp.footage2', { persona }),
        msg('apps.run.briefHelp.footage3', { persona }),
      ];
}

/**
 * What the box holds after an example is chosen. An empty box, or one that still holds an untouched
 * example, takes the new example; anything the person wrote is kept and the example goes on a new
 * line after it. Nothing typed is ever replaced.
 */
export function insertExample(current: string, example: string, examples: readonly string[]): string {
  const text = current.trim();
  if (text === '' || examples.includes(text)) return example;
  if (text.includes(example)) return current;
  return `${current.replace(/\s+$/, '')}\n${example}`;
}
