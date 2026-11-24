/**
 * The runner API (WP E4b): pairing a desktop machine into a workspace, listing the workspace's
 * paired machines, and revoking them. Shapes mirror api/handlers/runners/dto.go; the endpoints
 * themselves are in api/handlers/runners/runners.go.
 *
 * Two halves live here and they have different audiences:
 *
 *   - The DEVICE's half (POST /runners/pair/start, /poll, /token) is unauthenticated and belongs to
 *     the runner daemon, which is a Go binary that does not import this file. Nothing here drives it.
 *   - The USER's half (GET/POST /runners/pair/{code}..., GET /runners, DELETE /runners/{id}) is what
 *     the dashboard calls, and it is what this module is for.
 *
 * Security properties this layer must not blur (they are the server's, not the client's, but the
 * UI has to report them the same way):
 *
 *   1. Approval authority is judged in the workspace the caller NAMED, and a workspace the caller is
 *      not a member of, one that does not exist and the platform's own workspace all answer 404 —
 *      never 403. So a 404 out of /approve never means "you may not"; it means "no such pairing
 *      request or no such workspace", and describePairingError says exactly that.
 *   2. Revoking is allowed for an Owner or Admin of the device's workspace, OR for the user who
 *      paired it. The dashboard cannot tell which user paired a device (the session cookie carries
 *      no user id), so it never hides the control; the server decides and the UI reports its answer.
 *   3. Every Owner of the workspace is emailed when a device is approved. That is an anti-phishing
 *      control, and the approval screen says so.
 */

import { apiFetch, ApiError } from './client';
import type {
  RunnerDevice,
  RunnerPairing,
  ApprovePairingResponse,
  RevokeRunnerResponse,
} from '../types/runner';
import { readableServerMessage } from './describe-error';

// ---- codes ----

/**
 * The 32 symbols the API's user codes are drawn from (api/handlers/runners/runners.go).
 * 0/O and 1/I are absent because users transcribe them wrongly.
 */
const USER_CODE_ALPHABET = '23456789ABCDEFGHJKLMNPQRSTUVWXYZ';
const USER_CODE_LENGTH = 8;

/**
 * Normalize a code the way the API does: any case, with or without the dash or spaces.
 * Returns null for anything that is not eight characters of the alphabet, so the caller can refuse
 * a typo without a round trip. The server normalizes identically, so a value this accepts is one
 * the server will accept, and a value it rejects would have been a 404 anyway — the same answer,
 * which is the point (an unknown, malformed, expired and already-settled code are indistinguishable
 * by design).
 */
export function normalizeUserCode(input: string): string | null {
  let out = '';
  for (const ch of input.toUpperCase().trim()) {
    if (ch === '-' || ch === ' ') continue;
    if (!USER_CODE_ALPHABET.includes(ch)) return null;
    out += ch;
  }
  return out.length === USER_CODE_LENGTH ? out : null;
}

/** Render a normalized code the way the user reads it: XXXX-XXXX. Anything else is returned as-is. */
export function formatUserCode(code: string): string {
  if (code.length !== USER_CODE_LENGTH) return code;
  return code.slice(0, 4) + '-' + code.slice(4);
}

// ---- the user's side of pairing ----

/**
 * The URL a pairing request is read from. Exported so an SWR cache key and the request itself can
 * never drift apart (the same reason lib/api/inference-providers.ts exports its selectable URL).
 */
export function pairingRequestUrl(userCode: string): string {
  return '/api/v1/runners/pair/' + encodeURIComponent(userCode);
}

/**
 * GET /api/v1/runners/pair/{code} — what the device asking to pair is, for the approval screen.
 * 404 for an unknown, malformed, expired or already-settled code: all four are the same answer.
 */
export function getPairingRequest(userCode: string): Promise<RunnerPairing> {
  return apiFetch<RunnerPairing>(pairingRequestUrl(userCode));
}

/**
 * POST /api/v1/runners/pair/{code}/approve — attach the device to a workspace and create it.
 *
 * The workspace id is the one the caller NAMED; the server authorizes against THAT workspace's
 * membership, not the caller's active one.
 */
export function approvePairing(userCode: string, organizationId: string): Promise<ApprovePairingResponse> {
  return apiFetch<ApprovePairingResponse>(
    '/api/v1/runners/pair/' + encodeURIComponent(userCode) + '/approve',
    { method: 'POST', body: JSON.stringify({ organizationId }) },
  );
}

/**
 * POST /api/v1/runners/pair/{code}/deny — refuse the request. Answers 204, and needs no workspace:
 * refusing is never an authority question.
 */
export function denyPairing(userCode: string): Promise<void> {
  return apiFetch<void>('/api/v1/runners/pair/' + encodeURIComponent(userCode) + '/deny', {
    method: 'POST',
  });
}

