'use client';

import React from 'react';
import { Button, Group, Popover, Text, Tooltip } from '@mantine/core';
import { IconChevronDown } from '@tabler/icons-react';
import { useMediaQuery } from '@mantine/hooks';
import { useT } from '@/i18n';
import type { KeyAction } from '@/lib/editing/keymap';
import { useLiveChordLookup } from '../prefs/live-chords';

export interface FrameSteppersProps {
  /** The selected clip(s) can be nudged. */
  clipEnabled: boolean;
  onNudge: (deltaFrames: number) => void;
  onPlayhead: (deltaFrames: number) => void;
}

const STEPS = [-10, -1, 1, 10] as const;
/** The keymap action behind each stepper button; the tooltip shows its live chord. */
const CLIP_ACTIONS: Record<number, KeyAction> = { [-10]: 'nudgeBack10', [-1]: 'nudgeBack', 1: 'nudgeForward', 10: 'nudgeForward10' };
const PLAYHEAD_ACTIONS: Record<number, KeyAction> = { [-10]: 'frameBack10', [-1]: 'frameBack', 1: 'frameForward', 10: 'frameForward10' };

const withChord = (label: string, chord: string | undefined): string => (chord ? `${label} (${chord})` : label);

/** R03.2: visible -1 / +1 / -10 / +10 frame buttons for the selected clip and for the playhead (touch has no arrow keys). */
export function FrameSteppers({ clipEnabled, onNudge, onPlayhead }: FrameSteppersProps) {
  const t = useT('editor-timeline.steppers');
  const chordOf = useLiveChordLookup();
  const coarse = useMediaQuery('(pointer: coarse)', false, { getInitialValueInEffect: true }) ?? false;
  const group = (name: 'clip' | 'playhead', label: string, enabled: boolean, act: (n: number) => void, actions: Record<number, KeyAction>) => (
    <Group gap={2} wrap="nowrap" role="group" aria-label={label} data-testid={`steppers-${name}`}>
      <Text size="xs" c="dimmed" mr={2}>
        {name === 'clip' ? t('clip') : t('playhead')}
      </Text>
      {STEPS.map((n) => (
        <Tooltip key={n} label={withChord(t('nudge', { label, direction: n > 0 ? 'later' : 'earlier', count: Math.abs(n) }), chordOf(actions[n]))} openDelay={300}>
          <Button
            size="compact-sm"
            fz="xs"
            variant="subtle"
            color="gray"
            h={coarse ? 44 : undefined}
            miw={coarse ? 44 : undefined}
            px={coarse ? 6 : 4}
            disabled={!enabled}
            onClick={() => act(n)}
            aria-label={t('nudge', { label, direction: n > 0 ? 'later' : 'earlier', count: Math.abs(n) })}
            data-testid={`step-${name}-${n > 0 ? '+' : '-'}${Math.abs(n)}`}
          >
            {n > 0 ? `+${n}` : n}
          </Button>
        </Tooltip>
      ))}
    </Group>
  );
  return (
    <Group gap={8} wrap="nowrap" data-testid="frame-steppers">
      {group('clip', t('moveClip'), clipEnabled, onNudge, CLIP_ACTIONS) /* eslint-disable-line i18next/no-literal-string -- ids, not copy */}
      {group('playhead', t('movePlayhead'), true, onPlayhead, PLAYHEAD_ACTIONS) /* eslint-disable-line i18next/no-literal-string -- ids, not copy */}
    </Group>
  );
}

/** The steppers folded behind one button, when the toolbar has no room for them. It stays open between steps. */
export function FrameSteppersMenu(props: FrameSteppersProps) {
  const t = useT('editor-timeline.steppers');
  return (
    <Popover position="bottom-start" withinPortal shadow="md">
      <Popover.Target>
        <Button size="compact-sm" fz="xs" variant="subtle" color="gray" fw={500} rightSection={<IconChevronDown size={13} />} title={t('menuHint')} data-testid="steppers-menu">
          {t('menu')}
        </Button>
      </Popover.Target>
      <Popover.Dropdown data-testid="steppers-menu-panel">
        <FrameSteppers {...props} />
      </Popover.Dropdown>
    </Popover>
  );
}
