'use client';

import React from 'react';
import { Button, Menu, Text } from '@mantine/core';
import { IconCheck, IconChevronDown, IconTimelineEvent } from '@tabler/icons-react';
import { useT } from '@/i18n';
import type { EventsController } from './useEventsLaneController';

/**
 * The Events lane's view menu in the timeline toolbar: show or hide the lane, snap to its ticks, and (when a
 * video has no events yet) find them. Finding events is free; the price line says so before anything runs.
 */
export function EventsLaneMenu({ controller, disabled, shortcuts }: { controller: EventsController; disabled: boolean; shortcuts?: { lane?: string; snap?: string } }) {
  const t = useT('editor-timeline.eventsLane');
  const { data } = controller;
  const canFind = controller.laneOn && data.missing.length > 0 && !data.working;
  const priceLine = data.estimate === null ? null : data.estimate.free ? t('estimateFree') : t('estimateCredits', { n: data.estimate.credits });
  const status = data.working ? t('working') : data.error ?? (controller.laneOn && data.ready && data.events.length === 0 ? t('none') : null);
  return (
    <Menu position="bottom-start" withinPortal>
      <Menu.Target>
        <Button
          size="compact-sm"
          fz="xs"
          variant={controller.laneOn ? 'light' : 'subtle'}
          color={controller.laneOn ? 'violet' : 'gray'}
          fw={500}
          leftSection={<IconTimelineEvent size={15} stroke={1.7} />}
          rightSection={<IconChevronDown size={13} />}
          data-testid="events-lane-menu"
        >
          {t('button')}
        </Button>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Item onClick={controller.toggleLane} aria-checked={controller.laneOn} role="menuitemcheckbox" rightSection={controller.laneOn ? <IconCheck size={14} /> : <Text size="xs" c="dimmed">{shortcuts?.lane ?? ''}</Text>} data-testid="events-lane-toggle">
          {t('showLane')}
        </Menu.Item>
        <Menu.Item onClick={controller.toggleSnap} disabled={!controller.laneOn} aria-checked={controller.snapOn} role="menuitemcheckbox" rightSection={controller.snapOn ? <IconCheck size={14} /> : <Text size="xs" c="dimmed">{shortcuts?.snap ?? ''}</Text>} data-testid="events-snap-menu-toggle">
          {t('snapToEvents')}
        </Menu.Item>
        {canFind && (
          <>
            <Menu.Divider />
            <Menu.Item closeMenuOnClick={false} disabled={disabled || data.starting} onClick={() => void data.start()} data-testid="events-find">
              <div>{t('find')}</div>
              {priceLine !== null && <Text size="xs" c="dimmed" data-testid="events-estimate">{priceLine}</Text>}
            </Menu.Item>
          </>
        )}
        {status !== null && (
          <Menu.Label>
            <Text size="xs" c="dimmed" role="status" data-testid="events-status">{status}</Text>
          </Menu.Label>
        )}
      </Menu.Dropdown>
    </Menu>
  );
}
