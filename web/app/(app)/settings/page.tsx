'use client';

import { Suspense, useSyncExternalStore } from 'react';
import { Anchor, Badge, Group, SimpleGrid, Stack, Tabs, Text, Title } from '@mantine/core';
import Link from 'next/link';
import { PageHeader } from '@/components/shared/PageHeader';
import { LanguageSelect } from '@/components/shared/LanguageSelect';
import { ChangePasswordForm } from '@/components/auth/ChangePasswordForm';
import { CloudAccountsCard } from '@/components/cloud-storage/CloudAccountsCard';
import { AppearanceCard } from '@/components/settings/AppearanceCard';
import { DataExportCard } from '@/components/settings/DataExportCard';
import { DeleteAccountCard } from '@/components/settings/DeleteAccountCard';
import { GitHubAccountCard } from '@/components/settings/GitHubAccountCard';
import { MarketingEmailsCard } from '@/components/settings/MarketingEmailsCard';
import { McpAdminToolsCard } from '@/components/settings/McpAdminToolsCard';
import { PendingEmailChangeNotice } from '@/components/settings/PendingEmailChangeNotice';
import { McpMediaEgressCard } from '@/components/settings/McpMediaEgressCard';
import { DisplayNameForm } from '@/components/settings/DisplayNameForm';
import { SettingsCard } from '@/components/settings/SettingsCard';
import { StorageSettingsCard } from '@/components/settings/StorageSettingsCard';
import { useAuth } from '@/lib/hooks/use-auth';
import { orgDisplayLabel } from '@/lib/org-label';
import { canViewMembers } from '@/lib/org-permissions';
import { useT } from '@/i18n';

/**
 * Account settings -- the user's own page, and the only place in the dashboard that is about YOU
 * rather than about a project or the active workspace.
 *
 * It exists because the left navigation had grown into a flat wall of links with no home for
 * anything account-shaped: everything under /settings/* was workspace billing and metering. The
 * settings group in the navigation (shell/NavLinks) is the same idea seen from the menu -- Account,
 * Usage, then Plan &amp; billing, in that order, because that is the order of the thing each one acts
 * on: you, then your workspace's spend, then your workspace's plan.
 *
 * One identity rule runs through the whole page, and it comes from useAuth: every value shown here is
 * read from the readable 'reelbolt_user' cookie, which the Go API re-issues on sign-in, on a
 * workspace switch and on a password change. So nothing on this page needs its own fetch, role
 * display included -- and the workspace line follows a switch without a refetch, exactly as the
 * account menu does.
 *
 * What this page deliberately does NOT do: offer a role change or a workspace rename. Those act on the
 * workspace and live where their authority is checked (Workspace -> Members and Workspace ->
 * Settings), and duplicating them here would put two controls on one fact with different rules.
 */

/** One label/value line. Values are right-aligned so a long address wraps instead of overflowing. */
function Fact({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <Group justify="space-between" gap="md" wrap="nowrap" align="flex-start">
      <Text size="sm" c="dimmed">
        {label}
      </Text>
      <div style={{ textAlign: 'right', minWidth: 0 }}>{children}</div>
    </Group>
  );
}

const TABS = [
  { value: 'profile' },
  { value: 'security' },
  { value: 'notifications' },
  { value: 'data' },
] as const;
type TabValue = (typeof TABS)[number]['value'];

function isTab(value: string | null | undefined): value is TabValue {
  return TABS.some((t) => t.value === value);
}

function subscribeHash(onChange: () => void): () => void {
  window.addEventListener('hashchange', onChange);
  return () => window.removeEventListener('hashchange', onChange);
}
const readHash = (): string => window.location.hash.replace('#', '');

