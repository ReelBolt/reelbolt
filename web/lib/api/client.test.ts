import { describe, it, expect, vi, afterEach } from 'vitest';
import { apiFetch, ApiError, extractErrorMessage } from './client';

function mockFetchOnce(status: number, body: unknown, statusText = 'Bad Request') {
  const json = typeof body === 'string'
    ? () => Promise.reject(new SyntaxError('not json'))
    : () => Promise.resolve(body);
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: status >= 200 && status < 300, status, statusText, json }));
}

async function errorOf(promise: Promise<unknown>): Promise<ApiError> {
  try {
    await promise;
  } catch (err) {
    return err as ApiError;
  }
  throw new Error('expected rejection');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('extractErrorMessage', () => {
  it('prefers message, then error', () => {
    expect(extractErrorMessage({ message: 'm', error: 'e', title: 't' })).toBe('m');
    expect(extractErrorMessage({ error: 'e', title: 't' })).toBe('e');
  });

  it('flattens a ProblemDetails errors object, joined with "; "', () => {
    expect(
      extractErrorMessage({
        title: 'One or more validation errors occurred.',
        errors: { MaxSpendCredits: ['Max Spend (credits) must be greater than 0'], Takes: ['a', 'b'] },
      }),
    ).toBe('Max Spend (credits) must be greater than 0; a; b');
  });

  it('flattens a custom errors array', () => {
    expect(extractErrorMessage({ errors: ['first', 'second'], title: 'Bad' })).toBe('first; second');
  });

  it('falls back to detail, then title', () => {
    expect(extractErrorMessage({ title: 'Bad Request', detail: 'The detail' })).toBe('The detail');
    expect(extractErrorMessage({ title: 'Bad Request', errors: {} })).toBe('Bad Request');
    expect(extractErrorMessage({ title: 'Bad Request', errors: [] })).toBe('Bad Request');
  });

  it('returns null for unusable bodies', () => {
    expect(extractErrorMessage(null)).toBeNull();
    expect(extractErrorMessage('text')).toBeNull();
    expect(extractErrorMessage({})).toBeNull();
  });
});

describe('apiFetch errors', () => {
  it('surfaces a backend 400 ProblemDetails message as ApiError.message', async () => {
    mockFetchOnce(400, { title: 'One or more validation errors occurred.', errors: { InlinePrompt: ['Inline Prompt is required'] } });
    const err = await errorOf(apiFetch('/api/v1/x'));
    expect(err).toBeInstanceOf(ApiError);
    expect(err.status).toBe(400);
    expect(err.message).toBe('Inline Prompt is required');
  });

  it('keeps body.message first', async () => {
    mockFetchOnce(409, { message: 'Conflict happened', title: 'Conflict' }, 'Conflict');
    expect((await errorOf(apiFetch('/api/v1/x'))).message).toBe('Conflict happened');
  });

  it('surfaces a 400 RUN_ALREADY_ACTIVE body message as ApiError.message', async () => {
    mockFetchOnce(400, { code: 'RUN_ALREADY_ACTIVE', message: 'This run is already running or queued.' });
    const err = await errorOf(apiFetch('/api/v1/x'));
    expect(err.status).toBe(400);
    expect(err.message).toBe('This run is already running or queued.');
  });

  it('falls back to statusText for a non-JSON body', async () => {
    mockFetchOnce(500, 'oops', 'Internal Server Error');
    expect((await errorOf(apiFetch('/api/v1/x'))).message).toBe('Internal Server Error');
  });

  it('falls back to statusText for a JSON body with nothing usable', async () => {
    mockFetchOnce(400, {}, 'Bad Request');
    expect((await errorOf(apiFetch('/api/v1/x'))).message).toBe('Bad Request');
  });

  it('carries the decoded body, for the refusals whose structured fields the UI acts on', async () => {
    mockFetchOnce(402, { code: 'INSUFFICIENT_CREDITS', required: 40, balance: 12 }, 'Payment Required');
    const err = await errorOf(apiFetch('/api/v1/x'));

    expect(err.status).toBe(402);
    expect(err.body).toEqual({ code: 'INSUFFICIENT_CREDITS', required: 40, balance: 12 });
  });

  it('leaves the body undefined when there was no JSON to decode', async () => {
    mockFetchOnce(500, 'oops', 'Internal Server Error');
    expect((await errorOf(apiFetch('/api/v1/x'))).body).toBeUndefined();
  });
});
