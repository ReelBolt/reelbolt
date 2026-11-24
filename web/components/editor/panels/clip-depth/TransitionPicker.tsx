'use client';

import React, { useMemo, useState } from 'react';
import { notifications } from '@mantine/notifications';
import { Badge, Box, Button, Group, Select, Stack, Text } from '@mantine/core';
import type { EditOp } from '@/lib/editing/engine';
import {
  MORE_TRANSITIONS,
  TRANSITION_SECONDS,
  TRANSITION_WORDS,
  addTransitionOp,
  addTransitionToAllOps,
  findSeams,
  fitTransition,
  formatSeconds,
  makeRoomOps,
  secondsToFrames,
  transitionLabel,
  transitionParity,
  type Seam,
} from '@/lib/editing/clip-depth';
import type { EditTimeline, Rational, Transition } from '@/lib/editing/types';
import { useRefusalToast } from '../../shell/refusal-toast';
import { TransitionThumb } from './MotionThumb';
import classes from './clip-depth.module.css';
import { useT } from '@/i18n';
import { msg } from '@/i18n/msg';
import { transitionLabelMsg } from '@/lib/editing/labels-msg';

export interface TransitionPickerProps {
  doc: EditTimeline;
  seam: Seam;
  rate: Rational;
  readOnly: boolean;
  newId: () => string;
  /** Applies ops as ONE undo step. */
  onApply: (ops: EditOp[], label: string) => void;
  /** Called after something was applied (the popover closes). */
  onDone?: () => void;
}

function ParityLine({ type }: { type: string }) {
  const t = useT('editor.panels.clip-depth.transitionPicker');
  const parity = transitionParity(type);
  return (
    <Group gap={6} wrap="nowrap" align="flex-start" data-testid="transition-parity" data-parity={parity.parity}>
      <Badge size="md" variant="light" tt="none" c="var(--mantine-color-text)" color={parity.parity === 'faithful' ? 'teal' : 'yellow'}>
        {parity.parity === 'faithful' ? t('faithful') : t('approximate')}
      </Badge>
      <Text size="12px" c="dimmed">
        {parity.line}
      </Text>
    </Group>
  );
}

function WordGrid({ value, onPick, readOnly }: { value: string; onPick: (type: string) => void; readOnly: boolean }) {
  const t = useT('editor.panels.clip-depth.transitionPicker');
  return (
    <Box className={classes.grid} role="group" aria-label={t('transitions')}>
      {TRANSITION_WORDS.map((w) => (
        <button key={w.type} type="button" className={classes.wordButton} data-active={value === w.type} disabled={readOnly} onClick={() => onPick(w.type)} data-testid={`transition-word-${w.type}`}>
          <TransitionThumb type={w.type} />
          <span>{w.label}</span>
        </button>
      ))}
    </Box>
  );
}

function MoreSelect({ value, onPick, readOnly }: { value: string; onPick: (type: string) => void; readOnly: boolean }) {
  const t = useT('editor.panels.clip-depth.transitionPicker');
  const more = MORE_TRANSITIONS.some((m) => m.type === value) ? value : null;
  return (
    <Select
      size="xs"
      label={t('more')}
      placeholder={t('moreTransitions')}
      data={MORE_TRANSITIONS.map((m) => ({ value: m.type, label: m.label }))}
      value={more}
      onChange={(v) => v && onPick(v)}
      searchable
      clearable={false}
      disabled={readOnly}
      comboboxProps={{ withinPortal: true }}
      data-testid="transition-more"
    />
  );
}

/**
 * R07.4: the picker a seam's "+" opens (and the inspector's Transition section): eight labelled words with
 * looping thumbnails, "More" for the rest, a length, the parity line, Add, Apply to all cuts, and Make room.
 */
