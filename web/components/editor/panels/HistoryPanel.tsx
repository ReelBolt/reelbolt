'use client';

import React from 'react';
import { Group, ScrollArea, Stack, Text, Tooltip, UnstyledButton } from '@mantine/core';
import { IconArrowBackUp, IconArrowForwardUp } from '@tabler/icons-react';
import type { HistoryEntry } from '@/lib/editing/store';
import { EditorIcon } from '../ui';
import { useT } from '@/i18n';
import { useHelperText } from '../phone/helper-text';
import { useLiveChord } from '../prefs/live-chords';

export interface HistoryPanelProps {
  history: readonly HistoryEntry[];
  cursor: number;
  onUndo: () => void;
  onRedo: () => void;
  onJumpTo: (cursor: number) => void;
}

/**
 * The visible undo history: every entry, the current cursor, jump-to-any-point. Entries after the
 * cursor are the redo tail and render dimmed. Built on the store's inverse programs — no snapshots.
 */
export function HistoryPanel({ history, cursor, onUndo, onRedo, onJumpTo }: HistoryPanelProps) {
  const show = useHelperText();
  const t = useT('editor.panels.historyPanel');
  const undoChord = useLiveChord('undo');
  const redoChord = useLiveChord('redo');
  return (
    <Stack gap="xs" data-testid="history-panel">
      <Group justify="space-between">
        <Text size="sm" fw={600}>
          {t('history')}
        </Text>
        <Group gap={4}>
          <Tooltip label={undoChord.tip(t('undo'))}>
            <EditorIcon tooltip={false} icon={IconArrowBackUp} label={t('undo')} disabled={cursor === 0} onClick={onUndo} data-testid="history-undo" />
          </Tooltip>
          <Tooltip label={redoChord.tip(t('redo'))}>
            <EditorIcon tooltip={false} icon={IconArrowForwardUp} label={t('redo')} disabled={cursor >= history.length} onClick={onRedo} data-testid="history-redo" />
          </Tooltip>
        </Group>
      </Group>
      <ScrollArea.Autosize mah={320}>
        <Stack gap={2}>
          <UnstyledButton
            onClick={() => onJumpTo(0)}
            data-testid="history-entry-0"
            aria-current={cursor === 0 ? 'step' : undefined}
            style={{ padding: '4px 6px', borderRadius: 4, background: cursor === 0 ? 'var(--mantine-color-violet-light)' : undefined }}
          >
            <Text size="xs" c={cursor === 0 ? undefined : 'dimmed'}>
              {t('loadedDocument')}
            </Text>
          </UnstyledButton>
          {history.map((entry, index) => {
            const position = index + 1;
            const applied = position <= cursor;
            const current = position === cursor;
            return (
              <UnstyledButton
                key={entry.id}
                onClick={() => onJumpTo(position)}
                data-testid={`history-entry-${position}`}
                aria-current={current ? 'step' : undefined}
                style={{
                  padding: '4px 6px',
                  borderRadius: 4,
                  background: current ? 'var(--mantine-color-violet-light)' : undefined,
                  opacity: applied ? 1 : 0.5,
                }}
              >
                <Group justify="space-between" wrap="nowrap">
                  <Text size="xs" truncate>
                    {position}. {show(entry.label)}
                  </Text>
                  <Text size="xs" c="dimmed">
                    {t('opCount', { count: entry.forward.length })}
                  </Text>
                </Group>
              </UnstyledButton>
            );
          })}
        </Stack>
      </ScrollArea.Autosize>
    </Stack>
  );
}
