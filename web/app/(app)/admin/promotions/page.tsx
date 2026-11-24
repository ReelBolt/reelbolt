'use client';

import { useState } from 'react';
import { Alert, Button, Center, Loader, Stack, Tabs, Text, TextInput } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { IconCoin, IconDiscount, IconGift, IconMail, IconPlus, IconSearch } from '@tabler/icons-react';
import { PageHeader } from '@/components/shared/PageHeader';
import { EmptyState } from '@/components/shared/EmptyState';
import { CreatePromotionModal } from '@/components/admin/promotions/CreatePromotionModal';
import { EditPromotionModal } from '@/components/admin/promotions/EditPromotionModal';
import { PromotionsTable } from '@/components/admin/promotions/PromotionsTable';
import { RedemptionsDrawer } from '@/components/admin/promotions/RedemptionsDrawer';
import { OrgCreditsCard } from '@/components/admin/credits/OrgCreditsCard';
import { OrgPlanGrantsCard } from '@/components/admin/credits/OrgPlanGrantsCard';
import { MarketingContactsPanel } from '@/components/admin/marketing/MarketingContactsPanel';
import { MarketingListPanel } from '@/components/admin/marketing/MarketingListPanel';
import { ApiError } from '@/lib/api/client';
import {
  promotionAdminMessage,
  updatePromotion,
  type Promotion,
  type PromotionStatus,
} from '@/lib/api/promotions';
import { useAuth } from '@/lib/hooks/use-auth';
import { useBillingSummary } from '@/lib/hooks/use-billing';
import { useAdminOrgSearch, useAdminPromotions } from '@/lib/hooks/use-promotions';

const BREADCRUMBS = [{ label: 'Admin', href: '/admin' }, { label: 'Promotions' }];

/**
 * Platform-admin view of promotions: promo codes, automatic discounts and pack bonuses, the manual
 * credit grant, and the marketing email list. Every promotion route is billing-gated (404 when
 * billing is off); the marketing list is not, so its tab stays usable on such a deployment.
 */
