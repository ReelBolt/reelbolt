'use client';

import { useId, useState } from 'react';
import useSWR from 'swr';
import { Skeleton, Text } from '@mantine/core';
import { fetcher } from '@/lib/api/client';
import { useT } from '@/i18n';
import { authored } from './AppInputField';
import { costWords, recipeModelOptionsPath, unavailableMessage, type ModelOptionsResponse } from './app-input-model';
import styles from './AppsGallery.module.css';

/**
 * "Model choices" on a recipe card (docs/design/app-inputs.md 28): before the recipe is created, the models each of its model
 * fields offers, recommended first, the price in words, and why a model cannot be chosen (a kind the plan lacks included).
 * Read on demand only, from the same options source as a created app's run page.
 */
export function RecipeModelChoices({ projectId, recipeKey }: { projectId: string; recipeKey: string }) {
  const t = useT('apps.gallery');
  const tInputs = useT('apps.run.inputs');
  const apps = useT('apps');
  const listId = useId();
  const [open, setOpen] = useState(false);
  const { data, error, isLoading } = useSWR<ModelOptionsResponse[]>(
    open ? recipeModelOptionsPath(projectId, recipeKey) : null,
    fetcher,
  );

  return (
    <div className={styles.modelChoices}>
      <button
        type="button"
        className={styles.modelChoicesToggle}
        aria-expanded={open}
        aria-controls={listId}
        onClick={() => setOpen((o) => !o)}
      >
        {open ? t('hideModelChoices') : t('modelChoices')}
      </button>
      {open && (
        <div id={listId}>
          {error ? (
            <Text size="sm" className={styles.hint}>{t('modelChoicesFailed')}</Text>
          ) : isLoading || !data ? (
            <Skeleton height={14} mt={6} />
          ) : data.length === 0 ? (
            <Text size="sm" className={styles.hint}>{t('noModelChoices')}</Text>
          ) : (
            data.map((field) => (
              <div key={field.inputId}>
                <Text size="sm" fw={500} mt={6}>{authored(apps, field.labelKey, field.label) ?? field.inputId}</Text>
                <ul className={styles.modelChoiceList}>
                  {field.options.map((option) => {
                    const words = option.available ? costWords(option, field.options) : unavailableMessage(option);
                    return (
                      <li key={option.id}>
                        {option.label}
                        {option.recommended && <> · {tInputs('recommended')}</>}
                        {words && <> · {tInputs.msg(words)}</>}
                      </li>
                    );
                  })}
                </ul>
              </div>
            ))
          )}
        </div>
      )}
    </div>
  );
}
