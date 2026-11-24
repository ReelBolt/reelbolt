import { after, NextResponse } from 'next/server';
import { topicFromParam, validateContact } from '@/lib/validation';
import { readContactBody } from '@/lib/contact-body';
import { contactOptInFirstName, forwardSubscribe } from '@/lib/marketing/subscribe';
import { MARKETING_WORDING } from '@/lib/marketing/wording';
import { sendContactEnquiry } from '@/lib/marketing/mailer';
import { clientIpKey, UNKNOWN_CLIENT_KEY } from '@/lib/client-ip';
import { isLikelyBot } from '@/lib/bot-checks';
import { createRateLimiter } from '@/lib/rate-limit';

// This route is the `site` container's contact form (the one that serves reelbolt.ai). The legacy,
// unused Pages twin site/functions/api/contact.ts sends mail through the SAME module —
// lib/marketing/mailer.ts — so the two paths cannot drift while it stays in the tree. See
// docs/marketing-site.md, "Contact form delivery".

// NOTE: This route lives at /api/contact, NOT /api/v1/*, so nginx's
// `/api/v1/*` proxy rules (which forward to the Go API / inference service)
// never intercept it — it falls through to nginx's `location /` block and is
// served entirely by this Next.js marketing site. It has nothing to do with
// the Go/inference `/api/v1/*` APIs described in the repo's CLAUDE.md.

const RATE_LIMIT_WINDOW_MS = 10 * 60 * 1000;
const RATE_LIMIT_MAX_REQUESTS = 5;
// Where the newsletter opt-in is forwarded (POST /api/v1/marketing/subscribe). Inside the compose
// network that is the inference service; set REELBOLT_API_URL to override.
const DEFAULT_API_URL = 'http://inference:8080';

// Best-effort, in-memory, per-IP rate limit. Resets on redeploy/restart and
// is per-instance (not shared across replicas) — acceptable for a marketing
// contact form, not a substitute for a real edge/WAF rate limiter. Keyed on the
// address nginx vouches for (see lib/client-ip.ts) and checked before anything
// else, so an over-limit request neither sends the enquiry nor forwards the opt-in.
// The limiter prunes keys with no request inside the window once the map grows large, so a stream
// of distinct client addresses cannot grow it without bound (as app/api/subscribe/route.ts does).
const limiter = createRateLimiter({ windowMs: RATE_LIMIT_WINDOW_MS, maxRequests: RATE_LIMIT_MAX_REQUESTS });
const isRateLimited = (key: string): boolean => limiter.isRateLimited(key);

export async function POST(request: Request) {
  const clientIp = clientIpKey(request);
  if (isRateLimited(clientIp)) {
    return NextResponse.json({ ok: false, errors: [{ field: '_form', message: 'Too many requests. Please try again later.' }] }, { status: 429 });
  }

  const body = await readContactBody(request);
  if (!body.ok) {
    return NextResponse.json(body.response, { status: body.status });
  }
  const payload = body.payload;

  // Bot checks (lib/bot-checks.ts): a filled honeypot, or a form sent less than 3 s after it
  // opened (or with no or a forged `startedAt`). Silently swallow the submission without
  // forwarding, logging, or validating it, so a bot sees the same answer as a genuine success.
  if (isLikelyBot(payload, Date.now())) {
    return NextResponse.json({ ok: true });
  }

  // Server-side validation is authoritative — never trust that the client
  // already validated correctly (a direct API call bypassing the UI must
  // still be rejected for bad input).
  const errors = validateContact(payload);
  if (errors.length > 0) {
    return NextResponse.json({ ok: false, errors }, { status: 400 });
  }

  const enquiry = {
    name: payload.name,
    email: payload.email,
    company: payload.company,
    topic: topicFromParam(payload.topic),
    message: payload.message,
  };

  // The shared module sends through Cloudflare Email Sending. A non-2xx, a transport error or an
  // unconfigured mailer is the visitor's "Failed to deliver message" — never a false success.
  try {
    await sendContactEnquiry(enquiry, process.env);
  } catch (err) {
    console.error('[contact] email send failed', err instanceof Error ? err.message : 'unknown error');
    return NextResponse.json(
      { ok: false, errors: [{ field: '_form', message: 'Failed to deliver message.' }] },
      { status: 502 },
    );
  }

  // The enquiry is accepted at this point. The newsletter opt-in runs after the response is sent,
  // so a slow or failing API can neither delay nor fail it (forwardSubscribe never throws).
  if (payload.marketingOptIn === true) {
    const apiUrl = process.env.REELBOLT_API_URL || DEFAULT_API_URL;
    const email = String(payload.email);
    // C3: with SITE_FORWARD_TOKEN set, the API trusts the visitor IP we pass for its per-IP limit.
    const forward = {
      wordingVersion: MARKETING_WORDING.version,
      siteToken: process.env.SITE_FORWARD_TOKEN,
      clientIp: clientIp === UNKNOWN_CLIENT_KEY ? undefined : clientIp,
      // Only reached inside the opt-in branch: the name is never forwarded without the tick.
      firstName: contactOptInFirstName(payload),
    };
    after(() => forwardSubscribe(email, 'siteContact', apiUrl, forward));
  }

  return NextResponse.json({ ok: true });
}
