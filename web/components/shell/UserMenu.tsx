'use client';

import { Menu, UnstyledButton, Group, Avatar, Stack, Text } from '@mantine/core';
import Link from 'next/link';
import { IconBook2, IconCreditCard, IconLogout, IconRoute, IconUser, IconUserCircle } from '@tabler/icons-react';
import { useLocale, useT } from '@/i18n';
import { docsHomeHref } from '@/lib/onboarding/guide-links';
import { useTourLauncher } from '@/components/onboarding/OnboardingProvider';
import { useAuth } from '@/lib/hooks/use-auth';
import { orgDisplayLabel } from '@/lib/org-label';
import { ThemeToggle } from './ThemeToggle';

/**
 * Account menu. Besides the sign-out action it is where the shell states WHICH workspace the
 * caller is working in and what they are in it — the org switcher only names the active
 * workspace, and the role decides what the Workspace nav group shows (A15-UI).
 *
 * Both values come from the readable `reelbolt_user` cookie, which switch-org re-issues
 * alongside the token, so the label follows a workspace switch without a refetch.
 */
export function UserMenu() {
  const { user, logout } = useAuth();
  const t = useT('shell.userMenu');
  const tm = useT('misc');
  const { startTour } = useTourLauncher();
  const tOnb = useT('onboarding.menu');
  const locale = useLocale();

  // The Personal workspace always reads "Personal" whatever its stored name is (decision D1).
  const workspace = user ? orgDisplayLabel({ name: user.orgName ?? '', kind: user.orgKind }) : null;
  // Role values are Go's OrgRoleOwner/Admin/Member/Billing, already display-ready.
  const workspaceLine = workspace ? (user?.orgRole ? `${workspace} · ${tm('role', { role: user.orgRole })}` : workspace) : null;

  return (
    <Group gap="xs" wrap="nowrap" style={{ flexShrink: 0 }}>
      <ThemeToggle />
      <Menu shadow="md" width={220} position="bottom-end">
        <Menu.Target>
          <UnstyledButton className="touch-icon" data-tour="account-menu" aria-label={user?.email ? t('labelWithEmail', { email: user.email }) : t('label')}>
            <Group gap="xs">
              <Avatar size="sm" radius="xl" color="violet">
                <IconUser size={16} />
              </Avatar>
              <Text size="sm" fw={500} visibleFrom="sm">
                {user?.email}
              </Text>
            </Group>
          </UnstyledButton>
        </Menu.Target>
        <Menu.Dropdown>
          <Menu.Label>
            <Stack gap={2}>
              <Text size="xs" fw={500} c="inherit" lineClamp={1}>
                {user?.email}
              </Text>
              {workspaceLine && (
                <Text size="xs" c="dimmed" lineClamp={1}>
                  {workspaceLine}
                </Text>
              )}
            </Stack>
          </Menu.Label>
          <Menu.Item component={Link} href="/settings" leftSection={<IconUserCircle size={14} />}>
            {t('account')}
          </Menu.Item>
          <Menu.Item component={Link} href="/settings/billing" leftSection={<IconCreditCard size={14} />}>
            {t('billing')}
          </Menu.Item>
          <Menu.Item leftSection={<IconRoute size={14} />} onClick={startTour}>
            {tOnb('takeTheTour')}
          </Menu.Item>
          <Menu.Item
            component="a"
            href={docsHomeHref(locale)}
            target="_blank"
            rel="noopener noreferrer"
            leftSection={<IconBook2 size={14} />}
          >
            {tOnb('documentation')}
          </Menu.Item>
          <Menu.Divider />
          <Menu.Item leftSection={<IconLogout size={14} />} onClick={logout} color="red">
            {t('logout')}
          </Menu.Item>
        </Menu.Dropdown>
      </Menu>
    </Group>
  );
}
