'use client';

import { Alert, Button, Group, Text } from '@mantine/core';
import { EDIT_ONLY_TOOLTIP, PANEL_ERROR, RETRY_LABEL } from './copy';
import { useT } from '@/i18n';

/**
 * A failed load, said plainly and with a retry. A panel renders this instead of its empty state when
 * its fetch errored, so "nothing here" is never shown for "could not ask".
 */
export function PanelError({ onRetry, message }: { onRetry: () => void; message?: string }) {
  const tr = useT();
  return (
    <Alert color="red" data-testid="panel-error">
      <Group justify="space-between" align="center" wrap="nowrap">
        <Text size="sm">{message ?? tr.msg(PANEL_ERROR)}</Text>
        <Button size="compact-xs" variant="light" color="red" onClick={onRetry}>
          {tr.msg(RETRY_LABEL)}
        </Button>
      </Group>
    </Alert>
  );
}

/**
 * The visible, always-in-the-page reason a write control is disabled. Buttons point at it with
 * `aria-describedby`, so keyboard and screen-reader users get the reason a hover tooltip never gave.
 */
export function EditReasonText({ id }: { id: string }) {
  const tr = useT();
  return <Text size="xs" c="dimmed" id={id}>{tr.msg(EDIT_ONLY_TOOLTIP)}</Text>;
}
