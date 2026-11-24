'use client';

import { useCallback } from 'react';
import { Button, Group, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useT } from '@/i18n';

/**
 * T-012: a picture dropped on an upper track over an occupied lower track is full-frame and hides what is
 * below. The drop stays as it is; this one-line toast offers the fix as ONE tap (the caller's `onMake`
 * applies the corner preset as one undo step).
 */
export function showPipToast(copy: { covers: string; make: string }, onMake: () => void): void {
  const id = `pip-${Date.now()}`;
  notifications.show({
    id,
    color: 'blue',
    autoClose: 8000,
    message: (
      <Group gap="xs" justify="space-between" wrap="nowrap" data-testid="pip-toast">
        <Text size="sm">{copy.covers}</Text>
        <Button
          size="compact-sm"
          variant="light"
          miw={44}
          mih={44}
          data-testid="pip-toast-make"
          onClick={() => {
            onMake();
            notifications.hide(id);
          }}
        >
          {copy.make}
        </Button>
      </Group>
    ),
  });
}

/** {@link showPipToast} in the person's language. */
export function usePipToast(): (onMake: () => void) => void {
  const t = useT('editor-misc.pip');
  return useCallback((onMake) => showPipToast({ covers: t('covers'), make: t('make') }, onMake), [t]);
}
