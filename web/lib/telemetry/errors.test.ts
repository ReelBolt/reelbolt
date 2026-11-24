// @vitest-environment jsdom
/**
 * Client error reporting (alpha-launch T-018). What these protect:
 *   - with consent withheld, no report leaves the browser;
 *   - a report carries only the allowlisted fields, and the route loses its query string;
 *   - with telemetry switched off, nothing is reported.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const H = vi.hoisted(() => ({
  sendClientError: vi.fn(),
}));

vi.mock('./client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./client')>();
  return {
    ...actual,
    sendClientError: (payload: unknown) => H.sendClientError(payload),
  };
});

import { CLIENT_ERROR_FIELDS } from './allowlist';
import { TELEMETRY_CONSENT_STORAGE_KEY } from './consent';
import { reportClientError } from './errors';

beforeEach(() => {
  window.localStorage.clear();
  H.sendClientError.mockClear();
  process.env.NEXT_PUBLIC_TELEMETRY_ENABLED = 'true';
  window.history.replaceState({}, '', '/app/projects?tab=recent&token=secret#top');
});

afterEach(() => {
  delete process.env.NEXT_PUBLIC_TELEMETRY_ENABLED;
});

describe('reportClientError', () => {
  it('reports nothing when consent is withheld', () => {
    window.localStorage.setItem(TELEMETRY_CONSENT_STORAGE_KEY, 'denied');

    expect(reportClientError(new TypeError('boom'))).toBe(false);
    expect(H.sendClientError).not.toHaveBeenCalled();
  });

  it('reports nothing when no choice has been made', () => {
    expect(reportClientError(new TypeError('boom'))).toBe(false);
    expect(H.sendClientError).not.toHaveBeenCalled();
  });

  it('reports nothing when telemetry is off for the build', () => {
    window.localStorage.setItem(TELEMETRY_CONSENT_STORAGE_KEY, 'granted');
    process.env.NEXT_PUBLIC_TELEMETRY_ENABLED = 'false';

    expect(reportClientError(new TypeError('boom'))).toBe(false);
    expect(H.sendClientError).not.toHaveBeenCalled();
  });

  it('reports the allowlisted fields only, with the redacted pathname as the route', () => {
    window.localStorage.setItem(TELEMETRY_CONSENT_STORAGE_KEY, 'granted');

    expect(reportClientError(new TypeError('boom for secret@example.com'))).toBe(true);
    expect(H.sendClientError).toHaveBeenCalledTimes(1);

    const payload = H.sendClientError.mock.calls[0][0] as Record<string, unknown>;
    expect(Object.keys(payload).sort()).toEqual([...CLIENT_ERROR_FIELDS].sort());
    expect(payload.name).toBe('TypeError');
    // The query string (and any token in it) is gone; the pathname is what travels.
    expect(payload.route).toBe('/app/projects');
    // The message — which can carry user content — is not collected at all.
    expect(JSON.stringify(payload)).not.toContain('secret@example.com');
    expect(payload).not.toHaveProperty('message');
    expect(payload).not.toHaveProperty('stack');
  });

  it('redacts an opaque segment, so an invite token never leaves the browser', () => {
    window.localStorage.setItem(TELEMETRY_CONSENT_STORAGE_KEY, 'granted');

    reportClientError(new TypeError('boom'), { route: '/invite/u1.abc.def' });

    const payload = H.sendClientError.mock.calls[0][0] as Record<string, unknown>;
    expect(payload.route).toBe('/invite/:id');
  });
});
