import { PROVIDER_DATA_HANDLING } from './provider-data-handling.ts';
import { TRADEMARK_SYMBOL } from './trademark.ts';

// Legal and contact facts for the ReelBolt site. One source of truth: every legal/contact surface
// imports from here. An EMPTY STRING means the fact is deliberately absent (not yet incorporated, or
// not applicable) - the pages render those fields as blank rather than inventing a value.
//
// Supplied by the owner (2026-10-26). The operator and data controller is a natural person,
// Tommaso Celano, trading as a sole trader under the ReelBolt brand. REGISTERED_ADDRESS is personal
// data: this constant and the marketing-email footer are the ONLY places it may appear. No test,
// doc, log or fixture may contain the literal; tests assert shape through this import.

export const COMPANY_LEGAL_NAME = 'ReelBolt';
export const CONTROLLER_NAME = 'Tommaso Celano';
export const REGISTERED_ADDRESS = 'Via Fratelli Castaldi 7, 28060 Cureggio (NO), Italy';
export const COMPANY_REGISTRATION_NUMBER = '';
// Partita IVA (Italian VAT number), VIES-valid, registered to the controller (the operator, 2026-10-26).
export const PARTITA_IVA = 'IT02862070030';
// The one formatter for the legal identity: 'P.IVA IT02862070030', or '' while PARTITA_IVA is empty
// so callers render nothing rather than a bare label.
export const PARTITA_IVA_LABEL = PARTITA_IVA ? `P.IVA ${PARTITA_IVA}` : '';
export const GENERAL_EMAIL = 'hello@reelbolt.ai';
export const SUPPORT_EMAIL = 'support@reelbolt.ai';
export const PRIVACY_EMAIL = 'privacy@reelbolt.ai';
export const DPO_CONTACT = '';
export const GOVERNING_LAW = 'the laws of Italy';

// ── The operator statement ─────────────────────────────────────────────────
// The legal pages render this sentence wherever they name who operates the Service. It is built from
// the constants above, so the identity can never differ between the Terms, the Privacy Policy and the
// Runner EULA. The Partita IVA part is dropped while PARTITA_IVA is empty.
export const OPERATOR_STATEMENT =
  `ReelBolt is operated by ${CONTROLLER_NAME}, an individual trader, at ${REGISTERED_ADDRESS}` +
  (PARTITA_IVA_LABEL ? `, ${PARTITA_IVA_LABEL}` : '') +
  '.';

// ── The trademark notice ───────────────────────────────────────────────────
// Pending application, so the unregistered symbol only (see ./trademark.ts). No application number,
// receipt, tax code or address belongs next to this sentence.
export const TRADEMARK_STATEMENT = `${COMPANY_LEGAL_NAME}${TRADEMARK_SYMBOL} is a trademark of ${CONTROLLER_NAME}.`;

// No Data Protection Officer is appointed: at this scale the processing does not meet an Article 37
// GDPR trigger, so the privacy contact below is the route for data-protection questions. A DPO's
// absence is stated rather than shown as an empty field (the same rule as the operator statement).
export const DPO_STATEMENT =
  'We have not appointed a Data Protection Officer: at our scale we are not required to appoint one under Article 37 GDPR, and data-protection questions go to the privacy contact below.';
export const COURTS_JURISDICTION = 'the courts of Novara, Italy';
// Both documents were revised on November 10th, 2026 (community recipes: gallery licence and takedown
// in Terms 10, recipe data and retention in Privacy; before that, on November 5th, 2026, avatars). They keep separate constants because a later
// revision may touch only one: bumping the Terms must not claim the Privacy Policy changed on the
// same day.
// The Privacy Policy alone was revised on November 11th, 2026 (new-user funnel: short-link click
// counts, campaign labels on visit records and in sign-up links, the welcome questions, first-step
// records, the rf-visit-sent storage key). It matches the legal_documents Privacy version
// 2026-11-11 until the November 17th revision below.
// Both documents were revised again on November 17th, 2026 (avatars of objects, pictures with avatars and
// the workspace avatar library: Terms 9.1 and 11, the providers table and the avatar paragraphs of the
// Privacy Policy). They match the legal_documents versions 2026-11-17; a Terms bump asks every user to
// re-accept, and the Privacy Policy moves with it.
export const LAST_UPDATED = 'November 17th, 2026';
export const TERMS_LAST_UPDATED = 'November 17th, 2026';
export const PHONE_NUMBER = '';