export default function AccountSettingsPage() {
  const tr = useT();
  const t = useT('settings.page');
  const tabLabels = {
    profile: t('tabs.profile'),
    security: t('tabs.security'),
    notifications: t('tabs.notifications'),
    data: t('tabs.data'),
  };
  const { user, isPlatformAdmin, isOrgAdmin } = useAuth();
  // The tab lives in the URL hash (#security) so it can be linked to and survives a reload.
  const hash = useSyncExternalStore(subscribeHash, readHash, () => '');
  const tab: TabValue = isTab(hash) ? hash : 'profile';
  const selectTab = (next: string | null) => {
    if (!isTab(next)) return;
    window.history.replaceState(null, '', '#' + next);
    // replaceState raises no event, so tell the subscribers ourselves.
    window.dispatchEvent(new HashChangeEvent('hashchange'));
  };
  // Admin tools over MCP only exist for someone with administrative authority.
  const canUseAdminTools = isPlatformAdmin || isOrgAdmin;

  // The Personal workspace always reads "Personal" whatever its stored name is (decision D1).
  const workspace = user ? orgDisplayLabel({ name: user.orgName ?? '', kind: user.orgKind }, tr) : null;
  // A Billing member may not list members at all (403), so the link is offered only to a role whose
  // request would be answered -- the same rule the page behind it enforces on the server.
  const showMembersLink = canViewMembers(user?.orgRole);

  return (
    <>
      <PageHeader
        title={t('title')}
        breadcrumbs={[{ label: t('breadcrumbDashboard'), href: '/dashboard' }, { label: t('title') }]}
      />

      <Stack gap="lg" maw={860}>
        <Text size="sm" c="dimmed">
          {t('intro')}
        </Text>

        <Tabs value={tab} onChange={(next) => selectTab(next)} keepMounted={false} variant="outline">
          <Tabs.List mb="lg" style={{ overflowX: 'auto', flexWrap: 'nowrap' }}>
            {TABS.map((tab) => (
              <Tabs.Tab key={tab.value} value={tab.value} style={{ minHeight: 44 }}>
                {tabLabels[tab.value]}
              </Tabs.Tab>
            ))}
          </Tabs.List>

          <Tabs.Panel value="profile">
            <Stack gap="lg">
              <SettingsCard
                title={t('profile.title')}
                description={t('profile.description')}
                testId="profile-card"
              >
                <DisplayNameForm currentName={user?.displayName ?? ''} />
                <LanguageSelect />
                <Stack gap="sm">
                  <Fact label={t('profile.email')}>
                    <Text size="sm" style={{ wordBreak: 'break-word' }}>
                      {user?.email ?? '--'}
                    </Text>
                  </Fact>
                  <PendingEmailChangeNotice />
                  <Fact label={t('profile.emailConfirmed')}>
                    {user?.emailVerified === false ? (
                      <Badge variant="light" color="yellow">
                        {t('profile.notConfirmed')}
                      </Badge>
                    ) : (
                      <Badge variant="light" color="green">
                        {t('profile.confirmed')}
                      </Badge>
                    )}
                  </Fact>
                  <Fact label={t('profile.workspace')}>
                    <Text size="sm">
                      {workspace ?? '--'}
                      {user?.orgRole ? ' · ' + user.orgRole : ''}
                    </Text>
                  </Fact>
                  {isPlatformAdmin && (
                    <Fact label={t('profile.platformAdmin')}>
                      <Badge variant="light" color="violet">
                        {t('profile.yes')}
                      </Badge>
                    </Fact>
                  )}
                </Stack>
                {showMembersLink && (
                  <Text size="xs" c="dimmed">
                    {t.rich('profile.roleHint', {
                      link: (chunks) => (
                        <Anchor component={Link} href="/org/members" size="xs">
                          {chunks}
                        </Anchor>
                      ),
                    })}
                  </Text>
                )}
              </SettingsCard>

              <SimpleGrid cols={{ base: 1, lg: 2 }} spacing="lg">
                <AppearanceCard />
                {/* Renders nothing when this server cannot connect GitHub. */}
                <GitHubAccountCard />
              </SimpleGrid>

              {/* The workspace's storage, in the same words as the Billing page. Renders nothing when the
                  endpoint cannot answer. */}
              <StorageSettingsCard />

              {/* Renders nothing when this server has no cloud storage set up and nothing is connected.
                  It reads the `?cloud=` the account-connection callback returns with, hence the
                  Suspense boundary `useSearchParams` needs. */}
              <Suspense fallback={null}>
                <CloudAccountsCard />
              </Suspense>

              <div>
                <Title order={3}>{t('workspace.title')}</Title>
                <Text size="sm" c="dimmed" mt={4}>
                  {t.rich('workspace.body', {
                    usage: (chunks) => (
                      <Anchor component={Link} href="/settings/usage" size="sm">
                        {chunks}
                      </Anchor>
                    ),
                    billing: (chunks) => (
                      <Anchor component={Link} href="/settings/billing" size="sm">
                        {chunks}
                      </Anchor>
                    ),
                  })}
                </Text>
              </div>
            </Stack>
          </Tabs.Panel>

          <Tabs.Panel value="security">
            <Stack gap="lg">
              <SettingsCard
                title={t('security.passwordTitle')}
                description={t('security.passwordDescription')}
                testId="password-card"
              >
                <ChangePasswordForm redirectTo="/settings#security" />
              </SettingsCard>

              {/* There is no page of personal API keys: say so, and say how tools connect instead. */}
              <SettingsCard
                title={t('security.toolsTitle')}
                description={t('security.toolsDescription')}
                testId="connect-tools-card"
              >
                <Text size="sm">
                  {t('security.toolsBody')}
                </Text>
              </SettingsCard>

              {/* The administrative MCP tools only exist for someone with that authority. */}
              <McpAdminToolsCard offOnly={!canUseAdminTools} />

              {/* Connected tools may allow sending media for a run only after the person switches this on. */}
              <McpMediaEgressCard />
            </Stack>
          </Tabs.Panel>

          <Tabs.Panel value="notifications">
            <Stack gap="lg">
              {/* Your own consent to marketing emails, never the workspace's. Off in one click. */}
              <MarketingEmailsCard />
              <Text size="sm" c="dimmed">
                {t.rich('notificationsNote', {
                  link: (chunks) => (
                    <Anchor component={Link} href="/settings/notifications" size="sm">
                      {chunks}
                    </Anchor>
                  ),
                })}
              </Text>
            </Stack>
          </Tabs.Panel>

          <Tabs.Panel value="data">
            {/* Last, because the second card is the one irreversible action here. Both act on you and
                your personal workspace, never on a team you are in. */}
            <Stack gap="lg">
              <div>
                <Title order={3}>{t('data.title')}</Title>
                <Text size="sm" c="dimmed" mt={4}>
                  {t('data.intro')}
                </Text>
              </div>
              <SimpleGrid cols={{ base: 1, lg: 2 }} spacing="lg">
                <DataExportCard />
                <DeleteAccountCard />
              </SimpleGrid>
            </Stack>
          </Tabs.Panel>
        </Tabs>
      </Stack>
    </>
  );
}
