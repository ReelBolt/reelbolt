import { describe, expect, it } from 'vitest';
import { enT } from '../i18n-default';
import { ApiError } from './client';
import { describeError, describeFailure, readableServerMessage } from './describe-error';

describe('describeError', () => {
  it('maps a known code', () => {
    const err = new ApiError('Not enough credits', 402, [], { code: 'INSUFFICIENT_CREDITS' });
    expect(describeError(enT, err)).toMatch(/enough credits/i);
  });
  it('passes an unmapped code message through', () => {
    expect(describeError(enT, new ApiError('Weird thing', 400, [], { code: 'NOPE_NOT_MAPPED' }))).toBe('Weird thing');
  });
  it('uses status generics when there is no message', () => {
    expect(describeError(enT, new ApiError('', 404))).toMatch(/could not find/i);
    expect(describeError(enT, new ApiError('', 503))).toMatch(/our side/i);
  });
  it('turns an HTTP status text into the server sentence', () => {
    for (const m of ['Bad Gateway', 'Internal Server Error', 'Service Unavailable', '<html><body>502</body></html>']) {
      expect(describeError(enT, new ApiError(m, 502))).toMatch(/our side/i);
    }
    expect(describeError(enT, new ApiError('Bad Request', 400))).toBe('Bad Request');
  });
  it('turns client-error default titles into the status sentence, keeps real messages', () => {
    expect(describeError(enT, new ApiError('Forbidden', 403))).toMatch(/permission/i);
    expect(describeError(enT, new ApiError('Not Found', 404))).toMatch(/could not find/i);
    expect(describeError(enT, new ApiError('Unauthorized', 401))).toMatch(/session ended/i);
    expect(describeError(enT, new ApiError('Conflict', 409))).toMatch(/conflicts/i);
    expect(describeError(enT, new ApiError('Too Many Requests', 429))).toMatch(/too many requests/i);
    expect(describeError(enT, new ApiError('Project is archived', 404))).toBe('Project is archived');
    expect(describeError(enT, new ApiError('Not Found', 400))).toBe('Not Found');
  });
  it('knows the network case', () => {
    expect(describeError(enT, new TypeError('Failed to fetch'))).toMatch(/Could not reach ReelBolt/);
  });
});

describe('describeFailure', () => {
  it('maps codes and falls back', () => {
    expect(describeFailure(enT, 'delivery_failed')).toBe('The finished video could not be saved.');
    expect(describeFailure(enT, 'WHAT', 'Short server text')).toBe('Short server text');
    expect(describeFailure(enT, null)).toBe('Something went wrong while making your video.');
  });
});

describe('readableServerMessage', () => {
  it('keeps a real server message', () => {
    expect(readableServerMessage(new ApiError('Name is taken', 400))).toBe('Name is taken');
    expect(readableServerMessage(new Error('Boom'))).toBe('Boom');
  });
  it('drops empty, status-title and HTML messages', () => {
    expect(readableServerMessage(new ApiError('Bad Gateway', 502))).toBeNull();
    expect(readableServerMessage(new ApiError('<!DOCTYPE html><html></html>', 502))).toBeNull();
    expect(readableServerMessage(new ApiError('Not Found', 404))).toBeNull();
    expect(readableServerMessage(new Error('  '))).toBeNull();
    expect(readableServerMessage('nope')).toBeNull();
  });
});