// Sub-processors. Disclosed only as far as the privacy policy needs.
// The Service (the app, the APIs, the database) runs on our servers at Hetzner.
export const HOSTING_PROVIDER = 'Hetzner (Falkenstein, Germany)';
// The marketing website and its short links (`/l/<code>`) are served by Cloudflare Pages, and
// Cloudflare passes requests for the app on to the servers above (docs/tls.md, "Serving reelbolt.ai
// from the two boxes"). It sees the IP address and user agent of every request and applies the
// per-address rate limits in infra/cloudflare/.
export const WEBSITE_DELIVERY_PROVIDER = 'Cloudflare';
// Internal reporting. A nightly extract copies the tables the reporting role may read
// to files in the storage provider below, and the reporting tool reads
// them. The new-user funnel added sign-up source, welcome answers and visitor first touches to it.
export const REPORTING_PROVIDER = 'Microsoft Power BI';
// Cloudflare delivers our transactional email and contact-form mail. Marketing email, sent from our own
// consent list, goes through Brevo (Operator decision 2026-10-26: Cloudflare's service is transactional-only).
export const EMAIL_PROVIDER = 'Cloudflare';
export const MARKETING_EMAIL_PROVIDER = 'Brevo';
export const STORAGE_PROVIDER = 'Cloudflare R2';
// The payment provider sells to the customer as merchant of record and is an independent controller
// of the payment data it collects (Privacy Policy section 6), so it is named on its own, not as a
// processor. Operator decision (2026-10-26): Stripe becomes the payment provider and seller of record
// (Stripe Managed Payments); subscriptions that started on Paddle stay with Paddle. ONE statement
// below carries that, so the Terms and the Privacy Policy cannot tell it two ways. It must go live
// together with the Stripe switch: until then new purchases still go through Paddle.
export const PAYMENT_PROVIDERS: readonly string[] = ['Stripe', 'Paddle'];
export const PAYMENT_PROVIDER = PAYMENT_PROVIDERS.join(' and ');
// Product telemetry, error reports and product-health signals are exported to Grafana Cloud over OTLP
// (docs/observability.md, decision D19). The stack is in the EU region (the operator, 2026-10-26); the
// privacy page renders the region clause only while this is non-empty.
export const OBSERVABILITY_PROVIDER = 'Grafana Cloud (Grafana Labs)';
export const OBSERVABILITY_PROVIDER_REGION = 'EU';
export const OBSERVABILITY_DPA_URL = 'https://grafana.com/legal/data-processing-agreement/';
// IP reputation lookup used by the first-video abuse check (Privacy Policy sections 3, 4, 6, 9).
export const IP_REPUTATION_PROVIDER = 'ipinfo.io';
export const PAYMENT_PROVIDER_STATEMENT =
  'Payments are taken by Stripe (Link), which sells to you as the seller of record and merchant of record under Stripe Managed Payments. ' +
  'Subscriptions that began on Paddle are still handled by Paddle, which remains the merchant of record for them.';
// Derived from PROVIDER_DATA_HANDLING rather than written out again: the sub-processor row and the
// per-provider data-handling table a user reads are then guaranteed to name the same vendors, which
// is exactly the drift the Terms and the Privacy Policy cannot afford to have between them.
export const MODEL_PROVIDERS = PROVIDER_DATA_HANDLING.map((row) => row.vendor).join(', ');

export const SUPERVISORY_AUTHORITY = 'the Garante per la Protezione dei Dati Personali (Italy)';
export const CONTACT_RETENTION_PERIOD = '3 years';
// Server access logs: nginx keeps them for NGINX_LOG_RETENTION_DAYS, 30 by default. The figure here is the
// default; an operator who changes the setting must change it here too.
export const LOG_RETENTION_PERIOD = '30 days';
// Product analytics (initiative product-analytics, ships with its switch-on; see the PR that adds this).
export const FIRST_TOUCH_COOKIE_PERIOD = '30 days';
export const VISIT_RECORD_RETENTION_PERIOD = '13 months';
// Marketing email: an unconfirmed sign-up is deleted after this long.
export const PENDING_SUBSCRIPTION_PERIOD = '30 days';
// Marketing email: how long the consent record is kept after withdrawal, before only the
// suppression entry remains. Proposed in the 2026-10-12 compliance check; counsel confirms.
export const MARKETING_CONSENT_RETENTION_PERIOD = '3 years';
export const LIABILITY_CAP =
  'the fees paid in the twelve (12) months preceding the event giving rise to liability';
export const SERVICE_AGREEMENT_REFERENCE = 'these Terms';
export const TRANSFER_MECHANISM = 'the European Commission Standard Contractual Clauses';
export const RESPONSE_TIME_SLA = 'best effort; no committed response time';

// Subscription and desktop-runner facts.
export const CREDIT_EXPIRY_PERIOD = '1 year';
// Operator ruling (2026-10-26): the money-back is a FULL refund with NO deduction for credits used; the
// credits from that payment are clawed back; one refund per customer.
export const REFUND_WINDOW = '14 days';
// One sentence for a failed run, used verbatim by the Terms, the FAQ and the refund policy.
export const FAILED_RUN_POLICY =
  'If a run fails because of a problem on our side, the credits for the failed step are returned, and you can resume without paying again for the steps already done.';
