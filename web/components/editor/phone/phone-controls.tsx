'use client';

import React, { useState } from 'react';
import { Button, NumberInput, Slider, Text } from '@mantine/core';
import classes from './phone.module.css';
import { useT } from '@/i18n';
import { useHelperText } from './helper-text';

/** A plain refusal line under a control: one sentence, cleared by the next change that works. */
export function Refusal({ text, testId = 'phone-sheet-problem' }: { text: string | null; testId?: string }) {
  const show = useHelperText();
  return text ? (
    <p className={classes.sheetProblem} role="alert" data-testid={testId}>
      {show(text)}
    </p>
  ) : null;
}

export interface SliderFieldProps {
  label: string;
  value: number;
  min: number;
  max: number;
  step: number;
  /** Appended to the readout, e.g. "%". */
  unit?: string;
  decimals?: number;
  disabled?: boolean;
  /** Called once on release (or on Enter / leaving the number box). A plain sentence when refused, else null. */
  onCommit: (value: number) => string | null;
  testId: string;
  /** Replaces the controls with this plain line (e.g. "animated on desktop"). */
  readOnlyNote?: string;
}

/**
 * A touch slider with a numeric fallback: the thumb is 28 px in a 44 px row, the number box is 44 px high.
 * A drag shows a draft and commits ONCE on release (one undo step); a typed value commits on Enter or blur.
 */
export function SliderField({ label, value, min, max, step, unit = '', decimals = 0, disabled, onCommit, testId, readOnlyNote }: SliderFieldProps) {
  const t = useT('editor.phone.phonecontrols');
  const [draft, setDraft] = useState<number | null>(null);
  const [text, setText] = useState<number | string>(value);
  const [problem, setProblem] = useState<string | null>(null);
  // The box follows the document whenever the stored value changes (derived during render, not in an effect).
  const [seen, setSeen] = useState(value);
  if (seen !== value) {
    setSeen(value);
    setText(value);
  }
  const commit = (v: number) => {
    const p = onCommit(v);
    setProblem(p);
    if (p) setText(value);
    return p;
  };
  if (readOnlyNote) {
    return (
      <div className={classes.sheetStack} style={{ padding: 0, gap: 4 }} data-testid={`${testId}-readonly`}>
        <Text size="sm" fw={500}>
          {label}
        </Text>
        <p className={classes.sheetHint}>{readOnlyNote}</p>
      </div>
    );
  }
  const shown = draft ?? value;
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }} data-testid={testId}>
      <Text size="sm" fw={500} id={`${testId}-label`}>
        {label}
      </Text>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, minHeight: 44 }}>
        <Slider
          style={{ flex: 1 }}
          min={min}
          max={max}
          step={step}
          value={shown}
          disabled={disabled}
          onChange={setDraft}
          onChangeEnd={(v) => {
            setDraft(null);
            if (v !== value) commit(v);
          }}
          label={(v) => `${Number(v.toFixed(decimals))}${unit}`}
          thumbLabel={label}
          size="lg"
          styles={{ thumb: { width: 28, height: 28 }, root: { paddingBlock: 8 } }}
          data-testid={`${testId}-slider`}
        />
        <NumberInput
          w={92}
          size="md"
          styles={{ input: { height: 44, minHeight: 44 } }}
          hideControls
          min={min}
          max={max}
          step={step}
          decimalScale={decimals}
          suffix={unit}
          value={draft ?? text}
          disabled={disabled}
          allowNegative={min < 0}
          aria-label={t('typeANumber', { label })}
          onChange={setText}
          onBlur={() => {
            const n = typeof text === 'number' ? text : Number.parseFloat(String(text));
            if (!Number.isFinite(n)) {
              setText(value);
              return;
            }
            if (n !== value) commit(n);
          }}
          onKeyDown={(e) => {
            e.stopPropagation();
            if (e.key === 'Enter') e.currentTarget.blur();
          }}
          data-testid={`${testId}-number`}
        />
      </div>
      <Refusal text={problem} testId={`${testId}-problem`} />
    </div>
  );
}

/** A row of 44 px choice chips (one pressed). */
export function ChipRow<T extends string | number>({ label, options, value, onPick, disabled, testIdPrefix }: { label: string; options: ReadonlyArray<{ id: T; label: string }>; value: T | null; onPick: (id: T) => void; disabled?: boolean; testIdPrefix: string }) {
  return (
    <div role="group" aria-label={label}>
      <div className={classes.sheetRow}>
        {options.map((o) => (
          <Button key={String(o.id)} variant={value === o.id ? 'filled' : 'default'} h={44} miw={64} aria-pressed={value === o.id} disabled={disabled} onClick={() => onPick(o.id)} data-testid={`${testIdPrefix}-${String(o.id)}`}>
            {o.label}
          </Button>
        ))}
      </div>
    </div>
  );
}
