import type { T } from '@/i18n/translator';
import { enT } from '../i18n-default';
import { readableServerMessage } from './describe-error';

/** A browser-level fetch/XHR failure: the request never produced an HTTP response. */
export function isNetworkFailure(err: unknown): boolean {
  if (!(err instanceof Error)) return false;
  const m = err.message.trim();
  // Chromium "Failed to fetch"; Firefox "NetworkError when attempting to fetch resource."; WebKit
  // "Load failed", "The network connection was lost.", "The Internet connection appears to be offline.",
  // "Network request failed". A bare "cancelled" is only a network failure as a TypeError (WebKit's
  // aborted fetch); our own cancelled uploads are not TypeErrors.
  if (/failed to fetch|networkerror|^load failed\.?$|network request failed|^network error$|network connection was lost|internet connection appears to be offline|could not connect to the server|request timed out/i.test(m)) return true;
  return err instanceof TypeError && /^cancell?ed\.?$/i.test(m);
}

export const NETWORK_FAILURE_MESSAGE = enT('errors.generic.network');

/** A sentence for a thrown error: the network case in plain words, otherwise the error's own message or `fallback`. */
export function plainErrorMessage(err: unknown, fallback?: string, t: T = enT): string {
  if (isNetworkFailure(err)) return t('errors.generic.network');
  return readableServerMessage(err) ?? fallback ?? t('errors.generic.tryAgain');
}
