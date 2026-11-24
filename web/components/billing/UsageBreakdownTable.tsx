'use client';

import { Progress, Stack, Table, Text, Tooltip } from '@mantine/core';
import { useT } from '@/i18n';
import { msg, type Msg } from '@/i18n/msg';
import type { UsageGroup, UsageGroupBy, UsageUnit } from '@/lib/types/billing';
import { formatCredits, formatUnits, usageKindLabel } from '@/lib/utils/usage-labels';
import { usageSourceLabel } from './usageSourceLabels';

interface UsageBreakdownTableProps {
  groups: UsageGroup[];
  groupBy: UsageGroupBy;
  /** Our own provider cost is only present for a platform admin; the column is hidden otherwise. */
  showCost: boolean;
}

/**
 * One row per group of the selected axis. The bar is scaled to the largest group on screen, so its
 * length is a share of this view rather than of any absolute budget — the number beside it is the
 * figure that means something.
 */
export function UsageBreakdownTable({ groups, groupBy, showCost }: UsageBreakdownTableProps) {
  const t = useT('billing.usage');
  const axisHeading = (axis: UsageGroupBy): string => {
    switch (axis) {
      case 'day': return t('axis.day');
      case 'project': return t('axis.project');
      case 'model': return t('axis.model');
      case 'kind': return t('axis.kind');
      case 'mode': return t('axis.mode');
      case 'source': return t('axis.source');
    }
  };
  const max = groups.reduce((m, g) => Math.max(m, g.credits), 0);
  // "By step kind" and "By source" are the axes whose raw key is an enum member name, not a display name.
  const label = (g: UsageGroup) =>
    groupBy === 'kind' ? usageKindLabel(g.key) : groupBy === 'source' ? t.msg(asMsg(usageSourceLabel(g.key))) : g.label;

  return (
    <Table.ScrollContainer minWidth={480}>
      <Table verticalSpacing="sm" highlightOnHover>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{axisHeading(groupBy)}</Table.Th>
            <Table.Th style={{ textAlign: 'right' }}>{t('credits')}</Table.Th>
            <Table.Th style={{ textAlign: 'right' }}>{t('calls')}</Table.Th>
            <Table.Th>{t('consumed')}</Table.Th>
            {showCost && <Table.Th style={{ textAlign: 'right' }}>{t('providerCost')}</Table.Th>}
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {groups.map((group) => (
            <Table.Tr key={group.key || '(none)'}>
              <Table.Td style={{ minWidth: 0 }}>
                {/* The label is user data (a project name, a model id); let it break rather than
                    push the table into a horizontal scroll past the ScrollContainer's own one. */}
                <Text size="sm" fw={600} style={{ wordBreak: 'break-word' }}>{label(group)}</Text>
                <Progress
                  value={max > 0 ? (group.credits / max) * 100 : 0}
                  size={4}
                  radius="sm"
                  mt={6}
                  aria-label={t('shareAria', { name: label(group) })}
                />
              </Table.Td>
              <Table.Td style={{ textAlign: 'right', fontVariantNumeric: 'tabular-nums' }}>
                {formatCredits(group.credits)}
              </Table.Td>
              <Table.Td style={{ textAlign: 'right', fontVariantNumeric: 'tabular-nums' }}>
                {group.events.toLocaleString()}
              </Table.Td>
              <Table.Td>
                <UnitList units={group.units} />
              </Table.Td>
              {showCost && (
                <Table.Td style={{ textAlign: 'right', fontVariantNumeric: 'tabular-nums' }}>
                  {group.costUsd === null ? '—' : `$${group.costUsd.toFixed(4)}`}
                </Table.Td>
              )}
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

/** A usage-source label that is already display text (an unknown source, shown by name) rides in a pass-through message. */
function asMsg(label: Msg | string): Msg {
  return typeof label === 'string' ? msg('billing.usage.raw', { value: label }) : label;
}

/** Up to three kinds inline, the rest behind a hover — a day can carry a dozen units. */
function UnitList({ units }: { units: UsageUnit[] }) {
  const t = useT('billing.usage');
  if (units.length === 0) return <Text size="sm" c="dimmed">—</Text>;

  const shown = units.slice(0, 3);
  const rest = units.slice(3);
  return (
    <Stack gap={2}>
      {shown.map((unit) => (
        <Text key={unit.kind} size="xs" c="dimmed">
          {formatUnits(unit.quantity)} {usageKindLabel(unit.kind).toLowerCase()}
        </Text>
      ))}
      {rest.length > 0 && (
        <Tooltip
          multiline
          maw={280}
          label={rest.map((u) => `${formatUnits(u.quantity)} ${usageKindLabel(u.kind).toLowerCase()}`).join('\n')}
        >
          <Text size="xs" c="dimmed" style={{ whiteSpace: 'pre-line' }}>{t('more', { count: rest.length })}</Text>
        </Tooltip>
      )}
    </Stack>
  );
}
