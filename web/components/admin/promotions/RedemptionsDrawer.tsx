'use client';

import { Alert, Badge, Center, Drawer, Group, Loader, Stack, Table, Text, Tooltip } from '@mantine/core';
import {
  promotionAdminMessage,
  type Promotion,
  type PromotionNotGrantedReason,
  type PromotionRedemption,
} from '@/lib/api/promotions';
import { usePromotionRedemptions } from '@/lib/hooks/use-promotions';

const KIND_LABELS: Record<string, string> = {
  creditcoderedeemed: 'Code redeemed',
  discountapplied: 'Discount applied',
  bonusgranted: 'Bonus granted',
};

const NOT_GRANTED_REASONS: Record<PromotionNotGrantedReason, string> = {
  superseded: 'A newer checkout took its place and the limit was reached',
  expired: 'Its hold had expired by payment and the limit was reached',
  reservedOverCap: 'It was already over the limit when the checkout started',
  notReserved: 'No hold backed the payment and the limit was reached',
  overshootBoundReached: 'The promotion had used all its room past the limit',
  userCapReached: 'The buyer was at the limit and had already gone over it once',
  orgCapReached: 'The workspace was at the limit and had already gone over it once',
  checkoutMismatch: "The payment didn't match the checkout (other workspace or price)",
};

/** A plain-English reason a redemption was not given, or null when unknown/missing. */
export function notGrantedReasonLabel(reason: string | null | undefined): string | null {
  if (!reason || !Object.hasOwn(NOT_GRANTED_REASONS, reason)) return null;
  return NOT_GRANTED_REASONS[reason as PromotionNotGrantedReason];
}

const OVERSHOOT_HINT = 'Given past the limit because the checkout was reserved before it filled';

function shortDate(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, { dateStyle: 'medium' });
}

function WhatCell({ redemption: r }: { redemption: PromotionRedemption }) {
  const notGiven = r.outcome.toLowerCase() === 'notgranted';
  const reason = notGiven ? notGrantedReasonLabel(r.notGrantedReason) : null;
  return (
    <Stack gap={2}>
      <Group gap={6} wrap="nowrap">
        {notGiven ? (
          <Badge color="red" variant="light" size="sm">Not given</Badge>
        ) : (
          <Text size="sm">{KIND_LABELS[r.kind.toLowerCase()] ?? r.kind}</Text>
        )}
        {r.overshoot ? (
          <Tooltip label={OVERSHOOT_HINT} multiline w={240} withArrow>
            <Badge color="yellow" variant="light" size="sm">Over limit</Badge>
          </Tooltip>
        ) : null}
      </Group>
      {reason ? <Text size="xs" c="dimmed">{reason}</Text> : null}
      {r.discountRemovedAt ? (
        <Group gap={6} wrap="nowrap">
          <Badge color="gray" variant="light" size="sm">Ended on plan change</Badge>
          <Text size="xs" c="dimmed">{shortDate(r.discountRemovedAt)}</Text>
        </Group>
      ) : null}
    </Stack>
  );
}

function dateTime(iso: string): string {
  return new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

/** The newest 500 redemptions of one promotion. */
export function RedemptionsDrawer({ promotion, onClose }: { promotion: Promotion | null; onClose: () => void }) {
  const { data, error, isLoading } = usePromotionRedemptions(promotion?.id ?? null);

  return (
    <Drawer
      opened={promotion !== null}
      onClose={onClose}
      position="right"
      size="xl"
      title={promotion ? `Redemptions: ${promotion.name}` : 'Redemptions'}
    >
      {error ? (
        <Alert color="red" variant="light">{promotionAdminMessage(error)}</Alert>
      ) : isLoading || !data ? (
        <Center h={200}><Loader /></Center>
      ) : data.length === 0 ? (
        <Text size="sm" c="dimmed">Nothing has been redeemed yet.</Text>
      ) : (
        <Table.ScrollContainer minWidth={640}>
          <Table striped verticalSpacing="xs" data-testid="promotion-redemptions-table">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>When</Table.Th>
                <Table.Th>Workspace</Table.Th>
                <Table.Th>What</Table.Th>
                <Table.Th>Credits</Table.Th>
                <Table.Th>Discount (minor units)</Table.Th>
                <Table.Th>Provider transaction</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {data.map((r) => (
                <Table.Tr key={r.id}>
                  <Table.Td><Text size="sm">{dateTime(r.createdAt)}</Text></Table.Td>
                  <Table.Td><Text size="sm">{r.organizationName ?? r.organizationId}</Text></Table.Td>
                  <Table.Td><WhatCell redemption={r} /></Table.Td>
                  <Table.Td><Text size="sm">{r.credits !== null ? r.credits.toLocaleString('en-US') : '—'}</Text></Table.Td>
                  <Table.Td>
                    <Text size="sm">{r.discountTotal ? `${r.discountTotal} ${r.currencyCode ?? ''}`.trim() : '—'}</Text>
                  </Table.Td>
                  <Table.Td><Text size="xs" ff="monospace">{r.providerTransactionId ?? '—'}</Text></Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}
    </Drawer>
  );
}
