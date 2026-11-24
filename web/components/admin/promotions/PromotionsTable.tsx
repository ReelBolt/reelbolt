'use client';

import { ActionIcon, Badge, Menu, Table, Text } from '@mantine/core';
import {
  IconArchive,
  IconDots,
  IconEdit,
  IconList,
  IconPlayerPause,
  IconPlayerPlay,
} from '@tabler/icons-react';
import { sameEnum, type Promotion, type PromotionKind, type PromotionStatus } from '@/lib/api/promotions';
import { PROMOTION_KIND_LABELS } from './CreatePromotionModal';

function kindLabel(kind: string): string {
  const match = (Object.keys(PROMOTION_KIND_LABELS) as PromotionKind[]).find((k) => sameEnum(kind, k));
  return match ? PROMOTION_KIND_LABELS[match] : kind;
}

function shortDate(iso: string | null): string {
  return iso ? new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }) : '';
}

/** The promotion's value in admin shorthand. The discount amount is the configured value, as stored. */
export function promotionValueLabel(p: Promotion): string {
  if (sameEnum(p.kind, 'creditCode')) {
    return `${(p.credits ?? 0).toLocaleString('en-US')} credits, expire after ${p.creditExpiryDays ?? '—'} days`;
  }
  if (sameEnum(p.kind, 'packBonus')) return `+${p.bonusPercent ?? '—'}% credits`;
  const amount = sameEnum(p.discountType, 'flat')
    ? `${p.discountAmount ?? '—'} ${p.currencyCode ?? 'USD'} cents off`
    : `${p.discountAmount ?? '—'}% off`;
  if (!sameEnum(p.kind, 'planDiscount')) return amount;
  if (sameEnum(p.durationKind, 'months')) return `${amount}, ${p.durationMonths ?? '—'} months`;
  if (sameEnum(p.durationKind, 'forever')) return `${amount}, forever`;
  return `${amount}, first payment`;
}

function targetsLabel(p: Promotion): string {
  if (sameEnum(p.kind, 'creditCode')) return '—';
  if (sameEnum(p.kind, 'planDiscount')) {
    return p.targetPlanKeys && p.targetPlanKeys.length > 0 ? p.targetPlanKeys.join(', ') : 'All plans';
  }
  return p.targetPackCredits && p.targetPackCredits.length > 0
    ? p.targetPackCredits.map((n) => n.toLocaleString('en-US')).join(', ')
    : 'All packs';
}

function windowLabel(p: Promotion): string {
  if (!p.startsAt && !p.endsAt) return 'Always';
  if (!p.endsAt) return `From ${shortDate(p.startsAt)}`;
  if (!p.startsAt) return `Until ${shortDate(p.endsAt)}`;
  return `${shortDate(p.startsAt)} – ${shortDate(p.endsAt)}`;
}

const STATUS_COLORS: Record<string, string> = { active: 'green', paused: 'yellow', archived: 'gray' };

export function PromotionsTable({
  promotions,
  busyId,
  onStatusChange,
  onEdit,
  onShowRedemptions,
}: {
  promotions: Promotion[];
  /** The promotion with an action in flight, if any. */
  busyId: string | null;
  onStatusChange: (promotion: Promotion, status: PromotionStatus) => void;
  onEdit: (promotion: Promotion) => void;
  onShowRedemptions: (promotion: Promotion) => void;
}) {
  return (
    <Table.ScrollContainer minWidth={900}>
      <Table striped highlightOnHover verticalSpacing="sm" data-testid="admin-promotions-table">
        <Table.Thead>
          <Table.Tr>
            <Table.Th>Name</Table.Th>
            <Table.Th>Kind</Table.Th>
            <Table.Th>Code</Table.Th>
            <Table.Th>Value</Table.Th>
            <Table.Th>Targets</Table.Th>
            <Table.Th>Window</Table.Th>
            <Table.Th>Redemptions</Table.Th>
            <Table.Th>Status</Table.Th>
            <Table.Th aria-label="Actions" />
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {promotions.map((p) => {
            const status = p.status.toLowerCase();
            return (
              <Table.Tr key={p.id} data-testid={`promotion-row-${p.id}`}>
                <Table.Td>
                  <Text size="sm" fw={500}>{p.name}</Text>
                  {p.publicHeadline && <Text size="xs" c="dimmed">{p.publicHeadline}</Text>}
                </Table.Td>
                <Table.Td><Text size="sm">{kindLabel(p.kind)}</Text></Table.Td>
                <Table.Td>
                  {p.code ? (
                    <Text size="sm" ff="monospace">{p.code}</Text>
                  ) : (
                    <Badge variant="outline" size="sm">Automatic</Badge>
                  )}
                </Table.Td>
                <Table.Td><Text size="sm">{promotionValueLabel(p)}</Text></Table.Td>
                <Table.Td><Text size="sm">{targetsLabel(p)}</Text></Table.Td>
                <Table.Td><Text size="sm">{windowLabel(p)}</Text></Table.Td>
                <Table.Td>
                  <Text size="sm" style={{ fontVariantNumeric: 'tabular-nums' }}>
                    {p.redemptions.toLocaleString('en-US')}
                    {p.maxRedemptions !== null ? ` / ${p.maxRedemptions.toLocaleString('en-US')}` : ''}
                  </Text>
                </Table.Td>
                <Table.Td>
                  <Badge color={STATUS_COLORS[status] ?? 'gray'} variant="light">{status}</Badge>
                </Table.Td>
                <Table.Td>
                  <Menu position="bottom-end" withinPortal>
                    <Menu.Target>
                      <ActionIcon variant="subtle" loading={busyId === p.id} aria-label={`Actions for ${p.name}`}>
                        <IconDots size={16} />
                      </ActionIcon>
                    </Menu.Target>
                    <Menu.Dropdown>
                      <Menu.Item leftSection={<IconList size={14} />} onClick={() => onShowRedemptions(p)}>
                        Redemptions
                      </Menu.Item>
                      <Menu.Item leftSection={<IconEdit size={14} />} onClick={() => onEdit(p)}>
                        Edit
                      </Menu.Item>
                      {status === 'active' ? (
                        <Menu.Item leftSection={<IconPlayerPause size={14} />} onClick={() => onStatusChange(p, 'paused')}>
                          Pause
                        </Menu.Item>
                      ) : (
                        <Menu.Item leftSection={<IconPlayerPlay size={14} />} onClick={() => onStatusChange(p, 'active')}>
                          Activate
                        </Menu.Item>
                      )}
                      {status !== 'archived' && (
                        <Menu.Item
                          color="red"
                          leftSection={<IconArchive size={14} />}
                          onClick={() => onStatusChange(p, 'archived')}
                        >
                          Archive
                        </Menu.Item>
                      )}
                    </Menu.Dropdown>
                  </Menu>
                </Table.Td>
              </Table.Tr>
            );
          })}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}
