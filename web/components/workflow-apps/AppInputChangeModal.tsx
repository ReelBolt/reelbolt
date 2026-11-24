'use client';

import { useEffect, useMemo, useState } from 'react';
import { useRouter } from 'next/navigation';
import { Alert, Button, Group, List, Modal, Stack, Text } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { ApiError } from '@/lib/api/client';
import { createChangeRequest, estimateChangeRequest } from '@/lib/api/change-requests';
import { runHref } from '@/lib/api/revisions';
import { creditsNeededLine } from '@/lib/utils/billing';
import type { ChangeRequestBody, StepChangeAppInputs, StepChangeEstimatePreview } from '@/lib/types/change-requests';
import { describeChangeRequestError } from '@/components/change-requests/change-request-errors';
import { useCrT } from '@/components/change-requests/change-request-copy';
import type { FieldsChange } from '@/components/change-requests/change-set';
import type { RevisionRefusal } from '@/components/change-requests/revision-errors';
import { useFormat, useT } from '@/i18n';
import { AppInputField } from './AppInputField';
import { RunSettingField } from './RunSettingField';
import type { InputValue, InputValues } from './app-input-model';
import {
  changedBrief,
  changedInputs,
  hasBriefChange,
  picksAnotherBranch,
  runSettingValues,
  runValues,
  type SettingValues,
} from './app-input-change';

const DEBOUNCE_MS = 400;

export interface AppInputChangeModalProps {
  opened: boolean;
  onClose: () => void;
  projectId: string;
  /** The app's workflow (for model options when the run does not name its app). */
  workflowId: string;
  /** The finished run being changed. */
  executionId: string;
  appInputs: StepChangeAppInputs;
  /** "Change other things too": the old way, the form pre-filled from the run. Absent: no such button. */
  onRunAgain?: () => void;
  phone: boolean;
  /**
   * Set mode (docs/design/app-inputs.md 29): the change joins the run page's change set (with step changes) instead of being
   * sent on its own; the button reads "Add to the changes".
   */
  onAddToSet?: (change: FieldsChange) => void;
  /** The change already in the set, to reopen it as it was. */
  initial?: FieldsChange | null;
}

/**
 * "Change one thing and re-run" for an app run (docs/design/app-inputs.md 25, 29): the run's own fields and run settings with
 * its values; the person changes one or more, sees what is redone, what is kept and the price, and starts a new linked run
 * that re-makes only the steps that use what changed. A field that cannot be changed this way says why; a value that picks
 * another branch points to running the app again.
 */
export function AppInputChangeModal(props: AppInputChangeModalProps) {
  const t = useT('apps.run.change');
  // The form is mounted only while the dialog is open (and per run), so every opening starts from the run's own values.
  return (
    <Modal opened={props.opened} onClose={props.onClose} title={t('title')} centered size="lg" fullScreen={props.phone}>
      {props.opened && <ChangeForm key={props.executionId} {...props} />}
    </Modal>
  );
}

