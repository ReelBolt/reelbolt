'use client';

import Link from 'next/link';
import { Alert, Button, Stack, Text } from '@mantine/core';
import { IconAlertTriangle } from '@tabler/icons-react';
import { isStorageQuotaError, storageQuotaInfo, storageQuotaMessage } from '@/lib/api/quota';
import { useT } from '@/i18n';

/**
 * A storage refusal, rendered the one way the design document prescribes
 * (docs/design/local-folder-sync-and-storage.md Part 2): "Your workspace is out of storage (X of Y
 * used). Delete files or upgrade your plan." with a link to Billing.
 *
 * It takes the raw error so callers never have to decide whether it is one: `null` or any other error
 * renders nothing, which is what lets an upload tray drop it in unconditionally.
 */
export function StorageQuotaNotice({
  error,
  title,
  testId = 'storage-quota-notice',
}: {
  error: unknown;
  title?: string;
  testId?: string;
}) {
  const t = useT('outputs.quota');
  if (!isStorageQuotaError(error)) return null;
  return <StorageQuotaBanner message={storageQuotaMessage(storageQuotaInfo(error))} title={title ?? t('title')} testId={testId} />;
}

/** The same banner from an already-built sentence — for a `lastError` string a route handed back. */
export function StorageQuotaBanner({ message, title, testId = 'storage-quota-notice' }: { message: string; title: string; testId?: string }) {
  const t = useT('outputs.quota');
  return (
    <Alert color="yellow" variant="light" icon={<IconAlertTriangle size={16} />} title={title} role="alert" data-testid={testId}>
      <Stack gap="xs" align="flex-start">
        <Text size="sm">{message}</Text>
        <Button component={Link} href="/settings/billing" size="compact-sm" variant="light">
          {t('plans')}
        </Button>
      </Stack>
    </Alert>
  );
}

/**
 * True when a route's plain `lastError` prose is its way of saying the workspace is full — a folder
 * link's last sync, or any other sync that reports storage in words. The device routes send
 * "Storage is full (X of Y used)" and the server-side syncs send "Storage is full (X of Y used) — N
 * files were not copied…"; both are matched here so the UI can offer Billing instead of a dead end.
 */
export function lastErrorLooksLikeStorageFull(lastError: string | null | undefined): boolean {
  return Boolean(lastError && /storage is full|out of storage|no storage left/i.test(lastError));
}
