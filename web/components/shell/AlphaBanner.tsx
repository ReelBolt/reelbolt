'use client';

import { useSyncExternalStore } from 'react';
import { Alert, Text } from '@mantine/core';
import classes from './chrome.module.css';
import { IconInfoCircle } from '@tabler/icons-react';
import { PRODUCT_VERSION } from '@/lib/version';
import { useT } from '@/i18n';

// The alpha notice sentence lives in the `shell` catalogue (`shell.alpha.notice`); the version comes
// from `web/lib/version.ts`, so the sentence and the version drift together or not at all.

/**
 * `localStorage` key base, exported so the pre-paint boot script in `app/layout.tsx` and the tests
 * can name the same store. The dismissal is device-wide, not session-scoped: a visitor who closes
 * the notice does not see it again on the next visit.
 */
export const ALPHA_DISMISSED_KEY = 'reelbolt.alpha-banner.dismissed';

/**
 * The `<html>` attribute the boot script sets before the first paint. `app/layout.tsx` hides
 * `[data-alpha-banner]` while it is `1`, so a returning visitor's first paint never shows the bar
 * and then hides it once hydration reads `localStorage` — no flash, no hydration mismatch.
 */
export const ALPHA_DISMISSED_ATTR = 'data-alpha-dismissed';

/**
 * One notice version, one key: a new `PRODUCT_VERSION` re-shows the notice exactly once. The base
 * key stays the shipped one, so an older unversioned dismissal simply stops matching.
 */
export function alphaDismissalKey(version: string): string {
  return `${ALPHA_DISMISSED_KEY}:${version}`;
}

/**
 * The dismissal flag lives in `localStorage`, which is an external store: read it through
 * `useSyncExternalStore` rather than copying it into state in an effect, so there is no setState
 * during render/effect. The server snapshot is "not dismissed" — a browser cannot know storage —
 * and the boot script's attribute keeps that server-rendered bar hidden until the client snapshot
 * corrects it, so the mismatch never shows and never warns.
 */
const listeners = new Set<() => void>();

// One module-level copy so a remount (client-side navigation) does not re-read `localStorage` on
// every render; `localStorage` stays the real store, so a full reload still finds the dismissal.
let cached: boolean | null = null;

function load(): boolean {
  try {
    return window.localStorage.getItem(alphaDismissalKey(PRODUCT_VERSION)) === '1';
  } catch {
    // Storage refused (private mode, a blocked embed): the notice stays up, which is the safe
    // direction for a notice.
    return false;
  }
}

function readDismissed(): boolean {
  if (cached === null) cached = load();
  return cached;
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

function dismiss(): void {
  cached = true;
  try {
    window.localStorage.setItem(alphaDismissalKey(PRODUCT_VERSION), '1');
  } catch {
    // The dismissal still applies for this page; only its persistence is lost.
  }
  try {
    // Keep the boot script's marker in step, so a client-side remount is hidden even before React
    // re-renders this component away.
    document.documentElement.setAttribute(ALPHA_DISMISSED_ATTR, '1');
  } catch {
    // No DOM to mark (a non-browser render).
  }
  for (const listener of [...listeners]) listener();
}

/** Test-only: forget the module-level copy (`localStorage` remains the real store). */
export function __resetAlphaBannerForTests(): void {
  cached = null;
  listeners.clear();
}

/**
 * The dashboard-wide "this is an alpha" bar.
 *
 * It is mounted once in the authenticated shell (`AppShell`) and once in the auth layout, so every
 * page behind `/app` carries it — signed in or not. The notice is the whole affordance: the
 * feedback channel is a separate ticket (T-019) and deliberately does not live here, so the two do
 * not fight over the same file.
 */
export function AlphaBanner() {
  const t = useT('shell.alpha');
  const dismissed = useSyncExternalStore(subscribe, readDismissed, () => false);

  if (dismissed) return null;

  return (
    <Alert
      icon={<IconInfoCircle size={16} />}
      color="yellow"
      variant="light"
      mb="md"
      role="status"
      data-testid="alpha-banner"
      data-alpha-banner
      className={classes.alpha}
      withCloseButton
      closeButtonLabel={t('dismiss')}
      onClose={dismiss}
    >
      <Text size="sm">{t('notice', { version: PRODUCT_VERSION })}</Text>
    </Alert>
  );
}
