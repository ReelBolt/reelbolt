'use client';

import { useState } from 'react';
import { Group, Menu, Text, UnstyledButton } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconBuilding, IconCheck, IconChevronDown, IconUser } from '@tabler/icons-react';
import { useRouter } from 'next/navigation';
import { useAuth } from '@/lib/hooks/use-auth';
import { useOrgs, useSwitchOrg } from '@/lib/hooks/use-orgs';
import { orgDisplayLabel } from '@/lib/org-label';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

/**
 * Workspace switcher (A15-UI). Lists the caller's organizations — their Personal workspace
 * (always labelled "Personal", decision D1) plus every Team org they belong to — and switches
 * the active one. The list endpoint already orders Personal first and never returns the Platform
 * org (`ListUserOrgs` in api/services/org_store.go), so nothing is filtered here.
 *
 * Selecting a workspace runs three steps, in order:
 *   1. `useSwitchOrg` POSTs /api/v1/auth/switch-org, refreshes the `useAuth` snapshot from the
 *      re-issued readable cookie, and revalidates every entry in the client cache. This app's
 *      client cache is SWR, not React Query — that `mutate(() => true)` in the hook is the
 *      "clear the React Query cache" step, and it lives in the hook so a caller cannot skip it.
 *   2. The SSE streams reconnect on their own: `activeOrgId` is part of the effect key in
 *      use-execution-stream, use-workflow-monitor and useWorkflowEvents, so the cookie change
 *      above tears each EventSource down and reopens it under the new session. An already-open
 *      stream keeps the token it connected with, which is exactly the stale state to avoid.
 *   3. The caller is routed to /dashboard, so no page left over from the previous workspace
 *      keeps rendering; its queries refetch under the new session on mount.
 */
export function OrgSwitcher({ onSelect }: { onSelect?: () => void }) {
  const router = useRouter();
  const t = useT('shell.org');
  const tAll = useT();
  const { user, activeOrgId } = useAuth();
  const { data: orgs, error } = useOrgs();
  const switchOrg = useSwitchOrg();
  const [switching, setSwitching] = useState(false);

  // The list is authoritative for the active workspace's name; the cookie is the fallback used
  // while the list is still in flight.
  const active = orgs?.find((org) => org.id === activeOrgId);
  const label = active
    ? orgDisplayLabel(active)
    : user
      ? orgDisplayLabel({ name: user.orgName ?? '', kind: user.orgKind })
      : t('workspace');

  async function handleSelect(orgId: string) {
    if (switching || orgId === activeOrgId) return;
    setSwitching(true);
    try {
      await switchOrg(orgId);
      router.push('/dashboard');
    } catch (err) {
      notifications.show({
        message: describeError(tAll, err, t('switchFailed')),
        color: 'red',
      });
    } finally {
      setSwitching(false);
    }
  }

  return (
    <Menu shadow="md" width={220}>
      <Menu.Target>
        <UnstyledButton
          aria-label={t('switch')}
          disabled={switching}
          // The pill's surface must follow the colour scheme. `--mantine-color-default` is the
          // semantic "default" surface (white in light mode, dark-6 in dark mode); the fixed
          // `--mantine-color-gray-0` step stayed near-white in both schemes.
          style={{
            width: '100%',
            padding: '6px 10px',
            borderRadius: 'var(--mantine-radius-sm)',
            background: 'var(--mantine-color-default)',
          }}
        >
          <Group justify="space-between" wrap="nowrap" w="100%">
            <Group gap="xs" wrap="nowrap" flex="0 1 auto" min-w={0}>
              <IconBuilding size={16} />
              <Text size="sm" fw={500} lineClamp={1}>
                {label}
              </Text>
            </Group>
            <IconChevronDown size={14} />
          </Group>
        </UnstyledButton>
      </Menu.Target>
      <Menu.Dropdown>
        {orgs === undefined ? (
          <Menu.Label>{error ? t('loadFailed') : t('loading')}</Menu.Label>
        ) : (
          orgs.map((org) => {
            const isActive = org.id === activeOrgId;
            return (
              <Menu.Item
                key={org.id}
                disabled={switching}
                leftSection={org.kind === 'Personal' ? <IconUser size={14} /> : <IconBuilding size={14} />}
                rightSection={isActive ? <IconCheck size={14} /> : undefined}
                onClick={() => {
                  onSelect?.();
                  void handleSelect(org.id);
                }}
              >
                {orgDisplayLabel(org)}
              </Menu.Item>
            );
          })
        )}
      </Menu.Dropdown>
    </Menu>
  );
}
