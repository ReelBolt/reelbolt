'use client';

import { useEffect } from 'react';
import { usePathname } from 'next/navigation';
import { routePath } from '@/lib/telemetry/allowlist';
import { CONSENT_STORAGE_KEY, readStoredConsent } from '@/lib/analytics/consent';

type Gtag = (...args: unknown[]) => void;
type GaWindow = Window & { dataLayer?: unknown[]; gtag?: Gtag } & Record<string, unknown>;

const SCRIPT_ID = 'ga4-gtag-js';

// Routes whose URL can carry a secret (token in query or path) or that are account-flow pages:
// GA is never loaded on them. Matched on the first segment, with an optional leading `app`.
const BLOCKED_FIRST_SEGMENTS: ReadonlySet<string> = new Set([
  'login', 'reset-password', 'verify-email', 'invite', 'accept-terms', 'change-password',
  'marketing', 'unsubscribe', 'forgot-password', 'review',
]);
// Blocked routes that sit below a shared first segment, matched on the first two segments.
// The runner pairing page is `/runners/pair` (its query carries the pairing code).
const BLOCKED_TWO_SEGMENTS: ReadonlySet<string> = new Set(['runners/pair']);

export function isGaBlockedRoute(pathname: string | null | undefined): boolean {
  const parts = (pathname ?? '').split(/[?#]/)[0].split('/').filter(Boolean);
  if (parts[0] === 'app') parts.shift();
  if (parts.length === 0) return false;
  return BLOCKED_FIRST_SEGMENTS.has(parts[0]) || (parts.length > 1 && BLOCKED_TWO_SEGMENTS.has(`${parts[0]}/${parts[1]}`));
}

function safeLocation(path: string): string {
  return `${window.location.origin}${routePath(path) || '/'}`;
}

// NOTE (operator step): Enhanced Measurement (outbound clicks, site search, file downloads,
// video, form interactions) is a GA4 property-side setting that gtag config cannot turn off, and
// those events carry the raw URL. Turn them off for the web data stream (or use a separate stream).

/** Loads gtag.js (once) and configures it with manual, redacted page views. */
function loadGa(id: string): void {
  const w = window as unknown as GaWindow;
  if (document.getElementById(SCRIPT_ID)) return;
  w.dataLayer = w.dataLayer || [];
  w.gtag = function gtag(...args: unknown[]) {
    (w.dataLayer as unknown[]).push(args);
  };
  w[`ga-disable-${id}`] = false;
  w.gtag('js', new Date());
  w.gtag('config', id, {
    send_page_view: false,
    page_location: safeLocation(window.location.pathname),
    page_referrer: '',
    allow_google_signals: false,
    allow_ad_personalization_signals: false,
  });
  const s = document.createElement('script');
  s.id = SCRIPT_ID;
  s.async = true;
  s.src = `https://www.googletagmanager.com/gtag/js?id=${encodeURIComponent(id)}`;
  document.head.appendChild(s);
}

export function sendPageView(path: string): void {
  const w = window as unknown as GaWindow;
  if (!w.gtag) return;
  const redacted = routePath(path) || '/';
  w.gtag('set', { page_location: safeLocation(path), page_referrer: '' });
  w.gtag('event', 'page_view', {
    page_path: redacted,
    page_location: `${window.location.origin}${redacted}`,
    page_title: redacted,
  });
}

export function GoogleAnalytics({ measurementId = process.env.NEXT_PUBLIC_GA_MEASUREMENT_ID }: { measurementId?: string }) {
  const pathname = usePathname();

  // Single source of truth: sending is allowed only with consent 'granted' on a non-blocked
  // route. Once gtag.js has executed it cannot be unloaded, so the Google-documented
  // `ga-disable-<ID>` flag is the off switch (true on withdrawal/null consent or a token route,
  // false again when allowed). Cookies already written by gtag (_ga*) are NOT removed here.
  const sync = (id: string, path: string | null): boolean => {
    const w = window as unknown as GaWindow;
    const allowed =
      readStoredConsent() === 'granted' && !isGaBlockedRoute(path) && !isGaBlockedRoute(window.location.pathname);
    if (allowed) {
      w[`ga-disable-${id}`] = false;
      loadGa(id);
    } else if (w.gtag || document.getElementById(SCRIPT_ID)) {
      w[`ga-disable-${id}`] = true;
    } else if (readStoredConsent() === 'denied') {
      w[`ga-disable-${id}`] = true;
    }
    return allowed;
  };

  // Re-evaluate consent on mount and on a storage event.
  useEffect(() => {
    if (!measurementId) return;
    const onStorage = (e: StorageEvent) => {
      if (e.key === null || e.key === CONSENT_STORAGE_KEY) sync(measurementId, window.location.pathname);
    };
    window.addEventListener('storage', onStorage);
    return () => window.removeEventListener('storage', onStorage);
  }, [measurementId]);

  // On mount and every navigation: re-sync, then send a page view only if allowed.
  useEffect(() => {
    if (!measurementId) return;
    if (sync(measurementId, pathname)) sendPageView(pathname ?? '/');
  }, [measurementId, pathname]);

  return null;
}
