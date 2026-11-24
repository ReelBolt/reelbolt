'use client';

import { Alert } from '@mantine/core';
import { PageHeader } from '@/components/shared/PageHeader';
import { LinksPanel } from '@/components/admin/links/LinksPanel';
import { useT } from '@/i18n';
import { useAuth } from '@/lib/hooks/use-auth';

/**
 * Platform-admin tracked links: one short address per place a link is posted, with the funnel of the
 * people who came through it (docs/design/new-user-funnel.md). Nothing is requested for anyone who
 * is not a platform admin; the API answers 403 for them as well.
 */
export default function AdminLinksPage() {
  const t = useT('adminLinks');
  const { isPlatformAdmin } = useAuth();
  return (
    <>
      <PageHeader
        title={t('pageTitle')}
        breadcrumbs={[{ label: t('breadcrumbAdmin'), href: '/admin' }, { label: t('pageTitle') }]}
      />
      {isPlatformAdmin ? (
        <LinksPanel />
      ) : (
        <Alert color="red" variant="light" title={t('adminOnly.title')} data-testid="links-admin-only">
          {t('adminOnly.body')}
        </Alert>
      )}
    </>
  );
}
