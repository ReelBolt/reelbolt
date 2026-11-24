import { apiFetch } from './client';
import type { NotificationCategory } from '../types/notifications';

// Relative paths only: the JWT is an httpOnly cookie that nginx turns into an Authorization header.
export const NOTIFICATIONS_PATH = '/api/v1/notifications';
export const NOTIFICATION_PREFERENCES_PATH = '/api/v1/notifications/preferences';

export type { NotificationCategory };

/** The caller's own notification switches. A missing row on the server means "all on". */
export interface NotificationPreferences {
  jobSucceeded: boolean;
  jobFailed: boolean;
  fileCreated: boolean;
  graphicsEdited: boolean;
  /** Review notes and decisions. Absent from older responses: treat as on. */
  reviewActivity?: boolean;
  /** The 7-day warning before an avatar is deleted at the end of its retention. Absent from older responses: treat as on. */
  avatarRetention?: boolean;
  socialPublishing?: boolean;
}

/** A single attempted notification email, newest first. */
export interface NotificationRecord {
  id: string;
  organizationId: string;
  category: NotificationCategory;
  title: string;
  delivered: boolean;
  /** Present only when the attempt failed; the one-line reason. */
  error?: string | null;
  createdAt: string;
}

/** The shape the recent-activity endpoint wraps the records in. */
interface NotificationListResponse {
  notifications: NotificationRecord[];
}

/**
 * The switches the caller may change in a single call. A field the caller leaves out keeps its
 * current (or default) value, so the page can flip one switch without resending the rest.
 */
export type NotificationPreferencesPatch = Partial<NotificationPreferences>;

export function getNotificationPreferencesUrl(): string {
  return NOTIFICATION_PREFERENCES_PATH;
}

export async function fetchNotificationPreferences(): Promise<NotificationPreferences> {
  return apiFetch<NotificationPreferences>(NOTIFICATION_PREFERENCES_PATH);
}

export async function updateNotificationPreferences(
  patch: NotificationPreferencesPatch,
): Promise<NotificationPreferences> {
  return apiFetch<NotificationPreferences>(NOTIFICATION_PREFERENCES_PATH, {
    method: 'PUT',
    body: JSON.stringify(patch),
  });
}

export async function fetchRecentNotifications(
  limit = 30,
): Promise<NotificationRecord[]> {
  const res = await apiFetch<NotificationListResponse>(`${NOTIFICATIONS_PATH}?limit=${limit}`);
  return res.notifications;
}
