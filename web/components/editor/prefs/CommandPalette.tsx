'use client';

import React, { useMemo, useState } from 'react';
import { Button, Group, Loader, Modal, Stack, Text, TextInput } from '@mantine/core';
import { displayChord, liveChords, type KeyAction } from '@/lib/editing/keymap';
import { COMMANDS, SIMPLE_DECISIONS, type CommandUnavailable } from '@/lib/editing/commands';
import { keyActionLabelMsg, keyGroupLabelMsg } from '@/lib/editing/labels-msg';
import { useT } from '@/i18n';

export interface CommandPaletteProps {
  opened: boolean;
  onClose: () => void;
  overrides: Record<string, string>;
  /** Why an action cannot run now (null = available); read live from the store by the caller. */
  unavailable: (id: KeyAction) => CommandUnavailable | null;
  /** Runs the command through the one catalogue (the same `runCommand` as the keyboard). */
  onRun: (id: KeyAction) => void;
  /** The list is synchronous today; the loading state exists for a future async source. */
  loading?: boolean;
  /** The Simple shell shows: rows that open the full editor say so. */
  simple?: boolean;
}

const isMac = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform ?? '');

/**
 * T-007 (§10.3): the command palette (Mod+K). A combobox over the one command catalogue, filtered by
 * label; each row shows its live chord and why it cannot run (unavailable rows are disabled), with an
 * empty state and a loading state. Enter runs the first available match. Undo here and Mod+Z share
 * `runCommand`, so they are one path.
 */
export function CommandPalette({ opened, onClose, overrides, unavailable, onRun, loading = false, simple = false }: CommandPaletteProps) {
  const t = useT('editor.prefs.commandPalette');
  const [query, setQuery] = useState('');
  const chords = useMemo(() => liveChords(overrides), [overrides]);
  const shown = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return COMMANDS;
    return COMMANDS.filter((c) => t.msg(keyActionLabelMsg(c.id)).toLowerCase().includes(q) || t.msg(keyGroupLabelMsg(c.group)).toLowerCase().includes(q));
  }, [query, t]);
  const runFirst = () => {
    const first = shown.find((c) => unavailable(c.id) === null);
    if (!first) return;
    onRun(first.id);
    onClose();
  };

  return (
    <Modal opened={opened} onClose={onClose} title={t('title')} size="lg" transitionProps={{ duration: 0 }} data-testid="command-palette">
      <Stack gap="xs">
        <TextInput
          size="xs"
          label={t('findACommand')}
          placeholder={t('typeAnActionName')}
          value={query}
          onChange={(e) => setQuery(e.currentTarget.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              runFirst();
            }
          }}
          data-testid="palette-search"
          autoFocus
        />
        {loading ? (
          <Group gap="xs" data-testid="palette-loading">
            <Loader size="xs" />
            <Text size="xs" c="dimmed">{t('loadingCommands')}</Text>
          </Group>
        ) : (
          <Stack gap={2} role="listbox" aria-label={t('title')} data-testid="palette-list">
            {shown.map((command) => {
              const reason = unavailable(command.id);
              const chord = chords[command.id][0];
              return (
                <Button
                  key={command.id}
                  variant="subtle"
                  color="gray"
                  fullWidth
                  justify="space-between"
                  h={40}
                  fz="xs"
                  disabled={reason !== null}
                  styles={{ label: { width: '100%', display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 8 } }}
                  onClick={() => {
                    onRun(command.id);
                    onClose();
                  }}
                  role="option"
                  aria-disabled={reason !== null}
                  aria-selected={false}
                  data-testid={`palette-command-${command.id}`}
                  title={reason ? t(`reasons.${reason}`) : undefined}
                >
                  <span>{t.msg(keyActionLabelMsg(command.id))}</span>
                  <Group gap={8} wrap="nowrap">
                    {reason && <Text size="12px" c="dimmed">{t(`reasons.${reason}`)}</Text>}
                    {!reason && simple && SIMPLE_DECISIONS[command.id] === 'opensFull' && <Text size="12px" c="dimmed" data-testid={`palette-opens-full-${command.id}`}>{t('opensFullEditor')}</Text>}
                    {chord && <Text size="xs" ff="monospace">{displayChord(chord, isMac)}</Text>}
                  </Group>
                </Button>
              );
            })}
            {shown.length === 0 && (
              <Text size="xs" c="dimmed" data-testid="palette-empty">{t('noCommandMatches', { query: query.trim() })}</Text>
            )}
          </Stack>
        )}
      </Stack>
    </Modal>
  );
}
