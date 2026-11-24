'use client';

import { useState } from 'react';
import { Alert, Button, Loader, Center } from '@mantine/core';
import { IconInfoCircle, IconPlus } from '@tabler/icons-react';
import { notifications } from '@mantine/notifications';
import { useProviders } from '@/lib/hooks/use-inference-providers';
import { deleteProvider } from '@/lib/api/inference-providers';
import { PageHeader } from '@/components/shared/PageHeader';
import { InferenceProviderTable } from '@/components/admin/InferenceProviderTable';
import { InferenceProviderForm } from '@/components/admin/InferenceProviderForm';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { EmptyState } from '@/components/shared/EmptyState';
import { useAuth } from '@/lib/hooks/use-auth';
import { canManageOrg } from '@/lib/org-permissions';
import { orgDisplayLabel } from '@/lib/org-label';
import { useOrgs } from '@/lib/hooks/use-orgs';
import type { InferenceProvider } from '@/lib/types/inference-provider';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

/**
 * The active workspace's own inference providers (A17-UI, decision D3).
 *
 * Access follows the API rather than a hidden page:
 *   - Owner/Admin: full management — create, edit, test, delete — through
 *     `/api/v1/org/inference-providers`, which is what the reused form and table are pointed at.
 *   - Member/Billing: nothing. `GET /api/v1/org/inference-providers` answers 403 for either, so
 *     the page explains the role and asks the API for nothing. A Member still gets a provider
 *     CHOICE in the step editors — that is the member-facing `selectable` list, not this page.
 *
 * A workspace row is bring-your-own: no `env:` credential, no PersonalOAuthMode and no
 * private-address endpoint (the API enforces all three, the form does not offer them). Creating,
 * editing or testing one needs the plan's BYO entitlement, and a refusal comes back `402
 * byo_not_in_plan` — rendered by the form as a plan limit with the upgrade path, never as an error.
 */
export default function OrgInferenceProvidersPage() {
  const tr = useT();
  const t = useT('org.providers');
  const { user, activeOrgId } = useAuth();
  const { data: orgs } = useOrgs();
  const org = orgs?.find((candidate) => candidate.id === activeOrgId) ?? null;
  const role = org?.role ?? user?.orgRole ?? null;
  const manager = canManageOrg(role);

  const { data: providers, isLoading, error, mutate } = useProviders('org', manager);

  const [formOpened, setFormOpened] = useState(false);
  const [deleteTarget, setDeleteTarget] = useState<InferenceProvider | null>(null);
  const [deleteLoading, setDeleteLoading] = useState(false);

  const handleCreateSuccess = () => {
    void mutate();
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    setDeleteLoading(true);
    try {
      await deleteProvider(deleteTarget.id, 'org');
      void mutate();
      notifications.show({ title: t('deletedTitle'), message: t('deletedMessage'), color: 'green' });
    } catch (err: unknown) {
      notifications.show({
        title: t('errorTitle'),
        message: describeError(tr, err, t('deleteFailed')),
        color: 'red',
      });
    } finally {
      setDeleteLoading(false);
      setDeleteTarget(null);
    }
  };

  const header = (
    <PageHeader
      title={t('title')}
      breadcrumbs={[
        { label: org ? orgDisplayLabel(org, tr) : t('breadcrumbOrg'), href: '/org/settings' },
        { label: t('title') },
      ]}
    >
      {manager && (
        <Button w={{ base: '100%', sm: 'auto' }} h={{ base: 40, sm: 36 }} leftSection={<IconPlus size={16} />} onClick={() => setFormOpened(true)}>
          {t('newProvider')}
        </Button>
      )}
    </PageHeader>
  );

  // No active workspace role: the API answers 404 for this caller and there is nothing to show.
  if (!role) {
    return (
      <>
        {header}
        <Alert icon={<IconInfoCircle size={16} />} color="gray" variant="light" title={t('noRoleTitle')}>
          {t('noRoleBody')}
        </Alert>
      </>
    );
  }

  if (!manager) {
    return (
      <>
        {header}
        <Alert
          icon={<IconInfoCircle size={16} />}
          color="gray"
          variant="light"
          title={t('managedTitle')}
        >
          {t('managedBody', { role: role ?? '' })}
        </Alert>
      </>
    );
  }

  if (isLoading) {
    return (
      <>
        {header}
        <Center h={300}><Loader /></Center>
      </>
    );
  }

  return (
    <>
      {header}

      {error && (
        <Alert icon={<IconInfoCircle size={16} />} color="red" variant="light" title={t('loadFailedTitle')}>
          {describeError(tr, error, t('loadFailedFallback'))}
        </Alert>
      )}

      {!error && providers && providers.length > 0 ? (
        <InferenceProviderTable providers={providers} onDelete={setDeleteTarget} scope="org" />
      ) : !error ? (
        <EmptyState
          title={t('emptyTitle')}
          description={t('emptyDescription')}
        />
      ) : null}

      <InferenceProviderForm
        opened={formOpened}
        onClose={() => setFormOpened(false)}
        onSuccess={handleCreateSuccess}
        scope="org"
      />

      <ConfirmModal
        opened={!!deleteTarget}
        onClose={() => setDeleteTarget(null)}
        onConfirm={handleDelete}
        title={t('deleteTitle')}
        message={t('deleteMessage', { name: deleteTarget?.name ?? '' })}
        loading={deleteLoading}
      />
    </>
  );
}
