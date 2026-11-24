'use client';

import { useState, type ReactNode } from 'react';
import { Alert, Badge, Button, Card, Center, Loader, Stack, Table, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconGift, IconX } from '@tabler/icons-react';
import { ApiError } from '@/lib/api/client';
import {
  promotionAdminMessage,
  revokeOrgPlanGrant,
  type AdminOrg,
  type AdminPlanGrant,
} from '@/lib/api/promotions';
import { useAdminPlans, useOrgPlanGrants } from '@/lib/hooks/use-promotions';
import { GrantPlanModal } from './GrantPlanModal';

function shortDate(iso: string | null): string {
  return iso ? new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }) : '—';
}

/** Opaque per-opening id for the grant request. */
function newRequestId(): string {
  return crypto.randomUUID();
}

const STATUS_COLOR: Record<string, string> = { active: 'green', expired: 'gray', revoked: 'red' };

/**
 * Organizations with a "Grant plan" action and the manual plan grants of the selected one. A gift
 * is a `subscriptions` row with `provider = NULL`; the list shows its period, state and reason, and
 * lets an admin end it now so a test grant is cleaned up without SQL.
 */
export function OrgPlanGrantsCard({
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
  /** Re-read the org list (its plan column) after a grant or revoke. */
  onChanged: () => void;
  toolbar?: ReactNode;
}) {
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [grantTarget, setGrantTarget] = useState<{ org: AdminOrg; requestId: string } | null>(null);
  const plans = useAdminPlans(!(error instanceof ApiError && error.status === 404));
  const grants = useOrgPlanGrants(selectedId);
  const selected = orgs?.find((o) => o.id === selectedId) ?? null;
  const [revokingId, setRevokingId] = useState<string | null>(null);

  function granted(org: AdminOrg, grant: AdminPlanGrant) {
    notifications.show({
      title: 'Plan granted',
      message: `${grant.planName} to ${org.name}, until ${shortDate(grant.periodEnd)}.`,
      color: 'green',
    });
    setSelectedId(org.id);
    void grants.mutate();
    onChanged();
  }

  async function revoke(org: AdminOrg, grant: AdminPlanGrant) {
    setRevokingId(grant.id);
    try {
      await revokeOrgPlanGrant(org.id, grant.id);
      notifications.show({
        title: 'Grant revoked',
        message: `${grant.planName} ended for ${org.name}. The organization is back on its previous plan.`,
        color: 'green',
      });
      void grants.mutate();
      onChanged();
    } catch (revokeError) {
      notifications.show({ title: 'Not revoked', message: promotionAdminMessage(revokeError), color: 'red' });
    } finally {
      setRevokingId(null);
    }
  }

  const billingOff = error instanceof ApiError && error.status === 404;

  return (
    <Card withBorder data-testid="org-plan-grants-card">
      <Stack gap="sm">
        <div>
          <Text fw={600}>{title}</Text>
          <Text size="sm" c="dimmed">{description}</Text>
        </div>
        {toolbar}
        {billingOff ? (
          <Text size="sm" c="dimmed">Billing is not enabled on this deployment, so no plan can be granted.</Text>
        ) : error ? (
          <Alert color="red" variant="light">{promotionAdminMessage(error)}</Alert>
        ) : isLoading && !orgs ? (
          <Center h={80}><Loader size="sm" /></Center>
        ) : !orgs || orgs.length === 0 ? (
          <Text size="sm" c="dimmed">No organizations found.</Text>
        ) : (
          <Table.ScrollContainer minWidth={640}>
            <Table highlightOnHover verticalSpacing="xs" data-testid="org-plan-grants-table">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Organization</Table.Th>
                  <Table.Th>Owner</Table.Th>
                  <Table.Th>Plan</Table.Th>
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
                    <Table.Td><Text size="sm" fw={500}>{org.name}</Text></Table.Td>
                    <Table.Td><Text size="sm">{org.ownerEmail ?? '—'}</Text></Table.Td>
                    <Table.Td><Text size="sm">{org.planKey ?? '—'}</Text></Table.Td>
                    <Table.Td>
                      <Button
                        size="xs"
                        variant="light"
                        leftSection={<IconGift size={14} />}
                        data-testid="grant-plan-button"
                        onClick={(event) => {
                          event.stopPropagation();
                          setGrantTarget({ org, requestId: newRequestId() });
                        }}
                      >
                        Grant plan
                      </Button>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
        {selected && (
          <Stack gap={4} data-testid="org-plan-grant-list">
            <Text size="sm" fw={600}>Manual plan grants to {selected.name}</Text>
            {grants.error ? (
              <Text size="sm" c="red">{promotionAdminMessage(grants.error)}</Text>
            ) : !grants.data ? (
              <Loader size="xs" />
            ) : grants.data.length === 0 ? (
              <Text size="sm" c="dimmed">No manual plan grants. This organization pays through the billing provider or is on Trial.</Text>
            ) : (
              <Table verticalSpacing={4}>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Plan</Table.Th>
                    <Table.Th>Period</Table.Th>
                    <Table.Th>Status</Table.Th>
                    <Table.Th>Reason</Table.Th>
                    <Table.Th aria-label="Actions" />
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {grants.data.map((g) => (
                    <Table.Tr key={g.id}>
                      <Table.Td><Text size="sm">{g.planName || g.planKey}</Text></Table.Td>
                      <Table.Td>
                        <Text size="sm">{shortDate(g.periodStart)} – {shortDate(g.periodEnd)}</Text>
                      </Table.Td>
                      <Table.Td>
                        <Badge size="xs" variant="light" color={STATUS_COLOR[g.status] ?? 'gray'}>{g.status}</Badge>
                      </Table.Td>
                      <Table.Td><Text size="sm">{g.reason}{g.revokeReason ? ` (revoked: ${g.revokeReason})` : ''}</Text></Table.Td>
                      <Table.Td>
                        {g.status === 'active' && (
                          <Button
                            size="xs"
                            variant="subtle"
                            color="red"
                            leftSection={<IconX size={14} />}
                            loading={revokingId === g.id}
                            data-testid="revoke-plan-button"
                            onClick={(event) => {
                              event.stopPropagation();
                              void revoke(selected, g);
                            }}
                          >
                            Revoke
                          </Button>
                        )}
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </Stack>
        )}
      </Stack>
      {grantTarget && (
        <GrantPlanModal
          key={grantTarget.requestId}
          org={grantTarget.org}
          plans={plans.data ?? []}
          requestId={grantTarget.requestId}
          onClose={() => setGrantTarget(null)}
          onGranted={(grant) => granted(grantTarget.org, grant)}
        />
      )}
    </Card>
  );
}
