'use client';

import useSWR from 'swr';
import { Button, Checkbox, Group, Select, Skeleton, Stack, Switch, Text } from '@mantine/core';
import { fetcher } from '@/lib/api/client';
import { useT } from '@/i18n';
import type { AppManifestInput } from '@/lib/types/workflow';
import { MAX_ALLOWED_MODELS } from '@/lib/workflow-apps/app-input-builder';
import { costWords, unavailableMessage, type ModelOptionsResponse } from './app-input-model';
import { modelOptionsPath } from './ModelPicker';

interface AllowedModelsEditorProps {
  input: AppManifestInput;
  onChange: (next: AppManifestInput) => void;
  projectId: string;
  workflowId: string;
  /** The list is asked of the server by the input's id, so an input not saved yet has none. */
  saved: boolean;
}

/**
 * "Allowed models" and "Recommended" for a `model` field (docs/design/app-inputs.md 8.2, O1, 24): by default people may
 * pick any model of the kind the workspace has; the creator can narrow the list and say which one is recommended. The
 * list is the options route in the builder's view (`?all=true`: every model of the kind, the saved list ignored).
 */
export function AllowedModelsEditor({ input, onChange, projectId, workflowId, saved }: AllowedModelsEditorProps) {
  const t = useT('apps.builder.models');
  const run = useT('apps.run.inputs');
  const key = saved && input.id ? `${modelOptionsPath(projectId, workflowId, input.id)}?all=true` : null;
  const { data, error, isLoading, mutate } = useSWR<ModelOptionsResponse>(key, fetcher, { revalidateOnFocus: false });
  const options = data?.options ?? [];
  const allowed = input.allowed ?? [];
  const any = allowed.length === 0;
  const recommendable = options.filter((o) => o.available && (any || allowed.includes(o.id)));

  if (!saved) return <Text size="sm" c="dimmed">{t('saveFirst')}</Text>;
  if (isLoading) return <Skeleton height={60} aria-label={t('loading')} />;
  if (error && !data) {
    return (
      <Group gap="sm" role="alert">
        <Text size="sm">{t('loadFailed')}</Text>
        <Button size="sm" variant="default" onClick={() => { void mutate(); }}>{t('tryAgain')}</Button>
      </Group>
    );
  }
  if (options.length === 0) return <Text size="sm" c="dimmed">{t('none')}</Text>;

  const usable = options.filter((o) => o.available);
  return (
    <Stack gap="xs">
      <Switch
        label={t('any')}
        description={t('anyHint')}
        checked={any}
        onChange={(e) => {
          // Narrowing starts from every usable model, so nothing disappears until the creator unticks it.
          const next = e.currentTarget.checked ? null : usable.slice(0, MAX_ALLOWED_MODELS).map((o) => o.id);
          onChange({ ...input, allowed: next, recommended: next && input.recommended && !next.includes(input.recommended) ? null : input.recommended });
        }}
        styles={{ root: { minHeight: 44, display: 'flex', alignItems: 'center' } }}
      />
      {!any && (
        <Checkbox.Group
          label={t('allowed')}
          value={allowed}
          onChange={(ids) => {
            const next = ids.slice(0, MAX_ALLOWED_MODELS);
            onChange({ ...input, allowed: next.length > 0 ? next : null, recommended: input.recommended && next.includes(input.recommended) ? input.recommended : null });
          }}
        >
          <Stack gap={4} mt={4}>
            {options.map((o) => {
              const words = o.available ? costWords(o, options) : null;
              return (
                <Checkbox
                  key={o.id}
                  value={o.id}
                  disabled={!o.available}
                  label={o.label}
                  description={o.available ? (words ? run.msg(words) : undefined) : run.msg(unavailableMessage(o))}
                  styles={{ body: { minHeight: 44, alignItems: 'center' } }}
                />
              );
            })}
          </Stack>
        </Checkbox.Group>
      )}
      <Select
        label={t('recommended')}
        description={t('recommendedHint')}
        size="sm"
        data={recommendable.map((o) => ({ value: o.id, label: o.label }))}
        value={input.recommended && recommendable.some((o) => o.id === input.recommended) ? input.recommended : null}
        onChange={(v) => onChange({ ...input, recommended: v })}
        placeholder={t('recommendedDefault')}
        clearable
        comboboxProps={{ withinPortal: true }}
      />
    </Stack>
  );
}
