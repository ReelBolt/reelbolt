'use client';

import { Select, Switch, Text, Stack } from '@mantine/core';
import { useProjectExecutions } from '@/lib/hooks/use-executions';
import { useT } from '@/i18n';
import type { StepData } from './WorkflowStepList';
import { useFd } from './config-kit';

interface AgentStepOptionsProps {
  step: Pick<StepData, 'continueComposition' | 'registerOutputAsProjectFile'>;
  onChange: (updates: Partial<StepData>) => void;
  projectId?: string;
}

function runLabel(run: { workflowName: string; startedAt: string | null; status: string }, notStarted: string): string {
  const when = run.startedAt ? new Date(run.startedAt).toLocaleString() : notStarted;
  return `${run.workflowName} · ${when} · ${run.status}`;
}

/**
 * The two step options an Agent step can carry besides its agent: continue from an earlier run's composition (the
 * agent starts from that run's finished video project instead of an empty one) and save the step's output as a
 * project file. Both are off by default and neither is sent while off, so an untouched step saves exactly as before.
 * The server refuses a continuation on an agent that cannot build a composition, naming the step in the save error.
 */
export function AgentStepOptions({ step, onChange, projectId }: AgentStepOptionsProps) {
  const t = useT('wfSteps.agentOptions');
  const fd = useFd();
  const continuing = step.continueComposition != null;
  const { data: runs, isLoading } = useProjectExecutions(projectId, continuing);
  const executionId = step.continueComposition?.executionId ?? null;
  const versionId = step.continueComposition?.versionId ?? null;

  const options = (runs ?? []).map((r) => ({ value: r.id, label: runLabel(r, t('notStarted')) }));
  // A stored run that this list does not hold (deleted, or not loaded yet) stays visible instead of blanking.
  const data = executionId && !options.some((o) => o.value === executionId)
    ? [...options, { value: executionId, label: isLoading ? t('loading') : t('runNotListed') }]
    : options;

  return (
    <Stack gap="sm">
      <Switch
        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
        {...fd('col.continueCompositionJson')}
        label={t('continue.label')}
        description={t('continue.description')}
        checked={continuing}
        onChange={(e) => onChange({ continueComposition: e.currentTarget.checked ? { executionId: null } : null })}
      />
      {continuing && (versionId && !executionId ? (
        <Text size="xs" c="dimmed">{t('savedVersion', { versionId })}</Text>
      ) : (
        <Select
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('col.continueCompositionJson')}
          label={t('runSelect.label')}
          size="xs"
          data={data}
          value={executionId}
          onChange={(v) => onChange({ continueComposition: v ? { executionId: v } : { executionId: null } })}
          placeholder={isLoading ? t('runSelect.loading') : data.length > 0 ? t('runSelect.select') : t('runSelect.empty')}
          disabled={data.length === 0}
          searchable
        />
      ))}
      <Switch
        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
        {...fd('col.registerOutputAsProjectFile')}
        label={t('saveOutput.label')}
        description={t('saveOutput.description')}
        checked={step.registerOutputAsProjectFile === true}
        onChange={(e) => onChange({ registerOutputAsProjectFile: e.currentTarget.checked })}
      />
    </Stack>
  );
}
