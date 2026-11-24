'use client';

import { useState } from 'react';
import useSWR from 'swr';
import { Alert, Anchor, Breadcrumbs, Button, Group, Loader, Stack, Text, UnstyledButton } from '@mantine/core';
import { IconAlertTriangle, IconChevronRight, IconFolder } from '@tabler/icons-react';
import { describeCloudError, listCloudFolder } from '@/lib/api/cloudStorage';
import type { CloudFolder, CloudFolderList } from '@/lib/types/cloudStorage';
import { useT } from '@/i18n';

/** One folder level of a drive, cached by connection + drive + folder. */
export function useCloudFolder(connectionId: string | null, driveId: string | null, folderId: string | null) {
  return useSWR<CloudFolderList>(
    connectionId ? ['cloud-folder', connectionId, driveId ?? '', folderId ?? ''] : null,
    () => listCloudFolder(connectionId as string, driveId, folderId),
    { revalidateOnFocus: false },
  );
}

/**
 * Walks a drive one folder at a time and picks the folder to link. Only folders are listed here; the
 * files inside the chosen folder are chosen next, in the file selector.
 */
export function CloudFolderBrowser({
  connectionId,
  driveId,
  value,
  onChange,
}: {
  connectionId: string;
  driveId: string | null;
  value: CloudFolder | null;
  onChange: (folder: CloudFolder) => void;
}) {
  // The way down from the drive's top: each step is a folder id and the name to show for it.
  const t = useT('integrations.cloud.browser');
  const [trail, setTrail] = useState<{ folderId: string | null; name: string }[]>([{ folderId: null, name: '' }]);
  const here = trail[trail.length - 1];
  const { data, error, isLoading, mutate } = useCloudFolder(connectionId, driveId, here.folderId);

  const folders = (data?.items ?? []).filter((i) => i.isFolder);
  const fileCount = (data?.items ?? []).length - folders.length;
  const chosenHere = value && data && value.folderId === data.folder.folderId;

  return (
    <Stack gap="sm">
      <Breadcrumbs separator={<IconChevronRight size={12} aria-hidden />} aria-label={t('whereAria')}>
        {trail.map((step, index) =>
          index === trail.length - 1
            ? <Text key={`${step.folderId ?? 'top'}-${index}`} size="sm" fw={600}>{step.name || t('top')}</Text>
            : (
              <Anchor
                key={`${step.folderId ?? 'top'}-${index}`}
                component="button"
                type="button"
                size="sm"
                onClick={() => setTrail(trail.slice(0, index + 1))}
              >
                {step.name || t('top')}
              </Anchor>
            ))}
      </Breadcrumbs>

      {isLoading && <Group gap="xs"><Loader size="xs" /><Text size="sm" c="dimmed">{t('opening')}</Text></Group>}

      {error && (
        <Alert color="red" icon={<IconAlertTriangle size={16} />} role="alert">
          <Stack gap="xs" align="flex-start">
            <Text size="sm">{describeCloudError(error, t('openFailed'))}</Text>
            <Button size="xs" variant="default" onClick={() => void mutate()}>{t('tryAgain')}</Button>
          </Stack>
        </Alert>
      )}

      {data && (
        <>
          {folders.length === 0
            ? <Text size="sm" c="dimmed">{t('noFolders')}</Text>
            : (
              <Stack gap={2} role="list" aria-label={t('foldersAria')}>
                {folders.map((folder) => (
                  <div key={folder.id} role="listitem">
                    <UnstyledButton
                      onClick={() => setTrail([...trail, { folderId: folder.id, name: folder.name }])}
                      style={{ padding: '8px 6px', minHeight: 40, width: '100%' }}
                      aria-label={t('openFolderAria', { name: folder.name })}
                    >
                      <Group gap="xs" wrap="nowrap">
                        <IconFolder size={16} aria-hidden />
                        <Text size="sm" truncate>{folder.name}</Text>
                      </Group>
                    </UnstyledButton>
                  </div>
                ))}
              </Stack>
            )}
          {data.truncated && <Text size="xs" c="dimmed">{t('truncated')}</Text>}
          <Group gap="sm" align="center">
            <Button
              size="sm"
              style={{ minHeight: 40 }}
              variant={chosenHere ? 'filled' : 'light'}
              onClick={() => onChange(data.folder)}
            >
              {chosenHere ? t('chosen') : t('use', { name: data.folder.name || here.name || t('top') })}
            </Button>
            <Text size="xs" c="dimmed">
              {t('fileCount', { count: fileCount })}
            </Text>
          </Group>
        </>
      )}
    </Stack>
  );
}
