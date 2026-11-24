'use client';

import useSWR from 'swr';
import { fetcher, } from '../api/client';
import { fetchRecentNotifications, NOTIFICATION_PREFERENCES_PATH } from '../api/notifications';
import type { NotificationPreferences, NotificationRecord } from '../api/notifications';

/** The caller's own notification switches. A missing server row resolves to "all on". */
export function useNotificationPreferences() {
  return useSWR<NotificationPreferences>(NOTIFICATION_PREFERENCES_PATH, fetcher);
}

/** The caller's recent notification delivery log, newest first. */
export function useRecentNotifications(limit = 30) {
  // The list endpoint wraps its records, so it needs its own fetcher rather than the shared one.
  return useSWR<NotificationRecord[]>(
    `notifications:${limit}`,
    () => fetchRecentNotifications(limit),
    { revalidateOnFocus: false },
  );
}
