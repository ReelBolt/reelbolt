// Cloudflare Email Sending transport, shared by BOTH /api/contact handlers.
//
// This mirrors api/services/cloudflare_mailer.go (the Go API's transport): the same
// endpoint, the same JSON body — HTML and text as SEPARATE fields, never a MIME
// message — the same 15 s request timeout, and the same 8 KiB cap on a logged error
// body. It refuses to send when it is not configured, exactly as the Go mailer does,
// rather than silently accepting an enquiry nobody will ever see.
//
// The two entry points — site/app/api/contact/route.ts (the `site`
// container) and the legacy, unused site/functions/api/contact.ts — both call this
// module, so the two paths cannot drift. See docs/marketing-site.md, "Contact form
// delivery", for the "change one, change both" rule this file exists to satisfy.
import { CONTACT_TOPIC_LABELS, topicFromParam } from '../validation.ts';

// Cloudflare API origin. CLOUDFLARE_API_BASE_URL overrides it so tests can point at a
// local server; nothing in production sets it (mirrors MailerConfig.CloudflareAPIBaseURL).
export const CLOUDFLARE_API_BASE = 'https://api.cloudflare.com';

// One send, one request timeout — not a retry budget. A failure is reported, never
// retried against another provider.
export const CLOUDFLARE_SEND_TIMEOUT_MS = 15_000;

// Cloudflare's reason ("sender not verified", "domain not onboarded", "token revoked")
// is a short JSON envelope, so a bounded read loses nothing and an unbounded one could.
export const CLOUDFLARE_ERROR_BODY_LIMIT = 8 << 10;

// The contact form's recipient, and the fallback sender the site documents.
export const CONTACT_RECIPIENT = 'support@reelbolt.ai';

export interface MailEnv {
  CLOUDFLARE_EMAIL_ACCOUNT_ID?: string;
  /** A Cloudflare user API token — a SECRET. Never a literal in the repository. */
  CLOUDFLARE_EMAIL_TOKEN?: string;
  /** The single From address, used by BOTH providers (see .env.example). */
  SMTP_FROM?: string;
  /** Test-only override of the API origin. */
  CLOUDFLARE_API_BASE_URL?: string;
  // The env bag the callers actually pass (process.env, or the Pages Env), of which only the
  // fields above are read.
  [key: string]: string | undefined;
}

export interface MailMessage {
  to: string;
  from: string;
  subject: string;
  html: string;
  text: string;
}

export interface SendMailDeps {
  fetch?: typeof fetch;
  log?: (message: string, detail?: unknown) => void;
}

export interface ContactEnquiry {
  name: string;
  email: string;
  company?: string;
  topic?: string;
  message?: string;
}

export function mailFrom(env: MailEnv): string {
  return (env.SMTP_FROM ?? '').trim();
}

/** Everything a send needs: token, account and a From address. */
export function mailConfigured(env: MailEnv): boolean {
  return Boolean(
    (env.CLOUDFLARE_EMAIL_TOKEN ?? '').trim() &&
      (env.CLOUDFLARE_EMAIL_ACCOUNT_ID ?? '').trim() &&
      mailFrom(env),
  );
}

export function sendEndpoint(env: MailEnv): string {
  const base = ((env.CLOUDFLARE_API_BASE_URL ?? '').trim() || CLOUDFLARE_API_BASE).replace(/\/+$/, '');
  const account = encodeURIComponent((env.CLOUDFLARE_EMAIL_ACCOUNT_ID ?? '').trim());
  return `${base}/client/v4/accounts/${account}/email/sending/send`;
}

// Read at most `limit` bytes of a response body. The Go mailer uses io.LimitReader for
// the same reason: an error body is echoed to logs, so it must be bounded.
async function readBounded(res: Response, limit: number): Promise<string> {
  if (!res.body) return '';
  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let text = '';
  let read = 0;
  try {
    while (read < limit) {
      const { done, value } = await reader.read();
      if (done) break;
      read += value.byteLength;
      text += decoder.decode(value, { stream: true });
    }
  } finally {
    await reader.cancel().catch(() => {});
  }
  return text.slice(0, limit);
}

// Any address Cloudflare echoes back is redacted before it reaches a log (C4).
const EMAIL_RE = /[^\s@<>()"']+@[^\s@<>()"']+\.[^\s@<>()"']+/g;
function redactAddresses(text: string): string {
  return text.replace(EMAIL_RE, '[redacted]');
}

/**
 * POST one message through Cloudflare Email Sending. Throws on a missing
 * configuration, a transport error or a non-2xx, so the caller answers the visitor
 * with the form's "Failed to deliver message" rather than a false success.
 */
export async function sendMail(message: MailMessage, env: MailEnv, deps: SendMailDeps = {}): Promise<void> {
  const log = deps.log ?? ((msg: string, detail?: unknown) => console.error(msg, detail ?? ''));
  if (!mailConfigured(env)) {
    throw new Error(
      'cloudflare email sending is not configured: CLOUDFLARE_EMAIL_TOKEN, CLOUDFLARE_EMAIL_ACCOUNT_ID and SMTP_FROM are all required',
    );
  }

  const doFetch = deps.fetch ?? fetch;
  const body = JSON.stringify({
    to: message.to,
    from: message.from,
    subject: message.subject,
    html: message.html,
    text: message.text,
  });

  const res = await doFetch(sendEndpoint(env), {
    method: 'POST',
    headers: {
      authorization: `Bearer ${(env.CLOUDFLARE_EMAIL_TOKEN ?? '').trim()}`,
      'content-type': 'application/json',
    },
    body,
    signal: AbortSignal.timeout(CLOUDFLARE_SEND_TIMEOUT_MS),
  });

  if (res.status < 200 || res.status > 299) {
    const reason = redactAddresses((await readBounded(res, CLOUDFLARE_ERROR_BODY_LIMIT)).trim());
    log('[contact] cloudflare email send failed', reason ? `${res.status}: ${reason}` : String(res.status));
    throw new Error(`cloudflare email send failed: ${res.status}`);
  }
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

export function contactSubject(enquiry: ContactEnquiry): string {
  return `[ReelBolt contact] ${CONTACT_TOPIC_LABELS[topicFromParam(enquiry.topic)]} — ${enquiry.name}`;
}

/** Compose a contact enquiry into the message the Cloudflare API takes. */
export function contactMail(enquiry: ContactEnquiry, env: MailEnv): MailMessage {
  const topic = CONTACT_TOPIC_LABELS[topicFromParam(enquiry.topic)];
  const rows: Array<[string, string]> = [
    ['Name', enquiry.name],
    ['Email', enquiry.email],
    ['Topic', topic],
  ];
  if (enquiry.company) rows.push(['Company', enquiry.company]);

  const text = [...rows.map(([label, value]) => `${label}: ${value}`), '', enquiry.message ?? ''].join('\n');
  const html =
    `<h2>Contact enquiry</h2><dl>` +
    rows.map(([label, value]) => `<dt>${escapeHtml(label)}</dt><dd>${escapeHtml(value)}</dd>`).join('') +
    `</dl><pre>${escapeHtml(enquiry.message ?? '')}</pre>`;

  return {
    to: CONTACT_RECIPIENT,
    from: mailFrom(env),
    subject: contactSubject(enquiry),
    html,
    text,
  };
}

/** Send a contact enquiry. The one call both handlers make. */
export async function sendContactEnquiry(
  enquiry: ContactEnquiry,
  env: MailEnv,
  deps: SendMailDeps = {},
): Promise<void> {
  await sendMail(contactMail(enquiry, env), env, deps);
}
