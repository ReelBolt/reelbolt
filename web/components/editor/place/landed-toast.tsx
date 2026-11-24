'use client';

import { useCallback } from 'react';
import { Button, Group, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type { EditorStore } from '@/lib/editing/store';
import { landedAtLine, type LandedAt } from '@/lib/editing/media/place';
import { useT } from '@/i18n';

/** The words of the toast: the title line builder, the default note and the Undo button. */
export interface LandedToastCopy {
  line: (landed: LandedAt, verb: string) => string;
  defaultNote: string;
  undo: string;
}

/**
 * The English wording, for callers that cannot use `useLandedToast` yet (non-hook code). Components should use
 * the hook so the toast speaks the person's language.
 */
export const ENGLISH_LANDED_COPY: LandedToastCopy = { line: landedAtLine, defaultNote: 'It is selected on the timeline.', undo: 'Undo' };

/**
 * R03.6 / R10.5: the "Placed on Video 1 at 00:00:12:04" feedback after a place/add, with an Undo button
 * that reverts exactly that one step. The line is also kept in `data-testid="landed-at"` for tests.
 */
export function showLandedToast(store: EditorStore, landed: LandedAt, verb = 'Placed', note?: string, copy: LandedToastCopy = ENGLISH_LANDED_COPY): void {
  const id = `landed-${landed.frame}-${Date.now()}`;
  notifications.show({
    id,
    color: 'teal',
    title: copy.line(landed, verb),
    autoClose: 6000,
    message: (
      <Group gap="xs" justify="space-between" wrap="nowrap" data-testid="landed-at">
        <Text size="sm">{note ?? copy.defaultNote}</Text>
        <Button
          size="compact-sm"
          variant="light"
          miw={44}
          mih={44}
          data-testid="landed-undo"
          onClick={() => {
            store.undo();
            notifications.hide(id);
          }}
        >
          {copy.undo}
        </Button>
      </Group>
    ),
  });
}

/** `showLandedToast` in the person's language; `verb` and `note` are already-translated text. */
export function useLandedToast(): (store: EditorStore, landed: LandedAt, verb: string, note?: string) => void {
  const t = useT('editor-misc.landed');
  return useCallback(
    (store, landed, verb, note) =>
      showLandedToast(store, landed, verb, note, {
        line: (l, v) => t('line', { verb: v, track: l.trackName, time: l.tc }),
        defaultNote: t('selected'),
        undo: t('undo'),
      }),
    [t],
  );
}
