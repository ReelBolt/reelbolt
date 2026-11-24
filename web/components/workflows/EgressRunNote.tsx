'use client';

import { Text } from '@mantine/core';
import { useT } from '@/i18n';

/**
 * One line under a step's "allow sending" switch: the same permission can be given for a single run on the app page
 * (docs/design/app-inputs.md "Permission to send media as an app field"). The switch itself stays the saved default.
 */
export function EgressRunNote() {
  const t = useT('apps.permission.mediaEgress');
  return (
    <Text size="xs" c="dimmed" data-testid="egress-run-note">
      {t('stepEditorNote')}
    </Text>
  );
}
