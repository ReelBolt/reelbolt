'use client';

import { useState } from 'react';
import { Alert, Group, Loader, Switch, Text } from '@mantine/core';
import { SettingsCard } from './SettingsCard';
import { useAuthSettings } from '@/lib/hooks/use-auth-settings';
import { updateAuthSettings } from '@/lib/api/auth-settings';
import { ApiError } from '@/lib/api/client';
import { useT } from '@/i18n';

/**
 * Per-user opt-in for the 13 administrative tools over MCP (mcp-oauth.md §11, self-only).
 *
 * The state is server-authoritative, not optimistic: the switch only ever shows a value the server
 * has confirmed. On load it reflects the stored value; a write re-renders from the value the PATCH
 * response returns (not the value requested); and a failed write surfaces an error and keeps the last
 * confirmed value instead of pretending the change took. SWR revalidates on remount, so leaving and
 * returning to the page re-reads the truth.
 *
 * Accessibility: the switch carries a real, visible label (also its accessible name via aria-label),
 * is a native <input role="switch"> and therefore keyboard reachable, and the state is announced in
 * words, not just by the switch colour.
 */
export function McpAdminToolsCard({ offOnly = false }: { offOnly?: boolean } = {}) {
  const { data, isLoading, error, mutate } = useAuthSettings();
  const t = useT('settings.mcp');
  const [saving, setSaving] = useState(false);
  const [writeError, setWriteError] = useState<string | null>(null);

  const onChange = async (value: boolean) => {
    if (saving) return;
    setSaving(true);
    setWriteError(null);
    try {
      // Re-render from what the server stored, not what we asked for.
      const saved = await updateAuthSettings({ mcpAdminToolsEnabled: value });
      await mutate(saved, { revalidate: false });
    } catch (e) {
      // A failed write must not look like a success: surface it, keep the last confirmed value.
      setWriteError(
        e instanceof ApiError ? e.message : t('saveFailed'),
      );
    } finally {
      setSaving(false);
    }
  };

  const enabled = data?.mcpAdminToolsEnabled === true;

  // Someone without administrative authority has no use for the switch, but a setting that is still ON
  // (granted earlier, or when they held a different role) must stay possible to turn off.
  if (offOnly && !enabled) return null;

  return (
    <SettingsCard
      title={t('title')}
      description={t('description', { count: 13 })}
      testId="mcp-admin-tools-card"
    >
      {isLoading ? (
        <Group justify="center" py="sm">
          <Loader size="sm" />
        </Group>
      ) : error ? (
        <Alert color="red" data-testid="mcp-admin-tools-load-error">
          {t('loadFailed')}
        </Alert>
      ) : (
        <Group justify="space-between" gap="md" wrap="nowrap" align="center">
          <Switch
            label={t('switchLabel')}
            aria-label={t('switchLabel')}
            checked={enabled}
            disabled={saving || (offOnly && !enabled)}
            onChange={(e) => {
              void onChange(e.currentTarget.checked);
            }}
          />
          {/* The state in words, so it is not conveyed by colour alone. */}
          <Text size="xs" c="dimmed" data-testid="mcp-admin-tools-state">
            {enabled
              ? t('stateOn')
              : t('stateOff')}
          </Text>
        </Group>
      )}
      {writeError && (
        <Alert color="red" data-testid="mcp-admin-tools-write-error">
          {writeError}
        </Alert>
      )}
    </SettingsCard>
  );
}
