'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { ActionIcon, Badge, Button, Group, List, Menu, Stack, Text, Textarea, Tooltip } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { IconArrowDown, IconArrowUp, IconChevronDown, IconGripVertical, IconPlus, IconTrash, IconLock } from '@tabler/icons-react';
import { describeAppChanges } from '@/lib/api/workflows';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { useT } from '@/i18n';
import type { T } from '@/i18n/translator';
import type { AppInputType, AppManifest, AppManifestInput, CreateWorkflowStepRequest } from '@/lib/types/workflow';
import { addableBriefFields, briefFieldLabel, inputName, rowsFromManifest } from '@/lib/workflow-apps/app-inputs';
import { readableBriefFields } from '@/lib/workflow-apps/readable-fields';
import {
  MAX_REQUEST_TEMPLATE, errorsByInput, insertAt, inputUses, isTyped, manifestForSave, moveInput, moveInputTo, newInputId,
  normalizeInput, placeholder, removeInput, settleVersion, type InputUse,
} from '@/lib/workflow-apps/app-input-builder';
import restyle from '@/components/workflows/graph-restyle.module.css';
import { permissionSentence } from './PermissionBlock';
import { useAppBuilder } from './AppBuilderContext';
import { AppInputEditor } from './AppInputEditor';
import { AppPagePreview } from './AppPagePreview';
import { MAX_TOTAL_INPUTS, isUnderMore } from './app-input-model';
import styles from './AppInputsPanel.module.css';

/** The field types the "Add a field" menu offers (a model field is made from a step's Model setting). */
const NEW_TYPES: readonly AppInputType[] = ['shortText', 'longText', 'choice', 'toggle', 'number', 'duration', 'shape'];

export interface AppPageEditorProps {
  projectId: string;
  workflowId: string;
  draftSteps: CreateWorkflowStepRequest[];
  saving: boolean;
  errors: string[] | null;
  /** The ids of the `input` fields the server has stored (a model list is asked of the server by id). */
  savedIds: ReadonlySet<string>;
  onSave: (manifest: AppManifest) => Promise<boolean>;
  onReset: () => Promise<boolean>;
}

/**
 * The "App inputs" panel with app inputs on (docs/design/app-inputs.md 13.1, 24): the request page's fields as a short
 * list (name, kind, where it is used), each opened to edit; add, remove and reorder; the message the AI steps read; and a
 * live preview beside it. Edits go to the page's draft, which is saved with the steps.
 */