export function TransitionPicker({ doc, seam, rate, readOnly, newId, onApply, onDone }: TransitionPickerProps) {
  const t = useT('editor.panels.clip-depth.transitionPicker');
  const { showRefusalMessage } = useRefusalToast();
  const [type, setType] = useState('dissolve');
  const [seconds, setSeconds] = useState(0.5);
  const frames = secondsToFrames(seconds, rate);
  const fit = fitTransition(seam, frames);
  const room = useMemo(() => (fit.missingHandle > 0 ? makeRoomOps(doc, seam, frames) : null), [doc, seam, frames, fit.missingHandle]);
  const cuts = useMemo(() => findSeams(doc).length, [doc]);
  const tooShort = fit.maxFrames < 1;

  const add = () => {
    const result = addTransitionOp(seam, { transitionType: type, frames }, newId);
    if ('reason' in result) {
      showRefusalMessage(result.reason);
      return;
    }
    onApply([result.op], `Add ${transitionLabel(type)}`);
    onDone?.();
  };
  const addAll = () => {
    const plan = addTransitionToAllOps(doc, { transitionType: type, frames }, newId);
    if (plan.added === 0) {
      showRefusalMessage(t('noCutHasSpare'));
      return;
    }
    onApply(plan.ops, t('addToAllCuts', { v1: transitionLabel(type) }));
    notifications.show({
      color: plan.skipped > 0 ? 'yellow' : 'green',
      title: plan.skipped > 0 ? t('addedToOfCuts', { added: plan.added, v2: plan.added + plan.skipped }) : t.msg(msg('editor-helpers-2.fmt.addedToCuts', { count: plan.added })),
      message: plan.skipped > 0 ? t.msg(msg('editor-helpers-2.fmt.noSpareFootage', { count: plan.skipped })) : t('youCanUndoThis'),
    });
    onDone?.();
  };

  return (
    <Stack gap={8} data-testid="transition-picker">
      <WordGrid value={type} onPick={setType} readOnly={readOnly} />
      <MoreSelect value={type} onPick={setType} readOnly={readOnly} />
      <ParityLine type={type} />
      <Group gap={4} role="group" aria-label={t('transitionLength')}>
        {TRANSITION_SECONDS.map((s) => (
          <Button key={s} size="compact-sm" fz="xs" variant={seconds === s ? 'filled' : 'light'} className={classes.chip} aria-pressed={seconds === s} disabled={readOnly} onClick={() => setSeconds(s)} data-testid={`transition-seconds-${s}`}>
            {formatSeconds(s)}
          </Button>
        ))}
      </Group>
      {tooShort && (
        <Text size="12px" c="red" data-testid="transition-note">
          {t('oneOfTheTwo')}
        </Text>
      )}
      {!tooShort && fit.missingHandle > 0 && (
        <Stack gap={4} data-testid="transition-room">
          <Text size="12px" c="dimmed" data-testid="transition-note">
            {t('theNextClipHas')}
            {room && 'reason' in room ? ` ${room.reason}` : t('makeRoomTrimsIts')}
          </Text>
          <Button
            size="compact-sm"
            variant="light"
            className={classes.chip}
            disabled={readOnly || !room || 'reason' in room}
            onClick={() => {
              if (room && 'ops' in room) {
                onApply(room.ops, t('makeRoomForA'));
              }
            }}
            data-testid="transition-make-room"
          >
            {room && 'trimmedFrames' in room ? t('makeRoomTrimOff', { v1: formatSeconds(room.trimmedFrames / (rate.num / rate.den)) }) : t('makeRoom')}
          </Button>
        </Stack>
      )}
      <Group gap={6}>
        <Button size="compact-sm" fz="xs" className={classes.chip} disabled={readOnly || tooShort || fit.missingHandle > 0} onClick={add} data-testid="transition-add">
          {t('add')} {t.msg(transitionLabelMsg(type)).toLowerCase()}
        </Button>
        {cuts > 1 && (
          <Button size="compact-sm" fz="xs" variant="light" className={classes.chip} disabled={readOnly} onClick={addAll} data-testid="transition-apply-all">
            {t('applyToAllCuts')}
          </Button>
        )}
      </Group>
    </Stack>
  );
}

export interface TransitionEditorProps {
  transition: Transition;
  /** Frame the cut sits at when the transition is removed (the middle of its span). */
  cutFrame: number;
  readOnly: boolean;
  onApply: (ops: EditOp[], label: string) => void;
  onDone?: () => void;
}

/** The seam menu of an existing transition: change the word (the item name follows) or remove it. */
export function TransitionEditor({ transition, cutFrame, readOnly, onApply, onDone }: TransitionEditorProps) {
  const t = useT('editor.panels.clip-depth.transitionPicker');
  const pick = (type: string) => {
    if (type === transition.transitionType) return;
    onApply(
      [
        { type: 'setProperty', itemId: transition.id, key: 'transitionType', value: type },
        { type: 'setProperty', itemId: transition.id, key: 'name', value: transitionLabel(type) },
      ],
      t('changeTransitionTo', { v1: transitionLabel(type) }),
    );
  };
  return (
    <Stack gap={8} data-testid="transition-editor">
      <WordGrid value={transition.transitionType} onPick={pick} readOnly={readOnly} />
      <MoreSelect value={transition.transitionType} onPick={pick} readOnly={readOnly} />
      <ParityLine type={transition.transitionType} />
      <Button
        size="compact-sm"
        variant="light"
        color="red"
        className={classes.chip}
        disabled={readOnly}
        onClick={() => {
          onApply([{ type: 'removeTransition', transitionId: transition.id, cutFrame }], t('removeTransition'));
          onDone?.();
        }}
        data-testid="transition-remove"
      >
        {t('removeTransitionHardCut')}
      </Button>
    </Stack>
  );
}
