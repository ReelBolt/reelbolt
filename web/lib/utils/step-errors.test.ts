import { describe, it, expect } from 'vitest';
import { parseStepErrors, fieldPathsForMessage } from './step-errors';

describe('parseStepErrors', () => {
  it('groups lines by step order and keeps the rest as general', () => {
    const { byStep, general } = parseStepErrors([
      'Step 4 (VideoGenerate): maxSpendCredits must be greater than 0.',
      'Step 4 (VideoGenerate): takes must be <= maxClips.',
      'Step 2 (Voiceover): VoiceoverConfigJson is required.',
      'Something else',
    ]);
    expect(byStep.get(4)).toHaveLength(2);
    expect(byStep.get(2)).toEqual(['VoiceoverConfigJson is required.']);
    expect(general).toEqual(['Something else']);
  });
});

describe('fieldPathsForMessage', () => {
  const cfg = JSON.stringify({ takes: 2, maxClips: 1, maxSpendCredits: 0, pollIntervalSeconds: 5 });
  it('marks the config field named in the message', () => {
    expect(fieldPathsForMessage('maxSpendCredits must be greater than 0.', cfg)).toEqual(['config.maxSpendCredits']);
  });
  it('can name several fields', () => {
    expect(fieldPathsForMessage('takes must be <= maxClips.', cfg)).toContain('config.maxClips');
  });
  it('finds nothing for an unrelated message', () => {
    expect(fieldPathsForMessage('The moon is full.', cfg)).toEqual([]);
  });
  it('places the avatar messages next to the avatar fields, even before the config carries them', () => {
    expect(fieldPathsForMessage('AvatarReferenceRoles must include at least one picture role (Face, HalfBody or FullBody)', cfg))
      .toEqual(['config.avatarReferenceRoles']);
    expect(fieldPathsForMessage('AvatarAudio names both a step and a project file: choose one', cfg)[0]).toBe('config.avatarAudio');
    expect(fieldPathsForMessage('avatar 11111111-1111-1111-1111-111111111111 cannot be used: consent_revoked.', cfg)).toEqual(['config.avatarId']);
    expect(fieldPathsForMessage('avatar 11111111-1111-1111-1111-111111111111 was not found in this project.', cfg)).toEqual(['config.avatarId']);
  });
  it('maps a step column', () => {
    expect(fieldPathsForMessage('minScore must be 1-10', null)).toEqual(['col.minScore']);
  });
});
