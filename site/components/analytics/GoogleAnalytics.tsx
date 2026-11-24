'use client';

import { useEffect, useRef, type ReactNode } from 'react';
import { useCookieConsent } from '@/components/consent/CookieConsentProvider';
import { readStoredConsent } from '@/lib/consent';
import {
  GA_CONSENT_GRANTED,
  GA_CONSENT_REVOKED,
  disableFlagName,
  gaInitScript,
  installHitGuard,
  isMeasurementId,
} from '@/lib/ga-consent';

const INIT_SCRIPT_ID = 'ga4-init';

// Puts the init script, then gtag.js, in the document. The init script goes first so the consent
// default is already in the queue when gtag.js starts reading it. Idempotent: React strict mode and
// repeated choices never add the tag twice.
function loadTag(gaId: string): void {
  if (document.getElementById(INIT_SCRIPT_ID)) return;
  installHitGuard(window as unknown as Parameters<typeof installHitGuard>[0], gaId);
  const init = document.createElement('script');
  init.id = INIT_SCRIPT_ID;
  init.textContent = gaInitScript(gaId);
  document.head.appendChild(init);
  const lib = document.createElement('script');
  lib.async = true;
  lib.src = `https://www.googletagmanager.com/gtag/js?id=${encodeURIComponent(gaId)}`;
  document.head.appendChild(lib);
}

// Google Consent Mode v2, with the Google tag present on every page for a visitor who has not
// chosen, so Google can detect it.
//
//  - Undecided (or the choice just reopened from the footer): the tag loads with all storage
//    denied (see lib/ga-consent.ts). It writes no cookie and sends only cookieless pings.
//  - Accepted: `analytics_storage` is updated to granted. Ads purposes are never granted.
//  - Rejected, stored from an earlier visit: the tag is not loaded at all, so nothing goes to
//    Google. Accepting later on the same page loads it then.
//  - Rejected on this page, or withdrawn after accepting: Google's `ga-disable-<ID>` flag stops
//    every later event from this page, `installHitGuard` drops a hit gtag.js had already queued
//    (the trailing engagement hit it keeps for a granted visit), and consent goes back to denied.
//    Accepting again clears the flag.
//
// No measurement ID, or one that is not a GA4 ID, means no tag.
export function GoogleAnalytics({ gaId, children }: { gaId?: string; children?: ReactNode }) {
  const { consent } = useCookieConsent();
  // The last consent state pushed to gtag after mount. A returning accepter's grant is queued by
  // the init script itself, before `config`; the first render's `null` (the server snapshot,
  // before localStorage is read) must not be mistaken for a withdrawal.
  const pushed = useRef<'granted' | 'denied' | null>(null);
  // True once this page has seen a rejection (stored or just made) and no acceptance since: the
  // tag must not be loaded, even if the visitor reopens the banner without choosing.
  const rejected = useRef(false);

  useEffect(() => {
    if (!isMeasurementId(gaId)) return;
    // `consent` is the server snapshot (`null`) on the first render; localStorage is the truth.
    const choice = consent ?? readStoredConsent();
    if (choice === 'denied') rejected.current = true;
    if (choice === 'granted') rejected.current = false;

    if (!rejected.current) loadTag(gaId);

    const flags = window as unknown as Record<string, unknown>;
    if (choice === 'granted') {
      flags[disableFlagName(gaId)] = false;
    } else if (choice === 'denied' || pushed.current !== null) {
      // A rejection, or the banner reopened after a choice made on this page.
      flags[disableFlagName(gaId)] = true;
    }

    // `null` after a choice is the footer's "Cookie preferences": the choice is withdrawn, so
    // storage goes back to denied. `null` with nothing pushed is just the first render.
    const target = choice ?? (pushed.current ? 'denied' : null);
    if (target === null || target === pushed.current) return;
    pushed.current = target;
    window.gtag?.('consent', 'update', target === 'granted' ? GA_CONSENT_GRANTED : GA_CONSENT_REVOKED);
  }, [consent, gaId]);

  return children ?? null;
}
