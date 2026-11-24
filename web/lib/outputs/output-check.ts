'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { notifications } from '@mantine/notifications';
import { ApiError } from '@/lib/api/client';
import { useT, type T } from '@/i18n';

/** First wait before a video that is still being checked is asked about again. */
export const OUTPUT_CHECK_INTERVAL_MS = 5000;
/** The slowest the re-check ever gets. */
export const OUTPUT_CHECK_MAX_INTERVAL_MS = 60000;
/** Failed re-checks in a row (network error or 5xx) before recovery gives up. */
export const OUTPUT_CHECK_MAX_FAILS = 3;

/** Wait before re-check number `attempt` (0-based): 5, 10, 20, 40, then 60 seconds. */
export function outputCheckDelay(attempt: number): number {
  return Math.min(OUTPUT_CHECK_MAX_INTERVAL_MS, OUTPUT_CHECK_INTERVAL_MS * 2 ** Math.max(0, attempt));
}

/** A re-check answer that says nothing about the output itself (the network or the server hiccuped). */
export function isTransientProbe(result: OutputProbe): boolean {
  return result.kind === 'network' || (result.kind === 'other' && result.status >= 500);
}

/** What one ranged request to an output URL tells us. */
export type OutputProbe =
  | { kind: 'ok' }
  /** Not ready yet: a 202 (current servers) or a 409 `CONTENT_MODERATION_PENDING` (older ones). */
  | { kind: 'pending'; retryAfterMs?: number }
  | { kind: 'unavailable'; message: string }
  | { kind: 'unavailableFinal'; message: string }
  | { kind: 'blocked'; message: string }
  | { kind: 'missing' }
  | { kind: 'other'; message: string; status: number }
  | { kind: 'network' };

/**
 * The server's `Retry-After` in milliseconds (delta-seconds or an HTTP-date), clamped to the normal
 * back-off ceiling. `undefined` when absent or unreadable, so the caller keeps its own back-off.
 */
export function parseRetryAfter(value: string | null): number | undefined {
  if (!value) return undefined;
  const trimmed = value.trim();
  if (!trimmed) return undefined;
  const seconds = Number(trimmed);
  const ms = Number.isFinite(seconds) ? seconds * 1000 : Date.parse(trimmed) - Date.now();
  if (!Number.isFinite(ms)) return undefined;
  return Math.min(OUTPUT_CHECK_MAX_INTERVAL_MS, Math.max(0, ms));
}

/** One ranged request, never a loop: the single place the output endpoint's answers are read. */
export async function probeOutput(src: string): Promise<OutputProbe> {
  let response: Response;
  try {
    response = await fetch(src, { headers: { Range: 'bytes=0-0' }, cache: 'no-store' });
  } catch {
    return { kind: 'network' };
  }
  // 202 is "accepted, not ready yet" (with a Retry-After): a 2xx, but no bytes follow it, so it must
  // be read before `response.ok` or the player and the download guard would serve an empty body.
  if (response.status === 202) {
    return { kind: 'pending', retryAfterMs: parseRetryAfter(response.headers.get('Retry-After')) };
  }
  if (response.ok) return { kind: 'ok' };
  if (response.status === 404) return { kind: 'missing' };
  let body: { code?: unknown; message?: unknown; final?: unknown } | null = null;
  try {
    body = (await response.json()) as { code?: unknown; message?: unknown; final?: unknown };
  } catch {
    body = null;
  }
  if (body?.code === 'CONTENT_MODERATION_PENDING') return { kind: 'pending' };
  const message = typeof body?.message === 'string' ? body.message.trim() : '';
  if (body?.code === 'CONTENT_MODERATION_UNAVAILABLE') {
    // `final: true` marks a refusal that is certain for these bytes: no retry can change it, unlike an outage.
    return body.final === true ? { kind: 'unavailableFinal', message } : { kind: 'unavailable', message };
  }
  if (body?.code === 'CONTENT_BLOCKED' || response.status === 422) return { kind: 'blocked', message };
  return { kind: 'other', message, status: response.status };
}

