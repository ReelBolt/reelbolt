'use client';

import type { ReactNode } from 'react';
import { Button, Group, Skeleton, Stack, Text } from '@mantine/core';
import { useSlow } from '@/lib/hooks/use-slow';
import { useT } from '@/i18n';
import { FailureCard } from './FailureCard';

export interface LoadStateProps {
  loading: boolean;
  error?: unknown;
  onRetry?: () => void;
  /** Shown while loading. Defaults to three plain bars. */
  skeleton?: ReactNode;
  backHref?: string;
  backLabel?: string;
  /** A plain noun for titles ("Project"). */
  resource?: string;
  children?: ReactNode;
}

function DefaultSkeleton() {
  return (
    <Stack gap="sm" data-testid="load-skeleton">
      <Skeleton height={24} width="40%" />
      <Skeleton height={16} />
      <Skeleton height={16} width="80%" />
    </Stack>
  );
}

/** loading -> skeleton (+ slow notice and Retry after 10 s); error -> a plain failure card; otherwise the children. */
export function LoadState({ loading, error, onRetry, skeleton, backHref, backLabel, resource, children }: LoadStateProps) {
  const t = useT('errors.state');
  const slow = useSlow(loading && !error);
  if (error) {
    return <FailureCard error={error} onRetry={onRetry} backHref={backHref} backLabel={backLabel} resource={resource} />;
  }
  if (loading) {
    return (
      <Stack gap="md">
        {skeleton ?? <DefaultSkeleton />}
        {slow ? (
          <Group gap="sm" role="status">
            <Text size="sm" c="dimmed">{t('slow')}</Text>
            {onRetry ? <Button variant="light" size="xs" onClick={onRetry}>{t('retry')}</Button> : null}
          </Group>
        ) : null}
      </Stack>
    );
  }
  return <>{children}</>;
}
