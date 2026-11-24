import { isIP } from 'node:net';
import { after, NextResponse } from 'next/server';
import { handleFooterSubscribe } from '@/lib/marketing/subscribe';
import { MARKETING_WORDING } from '@/lib/marketing/wording';
import { locales } from '@/i18n/config';
import { clientIpKey, UNKNOWN_CLIENT_KEY } from '@/lib/client-ip';

// NOTE: This route lives at /api/subscribe, NOT /api/v1/*, so nginx's `/api/v1/*` proxy rules never
// intercept it. It falls through to the Next.js site, like /api/contact.

// Where the footer opt-in is forwarded (POST /api/v1/marketing/subscribe). Inside the compose network
// that is the inference service; set REELBOLT_API_URL to override.
const DEFAULT_API_URL = 'http://inference:8080';
const RATE_LIMIT_WINDOW_MS = 10 * 60 * 1000;
const RATE_LIMIT_MAX_REQUESTS = 5;

// Best-effort, in-memory, per-IP limit, same shape as the contact route: resets on restart and is per
// instance. Checked first, so an over-limit request is not validated or forwarded.
const requestLog = new Map<string, number[]>();
// Once the map holds more keys than this, keys whose newest request is outside the window are dropped,
// so a stream of distinct client addresses cannot grow the map without bound.
const PRUNE_THRESHOLD = 1000;

function pruneRequestLog(now: number): void {
  if (requestLog.size <= PRUNE_THRESHOLD) return;
  const windowStart = now - RATE_LIMIT_WINDOW_MS;
  for (const [key, timestamps] of requestLog) {
    const newest = timestamps[timestamps.length - 1];
    if (newest === undefined || newest <= windowStart) requestLog.delete(key);
  }
}

function isRateLimited(key: string): boolean {
  const now = Date.now();
  pruneRequestLog(now);
  const windowStart = now - RATE_LIMIT_WINDOW_MS;
  const timestamps = (requestLog.get(key) ?? []).filter((ts) => ts > windowStart);
  if (timestamps.length >= RATE_LIMIT_MAX_REQUESTS) {
    requestLog.set(key, timestamps);
    return true;
  }
  timestamps.push(now);
  requestLog.set(key, timestamps);
  return false;
}

export async function POST(request: Request) {
  const clientIp = clientIpKey(request);
  if (isRateLimited(clientIp)) {
    return NextResponse.json({ ok: false, error: 'rate_limited' }, { status: 429 });
  }

  let body: unknown;
  try {
    body = await request.json();
  } catch {
    return NextResponse.json({ ok: false, error: 'invalid_request' }, { status: 400 });
  }

  // Forward the address only when it parses as an IP; otherwise the header is omitted entirely.
  const forwardedIp = clientIp !== UNKNOWN_CLIENT_KEY && isIP(clientIp) !== 0 ? clientIp : undefined;
  const outcome = handleFooterSubscribe({
    wordingVersion: MARKETING_WORDING.version,
    body,
    now: Date.now(),
    apiUrl: process.env.REELBOLT_API_URL || DEFAULT_API_URL,
    siteToken: process.env.SITE_FORWARD_TOKEN,
    clientIp: forwardedIp,
    locales,
  });
  // The API call runs after the response is sent, so a slow API never delays the answer.
  if (outcome.forwarded) after(() => outcome.forwarded);
  return NextResponse.json(outcome.payload, { status: outcome.status });
}
