import { describe, expect, it } from 'vitest';
import { MAX_SAMPLE_CLIPS, onboardingSampleClipUrls, welcomeFlowEnabled } from './config';

describe('welcomeFlowEnabled', () => {
  it('defaults to on', () => {
    expect(welcomeFlowEnabled({})).toBe(true);
    expect(welcomeFlowEnabled({ WELCOME_FLOW_ENABLED: 'true' })).toBe(true);
    expect(welcomeFlowEnabled({ WELCOME_FLOW_ENABLED: '' })).toBe(true);
  });
  it('is off only for the exact string false', () => {
    expect(welcomeFlowEnabled({ WELCOME_FLOW_ENABLED: 'false' })).toBe(false);
  });
});

describe('onboardingSampleClipUrls', () => {
  it('is empty by default: no sample clip ships with the product', () => {
    expect(onboardingSampleClipUrls({})).toEqual([]);
    expect(onboardingSampleClipUrls({ ONBOARDING_SAMPLE_CLIP_URLS: '' })).toEqual([]);
    expect(onboardingSampleClipUrls({ ONBOARDING_SAMPLE_CLIP_URLS: ' , \n ' })).toEqual([]);
  });

  it('reads https links separated by commas or white space', () => {
    expect(
      onboardingSampleClipUrls({
        ONBOARDING_SAMPLE_CLIP_URLS: 'https://media.example.test/a.mp4, https://media.example.test/b.mp4\nhttps://media.example.test/c.mov',
      }),
    ).toEqual(['https://media.example.test/a.mp4', 'https://media.example.test/b.mp4', 'https://media.example.test/c.mov']);
  });

  it('drops anything that is not a plain https link, and duplicates', () => {
    expect(
      onboardingSampleClipUrls({
        ONBOARDING_SAMPLE_CLIP_URLS: [
          'http://media.example.test/plain.mp4',
          'ftp://media.example.test/a.mp4',
          'javascript:alert(1)',
          'not a url',
          'https://user:secret@media.example.test/a.mp4',
          'https://media.example.test/a.mp4',
          'https://media.example.test/a.mp4',
        ].join(','),
      }),
    ).toEqual(['https://media.example.test/a.mp4']);
  });

  it('keeps at most the documented number of clips', () => {
    const many = Array.from({ length: MAX_SAMPLE_CLIPS + 5 }, (_, i) => `https://media.example.test/${i}.mp4`).join(' ');
    expect(onboardingSampleClipUrls({ ONBOARDING_SAMPLE_CLIP_URLS: many })).toHaveLength(MAX_SAMPLE_CLIPS);
  });
});
