'use client';

import { useState, type ReactNode } from 'react';
import { Alert, Badge, Button, Card, Center, Group, Loader, Stack, Table, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconCoin } from '@tabler/icons-react';
import { ApiError } from '@/lib/api/client';
import { promotionAdminMessage, type AdminCreditGrant, type AdminOrg } from '@/lib/api/promotions';
import { useOrgCreditGrants } from '@/lib/hooks/use-promotions';
import { GrantCreditsModal } from './GrantCreditsModal';

function shortDate(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

/** Opaque per-opening id for the grant request. */
function newRequestId(): string {
  return crypto.randomUUID();
}

/**
 * Organizations with their credit balances, a "Grant credits" action per row and the recent admin
 * grants of the selected one. Used on the admin user page (the user's memberships) and on the
 * Promotions page (an org search, passed in as `toolbar`).
 */
export function OrgCreditsCard({
  title,
  description,
  orgs,
  error,
  isLoading,
  onChanged,
  toolbar,
}: {
  title: string;
  description: string;
  orgs: AdminOrg[] | undefined;
  error: unknown;
  isLoading: boolean;
  /** Re-read the org list (its balances) after a grant. */
  onChanged: () => void;
  toolbar?: ReactNode;
}) {
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [grantTarget, setGrantTarget] = useState<{ org: AdminOrg; requestId: string } | null>(null);
  const grants = useOrgCreditGrants(selectedId);
  const selected = orgs?.find((o) => o.id === selectedId) ?? null;

  function granted(org: AdminOrg, grant: AdminCreditGrant) {
    notifications.show({
      title: 'Credits granted',
      message: `${grant.credits.toLocaleString('en-US')} credits to ${org.name}, expiring ${shortDate(grant.expiresAt)}.`,
      color: 'green',
    });
    setSelectedId(org.id);
    void grants.mutate();
    onChanged();
  }

  const billingOff = error instanceof ApiError && error.status === 404;

  return (
    <Card withBorder data-testid="org-credits-card">
      <Stack gap="sm">
        <div>
          <Text fw={600}>{title}</Text>
          <Text size="sm" c="dimmed">{description}</Text>
        </div>
        {toolbar}

        {billingOff ? (
          <Text size="sm" c="dimmed">Billing is not enabled on this deployment, so there are no credits to grant.</Text>
        ) : error ? (
          <Alert color="red" variant="light">{promotionAdminMessage(error)}</Alert>
        ) : isLoading && !orgs ? (
          <Center h={80}><Loader size="sm" /></Center>
        ) : !orgs || orgs.length === 0 ? (
          <Text size="sm" c="dimmed">No organizations found.</Text>
        ) : (
          <Table.ScrollContainer minWidth={640}>
            <Table highlightOnHover verticalSpacing="xs" data-testid="org-credits-table">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Organization</Table.Th>
                  <Table.Th>Owner</Table.Th>
                  <Table.Th>Plan</Table.Th>
                  <Table.Th>Balance</Table.Th>
                  <Table.Th aria-label="Actions" />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {orgs.map((org) => (
                  <Table.Tr
                    key={org.id}
                    onClick={() => setSelectedId(org.id)}
                    style={{ cursor: 'var(--rb-cursor-pointer)' }}
                    bg={org.id === selectedId ? 'var(--mantine-color-default-hover)' : undefined}
                  >
                    <Table.Td>
                      <Group gap="xs">
                        <Text size="sm" fw={500}>{org.name}</Text>
                        <Badge size="xs" variant="light" color="gray">{org.kind}</Badge>
                      </Group>
                    </Table.Td>
                    <Table.Td><Text size="sm">{org.ownerEmail ?? '—'}</Text></Table.Td>
                    <Table.Td><Text size="sm">{org.planKey ?? '—'}</Text></Table.Td>
                    <Table.Td>
                      <Text size="sm" style={{ fontVariantNumeric: 'tabular-nums' }}>
                        {org.balance.toLocaleString('en-US')} credits
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <Button
                        size="xs"
                        variant="light"
                        leftSection={<IconCoin size={14} />}
                        data-testid="grant-credits-button"
                        onClick={(event) => {
                          event.stopPropagation();
                          setGrantTarget({ org, requestId: newRequestId() });
                        }}
                      >
                        Grant credits
                      </Button>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}

        {selected && (
          <Stack gap={4} data-testid="org-credit-grants">
            <Text size="sm" fw={600}>Recent grants to {selected.name}</Text>
            {grants.error ? (
              <Text size="sm" c="red">{promotionAdminMessage(grants.error)}</Text>
            ) : !grants.data ? (
              <Loader size="xs" />
            ) : grants.data.length === 0 ? (
              <Text size="sm" c="dimmed">No credits have been granted to this organization.</Text>
            ) : (
              <Table verticalSpacing={4}>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Granted</Table.Th>
                    <Table.Th>Credits</Table.Th>
                    <Table.Th>Expires</Table.Th>
                    <Table.Th>Reason</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {grants.data.slice(0, 10).map((g) => (
                    <Table.Tr key={g.id}>
                      <Table.Td><Text size="sm">{shortDate(g.createdAt)}</Text></Table.Td>
                      <Table.Td><Text size="sm">{g.credits.toLocaleString('en-US')}</Text></Table.Td>
                      <Table.Td><Text size="sm">{shortDate(g.expiresAt)}</Text></Table.Td>
                      <Table.Td><Text size="sm">{g.reason}</Text></Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </Stack>
        )}
      </Stack>

      {grantTarget && (
        <GrantCreditsModal
          key={grantTarget.requestId}
          org={grantTarget.org}
          requestId={grantTarget.requestId}
          onClose={() => setGrantTarget(null)}
          onGranted={(grant) => granted(grantTarget.org, grant)}
        />
      )}
    </Card>
  );
}
