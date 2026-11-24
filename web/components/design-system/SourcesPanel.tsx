'use client';

import { useState } from 'react';
import { useEffect, useRef } from 'react';
import { Badge, Button, Center, Group, Loader, Stack, Table, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { checkForDrift } from '@/lib/api/design-system';
import { useDesignSources } from '@/lib/hooks/use-design-system';
import { useRevalidateDesignSystem } from '@/lib/hooks/use-revalidate-design-system';
import type { DesignSourceStatus } from '@/lib/types/design-system';
import { EditReasonText, PanelError } from './PanelNotes';
import {
  CHECK_DONE,
  CHECK_ERROR,
  CHECKING_LABEL,
  CHECK_NOW_LABEL,
  SOURCES_COLUMN_IMPORTER,
  SOURCES_COLUMN_PATH,
  SOURCES_COLUMN_SHA,
  SOURCES_COLUMN_STATUS,
  SOURCES_EMPTY,
  SOURCES_TITLE,
  SOURCE_STATUS_LABELS,
} from './copy';
import mobile from './mobile.module.css';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

/** Milliseconds after a queued check at which the profile is looked at again. */
const CHECK_REFRESH_DELAYS_MS = [3000, 10000] as const;

function statusColor(status: DesignSourceStatus): string {
  if (status === 'Current') return 'green';
  if (status === 'Changed') return 'yellow';
  return 'red';
}

/**
 * The design files a profile was read from (path, importer, a short digest and status), plus the
 * "Check now" action (P3-04, SPEC §9): it queues a change check and refreshes the list. Edit
 * authority is the API's 403; the gate only avoids a button that could not work.
 */
export function SourcesPanel({
  projectId,
  canEdit = false,
  refreshDelaysMs = CHECK_REFRESH_DELAYS_MS,
}: {
  projectId: string;
  canEdit?: boolean;
  /** When to look for the proposals a queued check produces; overridable so a test need not wait. */
  refreshDelaysMs?: readonly number[];
}) {
  const tr = useT();
  const { data, error, isLoading, mutate } = useDesignSources(projectId, 'design');
  const revalidate = useRevalidateDesignSystem(projectId);
  const [checking, setChecking] = useState(false);
  const sources = data ?? [];
  const timers = useRef<ReturnType<typeof setTimeout>[]>([]);
  useEffect(() => () => timers.current.forEach(clearTimeout), []);

  const check = async () => {
    setChecking(true);
    try {
      await checkForDrift(projectId);
      notifications.show({ color: 'green', message: tr.msg(CHECK_DONE) });
      await mutate();
      // The check runs in the background: its suggestions (and any source status changes) show up
      // after the job has run, so look again a few times instead of claiming it is already done.
      timers.current.forEach(clearTimeout);
      timers.current = refreshDelaysMs.map((delay) => setTimeout(() => void revalidate(), delay));
    } catch (e) {
      notifications.show({
        color: 'red',
        message: describeError(tr, e, tr.msg(CHECK_ERROR)),
      });
    } finally {
      setChecking(false);
    }
  };

  const reasonId = 'sources-edit-reason';
  const header = (
    <Group justify="space-between" align="center" wrap="nowrap" className={mobile.wrapRow}>
      <Text fw={650}>{tr.msg(SOURCES_TITLE)}</Text>
      <Button
        size="xs"
        variant="light"
        loading={checking}
        disabled={!canEdit || checking}
        aria-describedby={canEdit ? undefined : reasonId}
        onClick={() => void check()}
      >
        {checking ? tr.msg(CHECKING_LABEL) : tr.msg(CHECK_NOW_LABEL)}
      </Button>
    </Group>
  );

  if (isLoading && sources.length === 0) {
    return (
      <Stack gap="sm">
        {header}
        <Center h={120}><Loader size="sm" /></Center>
      </Stack>
    );
  }

  return (
    <Stack gap="sm">
      {header}
      {!canEdit && <EditReasonText id={reasonId} />}
      {error && sources.length === 0 ? (
        <PanelError onRetry={() => void mutate()} />
      ) : sources.length === 0 ? (
        <Text size="sm" c="dimmed">{tr.msg(SOURCES_EMPTY)}</Text>
      ) : (
        <Table.ScrollContainer minWidth={520}>
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{tr.msg(SOURCES_COLUMN_PATH)}</Table.Th>
                <Table.Th>{tr.msg(SOURCES_COLUMN_IMPORTER)}</Table.Th>
                <Table.Th>{tr.msg(SOURCES_COLUMN_SHA)}</Table.Th>
                <Table.Th>{tr.msg(SOURCES_COLUMN_STATUS)}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {sources.map((source) => (
                <Table.Tr key={source.id}>
                  <Table.Td><Text size="sm">{source.path}</Text></Table.Td>
                  <Table.Td><Text size="sm">{source.importer}</Text></Table.Td>
                  <Table.Td><Text size="sm" ff="monospace">{source.contentSha256.slice(0, 8)}</Text></Table.Td>
                  <Table.Td>
                    <Badge size="sm" variant="light" color={statusColor(source.status)}>
                      {tr.msg(SOURCE_STATUS_LABELS[source.status])}
                    </Badge>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}
    </Stack>
  );
}
