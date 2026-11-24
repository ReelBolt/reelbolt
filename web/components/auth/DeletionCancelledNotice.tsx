'use client';

import { useEffect } from 'react';
import { usePathname, useRouter, useSearchParams } from 'next/navigation';
import { notifications } from '@mantine/notifications';
import { IconCircleCheck } from '@tabler/icons-react';
import { DELETION_CANCELLED_MESSAGE, DELETION_CANCELLED_TITLE } from '@/lib/account/format';

const NOTIFICATION_ID = 'account-deletion-cancelled';

/** Shows the "your account is staying" notice. One id, so a second call never stacks a duplicate. */
export function notifyDeletionCancelled() {
  notifications.show({
    id: NOTIFICATION_ID,
    title: DELETION_CANCELLED_TITLE,
    message: DELETION_CANCELLED_MESSAGE,
    color: 'green',
    icon: <IconCircleCheck size={18} />,
    autoClose: 12_000,
  });
}

/**
 * The Google/GitHub sign-in answers with a redirect rather than a JSON body, so when it cancelled a
 * pending deletion it says so with `?deletion=cancelled` on the page it lands on (the dashboard, or
 * the terms page on a first sign-in). This reads that, shows the notice once and takes the parameter
 * back off the address, so a reload or a shared link does not repeat it. Renders nothing.
 *
 * The password sign-in learns the same thing from its response and calls notifyDeletionCancelled
 * itself (components/auth/LoginForm).
 */
export function DeletionCancelledNotice() {
  const params = useSearchParams();
  const pathname = usePathname();
  const router = useRouter();
  const cancelled = params.get('deletion') === 'cancelled';

  useEffect(() => {
    if (!cancelled) return;
    notifyDeletionCancelled();
    const rest = new URLSearchParams(params.toString());
    rest.delete('deletion');
    const query = rest.toString();
    router.replace(query ? `${pathname}?${query}` : pathname, { scroll: false });
  }, [cancelled, params, pathname, router]);

  return null;
}
