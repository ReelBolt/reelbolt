'use client';

import { Badge, Table, Text } from '@mantine/core';
import type { RateMultiplier } from '@/lib/types/pricing';
import { effectiveStatus, formatMultiplier } from '@/lib/utils/billing';

const STATUS_COLOR: Record<string, string> = { Open: 'green', Scheduled: 'blue', Ended: 'gray' };

/**
 * Multipliers scale a priced event by billing mode (Platform 1.0, BYO 0 for LLM kinds, Runner 0 or a
 * fraction for compute kinds). A null usage kind is the default for the mode; a kind-specific row
 * wins over it. Read-only here: a change is a new superseding row.
 */
export function RateMultiplierTable({ multipliers, now }: { multipliers: RateMultiplier[]; now: Date }) {
  return (
    <Table.ScrollContainer minWidth={720}>
      <Table withTableBorder highlightOnHover>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>Billing mode</Table.Th>
            <Table.Th>Usage kind</Table.Th>
            <Table.Th>Multiplier</Table.Th>
            <Table.Th>Effective from</Table.Th>
            <Table.Th>Effective to</Table.Th>
            <Table.Th>Status</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {multipliers.map((row) => {
            const status = effectiveStatus(row, now);
            return (
              <Table.Tr key={row.id}>
                <Table.Td>{row.billingMode}</Table.Td>
                <Table.Td>
                  {row.usageKind ?? (
                    <Text span size="sm" c="dimmed">
                      all kinds (mode default)
                    </Text>
                  )}
                </Table.Td>
                <Table.Td>{formatMultiplier(row.multiplier)}</Table.Td>
                <Table.Td>
                  <Text size="sm">{new Date(row.effectiveFrom).toLocaleString()}</Text>
                </Table.Td>
                <Table.Td>
                  <Text size="sm" c={row.effectiveTo ? undefined : 'dimmed'}>
                    {row.effectiveTo ? new Date(row.effectiveTo).toLocaleString() : 'open'}
                  </Text>
                </Table.Td>
                <Table.Td>
                  <Badge color={STATUS_COLOR[status]} variant="light">
                    {status}
                  </Badge>
                </Table.Td>
              </Table.Tr>
            );
          })}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}
