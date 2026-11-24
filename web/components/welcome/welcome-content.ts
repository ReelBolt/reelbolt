/**
 * The welcome flow's catalogue keys, per audience and per starting material. Non-component data:
 * every entry is a `Msg` the flow resolves with `t.msg(...)`, so the words live only in
 * `messages/<locale>/welcome.json`. The lookups are spelled out (not built from the token) so the
 * catalogue tests can see every key that is used.
 *
 * Honesty rules of the product apply to this copy: the cards carry the audience's own words for
 * the problem, never a customer quote, a count or a time saving; the shot lists ask only for clips
 * anyone can film on a phone.
 */
import { msg, type Msg } from '@/i18n/msg';
import type { Persona } from '@/lib/attribution/persona';
import type { Material } from '@/lib/onboarding/state';

/** One audience card: who it is, then the problem in their own words. */
export const PERSONA_CARDS: Record<Persona, { title: Msg; words: Msg }> = {
  creator: { title: msg('welcome.personas.creator.title'), words: msg('welcome.personas.creator.words') },
  business: { title: msg('welcome.personas.business.title'), words: msg('welcome.personas.business.words') },
  educator: { title: msg('welcome.personas.educator.title'), words: msg('welcome.personas.educator.words') },
  nonprofit: { title: msg('welcome.personas.nonprofit.title'), words: msg('welcome.personas.nonprofit.words') },
  software: { title: msg('welcome.personas.software.title'), words: msg('welcome.personas.software.words') },
  agency: { title: msg('welcome.personas.agency.title'), words: msg('welcome.personas.agency.words') },
  other: { title: msg('welcome.personas.other.title'), words: msg('welcome.personas.other.words') },
};

/** One starting-material card. */
export const MATERIAL_CARDS: Record<Material, { title: Msg; body: Msg }> = {
  footage: { title: msg('welcome.material.footage.title'), body: msg('welcome.material.footage.body') },
  code: { title: msg('welcome.material.code.title'), body: msg('welcome.material.code.body') },
  idea: { title: msg('welcome.material.idea.title'), body: msg('welcome.material.idea.body') },
};

/** How many clips a shot list asks for. */
export const SHOTS_PER_LIST = 5;

/** "Film these five clips on your phone": five concrete prompts per audience, about ten seconds each. */
export const SHOT_LISTS: Record<Persona, readonly [Msg, Msg, Msg, Msg, Msg]> = {
  creator: [
    msg('welcome.shots.lists.creator.one'),
    msg('welcome.shots.lists.creator.two'),
    msg('welcome.shots.lists.creator.three'),
    msg('welcome.shots.lists.creator.four'),
    msg('welcome.shots.lists.creator.five'),
  ],
  business: [
    msg('welcome.shots.lists.business.one'),
    msg('welcome.shots.lists.business.two'),
    msg('welcome.shots.lists.business.three'),
    msg('welcome.shots.lists.business.four'),
    msg('welcome.shots.lists.business.five'),
  ],
  educator: [
    msg('welcome.shots.lists.educator.one'),
    msg('welcome.shots.lists.educator.two'),
    msg('welcome.shots.lists.educator.three'),
    msg('welcome.shots.lists.educator.four'),
    msg('welcome.shots.lists.educator.five'),
  ],
  nonprofit: [
    msg('welcome.shots.lists.nonprofit.one'),
    msg('welcome.shots.lists.nonprofit.two'),
    msg('welcome.shots.lists.nonprofit.three'),
    msg('welcome.shots.lists.nonprofit.four'),
    msg('welcome.shots.lists.nonprofit.five'),
  ],
  software: [
    msg('welcome.shots.lists.software.one'),
    msg('welcome.shots.lists.software.two'),
    msg('welcome.shots.lists.software.three'),
    msg('welcome.shots.lists.software.four'),
    msg('welcome.shots.lists.software.five'),
  ],
  agency: [
    msg('welcome.shots.lists.agency.one'),
    msg('welcome.shots.lists.agency.two'),
    msg('welcome.shots.lists.agency.three'),
    msg('welcome.shots.lists.agency.four'),
    msg('welcome.shots.lists.agency.five'),
  ],
  other: [
    msg('welcome.shots.lists.other.one'),
    msg('welcome.shots.lists.other.two'),
    msg('welcome.shots.lists.other.three'),
    msg('welcome.shots.lists.other.four'),
    msg('welcome.shots.lists.other.five'),
  ],
};
