'use client';

import { useEffect, useId, useState } from 'react';
import useSWR from 'swr';
import { Badge, Button, Drawer, Skeleton, Text } from '@mantine/core';
import { fetcher } from '@/lib/api/client';
import { useT } from '@/i18n';
import { costWords, unavailableMessage, type ModelOption, type ModelOptionsResponse } from './app-input-model';
import styles from './AppRunPage.module.css';

interface ModelPickerProps {
  projectId: string;
  workflowId: string;
  inputId: string;
  /** The control's id (focus target of the `chooseModel` fix). */
  fieldId: string;
  label: string;
  help?: string | undefined;
  value: string | null;
  onChange: (id: string | null) => void;
  /** A refusal or check sentence shown under the cards. */
  error?: string | undefined;
  phone: boolean;
  /** Bumped by the `chooseModel` fix: on a phone the sheet opens. */
  openSignal?: number;
}

export function modelOptionsPath(projectId: string, workflowId: string, inputId: string): string {
  return `/api/v1/projects/${projectId}/workflows/${workflowId}/app/inputs/${encodeURIComponent(inputId)}/options`;
}

/**
 * A `model` app input (docs/design/app-inputs.md 8.2, 12.1): model cards with the name, one note and the relative price in
 * words, "Recommended" first; a model that cannot be chosen says why. Nothing chosen yet picks the recommended model.
 * On a phone the cards open in a bottom sheet from one button that names the current choice.
 */
export function ModelPicker({ projectId, workflowId, inputId, fieldId, label, help, value, onChange, error, phone, openSignal = 0 }: ModelPickerProps) {
  const t = useT('apps.run.inputs');
  const name = useId();
  const helpId = useId();
  const errorId = useId();
  const { data, error: loadError, mutate } = useSWR<ModelOptionsResponse>(modelOptionsPath(projectId, workflowId, inputId), fetcher, {
    revalidateOnFocus: false,
  });
  const options = data?.options ?? [];
  const recommended = options.find((o) => o.recommended && o.available);

  // Nothing chosen: the recommended model is the choice, so the price and the run use the model the page shows.
  useEffect(() => {
    if (value === null && recommended) onChange(recommended.id);
  }, [value, recommended, onChange]);

  const [sheet, setSheet] = useState(false);
  // The `chooseModel` fix bumps the signal: on a phone the sheet opens (state adjusted while rendering, not in an effect).
  const [seenSignal, setSeenSignal] = useState(openSignal);
  if (openSignal !== seenSignal) {
    setSeenSignal(openSignal);
    if (phone) setSheet(true);
  }

  const describedBy = [help ? helpId : null, error ? errorId : null].filter(Boolean).join(' ') || undefined;
  const selected = options.find((o) => o.id === value);

  const card = (option: ModelOption, index: number) => {
    const words = option.available ? costWords(option, options) : null;
    const checked = option.id === value;
    return (
      <label
        key={option.id}
        className={`${styles.modelCard} ${checked ? styles.modelCardSelected : ''} ${option.available ? '' : styles.modelCardDisabled}`}
      >
        <input
          type="radio"
          name={name}
          value={option.id}
          checked={checked}
          disabled={!option.available}
          // The first card is the focus target of the fix; on a phone the trigger button is.
          id={!phone && index === 0 ? fieldId : undefined}
          onChange={() => {
            onChange(option.id);
            if (phone) setSheet(false);
          }}
          className={styles.modelRadio}
        />
        <span className={styles.modelText}>
          <span className={styles.modelName}>
            {option.label}
            {option.recommended && (
              <Badge size="sm" variant="light" ml={8} component="span">
                {t('recommended')}
              </Badge>
            )}
          </span>
          {option.note && !option.recommended && <span className={styles.modelNote}>{option.note}</span>}
          {option.available
            ? words && <span className={styles.modelNote}>{t.msg(words)}</span>
            : <span className={styles.modelNote}>{t.msg(unavailableMessage(option))}</span>}
        </span>
      </label>
    );
  };

  const list = (
    <div role="radiogroup" aria-label={label} aria-describedby={describedBy} className={styles.modelList}>
      {options.map(card)}
    </div>
  );

  let body: React.ReactNode;
  if (loadError && !data) {
    body = (
      <div className={styles.modelNote}>
        <Text size="sm">{t('modelsUnavailable')}</Text>
        <Button variant="subtle" size="compact-sm" onClick={() => void mutate()} mt={4}>
          {t('tryAgain')}
        </Button>
      </div>
    );
  } else if (!data) {
    body = <Skeleton height={56} aria-label={t('modelsLoading')} />;
  } else if (options.length === 0) {
    body = <Text size="sm">{t('noModels')}</Text>;
  } else if (phone) {
    body = (
      <>
        <Button id={fieldId} variant="default" fullWidth className={styles.modelTrigger} onClick={() => setSheet(true)} aria-describedby={describedBy}>
          {selected ? t('modelChosen', { name: selected.label }) : t('chooseModel')}
        </Button>
        <Drawer opened={sheet} onClose={() => setSheet(false)} position="bottom" title={label} size="auto">
          {list}
        </Drawer>
      </>
    );
  } else {
    body = list;
  }

  return (
    <div className={styles.field} data-testid={`app-input-model-${inputId}`}>
      <span className={styles.fieldLabel}>{label}</span>
      {help && (
        <Text id={helpId} size="sm" className={styles.muted} mb={6}>
          {help}
        </Text>
      )}
      {body}
      {error && (
        <Text id={errorId} size="sm" c="red" mt={6} role="alert">
          {error}
        </Text>
      )}
    </div>
  );
}
