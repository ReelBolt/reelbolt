import { describe, expect, it } from 'vitest';
import { ApiError } from '@/lib/api/client';
import { runBlockDetailFromKey, runBlockDetailOf } from './run-events';
import { isRunBlockedDetail } from './allowlist';

function apiError(status: number, body: Record<string, unknown>): ApiError {
  return new ApiError(String(body.message ?? 'x'), status, undefined, body);
}

describe('runBlockDetailFromKey', () => {
  it('maps the disabled-state keys to closed tokens', () => {
    expect(runBlockDetailFromKey(null)).toBeNull();
    expect(runBlockDetailFromKey('notes:required')).toBe('notes_required');
    expect(runBlockDetailFromKey('plan')).toBe('plan');
    expect(runBlockDetailFromKey('credits')).toBe('credits');
    expect(runBlockDetailFromKey('source:SOURCE_TOO_LARGE:a.mp4:0')).toBe('source');
    expect(runBlockDetailFromKey('slot:footage')).toBe('readiness');
  });
});

describe('runBlockDetailOf', () => {
  it('maps refusals to closed tokens', () => {
    expect(runBlockDetailOf(apiError(402, { code: 'INSUFFICIENT_CREDITS', required: 5, balance: 1 }))).toBe('credits');
    expect(runBlockDetailOf(apiError(402, { code: 'PLAN_LIMIT' }))).toBe('plan');
    expect(runBlockDetailOf(apiError(409, { code: 'ESTIMATE_NOT_ACCEPTED' }))).toBe('estimate_gate');
    expect(runBlockDetailOf(apiError(409, { code: 'ESTIMATE_OVER_CAP' }))).toBe('estimate_gate');
    expect(runBlockDetailOf(apiError(429, {}))).toBe('rate_limited');
    expect(runBlockDetailOf(apiError(422, { code: 'SOURCE_TOO_LARGE' }))).toBe('source');
    expect(runBlockDetailOf(apiError(500, {}))).toBe('other');
    expect(runBlockDetailOf(new Error('boom'))).toBe('other');
  });
  it('only ever yields members of the closed set', () => {
    expect(isRunBlockedDetail(runBlockDetailOf(apiError(418, {})))).toBe(true);
    expect(isRunBlockedDetail('free text')).toBe(false);
  });
});
