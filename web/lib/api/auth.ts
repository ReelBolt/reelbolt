import { apiFetch } from './client';
import { wipeEditorLocalData } from '../editing/sync/user-scope';
import type {
  AcceptedResponse,
  AcceptTermsRequest,
  ChangePasswordRequest,
  LoginRequest,
  LoginResponse,
  ResendVerificationRequest,
  ResetPasswordRequest,
  SignupRequest,
  VerifyEmailRequest,
} from '../types/auth';

export async function login(data: LoginRequest): Promise<LoginResponse> {
  return apiFetch<LoginResponse>('/api/v1/auth/token', {
    method: 'POST',
    body: JSON.stringify(data),
  });
}

/**
 * Creates an unverified account and answers 202 with a uniform message whether or not the address
 * already belongs to one (api/handlers/auth/signup.go) — callers must never treat the response as
 * proof that an account was created, and never tell the visitor which case it was.
 *
 * With an `inviteToken` the address is already proven, so the API answers 200 with a session
 * (cookies included) instead, like verify-email does.
 */
export async function signup(data: SignupRequest): Promise<AcceptedResponse> {
  return apiFetch<AcceptedResponse>('/api/v1/auth/signup', {
    method: 'POST',
    body: JSON.stringify(data),
  });
}

/** Redeems a verification link. On success the API logs the user in, exactly like `login`. */
export async function verifyEmail(data: VerifyEmailRequest): Promise<LoginResponse> {
  return apiFetch<LoginResponse>('/api/v1/auth/verify-email', {
    method: 'POST',
    body: JSON.stringify(data),
  });
}

/** Always 202, whether or not the address is an unverified account. */
export async function resendVerification(data: ResendVerificationRequest): Promise<AcceptedResponse> {
  return apiFetch<AcceptedResponse>('/api/v1/auth/resend-verification', {
    method: 'POST',
    body: JSON.stringify(data),
  });
}

/** Always 202, whether or not the address is an account. */
export async function forgotPassword(data: ResendVerificationRequest): Promise<AcceptedResponse> {
  return apiFetch<AcceptedResponse>('/api/v1/auth/forgot-password', {
    method: 'POST',
    body: JSON.stringify(data),
  });
}

/** Redeems a reset link. Deliberately does NOT sign the user in (the API kills every session). */
export async function resetPassword(data: ResetPasswordRequest): Promise<{ message: string }> {
  return apiFetch<{ message: string }>('/api/v1/auth/reset-password', {
    method: 'POST',
    body: JSON.stringify(data),
  });
}

/**
 * Records a legal acceptance for the signed-in user and re-issues the session
 * (`POST /api/v1/auth/accept-terms`, api/handlers/auth/legal.go). The response sets the session
 * cookies again, which is what carries the newly accepted version into the readable
 * `reelbolt_user` cookie that web/proxy.ts compares against.
 */
export async function acceptTerms(data: AcceptTermsRequest): Promise<LoginResponse> {
  return apiFetch<LoginResponse>('/api/v1/auth/accept-terms', {
    method: 'POST',
    body: JSON.stringify(data),
  });
}

export async function changePassword(data: ChangePasswordRequest): Promise<void> {
  await apiFetch<void>('/api/v1/auth/change-password', {
    method: 'POST',
    body: JSON.stringify(data),
  });
}

export async function logout(): Promise<void> {
  // Explicit logout wipes the editor's unsaved documents from this device (best effort).
  const wipe = wipeEditorLocalData().catch(() => undefined);
  await fetch('/api/auth/logout', { method: 'POST' });
  await Promise.race([wipe, new Promise<void>((resolve) => setTimeout(resolve, 1500))]);
}