export function AppPageEditor({ projectId, workflowId, draftSteps, saving, errors, savedIds, onSave, onReset }: AppPageEditorProps) {
  const t = useT('apps.builder');
  const root = useT();
  const tPerm = useT('apps.permission.mediaEgress');
  const builder = useAppBuilder()!;
  const { manifest, setManifest, steps, setSteps, bindableFields, hasExplicitManifest } = builder;
  const inputs = useMemo(() => manifest.inputs ?? [], [manifest]);
  const [open, setOpen] = useState<string | null>(null);
  const [resetOpen, setResetOpen] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [status, setStatus] = useState('');
  const templateRef = useRef<HTMLTextAreaElement>(null);

  const uses = useMemo(() => inputUses(manifest, steps, bindableFields), [manifest, steps, bindableFields]);
  const placed = useMemo(() => errorsByInput(manifest, errors), [manifest, errors]);
  const keyOf = (input: AppManifestInput, i: number) => (isTyped(input) ? `in:${input.id}` : `${input.kind}:${input.field ?? input.role ?? i}`);

  // Brief fields the steps can read, asked of the server (the old panel's probe), for "Add a field".
  const candidates = useMemo(() => addableBriefFields(rowsFromManifest(manifest)), [manifest]);
  const candidateKey = candidates.join(',');
  const [readable, setReadable] = useState<string[]>([]);
  useEffect(() => {
    let cancelled = false;
    if (candidateKey === '') {
      Promise.resolve().then(() => { if (!cancelled) setReadable([]); });
      return () => { cancelled = true; };
    }
    readableBriefFields(projectId, workflowId, draftSteps, candidateKey.split(','))
      .then((r) => { if (!cancelled) setReadable(r); })
      .catch(() => { if (!cancelled) setReadable([]); });
    return () => { cancelled = true; };
    // The probe runs per set of candidates, not per keystroke in a step.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, workflowId, candidateKey]);

  // "What changes" (docs/design/app-inputs.md 29): the draft as Save would send it, described by the server against what is
  // stored, the same sentences as the assistant's approval card. Asked again 600 ms after the draft stops changing.
  const changesKey = JSON.stringify({ appManifest: manifestForSave(manifest, hasExplicitManifest === true), steps: draftSteps });
  const [debouncedChanges] = useDebouncedValue(changesKey, 600);
  const [changes, setChanges] = useState<{ key: string; lines: string[] } | null>(null);
  useEffect(() => {
    let cancelled = false;
    describeAppChanges(projectId, workflowId, JSON.parse(debouncedChanges))
      .then((r) => { if (!cancelled) setChanges({ key: debouncedChanges, lines: r.lines ?? [] }); })
      .catch(() => { if (!cancelled) setChanges(null); });
    return () => { cancelled = true; };
  }, [projectId, workflowId, debouncedChanges]);
  const changeLines = changes?.key === debouncedChanges ? changes.lines : [];

  // Drag to reorder (the arrows stay for the keyboard, touch and screen readers).
  const [dragFrom, setDragFrom] = useState<number | null>(null);
  const [dragOver, setDragOver] = useState<number | null>(null);
  const dropAt = (to: number) => {
    const from = dragFrom;
    setDragFrom(null);
    setDragOver(null);
    if (from === null || from === to) return;
    commit(moveInputTo(manifest, from, to));
    setStatus(t('moved', { name: inputName(inputs[from], root), position: to + 1, total: inputs.length }));
  };

  const commit = (next: AppManifest) => setManifest(settleVersion(next));
  const replaceAt = (index: number, input: AppManifestInput) =>
    commit({ ...manifest, inputs: inputs.map((x, k) => (k === index ? normalizeInput(input) : x)) });

  const addTyped = (type: AppInputType) => {
    const label = t(`newLabel.${type}`);
    const id = newInputId(label, manifest);
    const input: AppManifestInput = { kind: 'input', id, type, label, ...defaultsFor(type, t) };
    commit({ ...manifest, inputs: [...inputs, input] });
    setOpen(`in:${id}`);
    setNotice(t('addedNotUsed', { name: label }));
  };
  const addBrief = (field: string) => {
    commit({ ...manifest, inputs: [...inputs, { kind: 'brief', field, label: briefFieldLabel(root, field) }] });
  };

  const remove = (index: number) => {
    const input = inputs[index];
    if (isTyped(input)) {
      const result = removeInput(manifest, steps, input.id);
      if (!result.ok) {
        setNotice(t('removeRefused', { n: result.stepIndex + 1 }));
        return;
      }
      setSteps(result.steps);
      commit(result.manifest);
    } else {
      commit({ ...manifest, inputs: inputs.filter((_, k) => k !== index) });
    }
    setStatus(t('removed', { name: inputName(input, root) }));
  };

  const move = (index: number, dir: -1 | 1) => {
    const target = index + dir;
    if (target < 0 || target >= inputs.length) return;
    commit(moveInput(manifest, index, dir));
    setStatus(t('moved', { name: inputName(inputs[index], root), position: target + 1, total: inputs.length }));
  };

  const useLine = (use: InputUse) => {
    switch (use.kind) {
      case 'condition':
        return t('used.condition', { n: (use.stepIndex ?? 0) + 1 });
      case 'request':
        return t('used.request');
      case 'agent':
        return t('used.agent', { n: (use.stepIndex ?? 0) + 1 });
      default:
        return t('used.setting', { n: (use.stepIndex ?? 0) + 1, setting: use.setting ?? '' });
    }
  };

  const templateFields = inputs.filter((i) => isTyped(i) && i.type !== 'model');
  const insertIntoTemplate = (id: string) => {
    const current = manifest.requestTemplate ?? '';
    const { text, caret } = insertAt(current, templateRef.current?.selectionStart, placeholder(id));
    commit({ ...manifest, requestTemplate: text.slice(0, MAX_REQUEST_TEMPLATE) });
    requestAnimationFrame(() => {
      templateRef.current?.focus();
      templateRef.current?.setSelectionRange(caret, caret);
    });
  };

  const full = inputs.length >= MAX_TOTAL_INPUTS;

  return (
    <div className={styles.editorGrid}>
      <Stack gap="sm">
        {placed.general.length > 0 && (
          <div className={styles.error} role="alert" data-testid="app-inputs-error">
            <Text fw={600} size="sm">{t('notSaved')}</Text>
            {placed.general.map((e) => <Text size="sm" key={e}>{e}</Text>)}
          </div>
        )}
        {notice && <Text size="sm" role="status">{notice}</Text>}
        <div role="status" aria-live="polite" className={styles.srOnly}>{status}</div>

        <ul className={styles.list} aria-label={t('fields')}>
          {inputs.map((input, i) => {
            const key = keyOf(input, i);
            const name = inputName(input, root);
            const fieldErrors = placed.byIndex.get(i) ?? [];
            const used = isTyped(input) ? uses.get(input.id) ?? [] : null;
            const expanded = open === key;
            return (
              <li
                key={key}
                className={styles.item}
                onDragOver={(e) => { if (dragFrom !== null) { e.preventDefault(); setDragOver(i); } }}
                onDrop={(e) => { e.preventDefault(); dropAt(i); }}
                style={dragOver === i && dragFrom !== null && dragFrom !== i ? { outline: '2px dashed var(--mantine-color-violet-5)', outlineOffset: 2 } : undefined}
                data-testid={`app-field-item-${i}`}
              >
                <div className={styles.row} data-testid={`app-field-${key}`}>
                  <Group justify="space-between" wrap="wrap" gap="xs">
                    <Stack gap={2} style={{ minWidth: 0, flex: '1 1 160px' }}>
                      <Group gap={6} wrap="wrap">
                        <Text fw={600} size="sm" className={styles.wrap}>{name}</Text>
                        <Badge variant="light" color="gray" size="sm">{kindWord(input, t)}</Badge>
                        {input.required === true && <Badge variant="light" size="sm">{t('badge.required')}</Badge>}
                        {isUnderMore(input) && <Badge variant="outline" color="gray" size="sm">{t('badge.more')}</Badge>}
                      </Group>
                      {used && (
                        <Text size="xs" className={used.length > 0 ? restyle.muted : restyle.warn}>
                          {used.length > 0 ? used.map(useLine).join(' · ') : t('used.none')}
                        </Text>
                      )}
                    </Stack>
                    <Group gap={2} wrap="nowrap">
                      <Tooltip label={t('drag')}>
                        <span
                          draggable
                          onDragStart={(e) => { setDragFrom(i); e.dataTransfer?.setData?.('text/plain', String(i)); }}
                          onDragEnd={() => { setDragFrom(null); setDragOver(null); }}
                          aria-hidden
                          data-testid={`app-field-drag-${i}`}
                          style={{ cursor: 'var(--rb-cursor-grab)', display: 'inline-flex', alignItems: 'center', padding: 8 }}
                        >
                          <IconGripVertical size={16} />
                        </span>
                      </Tooltip>
                      <Button
                        size="sm"
                        variant="default"
                        aria-expanded={expanded}
                        aria-controls={`app-field-edit-${i}`}
                        onClick={() => setOpen(expanded ? null : key)}
                        rightSection={<IconChevronDown size={14} style={{ transform: expanded ? 'rotate(180deg)' : undefined }} />}
                      >
                        {expanded ? t('done') : t('open')}
                      </Button>
                      <Tooltip label={t('moveUp')}>
                        <ActionIcon variant="subtle" color="gray" size={44} aria-label={t('moveNameUp', { name })} disabled={i === 0} onClick={() => move(i, -1)}>
                          <IconArrowUp size={16} />
                        </ActionIcon>
                      </Tooltip>
                      <Tooltip label={t('moveDown')}>
                        <ActionIcon variant="subtle" color="gray" size={44} aria-label={t('moveNameDown', { name })} disabled={i === inputs.length - 1} onClick={() => move(i, 1)}>
                          <IconArrowDown size={16} />
                        </ActionIcon>
                      </Tooltip>
                      <Tooltip label={t('remove')}>
                        <ActionIcon variant="subtle" color="gray" size={44} aria-label={t('removeName', { name })} onClick={() => remove(i)}>
                          <IconTrash size={16} />
                        </ActionIcon>
                      </Tooltip>
                    </Group>
                  </Group>
                  {fieldErrors.map((e) => (
                    <Text key={e} size="sm" className={restyle.bad} role="alert">{e}</Text>
                  ))}
                  {expanded && (
                    <div id={`app-field-edit-${i}`} className={styles.editBody}>
                      <AppInputEditor
                        input={input}
                        manifest={manifest}
                        name={name}
                        onChange={(next) => replaceAt(i, next)}
                        projectId={projectId}
                        workflowId={workflowId}
                        saved={isTyped(input) && savedIds.has(input.id)}
                      />
                    </div>
                  )}
                </div>
              </li>
            );
          })}
        </ul>

        {builder.mediaEgress && (
          <div className={styles.locked} data-testid="app-builder-permission">
            <Group gap="xs" wrap="nowrap">
              <IconLock size={14} aria-hidden />
              <Text size="sm" fw={500}>{tPerm('label')}</Text>
              <Text size="xs" className={restyle.muted}>{tPerm('builder.locked')}</Text>
            </Group>
            <Text size="xs" className={restyle.muted}>
              {tPerm('builder.explain')}{' '}
              {permissionSentence(tPerm, builder.mediaEgress)}
            </Text>
          </div>
        )}

        <Group gap="sm">
          <Menu position="bottom-start" withinPortal>
            <Menu.Target>
              <Button size="sm" variant="default" leftSection={<IconPlus size={14} />} disabled={full}>{t('add')}</Button>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Label>{t('addNew')}</Menu.Label>
              {NEW_TYPES.map((type) => (
                <Menu.Item key={type} onClick={() => addTyped(type)}>{t(`types.${type}`)}</Menu.Item>
              ))}
              {readable.length > 0 && <Menu.Label>{t('addBrief')}</Menu.Label>}
              {readable.map((field) => (
                <Menu.Item key={field} onClick={() => addBrief(field)}>{briefFieldLabel(root, field)}</Menu.Item>
              ))}
            </Menu.Dropdown>
          </Menu>
        </Group>
        <Text size="xs" className={restyle.muted}>{full ? t('atMost', { max: MAX_TOTAL_INPUTS }) : t('addHint')}</Text>

        <Stack gap={4}>
          <Textarea
            ref={templateRef}
            label={t('request.label')}
            description={t('request.hint')}
            size="sm"
            autosize
            minRows={2}
            maxRows={8}
            maxLength={MAX_REQUEST_TEMPLATE}
            value={manifest.requestTemplate ?? ''}
            onChange={(e) => commit({ ...manifest, requestTemplate: e.currentTarget.value || null })}
          />
          {templateFields.length > 0 && (
            <Menu position="bottom-start" withinPortal>
              <Menu.Target>
                <Button size="sm" variant="subtle" style={{ alignSelf: 'flex-start' }}>{t('request.insert')}</Button>
              </Menu.Target>
              <Menu.Dropdown>
                {templateFields.map((f) => (
                  <Menu.Item key={f.id!} onClick={() => insertIntoTemplate(f.id!)}>{inputName(f, root)}</Menu.Item>
                ))}
              </Menu.Dropdown>
            </Menu>
          )}
        </Stack>

        {changeLines.length > 0 && (
          <Stack gap={4} data-testid="app-page-changes" aria-live="polite">
            <Text size="sm" fw={600}>{t('changes.title')}</Text>
            <List size="sm" spacing={2}>
              {changeLines.map((line) => <List.Item key={line}><span className={styles.wrap}>{line}</span></List.Item>)}
            </List>
          </Stack>
        )}

        <Group justify="space-between">
          <Button size="sm" variant="subtle" color="gray" onClick={() => setResetOpen(true)}>{t('reset')}</Button>
          <Button size="sm" loading={saving} onClick={() => { void onSave(manifest); }}>{t('save')}</Button>
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

      <section aria-label={t('preview.title')}>
        <Text size="sm" fw={600} mb={6}>{t('preview.title')}</Text>
        <AppPagePreview manifest={manifest} projectId={projectId} workflowId={workflowId} />
      </section>
    </div>
  );
}

function kindWord(input: AppManifestInput, t: T): string {
  if (isTyped(input)) return t(`types.${input.type}`);
  return t(`kinds.${input.kind === 'files' ? 'files' : input.kind === 'notes' ? 'notes' : 'brief'}`);
}

/** What a new field of a type starts with, so it is valid at once. */
function defaultsFor(type: AppInputType, t: T): Partial<AppManifestInput> {
  switch (type) {
    case 'choice':
      return { options: [{ value: 'option1', label: t('edit.optionN', { n: 1 }) }, { value: 'option2', label: t('edit.optionN', { n: 2 }) }], default: 'option1' };
    case 'toggle':
      return { default: false };
    case 'number':
      return { min: 1, max: 10, default: 1 };
    case 'duration':
      return { min: 2, max: 60, default: 6 };
    case 'shape':
      return { choices: ['9:16', '1:1', '16:9'], default: '9:16' };
    case 'longText':
      return { maxLength: 2000 };
    case 'shortText':
      return { maxLength: 120 };
    default:
      return {};
  }
}
