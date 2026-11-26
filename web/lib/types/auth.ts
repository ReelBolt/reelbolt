import type { Persona } from '@/lib/attribution/persona';

export interface LoginRequest {
  email: string;
  password: string;
}

export interface LoginResponse {
  accessToken: string;
  tokenType: string;
  expiresIn: number;
  mustChangePassword: boolean;
  /** The Go API sends these too (TokenResponse, api/handlers/auth/dto.go). */
  email?: string;
  isPlatformAdmin?: boolean;
  emailVerified?: boolean;
  org?: string;
  orgName?: string;
  orgKind?: string;
  orgRole?: string;
  /** The terms version the session accepted, once the Go cookie carries it (C7-UI). */
  termsVersion?: string;
  /** The latest Privacy version the user ACCEPTED (Go `TokenResponse.PrivacyVersion`, omitempty). */
  privacyVersion?: string;
  /** The saved interface language (a code in i18n/locales.json); absent until the user picks one. */
  locale?: string;
  /** True when this sign-in cancelled a pending account deletion (C9b); absent otherwise. */
  deletionCancelled?: boolean;
}

export interface SignupRequest {
  email: string;
  password: string;
  /** Optional: the sign-up form no longer asks; the API derives a name from the address. */
  displayName?: string;
  acceptTerms: boolean;
  termsVersion: string;
  privacyVersion: string;
  /** Required by the API only when TURNSTILE_SECRET is set on it (C5). */
  turnstileToken?: string;
  /** A team invite token: the account is created already verified and joins the org (C8). */
  inviteToken?: string;
  /** The optional marketing-email box. Never required; signup completes the same either way. */
  marketingOptIn?: boolean;
  /** The wording version the box showed; sent only with an opt-in. A stale one → 400. */
  marketingWordingVersion?: string;
  /**
   * The audience token the signup link carried (`?for=creator`), one of the closed set in
   * lib/attribution/persona.ts. The API stores it as a suggestion on a NEW account and drops
   * anything else; it never refuses a signup over it (docs/design/new-user-funnel.md section 6).
   */
  persona?: Persona;
  /** The plan a pricing card started, sanitized; the API stores it for resuming checkout (best effort). */
  plan?: string;
  /** The campaign parameters of the signup link. Omitted when the link carried none. */
  attribution?: SignupAttribution;
}

/**
 * `SignupRequest.attribution`: the `utm_*` values of the signup link, plus the landing path when
 * the signup page itself was the landing. Every field is optional and sanitized like the
 * first-touch cookie (`^[a-z0-9][a-z0-9._~+-]{0,63}$`, lowercased and trimmed). The API records
 * them only when its URL capture switch is on, and only when no consented cookie row exists.
 */
export interface SignupAttribution {
  source?: string;
  medium?: string;
  campaign?: string;
  content?: string;
  term?: string;
  landingPath?: string;
}

/**
 * The uniform body of every enumeration-safe response — signup, resend-verification and
 * forgot-password all answer this, whether or not the address belongs to an account.
 */
export interface AcceptedResponse {
  message: string;
}

export interface VerifyEmailRequest {
  token: string;
}

export interface ResendVerificationRequest {
  email: string;
  turnstileToken?: string;
}

export interface ResetPasswordRequest {
  token: string;
  newPassword: string;
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

/**
 * The re-acceptance an already-signed-in user posts from /accept-terms. Same field names and rules
 * as the signup form's legal pair, because both write the same `legal_acceptances` rows.
 */
export interface AcceptTermsRequest {
  acceptTerms: boolean;
  /**
   * Set by the privacy-only acknowledgement (with `acceptTerms: false`): the API then records only
   * a Privacy acknowledgement and answers 409 when the user's stored Terms version is not current
   * (S-23). Never sent by the full Terms acceptance.
   */
  privacyAcknowledgement?: boolean;
  termsVersion: string;
  privacyVersion: string;
}

export type OrgKind = 'Personal' | 'Team' | 'Platform';
// The authoritative set is the Go API's (api/models/organization.go: OrgRoleOwner/Admin/Member/Billing)
// and ReelBolt.Shared.Tenancy.OrganizationRole. 'Viewer' was never one of them.
export type OrgRole = 'Owner' | 'Admin' | 'Member' | 'Billing';

export interface UserInfo {
  email: string;
  /** The name shown in greetings; always present on the cookie the API issues, may be empty. */
  displayName?: string;
  /** @deprecated alias of isPlatformAdmin (kept by the API for one release). */
  isAdmin?: boolean;
  isPlatformAdmin?: boolean;
  mustChangePassword: boolean;
  emailVerified?: boolean;
  /** Active organization id, display name, kind and the caller's role in it. */
  org?: string;
  orgName?: string;
  orgKind?: string;
  orgRole?: string;
  /** The terms version this session accepted; absent until the Go cookie carries it (C7-UI). */
  termsVersion?: string;
  /**
   * The latest Privacy version this user accepted; absent on sessions issued before the Go API
   * carried it, and the proxy's privacy gate leaves those alone.
   */
  privacyVersion?: string;
  /** The saved interface language (a code in i18n/locales.json); absent until the user picks one. */
  locale?: string;
}
