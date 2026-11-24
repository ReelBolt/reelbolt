'use client';

import { Group, Title, Breadcrumbs, Anchor, Stack } from '@mantine/core';
import Link from 'next/link';

interface Crumb {
  label: string;
  href?: string;
}

interface PageHeaderProps {
  title: string;
  breadcrumbs?: Crumb[];
  children?: React.ReactNode;
}

export function PageHeader({ title, breadcrumbs, children }: PageHeaderProps) {
  return (
    <Stack gap="xs" mb="lg">
      {breadcrumbs && breadcrumbs.length > 0 && (
        // A deep trail of long project/workflow names is wider than a phone viewport; scroll the
        // trail itself rather than letting it push the whole page into a horizontal scroll.
        <Breadcrumbs style={{ overflowX: 'auto', maxWidth: '100%' }}>
          {breadcrumbs.map((crumb, i) =>
            crumb.href ? (
              <Anchor component={Link} href={crumb.href} key={i} size="sm">
                {crumb.label}
              </Anchor>
            ) : (
              <span key={i} style={{ fontSize: 'var(--mantine-font-size-sm)' }}>
                {crumb.label}
              </span>
            ),
          )}
        </Breadcrumbs>
      )}
      <Group justify="space-between" align="center" wrap="wrap" gap="sm">
        {/* `minWidth: 0` lets a long, unbroken workflow name wrap instead of overflowing the
            flex row, which on a phone would otherwise scroll the entire page sideways. */}
        <Title order={1} size="h2" style={{ minWidth: 0, wordBreak: 'break-word' }}>{title}</Title>
        {children && <Group gap="sm">{children}</Group>}
      </Group>
    </Stack>
  );
}
