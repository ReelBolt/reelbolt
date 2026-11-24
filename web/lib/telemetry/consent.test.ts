// @vitest-environment jsdom
/**
 * The app-side consent store (alpha-launch T-018). Opt-in: an unanswered visitor has not consented.
 *
 * product-events-funnel T-005: the decision itself is delivered to the server — a decline is a
 * stated fact, not silence — and that send is deliberately outside the consent gate.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TELEMETRY_EVENTS_PATH } from './client';
import {
  TELEMETRY_CONSENT_STORAGE_KEY,
  readTelemetryConsent,
  resetTelemetryConsent,
  subscribeTelemetryConsent,
  writeTelemetryConsent,
} from './consent';

const fetchMock = vi.fn(() => Promise.resolve({} as Response));

/** The `[path, init]` of the nth fetch call. */
function fetchCall(index: number): [string, RequestInit] | undefined {
  return fetchMock.mock.calls[index] as unknown as [string, RequestInit] | undefined;
}

/** The JSON body of the nth fetch call, parsed. */
function postedBody(index: number): unknown {
  return JSON.parse((fetchCall(index)?.[1]?.body as string) ?? 'null');
}

beforeEach(() => {
  window.localStorage.clear();
  fetchMock.mockClear();
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('telemetry consent', () => {
  it('reads null until a choice is made', () => {
    expect(readTelemetryConsent()).toBeNull();
  });

  it('stores a granted choice', () => {
    writeTelemetryConsent('granted');
    expect(readTelemetryConsent()).toBe('granted');
    expect(window.localStorage.getItem(TELEMETRY_CONSENT_STORAGE_KEY)).toBe('granted');
  });

  it('stores a denied choice', () => {
    writeTelemetryConsent('denied');
    expect(readTelemetryConsent()).toBe('denied');
  });

  it('treats a garbage stored value as no choice', () => {
    window.localStorage.setItem(TELEMETRY_CONSENT_STORAGE_KEY, 'yes');
    expect(readTelemetryConsent()).toBeNull();
  });

  it('reset clears the choice and notifies subscribers', () => {
    const listener = vi.fn();
    const unsubscribe = subscribeTelemetryConsent(listener);
    writeTelemetryConsent('granted');
    resetTelemetryConsent();

    expect(readTelemetryConsent()).toBeNull();
    expect(listener).toHaveBeenCalled();
    unsubscribe();
  });
});

describe('the decision reaches the server', () => {
  it('posts the declined name, and only the decision, even though consent is not granted', () => {
    writeTelemetryConsent('denied');

    expect(readTelemetryConsent()).toBe('denied');
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchCall(0)?.[0]).toBe(TELEMETRY_EVENTS_PATH);

    // The whole body, field by field: no user id, no device identifier, no route, no extra field.
    const body = postedBody(0) as { events: Record<string, unknown>[] };
    expect(body).toEqual({ events: [{ name: 'telemetry.consent.declined' }] });
    expect(Object.keys(body)).toEqual(['events']);
    expect(Object.keys(body.events[0]!)).toEqual(['name']);
  });

  it('posts the granted name', () => {
    writeTelemetryConsent('granted');

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(postedBody(0)).toEqual({ events: [{ name: 'telemetry.consent.granted' }] });
  });

  it('posts nothing when there is no choice', () => {
    writeTelemetryConsent(null);

    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('posts nothing when the device could not store the choice', () => {
    const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage unavailable');
    });

    writeTelemetryConsent('denied');

    // The device is authoritative: if it could not remember the choice, we do not claim it either.
    expect(fetchMock).not.toHaveBeenCalled();
    setItem.mockRestore();
  });
});
