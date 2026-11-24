'use client';

import { Suspense } from 'react';
import { Tabs } from '@mantine/core';
import { PageHeader } from '@/components/shared/PageHeader';
import { AccountsTab } from '@/components/publishing/AccountsTab';
import { PerformanceTab } from '@/components/publishing/PerformanceTab';
import { QueueTab } from '@/components/publishing/QueueTab';
import { useT } from '@/i18n';

/** Publishing: connected accounts, the queue and calendar, and how published posts are doing. */
export default function PublishingPage() {
  const t = useT('publishing.page');
  return (
    <>
      <PageHeader title={t('title')} />
      <Tabs defaultValue="accounts" keepMounted={false}>
        <Tabs.List style={{ flexWrap: 'nowrap', overflowX: 'auto' }}>
          <Tabs.Tab value="accounts" style={{ minHeight: 44 }}>{t('tabAccounts')}</Tabs.Tab>
          <Tabs.Tab value="queue" style={{ minHeight: 44 }}>{t('tabQueue')}</Tabs.Tab>
          <Tabs.Tab value="performance" style={{ minHeight: 44 }}>{t('tabPerformance')}</Tabs.Tab>
        </Tabs.List>
        <Tabs.Panel value="accounts" pt="md"><Suspense fallback={null}><AccountsTab /></Suspense></Tabs.Panel>
        <Tabs.Panel value="queue" pt="md"><QueueTab /></Tabs.Panel>
        <Tabs.Panel value="performance" pt="md"><PerformanceTab /></Tabs.Panel>
      </Tabs>
    </>
  );
}
