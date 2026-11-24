'use client';

import { useEffect } from 'react';
import { usePathname, useRouter, useSearchParams } from 'next/navigation';
import { notifications } from '@mantine/notifications';
import { useT } from '@/i18n';

/** The query flag proxy.ts adds when it sends a non-admin away from /admin/*. */
const NOTICE_PARAM = 'notice';
const NOTICE_VALUE = 'admin-only';

/**
 * Explains the bounce from an /admin page: shows one notification, then removes the flag from the
 * address so a reload or a shared link does not repeat it. Renders nothing.
 */
export function AdminOnlyNotice() {
  const t = useT('auth.adminOnly');
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();
  const flagged = params.get(NOTICE_PARAM) === NOTICE_VALUE;
  const title = t('title');
  const body = t('body');

  useEffect(() => {
    if (!flagged) return;
    notifications.show({ id: 'admin-only-notice', title, message: body, color: 'yellow' });
    const rest = new URLSearchParams(params.toString());
    rest.delete(NOTICE_PARAM);
    const query = rest.toString();
    router.replace(query ? `${pathname}?${query}` : pathname);
  }, [flagged, title, body, params, pathname, router]);

  return null;
}
