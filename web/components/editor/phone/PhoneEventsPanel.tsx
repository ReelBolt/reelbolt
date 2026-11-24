'use client';

import React from 'react';
import { Button, Text } from '@mantine/core';
import { useT } from '@/i18n';
import { useEventsLaneController } from '../timeline/useEventsLaneController';
import type { PhoneEventsHost } from './phone-actions';
import classes from './phone.module.css';

/**
 * The phone's Events sheet: a tap-only list of the actions for the event nearest the playhead, with Previous and
 * Next to move between events. The phone has no lane and adds no gesture; every choice is a 44 px button that runs
 * the same plan the desktop lane's menu does, as one undo step.
 */
export function PhoneEventsPanel({ host, onBack, onClose }: { host: PhoneEventsHost; onBack: () => void; onClose: () => void }) {
  const t = useT('editor-timeline.eventsLane');
  const c = useEventsLaneController({ ...host, forceData: true });
  const current = c.data.events.find((e) => e.key === c.activeKey) ?? c.nearest();
  const choices = current ? c.actionsFor(current) : [];
  return (
    <div className={classes.sheetStack} data-testid="phone-events-sheet">
      <Button variant="default" h={44} justify="flex-start" onClick={onBack} data-testid="phone-events-back">{t('phone.back')}</Button>
      {current === null ? (
        <>
          <Text size="sm" data-testid="phone-events-none">{c.data.working ? t('working') : t('phone.none')}</Text>
          {c.data.missing.length > 0 && !c.data.working && (
            <>
              <Button h={44} onClick={() => void c.data.start()} disabled={host.readOnly || c.data.starting} data-testid="phone-events-find">{t('find')}</Button>
              {c.data.estimate && <Text size="xs" c="dimmed">{c.data.estimate.free ? t('estimateFree') : t('estimateCredits', { n: c.data.estimate.credits })}</Text>}
            </>
          )}
        </>
      ) : (
        <>
          <Text size="sm" fw={600} data-testid="phone-events-current">{c.describe(current)}</Text>
          <div className={classes.sheetGrid}>
            <Button variant="default" h={44} onClick={() => c.step(-1)} data-testid="phone-events-previous">{t('phone.previous')}</Button>
            <Button variant="default" h={44} onClick={() => c.step(1)} data-testid="phone-events-next">{t('phone.next')}</Button>
          </div>
          <Text size="xs" c="dimmed">{t('phone.choose')}</Text>
          <div className={classes.sheetGrid}>
            {choices.map(({ id, label }) => (
              <Button key={id} variant="default" h={44} justify="flex-start" onClick={() => { c.run(id, current); onClose(); }} data-testid={`phone-event-action-${id}`}>
                {label}
              </Button>
            ))}
          </div>
        </>
      )}
    </div>
  );
}
