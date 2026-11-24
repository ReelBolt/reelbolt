'use client';

import { useMemo, useState } from 'react';
import Link from 'next/link';
import { Alert, Anchor, Button, Center, Group, Loader, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconAlertCircle, IconDownload, IconKey } from '@tabler/icons-react';
import { PageHeader } from '@/components/shared/PageHeader';
import { EmptyState } from '@/components/shared/EmptyState';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { RunnerTable } from '@/components/runners/RunnerTable';
import { useRunners } from '@/lib/hooks/use-runners';
import { useOrgMembers } from '@/lib/hooks/use-orgs';
import { useAuth } from '@/lib/hooks/use-auth';
import { ApiError } from '@/lib/api/client';
import { describeRevokeError, revokeRunner } from '@/lib/api/runners';
import { canViewMembers } from '@/lib/org-permissions';
import type { RunnerDevice } from '@/lib/types/runner';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

/**
 * The workspace's runners (E7-UI).
 *
 * What this page is: the machines the ACTIVE workspace has paired, what each of them can run, and
 * the way to cut one off. Pairing itself starts on the device — it asks the API for a code and
 * shows it — so this page's job on that front is to route the user to the approval screen.
 *
 * Two things it deliberately does not do:
 *   - It cannot show a revoked device. `GET /api/v1/runners` returns active devices only, so
 *     revoking removes the row rather than flipping it to a 'Revoked' badge, and the copy says so
 *     before the user commits.
 *   - It claims no location for anything. There is no geolocation in this product (no GeoIP
 *     database, no vendor lookup), so nothing here is ever labelled with a city.
 */
export default function RunnersPage() {
  const t = useT('runners.page');
  const tAll = useT();
  const { user, activeOrgId } = useAuth();
  const { data: devices, isLoading, error, mutate } = useRunners();

  // Member names for the 'Paired by' column. The device list carries only an account id, and the
  // members endpoint refuses a Billing caller, so it is fetched only for a role that may read it;
  // without it the column falls back to the id's short form (RunnerTable).
  const mayReadMembers = canViewMembers(user?.orgRole);
  const { data: members } = useOrgMembers(mayReadMembers ? activeOrgId : null);

  const pairerNames = useMemo(() => {
    const map: Record<string, string> = {};
    for (const member of members ?? []) {
      map[member.userId] = member.displayName || member.email;
    }
    return map;
  }, [members]);

  const [target, setTarget] = useState<RunnerDevice | null>(null);
  const [revoking, setRevoking] = useState(false);

  async function handleRevoke() {
    if (!target) return;
    setRevoking(true);
    try {
      const result = await revokeRunner(target.id);
      await mutate();
      notifications.show({
        title: result.alreadyRevoked ? t('revokedTitleAlready') : t('revokedTitle'),
        message: result.alreadyRevoked
          ? t('revokedAlready', { name: target.name })
          : t('revokedNow', { name: target.name }),
        color: result.alreadyRevoked ? 'yellow' : 'green',
      });
    } catch (err: unknown) {
      notifications.show({ title: t('revokeFailedTitle'), message: describeRevokeError(err), color: 'red' });
    } finally {
      setRevoking(false);
      setTarget(null);
    }
  }

  if (isLoading) {
    return <Center h={300}><Loader /></Center>;
  }

  // 403 here is 'your session has no active workspace' — the only 403 this endpoint answers with.
  const forbidden = error instanceof ApiError && error.status === 403;

  return (
    <>
      <PageHeader title={t('title')}>
        <Button component={Link} href="/runners/pair" leftSection={<IconKey size={16} />}>
          {t('pairButton')}
        </Button>
      </PageHeader>

      <Text size="sm" c="dimmed" maw={720} mt={-8} mb="lg">
        {t('intro')}
      </Text>

      {forbidden ? (
        <Alert color="yellow" icon={<IconAlertCircle size={16} />} title={t('noWorkspaceTitle')}>
          {t('noWorkspaceBody')}
        </Alert>
      ) : error ? (
        <Alert color="red" icon={<IconAlertCircle size={16} />} title={t('loadFailedTitle')}>
          {describeError(tAll, error, t('loadFailedFallback'))}
        </Alert>
      ) : devices && devices.length > 0 ? (
        <RunnerTable devices={devices} onRevoke={setTarget} pairerNames={pairerNames} />
      ) : (
        <EmptyState
          title={t('empty.title')}
          // Says what pairing IS and nothing about availability: installers are E10b's, and the
          // site page this links to is the one place that states whether a download exists yet.
          description={t('empty.description', { platforms: t('supportedPlatforms') })}
        >
          <Stack align="center" gap="sm">
            <Group gap="sm">
              <Button component={Link} href="/runners/pair" leftSection={<IconKey size={16} />}>
                {t('empty.havePairingCode')}
              </Button>
              {/*
                A plain anchor, NOT next/link: the dashboard runs with basePath '/app', and Link
                prepends it, which would point at /app/runner — a route that does not exist. The
                marketing site is served from the same origin at the root (nginx location /), so
                the absolute path is the correct one. It currently renders that page's own
                'not available yet' state until E10b publishes signed installers; linking there is
                still right, and it is why the label promises nothing more than the page does.
              */}
              <Anchor href="/runner" size="sm">
                <Group gap={6} component="span">
                  <IconDownload size={16} />
                  {t('empty.desktopRunner')}
                </Group>
              </Anchor>
            </Group>
          </Stack>
        </EmptyState>
      )}

      <ConfirmModal
        opened={!!target}
        onClose={() => setTarget(null)}
        onConfirm={handleRevoke}
        title={t('revokeDialog.title')}
        confirmLabel={t('revokeDialog.confirm')}
        loading={revoking}
        message={t('revokeDialog.message', { name: target?.name ?? t('revokeDialog.thisDevice') })}
      />
    </>
  );
}
