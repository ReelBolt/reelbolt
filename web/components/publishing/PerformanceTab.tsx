'use client';

import { Alert, Anchor, Card, Group, Loader, SimpleGrid, Stack, Text, Title } from '@mantine/core';
import { IconAlertTriangle } from '@tabler/icons-react';
import { EmptyState } from '@/components/shared/EmptyState';
import { useFormat, useT } from '@/i18n';
import { useSocialMetricsSummary } from '@/lib/hooks/use-social';
import type { SocialMetricTotals } from '@/lib/types/social';
import { BarChart } from './BarChart';

const DAYS = 30;

/** Totals and per-post counts for the last 30 days. A number the platform does not give reads "not available", never 0. */
export function PerformanceTab() {
  const t = useT('publishing.performance');
  const f = useFormat();
  const { data, error, isLoading } = useSocialMetricsSummary(DAYS);

  if (isLoading) return <Group gap="xs"><Loader size="xs" /><Text size="sm" c="dimmed">{t('loading')}</Text></Group>;
  if (error || !data) return <Alert color="red" icon={<IconAlertTriangle size={16} />} role="alert">{t('loadFailed')}</Alert>;
  if (data.publishedPosts === 0) return <EmptyState title={t('emptyTitle')} description={t('empty', { days: DAYS })} />;

  const na = t('notAvailable');
  const count = (n: number) => f.number(n);
  const show = (n: number | null) => (n === null ? na : count(n));
  const totals = (x: SocialMetricTotals) => [
    { id: 'views', label: t('views'), value: x.views },
    { id: 'likes', label: t('likes'), value: x.likes },
    { id: 'comments', label: t('comments'), value: x.comments },
    { id: 'shares', label: t('shares'), value: x.shares },
    { id: 'saves', label: t('saves'), value: x.saves },
  ];

  return (
    <Stack gap="lg" data-testid="publishing-performance">
      <Text size="sm" c="dimmed">{t('period', { days: data.days, count: data.publishedPosts })}</Text>

      <SimpleGrid cols={{ base: 2, sm: 5 }}>
        {totals(data.totals).map((m) => (
          <Card key={m.id} withBorder padding="sm" data-testid={`total-${m.id}`}>
            <Text size="xs" c="dimmed">{m.label}</Text>
            <Text fw={700} size="lg">{show(m.value)}</Text>
          </Card>
        ))}
      </SimpleGrid>

      {data.platforms.length > 0 && (
        <Stack gap="xs">
          <Title order={4}>{t('byPlatform')}</Title>
          <BarChart
            ariaLabel={t('byPlatformChart')} unavailable={na} format={count}
            data={data.platforms.map((p) => ({ label: p.platform, value: p.totals.views }))}
          />
        </Stack>
      )}

      <Stack gap="xs">
        <Title order={4}>{t('byPost')}</Title>
        <BarChart
          ariaLabel={t('byPostChart')} unavailable={na} format={count}
          data={data.posts.slice(0, 10).map((p) => ({ label: p.title || p.platform, value: p.latest?.views ?? null }))}
        />
        {data.posts.map((p) => (
          <Card key={p.postId} withBorder padding="sm" data-testid={`metrics-${p.postId}`}>
            <Group justify="space-between" wrap="wrap">
              <div>
                <Text fw={500} lineClamp={1}>{p.title || p.platform}</Text>
                <Text size="xs" c="dimmed">{p.platform}{p.publishedAt ? ` · ${f.date(p.publishedAt, 'short')}` : ''}</Text>
              </div>
              {p.remotePostUrl && <Anchor size="sm" href={p.remotePostUrl} target="_blank" rel="noopener noreferrer">{t('open')}</Anchor>}
            </Group>
            {p.latest ? (
              <>
                <SimpleGrid cols={{ base: 3, sm: 5 }} mt="xs">
                  {totals(p.latest).map((m) => (
                    <div key={m.id}>
                      <Text size="xs" c="dimmed">{m.label}</Text>
                      <Text size="sm">{show(m.value)}</Text>
                    </div>
                  ))}
                </SimpleGrid>
                <Text size="xs" c="dimmed" mt="xs">{t('asOf', { when: f.date(p.latest.capturedAt, 'dateTime') })}</Text>
              </>
            ) : <Text size="sm" c="dimmed" mt="xs">{t('noReading')}</Text>}
          </Card>
        ))}
      </Stack>
    </Stack>
  );
}
