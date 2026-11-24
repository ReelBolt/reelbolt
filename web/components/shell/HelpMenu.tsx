'use client';

import { ActionIcon, Menu } from '@mantine/core';
import { IconBook, IconBook2, IconHelp, IconRoute } from '@tabler/icons-react';
import { useTourLauncher } from '@/components/onboarding/OnboardingProvider';
import { useLocale, useT } from '@/i18n';
import { docsHomeHref, guideHrefFor } from '@/lib/onboarding/guide-links';

/**
 * The header's Help menu: take the tour again, or open the written guides or the whole documentation. On a phone the header is
 * already full, so the menu steps aside there and the account menu carries "Take the tour" and
 * "Documentation" instead.
 */
export function HelpMenu() {
  const { startTour } = useTourLauncher();
  const t = useT('onboarding.menu');
  const locale = useLocale();
  return (
    <Menu shadow="md" width={220} position="bottom-end">
      <Menu.Target>
        <ActionIcon variant="subtle" color="violet" size="lg" visibleFrom="sm" aria-label={t('help')}>
          <IconHelp size={20} />
        </ActionIcon>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Item leftSection={<IconRoute size={14} />} onClick={startTour}>
          {t('takeTheTour')}
        </Menu.Item>
        <Menu.Item
          component="a"
          href={guideHrefFor('index', locale)}
          target="_blank"
          rel="noopener noreferrer"
          leftSection={<IconBook size={14} />}
        >
          {t('readTheGuides')}
        </Menu.Item>
        <Menu.Item
          component="a"
          href={docsHomeHref(locale)}
          target="_blank"
          rel="noopener noreferrer"
          leftSection={<IconBook2 size={14} />}
        >
          {t('documentation')}
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );
}
