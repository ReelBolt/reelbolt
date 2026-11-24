'use client';

import React, { useEffect, useRef, useState } from 'react';
import { Button, Text } from '@mantine/core';
import type { TrimEdge, TrimMode } from '@/lib/editing/engine';
import { ClipTimingFields } from '../panels/ClipTimingFields';
import type { ClipSheetSection } from './ClipSheet';
import type { PhoneActions } from './phone-actions';
import classes from './phone.module.css';
import { useT } from '@/i18n';
import { useHelperText } from './helper-text';
import { PhoneEventsPanel } from './PhoneEventsPanel';
import { msg, type Msg } from '@/i18n/msg';

const STEPS = [-10, -1, 1, 10] as const;


const MODES: ReadonlyArray<{ id: TrimMode; label: Msg; hint: Msg }> = [
  { id: 'ripple', label: msg('editor.phone.actionSheets.modes.ripple.label'), hint: msg('editor.phone.actionSheets.modes.ripple.hint') },
  { id: 'roll', label: msg('editor.phone.actionSheets.modes.roll.label'), hint: msg('editor.phone.actionSheets.modes.roll.hint') },
  { id: 'slip', label: msg('editor.phone.actionSheets.modes.slip.label'), hint: msg('editor.phone.actionSheets.modes.slip.hint') },
  { id: 'slide', label: msg('editor.phone.actionSheets.modes.slide.label'), hint: msg('editor.phone.actionSheets.modes.slide.hint') },
];

/** The shared "Problem" line: one plain sentence, cleared by the next press that works. */
function Problem({ text }: { text: string | null }) {
  const show = useHelperText();
  return text ? (
    <p className={classes.sheetProblem} role="alert" data-testid="phone-sheet-problem">
      {show(text)}
    </p>
  ) : null;
}

function Steppers({ label, edge, onStep, disabled, testId }: { label: string; edge: TrimEdge; onStep: (edge: TrimEdge, delta: number) => void; disabled: boolean; testId: string }) {
  const tm = useT();
  return (
    <div role="group" aria-label={label} data-testid={testId}>
      <Text size="sm" fw={500} mb={4}>
        {label}
      </Text>
      <div className={classes.sheetRow}>
        {STEPS.map((n) => (
          <Button
            key={n}
            variant="default"
            h={44}
            miw={64}
            disabled={disabled}
            onClick={() => onStep(edge, n)}
            aria-label={tm.msg(msg('editor-helpers-2.fmt.moveFrames', { label, direction: n > 0 ? 'later' : 'earlier', count: Math.abs(n) }))}
            data-testid={`${testId}${n > 0 ? '+' : '-'}${Math.abs(n)}`}
          >
            {n > 0 ? `+${n}` : n}
          </Button>
        ))}
      </div>
    </div>
  );
}

/** RM.5 trim sheet: In/Out/Duration, frame steppers for each edge, the trim mode, typed entry. One undo step per session. */
export function TrimSheetBody({ actions, onDone }: { actions: PhoneActions; onDone: () => void }) {
  const t = useT('editor.phone.actionSheets');
  const a = actions;
  const [problem, setProblem] = useState<string | null>(null);
  const ref = useRef(a);
  useEffect(() => {
    ref.current = a;
  });
  useEffect(() => {
    const start = ref.current.beginTrim();
    return () => ref.current.endTrim(start);
  }, []);
  if (!a.clip) return null;
  const step = (edge: TrimEdge, delta: number) => setProblem(a.trimStep(edge, delta));
  const mode = MODES.find((m) => m.id === a.trimMode) ?? MODES[0];
  return (
    <div className={classes.sheetStack} data-testid="trim-sheet">
      <Text size="sm" fw={600} data-testid="trim-sheet-clip">
        {a.clip.name}
      </Text>
      <ClipTimingFields item={a.clip.item} rate={a.rate} readOnly={a.readOnly} touch onApply={(field, frames) => a.trimNumeric(field, frames)} />
      <Steppers label={t('start')} edge="in" onStep={step} disabled={a.readOnly} testId="trim-start" />
      <Steppers label={t('end')} edge="out" onStep={step} disabled={a.readOnly} testId="trim-end" />
      <div role="group" aria-label={t('trimMode')} data-testid="trim-modes">
        <Text size="sm" fw={500} mb={4}>
          {t('trimMode2')}
        </Text>
        <div className={classes.sheetRow}>
          {MODES.map((m) => (
            <Button key={m.id} variant={a.trimMode === m.id ? 'filled' : 'default'} h={44} miw={72} aria-pressed={a.trimMode === m.id} onClick={() => a.setTrimMode(m.id)} data-testid={`trim-mode-chip-${m.id}`}>
              {t.msg(m.label)}
            </Button>
          ))}
        </div>
        <p className={classes.sheetHint}>{t.msg(mode.hint)}</p>
      </div>
      <Problem text={problem} />
      <Button h={44} onClick={onDone} data-testid="trim-done">
        {t('done')}
      </Button>
    </div>
  );
}

