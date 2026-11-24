'use client';

import { useState } from 'react';
import { Alert, Group, Loader, Switch, Text } from '@mantine/core';
import { SettingsCard } from './SettingsCard';
import { useAuthSettings } from '@/lib/hooks/use-auth-settings';
import { updateAuthSettings } from '@/lib/api/auth-settings';
import { ApiError } from '@/lib/api/client';
import { useT } from '@/i18n';

/**
 * Per-user opt-in that lets connected tools (MCP) allow sending the user's media to AI services for a run they start
 * (docs/design/app-inputs.md "Permission to send media as an app field"). It gives the ability only: each run must still
 * set the value itself. Server-authoritative like the admin-tools switch: the switch shows only a value the server
 * confirmed, and a failed write keeps the last confirmed value and says so.
 */
export function McpMediaEgressCard() {
  const { data, isLoading, error, mutate } = useAuthSettings();
  const t = useT('settings.mcpMediaEgress');
  const [saving, setSaving] = useState(false);
  const [writeError, setWriteError] = useState<string | null>(null);

  const onChange = async (value: boolean) => {
    if (saving) return;
    setSaving(true);
    setWriteError(null);
    try {
      // Re-render from what the server stored, not what we asked for.
      const saved = await updateAuthSettings({ mcpMediaEgressEnabled: value });
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

  const enabled = data?.mcpMediaEgressEnabled === true;


  return (
    <SettingsCard
      title={t('title')}
      description={t('description')}
      testId="mcp-media-egress-card"
    >
      {isLoading ? (
        <Group justify="center" py="sm">
          <Loader size="sm" />
        </Group>
      ) : error ? (
        <Alert color="red" data-testid="mcp-media-egress-load-error">
          {t('loadFailed')}
        </Alert>
      ) : (
        <Group justify="space-between" gap="md" wrap="nowrap" align="center">
          <Switch
            label={t('switchLabel')}
            aria-label={t('switchLabel')}
            checked={enabled}
            disabled={saving}
            onChange={(e) => {
              void onChange(e.currentTarget.checked);
            }}
          />
          {/* The state in words, so it is not conveyed by colour alone. */}
          <Text size="xs" c="dimmed" data-testid="mcp-media-egress-state">
            {enabled
              ? t('stateOn')
              : t('stateOff')}
          </Text>
        </Group>
      )}
      {writeError && (
        <Alert color="red" data-testid="mcp-media-egress-write-error">
          {writeError}
        </Alert>
      )}
    </SettingsCard>
  );
}
