/**
 * The account requests' mapping from server answers to the cases the UI has words for. Each server
 * contract is pinned here (inference AccountController, api/handlers/auth/account.go), so a change
 * on either side fails this file rather than turning a refusal into "something went wrong".
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  getDataExportStatus,
  getDataExportLink,
  requestAccountDeletion,
  requestDataExport,
} from './account';

function answer(status: number, body?: unknown) {
  vi.stubGlobal(
    'fetch',
    vi.fn().mockResolvedValue({
      ok: status >= 200 && status < 300,
      status,
      statusText: String(status),
      json: () => (body === undefined ? Promise.reject(new Error('no body')) : Promise.resolve(body)),
    }),
  );
}

afterEach(() => vi.unstubAllGlobals());

describe('requestDataExport', () => {
  it('maps every answer of POST /api/v1/account/export', async () => {
    answer(202, { status: 'accepted' });
    expect(await requestDataExport()).toEqual({ kind: 'accepted' });
    answer(409, { status: 'already_running' });
    expect(await requestDataExport()).toEqual({ kind: 'already_running' });
    answer(429, { status: 'too_soon', retryAfterSeconds: 61200 });
    expect(await requestDataExport()).toEqual({ kind: 'too_soon', retryAfterSeconds: 61200 });
    answer(404);
    expect(await requestDataExport()).toEqual({ kind: 'unavailable' });
  });

  it('throws on a failure nobody planned for', async () => {
    answer(500, { error: 'boom' });
    await expect(requestDataExport()).rejects.toThrow();
  });
});

describe('getDataExportLink', () => {
  it('returns the link when there is a copy', async () => {
    answer(200, { url: 'https://s3.example/exports/u/abc.zip?sig=1', expiresInSeconds: 900 });
    expect(await getDataExportLink()).toEqual({
      kind: 'ready',
      url: 'https://s3.example/exports/u/abc.zip?sig=1',
      expiresInSeconds: 900,
    });
  });

  it('tells "no copy" from "exports are switched off" by the body', async () => {
    answer(404, { status: 'none' });
    expect(await getDataExportLink()).toEqual({ kind: 'none' });
    // ASP.NET's empty NotFound() is ProblemDetails, whose status is the NUMBER 404.
    answer(404, { type: 'https://tools.ietf.org/html/rfc9110#section-15.5.5', title: 'Not Found', status: 404 });
    expect(await getDataExportLink()).toEqual({ kind: 'unavailable' });
  });
});

describe('getDataExportStatus', () => {
  it('reads whether a copy is being prepared and the newest copy on file', async () => {
    answer(200, {
      preparing: true,
      latest: { createdAt: '2026-10-10T09:00:00Z', sizeBytes: 1048576, expiresAt: '2026-10-17T09:00:00Z' },
      nextRequestAt: '2026-10-11T20:00:00Z',
    });
    expect(await getDataExportStatus()).toEqual({
      kind: 'ok',
      preparing: true,
      latest: { createdAt: '2026-10-10T09:00:00Z', sizeBytes: 1048576, expiresAt: '2026-10-17T09:00:00Z' },
      nextRequestAt: '2026-10-11T20:00:00Z',
    });
  });

  it('treats a missing copy and missing throttle as nulls, and a 404 as switched off', async () => {
    answer(200, { preparing: false, latest: null });
    expect(await getDataExportStatus()).toEqual({ kind: 'ok', preparing: false, latest: null, nextRequestAt: null });
    answer(404);
    expect(await getDataExportStatus()).toEqual({ kind: 'unavailable' });
  });
});

describe('requestAccountDeletion', () => {
  it('returns the date when the deletion is scheduled', async () => {
    answer(202, {
      status: 'deletion_scheduled',
      deletionDate: '2026-11-10T12:00:00Z',
      graceDays: 30,
      cancelBySigningIn: 'https://x/app/login',
    });
    expect(await requestAccountDeletion('pw')).toEqual({
      kind: 'scheduled',
      deletionDate: '2026-11-10T12:00:00Z',
      graceDays: 30,
    });
  });

  it('maps each refusal', async () => {
    answer(401, { error: 'password is incorrect' });
    expect(await requestAccountDeletion('pw')).toEqual({ kind: 'wrong_password' });
    answer(401, { error: 'unauthorized' });
    expect(await requestAccountDeletion('pw')).toEqual({ kind: 'signed_out' });
    answer(400, { error: 'password is required' });
    expect(await requestAccountDeletion('')).toEqual({ kind: 'password_required' });
    answer(409, { error: 'sole_owner', organization: 'Acme', message: '…' });
    expect(await requestAccountDeletion('pw')).toEqual({ kind: 'sole_owner', organization: 'Acme' });
    answer(409, { error: 'provider_confirmation_required', provider: 'google', confirmUrl: 'https://accounts.google.com/x', message: '…' });
    expect(await requestAccountDeletion('')).toEqual({
      kind: 'provider_confirmation',
      provider: 'google',
      confirmUrl: 'https://accounts.google.com/x',
    });
    answer(409, { error: 'confirmation_unavailable', message: '…' });
    expect(await requestAccountDeletion('')).toEqual({ kind: 'confirmation_unavailable' });
  });

  it('sends the password in the body', async () => {
    answer(202, { deletionDate: '2026-11-10T12:00:00Z', graceDays: 30 });
    await requestAccountDeletion('s3cret');
    const [url, init] = (fetch as unknown as ReturnType<typeof vi.fn>).mock.calls[0];
    expect(url).toBe('/api/v1/auth/account/delete');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body)).toEqual({ password: 's3cret' });
  });

  it('throws on a 503', async () => {
    answer(503, { error: 'service unavailable' });
    await expect(requestAccountDeletion('pw')).rejects.toThrow();
  });
});
