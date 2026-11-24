'use client';

import { Fragment, useCallback, useEffect, useState } from 'react';
import { Badge, Button, Group, Loader, Stack, Table, Text, Tooltip } from '@mantine/core';
import { getImportCapabilities } from '@/lib/api/design-system';
import type {
  ImportCapabilityAxis,
  ImportCapabilityImporter,
  ImportCapabilitySupport,
} from '@/lib/types/design-system';
import {
  CAPABILITIES_EMPTY,
  CAPABILITIES_LOADING,
  CAPABILITIES_TITLE,
  CAPABILITIES_UNAVAILABLE,
  CAPABILITY_DETAILS_COLUMN,
  CAPABILITY_HIDE_DETAILS_LABEL,
  CAPABILITY_IMPORTER_COLUMN,
  CAPABILITY_OPEN_DETAILS_LABEL,
  CAPABILITY_SUPPORT_LABELS,
  capabilityAxisLabel,
  capabilityCellLabel,
} from './copy';
import { useT } from '@/i18n';

/** What the one fetch has produced so far; a failure is a state, never a thrown render. */
type MatrixState =
  | { status: 'loading' }
  | { status: 'error' }
  | { status: 'ready'; importers: ImportCapabilityImporter[] };

/** Green/yellow/grey, always paired with the word, so colour is never the only signal. */
const SUPPORT_COLORS: Record<ImportCapabilitySupport, string> = {
  Full: 'green',
  Partial: 'yellow',
  None: 'gray',
};

/** The axis order the first importer declares; every row is rendered against this one list. */
function axesOf(importers: ImportCapabilityImporter[]): ImportCapabilityAxis[] {
  return importers[0]?.axes ?? [];
}

/** One row's entry for an axis; null only if the server sends an incomplete row. */
function axisFor(importer: ImportCapabilityImporter, axis: string): ImportCapabilityAxis | null {
  return importer.axes.find((entry) => entry.axis === axis) ?? null;
}

/**
 * The importer × axis capability table (T-101; SPEC §17): which part of a profile each kind of
 * file can give us. Compact by design — a word per cell, the sentence on hover or in the expanded
 * notes row. The matrix is fetched once when this component mounts, and the caller only mounts it
 * when the section is expanded, so the request is lazy. A failed fetch degrades to one short line
 * and never touches the import form.
 */
export function ImportCapabilityMatrix() {
  const tr = useT();
  const [state, setState] = useState<MatrixState>({ status: 'loading' });
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set());

  useEffect(() => {
    let cancelled = false;
    getImportCapabilities()
      .then((response) => {
        if (!cancelled) setState({ status: 'ready', importers: response?.importers ?? [] });
      })
      .catch(() => {
        if (!cancelled) setState({ status: 'error' });
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const toggle = useCallback((importerId: string) => {
    setExpanded((current) => {
      const next = new Set(current);
      if (next.has(importerId)) next.delete(importerId);
      else next.add(importerId);
      return next;
    });
  }, []);

  if (state.status === 'loading') {
    return (
      <Group gap="xs" data-testid="capability-loading">
        <Loader size="xs" />
        <Text size="sm" c="dimmed">
          {tr.msg(CAPABILITIES_LOADING)}
        </Text>
      </Group>
    );
  }

  if (state.status === 'error') {
    return (
      <Text size="sm" c="dimmed" data-testid="capability-unavailable">
        {tr.msg(CAPABILITIES_UNAVAILABLE)}
      </Text>
    );
  }

  if (state.importers.length === 0) {
    return (
      <Text size="sm" c="dimmed" data-testid="capability-unavailable">
        {tr.msg(CAPABILITIES_EMPTY)}
      </Text>
    );
  }

  const axes = axesOf(state.importers);

  return (
    <Table.ScrollContainer minWidth={720}>
      <Table
        aria-label={tr.msg(CAPABILITIES_TITLE)}
        striped
        withTableBorder
        highlightOnHover
        verticalSpacing={4}
        horizontalSpacing={6}
        data-testid="capability-matrix"
      >
        <Table.Thead>
          <Table.Tr>
            <Table.Th scope="col">{tr.msg(CAPABILITY_IMPORTER_COLUMN)}</Table.Th>
            {axes.map((axis) => (
              <Table.Th key={axis.axis} scope="col">
                {tr.msg(capabilityAxisLabel(axis.axis))}
              </Table.Th>
            ))}
            <Table.Th scope="col">{tr.msg(CAPABILITY_DETAILS_COLUMN)}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {state.importers.map((importer) => {
            const isExpanded = expanded.has(importer.id);
            const notesId = `capability-notes-${importer.id}`;
            return (
              <Fragment key={importer.id}>
                <Table.Tr data-testid={`capability-row-${importer.id}`}>
                  <Table.Th scope="row">{importer.label}</Table.Th>
                  {axes.map((axis) => {
                    const capability = axisFor(importer, axis.axis);
                    return (
                      <Table.Td
                        key={axis.axis}
                        data-testid={`capability-cell-${importer.id}-${axis.axis}`}
                        aria-label={
                          capability
                            ? tr.msg(capabilityCellLabel(
                                importer.label,
                                axis.axis,
                                capability.support,
                                capability.note,
                              ))
                            : tr('brandLibraries.profile.capabilities.notListed', { importer: importer.label, axis: tr.msg(capabilityAxisLabel(axis.axis)) })
                        }
                      >
                        {capability ? (
                          <Tooltip
                            label={capability.note}
                            disabled={!capability.note}
                            multiline
                            w={240}
                            withArrow
                          >
                            <Badge
                              color={SUPPORT_COLORS[capability.support] ?? 'gray'}
                              variant="light"
                              size="sm"
                            >
                              {(CAPABILITY_SUPPORT_LABELS[capability.support] ? tr.msg(CAPABILITY_SUPPORT_LABELS[capability.support]) : capability.support)}
                            </Badge>
                          </Tooltip>
                        ) : (
                          <Text size="xs" c="dimmed">
                            —
                          </Text>
                        )}
                      </Table.Td>
                    );
                  })}
                  <Table.Td>
                    <Button
                      variant="subtle"
                      size="compact-xs"
                      onClick={() => toggle(importer.id)}
                      aria-expanded={isExpanded}
                      aria-controls={notesId}
                      data-testid={`capability-toggle-${importer.id}`}
                    >
                      {isExpanded ? tr.msg(CAPABILITY_HIDE_DETAILS_LABEL) : tr.msg(CAPABILITY_OPEN_DETAILS_LABEL)}
                    </Button>
                  </Table.Td>
                </Table.Tr>
                {isExpanded ? (
                  <Table.Tr data-testid={notesId}>
                    <Table.Td colSpan={axes.length + 2}>
                      <Stack gap={4}>
                        {importer.axes.map((entry) => (
                          <Group key={entry.axis} gap={6} align="flex-start" wrap="nowrap">
                            <Text size="xs" fw={600}>
                              {tr.msg(capabilityAxisLabel(entry.axis))}
                            </Text>
                            <Text size="xs">{entry.note}</Text>
                          </Group>
                        ))}
                      </Stack>
                    </Table.Td>
                  </Table.Tr>
                ) : null}
              </Fragment>
            );
          })}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}
