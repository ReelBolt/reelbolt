'use client';

import { Stack } from '@mantine/core';
import { PageHeader } from '@/components/shared/PageHeader';
import { LibraryManager } from '@/components/avatars/library/LibraryManager';
import { useT } from '@/i18n';

/**
 * Settings: the active workspace's avatar library (docs/design/avatar-objects.md "The workspace avatar
 * library"). Every member may read it; an Owner or Admin manages the items. Items are added from a project's
 * Avatars tab ("Save to the workspace library"), never here: the files come from a project.
 */
export default function AvatarLibraryPage() {
  const t = useT('avatars.library.page');
  return (
    <Stack gap="lg" w="100%">
      <PageHeader
        title={t('title')}
        breadcrumbs={[{ label: t('breadcrumbSettings'), href: '/settings' }, { label: t('title') }]}
      />
      <LibraryManager />
    </Stack>
  );
}
