import type { T } from '@/i18n/translator';
import { enLabels, labelOf } from '@/lib/i18n-default';

/**
 * The notification classes (seven switchable) (docs/notifications.md). The values are the wire names
 * the Go API switches on, so they must stay byte-identical to the server's constants.
 */
export type NotificationCategory =
  | 'job_succeeded'
  | 'job_failed'
  | 'file_created'
  | 'graphics_edited'
  | 'review_activity'
  | 'avatar_retention'
  | 'social_publishing';

/** Plain labels for the delivery log. `review_activity` and `avatar_retention` have their own switches, on by default. */
export const NOTIFICATION_CATEGORY_LABELS: Record<NotificationCategory, string> = enLabels('notificationCategories', [
  'job_succeeded',
  'job_failed',
  'file_created',
  'graphics_edited',
  'review_activity',
  'avatar_retention',
  'social_publishing',
] as const);

/** The category's label in the active language (an unknown category shows as it is). */
export function notificationCategoryLabel(t: T, category: string): string {
  return labelOf(t, 'notificationCategories', category);
}
