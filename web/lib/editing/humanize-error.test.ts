import { describe, expect, it } from 'vitest';
import { ApiError } from '@/lib/api/client';
import { humanizeError, seedRule, seedRuleSentence } from './humanize-error';

const GENERIC_500 = 'Something went wrong on our side. Try again in a moment.';

/** T-008's seed-invalid body: the message names the first rule, the violations list what broke it. */
function seedBody(message = 'A clip on the timeline overlaps the next clip, so the seed was refused.') {
  return {
    message,
    violations: [
      { path: 'tracks[1].clips[0].sourceIn', message: 'must not overlap the previous clip' },
      { path: 'tracks[1].clips[1].sourceIn', message: 'must not start before the previous clip ends' },
    ],
  };
}

describe('humanizeError with a seed-invalid 500', () => {
  it('returns the server sentence instead of the generic one', () => {
    const error = new ApiError('seed failed', 500, [], seedBody());
    expect(humanizeError(error, 'fallback')).toBe(
      'A clip on the timeline overlaps the next clip, so the seed was refused.',
    );
  });

  it('drops an op prefix from the server message', () => {
    const error = new ApiError('seed failed', 500, [], seedBody('setTimelineSettings: clips overlap on track 1.'));
    const sentence = humanizeError(error, 'fallback');
    expect(sentence).not.toContain('setTimelineSettings:');
    expect(sentence).toContain('overlap on track 1');
  });

  it('keeps a plain 500 generic', () => {
    expect(humanizeError(new ApiError('Boom', 500), 'fallback')).toBe(GENERIC_500);
    expect(humanizeError(new ApiError('Boom', 500, [], { message: 'Only a message.' }), 'fallback')).toBe(GENERIC_500);
    expect(humanizeError(new ApiError('Boom', 500, [], { message: 'Boom.', violations: [] }), 'fallback')).toBe(
      GENERIC_500,
    );
  });

  it('keeps a 500 whose body message is raw JSON generic', () => {
    const body = { message: '{"tracks":[]}', violations: [{ path: 'tracks', message: 'bad' }] };
    expect(humanizeError(new ApiError('Boom', 500, [], body), 'fallback')).toBe(GENERIC_500);
    expect(seedRule(new ApiError('Boom', 500, [], body))).toBeNull();
  });

  it('leaves a 400 path unchanged', () => {
    expect(humanizeError(new ApiError('Name is too long.', 400), 'fallback')).toBe('Name is too long.');
  });
});

describe('seedRuleSentence', () => {
  it('appends the first violation path and message', () => {
    const error = new ApiError('seed failed', 500, [], seedBody());
    expect(seedRuleSentence(error)).toBe(
      'A clip on the timeline overlaps the next clip, so the seed was refused. (tracks[1].clips[0].sourceIn: must not overlap the previous clip)',
    );
  });

  it('returns null for a plain 500 so the page keeps its own sentence', () => {
    expect(seedRuleSentence(new ApiError('Boom', 500))).toBeNull();
    expect(seedRuleSentence(new Error('Boom'))).toBeNull();
  });
});
