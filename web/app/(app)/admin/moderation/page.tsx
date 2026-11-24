'use client';

import { describeError } from '@/lib/api/describe-error';
import { useEffect, useState } from 'react';
import {
  Alert,
  Badge,
  Button,
  Card,
  Center,
  Group,
  Loader,
  SimpleGrid,
  Stack,
  Table,
  Text,
} from '@mantine/core';
import { IconAlertTriangle, IconInfoCircle, IconShieldCheck } from '@tabler/icons-react';
import { notifications } from '@mantine/notifications';
import { PageHeader } from '@/components/shared/PageHeader';
import { EmptyState } from '@/components/shared/EmptyState';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { useT, type T } from '@/i18n';
import { useAuth } from '@/lib/hooks/use-auth';
import {
  acknowledgeModerationItemAndRevalidate,
  useModerationQueue,
  useModerationSettings,
} from '@/lib/hooks/use-moderation-queue';
import type { ModerationReviewQueueItem } from '@/lib/types/moderation';

const BREADCRUMBS = [{ label: 'Admin', href: '/admin' }, { label: 'Content moderation' }];

const REVIEW_ONLY_BANNER =
  'Review only — acknowledging records that you have seen this item. It changes no user content.';

/** Scores are 0..1; a missing category/score is shown as a dash, never as 0. */
const formatScore = (score: number | null): string =>
  score === null || !Number.isFinite(score) ? '—' : `${(score * 100).toFixed(1)}%`;

const formatThreshold = (value: number): string => (Number.isFinite(value) ? value.toFixed(2) : '—');

const formatCreated = (iso: string): string => {
  const date = new Date(iso);
  return Number.isNaN(date.getTime())
    ? iso
    : date.toLocaleString('en-US', { dateStyle: 'medium', timeStyle: 'short' });
};

const errorMessage = (tAll: T, error: unknown, fallback: string): string => describeError(tAll, error, fallback);

/**
 * Platform-admin review queue for mid-band content-moderation decisions.
 *
 * REVIEW-ONLY by design: the single action acknowledges that an operator has seen a row,
 * which changes no user content. There is deliberately no block, dismiss or delete action
 * here, and the banner says so where the table is read.
 */
