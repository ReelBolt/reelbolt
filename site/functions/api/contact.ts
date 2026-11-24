// LEGACY, UNUSED: nothing deploys this Pages Function (reelbolt.ai is the `site` container behind nginx).
// Cloudflare Pages Function serving POST /api/contact for the static export. The docker-compose
// build keeps using app/api/contact/route.ts instead; both share lib/validation.ts AND
// lib/marketing/mailer.ts — the single module that sends the mail — so the two paths cannot drift.
// See docs/marketing-site.md, "Contact form delivery": change one, change both.
// The route's in-memory limit cannot be reused here: the Pages runtime has no shared memory between
// isolates. This function carries a BEST-EFFORT per-isolate counter (defence in depth, not a limit);
// the authoritative guard is the zone's http_ratelimit ruleset, which covers /api/contact and
// /api/subscribe regardless of hostname (created by hand with the `cf` CLI; see
// docs/marketing-site.md step 9). Any per-client key added here must come
// from CF-Connecting-IP, never X-Forwarded-For.
import { topicFromParam, validateContact } from '../../lib/validation';
import { contactOptInFirstName, forwardSubscribe } from '../../lib/marketing/subscribe';
import { FUNCTION_WORDING_VERSION } from '../../lib/marketing/function-wording';
import { sendContactEnquiry } from '../../lib/marketing/mailer';
import { isLikelyBot } from '../../lib/bot-checks';
import { readContactBody } from '../../lib/contact-body';
import { createRateLimiter } from '../../lib/rate-limit';

interface Env {
  // Cloudflare Email Sending (mirrors api/services/cloudflare_mailer.go). All three are required
  // for a send; a missing one fails the enquiry loudly instead of accepting it into the void.
  CLOUDFLARE_EMAIL_ACCOUNT_ID?: string;
  CLOUDFLARE_EMAIL_TOKEN?: string;
  SMTP_FROM?: string;
  // Public origin of the API that receives newsletter opt-ins. Unset: opt-ins are logged, not sent.
  REELBOLT_API_URL?: string;
  // Shared secret that lets the API trust the visitor IP sent with an opt-in. Unset: not sent.
  SITE_FORWARD_TOKEN?: string;
  // The Pages env bag; only the fields above are read.
  [key: string]: string | undefined;
}

interface PagesContext {
  request: Request;
  env: Env;
  waitUntil?: (promise: Promise<unknown>) => void;
}

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });

const deliveryFailed = () =>
  json({ ok: false, errors: [{ field: '_form', message: 'Failed to deliver message.' }] }, 502);

const rateLimited = () =>
  json({ ok: false, errors: [{ field: '_form', message: 'Too many requests. Please try again later.' }] }, 429);

// BEST-EFFORT ONLY, and the comment matters: the Pages runtime gives every isolate its own memory,
// so this counter is not a shared limit and cannot enforce one. It raises the cost of a burst aimed
// at a single isolate and nothing more. The authoritative limit is the zone's http_ratelimit
// ruleset (docs/marketing-site.md step 9), which covers the whole deployment regardless of hostname.
// Keyed on CF-Connecting-IP (a client cannot override it; never X-Forwarded-For), with the same
// window and count as the self-hosted route (site/app/api/contact/route.ts): 5 per 10 minutes.
const RATE_LIMIT_WINDOW_MS = 10 * 60 * 1000;
const RATE_LIMIT_MAX_REQUESTS = 5;
// The limiter prunes keys with no request inside the window once the map grows large, so a stream
// of distinct client addresses cannot grow it without bound (as app/api/subscribe/route.ts does).
const limiter = createRateLimiter({ windowMs: RATE_LIMIT_WINDOW_MS, maxRequests: RATE_LIMIT_MAX_REQUESTS });
const isRateLimited = (key: string): boolean => limiter.isRateLimited(key);

// Runs once the enquiry is accepted. waitUntil lets the subscribe call finish after the response
// has gone out, so it never delays or fails the enquiry (forwardSubscribe never throws).
function forwardOptIn(
  payload: Record<string, unknown>,
  env: Env,
  clientIp: string | undefined,
  waitUntil?: PagesContext['waitUntil'],
) {
  if (payload.marketingOptIn !== true) return;
  if (!env.REELBOLT_API_URL) {
    console.info('[contact] newsletter opt-in not forwarded: REELBOLT_API_URL is not set');
    return;
  }
  const pending = forwardSubscribe(String(payload.email), 'siteContact', env.REELBOLT_API_URL, {
    wordingVersion: FUNCTION_WORDING_VERSION,
    siteToken: env.SITE_FORWARD_TOKEN,
    clientIp,
    // Only reached when the opt-in is ticked: the name is never forwarded without it.
    firstName: contactOptInFirstName(payload),
  });
  if (waitUntil) waitUntil(pending);
}

export const onRequestPost = async ({ request, env, waitUntil }: PagesContext) => {
  // Shared with the container route: malformed JSON and a non-object JSON body (null, array,
  // string, number) are both a 400, never a thrown field read further down.
  const body = await readContactBody(request);
  if (!body.ok) return json(body.response, body.status);
  const payload = body.payload as unknown as Record<string, unknown>;

  // Cloudflare sets CF-Connecting-IP and a client cannot override it; never use X-Forwarded-For.
  const clientIp = request.headers.get('cf-connecting-ip')?.trim() || undefined;

  // Best-effort per-isolate counter, checked before anything else, exactly as the container route
  // does. It is defence in depth, not the limit — see the comment on isRateLimited.
  if (clientIp && isRateLimited(clientIp)) {
    return rateLimited();
  }

  // Bot checks (lib/bot-checks.ts): a filled honeypot or a form sent less than 3 s after it opened.
  // Answer like a success so the bot learns nothing; send and forward nothing.
  if (isLikelyBot(payload, Date.now())) {
    return json({ ok: true });
  }

  const errors = validateContact(payload as never);
  if (errors.length > 0) return json({ ok: false, errors }, 400);

  const enquiry = {
    name: String(payload.name ?? ''),
    email: String(payload.email ?? ''),
    company: payload.company ? String(payload.company) : undefined,
    topic: topicFromParam(payload.topic as string | undefined),
    message: payload.message ? String(payload.message) : undefined,
  };

  // The shared module sends through Cloudflare Email Sending. A non-2xx, a transport error or an
  // unconfigured mailer is the visitor's "Failed to deliver message" — never a false success.
  try {
    await sendContactEnquiry(enquiry, env);
  } catch (err) {
    console.error('[contact] email send failed', err instanceof Error ? err.message : 'unknown error');
    return deliveryFailed();
  }
  forwardOptIn(payload, env, clientIp, waitUntil);
  return json({ ok: true });
};
