'use client';

import { Alert } from '@mantine/core';
import { PageHeader } from '@/components/shared/PageHeader';
import { CampaignsPanel } from '@/components/admin/campaigns/CampaignsPanel';
import { useAuth } from '@/lib/hooks/use-auth';

const BREADCRUMBS = [{ label: 'Admin', href: '/admin' }, { label: 'Campaigns' }];

/** Platform-admin marketing campaigns: history, compose, dry run, test send and send. */
export default function AdminCampaignsPage() {
  const { isPlatformAdmin } = useAuth();
  return (
    <>
      <PageHeader title="Campaigns" breadcrumbs={BREADCRUMBS} />
      {isPlatformAdmin ? (
        <CampaignsPanel />
      ) : (
        <Alert color="red" variant="light" title="Platform administrators only">
          Campaigns are managed by platform administrators. The API answers 403 for anyone else.
        </Alert>
      )}
    </>
  );
}
