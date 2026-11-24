'use client';

import { useEffect, useState } from 'react';
import useSWR from 'swr';
import { Alert, Button, Group, Loader, Stack, Text } from '@mantine/core';
import { IconDownload, IconInfoCircle } from '@tabler/icons-react';
import { SettingsCard } from './SettingsCard';
import {
  getDataExportLink,
  getDataExportStatus,
  requestDataExport,
  type ExportStatusResult,
} from '@/lib/api/account';
import { formatFileSize } from '@/lib/utils/format';
import { useT, useFormat, type Format } from '@/i18n';

/** How often the card re-reads the status while a copy is being prepared. */
export const EXPORT_POLL_INTERVAL_MS = 15_000;

/** The SWR key of the status read. */
export const DATA_EXPORT_KEY = '/api/v1/account/export/status';

/**
 * "Download your data": ask for a zip of everything ReelBolt holds about you and your personal
 * workspace, then download it here (the same file the email links to).
 *
 * Everything the card says comes from the export status endpoint: whether a copy is being prepared,
 * the newest copy's date, size and expiry, and when another copy can be asked for. It polls only
 * while a copy is being prepared.
 *
 * The download button asks for a fresh link at the moment of the click: links last 15 minutes, and
 * a page left open longer must not hand out a dead one.
 */
type Notice = { kind: 'too_soon'; retryAfterSeconds: number } | { kind: 'request_failed' } | { kind: 'download_failed' } | { kind: 'expired' } | null;

function formatDay(iso: string | null | undefined, f: Format): string | null {
  if (!iso) return null;
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return null;
  return f.date(d, 'medium');
}

export function DataExportCard() {
  const t = useT('settings.export');
  const f = useFormat();
  const [notice, setNotice] = useState<Notice>(null);
  const [requesting, setRequesting] = useState(false);
  const [downloading, setDownloading] = useState(false);
  // The clock the "next copy possible in…" sentence is measured against, ticking once a minute so
  // the wait counts down and the button comes back on its own.
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), 60_000);
    return () => window.clearInterval(id);
  }, []);

  const { data, error, isLoading, mutate } = useSWR<ExportStatusResult>(DATA_EXPORT_KEY, getDataExportStatus, {
    refreshInterval: (latest) => (latest?.kind === 'ok' && latest.preparing ? EXPORT_POLL_INTERVAL_MS : 0),
  });

  const onRequest = async () => {
    setRequesting(true);
    setNotice(null);
    try {
      const res = await requestDataExport();
      switch (res.kind) {
        case 'accepted':
        case 'already_running':
          await mutate();
          break;
        case 'too_soon':
          setNotice({ kind: 'too_soon', retryAfterSeconds: res.retryAfterSeconds });
          void mutate();
          break;
        case 'unavailable':
          void mutate({ kind: 'unavailable' }, { revalidate: false });
          break;
      }
    } catch {
      setNotice({ kind: 'request_failed' });
    } finally {
      setRequesting(false);
    }
  };

  const onDownload = async () => {
    setDownloading(true);
    setNotice(null);
    try {
      const res = await getDataExportLink();
      if (res.kind === 'ready') {
        window.location.assign(res.url);
      } else {
        if (res.kind === 'none') setNotice({ kind: 'expired' });
        void mutate();
      }
    } catch {
      setNotice({ kind: 'download_failed' });
    } finally {
      setDownloading(false);
    }
  };

  const description = t('description');

  if (isLoading && !data) {
    return (
      <SettingsCard title={t('title')} description={description} testId="data-export-card">
        <Group justify="center" py="sm">
          <Loader size="sm" aria-label={t('checking')} />
        </Group>
      </SettingsCard>
    );
  }

  if (data?.kind === 'unavailable') {
    return (
      <SettingsCard title={t('title')} description={description} testId="data-export-card">
        <Text size="sm" c="dimmed" data-testid="data-export-unavailable">
          {t('unavailable')}
        </Text>
      </SettingsCard>
    );
  }

  const status = data?.kind === 'ok' ? data : null;
  const preparing = status?.preparing ?? false;
  const latest = status?.latest ?? null;
  const waitSeconds = status?.nextRequestAt
    ? Math.max(0, (new Date(status.nextRequestAt).getTime() - now) / 1000)
    : notice?.kind === 'too_soon'
      ? notice.retryAfterSeconds
      : 0;
  const throttled = waitSeconds > 0;
  const made = formatDay(latest?.createdAt, f);
  const until = formatDay(latest?.expiresAt, f);

  return (
    <SettingsCard title={t('title')} description={description} testId="data-export-card">
      <Stack gap="sm">
        {preparing ? (
          <Alert
            color="violet"
            variant="light"
            icon={<Loader size={14} color="violet" />}
            title={t('preparingTitle')}
            data-testid="data-export-preparing"
            role="status"
          >
            <Text size="sm">
              {t('preparingBody')}
            </Text>
          </Alert>
        ) : null}

        {latest ? (
          <Text size="sm" data-testid="data-export-ready">
            {t('ready', {
              previous: preparing ? 'yes' : 'no',
              made: made ?? 'none',
              size: latest.sizeBytes ? formatFileSize(latest.sizeBytes) : 'none',
              until: until ?? 'none',
            })}
          </Text>
        ) : !preparing ? (
          <Text size="sm" c="dimmed">
            {t('background')}
          </Text>
        ) : null}

        {!data && error ? (
          <Text size="xs" c="dimmed" data-testid="data-export-unknown">
            {t('checkFailed')}
          </Text>
        ) : null}

        {throttled && !preparing ? (
          <Alert color="blue" variant="light" icon={<IconInfoCircle size={16} />} data-testid="data-export-too-soon">
            {t('tooSoon', waitSeconds < 3600 ? { unit: 'minutes', n: Math.max(1, Math.ceil(waitSeconds / 60)) } : { unit: 'hours', n: Math.ceil(waitSeconds / 3600) })}
          </Alert>
        ) : null}
        {notice?.kind === 'request_failed' ? (
          <Alert color="red" variant="light" data-testid="data-export-error">
            {t('requestFailed')}
          </Alert>
        ) : null}
        {notice?.kind === 'download_failed' ? (
          <Alert color="red" variant="light" data-testid="data-export-error">
            {t('downloadFailed')}
          </Alert>
        ) : null}
        {notice?.kind === 'expired' ? (
          <Alert color="blue" variant="light" data-testid="data-export-expired">
            {t('expired')}
          </Alert>
        ) : null}

        <Group gap="sm">
          {latest ? (
            <Button leftSection={<IconDownload size={16} />} onClick={onDownload} loading={downloading}>
              {t('download')}
            </Button>
          ) : null}
          {!preparing ? (
            <Button
              variant={latest ? 'default' : 'filled'}
              onClick={onRequest}
              loading={requesting}
              disabled={throttled}
            >
              {latest ? t('requestNew') : t('request')}
            </Button>
          ) : null}
        </Group>
      </Stack>
    </SettingsCard>
  );
}
