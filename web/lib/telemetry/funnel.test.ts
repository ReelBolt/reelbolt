// @vitest-environment jsdom
/**
 * The alpha funnel (alpha-launch T-018). What these protect:
 *   - each event fires exactly once per user, on the transition, never twice on a retry;
 *   - a second user gets their own once;
 *   - with consent withheld (or never given), no event is emitted at all;
 *   - with telemetry switched off for the build, nothing is emitted;
 *   - a funnel event carries only the event name (the allowlist).
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const H = vi.hoisted(() => ({
  sendFunnelEvent: vi.fn(() => Promise.resolve()),
}));

vi.mock('./client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./client')>();
  return {
    ...actual,
    sendFunnelEvent: (...args: unknown[]) => H.sendFunnelEvent(...(args as [])),
  };
});

import { TELEMETRY_CONSENT_STORAGE_KEY } from './consent';
import { clearFunnelEventMarkers, emitFunnelEvent, hasFunnelEventBeenSent } from './funnel';

function grant() {
  window.localStorage.setItem(TELEMETRY_CONSENT_STORAGE_KEY, 'granted');
}

beforeEach(() => {
  window.localStorage.clear();
  H.sendFunnelEvent.mockClear();
  process.env.NEXT_PUBLIC_TELEMETRY_ENABLED = 'true';
});

afterEach(() => {
  delete process.env.NEXT_PUBLIC_TELEMETRY_ENABLED;
});

describe('emitFunnelEvent', () => {
  it('fires exactly once per user on the transition, and not again on a retry', () => {
    grant();

    expect(emitFunnelEvent('alpha.first_project', 'user-1')).toBe(true);
    expect(H.sendFunnelEvent).toHaveBeenCalledTimes(1);

    // A re-render, a double click or a retry all land here: the marker already exists.
    expect(emitFunnelEvent('alpha.first_project', 'user-1')).toBe(false);
    expect(H.sendFunnelEvent).toHaveBeenCalledTimes(1);
    expect(hasFunnelEventBeenSent('alpha.first_project', 'user-1')).toBe(true);
  });

  it('gives each user their own once', () => {
    grant();

    expect(emitFunnelEvent('alpha.first_run', 'user-1')).toBe(true);
    expect(emitFunnelEvent('alpha.first_run', 'user-2')).toBe(true);
    expect(H.sendFunnelEvent).toHaveBeenCalledTimes(2);
  });

  it('keeps the four events independent', () => {
    grant();

    expect(emitFunnelEvent('alpha.signup', 'user-1')).toBe(true);
    expect(emitFunnelEvent('alpha.first_project', 'user-1')).toBe(true);
    expect(emitFunnelEvent('alpha.first_run', 'user-1')).toBe(true);
    expect(emitFunnelEvent('alpha.first_export_initiated', 'user-1')).toBe(true);
    expect(H.sendFunnelEvent).toHaveBeenCalledTimes(4);
  });

  it('sends nothing when consent is withheld', () => {
    window.localStorage.setItem(TELEMETRY_CONSENT_STORAGE_KEY, 'denied');

    expect(emitFunnelEvent('alpha.signup', 'user-1')).toBe(false);
    expect(H.sendFunnelEvent).not.toHaveBeenCalled();
    expect(hasFunnelEventBeenSent('alpha.signup', 'user-1')).toBe(false);
  });

  it('sends nothing when no choice has been made (opt-in default)', () => {
    expect(emitFunnelEvent('alpha.signup', 'user-1')).toBe(false);
    expect(H.sendFunnelEvent).not.toHaveBeenCalled();
  });

  it('sends nothing when the build has not enabled telemetry', () => {
    grant();
    process.env.NEXT_PUBLIC_TELEMETRY_ENABLED = 'false';

    expect(emitFunnelEvent('alpha.signup', 'user-1')).toBe(false);
    expect(H.sendFunnelEvent).not.toHaveBeenCalled();
  });

  it('sends nothing without a user to key it to', () => {
    grant();
    expect(emitFunnelEvent('alpha.signup', '')).toBe(false);
    expect(H.sendFunnelEvent).not.toHaveBeenCalled();
  });

  it('sends only the event name — the allowlist, nothing else', () => {
    grant();
    emitFunnelEvent('alpha.first_export_initiated', 'user-1');

    expect(H.sendFunnelEvent).toHaveBeenCalledWith({ name: 'alpha.first_export_initiated' });
  });

  it('re-arms only when the markers are cleared', () => {
    grant();
    expect(emitFunnelEvent('alpha.signup', 'user-1')).toBe(true);
    clearFunnelEventMarkers();
    expect(emitFunnelEvent('alpha.signup', 'user-1')).toBe(true);
    expect(H.sendFunnelEvent).toHaveBeenCalledTimes(2);
  });
});

describe('run events (detail)', () => {
  beforeEach(() => {
    window.localStorage.clear();
    H.sendFunnelEvent.mockClear();
    window.localStorage.setItem(TELEMETRY_CONSENT_STORAGE_KEY, 'granted');
  });

  it('dedups per user per (event, detail) and sends the detail', () => {
    expect(emitFunnelEvent('alpha.run_blocked', 'u1', 'credits')).toBe(true);
    expect(emitFunnelEvent('alpha.run_blocked', 'u1', 'credits')).toBe(false);
    expect(emitFunnelEvent('alpha.run_blocked', 'u1', 'plan')).toBe(true);
    expect(H.sendFunnelEvent).toHaveBeenCalledWith({ name: 'alpha.run_blocked', detail: 'credits' });
  });

  it('rejects an event/detail mismatch without writing a marker', () => {
    expect(emitFunnelEvent('alpha.run_clicked', 'u1', 'credits')).toBe(false);
    expect(emitFunnelEvent('alpha.run_blocked', 'u1')).toBe(false);
    expect(hasFunnelEventBeenSent('alpha.run_blocked', 'u1')).toBe(false);
    expect(H.sendFunnelEvent).not.toHaveBeenCalled();
  });

  it('rejects an unknown detail and sends nothing without consent', () => {
    expect(emitFunnelEvent('alpha.run_blocked', 'u1', 'oops' as never)).toBe(false);
    window.localStorage.setItem(TELEMETRY_CONSENT_STORAGE_KEY, 'denied');
    expect(emitFunnelEvent('alpha.run_clicked', 'u1')).toBe(false);
    expect(H.sendFunnelEvent).not.toHaveBeenCalled();
  });
});
