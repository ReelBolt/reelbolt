'use client';

import React, { useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Badge, Button, Group, Kbd, Modal, ScrollArea, Stack, Table, Text, TextInput } from '@mantine/core';
import { KEY_ACTIONS, conflicts, effectiveChords, eventToChord, isModifierOnly, sanitizeOverrides, type KeyAction } from '@/lib/editing/keymap';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { useT } from '@/i18n';

export interface KeymapEditorProps {
  opened: boolean;
  onClose: () => void;
  overrides: Record<string, string>;
  /** Saves the new overrides (the caller persists them in the user's preferences). */
  onSave: (overrides: Record<string, string>) => Promise<void> | void;
  /** R12.4: puts the settings from before the last save back (resolves; rejects with the reason). */
  onRestore?: () => Promise<void>;
  canRestore?: boolean;
}

/** The shortcuts as a file the user can keep or share; only the changed ones are written. */
export function keymapToJson(overrides: Record<string, string>): string {
  return JSON.stringify({ reelboltKeymap: 1, shortcuts: overrides }, null, 2);
}

/** Reads a keymap file back; null when it is not one of ours. Unknown actions are dropped. */
export function keymapFromJson(text: string): Record<string, string> | null {
  try {
    const parsed: unknown = JSON.parse(text);
    if (!parsed || typeof parsed !== 'object') return null;
    const shortcuts = (parsed as { shortcuts?: unknown }).shortcuts;
    if (!shortcuts || typeof shortcuts !== 'object') return null;
    const known = new Set<string>(KEY_ACTIONS.map((a) => a.id));
    return Object.fromEntries(Object.entries(sanitizeOverrides(shortcuts)).filter(([id]) => known.has(id)));
  } catch {
    return null;
  }
}

/**
 * P9: the keymap editor — every action with its effective shortcut(s), NLE-standard by default.
 * "Change" captures the next key press (Escape cancels; Backspace on its own unbinds), conflicts are
 * listed and must be resolved before saving, "Reset" returns an action (or everything) to its
 * defaults. Saved per user, so shortcuts follow the editor across projects and devices.
 */
