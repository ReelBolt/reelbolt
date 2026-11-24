'use client';

import { useState } from 'react';
import { Alert, Badge, Box, Button, Group, Loader, Select, Stack, Text, Tooltip } from '@mantine/core';
import { IconArrowLeft, IconRestore, IconTrash } from '@tabler/icons-react';
import { deleteProjectHistoryVersion, eraseProjectHistoryFile } from '@/lib/api/project-history';
import {
  useProjectHistoryChanges,
  useProjectHistoryVersion,
  useProjectHistoryVersions,
} from '@/lib/hooks/use-project-history';
import type { ProjectHistoryItem, ProjectHistoryRestoreRequest } from '@/lib/types/project-history';
import { useFormat, useT } from '@/i18n';
import { HistoryConfirm } from './HistoryConfirm';
import { ItemDiffView } from './ItemDiffView';
import {
  CHANGE_COLORS, causeMessage, changeMessage, expiryMessage, groupChangesByKind,
  kindMessage, reasonMessage, versionTitle,
} from './labels';
import type { ProjectHistoryPermissions } from './permissions';
import { RestoreModal } from './RestoreModal';

interface Props {
  projectId: string;
  versionId: string;
  /**
   * The tab's permission guess, used only while this detail's own list request is loading; the
   * server's `canEdit`/`canDelete` from that request take over as soon as it arrives.
   */
  permissions: ProjectHistoryPermissions;
  onBack: () => void;
  /** Called after anything that changes the list (a delete, a restore). */
  onChanged: () => void;
}

