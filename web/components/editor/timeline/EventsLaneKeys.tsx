'use client';

import React, { useId, useState } from 'react';
import { Box } from '@mantine/core';
import { nearestEvent } from '@/lib/editing/events/events-data';
import { EVENTS_LANE_HEIGHT } from './eventsLane';
import { frameToX, type Viewport } from './paint';
import type { EventsLaneProps } from './TimelineCanvas';

/**
 * The Events lane for the keyboard: one focusable listbox laid over the lane (it takes no pointer events, the
 * canvas keeps those). Left and right step to the previous or next tick and move the playhead there; Enter,
 * Space or the context-menu key opens "Act on this event". The focused tick's sentence (type and time) shows as
 * a tooltip and is the option a screen reader reads. It is a `listbox`, so the editor's global shortcuts stay
 * out of its way while it has focus.
 */
export function EventsLaneKeys({ eventsLane, viewport, top, playheadFrame, canvasRef }: {
  eventsLane: EventsLaneProps;
  viewport: Viewport;
  top: number;
  playheadFrame: number;
  canvasRef: React.RefObject<HTMLCanvasElement | null>;
}) {
  const id = useId();
  const [focused, setFocused] = useState(false);
  const items = eventsLane.items;
  const active = items.find((e) => e.key === eventsLane.activeKey) ?? null;

  const step = (delta: 1 | -1) => {
    if (items.length === 0) return;
    const from = active ? items.indexOf(active) : -1;
    const next = from < 0 ? nearestEvent(items, playheadFrame) : items[Math.min(items.length - 1, Math.max(0, from + delta))];
    if (next) eventsLane.onActivate(next);
  };
  const openMenu = () => {
    const target = active ?? nearestEvent(items, playheadFrame);
    if (!target) return;
    const rect = canvasRef.current?.getBoundingClientRect();
    eventsLane.onActivate(target);
    eventsLane.onMenu(target, { clientX: (rect?.left ?? 0) + frameToX(target.frame, viewport) + 8, clientY: (rect?.top ?? 0) + top + EVENTS_LANE_HEIGHT });
  };
  const onKeyDown = (event: React.KeyboardEvent) => {
    if (event.key === 'ArrowRight') step(1);
    else if (event.key === 'ArrowLeft') step(-1);
    else if (event.key === 'Enter' || event.key === ' ' || event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10')) openMenu();
    else return;
    event.preventDefault();
  };
  const optionId = `${id}-active`;
  const text = active ? eventsLane.describe(active) : '';
  const x = active ? frameToX(active.frame, viewport) : 0;

  return (
    <Box
      role="listbox"
      tabIndex={0}
      aria-label={eventsLane.ariaLabel}
      aria-activedescendant={active ? optionId : undefined}
      data-testid="events-lane-keys"
      onKeyDown={onKeyDown}
      onFocus={() => setFocused(true)}
      onBlur={() => setFocused(false)}
      style={{ position: 'absolute', left: 0, top, width: viewport.width, height: EVENTS_LANE_HEIGHT, pointerEvents: 'none', outlineOffset: -2 }}
    >
      {active && (
        <div id={optionId} role="option" aria-selected="true" style={{ position: 'absolute', left: Math.max(0, Math.min(viewport.width - 220, x + 10)), top: EVENTS_LANE_HEIGHT + 2, display: focused ? 'block' : 'none', zIndex: 5, whiteSpace: 'nowrap', padding: '2px 8px', borderRadius: 4, fontSize: 12, background: 'var(--mantine-color-dark-8)', color: 'var(--mantine-color-gray-0)', border: '1px solid var(--mantine-color-default-border)' }} data-testid="events-lane-tooltip">
          {text}
        </div>
      )}
    </Box>
  );
}