export default function AdminPromotionsPage() {
  const { isPlatformAdmin } = useAuth();
  const promotions = useAdminPromotions(isPlatformAdmin);
  // The plans and packs on sale, for targeting. Read from the summary, which lists what can be sold.
  const summary = useBillingSummary();
  const [search, setSearch] = useState('');
  const [debouncedSearch] = useDebouncedValue(search, 300);
  const orgs = useAdminOrgSearch(debouncedSearch, isPlatformAdmin);

  const [createOpened, setCreateOpened] = useState(false);
  const [editing, setEditing] = useState<Promotion | null>(null);
  const [viewing, setViewing] = useState<Promotion | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [tab, setTab] = useState<string | null>('promotions');

  if (!isPlatformAdmin) {
    return (
      <>
        <PageHeader title="Promotions" breadcrumbs={BREADCRUMBS} />
        <Alert color="red" variant="light" title="Platform administrators only">
          Promotions and credit grants are managed by platform administrators. The API answers 403 for
          anyone else.
        </Alert>
      </>
    );
  }

  const billingOff = promotions.error instanceof ApiError && promotions.error.status === 404;

  async function changeStatus(promotion: Promotion, status: PromotionStatus) {
    setBusyId(promotion.id);
    try {
      await updatePromotion(promotion.id, { status });
      await promotions.mutate();
      notifications.show({ title: 'Saved', message: `${promotion.name} is now ${status}.`, color: 'green' });
    } catch (error) {
      notifications.show({ title: 'Not saved', message: promotionAdminMessage(error), color: 'red' });
    } finally {
      setBusyId(null);
    }
  }

  const planOptions = (summary.data?.plans ?? []).map((p) => ({ value: p.key, label: p.name }));
  const packOptions = summary.data?.topUp.packs ?? [];

  return (
    <>
      <PageHeader title="Promotions" breadcrumbs={BREADCRUMBS}>
        <Button
          leftSection={<IconPlus size={16} />}
          onClick={() => setCreateOpened(true)}
          disabled={billingOff}
          data-testid="create-promotion-button"
        >
          New promotion
        </Button>
      </PageHeader>

      {billingOff && (
        <Alert color="blue" variant="light" title="Billing is not enabled on this deployment" mb="md">
          Promotions and credit grants need billing. Every promotion route answers 404 here. The
          marketing list does not depend on billing.
        </Alert>
      )}
      {/* With billing off only the marketing list answers, so it is the tab shown. */}
      <Tabs value={billingOff ? 'marketing' : tab} onChange={setTab} keepMounted={false}>
        <Tabs.List mb="md">
          <Tabs.Tab value="promotions" leftSection={<IconDiscount size={16} />} disabled={billingOff}>Promotions</Tabs.Tab>
          <Tabs.Tab value="grants" leftSection={<IconCoin size={16} />} disabled={billingOff}>Grant credits</Tabs.Tab>
          <Tabs.Tab value="plans" leftSection={<IconGift size={16} />} disabled={billingOff} data-testid="grant-plans-tab">Grant plans</Tabs.Tab>
          <Tabs.Tab value="marketing" leftSection={<IconMail size={16} />} data-testid="marketing-list-tab">Marketing list</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="promotions">
          {billingOff ? null : promotions.error ? (
            <Alert color="red" variant="light" title="Could not load promotions">
              {promotionAdminMessage(promotions.error)}
            </Alert>
          ) : !promotions.data ? (
            <Center h={200}><Loader /></Center>
          ) : promotions.data.length === 0 ? (
            <EmptyState
              title="No promotions yet"
              description="Create a credit code, a plan or pack discount, or a pack bonus."
            />
          ) : (
            <PromotionsTable
              promotions={promotions.data}
              busyId={busyId}
              onStatusChange={(p, status) => { void changeStatus(p, status); }}
              onEdit={setEditing}
              onShowRedemptions={setViewing}
            />
          )}
        </Tabs.Panel>

        <Tabs.Panel value="grants">
          {billingOff ? null : (
            <Stack gap="md">
              <Text size="sm" c="dimmed">
                Grant promo credits to an organization, with an expiry and a reason. Each grant is
                recorded with who made it.
              </Text>
              <OrgCreditsCard
                title="Organizations"
                description="Search by organization name or owner email. Click a row to see its recent grants."
                orgs={orgs.data}
                error={orgs.error}
                isLoading={orgs.isLoading}
                onChanged={() => { void orgs.mutate(); }}
                toolbar={
                  <TextInput
                    leftSection={<IconSearch size={16} />}
                    placeholder="Search organizations"
                    value={search}
                    onChange={(e) => setSearch(e.currentTarget.value)}
                    aria-label="Search organizations"
                    data-testid="org-search-input"
                  />
                }
              />
            </Stack>
          )}
        </Tabs.Panel>

        <Tabs.Panel value="plans">
          {billingOff ? null : (
            <Stack gap="md">
              <Text size="sm" c="dimmed">
                Gift a paid plan to an organization, with a stated period and a reason. The grant is a
                subscription with no payment provider: it is not revenue, and it ends at the end of its
                period unless someone grants it again.
              </Text>
              <OrgPlanGrantsCard
                title="Organizations"
                description="Search by organization name or owner email. Click a row to see its manual plan grants and end one."
                orgs={orgs.data}
                error={orgs.error}
                isLoading={orgs.isLoading}
                onChanged={() => { void orgs.mutate(); }}
                toolbar={
                  <TextInput
                    leftSection={<IconSearch size={16} />}
                    placeholder="Search organizations"
                    value={search}
                    onChange={(e) => setSearch(e.currentTarget.value)}
                    aria-label="Search organizations"
                    data-testid="org-plan-search-input"
                  />
                }
              />
            </Stack>
          )}
        </Tabs.Panel>

        <Tabs.Panel value="marketing">
          <Stack gap="xl">
            <MarketingListPanel />
            <MarketingContactsPanel />
          </Stack>
        </Tabs.Panel>
      </Tabs>

      <CreatePromotionModal
        opened={createOpened}
        onClose={() => setCreateOpened(false)}
        onCreated={(created) => {
          notifications.show({ title: 'Promotion created', message: created.name, color: 'green' });
          void promotions.mutate();
        }}
        planOptions={planOptions}
        packOptions={packOptions}
      />
      {editing && (
        <EditPromotionModal
          key={editing.id}
          promotion={editing}
          onClose={() => setEditing(null)}
          onSaved={() => { void promotions.mutate(); }}
        />
      )}
      <RedemptionsDrawer promotion={viewing} onClose={() => setViewing(null)} />
    </>
  );
}
