import { ApiError, apiFetch } from './client';

/**
 * The data subject's own account requests: a copy of their data, and closing the account.
 *
 * The two live on different services — the export is the Inference API's
 * (`inference/.../Controllers/AccountController.cs`), the deletion the Go API's
 * (`api/handlers/auth/account.go`), because the deletion has to re-verify the password and kill the
 * session — but they are one feature to the person using them, so they share this module.
 *
 * Every refusal the server can give is turned into a tagged result rather than thrown, because each
 * one is an ordinary, expected answer the UI has its own words for. Only a failure nobody planned
 * for (a 500, a network error) is thrown.
 */

function bodyField(body: unknown, key: string): unknown {
  return typeof body === 'object' && body !== null ? (body as Record<string, unknown>)[key] : undefined;
}

function stringField(body: unknown, key: string): string {
  const value = bodyField(body, key);
  return typeof value === 'string' ? value : '';
}

// ---------------------------------------------------------------------------------------------
// Export
// ---------------------------------------------------------------------------------------------

export type ExportRequestResult =
  /** Queued: the archive is built in the background and mailed when it is ready. */
  | { kind: 'accepted' }
  /** This server is already building one for the caller. */
  | { kind: 'already_running' }
  /** One request per day: another is possible after `retryAfterSeconds`. */
  | { kind: 'too_soon'; retryAfterSeconds: number }
  /** Exports are switched off on this server (`Account:Export:Enabled`). */
  | { kind: 'unavailable' };

/** `POST /api/v1/account/export`. */
export async function requestDataExport(): Promise<ExportRequestResult> {
  try {
    await apiFetch<unknown>('/api/v1/account/export', { method: 'POST' });
    return { kind: 'accepted' };
  } catch (err) {
    if (!(err instanceof ApiError)) throw err;
    if (err.status === 409) return { kind: 'already_running' };
    if (err.status === 429) {
      const seconds = Number(bodyField(err.body, 'retryAfterSeconds'));
      return { kind: 'too_soon', retryAfterSeconds: Number.isFinite(seconds) && seconds > 0 ? seconds : 24 * 3600 };
    }
    if (err.status === 404) return { kind: 'unavailable' };
    throw err;
  }
}

export type ExportLinkResult =
  /** A fresh, short-lived link to the caller's newest archive. */
  | { kind: 'ready'; url: string; expiresInSeconds: number }
  /** No archive is stored for the caller (never asked, still being built, or already expired). */
  | { kind: 'none' }
  /** Exports are switched off on this server. */
  | { kind: 'unavailable' };

/**
 * `GET /api/v1/account/export`. Both "no archive" and "exports are off" are a 404; they are told
 * apart by the body: only the former carries `{ status: "none" }` (the latter is an empty 404, which
 * ASP.NET renders as ProblemDetails whose `status` is the number 404, never the string).
 */
export async function getDataExportLink(): Promise<ExportLinkResult> {
  try {
    const res = await apiFetch<{ url: string; expiresInSeconds: number }>('/api/v1/account/export');
    return { kind: 'ready', url: res.url, expiresInSeconds: res.expiresInSeconds };
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) {
      return bodyField(err.body, 'status') === 'none' ? { kind: 'none' } : { kind: 'unavailable' };
    }
    throw err;
  }
}

export type ExportStatusResult =
  | {
      kind: 'ok';
      /** A copy was asked for and has not landed yet. */
      preparing: boolean;
      /** The newest copy on file, or null when there is none. */
      latest: { createdAt: string; sizeBytes: number | null; expiresAt: string | null } | null;
      /** When another copy can be asked for; null when it can be asked for now. */
      nextRequestAt: string | null;
    }
  /** Exports are switched off on this server. */
  | { kind: 'unavailable' };

/**
 * `GET /api/v1/account/export/status`: whether a copy is being prepared, the newest copy's date,
 * size and expiry, and when another request is accepted. It never carries a link; the link is
 * minted only when the person clicks Download.
 */
export async function getDataExportStatus(): Promise<ExportStatusResult> {
  try {
    const res = await apiFetch<{
      preparing: boolean;
      latest: { createdAt: string; sizeBytes?: number | null; expiresAt?: string | null } | null;
      nextRequestAt?: string | null;
    }>('/api/v1/account/export/status');
    return {
      kind: 'ok',
      preparing: Boolean(res.preparing),
      latest: res.latest
        ? { createdAt: res.latest.createdAt, sizeBytes: res.latest.sizeBytes ?? null, expiresAt: res.latest.expiresAt ?? null }
        : null,
      nextRequestAt: res.nextRequestAt ?? null,
    };
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) return { kind: 'unavailable' };
    throw err;
  }
}

// ---------------------------------------------------------------------------------------------
// Deletion
// ---------------------------------------------------------------------------------------------

export type DeleteAccountResult =
  /** Scheduled. The server has already signed this browser out. */
  | { kind: 'scheduled'; deletionDate: string; graceDays: number }
  | { kind: 'wrong_password' }
  /** An empty password from an account that has one. */
  | { kind: 'password_required' }
  /** The caller is the only owner of a team that still has other members. */
  | { kind: 'sole_owner'; organization: string }
  /** A Google/GitHub account: the browser has to complete the sign-in round trip at `confirmUrl`. */
  | { kind: 'provider_confirmation'; provider: string; confirmUrl: string }
  /** A Google/GitHub account whose sign-in service is not set up on this server. */
  | { kind: 'confirmation_unavailable' }
  /** The session is gone (expired, or signed out elsewhere). */
  | { kind: 'signed_out' };

/**
 * `POST /api/v1/auth/account/delete`. An empty `password` asks for the provider confirmation instead,
 * which the server offers only to an account that signs in with Google or GitHub.
 */
export async function requestAccountDeletion(password: string): Promise<DeleteAccountResult> {
  try {
    const res = await apiFetch<{ deletionDate: string; graceDays: number }>('/api/v1/auth/account/delete', {
      method: 'POST',
      body: JSON.stringify({ password }),
    });
    return { kind: 'scheduled', deletionDate: res.deletionDate, graceDays: res.graceDays };
  } catch (err) {
    if (!(err instanceof ApiError)) throw err;
    const code = stringField(err.body, 'error');
    if (err.status === 401) {
      return code === 'password is incorrect' ? { kind: 'wrong_password' } : { kind: 'signed_out' };
    }
    if (err.status === 400 && code === 'password is required') return { kind: 'password_required' };
    if (err.status === 409) {
      if (code === 'sole_owner') return { kind: 'sole_owner', organization: stringField(err.body, 'organization') };
      if (code === 'provider_confirmation_required') {
        return {
          kind: 'provider_confirmation',
          provider: stringField(err.body, 'provider'),
          confirmUrl: stringField(err.body, 'confirmUrl'),
        };
      }
      if (code === 'confirmation_unavailable') return { kind: 'confirmation_unavailable' };
    }
    throw err;
  }
}
