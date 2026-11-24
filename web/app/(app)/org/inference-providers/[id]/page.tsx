'use client';

import { use } from 'react';
import { Alert } from '@mantine/core';
import { IconInfoCircle } from '@tabler/icons-react';
import { PageHeader } from '@/components/shared/PageHeader';
import { ProviderDetail } from '@/components/admin/ProviderDetail';
import { useAuth } from '@/lib/hooks/use-auth';
import { canManageOrg } from '@/lib/org-permissions';
import { useOrgs } from '@/lib/hooks/use-orgs';
import { orgDisplayLabel } from '@/lib/org-label';
import { useT } from '@/i18n';

/**
 * One of the active workspace's own providers: its account, models and capabilities. Owner/Admin
 * only — a Member or Billing caller gets the workspace explanation instead, because the API answers
 * 403 for the read itself.
 *
 * Same view as the platform admin's page, against `/api/v1/org/inference-providers`. A workspace
 * provider is never priced, so this page renders no price and calls no price route.
 */
export default function OrgInferenceProviderDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const tr = useT();
  const t = useT('org.providers');
  const { id } = use(params);
  const { user, activeOrgId } = useAuth();
  const { data: orgs } = useOrgs();
  const org = orgs?.find((candidate) => candidate.id === activeOrgId) ?? null;
  const role = org?.role ?? user?.orgRole ?? null;
  const manager = canManageOrg(role);

  const breadcrumbs = [
    { label: org ? orgDisplayLabel(org, tr) : t('breadcrumbOrg'), href: '/org/settings' },
    { label: t('title'), href: '/org/inference-providers' },
  ];

  if (!manager) {
    return (
      <>
        <PageHeader title={t('provider')} breadcrumbs={[...breadcrumbs, { label: t('provider') }]} />
        <Alert
          icon={<IconInfoCircle size={16} />}
          color="gray"
          variant="light"
          title={t('managedTitle')}
        >
          {t('managedBodyShort', { role: role ?? '' })}
        </Alert>
      </>
    );
  }

  return <ProviderDetail id={id} scope="org" breadcrumbs={breadcrumbs} listHref="/org/inference-providers" />;
}
