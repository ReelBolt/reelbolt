'use client';

import { useState } from 'react';
import { notFound, usePathname, useRouter, useSearchParams } from 'next/navigation';
import { Alert, Button, Card, Center, Loader, Stack, Tabs, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconInfoCircle, IconPlus } from '@tabler/icons-react';
import { PageHeader } from '@/components/shared/PageHeader';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { EmptyState } from '@/components/shared/EmptyState';
import { usePublicApiEnabled } from '@/components/shell/PublicApiFlag';
import { ApiKeysTable } from '@/components/org/api-keys/ApiKeysTable';
import { CreateApiKeyModal } from '@/components/org/api-keys/CreateApiKeyModal';
import { EditApiKeyModal } from '@/components/org/api-keys/EditApiKeyModal';
import { ApiKeyAuditDrawer } from '@/components/org/api-keys/ApiKeyAuditDrawer';
import { TrialReadOnlyNotice } from '@/components/org/api-keys/TrialReadOnlyNotice';
import { WebhooksPanel } from '@/components/org/api-keys/WebhooksPanel';
import { ApiError } from '@/lib/api/client';
import { revokeApiKey } from '@/lib/api/api-keys';
import { useApiKeys } from '@/lib/hooks/use-api-keys';
import { useAuth } from '@/lib/hooks/use-auth';
import { useOrgs } from '@/lib/hooks/use-orgs';
import { canManageOrg } from '@/lib/org-permissions';
import { orgDisplayLabel } from '@/lib/org-label';
import type { ApiKeyItem } from '@/lib/types/api-key';
import { useLocale, useT } from '@/i18n';
import { docsPageHref } from '@/lib/onboarding/guide-links';

/**
 * Workspace API keys (public API): create with a secret shown once, list with live usage, edit
 * name and caps, view the audit trail, revoke.
 *
 * The route is absent unless `PUBLIC_API_ENABLED=true`: the shell resolves the runtime flag and
 * publishes it through `PublicApiFlag`; off means 404, never a page whose every call fails.
 * When on, the API still refuses a Member or Billing caller (403), so those roles get an
 * explanation instead of a request. A Personal workspace is allowed — the server treats it like
 * any other organization.
 */
export default function OrgApiKeysPage() {
  const enabled = usePublicApiEnabled();
  if (!enabled) notFound();
  return <OrgApiKeysView />;
}