/** Frame steppers for the selected clips (a group in Select mode moves together). */
export function NudgeSheetBody({ actions }: { actions: PhoneActions }) {
  const t = useT('editor.phone.actionSheets');
  return (
    <div className={classes.sheetStack} data-testid="nudge-sheet">
      <div className={classes.sheetRow} role="group" aria-label={t('moveTheSelectedClips')}>
        {STEPS.map((n) => (
          <Button key={n} variant="default" h={44} miw={64} disabled={actions.readOnly} onClick={() => actions.nudge(n)} aria-label={t.msg(msg('editor-helpers-2.fmt.nudgeFrames', { direction: n > 0 ? 'later' : 'earlier', count: Math.abs(n) }))} data-testid={`phone-nudge${n > 0 ? '+' : '-'}${Math.abs(n)}`}>
            {n > 0 ? `+${n}` : n}
          </Button>
        ))}
      </div>
      <p className={classes.sheetHint}>{t('movesTheSelectedClips')}</p>
    </div>
  );
}


/** Everything else a clip can do, from the same handlers the desktop menu uses. */
export function MoreSheetBody({ actions, onClose, onOpen, onEnterSelect }: { actions: PhoneActions; onClose: () => void; onOpen: (section: ClipSheetSection) => void; onEnterSelect: () => void }) {
  const t = useT('editor.phone.actionSheets');
  const te = useT('editor-timeline.eventsLane');
  const [showEvents, setShowEvents] = useState(false);
  const a = actions;
  const clip = a.clip;
  const item = (id: string, label: string, run: () => void, opts: { disabled?: boolean; stay?: boolean; pressed?: boolean; danger?: boolean } = {}) => (
    <Button
      key={id}
      variant="default"
      h={44}
      justify="flex-start"
      disabled={opts.disabled}
      aria-pressed={opts.pressed}
      c={opts.danger ? 'var(--mantine-color-red-text)' : undefined}
      onClick={() => {
        run();
        if (!opts.stay) onClose();
      }}
      data-testid={`phone-more-${id}`}
    >
      {label}
    </Button>
  );
  const ro = a.readOnly;
  if (showEvents && a.events) return <PhoneEventsPanel host={a.events} onBack={() => setShowEvents(false)} onClose={onClose} />;
  return (
    <div className={classes.sheetStack} data-testid="more-sheet">
      <div className={classes.sheetGrid}>
        {item('select-several', t('more.selectSeveral'), onEnterSelect) /* eslint-disable-line i18next/no-literal-string -- ids */}
        {item('clip', t('more.fitPositionFades'), () => onOpen('picture'), { disabled: ro || !clip?.isMedia, stay: true }) /* eslint-disable-line i18next/no-literal-string -- ids */}
        {item('speed', t('more.speed'), () => onOpen('speed'), { disabled: ro || !clip?.isMedia, stay: true }) /* eslint-disable-line i18next/no-literal-string -- ids */}
        {item('objects', t('more.blurObject'), () => onOpen('objects'), { disabled: ro || !clip?.isMedia || !clip.onPictureTrack, stay: true }) /* eslint-disable-line i18next/no-literal-string -- ids */}
        {item('volume', t('more.volume'), () => onOpen('volume'), { disabled: ro || !clip?.isMedia, stay: true }) /* eslint-disable-line i18next/no-literal-string -- ids */}
        {item('move-earlier', t('more.moveEarlier'), () => a.moveInOrder(-1), { disabled: ro }) /* eslint-disable-line i18next/no-literal-string -- ids */}
        {item('move-later', t('more.moveLater'), () => a.moveInOrder(1), { disabled: ro }) /* eslint-disable-line i18next/no-literal-string -- ids */}
        {item('extract-audio', t('more.extractAudio'), a.extractAudio, { disabled: ro || !clip || !clip.isMedia || !clip.onPictureTrack }) /* eslint-disable-line i18next/no-literal-string -- ids */}
        {item('freeze', t('more.freeze'), a.freezeFrame, { disabled: ro || !clip?.isMedia || !clip.onPictureTrack }) /* eslint-disable-line i18next/no-literal-string -- ids */}
        {item('link', t('more.link'), a.link, { disabled: ro || a.selection.length !== 2 }) /* eslint-disable-line i18next/no-literal-string -- ids */}
        {item('unlink', t('more.unlink'), a.unlink, { disabled: ro || !clip?.linked }) /* eslint-disable-line i18next/no-literal-string -- ids */}
        {item('copy', t('more.copy'), a.copy, { disabled: a.selection.length === 0 }) /* eslint-disable-line i18next/no-literal-string -- ids */}
        {item('cut', t('more.cut'), a.cut, { disabled: ro }) /* eslint-disable-line i18next/no-literal-string -- ids */}
        {item('paste', t('more.paste'), a.paste, { disabled: ro }) /* eslint-disable-line i18next/no-literal-string -- ids */}
        {item('previous-marker', t('more.previousMarker'), a.previousMarker, { stay: true }) /* eslint-disable-line i18next/no-literal-string -- ids */}
        {item('next-marker', t('more.nextMarker'), a.nextMarker, { stay: true }) /* eslint-disable-line i18next/no-literal-string -- ids */}
        {item('clear-in-out', t('more.clearInOut'), a.clearInOut, { stay: true }) /* eslint-disable-line i18next/no-literal-string -- ids */}
        {item('snapping', a.snapping ? t('snappingIsOn') : t('snappingIsOff'), a.toggleSnapping, { stay: true, pressed: a.snapping }) /* eslint-disable-line i18next/no-literal-string -- ids */}
        {a.events && item('events', te('phone.open'), () => setShowEvents(true), { stay: true }) /* eslint-disable-line i18next/no-literal-string -- ids */}
        {item('lift', t('more.lift'), a.lift, { disabled: ro, danger: true }) /* eslint-disable-line i18next/no-literal-string -- ids */}
      </div>
    </div>
  );
}
