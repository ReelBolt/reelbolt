import { handleUnauthorized, unauthorizedMessage } from '../auth/session-expiry';

export class ApiError extends Error {
  status: number;
  /** The individual validation messages of a 400 `{ message, errors }` body, when it carried any. */
  errors: string[];
  /**
   * The decoded JSON body, when the response carried one. Every consumer that only needs a sentence
   * keeps reading `message`; this is for the few refusals whose body carries STRUCTURED facts the UI
   * must act on — the 402 `INSUFFICIENT_CREDITS` body's `required`/`balance` (B7), where the shortfall
   * is the whole point of the state and re-parsing the prose would only be a second, worse copy of it.
   * `undefined` for a non-JSON body.
   */
  body?: unknown;
  /**
   * The machine-readable refusal code from the body — `code` first, then `error` — or null when the
   * body carried neither. Two sibling conventions exist on the server: the plan/billing gate answers
   * `{ code, feature, message, limit, current }` (`STORAGE_QUOTA_EXCEEDED`, `INSUFFICIENT_CREDITS`)
   * and the sync routes answer `{ error: "reconnect_required" }`. Consumers that already dig into
   * `body` themselves (see `cloudErrorCode` in lib/api/cloudStorage.ts) keep working unchanged: this
   * is a convenience, never a replacement.
   */
  code: string | null;
  /** Seconds from the `Retry-After` header of a refusal (429), when it carried a usable one. */
  retryAfterSeconds?: number;
  constructor(message: string, status: number, errors: string[] = [], body?: unknown) {
    super(message);
    this.status = status;
    this.errors = errors;
    this.body = body;
    this.code = refusalCodeOf(body);
  }
}

/** The refusal code of a decoded error body: `code`, else `error`, else null. */
function refusalCodeOf(body: unknown): string | null {
  if (typeof body !== 'object' || body === null) return null;
  const b = body as Record<string, unknown>;
  for (const key of ['code', 'error'] as const) {
    const value = b[key];
    if (typeof value === 'string' && value.trim()) return value;
  }
  return null;
}

/** Every non-empty string in a body's `errors` (array, or ProblemDetails `{Field: [msg]}`). */
export function extractErrorList(body: unknown): string[] {
  if (typeof body !== 'object' || body === null) return [];
  const errors = (body as Record<string, unknown>).errors;
  const out: string[] = [];
  const add = (v: unknown) => {
    if (typeof v === 'string' && v.trim()) out.push(v);
    else if (Array.isArray(v)) v.forEach(add);
  };
  if (Array.isArray(errors)) add(errors);
  else if (errors && typeof errors === 'object') Object.values(errors).forEach(add);
  return out;
}

function nonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0;
}

/**
 * Extracts a user-facing message from a non-ok JSON body. `message`/`error` first; then ASP.NET
 * ProblemDetails: `errors` flattened (`{Field: [msg, ...]}` or a custom `[msg, ...]`) joined with
 * '; ', then `detail`, then `title`. Returns null when nothing usable is present.
 */
export function extractErrorMessage(body: unknown): string | null {
  if (typeof body !== 'object' || body === null) return null;
  const b = body as Record<string, unknown>;

  if (nonEmptyString(b.message)) return b.message;
  if (nonEmptyString(b.error)) return b.error;

  const errors = b.errors;
  const collected: string[] = [];
  const collect = (value: unknown) => {
    if (nonEmptyString(value)) collected.push(value);
    else if (Array.isArray(value)) value.forEach((v) => { if (nonEmptyString(v)) collected.push(v); });
  };
  if (Array.isArray(errors)) collect(errors);
  else if (typeof errors === 'object' && errors !== null) Object.values(errors).forEach(collect);
  if (collected.length > 0) return collected.join('; ');

  if (nonEmptyString(b.detail)) return b.detail;
  if (nonEmptyString(b.title)) return b.title;
  return null;
}

export async function apiFetch<T>(url: string, options?: RequestInit): Promise<T> {
  const res = await fetch(url, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      ...options?.headers,
    },
  });

  if (!res.ok) {
    let message = res.statusText;
    let errors: string[] = [];
    let body: unknown;
    try {
      body = await res.json();
      message = extractErrorMessage(body) || message;
      errors = extractErrorList(body);
    } catch {}
    // A 401 on an authenticated call is a session expiry: one central redirect, and a sentence
    // instead of the server's raw "Unauthorized" for whichever toast or banner shows the error.
    handleUnauthorized(res.status, url);
    message = unauthorizedMessage(res.status, url) ?? message;
    const apiError = new ApiError(message, res.status, errors, body);
    const retryAfter = Number(res.headers?.get('Retry-After'));
    if (Number.isFinite(retryAfter) && retryAfter > 0) apiError.retryAfterSeconds = retryAfter;
    throw apiError;
  }

  if (res.status === 204) return undefined as T;
  return res.json();
}

export const fetcher = <T>(url: string): Promise<T> => apiFetch<T>(url);
