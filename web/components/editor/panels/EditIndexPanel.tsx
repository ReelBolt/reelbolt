'use client';

import React, { useMemo, useState } from 'react';
import { Group, ScrollArea, Stack, Table, Text, TextInput, UnstyledButton } from '@mantine/core';
import { framesToTimecode } from '@/lib/editing/time';
import type { Rational } from '@/lib/editing/types';
import { buildEditIndex, filterEditIndex, sortEditIndex, type EditIndexRow, type EditIndexSortKey } from '@/lib/editing/edit-index/rows';
import type { EditTimeline } from '@/lib/editing/types';
import { useT } from '@/i18n';

export interface EditIndexPanelProps {
  doc: EditTimeline;
  rate: Rational;
  selection: readonly string[];
  onSeek: (frame: number) => void;
  onSelect: (itemId: string) => void;
}

const COLUMNS: { key: EditIndexSortKey; label?: string }[] = [
  { key: 'index', label: '#' },
  { key: 'trackName' },
  { key: 'name' },
  { key: 'itemType' },
  { key: 'recordIn' },
  { key: 'durationFrames' },
];

/** The EDL-style edit index: every event, sortable, filterable, click-to-seek. Read-only projection. */
export function EditIndexPanel({ doc, rate, selection, onSeek, onSelect }: EditIndexPanelProps) {
  const t = useT('editor.panels.editIndexPanel');
  const [sortKey, setSortKey] = useState<EditIndexSortKey>('index');
  const [descending, setDescending] = useState(false);
  const [query, setQuery] = useState('');

  const rows = useMemo(() => buildEditIndex(doc), [doc]);
  const visible = useMemo(() => sortEditIndex(filterEditIndex(rows, query), sortKey, descending), [rows, query, sortKey, descending]);
  const selected = useMemo(() => new Set(selection), [selection]);

  const toggleSort = (key: EditIndexSortKey) => {
    if (key === sortKey) setDescending((d) => !d);
    else {
      setSortKey(key);
      setDescending(false);
    }
  };

  return (
    <Stack gap="xs" data-testid="edit-index-panel">
      <Group justify="space-between">
        <Text size="sm" fw={600}>
          {t('editIndex')}
        </Text>
        <Text size="xs" c="dimmed" data-testid="edit-index-count">
          {visible.length} / {rows.length}
        </Text>
      </Group>
      <TextInput size="xs" placeholder={t('filter')} value={query} onChange={(e) => setQuery(e.currentTarget.value)} onKeyDown={(e) => e.stopPropagation()} aria-label={t('filterEditIndex')} />
      <ScrollArea.Autosize mah={360} offsetScrollbars>
        <Table fz="xs" verticalSpacing={2} horizontalSpacing={6} highlightOnHover withRowBorders={false}>
          <Table.Thead>
            <Table.Tr>
              {COLUMNS.map((column) => (
                <Table.Th key={column.key}>
                  <UnstyledButton onClick={() => toggleSort(column.key)} aria-label={t('sortBy', { label: column.label ?? t(`columns.${column.key}`) })} style={{ fontSize: 12, fontWeight: 600 }}>
                    {column.label ?? t(`columns.${column.key}`)}
                    {sortKey === column.key ? (descending ? ' ↓' : ' ↑') : '' /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
                  </UnstyledButton>
                </Table.Th>
              ))}
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {visible.map((row: EditIndexRow) => (
              <Table.Tr
                key={row.itemId}
                data-testid={`edit-index-row-${row.itemId}`}
                style={{ cursor: 'var(--rb-cursor-pointer)', background: selected.has(row.itemId) ? 'var(--mantine-color-violet-light)' : undefined, opacity: row.enabled ? 1 : 0.6 }}
                onClick={() => {
                  onSeek(row.recordIn);
                  onSelect(row.itemId);
                }}
              >
                <Table.Td>{row.index}</Table.Td>
                <Table.Td>{row.trackName}</Table.Td>
                <Table.Td>
                  <Text size="xs" truncate maw={140} title={row.rationale ?? undefined}>
                    {row.name}
                  </Text>
                </Table.Td>
                <Table.Td>{row.itemType}</Table.Td>
                <Table.Td ff="monospace">{framesToTimecode(row.recordIn, rate)}</Table.Td>
                <Table.Td ff="monospace">{framesToTimecode(row.durationFrames, rate)}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea.Autosize>
    </Stack>
  );
}
