'use client';

import Link from 'next/link';
import { Anchor, Card, Stack, Table, Text, Title } from '@mantine/core';
import type { ConsolidationReport } from '@/lib/types/inference-provider';

/**
 * Read-only: which providers the catalog upgrade merged into one account, so an admin can see why a
 * provider they remember is gone. Renders nothing when there was nothing to merge.
 */
export function ConsolidationReportPanel({ report }: { report: ConsolidationReport | undefined }) {
  if (!report || report.groups.length === 0) return null;
  return (
    <Card withBorder padding="md">
      <Stack gap="xs">
        <Title order={5}>Providers merged by the catalog upgrade</Title>
        <Text size="sm" c="dimmed">
          Providers that shared a kind, an endpoint and a key became one provider with several models.
          Nothing that pointed at them changed.
        </Text>
        <Table.ScrollContainer minWidth={520}>
          <Table withTableBorder fz="sm">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Provider kept</Table.Th>
                <Table.Th>Providers merged into it</Table.Th>
                <Table.Th>Models moved</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {report.groups.map((group) => (
                <Table.Tr key={group.survivorId}>
                  <Table.Td>
                    <Anchor component={Link} href={`/admin/inference-providers/${group.survivorId}`} size="sm">
                      {group.survivorName}
                    </Anchor>
                  </Table.Td>
                  <Table.Td>{group.mergedProviderIds.length}</Table.Td>
                  <Table.Td>{group.mergedModelCount}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      </Stack>
    </Card>
  );
}
