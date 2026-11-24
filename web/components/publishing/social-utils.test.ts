import { describe, expect, it } from 'vitest';
import type { SocialPost } from '@/lib/types/social';
import { groupByDay, localDateTime, monthGrid, parseHashtags, postTextPlatform, privacyChoices, splitLocal } from './social-utils';

describe('social-utils', () => {
  it('offers the privacy choices each platform really has', () => {
    expect(privacyChoices('youtube')).toEqual(['public', 'unlisted', 'private']);
    expect(privacyChoices('linkedin')).toEqual(['public', 'connections']);
  });
  it('maps YouTube to the Shorts post text', () => {
    expect(postTextPlatform('youtube')).toBe('youtube_shorts');
    expect(postTextPlatform('linkedin')).toBe('linkedin');
  });
  it('parses hashtags and local times', () => {
    expect(parseHashtags('#a, b  ##c')).toEqual(['a', 'b', 'c']);
    expect(localDateTime('2026-11-20', '09:30')).toBe('2026-11-20T09:30:00');
    expect(localDateTime('', '09:30')).toBeNull();
    expect(splitLocal('2026-11-20T09:30:00')).toEqual({ date: '2026-11-20', time: '09:30' });
  });
  it('builds Monday-first weeks covering the month', () => {
    const weeks = monthGrid(2026, 10);
    expect(weeks.every((w) => w.length === 7)).toBe(true);
    expect(weeks[0][0].key).toBe('2026-10-26');
    expect(weeks.flat().filter((c) => c.inMonth)).toHaveLength(30);
  });
  it('groups posts by their local day', () => {
    const post = { id: 'p', scheduledLocal: '2026-11-20T09:30:00', scheduledAt: null, publishedAt: null } as SocialPost;
    expect(groupByDay([post]).get('2026-11-20')).toHaveLength(1);
  });
});
