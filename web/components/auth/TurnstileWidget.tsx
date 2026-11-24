'use client';

import { useEffect, useRef } from 'react';

/**
 * The part of Cloudflare's Turnstile explicit-render API this app uses.
 * https://developers.cloudflare.com/turnstile/get-started/client-side-rendering/
 */
interface TurnstileApi {
  render: (
    container: HTMLElement,
    options: {
      sitekey: string;
      callback: (token: string) => void;
      'expired-callback': () => void;
      'error-callback': () => void;
    },
  ) => string;
  remove: (widgetId: string) => void;
}

declare global {
  interface Window {
    turnstile?: TurnstileApi;
  }
}

const SCRIPT_SRC = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit';

// One download for the whole page, shared by every widget and every mount: the script is loaded
// once and the explicit API then renders each container. A failed load clears the memo so a later
// mount can retry rather than being stuck with a rejected promise forever.
let scriptPromise: Promise<void> | null = null;

function loadTurnstileScript(): Promise<void> {
  if (typeof window === 'undefined') return Promise.resolve();
  if (window.turnstile) return Promise.resolve();
  if (scriptPromise) return scriptPromise;
  scriptPromise = new Promise<void>((resolve, reject) => {
    const script = document.createElement('script');
    script.src = SCRIPT_SRC;
    script.async = true;
    script.defer = true;
    script.onload = () => resolve();
    script.onerror = () => {
      scriptPromise = null;
      reject(new Error('turnstile script failed to load'));
    };
    document.head.appendChild(script);
  });
  return scriptPromise;
}

interface TurnstileWidgetProps {
  /** Cloudflare Turnstile site key. An empty key renders nothing and requires no token. */
  siteKey: string;
  /** Called with the token, and with '' when the challenge expires or fails. */
  onToken: (token: string) => void;
}

/**
 * The Cloudflare Turnstile challenge, rendered inline above a submit button.
 *
 * Only the *site* key lives here; the matching secret is server-side in Go (TURNSTILE_SECRET) and
 * never reaches the browser. When the deployment has no site key the widget renders nothing and
 * the caller sends no `turnstileToken` — Go skips the check when it has no secret either, so the
 * two stay in step by both being unset.
 *
 * A verification failure calls `onToken('')`, which leaves the caller with no token and therefore
 * refuses to submit: the challenge fails closed, like the server does.
 */
export function TurnstileWidget({ siteKey, onToken }: TurnstileWidgetProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const onTokenRef = useRef(onToken);

  // The Turnstile callbacks are registered once per widget; keep them pointing at the latest prop
  // without re-rendering the challenge on every parent render.
  useEffect(() => {
    onTokenRef.current = onToken;
  }, [onToken]);

  useEffect(() => {
    if (!siteKey) return;
    let widgetId: string | null = null;
    let cancelled = false;

    loadTurnstileScript()
      .then(() => {
        if (cancelled || !containerRef.current || !window.turnstile) return;
        widgetId = window.turnstile.render(containerRef.current, {
          sitekey: siteKey,
          callback: (token: string) => onTokenRef.current(token),
          'expired-callback': () => onTokenRef.current(''),
          'error-callback': () => onTokenRef.current(''),
        });
      })
      .catch(() => onTokenRef.current(''));

    return () => {
      cancelled = true;
      if (widgetId && window.turnstile) window.turnstile.remove(widgetId);
    };
  }, [siteKey]);

  if (!siteKey) return null;
  return <div ref={containerRef} data-testid="turnstile-widget" />;
}