/**
 * Runs `run`; while the server answers "still being checked" (409 `CONTENT_MODERATION_PENDING`) it calls
 * `onPending` and tries again on the back-off (5 s up to 60 s). Any other failure is thrown at once.
 */
export async function retryWhilePending<T>(
  run: () => Promise<T>,
  opts: { isCancelled: () => boolean; onPending?: () => void },
): Promise<T> {
  for (let attempt = 0; ; attempt += 1) {
    try {
      return await run();
    } catch (error) {
      if (!(error instanceof ApiError) || error.code !== 'CONTENT_MODERATION_PENDING' || opts.isCancelled()) throw error;
      opts.onPending?.();
      await new Promise<void>((resolve) => setTimeout(resolve, outputCheckDelay(attempt)));
      if (opts.isCancelled()) throw error;
    }
  }
}

export type OutputCheckStatus = 'unknown' | 'checking' | 'pending' | 'blocked' | 'unavailable' | 'unavailableFinal' | 'missing' | 'failed';

export interface OutputCheck {
  status: OutputCheckStatus;
  /** Plain words for the current status; null while the output is fine or not yet known. */
  message: string | null;
  /** Changes when a pending output becomes ready: use as the media element's `key` so it loads again. */
  remountKey: number;
  /** Hand to the media element's `onError`. */
  onError: () => void;
  /** One probe on request (a download click); does not change the status. */
  probe: () => Promise<OutputProbe>;
}

/**
 * Tracks whether an output's video can be shown. Starts `unknown` and stays silent until the media
 * element errors; then one probe says why. Only a pending answer (202 with the server's Retry-After,
 * or 409 from an older server) starts a re-check: after the Retry-After when given, otherwise on the
 * 5 s back-off (while the tab is visible); when the output is ready the status resets and `remountKey`
 * changes, so the element loads by itself. Polling stops on unmount, and blocked is terminal.
 */
