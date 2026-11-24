'use client';

import { Button, Group } from '@mantine/core';
import Link from 'next/link';
import { useT } from '@/i18n';

/**
 * The two ways onward from the checkout page.
 *
 * A client component of its own because this page is a server component and `Link` is not
 * serializable as a `component` prop across that boundary — `<Button component={Link}>` renders
 * "Functions cannot be passed directly to Client Components" at request time. `next/link` is a
 * client component, so the pass is fine once both ends are on the client side, which is how every
 * other page in this app does it.
 */
export function CheckoutActions() {
  const t = useT('checkout.actions');
  return (
    <Group gap="sm" wrap="wrap">
      <Button component={Link} href="/settings/billing">
        {t('checkPlan')}
      </Button>
      <Button component={Link} href="/dashboard" variant="default">
        {t('dashboard')}
      </Button>
    </Group>
  );
}