export default function ContentModerationPage() {
  const { isPlatformAdmin } = useAuth();
  const tAll = useT();
  const queue = useModerationQueue(isPlatformAdmin);
  const settings = useModerationSettings(isPlatformAdmin);
  const [target, setTarget] = useState<ModerationReviewQueueItem | null>(null);
  const [acknowledging, setAcknowledging] = useState(false);

  useEffect(() => {
    if (!queue.error) return;
    notifications.show({
      title: 'Error',
      message: errorMessage(tAll, queue.error, 'Failed to load the moderation review queue'),
      color: 'red',
    });
  }, [queue.error]);

  const handleAcknowledge = async () => {
    if (!target) return;
    setAcknowledging(true);
    try {
      await acknowledgeModerationItemAndRevalidate(target.id);
      notifications.show({
        title: 'Acknowledged',
        message: 'You have seen this item. No user content changed.',
        color: 'green',
      });
      setTarget(null);
    } catch (error) {
      notifications.show({
        title: 'Error',
        message: errorMessage(tAll, error, 'Failed to acknowledge the moderation item'),
        color: 'red',
      });
    } finally {
      setAcknowledging(false);
    }
  };

  if (!isPlatformAdmin) {
    return (
      <>
        <PageHeader title="Content moderation" breadcrumbs={BREADCRUMBS} />
        <Alert color="red" variant="light" title="Platform administrators only">
          This queue shows moderation decisions across the platform. It is restricted to platform
          administrators, and the API answers 403 for anyone else.
        </Alert>
      </>
    );
  }

  let body: React.ReactNode;
  if (queue.isLoading) {
    body = (
      <Center h={240}>
        <Loader aria-label="Loading the review queue" />
      </Center>
    );
  } else if (queue.error && !queue.data) {
    body = (
      <Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} title="Could not load the review queue">
        The server answered: {errorMessage(tAll, queue.error, 'the request did not reach the server')}.
      </Alert>
    );
  } else if (!queue.data || queue.data.items.length === 0) {
    body = (
      <EmptyState
        title="Nothing to review"
        description="No pending mid-band moderation decisions. An item appears here when its highest category score sits between the review and block thresholds."
      />
    );
  } else {
    body = (
      <Stack gap="xs">
        <Table.ScrollContainer minWidth={960}>
          <Table withTableBorder highlightOnHover verticalSpacing="sm">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Created</Table.Th>
                <Table.Th>Surface</Table.Th>
                <Table.Th>Project</Table.Th>
                <Table.Th>Kind</Table.Th>
                <Table.Th>Category</Table.Th>
                <Table.Th>Score</Table.Th>
                <Table.Th>Action</Table.Th>
                <Table.Th>State</Table.Th>
                <Table.Th>Queue</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {queue.data.items.map((item) => (
                <Table.Tr key={item.id}>
                  <Table.Td>
                    <Text component="time" dateTime={item.createdAt} size="sm">
                      {formatCreated(item.createdAt)}
                    </Text>
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm">{item.surface}</Text>
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm">{item.projectName ?? item.projectId ?? '—'}</Text>
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm">{item.contentKind}</Text>
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm">{item.topCategory ?? '—'}</Text>
                    {item.reasonCode ? (
                      <Text size="xs" c="dimmed">
                        {item.reasonCode}
                      </Text>
                    ) : null}
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm">{formatScore(item.topScore)}</Text>
                  </Table.Td>
                  <Table.Td>
                    <Badge
                      variant="light"
                      color={item.action === 'Block' ? 'red' : item.action === 'Review' ? 'yellow' : 'gray'}
                    >
                      {item.action}
                    </Badge>
                  </Table.Td>
                  <Table.Td>
                    <Badge variant="light" color="yellow">
                      {item.reviewState}
                    </Badge>
                  </Table.Td>
                  <Table.Td>
                    <Button
                      size="xs"
                      variant="default"
                      aria-label={`Acknowledge ${item.surface} item`}
                      onClick={() => setTarget(item)}
                    >
                      Acknowledge
                    </Button>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
        <Text size="xs" c="dimmed">
          Showing {queue.data.items.length} of {queue.data.total} pending decisions.
        </Text>
      </Stack>
    );
  }

  const effective = settings.data;

  return (
    <>
      <PageHeader title="Content moderation" breadcrumbs={BREADCRUMBS}>
        {queue.isValidating && queue.data ? <Loader size="xs" aria-label="Refreshing" /> : null}
      </PageHeader>

      <Alert color="blue" variant="light" icon={<IconInfoCircle size={16} />} title="Review only" mb="md">
        {REVIEW_ONLY_BANNER}
      </Alert>

      <Card withBorder radius="md" padding="md" mb="md">
        <Group justify="space-between" mb="sm" wrap="wrap">
          <Group gap="sm">
            <IconShieldCheck size={20} />
            <Text fw={600}>Moderation settings</Text>
          </Group>
          {settings.isLoading ? <Loader size="xs" aria-label="Loading moderation settings" /> : null}
          {settings.error ? (
            <Badge color="red" variant="light">
              Unavailable
            </Badge>
          ) : null}
        </Group>
        {effective ? (
          <SimpleGrid cols={{ base: 2, md: 4 }} spacing="md">
            <Stack gap={2}>
              <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
                Enabled
              </Text>
              <Badge variant="light" color={effective.enabled ? 'green' : 'gray'} w="fit-content">
                {effective.enabled ? 'Enabled' : 'Disabled'}
              </Badge>
            </Stack>
            <Stack gap={2}>
              <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
                Model
              </Text>
              <Text size="sm">{effective.model ?? 'Not configured'}</Text>
            </Stack>
            <Stack gap={2}>
              <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
                Block at or above
              </Text>
              <Text size="sm">{formatThreshold(effective.blockThreshold)}</Text>
            </Stack>
            <Stack gap={2}>
              <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
                Review at or above
              </Text>
              <Text size="sm">{formatThreshold(effective.reviewThreshold)}</Text>
            </Stack>
          </SimpleGrid>
        ) : !settings.isLoading && !settings.error ? (
          <Text size="sm" c="dimmed">
            No moderation settings are available.
          </Text>
        ) : null}
      </Card>

      {body}

      <ConfirmModal
        opened={target !== null}
        onClose={() => {
          if (!acknowledging) setTarget(null);
        }}
        onConfirm={() => {
          void handleAcknowledge();
        }}
        title="Acknowledge this item?"
        message="Acknowledging records that you have seen it. It changes no user content."
        confirmLabel="Acknowledge"
        loading={acknowledging}
      />
    </>
  );
}
