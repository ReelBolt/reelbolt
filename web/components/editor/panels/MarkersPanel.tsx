'use client';

import React, { useState } from 'react';
import { Badge, Button, Group, ScrollArea, Stack, Text, TextInput, Tooltip, UnstyledButton } from '@mantine/core';
import { IconBookmark, IconBookmarkPlus, IconTrash } from '@tabler/icons-react';
import { EditorEmpty } from '../ui/EditorEmpty';
import { framesToTimecode } from '@/lib/editing/time';
import type { Rational } from '@/lib/editing/types';
import type { MarkerRow } from '@/lib/editing/markers/markers';
import { useRefusalToast } from '../shell/refusal-toast';
import { EditorIcon } from '../ui';
import { useT } from '@/i18n';

export interface MarkersPanelProps {
  rows: readonly MarkerRow[];
  rate: Rational;
  playheadFrame: number;
  onSeek: (frame: number) => void;
  onAdd: (name: string) => void;
  onRename: (markerId: string, name: string) => void;
  onRemove: (markerId: string) => void;
  onSelectItem: (itemId: string) => void;
}

/**
 * The marker index: user markers (editable) and agent-rationale markers (derived, read-only, labelled
 * with the real agent/provider names from provenance — never a fabricated label).
 */
export function MarkersPanel({ rows, rate, playheadFrame, onSeek, onAdd, onRename, onRemove, onSelectItem }: MarkersPanelProps) {
  const t = useT('editor.panels.markersPanel');
  const { showRefusalMessage } = useRefusalToast();
  const [newName, setNewName] = useState('');
  return (
    <Stack gap="xs" data-testid="markers-panel">
      <Group justify="space-between">
        <Text size="sm" fw={600}>
          {t('markers')}
        </Text>
        <Text size="xs" c="dimmed">
          {rows.length}
        </Text>
      </Group>
      <Group gap={4} wrap="nowrap">
        <TextInput
          size="xs"
          placeholder={t('markerAt', { v1: framesToTimecode(playheadFrame, rate) })}
          value={newName}
          onChange={(e) => setNewName(e.currentTarget.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              onAdd(newName.trim() || 'Marker');
              setNewName('');
            }
            e.stopPropagation();
          }}
          style={{ flex: 1 }}
          aria-label={t('newMarkerName')}
          data-testid="marker-name-input"
        />
        <Button
          size="xs" fz="xs"
          variant="light"
          leftSection={<IconBookmarkPlus size={14} />}
          onClick={() => {
            onAdd(newName.trim() || 'Marker');
            setNewName('');
          }}
          data-testid="marker-add"
        >
          {t('add')}
        </Button>
      </Group>
      <ScrollArea.Autosize mah={360}>
        <Stack gap={2}>
          {rows.length === 0 && (
            <EditorEmpty
              compact
              icon={IconBookmark}
              title={t('noMarkersYet')}
              hint={t('markersFlagMomentsOn')}
              action={{ label: t('addAMarkerHere'), onClick: () => onAdd('Marker'), testId: 'marker-add-first' }}
              testId="markers-empty"
            />
          )}
          {rows.map((row) => (
            <Group key={row.id} gap={4} wrap="nowrap" data-testid={`marker-row-${row.id}`} data-marker-kind={row.kind}>
              <UnstyledButton onClick={() => onSeek(row.frame)} style={{ flex: 1, minWidth: 0 }} aria-label={t('goTo', { name: row.name })}>
                <Group gap={6} wrap="nowrap">
                  <Text size="xs" ff="monospace" c="dimmed">
                    {framesToTimecode(row.frame, rate)}
                  </Text>
                  <Badge size="xs" fz="xs" color={row.kind === 'rationale' ? 'green' : 'blue'} variant="light">
                    {row.kind === 'rationale' ? t('rationale') : t('marker')}
                  </Badge>
                  <Text size="xs" truncate>
                    {row.name}
                  </Text>
                </Group>
                {row.note && (
                  <Text size="xs" c="dimmed" lineClamp={2}>
                    {row.note}
                    {row.kind === 'rationale' && (row.agentName || row.providerName) ? `: ${[row.agentName, row.providerName].filter(Boolean).join(' · ')}` : ''}
                  </Text>
                )}
              </UnstyledButton>
              {row.kind === 'rationale' && row.itemId ? (
                <Tooltip label={t('selectTheClipThis')}>
                  <Button size="compact-xs" fz="xs" variant="subtle" onClick={() => onSelectItem(row.itemId!)}>
                    {t('clip')}
                  </Button>
                </Tooltip>
              ) : (
                <>
                  <TextInput
                    size="xs"
                    defaultValue={row.name}
                    onBlur={(e) => {
                      const value = e.currentTarget.value.trim();
                      if (!value) {
                        // d1 N6: a marker keeps a name; say so and put the stored one back.
                        e.currentTarget.value = row.name;
                        showRefusalMessage('A marker needs a name. The old name is kept.');
                        return;
                      }
                      if (value !== row.name) onRename(row.id, value);
                    }}
                    onKeyDown={(e) => {
                      e.stopPropagation();
                      // Enter commits (the blur handler above stores it); Escape puts the stored name back.
                      if (e.key === 'Enter') e.currentTarget.blur();
                      else if (e.key === 'Escape') {
                        e.currentTarget.value = row.name;
                        e.currentTarget.blur();
                      }
                    }}
                    w={90}
                    aria-label={t('rename', { name: row.name })}
                  />
                  <EditorIcon icon={IconTrash} size="sm" color="red" label={t('remove', { name: row.name })} onClick={() => onRemove(row.id)} />
                </>
              )}
            </Group>
          ))}
        </Stack>
      </ScrollArea.Autosize>
    </Stack>
  );
}
