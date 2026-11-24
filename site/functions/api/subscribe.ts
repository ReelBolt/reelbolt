// LEGACY, UNUSED: nothing deploys this Pages Function (reelbolt.ai is the `site` container behind nginx).
// Cloudflare Pages Function serving POST /api/subscribe for the footer newsletter. The docker-compose
// build keeps using app/api/subscribe/route.ts; both call handleFooterSubscribe. Like the contact
// function there is no in-memory rate limit: production needs the Cloudflare WAF rule on /api/subscribe.
// Any per-client key must come from CF-Connecting-IP, never X-Forwarded-For.
import { handleFooterSubscribe } from '../../lib/marketing/subscribe';
import { FUNCTION_WORDING_VERSION } from '../../lib/marketing/function-wording';
// A plain JSON import (no import attribute), as in function-wording.ts: the Pages bundler loads it natively.
import registry from '../../i18n/locales.json';

const LOCALES: string[] = registry.locales.map((l) => l.code);

interface Env {
  // Public origin of the API that receives newsletter opt-ins. Unset: accepted but not forwarded.
  REELBOLT_API_URL?: string;
  // Shared secret that lets the API trust the visitor IP sent with an opt-in. Unset: not sent.
  SITE_FORWARD_TOKEN?: string;
}

interface PagesContext {
  request: Request;
  env: Env;
  waitUntil?: (promise: Promise<unknown>) => void;
}

const json = (body: unknown, status: number) =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });

export const onRequestPost = async ({ request, env, waitUntil }: PagesContext) => {
  let body: unknown;
  try {
    body = await request.json();
  } catch {
    return json({ ok: false, error: 'invalid_request' }, 400);
  }

  // Cloudflare sets CF-Connecting-IP and a client cannot override it; never use X-Forwarded-For.
  const clientIp = request.headers.get('cf-connecting-ip')?.trim() || undefined;
  const outcome = handleFooterSubscribe({
    body,
    now: Date.now(),
    wordingVersion: FUNCTION_WORDING_VERSION,
    apiUrl: env.REELBOLT_API_URL,
    siteToken: env.SITE_FORWARD_TOKEN,
    clientIp,
    locales: LOCALES,
  });
  // waitUntil lets the API call finish after the response has gone out (forwardSubscribe never throws).
  if (outcome.forwarded && waitUntil) waitUntil(outcome.forwarded);
  return json(outcome.payload, outcome.status);
};
