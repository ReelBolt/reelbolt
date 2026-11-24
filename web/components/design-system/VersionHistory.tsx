'use client';

import { useMemo, useRef, useState } from 'react';
import { Badge, Button, Center, Checkbox, Group, Loader, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { getVersionDiff, restoreVersion } from '@/lib/api/design-system';
import { useDesignVersions } from '@/lib/hooks/use-design-system';
import { useRevalidateDesignSystem } from '@/lib/hooks/use-revalidate-design-system';
import type { DesignDiff, DesignVersionState, DesignVersionSummary } from '@/lib/types/design-system';
import { EditReasonText, PanelError } from './PanelNotes';
import { VersionDiff } from './VersionDiff';
import {
  changesFromTo,
  COMPARE_CLEAR_LABEL,
  compareSelectionLabel,
  DIFF_ERROR,
  HISTORY_EMPTY,
  HISTORY_TITLE,
  RESTORE_BUTTON_LABEL,
  VERSION_ORIGIN_LABELS,
  VERSION_STATE_LABELS,
  versionLabel,
} from './copy';
import mobile from './mobile.module.css';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

function stateColor(state: DesignVersionState): string {
  if (state === 'Draft') return 'blue';
  if (state === 'Approved') return 'green';
  if (state === 'Superseded') return 'gray';
  return 'red';
}

function restorable(state: DesignVersionState): boolean {
  return state !== 'Draft';
}

/**
 * The design system's versions, newest first, each with its state, origin and creation time. Two
 * versions can be picked for a side-by-side diff (P3-04): the OLDER one is sent as `against`, the
 * NEWER one is the addressed version, so the diff reads oldest → newest. Restore is untouched.
 */
export function VersionHistory({ projectId, canEdit }: { projectId: string; canEdit: boolean }) {
  const tr = useT();
  const { data, error, isLoading, mutate } = useDesignVersions(projectId, 'design');
  const revalidate = useRevalidateDesignSystem(projectId);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [selected, setSelected] = useState<string[]>([]);
  const [diff, setDiff] = useState<DesignDiff | null>(null);
  const [comparing, setComparing] = useState(false);
  const request = useRef(0);

  const versions = useMemo(() => data ?? [], [data]);

  const restore = async (versionId: string) => {
    setBusyId(versionId);
    try {
      await restoreVersion(projectId, versionId);
      await mutate();
      // A restore creates a draft: the banner, summary and tokens must see it too.
      void revalidate();
    } catch (e) {
      notifications.show({
        color: 'red',
        message: describeError(tr, e, tr('brandLibraries.profile.history.restoreFailed')),
      });
    } finally {
      setBusyId(null);
    }
  };

  const compare = async (older: DesignVersionSummary, newer: DesignVersionSummary) => {
    const id = request.current + 1;
    request.current = id;
    setComparing(true);
    setDiff(null);
    try {
      const result = await getVersionDiff(projectId, newer.id, older.id);
      if (request.current === id) setDiff(result);
    } catch (e) {
      if (request.current === id) {
        notifications.show({
          color: 'red',
          message: describeError(tr, e, tr.msg(DIFF_ERROR)),
        });
      }
    } finally {
      if (request.current === id) setComparing(false);
    }
  };

  const toggle = (versionId: string) => {
    const next = selected.includes(versionId)
      ? selected.filter((id) => id !== versionId)
      : selected.length >= 2
        ? [selected[1], versionId]
        : [...selected, versionId];

    setSelected(next);

    if (next.length !== 2) {
      request.current += 1;
      setDiff(null);
      setComparing(false);
      return;
    }

    const first = versions.find((version) => version.id === next[0]);
    const second = versions.find((version) => version.id === next[1]);
    if (!first || !second) return;

    const older = first.versionNumber <= second.versionNumber ? first : second;
    const newer = older === first ? second : first;
    void compare(older, newer);
  };

  const pair = useMemo(() => {
    if (selected.length !== 2) return null;
    const first = versions.find((version) => version.id === selected[0]);
    const second = versions.find((version) => version.id === selected[1]);
    if (!first || !second) return null;
    return first.versionNumber <= second.versionNumber
      ? { older: first, newer: second }
      : { older: second, newer: first };
  }, [selected, versions]);

  const clear = () => {
    request.current += 1;
    setSelected([]);
    setDiff(null);
    setComparing(false);
  };

  if (isLoading && versions.length === 0) {
    return <Center h={120}><Loader size="sm" /></Center>;
  }

  const reasonId = 'history-edit-reason';

  return (
    <Stack gap="sm">
      <Text fw={650}>{tr.msg(HISTORY_TITLE)}</Text>
      {!canEdit && <EditReasonText id={reasonId} />}
      {error && versions.length === 0 ? (
        <PanelError onRetry={() => void mutate()} />
      ) : versions.length === 0 ? (
        <Text size="sm" c="dimmed">{tr.msg(HISTORY_EMPTY)}</Text>
      ) : (
        <>
          <Stack gap="xs">
            {versions.map((version) => (
              <Group key={version.id} justify="space-between" wrap="nowrap" className={mobile.wrapRow}>
                <Group gap="xs" wrap="nowrap" className={mobile.wrapRow}>
                  <Checkbox
                    size="xs"
                    checked={selected.includes(version.id)}
                    onChange={() => toggle(version.id)}
                    aria-label={tr.msg(compareSelectionLabel(version.versionNumber))}
                  />
                  <Badge size="sm" variant="light" color={stateColor(version.state)}>
                    {tr.msg(VERSION_STATE_LABELS[version.state])}
                  </Badge>
                  <Text size="sm" fw={600}>{tr.msg(versionLabel(version.versionNumber))}</Text>
                </Group>
                <Group gap="xs" wrap="nowrap" className={mobile.wrapRow}>
                  <Text size="xs" c="dimmed">{tr.msg(VERSION_ORIGIN_LABELS[version.origin])}</Text>
                  <Text size="xs" c="dimmed">{version.createdAt}</Text>
                  {restorable(version.state) && (
                    <Button
                      size="compact-xs"
                      variant="subtle"
                      disabled={!canEdit || busyId !== null}
                      loading={busyId === version.id}
                      aria-describedby={canEdit ? undefined : reasonId}
                      onClick={() => void restore(version.id)}
                    >
                      {tr.msg(RESTORE_BUTTON_LABEL)}
                    </Button>
                  )}
                </Group>
              </Group>
            ))}
          </Stack>

          {pair && (
            <Stack gap="sm">
              <Group justify="space-between" align="center" wrap="nowrap" className={mobile.wrapRow}>
                <Text size="sm" fw={600}>
                  {tr.msg(changesFromTo(pair.older.versionNumber, pair.newer.versionNumber))}
                </Text>
                <Button size="compact-xs" variant="subtle" onClick={clear}>
                  {tr.msg(COMPARE_CLEAR_LABEL)}
                </Button>
              </Group>
              {comparing ? (
                <Center h={80}><Loader size="sm" /></Center>
              ) : diff ? (
                <VersionDiff diff={diff} />
              ) : null}
            </Stack>
          )}
        </>
      )}
    </Stack>
  );
}
