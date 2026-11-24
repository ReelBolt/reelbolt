'use client';

import { useId, useMemo, useState } from 'react';
import { Button, Paper, Stack, Text, TextInput } from '@mantine/core';
import { useT } from '@/i18n';
import type { AppManifest, AppManifestInput } from '@/lib/types/workflow';
import { inputName } from '@/lib/workflow-apps/app-inputs';
import { AppInputField, authored } from './AppInputField';
import { initialValues, isAppInput, isVisible, pageLayout, type InputValue, type InputValues } from './app-input-model';
import styles from './AppRunPage.module.css';

interface AppPagePreviewProps {
  manifest: AppManifest;
  projectId: string;
  workflowId: string;
}

/**
 * What a person will see when they open the app (docs/design/app-inputs.md 13.1, 24), drawn with the run page's own
 * layout (`pageLayout`) and fields (`AppInputField`), so the preview cannot drift from the page. Values typed here are
 * only for trying it out; nothing is sent. A model field shows a stand-in: its cards need the saved page.
 */
export function AppPagePreview({ manifest, projectId, workflowId }: AppPagePreviewProps) {
  const t = useT('apps.builder.preview');
  const apps = useT('apps');
  const root = useT();
  const moreId = useId();
  const layout = useMemo(() => pageLayout(manifest), [manifest]);
  // A new set of fields starts from their defaults again; edits to words keep what was typed.
  const shapeKey = layout.all.map((i) => `${i.kind}:${i.id ?? i.field ?? i.role}:${i.type ?? ''}:${JSON.stringify(i.default ?? null)}`).join('|');
  const [state, setState] = useState<{ key: string; values: InputValues }>(() => ({ key: shapeKey, values: initialValues(layout.all) }));
  const values = state.key === shapeKey ? state.values : initialValues(layout.all);
  const [moreOpen, setMoreOpen] = useState(false);
  const setValue = (id: string, value: InputValue) => setState({ key: shapeKey, values: { ...values, [id]: value } });

  const render = (input: AppManifestInput, i: number) => {
    if (isAppInput(input)) {
      if (!isVisible(input, values)) return null;
      if (input.type === 'model') {
        return (
          <div key={input.id} className={styles.field}>
            <Text size="sm" fw={500}>{authored(apps, input.labelKey, input.label) ?? input.id}</Text>
            <Text size="sm" c="dimmed">{t('modelStandIn')}</Text>
          </div>
        );
      }
      return (
        <AppInputField
          key={input.id}
          input={input}
          value={values[input.id] ?? null}
          onChange={(v) => setValue(input.id, v)}
          projectId={projectId}
          workflowId={workflowId}
          phone={false}
        />
      );
    }
    // Brief fields, file slots and notes are drawn by the run page's own pickers; here a plain stand-in with the label.
    return (
      <TextInput
        key={`${input.kind}-${i}`}
        label={inputName(input, root)}
        placeholder={input.kind === 'files' ? t('filesStandIn') : t('briefStandIn')}
        readOnly
        className={styles.field}
      />
    );
  };

  return (
    <Paper withBorder p="md" radius="md" aria-label={t('title')} data-testid="app-page-preview">
      <Stack gap="sm">
        <Text size="xs" c="dimmed">{t('note')}</Text>
        <Text fw={600}>{manifest.title?.trim() || t('untitled')}</Text>
        {manifest.tagline && <Text size="sm" c="dimmed">{manifest.tagline}</Text>}
        {layout.shown.length === 0 && layout.more.length === 0 && <Text size="sm" c="dimmed">{t('empty')}</Text>}
        {layout.shown.map(render)}
        {layout.more.length > 0 && (
          <>
            <Button
              variant="subtle"
              size="sm"
              aria-expanded={moreOpen}
              aria-controls={moreId}
              onClick={() => setMoreOpen((o) => !o)}
              style={{ alignSelf: 'flex-start' }}
            >
              {t('moreOptions', { count: layout.more.length })}
            </Button>
            {moreOpen && <Stack id={moreId} gap="sm">{layout.more.map((input, i) => render(input, layout.shown.length + i))}</Stack>}
          </>
        )}
      </Stack>
    </Paper>
  );
}
