import { describe, expect, it } from 'vitest';
import { NOTIFICATION_CATEGORY_LABELS, type NotificationCategory } from './notifications';

describe('NotificationCategory', () => {
  it('labels review_activity in plain words', () => {
    const c: NotificationCategory = 'review_activity';
    expect(NOTIFICATION_CATEGORY_LABELS[c]).toBe('Review notes and decisions');
  });

  it('labels avatar_retention in plain words', () => {
    expect(NOTIFICATION_CATEGORY_LABELS.avatar_retention).toBe('Avatar about to be deleted');
  });

  it('labels social_publishing in plain words', () => {
    expect(NOTIFICATION_CATEGORY_LABELS.social_publishing).toBe('Social post published or failed');
  });

  it('labels all seven wire names', () => {
    expect(Object.keys(NOTIFICATION_CATEGORY_LABELS).sort()).toEqual([
      'avatar_retention',
      'file_created',
      'graphics_edited',
      'job_failed',
      'job_succeeded',
      'review_activity',
      'social_publishing',
    ]);
  });
});
