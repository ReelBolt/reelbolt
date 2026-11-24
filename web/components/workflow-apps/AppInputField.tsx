'use client';

import { useId } from 'react';
import { NumberInput, SegmentedControl, Select, Switch, Text, TextInput, Textarea } from '@mantine/core';
import { useT } from '@/i18n';
import type { T } from '@/i18n/translator';
import type { AppManifestInput } from '@/lib/types/workflow';
import { inputFieldId, type InputValue } from './app-input-model';
import { ModelPicker } from './ModelPicker';
import styles from './AppRunPage.module.css';

/** The shapes a `shape` input offers, in the order the page shows them (4.2). */
const SHAPES = ['9:16', '1:1', '16:9', '4:5'] as const;
const SHAPE_WORD: Record<(typeof SHAPES)[number], string> = { '9:16': 'vertical', '1:1': 'square', '16:9': 'landscape', '4:5': 'portrait' };

/** The longest text the page offers for a type when the manifest names no cap (4.2). */
const TEXT_CAP = { shortText: 200, longText: 7000 } as const;

/** Segmented controls up to this many options; a select beyond. */
const MAX_SEGMENTS = 4;

/** Longest option label (characters) that still sits in one row of a phone-width segmented control. */
const PHONE_ROW_LABEL_CHARS = 10;

/** Total characters across a row's labels beyond which a phone stacks the options instead of squeezing them. */
const PHONE_ROW_TOTAL_CHARS = 26;

/** Whether a segmented control's labels fit side by side on a phone; if not, the options are stacked (full width, 44 px each). */
export function labelsFitOneRow(labels: readonly string[]): boolean {
  return labels.every((l) => l.length <= PHONE_ROW_LABEL_CHARS) && labels.join('').length <= PHONE_ROW_TOTAL_CHARS;
}

/** A curated recipe's catalogue key (in `apps.json`) when this build has it, else the author's own words as written. */
export function authored(t: T, key: string | null | undefined, text: string | null | undefined): string | undefined {
  if (key && t.has(key)) return t(key);
  const plain = text?.trim();
  return plain ? plain : undefined;
}

interface AppInputFieldProps {
  input: AppManifestInput & { id: string };
  value: InputValue;
  onChange: (value: InputValue) => void;
  /** A sentence under the field: a run-gate refusal or a missing required value. */
  error?: string | undefined;
  projectId: string;
  workflowId: string;
  phone: boolean;
  /** Bumped by the `chooseModel` fix. */
  openSignal?: number;
}

/**
 * One `input` app input on the run page (docs/design/app-inputs.md 4.2, 12.1): a plain label tied to its control, the
 * help line as its description, the example as placeholder text (never sent), a counter on long text.
 */
