'use client';

import { SegmentedControl, Text } from '@mantine/core';
import { useMantineColorScheme } from '@mantine/core';
import { SettingsCard } from './SettingsCard';
import { useT } from '@/i18n';

/**
 * Colour scheme, as a three-way choice rather than the header's two-way toggle.
 *
 * The header toggle (shell/ThemeToggle) can only ever write an EXPLICIT scheme: its icon follows the
 * COMPUTED scheme, so a click out of "follow the system" can never put you back. Once you have
 * clicked it, the stored value is 'light' or 'dark' for good and the installation's default stops
 * applying. This is the one place that choice can be undone.
 *
 * It is a browser preference, not an account one: Mantine stores it under
 * 'mantine-color-scheme-value' in localStorage, so it follows the DEVICE, not the sign-in. Saying
 * so is the honest thing -- a user who sets Dark at work and signs in at home will otherwise think
 * the setting has been forgotten.
 */
export function AppearanceCard() {
  const { colorScheme, setColorScheme } = useMantineColorScheme();
  const t = useT('settings.appearance');

  return (
    <SettingsCard
      title={t('title')}
      description={t('description')}
      testId="appearance-card"
    >
      <SegmentedControl
        value={colorScheme}
        onChange={(value) => setColorScheme(value as 'auto' | 'light' | 'dark')}
        data={[
          { label: t('auto'), value: 'auto' },
          { label: t('light'), value: 'light' },
          { label: t('dark'), value: 'dark' },
        ]}
        aria-label={t('ariaLabel')}
      />
      <Text size="xs" c="dimmed">
        {t('hint')}
      </Text>
    </SettingsCard>
  );
}
