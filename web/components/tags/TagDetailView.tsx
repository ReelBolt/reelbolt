'use client';

import { useState } from 'react';
import Link from 'next/link';
import { Alert, Anchor, Button, Group, SegmentedControl, Skeleton, Stack, Table, Text, Title } from '@mantine/core';
import { PageHeader } from '@/components/shared/PageHeader';
import { StatusBadge } from '@/components/projects/StatusBadge';
import { useCreditAmount } from '@/components/billing/useCreditAmount';
import { useTagDetailActivity } from '@/lib/hooks/use-tag-activity';
import type { ActivityDays, TagActivityRun } from '@/lib/types/project-organization';
import { useFormat, useT } from '@/i18n';
import { TagChip } from '@/components/projects/TagChip';

/** Link to the run page; fall back to the project when no workflow id is present. */
function runHref(r: TagActivityRun): string {
  const wf = r.workflowId;
  return wf ? `/projects/${encodeURIComponent(r.projectId)}/workflows/${encodeURIComponent(wf)}/executions/${encodeURIComponent(r.executionId)}` : `/projects/${encodeURIComponent(r.projectId)}`;
}

export function TagDetailView({ tagId }: { tagId: string }) {
  const t = useT('tags');
  const f = useFormat();
  const credits = useCreditAmount();
  const [days, setDays] = useState<ActivityDays>(30);
  const { data, error, isLoading, mutate } = useTagDetailActivity(tagId, days);

  const body = (() => {
    if (error) {
      return (
        <Alert color="red" role="alert">
          <Text size="sm">{t('error')}</Text>
          <Button mt="xs" size="xs" variant="default" onClick={() => void mutate()}>{t('retry')}</Button>
        </Alert>
      );
    }
    if (isLoading || !data) return <Skeleton height={200} aria-busy="true" />;
    const showCredits = data.billingEnabled;
    const tt = data.totals;
    return (
      <Stack gap="lg">
        <Group gap="lg" data-testid="tag-totals">
          <Text size="sm">{t('colProjects')}: {f.number(tt.projects)}</Text>
          <Text size="sm">{t('colRuns')}: {f.number(tt.runs)}</Text>
          <Text size="sm">{t('colPassed')}: {f.number(tt.passed)}</Text>
          <Text size="sm">{t('colFailed')}: {f.number(tt.failed)}</Text>
          {showCredits ? <Text size="sm">{t('colCredits')}: {credits(tt.credits ?? 0)}</Text> : null}
        </Group>
        <Stack gap="xs">
          <Title order={3} size="h4">{t('projectsHeading')}</Title>
          {data.projects.length === 0 ? <Text size="sm" c="dimmed">{t('noProjects')}</Text> : (
            <Table.ScrollContainer minWidth={360}>
              <Table striped withTableBorder>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th scope="col">{t('colProject')}</Table.Th>
                    <Table.Th scope="col">{t('colRuns')}</Table.Th>
                    {showCredits ? <Table.Th scope="col">{t('colCredits')}</Table.Th> : null}
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {data.projects.map((p) => (
                    <Table.Tr key={p.projectId}>
                      <Table.Th scope="row"><Anchor component={Link} href={`/projects/${encodeURIComponent(p.projectId)}`}>{p.name}</Anchor></Table.Th>
                      <Table.Td>{f.number(p.runs)}</Table.Td>
                      {showCredits ? <Table.Td>{credits(p.credits ?? 0)}</Table.Td> : null}
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>
          )}
        </Stack>
        <Stack gap="xs">
          <Title order={3} size="h4">{t('recentRunsHeading')}</Title>
          {data.recentRuns.length === 0 ? <Text size="sm" c="dimmed">{t('noRuns')}</Text> : (
            <Table.ScrollContainer minWidth={520}>
              <Table striped withTableBorder>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th scope="col">{t('colProject')}</Table.Th>
                    <Table.Th scope="col">{t('colWorkflow')}</Table.Th>
                    <Table.Th scope="col">{t('colStatus')}</Table.Th>
                    <Table.Th scope="col">{t('colStarted')}</Table.Th>
                    {showCredits ? <Table.Th scope="col">{t('colCredits')}</Table.Th> : null}
                    <Table.Th scope="col">{t('colRun')}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {data.recentRuns.map((r) => (
                    <Table.Tr key={r.executionId}>
                      <Table.Td>{r.projectName}</Table.Td>
                      <Table.Td>{r.workflowName}</Table.Td>
                      <Table.Td><StatusBadge status={r.status} /></Table.Td>
                      <Table.Td>{r.startedAt ? f.date(r.startedAt, 'dateTime') : '–'}</Table.Td>
                      {showCredits ? <Table.Td>{credits(r.credits ?? 0)}</Table.Td> : null}
                      <Table.Td>
                        <Anchor component={Link} href={runHref(r)}>{t('openRun')}</Anchor>
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>
          )}
        </Stack>
      </Stack>
    );
  })();

  return (
    <Stack gap="md">
      <PageHeader title={data ? t('detailTitle', { name: data.tag.name }) : t('title')} breadcrumbs={[{ label: t('title'), href: '/tags' }]} />
      {data ? <TagChip name={data.tag.name} color={data.tag.color} /> : null}
      <SegmentedControl
        aria-label={t('window')}
        value={String(days)}
        onChange={(v) => setDays(Number(v) as ActivityDays)}
        data={[{ value: '7', label: t('days7') }, { value: '30', label: t('days30') }, { value: '90', label: t('days90') }]}
      />
      {body}
    </Stack>
  );
}

export default TagDetailView;
