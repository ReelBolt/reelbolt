'use client';

import { Stack, Text } from '@mantine/core';
import { useT } from '@/i18n';
import type { RunAppInput } from '@/lib/types/workflow';
import { runInputWords } from './run-app-inputs';

/**
 * Every app field a run was started with (docs/design/app-inputs.md 10.4): its label of the time and its value in
 * words, a model by name. A description list, so a screen reader pairs each label with its value.
 */
export function RunAppInputsList({ inputs }: { inputs: readonly RunAppInput[] }) {
  const t = useT();
  const rows = inputs.map((i) => runInputWords(t, i));
  return (
    <Stack gap={4} data-testid="run-app-inputs">
      <Text size="sm" fw={600} id="run-app-inputs-heading">{t('apps.run.history.heading')}</Text>
      <dl aria-labelledby="run-app-inputs-heading" style={{ margin: 0, display: 'grid', gridTemplateColumns: 'minmax(0, max-content) minmax(0, 1fr)', columnGap: 12, rowGap: 4 }}>
        {rows.map((r) => (
          <div key={r.id} style={{ display: 'contents' }}>
            <Text component="dt" size="sm" c="dimmed">{r.label}</Text>
            <Text component="dd" size="sm" style={{ margin: 0, overflowWrap: 'anywhere', whiteSpace: 'pre-wrap' }}>{r.value}</Text>
          </div>
        ))}
      </dl>
    </Stack>
  );
}