export function useOutputCheck(url: string | null, opts: { neutral?: boolean } = {}): OutputCheck {
  const t = useT('outputs');
  const [status, setStatus] = useState<OutputCheckStatus>('unknown');
  const [blockedText, setBlockedText] = useState('');
  const [otherText, setOtherText] = useState('');
  const [remountKey, setRemountKey] = useState(0);
  const alive = useRef(true);
  const busy = useRef(false);
  const urlRef = useRef(url);
  // The server's Retry-After for the current pending answer, if it gave one; undefined means use the back-off.
  const retryAfterRef = useRef<number | undefined>(undefined);
  useEffect(() => { urlRef.current = url; }, [url]);

  useEffect(() => {
    alive.current = true;
    return () => { alive.current = false; };
  }, []);

  // A different URL starts over (adjusting state during render, not in an effect).
  const [seenUrl, setSeenUrl] = useState(url);
  if (seenUrl !== url) {
    setSeenUrl(url);
    setStatus('unknown');
  }

  const apply = useCallback((result: OutputProbe, recovering: boolean) => {
    if (!alive.current) return;
    switch (result.kind) {
      case 'ok':
        if (recovering) { setStatus('unknown'); setRemountKey((k) => k + 1); } else setStatus('failed');
        break;
      case 'pending': retryAfterRef.current = result.retryAfterMs; setStatus('pending'); break;
      case 'unavailable': setStatus('unavailable'); break;
      case 'unavailableFinal': setBlockedText(result.message); setStatus('unavailableFinal'); break;
      case 'blocked': setBlockedText(result.message); setStatus('blocked'); break;
      case 'missing': setStatus('missing'); break;
      case 'other': setOtherText(result.message); setStatus('failed'); break;
      default: setStatus('failed');
    }
  }, []);

  const onError = useCallback(() => {
    if (!url || busy.current) return;
    busy.current = true;
    setStatus('checking');
    void probeOutput(url).then((result) => {
      busy.current = false;
      if (urlRef.current === url) apply(result, false);
    });
  }, [url, apply]);

  // Pending re-checks back off (5 s up to 60 s) while the tab is visible; "unavailable" re-checks at the slowest
  // rate so a later verdict shows without a reload. A network error or 5xx does not end recovery until it repeats.
  useEffect(() => {
    if ((status !== 'pending' && status !== 'unavailable') || !url) return;
    let attempt = 0;
    let fails = 0;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const schedule = () => {
      const delay = status === 'unavailable'
        ? OUTPUT_CHECK_MAX_INTERVAL_MS
        : retryAfterRef.current ?? outputCheckDelay(attempt);
      timer = setTimeout(run, delay);
    };
    const run = () => {
      if (document.visibilityState !== 'visible' || busy.current) { schedule(); return; }
      busy.current = true;
      void probeOutput(url).then((result) => {
        busy.current = false;
        if (cancelled || urlRef.current !== url) return;
        if (isTransientProbe(result)) {
          fails += 1;
          if (fails >= OUTPUT_CHECK_MAX_FAILS) { apply(result, true); return; }
          attempt += 1;
          schedule();
          return;
        }
        fails = 0;
        if (result.kind === 'pending') retryAfterRef.current = result.retryAfterMs;
        if (result.kind === status) {
          // The server asked for its own wait: honour it and keep the back-off unadvanced for the fallback.
          if (retryAfterRef.current === undefined) attempt += 1;
          schedule();
          return;
        }
        attempt += 1;
        apply(result, true);
      });
    };
    schedule();
    return () => { cancelled = true; if (timer !== undefined) clearTimeout(timer); };
  }, [status, url, apply]);

  const probe = useCallback(() => (url ? probeOutput(url) : Promise.resolve<OutputProbe>({ kind: 'network' })), [url]);

  let message: string | null = null;
  if (status === 'checking') message = t('player.checking');
  else if (status === 'pending') message = opts.neutral ? t('check.pendingFile') : t('check.pending');
  else if (status === 'unavailable') message = opts.neutral ? t('loadError.moderationFile') : t('loadError.moderation');
  else if (status === 'unavailableFinal') message = blockedText || t('loadError.generic');
  else if (status === 'blocked') message = blockedText || t('check.blocked');
  else if (status === 'missing') message = t('loadError.missing');
  else if (status === 'failed') message = otherText || t('loadError.generic');
  return { status, message, remountKey, onError, probe };
}

/** Plain-words text for a probe result that is not `ok`, for notices raised by a click. */
export function probeNotice(result: OutputProbe, t: T): string {
  switch (result.kind) {
    case 'pending': return t('check.pending');
    case 'unavailable': return t('loadError.moderation');
    case 'unavailableFinal': return result.message || t('loadError.generic');
    case 'blocked': return result.message || t('check.blocked');
    case 'missing': return t('loadError.missing');
    case 'other': return result.message || t('loadError.generic');
    default: return t('loadError.generic');
  }
}

/**
 * Click handler factory for a download link: asks first, so a link never saves or opens an error
 * body. When the output is ready the download goes ahead; otherwise a short notice says why.
 */
export function useDownloadGuard() {
  const t = useT('outputs');
  const inFlight = useRef(new Set<string>());
  return useCallback((url: string, fileName?: string) => async (e: { preventDefault: () => void }) => {
    e.preventDefault();
    if (inFlight.current.has(url)) return;
    inFlight.current.add(url);
    let result: OutputProbe;
    try {
      result = await probeOutput(url);
    } finally {
      inFlight.current.delete(url);
    }
    if (result.kind === 'ok') {
      const a = document.createElement('a');
      a.href = url;
      a.download = fileName ?? '';
      document.body.appendChild(a);
      a.click();
      a.remove();
      return;
    }
    notifications.show({
      title: result.kind === 'pending' ? t('check.cue') : undefined,
      message: probeNotice(result, t),
      color: result.kind === 'pending' ? 'blue' : 'orange',
    });
  }, [t]);
}
