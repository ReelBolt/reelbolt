import { describe, expect, it } from 'vitest';
import { ApiError } from './client';
import { classifyFailure } from './failure-kind';

describe('classifyFailure', () => {
  it.each([
    [new TypeError('Failed to fetch'), 'network'],
    [new ApiError('x', 404), 'notFound'],
    [new ApiError('x', 403), 'forbidden'],
    [new ApiError('x', 401), 'unauthorized'],
    [new ApiError('x', 402), 'paymentRequired'],
    [new ApiError('x', 429), 'rateLimited'],
    [new ApiError('x', 502), 'server'],
    [new ApiError('x', 400), 'other'],
    [new Error('boom'), 'other'],
    ['text', 'other'],
  ])('%#', (err, kind) => {
    expect(classifyFailure(err)).toBe(kind);
  });
});
