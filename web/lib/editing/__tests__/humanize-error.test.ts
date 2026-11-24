import { describe, expect, it } from 'vitest';
import { ApiError } from '@/lib/api/client';
import { humanizeError, looksRaw } from '../humanize-error';
import { doc, media, track } from './fixtures';

const TRACK_ID = '5f0c2b1e-0d7a-4c55-9a51-1b2c3d4e5f60';
const FALLBACK = 'The timeline could not be renamed.';

describe('VF3 humanizeError', () => {
  it('keeps an already plain sentence', () => {
    expect(humanizeError(new Error('That name is already taken'), FALLBACK)).toBe('That name is already taken.');
    expect(humanizeError(new ApiError('That name is already taken.', 400), FALLBACK)).toBe('That name is already taken.');
  });

  it.each([
    ['{"title":"Bad Request","status":400}'],
    ['System.InvalidOperationException: boom at Foo.Bar()'],
    ['Npgsql.PostgresException: 23505'],
    ['HTTP 500 Internal Server Error'],
    ['500'],
    ['TypeError: x is undefined'],
    ['<html><body>502</body></html>'],
    ['request to https://api.example.com/x failed'],
    ['[object Object]'],
  ])('replaces raw text %s with the caller fallback', (raw) => {
    expect(looksRaw(raw)).toBe(true);
    expect(humanizeError(new Error(raw), FALLBACK)).toBe(FALLBACK);
  });

  it('says a failed request in words, never with its status', () => {
    for (const status of [401, 403, 404, 409, 413, 429, 500, 502, 503]) {
      const text = humanizeError(new ApiError('{"error":"x"}', status), FALLBACK);
      expect(text).not.toMatch(/\d{3}|\{|"/);
      expect(text).toMatch(/\.$/);
    }
    expect(humanizeError(new ApiError('boom', 500), FALLBACK)).toBe('Something went wrong on our side. Try again in a moment.');
    expect(humanizeError(new ApiError('', 401), FALLBACK)).toMatch(/session/);
  });

  it('a lost connection is plain', () => {
    expect(humanizeError(new TypeError('Failed to fetch'), FALLBACK)).toBe('Could not reach the server. Check your connection and try again.');
  });

  it('names a track the document knows instead of showing its id', () => {
    const d = doc([track(TRACK_ID, 'video', [media('9a8b7c6d-1111-4222-8333-444455556666', 0, 60)])]);
    d.tracks[0].name = 'Interviews';
    const text = humanizeError(new Error(`nudge: track '${TRACK_ID}' is locked`), FALLBACK, d);
    expect(text).toContain('“Interviews”');
    expect(text).not.toMatch(/[0-9a-f]{8}-|nudge/);
  });

  it('unknown values fall back', () => {
    expect(humanizeError(undefined, FALLBACK)).toBe(FALLBACK);
    expect(humanizeError({ a: 1 }, FALLBACK)).toBe(FALLBACK);
  });
});

describe('censored seed refusal', () => {
  it('maps SEED_WOULD_REVEAL_CENSORED to a plain sentence, never the server text', () => {
    const sentence = humanizeError(new ApiError('server english', 409, [], { code: 'SEED_WOULD_REVEAL_CENSORED' }), 'x');
    expect(sentence).toMatch(/hides part of the picture/);
    expect(sentence).not.toMatch(/server english|SEED_WOULD/);
  });
});

describe('guest review link codes', () => {
  it('maps NO_RENDER_TO_SHARE and REVIEW_LINK_LIMIT to plain sentences', () => {
    expect(humanizeError(new ApiError('Render this timeline first, then share the finished video.', 409, [], { code: 'NO_RENDER_TO_SHARE' }), 'x')).toBe('Make a full render first.');
    expect(humanizeError(new ApiError('limit', 409, [], { code: 'REVIEW_LINK_LIMIT' }), 'x')).toMatch(/too many share links/);
  });
});
