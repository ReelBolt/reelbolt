'use client';

import Link from 'next/link';
import { Button } from '@mantine/core';
import { IconChartBar } from '@tabler/icons-react';
import { useT } from '@/i18n';

/** A small button on the projects page header that opens the per-tag activity view. */
export function TagActivityLink() {
  const t = useT('tags');
  return (
    <Button component={Link} href="/tags" variant="default" style={{ minHeight: 44 }} leftSection={<IconChartBar size={16} aria-hidden />}>
      {t('link')}
    </Button>
  );
}

export default TagActivityLink;
