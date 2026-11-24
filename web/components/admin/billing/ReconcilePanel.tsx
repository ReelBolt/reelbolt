'use client';

import { readableServerMessage } from '@/lib/api/describe-error';
import { useId, useState } from 'react';
import useSWR from 'swr';
import { Alert, Button, Center, Group, Loader, Stack, Table, Text, TextInput, Tooltip, VisuallyHidden } from '@mantine/core';
import { IconAlertTriangle, IconRefresh } from '@tabler/icons-react';
import { ApiError } from '@/lib/api/client';
import { BILLING_ADMIN_RECONCILE_PATH, getBillingAdminReconcile } from '@/lib/api/billing-admin';
import type { BillingReconcileItem } from '@/lib/types/billing-admin';
import { useMerchantLabel } from '@/lib/hooks/use-billing';
import { formatRelative } from '@/lib/utils/runners';
import { EmptyState } from '@/components/shared/EmptyState';
import { BillingSectionHeading } from './BillingSectionHeading';
import { ResyncConfirmModal, type ResyncTarget } from './ResyncConfirmModal';
import { formatExactTime, formatStuckAfter, isGuid, outcomeLabel, reasonLabel } from './reconcile-copy';

/** A relative time that carries the exact instant in a tooltip (and the `dateTime` attribute). */
function RelativeTime({ iso, now }: { iso: string; now: number }) {
  return (
    <Tooltip label={formatExactTime(iso)} withArrow>
      <Text component="time" dateTime={iso} size="sm" tabIndex={0}>
        {formatRelative(iso, now)}
      </Text>
    </Tooltip>
  );
}

function LastCheck({ item, now }: { item: BillingReconcileItem; now: number }) {
  const provider = useMerchantLabel();
  if (!item.lastCheckedAt) {
    return (
      <Text size="sm" c="dimmed">
        Not checked yet
      </Text>
    );
  }
  return (
    <Stack gap={2}>
      <Text size="sm">{outcomeLabel(item.lastOutcome, provider)}</Text>
      <Group gap={4} wrap="nowrap">
        <Text size="xs" c="dimmed">
          Checked
        </Text>
        <Tooltip label={formatExactTime(item.lastCheckedAt)} withArrow>
          <Text component="time" dateTime={item.lastCheckedAt} size="xs" c="dimmed" tabIndex={0}>
            {formatRelative(item.lastCheckedAt, now)}
          </Text>
        </Tooltip>
      </Group>
      {item.lastError ? (
        <Text size="xs" c="red" lineClamp={2} title={item.lastError}>
          {item.lastError}
        </Text>
      ) : null}
    </Stack>
  );
}

/**
 * The "Resync a workspace by ID" input, for a workspace that is not on the list (a support ticket,
 * a log line). The button stays disabled until the value is a GUID, and the hint under the input
 * says why rather than leaving a dead button.
 */
function ResyncByIdForm({ onResync }: { onResync: (organizationId: string) => void }) {
  const provider = useMerchantLabel();
  const [value, setValue] = useState('');
  const hintId = useId();
  const trimmed = value.trim();
  const valid = isGuid(trimmed);
  const showError = trimmed.length > 0 && !valid;

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        if (valid) onResync(trimmed.toLowerCase());
      }}
    >
      <Stack gap={4}>
        <Group align="flex-end" gap="sm" wrap="wrap">
          <TextInput
            label="Resync a workspace by ID"
            placeholder="00000000-0000-0000-0000-000000000000"
            value={value}
            onChange={(event) => setValue(event.currentTarget.value)}
            error={showError}
            aria-describedby={hintId}
            ff="monospace"
            w={360}
            maw="100%"
            spellCheck={false}
            autoComplete="off"
          />
          <Button type="submit" variant="default" leftSection={<IconRefresh size={16} />} disabled={!valid}>
            Resync from {provider}
          </Button>
        </Group>
        <Text id={hintId} size="xs" c={showError ? 'red' : 'dimmed'}>
          {showError
            ? 'That is not a workspace ID. Paste the full ID: 32 hex characters in groups of 8-4-4-4-12.'
            : 'For a workspace that is not listed below, for example one named in a support ticket.'}
        </Text>
      </Stack>
    </form>
  );
}

