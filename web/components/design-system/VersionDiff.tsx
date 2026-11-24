'use client';

import { Badge, Group, Stack, Table, Text } from '@mantine/core';
import type { DesignDiff, DesignDiffChange } from '@/lib/types/design-system';
import { fontStackText, isFontFamilyPath } from '@/lib/design-system/font-diff';
import { ColorSwatch } from './ColorSwatch';
import { TypeSpecimen } from './TypeSpecimen';
import {
  DIFF_ADDED_LABEL,
  DIFF_AFTER_LABEL,
  DIFF_BEFORE_LABEL,
  DIFF_CHANGED_LABEL,
  DIFF_EMPTY,
  DIFF_PATH_LABEL,
  DIFF_REMOVED_LABEL,
  LOCKED_LABEL,
  PROPOSAL_CONFLICT_BADGE,
  TOKEN_PROVENANCE_LOCATION,
} from './copy';
import { useT } from '@/i18n';

/** The `$value.hex` of one side of a diff, when that side is a colour; null for any other shape. */
function hexOf(value: unknown): string | null {
  if (typeof value !== 'object' || value === null) return null;
  const hex = (value as Record<string, unknown>).hex;
  return typeof hex === 'string' ? hex : null;
}

/** A compact rendering of a non-colour diff value. */
function valueText(value: unknown): string | null {
  if (value === undefined || value === null) return null;
  if (typeof value === 'string') return value;
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);
  try {
    return JSON.stringify(value);
  } catch {
    return null;
  }
}

/** One diff side: a colour swatch with its hex, any other value as JSON text, or an em dash. */
function ValueCell({ value, path }: { value: unknown; path: string }) {
  const hex = hexOf(value);
  if (hex) return <ColorSwatch name="" hex={hex} />;
  const text = valueText(value);
  const font = isFontFamilyPath(path) ? fontStackText(value) : null;
  return text ? (
    <>
      <Text size="xs" ff="monospace">{text}</Text>
      {font && <TypeSpecimen compact family={font} />}
    </>
  ) : (
    <Text size="xs" c="dimmed">—</Text>
  );
}

/**
 * The side-by-side two-version diff (P3-04, SPEC §13.3): a counts summary over a before/after table
 * with one row per added/removed/changed path, colour values as swatch + hex and provenance shown
 * as a muted `file:line` caption.
 */
export function VersionDiff({ diff }: { diff: DesignDiff }) {
  const tr = useT();
  const counts = diff.counts ?? { added: 0, removed: 0, changed: 0 };
  const rows: DesignDiffChange[] = [
    ...(diff.changed ?? []),
    ...(diff.added ?? []),
    ...(diff.removed ?? []),
  ];

  return (
    <Stack gap="sm" data-testid="version-diff">
      <Group gap="xs">
        <Badge variant="light" color="green" data-testid="diff-added">
          {tr.msg(DIFF_ADDED_LABEL)} {counts.added}
        </Badge>
        <Badge variant="light" color="red" data-testid="diff-removed">
          {tr.msg(DIFF_REMOVED_LABEL)} {counts.removed}
        </Badge>
        <Badge variant="light" color="blue" data-testid="diff-changed">
          {tr.msg(DIFF_CHANGED_LABEL)} {counts.changed}
        </Badge>
      </Group>

      {rows.length === 0 ? (
        <Text size="sm" c="dimmed">{tr.msg(DIFF_EMPTY)}</Text>
      ) : (
        <Table.ScrollContainer minWidth={560}>
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{tr.msg(DIFF_PATH_LABEL)}</Table.Th>
                <Table.Th>{tr.msg(DIFF_BEFORE_LABEL)}</Table.Th>
                <Table.Th>{tr.msg(DIFF_AFTER_LABEL)}</Table.Th>
                <Table.Th>{tr.msg(TOKEN_PROVENANCE_LOCATION)}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rows.map((entry) => (
                <Table.Tr key={entry.path} data-testid={`diff-row-${entry.path}`}>
                  <Table.Td>
                    <Group gap="xs" wrap="nowrap">
                      <Text size="sm" ff="monospace">{entry.path}</Text>
                      {entry.conflict && (
                        <Badge size="xs" variant="light" color="yellow">
                          {tr.msg(PROPOSAL_CONFLICT_BADGE)}
                        </Badge>
                      )}
                      {entry.locked && (
                        <Badge size="xs" variant="light" color="gray">
                          {tr.msg(LOCKED_LABEL)}
                        </Badge>
                      )}
                    </Group>
                  </Table.Td>
                  <Table.Td><ValueCell value={entry.before} path={entry.path} /></Table.Td>
                  <Table.Td><ValueCell value={entry.after} path={entry.path} /></Table.Td>
                  <Table.Td>
                    {entry.prov?.loc ? (
                      <Text size="xs" c="dimmed">{entry.prov.loc}</Text>
                    ) : (
                      <Text size="xs" c="dimmed">—</Text>
                    )}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}
    </Stack>
  );
}
