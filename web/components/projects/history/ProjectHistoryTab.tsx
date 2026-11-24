'use client';

import { useState } from 'react';
import { Alert, Button, Group, Loader, Modal, Stack, Text, TextInput, UnstyledButton } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconHistory } from '@tabler/icons-react';
import { createProjectHistoryVersion } from '@/lib/api/project-history';
import { HISTORY_PAGE_SIZE, useProjectHistoryVersionPages } from '@/lib/hooks/use-project-history';
import { formatBytes } from '@/lib/utils/bytes';
import { useAuth } from '@/lib/hooks/use-auth';
import { useFormat, useT } from '@/i18n';
import { causeMessage, errorReasonCode, expiryMessage, saveErrorMessage, versionTitle } from './labels';
import { projectHistoryPermissions, type ProjectHistoryPermissions } from './permissions';
import { VersionDetail } from './VersionDetail';

export const VERSION_NAME_MAX = 80;

export function ProjectHistoryTab({ projectId }: { projectId: string }) {
  const t = useT('projectHistory');
  const tr = useT();
  const fm = useFormat();
  const { user, isPlatformAdmin } = useAuth();
  const { data: pages, error, isLoading, isValidating, mutate, size, setSize } = useProjectHistoryVersionPages(projectId);
  const data = pages?.[0];
  const versions = pages?.flatMap((page) => page.versions) ?? [];
  // A full last page may have more; a short one is the end of the list.
  const hasOlder = (pages?.[pages.length - 1]?.versions.length ?? 0) === HISTORY_PAGE_SIZE;
  // The server's flags are the authority; the org-role guess fills the gap only until they arrive.
  const permissions: ProjectHistoryPermissions = data
    ? { canEdit: data.canEdit, canDelete: data.canDelete }
    : projectHistoryPermissions(user?.orgRole, isPlatformAdmin);
  const [openId, setOpenId] = useState<string | null>(null);
  const [saveOpen, setSaveOpen] = useState(false);
  const [name, setName] = useState('');
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  const trimmed = name.trim();
  const nameProblem = trimmed.length === 0 ? t('nameRequired') : trimmed.length > VERSION_NAME_MAX ? t('nameTooLong', { max: VERSION_NAME_MAX }) : null;

  const save = async () => {
    if (nameProblem) return;
    setSaving(true);
    setSaveError(null);
    try {
      const created = await createProjectHistoryVersion(projectId, trimmed);
      notifications.show({ title: t('savedTitle'), message: t('savedMessage', { name: created.name ?? trimmed }), color: 'green' });
      setSaveOpen(false);
      setName('');
      await mutate();
    } catch (e) {
      const code = errorReasonCode(e);
      setSaveError(tr.msg(saveErrorMessage(code)));
    } finally {
      setSaving(false);
    }
  };

  if (openId) {
    return (
      <VersionDetail
        projectId={projectId}
        versionId={openId}
        permissions={permissions}
        onBack={() => setOpenId(null)}
        onChanged={() => void mutate()}
      />
    );
  }

  return (
    <Stack gap="md">
      <Group justify="space-between" align="flex-start" wrap="wrap">
        <Stack gap={2} style={{ flex: 1, minWidth: 220 }}>
          <Text fw={600} size="lg">{t('title')}</Text>
          <Text size="sm" c="dimmed">{t('subtitle')}</Text>
          {data && (
            <Text size="sm" c="dimmed">
              {t('retentionNote', { size: formatBytes(data.historyBytes, fm), days: data.retentionDays })}
            </Text>
          )}
        </Stack>
        {permissions.canEdit && (
          <Button leftSection={<IconHistory size={16} />} onClick={() => { setSaveError(null); setSaveOpen(true); }} style={{ minHeight: 44 }}>
            {t('saveVersion')}
          </Button>
        )}
      </Group>

      {isLoading && <Loader size="sm" />}
      {error && (
        <Alert color="red">
          <Group justify="space-between">
            <Text size="sm">{t('loadFailed')}</Text>
            <Button size="compact-sm" variant="light" onClick={() => void mutate()}>{t('retry')}</Button>
          </Group>
        </Alert>
      )}
      {data && versions.length === 0 && (
        <Stack gap={2}>
          <Text fw={600}>{t('empty')}</Text>
          <Text size="sm" c="dimmed">{t('emptyHint')}</Text>
        </Stack>
      )}
      {data && versions.length > 0 && (
        <Stack gap="xs">
          {versions.map((v) => {
            const title = versionTitle(v, (m) => tr.msg(m));
            return (
              <UnstyledButton
                key={v.id}
                onClick={() => setOpenId(v.id)}
                aria-label={t('openVersion', { number: v.number })}
                p="sm"
                style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 8, minHeight: 44 }}
              >
                <Group justify="space-between" align="flex-start" wrap="wrap" gap="xs">
                  <Stack gap={0} style={{ flex: 1, minWidth: 180 }}>
                    <Text fw={600} size="sm" style={{ overflowWrap: 'anywhere' }}>{title}</Text>
                    <Text size="xs" c="dimmed">
                      {v.name ? `${tr.msg(causeMessage(v.cause))} · ` : ''}
                      {t('meta', { author: v.createdByName ?? t('automaticAuthor'), when: fm.date(v.createdAt, 'dateTime') })}
                    </Text>
                  </Stack>
                  <Stack gap={0} align="flex-end">
                    <Text size="xs" c="dimmed">{tr.msg(expiryMessage(v.expiresAt))}</Text>
                    <Text size="xs" c="dimmed">{t('itemCount', { count: v.itemCount })}</Text>
                  </Stack>
                </Group>
              </UnstyledButton>
            );
          })}
          {hasOlder && (
            <Button
              variant="default"
              onClick={() => void setSize(size + 1)}
              loading={isValidating}
              style={{ minHeight: 44 }}
            >
              {t('loadOlder')}
            </Button>
          )}
        </Stack>
      )}

      <Modal opened={saveOpen} onClose={() => setSaveOpen(false)} title={t('saveTitle')} centered size="sm">
        <Stack gap="sm">
          <Text size="sm" c="dimmed">{t('saveHint')}</Text>
          <TextInput
            label={t('nameLabel')}
            placeholder={t('namePlaceholder')}
            value={name}
            onChange={(e) => setName(e.currentTarget.value)}
            error={name.length > 0 ? nameProblem : null}
            data-autofocus
            styles={{ input: { minHeight: 44 } }}
          />
          {saveError && <Alert color="red" role="alert">{saveError}</Alert>}
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setSaveOpen(false)}>{t('restore.cancel')}</Button>
            <Button onClick={() => void save()} loading={saving} disabled={nameProblem !== null}>{t('save')}</Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
