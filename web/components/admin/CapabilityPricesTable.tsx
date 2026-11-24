'use client';

import { Badge, Button, Group, Table, Text, Tooltip } from '@mantine/core';
import { IconPlus } from '@tabler/icons-react';
import type { ReactNode } from 'react';
import type { ModelCapability, ModelCapabilityPrice } from '@/lib/types/inference-provider';
import {
  canRetirePrice,
  effectiveStatus,
  formatPercent,
  formatPriceCost,
  formatPriceCredits,
} from '@/lib/utils/billing';

const STATUS_COLOR: Record<string, string> = { Open: 'green', Scheduled: 'blue', Ended: 'gray' };

/** Stacked-card cell on a phone, ordinary table cell from `sm` up. */
const CELL = { display: { base: 'block', sm: 'table-cell' } } as const;

/** Column name above a value in the stacked layout; hidden once the real header row shows. */
function CellLabel({ children }: { children: ReactNode }) {
  return (
    <Text size="xs" c="dimmed" hiddenFrom="sm">
      {children}
    </Text>
  );
}

const formatInstant = (iso: string) => new Date(iso).toLocaleString();

/**
 * The price history of one PLATFORM model capability: per row its unit, variant, cost, the credits
 * override if any, the credits a unit is actually charged at and the margin that leaves. Token prices
 * read per million tokens. A price is never edited in place; Retire ends an open one.
 *
 * Rendered only on the platform admin's pages. Workspace capabilities are never priced.
 */
export function CapabilityPricesTable({
  capability,
  label,
  now,
  onAdd,
  onRetire,
}: {
  capability: ModelCapability;
  /** "{model} · {capability}", for the accessible names of the row actions. */
  label: string;
  now: Date;
  onAdd: () => void;
  onRetire: (price: ModelCapabilityPrice) => void;
}) {
  const prices = capability.prices ?? [];
  return (
    <>
      <Group justify="space-between" mt="xs" wrap="wrap" gap="xs">
        <Text size="xs" c="dimmed" fw={600} tt="uppercase">
          Prices{capability.priceSource === 'Oracle' ? ' (the vendor reports its own cost)' : ''}
        </Text>
        <Button size="compact-xs" mih={{ base: 40, sm: 0 }} variant="light" leftSection={<IconPlus size={12} />} onClick={onAdd} aria-label={`Add price to ${label}`}>
          Add price
        </Button>
      </Group>
      {prices.length === 0 ? (
        <Text size="xs" c="orange">
          No price yet. Platform usage of this capability is charged nothing until it has one.
        </Text>
      ) : (
        <Table.ScrollContainer minWidth={0}>
          {/* Below `sm` each price is a stacked, labelled card; from `sm` up it is the ten-column table. */}
          <Table withTableBorder fz="xs" verticalSpacing={4} miw={{ base: 0, sm: 900 }}>
            <Table.Thead visibleFrom="sm">
              <Table.Tr>
                <Table.Th>Unit</Table.Th>
                <Table.Th>Variant</Table.Th>
                <Table.Th>Cost</Table.Th>
                <Table.Th>Credits override</Table.Th>
                <Table.Th>Effective credits</Table.Th>
                <Table.Th>Margin</Table.Th>
                <Table.Th>From</Table.Th>
                <Table.Th>To</Table.Th>
                <Table.Th>Status</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {prices.map((price) => {
                const status = effectiveStatus(price, now);
                return (
                  <Table.Tr key={price.id} display={{ base: 'block', sm: 'table-row' }} pb={{ base: 'xs', sm: 0 }}>
                    <Table.Td {...CELL}><CellLabel>Unit</CellLabel>{price.unit}</Table.Td>
                    <Table.Td {...CELL}><CellLabel>Variant</CellLabel>{price.variant ?? <Text span size="xs" c="dimmed">any</Text>}</Table.Td>
                    <Table.Td {...CELL}><CellLabel>Cost</CellLabel>{formatPriceCost(price.costUsdPerUnit, price.unit)}</Table.Td>
                    <Table.Td {...CELL}><CellLabel>Credits override</CellLabel>
                      {price.creditsMillisPerUnitOverride === null ? (
                        <Text span size="xs" c="dimmed">from markup</Text>
                      ) : (
                        formatPriceCredits(price.creditsMillisPerUnitOverride, price.unit)
                      )}
                    </Table.Td>
                    <Table.Td {...CELL}><CellLabel>Effective credits</CellLabel>{formatPriceCredits(price.effectiveCreditsMillisPerUnit, price.unit)}</Table.Td>
                    <Table.Td {...CELL}><CellLabel>Margin</CellLabel>
                      <Text span size="xs" c={price.effectiveMarginPercent < 0 ? 'red' : undefined}>
                        {formatPercent(price.effectiveMarginPercent)}
                      </Text>
                    </Table.Td>
                    <Table.Td {...CELL}><CellLabel>From</CellLabel>{formatInstant(price.effectiveFrom)}</Table.Td>
                    <Table.Td {...CELL}><CellLabel>To</CellLabel>{price.effectiveTo ? formatInstant(price.effectiveTo) : <Text span size="xs" c="dimmed">open</Text>}</Table.Td>
                    <Table.Td {...CELL}><CellLabel>Status</CellLabel>
                      <Tooltip label={price.notes ?? ''} disabled={!price.notes} multiline w={280}>
                        <Badge size="xs" variant="light" color={STATUS_COLOR[status]}>{status}</Badge>
                      </Tooltip>
                    </Table.Td>
                    <Table.Td {...CELL}>
                      {canRetirePrice(price, now) && (
                        <Button
                          size="compact-xs"
                          mih={{ base: 40, sm: 0 }}
                          variant="subtle"
                          color="red"
                          onClick={() => onRetire(price)}
                          aria-label={`Retire ${price.unit}${price.variant ? ` ${price.variant}` : ''} price of ${label}`}
                        >
                          Retire
                        </Button>
                      )}
                    </Table.Td>
                  </Table.Tr>
                );
              })}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}
    </>
  );
}
