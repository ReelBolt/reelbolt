'use client';

import { useEffect, useRef } from 'react';
import { usePathname, useRouter, useSearchParams } from 'next/navigation';
import { notifications } from '@mantine/notifications';
import { describeCloudConnectOutcome, providerName } from '@/lib/api/cloudStorage';
import type { CloudProvider } from '@/lib/types/cloudStorage';

/** The query keys the connect callback adds on its way back. */
const OUTCOME_KEYS = ['cloud', 'provider', 'reason'] as const;

/**
 * Reads the `?cloud=connected|error&provider=…&reason=…` the account-connection callback redirects
 * back with: says once how it went, runs `onReturn` (re-read the connections), and strips those keys
 * with `router.replace`, so a reload or a shared link does not announce it again.
 */
export function useCloudConnectOutcome(providers: CloudProvider[] | undefined, onReturn?: () => void) {
  const search = useSearchParams();
  const router = useRouter();
  const pathname = usePathname();
  const handled = useRef<string | null>(null);
  const onReturnRef = useRef(onReturn);
  const providersRef = useRef(providers);

  useEffect(() => {
    onReturnRef.current = onReturn;
    providersRef.current = providers;
  });

  const query = search?.toString() ?? '';
  const outcome = search?.get('cloud') ?? null;

  useEffect(() => {
    if (!outcome || handled.current === query) return;
    handled.current = query;

    const params = new URLSearchParams(query);
    const notice = describeCloudConnectOutcome(
      outcome,
      providerName(params.get('provider'), providersRef.current),
      params.get('reason'),
    );
    if (notice) notifications.show({ color: notice.color, message: notice.message, autoClose: notice.color === 'green' ? 6000 : 10000 });
    onReturnRef.current?.();

    for (const key of OUTCOME_KEYS) params.delete(key);
    const rest = params.toString();
    router.replace(rest ? `${pathname}?${rest}` : pathname, { scroll: false });
  }, [outcome, query, pathname, router]);
}