// ---- devices ----

/**
 * GET /api/v1/runners — the ACTIVE paired machines of the caller's active workspace. Revoked devices
 * are excluded (they cannot run work), so a revoked device simply disappears from this list.
 */
export function getRunners(): Promise<RunnerDevice[]> {
  return apiFetch<RunnerDevice[]>('/api/v1/runners');
}

/**
 * DELETE /api/v1/runners/{id} — revoke a device. Revoking twice is not an error; the response says
 * whether this call was the one that did it.
 *
 * A 403 here means the caller is neither an Owner/Admin of the device's workspace nor the user who
 * paired it; a 404 means the device is unknown, belongs to another workspace, or is a pool worker —
 * the server looks it up without an organization filter precisely so those four cases are one answer.
 */
export function revokeRunner(id: string): Promise<RevokeRunnerResponse> {
  return apiFetch<RevokeRunnerResponse>('/api/v1/runners/' + encodeURIComponent(id), {
    method: 'DELETE',
  });
}

// ---- errors ----

/**
 * The machine-readable `error` code a failing response carries, when it carries one. Every failure
 * from this package is a single-key JSON body (the Go handler's `fail`), and the code — not the
 * prose — is what the UI maps to a sentence.
 */
export function runnerErrorCode(error: unknown): string | null {
  if (!(error instanceof ApiError)) return null;
  const body = error.body;
  if (typeof body !== 'object' || body === null) return null;
  const code = (body as Record<string, unknown>).error;
  return typeof code === 'string' && code !== '' ? code : null;
}

/**
 * The single sentence every 404 from the pairing endpoints gets.
 *
 * It has to be true for each of the causes the server deliberately merges into one status: an
 * unknown code, a malformed one, an expired one, one that already settled, a workspace the caller
 * is not a member of, a workspace that does not exist and the platform's own workspace.
 */
const PAIRING_NOT_AVAILABLE =
  'That pairing request is not available. It may have expired or been used already, or the ' +
  'workspace you picked may not be one you can approve into. Ask the device for a fresh code and ' +
  'try again.';

/**
 * The sentence to show for a failed pairing lookup or approval.
 *
 * The 404 wording is deliberately ONE sentence for every cause: an unknown code, a malformed one, an
 * expired one, one that already settled, a workspace the caller is not a member of, a workspace that
 * does not exist, and the platform's own workspace are indistinguishable to the server on purpose
 * (D5), and the UI must not invent a distinction the server refuses to make. In particular it never
 * renders "you do not have permission" for a 404 — that is what the server would have said with a
 * 403, and it never does.
 */
export function describePairingError(error: unknown): string {
  const code = runnerErrorCode(error);

  // ONE sentence, covering both causes a 404 can have here (the request is gone, or the workspace
  // is not one the caller may use). Splitting them would be exactly the distinction the server
  // refuses to make, and the sentence is written so it is true whichever it was.
  if (code === 'pairing request not found' || code === 'organization not found') {
    return PAIRING_NOT_AVAILABLE;
  }
  if (code === 'insufficient_role') {
    return 'Your role in that workspace cannot approve devices. Ask an Owner or Admin to do it.';
  }
  if (code === 'already_paired') {
    return 'That machine is already paired to a workspace. Revoke the existing device first, then pair it again.';
  }
  if (code === 'pairing_request_settled') {
    return 'That pairing request has already been approved or denied. Ask the device for a fresh code.';
  }
  if (code === 'invalid_organization') {
    return 'Choose the workspace this machine should join.';
  }
  if (code === 'no_active_organization') {
    return 'Your session has no active workspace. Switch to one and try again.';
  }
  if (code === 'too_many_requests') {
    return 'Too many pairing attempts from this network. Wait a minute and try again.';
  }

  if (error instanceof ApiError && error.status === 404) {
    // A 404 with an unrecognised body still gets the one-answer sentence rather than the bare code.
    return PAIRING_NOT_AVAILABLE;
  }
  const own = readableServerMessage(error);
  if (own) return own;
  return 'Something went wrong. Try again.';
}

/**
 * The sentence to show for a failed revoke. A 404 covers an unknown device, one in another
 * workspace and a pool worker; a 403 is the real refusal (not an Owner/Admin, and not the user who
 * paired it).
 */
export function describeRevokeError(error: unknown): string {
  const code = runnerErrorCode(error);
  if (code === 'device not found' || (error instanceof ApiError && error.status === 404)) {
    return 'That device is no longer in this workspace. It may have been revoked already.';
  }
  if (code === 'insufficient_role') {
    return 'Only an Owner or Admin of this workspace, or the person who paired the device, can revoke it.';
  }
  const own = readableServerMessage(error);
  if (own) return own;
  return 'Could not revoke the device. Try again.';
}
