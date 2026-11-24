/**
 * The legal documents the signup and accept-terms forms record against (F-7).
 *
 * These used to carry `TERMS_VERSION`/`PRIVACY_VERSION` build-time constants, and `proxy.ts`
 * compared the readable `reelbolt_user` cookie against them. That comparison was between two
 * things the browser could produce: the Go API stored whatever version string the caller sent — it
 * only checked non-empty and at most 32 characters — and the constant came from a
 * `NEXT_PUBLIC_TERMS_VERSION` build arg. Nothing in the pair was evidence of anything.
 *
 * The versions now live in the `legal_documents` table, which the Inference API owns and publishes,
 * and every reader asks for them at runtime (`legal-server.ts`). No module here may reintroduce a
 * version constant: a cached copy would be a second source of truth, and the whole finding is that
 * a second source of truth is what made the record unauditable.
 */

/** The version of each legal document the deployment says it is serving. */
export interface LegalVersions {
  termsVersion: string;
  privacyVersion: string;
}

/**
 * Reads the versions out of a `GET /api/v1/legal/documents` body, or out of the 409 body the Go
 * API answers a stale submission with (same two field names). Anything missing or not a non-empty
 * string yields null, so a caller never compares against a half-parsed answer.
 */
export function parseLegalVersions(payload: unknown): LegalVersions | null {
  if (typeof payload !== 'object' || payload === null) return null;
  const body = payload as Record<string, unknown>;
  const terms = body.termsVersion;
  const privacy = body.privacyVersion;
  if (typeof terms !== 'string' || typeof privacy !== 'string') return null;
  const trimmedTerms = terms.trim();
  const trimmedPrivacy = privacy.trim();
  if (!trimmedTerms || !trimmedPrivacy) return null;
  return { termsVersion: trimmedTerms, privacyVersion: trimmedPrivacy };
}

/**
 * Reads the current versions out of the 409 the Go API answers a submission that names something
 * other than what the deployment serves (api/handlers/auth/legal.go). The field names differ from
 * `parseLegalVersions`'s on purpose — `currentTermsVersion` cannot be mistaken for the version the
 * caller just sent — so the mapping lives here rather than at each call site.
 */
export function legalVersionsFromRefusal(body: unknown): LegalVersions | null {
  if (typeof body !== 'object' || body === null) return null;
  const refusal = body as Record<string, unknown>;
  return parseLegalVersions({
    termsVersion: refusal.currentTermsVersion,
    privacyVersion: refusal.currentPrivacyVersion,
  });
}

/**
 * The marketing site's legal pages. Root-relative on purpose: they are served by `site` at the
 * domain root, while `web` lives under the '/app' basePath. A next/link href would get '/app'
 * prepended and 404, so these must be plain anchors.
 */
export const LEGAL_TERMS_URL = '/legal/terms';
export const LEGAL_PRIVACY_URL = '/legal/privacy';

/** What a session carries about its own acceptances (the readable `reelbolt_user` cookie). */
export interface AcceptedLegalVersions {
  termsVersion?: string;
  privacyVersion?: string;
}

/**
 * Which document a session is behind on, or null when it is current — or when it cannot be told.
 *
 * - `'terms'`: the Terms moved (whatever the Privacy Policy did). That is a contract change and
 *   needs an explicit acceptance.
 * - `'privacy'`: only the Privacy Policy moved. A privacy notice is information, not consent, so
 *   /accept-terms shows an acknowledgement ("Continue"), never an "I agree" checkbox.
 *
 * A session with no `termsVersion` predates the gate and is left alone. A session with no
 * `privacyVersion` (issued before the Go API carried it) is never sent back for Privacy alone.
 */
export function legalChangeFor(
  accepted: AcceptedLegalVersions | null | undefined,
  served: LegalVersions | null | undefined,
): 'terms' | 'privacy' | null {
  if (!accepted || !served || typeof accepted.termsVersion !== 'string') return null;
  if (served.termsVersion !== accepted.termsVersion) return 'terms';
  if (typeof accepted.privacyVersion === 'string' && served.privacyVersion !== accepted.privacyVersion) {
    return 'privacy';
  }
  return null;
}