export const REFUND_POLICY =
  `${FAILED_RUN_POLICY} A run that fails because of your input, such as a file that cannot be read, keeps its consumption. ` +
  `Money-back promise: if you are a new paying customer, you may ask for a full refund of your first subscription payment or of a credit top-up within ${REFUND_WINDOW} of the payment, ` +
  `by writing to ${SUPPORT_EMAIL}. The refund is the full amount paid, with no deduction for credits you have already used; the credits granted by that payment are withdrawn. ` +
  `One refund is available per customer, and we may refuse repeat refund requests in case of abuse. ` +
  `This promise is a contractual commitment on top of, and does not limit, any statutory right you have`;

// Public alpha first-video offer. One place for the numbers the Terms and the pricing page quote.
// The founder price is promotional only and lives on the pricing page (lib/launch-offer.ts); the
// Terms make no promise about it.
export const FIRST_VIDEO_GRANT_CREDITS = 750;
// The grant's lifetime as a number, for the pages that state it in the visitor's language (the trust
// strip, the audience cost answer). The English phrase the Terms print is built from it.
export const FIRST_VIDEO_GRANT_EXPIRY_DAYS = 30;
export const FIRST_VIDEO_GRANT_EXPIRY = `${FIRST_VIDEO_GRANT_EXPIRY_DAYS} days`;
export const CONTENT_RETENTION_PERIOD = '3 years';
export const RUNNER_EULA_LAST_UPDATED = 'October 8th, 2026';

// ── The alpha's data disclosure ────────────────────────────────────────────
// The five categories the paid alpha collects, written once and rendered by the privacy policy's
// Section 3 so the words a visitor reads have one source. Each entry comes from the T-018/T-019/T-020
// hand-offs (docs/observability.md, the feedback channel, the ambient signals) — no category is
// invented, and each says what it does NOT collect, because that is the part a reader needs.
export interface DataCategoryDisclosure {
  /** The label the policy's list shows. */
  label: string;
  /** One sentence: what is collected, and what it never includes. */
  description: string;
}

export const DISCLOSED_DATA_CATEGORIES: readonly DataCategoryDisclosure[] = [
  {
    label: 'Product telemetry',
    description:
      'Traces and metrics about how the Service runs: which operations a request performs, how long ' +
      'they take and whether they succeed. They carry account and workspace identifiers, not your ' +
      'files, prompts or other content.',
  },
  {
    label: 'Error tracking',
    description:
      'When something breaks, a record of the failure, on our servers and in your browser. A ' +
      'browser error report carries the error class name and the page it happened on, with ' +
      'identifying parts of the address removed. It does not include the error message or stack, ' +
      'because they can carry your content.',
  },
  {
    label: 'Funnel analytics',
    description:
      'Four events that mark the first time you sign up, create a project, run a workflow and export. ' +
      'Each event carries its name only and is sent only if you allow it.',
  },
  {
    label: 'The feedback channel',
    description:
      'The bug reports and feedback you choose to send us from inside the product: the message you ' +
      'write and any context you choose to attach.',
  },
  {
    label: 'Ambient product-health signals',
    description:
      'Automatic signals that tell us the alpha is struggling: a failed tool call, a refused request, ' +
      'a failed workflow step, or a chat that ended on an error. They are metadata only, with no ' +
      'message content, and are linked to your account and workspace (and so to your email address), ' +
      'which means they are not anonymous.',
  },
];

// Retention for the five categories above (Operator ruling 2026-10-26): twelve months, then deleted or
// aggregated.
export const TELEMETRY_RETENTION_PERIOD = '12 months';
export const DISCLOSED_DATA_RETENTION_STATEMENT =
  `We keep product telemetry, error tracking, funnel analytics, the feedback channel and the ` +
  `product-health signals for ${TELEMETRY_RETENTION_PERIOD}, then delete or aggregate them. The record of ` +
  `your telemetry choice is kept while your account exists, and deleted within ${TELEMETRY_RETENTION_PERIOD} after it is closed.`;

// The ambient-signal clause, kept in step with docs/observability.md ("Alpha-notice wording for
// T-021"). Kept on its own so a test can pin the exact string and the alpha banner can render it.
// The signals are NOT anonymous: each span carries the acting user's id and the organization's id,
// and the BI extract joins them to the account's email. The sentence says so.
export const AMBIENT_SIGNAL_NOTICE =
  'We also record product-health signals (a failed tool call, a refused request, a failed workflow ' +
  'step, or a chat that ended on an error) as metadata only, so we can see when the alpha is ' +
  'struggling. Each is linked to your account and workspace, and so to your email address; it is not ' +
  'anonymous. It carries no message content.';

// The one-line alpha-notice addition (T-021): it states that the early-access alpha collects
// telemetry, then carries T-020's ambient-signal clause verbatim. the removed alpha bar rendered this directly
// below the pinned ALPHA_NOTICE sentence (lib/site-config), which stays byte-identical on every
// surface; this is a second sentence, not a rewrite of the pinned one.
export const ALPHA_TELEMETRY_NOTICE =
  'We collect telemetry to see how the alpha is going. ' + AMBIENT_SIGNAL_NOTICE;
