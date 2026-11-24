'use client';

import React, { useMemo, useState } from 'react';
import { Button, Group, Modal, ScrollArea, Stack, Table, Text, TextInput } from '@mantine/core';
import { liveChords, displayChord, KEY_ACTIONS, type KeyActionInfo } from '@/lib/editing/keymap';
import { keyActionLabelMsg, keyGroupLabelMsg } from '@/lib/editing/labels-msg';
import { useT } from '@/i18n';

export interface ShortcutsCheatsheetProps {
  opened: boolean;
  onClose: () => void;
  /** The user's overrides, so a remapped action shows its NEW chord. */
  overrides: Record<string, string>;
  /** Opens the editable keymap modal (the cheatsheet stays read-only). */
  onEditShortcuts: () => void;
}

const GROUP_ORDER: readonly KeyActionInfo['group'][] = ['Transport', 'Navigate', 'Marks', 'Tools', 'Edit', 'Compose', 'Pages'];
const isMac = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform ?? '');

/**
 * T-007 (§10.3): the read-only cheatsheet `?` opens. Every action with its EFFECTIVE chord, grouped by
 * Transport / Navigate / Marks / Tools / Edit / Pages, searchable, with a no-match state and one button
 * that opens the editable keymap. The dialog traps focus and closes on Escape (Mantine Modal).
 */
export function ShortcutsCheatsheet({ opened, onClose, overrides, onEditShortcuts }: ShortcutsCheatsheetProps) {
  const t = useT('editor.prefs.cheatsheet');
  const [query, setQuery] = useState('');
  const chords = useMemo(() => liveChords(overrides), [overrides]);

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase();
    return KEY_ACTIONS.filter((a) => {
      if (!q) return true;
      const label = t.msg(keyActionLabelMsg(a.id)).toLowerCase();
      const group = t.msg(keyGroupLabelMsg(a.group)).toLowerCase();
      return label.includes(q) || group.includes(q) || chords[a.id].some((c) => displayChord(c, isMac).toLowerCase().includes(q));
    });
  }, [query, chords, t]);

  return (
    <Modal opened={opened} onClose={onClose} title={t('title')} size="lg" transitionProps={{ duration: 0 }} data-testid="shortcuts-cheatsheet">
      <Stack gap="xs">
        <TextInput
          size="xs"
          label={t('findAShortcut')}
          placeholder={t('actionOrKey')}
          value={query}
          onChange={(e) => setQuery(e.currentTarget.value)}
          data-testid="shortcuts-search"
        />
        <ScrollArea h={420}>
          {GROUP_ORDER.map((group) => {
            const rows = shown.filter((a) => a.group === group);
            if (rows.length === 0) return null;
            return (
              <Table key={group} striped withRowBorders={false} verticalSpacing={2} aria-label={t.msg(keyGroupLabelMsg(group))} data-testid={`shortcuts-group-${group}`}>
                <Table.Caption style={{ captionSide: 'top', textAlign: 'left' }}>
                  <Text size="xs" fw={600}>
                    {t.msg(keyGroupLabelMsg(group))}
                  </Text>
                </Table.Caption>
                <Table.Tbody>
                  {rows.map((action) => (
                    <Table.Tr key={action.id} data-testid={`shortcuts-row-${action.id}`}>
                      <Table.Td>
                        <Text size="xs">{t.msg(keyActionLabelMsg(action.id))}</Text>
                      </Table.Td>
                      <Table.Td>
                        {chords[action.id].length === 0 ? (
                          <Text size="xs" c="dimmed">{t('unbound')}</Text>
                        ) : (
                          <Group gap={4}>
                            {chords[action.id].map((chord) => (
                              <Text key={chord} size="xs" ff="monospace">
                                {displayChord(chord, isMac)}
                              </Text>
                            ))}
                          </Group>
                        )}
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            );
          })}
        </ScrollArea>
        {shown.length === 0 && <Text size="xs" c="dimmed" data-testid="shortcuts-none">{t('noShortcutMatches', { query: query.trim() })}</Text>}
        <Group justify="space-between">
          <Text size="12px" c="dimmed">{t('hint')}</Text>
          <Group gap="xs">
            <Button size="xs" fz="xs" variant="subtle" onClick={onClose} data-testid="shortcuts-close">{t('close')}</Button>
            <Button size="xs" fz="xs" onClick={onEditShortcuts} data-testid="shortcuts-edit">{t('editShortcuts')}</Button>
          </Group>
        </Group>
      </Stack>
    </Modal>
  );
}
