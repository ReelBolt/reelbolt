// Browser glue for the in-memory landing (lib/attribution-landing.ts): reads the current document's
// URL and referrer into module memory, once. It reads nothing from the device's storage and writes
// nothing to it, so it is safe before any cookie choice; what is done with the landing afterwards
// (cookie, beacon) is still gated on consent in AttributionTracker.
import { splitLocalePath } from '@/i18n/config';
import type { Attribution } from '@/lib/attribution';
import { landingMemory } from '@/lib/attribution-landing';

/**
 * The landing of this document, remembered on the first call and returned unchanged ever after,
 * however far the visitor has navigated client-side since. `null` on the server.
 */
export function browserLanding(): Attribution | null {
  if (typeof window === 'undefined') return null;
  const existing = landingMemory.landing();
  if (existing) return existing;
  const { pathname } = window.location;
  const clean = pathname.length > 1 ? pathname.replace(/\/+$/, '') : pathname;
  return landingMemory.remember({
    search: window.location.search,
    referrer: document.referrer,
    pathname,
    basePath: splitLocalePath(clean).path,
    ownHost: window.location.hostname,
  });
}
