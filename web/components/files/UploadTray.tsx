'use client';

import { useState } from 'react';
import Link from 'next/link';
import { ActionIcon, Alert, Button, Group, Progress, Stack, Text } from '@mantine/core';
import { IconAlertTriangle, IconCheck, IconChevronDown, IconChevronUp, IconRefresh, IconX } from '@tabler/icons-react';
import { formatBytes } from '@/lib/utils/bytes';
import type { UploadEntry } from '@/lib/hooks/use-upload-queue';
import { useFormat, useT } from '@/i18n';
import { useStorageUsage } from '@/lib/hooks/use-storage';

const VIDEO_NAME = /\.(?:mp4|m4v|mov|mkv|webm|avi|mts|m2ts|mxf|wmv|flv|mpg|mpeg|3gp)$/i;
const GIB = 1024 * 1024 * 1024;
import classes from './files.module.css';

/** Floating per-file progress for the upload queue; a bottom sheet on phones. */
export function UploadTray({
  entries, onRetry, onClear,
}: {
  entries: UploadEntry[];
  onRetry: (id: string) => void;
  onClear: () => void;
}) {
  const t = useT('files.upload');
  const fm = useFormat();
  const [collapsed, setCollapsed] = useState(false);
  const { data: usage } = useStorageUsage();
  const maxGb = usage?.videoSourceLimits?.maxGb ?? null;
  if (entries.length === 0) return null;

  const active = entries.filter((e) => e.status === 'queued' || e.status === 'uploading').length;
  const failed = entries.filter((e) => e.status === 'error').length;
  const done = entries.filter((e) => e.status === 'done').length;
  const overall = Math.round(entries.reduce((s, e) => s + (e.status === 'done' ? 100 : e.progress), 0) / entries.length);
  const quotaEntries = entries.filter((e) => e.status === 'error' && e.quotaExceeded);

  return (
    <aside className={classes.tray} aria-label={t('trayLabel')} aria-live="polite">
      <Group justify="space-between" wrap="nowrap" px="md" py={10} gap="xs">
        <div style={{ minWidth: 0 }}>
          <Text size="sm" fw={600}>
            {active > 0 ? t('uploading', { count: active }) : failed > 0 ? t('failedSummary', { failed, done }) : t('doneSummary', { done })}
          </Text>
          {active > 0 && <Progress value={overall} size="xs" mt={6} aria-label={t('overallProgress')} />}
        </div>
        <Group gap={2} wrap="nowrap">
          <ActionIcon variant="subtle" size={44} color="gray" onClick={() => setCollapsed((c) => !c)} aria-label={collapsed ? t('expand') : t('collapse')}>
            {collapsed ? <IconChevronUp size={18} /> : <IconChevronDown size={18} />}
          </ActionIcon>
          {active === 0 && (
            <ActionIcon variant="subtle" size={44} color="gray" onClick={onClear} aria-label={t('dismiss')}>
              <IconX size={18} />
            </ActionIcon>
          )}
        </Group>
      </Group>
      {!collapsed && (
        <div className={classes.trayList}>
          {/* One banner for the workspace being full, above the per-file rows: it is one condition,
              it has one fix, and Retry on a single file cannot change it. */}
          {quotaEntries.length > 0 && (
            <Alert
              color="orange" variant="light" icon={<IconAlertTriangle size={16} />} role="alert"
              title={t('quotaTitle')} data-testid="upload-quota-notice"
            >
              <Stack gap="xs" align="flex-start">
                <Text size="sm">{quotaEntries[0].error}</Text>
                <Button component={Link} href="/settings/billing" size="compact-sm" variant="light">
                  {t('quotaCta')}
                </Button>
              </Stack>
            </Alert>
          )}
          {entries.map((e) => (
            <div key={e.id} style={{ padding: '8px 0' }}>
              <Group justify="space-between" wrap="nowrap" gap="xs">
                <div style={{ minWidth: 0 }}>
                  <Text size="sm" truncate>{e.name}</Text>
                  <Text size="xs" c={e.status === 'error' ? 'red' : 'dimmed'} truncate>
                    {e.status === 'error' ? e.error : e.destination ? t('destinationSize', { destination: e.destination, size: formatBytes(e.size, fm) }) : formatBytes(e.size, fm)}
                  </Text>
                </div>
                {e.status === 'done' && <IconCheck size={16} color="var(--mantine-color-green-6)" aria-label={t('uploaded')} />}
                {e.status === 'error' && !e.quotaExceeded && (
                  <Button size="compact-sm" variant="light" color="red" leftSection={<IconRefresh size={14} />} onClick={() => onRetry(e.id)}>
                    {t('retry')}
                  </Button>
                )}
                {(e.status === 'uploading' || e.status === 'queued') && (
                  <Text size="xs" c="dimmed" style={{ fontVariantNumeric: 'tabular-nums' }}>{e.status === 'queued' ? t('waiting') : `${e.progress}%`}</Text>
                )}
              </Group>
              {/* A video over the editing limit is stored like any file, but a run would refuse it: say so now. */}
              {maxGb !== null && e.status !== 'error' && VIDEO_NAME.test(e.name) && e.size > maxGb * GIB && (
                <Text size="xs" c="orange" mt={4} data-testid="upload-too-large-note">
                  {t('tooLargeToEdit', { size: Math.ceil(e.size / GIB), limit: maxGb })}
                </Text>
              )}
              {e.status === 'uploading' && <Progress value={e.progress} size={4} mt={6} aria-label={t('uploadingFile', { name: e.name })} />}
            </div>
          ))}
        </div>
      )}
    </aside>
  );
}
