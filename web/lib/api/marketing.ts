import { apiFetch, ApiError } from './client';

/**
 * Marketing email consent (`/api/v1/marketing/*`, saas-promotions plan "Marketing consent
 * endpoints"). Not billing-gated: a deployment with billing switched off still answers these.
 *
 * Consent belongs to the signed-in person's email, never to a workspace, and every grant names the
 * wording version the person was shown — so a caller that offers the box always renders the text
 * `GET wording` answered and sends that same version back.
 */

export const MARKETING_WORDING_URL = '/api/v1/marketing/wording';
export const MARKETING_CONSENT_URL = '/api/v1/marketing/consent';

/** The current entry of the wording registry. */
export interface MarketingWording {
  version: string;
  text: string;
}

export type MarketingConsentStatus = 'none' | 'pending' | 'optedIn' | 'withdrawn';

/** Where a signed-in grant or withdrawal was made. */
// 'checkout' is no longer sent by the web app (the pre-checkout box was removed); the server enum keeps it.
export type MarketingConsentSource = 'settings' | 'checkout' | 'acceptTerms';

export interface MarketingConsent {
  status: MarketingConsentStatus;
  wordingVersion: string | null;
  consentedAt: string | null;
  confirmedAt: string | null;
  withdrawnAt: string | null;
}

export interface UpdateMarketingConsentRequest {
  optIn: boolean;
  source: MarketingConsentSource;
  wordingVersion: string;
}

/** Statuses compared case-insensitively, so a PascalCase serializer still reads correctly. */
export function consentStatusOf(value: unknown): MarketingConsentStatus {
  const lowered = typeof value === 'string' ? value.toLowerCase() : '';
  if (lowered === 'pending') return 'pending';
  if (lowered === 'optedin') return 'optedIn';
  if (lowered === 'withdrawn') return 'withdrawn';
  return 'none';
}

function normalizeConsent(raw: Partial<MarketingConsent> | null | undefined): MarketingConsent {
  return {
    status: consentStatusOf(raw?.status),
    wordingVersion: raw?.wordingVersion ?? null,
    consentedAt: raw?.consentedAt ?? null,
    confirmedAt: raw?.confirmedAt ?? null,
    withdrawnAt: raw?.withdrawnAt ?? null,
  };
}

export async function getMarketingWording(): Promise<MarketingWording> {
  const body = await apiFetch<Partial<MarketingWording> | null>(MARKETING_WORDING_URL);
  if (!body || typeof body.version !== 'string' || !body.version || typeof body.text !== 'string' || !body.text) {
    // A box whose text or version is unknown can never record a valid grant, so callers hide it.
    throw new Error('The marketing consent wording is unavailable.');
  }
  return { version: body.version, text: body.text };
}

export async function getMarketingConsent(): Promise<MarketingConsent> {
  return normalizeConsent(await apiFetch<Partial<MarketingConsent> | null>(MARKETING_CONSENT_URL));
}

export async function updateMarketingConsent(request: UpdateMarketingConsentRequest): Promise<MarketingConsent> {
  return normalizeConsent(
    await apiFetch<Partial<MarketingConsent> | null>(MARKETING_CONSENT_URL, {
      method: 'PUT',
      body: JSON.stringify(request),
    }),
  );
}

/**
 * The version a `409 {currentWordingVersion}` refusal names: the wording moved between showing the
 * box and ticking it. Null for any other error.
 */
export function staleWordingVersionOf(error: unknown): string | null {
  if (!(error instanceof ApiError) || error.status !== 409) return null;
  const body = error.body;
  if (typeof body !== 'object' || body === null) return null;
  const current = (body as Record<string, unknown>).currentWordingVersion;
  return typeof current === 'string' && current ? current : null;
}

/**
 * The refusal code the Inference API sends, platform-wide, when an account's email is not yet
 * verified: `403 { code: "EMAIL_NOT_VERIFIED", message }` (EmailVerificationGate.ErrorCode).
 */
export const EMAIL_NOT_VERIFIED_CODE = 'EMAIL_NOT_VERIFIED';

/**
 * True for the `403 {code:"EMAIL_NOT_VERIFIED", message}` refusal GET/PUT `consent` answer for an
 * account whose email is not verified (S-02): until the address is proven, the record that address
 * keys may belong to someone else, so the account can neither read nor change it. Callers then hide
 * the marketing box (checkout and acceptance go ahead without it) or explain why it is unavailable.
 */
export function isEmailNotVerified(error: unknown): boolean {
  return error instanceof ApiError && error.status === 403 && error.code === EMAIL_NOT_VERIFIED_CODE;
}

/** `POST unsubscribe` with the token from an email link. Never sent on page load. */
export async function unsubscribeMarketing(token: string): Promise<void> {
  await apiFetch<unknown>('/api/v1/marketing/unsubscribe', {
    method: 'POST',
    body: JSON.stringify({ token }),
  });
}

/** `POST confirm` with the double-opt-in token. Never sent on page load. */
export async function confirmMarketingSubscription(token: string): Promise<void> {
  await apiFetch<unknown>('/api/v1/marketing/confirm', {
    method: 'POST',
    body: JSON.stringify({ token }),
  });
}

// ---------------------------------------------------------------------------------------------
// Platform admin: the marketing list (export for a send). Platform admin only on the server too.
// ---------------------------------------------------------------------------------------------

export const MARKETING_ADMIN_CONSENTS_URL = '/api/v1/marketing/admin/consents';
/** A plain download: the browser follows it with the session cookie, and nothing is cached. */
export const MARKETING_ADMIN_EXPORT_URL = '/api/v1/marketing/admin/consents/export.csv';

export interface MarketingListCounts {
  pending: number;
  optedIn: number;
  withdrawn: number;
  exportable: number;
}

export interface MarketingListExport {
  exportedAt: string;
  rowCount: number;
}

export interface MarketingListSummary {
  counts: MarketingListCounts;
  lastExport: MarketingListExport | null;
}

function countOf(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : 0;
}

export async function getMarketingListSummary(): Promise<MarketingListSummary> {
  const body = await apiFetch<{ counts?: Partial<Record<keyof MarketingListCounts, unknown>>; lastExport?: unknown } | null>(
    MARKETING_ADMIN_CONSENTS_URL,
  );
  const counts = body?.counts ?? {};
  const last = body?.lastExport;
  let lastExport: MarketingListExport | null = null;
  if (typeof last === 'object' && last !== null) {
    const raw = last as Record<string, unknown>;
    if (typeof raw.exportedAt === 'string' && raw.exportedAt) {
      lastExport = { exportedAt: raw.exportedAt, rowCount: countOf(raw.rowCount) };
    }
  }
  return {
    counts: {
      pending: countOf(counts.pending),
      optedIn: countOf(counts.optedIn),
      withdrawn: countOf(counts.withdrawn),
      exportable: countOf(counts.exportable),
    },
    lastExport,
  };
}
