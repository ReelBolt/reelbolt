'use client';

import React, { useEffect, useRef } from 'react';
import { Text } from '@mantine/core';
import { IconCheck } from '@tabler/icons-react';
import type { TimelineRenderPreset } from '@/lib/types/workflow';
import { useT } from '@/i18n';
import { EditorIcon } from '../ui/EditorIcon';
import { PRESET_SIZE, presetPicture, type PresetAvailability } from './render-presets';
import classes from './Deliver.module.css';

export interface PresetListProps {
  /** The presets in the order they are listed, each with its plain name. */
  options: readonly { value: TimelineRenderPreset; label: string }[];
  value: TimelineRenderPreset;
  onChange: (preset: TimelineRenderPreset) => void;
  /** What the plan does with each preset, known from the plan data before anything is asked of the server. */
  availability: (preset: TimelineRenderPreset) => PresetAvailability;
  /** The plan's largest output short side; null when unlimited or unknown. */
  maxOutputHeight: number | null;
  /** The timeline's own size, which `Master` renders at. */
  timelineSize: { width: number; height: number } | null;
  /** Bumped to move the keyboard focus to the chosen preset (the queue's "Choose a preset" action). */
  focusRequest?: number;
}

/**
 * The spine of the settings column: every render preset as a row that says what it makes (its size, its shape and
 * what it is for) and what the plan does with it. One row is always chosen; the list is never folded away.
 * A preset the plan refuses is shown, marked and cannot be chosen, so the refusal is read before any click.
 * Keyboard: the arrow keys, Home and End move the choice, as in any single-choice list.
 */
export function PresetList({ options, value, onChange, availability, maxOutputHeight, timelineSize, focusRequest = 0 }: PresetListProps) {
  const t = useT('editor.deliver.presetList');
  const rows = useRef(new Map<TimelineRenderPreset, HTMLDivElement>());
  const enabled = options.filter((o) => availability(o.value) !== 'refused');

  useEffect(() => {
    if (focusRequest > 0) rows.current.get(value)?.focus();
    // Only a new request moves the focus; a change of the choice must not steal it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [focusRequest]);

  const choose = (preset: TimelineRenderPreset, focus: boolean) => {
    if (availability(preset) === 'refused') return;
    onChange(preset);
    if (focus) rows.current.get(preset)?.focus();
  };

  const onKeyDown = (event: React.KeyboardEvent<HTMLDivElement>, preset: TimelineRenderPreset) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      choose(preset, false);
      return;
    }
    const at = enabled.findIndex((o) => o.value === preset);
    const next =
      event.key === 'ArrowDown' ? enabled[Math.min(enabled.length - 1, at + 1)]
      : event.key === 'ArrowUp' ? enabled[Math.max(0, at - 1)]
      : event.key === 'Home' ? enabled[0]
      : event.key === 'End' ? enabled[enabled.length - 1]
      : undefined;
    if (!next) return;
    event.preventDefault();
    choose(next.value, true);
  };

  return (
    <div role="listbox" aria-label={t('listLabel')} aria-orientation="vertical" className={classes.presetList} data-testid="render-preset">
      {options.map((option) => {
        const state = availability(option.value);
        const selected = option.value === value;
        const picture = presetPicture(option.value, timelineSize);
        const shape = PRESET_SIZE[option.value]?.shape ?? null;
        const facts = option.value === 'AudioOnly' ? t('noPicture') : picture ? t('size', { width: picture.width, height: picture.height }) : t('timelineSize');
        return (
          <div
            key={option.value}
            ref={(el) => {
              if (el) rows.current.set(option.value, el);
              else rows.current.delete(option.value);
            }}
            role="option"
            aria-selected={selected}
            aria-disabled={state === 'refused' ? true : undefined}
            tabIndex={selected ? 0 : -1}
            className={classes.presetRow}
            onClick={() => choose(option.value, false)}
            onKeyDown={(event) => onKeyDown(event, option.value)}
            data-testid={`render-preset-${option.value}`}
            data-availability={state}
          >
            <span className={classes.presetHead}>
              <Text component="span" size="sm" fw={600} className={classes.presetName}>
                {option.label}
                {selected ? <EditorIcon icon={IconCheck} size="sm" title={t('chosen')} /> : null}
              </Text>
              <Text component="span" size="xs" className={classes.presetFacts}>
                {shape ? t('factsWithShape', { facts, shape }) : facts}
              </Text>
            </span>
            <Text component="span" size="xs" className={classes.presetNote}>
              {t(`use.${option.value}`)}
            </Text>
            {state === 'refused' ? (
              <Text component="span" size="xs" fw={600} data-testid={`render-preset-${option.value}-refused`}>
                {t('notInPlan')}
              </Text>
            ) : state === 'clamped' && maxOutputHeight !== null ? (
              <Text component="span" size="xs" fw={600} data-testid={`render-preset-${option.value}-clamped`}>
                {t('clampedBadge', { cap: maxOutputHeight })}
              </Text>
            ) : null}
          </div>
        );
      })}
    </div>
  );
}
