'use client';

import { Alert } from '@mantine/core';
import { IconWifiOff } from '@tabler/icons-react';
import { useOnline } from '@/lib/hooks/use-online';
import { useT } from '@/i18n';

/** App-wide slim notice while the browser reports it is offline. Renders nothing online. */
export function OfflineBanner() {
  const t = useT('shell.offline');
  const online = useOnline();
  if (online) return null;
  return (
    <Alert role="status" color="yellow" variant="light" py={6} mb="sm" icon={<IconWifiOff size={16} />} data-testid="offline-banner">
      {t('banner')}
    </Alert>
  );
}
