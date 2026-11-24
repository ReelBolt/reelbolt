'use client';

import React, { useState } from 'react';
import { Group, Stack, Text, TextInput, Tooltip } from '@mantine/core';
import { EditorIcon } from '../ui';
import { IconRestore } from '@tabler/icons-react';
import { LiveSlider } from '../shell/LiveSlider';
import { FADER_MARKS, FADER_MAX_DB, FADER_MIN_DB, FADER_RESET_DB, FADER_STEP_DB, formatFaderDb, parseFaderDb } from '@/lib/editing/audio/fader';
import { useT } from '@/i18n';
import classes from './audio.module.css';

export interface FaderProps {
  /** The track's static gain in dB (the document's `gainDb.value`). */
  value: number;
  /** Shown beside the label when the fader is automated. */
  automated?: boolean;
  disabled: boolean;
  label: string;
  testId: string;
  onCommit: (db: number) => void;
}

/**
 * R08.3: the one fader. dB scale marks from −∞ to +12, a live readout while dragging, a typed dB
 * field (Enter or leaving the field commits), and a double-click on the slider (or the 0 dB button
 * on a touch screen) back to 0 dB. One commit = one op = one undo step.
 */
export function Fader({ value, automated, disabled, label, testId, onCommit }: FaderProps) {
  const t = useT('editor-audio.fader');
  const [draft, setText] = useState('');
  const [editing, setEditing] = useState(false);
  const text = editing ? draft : formatFaderDb(value);

  const commitText = () => {
    setEditing(false);
    const parsed = parseFaderDb(draft);
    if (parsed === null) return;
    if (parsed !== value) onCommit(parsed);
  };

  return (
    <Stack gap={2} style={{ flex: '1 1 150px', minWidth: 150 }}>
      <Group justify="space-between" gap={4} wrap="nowrap">
        <Text size="xs" c="dimmed">
          {automated ? t('volumeAutomated') : t('volume')}
        </Text>
        <Group gap={2} wrap="nowrap">
        <Tooltip label={t('resetTooltip')}>
          <EditorIcon icon={IconRestore} size="sm" tooltip={false} label={t('resetLabel', { label })} disabled={disabled || value === FADER_RESET_DB} onClick={() => onCommit(FADER_RESET_DB)} data-testid={`${testId}-reset`} />
        </Tooltip>
        <TextInput
          size="xs"
          w={76}
          aria-label={t('inputLabel', { label })}
          inputMode="decimal"
          value={text}
          disabled={disabled}
          rightSection={<Text size="xs" c="dimmed">dB</Text>}
          rightSectionWidth={26}
          classNames={{ input: classes.touchInput }}
          className={classes.touchInput}
          onFocus={() => {
            setText(formatFaderDb(value));
            setEditing(true);
          }}
          onChange={(e) => setText(e.currentTarget.value)}
          onBlur={commitText}
          onKeyDown={(e) => {
            if (e.key === 'Enter') e.currentTarget.blur();
            if (e.key === 'Escape') {
              setEditing(false);
              setText(formatFaderDb(value));
              e.currentTarget.blur();
            }
          }}
          data-testid={`${testId}-db`}
        />
        </Group>
      </Group>
      <div onDoubleClick={() => !disabled && value !== FADER_RESET_DB && onCommit(FADER_RESET_DB)} title={t('doubleClick')} data-testid={`${testId}-wrap`} style={{ paddingTop: 10, paddingBottom: 18 }}>
        <LiveSlider
          size="xs"
          min={FADER_MIN_DB}
          max={FADER_MAX_DB}
          step={FADER_STEP_DB}
          value={Math.max(FADER_MIN_DB, Math.min(FADER_MAX_DB, value))}
          disabled={disabled}
          marks={FADER_MARKS.map((m) => ({ value: m.value, label: m.label }))}
          label={(v) => `${formatFaderDb(v)} dB`}
          thumbLabel={`${label} volume`}
          onChangeEnd={onCommit}
          styles={{ markLabel: { fontSize: 12 } }}
          data-testid={testId}
        />
      </div>
    </Stack>
  );
}
