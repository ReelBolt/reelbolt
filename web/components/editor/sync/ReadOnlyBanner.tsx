'use client';

import React from 'react';
import { Alert, Box, Button, Group, Stack, Text } from '@mantine/core';
import { IconCloudOff, IconDownload, IconLock } from '@tabler/icons-react';
import type { SaveState } from '@/lib/editing/sync';
import { useT } from '@/i18n';

export interface ReadOnlyBannerProps {
  save: SaveState;
  onReload: () => void;
  /** P9: download the offline queue that could not be rebased. */
  onDownloadQueued?: () => void;
  /** P9: drop the offline queue (after downloading it, or deliberately). */
  onDiscardQueued?: () => void;
  /** R02.1: "Keep my copy" — save this tab's edits as a new timeline. */
  onKeepCopy?: () => void;
  /** R02.1: "Use the latest" — download a recovery file, then reload what the server has. */
  onUseLatest?: () => void;
  /** R02.1: a copy is being written. */
  busy?: boolean;
  /**
   * `bar`: the phone's one-line conflict bar (about 52 px), so the timeline keeps its height.
   * `full` (default): the explanatory banner.
   */
  variant?: 'full' | 'bar';
}

/**
 * Shown when the sync client is in `conflict`: another tab or user advanced the revision, this one
 * is stale, and nothing it does will be saved until the user reloads. The banner says exactly that
 * and offers the one honest action.
 */
export function ReadOnlyBanner({ save, onReload, onDownloadQueued, onDiscardQueued, onKeepCopy, onUseLatest, busy = false, variant = 'full' }: ReadOnlyBannerProps) {
  const t = useT('editor.sync.readOnlyBanner');
  if (save.status === 'offline' || save.status === 'reconnecting') {
    return (
      <Alert color="blue" icon={<IconCloudOff size={16} />} title={save.status === 'offline' ? t('offline') : t('reconnecting')} data-testid="offline-banner">
        <Text size="sm">{save.message}</Text>
      </Alert>
    );
  }
  if (save.status !== 'conflict') return null;
  if (save.offlineRefused) {
    return (
      <Alert color="orange" icon={<IconLock size={16} />} title={t('yourOfflineEditsCould')} data-testid="offline-refused-banner">
        <Stack gap={6}>
          <Text size="sm">{save.message}</Text>
          <Group gap="xs">
            {onDownloadQueued && (
              <Button size="xs" fz="xs" variant="light" color="orange" leftSection={<IconDownload size={14} />} onClick={onDownloadQueued} data-testid="offline-download">
                {t('downloadMyOfflineEdits')}
              </Button>
            )}
            {onDiscardQueued && (
              <Button size="xs" fz="xs" variant="subtle" color="red" onClick={onDiscardQueued} data-testid="offline-discard">
                {t('discardThemAndReload')}
              </Button>
            )}
          </Group>
        </Stack>
      </Alert>
    );
  }
  if (variant === 'bar') {
    return (
      <Box
        role="alert"
        data-testid="read-only-banner"
        data-variant="bar"
        px={12}
        py={4}
        style={{ display: 'flex', alignItems: 'center', gap: 8, flex: '0 0 auto', background: 'var(--mantine-color-orange-light)', borderBottom: '1px solid var(--mantine-color-orange-outline)' }}
      >
        <IconLock size={16} aria-hidden style={{ flex: '0 0 auto' }} />
        <Text size="sm" fw={500} truncate="end" style={{ flex: 1, minWidth: 0 }}>
          {t('changedElsewhereYourEdits')}
        </Text>
        {onKeepCopy && (
          <Button h={44} variant="filled" color="orange" loading={busy} onClick={onKeepCopy} data-testid="conflict-keep-copy-bar" style={{ flex: '0 0 auto' }}>
            {t('keepMyCopy')}
          </Button>
        )}
        <Button h={44} variant="default" onClick={onUseLatest ?? onReload} data-testid="read-only-reload" style={{ flex: '0 0 auto' }}>
          {t('useTheLatest')}
        </Button>
      </Box>
    );
  }
  return (
    <Alert color="orange" icon={<IconLock size={16} />} title={t('thisTimelineWasChanged')} data-testid="read-only-banner">
      <Group justify="space-between" align="center" wrap="wrap">
        <Text size="sm" style={{ flex: '1 1 320px' }}>
          {t('yourEditsAreKept')}
        </Text>
        <Group gap="xs">
          {onKeepCopy && (
            <Button size="xs" fz="xs" variant="filled" color="orange" loading={busy} onClick={onKeepCopy} data-testid="conflict-keep-copy-banner">
              {t('keepMyCopy2')}
            </Button>
          )}
          <Button size="xs" fz="xs" variant="default" onClick={onUseLatest ?? onReload} data-testid="read-only-reload">
            {t('useTheLatest2')}
          </Button>
        </Group>
      </Group>
    </Alert>
  );
}
