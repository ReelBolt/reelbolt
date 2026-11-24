'use client';

import Link from 'next/link';
import { Alert, Anchor, Badge, Group, Table, Text } from '@mantine/core';
import { IconAlertTriangle, IconCircleCheck } from '@tabler/icons-react';
import type { PriceCoverageGap } from '@/lib/types/pricing';

/**
 * Which enabled platform capabilities can be metered in a unit that has no price. Platform usage of
 * such a unit is charged nothing, so each gap links to the provider page where the price is added.
 */
export function PriceCoveragePanel({ gaps }: { gaps: PriceCoverageGap[] | undefined }) {
  if (!gaps) return null;
  if (gaps.length === 0) {
    return (
      <Alert color="green" variant="light" icon={<IconCircleCheck size={16} />} title="Every capability has a price">
        Every enabled platform capability has a price for each unit it can be metered in.
      </Alert>
    );
  }
  return (
    <Alert
      color="yellow"
      variant="light"
      icon={<IconAlertTriangle size={16} />}
      title={`${gaps.length} capabilit${gaps.length === 1 ? 'y has' : 'ies have'} no price`}
    >
      <Text size="sm" mb="xs">
        Platform usage of these is charged nothing until a price is added on the provider&apos;s page.
      </Text>
      <Table.ScrollContainer minWidth={640}>
        <Table withTableBorder fz="sm">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Provider</Table.Th>
              <Table.Th>Model</Table.Th>
              <Table.Th>Capability</Table.Th>
              <Table.Th>Missing units</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {gaps.map((gap) => (
              <Table.Tr key={gap.capabilityId}>
                <Table.Td>
                  <Anchor component={Link} href={`/admin/inference-providers/${gap.providerId}`} size="sm">
                    {gap.providerName}
                  </Anchor>
                </Table.Td>
                <Table.Td>{gap.modelDisplayName}</Table.Td>
                <Table.Td>{gap.capability}</Table.Td>
                <Table.Td>
                  <Group gap={4}>
                    {gap.missingUnits.map((unit) => (
                      <Badge key={unit} size="sm" variant="light" color="yellow">{unit}</Badge>
                    ))}
                  </Group>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Table.ScrollContainer>
    </Alert>
  );
}