export function KeymapEditor({ opened, onClose, overrides, onSave, onRestore, canRestore = false }: KeymapEditorProps) {
  const t = useT('editor.prefs.keymapEditor');
  const [query, setQuery] = useState('');
  const [note, setNote] = useState<string | null>(null);
  const fileRef = useRef<HTMLInputElement | null>(null);
  const [draft, setDraft] = useState<Record<string, string>>(overrides);
  const [capturing, setCapturing] = useState<KeyAction | null>(null);
  const [saving, setSaving] = useState(false);
  // W0.6 (UI2-08): a failed save keeps the dialog (and the draft) open with the reason.
  const [saveError, setSaveError] = useState<string | null>(null);
  useEffect(() => {
    if (opened) {
      const handle = window.setTimeout(() => {
        setDraft(overrides);
        setSaveError(null);
      }, 0);
      return () => window.clearTimeout(handle);
    }
  }, [opened, overrides]);

  useEffect(() => {
    if (!capturing) return;
    const onKey = (event: KeyboardEvent) => {
      event.preventDefault();
      event.stopPropagation();
      const chord = eventToChord(event);
      if (chord === 'Escape') {
        setCapturing(null);
        return;
      }
      if (isModifierOnly(chord)) return;
      setDraft((d) => ({ ...d, [capturing]: chord === 'Backspace' ? '' : chord }));
      setCapturing(null);
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [capturing]);

  const chords = useMemo(() => effectiveChords(draft), [draft]);
  const clashes = useMemo(() => conflicts(draft), [draft]);
  const clashing = new Set(clashes.flatMap((c) => c.actions));
  const shown = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return KEY_ACTIONS;
    return KEY_ACTIONS.filter((a) => a.label.toLowerCase().includes(q) || a.group.toLowerCase().includes(q) || chords[a.id].some((c) => c.toLowerCase().includes(q)));
  }, [query, chords]);

  return (
    <Modal opened={opened} onClose={onClose} title={t('keyboardShortcuts')} size="lg" data-testid="keymap-editor">
      <Stack gap="xs">
        {saveError && (
          <Alert color="red" variant="light" title={t('shortcutsNotSaved')} data-testid="keymap-save-error">
            {saveError}
          </Alert>
        )}
        <Group gap="xs" align="flex-end" wrap="wrap">
          <TextInput size="xs" label={t('findAShortcut')} placeholder={t('actionOrKey')} value={query} onChange={(e) => setQuery(e.currentTarget.value)} style={{ flex: 1, minWidth: 160 }} data-testid="keymap-search" />
          <Button size="xs" fz="xs" variant="default" mih={44} onClick={() => {
            const url = URL.createObjectURL(new Blob([keymapToJson(draft)], { type: 'application/json' }));
            const a = document.createElement('a');
            a.href = url;
            a.download = 'reelbolt-shortcuts.json';
            a.click();
            URL.revokeObjectURL(url);
          }} data-testid="keymap-export">{t('exportJson')}</Button>
          <Button size="xs" fz="xs" variant="default" mih={44} onClick={() => fileRef.current?.click()} data-testid="keymap-import">{t('importJson')}</Button>
          <input
            ref={fileRef}
            type="file"
            accept="application/json,.json"
            hidden
            data-testid="keymap-import-file"
            onChange={async (e) => {
              const file = e.currentTarget.files?.[0];
              e.currentTarget.value = '';
              if (!file) return;
              const imported = keymapFromJson(await file.text());
              if (!imported) {
                setNote(t('thatFileIsNot'));
                return;
              }
              setDraft(imported);
              setNote(t('loadedOtherSaveTo', { length: Object.keys(imported).length }));
            }}
          />
        </Group>
        {note && <Text size="xs" c="dimmed" data-testid="keymap-note">{note}</Text>}
        {clashes.length > 0 && (
          <Alert color="orange" variant="light" data-testid="keymap-conflicts">
            {clashes.map((c) => t('isBoundTo', { chord: c.chord, v2: c.actions.map((a) => KEY_ACTIONS.find((x) => x.id === a)?.label).join(' and ') })).join('; ')}.
          </Alert>
        )}
        <ScrollArea h={420}>
          <Table striped withRowBorders={false} verticalSpacing={2}>
            <Table.Tbody>
              {shown.map((action) => (
                <Table.Tr key={action.id} data-testid={`keymap-row-${action.id}`}>
                  <Table.Td>
                    <Text size="xs">{action.label}</Text>
                    <Text size="12px" c="dimmed">{action.group}</Text>
                  </Table.Td>
                  <Table.Td>
                    {capturing === action.id ? (
                      <Badge size="xs" fz="xs" color="blue">{t('pressAKey')}</Badge>
                    ) : chords[action.id].length === 0 ? (
                      <Text size="xs" c="dimmed">{t('unbound')}</Text>
                    ) : (
                      <Group gap={4}>
                        {chords[action.id].map((c) => (
                          <Kbd key={c} size="xs" style={clashing.has(action.id) ? { borderColor: 'var(--mantine-color-orange-6)' } : undefined}>{c}</Kbd>
                        ))}
                      </Group>
                    )}
                  </Table.Td>
                  <Table.Td>
                    <Group gap={4} justify="flex-end">
                      <Button size="compact-xs" fz="xs" variant="light" onClick={() => setCapturing(action.id)} data-testid={`keymap-change-${action.id}`}>
                        {t('change')}
                      </Button>
                      {draft[action.id] !== undefined && (
                        <Button size="compact-xs" fz="xs" variant="subtle" onClick={() => setDraft((d) => { const next = { ...d }; delete next[action.id]; return next; })} data-testid={`keymap-reset-${action.id}`}>
                          {t('reset')}
                        </Button>
                      )}
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </ScrollArea>
        {shown.length === 0 && <Text size="xs" c="dimmed" data-testid="keymap-none">{t('noShortcutMatchesThat')}</Text>}
        <Group justify="space-between">
          <Group gap="xs">
            <Button size="xs" fz="xs" variant="subtle" onClick={() => setDraft({})} data-testid="keymap-reset-all">
              {t('resetAllToDefaults')}
            </Button>
            {onRestore && (
              <Button
                size="xs" fz="xs" variant="subtle" disabled={!canRestore}
                onClick={async () => {
                  setSaveError(null);
                  try {
                    await onRestore();
                    onClose();
                  } catch (error) {
                    setSaveError(humanizeErrorT(t, error, t('theEarlierSettingsCould')));
                  }
                }}
                data-testid="prefs-restore"
              >
                {t('restorePreviousSettings')}
              </Button>
            )}
          </Group>
          <Group gap="xs">
            <Button size="xs" fz="xs" variant="subtle" onClick={onClose}>{t('cancel')}</Button>
            <Button
              size="xs" fz="xs"
              disabled={clashes.length > 0}
              loading={saving}
              onClick={async () => {
                setSaving(true);
                setSaveError(null);
                try {
                  await onSave(draft);
                  onClose();
                } catch (error) {
                  setSaveError(humanizeErrorT(t, error, t('theShortcutsCouldNot')));
                } finally {
                  setSaving(false);
                }
              }}
              data-testid="keymap-save"
            >
              {t('saveShortcuts')}
            </Button>
          </Group>
        </Group>
        <Text size="12px" c="dimmed">{t('changeThenPressThe')}</Text>
      </Stack>
    </Modal>
  );
}