function ChangeForm({ opened, onClose, projectId, workflowId, executionId, appInputs, onRunAgain, phone, onAddToSet, initial }: AppInputChangeModalProps) {
  const t = useT('apps.run.change');
  const tr = useT();
  const crT = useCrT();
  const fm = useFormat();
  const router = useRouter();
  const settings = useMemo(() => appInputs.briefFields ?? [], [appInputs.briefFields]);
  const [values, setValues] = useState<InputValues>(() => ({ ...runValues(appInputs.fields), ...(initial?.inputs ?? {}) }));
  const [settingValues, setSettingValues] = useState<SettingValues>(() => {
    const own = runSettingValues(settings);
    const brief = initial?.brief;
    if (!brief) return own;
    for (const s of settings) {
      if (s.kind === 'notes' && brief.notes !== undefined) own[s.key] = brief.notes;
      else if (s.kind === 'files') {
        const group = brief.sourceFiles?.find((g) => g.role === s.role);
        if (group) own[s.key] = group.fileIds;
      } else if (s.field && s.field in brief) own[s.key] = (brief as Record<string, unknown>)[s.field];
    }
    return own;
  });
  const [preview, setPreview] = useState<{ key: string; value: StepChangeEstimatePreview | null } | null>(null);
  const [refusal, setRefusal] = useState<(RevisionRefusal & { branch: boolean }) | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const changed = useMemo(() => changedInputs(appInputs.fields, values), [appInputs.fields, values]);
  const brief = useMemo(() => changedBrief(settings, settingValues), [settings, settingValues]);
  const branchField = appInputs.fields.find((f) => f.supported && picksAnotherBranch(f, values[f.inputId]));
  const hasInputs = Object.keys(changed).length > 0;
  const body: ChangeRequestBody | null = hasInputs || hasBriefChange(brief)
    ? { ...(hasInputs ? { inputs: changed } : {}), ...(hasBriefChange(brief) ? { brief } : {}) }
    : null;
  const key = body && !branchField ? JSON.stringify(body) : '';
  const [debouncedKey] = useDebouncedValue(key, DEBOUNCE_MS);

  const fail = (e: unknown) => {
    const branch = e instanceof ApiError && e.code === 'INPUT_CHANGES_BRANCH';
    setRefusal({ ...describeChangeRequestError(e, crT), branch });
  };

  useEffect(() => {
    if (!debouncedKey || !opened) return;
    let cancelled = false;
    estimateChangeRequest(projectId, executionId, JSON.parse(debouncedKey))
      .then((p) => { if (!cancelled) { setPreview({ key: debouncedKey, value: p }); setRefusal(null); } })
      .catch((e: unknown) => { if (!cancelled) { setPreview({ key: debouncedKey, value: null }); fail(e); } });
    return () => { cancelled = true; };
  // `fail` changes identity per render; the price is asked again when the values change only.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [debouncedKey, opened, projectId, executionId]);

  const shown = key && preview?.key === key ? preview.value : null;
  const estimating = !!key && preview?.key !== key;
  const estimate = shown?.estimate ?? null;
  const needs = estimate?.billingEnabled ? creditsNeededLine(estimate.requiredCredits, tr, fm.locale) : null;
  const redo = shown?.replay.steps.filter((s) => s.disposition === 'Revise' || s.disposition === 'Run') ?? [];
  const reuse = shown?.replay.steps.filter((s) => s.disposition === 'Reuse' || s.disposition === 'KeepIfCompatible') ?? [];
  const lines = (shown?.inputChanges ?? []).map((c) => t('changeLine', { label: c.label, before: c.before, after: c.after }));

  const submit = async () => {
    if (!key || !body) return;
    if (onAddToSet) {
      onAddToSet({ ...(hasInputs ? { inputs: changed } : {}), ...(hasBriefChange(brief) ? { brief } : {}), lines });
      onClose();
      return;
    }
    setSubmitting(true);
    setRefusal(null);
    try {
      const created = await createChangeRequest(projectId, executionId, body);
      onClose();
      router.push(runHref(projectId, created.workflowDefinitionId, created.executionId));
    } catch (e) {
      fail(e);
    } finally {
      setSubmitting(false);
    }
  };

  const setValue = (id: string, value: InputValue) => setValues((current) => ({ ...current, [id]: value }));
  const setSetting = (k: string, value: unknown) => setSettingValues((current) => ({ ...current, [k]: value }));
  const runAgain = onRunAgain ? () => { onClose(); onRunAgain(); } : null;
  const fixed = [
    ...appInputs.fields.filter((f) => !f.supported).map((f) => ({ key: f.inputId, label: f.label, reason: f.notSupportedReason })),
    ...settings.filter((s) => !s.supported).map((s) => ({ key: s.key, label: s.label, reason: s.notSupportedReason })),
  ];

  return (
    <Stack gap="md" data-testid="app-input-change">
      <Text size="sm">{t('intro')}</Text>

      {appInputs.fields.filter((f) => f.supported).map((field) => (
        <div key={field.inputId} data-testid={`app-input-change-field-${field.inputId}`}>
          <AppInputField
            input={{ ...field.input, id: field.inputId }}
            value={values[field.inputId] ?? null}
            onChange={(v) => setValue(field.inputId, v)}
            error={picksAnotherBranch(field, values[field.inputId]) ? t('picksBranch') : undefined}
            projectId={projectId}
            workflowId={appInputs.workflowId ?? workflowId}
            phone={phone}
          />
          {field.usedBy.length > 0 && (
            <Text size="xs" c="dimmed" mt={4}>{t('redoes', { steps: field.usedBy.join(', ') })}</Text>
          )}
        </div>
      ))}

      {settings.some((s) => s.supported) && (
        <Stack gap="sm" data-testid="app-input-change-settings">
          <Text size="sm" fw={600}>{t('settingsTitle')}</Text>
          {settings.filter((s) => s.supported).map((setting) => (
            <div key={setting.key} data-testid={`app-input-change-setting-${setting.key}`}>
              <RunSettingField projectId={projectId} field={setting} value={settingValues[setting.key]} onChange={(v) => setSetting(setting.key, v)} phone={phone} />
              {setting.usedBy.length > 0 && (
                <Text size="xs" c="dimmed" mt={4}>{t('redoes', { steps: setting.usedBy.join(', ') })}</Text>
              )}
            </div>
          ))}
        </Stack>
      )}

      {fixed.length > 0 && (
        <Stack gap={4} data-testid="app-input-change-fixed">
          <Text size="sm" fw={600}>{t('fixedTitle')}</Text>
          <List size="sm" spacing={2}>
            {fixed.map((f) => (
              <List.Item key={f.key}>{t('fixedLine', { label: f.label, reason: f.reason ?? t('fixedReason') })}</List.Item>
            ))}
          </List>
        </Stack>
      )}

      {estimating && <Text size="sm" c="dimmed">{t('estimating')}</Text>}
      {shown && (
        <Stack gap={4} data-testid="app-input-change-preview" aria-live="polite">
          {lines.map((line) => (
            <Text key={line} size="sm" style={{ overflowWrap: 'anywhere' }}>{line}</Text>
          ))}
          {redo.length > 0 && <Text size="sm">{t('redone', { steps: redo.map((s) => s.label).join(', ') })}</Text>}
          {reuse.length > 0 && <Text size="sm" c="dimmed">{t('reused', { count: reuse.length })}</Text>}
          {needs && <Text size="sm" fw={600}>{needs}</Text>}
        </Stack>
      )}

      {refusal && (
        <Alert color={refusal.color} title={refusal.title} data-testid="app-input-change-refusal">
          <Text size="sm">{refusal.message}</Text>
          {refusal.branch && runAgain && <Button variant="light" size="xs" mt="xs" onClick={runAgain}>{t('runAgain')}</Button>}
        </Alert>
      )}
      {branchField && !refusal && (
        <Alert color="orange" data-testid="app-input-change-branch">
          <Text size="sm">{t('branchNote', { label: branchField.label })}</Text>
          {runAgain && <Button variant="light" size="xs" mt="xs" onClick={runAgain}>{t('runAgain')}</Button>}
        </Alert>
      )}

      <Group justify="space-between" wrap="wrap" gap="xs">
        {runAgain ? <Button variant="subtle" onClick={runAgain} style={{ minHeight: 44 }}>{t('otherThings')}</Button> : <span />}
        <Button onClick={() => void submit()} loading={submitting} disabled={!key || estimating || !shown} style={{ minHeight: 44 }} data-testid="app-input-change-submit">
          {onAddToSet ? t('addToSet') : t('submit')}
        </Button>
      </Group>
    </Stack>
  );
}