export function AppInputField({ input, value, onChange, error, projectId, workflowId, phone, openSignal }: AppInputFieldProps) {
  const t = useT('apps.run.inputs');
  const apps = useT('apps');
  const page = useT('apps.run.page');
  const id = inputFieldId(input.id);
  const counterId = useId();
  const label = authored(apps, input.labelKey, input.label) ?? input.id;
  const help = authored(apps, input.helpKey, input.help);
  const example = authored(apps, input.exampleKey, input.example);
  const required = input.required === true;

  switch (input.type) {
    case 'shortText':
    case 'longText': {
      const cap = Math.min(input.maxLength ?? TEXT_CAP[input.type], TEXT_CAP[input.type]);
      const text = typeof value === 'string' ? value : '';
      // The counter is shown always and read out only near the cap, so typing is not announced letter by letter.
      const near = text.length >= cap * 0.9;
      const counter = (
        <>
          <span aria-hidden="true">{t('counter', { count: text.length, max: cap })}</span>
          <span id={counterId} aria-live="polite" className={styles.visuallyHidden}>
            {near ? t('counterLeft', { left: Math.max(0, cap - text.length) }) : ''}
          </span>
        </>
      );
      const common = {
        id,
        label,
        description: help,
        placeholder: example,
        required,
        maxLength: cap,
        value: text,
        error,
        className: styles.field,
        onChange: (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => onChange(e.currentTarget.value),
      };
      return input.type === 'longText' ? (
        <div>
          <Textarea {...common} autosize minRows={3} maxRows={12} />
          <Text size="sm" className={`${styles.muted} ${styles.counter}`}>
            {counter}
          </Text>
        </div>
      ) : (
        <TextInput {...common} />
      );
    }
    case 'number':
    case 'duration': {
      const choices = (input.choices ?? []).filter((c): c is number => typeof c === 'number');
      const unit = input.type === 'duration' ? 'seconds' : input.unit ?? null;
      if (choices.length > 0 && choices.length <= MAX_SEGMENTS && typeof value === 'number') {
        return (
          <div className={styles.field}>
            <span className={styles.fieldLabel} id={`${id}-label`}>
              {label}
            </span>
            {help && (
              <Text size="sm" className={styles.muted} id={`${id}-help`} mb={6}>
                {help}
              </Text>
            )}
            <SegmentedControl
              id={id}
              aria-labelledby={`${id}-label`}
              aria-describedby={help ? `${id}-help` : undefined}
              className={styles.segments}
              fullWidth
              value={String(value)}
              onChange={(v) => onChange(Number(v))}
              data={choices.map((c) => ({ value: String(c), label: unit === 'seconds' ? page('seconds', { count: c }) : unitText(t, unit, c) }))}
            />
            {error && <Text size="sm" className={styles.fieldError} role="alert">{error}</Text>}
          </div>
        );
      }
      if (choices.length > 0) {
        return (
          <Select
            id={id}
            label={label}
            description={help}
            required={required}
            error={error}
            data={choices.map((c) => ({ value: String(c), label: unit === 'seconds' ? page('seconds', { count: c }) : unitText(t, unit, c) }))}
            value={typeof value === 'number' ? String(value) : null}
            onChange={(v) => onChange(v === null ? null : Number(v))}
            placeholder={t('choose')}
            comboboxProps={{ withinPortal: true }}
            className={styles.field}
          />
        );
      }
      return (
        <NumberInput
          id={id}
          label={label}
          description={help}
          required={required}
          error={error}
          min={input.min ?? undefined}
          max={input.max ?? undefined}
          step={input.step ?? 1}
          suffix={unit ? ` ${t(`unitSuffix.${unit}`)}` : undefined}
          value={typeof value === 'number' ? value : ''}
          onChange={(v) => onChange(typeof v === 'number' ? v : null)}
          className={styles.field}
        />
      );
    }
    case 'choice': {
      const options = (input.options ?? []).map((o) => ({
        value: String(o.value),
        label: authored(apps, o.labelKey, o.label) ?? String(o.value),
        hint: o.hint?.trim() || null,
      }));
      const current = value === null || value === undefined ? null : String(value);
      const hint = options.find((o) => o.value === current)?.hint;
      // Back to the option's own type (a number option sends a number).
      const typed = (v: string | null): InputValue => {
        const option = (input.options ?? []).find((o) => String(o.value) === v);
        return option ? option.value : null;
      };
      if (options.length <= MAX_SEGMENTS && current !== null) {
        return (
          <div className={styles.field}>
            <span className={styles.fieldLabel} id={`${id}-label`}>
              {label}
            </span>
            {help && (
              <Text size="sm" className={styles.muted} id={`${id}-help`} mb={6}>
                {help}
              </Text>
            )}
            <SegmentedControl
              id={id}
              aria-labelledby={`${id}-label`}
              aria-describedby={help ? `${id}-help` : undefined}
              className={styles.segments}
              fullWidth
              orientation={phone && !labelsFitOneRow(options.map((o) => o.label)) ? 'vertical' : 'horizontal'}
              value={current}
              onChange={(v) => onChange(typed(v))}
              data={options.map((o) => ({ value: o.value, label: o.label }))}
            />
            {hint && <Text size="sm" className={styles.muted} mt={4}>{hint}</Text>}
            {error && <Text size="sm" className={styles.fieldError} role="alert">{error}</Text>}
          </div>
        );
      }
      return (
        <Select
          id={id}
          label={label}
          description={hint ?? help}
          required={required}
          error={error}
          data={options.map((o) => ({ value: o.value, label: o.label }))}
          value={current}
          onChange={(v) => onChange(typed(v))}
          placeholder={t('choose')}
          allowDeselect={!required}
          comboboxProps={{ withinPortal: true }}
          className={styles.field}
        />
      );
    }
    case 'toggle': {
      const on = value === true;
      const state = on ? input.onLabel?.trim() : input.offLabel?.trim();
      return (
        <div className={styles.field}>
          <Switch
            id={id}
            label={label}
            description={state || help}
            checked={on}
            onChange={(e) => onChange(e.currentTarget.checked)}
            error={error}
            styles={{ root: { minHeight: 44, display: 'flex', alignItems: 'center' } }}
          />
        </div>
      );
    }
    case 'shape': {
      const offered = (input.choices ?? []).map(String).filter((c): c is (typeof SHAPES)[number] => (SHAPES as readonly string[]).includes(c));
      const shapes = offered.length > 0 ? SHAPES.filter((s) => offered.includes(s)) : [...SHAPES];
      const current = typeof value === 'string' && (shapes as readonly string[]).includes(value) ? value : null;
      return (
        <div className={styles.field}>
          <span className={styles.fieldLabel} id={`${id}-label`}>
            {label}
          </span>
          {help && (
            <Text size="sm" className={styles.muted} id={`${id}-help`} mb={6}>
              {help}
            </Text>
          )}
          <SegmentedControl
            id={id}
            aria-labelledby={`${id}-label`}
            aria-describedby={help ? `${id}-help` : undefined}
            className={styles.segments}
            fullWidth
            value={current ?? ''}
            onChange={(v) => onChange(v)}
            data={shapes.map((s) => ({
              value: s,
              label: (
                <span className={styles.shapeChip}>
                  <span aria-hidden="true" className={styles.shapeIcon} data-shape={s} />
                  <span>{t(`shape.${SHAPE_WORD[s]}`)}</span>
                </span>
              ),
            }))}
          />
          {error && <Text size="sm" className={styles.fieldError} role="alert">{error}</Text>}
        </div>
      );
    }
    case 'model':
      return (
        <ModelPicker
          projectId={projectId}
          workflowId={workflowId}
          inputId={input.id}
          fieldId={id}
          label={label}
          help={help}
          value={typeof value === 'string' ? value : null}
          onChange={onChange}
          error={error}
          phone={phone}
          openSignal={openSignal}
        />
      );
    default:
      return null;
  }
}

function unitText(t: T, unit: string | null, count: number): string {
  return unit === 'takes' || unit === 'percent' ? t(`unit.${unit}`, { count }) : String(count);
}
