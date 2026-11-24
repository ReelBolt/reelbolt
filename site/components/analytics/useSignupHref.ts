'use client';

import { useSyncExternalStore } from 'react';
import { landingMemory } from '@/lib/attribution-landing';
import type { LandingPersona } from '@/lib/attribution-persona';
import { decorateCarryHref, decorateEntryHref } from '@/lib/attribution-signup-link';
import { browserLanding } from './landing';

/**
 * The href a link to the signup page (or the sign-in page) should have in the browser: the given
 * one plus the audience (`?for=`), the campaign parameters of the landing (`utm_*`) and, next to a
 * campaign parameter, the path of the page the visit began on (`lp=`), when there is anything to
 * carry.
 *
 *  - The server render and the hydration pass return `href` untouched (the site is a static
 *    export, so the HTML cannot know the visitor's link); the decorated value arrives right after.
 *  - `persona` is the audience of the page or section the link sits in. The link the visitor
 *    arrived through wins over it while it holds; without either, the link carries the audience
 *    page the visitor last opened (`LandingMemory.personaFor`).
 *  - Any href that is not one of those two app pages comes back unchanged, so a call site does not
 *    need to know which state the "Start free" switch is in.
 *
 * Nothing is stored: the values come from memory and travel in the URL the visitor clicks.
 */
export function useSignupHref(href: string, persona?: LandingPersona): string {
  return useSyncExternalStore(
    landingMemory.subscribe,
    () => {
      const landing = browserLanding();
      return decorateEntryHref(href, { persona: landingMemory.personaFor(persona), landing });
    },
    () => href,
  );
}

/**
 * The href of a link to another page of THIS site that the browser opens as a new document: the
 * language switcher, or a link from a translated page to an English-only one (the two are
 * different root layouts, so the navigation is a full load). The landing lives in memory only and
 * a new document starts empty, so the link carries the audience and the campaign parameters in its
 * URL, exactly as a signup link does, and the next document reads them back as its own landing.
 * Nothing is stored. A link that stays inside one layout must not use this: client-side navigation
 * keeps the memory, and its URLs stay clean. With a campaign the link also names the page the
 * visit began on (`lp=`), which the next document could not know by itself.
 */
export function useCarryHref(href: string): string {
  return useSyncExternalStore(
    landingMemory.subscribe,
    () => {
      const landing = browserLanding();
      return decorateCarryHref(href, { persona: landingMemory.persona(), landing });
    },
    () => href,
  );
}
