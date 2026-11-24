'use client';

import { Button, Text } from '@mantine/core';
import { IconRoute } from '@tabler/icons-react';
import { useOnboarding } from '@/lib/hooks/use-onboarding';
import { useT } from '@/i18n';
import { useOnboardingContext } from './onboarding-context';
import classes from './onboarding.module.css';

/**
 * The tour as a quiet card on the dashboard, for someone who answered the welcome questions: the
 * tour no longer opens by itself for them (they are on the way to a first video), so it is offered
 * here, one click away, until it is taken or turned down. Everyone else never sees this card: the
 * tour opens for them as before.
 */
export function TourCard() {
  const t = useT('onboarding.tourCard');
  const tMenu = useT('onboarding.menu');
  const { settled, offerTour, answeredWelcome, finishTour } = useOnboarding();
  const { startTour } = useOnboardingContext();
  if (!settled || !offerTour || !answeredWelcome) return null;
  return (
    <section className={classes.checklist} aria-label={t('title')} data-testid="tour-card">
      <h2 className={classes.checkTitle}>{t('title')}</h2>
      <Text size="sm" c="dimmed" mt={4}>
        {t('body')}
      </Text>
      <div className={classes.actionsMain} style={{ marginTop: 'var(--mantine-spacing-sm)' }}>
        <Button variant="light" leftSection={<IconRoute size={16} />} style={{ minHeight: 44 }} onClick={startTour}>
          {tMenu('takeTheTour')}
        </Button>
        <Button variant="subtle" color="gray" style={{ minHeight: 44 }} onClick={() => finishTour('skipped')}>
          {t('dismiss')}
        </Button>
      </div>
    </section>
  );
}
