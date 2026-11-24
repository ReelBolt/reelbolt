import { describe, it, expect, vi, beforeEach } from 'vitest';

// `apiFetch` is stubbed so the assertion is on the URL the client builds, not on
// fetch semantics (client.test.ts already covers those). There is no MSW in this repo.
vi.mock('./client', async () => {
  const actual = await vi.importActual<typeof import('./client')>('./client');
  return { ...actual, apiFetch: vi.fn() };
});

import { apiFetch } from './client';
import { getDecisionCalibration } from './decision-calibration';
import type { DecisionCalibrationReport } from '../types/decision-calibration';

const apiFetchMock = vi.mocked(apiFetch);

const EMPTY_REPORT: DecisionCalibrationReport = {
  sites: [],
  providers: [],
  totals: {
    observationCount: 0,
    agreementRate: 0,
    escalationRate: 0,
    tokensSaved: 0,
    tokensSavedIsProjected: true,
    acceptAt: 0.85,
    minMargin: 0.25,
  },
  sampleSizeWarning: 'Not enough observations yet.',
};

/** The URL of the single apiFetch call this test made. */
function calledUrl(): string {
  expect(apiFetchMock).toHaveBeenCalledTimes(1);
  return apiFetchMock.mock.calls[0][0] as string;
}

beforeEach(() => {
  apiFetchMock.mockReset();
  apiFetchMock.mockResolvedValue(EMPTY_REPORT as never);
});

describe('getDecisionCalibration URL construction', () => {
  it('appends every supplied param, in the order acceptAt, minMargin, providerId, site', async () => {
    await getDecisionCalibration({
      acceptAt: 0.855,
      minMargin: 0.123,
      providerId: '3f2504e0-4f89-11d3-9a0c-0305e82c3301',
      site: 'Colorist',
    });

    expect(calledUrl()).toBe(
      '/api/v1/workflow-engine/decision-calibration' +
        '?acceptAt=0.855&minMargin=0.123' +
        '&providerId=3f2504e0-4f89-11d3-9a0c-0305e82c3301&site=Colorist',
    );
  });

  it('sends no querystring at all when no params are supplied', async () => {
    await getDecisionCalibration();

    expect(calledUrl()).toBe('/api/v1/workflow-engine/decision-calibration');
  });

  it('sends an explicitly empty param object with no querystring', async () => {
    await getDecisionCalibration({});

    expect(calledUrl()).toBe('/api/v1/workflow-engine/decision-calibration');
  });

  it('sends only providerId and site when only those are supplied', async () => {
    await getDecisionCalibration({
      providerId: '3f2504e0-4f89-11d3-9a0c-0305e82c3301',
      site: 'MusicSupervisor',
    });

    expect(calledUrl()).toBe(
      '/api/v1/workflow-engine/decision-calibration' +
        '?providerId=3f2504e0-4f89-11d3-9a0c-0305e82c3301&site=MusicSupervisor',
    );
  });

  it('keeps acceptAt=0 rather than dropping it as falsy', async () => {
    await getDecisionCalibration({ acceptAt: 0 });

    expect(calledUrl()).toBe('/api/v1/workflow-engine/decision-calibration?acceptAt=0');
  });

  it('keeps minMargin=0 and acceptAt=1 rather than dropping or rounding them', async () => {
    await getDecisionCalibration({ acceptAt: 1, minMargin: 0 });

    expect(calledUrl()).toBe(
      '/api/v1/workflow-engine/decision-calibration?acceptAt=1&minMargin=0',
    );
  });

  it('keeps full threshold precision (never rounds to 0.85/0.25)', async () => {
    await getDecisionCalibration({ acceptAt: 0.855123456789, minMargin: 0.2500001 });

    expect(calledUrl()).toBe(
      '/api/v1/workflow-engine/decision-calibration?acceptAt=0.855123456789&minMargin=0.2500001',
    );
  });

  it('does not send the backend defaults when the params are omitted', async () => {
    await getDecisionCalibration({ site: 'Colorist' });

    const url = calledUrl();
    expect(url).not.toContain('acceptAt');
    expect(url).not.toContain('minMargin');
  });

  it('drops an empty-string site rather than sending a meaningless empty filter', async () => {
    await getDecisionCalibration({ site: '', providerId: 'abc' });

    expect(calledUrl()).toBe('/api/v1/workflow-engine/decision-calibration?providerId=abc');
  });

  it('encodeURIComponent-escapes providerId and site', async () => {
    await getDecisionCalibration({ providerId: 'a b/c&d=e', site: 'Color ist+grade' });

    expect(calledUrl()).toBe(
      '/api/v1/workflow-engine/decision-calibration' +
        `?providerId=${encodeURIComponent('a b/c&d=e')}&site=${encodeURIComponent('Color ist+grade')}`,
    );
  });

  it('never sends a base URL or an Authorization header', async () => {
    await getDecisionCalibration({ site: 'Colorist' });

    const url = calledUrl();
    expect(url.startsWith('/api/v1/')).toBe(true);
    expect(url).not.toMatch(/^[a-z]+:\/\//i);
    // Relative URL, no options → apiFetch sets Content-Type itself and the JWT rides
    // the httpOnly cookie that nginx translates into an Authorization header.
    expect(apiFetchMock.mock.calls[0][1]).toBeUndefined();
  });

  it('returns the report apiFetch resolved with', async () => {
    const result = await getDecisionCalibration({ site: 'Colorist' });

    expect(result).toBe(EMPTY_REPORT);
  });
});
