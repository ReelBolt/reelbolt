'use client';

import { useEffect, useMemo, useState } from 'react';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { ActionIcon, Button, Group, Loader, Select, Stack, Switch, Text, TextInput, Tooltip } from '@mantine/core';
import { IconArrowDown, IconArrowUp } from '@tabler/icons-react';
import { useWorkflowApp } from '@/lib/hooks/use-workflow-apps';
import { readableBriefFields } from '@/lib/workflow-apps/readable-fields';
import {
  APP_BRIEF_FIELDS,
  MAX_APP_INPUTS,
  MAX_APP_LABEL,
  addableBriefFields,
  inputLabel,
  inputName,
  inputsFromRows,
  manifestWithInputs,
  moveRow,
  parseDefault,
  rowsFromManifest,
  type AppInputRow,
} from '@/lib/workflow-apps/app-inputs';
import type { AppManifest, CreateWorkflowStepRequest } from '@/lib/types/workflow';
import restyle from '@/components/workflows/graph-restyle.module.css';
import { useT } from '@/i18n';
import styles from './AppInputsPanel.module.css';
import { useAppBuilder } from './AppBuilderContext';
import { withoutPermission } from './app-input-model';
import { AppPageEditor } from './AppPageEditor';

export interface AppInputsPanelProps {
  projectId: string;
  workflowId: string;
  /** The steps as drafted on the page: readable fields are judged against what would be saved. */
  draftSteps: CreateWorkflowStepRequest[];
  saving: boolean;
  /** The API's APP_MANIFEST_INVALID messages from the last refused save, naming the orphaned input. */
  errors: string[] | null;
  /** Saves the manifest with the draft steps. Resolves true when the server accepted it. */
  onSave: (manifest: AppManifest) => Promise<boolean>;
  /** Drops the stored manifest so the app is derived from the steps again (clearAppManifest). */
  onReset: () => Promise<boolean>;
}