function WaitingTable({
  items,
  now,
  onResync,
}: {
  items: BillingReconcileItem[];
  /** When the list was last read, so "5 minutes ago" is relative to the data, not the render. */
  now: number;
  onResync: (target: ResyncTarget) => void;
}) {
  const provider = useMerchantLabel();
  return (
    <Table.ScrollContainer minWidth={960}>
      <Table withTableBorder highlightOnHover verticalSpacing="sm">
        <Table.Thead>
          <Table.Tr>
            <Table.Th>Workspace</Table.Th>
            <Table.Th>Plan</Table.Th>
            <Table.Th>Reason</Table.Th>
            <Table.Th>Waiting since</Table.Th>
            <Table.Th>Last check</Table.Th>
            <Table.Th>
              <VisuallyHidden>Action</VisuallyHidden>
            </Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {items.map((item) => (
            <Table.Tr key={item.organizationId}>
              <Table.Td>
                <Text size="sm" fw={500}>
                  {item.organizationName}
                </Text>
                <Text size="xs" c="dimmed" ff="monospace">
                  {item.organizationId}
                </Text>
              </Table.Td>
              <Table.Td>
                <Text size="sm">{item.planKey ?? 'No plan'}</Text>
                {item.status ? (
                  <Text size="xs" c="dimmed">
                    {item.status}
                  </Text>
                ) : null}
              </Table.Td>
              <Table.Td>
                <Text size="sm" c={item.reason === 'reconcile_failing' ? 'red' : undefined}>
                  {reasonLabel(item.reason, provider)}
                </Text>
              </Table.Td>
              <Table.Td>
                <RelativeTime iso={item.waitingSince} now={now} />
              </Table.Td>
              <Table.Td>
                <LastCheck item={item} now={now} />
              </Table.Td>
              <Table.Td>
                <Button
                  size="xs"
                  variant="default"
                  leftSection={<IconRefresh size={14} />}
                  aria-label={`Resync from ${provider}: ${item.organizationName}`}
                  onClick={() =>
                    onResync({ organizationId: item.organizationId, organizationName: item.organizationName })
                  }
                >
                  Resync from {provider}
                </Button>
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

/**
 * Platform-admin attention list: workspaces whose subscription change, renewal, cancellation or
 * checkout the provider has not confirmed in time, with a "Resync" per row and by ID. The list
 * is read from our database only; nothing here calls the provider until a resync is confirmed.
 *
 * Mount it only for platform admins: the API answers 403 to anyone else.
 */
export function ReconcilePanel() {
  const provider = useMerchantLabel();
  const [readAt, setReadAt] = useState(() => Date.now());
  const list = useSWR(BILLING_ADMIN_RECONCILE_PATH, () => getBillingAdminReconcile(), {
    onSuccess: () => setReadAt(Date.now()),
  });
  const [target, setTarget] = useState<ResyncTarget | null>(null);

  const openById = (organizationId: string) => {
    const listed = list.data?.items.find((item) => item.organizationId.toLowerCase() === organizationId);
    setTarget({ organizationId, organizationName: listed?.organizationName ?? null });
  };

  const stuckAfter = list.data ? formatStuckAfter(list.data.stuckAfterSeconds) : null;
  const description = `Workspaces whose plan change, renewal, cancellation or checkout has not been confirmed by a ${provider} webhook${
    stuckAfter ? ` within ${stuckAfter}` : ' in time'
  }. A background sweep re-reads them from ${provider} on its own; Resync from ${provider} does it now.`;

  let body: React.ReactNode;
  if (list.data) {
    body =
      list.data.items.length === 0 ? (
        <EmptyState
          title={`Nothing is waiting on ${provider}`}
          description={`Every subscription change has been confirmed. A workspace appears here when one goes unconfirmed for more than ${stuckAfter}.`}
        />
      ) : (
        <WaitingTable items={list.data.items} now={readAt} onResync={setTarget} />
      );
  } else if (list.error) {
    const disabled = list.error instanceof ApiError && list.error.status === 404;
    body = disabled ? (
      <Alert color="gray" variant="light" title="Billing is turned off on this deployment">
        With billing disabled there are no {provider} subscriptions to wait on, and resync is unavailable.
      </Alert>
    ) : (
      <Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} title="Could not load the list">
        <Stack gap="xs" align="flex-start">
          <Text size="sm">
            {readableServerMessage(list.error)
              ? `The server answered: ${readableServerMessage(list.error)}.`
              : 'The request did not reach the server.'}{' '}
            Resync by ID above still works.
          </Text>
          <Button size="xs" variant="default" onClick={() => list.mutate()}>
            Try again
          </Button>
        </Stack>
      </Alert>
    );
  } else {
    body = (
      <Center h={120}>
        <Loader />
      </Center>
    );
  }

  return (
    <Stack gap="md">
      <BillingSectionHeading
        title={`Subscriptions waiting on ${provider}`}
        description={description}
        action={list.isValidating && list.data ? <Loader size="xs" aria-label="Refreshing" /> : undefined}
      />
      <ResyncByIdForm onResync={openById} />
      {body}
      <ResyncConfirmModal
        key={target?.organizationId ?? 'none'}
        target={target}
        onClose={() => setTarget(null)}
        onSettled={() => {
          void list.mutate();
        }}
      />
    </Stack>
  );
}
