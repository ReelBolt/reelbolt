'use client';

import { useState } from 'react';
import Link from 'next/link';
import { Alert, Anchor, Button, Skeleton, Stack, Table, Text, SegmentedControl } from '@mantine/core';
import { PageHeader } from '@/components/shared/PageHeader';
import { EmptyState } from '@/components/shared/EmptyState';
import { useCreditAmount } from '@/components/billing/useCreditAmount';
import { useTagActivity } from '@/lib/hooks/use-tag-activity';
import type { ActivityDays } from '@/lib/types/project-organization';
import { useFormat, useT } from '@/i18n';
import { TagChip } from '@/components/projects/TagChip';

export function TagActivityView() {
  const t = useT('tags');
  const f = useFormat();
  const credits = useCreditAmount();
  const [days, setDays] = useState<ActivityDays>(30);
  const { data, error, isLoading, mutate } = useTagActivity(days);
  const num = (n: number) => f.number(n);

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
    if (data.tags.length === 0) {
      return (
        <EmptyState title={t('emptyTitle')} description={t('emptyBody')}>
          <Button component={Link} href="/projects" variant="default">{t('emptyLink')}</Button>
        </EmptyState>
      );
    }
    const showCredits = data.billingEnabled;
    const doubleCounted = data.tags.reduce((sum, r) => sum + r.projects, 0) > data.workspace.projects - data.untagged.projects;
    return (
      <Stack gap="sm">
        <Table.ScrollContainer minWidth={520}>
          <Table striped withTableBorder>
            <Table.Thead>
              <Table.Tr>
                <Table.Th scope="col">{t('colTag')}</Table.Th>
                <Table.Th scope="col">{t('colProjects')}</Table.Th>
                <Table.Th scope="col">{t('colRuns')}</Table.Th>
                <Table.Th scope="col">{t('colPassed')}</Table.Th>
                <Table.Th scope="col">{t('colFailed')}</Table.Th>
                {showCredits ? <Table.Th scope="col">{t('colCredits')}</Table.Th> : null}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {data.tags.map((r) => (
                <Table.Tr key={r.tagId}>
                  <Table.Th scope="row">
                    <Anchor component={Link} href={`/tags/${encodeURIComponent(r.tagId)}`} underline="never"><TagChip name={r.name} color={r.color} /></Anchor>
                  </Table.Th>
                  <Table.Td>{num(r.projects)}</Table.Td>
                  <Table.Td>{num(r.runs)}</Table.Td>
                  <Table.Td>{num(r.passed)}</Table.Td>
                  <Table.Td>{num(r.failed)}</Table.Td>
                  {showCredits ? <Table.Td>{credits(r.credits ?? 0)}</Table.Td> : null}
                </Table.Tr>
              ))}
              <Table.Tr>
                <Table.Th scope="row">{t('untagged')}</Table.Th>
                <Table.Td>{num(data.untagged.projects)}</Table.Td>
                <Table.Td>{num(data.untagged.runs)}</Table.Td>
                <Table.Td>–</Table.Td>
                <Table.Td>–</Table.Td>
                {showCredits ? <Table.Td>{credits(data.untagged.credits ?? 0)}</Table.Td> : null}
              </Table.Tr>
            </Table.Tbody>
            <Table.Tfoot>
              <Table.Tr>
                <Table.Th scope="row">{t('workspaceTotal')}</Table.Th>
                <Table.Td>{num(data.workspace.projects)}</Table.Td>
                <Table.Td>{num(data.workspace.runs)}</Table.Td>
                <Table.Td>–</Table.Td>
                <Table.Td>–</Table.Td>
                {showCredits ? <Table.Td>{credits(data.workspace.credits ?? 0)}</Table.Td> : null}
              </Table.Tr>
            </Table.Tfoot>
          </Table>
        </Table.ScrollContainer>
        {doubleCounted ? <Text size="sm" c="dimmed" data-testid="tags-double-count-note">{t('doubleCountNote')}</Text> : null}
      </Stack>
    );
  })();

  return (
    <Stack gap="md">
      <PageHeader title={t('title')} />
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

export default TagActivityView;