function PanelBody({ manifest, hasExplicit, ...props }: AppInputsPanelProps & { manifest: AppManifest; hasExplicit: boolean }) {
  const t = useT('apps.inputs');
  const tAll = useT();
  const { projectId, workflowId, draftSteps, saving, errors, onSave, onReset } = props;
  const [resetOpen, setResetOpen] = useState(false);
  const [rows, setRows] = useState<AppInputRow[]>(() => rowsFromManifest(manifest));
  const [nextKey, setNextKey] = useState(rows.length);
  const [readable, setReadable] = useState<string[] | null>(null);
  const [probeFailed, setProbeFailed] = useState(false);
  const [toAdd, setToAdd] = useState<string | null>(null);
  // Polite status line for reorders, and where focus goes when the pressed arrow dims at an end.
  const [moved, setMoved] = useState('');
  const [focusMove, setFocusMove] = useState<{ key: string; dir: 'up' | 'down' } | null>(null);
  useEffect(() => {
    if (!focusMove) return;
    document.getElementById(`move-${focusMove.key}-${focusMove.dir}`)?.focus();
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setFocusMove(null);
  }, [focusMove]);

  const moveBy = (index: number, dir: -1 | 1) => {
    const target = index + dir;
    if (target < 0 || target >= rows.length) return;
    const row = rows[index];
    setRows((rs) => moveRow(rs, index, dir));
    setMoved(t('moved', { name: inputName(row.input, tAll), position: target + 1, total: rows.length }));
    // Pressing the arrow that just reached an end dims it: hand focus to the opposite arrow.
    if (target === 0 && dir === -1) setFocusMove({ key: row.key, dir: 'down' });
    if (target === rows.length - 1 && dir === 1) setFocusMove({ key: row.key, dir: 'up' });
  };

  // Which fields could still be added: asked of the server once per set of candidates, not copied here.
  const candidates = useMemo(() => addableBriefFields(rows), [rows]);
  const candidateKey = candidates.join(',');
  useEffect(() => {
    let cancelled = false;
    if (candidateKey === '') {
      Promise.resolve().then(() => { if (!cancelled) setReadable([]); });
      return () => { cancelled = true; };
    }
    readableBriefFields(projectId, workflowId, draftSteps, candidateKey.split(','))
      .then((r) => { if (!cancelled) { setReadable(r); setProbeFailed(false); } })
      .catch(() => { if (!cancelled) { setReadable([]); setProbeFailed(true); } });
    return () => { cancelled = true; };
    // draftSteps is deliberately not a dependency: the probe runs for the panel's candidates, not per keystroke.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, workflowId, candidateKey]);

  const shownCount = rows.filter((r) => r.shown).length;
  const addOptions = (readable ?? [])
    .filter((f) => candidates.includes(f))
    .map((f) => ({ value: f, label: APP_BRIEF_FIELDS.find((b) => b.field === f)?.label ?? f }));

  const update = (key: string, change: (r: AppInputRow) => AppInputRow) =>
    setRows((rs) => rs.map((r) => (r.key === key ? change(r) : r)));

  const addField = () => {
    if (!toAdd) return;
    const label = APP_BRIEF_FIELDS.find((b) => b.field === toAdd)?.label ?? toAdd;
    setRows((rs) => [...rs, { key: `row-${nextKey}`, input: { kind: 'brief', field: toAdd, label }, shown: true }]);
    setNextKey((k) => k + 1);
    setToAdd(null);
  };

  return (
    <Stack gap="sm">
      {errors && errors.length > 0 && (
        <div className={styles.error} role="alert" data-testid="app-inputs-error">
          <Text fw={600} size="sm">
            {errors.every((e) => /not read by any step|no step of this workflow/.test(e))
              ? t('notSavedOrphan')
              : t('notSavedFix')}
          </Text>
          {errors.map((e) => (
            <Text size="sm" key={e}>{e}</Text>
          ))}
        </div>
      )}

      <div role="status" aria-live="polite" className={styles.srOnly}>{moved}</div>
      <ul className={styles.list} aria-label={t('title')}>
        {rows.map((row, i) => {
          const name = inputName(row.input, tAll);
          return (
            <li key={row.key} className={styles.item}>
              <div className={`${styles.row} ${row.shown ? '' : styles.rowHidden}`}>
                <Group gap="sm" wrap="wrap" align="flex-end">
                  <Switch
                    label={row.shown ? t('shown') : t('hidden')}
                    checked={row.shown}
                    onChange={(e) => {
                      const shown = e.currentTarget.checked;
                      update(row.key, (r) => ({ ...r, shown }));
                    }}
                    aria-label={t('shownLabel', { state: row.shown ? t('shown') : t('hidden'), name })}
                  />
                  <TextInput
                    label={t('nameInApp')}
                    size="sm"
                    maxLength={MAX_APP_LABEL}
                    value={inputLabel(tAll, row.input) ?? ''}
                    disabled={!row.shown}
                    onChange={(e) => {
                      const label = e.currentTarget.value;
                      update(row.key, (r) => ({ ...r, input: { ...r.input, label } }));
                    }}
                    aria-label={t('nameInAppFor', { name })}
                    style={{ flex: '1 1 140px' }}
                  />
                  {/* An avatar has no default: it is one project's own person, chosen when the run starts. */}
                  {row.input.kind === 'brief' && row.input.field !== 'avatar' && (
                    <TextInput
                      label={t('startsAs')}
                      size="sm"
                      value={row.input.default == null ? '' : String(row.input.default)}
                      disabled={!row.shown}
                      onChange={(e) => {
                        const value = parseDefault(row.input.field, e.currentTarget.value);
                        update(row.key, (r) => ({ ...r, input: { ...r.input, default: value } }));
                      }}
                      aria-label={t('startsAsFor', { name })}
                      style={{ flex: '1 1 100px' }}
                    />
                  )}
                  <Group gap={2} wrap="nowrap">
                    <Tooltip label={t('moveUp')}>
                      <ActionIcon
                        id={`move-${row.key}-up`}
                        variant="subtle" color="gray" size={44}
                        data-disabled={i === 0 || undefined} aria-disabled={i === 0}
                        aria-label={t('moveNameUp', { name })}
                        onClick={() => moveBy(i, -1)}
                      >
                        <IconArrowUp size={16} />
                      </ActionIcon>
                    </Tooltip>
                    <Tooltip label={t('moveDown')}>
                      <ActionIcon
                        id={`move-${row.key}-down`}
                        variant="subtle" color="gray" size={44}
                        data-disabled={i === rows.length - 1 || undefined} aria-disabled={i === rows.length - 1}
                        aria-label={t('moveNameDown', { name })}
                        onClick={() => moveBy(i, 1)}
                      >
                        <IconArrowDown size={16} />
                      </ActionIcon>
                    </Tooltip>
                  </Group>
                </Group>
              </div>
            </li>
          );
        })}
      </ul>

      <Group align="flex-end" gap="sm">
        <Select
          label={t('addField')}
          size="sm"
          placeholder={readable === null ? t('checkingSteps') : t('chooseField')}
          data={addOptions}
          value={toAdd}
          onChange={setToAdd}
          disabled={readable === null || addOptions.length === 0 || shownCount >= MAX_APP_INPUTS}
          nothingFoundMessage={t('noOtherField')}
          style={{ flex: '1 1 180px' }}
        />
        <Button size="sm" variant="default" onClick={addField} disabled={!toAdd || shownCount >= MAX_APP_INPUTS}>
          {t('add')}
        </Button>
        {readable === null && <Loader size="xs" role="status" aria-label={t('checkingFields')} />}
      </Group>
      {readable !== null && !probeFailed && addOptions.length === 0 && (
        <Text size="xs" className={restyle.muted}>{t('noOtherFieldWorkflow')}</Text>
      )}
      {probeFailed && (
        <Text size="xs" className={restyle.muted}>{t('probeFailed')}</Text>
      )}
      {shownCount >= MAX_APP_INPUTS && (
        <Text size="xs" className={restyle.muted}>{t('atMost', { max: MAX_APP_INPUTS })}</Text>
      )}

      <Group justify="space-between">
        <Button size="sm" variant="subtle" color="gray" onClick={() => setResetOpen(true)}>
          {t('reset')}
        </Button>
        <Button
          size="sm"
          loading={saving}
          onClick={() => { void onSave(manifestWithInputs(hasExplicit ? manifest : { version: manifest.version }, inputsFromRows(rows))); }}
        >
          {t('save')}
        </Button>
      </Group>
      <ConfirmModal
        opened={resetOpen}
        onClose={() => setResetOpen(false)}
        onConfirm={() => { setResetOpen(false); void onReset(); }}
        title={t('reset')}
        message={t('resetMessage')}
        confirmLabel={t('resetConfirm')}
        loading={saving}
      />
    </Stack>
  );
}

/** The "App inputs" panel of Advanced (docs/design/workflow-apps.md 4.5): the effective inputs, editable. */
export function AppInputsPanel(props: AppInputsPanelProps) {
  const t = useT('apps.inputs');
  const { data, error, isLoading, mutate } = useWorkflowApp(props.projectId, props.workflowId);
  // App inputs on (the page provides the draft): the field editor with its preview; off, today's panel exactly.
  const builder = useAppBuilder();
  const savedIds = useMemo(
    () => new Set(data?.hasExplicitManifest ? (data.manifest.inputs ?? []).flatMap((i) => (i.kind === 'input' && i.id ? [i.id] : [])) : []),
    [data],
  );
  return (
    <section className={`${styles.panel} ${restyle.calm}`} aria-label={t('title')} data-testid="app-inputs-panel">
      {isLoading && <Loader size="sm" role="status" aria-label={t('loading')} />}
      {error && !data && (
        <Group gap="sm" role="alert">
          <Text size="sm" className={restyle.bad}>{t('loadFailed')}</Text>
          <Button size="sm" variant="default" onClick={() => { void mutate(); }}>{t('tryAgain')}</Button>
        </Group>
      )}
      {/* Keyed on the manifest's identity so a saved manifest reseeds the rows. */}
      {data && builder && <AppPageEditor {...props} savedIds={savedIds} />}
      {data && !builder && (
        <PanelBody key={props.workflowId + (data.hasExplicitManifest ? ':x' : ':d')} manifest={withoutPermission(data.manifest)} hasExplicit={data.hasExplicitManifest} {...props} />
      )}
    </section>
  );
}