export function VersionDetail({ projectId, versionId, permissions: fallbackPermissions, onBack, onChanged }: Props) {
  const t = useT('projectHistory');
  const tr = useT();
  const fm = useFormat();
  const { data: version, error, isLoading, mutate } = useProjectHistoryVersion(projectId, versionId);
  const { data: list } = useProjectHistoryVersions(projectId);
  // The list response carries the server's permission flags; the prop is only the loading fallback.
  const permissions: ProjectHistoryPermissions = list
    ? { canEdit: list.canEdit, canDelete: list.canDelete }
    : fallbackPermissions;
  const [to, setTo] = useState('current');
  const changes = useProjectHistoryChanges(projectId, versionId, to);
  const [diffTarget, setDiffTarget] = useState<{ kind: string; key: string; name: string } | null>(null);
  const [restoreRequest, setRestoreRequest] = useState<{ request: ProjectHistoryRestoreRequest; itemName?: string } | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [eraseItem, setEraseItem] = useState<ProjectHistoryItem | null>(null);
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

  if (isLoading) return <Loader size="sm" />;
  if (error || !version) {
    return (
      <Stack gap="sm">
        <Button variant="subtle" leftSection={<IconArrowLeft size={16} />} onClick={onBack} style={{ alignSelf: 'flex-start' }}>{t('backToList')}</Button>
        <Alert color="red">{t('loadFailed')}</Alert>
      </Stack>
    );
  }

  const nameOf = (name: string | null) => name?.trim() || t('detail.itemFallback');
  const title = versionTitle(version, (m) => tr.msg(m));
  const compareOptions = [
    { value: 'current', label: t('detail.compareCurrent') },
    ...(list?.versions ?? [])
      .filter((v) => v.id !== versionId)
      .map((v) => ({
        value: v.id,
        label: t('detail.compareVersion', { number: v.number, label: versionTitle(v, (m) => tr.msg(m)) }),
      })),
  ];

  const runDelete = async (action: () => Promise<void>, done: () => void) => {
    setBusy(true);
    setActionError(null);
    try {
      await action();
      done();
    } catch {
      setActionError(t('delete.failed'));
    } finally {
      setBusy(false);
    }
  };

  const restoreReason = (item: ProjectHistoryItem): string | null => {
    if (!permissions.canEdit) return t('detail.noEditPermission');
    if (!item.restorable) return tr.msg(reasonMessage(item.notRestorableReason));
    return null;
  };

  const grouped = groupChangesByKind(changes.data?.changes ?? []);

  return (
    <Stack gap="md">
      <Button variant="subtle" leftSection={<IconArrowLeft size={16} />} onClick={onBack} style={{ alignSelf: 'flex-start' }}>
        {t('backToList')}
      </Button>
      <Stack gap={2}>
        <Text fw={600} size="lg">{title}</Text>
        <Text size="sm" c="dimmed">
          {tr.msg(causeMessage(version.cause))} · {t('meta', { author: version.createdByName ?? t('automaticAuthor'), when: fm.date(version.createdAt, 'dateTime') })} · {tr.msg(expiryMessage(version.expiresAt))} · {t('itemCount', { count: version.itemCount })}
        </Text>
      </Stack>

      <Group gap="sm">
        {permissions.canDelete && (
          <Button leftSection={<IconRestore size={16} />} onClick={() => setRestoreRequest({ request: { scope: 'project' } })}>
            {t('detail.restoreProject')}
          </Button>
        )}
        {permissions.canDelete && (
          <Button variant="default" color="red" leftSection={<IconTrash size={16} />} onClick={() => setConfirmDelete(true)}>
            {t('detail.deleteVersion')}
          </Button>
        )}
      </Group>
      {!permissions.canDelete && <Text size="sm" c="dimmed">{t('detail.noDeletePermission')}</Text>}
      {actionError && <Alert color="red" role="alert">{actionError}</Alert>}

      <Select
        label={t('detail.compareLabel')}
        data={compareOptions}
        value={to}
        onChange={(v) => { setTo(v ?? 'current'); setDiffTarget(null); }}
        allowDeselect={false}
        comboboxProps={{ withinPortal: true }}
        styles={{ input: { minHeight: 44 } }}
      />

      <Stack gap="xs">
        <Text fw={600}>{t('detail.changesTitle')}</Text>
        {changes.isLoading && <Loader size="sm" />}
        {changes.error && <Alert color="red">{t('detail.changesFailed')}</Alert>}
        {changes.data && changes.data.changes.length === 0 && <Text size="sm" c="dimmed">{t('detail.noChanges')}</Text>}
        {changes.data && changes.data.changes.length > 0 && (
          <Text size="sm" c="dimmed">
            {t('detail.changesSummary', { count: changes.data.changes.length, unchanged: changes.data.unchangedCount })}
          </Text>
        )}
        {changes.data?.truncated && <Text size="sm" c="dimmed">{t('detail.changesTruncated')}</Text>}
        {grouped.map((group) => (
          <Box key={group.kind}>
            <Text size="sm" fw={600} mb={4}>{tr.msg(kindMessage(group.kind))}</Text>
            <Stack gap={6}>
              {group.changes.map((c) => {
                const label = nameOf(c.name);
                return (
                  <Group key={`${c.kind}:${c.key}`} gap="xs" wrap="nowrap" align="center">
                    <Badge size="sm" variant="light" color={CHANGE_COLORS[c.change] ?? 'gray'}>{tr.msg(changeMessage(c.change))}</Badge>
                    <Text size="sm" style={{ flex: 1, minWidth: 0, overflowWrap: 'anywhere' }}>
                      {label}
                      {c.previousName ? <Text span size="xs" c="dimmed"> ({t('detail.previousName', { name: c.previousName })})</Text> : null}
                    </Text>
                    <Button
                      size="compact-sm"
                      variant="subtle"
                      aria-label={t('detail.viewDiffAria', { name: label })}
                      onClick={() => setDiffTarget({ kind: c.kind, key: c.key, name: label })}
                      style={{ minHeight: 32 }}
                    >
                      {t('detail.viewDiff')}
                    </Button>
                  </Group>
                );
              })}
            </Stack>
          </Box>
        ))}
        {diffTarget && (
          <Box p="sm" style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 8, overflow: 'hidden' }}>
            <ItemDiffView
              projectId={projectId}
              versionId={versionId}
              kind={diffTarget.kind}
              itemKey={diffTarget.key}
              to={to}
              name={diffTarget.name}
            />
          </Box>
        )}
      </Stack>

      <Stack gap="xs">
        <Text fw={600}>{t('detail.itemsTitle')}</Text>
        {version.items.length === 0 && <Text size="sm" c="dimmed">{t('detail.itemsEmpty')}</Text>}
        {version.items.map((item) => {
          const label = nameOf(item.name);
          const blocked = restoreReason(item);
          return (
            <Group key={`${item.kind}:${item.key}`} gap="xs" justify="space-between" align="flex-start" wrap="wrap">
              <Stack gap={0} style={{ flex: 1, minWidth: 160 }}>
                <Text size="sm" style={{ overflowWrap: 'anywhere' }}>{label}</Text>
                <Text size="xs" c="dimmed">
                  {tr.msg(kindMessage(item.kind))} · {item.restorable ? t('detail.restorable') : t('detail.notRestorable')}
                  {!item.restorable && item.notRestorableReason ? ` · ${tr.msg(reasonMessage(item.notRestorableReason))}` : ''}
                </Text>
              </Stack>
              <Group gap="xs">
                <Tooltip label={blocked ?? ''} disabled={!blocked} multiline maw={260}>
                  <span>
                    <Button
                      size="compact-sm"
                      variant="light"
                      disabled={blocked !== null}
                      aria-label={t('detail.restoreItemAria', { name: label })}
                      onClick={() => setRestoreRequest({ request: { scope: 'item', items: [{ kind: item.kind, key: item.key }] }, itemName: label })}
                      style={{ minHeight: 32 }}
                    >
                      {t('detail.restoreItem')}
                    </Button>
                  </span>
                </Tooltip>
                {item.kind === 'File' && permissions.canDelete && (
                  <Tooltip label={t('detail.deleteForeverHint')} multiline maw={260}>
                    <Button size="compact-sm" variant="subtle" color="red" onClick={() => setEraseItem(item)} style={{ minHeight: 32 }}>
                      {t('detail.deleteForever')}
                    </Button>
                  </Tooltip>
                )}
              </Group>
            </Group>
          );
        })}
      </Stack>

      {restoreRequest && (
        <RestoreModal
          opened
          onClose={() => { setRestoreRequest(null); void mutate(); void changes.mutate(); }}
          projectId={projectId}
          versionId={versionId}
          request={restoreRequest.request}
          itemName={restoreRequest.itemName}
          onRestored={onChanged}
        />
      )}
      <HistoryConfirm
        opened={confirmDelete}
        onClose={() => setConfirmDelete(false)}
        onConfirm={() => void runDelete(() => deleteProjectHistoryVersion(projectId, versionId), () => { setConfirmDelete(false); onChanged(); onBack(); })}
        title={t('delete.versionTitle')}
        message={t('delete.versionMessage', { number: version.number, label: title })}
        confirmLabel={t('delete.versionConfirm')}
        loading={busy}
        error={actionError}
      />
      <HistoryConfirm
        opened={eraseItem !== null}
        onClose={() => setEraseItem(null)}
        onConfirm={() => eraseItem && void runDelete(() => eraseProjectHistoryFile(projectId, eraseItem.key), () => { setEraseItem(null); void mutate(); onChanged(); })}
        title={t('delete.fileTitle')}
        message={t('delete.fileMessage', { name: eraseItem ? nameOf(eraseItem.name) : '' })}
        confirmLabel={t('delete.fileConfirm')}
        loading={busy}
        error={actionError}
      />
    </Stack>
  );
}
