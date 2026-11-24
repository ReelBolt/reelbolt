'use client';

import { useMemo, useState } from 'react';
import { Alert, Button, Center, Group, Loader, Modal, NativeSelect, Stack, Switch, Text } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { IconAlertTriangle, IconInfoCircle, IconPlus } from '@tabler/icons-react';
import { EmptyState } from '@/components/shared/EmptyState';
import { useFormat, useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { patchMarketingLink } from '@/lib/api/marketing-links';
import { useMarketingLinks, useMarketingLinksFunnel, useRevalidateMarketingLinks } from '@/lib/hooks/use-marketing-links';
import {
  DEFAULT_FUNNEL_PERIOD,
  FUNNEL_PERIODS,
  type FunnelPeriod,
  type MarketingLink,
} from '@/lib/types/marketing-links';
import { EditLinkModal } from './EditLinkModal';
import { LinkUrls, copyText, longUrlOf, shortUrlOf } from './LinkUrls';
import { LinksFunnelTable, type LinkRow } from './LinksFunnelTable';
import { NewLinkModal } from './NewLinkModal';
import { funnelRowOf, funnelTotals, periodCoversLink, startingCurrency } from './link-model';

/** The three populations the figures count, and what URL capture changes about the account ones. */
function PopulationsNote({
  urlCaptureEnabled,
  visitIngestEnabled,
}: {
  urlCaptureEnabled: boolean | undefined;
  /** False when this server accepts no visit beacon at all; undefined from an API that does not say. */
  visitIngestEnabled: boolean | undefined;
}) {
  const t = useT('adminLinks.note');
  return (
    <Alert color="blue" variant="light" icon={<IconInfoCircle size={16} />} title={t('title')} data-testid="links-note">
      <Stack gap={6} component="ul" style={{ margin: 0, paddingInlineStart: '1.1rem' }}>
        <li>{t('clicks')}</li>
        <li>{t('visitors')}</li>
        {visitIngestEnabled === false ? <li data-testid="links-note-visits-off">{t('visitsOff')}</li> : null}
        <li>{t('accounts')}</li>
        {urlCaptureEnabled === undefined ? null : (
          <li data-testid={urlCaptureEnabled ? 'links-note-capture-on' : 'links-note-capture-off'}>
            {urlCaptureEnabled ? t('captureOn') : t('captureOff')}
          </li>
        )}
      </Stack>
    </Alert>
  );
}

/**
 * Platform admin: every tracked link with its funnel for the chosen period, and the dialogs that
 * create, edit and archive one. The API is the authority on every rule; this only shows and sends.
 */
export function LinksPanel() {
  const t = useT('adminLinks');
  const tAll = useT();
  const f = useFormat();
  const isPhone = useMediaQuery('(max-width: 48em)');
  const [days, setDays] = useState<FunnelPeriod>(DEFAULT_FUNNEL_PERIOD);
  const [showArchived, setShowArchived] = useState(false);
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<MarketingLink | null>(null);
  const [toggling, setToggling] = useState<MarketingLink | null>(null);
  const [togglePending, setTogglePending] = useState(false);
  const [urlsFor, setUrlsFor] = useState<MarketingLink | null>(null);

  const links = useMarketingLinks();
  const funnel = useMarketingLinksFunnel(days);
  const revalidate = useRevalidateMarketingLinks();

  const all = useMemo(() => links.data?.items ?? [], [links.data]);
  const archivedCount = all.filter((link) => link.archivedAt !== null).length;
  const rows: LinkRow[] = useMemo(
    () =>
      all
        .filter((link) => showArchived || link.archivedAt === null)
        .map((link) => ({
          link,
          funnel: funnel.data ? funnelRowOf(link, funnel.data.items) : null,
          // Cost per signup needs all of a link's signups: only a period that reaches back to the
          // link's creation shows them (the response's own period and clock, not the selector's).
          coversLife: funnel.data ? periodCoversLink(link.createdAt, funnel.data.days, funnel.data.generatedAt) : true,
        })),
    [all, funnel.data, showArchived],
  );
  const totals = useMemo(() => {
    if (!funnel.data || rows.length === 0) return null;
    const covered = new Set(rows.filter((row) => row.coversLife !== false).map((row) => row.link.id));
    return funnelTotals(
      rows.flatMap((row) => (row.funnel ? [row.funnel] : [])),
      (item) => covered.has(item.linkId),
    );
  }, [funnel.data, rows]);

  const copy = async (link: MarketingLink, kind: 'short' | 'long') => {
    const copied = await copyText(kind === 'short' ? shortUrlOf(link) : longUrlOf(link));
    // No clipboard (an insecure origin, a refused permission): show both addresses to copy by hand.
    if (copied) notifications.show({ message: kind === 'short' ? t('copied.short') : t('copied.long'), color: 'green' });
    else setUrlsFor(link);
  };

  const toggleArchive = async () => {
    if (!toggling) return;
    const archive = toggling.archivedAt === null;
    setTogglePending(true);
    try {
      await patchMarketingLink(toggling.id, { archived: archive });
      notifications.show({ message: archive ? t('archive.done') : t('unarchive.done'), color: 'green' });
      setToggling(null);
      await revalidate();
    } catch {
      notifications.show({ message: archive ? t('archive.error') : t('unarchive.error'), color: 'red' });
    } finally {
      setTogglePending(false);
    }
  };

  const newLinkButton = (
    <Button leftSection={<IconPlus size={16} />} onClick={() => setCreating(true)} data-testid="new-link">
      {t('newLink')}
    </Button>
  );

  let body: React.ReactNode;
  if (links.error && !links.data) {
    body = (
      <Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} title={t('loadError')} data-testid="links-error">
        {describeError(tAll, links.error)}
      </Alert>
    );
  } else if (!links.data) {
    body = (
      <Center h={200}>
        <Loader aria-label={t('loading')} />
      </Center>
    );
  } else if (all.length === 0) {
    body = (
      <EmptyState title={t('empty.title')} description={t('empty.body')}>
        {newLinkButton}
      </EmptyState>
    );
  } else {
    body = (
      <Stack gap="sm">
        {funnel.error && !funnel.data ? (
          <Alert
            color="red"
            variant="light"
            icon={<IconAlertTriangle size={16} />}
            title={t('funnelError.title')}
            data-testid="links-funnel-error"
          >
            {t('funnelError.body')}
          </Alert>
        ) : null}
        {rows.length === 0 ? (
          <Text size="sm" data-testid="links-all-archived">
            {t('empty.allArchived')}
          </Text>
        ) : null}
        {rows.length > 0 || funnel.data ? (
          <LinksFunnelTable
            rows={rows}
            totals={totals}
            unattributed={funnel.data?.unattributed ?? null}
            compact={!!isPhone}
            onCopy={(link, kind) => void copy(link, kind)}
            onEdit={setEditing}
            onToggleArchive={setToggling}
          />
        ) : null}
        <Text size="sm" data-testid="links-legend">
          {t('table.legend')}
        </Text>
      </Stack>
    );
  }

  const archive = toggling ? toggling.archivedAt === null : true;

  return (
    <Stack gap="md" data-testid="links-panel">
      <Text size="sm">{t('intro')}</Text>
      <PopulationsNote urlCaptureEnabled={funnel.data?.urlCaptureEnabled} visitIngestEnabled={funnel.data?.visitIngestEnabled} />

      <Group justify="space-between" align="flex-end" wrap="wrap" gap="sm">
        <Group align="flex-end" gap="md" wrap="wrap">
          <NativeSelect
            label={t('controls.period')}
            value={String(days)}
            data={FUNNEL_PERIODS.map((period) => ({ value: String(period), label: t('controls.days', { days: period }) }))}
            onChange={(event) => {
              const next = Number(event.currentTarget.value);
              const period = FUNNEL_PERIODS.find((candidate) => candidate === next);
              if (period) setDays(period);
            }}
            data-testid="links-period"
          />
          {archivedCount > 0 ? (
            <Switch
              label={t('controls.showArchived')}
              checked={showArchived}
              onChange={(event) => setShowArchived(event.currentTarget.checked)}
              data-testid="links-show-archived"
              mb={8}
            />
          ) : null}
        </Group>
        <Group gap="sm" align="center">
          {funnel.isValidating && links.data ? <Loader size="xs" aria-label={t('controls.refreshing')} /> : null}
          {all.length > 0 ? newLinkButton : null}
        </Group>
      </Group>
      {funnel.data?.generatedAt ? (
        <Text size="xs" data-testid="links-as-of">
          {t('controls.asOf', { time: f.date(funnel.data.generatedAt, 'dateTime') })}
        </Text>
      ) : null}

      {body}

      {creating ? (
        <NewLinkModal links={all} onClose={() => setCreating(false)} onCreated={() => void revalidate()} />
      ) : null}

      {editing ? (
        <EditLinkModal
          key={editing.id}
          link={editing}
          fallbackCurrency={startingCurrency(all)}
          onClose={() => setEditing(null)}
          onSaved={() => void revalidate()}
        />
      ) : null}

      <Modal
        opened={toggling !== null}
        onClose={() => {
          if (!togglePending) setToggling(null);
        }}
        title={toggling ? (archive ? t('archive.title', { code: toggling.code }) : t('unarchive.title', { code: toggling.code })) : ''}
        centered
        size="sm"
      >
        {toggling ? (
          <Stack gap="lg" data-testid="link-archive-confirm">
            <Text size="sm">{archive ? t('archive.body') : t('unarchive.body', { path: toggling.destinationPath })}</Text>
            <Group justify="flex-end">
              <Button variant="default" onClick={() => setToggling(null)} disabled={togglePending}>
                {t('form.cancel')}
              </Button>
              <Button
                color={archive ? 'red' : undefined}
                loading={togglePending}
                onClick={() => void toggleArchive()}
                data-testid="link-archive-confirm-button"
              >
                {archive ? t('archive.confirm') : t('unarchive.confirm')}
              </Button>
            </Group>
          </Stack>
        ) : null}
      </Modal>

      <Modal
        opened={urlsFor !== null}
        onClose={() => setUrlsFor(null)}
        title={urlsFor ? t('urls.title', { code: urlsFor.code }) : ''}
        centered
        size="lg"
        fullScreen={!!isPhone}
      >
        {urlsFor ? (
          <Stack gap="lg" data-testid="link-urls-fallback">
            <Alert color="yellow" variant="light" icon={<IconAlertTriangle size={16} />}>
              {t('urls.copyFailed')}
            </Alert>
            <LinkUrls link={urlsFor} />
            <Group justify="flex-end">
              <Button onClick={() => setUrlsFor(null)}>{t('urls.done')}</Button>
            </Group>
          </Stack>
        ) : null}
      </Modal>
    </Stack>
  );
}