function OrgApiKeysView() {
  const t = useT('org.apiKeys');
  const tr = useT();
  const locale = useLocale();
  const { user, activeOrgId } = useAuth();
  const { data: orgs } = useOrgs();

  const org = orgs?.find((candidate) => candidate.id === activeOrgId) ?? null;
  const role = org?.role ?? user?.orgRole ?? null;
  const manager = canManageOrg(role);

  const { data, error, mutate } = useApiKeys(manager ? activeOrgId : null);
  const items = data?.items ?? [];
  const capsEnforced = data?.capsEnforced ?? null;

  const [createOpened, setCreateOpened] = useState(false);
  const [editing, setEditing] = useState<ApiKeyItem | null>(null);
  const [auditKey, setAuditKey] = useState<ApiKeyItem | null>(null);
  const [revokeTarget, setRevokeTarget] = useState<ApiKeyItem | null>(null);
  const [revoking, setRevoking] = useState(false);

  async function handleRevoke() {
    const target = revokeTarget;
    if (!target) return;
    setRevoking(true);
    try {
      await revokeApiKey(target.id);
      await mutate();
      notifications.show({
        title: t('notifications.revokedTitle'),
        message: t('notifications.revokedMessage', { name: target.name }),
        color: 'green',
      });
    } catch {
      notifications.show({
        title: t('notifications.revokeFailedTitle'),
        message: t('notifications.revokeFailed'),
        color: 'red',
      });
    } finally {
      setRevoking(false);
      setRevokeTarget(null);
    }
  }

  const hidden = !manager;
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const tab = searchParams?.get('tab') === 'webhooks' ? 'webhooks' : 'keys';
  const selectTab = (value: string | null) => {
    router.replace(value === 'webhooks' ? `${pathname}?tab=webhooks` : (pathname ?? '/org/api-keys'));
  };

  return (
    <>
      <PageHeader
        title={t('title')}
        breadcrumbs={[
          { label: org ? orgDisplayLabel(org, tr) : t('breadcrumbOrg'), href: '/org/settings' },
          { label: t('title') },
        ]}
      >
        {manager && tab === 'keys' && (
          <Button leftSection={<IconPlus size={16} aria-hidden="true" />} onClick={() => setCreateOpened(true)}>
            {t('newKey')}
          </Button>
        )}
      </PageHeader>

      {hidden ? (
        <Alert
          icon={<IconInfoCircle size={16} />}
          color="gray"
          variant="light"
          title={role === 'Billing' ? t('hidden.billingTitle') : role ? t('hidden.memberTitle') : t('hidden.noRoleTitle')}
        >
          {role === 'Billing' ? t('hidden.billingBody') : role ? t('hidden.memberBody') : t('hidden.noRoleBody')}
        </Alert>
      ) : (
        <Tabs value={tab} onChange={selectTab} keepMounted={false}>
          <Tabs.List mb="lg">
            <Tabs.Tab value="keys">{t('tabKeys')}</Tabs.Tab>
            <Tabs.Tab value="webhooks">{t('tabWebhooks')}</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panel value="webhooks">
            <WebhooksPanel orgId={activeOrgId} />
          </Tabs.Panel>
          <Tabs.Panel value="keys">
        <Stack gap="lg">
          <Text size="sm" c="dimmed">
            {t('description')}
          </Text>

          <TrialReadOnlyNotice />

          <Card withBorder padding="lg">
            {error ? (
              <Alert color={error instanceof ApiError && error.status === 404 ? 'gray' : 'red'} variant="light">
                {error instanceof ApiError && error.status === 404 ? (
                  <>
                    <Text fw={600}>{t('notAvailableTitle')}</Text>
                    <Text size="sm">{t('notAvailableBody')}</Text>
                  </>
                ) : (
                  <>
                    <Text fw={600}>{t('loadFailedTitle')}</Text>
                    <Text size="sm">{t('loadFailedBody')}</Text>
                  </>
                )}
              </Alert>
            ) : data === undefined ? (
              <Center h={140}>
                <Loader size="sm" />
              </Center>
            ) : items.length === 0 ? (
              <EmptyState title={t('emptyTitle')} description={t('emptyDescription')}>
                <Button variant="light" leftSection={<IconPlus size={16} aria-hidden="true" />} onClick={() => setCreateOpened(true)}>
                  {t('newKey')}
                </Button>
                <a href={docsPageHref('api/', locale)}>{t('docsLink')}</a>
              </EmptyState>
            ) : (
              <ApiKeysTable
                items={items}
                capsEnforced={capsEnforced}
                onEdit={setEditing}
                onViewAudit={setAuditKey}
                onRevoke={setRevokeTarget}
                busyId={revoking ? revokeTarget?.id ?? null : null}
              />
            )}
          </Card>
        </Stack>
          </Tabs.Panel>
        </Tabs>
      )}

      {manager && (
        <>
          <CreateApiKeyModal
            opened={createOpened}
            onClose={() => setCreateOpened(false)}
            onCreated={() => mutate()}
            defaultDailyCap={data?.defaultDailyCapCredits ?? null}
          />
          <EditApiKeyModal
            item={editing}
            opened={!!editing}
            onClose={() => setEditing(null)}
            onSaved={async (updated) => {
              await mutate();
              notifications.show({
                title: t('notifications.updatedTitle'),
                message: t('notifications.updatedMessage', { name: updated.name }),
                color: 'green',
              });
            }}
          />
          <ApiKeyAuditDrawer item={auditKey} opened={!!auditKey} onClose={() => setAuditKey(null)} />
          <ConfirmModal
            opened={!!revokeTarget}
            onClose={() => setRevokeTarget(null)}
            onConfirm={() => void handleRevoke()}
            title={t('revoke.title')}
            message={t('revoke.message', { name: revokeTarget?.name ?? t('title') })}
            confirmLabel={t('revoke.confirm')}
            loading={revoking}
          />
        </>
      )}
    </>
  );
}
